using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Diagnostics;
using Rbm.Data;
using Rbm.Latency;
using Rbm.UI.Controls;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.Analysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

// ---------- Linhas de exibição (somente leitura) ----------
public sealed record LatencyModuleRow(string Module, string Category, string? ImagePath,
    long DpcCount, string DpcTotal, string DpcMax, string DpcP99,
    long IsrCount, string IsrTotal, string IsrMax, string IsrP99,
    string Highest, string HighestBrush, string Share);
public sealed record LatencyExecRow(string Kind, string Module, string Duration, string DurationBrush, string When, int Cpu);
public sealed record LatencyBucketRow(string Label, long Dpc, long Isr);
public sealed record LatencyKv(string Label, string Value, string? Hint = null);
/// <summary>Documento persistido em <see cref="Collections.LatencyReports"/>.</summary>
public sealed record LatencyHistoryDoc(string Id, DateTimeOffset At, LatencyReport Report);
public sealed record LatencyHistoryItem(string Id, DateTimeOffset At, LatencyReport Report)
{
    public string Title => $"{At.LocalDateTime:g} · {Report.RequestedSeconds} s";
    public string VerdictLabel => LatencyViewModel.VerdictPt(Report.Verdict);
    public string VerdictBrush => LatencyViewModel.VerdictBrushKey(Report.Verdict);
    public string Detail => $"Pico DPC {AnalysisUi.Us(Report.DpcOverall.MaxUs)} · ISR {AnalysisUi.Us(Report.IsrOverall.MaxUs)}";
}
public sealed record CpuCounterRow(string Cpu, string DpcMean, string DpcMax, string IntMean, string IntMax, string Brush);
public sealed record InputDeviceRow(string Name, string Category, string Bus, string Service, string PowerState, string PowerBrush, string PowerText, string Chain, string Instance);
public sealed record PollingRow(string Device, string Rate, string Nearest, string Median, string P99, string Stability, string Confidence, string ConfidenceBrush, int Intervals, string Notes);
public sealed record MsiRow(string Name, string Category, string Mode, string ModeBrush, string MsiSupported, string Limit, string Policy, string Priority, string Irqs, string Interpretation);
public sealed record TimerRequestRow(string Pid, string Path, string Period);

public sealed partial class LatencyViewModel : PageViewModel
{
    private readonly IAgentClient _agent;
    private readonly RbmDatabase _db;
    public override string Key => "latency";

    public string NotInputLag => LatencyTexts.NotInputLag;
    public string PollingNote => LatencyTexts.PollingRateNote + " " + LatencyTexts.RawInputTimingNote;

    [ObservableProperty] private string _tab = "dpc";

    // ===================== Resumo em linguagem simples =====================
    [ObservableProperty] private string _heroChip = "Nenhuma análise ainda";
    [ObservableProperty] private string _heroTone = "info";
    /// <summary>Frase principal do relatório aberto (a partir do driver com o maior pico medido).</summary>
    [ObservableProperty] private string _plainVerdict = "";
    [ObservableProperty] private string _plainDetail = "";

    [RelayCommand]
    private void GoTab(string? tab) { if (!string.IsNullOrWhiteSpace(tab)) Tab = tab; }

    private void UpdateHeroChip()
    {
        if (IsCapturing) { (HeroChip, HeroTone) = ("Analisando…", "info"); return; }
        var last = History.FirstOrDefault();
        (HeroChip, HeroTone) = last is null ? ("Nenhuma análise ainda", "info")
            : ($"Última análise: {last.VerdictLabel.ToLowerInvariant()} ({last.At.LocalDateTime:dd/MM HH:mm})", last.Report.Verdict switch
            {
                LatencyVerdict.Good => "ok", LatencyVerdict.Attention => "warn", LatencyVerdict.Problematic => "danger", _ => "info",
            });
    }

    partial void OnIsCapturingChanged(bool value) => UpdateHeroChip();

    private void BuildPlainVerdict(LatencyReport r, LatencyThresholds t)
    {
        var top = r.Modules.Where(m => !string.IsNullOrWhiteSpace(m.Module)).MaxBy(m => m.HighestUs);
        string who(ModuleLatencyStats m) => string.IsNullOrEmpty(m.Category) ? m.Module : $"{m.Module} ({m.Category})";
        if (r.Verdict == LatencyVerdict.Inconclusive || top is null)
        {
            PlainVerdict = "Não foi possível tirar uma conclusão desta análise";
            PlainDetail = "Faltaram dados (captura curta ou interrompida). Tente de novo usando o PC normalmente durante a análise.";
            return;
        }
        string peak = AnalysisUi.Us(top.HighestUs);
        bool high = top.HighestUs >= t.HighUs;
        switch (r.Verdict)
        {
            case LatencyVerdict.Problematic:
                PlainVerdict = $"O driver {who(top)} causou picos que podem gerar estalos de áudio ou travadas";
                PlainDetail = $"Os atrasos se repetiram durante a análise; o maior foi de {peak}. Veja abaixo o que fazer.";
                break;
            case LatencyVerdict.Attention:
                PlainVerdict = high ? $"O driver {who(top)} teve picos que podem gerar estalos de áudio"
                                    : "Atrasos moderados — só preocupam se você nota estalos ou travadas";
                PlainDetail = $"O maior atraso foi de {peak}, de {who(top)}. Vale investigar se o problema acontece com frequência.";
                break;
            default:
                PlainVerdict = high ? $"Tudo bem no geral — houve só picos pontuais de {who(top)}" : "Nenhum driver causou atrasos relevantes";
                PlainDetail = high ? $"O maior pico foi de {peak}, mas não se manteve ao longo da análise. Se você não nota estalos de áudio, não precisa fazer nada."
                                   : $"O maior atraso medido foi de {peak}, de {who(top)} — abaixo do que costuma causar estalos de áudio. Se ainda nota travadas, a causa provavelmente é outra.";
                break;
        }
    }

