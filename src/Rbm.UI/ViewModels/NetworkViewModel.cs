using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Diagnostics;
using Rbm.Data;
using Rbm.Network.Adapters;
using Rbm.Network.Agent;
using Rbm.Network.Diagnostics;
using Rbm.Network.Dns;
using Rbm.Network.Latency;
using Rbm.Network.Path;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.SystemPages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

public sealed partial class NetworkViewModel : PageViewModel
{
    private static readonly CultureInfo PtBr = CultureInfo.GetCultureInfo("pt-BR");
    private readonly ISystemInventory _inventory;
    public override string Key => "network";

    // ---- Estado bruto (dados reais) ----
    private NetworkInventory? _inv;
    private readonly Dictionary<string, AdapterAdvancedSettings> _advanced = new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, AdapterCounterDelta> _deltas = new();
    private IReadOnlyList<TargetLatencyResult> _lastLatency = Array.Empty<TargetLatencyResult>();
    private PathAnalysis? _lastPath;
    private bool _loaded;

    private CancellationTokenSource? _latCts, _pathCts, _dnsCts;
    private LatencyAccumulator? _acc;
    private readonly DispatcherTimer _latTimer = new() { Interval = TimeSpan.FromSeconds(1) };

    public NetworkViewModel(ISystemInventory inventory)
    {
        _inventory = inventory;
        _latTimer.Tick += (_, _) => RefreshLatencyCards();
    }

    [ObservableProperty] private int _selectedTab;

    // ---- Resumo em linguagem simples (banner) ----
    [ObservableProperty] private string _heroTitle = "Lendo a sua conexão…";
    [ObservableProperty] private string _heroText = "Verificando adaptadores e configurações da placa de rede. Nada é alterado.";
    [ObservableProperty] private string _heroChip = "Verificando…";
    [ObservableProperty] private string _heroTone = "info";
    [ObservableProperty] private string? _dnsVerdict;
    /// <summary>Linhas técnicas do adaptador (endereços, MAC, driver, contadores) — exibidas em "Detalhes técnicos".</summary>
    public ObservableCollection<InfoRow> AdapterTech { get; } = new();

    /// <summary>Atalho do banner: abre a aba de ping e inicia o teste com as opções atuais.</summary>
    [RelayCommand]
    private async Task QuickPingAsync()
    {
        SelectedTab = 1;
        if (!LatencyRunning) await StartLatencyAsync();
    }

    private void UpdateHero()
    {
        if (_inv is null) return;
        var def = _inv.Adapters.FirstOrDefault(a => a.IsDefaultRouteInterface);
        if (def is null)
        {
            HeroTitle = "Nenhuma conexão de rede ativa";
            HeroText = "Não há adaptador com rota para a internet. Verifique o cabo ou o Wi-Fi.";
            (HeroChip, HeroTone) = ("Sem conexão", "danger");
            return;
        }
        var item = new NetworkAdapterItem(def, _advanced.GetValueOrDefault(def.InterfaceGuid));
        string kind = def.Kind switch { AdapterKind.Ethernet => "cabo (Ethernet)", AdapterKind.WiFi => "Wi-Fi", _ => item.KindText };
        string wifi = def.Wifi is { Available: true, SignalQualityPercent: { } q } ? $" · sinal {q}%" : "";
        HeroTitle = $"Sua conexão: {kind} a {item.SpeedText}{wifi}" +
                    (item.IsBelowCapability ? $" — abaixo do possível ({NetworkAdapterInfo.FormatSpeed(item.CapabilityBps!.Value)})" : "");
        int n = Findings.Count;
        int serious = Findings.Count(f => f.Severity >= Severity.High);
        HeroText = (n == 0 ? "Não encontramos problemas na configuração da rede. " : $"Encontramos {n} ponto(s) de atenção — veja abaixo o que fazer. ")
                   + "Teste o ping para ver a qualidade da conexão com a internet.";
        (HeroChip, HeroTone) = serious > 0 ? ($"{serious} problema(s) importante(s)", "danger")
            : item.IsBelowCapability ? ("Velocidade do cabo limitada", "warn")
            : n > 0 ? ($"{n} ponto(s) de atenção", "warn")
            : ("Tudo certo", "ok");
    }

    // ---- Achados ----
    public ObservableCollection<Finding> Findings { get; } = new();
    public ObservableCollection<NetworkLinkAlert> LinkAlerts { get; } = new();
    [ObservableProperty] private string _findingsStatus = "Coletando dados reais da rede…";

    // ---- Adaptadores ----
    public ObservableCollection<NetworkAdapterItem> Adapters { get; } = new();
    [ObservableProperty] private NetworkAdapterItem? _selectedAdapter;
    public ObservableCollection<InfoRow> AdapterDetails { get; } = new();
    public ObservableCollection<InfoRow> OffloadRows { get; } = new();
    public ObservableCollection<NetworkAdvancedRow> AdvancedRows { get; } = new();
    public ObservableCollection<NetworkRollbackEntry> Rollbacks { get; } = new();
    [ObservableProperty] private string _adaptersStatus = "";
    [ObservableProperty] private string? _powerText;
    [ObservableProperty] private string? _powerHint;
    [ObservableProperty] private string? _advancedWarnings;
    [ObservableProperty] private bool _showVirtual;
    [ObservableProperty] private bool _isLoadingAdapters;

