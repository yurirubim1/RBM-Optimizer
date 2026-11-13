using System.Globalization;
using System.Windows;
using System.Windows.Data;
using Rbm.Network.Adapters;
using Rbm.Network.Latency;
using Rbm.UI.Controls;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rbm.UI.Pages.SystemPages;

/// <summary>Adaptador na lista da página de Rede (dados reais do inventário + capacidade lida do driver).</summary>
public sealed partial class NetworkAdapterItem : ObservableObject
{
    public NetworkAdapterItem(NetworkAdapterInfo info, AdapterAdvancedSettings? adv)
    {
        Info = info;
        Advanced = adv;
        CapabilityBps = AdvancedKeywords.MaxSpeedFromSpeedDuplex(adv?.Find(AdvancedKeywords.SpeedDuplex));
        CapabilityFromDriver = CapabilityBps is not null;
        if (CapabilityBps is null)
        {
            long fromDesc = AdvancedKeywords.ParseSpeedText(info.Description);
            if (fromDesc == 0 && (info.Description.Contains("GbE", StringComparison.OrdinalIgnoreCase) || info.Description.Contains("Gigabit", StringComparison.OrdinalIgnoreCase)))
                fromDesc = 1_000_000_000;
            if (fromDesc > 0) CapabilityBps = fromDesc;
        }
    }

    public NetworkAdapterInfo Info { get; }
    [ObservableProperty] private AdapterAdvancedSettings? _advanced;

    public long? CapabilityBps { get; }
    public bool CapabilityFromDriver { get; }
    public string Name => Info.Name;
    public string Description => Info.Description;
    public bool IsUp => Info.Status == AdapterStatus.Up;
    public bool IsDefault => Info.IsDefaultRouteInterface;

    public string KindText => Info.Kind switch
    {
        AdapterKind.Ethernet => "Ethernet", AdapterKind.WiFi => "Wi-Fi", AdapterKind.Cellular => "Celular",
        AdapterKind.Bluetooth => "Bluetooth", AdapterKind.Virtual => "Virtual", AdapterKind.Other => "Outro", _ => "Desconhecido",
    } + (Info.IsVirtual && Info.Kind != AdapterKind.Virtual ? " (virtual)" : "");

    public string StatusText => string.IsNullOrWhiteSpace(Info.StatusText) ? Info.Status.ToString() : Info.StatusText;

    public string SpeedText => IsUp && Info.LinkSpeedBps is { } s
        ? NetworkAdapterInfo.FormatSpeed(s) + (Info.FullDuplex == false ? " (half duplex)" : "")
        : "—";

    public string CapabilityText => CapabilityBps is { } c
        ? $"capacidade {NetworkAdapterInfo.FormatSpeed(c)}" + (CapabilityFromDriver ? "" : " (pela descrição)")
        : "capacidade não declarada";

    /// <summary>Ethernet física conectada abaixo da capacidade declarada pelo driver.</summary>
    public bool IsBelowCapability => IsUp && Info.Kind == AdapterKind.Ethernet && Info.IsHardware
        && Info.LinkSpeedBps is { } s && CapabilityBps is { } c && s < c;

    public string LinkSummary => IsUp ? $"{SpeedText} · {CapabilityText}" : StatusText;
}

/// <summary>Alerta destacado de enlace abaixo da capacidade (ex.: placa gigabit negociada em 100 Mbps).</summary>
public sealed record NetworkLinkAlert(string Title, string Detail, IReadOnlyList<string> Causes);

/// <summary>Linha editável da tabela de propriedades avançadas.</summary>
public sealed partial class NetworkAdvancedRow : ObservableObject
{
    public NetworkAdvancedRow(AdvancedProperty p)
    {
        Prop = p;
        Options = p.ValidOptions;
        _selectedOption = p.CurrentRegistryValue is { } cur
            ? p.ValidOptions.FirstOrDefault(o => string.Equals(o.RegistryValue, cur, StringComparison.OrdinalIgnoreCase))
            : null;
        _numericText = p.CurrentRegistryValue ?? "";
    }