    // ===================== (a) Captura DPC/ISR =====================
    public int[] CaptureDurations { get; } = { 15, 30, 60, 120, 300 };
    [ObservableProperty] private int _captureSeconds = 60;
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private double _captureProgress;
    [ObservableProperty] private string _captureStatus = "";
    [ObservableProperty] private LatencyReport? _report;
    [ObservableProperty] private bool _hasReport;
    [ObservableProperty] private string _reportTitle = "";
    [ObservableProperty] private string _verdictLabel = "";
    [ObservableProperty] private string _verdictText = "";
    [ObservableProperty] private string _verdictBrush = "B.TextMuted";
    [ObservableProperty] private string _thresholdsText = "";
    [ObservableProperty] private double[] _dpcHistogram = Array.Empty<double>();
    [ObservableProperty] private double[] _isrHistogram = Array.Empty<double>();
    [ObservableProperty] private string[] _histogramLabels = Array.Empty<string>();
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _timeline;
    [ObservableProperty] private int _timelineCapacity = 60;
    [ObservableProperty] private double[] _perCpuPercent = Array.Empty<double>();
    [ObservableProperty] private string _perCpuText = "";
    public ObservableCollection<LatencyKv> ReportSummary { get; } = new();
    public ObservableCollection<LatencyModuleRow> Modules { get; } = new();
    public ObservableCollection<LatencyExecRow> TopExecutions { get; } = new();
    public ObservableCollection<LatencyBucketRow> Buckets { get; } = new();
    public ObservableCollection<Finding> Findings { get; } = new();
    public ObservableCollection<string> ReportWarnings { get; } = new();
    public ObservableCollection<string> ReportLimitations { get; } = new();
    public ObservableCollection<LatencyHistoryItem> History { get; } = new();
    [ObservableProperty] private LatencyHistoryItem? _selectedHistory;
    private CancellationTokenSource? _captureCts;
    private DispatcherTimer? _captureTimer;

    // ===================== (b) Indicadores =====================
    public int[] IndicatorDurations { get; } = { 5, 10, 30 };
    [ObservableProperty] private int _indicatorSeconds = 10;
    [ObservableProperty] private bool _isSampling;
    [ObservableProperty] private double _samplingProgress;
    [ObservableProperty] private bool _hasIndicators;
    [ObservableProperty] private string _indicatorInterpretation = "";
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _indicatorSeries;
    [ObservableProperty] private int _indicatorCapacity = 10;
    public ObservableCollection<LatencyKv> IndicatorValues { get; } = new();
    public ObservableCollection<CpuCounterRow> IndicatorPerCpu { get; } = new();
    public ObservableCollection<string> IndicatorLimitations { get; } = new();
    private CancellationTokenSource? _samplingCts;

    // ===================== (c) Timer =====================
    [ObservableProperty] private string _timerCurrent = "—";
    [ObservableProperty] private string _timerFinest = "—";
    [ObservableProperty] private string _timerCoarsest = "—";
    [ObservableProperty] private string _timerGlobalRequests = "—";
    [ObservableProperty] private string _timerExplanation = TimerResolutionInspector.Explanation;
    public IReadOnlyList<string> TimerReferences => TimerResolutionInspector.References;
    public int[] TimerRequestDurations { get; } = { 10, 30, 60, 120 };
    [ObservableProperty] private int _timerRequestSeconds = 30;
    [ObservableProperty] private bool _isCollectingTimers;
    [ObservableProperty] private double _timerProgress;
    [ObservableProperty] private string _timerStatus = "";
    [ObservableProperty] private bool _hasTimerRequests;
    [ObservableProperty] private string _timerPlatformCurrent = "";
    public ObservableCollection<TimerRequestRow> TimerRequests { get; } = new();
    public ObservableCollection<string> TimerWarnings { get; } = new();
    private CancellationTokenSource? _timerCts;
    private DispatcherTimer? _timerTimer;

