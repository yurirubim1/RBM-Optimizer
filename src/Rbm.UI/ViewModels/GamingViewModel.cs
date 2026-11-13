using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows.Threading;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Hardware;
using Rbm.Core.Telemetry;
using Rbm.Data;
using Rbm.Gaming;
using Rbm.Gaming.Correlation;
using Rbm.Gaming.Frames;
using Rbm.Gaming.Library;
using Rbm.Gaming.Overlays;
using Rbm.Gaming.PresentMon;
using Rbm.Gaming.Profiles;
using Rbm.Gaming.Sessions;
using Rbm.UI.Controls;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.Analysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.ViewModels;

public sealed record GameRow(DetectedGame Game)
{
    public string Name => Game.Name;
    public string Source => GamingViewModel.SourcePt(Game.Source) + (Game.AlsoFoundIn.Count > 0 ? " (+" + string.Join(", ", Game.AlsoFoundIn.Select(GamingViewModel.SourcePt)) + ")" : "");
    public string Exe => Game.ProcessName ?? "executável não identificado";
    public string ConfidenceText => Game.Executable.IsResolved ? "confiança " + Game.Executable.Confidence.Pt().ToLowerInvariant() : "—";
    public string ConfidenceBrush => !Game.Executable.IsResolved ? "B.Danger" : Game.Executable.Confidence switch { Confidence.High => "B.Success", Confidence.Medium => "B.Warning", _ => "B.TextMuted" };
    public bool IsManual => Game.Source == GameSource.Manual;
}

/// <summary>Documento persistido em <see cref="Collections.GameSessions"/> (sessão completa + frametimes reduzidos para o gráfico).</summary>
public sealed record GameSessionDoc(string Id, DateTimeOffset At, string GameId, string GameName, FrameSession Session, StutterCorrelationReport? Correlation, double[] FrameTimes);

public sealed record GameSessionItem(GameSessionDoc Doc)
{
    public string Display => $"{Doc.At.LocalDateTime:g} · {Doc.GameName} · {Doc.Session.Metrics.AverageFps:0.#} FPS · {Doc.Session.Duration.TotalSeconds:0} s"
                             + (string.IsNullOrWhiteSpace(Doc.Session.Conditions.Resolution) ? "" : " · " + Doc.Session.Conditions.Resolution);
    public override string ToString() => Display;
}

public sealed record GpuOption(GpuPreference Key, string Value) { public override string ToString() => Value; }
public sealed record GKv(string Label, string Value, string? Hint = null, string Brush = "B.Text");
public sealed record StutterRow(string Time, string FrameTime, string Baseline);
public sealed record PresentModeRow(string Mode, string Percent, string Impact, string Brush);
public sealed record AssociationRow(string Label, string Text, string Lift, string Brush, bool Notable);
public sealed record OverlayRow(string Product, string Category, string Process, string Impact, string Test, string Confidence);
public sealed record DeltaRow(string Name, string Before, string After, string Delta, string Brush);
public sealed record ProfileSessionRow(string When, string Fps, string Lows, string Stutters, string Bound, string Conditions);

public sealed partial class GamingViewModel : PageViewModel
{
    private static readonly HttpClient s_http = new() { Timeout = TimeSpan.FromMinutes(5) };
    private readonly IAgentClient _agent;
    private readonly RbmDatabase _db;
    private readonly LiveTelemetry _live;
    private readonly ISystemInventory _inventory;
    private readonly IChangeJournal _journal;
    private readonly ISettingsStore _settings;
    private readonly GameProfileStore _profiles = new();
    public override string Key => "gaming";

    [ObservableProperty] private string _tab = "library";

    // ---------------- Resumo (banner) ----------------
    [ObservableProperty] private string _heroTitle = "Procurando jogos instalados…";
    [ObservableProperty] private string _heroChip = "Verificando o medidor de FPS…";
    [ObservableProperty] private string _heroTone = "info";
    public string HeroText => "Escolha um jogo, meça o FPS enquanto joga e compare antes e depois de uma mudança — tudo sem injetar nada no jogo.";
    public bool HasProcess => !string.IsNullOrWhiteSpace(ProcessName);

    [RelayCommand]
    private void GoTab(string? tab) { if (!string.IsNullOrWhiteSpace(tab)) Tab = tab; }

    /// <summary>Atalho do perfil do jogo: preenche o processo e abre o passo a passo de medição.</summary>
    [RelayCommand]
    private void MeasureSelected()
    {
        if (SelectedGame?.Game.ProcessName is { } pn && !IsCapturing) ProcessName = pn;
        Tab = "capture";
    }

    partial void OnProcessNameChanged(string value) => OnPropertyChanged(nameof(HasProcess));

    partial void OnPmReadyChanged(bool value) => UpdateHeroChip();
    partial void OnPmStatusChanged(string value) => UpdateHeroChip();

    private void UpdateHeroChip()
    {
        (HeroChip, HeroTone) = PmReady ? ("Medidor de FPS pronto", "ok")
            : PmStatus.StartsWith("Verificando", StringComparison.Ordinal) ? ("Verificando o medidor de FPS…", "info")
            : _tool is null ? ("Medidor de FPS não instalado", "warn") : ("Medidor de FPS bloqueado", "danger");
    }

    // ---------------- Biblioteca ----------------
    private List<GameRow> _allGames = new();
    public ObservableCollection<GameRow> Games { get; } = new();
    public ObservableCollection<string> ScanSummary { get; } = new();
    public ObservableCollection<string> ScanWarnings { get; } = new();
    [ObservableProperty] private string _search = "";
    [ObservableProperty] private GameRow? _selectedGame;
    [ObservableProperty] private bool _scanned;
    [ObservableProperty] private string _scanStatus = "Biblioteca ainda não lida.";

