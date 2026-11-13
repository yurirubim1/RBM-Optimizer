using System.Globalization;
using Rbm.Core;
using Rbm.Core.Diagnostics;
using Rbm.Network.Adapters;
using Rbm.Network.Latency;
using Rbm.Network.Path;

namespace Rbm.Network.Diagnostics;

/// <summary>Limiares das regras (ajustáveis; padrões conservadores).</summary>
public sealed record NetworkDiagnosticThresholds
{
    public int WifiWeakQualityPercent { get; init; } = 40;
    public int WifiVeryWeakQualityPercent { get; init; } = 25;
    public int WifiWeakRssiDbm { get; init; } = -72;
    public int WifiVeryWeakRssiDbm { get; init; } = -80;
    public double LossPercent { get; init; } = 1.0;
    public double HighLossPercent { get; init; } = 5.0;
    public int MinSamplesForLoss { get; init; } = 10;
    public double LanJitterMs { get; init; } = 5.0;
    public double LanP95SpreadMs { get; init; } = 15.0;
    public int DriverOldDays { get; init; } = 3 * 365;
}

/// <summary>Entrada das regras. Tudo é opcional: regras sem dados simplesmente não disparam.</summary>
public sealed record NetworkDiagnosticInput
{
    public NetworkInventory? Inventory { get; init; }
    public IReadOnlyList<AdapterAdvancedSettings> Advanced { get; init; } = Array.Empty<AdapterAdvancedSettings>();
    public IReadOnlyList<TargetLatencyResult> Latency { get; init; } = Array.Empty<TargetLatencyResult>();
    public PathAnalysis? Path { get; init; }
    /// <summary>Variação de contadores por GUID de interface (duas leituras separadas por alguns minutos).</summary>
    public IReadOnlyDictionary<string, AdapterCounterDelta> CounterDeltas { get; init; } = new Dictionary<string, AdapterCounterDelta>();
    public FormFactor FormFactor { get; init; } = FormFactor.Unknown;
    public DateTime Now { get; init; } = DateTime.Now;
    public NetworkDiagnosticThresholds Thresholds { get; init; } = new();
}

/// <summary>
/// Regras de diagnóstico de rede (puras, sem I/O). Produzem <see cref="Finding"/> com evidências objetivas,
/// gravidade, confiança e recomendação em pt-BR. Nenhuma regra promete redução de ping.
/// </summary>
public static class NetworkDiagnostics
{
    public const string Category = "Rede";
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static IReadOnlyList<Finding> Evaluate(NetworkDiagnosticInput input)
    {
        var f = new List<Finding>();
        f.AddRange(WifiSignal(input));
        f.AddRange(LinkBelowCapability(input));
        f.AddRange(LossLocalVsRemote(input));
        f.AddRange(LanJitter(input));
        f.AddRange(PowerSavingOnDesktop(input));
        f.AddRange(ErrorsIncreasing(input));
        f.AddRange(DriverAgeAndSignature(input));
        f.AddRange(PathRealLoss(input));
        return f.OrderByDescending(x => x.Severity).ThenByDescending(x => x.Confidence).ToList();
    }

    private static string Ms(double v) => double.IsNaN(v) ? "—" : v.ToString("0.0", Pt) + " ms";
    private static string Pct(double v) => double.IsNaN(v) ? "—" : v.ToString("0.#", Pt) + "%";

    private static IEnumerable<NetworkAdapterInfo> ActivePhysical(NetworkDiagnosticInput i) =>
        i.Inventory?.Adapters.Where(a => a.Status == AdapterStatus.Up && !a.IsVirtual && a.Kind is AdapterKind.Ethernet or AdapterKind.WiFi)
        ?? Enumerable.Empty<NetworkAdapterInfo>();