    // ---- Latência ----
    public ObservableCollection<NetworkLatencyCard> LatencyCards { get; } = new();
    public ObservableCollection<LatencyTarget> CustomTargets { get; } = new();
    [ObservableProperty] private string _customHost = "";
    [ObservableProperty] private bool _customTcp;
    [ObservableProperty] private string _customPort = "443";
    [ObservableProperty] private bool _continuous = true;
    [ObservableProperty] private int _sampleCount = 30;
    [ObservableProperty] private bool _latencyRunning;
    [ObservableProperty] private string _latencyStatus = "Pronto. O teste mede gateway, primeiro salto do provedor, DNS públicos e seus alvos.";
    [ObservableProperty] private string? _latencyNotes;
    public IReadOnlyList<int> SampleCountOptions { get; } = new[] { 20, 30, 60, 120 };

    // ---- Caminho ----
    [ObservableProperty] private string _pathHost = "1.1.1.1";
    [ObservableProperty] private int _pathRounds = 20;
    [ObservableProperty] private bool _pathRunning;
    [ObservableProperty] private string _pathStatus = "Informe um destino (ex.: servidor do jogo) e clique em Analisar.";
    [ObservableProperty] private string? _pathVerdict;
    public ObservableCollection<NetworkSegmentRow> PathSegments { get; } = new();
    public ObservableCollection<NetworkHopRow> PathHops { get; } = new();
    public ObservableCollection<string> PathNotes { get; } = new();
    public IReadOnlyList<int> PathRoundOptions { get; } = new[] { 10, 20, 60 };

    // ---- DNS ----
    [ObservableProperty] private string _dnsCustom = "";
    [ObservableProperty] private bool _dnsRunning;
    [ObservableProperty] private string _dnsStatus = "Mede o tempo de resposta dos servidores DNS configurados e públicos. Nada é alterado no sistema.";
    [ObservableProperty] private string? _dnsSystemText;
    public ObservableCollection<NetworkDnsRow> DnsRows { get; } = new();
    public ObservableCollection<string> DnsNotes { get; } = new();

    // ---- Histórico ----
    public ObservableCollection<NetworkHistoryRow> History { get; } = new();
    [ObservableProperty] private string _historyStatus = "";

    // =====================================================================================
    public override async Task OnNavigatedToAsync()
    {
        if (!_loaded)
        {
            _loaded = true;
            await LoadAdaptersAsync();
            await LoadHistoryAsync();
        }
    }

    public override void OnNavigatedFrom()
    {
        // Testes de rede não continuam em segundo plano fora da página.
        _latCts?.Cancel();
        _pathCts?.Cancel();
        _dnsCts?.Cancel();
    }

    partial void OnSelectedAdapterChanged(NetworkAdapterItem? value) => BuildAdapterDetails();
    partial void OnShowVirtualChanged(bool value) => BuildAdapterList();

    // =============================== Adaptadores ===============================
    [RelayCommand]
    private async Task LoadAdaptersAsync()
    {
        if (IsLoadingAdapters) return;
        IsLoadingAdapters = true;
        AdaptersStatus = "Lendo adaptadores e propriedades do driver…";
        using var _ = App.Tasks.Begin("Inventário de rede");
        try
        {
            var invTask = NetworkAdapterInventory.EnumerateAsync(new AdapterQueryOptions());
            var advTask = AdvancedPropertyService.ReadAllAsync();
            var inv = await invTask;
            IReadOnlyList<AdapterAdvancedSettings> adv;
            try { adv = await advTask; }
            catch (Exception ex) { App.Log(ex, "Rede: propriedades avançadas"); adv = Array.Empty<AdapterAdvancedSettings>(); }

            // Delta de contadores de erro desde a leitura anterior (para a regra de erros crescentes).
            if (_inv is not null)
            {
                var d = new Dictionary<string, AdapterCounterDelta>(StringComparer.OrdinalIgnoreCase);
                foreach (var a in inv.Adapters)
                {
                    var prev = _inv.Adapters.FirstOrDefault(x => string.Equals(x.InterfaceGuid, a.InterfaceGuid, StringComparison.OrdinalIgnoreCase));
                    if (prev?.Counters is { } b && a.Counters is { } c) d[a.InterfaceGuid] = AdapterCounterDelta.Compute(b, c);
                }
                _deltas = d;
            }
            _inv = inv;
            foreach (var s in adv) _advanced[s.InterfaceGuid] = s;

            BuildAdapterList();
            AdaptersStatus = $"{inv.Adapters.Count} adaptador(es) · lido em {inv.At.ToLocalTime():HH:mm:ss}" +
                             (inv.Warnings.Count > 0 ? " · " + string.Join(" · ", inv.Warnings) : "");
            EvaluateFindings();
        }
        catch (Exception ex)
        {
            App.Log(ex, "Rede: inventário");
            AdaptersStatus = "Falha ao ler adaptadores: " + ex.Message;
            App.Notify.Error("Rede", "Falha ao ler adaptadores: " + ex.Message);
        }
        finally { IsLoadingAdapters = false; }
    }

