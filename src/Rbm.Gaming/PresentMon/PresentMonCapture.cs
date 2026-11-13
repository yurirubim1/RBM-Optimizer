using System.Security.Principal;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Security;

namespace Rbm.Gaming.PresentMon;

/// <summary>Permissões do usuário atual para captura ETW.</summary>
/// <param name="IsElevated">Processo atual é administrador elevado.</param>
/// <param name="IsPerformanceLogUser">O token atual contém "Usuários de log de desempenho" (S-1-5-32-559).</param>
/// <param name="CanCaptureUnelevated">Pode capturar sem o agente elevado.</param>
public sealed record FrameCaptureCapability(bool IsElevated, bool IsPerformanceLogUser, bool CanCaptureUnelevated, string MessagePt);

/// <summary>Resultado de uma captura.</summary>
/// <param name="CsvPath">CSV gerado (null se nenhum quadro foi capturado).</param>
/// <param name="StartedAt">Momento (relógio local) em que o PresentMon foi iniciado — origem aproximada dos tempos do CSV.</param>
public sealed record FrameCaptureResult(
    string? CsvPath, int ExitCode, bool TimedOut, string StdOut, string StdErr,
    DateTimeOffset StartedAt, TimeSpan Elapsed, string PresentMonVersion, string PresentMonSha256, string MessagePt);

/// <summary>Requisição já validada.</summary>
public sealed record ValidatedCaptureRequest(string ProcessName, int Seconds, string PresentMonPath);

/// <summary>
/// Captura de quadros via PresentMon (somente ETW — nada é injetado no jogo; seguro para anti-cheat).
/// <para>
/// <see cref="CaptureAsync"/> é "elevated-capable": é o método que o RBM.Agent deve chamar na op
/// <see cref="AgentOps.FrameCapture"/>. Também roda sem elevação quando o usuário pertence ao grupo
/// "Usuários de log de desempenho" (ver <see cref="GetCapability"/>). Validação estrita sempre:
/// nome de processo simples terminando em .exe, duração limitada, PresentMon com assinatura Authenticode
/// válida da Intel. Para evitar troca do binário entre verificação e execução (TOCTOU) e cache de assinatura,
/// o executável é copiado para uma pasta privada aleatória, verificado e executado a partir da cópia,
/// mantida aberta sem compartilhamento de escrita/exclusão durante a execução.
/// </para>
/// </summary>
public static class PresentMonCapture
{
    public const int MinSeconds = 1;
    public const int MaxSeconds = 600;
    public const int MaxProcessNameLength = 128;
    public const string SessionName = "RBMOptimizer_FrameCapture";

    private const string AllowedPunctuation = " _.-+()[]!&'~,#";