    // ---------------------------------------------------------------- Wi-Fi fraco
    internal static IEnumerable<Finding> WifiSignal(NetworkDiagnosticInput i)
    {
        var t = i.Thresholds;
        foreach (var a in ActivePhysical(i).Where(a => a.Kind == AdapterKind.WiFi))
        {
            var w = a.Wifi;
            if (w is null || (w.SignalQualityPercent is null && w.RssiDbm is null)) continue;
            bool veryWeak = w.SignalQualityPercent < t.WifiVeryWeakQualityPercent || w.RssiDbm < t.WifiVeryWeakRssiDbm;
            bool weak = veryWeak || w.SignalQualityPercent < t.WifiWeakQualityPercent || w.RssiDbm < t.WifiWeakRssiDbm;
            if (!weak) continue;
            var ev = new List<string> { $"Adaptador: {a.Name} ({a.Description})" };
            if (w.Ssid is not null) ev.Add($"Rede: {w.Ssid}");
            if (w.SignalQualityPercent is { } q) ev.Add($"Qualidade do sinal: {q}%");
            if (w.RssiDbm is { } r) ev.Add($"RSSI: {r} dBm");
            if (w.Band is not null) ev.Add($"Banda: {w.Band}" + (w.Channel is { } c ? $", canal {c}" : ""));
            if (w.RxRateMbps is { } rx) ev.Add($"Taxa de recepção negociada: {rx:0.#} Mbps");
            yield return new Finding
            {
                RuleId = "net.wifi.weak-signal", Category = Category,
                Title = veryWeak ? "Sinal Wi-Fi muito fraco" : "Sinal Wi-Fi fraco",
                Description = "O sinal recebido do ponto de acesso está baixo. Sinal fraco reduz a taxa negociada e aumenta retransmissões, o que eleva latência, jitter e perda.",
                Evidence = ev,
                ProbableCause = "Distância/obstáculos até o roteador, interferência ou antena/posicionamento ruim.",
                Confidence = Confidence.High,
                Severity = veryWeak ? Severity.High : Severity.Medium,
                ExpectedImpact = veryWeak ? ImpactLevel.High : ImpactLevel.Medium,
                Solution = "Aproxime o PC do roteador ou o roteador do PC, evite paredes/metais no caminho, prefira 5 GHz/6 GHz a curta distância, " +
                           "considere repetidor mesh ou, para jogos, cabo Ethernet (a opção mais estável).",
                Risks = "Nenhum (mudanças físicas).",
                Restart = RestartRequirement.None, Reversible = true, Effort = EffortLevel.Low, NavigateTo = "Network",
            };
        }
    }

