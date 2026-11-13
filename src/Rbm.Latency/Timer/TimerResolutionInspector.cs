using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using Rbm.Core;
using Rbm.Latency.Native;
using Rbm.Security;
using Microsoft.Win32;

namespace Rbm.Latency;

/// <summary>
/// Resolução do timer do sistema e pedidos de resolução por processo.
/// <para>Leitura (sem elevação): <see cref="Query"/> via NtQueryTimerResolution.</para>
/// <para>Pedidos por processo (<b>Requer elevação — executado via RBM.Agent</b>): <see cref="CollectTimerRequestsAsync"/>
/// executa <c>powercfg /energy</c> por N segundos e extrai "Platform Timer Resolution:Outstanding Timer Request".</para>
/// </summary>
public static class TimerResolutionInspector
{
    public const int EnergyMinSeconds = 10;
    public const int EnergyMaxSeconds = 120;
    public const int EnergyDefaultSeconds = 60;

    private const string KernelKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\kernel";

    public static readonly IReadOnlyList<string> References = new[]
    {
        "Microsoft Learn — timeBeginPeriod (Remarks: mudança no Windows 10 2004 e comportamento no Windows 11): https://learn.microsoft.com/windows/win32/api/timeapi/nf-timeapi-timebeginperiod",
        "Microsoft Learn — SetProcessInformation / PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION: https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation",
        "Bruce Dawson (fonte de terceiros), \"Windows Timer Resolution: The Great Rule Change\" (2020) — análise do novo comportamento e do valor GlobalTimerResolutionRequests: https://randomascii.wordpress.com/2020/10/04/windows-timer-resolution-the-great-rule-change/",
    };

    public const string Explanation =
        "Até o Windows 10 2004, timeBeginPeriod alterava uma configuração GLOBAL: todos os processos passavam a usar a menor resolução " +
        "(maior precisão) pedida por qualquer processo. A partir do Windows 10 versão 2004, segundo a documentação da Microsoft, a função " +
        "não afeta mais a resolução global: processos que chamam timeBeginPeriod recebem a maior precisão pedida por qualquer processo, " +
        "e processos que não chamam não têm garantia de resolução melhor que a padrão do sistema (normalmente 15,625 ms). " +
        "No Windows 11, se um processo que possui janela fica totalmente encoberto, minimizado ou invisível/inaudível, o Windows também " +
        "deixa de garantir a resolução maior para ele (o processo pode optar por ignorar isso via SetProcessInformation com " +
        "PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION). O valor \"atual\" lido aqui é a frequência global da interrupção de timer " +
        "(reflete o menor período pedido por algum processo), mas não significa que TODOS os processos se beneficiem dele. " +
        "Resolução mais fina melhora a precisão de Sleep/esperas, porém aumenta o consumo de energia e não altera o contador de alta " +
        "resolução (QueryPerformanceCounter). Forçar 0,5 ms globalmente não é um ganho comprovado de FPS/latência (grau de evidência baixo). " +
        "O valor de registro GlobalTimerResolutionRequests (Session Manager\\kernel) restaura o comportamento global no Windows 11/Server 2022+; " +
        "ele é descrito por fontes de terceiros, não na página da API — aqui é apenas lido e informado.";

    /// <summary>Lê min/máx/atual via NtQueryTimerResolution (unidades de 100 ns → ms). Não requer elevação.</summary>
    public static TimerResolutionInfo Query()
    {
        int status = NativeMethods.NtQueryTimerResolution(out uint coarsest, out uint finest, out uint current);
        if (status != 0) throw new InvalidOperationException($"NtQueryTimerResolution falhou (NTSTATUS 0x{status:X8}).");
        return new TimerResolutionInfo(
            ToMs(coarsest), ToMs(finest), ToMs(current),
            coarsest, finest, current,
            ReadGlobalTimerResolutionRequests(),
            Explanation,
            References);
    }

    /// <summary>Converte unidades de 100 ns em milissegundos.</summary>
    public static double ToMs(uint units100ns) => units100ns / 10_000.0;

    private static int? ReadGlobalTimerResolutionRequests()
    {
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(KernelKey);
            return k?.GetValue("GlobalTimerResolutionRequests") is int v ? v : null;
        }
        catch { return null; }
    }

    /// <summary>
    /// Executa <c>powercfg /energy /duration N</c> e lista os processos com pedidos de resolução de timer pendentes.
    /// <para><b>Requer elevação — executado via RBM.Agent.</b> Duração validada em 10..120 s (padrão 60 s); o comando
    /// bloqueia durante toda a observação e o relatório HTML temporário é apagado ao final.</para>
    /// Aviso: o powercfg /energy observa o sistema durante a duração inteira; mantenha os aplicativos de interesse abertos e em primeiro plano.
    /// </summary>
    public static async Task<TimerRequestsReport> CollectTimerRequestsAsync(int seconds = EnergyDefaultSeconds, CancellationToken ct = default)
    {
        if (seconds is < EnergyMinSeconds or > EnergyMaxSeconds)
            throw new ArgumentOutOfRangeException(nameof(seconds), seconds, $"Duração do powercfg /energy deve estar entre {EnergyMinSeconds} e {EnergyMaxSeconds} segundos.");
        if (!ChannelSecurity.IsElevated())
            throw new UnauthorizedAccessException("powercfg /energy requer elevação. Execute pelo agente elevado do RBM Optimizer (RBM.Agent).");

        var started = DateTimeOffset.Now;
        var dir = Path.Combine(Path.GetTempPath(), "RBMEnergy-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var file = Path.Combine(dir, "energy-report.html");
        var warnings = new List<string>();
        try
        {
            var res = await ProcessRunner.RunAsync(ProcessRunner.System32("powercfg.exe"),
                new[] { "/energy", "/output", file, "/duration", seconds.ToString(CultureInfo.InvariantCulture) },
                TimeSpan.FromSeconds(seconds + 120), ct, ProcessRunner.OemEncoding).ConfigureAwait(false);
            if (res.TimedOut) throw new TimeoutException("powercfg /energy excedeu o tempo limite.");
            if (!File.Exists(file))
                throw new InvalidOperationException($"powercfg /energy não gerou o relatório (código {res.ExitCode}): {res.StdErr.Trim()} {res.StdOut.Trim()}".Trim());
            // powercfg /energy retorna código ≠ 0 quando o relatório contém erros de eficiência; isso não é falha da coleta.
            var html = await File.ReadAllTextAsync(file, ct).ConfigureAwait(false);
            var parsed = EnergyReportTimerParser.Parse(html);
            if (parsed.Requests.Count == 0)
                warnings.Add("Nenhum pedido de resolução de timer encontrado. Se o Windows estiver em outro idioma, o formato do relatório pode não ter sido reconhecido.");
            return new TimerRequestsReport(started, seconds, parsed.CurrentResolutionMs, parsed.Requests, warnings);
        }
        finally
        {
            try { Directory.Delete(dir, recursive: true); } catch { /* melhor esforço */ }
        }
    }
}

