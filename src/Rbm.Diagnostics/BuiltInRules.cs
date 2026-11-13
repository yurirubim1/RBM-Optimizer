using System.Diagnostics.Eventing.Reader;
using System.Management;
using System.Text.RegularExpressions;
using Rbm.Core;
using Rbm.Core.Diagnostics;
using Rbm.Core.Hardware;
using Rbm.Core.Telemetry;
using Microsoft.Win32;

namespace Rbm.Diagnostics;

/// <summary>Regra simples baseada em delegate.</summary>
public sealed class Rule : IDiagnosticRule
{
    private readonly Func<DiagnosticContext, IEnumerable<Finding>> _eval;
    public Rule(string id, string category, Func<DiagnosticContext, IEnumerable<Finding>> eval) { Id = id; Category = category; _eval = eval; }
    public string Id { get; }
    public string Category { get; }
    public Task<IEnumerable<Finding>> EvaluateAsync(DiagnosticContext context, CancellationToken ct) => Task.Run(() => _eval(context).ToList().AsEnumerable(), ct);
}

/// <summary>
/// Regras embutidas. Princípios: somente evidência medida; limiares documentados; associação ≠ causa;
/// não recomendar ações incompatíveis com o hardware.
/// </summary>
public static partial class BuiltInRules
{
    public static List<IDiagnosticRule> All() => new()
    {
        new Rule("storage.freespace", "Armazenamento", FreeSpace),
        new Rule("storage.health", "Armazenamento", DiskHealth),
        new Rule("storage.system-on-hdd", "Armazenamento", SystemOnHdd),
        new Rule("storage.saturation", "Armazenamento", DiskSaturation),
        new Rule("memory.pressure", "Memória", MemoryPressure),
        new Rule("memory.channels", "Memória", MemoryChannels),
        new Rule("memory.speed", "Memória", MemorySpeed),
        new Rule("memory.pagefile", "Memória", PageFile),
        new Rule("cpu.saturation", "CPU", CpuSaturation),
        new Rule("cpu.throttling", "CPU", CpuThrottling),
        new Rule("thermal.gpu", "Temperatura", GpuThermal),
        new Rule("thermal.cpu", "Temperatura", CpuThermal),
        new Rule("latency.dpc", "Latência", DpcInterrupt),
        new Rule("power.plan", "Energia", PowerPlan),
        new Rule("devices.problems", "Drivers", DeviceProblems),
        new Rule("gpu.driver-age", "Drivers", GpuDriverAge),
        new Rule("stability.events", "Estabilidade", StabilityEvents),
        new Rule("system.uptime", "Sistema", Uptime),
        new Rule("security.vbs", "Sistema", VbsInfo),
        new Rule("process.background-cpu", "Processos", BackgroundCpu),
        new Rule("memory.commit-exhaustion", "Memória", CommitExhaustion),
        new Rule("gpu.tdr", "GPU", GpuTdr),
    };

    // ---------- Esgotamento de memória virtual (evento 2004 do próprio Windows) ----------
    [GeneratedRegex(@"(?<proc>[\w\.\- ]+\.exe) \((?<pid>\d+)\) (?:consumiu|consumed) (?<bytes>\d+)", RegexOptions.IgnoreCase)]
    private static partial Regex ExhaustionRegex();

    private static IEnumerable<Finding> CommitExhaustion(DiagnosticContext c)
    {
        var since = DateTime.Now.AddDays(-30);
        var events = new List<(DateTime when, string proc, long bytes)>();
        try
        {
            string q = $"*[System[Provider[@Name='Microsoft-Windows-Resource-Exhaustion-Detector'] and EventID=2004 and TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, q));
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
                using (e)
                {
                    string msg = "";
                    try { msg = e.FormatDescription() ?? ""; } catch { }
                    var m = ExhaustionRegex().Match(msg);
                    events.Add((e.TimeCreated ?? DateTime.MinValue, m.Success ? m.Groups["proc"].Value.Trim() : "?", m.Success ? long.Parse(m.Groups["bytes"].Value) : 0));
                }
        }
        catch { yield break; }
        if (events.Count == 0) yield break;

        var sys = c.Profile.Volumes.FirstOrDefault(v => v.IsSystem);
        var top = events.GroupBy(e => e.proc).OrderByDescending(g => g.Count()).First();
        yield return new Finding
        {
            RuleId = "memory.commit-exhaustion", Category = "Memória",
            Title = "Faltou memória para os programas recentemente",
            Description = "O Windows registrou que a memória disponível para os programas (RAM + arquivo de paginação) acabou. Quando isso acontece, programas fecham sozinhos, a placa de vídeo pode travar e o PC pode congelar.",
            Evidence =
            {
                $"{events.Count} evento(s) 2004 (Resource-Exhaustion-Detector) nos últimos 30 dias; último em {events.Max(e => e.when):g}.",
                $"Maior consumidor apontado pelo Windows: {top.Key} ({top.Count()}x, pico {top.Max(x => x.bytes) / 1e9:0.0} GB de memória virtual).",
                sys is null ? "" : $"Unidade do sistema com {sys.FreeBytes / 1e9:0.0} GB livres ({sys.FreePercent:0}%): o arquivo de paginação gerenciado pelo sistema não consegue crescer muito.",
            },
            ProbableCause = $"Consumo excessivo de memória por {top.Key} (possível vazamento) somado a um limite de commit que não pode crescer.",
            Confidence = Confidence.High, Severity = Severity.High, ExpectedImpact = ImpactLevel.High,
            Solution = "Libere espaço no disco do sistema e evite manter o programa apontado aberto em várias janelas. Se continuar, aumente o arquivo de paginação.",
            Risks = "Nenhum (investigação e limpeza segura).", Effort = EffortLevel.Low, NavigateTo = "Memory",
        };
    }