    private void BuildAdapterList()
    {
        if (_inv is null) return;
        string? keep = SelectedAdapter?.Info.InterfaceGuid;
        Adapters.Clear();
        LinkAlerts.Clear();
        var items = _inv.Adapters
            .Select(a => new NetworkAdapterItem(a, _advanced.GetValueOrDefault(a.InterfaceGuid)))
            .OrderByDescending(a => a.IsDefault).ThenByDescending(a => a.IsUp).ThenBy(a => a.Info.IsVirtual).ThenBy(a => a.Name)
            .ToList();
        foreach (var a in items)
        {
            if (!ShowVirtual && a.Info.IsVirtual && !a.IsDefault) continue;
            Adapters.Add(a);
        }
        foreach (var a in items.Where(a => a.IsBelowCapability))
        {
            var sd = a.Advanced?.Find(AdvancedKeywords.SpeedDuplex);
            bool forced = sd?.CurrentRegistryValue is { } cur && cur != "0";
            var causes = new List<string>();
            if (forced) causes.Add($"\"Velocidade e Duplex\" está fixado em \"{sd!.DisplayValue}\" em vez de Auto Negociação.");
            causes.Add("Cabo antigo/Cat5 ou com um par danificado: gigabit usa os 4 pares; com 2 pares o enlace cai para 100 Mbps. Use Cat5e/Cat6 bem crimpado.");
            causes.Add("Porta do roteador/switch limitada a 100 Mbps, ou porta com defeito: teste outra porta e confirme que ela é gigabit.");
            causes.Add("Falha de auto-negociação: os dois lados devem estar em Auto (não force velocidade só de um lado).");
            var ps = a.Advanced?.Properties.Where(p => p.Known?.IsPowerSaving == true && p.CurrentRegistryValue is { } v && v != "0")
                .Select(p => $"{p.DisplayName} = {p.DisplayValue}").ToList();
            causes.Add("Recursos de economia de energia do driver (Green Ethernet, EEE, \"Auto Disable Gigabit\"/GigaLite) podem reduzir a velocidade" +
                       (ps is { Count: > 0 } ? ": ativos aqui → " + string.Join("; ", ps) + "." : "."));
            LinkAlerts.Add(new NetworkLinkAlert(
                $"{a.Name}: enlace em {a.SpeedText}, mas a placa suporta {NetworkAdapterInfo.FormatSpeed(a.CapabilityBps!.Value)}",
                $"{a.Description}. A banda local fica limitada a {a.SpeedText} (downloads, streaming e transferências na rede). " +
                "Isso não muda o ping do jogo por si só, mas pode saturar o enlace quando há tráfego simultâneo.",
                causes));
        }
        SelectedAdapter = Adapters.FirstOrDefault(a => a.Info.InterfaceGuid == keep) ?? Adapters.FirstOrDefault();
        BuildAdapterDetails();
    }