/// <summary>Resultado do parser do relatório powercfg /energy.</summary>
public sealed record EnergyTimerParseResult(double? CurrentResolutionMs, IReadOnlyList<TimerResolutionRequest> Requests);

/// <summary>
/// Parser puro do HTML de <c>powercfg /energy</c>, focado nas entradas
/// "Platform Timer Resolution:Outstanding Timer Request" (Requested Period / Requesting Process ID / Requesting Process Path)
/// e "Platform Timer Resolution:Platform Timer Resolution" (Current Timer Resolution (100ns units)).
/// Tolerante a variações de marcação: o HTML é achatado em uma sequência de células (&lt;td&gt;) e os rótulos são
/// reconhecidos em inglês e português (o relatório é localizado pelo idioma do Windows).
/// </summary>
public static class EnergyReportTimerParser
{
    private static readonly Regex CellSplit = new(@"<\s*/?\s*td\b[^>]*>", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex Tag = new(@"<[^>]+>", RegexOptions.Compiled);
    private static readonly Regex ScriptStyle = new(@"<(script|style)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex Ws = new(@"\s+", RegexOptions.Compiled);
    private static readonly Regex TitleLike = new(@"^[^:\\/]{3,}:[^:\\/]{3,}$", RegexOptions.Compiled);
    private static readonly Regex Digits = new(@"\d+", RegexOptions.Compiled);

    public static EnergyTimerParseResult Parse(string html)
    {
        var cells = Cells(html);
        var requests = new List<TimerResolutionRequest>();
        double? current = null;

        for (int i = 0; i < cells.Count; i++)
        {
            string c = cells[i];
            if (!TitleLike.IsMatch(c)) continue;
            bool isTimerSection = Regex.IsMatch(c, "timer|temporizador", RegexOptions.IgnoreCase);
            if (!isTimerSection) continue;
            bool isOutstanding = Regex.IsMatch(c, "outstanding|pendente|solicita", RegexOptions.IgnoreCase) && !Regex.IsMatch(c, "stack|pilha", RegexOptions.IgnoreCase);

            int? pid = null; string? path = null; uint? period = null;
            int j = i + 1;
            for (; j < cells.Count && j <= i + 24; j++)
            {
                string label = cells[j];
                if (TitleLike.IsMatch(label)) break;
                string? value = j + 1 < cells.Count ? cells[j + 1] : null;
                if (value is null) break;

                if (Regex.IsMatch(label, @"current timer resolution|resolução (atual )?do (timer|temporizador)|resolução atual", RegexOptions.IgnoreCase))
                {
                    if (TryUInt(value, out var cur)) current = TimerResolutionInspector.ToMs(cur);
                    j++;
                }
                else if (isOutstanding && Regex.IsMatch(label, @"period|período", RegexOptions.IgnoreCase))
                {
                    if (TryUInt(value, out var p)) period = p;
                    j++;
                }
                else if (isOutstanding && Regex.IsMatch(label, @"process id|id do processo|\bpid\b", RegexOptions.IgnoreCase))
                {
                    if (TryUInt(value, out var p)) pid = (int)p;
                    j++;
                }
                else if (isOutstanding && Regex.IsMatch(label, @"path|caminho", RegexOptions.IgnoreCase))
                {
                    path = value;
                    j++;
                }
            }
            if (isOutstanding && (pid is not null || path is not null || period is not null))
                requests.Add(new TimerResolutionRequest(pid, path, period, period is { } pp ? TimerResolutionInspector.ToMs(pp) : null));
            i = j - 1;
        }
        return new EnergyTimerParseResult(current, requests);
    }

    /// <summary>Texto de cada célula &lt;td&gt;, sem marcação e com entidades HTML decodificadas (vazias descartadas).</summary>
    internal static List<string> Cells(string html)
    {
        var list = new List<string>();
        var clean = ScriptStyle.Replace(html ?? "", " ");
        foreach (var part in CellSplit.Split(clean))
        {
            var text = Ws.Replace(WebUtility.HtmlDecode(Tag.Replace(part, " ")), " ").Trim();
            if (text.Length > 0) list.Add(text);
        }
        return list;
    }

    private static bool TryUInt(string s, out uint v)
    {
        v = 0;
        var m = Digits.Match(s.Replace(".", "").Replace(",", ""));
        return m.Success && uint.TryParse(m.Value, NumberStyles.None, CultureInfo.InvariantCulture, out v);
    }
}