    // ===================== (d) Entrada =====================
    [ObservableProperty] private bool _inputLoaded;
    [ObservableProperty] private string _globalSuspend = "—";
    [ObservableProperty] private string _globalSuspendDetail = "";
    public ObservableCollection<InputDeviceRow> InputDevices { get; } = new();
    public ObservableCollection<string> InputNotes { get; } = new();
    public ObservableCollection<PollingRow> Polling { get; } = new();
    [ObservableProperty] private bool _isPolling;
    [ObservableProperty] private string _pollingStatus = "Ainda não medido.";
    private PollingRateEstimator? _estimator;
    private HwndSource? _hwndSource;
    private DispatcherTimer? _pollTimer;
    private readonly Dictionary<long, string> _deviceNames = new();
    private IReadOnlyList<InputDeviceInfo> _inputInfo = Array.Empty<InputDeviceInfo>();

    // ===================== (e) MSI =====================
    [ObservableProperty] private bool _msiLoaded;
    [ObservableProperty] private string _msiSummary = "";
    public ObservableCollection<MsiRow> MsiDevices { get; } = new();
    public ObservableCollection<string> MsiNotes { get; } = new();

    public LatencyViewModel(IAgentClient agent, RbmDatabase db)
    {
        _agent = agent; _db = db;
        _tab = AnalysisUi.StartupTab("dpc", "indicators", "timer", "input", "msi") ?? "dpc";
    }

    public override async Task OnNavigatedToAsync()
    {
        QueryTimer();
        await LoadHistoryAsync();
        await EnsureTabLoadedAsync();
    }

    public override void OnNavigatedFrom() => StopPollingCore();

    partial void OnTabChanged(string value) => _ = EnsureTabLoadedAsync();

    private async Task EnsureTabLoadedAsync()
    {
        if (Tab == "input" && !InputLoaded) await LoadInputAsync();
        else if (Tab == "msi" && !MsiLoaded) await LoadMsiAsync();
        else if (Tab == "timer") QueryTimer();
    }

    // ------------------------------------------------------------------ (a)
    public static string VerdictPt(LatencyVerdict v) => v switch
    {
        LatencyVerdict.Good => "Bom", LatencyVerdict.Attention => "Atenção", LatencyVerdict.Problematic => "Problemático", _ => "Inconclusivo",
    };
    public static string VerdictBrushKey(LatencyVerdict v) => v switch
    {
        LatencyVerdict.Good => "B.Success", LatencyVerdict.Attention => "B.Warning", LatencyVerdict.Problematic => "B.Danger", _ => "B.TextMuted",
    };

    [RelayCommand]
    private async Task StartCapture()
    {
        if (IsCapturing) return;
        int sec = Math.Clamp(CaptureSeconds, 5, 300);
        bool ok = await App.Dialogs.ConfirmAsync("Captura DPC/ISR (ETW de kernel)",
            $"A captura usa uma sessão ETW de kernel no agente elevado do RBM Optimizer (o Windows pedirá confirmação UAC no primeiro uso) e dura {sec} s.",
            "Durante a captura, use o PC normalmente — de preferência reproduzindo o problema (áudio estalando, jogo travando). " +
            "Enquanto a captura roda, outras operações administrativas aguardam na fila do agente. A captura em si não altera nada no sistema.",
            "Iniciar captura");
        if (!ok) return;

        if (!await _agent.EnsureConnectedAsync())
        {
            App.Notify.Warning("Agente elevado indisponível", AnalysisUi.AgentError(_agent) ?? "Elevação recusada.");
            return;
        }

        _captureCts = new CancellationTokenSource();
        IsCapturing = true; CaptureProgress = 0;
        var started = DateTime.UtcNow;
        _captureTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _captureTimer.Tick += (_, _) =>
        {
            double el = (DateTime.UtcNow - started).TotalSeconds;
            CaptureProgress = Math.Min(1, el / sec);
            CaptureStatus = el < sec ? $"Capturando… {el:0} / {sec} s" : $"Encerrando a sessão e analisando… ({el:0} s)";
        };
        _captureTimer.Start();
        using var task = App.Tasks.Begin("Captura DPC/ISR");
        try
        {
            var resp = await AnalysisUi.InvokeAbandonableAsync(_agent, AgentOps.LatencyCapture, new LatencyCaptureRequest(sec),
                TimeSpan.FromSeconds(sec + 30), _captureCts.Token);
            if (resp is null)
            {
                App.Notify.Info("Captura abandonada", "O agente não aceita cancelamento remoto: a sessão termina sozinha em até " +
                    $"{sec} s e o resultado será descartado. Novas operações administrativas aguardam até lá.");
                return;
            }
            if (!resp.Ok) { App.Notify.Error("Falha na captura DPC/ISR", resp.Error ?? "Erro desconhecido."); return; }
            var report = resp.Read<LatencyReport>();
            if (report is null) { App.Notify.Error("Falha na captura DPC/ISR", "Resposta vazia do agente."); return; }
            var id = Guid.NewGuid().ToString("N");
            var at = DateTimeOffset.Now;
            try { await _db.UpsertAsync(Collections.LatencyReports, id, new LatencyHistoryDoc(id, at, report), tag: report.Verdict.ToString(), createdAt: at); }
            catch (Exception ex) { App.Log(ex, "Salvar relatório de latência"); App.Notify.Warning("Relatório não salvo", ex.Message); }
            var item = new LatencyHistoryItem(id, at, report);
            History.Insert(0, item);
            SelectedHistory = item;
            UpdateHeroChip();
            App.Notify.Success("Captura concluída", $"Veredito: {VerdictPt(report.Verdict)}.");
        }
        catch (Exception ex)
        {
            App.Log(ex, "Captura DPC/ISR");
            App.Notify.Error("Falha na captura DPC/ISR", ex.Message);
        }
        finally
        {
            _captureTimer?.Stop(); _captureTimer = null;
            IsCapturing = false; CaptureStatus = ""; CaptureProgress = 0;
            _captureCts?.Dispose(); _captureCts = null;
        }
    }