    // ---------------------------------------------------------------- Enlace abaixo da capacidade
    internal static IEnumerable<Finding> LinkBelowCapability(NetworkDiagnosticInput i)
    {
        foreach (var a in ActivePhysical(i).Where(a => a.Kind == AdapterKind.Ethernet && a.IsHardware && a.LinkSpeedBps is not null))
        {
            var adv = i.Advanced.FirstOrDefault(x => string.Equals(x.InterfaceGuid, a.InterfaceGuid, StringComparison.OrdinalIgnoreCase));
            var sd = adv?.Find(AdvancedKeywords.SpeedDuplex);
            long? cap = AdvancedKeywords.MaxSpeedFromSpeedDuplex(sd);
            var conf = Confidence.High;
            string capSource = "valores aceitos de \"Velocidade e Duplex\" do driver";
            if (cap is null)
            {
                long fromDesc = AdvancedKeywords.ParseSpeedText(a.Description);
                if (fromDesc == 0 && (a.Description.Contains("GbE", StringComparison.OrdinalIgnoreCase) || a.Description.Contains("Gigabit", StringComparison.OrdinalIgnoreCase)))
                    fromDesc = 1_000_000_000;
                if (fromDesc == 0) continue;
                cap = fromDesc; conf = Confidence.Medium; capSource = "descrição do adaptador";
            }
            long speed = a.LinkSpeedBps!.Value;
            if (speed >= cap.Value) continue;

            var ev = new List<string>
            {
                $"Adaptador: {a.Name} ({a.Description})",
                $"Velocidade negociada: {NetworkAdapterInfo.FormatSpeed(speed)}" + (a.FullDuplex == false ? " (half duplex)" : ""),
                $"Capacidade máxima: {NetworkAdapterInfo.FormatSpeed(cap.Value)} (fonte: {capSource})",
            };
            if (sd is not null) ev.Add($"Velocidade e Duplex configurado: {sd.DisplayValue} [{sd.CurrentRegistryValue}]");
            foreach (var k in new[] { "AutoDisableGigabit", "GigaLite", "PowerSavingMode", "EnableGreenEthernet", AdvancedKeywords.Eee })
                if (adv?.Find(k) is { } p && p.CurrentRegistryValue is { } v && v != "0") ev.Add($"{p.DisplayName}: {p.DisplayValue}");

            bool forced = sd?.CurrentRegistryValue is { } cur && cur != "0";
            yield return new Finding
            {
                RuleId = "net.link.below-capability", Category = Category,
                Title = $"Enlace Ethernet em {NetworkAdapterInfo.FormatSpeed(speed)} numa placa de {NetworkAdapterInfo.FormatSpeed(cap.Value)}",
                Description = "A placa negociou uma velocidade abaixo da sua capacidade. Isso limita a banda local e, em 100 Mbps, pode saturar com downloads/streams simultâneos.",
                Evidence = ev,
                ProbableCause = forced
                    ? "\"Velocidade e Duplex\" está fixado manualmente em vez de Auto."
                    : "Cabo com par danificado ou categoria inferior (Cat5 antigo usa só 2 pares), porta do roteador/switch de 100 Mbps, ou recurso de economia de energia do driver.",
                Confidence = conf,
                Severity = speed <= 100_000_000 && cap.Value >= 1_000_000_000 ? Severity.Medium : Severity.Low,
                ExpectedImpact = ImpactLevel.Medium,
                Solution = "Teste outro cabo (Cat5e ou superior, conectores bem crimpados) e outra porta do roteador; confirme que a porta do roteador é gigabit; " +
                           "mantenha \"Velocidade e Duplex\" em Auto Negociação. Em desktop, recursos como \"Auto Desactivar Gigabit\"/\"Gigabit Lite\" podem ser desligados.",
                Risks = "Trocar cabo/porta: nenhum. Alterar propriedades do driver reinicia o adaptador (queda breve).",
                Restart = RestartRequirement.None, Reversible = true, Effort = EffortLevel.Low, NavigateTo = "Network",
            };
        }
    }