    public AdvancedProperty Prop { get; }
    public IReadOnlyList<AdvancedPropertyOption> Options { get; }
    public string DisplayName => Prop.DisplayName;
    public string Keyword => Prop.RegistryKeyword;
    public string CurrentText => Prop.DisplayValue ?? Prop.CurrentRegistryValue ?? "—";
    public string DefaultText => Prop.DefaultRegistryValue is { } d ? Prop.DisplayFor(d) ?? d : "—";
    public string? Description => Prop.Known?.DescriptionPt;
    public string CategoryText => Prop.Known?.Category is { Length: > 0 } c ? " · " + c : "";
    public bool IsPowerSaving => Prop.Known?.IsPowerSaving == true;
    public bool IsBlocked => AdvancedKeywords.IsWriteBlocked(Prop.RegistryKeyword);
    public bool IsEnum => !IsBlocked && Prop.ValueKind == AdvancedValueKind.Enumeration && Options.Count > 0;
    public bool IsNumeric => !IsBlocked && Prop.ValueKind == AdvancedValueKind.Numeric && Prop.Range is not null;
    public bool IsEditable => IsEnum || IsNumeric;
    public string RangeText => Prop.Range is { } r ? $"{r.Min}–{r.Max}" + (r.Step > 1 ? $" (passo {r.Step})" : "") + (r.Base == 16 ? " hex" : "") : "";
    public string NotEditableReason => IsBlocked ? "Bloqueado (identidade/VLAN)"
        : Prop.ValueKind == AdvancedValueKind.FreeText ? "Texto livre: não editável"
        : "Sem valores declarados pelo driver";

    [ObservableProperty] private AdvancedPropertyOption? _selectedOption;
    [ObservableProperty] private string _numericText;

    public string? NewValue => IsEnum ? SelectedOption?.RegistryValue : IsNumeric ? NumericText?.Trim() : null;
    public bool IsChanged => NewValue is { Length: > 0 } v && !string.Equals(v, Prop.CurrentRegistryValue, StringComparison.OrdinalIgnoreCase);
    public string NewValueDisplay => NewValue is { } v ? Prop.DisplayFor(v) ?? v : "—";

    partial void OnSelectedOptionChanged(AdvancedPropertyOption? value) => OnPropertyChanged(nameof(IsChanged));
    partial void OnNumericTextChanged(string value) => OnPropertyChanged(nameof(IsChanged));
}

/// <summary>Alteração aplicada nesta sessão, com o pedido de reversão devolvido pelo agente.</summary>
public sealed record NetworkRollbackEntry(string AdapterName, string PropertyName, string FromText, string ToText, DateTimeOffset At, AdvancedPropertySetRequest Rollback)
{
    public string Summary => $"{PropertyName}: {FromText} → {ToText}";
    public string When => At.ToLocalTime().ToString("HH:mm:ss", CultureInfo.GetCultureInfo("pt-BR"));
}

/// <summary>Cartão de um alvo no teste de latência (gráfico + estatísticas ao vivo).</summary>
public sealed partial class NetworkLatencyCard : ObservableObject
{
    public NetworkLatencyCard(LatencyTarget target, System.Windows.Media.Brush stroke, int capacity) { Target = target; Stroke = stroke; Capacity = Math.Max(2, capacity); }
    public int Capacity { get; }
    public LatencyTarget Target { get; }
    public System.Windows.Media.Brush Stroke { get; }
    public string Label => Target.Label;
    public string MethodText => Target.Method == ProbeMethod.Tcp ? $"TCP {Target.TcpPort}" : "ICMP";
    public string RoleText => Target.Role switch
    {
        TargetRole.Gateway => "Rede local", TargetRole.IspFirstHop => "Provedor", TargetRole.PublicDns => "Internet",
        TargetRole.Custom => "Personalizado", _ => "Salto",
    };

    [ObservableProperty] private IReadOnlyList<ChartSeries> _series = Array.Empty<ChartSeries>();
    [ObservableProperty] private string _minText = "—";
    [ObservableProperty] private string _avgText = "—";
    [ObservableProperty] private string _p95Text = "—";
    [ObservableProperty] private string _jitterText = "—";
    [ObservableProperty] private string _lossText = "—";
    [ObservableProperty] private string _sentText = "0 enviados";
    [ObservableProperty] private string? _address;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private bool _hasLoss;
    // Leitura simples de cada número medido (faixas de referência; a rede local tem faixas próprias).
    [ObservableProperty] private string _pingVerdict = "";
    [ObservableProperty] private string _pingBrush = "B.TextMuted";
    [ObservableProperty] private string _jitterVerdict = "";
    [ObservableProperty] private string _jitterBrush = "B.TextMuted";
    [ObservableProperty] private string _lossVerdict = "";
    [ObservableProperty] private string _lossBrush = "B.TextMuted";