    [RelayCommand]
    private void CancelCapture() => _captureCts?.Cancel();

    partial void OnSelectedHistoryChanged(LatencyHistoryItem? value)
    {
        if (value is not null) ShowReport(value.Report, value.At);
    }

    private async Task LoadHistoryAsync()
    {
        try
        {
            var docs = await Task.Run(() => _db.ListAsync<LatencyHistoryDoc>(Collections.LatencyReports, 100));
            History.Clear();
            foreach (var d in docs.Where(d => d.Report is not null)) History.Add(new LatencyHistoryItem(d.Id, d.At, d.Report));
            if (Report is null && History.Count > 0) SelectedHistory = History[0];
            UpdateHeroChip();
        }
        catch (Exception ex) { App.Log(ex, "Histórico de latência"); App.Notify.Warning("Histórico indisponível", ex.Message); }
    }

    [RelayCommand]
    private async Task DeleteHistory(LatencyHistoryItem? item)
    {
        if (item is null) return;
        if (!await App.Dialogs.ConfirmAsync("Excluir relatório", $"Excluir o relatório de {item.At.LocalDateTime:g} do histórico?", confirmText: "Excluir")) return;
        try
        {
            await _db.DeleteAsync(Collections.LatencyReports, item.Id);
            History.Remove(item);
            if (SelectedHistory == item || SelectedHistory is null) { SelectedHistory = null; ClearReport(); }
            UpdateHeroChip();
        }
        catch (Exception ex) { App.Notify.Error("Falha ao excluir", ex.Message); }
    }

    private void ClearReport()
    {
        Report = null; HasReport = false;
        Modules.Clear(); TopExecutions.Clear(); Buckets.Clear(); Findings.Clear(); ReportWarnings.Clear(); ReportLimitations.Clear(); ReportSummary.Clear();
        Timeline = null; DpcHistogram = IsrHistogram = PerCpuPercent = Array.Empty<double>();
    }