    // ---------------------------------------------------------------- Perda local vs remota
    internal static IEnumerable<Finding> LossLocalVsRemote(NetworkDiagnosticInput i)
    {
        var t = i.Thresholds;
        bool Enough(TargetLatencyResult r) => r.Stats.Sent >= t.MinSamplesForLoss;
        var gw = i.Latency.FirstOrDefault(r => r.Target.Role == TargetRole.Gateway && Enough(r));
        var remote = i.Latency.Where(r => r.Target.Role is TargetRole.PublicDns or TargetRole.Custom && Enough(r)).ToList();
        var wifiDefault = i.Inventory?.DefaultAdapter?.Kind == AdapterKind.WiFi;

        if (gw is not null && gw.Stats.LossPercent >= t.LossPercent)
        {
            var ev = new List<string> { $"Gateway {gw.ResolvedAddress ?? gw.Target.Host}: perda {Pct(gw.Stats.LossPercent)} ({gw.Stats.Lost}/{gw.Stats.Sent}), média {Ms(gw.Stats.AvgMs)}, jitter {Ms(gw.Stats.JitterMs)}" };
            ev.AddRange(remote.Select(r => $"{r.Target.Label}: perda {Pct(r.Stats.LossPercent)}"));
            yield return new Finding
            {
                RuleId = "net.loss.local", Category = Category,
                Title = "Perda de pacotes já no primeiro salto (rede local)",
                Description = "Pacotes se perdem entre o PC e o roteador. Como tudo passa por esse trecho, a perda afeta toda a conexão.",
                Evidence = ev,
                ProbableCause = wifiDefault ? "Wi-Fi: sinal fraco, interferência ou congestionamento do canal." : "Cabo/porta defeituosos, roteador sobrecarregado ou problema de driver/placa.",
                Confidence = gw.Stats.Sent >= 50 ? Confidence.High : Confidence.Medium,
                Severity = gw.Stats.LossPercent >= t.HighLossPercent ? Severity.High : Severity.Medium,
                ExpectedImpact = ImpactLevel.High,
                Solution = wifiDefault
                    ? "Teste com cabo para confirmar; melhore o sinal (posição/banda/canal) ou reinicie o roteador. Alguns roteadores limitam respostas ICMP: confirme com o teste de caminho."
                    : "Troque o cabo e a porta do roteador, reinicie o roteador e verifique erros na placa. Alguns roteadores limitam respostas ICMP: confirme com o teste de caminho.",
                Risks = "Nenhum.", Restart = RestartRequirement.None, Reversible = true, Effort = EffortLevel.Low, NavigateTo = "Network",
            };
        }
        else if (gw is not null && gw.Stats.LossPercent < t.LossPercent)
        {
            var lossy = remote.Where(r => r.Stats.LossPercent >= t.LossPercent).ToList();
            // Exige perda em mais de um alvo remoto (ou no único existente) para não confundir com limitação ICMP de um servidor.
            if (lossy.Count == 0 || (remote.Count > 1 && lossy.Count < 2)) yield break;
            var ev = new List<string> { $"Gateway: perda {Pct(gw.Stats.LossPercent)} ({gw.Stats.Lost}/{gw.Stats.Sent})" };
            ev.AddRange(lossy.Select(r => $"{r.Target.Label}: perda {Pct(r.Stats.LossPercent)} ({r.Stats.Lost}/{r.Stats.Sent}), média {Ms(r.Stats.AvgMs)}"));
            var isp = i.Latency.FirstOrDefault(r => r.Target.Role == TargetRole.IspFirstHop && Enough(r));
            if (isp is not null) ev.Add($"Primeiro salto do provedor: perda {Pct(isp.Stats.LossPercent)}");
            yield return new Finding
            {
                RuleId = "net.loss.remote-only", Category = Category,
                Title = "Perda só fora da sua rede local",
                Description = "O trecho PC → roteador está limpo; a perda aparece nos destinos na internet. Ou seja, não é causada pelo seu PC nem pelo Wi-Fi/cabo.",
                Evidence = ev,
                ProbableCause = "Congestionamento ou falha na rede do provedor ou no trânsito até os servidores.",
                Confidence = Confidence.Medium,
                Severity = lossy.Max(r => r.Stats.LossPercent) >= t.HighLossPercent ? Severity.High : Severity.Medium,
                ExpectedImpact = ImpactLevel.Medium,
                Solution = "Rode o teste de caminho para localizar onde a perda começa e, se for no provedor, abra chamado com essas evidências. Ajustes no Windows não resolvem perda fora do PC.",
                Risks = "Nenhum.", Restart = RestartRequirement.None, Reversible = true, Effort = EffortLevel.Medium, NavigateTo = "Network",
            };
        }
    }

    // ---------------------------------------------------------------- Jitter na LAN
    internal static IEnumerable<Finding> LanJitter(NetworkDiagnosticInput i)
    {
        var t = i.Thresholds;
        var gw = i.Latency.FirstOrDefault(r => r.Target.Role == TargetRole.Gateway && r.Stats.Received >= t.MinSamplesForLoss);
        if (gw is null) yield break;
        var s = gw.Stats;
        double spread = s.P95Ms - s.MedianMs;
        if (!(s.JitterMs >= t.LanJitterMs || spread >= t.LanP95SpreadMs)) yield break;
        var def = i.Inventory?.DefaultAdapter;
        bool wifi = def?.Kind == AdapterKind.WiFi;
        var ev = new List<string>
        {
            $"Gateway {gw.ResolvedAddress ?? gw.Target.Host}: mediana {Ms(s.MedianMs)}, P95 {Ms(s.P95Ms)}, máx {Ms(s.MaxMs)}",
            $"Jitter (média |Δ|): {Ms(s.JitterMs)}; jitter RFC 3550: {Ms(s.JitterRfc3550Ms)}",
            $"Amostras: {s.Received}/{s.Sent}",
        };
        if (def is not null) ev.Add($"Conexão: {def.Name} ({(wifi ? "Wi-Fi" : def.Kind.ToString())})");
        if (wifi && def?.Wifi is { } w && w.Band is not null) ev.Add($"Wi-Fi: {w.Band}, canal {w.Channel?.ToString() ?? "—"}, sinal {w.SignalQualityPercent?.ToString() ?? "—"}%");
        yield return new Finding
        {
            RuleId = "net.jitter.lan", Category = Category,
            Title = wifi ? "Jitter alto na rede local (Wi-Fi)" : "Jitter alto até o roteador",
            Description = "A latência até o roteador varia muito entre pacotes. Em rede local saudável ela é estável (poucos ms).",
            Evidence = ev,
            ProbableCause = wifi
                ? "Interferência/congestionamento do canal Wi-Fi, varreduras de rede em segundo plano ou economia de energia do Wi-Fi."
                : "Roteador com CPU ocupada (respostas ICMP são de baixa prioridade), uso intenso da rede local ou problema de cabo.",
            Confidence = Confidence.Medium,
            Severity = wifi ? Severity.Medium : Severity.Low,
            ExpectedImpact = ImpactLevel.Medium,
            Solution = wifi
                ? "Teste com cabo para comparar; mude de canal/banda (5 GHz/6 GHz), afaste o roteador de outros aparelhos e reduza o uso simultâneo da rede."
                : "Verifique se algum dispositivo está saturando a rede (downloads/backup), reinicie o roteador e teste outro cabo. Lembre que o roteador responde ICMP com baixa prioridade.",
            Risks = "Nenhum.", Restart = RestartRequirement.None, Reversible = true, Effort = EffortLevel.Low, NavigateTo = "Network",
        };
    }