    public static FrameCaptureCapability GetCapability()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            var p = new WindowsPrincipal(id);
            bool admin = p.IsInRole(WindowsBuiltInRole.Administrator);
            bool plu = p.IsInRole(new SecurityIdentifier(WellKnownSidType.BuiltinPerformanceLoggingUsersSid, null));
            string msg = admin
                ? "Executando como administrador: captura disponível."
                : plu
                    ? "Seu usuário pertence ao grupo 'Usuários de log de desempenho': a captura funciona sem elevação (processos do seu usuário)."
                    : "A captura de FPS exige o agente elevado do RBM Optimizer (UAC) ou adicionar seu usuário ao grupo 'Usuários de log de desempenho' (requer novo logon).";
            return new FrameCaptureCapability(admin, plu, admin || plu, msg);
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException)
        {
            return new FrameCaptureCapability(false, false, false, $"Não foi possível verificar permissões: {ex.Message}");
        }
    }

    /// <summary>Valida o nome do processo: sem caminho, termina em .exe, sem curingas/aspas, não começa com '-'.</summary>
    public static OperationResult<string> ValidateProcessName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return OperationResult<string>.Fail("Informe o nome do processo (ex.: jogo.exe).", "invalid_process");
        string n = name.Trim();
        if (n.Length > MaxProcessNameLength) return OperationResult<string>.Fail("Nome do processo muito longo.", "invalid_process");
        if (n.IndexOfAny(['\\', '/', ':']) >= 0) return OperationResult<string>.Fail("Informe só o nome do processo, sem pasta.", "invalid_process");
        if (!n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || n.Length <= 4) return OperationResult<string>.Fail("O nome do processo precisa terminar em .exe.", "invalid_process");
        if (!char.IsLetterOrDigit(n[0])) return OperationResult<string>.Fail("O nome do processo precisa começar com letra ou número.", "invalid_process");
        foreach (char c in n)
            if (!char.IsLetterOrDigit(c) && AllowedPunctuation.IndexOf(c) < 0)
                return OperationResult<string>.Fail($"Caractere não permitido no nome do processo: '{c}'.", "invalid_process");
        if (n.Contains("..")) return OperationResult<string>.Fail("Nome do processo inválido.", "invalid_process");
        return OperationResult<string>.Ok(n);
    }

    /// <summary>
    /// Validação completa de uma <see cref="FrameCaptureRequest"/> (usar no agente antes de qualquer ação).
    /// O PresentMon deve ter assinatura Authenticode válida com assinante "Intel" — inclusive dentro de
    /// RbmPaths.Tools, porque essa pasta é gravável pelo usuário sem privilégios.
    /// </summary>
    public static OperationResult<ValidatedCaptureRequest> ValidateRequest(FrameCaptureRequest? request)
    {
        if (request is null) return OperationResult<ValidatedCaptureRequest>.Fail("Requisição vazia.", "invalid_request");
        var pn = ValidateProcessName(request.ProcessName);
        if (!pn.Success) return OperationResult<ValidatedCaptureRequest>.Fail(pn.Message, pn.ErrorCode);
        if (request.Seconds is < MinSeconds or > MaxSeconds)
            return OperationResult<ValidatedCaptureRequest>.Fail($"Duração deve estar entre {MinSeconds} e {MaxSeconds} s.", "invalid_duration");
        if (string.IsNullOrWhiteSpace(request.PresentMonPath) || !Path.IsPathFullyQualified(request.PresentMonPath))
            return OperationResult<ValidatedCaptureRequest>.Fail("Caminho do PresentMon precisa ser absoluto.", "invalid_path");
        string full;
        try { full = Path.GetFullPath(request.PresentMonPath); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return OperationResult<ValidatedCaptureRequest>.Fail($"Caminho inválido: {ex.Message}", "invalid_path"); }
        if (full.StartsWith(@"\\", StringComparison.Ordinal)) return OperationResult<ValidatedCaptureRequest>.Fail("Caminhos de rede não são permitidos.", "invalid_path");
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Path.GetFileName(full).StartsWith("PresentMon", StringComparison.OrdinalIgnoreCase))
            return OperationResult<ValidatedCaptureRequest>.Fail("O executável precisa ser o PresentMon (PresentMon*.exe).", "invalid_path");
        if (!File.Exists(full)) return OperationResult<ValidatedCaptureRequest>.Fail("PresentMon não encontrado.", "not_found");
        return OperationResult<ValidatedCaptureRequest>.Ok(new ValidatedCaptureRequest(pn.Value!, request.Seconds, full));
    }

    /// <summary>
    /// Captura quadros do processo por N segundos e grava CSV em <paramref name="outputDirectory"/>.
    /// MÉTODO PARA O AGENTE ELEVADO (op frames.capture) — também funciona sem elevação para membros de
    /// "Usuários de log de desempenho". O nome do arquivo é gerado aqui (aleatório) e a pasta de saída não pode
    /// ser reparse point (evita ataques de link simbólico quando executado elevado).
    /// </summary>
    public static async Task<OperationResult<FrameCaptureResult>> CaptureAsync(FrameCaptureRequest request, string outputDirectory, CancellationToken ct = default)
    {
        var v = ValidateRequest(request);
        if (!v.Success) return OperationResult<FrameCaptureResult>.Fail(v.Message, v.ErrorCode);
        var req = v.Value!;

        if (string.IsNullOrWhiteSpace(outputDirectory) || !Path.IsPathFullyQualified(outputDirectory))
            return OperationResult<FrameCaptureResult>.Fail("Pasta de saída precisa ser absoluta.", "invalid_output");
        string outDir = Path.GetFullPath(outputDirectory);
        if (outDir.StartsWith(@"\\", StringComparison.Ordinal)) return OperationResult<FrameCaptureResult>.Fail("Pasta de saída em rede não é permitida.", "invalid_output");
        Directory.CreateDirectory(outDir);
        if (IsReparsePointChain(outDir)) return OperationResult<FrameCaptureResult>.Fail("A pasta de saída (ou uma pasta acima) é um link/junção; recusado por segurança.", "invalid_output");

        string safeName = string.Concat(Path.GetFileNameWithoutExtension(req.ProcessName).Select(c => char.IsLetterOrDigit(c) ? c : '_'));
        string csv = Path.Combine(outDir, $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}_{Guid.NewGuid().ToString("N")[..8]}.csv");

        // Cópia privada verificada do PresentMon.
        string privDir = Path.Combine(Path.GetTempPath(), "RBM-PM-" + Guid.NewGuid().ToString("N"));
        string privExe = Path.Combine(privDir, "PresentMon.exe");
        FileStream? lockHandle = null;
        try
        {
            Directory.CreateDirectory(privDir);
            await using (var src = new FileStream(req.PresentMonPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (src.Length > PresentMonDownloader.MaxAssetBytes) return OperationResult<FrameCaptureResult>.Fail("PresentMon com tamanho inesperado.", "bad_binary");
                await using var dst = new FileStream(privExe, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                await src.CopyToAsync(dst, ct).ConfigureAwait(false);
            }
            // Mantém a cópia aberta só para leitura: impede escrita/exclusão/troca até o fim da execução.
            lockHandle = new FileStream(privExe, FileMode.Open, FileAccess.Read, FileShare.Read);
            var sig = SignatureVerifier.Verify(privExe);
            if (!PresentMonLocator.IsTrustedSignature(sig))
                return OperationResult<FrameCaptureResult>.Fail($"PresentMon rejeitado: {sig.Display}. É necessária assinatura válida da Intel.", "bad_signature");
            string sha = Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(lockHandle, ct).ConfigureAwait(false));
            var version = PresentMonLocator.ReadVersion(privExe, fallbackFileName: req.PresentMonPath);
            int major = version?.Major ?? 2;

            var args = PresentMonLocator.BuildCaptureArguments(major, req.ProcessName, csv, req.Seconds, SessionName);
            var started = DateTimeOffset.Now;
            var sw = System.Diagnostics.Stopwatch.StartNew();
            ProcessRunResult run;
            try
            {
                run = await ProcessRunner.RunAsync(privExe, args, TimeSpan.FromSeconds(req.Seconds + 30), ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Encerra a sessão ETW órfã (melhor esforço, sem o token cancelado).
                try { await ProcessRunner.RunAsync(privExe, PresentMonLocator.BuildTerminateSessionArguments(major, SessionName), TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false); }
                catch (Exception ex) when (ex is IOException or InvalidOperationException or System.ComponentModel.Win32Exception) { }
                throw;
            }
            sw.Stop();

            bool hasCsv = File.Exists(csv) && new FileInfo(csv).Length > 0;
            string msg;
            if (hasCsv) msg = $"Captura concluída ({req.Seconds} s).";
            else if (run.StdErr.Contains("access denied", StringComparison.OrdinalIgnoreCase) ||
                     (run.StdErr.Contains("elevat", StringComparison.OrdinalIgnoreCase) && !GetCapability().CanCaptureUnelevated))
                msg = "O PresentMon não conseguiu iniciar a sessão ETW: execute pelo agente elevado ou entre no grupo 'Usuários de log de desempenho'.";
            else msg = $"Nenhum quadro capturado de '{req.ProcessName}'. Verifique se o jogo estava aberto e renderizando, e se o nome do processo está correto (processos de outra conta exigem elevação).";
            var result = new FrameCaptureResult(hasCsv ? csv : null, run.ExitCode, run.TimedOut, Trim(run.StdOut), Trim(run.StdErr), started, sw.Elapsed,
                version?.ToString() ?? "?", sha, msg);
            return hasCsv ? OperationResult<FrameCaptureResult>.Ok(result, msg) : new OperationResult<FrameCaptureResult>(false, result, msg, "no_frames");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return OperationResult<FrameCaptureResult>.Fail($"Falha ao executar o PresentMon: {ex.Message}", "run_error");
        }
        finally
        {
            lockHandle?.Dispose();
            try { if (Directory.Exists(privDir)) Directory.Delete(privDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static string Trim(string s) => s.Length > 8000 ? s[..8000] + "…" : s;

    private static bool IsReparsePointChain(string dir)
    {
        try
        {
            for (var d = new DirectoryInfo(dir); d is not null; d = d.Parent)
                if (d.Exists && (d.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    // OneDrive marca pastas sincronizadas como reparse points (cloud files); só rejeita links/junções.
                    if (d.LinkTarget is not null) return true;
                }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return true; }
        return false;
    }
}