    private void ShowReport(LatencyReport r, DateTimeOffset at)
    {
        Report = r; HasReport = true;
        ReportTitle = $"Captura de {at.LocalDateTime:g} · {r.DurationSeconds:0} s · {r.LogicalProcessors} CPUs lógicas";
        VerdictLabel = VerdictPt(r.Verdict);
        VerdictText = r.VerdictText;
        VerdictBrush = VerdictBrushKey(r.Verdict);
        var t = r.Thresholds ?? LatencyThresholds.Default;
        BuildPlainVerdict(r, t);
        ThresholdsText = $"< {t.ElevatedUs:0} µs normal · {t.ElevatedUs:0}–{t.HighUs:0} µs atenção se recorrente · > {t.HighUs:0} µs pode causar estalos de áudio · " +
                         $"> {t.SevereUs:0} µs em ≥ {t.SustainedSeconds} segundos distintos = problemático (heurística estilo LatencyMon)";

        ReportSummary.Clear();
        ReportSummary.Add(new("DPC", $"{r.DpcOverall.Count:N0} execuções", $"máx {AnalysisUi.Us(r.DpcOverall.MaxUs)} · P99 {AnalysisUi.Us(r.DpcOverall.P99Us)} · média {AnalysisUi.Us(r.DpcOverall.MeanUs)}"));
        ReportSummary.Add(new("ISR", $"{r.IsrOverall.Count:N0} execuções", $"máx {AnalysisUi.Us(r.IsrOverall.MaxUs)} · P99 {AnalysisUi.Us(r.IsrOverall.P99Us)} · média {AnalysisUi.Us(r.IsrOverall.MeanUs)}"));
        ReportSummary.Add(new("Qualidade", $"{r.EventsLost:N0} evento(s) perdido(s)", $"{r.InvalidSamples:N0} amostra(s) inválida(s) · {r.UnresolvedPercent:0.#}% do tempo sem módulo identificado · sessão {r.SessionName}"));

        var th = t;
        Modules.Clear();
        foreach (var m in r.Modules.OrderByDescending(m => m.HighestUs))
            Modules.Add(new LatencyModuleRow(m.Module, string.IsNullOrEmpty(m.Category) ? "—" : m.Category, m.ImagePath,
                m.Dpc.Count, AnalysisUi.Us(m.Dpc.TotalUs), m.Dpc.Count > 0 ? AnalysisUi.Us(m.Dpc.MaxUs) : "—", m.Dpc.Count > 0 ? AnalysisUi.Us(m.Dpc.P99Us) : "—",
                m.Isr.Count, AnalysisUi.Us(m.Isr.TotalUs), m.Isr.Count > 0 ? AnalysisUi.Us(m.Isr.MaxUs) : "—", m.Isr.Count > 0 ? AnalysisUi.Us(m.Isr.P99Us) : "—",
                AnalysisUi.Us(m.HighestUs), UsBrush(m.HighestUs, th), $"{m.PercentOfCaptureTime:0.###}%"));

        // Histograma (contagens em escala logarítmica para que faixas raras — as importantes — fiquem visíveis).
        var dh = r.DpcOverall.Histogram; var ih = r.IsrOverall.Histogram;
        HistogramLabels = dh.Select(b => b.Label).ToArray();
        DpcHistogram = dh.Select(b => Math.Log10(b.Count + 1)).ToArray();
        IsrHistogram = ih.Select(b => Math.Log10(b.Count + 1)).ToArray();
        Buckets.Clear();
        for (int i = 0; i < dh.Count; i++) Buckets.Add(new LatencyBucketRow(dh[i].Label, dh[i].Count, i < ih.Count ? ih[i].Count : 0));

        TimelineCapacity = Math.Max(2, r.Timeline.Count);
        Timeline = new[]
        {
            new ChartSeries { Name = "Máx. DPC (µs)", Values = r.Timeline.Select(p => p.MaxDpcUs).ToArray(), Stroke = AnalysisUi.Brush("B.Cyan") },
            new ChartSeries { Name = "Máx. ISR (µs)", Values = r.Timeline.Select(p => p.MaxIsrUs).ToArray(), Stroke = AnalysisUi.Brush("B.Violet"), Fill = false },
        };

        PerCpuPercent = r.PerCpu.OrderBy(c => c.Cpu).Select(c => c.PercentTime).ToArray();
        var top = r.PerCpu.OrderByDescending(c => c.PercentTime).FirstOrDefault();
        PerCpuText = top is null ? "" : $"Maior carga: CPU {top.Cpu} com {top.PercentTime:0.###}% do tempo em DPC+ISR (pico DPC {AnalysisUi.Us(top.DpcMaxUs)}, ISR {AnalysisUi.Us(top.IsrMaxUs)}).";

        TopExecutions.Clear();
        foreach (var e in r.TopExecutions.Take(50))
            TopExecutions.Add(new LatencyExecRow(e.Kind == LatencyEventKind.Dpc ? "DPC" : "ISR", e.Module, AnalysisUi.Us(e.DurationUs), UsBrush(e.DurationUs, th),
                $"+{e.OffsetMs / 1000:0.000} s ({e.Timestamp.LocalDateTime:HH:mm:ss})", e.Cpu));

        Findings.Clear();
        foreach (var f in r.Findings) Findings.Add(f);
        ReportWarnings.Clear();
        foreach (var w in r.Warnings) ReportWarnings.Add(w);
        ReportLimitations.Clear();
        foreach (var l in r.Limitations.Count > 0 ? r.Limitations : LatencyTexts.CaptureLimitations) ReportLimitations.Add(l);
    }

    private static string UsBrush(double us, LatencyThresholds t) =>
        us >= t.HighUs ? "B.Danger" : us >= t.ElevatedUs ? "B.Warning" : "B.Success";