    // ---------- TDR da GPU (LiveKernelEvent 117/141) e telas azuis por tipo ----------
    private static IEnumerable<Finding> GpuTdr(DiagnosticContext c)
    {
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in new[] { @"C:\ProgramData\Microsoft\Windows\WER\ReportArchive", @"C:\ProgramData\Microsoft\Windows\WER\ReportQueue" })
        {
            try
            {
                foreach (var d in Directory.EnumerateDirectories(root, "Kernel_*"))
                {
                    var code = Path.GetFileName(d).Split('_').ElementAtOrDefault(1) ?? "";
                    counts[code] = counts.GetValueOrDefault(code) + 1;
                }
            }
            catch { }
        }
        int tdr = counts.GetValueOrDefault("117") + counts.GetValueOrDefault("141");
        if (tdr == 0) yield break;
        var others = counts.Where(kv => kv.Key is not ("117" or "141" or "193")).Select(kv => $"0x{kv.Key.ToUpperInvariant()} ({kv.Value}x)").ToList();
        bool mixed = counts.Keys.Any(k => k is "7f" or "20001" or "1a" or "50" or "124" or "3b");
        yield return new Finding
        {
            RuleId = "gpu.tdr", Category = "GPU",
            Title = $"A placa de vídeo travou e foi reiniciada pelo Windows {tdr} vez(es)",
            Description = "Quando a placa de vídeo para de responder por cerca de 2 segundos, o Windows reinicia o driver (a tela pisca ou apaga e jogos fecham). Isso se chama TDR.",
            Evidence =
            {
                $"Relatórios LiveKernelEvent 117/141 arquivados pelo Windows: {tdr}.",
                others.Count > 0 ? $"Outras falhas de kernel registradas: {string.Join(", ", others)}." : "Sem outras telas azuis registradas.",
                c.Profile.Gpus.Any(g => g.IsVirtual) ? $"Adaptador(es) de vídeo virtual(is) instalado(s): {string.Join(", ", c.Profile.Gpus.Where(g => g.IsVirtual).Select(g => g.Name))}." : "",
            },
            ProbableCause = "Driver de vídeo, esgotamento de memória/VRAM, instabilidade de hardware (memória/overclock, fonte) ou conflito com drivers de vídeo virtuais. " +
                            (mixed ? "Telas azuis de tipos variados reforçam a hipótese de instabilidade de hardware." : ""),
            Confidence = Confidence.Medium, Severity = tdr >= 5 ? Severity.High : Severity.Medium, ExpectedImpact = ImpactLevel.High,
            Solution = "Comece resolvendo falta de memória ou de espaço, se houver. Depois: teste alguns dias sem XMP/EXPO e sem overclock, reinstale o driver de vídeo de forma limpa, remova drivers de vídeo virtuais sem uso e verifique a fonte de alimentação.",
            Risks = "Desativar XMP reduz o desempenho de memória temporariamente.", Effort = EffortLevel.Medium, NavigateTo = "Drivers", CorrelationOnly = true,
        };
    }

    // ---------- helpers ----------
    private static double Avg(IReadOnlyList<TelemetrySnapshot> s, Func<TelemetrySnapshot, double> f) => s.Count == 0 ? double.NaN : s.Average(f);
    private static double Max(IReadOnlyList<TelemetrySnapshot> s, Func<TelemetrySnapshot, double> f) => s.Count == 0 ? double.NaN : s.Max(f);
    private static double FractionAbove(IReadOnlyList<TelemetrySnapshot> s, Func<TelemetrySnapshot, double> f, double t) => s.Count == 0 ? 0 : s.Count(x => f(x) > t) / (double)s.Count;
    private static string Gb(long b) => $"{b / 1e9:0.0} GB";
    private static string Window(IReadOnlyList<TelemetrySnapshot> s) => s.Count < 2 ? "amostra única" : $"{s.Count} amostras em {(s[^1].Timestamp - s[0].Timestamp).TotalSeconds:0} s";

    // ---------- Armazenamento ----------
    private static IEnumerable<Finding> FreeSpace(DiagnosticContext c)
    {
        foreach (var v in c.Profile.Volumes)
        {
            bool critical = v.FreePercent < 5 || v.FreeBytes < 5_000_000_000;
            bool low = v.FreePercent < 12 || (v.IsSystem && v.FreeBytes < 20_000_000_000);
            if (!critical && !low) continue;
            yield return new Finding
            {
                RuleId = "storage.freespace", Category = "Armazenamento",
                Title = $"Pouco espaço livre em {v.Letter}" + (v.IsSystem ? " (unidade do sistema)" : ""),
                Description = "Volumes muito cheios degradam o desempenho (fragmentação em HDD, menos blocos livres para o controlador de SSD) e podem impedir atualizações, arquivo de paginação e caches de shader.",
                Evidence = { $"Livre: {Gb(v.FreeBytes)} de {Gb(v.TotalBytes)} ({v.FreePercent:0.0}%)." },
                ProbableCause = "Acúmulo de arquivos, caches, jogos ou pontos de restauração.",
                Confidence = Confidence.High, Severity = critical ? (v.IsSystem ? Severity.Critical : Severity.High) : Severity.Medium,
                ExpectedImpact = v.IsSystem ? ImpactLevel.Medium : ImpactLevel.Low,
                Solution = "Use a Limpeza para apagar temporários com segurança e veja em Espaço em disco o que mais ocupa. Mover jogos para outro disco também ajuda.",
                Risks = "Limpeza segura não remove dados pessoais. Exclusões manuais são irreversíveis.",
                Restart = RestartRequirement.None, Reversible = false, Effort = EffortLevel.Low, NavigateTo = "Cleanup",
            };
        }
    }

    private static IEnumerable<Finding> DiskHealth(DiagnosticContext c)
    {
        foreach (var d in c.Profile.Disks.Where(d => d.HealthStatus is not ("Saudável" or "Desconhecido") || d.OperationalStatus.Contains("Falha") || d.OperationalStatus.Contains("Degradado")))
            yield return new Finding
            {
                RuleId = "storage.health", Category = "Armazenamento",
                Title = $"Disco {d.Number} ({d.Model}) reporta problema de saúde",
                Description = "O próprio Windows (Storage Management) sinaliza o disco como não saudável ou com falha prevista.",
                Evidence = { $"HealthStatus: {d.HealthStatus}", $"OperationalStatus: {d.OperationalStatus}" },
                ProbableCause = "Desgaste, setores realocados, erros de mídia ou falha de controlador.",
                Confidence = Confidence.High, Severity = Severity.Critical, ExpectedImpact = ImpactLevel.High,
                Solution = "Faça backup imediatamente. Verifique os atributos SMART/NVMe na página Armazenamento e considere substituir a unidade.",
                Risks = "Risco de perda de dados.", Reversible = false, Effort = EffortLevel.High, NavigateTo = "Storage",
            };
    }

    private static IEnumerable<Finding> SystemOnHdd(DiagnosticContext c)
    {
        var sys = c.Profile.Disks.FirstOrDefault(d => d.IsSystemDisk);
        if (sys?.MediaType != StorageMediaType.Hdd) yield break;
        yield return new Finding
        {
            RuleId = "storage.system-on-hdd", Category = "Armazenamento",
            Title = "Windows instalado em disco rígido (HDD)",
            Description = "Latência de acesso aleatório de HDDs é ordens de grandeza maior que a de SSDs; inicialização, carregamento de apps e paginação ficam lentos.",
            Evidence = { $"Disco do sistema: {sys.Model} (MediaType=HDD, {sys.BusType})." },
            ProbableCause = "Hardware.", Confidence = Confidence.High, Severity = Severity.Medium, ExpectedImpact = ImpactLevel.High,
            Solution = "Migrar o sistema para um SSD (SATA ou NVMe) é a melhoria de responsividade mais significativa disponível. Nenhum ajuste de software compensa.",
            Risks = "Migração requer backup/clonagem.", Reversible = true, Effort = EffortLevel.High,
        };
    }

    private static IEnumerable<Finding> DiskSaturation(DiagnosticContext c)
    {
        if (c.Samples.Count < 10) yield break;
        var instances = c.Samples.SelectMany(s => s.Disks.Select(d => d.Instance)).Distinct();
        foreach (var inst in instances)
        {
            var series = c.Samples.Select(s => s.Disks.FirstOrDefault(d => d.Instance == inst)).Where(d => d is not null).Cast<DiskTelemetry>().ToList();
            if (series.Count < 10) continue;
            double busy = series.Count(d => d.ActivePercent > 90) / (double)series.Count;
            double lat = series.Average(d => Math.Max(d.AvgReadLatencyMs, d.AvgWriteLatencyMs));
            if (busy < 0.3 && lat < 50) continue;
            yield return new Finding
            {
                RuleId = "storage.saturation", Category = "Armazenamento",
                Title = $"Disco {inst} saturado durante a observação",
                Description = "Disco ocupado > 90% em boa parte do tempo ou latência média alta indica gargalo de E/S, que causa travamentos curtos e carregamentos lentos.",
                Evidence = { $"Ocupado > 90% em {busy:P0} de {Window(c.Samples)}.", $"Latência média: {lat:0.0} ms." },
                ProbableCause = "Atividade intensa de E/S (atualizações, indexação, antivírus, paginação ou aplicativos). Verifique os processos com mais E/S.",
                Confidence = Confidence.Medium, Severity = busy > 0.6 ? Severity.High : Severity.Medium, ExpectedImpact = ImpactLevel.Medium,
                Solution = "Identifique os processos com mais E/S na página Processos. Se coincidir com pouca RAM, investigue a paginação.",
                Risks = "Nenhum (apenas investigação).", Effort = EffortLevel.Medium, NavigateTo = "Processes", CorrelationOnly = true,
            };
        }
    }

    // ---------- Memória ----------
    private static IEnumerable<Finding> MemoryPressure(DiagnosticContext c)
    {
        if (c.Samples.Count < 5) yield break;
        double used = Avg(c.Samples, s => s.MemUsedPercent);
        double hf = Avg(c.Samples, s => s.HardFaultsPerSec);
        double commitRatio = Avg(c.Samples, s => s.CommitLimitBytes > 0 ? s.CommitBytes / (double)s.CommitLimitBytes : 0);
        if (used < 85 && commitRatio < 0.85) yield break;
        bool paging = hf > 200;
        yield return new Finding
        {
            RuleId = "memory.pressure", Category = "Memória",
            Title = paging ? "Pressão de memória com paginação ativa" : "Uso de memória elevado",
            Description = "Quando a RAM disponível se esgota, o Windows pagina para o disco; isso causa engasgos (stuttering) e lentidão, principalmente com HDD.",
            Evidence =
            {
                $"RAM em uso média: {used:0}% ({Window(c.Samples)}).",
                $"Commit médio: {commitRatio:P0} do limite.",
                $"Leituras de página (hard faults) médias: {hf:0}/s.",
            },
            ProbableCause = "Carga de trabalho maior que a RAM instalada, ou processo com consumo crescente (possível vazamento).",
            Confidence = paging ? Confidence.High : Confidence.Medium, Severity = paging ? Severity.High : Severity.Medium, ExpectedImpact = ImpactLevel.High,
            Solution = "Veja na página Memória/Processos quem consome mais (working set e bytes privados). Feche apps desnecessários; se for recorrente, considere mais RAM. 'Limpadores de RAM' não resolvem: apenas movem dados para o disco.",
            Risks = "Nenhum (investigação).", Effort = EffortLevel.Low, NavigateTo = "Memory",
        };
    }

    private static IEnumerable<Finding> MemoryChannels(DiagnosticContext c)
    {
        var m = c.Profile.Memory;
        if (m.EstimatedChannels != 1 || m.SlotsTotal < 2 || c.Profile.FormFactor == FormFactor.Laptop && m.Modules.Count == 0) yield break;
        yield return new Finding
        {
            RuleId = "memory.channels", Category = "Memória",
            Title = "Memória operando em canal único (single-channel)",
            Description = "Com um só canal, a largura de banda de memória cai pela metade. Afeta GPUs integradas fortemente e CPUs em jogos com muitos dados.",
            Evidence = { m.ChannelEvidence, $"Slots físicos reportados: {m.SlotsTotal}; módulos instalados: {m.Modules.Count}." },
            ProbableCause = "Apenas um módulo instalado, ou módulos no mesmo canal.",
            Confidence = m.Modules.Count == 1 ? Confidence.High : Confidence.Medium, Severity = Severity.Medium, ExpectedImpact = ImpactLevel.Medium,
            Solution = "Instale módulos em pares nos slots indicados pelo manual da placa-mãe (geralmente A2/B2). Meça antes/depois com o benchmark de memória.",
            Risks = "Alteração de hardware.", Effort = EffortLevel.High, Reversible = true,
        };
    }

    [GeneratedRegex(@"(?<!\d)(2133|2400|2666|2933|3000|3200|3466|3600|3733|3800|4000|4266|4400|4800|5200|5600|6000|6400|6800|7200|7600|8000)(?!\d)")]
    private static partial Regex PartSpeedRegex();

    private static IEnumerable<Finding> MemorySpeed(DiagnosticContext c)
    {
        var mods = c.Profile.Memory.Modules;
        if (mods.Count == 0) yield break;
        int configured = mods.Min(x => x.ConfiguredSpeedMTs);
        int rated = mods.Min(x => x.SpeedMTs);
        int partSpeed = mods.Select(x => PartSpeedRegex().Match(x.PartNumber)).Where(m => m.Success).Select(m => int.Parse(m.Value)).DefaultIfEmpty(0).Min();
        int target = Math.Max(rated, partSpeed);
        if (configured <= 0 || target <= configured + 100) yield break;
        yield return new Finding
        {
            RuleId = "memory.speed", Category = "Memória",
            Title = "Memória abaixo da velocidade anunciada (XMP/EXPO possivelmente desativado)",
            Description = "Kits de memória costumam vir em velocidade JEDEC conservadora; o perfil XMP (Intel) ou EXPO (AMD) precisa ser ativado na BIOS/UEFI.",
            Evidence =
            {
                $"Velocidade configurada: {configured} MT/s.",
                rated > configured ? $"Velocidade máxima reportada pelo SPD: {rated} MT/s." : "SPD não reporta velocidade maior.",
                partSpeed > 0 ? $"Part number indica {partSpeed} MT/s ({mods[0].PartNumber})." : "Part number sem velocidade identificável.",
            },
            ProbableCause = "Perfil XMP/EXPO não habilitado na BIOS/UEFI.",
            Confidence = rated > configured ? Confidence.Medium : Confidence.Low, Severity = Severity.Low, ExpectedImpact = ImpactLevel.Medium,
            Solution = "Ative XMP/EXPO na BIOS/UEFI (o RBM não altera a BIOS). Depois verifique a estabilidade (ex.: teste de memória) e compare o benchmark de memória antes/depois.",
            Risks = "Perfis XMP/EXPO são overclock suportado pelo fabricante da memória; alguns sistemas exigem ajuste de tensão ou ficam instáveis.",
            Restart = RestartRequirement.Reboot, Effort = EffortLevel.Medium, Reversible = true,
        };
    }

    private static IEnumerable<Finding> PageFile(DiagnosticContext c)
    {
        string? paging = null;
        using (var k = Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management"))
            paging = (k?.GetValue("PagingFiles") as string[])?.FirstOrDefault(s => !string.IsNullOrWhiteSpace(s));
        if (!string.IsNullOrWhiteSpace(paging)) yield break;
        yield return new Finding
        {
            RuleId = "memory.pagefile", Category = "Memória",
            Title = "Arquivo de paginação desativado",
            Description = "Sem arquivo de paginação, o limite de commit fica igual à RAM: aplicativos e jogos podem falhar com 'memória insuficiente' mesmo com RAM livre, e despejos de falha (minidumps) não são gravados.",
            Evidence = { "Valor 'PagingFiles' vazio em Memory Management." },
            ProbableCause = "Desativado manualmente (dica comum de 'otimização' sem benefício comprovado).",
            Confidence = Confidence.High, Severity = Severity.Medium, ExpectedImpact = ImpactLevel.Medium,
            Solution = "Reative o tamanho gerenciado pelo sistema em Configurações avançadas do sistema > Desempenho > Memória virtual.",
            Risks = "Nenhum relevante; usa espaço em disco.", Restart = RestartRequirement.Reboot, Effort = EffortLevel.Low,
        };
    }

    // ---------- CPU ----------
    private static IEnumerable<Finding> CpuSaturation(DiagnosticContext c)
    {
        if (c.Samples.Count < 10) yield break;
        double avg = Avg(c.Samples, s => s.CpuTotalPercent);
        double frac = FractionAbove(c.Samples, s => s.CpuTotalPercent, 90);
        if (avg < 80 && frac < 0.4) yield break;
        var top = c.Processes.OrderByDescending(p => p.CpuPercent).Take(5).Select(p => $"{p.Name} ({p.CpuPercent:0}%)");
        yield return new Finding
        {
            RuleId = "cpu.saturation", Category = "CPU",
            Title = "CPU saturada durante a observação",
            Description = "Uso total da CPU muito alto por período prolongado: tudo fica mais lento e jogos ficam limitados pela CPU.",
            Evidence = { $"Uso médio: {avg:0}%; acima de 90% em {frac:P0} do tempo ({Window(c.Samples)}).", $"Maiores consumidores no momento: {string.Join(", ", top)}." },
            ProbableCause = "Carga legítima, processo em segundo plano (atualização, antivírus, indexação) ou software indesejado.",
            Confidence = Confidence.Medium, Severity = Severity.Medium, ExpectedImpact = ImpactLevel.Medium,
            Solution = "Verifique os processos na página Processos (assinatura, caminho). Ajustes de registro não reduzem carga real de CPU.",
            Risks = "Encerrar processos pode causar perda de dados não salvos.", Effort = EffortLevel.Low, NavigateTo = "Processes",
        };
    }

    private static IEnumerable<Finding> CpuThrottling(DiagnosticContext c)
    {
        var lim = c.Samples.Where(s => s.CpuPerformanceLimitPercent.Available).ToList();
        if (lim.Count < 10) yield break;
        double frac = lim.Count(s => s.CpuPerformanceLimitPercent.Value < 90) / (double)lim.Count;
        if (frac < 0.25) yield break;
        double minLim = lim.Min(s => s.CpuPerformanceLimitPercent.Value);
        var temps = lim.Where(s => s.CpuTemperatureC.Available).Select(s => s.CpuTemperatureC.Value).ToList();
        yield return new Finding
        {
            RuleId = "cpu.throttling", Category = "CPU",
            Title = "O processador está sendo freado (throttling)",
            Description = "O contador '% Performance Limit' do Windows indica que a CPU esteve limitada (térmica, energia ou plataforma) abaixo do desempenho nominal.",
            Evidence =
            {
                $"Limite < 90% em {frac:P0} das amostras (mínimo {minLim:0}%).",
                temps.Count > 0 ? $"Temperatura da CPU: média {temps.Average():0} °C, máx {temps.Max():0} °C." : "Temperatura da CPU indisponível (sensor requer PawnIO + elevação).",
            },
            ProbableCause = temps.Count > 0 && temps.Max() > 90 ? "Provável limitação térmica." : "Limitação térmica, de energia (PL1/PPT, plano de energia) ou da plataforma — causa exata não determinável só por este contador.",
            Confidence = Confidence.Medium, Severity = Severity.High, ExpectedImpact = ImpactLevel.High,
            Solution = "Investigue a refrigeração ANTES de alterar limites de energia: poeira, pasta térmica, curva de ventoinhas, fluxo de ar do gabinete. Em notebooks, use superfície plana e o modo de desempenho do fabricante.",
            Risks = "Aumentar limites de energia sem resolver a temperatura pode agravar o problema.", Effort = EffortLevel.Medium, NavigateTo = "Thermal",
        };
    }

    // ---------- Temperatura ----------
    private static IEnumerable<Finding> GpuThermal(DiagnosticContext c)
    {
        var temps = c.Samples.SelectMany(s => s.Gpus).Where(g => g.TemperatureC.Available).ToList();
        if (temps.Count == 0) yield break;
        double max = temps.Max(g => g.TemperatureC.Value);
        var hs = temps.Where(g => g.HotspotC.Available).Select(g => g.HotspotC.Value).DefaultIfEmpty(double.NaN).Max();
        bool hot = max >= 85 || (!double.IsNaN(hs) && hs >= 100);
        if (!hot) yield break;
        yield return new Finding
        {
            RuleId = "thermal.gpu", Category = "Temperatura",
            Title = "Temperatura alta na GPU",
            Description = "GPUs reduzem clocks perto do limite térmico; temperaturas altas sustentadas indicam refrigeração insuficiente.",
            Evidence = { $"Núcleo: máx {max:0} °C.", double.IsNaN(hs) ? "Hotspot indisponível." : $"Hotspot/junção: máx {hs:0} °C." },
            ProbableCause = "Fluxo de ar insuficiente, poeira, curva de ventoinha conservadora ou pasta térmica degradada.",
            Confidence = Confidence.Medium, Severity = max >= 90 ? Severity.High : Severity.Medium, ExpectedImpact = ImpactLevel.Medium,
            Solution = "Limpe filtros e dissipadores, melhore o fluxo de ar e revise a curva de ventoinha no software oficial (Adrenalin/NVIDIA App/MSI Afterburner). Limitar FPS reduz calor e consumo.",
            Risks = "Baixo.", Effort = EffortLevel.Medium, NavigateTo = "Thermal",
        };
    }

    private static IEnumerable<Finding> CpuThermal(DiagnosticContext c)
    {
        var t = c.Samples.Where(s => s.CpuTemperatureC.Available).Select(s => s.CpuTemperatureC.Value).ToList();
        if (t.Count == 0 || t.Max() < 90) yield break;
        yield return new Finding
        {
            RuleId = "thermal.cpu", Category = "Temperatura",
            Title = "Temperatura alta na CPU",
            Description = "Temperaturas próximas do limite (Tjmax) fazem a CPU reduzir frequência.",
            Evidence = { $"Máx {t.Max():0} °C; média {t.Average():0} °C." },
            ProbableCause = "Refrigeração insuficiente ou carga sustentada alta.",
            Confidence = Confidence.Medium, Severity = Severity.High, ExpectedImpact = ImpactLevel.Medium,
            Solution = "Verifique cooler, pasta térmica e fluxo de ar antes de qualquer ajuste de energia.",
            Risks = "Baixo.", Effort = EffortLevel.Medium, NavigateTo = "Thermal",
        };
    }

    // ---------- Latência ----------
    private static IEnumerable<Finding> DpcInterrupt(DiagnosticContext c)
    {
        if (c.Samples.Count < 10) yield break;
        double dpc = Avg(c.Samples, s => s.DpcTimePercent);
        double isr = Avg(c.Samples, s => s.InterruptTimePercent);
        double dpcMax = Max(c.Samples, s => s.DpcTimePercent);
        if (dpc < 2 && isr < 2 && dpcMax < 10) yield break;
        yield return new Finding
        {
            RuleId = "latency.dpc", Category = "Latência",
            Title = "Drivers podem estar causando pequenos atrasos (DPC/ISR)",
            Description = "Drivers que gastam muito tempo em DPCs/ISRs podem causar estalos de áudio, engasgos e atraso de entrada. Estes contadores são apenas indicativos.",
            Evidence = { $"% DPC média {dpc:0.00}% (pico {dpcMax:0.0}%), % Interrupção média {isr:0.00}% ({Window(c.Samples)})." },
            ProbableCause = "Driver de rede, áudio, armazenamento, GPU ou ACPI — identificável apenas com rastreamento ETW.",
            Confidence = Confidence.Low, Severity = dpcMax > 10 ? Severity.Medium : Severity.Low, ExpectedImpact = ImpactLevel.Unknown,
            Solution = "Execute o Analisador de Latência (DPC/ISR via ETW) para identificar o driver responsável.",
            Risks = "Nenhum (investigação).", Effort = EffortLevel.Low, NavigateTo = "Latency", CorrelationOnly = true,
        };
    }

    // ---------- Energia ----------
    private static IEnumerable<Finding> PowerPlan(DiagnosticContext c)
    {
        var p = c.Profile.Power;
        bool saver = p.ActiveSchemeGuid == new Guid("a1841308-3541-4fab-bc81-f71556f20b4a");
        if (saver && c.Profile.FormFactor != FormFactor.Laptop)
            yield return new Finding
            {
                RuleId = "power.plan", Category = "Energia",
                Title = "Plano 'Economia de energia' ativo em desktop",
                Description = "Esse plano limita o estado máximo do processador e prioriza consumo, reduzindo desempenho.",
                Evidence = { $"Plano ativo: {p.ActiveSchemeName} ({p.ActiveSchemeGuid})." },
                ProbableCause = "Configuração manual ou de software de terceiros.",
                Confidence = Confidence.High, Severity = Severity.Medium, ExpectedImpact = ImpactLevel.Medium,
                Solution = "Use 'Equilibrado' (recomendado pela Microsoft e pelos fabricantes de CPU modernas) ou 'Alto desempenho'.",
                Risks = "Maior consumo de energia.", Effort = EffortLevel.Trivial, NavigateTo = "Power",
            };
        bool max = p.ActiveSchemeGuid == new Guid("e9a42b02-d5df-448d-aa00-03f14749eb61") || p.ActiveSchemeGuid == new Guid("8c5e7fda-e8bf-4a96-9a85-a6e23a8c635c");
        if (max && c.Profile.FormFactor == FormFactor.Laptop && !p.OnAcPower)
            yield return new Finding
            {
                RuleId = "power.plan", Category = "Energia",
                Title = "Plano de alto desempenho na bateria",
                Description = "Planos de desempenho máximo mantêm frequências altas e reduzem a autonomia, com ganho pequeno em uso típico.",
                Evidence = { $"Plano ativo: {p.ActiveSchemeName}; alimentação: bateria ({p.BatteryPercent}%)." },
                ProbableCause = "Configuração manual.",
                Confidence = Confidence.High, Severity = Severity.Low, ExpectedImpact = ImpactLevel.Low,
                Solution = "Use 'Equilibrado' na bateria ou o perfil 'Laptop' do RBM.",
                Risks = "Nenhum.", Effort = EffortLevel.Trivial, NavigateTo = "Power",
            };
    }

    // ---------- Drivers / dispositivos ----------
    private static IEnumerable<Finding> DeviceProblems(DiagnosticContext c)
    {
        var problems = new List<string>();
        try
        {
            using var s = new ManagementObjectSearcher("SELECT Name, PNPDeviceID, ConfigManagerErrorCode FROM Win32_PnPEntity WHERE ConfigManagerErrorCode <> 0");
            foreach (var o in s.Get())
            {
                int code = Convert.ToInt32(o["ConfigManagerErrorCode"]);
                if (code == 22) continue; // desativado pelo usuário
                problems.Add($"{o["Name"] ?? o["PNPDeviceID"]} — código {code}: {CmProblem(code)}");
            }
        }
        catch { yield break; }
        if (problems.Count == 0) yield break;
        yield return new Finding
        {
            RuleId = "devices.problems", Category = "Drivers",
            Title = $"{problems.Count} dispositivo(s) com problema no Gerenciador de Dispositivos",
            Description = "Dispositivos com erro podem estar sem driver, com driver incompatível ou com conflito de recursos.",
            Evidence = problems.Take(10).ToList(),
            ProbableCause = "Driver ausente, corrompido ou incompatível; hardware com defeito.",
            Confidence = Confidence.High, Severity = problems.Count > 2 ? Severity.High : Severity.Medium, ExpectedImpact = ImpactLevel.Unknown,
            Solution = "Abra a Central de Drivers para ver o Hardware ID e a fonte oficial do driver adequado.",
            Risks = "Instalar drivers de fontes não oficiais é arriscado; o RBM só indica fontes oficiais.", Effort = EffortLevel.Medium, NavigateTo = "Drivers",
        };
    }

    public static string CmProblem(int code) => code switch
    {
        1 => "dispositivo não configurado corretamente", 3 => "driver corrompido ou memória insuficiente", 10 => "o dispositivo não pode ser iniciado",
        12 => "recursos insuficientes/conflito", 14 => "requer reinicialização", 18 => "reinstalar drivers", 19 => "configuração do Registro corrompida",
        21 => "o Windows está removendo o dispositivo", 24 => "dispositivo ausente ou não funciona", 28 => "drivers não instalados",
        29 => "desativado pelo firmware", 31 => "o Windows não pode carregar os drivers", 32 => "serviço do driver desativado",
        37 => "o driver retornou falha", 39 => "driver corrompido ou ausente", 43 => "o dispositivo reportou problemas (comum em GPU/USB)",
        45 => "dispositivo não conectado", 48 => "driver bloqueado por problemas conhecidos", 52 => "assinatura digital não verificada",
        _ => "veja a documentação de códigos de erro do Gerenciador de Dispositivos",
    };

    private static IEnumerable<Finding> GpuDriverAge(DiagnosticContext c)
    {
        foreach (var g in c.Profile.Gpus.Where(g => !g.IsVirtual && g.DriverDate is not null))
        {
            var age = DateTime.Now - g.DriverDate!.Value;
            if (age.TotalDays < 365) continue;
            yield return new Finding
            {
                RuleId = "gpu.driver-age", Category = "Drivers",
                Title = $"Driver de vídeo com mais de {age.TotalDays / 365:0} ano(s)",
                Description = "Drivers de GPU recentes costumam trazer correções e otimizações para jogos novos. Isso NÃO significa que a versão mais nova seja sempre a melhor para o seu sistema.",
                Evidence = { $"{g.Name}: versão {g.DriverVersion}, data {g.DriverDate:d}." },
                ProbableCause = "Atualizações não aplicadas.",
                Confidence = Confidence.High, Severity = Severity.Low, ExpectedImpact = ImpactLevel.Unknown,
                Solution = "Consulte o site oficial do fabricante (link na Central de Drivers). Faça backup do driver atual antes de atualizar.",
                Risks = "Drivers novos podem trazer regressões; o backup permite reverter.", Effort = EffortLevel.Medium, NavigateTo = "Drivers",
            };
        }
    }

    // ---------- Estabilidade ----------
    private static IEnumerable<Finding> StabilityEvents(DiagnosticContext c)
    {
        var since = DateTime.Now.AddDays(-30);
        int kp41 = 0, bugcheck = 0, whea = 0;
        DateTime? last = null;
        try
        {
            string q = $"*[System[(Provider[@Name='Microsoft-Windows-Kernel-Power'] and EventID=41) or (Provider[@Name='Microsoft-Windows-WER-SystemErrorReporting'] and EventID=1001) or Provider[@Name='Microsoft-Windows-WHEA-Logger']] and System[TimeCreated[@SystemTime>='{since.ToUniversalTime():o}']]]";
            using var reader = new EventLogReader(new EventLogQuery("System", PathType.LogName, q));
            for (var e = reader.ReadEvent(); e is not null; e = reader.ReadEvent())
            {
                using (e)
                {
                    if (e.ProviderName == "Microsoft-Windows-Kernel-Power") kp41++;
                    else if (e.ProviderName == "Microsoft-Windows-WER-SystemErrorReporting") bugcheck++;
                    else if (e.ProviderName == "Microsoft-Windows-WHEA-Logger" && e.Level <= 3) whea++;
                    if (e.TimeCreated is { } t && (last is null || t > last)) last = t;
                }
            }
        }
        catch { yield break; }
        if (kp41 + bugcheck + whea == 0) yield break;
        yield return new Finding
        {
            RuleId = "stability.events", Category = "Estabilidade",
            Title = "O PC travou ou reiniciou sozinho nos últimos 30 dias",
            Description = "Desligamentos inesperados, telas azuis e erros de hardware (WHEA) indicam instabilidade que deve ser resolvida ANTES de qualquer otimização.",
            Evidence =
            {
                $"Kernel-Power 41 (desligamento inesperado): {kp41}",
                $"BugCheck/tela azul (WER 1001): {bugcheck}",
                $"Erros/avisos WHEA (hardware): {whea}",
                last is null ? "" : $"Último evento: {last:g}",
            },
            ProbableCause = whea > 0 ? "Erros WHEA apontam hardware (overclock/XMP instável, temperatura, PSU, PCIe)." : "Driver, energia ou hardware — verifique os detalhes no Visualizador de Eventos.",
            Confidence = Confidence.High, Severity = bugcheck + whea > 0 ? Severity.High : Severity.Medium, ExpectedImpact = ImpactLevel.High,
            Solution = "Antes de otimizar, resolva a instabilidade: desfaça overclocks recentes, teste a memória e verifique temperaturas e drivers.",
            Risks = "Nenhum.", Effort = EffortLevel.High,
        };
    }

    private static IEnumerable<Finding> Uptime(DiagnosticContext c)
    {
        var up = c.Profile.Os.Uptime;
        if (up.TotalDays < 14) yield break;
        yield return new Finding
        {
            RuleId = "system.uptime", Category = "Sistema",
            Title = $"Sistema ligado há {up.TotalDays:0} dias sem reiniciar",
            Description = "Com Inicialização Rápida, 'Desligar' não reinicia o kernel. Atualizações e drivers pendentes só entram em vigor após reiniciar.",
            Evidence = { $"Último boot: {c.Profile.Os.LastBoot:g}." },
            ProbableCause = "Uso contínuo/hibernação.",
            Confidence = Confidence.High, Severity = Severity.Info, ExpectedImpact = ImpactLevel.Low,
            Solution = "Reinicie (Reiniciar, não Desligar) periodicamente.", Risks = "Nenhum.", Effort = EffortLevel.Trivial,
        };
    }

    private static IEnumerable<Finding> VbsInfo(DiagnosticContext c)
    {
        if (c.Profile.Os.VbsRunning != true) yield break;
        yield return new Finding
        {
            RuleId = "security.vbs", Category = "Sistema",
            Title = "Segurança baseada em virtualização (VBS/HVCI) ativa",
            Description = "VBS e Integridade de Memória protegem o kernel contra malware. A Microsoft reconhece impacto de desempenho em alguns jogos. O RBM não desativa proteções de segurança.",
            Evidence = { $"VBS em execução: sim; HVCI: {(c.Profile.Os.HvciEnabled ? "sim" : "não")}." },
            ProbableCause = "Padrão em instalações limpas do Windows 11 com hardware compatível.",
            Confidence = Confidence.High, Severity = Severity.Info, ExpectedImpact = ImpactLevel.Unknown,
            Solution = "Apenas informativo. Se decidir avaliar o impacto, faça-o conscientemente pelas Configurações de Segurança do Windows e meça antes/depois.",
            Risks = "Desativar reduz a proteção contra malware em nível de kernel.", Effort = EffortLevel.Low, Reversible = true,
        };
    }

    // ---------- Processos ----------
    private static IEnumerable<Finding> BackgroundCpu(DiagnosticContext c)
    {
        var heavy = c.Processes.Where(p => p.CpuPercent >= 15 && p.Pid > 4).OrderByDescending(p => p.CpuPercent).Take(5).ToList();
        if (heavy.Count == 0) yield break;
        yield return new Finding
        {
            RuleId = "process.background-cpu", Category = "Processos",
            Title = "Processos com consumo relevante de CPU",
            Description = "Processos consumindo CPU continuamente competem com jogos e aplicações em primeiro plano.",
            Evidence = heavy.Select(p => $"{p.Name} (PID {p.Pid}): {p.CpuPercent:0}% CPU, {p.WorkingSetBytes / 1048576} MiB").ToList(),
            ProbableCause = "Tarefas em segundo plano ou aplicativos ativos. Instantâneo de um momento: confirme na página Processos.",
            Confidence = Confidence.Low, Severity = Severity.Low, ExpectedImpact = ImpactLevel.Unknown,
            Solution = "Avalie na página Processos (assinatura, caminho e impacto ao longo do tempo).", Risks = "Encerrar processos pode causar perda de dados.",
            Effort = EffortLevel.Low, NavigateTo = "Processes", CorrelationOnly = true,
        };
    }
}