    // ---------------------------------------------------------------- Economia de energia em desktop
    internal static IEnumerable<Finding> PowerSavingOnDesktop(NetworkDiagnosticInput i)
    {
        if (i.FormFactor != FormFactor.Desktop) yield break;
        foreach (var a in ActivePhysical(i).Where(a => a.IsHardware))
        {
            var adv = i.Advanced.FirstOrDefault(x => string.Equals(x.InterfaceGuid, a.InterfaceGuid, StringComparison.OrdinalIgnoreCase));
            if (adv is null) continue;
            var ev = new List<string>();
            if (adv.Power.AllowComputerToTurnOffDevice == PowerSavingState.Enabled)
                ev.Add($"\"Permitir que o computador desligue este dispositivo\": habilitado (fonte: {adv.Power.Source})");
            foreach (var p in adv.Properties.Where(p => p.Known?.IsPowerSaving == true && p.CurrentRegistryValue is { } v && v != "0"))
                ev.Add($"{p.DisplayName} ({p.RegistryKeyword}): {p.DisplayValue}");
            if (ev.Count == 0) continue;
            ev.Insert(0, $"Adaptador: {a.Name} ({a.Description}) em um desktop");
            yield return new Finding
            {
                RuleId = "net.power.saving-desktop", Category = Category,
                Title = "Economia de energia da placa de rede ativa em desktop",
                Description = "Recursos de economia de energia da placa estão ligados. Em desktop o ganho de energia é mínimo; em alguns drivers eles causam reconexões após ociosidade ou atrasos ao despertar o enlace.",
                Evidence = ev,
                ProbableCause = "Padrão do driver/Windows.",
                Confidence = Confidence.Medium,
                Severity = Severity.Low,
                ExpectedImpact = ImpactLevel.Unknown,
                Solution = "Opcional: desative os itens listados (propriedades avançadas) se houver quedas ou picos após ociosidade. " +
                           "Não espere redução de ping mensurável na maioria dos casos — meça antes e depois.",
                Risks = "Aplicar reinicia o adaptador (queda breve). Reversível pelo histórico.",
                Restart = RestartRequirement.None, Reversible = true, Effort = EffortLevel.Low, NavigateTo = "Network", CorrelationOnly = true,
            };
        }
    }