    private void BuildAdapterDetails()
    {
        AdapterDetails.Clear(); AdapterTech.Clear(); OffloadRows.Clear(); AdvancedRows.Clear();
        PowerText = null; PowerHint = null; AdvancedWarnings = null;
        var a = SelectedAdapter;
        if (a is null) return;
        var i = a.Info;
        AdapterDetails.Add(new("Conexão", $"{a.KindText} · {a.StatusText}", i.IsDefaultRouteInterface ? "É por aqui que o PC acessa a internet (conexão principal)" : null));
        AdapterDetails.Add(new("Velocidade do link", a.SpeedText, a.CapabilityText + (i.ReceiveLinkSpeedBps is { } rx && i.TransmitLinkSpeedBps is { } tx && rx != tx
            ? $" · RX {NetworkAdapterInfo.FormatSpeed(rx)} / TX {NetworkAdapterInfo.FormatSpeed(tx)}" : "")));
        if (i.Ip is { } ip)
        {
            AdapterTech.Add(new("IPv4", ip.IPv4Addresses.Count > 0 ? string.Join(", ", ip.IPv4Addresses) : "—",
                (ip.DhcpEnabled == true ? "DHCP" : ip.DhcpEnabled == false ? "IP fixo" : "") + (ip.MtuIPv4 is { } m ? $" · MTU {m}" : "")));
            if (ip.IPv6Addresses.Count > 0) AdapterTech.Add(new("IPv6", string.Join(", ", ip.IPv6Addresses.Take(2)), ip.IPv6Addresses.Count > 2 ? $"+{ip.IPv6Addresses.Count - 2}" : null));
            AdapterTech.Add(new("Gateway", ip.Gateways.Count > 0 ? string.Join(", ", ip.Gateways) : "—"));
            AdapterTech.Add(new("DNS", ip.DnsServers.Count > 0 ? string.Join(", ", ip.DnsServers) : "—", ip.DnsSuffix));
        }
        if (i.MacAddress is { } mac) AdapterTech.Add(new("MAC", mac));
        var d = i.Driver;
        AdapterTech.Add(new("Driver", d.Version is null ? "—" : $"{d.Version}" + (d.Date is { } dd ? $" ({dd:dd/MM/yyyy})" : ""),
            string.Join(" · ", new[] { d.Provider, d.InfName, d.IsSigned == true ? "assinado" + (d.Signer is null ? "" : $" por {d.Signer}") : d.IsSigned == false ? "NÃO assinado" : null }
                .Where(s => !string.IsNullOrWhiteSpace(s)))));
        if (i.Counters is { } c)
            AdapterTech.Add(new("Erros / descartes", $"RX {c.ReceivedErrors?.ToString() ?? "—"} / {c.ReceivedDiscards?.ToString() ?? "—"} · TX {c.OutboundErrors?.ToString() ?? "—"} / {c.OutboundDiscards?.ToString() ?? "—"}",
                _deltas.TryGetValue(i.InterfaceGuid, out var dl) ? $"Desde a leitura anterior ({dl.Interval.TotalSeconds:0} s): +{dl.TotalErrorsAndDiscards}" : "Atualize para comparar com uma nova leitura"));
        if (i.Wifi is { } w)
        {
            if (w.Available)
                AdapterDetails.Add(new("Wi-Fi", $"{w.Ssid ?? "(SSID oculto/sem permissão)"} · {w.SignalQualityPercent?.ToString() ?? "—"}% · {w.RssiDbm?.ToString() ?? "—"} dBm",
                    $"Canal {w.Channel?.ToString() ?? "—"} · {w.Band ?? "—"} · {w.PhyType ?? "—"} · RX {w.RxRateMbps?.ToString("0") ?? "—"} / TX {w.TxRateMbps?.ToString("0") ?? "—"} Mbps"));
            else AdapterDetails.Add(new("Wi-Fi", "Indisponível", w.UnavailableReason));
        }
        foreach (var n in i.Notes) AdapterDetails.Add(new("Nota", n));

        var adv = a.Advanced;
        if (adv is null)
        {
            AdvancedWarnings = "Propriedades avançadas não disponíveis para este adaptador (adaptadores virtuais/desconectados podem não expor).";
            return;
        }
        var pw = adv.Power;
        PowerText = pw.AllowComputerToTurnOffDevice switch
        {
            PowerSavingState.Enabled => "\"Permitir que o computador desligue este dispositivo\": ATIVADO",
            PowerSavingState.Disabled => "\"Permitir que o computador desligue este dispositivo\": desativado",
            PowerSavingState.Unsupported => "Gerenciamento de energia não suportado pelo dispositivo",
            _ => "Gerenciamento de energia: desconhecido",
        } + $" (fonte: {pw.Source})";
        if (pw.UnavailableReason is { } why)
            PowerHint = $"Leitura parcial sem privilégios: {why.TrimEnd().TrimEnd('.')}. Use \"Ler como administrador\" para a leitura completa.";

        var s = adv.Summary;
        void Off(string label, string? v) { if (!string.IsNullOrWhiteSpace(v)) OffloadRows.Add(new(label, v)); }
        Off("Velocidade e duplex", s.SpeedDuplex);
        Off("RSS", s.RssEnabled is { } r ? (r ? "Ativado" : "Desativado") + (s.RssReceiveQueues is { } q ? $" · {q} fila(s)" : "") : null);
        Off("RSC IPv4 / IPv6", s.RscIPv4Enabled is null && s.RscIPv6Enabled is null ? null : $"{OnOff(s.RscIPv4Enabled)} / {OnOff(s.RscIPv6Enabled)}");
        Off("Moderação de interrupção", s.InterruptModeration);
        Off("Controle de fluxo", s.FlowControl);
        Off("EEE", s.EnergyEfficientEthernet);
        Off("Green Ethernet", s.GreenEthernet);
        Off("LSO IPv4 / IPv6", s.LargeSendOffloadIPv4 is null && s.LargeSendOffloadIPv6 is null ? null : $"{s.LargeSendOffloadIPv4 ?? "—"} / {s.LargeSendOffloadIPv6 ?? "—"}");
        Off("Buffers RX / TX", s.ReceiveBuffers is null && s.TransmitBuffers is null ? null : $"{s.ReceiveBuffers ?? "—"} / {s.TransmitBuffers ?? "—"}");

        foreach (var p in adv.Properties.OrderBy(p => p.Known is null).ThenBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase))
            AdvancedRows.Add(new NetworkAdvancedRow(p));
        if (adv.Warnings.Count > 0) AdvancedWarnings = string.Join(" · ", adv.Warnings);
    }

    private static string OnOff(bool? b) => b is null ? "—" : b.Value ? "Ativado" : "Desativado";

    [RelayCommand]
    private async Task ReadElevatedAsync()
    {
        var a = SelectedAdapter;
        if (a is null) return;
        await RunBusy("Leitura elevada das propriedades…", async () =>
        {
            var r = await AgentCall.InvokeAsync(AgentOps.NetAdvPropRead, new AdvancedPropertyReadRequest(a.Info.InterfaceGuid), "Leitura elevada", TimeSpan.FromSeconds(60));
            if (r is null) return;
            var s = AgentCall.Read<AdapterAdvancedSettings>(r);
            if (s is null) { App.Notify.Warning("Leitura elevada", "O agente não retornou propriedades para este adaptador."); return; }
            _advanced[s.InterfaceGuid] = s;
            BuildAdapterList();
            EvaluateFindings();
            App.Notify.Success("Leitura elevada concluída", $"{s.Properties.Count} propriedade(s) lida(s) de {s.AdapterName}.");
        });
    }

    [RelayCommand]
    private async Task ApplyAdvancedAsync(NetworkAdvancedRow? row)
    {
        var a = SelectedAdapter;
        if (row is null || a is null || !row.IsChanged || row.NewValue is not { } newValue) return;
        if (row.IsBlocked) { App.Notify.Warning("Propriedade bloqueada", "Endereço de rede/VLAN não são alterados pelo RBM Optimizer."); return; }
        var v = AdvancedPropertyValidator.ValidateValue(row.Prop, newValue);
        if (!v.Success) { App.Notify.Warning("Valor não aceito pelo driver", v.Message); return; }

        string details =
            $"Adaptador: {a.Name} ({a.Description})\nPropriedade: {row.DisplayName} [{row.Keyword}]\nValor atual: {row.CurrentText}\nNovo valor: {row.NewValueDisplay}\n\n" +
            (row.Description is { } desc ? desc + "\n\n" : "") +
            "O valor anterior fica registrado e você pode usar \"Reverter\" nesta página. " +
            "Mudanças em propriedades do driver não garantem redução de ping: meça a latência antes e depois.";
        string msg = AdvancedPropertyService.ResetWarning + (a.IsDefault ? " Este é o adaptador da sua conexão principal." : "");
        if (!await App.Dialogs.ConfirmAsync("Alterar propriedade do adaptador", msg, details, "Aplicar (reinicia o adaptador)")) return;

        await RunBusy("Aplicando propriedade via RBM Agent…", async () =>
        {
            var req = new AdvancedPropertySetRequest(a.Info.InterfaceGuid, row.Keyword, newValue, row.Prop.CurrentRegistryValue);
            var r = await AgentCall.InvokeAsync(AgentOps.NetAdvPropSet, req, "Alterar propriedade", TimeSpan.FromSeconds(90));
            if (r is null) { await SysUi.AuditAsync("net.advprop.set", $"{a.Name} {row.Keyword} → {newValue}: não executado (agente indisponível/recusou)", false); return; }
            var resp = AgentCall.Read<AdvancedPropertySetResponse>(r);
            if (resp is null || !resp.Success)
            {
                string why = resp?.Message ?? "Resposta vazia do agente.";
                App.Notify.Error("Propriedade não alterada", why + (resp?.ErrorCode is { } code ? $" ({code})" : ""));
                await SysUi.AuditAsync("net.advprop.set", $"{a.Name} {row.Keyword} → {newValue}: falhou: {why}", false);
                return;
            }
            if (resp.RollbackRequest is { } rb)
                Rollbacks.Insert(0, new NetworkRollbackEntry(resp.AdapterName ?? a.Name, row.DisplayName,
                    resp.PreviousDisplayValue ?? resp.PreviousRegistryValue ?? "?", resp.NewDisplayValue ?? resp.NewRegistryValue ?? newValue, DateTimeOffset.Now, rb));
            await SysUi.AuditAsync("net.advprop.set",
                $"{resp.AdapterName ?? a.Name} {row.Keyword}: {resp.PreviousRegistryValue} → {resp.NewRegistryValue} (verificado: {(resp.Verified ? "sim" : "não")})", true);
            if (resp.Verified) App.Notify.Success("Propriedade alterada", $"{row.DisplayName}: {resp.NewDisplayValue ?? newValue}. O adaptador reinicia em alguns segundos.");
            else App.Notify.Warning("Alteração não confirmada", resp.Message);
            BusyText = "Aguardando o adaptador reiniciar…";
            await Task.Delay(TimeSpan.FromSeconds(5));
        });
        await LoadAdaptersAsync();
    }

    [RelayCommand]
    private async Task RevertAsync(NetworkRollbackEntry? e)
    {
        if (e is null) return;
        if (!await App.Dialogs.ConfirmAsync("Reverter propriedade", AdvancedPropertyService.ResetWarning,
                $"Adaptador: {e.AdapterName}\n{e.PropertyName}: {e.ToText} → {e.FromText}", "Reverter (reinicia o adaptador)")) return;
        await RunBusy("Revertendo via RBM Agent…", async () =>
        {
            var r = await AgentCall.InvokeAsync(AgentOps.NetAdvPropSet, e.Rollback, "Reverter propriedade", TimeSpan.FromSeconds(90));
            var resp = AgentCall.Read<AdvancedPropertySetResponse>(r);
            bool ok = resp is { Success: true };
            await SysUi.AuditAsync("net.advprop.revert", $"{e.AdapterName} {e.Rollback.RegistryKeyword} → {e.Rollback.NewRegistryValue}: {(ok ? "ok" : resp?.Message ?? "não executado")}", ok);
            if (!ok)
            {
                if (r is not null) App.Notify.Error("Reversão não aplicada", (resp?.Message ?? "Resposta vazia do agente.") +
                    (resp?.ErrorCode == "drift" ? " O valor atual mudou desde a alteração; nada foi escrito." : ""));
                return;
            }
            Rollbacks.Remove(e);
            App.Notify.Success("Revertido", $"{e.PropertyName} voltou para {resp!.NewDisplayValue ?? e.FromText}.");
            BusyText = "Aguardando o adaptador reiniciar…";
            await Task.Delay(TimeSpan.FromSeconds(5));
        });
        await LoadAdaptersAsync();
    }

    // =============================== Achados ===============================
    [RelayCommand]
    private void EvaluateFindings()
    {
        try
        {
            var input = new NetworkDiagnosticInput
            {
                Inventory = _inv,
                Advanced = _advanced.Values.ToList(),
                Latency = _lastLatency,
                Path = _lastPath,
                CounterDeltas = _deltas,
                FormFactor = _inventory.Current?.FormFactor ?? FormFactor.Unknown,
                Now = DateTime.Now,
            };
            var list = NetworkDiagnostics.Evaluate(input).OrderByDescending(f => f.Severity).ToList();
            Findings.Clear();
            foreach (var f in list) Findings.Add(f);
            string basis = "Base: inventário + propriedades do driver" + (_lastLatency.Count > 0 ? " + último teste de latência" : "") + (_lastPath is not null ? " + análise de caminho" : "");
            FindingsStatus = list.Count == 0 ? $"Nenhum problema detectado. {basis}." : $"{list.Count} achado(s). {basis}.";
            UpdateHero();
        }
        catch (Exception ex) { App.Log(ex, "Rede: diagnóstico"); FindingsStatus = "Falha ao avaliar: " + ex.Message; }
    }

    // =============================== Latência ===============================
    [RelayCommand]
    private void AddCustomTarget()
    {
        string host = CustomHost.Trim();
        int port = 443;
        // Aceita "host:porta" (IPv4/nome) como atalho para TCP.
        bool tcp = CustomTcp;
        int colon = host.LastIndexOf(':');
        if (colon > 0 && host.IndexOf(':') == colon && int.TryParse(host[(colon + 1)..], out var hp)) { port = hp; host = host[..colon]; tcp = true; }
        else if (tcp && !int.TryParse(CustomPort, out port)) { App.Notify.Warning("Porta inválida", "Informe uma porta TCP entre 1 e 65535."); return; }
        if (port is < 1 or > 65535) { App.Notify.Warning("Porta inválida", "Informe uma porta TCP entre 1 e 65535."); return; }
        if (!Probes.IsValidHost(host)) { App.Notify.Warning("Host inválido", "Use um nome DNS, IPv4 ou IPv6 (ex.: servidor.jogo.com ou 200.1.2.3:27015)."); return; }
        var t = new LatencyTarget(host, tcp ? $"{host} (TCP {port})" : host, TargetRole.Custom, tcp ? ProbeMethod.Tcp : ProbeMethod.Icmp, port);
        if (CustomTargets.Any(x => x.Id == t.Id)) return;
        CustomTargets.Add(t);
        CustomHost = "";
    }

    [RelayCommand]
    private void RemoveCustomTarget(LatencyTarget? t) { if (t is not null) CustomTargets.Remove(t); }

    [RelayCommand]
    private async Task StartLatencyAsync()
    {
        if (LatencyRunning) return;
        _latCts?.Dispose();
        _latCts = new CancellationTokenSource();
        var ct = _latCts.Token;
        LatencyRunning = true;
        LatencyStatus = "Descobrindo alvos (gateway e primeiro salto do provedor)…";
        using var task = App.Tasks.Begin("Teste de latência");
        try
        {
            var disc = await Task.Run(() => DefaultTargets.DiscoverAsync(CustomTargets.ToList(), true, ct), ct);
            LatencyNotes = disc.NotesPt.Count > 0 ? string.Join(" · ", disc.NotesPt) : null;
            var brushes = new[] { "B.Cyan", "B.Accent", "B.Violet", "B.Success", "B.Warning", "B.Info" }
                .Select(k => Application.Current.TryFindResource(k) as Brush ?? Brushes.DeepSkyBlue).ToArray();
            LatencyCards.Clear();
            for (int i = 0; i < disc.Targets.Count; i++) LatencyCards.Add(new NetworkLatencyCard(disc.Targets[i], brushes[i % brushes.Length], Continuous ? 120 : SampleCount));

            var acc = new LatencyAccumulator(disc.Targets, 600);
            _acc = acc;
            var opts = new LatencyTestOptions
            {
                Targets = disc.Targets, Count = Continuous ? null : SampleCount, Interval = TimeSpan.FromSeconds(1), Timeout = TimeSpan.FromSeconds(1),
            };
            LatencyStatus = Continuous ? "Medindo continuamente (1 sonda/s por alvo)… clique em Parar para encerrar e salvar."
                                       : $"Medindo {SampleCount} sondas por alvo (1/s)…";
            _latTimer.Start();
            var result = await Task.Run(() => LatencyTester.RunAsync(opts, new NetworkInlineProgress<LatencySample>(acc.Add), ct));
            _latTimer.Stop();
            _acc = null;
            foreach (var r in result.Targets)
                LatencyCards.FirstOrDefault(c => c.Target.Id == r.Target.Id)?.Update(r);
            _lastLatency = result.Targets;
            int saved = await SaveLatencyAsync(result);
            LatencyStatus = (result.Cancelled ? "Teste interrompido" : "Teste concluído") +
                            $" às {result.EndedAt.ToLocalTime():HH:mm:ss} · {(result.EndedAt - result.StartedAt).TotalSeconds:0} s" +
                            (saved > 0 ? $" · {saved} resumo(s) salvos no histórico" : "");
            EvaluateFindings();
        }
        catch (OperationCanceledException) { LatencyStatus = "Teste cancelado antes de iniciar as medições."; }
        catch (Exception ex)
        {
            App.Log(ex, "Rede: latência");
            LatencyStatus = "Falha no teste: " + ex.Message;
            App.Notify.Error("Teste de latência", ex.Message);
        }
        finally
        {
            _latTimer.Stop();
            LatencyRunning = false;
        }
    }

    [RelayCommand]
    private void StopLatency() => _latCts?.Cancel();

    private void RefreshLatencyCards()
    {
        if (_acc is not { } acc) return;
        foreach (var r in acc.Snapshot())
            LatencyCards.FirstOrDefault(c => c.Target.Id == r.Target.Id)?.Update(r);
    }

    private async Task<int> SaveLatencyAsync(LatencyTestResult result)
    {
        try
        {
            var def = _inv?.DefaultAdapter;
            string? kind = def?.Kind switch { AdapterKind.Ethernet => "Cabo", AdapterKind.WiFi => "Wi-Fi", null => null, _ => def!.Kind.ToString() };
            var recs = LatencyHistoryRecord.From(result, def?.Name, kind).Where(r => r.Sent > 0).ToList();
            foreach (var rec in recs)
                await SysUi.Db.UpsertAsync(Collections.NetworkHistory, Guid.NewGuid().ToString("N"), rec, tag: "latency");
            await LoadHistoryAsync();
            return recs.Count;
        }
        catch (Exception ex) { App.Log(ex, "Rede: salvar histórico"); App.Notify.Warning("Histórico", "Não foi possível salvar o resumo: " + ex.Message); return 0; }
    }

    // =============================== Caminho ===============================
    [RelayCommand]
    private async Task StartPathAsync()
    {
        if (PathRunning) return;
        string host = PathHost.Trim();
        if (!Probes.IsValidHost(host)) { App.Notify.Warning("Destino inválido", "Use um nome DNS ou IP (ex.: 1.1.1.1 ou servidor.do.jogo)."); return; }
        _pathCts?.Dispose();
        _pathCts = new CancellationTokenSource();
        var ct = _pathCts.Token;
        PathRunning = true;
        PathVerdict = null; PathSegments.Clear(); PathHops.Clear(); PathNotes.Clear();
        PathStatus = $"Traceroute até {host} (até 30 saltos)…";
        using var task = App.Tasks.Begin("Análise de caminho");
        try
        {
            var progress = new Progress<PathAnalysis>(p => { ApplyPath(p); PathStatus = $"Rodada {p.Rounds}/{PathRounds} · {p.Hops.Count} salto(s)"; });
            var res = await Task.Run(() => PathAnalyzer.AnalyzeAsync(host, new PathAnalysisOptions { Rounds = PathRounds }, progress, ct), ct);
            ApplyPath(res);
            if (res.Hops.Count > 0) _lastPath = res;
            PathStatus = (ct.IsCancellationRequested ? "Interrompido" : "Concluído") + $" · {res.Rounds} rodada(s) · {res.Hops.Count} salto(s)" +
                         (res.Reached ? "" : " · destino não respondeu");
            EvaluateFindings();
        }
        catch (OperationCanceledException) { PathStatus = "Análise cancelada."; }
        catch (Exception ex)
        {
            App.Log(ex, "Rede: caminho");
            PathStatus = "Falha: " + ex.Message;
            App.Notify.Error("Análise de caminho", ex.Message);
        }
        finally { PathRunning = false; }
    }

    [RelayCommand]
    private void StopPath() => _pathCts?.Cancel();

    private void ApplyPath(PathAnalysis p)
    {
        PathVerdict = p.VerdictPt;
        PathSegments.Clear();
        foreach (var s in p.Segments)
            PathSegments.Add(new NetworkSegmentRow(s.LabelPt, s.FromTtl == s.ToTtl ? $"salto {s.FromTtl}" : $"saltos {s.FromTtl}–{s.ToTtl}",
                SysUi.Ms(s.MedianRttMs), double.IsFinite(s.IncrementalMs) ? "+" + SysUi.Ms(Math.Max(0, s.IncrementalMs)) : "—",
                SysUi.Ms(s.JitterMs), SysUi.Pct(s.LossPercent), s.LossIsReal, s.NotePt));
        PathHops.Clear();
        foreach (var h in p.Hops)
        {
            var st = h.Hop.Stats;
            string interp = h.Interpretation switch
            {
                HopInterpretation.Normal => "Normal",
                HopInterpretation.NoIcmpResponse => "Sem resposta ICMP (normal)",
                HopInterpretation.IcmpRateLimitedLoss => "Limitação de ICMP (não é perda real)",
                HopInterpretation.RealLossOrigin => "Origem de perda real",
                HopInterpretation.RealLossPropagated => "Perda real (propagada)",
                HopInterpretation.ControlPlaneDelay => "Resposta lenta do roteador (não afeta o tráfego)",
                _ => h.Interpretation.ToString(),
            };
            PathHops.Add(new NetworkHopRow(h.Hop.Ttl, h.Hop.Address ?? "*", h.Hop.HostName, HopClassifier.SegmentLabel(h.Segment),
                st.HasData ? SysUi.Ms(st.MedianMs) : "—", st.Sent > 0 ? SysUi.Pct(st.LossPercent) : "—", interp, h.NotePt,
                h.Interpretation is HopInterpretation.RealLossOrigin or HopInterpretation.RealLossPropagated,
                h.Interpretation is HopInterpretation.IcmpRateLimitedLoss or HopInterpretation.NoIcmpResponse or HopInterpretation.ControlPlaneDelay));
        }
        PathNotes.Clear();
        foreach (var n in p.NotesPt) PathNotes.Add(n);
    }

    // =============================== DNS ===============================
    [RelayCommand]
    private async Task StartDnsAsync()
    {
        if (DnsRunning) return;
        var custom = DnsCustom.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        foreach (var c in custom)
            if (!System.Net.IPAddress.TryParse(c, out _)) { App.Notify.Warning("Servidor DNS inválido", $"\"{c}\" não é um endereço IP."); return; }
        _dnsCts?.Dispose();
        _dnsCts = new CancellationTokenSource();
        var ct = _dnsCts.Token;
        DnsRunning = true; DnsRows.Clear(); DnsNotes.Clear(); DnsSystemText = null; DnsVerdict = null;
        DnsStatus = "Consultando servidores…";
        using var task = App.Tasks.Begin("Benchmark de DNS");
        try
        {
            int n = 0;
            var progress = new Progress<DnsQueryResult>(_ => { n++; DnsStatus = $"{n} consulta(s) realizadas…"; });
            var res = await Task.Run(() => DnsBenchmark.RunAsync(new DnsBenchmarkOptions { CustomServers = custom }, progress, ct), ct);
            var ranking = res.Ranking;
            var ordered = ranking.Concat(res.Servers.Except(ranking).OrderBy(s => s.Server.Label)).ToList();
            int rank = 0;
            foreach (var s in ordered)
            {
                bool ranked = ranking.Contains(s);
                if (ranked) rank++;
                DnsRows.Add(new NetworkDnsRow(ranked ? rank.ToString(PtBr) : "—", s.Server.Label, s.Server.Address, SourceText(s.Server.Source),
                    SysUi.Ms(s.FirstQuery.Median), SysUi.Ms(s.FirstQuery.P95), SysUi.Ms(s.CachedQuery.Median),
                    SysUi.Pct(s.SuccessPercent) + (s.Timeouts > 0 ? $" ({s.Timeouts} timeout)" : ""), ranked && rank == 1));
            }
            var best = ranking.FirstOrDefault();
            var mine = res.Servers.FirstOrDefault(s => s.Server.Source == DnsServerSource.Configured);
            DnsVerdict = best is null ? "Nenhum servidor respondeu de forma confiável (≥ 80% de sucesso) para entrar no ranking."
                : $"O mais rápido aqui foi {best.Server.Label} ({SysUi.Ms(best.FirstQuery.Median)})."
                  + (mine is null ? "" : Equals(mine, best) ? " É o DNS que você já usa." : $" O DNS que você usa hoje ({mine.Server.Label}) levou {SysUi.Ms(mine.FirstQuery.Median)}.");
            if (res.SystemResolver is { } sys)
                DnsSystemText = $"Resolvedor do Windows (com cache local): mediana {SysUi.Ms(sys.FirstQuery.Median)} na 1ª consulta, {SysUi.Ms(sys.CachedQuery.Median)} repetida · sucesso {SysUi.Pct(sys.SuccessPercent)}";
            foreach (var note in res.NotesPt) DnsNotes.Add(note);
            DnsStatus = $"Concluído às {res.At.ToLocalTime():HH:mm:ss} · {res.Servers.Count} servidor(es). Nada foi alterado nas configurações de DNS.";
        }
        catch (OperationCanceledException) { DnsStatus = "Benchmark cancelado."; }
        catch (Exception ex)
        {
            App.Log(ex, "Rede: DNS");
            DnsStatus = "Falha: " + ex.Message;
            App.Notify.Error("Benchmark de DNS", ex.Message);
        }
        finally { DnsRunning = false; }
    }

    [RelayCommand]
    private void StopDns() => _dnsCts?.Cancel();

    private static string SourceText(DnsServerSource s) => s switch
    {
        DnsServerSource.Configured => "Configurado", DnsServerSource.Public => "Público", DnsServerSource.Custom => "Personalizado",
        DnsServerSource.SystemResolver => "Sistema", _ => s.ToString(),
    };

    // =============================== Histórico ===============================
    [RelayCommand]
    private async Task LoadHistoryAsync()
    {
        try
        {
            var list = await SysUi.Db.ListAsync<LatencyHistoryRecord>(Collections.NetworkHistory, 300, "latency");
            History.Clear();
            foreach (var r in list.Where(r => r.TargetId is not null))
                History.Add(new NetworkHistoryRow(r.StartedAt.ToLocalTime().ToString("dd/MM/yyyy HH:mm", PtBr), r.Label,
                    r.Method == ProbeMethod.Tcp ? "TCP" : "ICMP", SysUi.Ms(r.AvgMs), SysUi.Ms(r.P95Ms), SysUi.Ms(r.JitterMs),
                    SysUi.Pct(r.LossPercent), $"{r.Received}/{r.Sent}", string.Join(" · ", new[] { r.AdapterName, r.ConnectionKind }.Where(x => !string.IsNullOrEmpty(x)))));
            HistoryStatus = History.Count == 0 ? "Nenhum teste salvo ainda. Cada teste de latência concluído ou interrompido grava um resumo por alvo."
                                               : $"{History.Count} resumo(s) (mais recentes primeiro).";
        }
        catch (Exception ex) { App.Log(ex, "Rede: histórico"); HistoryStatus = "Falha ao ler o histórico: " + ex.Message; }
    }
}