    // Perfil do jogo selecionado
    [ObservableProperty] private bool _hasSelection;
    public ObservableCollection<GKv> GameInfo { get; } = new();
    public ObservableCollection<string> GameAlternatives { get; } = new();
    public ObservableCollection<ProfileSessionRow> GameSessions { get; } = new();
    public IReadOnlyList<GpuOption> GpuChoices { get; } = new[] { GpuPreference.Default, GpuPreference.PowerSaving, GpuPreference.HighPerformance }
        .Select(p => new GpuOption(p, GpuPreferenceStore.Describe(p))).ToArray();
    [ObservableProperty] private GpuPreference _gpuChoice = GpuPreference.Default;
    [ObservableProperty] private string _gpuCurrent = "—";
    [ObservableProperty] private bool _gpuAvailable;
    [ObservableProperty] private bool _canUndoGpu;
    private GpuPreferenceReading? _gpuPrevious;

    // ---------------- PresentMon ----------------
    [ObservableProperty] private string _pmStatus = "Verificando…";
    [ObservableProperty] private string _pmDetail = "";
    [ObservableProperty] private string _pmBrush = "B.TextMuted";
    [ObservableProperty] private bool _pmReady;
    [ObservableProperty] private bool _isDownloading;
    [ObservableProperty] private double _downloadProgress;
    [ObservableProperty] private string _capabilityText = "";
    [ObservableProperty] private bool _canCaptureUnelevated;
    private PresentMonTool? _tool;

    // ---------------- Captura ----------------
    public int[] CaptureDurations { get; } = { 20, 30, 60, 120, 300 };
    [ObservableProperty] private int _captureSeconds = 60;
    [ObservableProperty] private string _processName = "";
    [ObservableProperty] private string _resolution = "";
    [ObservableProperty] private string _settingsLabel = "";
    [ObservableProperty] private string _scenario = "";
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private double _captureProgress;
    [ObservableProperty] private string _captureStatus = "";
    private CancellationTokenSource? _captureCts;

    // ---------------- Resultado da sessão ----------------
    [ObservableProperty] private bool _hasSession;
    [ObservableProperty] private string _sessionTitle = "";
    /// <summary>Resumo em linguagem simples da sessão aberta (a partir das métricas medidas).</summary>
    [ObservableProperty] private string _sessionHeadline = "";
    [ObservableProperty] private string _sessionPlain = "";
    [ObservableProperty] private string _sessionGame = "";
    public ObservableCollection<GKv> SessionMetrics { get; } = new();
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _frameTimeSeries;
    [ObservableProperty] private int _frameTimeCapacity = 100;
    [ObservableProperty] private IReadOnlyList<ChartSeries>? _fpsSeries;
    [ObservableProperty] private int _fpsCapacity = 60;
    [ObservableProperty] private double[] _histogramValues = Array.Empty<double>();
    [ObservableProperty] private string[] _histogramLabels = Array.Empty<string>();
    [ObservableProperty] private string _stutterSummary = "";
    [ObservableProperty] private string _stutterDefinition = "";
    public ObservableCollection<StutterRow> Stutters { get; } = new();
    [ObservableProperty] private string _presentModeText = "";
    public ObservableCollection<PresentModeRow> PresentModes { get; } = new();
    [ObservableProperty] private string _tearingText = "";
    [ObservableProperty] private string _boundLabel = "";
    [ObservableProperty] private string _boundBrush = "B.TextMuted";
    [ObservableProperty] private string _boundDetail = "";
    [ObservableProperty] private string _boundExplanation = "";
    [ObservableProperty] private string _correlationSummary = "";
    [ObservableProperty] private bool _hasCorrelation;
    public ObservableCollection<AssociationRow> Associations { get; } = new();
    public ObservableCollection<string> ClusterNotes { get; } = new();
    public ObservableCollection<string> SessionWarnings { get; } = new();
    public string MetricDefinitions => FrameMetrics.Definitions;
    public string CorrelationDisclaimer => StutterCorrelationReport.DisclaimerPt;

    // ---------------- Sessões e comparação ----------------
    public ObservableCollection<GameSessionItem> SavedSessions { get; } = new();
    [ObservableProperty] private GameSessionItem? _selectedSession;
    [ObservableProperty] private GameSessionItem? _beforeSession;
    [ObservableProperty] private GameSessionItem? _afterSession;
    [ObservableProperty] private bool _settingsChanged;
    [ObservableProperty] private bool _hasComparison;
    [ObservableProperty] private string _compareHeadline = "";
    [ObservableProperty] private string _compareVerdict = "";
    [ObservableProperty] private string _compareBrush = "B.TextMuted";
    public ObservableCollection<DeltaRow> CompareDeltas { get; } = new();
    public ObservableCollection<string> CompareCaveats { get; } = new();

    // ---------------- Sistema ----------------
    public ObservableCollection<OverlayRow> Overlays { get; } = new();
    [ObservableProperty] private string _overlayStatus = "";
    [ObservableProperty] private string _dvrText = "";
    public ObservableCollection<GKv> DvrValues { get; } = new();
    public string OverlayDisclaimer => OverlayDetector.DisclaimerPt;

    public GamingViewModel(IAgentClient agent, RbmDatabase db, LiveTelemetry live, ISystemInventory inventory, IChangeJournal journal, ISettingsStore settings)
    {
        _agent = agent; _db = db; _live = live; _inventory = inventory; _journal = journal; _settings = settings;
        _tab = AnalysisUi.StartupTab("library", "capture", "sessions", "system") ?? "library";
    }

    public static string SourcePt(GameSource s) => s switch
    {
        GameSource.Steam => "Steam", GameSource.Epic => "Epic Games", GameSource.Gog => "GOG", GameSource.Xbox => "Xbox / Microsoft Store",
        GameSource.EaApp => "EA app", GameSource.UbisoftConnect => "Ubisoft Connect", GameSource.BattleNet => "Battle.net",
        GameSource.Manual => "Manual", _ => "Desconhecida",
    };