    // ---------------------------------------------------------------- Erros/descartes crescendo
    internal static IEnumerable<Finding> ErrorsIncreasing(NetworkDiagnosticInput i)
    {
        foreach (var (guid, d) in i.CounterDeltas)
        {
            if (d.CounterReset || d.TotalErrorsAndDiscards <= 0) continue;
            var a = i.Inventory?.Adapters.FirstOrDefault(x => string.Equals(x.InterfaceGuid, guid, StringComparison.OrdinalIgnoreCase));
            if (a is { IsVirtual: true }) continue;
            long errors = d.ReceivedErrors + d.OutboundErrors;
            long packets = d.PacketsReceived + d.PacketsSent;
            double ratePct = packets > 0 ? errors * 100.0 / packets : double.NaN;
            var ev = new List<string>
            {
                $"Adaptador: {a?.Name ?? guid}",
                $"Intervalo observado: {d.Interval.TotalMinutes.ToString("0.#", Pt)} min; pacotes unicast: {packets}",
                $"Erros de recepção: +{d.ReceivedErrors}; erros de envio: +{d.OutboundErrors}",
                $"Descartes de recepção: +{d.ReceivedDiscards}; descartes de envio: +{d.OutboundDiscards}",
            };
            if (!double.IsNaN(ratePct) && errors > 0) ev.Add($"Taxa de erro: {ratePct.ToString("0.###", Pt)}% dos pacotes");
            bool hasErrors = errors > 0;
            yield return new Finding
            {
                RuleId = "net.errors.increasing", Category = Category,
                Title = hasErrors ? "Erros de pacote aumentando na placa de rede" : "Descartes de pacotes aumentando",
                Description = hasErrors
                    ? "A placa está registrando pacotes com erro (ex.: CRC) durante o uso — sinal típico de problema físico."
                    : "A pilha de rede está descartando pacotes. Descartes de recepção também incluem pacotes de protocolos não usados, então podem ser normais.",
                Evidence = ev,
                ProbableCause = hasErrors ? "Cabo/conector danificado, porta do roteador com defeito, interferência elétrica ou driver." : "Pacotes de protocolos desconhecidos/broadcast, buffers cheios sob carga alta ou filtros.",
                Confidence = hasErrors ? Confidence.Medium : Confidence.Low,
                Severity = hasErrors ? (ratePct >= 0.1 ? Severity.High : Severity.Medium) : Severity.Low,
                ExpectedImpact = hasErrors ? ImpactLevel.Medium : ImpactLevel.Unknown,
                Solution = hasErrors ? "Troque o cabo e a porta, verifique conectores e atualize o driver da placa." : "Observe por mais tempo; só investigue se coincidir com perda medida nos testes de latência.",
                Risks = "Nenhum.", Restart = RestartRequirement.None, Reversible = true, Effort = EffortLevel.Low, NavigateTo = "Network",
            };
        }
    }

    // ---------------------------------------------------------------- Driver antigo / não assinado
    internal static IEnumerable<Finding> DriverAgeAndSignature(NetworkDiagnosticInput i)
    {
        foreach (var a in i.Inventory?.Adapters.Where(a => a.IsHardware && !a.IsVirtual) ?? Enumerable.Empty<NetworkAdapterInfo>())
        {
            var d = a.Driver;
            if (d.IsSigned == false)
            {
                yield return new Finding
                {
                    RuleId = "net.driver.unsigned", Category = Category,
                    Title = "Driver de rede sem assinatura digital",
                    Description = "O Windows informa que o driver deste adaptador não é assinado.",
                    Evidence = new() { $"Adaptador: {a.Name} ({a.Description})", $"Driver: {d.Version ?? "—"} de {d.Provider ?? "—"} ({d.InfName ?? "INF desconhecido"})" },
                    ProbableCause = "Driver modificado, de teste ou instalado manualmente.",
                    Confidence = Confidence.High, Severity = Severity.Medium, ExpectedImpact = ImpactLevel.Unknown,
                    Solution = "Prefira o driver assinado do fabricante da placa/placa-mãe ou do Windows Update.",
                    Risks = "Trocar driver pode exigir reinício; crie um ponto de restauração antes.",
                    Restart = RestartRequirement.Reboot, Reversible = true, Effort = EffortLevel.Medium, NavigateTo = "Drivers",
                };
            }
            if (d.AgeDays(i.Now) is { } age && age >= i.Thresholds.DriverOldDays)
            {
                bool microsoft = d.Provider?.Contains("Microsoft", StringComparison.OrdinalIgnoreCase) == true;
                yield return new Finding
                {
                    RuleId = "net.driver.age", Category = Category,
                    Title = "Driver de rede antigo",
                    Description = "A data declarada do driver é antiga. Isso é apenas informativo: drivers antigos podem funcionar bem, mas versões novas costumam corrigir bugs de estabilidade e economia de energia.",
                    Evidence = new()
                    {
                        $"Adaptador: {a.Name} ({a.Description})",
                        $"Versão {d.Version ?? "—"}, data {d.Date:dd/MM/yyyy} (~{age / 365.0:0.#} anos), provedor {d.Provider ?? "—"}",
                    },
                    ProbableCause = microsoft ? "Driver da caixa do Windows (a data do INF da Microsoft costuma ser fixa e não reflete a versão)." : "Driver não atualizado desde a instalação.",
                    Confidence = microsoft ? Confidence.Low : Confidence.Medium,
                    Severity = Severity.Info, ExpectedImpact = ImpactLevel.Unknown,
                    Solution = "Se houver problemas de conexão, verifique a versão mais recente no site do fabricante da placa de rede/placa-mãe.",
                    Risks = "Atualizar driver pode exigir reinício; mantenha o atual exportado para rollback.",
                    Restart = RestartRequirement.Reboot, Reversible = true, Effort = EffortLevel.Medium, NavigateTo = "Drivers",
                };
            }
        }
    }

    // ---------------------------------------------------------------- Perda real no caminho
    internal static IEnumerable<Finding> PathRealLoss(NetworkDiagnosticInput i)
    {
        var p = i.Path;
        if (p is null || p.RealLossOrigin is null || p.Rounds < i.Thresholds.MinSamplesForLoss) yield break;
        var origin = p.Hops.FirstOrDefault(h => h.Interpretation == HopInterpretation.RealLossOrigin);
        var ev = new List<string> { $"Destino: {p.Target} ({p.DestinationAddress}), {p.Rounds} rodadas", $"Perda fim a fim: {Pct(p.EndToEndLossPercent)}" };
        if (origin is not null) ev.Add($"Origem: salto {origin.Hop.Ttl} ({origin.Hop.Address ?? "*"}{(origin.Hop.HostName is { } hn ? ", " + hn : "")}) — {HopClassifier.SegmentLabel(origin.Segment)}, perda {Pct(origin.Hop.Stats.LossPercent)}");
        int ignored = p.Hops.Count(h => h.Interpretation == HopInterpretation.IcmpRateLimitedLoss);
        if (ignored > 0) ev.Add($"{ignored} salto(s) com perda apenas aparente (limitação de ICMP) foram desconsiderados.");
        var seg = p.RealLossOrigin.Value;
        yield return new Finding
        {
            RuleId = "net.path.real-loss", Category = Category,
            Title = $"Perda real no caminho: origem em {HopClassifier.SegmentLabel(seg)}",
            Description = p.VerdictPt,
            Evidence = ev,
            ProbableCause = seg switch
            {
                PathSegment.Router => "Problema na rede local (Wi-Fi, cabo, roteador ou placa).",
                PathSegment.Isp => "Congestionamento ou falha na rede do provedor.",
                PathSegment.Destination => "Perda perto do servidor ou limitação de ICMP no destino.",
                _ => "Congestionamento no trânsito da internet.",
            },
            Confidence = p.Rounds >= 50 ? Confidence.High : Confidence.Medium,
            Severity = p.EndToEndLossPercent >= i.Thresholds.HighLossPercent ? Severity.High : Severity.Medium,
            ExpectedImpact = ImpactLevel.High,
            Solution = seg switch
            {
                PathSegment.Router => "Teste com cabo, troque cabo/porta, reinicie o roteador e verifique o sinal Wi-Fi.",
                PathSegment.Isp => "Abra chamado no provedor anexando este resultado (salto, endereço e perda). Ajustes no Windows não resolvem.",
                PathSegment.Destination => "Repita o teste via TCP na porta do serviço para confirmar; se persistir, o problema é do lado do servidor.",
                _ => "Fora do seu controle e do provedor imediato; se persistir em horários específicos, registre e informe o provedor/serviço.",
            },
            Risks = "Nenhum.", Restart = RestartRequirement.None, Reversible = true, Effort = EffortLevel.Low, NavigateTo = "Network",
        };
    }
}