    private void UpdateVerdicts(LatencyStats s)
    {
        if (s.Received == 0)
        {
            PingVerdict = JitterVerdict = "";
            (LossVerdict, LossBrush) = s.Sent > 0 ? ("sem resposta", "B.Danger") : ("", "B.TextMuted");
            return;
        }
        bool local = Target.Role == TargetRole.Gateway;
        double p = s.AvgMs;
        (PingVerdict, PingBrush) = !double.IsFinite(p) ? ("", "B.TextMuted")
            : local ? (p <= 5 ? ("ótimo", "B.Success") : p <= 20 ? ("bom", "B.Cyan") : ("alto para a rede local", "B.Warning"))
            : p <= 30 ? ("ótimo", "B.Success") : p <= 60 ? ("bom", "B.Cyan") : p <= 100 ? ("razoável", "B.Warning") : ("alto", "B.Danger");
        double j = s.JitterMs;
        (JitterVerdict, JitterBrush) = !double.IsFinite(j) ? ("", "B.TextMuted")
            : j <= 5 ? ("estável", "B.Success") : j <= 15 ? ("oscila um pouco", "B.Warning") : ("instável", "B.Danger");
        double l = s.LossPercent;
        (LossVerdict, LossBrush) = s.Sent == 0 || !double.IsFinite(l) ? ("", "B.TextMuted")
            : s.Lost == 0 ? ("sem perdas", "B.Success") : l <= 1 ? ("perda leve", "B.Warning") : ("perda alta", "B.Danger");
    }

    public void Update(TargetLatencyResult r)
    {
        var s = r.Stats;
        UpdateVerdicts(s);
        MinText = SysUi.Ms(s.MinMs); AvgText = SysUi.Ms(s.AvgMs); P95Text = SysUi.Ms(s.P95Ms);
        JitterText = SysUi.Ms(s.JitterMs); LossText = s.Sent > 0 ? SysUi.Pct(s.LossPercent) : "—";
        HasLoss = s.Sent > 0 && s.Lost > 0;
        SentText = $"{s.Received}/{s.Sent} respostas" + (s.MaxConsecutiveLost > 1 ? $" · maior sequência perdida {s.MaxConsecutiveLost}" : "");
        Address = r.ResolvedAddress ?? r.Samples.LastOrDefault(x => x.RemoteAddress is not null)?.RemoteAddress;
        Error = r.Error;
        var values = r.Samples.Where(x => x.Received).TakeLast(Capacity).Select(x => x.RttMs!.Value).ToArray();
        Series = new[] { new ChartSeries { Name = Label, Values = values, Stroke = Stroke } };
    }
}

public sealed record NetworkHopRow(int Ttl, string Address, string? HostName, string Segment, string Median, string Loss, string Interpretation, string Note, bool IsProblem, bool IsBenign);
public sealed record NetworkSegmentRow(string Label, string Hops, string Median, string Incremental, string Jitter, string Loss, bool LossIsReal, string Note);
public sealed record NetworkDnsRow(string Rank, string Label, string Address, string Source, string FirstMedian, string FirstP95, string CachedMedian, string Success, bool IsBest);
public sealed record NetworkHistoryRow(string When, string Label, string Method, string Avg, string P95, string Jitter, string Loss, string Samples, string Adapter);

/// <summary>Converte índice de aba (int) ⇄ RadioButton.IsChecked; parâmetro = índice da aba.</summary>
public sealed class NetworkTabConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is int i && int.TryParse(p?.ToString(), out var k) && i == k;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) =>
        value is true && int.TryParse(p?.ToString(), out var k) ? k : Binding.DoNothing;
}

/// <summary>Índice de aba → Visibility (Visible só na aba do parâmetro).</summary>
public sealed class NetworkTabVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        value is int i && int.TryParse(p?.ToString(), out var k) && i == k ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>IProgress que executa o callback na própria thread (sem postar para a UI).</summary>
public sealed class NetworkInlineProgress<T>(Action<T> report) : IProgress<T>
{
    public void Report(T value) => report(value);
}