    // ------------------------------------------------------------------ (b)
    [RelayCommand]
    private async Task SampleIndicators()
    {
        if (IsSampling) return;
        int sec = Math.Clamp(IndicatorSeconds, 1, 60);
        _samplingCts = new CancellationTokenSource();
        IsSampling = true; SamplingProgress = 0;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) => SamplingProgress = Math.Min(1, (DateTime.UtcNow - started).TotalSeconds / sec);
        timer.Start();
        try
        {
            var ct = _samplingCts.Token;
            var s = await Task.Run(() => new InterruptCounterSampler().SampleAsync(sec, null, ct), ct);
            IndicatorValues.Clear();
            IndicatorValues.Add(new("% tempo em DPC", $"{s.DpcTimePercent.Mean:0.00}% médio", $"pico {s.DpcTimePercent.Max:0.00}% · P95 {s.DpcTimePercent.P95:0.00}%"));
            IndicatorValues.Add(new("% tempo em interrupção", $"{s.InterruptTimePercent.Mean:0.00}% médio", $"pico {s.InterruptTimePercent.Max:0.00}% · P95 {s.InterruptTimePercent.P95:0.00}%"));
            IndicatorValues.Add(new("DPCs enfileirados/s", $"{s.DpcsQueuedPerSec.Mean:N0}", $"pico {s.DpcsQueuedPerSec.Max:N0}"));
            IndicatorValues.Add(new("Interrupções/s", $"{s.InterruptsPerSec.Mean:N0}", $"pico {s.InterruptsPerSec.Max:N0}"));
            IndicatorCapacity = Math.Max(2, s.DpcTimePercent.Samples.Count);
            IndicatorSeries = new[]
            {
                new ChartSeries { Name = "% DPC", Values = s.DpcTimePercent.Samples.ToArray(), Stroke = AnalysisUi.Brush("B.Cyan") },
                new ChartSeries { Name = "% interrupção", Values = s.InterruptTimePercent.Samples.ToArray(), Stroke = AnalysisUi.Brush("B.Violet"), Fill = false },
            };
            IndicatorPerCpu.Clear();
            foreach (var c in s.PerCpu)
                IndicatorPerCpu.Add(new CpuCounterRow(c.Instance, $"{c.MeanDpcPercent:0.00}%", $"{c.MaxDpcPercent:0.00}%", $"{c.MeanInterruptPercent:0.00}%", $"{c.MaxInterruptPercent:0.00}%",
                    Math.Max(c.MaxDpcPercent, c.MaxInterruptPercent) > 10 ? "B.Warning" : "B.Text"));
            IndicatorInterpretation = s.Interpretation;
            IndicatorLimitations.Clear();
            foreach (var l in s.Limitations) IndicatorLimitations.Add(l);
            HasIndicators = true;
        }
        catch (OperationCanceledException) { App.Notify.Info("Teste interrompido", "Nenhum resultado parcial foi exibido."); }
        catch (Exception ex) { App.Log(ex, "Indicadores DPC/ISR"); App.Notify.Error("Falha nos indicadores", ex.Message); }
        finally
        {
            timer.Stop();
            IsSampling = false; SamplingProgress = 0;
            _samplingCts?.Dispose(); _samplingCts = null;
        }
    }

    [RelayCommand]
    private void CancelSampling() => _samplingCts?.Cancel();

    // ------------------------------------------------------------------ (c)
    private void QueryTimer()
    {
        try
        {
            var t = TimerResolutionInspector.Query();
            TimerCurrent = $"{t.CurrentMs:0.###} ms";
            TimerFinest = $"{t.FinestMs:0.###} ms";
            TimerCoarsest = $"{t.CoarsestMs:0.###} ms";
            TimerGlobalRequests = t.GlobalTimerResolutionRequests is { } g ? $"{g} ({(g != 0 ? "comportamento global restaurado" : "desativado")})" : "ausente (padrão do Windows)";
            TimerExplanation = t.Explanation;
        }
        catch (Exception ex) { App.Log(ex, "Resolução do timer"); TimerCurrent = "indisponível"; TimerExplanation = ex.Message; }
    }

    [RelayCommand]
    private void RefreshTimer() => QueryTimer();

    [RelayCommand]
    private async Task CollectTimerRequests()
    {
        if (IsCollectingTimers) return;
        int sec = Math.Clamp(TimerRequestSeconds, TimerResolutionInspector.EnergyMinSeconds, TimerResolutionInspector.EnergyMaxSeconds);
        bool ok = await App.Dialogs.ConfirmAsync("Pedidos de resolução de timer",
            $"Será executado \"powercfg /energy\" no agente elevado por {sec} s (UAC no primeiro uso). O comando BLOQUEIA o agente durante toda a duração " +
            "e o processamento do relatório pode levar até ~2 minutos a mais.",
            "Enquanto isso, outras operações administrativas do RBM Optimizer ficam na fila. Deixe abertos os programas que você quer investigar " +
            "(jogo, navegador com vídeo, áudio). O relatório HTML temporário é apagado ao final; nada é alterado.",
            "Coletar");
        if (!ok) return;
        if (!await _agent.EnsureConnectedAsync())
        {
            App.Notify.Warning("Agente elevado indisponível", AnalysisUi.AgentError(_agent) ?? "Elevação recusada.");
            return;
        }
        _timerCts = new CancellationTokenSource();
        IsCollectingTimers = true; TimerProgress = 0;
        var started = DateTime.UtcNow;
        _timerTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _timerTimer.Tick += (_, _) =>
        {
            double el = (DateTime.UtcNow - started).TotalSeconds;
            TimerProgress = Math.Min(1, el / sec);
            TimerStatus = el < sec ? $"powercfg /energy em andamento… {el:0} / {sec} s" : $"Analisando o relatório… ({el:0} s)";
        };
        _timerTimer.Start();
        using var task = App.Tasks.Begin("Coleta de pedidos de timer");
        try
        {
            var resp = await AnalysisUi.InvokeAbandonableAsync(_agent, AgentOps.LatencyTimerRequests, new TimerRequestsCaptureRequest(sec),
                TimeSpan.FromSeconds(sec + 150), _timerCts.Token);
            if (resp is null)
            {
                App.Notify.Info("Coleta abandonada", "O powercfg continua no agente até terminar; o resultado será descartado.");
                return;
            }
            if (!resp.Ok) { App.Notify.Error("Falha na coleta", resp.Error ?? "Erro desconhecido."); return; }
            var r = resp.Read<TimerRequestsReport>();
            if (r is null) { App.Notify.Error("Falha na coleta", "Resposta vazia do agente."); return; }
            TimerRequests.Clear();
            foreach (var q in r.Requests)
                TimerRequests.Add(new TimerRequestRow(q.ProcessId?.ToString() ?? "—", q.ProcessPath ?? "(desconhecido)", q.RequestedPeriodMs is { } ms ? $"{ms:0.###} ms" : "—"));
            TimerWarnings.Clear();
            foreach (var w in r.Warnings) TimerWarnings.Add(w);
            TimerPlatformCurrent = r.PlatformCurrentResolutionMs is { } cur ? $"Resolução da plataforma durante a coleta: {cur:0.###} ms · {r.Requests.Count} pedido(s) pendente(s)" : $"{r.Requests.Count} pedido(s) pendente(s)";
            HasTimerRequests = true;
            QueryTimer();
        }
        catch (Exception ex) { App.Log(ex, "Pedidos de timer"); App.Notify.Error("Falha na coleta", ex.Message); }
        finally
        {
            _timerTimer?.Stop(); _timerTimer = null;
            IsCollectingTimers = false; TimerStatus = ""; TimerProgress = 0;
            _timerCts?.Dispose(); _timerCts = null;
        }
    }

    [RelayCommand]
    private void CancelTimerRequests() => _timerCts?.Cancel();

    // ------------------------------------------------------------------ (d)
    [RelayCommand]
    private Task RefreshInput() => LoadInputAsync();

    private Task LoadInputAsync() => RunBusy("Lendo dispositivos de entrada…", async () =>
    {
        var rep = await InputDeviceInspector.InspectAsync();
        _inputInfo = rep.Devices;
        var g = rep.GlobalSelectiveSuspend;
        GlobalSuspend = (g.AcValue, g.DcValue) switch
        {
            (null, null) => "Indisponível",
            ({ } ac, { } dc) => $"Na tomada: {(ac == 0 ? "desativada" : "ativada")} · Na bateria: {(dc == 0 ? "desativada" : "ativada")}",
            ({ } ac, null) => $"Na tomada: {(ac == 0 ? "desativada" : "ativada")}",
            (null, { } dc) => $"Na bateria: {(dc == 0 ? "desativada" : "ativada")}",
        };
        GlobalSuspendDetail = g.Interpretation;
        InputDevices.Clear();
        foreach (var d in rep.Devices.OrderBy(d => d.Category).ThenBy(d => d.Name))
        {
            var p = d.Power ?? d.ParentChain.Select(n => n.Power).FirstOrDefault(x => x is not null);
            string state = p?.State switch
            {
                SelectiveSuspendState.Disabled => "Suspensão desativada", SelectiveSuspendState.Enabled => "Suspensão permitida",
                SelectiveSuspendState.Mixed => "Valores mistos", _ => "Padrão do driver",
            };
            string brush = p?.State switch { SelectiveSuspendState.Enabled => "B.Warning", SelectiveSuspendState.Disabled => "B.Success", SelectiveSuspendState.Mixed => "B.Warning", _ => "B.TextMuted" };
            string chain = string.Join("  ›  ", d.ParentChain.Select(n => $"{n.Role}: {n.Name}"));
            InputDevices.Add(new InputDeviceRow(d.Name, CategoryPt(d.Category), d.Bus, d.Service ?? "—", state, brush, p?.Interpretation ?? "Sem valores de energia no registro.",
                string.IsNullOrEmpty(chain) ? "—" : chain, d.InstanceId));
        }
        InputNotes.Clear();
        foreach (var n in rep.Warnings.Concat(rep.Notes)) InputNotes.Add(n);
        InputLoaded = true;
    });

    private static string CategoryPt(InputDeviceCategory c) => c switch
    {
        InputDeviceCategory.Mouse => "Mouse", InputDeviceCategory.Keyboard => "Teclado", InputDeviceCategory.GameController => "Controle", _ => "Outro HID",
    };

    [RelayCommand]
    private void TogglePolling()
    {
        if (IsPolling) { StopPollingCore(); PollingStatus = "Teste encerrado. Os últimos valores medidos permanecem na tabela."; return; }
        try
        {
            var win = Application.Current.MainWindow ?? throw new InvalidOperationException("Janela principal indisponível.");
            var hwnd = new WindowInteropHelper(win).Handle;
            _hwndSource = HwndSource.FromHwnd(hwnd) ?? throw new InvalidOperationException("HWND da janela principal indisponível.");
            _estimator = PollingRateEstimator.CreateDefault();
            _hwndSource.AddHook(WndProc);
            RawInputHelper.Register(hwnd, RawInputDeviceKinds.Mouse);
            Polling.Clear();
            IsPolling = true;
            PollingStatus = "Medindo… mova o mouse continuamente (círculos rápidos). Pausas > 50 ms são ignoradas.";
            _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            _pollTimer.Tick += (_, _) => RefreshPolling();
            _pollTimer.Start();
        }
        catch (Exception ex)
        {
            StopPollingCore();
            App.Log(ex, "Teste de polling");
            App.Notify.Error("Teste de polling indisponível", ex.Message);
        }
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == RawInputHelper.WM_INPUT && _estimator is { } est)
        {
            long ts = Stopwatch.GetTimestamp();
            if (RawInputHelper.TryParse(lParam, out var e) && e.IsMovement) est.Add(e.Device, ts);
        }
        return IntPtr.Zero; // não marca handled: DefWindowProc precisa processar WM_INPUT
    }

    private void RefreshPolling()
    {
        if (_estimator is null) return;
        var all = _estimator.GetAllEstimates();
        Polling.Clear();
        foreach (var e in all)
        {
            Polling.Add(new PollingRow(DeviceName(e.DeviceHandle),
                e.EstimatedRateHz > 0 ? $"{e.EstimatedRateHz:0} Hz" : "—",
                e.NearestStandardRateHz > 0 ? $"{e.NearestStandardRateHz} Hz" : "—",
                $"{e.MedianIntervalMs:0.###} ms", $"{e.P99IntervalMs:0.###} ms", $"{e.StabilityPercent:0}%",
                e.Confidence.Pt(), e.Confidence switch { Confidence.High => "B.Success", Confidence.Medium => "B.Warning", _ => "B.TextMuted" },
                e.IntervalCount, string.Join(" ", e.Notes.Skip(1))));
        }
        if (all.Count == 0 && IsPolling) PollingStatus = "Aguardando movimento do mouse… (são necessários ao menos 10 intervalos)";
        else if (IsPolling) PollingStatus = "Medindo… a confiança sobe com mais intervalos e movimento contínuo.";
    }

    private string DeviceName(long handle)
    {
        if (_deviceNames.TryGetValue(handle, out var n)) return n;
        string name = $"Dispositivo 0x{handle:X}";
        try
        {
            var path = RawInputHelper.GetDeviceName(new IntPtr(handle));
            if (!string.IsNullOrEmpty(path))
            {
                // \\?\HID#VID_xxxx&PID_yyyy&MI_00#7&abc&0&0000#{guid} → HID\VID_xxxx&PID_yyyy&MI_00\7&abc&0&0000
                var p = path.StartsWith(@"\\?\", StringComparison.Ordinal) ? path[4..] : path;
                int g = p.LastIndexOf("#{", StringComparison.Ordinal);
                if (g > 0) p = p[..g];
                var instance = p.Replace('#', '\\');
                var match = _inputInfo.FirstOrDefault(d => string.Equals(d.InstanceId, instance, StringComparison.OrdinalIgnoreCase)
                    || d.ParentChain.Any(c => string.Equals(c.InstanceId, instance, StringComparison.OrdinalIgnoreCase)));
                name = match?.Name ?? instance;
            }
        }
        catch { /* nome é apenas informativo */ }
        _deviceNames[handle] = name;
        return name;
    }

    private void StopPollingCore()
    {
        _pollTimer?.Stop(); _pollTimer = null;
        if (_hwndSource is not null)
        {
            try { RawInputHelper.Unregister(RawInputDeviceKinds.Mouse); } catch (Exception ex) { App.Log(ex, "Cancelar Raw Input"); }
            try { _hwndSource.RemoveHook(WndProc); } catch { }
            _hwndSource = null;
        }
        if (_estimator is not null) RefreshPolling();
        _estimator = null;
        IsPolling = false;
    }

    // ------------------------------------------------------------------ (e)
    [RelayCommand]
    private Task RefreshMsi() => LoadMsiAsync();

    private Task LoadMsiAsync() => RunBusy("Lendo modo de interrupção dos dispositivos PCI…", async () =>
    {
        var rep = await InterruptModeInspector.InspectAsync();
        MsiDevices.Clear();
        foreach (var d in rep.Devices.OrderBy(d => d.Category == PciDeviceCategory.Other).ThenBy(d => d.Category).ThenBy(d => d.Name))
            MsiDevices.Add(new MsiRow(d.Name, PciCategoryPt(d.Category),
                d.EffectiveMode switch { InterruptMode.Msi => "MSI", InterruptMode.LineBased => "Linha (INTx)", _ => "Desconhecido" },
                d.EffectiveMode switch { InterruptMode.Msi => "B.Success", InterruptMode.LineBased => "B.Warning", _ => "B.TextMuted" },
                d.MsiSupported is { } s ? (s != 0 ? "Sim" : "Não") : "—",
                d.MessageNumberLimit?.ToString() ?? "—", d.DevicePolicyName ?? "—", d.DevicePriorityName ?? "—",
                d.Irqs.Count == 0 ? "—" : string.Join(", ", d.Irqs.Take(6)) + (d.Irqs.Count > 6 ? "…" : ""),
                d.Interpretation));
        int msi = rep.Devices.Count(d => d.EffectiveMode == InterruptMode.Msi), line = rep.Devices.Count(d => d.EffectiveMode == InterruptMode.LineBased);
        MsiSummary = $"{rep.Devices.Count} dispositivo(s) PCI · {msi} em MSI · {line} por linha (INTx)";
        MsiNotes.Clear();
        foreach (var n in rep.Warnings.Concat(rep.Notes)) MsiNotes.Add(n);
        MsiLoaded = true;
    });

    private static string PciCategoryPt(PciDeviceCategory c) => c switch
    {
        PciDeviceCategory.Gpu => "GPU", PciDeviceCategory.Network => "Rede", PciDeviceCategory.Storage => "Armazenamento",
        PciDeviceCategory.UsbController => "Controlador USB", PciDeviceCategory.Audio => "Áudio", PciDeviceCategory.Bridge => "Ponte", _ => "Outro",
    };
}