    public override async Task OnNavigatedToAsync()
    {
        var cap = PresentMonCapture.GetCapability();
        CapabilityText = cap.MessagePt;
        CanCaptureUnelevated = cap.CanCaptureUnelevated;
        var pm = RefreshPresentMonAsync();
        if (!Scanned) await ScanAsync();
        await LoadSessionsAsync();
        await pm;
        if (Tab == "system") await LoadSystemAsync();
    }

    public override void OnNavigatedFrom() { }

    partial void OnTabChanged(string value)
    {
        if (value == "system") _ = LoadSystemAsync();
    }

    // =====================================================================================
    // Biblioteca
    // =====================================================================================
    [RelayCommand]
    private Task Scan() => ScanAsync();

    private Task ScanAsync() => RunBusy("Procurando jogos instalados…", async () =>
    {
        var doc = await _profiles.LoadAsync();
        if (_profiles.LastLoadWarning is { } w) App.Notify.Warning("Perfis de jogos", w);
        var manual = doc.Games.Where(g => g.Source == GameSource.Manual && g.ExecutablePath is not null)
            .Select(g => GameLibraryService.CreateManualEntry(g.ExecutablePath!, g.Name))
            .Where(r => r.Success).Select(r => r.Value!).ToList();
        var result = await Task.Run(() => new GameLibraryService().ScanAsync(manual));
        _allGames = result.Games.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).Select(g => new GameRow(g)).ToList();
        ScanSummary.Clear();
        foreach (var s in result.PerSource.Where(s => s.Source != GameSource.Manual))
            ScanSummary.Add($"{SourcePt(s.Source)}: " + (s.SourcePresent ? $"{s.Games.Count} jogo(s)" : "não instalado"));
        ScanWarnings.Clear();
        foreach (var w2 in result.AllWarnings.Take(20)) ScanWarnings.Add(w2);
        Scanned = true;
        ScanStatus = $"{_allGames.Count} jogo(s) · lido em {result.ScannedAt.LocalDateTime:t}";
        HeroTitle = _allGames.Count switch
        {
            0 => "Nenhum jogo encontrado nas lojas instaladas",
            1 => "1 jogo encontrado",
            var n => $"{n} jogos encontrados",
        };
        ApplyFilter();
    });

    partial void OnSearchChanged(string value) => ApplyFilter();

    private void ApplyFilter()
    {
        var keep = SelectedGame?.Game.Id;
        var q = Search?.Trim() ?? "";
        Games.Clear();
        foreach (var g in _allGames.Where(g => q.Length == 0 || g.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase)
                                               || (g.Game.ProcessName?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false)))
            Games.Add(g);
        SelectedGame = Games.FirstOrDefault(g => g.Game.Id == keep);
    }

    [RelayCommand]
    private async Task AddManual()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Escolha o executável do jogo",
            Filter = "Executáveis (*.exe)|*.exe",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() != true) return;
        var r = GameLibraryService.CreateManualEntry(dlg.FileName);
        if (!r.Success) { App.Notify.Error("Entrada inválida", r.Message); return; }
        try
        {
            await _profiles.UpsertAsync(GameProfile.FromDetected(r.Value!));
            var row = new GameRow(r.Value!);
            _allGames.RemoveAll(g => g.Game.Id == row.Game.Id);
            _allGames.Add(row);
            _allGames = _allGames.OrderBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
            HeroTitle = _allGames.Count == 1 ? "1 jogo encontrado" : $"{_allGames.Count} jogos encontrados";
            Search = "";
            ApplyFilter();
            SelectedGame = Games.FirstOrDefault(g => g.Game.Id == row.Game.Id);
            App.Notify.Success("Jogo adicionado", r.Value!.Name);
        }
        catch (Exception ex) { App.Log(ex, "Adicionar jogo manual"); App.Notify.Error("Falha ao salvar", ex.Message); }
    }

    [RelayCommand]
    private async Task RemoveManual()
    {
        if (SelectedGame is not { IsManual: true } g) return;
        if (!await App.Dialogs.ConfirmAsync("Remover jogo manual", $"Remover \"{g.Name}\" da lista? O histórico de sessões deste perfil também é removido do games.json.", confirmText: "Remover")) return;
        try
        {
            await _profiles.RemoveAsync(g.Game.Id);
            _allGames.Remove(g);
            HeroTitle = _allGames.Count == 1 ? "1 jogo encontrado" : $"{_allGames.Count} jogos encontrados";
            ApplyFilter();
        }
        catch (Exception ex) { App.Notify.Error("Falha ao remover", ex.Message); }
    }

    [RelayCommand]
    private void GoProfiles() => App.Services.GetRequiredService<MainViewModel>().Navigate("profiles");

    partial void OnSelectedGameChanged(GameRow? value)
    {
        HasSelection = value is not null;
        GameInfo.Clear(); GameAlternatives.Clear(); GameSessions.Clear();
        _gpuPrevious = null; CanUndoGpu = false;
        if (value is null) return;
        var g = value.Game;
        if (g.ProcessName is { } pn && !IsCapturing) ProcessName = pn;
        GameInfo.Add(new("Origem", value.Source, g.SourceId is null ? null : $"ID na loja: {g.SourceId}"));
        GameInfo.Add(new("Executável", g.Executable.Path ?? "não identificado", g.Executable.Reason, value.ConfidenceBrush));
        GameInfo.Add(new("Confiança", g.Executable.IsResolved ? g.Executable.Confidence.Pt() : "—", "Alta = informado pelo manifesto da loja; média/baixa = heurística (verifique).", value.ConfidenceBrush));
        if (g.LaunchExecutablePath is { } le && !string.Equals(le, g.Executable.Path, StringComparison.OrdinalIgnoreCase))
            GameInfo.Add(new("Iniciado por", le, "A loja abre este executável, mas o processo que renderiza é o acima."));
        GameInfo.Add(new("Pasta", g.InstallDirectory ?? "—", g.SizeOnDiskBytes is { } sz ? $"Tamanho informado: {Fmt.Bytes(sz)}" : null));
        foreach (var n in g.Notes) GameInfo.Add(new("Observação", n));
        foreach (var a in g.Executable.Alternatives.Take(6)) GameAlternatives.Add(a);
        ReadGpu();
        _ = LoadProfileSessionsAsync(g.Id);
    }

    private void ReadGpu()
    {
        var exe = SelectedGame?.Game.Executable.Path;
        GpuAvailable = exe is not null && File.Exists(exe);
        if (!GpuAvailable) { GpuCurrent = "Executável não identificado — escolha-o manualmente para ajustar a GPU."; return; }
        try
        {
            var r = GpuPreferenceStore.Read(exe!);
            GpuChoice = r.Preference ?? GpuPreference.Default;
            GpuCurrent = r.Exists ? $"Atual: {GpuPreferenceStore.Describe(r.Preference ?? GpuPreference.Default)}" + (r.Preference is null ? $" (valor não reconhecido: {r.RawValue})" : "")
                                  : "Atual: sem configuração (o Windows decide)";
        }
        catch (Exception ex) { GpuCurrent = "Não foi possível ler: " + ex.Message; }
    }

    [RelayCommand]
    private async Task ApplyGpu()
    {
        var g = SelectedGame?.Game;
        if (g?.Executable.Path is not { } exe) return;
        var r = GpuPreferenceStore.Set(exe, GpuChoice);
        if (!r.Success) { App.Notify.Error("Preferência de GPU", r.Message); return; }
        _gpuPrevious = r.Value; CanUndoGpu = true;
        App.Notify.Success("Preferência de GPU", r.Message);
        try
        {
            var doc = await _profiles.LoadAsync();
            var prof = doc.Games.FirstOrDefault(p => string.Equals(p.Id, g.Id, StringComparison.OrdinalIgnoreCase)) ?? GameProfile.FromDetected(g);
            await _profiles.UpsertAsync(prof with { PreferredGpu = GpuChoice });
        }
        catch (Exception ex) { App.Log(ex, "Salvar perfil de jogo"); }
        ReadGpu();
    }

    [RelayCommand]
    private void UndoGpu()
    {
        if (_gpuPrevious is null) return;
        var r = GpuPreferenceStore.Restore(_gpuPrevious);
        if (r.Success) { App.Notify.Success("Preferência de GPU", r.Message); _gpuPrevious = null; CanUndoGpu = false; }
        else App.Notify.Error("Preferência de GPU", r.Message);
        ReadGpu();
    }

    private async Task LoadProfileSessionsAsync(string gameId)
    {
        try
        {
            var doc = await _profiles.LoadAsync();
            var prof = doc.Games.FirstOrDefault(p => string.Equals(p.Id, gameId, StringComparison.OrdinalIgnoreCase));
            if (SelectedGame?.Game.Id != gameId) return;
            GameSessions.Clear();
            if (prof is null) return;
            foreach (var s in prof.Sessions.OrderByDescending(s => s.Start).Take(30))
                GameSessions.Add(new ProfileSessionRow($"{s.Start.LocalDateTime:g} · {s.Duration.TotalSeconds:0} s", $"{s.AverageFps:0.#} FPS",
                    $"1% {s.Low1PercentFps:0.#} · 0,1% {s.Low01PercentFps:0.#}", $"{s.StutterCount} ({s.StuttersPerMinute:0.#}/min)", s.BoundLabelPt,
                    string.Join(" · ", new[] { s.Conditions.Resolution, s.Conditions.SettingsLabel, s.Conditions.Scenario }.Where(x => !string.IsNullOrWhiteSpace(x)))));
        }
        catch (Exception ex) { App.Log(ex, "Sessões do perfil"); }
    }

    // =====================================================================================
    // PresentMon
    // =====================================================================================
    private async Task RefreshPresentMonAsync()
    {
        PmStatus = "Verificando PresentMon…"; PmBrush = "B.TextMuted";
        try
        {
            string? chosen = _settings.Get<string?>("gaming.presentmonPath", null);
            if (chosen is not null && !File.Exists(chosen)) chosen = null;
            var r = await Task.Run(() => PresentMonLocator.Find(chosen));
            ApplyTool(r);
        }
        catch (Exception ex) { App.Log(ex, "PresentMon"); PmStatus = "Falha ao verificar o PresentMon"; PmDetail = ex.Message; PmBrush = "B.Danger"; PmReady = false; }
    }

    private void ApplyTool(OperationResult<PresentMonTool> r)
    {
        if (!r.Success || r.Value is null)
        {
            _tool = null; PmReady = false; PmBrush = "B.Warning";
            PmStatus = "PresentMon não instalado";
            PmDetail = r.Message;
            return;
        }
        var t = r.Value;
        _tool = t;
        PmReady = t.IsTrusted && !t.HashMismatch;
        PmBrush = PmReady ? "B.Success" : "B.Danger";
        PmStatus = PmReady ? $"PresentMon {t.Version?.ToString() ?? "(versão desconhecida)"} pronto"
                 : !t.IsTrusted ? "Assinatura não confiável — captura bloqueada" : "O arquivo mudou desde o download — captura bloqueada";
        PmDetail = $"{t.Signature.Display} · SHA-256 {t.Sha256[..Math.Min(16, t.Sha256.Length)]}… · {t.Path}";
    }

    [RelayCommand]
    private async Task DownloadPresentMon()
    {
        if (IsDownloading) return;
        bool ok = await App.Dialogs.ConfirmAsync("Baixar PresentMon",
            "O PresentMon é a ferramenta oficial da Intel (código aberto) que mede FPS e frametime via ETW, sem injetar nada no jogo (seguro para anti-cheat).",
            "Origem: release mais recente de github.com/GameTechDev/PresentMon (somente o executável console x64).\n" +
            $"Verificações: tamanho, SHA-256 publicado pelo GitHub e assinatura Authenticode válida da Intel. Um arquivo que falhe em qualquer verificação é descartado.\n" +
            $"Destino: {RbmPaths.Tools}\nNenhum outro dado é enviado.", "Baixar e verificar");
        if (!ok) return;
        IsDownloading = true; DownloadProgress = 0;
        using var task = App.Tasks.Begin("Baixando PresentMon");
        try
        {
            var progress = new Progress<double>(p => DownloadProgress = Math.Clamp(p, 0, 1));
            var r = await Task.Run(() => new PresentMonDownloader(s_http).DownloadLatestAsync(progress));
            if (!r.Success) { App.Notify.Error("Download do PresentMon", r.Message); return; }
            _settings.Set<string?>("gaming.presentmonPath", null);
            App.Notify.Success("PresentMon verificado", $"Versão {r.Value!.Version} · {r.Value.Signer ?? "assinatura válida"}");
            await RefreshPresentMonAsync();
        }
        catch (Exception ex) { App.Log(ex, "Download PresentMon"); App.Notify.Error("Download do PresentMon", ex.Message); }
        finally { IsDownloading = false; DownloadProgress = 0; }
    }

    [RelayCommand]
    private async Task ChoosePresentMon()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Escolha o PresentMon (console)", Filter = "PresentMon (PresentMon*.exe)|PresentMon*.exe|Executáveis (*.exe)|*.exe", CheckFileExists = true };
        if (dlg.ShowDialog() != true) return;
        var path = dlg.FileName;
        var r = await Task.Run(() => PresentMonLocator.Inspect(path));
        if (!r.Success) { App.Notify.Error("PresentMon", r.Message); return; }
        if (!r.Value!.IsTrusted) { App.Notify.Error("PresentMon recusado", $"O arquivo precisa ter assinatura válida da Intel. {r.Value.Signature.Display}"); return; }
        _settings.Set<string?>("gaming.presentmonPath", r.Value.Path);
        ApplyTool(r);
    }

    [RelayCommand]
    private Task RecheckPresentMon() => RefreshPresentMonAsync();

    // =====================================================================================
    // Captura
    // =====================================================================================
    [RelayCommand]
    private async Task StartCapture()
    {
        if (IsCapturing) return;
        var name = PresentMonCapture.ValidateProcessName(ProcessName);
        if (!name.Success) { App.Notify.Error("Processo inválido", name.Message); return; }
        if (_tool is null || !PmReady) { App.Notify.Warning("PresentMon indisponível", "Baixe e verifique o PresentMon antes de capturar."); return; }
        string proc = name.Value!;
        var running = Process.GetProcessesByName(Path.GetFileNameWithoutExtension(proc));
        bool isRunning = running.Length > 0;
        foreach (var p in running) p.Dispose();
        if (!isRunning)
        {
            App.Notify.Warning("Jogo não está aberto", $"O processo {proc} não está em execução. Abra o jogo, entre na cena a medir e inicie a captura.");
            return;
        }
        int sec = Math.Clamp(CaptureSeconds, PresentMonCapture.MinSeconds, PresentMonCapture.MaxSeconds);
        var cap = PresentMonCapture.GetCapability();
        CanCaptureUnelevated = cap.CanCaptureUnelevated; CapabilityText = cap.MessagePt;
        if (!cap.CanCaptureUnelevated && !await _agent.EnsureConnectedAsync())
        {
            App.Notify.Warning("Captura indisponível", (AnalysisUi.AgentError(_agent) ?? "Elevação recusada.") + " " + cap.MessagePt);
            return;
        }

        var game = SelectedGame?.Game is { } sg && string.Equals(sg.ProcessName, proc, StringComparison.OrdinalIgnoreCase) ? sg : null;
        var conditions = await BuildConditionsAsync();
        var req = new FrameCaptureRequest(proc, sec, _tool.Path);
        _captureCts = new CancellationTokenSource();
        IsCapturing = true; CaptureProgress = 0;
        var started = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        timer.Tick += (_, _) =>
        {
            double el = (DateTime.UtcNow - started).TotalSeconds;
            CaptureProgress = Math.Min(1, el / sec);
            CaptureStatus = el < sec ? $"Capturando {proc}… {el:0} / {sec} s — continue jogando normalmente" : "Finalizando o PresentMon e analisando…";
        };
        timer.Start();
        using var task = App.Tasks.Begin("Captura de FPS");
        try
        {
            FrameCaptureResult? res;
            if (cap.CanCaptureUnelevated)
            {
                var outDir = RbmPaths.Ensure(Path.Combine(RbmPaths.DataRoot, "captures"));
                var ct = _captureCts.Token;
                var r = await Task.Run(() => PresentMonCapture.CaptureAsync(req, outDir, ct));
                if (ct.IsCancellationRequested) { App.Notify.Info("Captura cancelada", "A sessão ETW do PresentMon foi encerrada; nada foi salvo."); return; }
                if (!r.Success) { App.Notify.Error("Falha na captura", r.Message); return; }
                res = r.Value;
            }
            else
            {
                var resp = await AnalysisUi.InvokeAbandonableAsync(_agent, AgentOps.FrameCapture, req, TimeSpan.FromSeconds(sec + 60), _captureCts.Token);
                if (resp is null) { App.Notify.Info("Captura abandonada", "O agente termina a captura sozinho ao fim da duração; o resultado será descartado."); return; }
                if (!resp.Ok) { App.Notify.Error("Falha na captura", resp.Error ?? "Erro desconhecido."); return; }
                res = resp.Read<FrameCaptureResult>();
            }
            if (res?.CsvPath is null || !File.Exists(res.CsvPath))
            {
                App.Notify.Warning("Nenhum quadro capturado", res?.MessagePt ?? "O PresentMon não gerou dados. O jogo estava em primeiro plano renderizando?");
                return;
            }
            CaptureStatus = "Analisando quadros…";
            await AnalyzeAndSaveAsync(res, proc, game, conditions);
        }
        catch (Exception ex)
        {
            App.Log(ex, "Captura de FPS");
            App.Notify.Error("Falha na captura", ex.Message);
        }
        finally
        {
            timer.Stop();
            IsCapturing = false; CaptureProgress = 0; CaptureStatus = "";
            _captureCts?.Dispose(); _captureCts = null;
        }
    }

    [RelayCommand]
    private void CancelCapture() => _captureCts?.Cancel();

    private async Task<SessionConditions> BuildConditionsAsync()
    {
        List<string>? tweaks = null;
        try { tweaks = (await _journal.ActiveChangesAsync()).Select(c => c.TweakId).Distinct().ToList(); } catch { }
        var power = _inventory.Current?.Power;
        static string? N(string s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
        return new SessionConditions(N(power?.ActiveSchemeName ?? ""), power is { ActiveSchemeGuid: var g } && g != Guid.Empty ? g : null, tweaks,
            N(Resolution), N(SettingsLabel), N(Scenario));
    }

    private async Task AnalyzeAndSaveAsync(FrameCaptureResult res, string proc, DetectedGame? game, SessionConditions conditions)
    {
        var telemetry = _live.History;
        var (analysis, frameTimes) = await Task.Run(() =>
        {
            var parsed = PresentMonCsvParser.ParseFile(res.CsvPath!, new PresentMonParseOptions(proc));
            if (parsed.Frames.Count < 2) throw new InvalidOperationException("O CSV tem quadros insuficientes para análise (o jogo estava minimizado ou em outra conta?).");
            return (FrameAnalyzer.Analyze(parsed), Downsample(parsed.Frames.Select(f => f.FrameTimeMs).ToArray(), 1500));
        });
        var start = res.StartedAt;
        var window = telemetry.Where(t => t.Timestamp >= start.AddSeconds(-2) && t.Timestamp <= start + res.Elapsed + TimeSpan.FromSeconds(2)).ToList();
        string gameId = game?.Id ?? "process:" + proc.ToLowerInvariant();
        string gameName = game?.Name ?? proc;
        var (session, corr) = GamingPerformanceCenter.BuildSession(gameId, gameName, start, analysis, conditions, res.CsvPath, window);
        var doc = new GameSessionDoc(session.Id.ToString("N"), start, gameId, gameName, session, corr, frameTimes);
        try
        {
            await _db.UpsertAsync(Collections.GameSessions, doc.Id, doc, tag: gameId, createdAt: start);
            var docs = await _profiles.LoadAsync();
            if (!docs.Games.Any(p => string.Equals(p.Id, gameId, StringComparison.OrdinalIgnoreCase)))
                await _profiles.UpsertAsync(game is not null ? GameProfile.FromDetected(game)
                    : new GameProfile(gameId, gameName, null, null, GameSource.Unknown, null, null, [], "Criado a partir de uma captura por nome de processo.", [], DateTimeOffset.Now, DateTimeOffset.Now));
            await _profiles.AddSessionAsync(gameId, session.ToSummary());
        }
        catch (Exception ex) { App.Log(ex, "Salvar sessão de jogo"); App.Notify.Warning("Sessão não salva", ex.Message); }
        var item = new GameSessionItem(doc);
        SavedSessions.Insert(0, item);
        SelectedSession = item;
        Tab = "sessions";
        if (SelectedGame?.Game.Id == gameId) _ = LoadProfileSessionsAsync(gameId);
        if (session.Duration.TotalSeconds < 20)
            App.Notify.Info("Sessão curta", "Sessões com menos de 20 s não podem ser usadas na comparação antes × depois.");
        App.Notify.Success("Captura concluída", $"{session.Metrics.AverageFps:0.#} FPS médio · 1% low {session.Metrics.Low1PercentFps:0.#}");
    }

    /// <summary>Reduz a série preservando picos (máximo de cada bloco) — stutters continuam visíveis.</summary>
    private static double[] Downsample(double[] v, int max)
    {
        v = v.Where(double.IsFinite).ToArray();
        if (v.Length <= max) return v;
        var r = new double[max];
        double step = (double)v.Length / max;
        for (int i = 0; i < max; i++)
        {
            int a = (int)(i * step), b = Math.Min(v.Length, (int)((i + 1) * step));
            double m = v[a];
            for (int j = a + 1; j < b; j++) if (v[j] > m) m = v[j];
            r[i] = m;
        }
        return r;
    }

    // =====================================================================================
    // Sessões
    // =====================================================================================
    private async Task LoadSessionsAsync()
    {
        try
        {
            var docs = await Task.Run(() => _db.ListAsync<GameSessionDoc>(Collections.GameSessions, 300));
            var keep = SelectedSession?.Doc.Id;
            SavedSessions.Clear();
            foreach (var d in docs.Where(d => d.Session is not null)) SavedSessions.Add(new GameSessionItem(d));
            SelectedSession = SavedSessions.FirstOrDefault(s => s.Doc.Id == keep) ?? (HasSession ? null : SavedSessions.FirstOrDefault());
        }
        catch (Exception ex) { App.Log(ex, "Sessões de jogo"); }
    }

    partial void OnSelectedSessionChanged(GameSessionItem? value)
    {
        if (value is not null) ShowSession(value.Doc);
    }

    [RelayCommand]
    private async Task DeleteSession(GameSessionItem? item)
    {
        if (item is null) return;
        if (!await App.Dialogs.ConfirmAsync("Excluir sessão", $"Excluir a sessão \"{item.Display}\"?", confirmText: "Excluir")) return;
        try
        {
            await _db.DeleteAsync(Collections.GameSessions, item.Doc.Id);
            SavedSessions.Remove(item);
            if (BeforeSession == item) BeforeSession = null;
            if (AfterSession == item) AfterSession = null;
        }
        catch (Exception ex) { App.Notify.Error("Falha ao excluir", ex.Message); }
    }

    private static string F(double v, string fmt = "0.#") => double.IsFinite(v) ? v.ToString(fmt, System.Globalization.CultureInfo.CurrentCulture) : "—";

    private void ShowSession(GameSessionDoc d)
    {
        var s = d.Session; var m = s.Metrics;
        HasSession = true;
        SessionTitle = $"{d.GameName} · {d.At.LocalDateTime:g} · {s.Duration.TotalSeconds:0} s · {m.FrameCount:N0} quadros · {s.ProcessName}";
        SessionGame = $"{d.GameName} · medido em {d.At.LocalDateTime:g} por {s.Duration.TotalSeconds:0} s";
        SessionHeadline = double.IsFinite(m.AverageFps) ? $"{F(m.AverageFps, "0")} FPS em média" : "FPS médio indisponível";
        var stc = s.Stutters.Count;
        SessionPlain = (double.IsFinite(m.Low1PercentFps) ? $"Nos piores momentos (1% low) o jogo caiu para {F(m.Low1PercentFps, "0")} FPS. " : "")
                       + (stc == 0 ? "Nenhuma travada (stutter) foi detectada." : $"{stc} travada(s) (stutter) detectada(s) — {F(s.Stutters.PerMinute)} por minuto.")
                       + (s.Bound.Kind == BoundKind.Indeterminate ? "" : $" Limite provável: {s.Bound.LabelPt}.");
        SessionMetrics.Clear();
        SessionMetrics.Add(new("FPS médio", F(m.AverageFps), "quadros ÷ tempo total", "B.Cyan"));
        SessionMetrics.Add(new("1% low", F(m.Low1PercentFps), $"1000 ÷ P99 do frametime ({F(m.P99FrameTimeMs, "0.0")} ms) · média dos piores 1%: {F(m.Low1PercentAvgFps)}"));
        SessionMetrics.Add(new("0,1% low", F(m.Low01PercentFps), (m.Low01PercentReliable ? "" : "POUCO CONFIÁVEL (< 1000 quadros) · ") + $"1000 ÷ P99,9 ({F(m.P999FrameTimeMs, "0.0")} ms)",
            m.Low01PercentReliable ? "B.Text" : "B.Warning"));
        SessionMetrics.Add(new("Frametime mediano", $"{F(m.MedianFrameTimeMs, "0.00")} ms", $"FPS mediano {F(m.MedianFps)} · desvio {F(m.StdDevFrameTimeMs, "0.00")} ms · máx {F(m.MaxFrameTimeMs, "0.0")} ms"));
        if (s.DisplayedMetrics is { } dm)
            SessionMetrics.Add(new("FPS exibido", F(dm.AverageFps), "quadros que chegaram à tela (MsBetweenDisplayChange)"));

        FrameTimeCapacity = Math.Max(2, d.FrameTimes.Length);
        FrameTimeSeries = new[] { new ChartSeries { Name = "Frametime (ms)", Values = d.FrameTimes, Stroke = AnalysisUi.Brush("B.Cyan") } };
        FpsCapacity = Math.Max(2, s.PerSecondFps.Count);
        FpsSeries = new[] { new ChartSeries { Name = "FPS por segundo", Values = s.PerSecondFps.ToArray(), Stroke = AnalysisUi.Brush("B.Violet") } };

        var hist = s.Histogram.Where(b => b.Count > 0 || s.Histogram.Count <= 40).ToList();
        HistogramValues = hist.Select(b => (double)b.Count).ToArray();
        HistogramLabels = hist.Select(b => b.UpperMs is null ? $"≥{b.LowerMs:0}" : $"{b.LowerMs:0.#}").ToArray();

        var st = s.Stutters;
        StutterSummary = $"{st.Count} stutter(s) · {F(st.PerMinute)} por minuto · {st.Clusters.Count} agrupamento(s)";
        StutterDefinition = st.Definition;
        Stutters.Clear();
        foreach (var e in st.Events.OrderByDescending(e => e.FrameTimeMs).Take(40))
            Stutters.Add(new StutterRow($"{e.TimeMs / 1000:0.00} s", $"{F(e.FrameTimeMs, "0.0")} ms", $"{F(e.BaselineMs, "0.0")} ms"));

        PresentModeText = s.PresentModes.ExplanationPt;
        PresentModes.Clear();
        foreach (var pm in s.PresentModes.Modes)
            PresentModes.Add(new PresentModeRow(pm.Mode, $"{pm.Percent:0.#}%", pm.ImpactPt,
                pm.Latency switch { PresentLatencyClass.Lowest => "B.Success", PresentLatencyClass.Low => "B.Cyan", PresentLatencyClass.Higher => "B.Warning", _ => "B.TextMuted" }));
        TearingText = s.Tearing.InterpretationPt;

        var b = s.Bound;
        BoundLabel = b.LabelPt;
        BoundBrush = b.Kind switch { BoundKind.Indeterminate => "B.TextMuted", BoundKind.Mixed => "B.Warning", _ => "B.Cyan" };
        BoundDetail = $"Confiança {b.Confidence.Pt().ToLowerInvariant()} · {b.FramesEvaluated:N0} quadros avaliados"
                      + (b.GpuBoundPercent is { } gp ? $" · GPU {gp:0}%" : "") + (b.CpuBoundPercent is { } cp ? $" · CPU {cp:0}%" : "") + (b.LimitedPercent is { } lp ? $" · limitado {lp:0}%" : "");
        BoundExplanation = b.ExplanationPt + " (Heurística — não substitui um profiler.)";

        Associations.Clear(); ClusterNotes.Clear();
        if (d.Correlation is { } c)
        {
            HasCorrelation = true;
            CorrelationSummary = c.SummaryPt + $" ({c.TelemetryWindows} janelas de telemetria; {c.StutterWindows} com stutter.)";
            foreach (var a in c.Associations.OrderByDescending(a => a.Notable).ThenByDescending(a => a.Lift ?? 0))
                Associations.Add(new AssociationRow(a.LabelPt, a.TextPt, a.Lift is { } l ? $"{l:0.0}×" : "—", a.Notable ? "B.Warning" : "B.TextMuted", a.Notable));
            foreach (var cl in c.Clusters.Take(10)) ClusterNotes.Add(cl.TextPt);
        }
        else
        {
            HasCorrelation = false;
            CorrelationSummary = "Sem telemetria do RBM Optimizer no período da captura (o monitoramento estava pausado?). A associação temporal não pôde ser calculada.";
        }
        SessionWarnings.Clear();
        foreach (var w in s.Warnings) SessionWarnings.Add(w);
        var cond = s.Conditions;
        var condText = string.Join(" · ", new[]
        {
            cond.Resolution is { } r ? "resolução " + r : null, cond.SettingsLabel is { } sl ? "config. " + sl : null, cond.Scenario is { } sc ? "cena " + sc : null,
            cond.PowerPlanName is { } pp ? "plano " + pp : null, cond.TweakIds.Count > 0 ? $"{cond.TweakIds.Count} ajuste(s) ativo(s)" : null,
        }.Where(x => x is not null));
        if (condText.Length > 0) SessionWarnings.Insert(0, "Condições registradas: " + condText);
    }

    [RelayCommand]
    private void CompareSessions()
    {
        if (BeforeSession is null || AfterSession is null) { App.Notify.Info("Escolha duas sessões", "Selecione as sessões \"antes\" e \"depois\"."); return; }
        if (BeforeSession == AfterSession) { App.Notify.Info("Mesma sessão", "Escolha duas sessões diferentes."); return; }
        var a = BeforeSession.Doc; var b = AfterSession.Doc;
        if (a.At > b.At) (a, b) = (b, a);
        CompareDeltas.Clear(); CompareCaveats.Clear();
        var r = SessionComparer.Compare(a.Session.ToSummary(), b.Session.ToSummary(), new SessionComparisonOptions(UserFlaggedDifferentSettings: SettingsChanged));
        HasComparison = true;
        if (!r.Success || r.Value is null)
        {
            CompareHeadline = "Comparação recusada"; CompareBrush = "B.Danger"; CompareVerdict = r.Message;
            return;
        }
        var c = r.Value;
        var p = c.FpsComparison;
        bool better = p.DeltaPercent > 0;
        CompareHeadline = !p.Significant ? "Sem diferença comprovada no FPS" : better ? "FPS maior (estatisticamente significativo)" : "FPS menor (estatisticamente significativo)";
        CompareBrush = !p.Significant ? "B.TextMuted" : better ? "B.Success" : "B.Danger";
        CompareVerdict = c.VerdictPt;
        foreach (var dlt in c.Deltas)
        {
            bool good = dlt.HigherIsBetter ? dlt.DeltaPercent > 0 : dlt.DeltaPercent < 0;
            CompareDeltas.Add(new DeltaRow(dlt.NamePt, F(dlt.Before, "0.##"), F(dlt.After, "0.##"),
                double.IsFinite(dlt.DeltaPercent) ? $"{dlt.DeltaPercent:+0.0;-0.0;0.0}%" : "—",
                !double.IsFinite(dlt.DeltaPercent) || Math.Abs(dlt.DeltaPercent) < 2 ? "B.TextMuted" : good ? "B.Success" : "B.Danger"));
        }
        foreach (var cv in c.Caveats) CompareCaveats.Add(cv);
    }

    // =====================================================================================
    // Overlays e Game DVR
    // =====================================================================================
    [RelayCommand]
    private Task RefreshSystem() => LoadSystemAsync();

    private async Task LoadSystemAsync()
    {
        try
        {
            var (overlays, dvr) = await Task.Run(() => (OverlayDetector.DetectRunning(), OverlayDetector.ReadGameDvrState()));
            Overlays.Clear();
            foreach (var o in overlays.OrderBy(o => o.Category).ThenBy(o => o.Product))
                Overlays.Add(new OverlayRow(o.Product, CategoryPt(o.Category), $"{o.ProcessName} ({o.Pids.Count} processo(s))", o.ImpactPt, o.TestSuggestionPt, o.Confidence.Pt()));
            OverlayStatus = Overlays.Count == 0 ? "Nenhum overlay/gravador conhecido em execução agora." : $"{Overlays.Count} programa(s) conhecido(s) em execução agora.";
            DvrText = dvr.InterpretationPt;
            DvrValues.Clear();
            DvrValues.Add(new("Game DVR (captura em segundo plano)", Bool(dvr.GameDvrEnabled)));
            DvrValues.Add(new("Captura de aplicativos (AppCapture)", Bool(dvr.AppCaptureEnabled)));
            DvrValues.Add(new("Modo de Jogo automático", Bool(dvr.AutoGameModeEnabled)));
            DvrValues.Add(new("Política AllowGameDVR", dvr.PolicyAllowGameDvr is { } pa ? (pa == 0 ? "bloqueado por política" : "permitido") : "não definida"));
        }
        catch (Exception ex) { App.Log(ex, "Overlays"); App.Notify.Error("Falha ao ler overlays", ex.Message); }
    }

    private static string Bool(int? v) => v is null ? "não definido (padrão do Windows)" : v == 0 ? "desativado" : "ativado";

    private static string CategoryPt(OverlayCategory c) => c switch
    {
        OverlayCategory.Overlay => "Overlay", OverlayCategory.Recorder => "Gravação", OverlayCategory.Monitoring => "Monitoramento",
        OverlayCategory.Launcher => "Launcher", OverlayCategory.Peripheral => "Periférico", _ => "Papel de parede",
    };
}
