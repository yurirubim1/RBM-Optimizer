using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using Rbm.Benchmarks;
using Rbm.Benchmarks.Cpu;
using Rbm.Core;
using Rbm.Data;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.Analysis;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.ViewModels;

public sealed partial class BenchItem : ObservableObject
{
    public BenchItem(IBenchmark b) => B = b;
    public IBenchmark B { get; }
    [ObservableProperty] private bool _isSelected;
    public string Id => B.Id;
    public string Name => B.Name;
    public string Unit => B.Unit;
    public bool IsImplemented => B.IsImplemented;
    public bool WritesToStorage => B.WritesToStorage;
    public string Methodology => B.Methodology;
    public string Direction => !B.IsImplemented ? "não implementado" : B.Category == BenchmarkCategory.Storage ? "maior é melhor (MB/s, IOPS) · menor é melhor (latência)" : B.HigherIsBetter ? "maior é melhor" : "menor é melhor";
    public string Secondary => B.SecondaryMetrics.Count == 0 ? "" : "Métricas secundárias: " + string.Join(", ", B.SecondaryMetrics.Select(m => $"{m.Name} ({m.Unit})"));
}

public sealed record BenchGroup(string Title, string Description, IReadOnlyList<BenchItem> Items);

/// <summary>Conjunto sugerido de testes (atalho para quem não quer montar a seleção manualmente).</summary>
public sealed partial class BenchPreset : ObservableObject
{
    public BenchPreset(string title, string description, string icon, IReadOnlyList<string> ids, bool forceLight)
    { Title = title; Description = description; Icon = icon; Ids = ids; ForceLight = forceLight; }
    public string Title { get; }
    public string Description { get; }
    public string Icon { get; }
    public IReadOnlyList<string> Ids { get; }
    /// <summary>Usa o perfil leve com 3 iterações (teste rápido), ignorando as opções personalizadas.</summary>
    public bool ForceLight { get; }
    public string CountText => Ids.Count == 1 ? "1 teste" : $"{Ids.Count} testes";
    /// <summary>Duração da última vez (soma das últimas execuções salvas desses testes) — só quando há histórico.</summary>
    [ObservableProperty] private string? _estimateText;
}

/// <summary>Documento persistido em <see cref="Collections.Benchmarks"/>: o resultado vai como JSON do BenchmarkJson (seguro para NaN).</summary>
public sealed record BenchRecord(string Id, string RunId, DateTimeOffset At, string BenchmarkId, string Name, string Status, string Json);

public sealed class BenchHistoryItem
{
    public BenchHistoryItem(BenchRecord rec, BenchmarkResult result) { Record = rec; Result = result; }
    public BenchRecord Record { get; }
    public BenchmarkResult Result { get; }
    public string BenchmarkId => Result.BenchmarkId;
    public string Display => $"{Record.At.LocalDateTime:g} · {Result.Name} · {BenchFmt.Value(Result.Summary.Mean)} {Result.Unit}" + (Result.UnstableConditions ? " · instável" : "");
    public string Short => $"{Record.At.LocalDateTime:g} · {BenchFmt.Value(Result.Summary.Mean)} {Result.Unit} (n={Result.Samples.Count})" + (Result.UnstableConditions ? " · instável" : "");
    public override string ToString() => Display;
}

public sealed record BenchMetricRow(string Name, string Mean, string Detail);
public sealed record BenchResultCard(
    string Name, string Id, string Status, string StatusBrush, string Mean, string Unit, string Direction,
    string Median, string Cv, string Ci, string Samples, string Duration, string Conditions,
    bool Unstable, IReadOnlyList<string> Warnings, IReadOnlyList<BenchMetricRow> Metrics, string? Message, bool IsCompleted);
public sealed record ScalingRow(string Workload, string St, string Mt, string Ratio, string Efficiency, string Threads);
public sealed record CompareRow(string Name, string Before, string After, string Delta, string PValue, string Verdict, string Brush);
public sealed record VolumeChoice(string Root, string Label) { public override string ToString() => Label; }

internal static class BenchFmt
{
    public static string Value(double v) => !double.IsFinite(v) ? "—"
        : Math.Abs(v) >= 1000 ? v.ToString("N0", CultureInfo.CurrentCulture)
        : Math.Abs(v) >= 100 ? v.ToString("0.0", CultureInfo.CurrentCulture)
        : Math.Abs(v) >= 1 ? v.ToString("0.00", CultureInfo.CurrentCulture)
        : v.ToString("0.####", CultureInfo.CurrentCulture);
}

public sealed partial class BenchmarksViewModel : PageViewModel
{
    private readonly RbmDatabase _db;
    public override string Key => "benchmarks";

    public ObservableCollection<BenchGroup> Groups { get; } = new();
    private readonly List<BenchItem> _items = new();

    // Opções
    public int[] IterationChoices { get; } = { 3, 5, 10 };
    [ObservableProperty] private int _iterations = 3;
    [ObservableProperty] private bool _lightProfile = true;
    public ObservableCollection<VolumeChoice> Volumes { get; } = new();
    [ObservableProperty] private VolumeChoice? _selectedVolume;
    [ObservableProperty] private bool _includeWriteTests;
    [ObservableProperty] private string _selectionSummary = "Nenhum teste marcado. Marque os testes no catálogo abaixo ou use uma das sugestões acima.";

    // Execução
    [ObservableProperty] private bool _isRunning;
    [ObservableProperty] private double _overallProgress;
    [ObservableProperty] private double _currentProgress;
    [ObservableProperty] private string _runStatus = "";
    private CancellationTokenSource? _cts;

    // Resultados
    public ObservableCollection<BenchResultCard> Results { get; } = new();
    [ObservableProperty] private string _resultsTitle = "Resultados";
    public ObservableCollection<ScalingRow> Scaling { get; } = new();
    [ObservableProperty] private string _scalingNote = "";

    // Histórico e comparação
    public ObservableCollection<BenchHistoryItem> History { get; } = new();
    public ObservableCollection<BenchHistoryItem> AfterChoices { get; } = new();
    [ObservableProperty] private BenchHistoryItem? _selectedHistory;
    [ObservableProperty] private BenchHistoryItem? _beforeItem;
    [ObservableProperty] private BenchHistoryItem? _afterItem;
    [ObservableProperty] private bool _hasComparison;
    [ObservableProperty] private string _compareVerdict = "";
    [ObservableProperty] private string _compareBrush = "B.TextMuted";
    [ObservableProperty] private string _compareHeadline = "";
    public ObservableCollection<CompareRow> CompareRows { get; } = new();
    public ObservableCollection<string> CompareWarnings { get; } = new();

    public string StorageWearWarning =>
        "Os testes de gravação escrevem vários GiB no volume escolhido (preparação + medições). Em SSDs isso consome ciclos de escrita (desgaste). " +
        "Deixe desligado, a não ser que precise medir gravação. A leitura já exige gravar o arquivo de teste uma vez.";

    public BenchmarksViewModel(RbmDatabase db)
    {
        _db = db;
        var all = BenchmarkCatalog.CreateAll();
        foreach (var b in all)
        {
            var it = new BenchItem(b);
            it.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(BenchItem.IsSelected)) UpdateSelectionSummary(); };
            _items.Add(it);
        }
        foreach (var g in _items.GroupBy(i => i.B.Category))
            Groups.Add(new BenchGroup(CategoryTitle(g.Key), CategoryDescription(g.Key), g.ToList()));

        string[] Ids(Func<BenchItem, bool> f) => _items.Where(i => i.IsImplemented && f(i)).Select(i => i.Id).ToArray();
        QuickPreset = new BenchPreset("Teste rápido", "Processador (1 e todos os núcleos) e memória (velocidade e latência). Perfil leve.", "",
            Ids(i => i.Id is "cpu.integer.st" or "cpu.integer.mt" or "mem.bandwidth.read" or "mem.latency.dram"), forceLight: true);
        Presets.Add(QuickPreset);
        Presets.Add(new BenchPreset("Processador", "Todas as cargas de CPU, em 1 núcleo e em todos.", "", Ids(i => i.B.Category == BenchmarkCategory.Cpu), false));
        Presets.Add(new BenchPreset("Memória RAM", "Velocidade de leitura/escrita/cópia e tempo de resposta.", "", Ids(i => i.B.Category == BenchmarkCategory.Memory), false));
        Presets.Add(new BenchPreset("Disco", "Leitura sequencial e aleatória no volume escolhido em Personalizar.", "", Ids(i => i.B.Category == BenchmarkCategory.Storage), false));
        Presets.Add(new BenchPreset("Travadinhas", "Precisão do relógio do Windows e atrasos ao acordar tarefas.", "", Ids(i => i.B.Category == BenchmarkCategory.Responsiveness), false));
        History.CollectionChanged += (_, _) => UpdateHeroAndEstimates();
        UpdateHeroAndEstimates();
    }

    // ---------------- Sugestões e resumo ----------------
    public ObservableCollection<BenchPreset> Presets { get; } = new();
    public BenchPreset QuickPreset { get; }
    [ObservableProperty] private string _heroText = "";
    [ObservableProperty] private string _heroChipText = "";
    /// <summary>ok | info</summary>
    [ObservableProperty] private string _heroLevel = "info";
    [ObservableProperty] private bool _showCustomize;

    private void UpdateHeroAndEstimates()
    {
        int n = History.Count;
        HeroLevel = n == 0 ? "info" : "ok";
        HeroChipText = n == 0 ? "Nenhum teste feito ainda" : n == 1 ? "1 resultado salvo" : $"{n} resultados salvos";
        HeroText = n == 0
            ? "Rode um teste agora, aplique uma otimização e rode de novo: o RBM diz se a diferença é real ou só variação normal. O PC fica ocupado durante o teste — feche jogos e programas pesados antes."
            : $"Último resultado: {History[0].Display}. Para saber se uma mudança ajudou, rode o mesmo teste de novo e use “Comparar antes × depois”.";
        foreach (var p in Presets)
        {
            var last = p.Ids.Select(id => History.FirstOrDefault(h => h.BenchmarkId == id)).ToList();
            p.EstimateText = last.All(h => h is not null)
                ? $"Da última vez: {FormatDuration(TimeSpan.FromSeconds(last.Sum(h => h!.Result.Duration.TotalSeconds)))}"
                : null;
        }
    }

    private static string FormatDuration(TimeSpan t) => t.TotalSeconds < 60 ? $"~{Math.Max(1, Math.Round(t.TotalSeconds)):0} s" : $"~{Math.Round(t.TotalMinutes, 1):0.#} min";

    /// <summary>Seleciona o conjunto sugerido e executa (a confirmação de sempre é exibida antes).</summary>
    [RelayCommand]
    private async Task RunPreset(BenchPreset? p)
    {
        if (p is null || IsRunning) return;
        foreach (var i in _items) i.IsSelected = p.Ids.Contains(i.Id);
        if (p.ForceLight) { LightProfile = true; Iterations = 3; }
        await Run();
    }

    private static string CategoryTitle(BenchmarkCategory c) => c switch
    {
        BenchmarkCategory.Cpu => "Processador", BenchmarkCategory.Memory => "Memória", BenchmarkCategory.Storage => "Armazenamento",
        BenchmarkCategory.Responsiveness => "Responsividade do agendador", _ => "GPU",
    };
    private static string CategoryDescription(BenchmarkCategory c) => c switch
    {
        BenchmarkCategory.Cpu => "ST = 1 thread; MT = todas as threads lógicas. Cada carga valida um checksum (o trabalho não pode ser eliminado).",
        BenchmarkCategory.Memory => "Largura de banda (GB/s) e latência por acesso (ns) com buffers muito maiores que o cache L3.",
        BenchmarkCategory.Storage => "Arquivo temporário sem cache do Windows; leitura sequencial e aleatória 4 KiB (QD1/QD32). Gravação só com opt-in.",
        BenchmarkCategory.Responsiveness => "Precisão de Sleep, latência de despertar e lacunas de execução (indicadores indiretos, não DPC).",
        _ => "Sem benchmark sintético de GPU — use a captura de FPS/frametime em jogos reais.",
    };

    public override async Task OnNavigatedToAsync()
    {
        LoadVolumes();
        await LoadHistoryAsync();
    }

    private void LoadVolumes()
    {
        var keep = SelectedVolume?.Root;
        Volumes.Clear();
        try
        {
            string sys = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
            foreach (var d in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
            {
                bool isSys = string.Equals(d.RootDirectory.FullName, sys, StringComparison.OrdinalIgnoreCase);
                Volumes.Add(new VolumeChoice(d.RootDirectory.FullName,
                    $"{d.RootDirectory.FullName.TrimEnd('\\')}{(isSys ? " (sistema)" : "")} · {d.VolumeLabel} · {Fmt.Bytes(d.AvailableFreeSpace)} livres de {Fmt.Bytes(d.TotalSize)}"));
            }
            SelectedVolume = Volumes.FirstOrDefault(v => v.Root == keep)
                ?? Volumes.FirstOrDefault(v => string.Equals(v.Root, sys, StringComparison.OrdinalIgnoreCase)) ?? Volumes.FirstOrDefault();
        }
        catch (Exception ex) { App.Log(ex, "Listar volumes"); }
    }

    private void UpdateSelectionSummary()
    {
        var sel = _items.Where(i => i.IsSelected && i.IsImplemented).ToList();
        SelectionSummary = sel.Count == 0 ? "Nenhum teste marcado. Marque os testes no catálogo abaixo ou use uma das sugestões acima."
            : sel.Count == 1 ? $"1 teste marcado: {sel[0].Name}" : $"{sel.Count} testes marcados: {string.Join(", ", sel.Take(3).Select(s => s.Name))}{(sel.Count > 3 ? "…" : "")}";
    }

    [RelayCommand]
    private void SelectGroup(BenchGroup? g)
    {
        if (g is null || IsRunning) return;
        var impl = g.Items.Where(i => i.IsImplemented).ToList();
        bool all = impl.All(i => i.IsSelected);
        foreach (var i in impl) i.IsSelected = !all;
    }

    [RelayCommand]
    private void ClearSelection() { foreach (var i in _items) i.IsSelected = false; }

    [RelayCommand]
    private void GoGaming() => App.Services.GetRequiredService<MainViewModel>().Navigate("gaming");

    private BenchmarkOptions BuildOptions()
    {
        var o = new BenchmarkOptions
        {
            WarmupIterations = 1,
            MeasuredIterations = Math.Clamp(Iterations, 1, 50),
            StorageVolume = SelectedVolume?.Root,
            StorageIncludeWriteTests = IncludeWriteTests,
        };
        if (LightProfile)
            o = o with
            {
                WorkScale = 0.5,
                ConditionsSampleWindow = TimeSpan.FromSeconds(2),
                StorageFileBytes = 256 * BenchmarkOptions.MiB,
                StorageFreeSpaceMarginBytes = 1 * BenchmarkOptions.GiB,
                StorageSequentialDuration = TimeSpan.FromSeconds(2),
                StorageRandomDuration = TimeSpan.FromSeconds(1),
            };
        return o;
    }

    [RelayCommand]
    private async Task Run()
    {
        if (IsRunning) return;
        var selected = _items.Where(i => i.IsSelected && i.IsImplemented).Select(i => i.B).ToList();
        if (selected.Count == 0) { App.Notify.Info("Nada selecionado", "Marque ao menos um teste no catálogo."); return; }
        var options = BuildOptions();
        if (options.Validate() is { } err) { App.Notify.Error("Opções inválidas", err); return; }

        bool storage = selected.Any(b => b.Category == BenchmarkCategory.Storage);
        string details = "Feche jogos, navegadores e downloads: carga de fundo acima de 10% marca o resultado como \"condições instáveis\". " +
                         "Mantenha o PC na tomada e no mesmo plano de energia entre execuções que você pretende comparar.";
        if (storage)
            details += IncludeWriteTests
                ? $"\n\nARMAZENAMENTO COM GRAVAÇÃO em {SelectedVolume?.Root ?? "unidade do sistema"}: {StorageWearWarning}"
                : $"\n\nArmazenamento em {SelectedVolume?.Root ?? "unidade do sistema"}: o arquivo temporário é gravado uma vez (preparação) e removido ao final.";
        bool ok = await App.Dialogs.ConfirmAsync("Executar benchmarks",
            $"Executar {selected.Count} teste(s) com {options.MeasuredIterations} iterações medidas ({(LightProfile ? "perfil leve" : "perfil padrão")})? O PC ficará sob carga durante a execução.",
            details, "Executar", danger: storage && IncludeWriteTests);
        if (!ok) return;

        _cts = new CancellationTokenSource();
        IsRunning = true; OverallProgress = 0; CurrentProgress = 0; RunStatus = "Preparando…";
        var ids = selected.Select(b => b.Id).ToList();
        var names = selected.ToDictionary(b => b.Id, b => b.Name);
        var progress = new Progress<BenchmarkProgress>(p =>
        {
            int idx = Math.Max(0, ids.IndexOf(p.BenchmarkId));
            CurrentProgress = Math.Clamp(p.Fraction, 0, 1);
            OverallProgress = Math.Clamp((idx + CurrentProgress) / ids.Count, 0, 1);
            RunStatus = $"{idx + 1}/{ids.Count} · {names.GetValueOrDefault(p.BenchmarkId, p.BenchmarkId)} — {p.Message}";
        });
        using var task = App.Tasks.Begin("Benchmarks em execução");
        try
        {
            var ct = _cts.Token;
            var results = await Task.Run(() => BenchmarkSuite.RunAsync(selected, options, progress, ct));
            var runId = Guid.NewGuid().ToString("N");
            var at = DateTimeOffset.Now;
            Results.Clear();
            foreach (var r in results) Results.Add(BuildCard(r));
            ResultsTitle = $"Resultados da execução de {at.LocalDateTime:g}";
            int saved = 0;
            foreach (var r in results.Where(r => r.Status == BenchmarkStatus.Completed))
            {
                var rec = new BenchRecord(Guid.NewGuid().ToString("N"), runId, r.StartedAt == default ? at : r.StartedAt, r.BenchmarkId, r.Name, r.Status.ToString(), BenchmarkJson.Serialize(r));
                try
                {
                    await _db.UpsertAsync(Collections.Benchmarks, rec.Id, rec, tag: r.BenchmarkId, createdAt: rec.At);
                    History.Insert(0, new BenchHistoryItem(rec, r));
                    saved++;
                }
                catch (Exception ex) { App.Log(ex, "Salvar benchmark"); }
            }
            ComputeScaling();
            RefreshAfterChoices();
            int cancelled = results.Count(r => r.Status == BenchmarkStatus.Cancelled), failed = results.Count(r => r.Status == BenchmarkStatus.Failed);
            if (cancelled > 0 || ct.IsCancellationRequested) App.Notify.Info("Execução interrompida", $"{saved} resultado(s) concluído(s) salvo(s). Nenhum número parcial é exibido para o teste interrompido.");
            else if (failed > 0) App.Notify.Warning("Execução concluída com falhas", $"{failed} teste(s) falharam — veja o motivo no cartão. {saved} salvo(s).");
            else App.Notify.Success("Benchmarks concluídos", $"{saved} resultado(s) salvo(s) no histórico.");
        }
        catch (Exception ex)
        {
            App.Log(ex, "Benchmarks");
            App.Notify.Error("Falha nos benchmarks", ex.Message);
        }
        finally
        {
            IsRunning = false; RunStatus = ""; OverallProgress = 0; CurrentProgress = 0;
            _cts?.Dispose(); _cts = null;
        }
    }

    [RelayCommand]
    private void Cancel() { _cts?.Cancel(); RunStatus = "Cancelando… (o teste em andamento é interrompido no próximo ponto seguro)"; }

    private static BenchResultCard BuildCard(BenchmarkResult r)
    {
        var s = r.Summary;
        string status = r.Status switch
        {
            BenchmarkStatus.Completed => r.UnstableConditions ? "Concluído · condições instáveis" : "Concluído",
            BenchmarkStatus.Cancelled => "Cancelado", BenchmarkStatus.Failed => "Falhou", _ => "Não implementado",
        };
        string brush = r.Status switch
        {
            BenchmarkStatus.Completed => r.UnstableConditions ? "B.Warning" : "B.Success",
            BenchmarkStatus.Failed => "B.Danger", _ => "B.TextMuted",
        };
        bool done = r.Status == BenchmarkStatus.Completed;
        string cond = "";
        if (r.Conditions is { } c)
        {
            var parts = new List<string>();
            if (c.PowerPlanName is not null) parts.Add($"plano \"{c.PowerPlanName}\"");
            if (c.OnAcPower is { } ac) parts.Add(ac ? "na tomada" : "na bateria");
            if (c.BackgroundCpuPercent is { } bg) parts.Add($"carga de fundo {bg:0.#}%");
            if (c.HighCpuProcesses.Count > 0) parts.Add("processos ativos: " + string.Join(", ", c.HighCpuProcesses.Take(4)));
            if (c.TimerResolutionCurrentMs is { } tr) parts.Add($"timer {tr:0.###} ms");
            parts.Add($"Windows {c.WindowsVersion}");
            parts.Add($".NET {c.RuntimeVersion}");
            parts.Add($"{c.LogicalProcessors} threads lógicas");
            cond = string.Join(" · ", parts);
        }
        var metrics = r.Metrics.Select(m => new BenchMetricRow(m.Name, $"{BenchFmt.Value(m.Summary.Mean)} {m.Unit}",
            $"mediana {BenchFmt.Value(m.Summary.Median)} · CV {Pct(m.Summary.CoefficientOfVariation)} · {(m.HigherIsBetter ? "maior é melhor" : "menor é melhor")}")).ToList();
        return new BenchResultCard(r.Name, r.BenchmarkId, status, brush,
            done ? BenchFmt.Value(s.Mean) : "—", r.Unit, r.HigherIsBetter ? "maior é melhor" : "menor é melhor",
            done ? BenchFmt.Value(s.Median) : "—", done ? Pct(s.CoefficientOfVariation) : "—",
            done && double.IsFinite(s.Ci95Low) ? $"{BenchFmt.Value(s.Ci95Low)} – {BenchFmt.Value(s.Ci95High)}" : "—",
            done ? string.Join("; ", r.Samples.Select(BenchFmt.Value)) : "—",
            $"{r.Duration.TotalSeconds:0.#} s", cond, r.UnstableConditions, r.Warnings.ToList(), metrics, r.Message, done);
    }

    private static string Pct(double frac) => double.IsFinite(frac) ? $"{frac * 100:0.0}%" : "—";

    private void ComputeScaling()
    {
        Scaling.Clear();
        // Usa pares ST/MT da MESMA execução (mesmas condições), a mais recente de cada carga.
        var byRun = History.GroupBy(h => h.Record.RunId).OrderByDescending(g => g.Max(h => h.Record.At));
        var done = new HashSet<string>();
        foreach (var run in byRun)
            foreach (var st in run.Where(h => h.BenchmarkId.StartsWith("cpu.", StringComparison.Ordinal) && h.BenchmarkId.EndsWith(".st", StringComparison.Ordinal)))
            {
                var key = st.BenchmarkId[..^3];
                if (done.Contains(key)) continue;
                var mt = run.FirstOrDefault(h => h.BenchmarkId == key + ".mt");
                if (mt is null || CpuSuite.ComputeScaling(st.Result, mt.Result) is not { } sc) continue;
                done.Add(key);
                Scaling.Add(new ScalingRow(sc.Name, $"{BenchFmt.Value(sc.SingleThreadMean)} {sc.Unit}", $"{BenchFmt.Value(sc.MultiThreadMean)} {sc.Unit}",
                    $"{sc.Ratio:0.00}×", double.IsFinite(sc.EfficiencyPercent) ? $"{sc.EfficiencyPercent:0}%" : "—", sc.Threads > 0 ? sc.Threads.ToString() : "—"));
            }
        ScalingNote = Scaling.Count == 0
            ? "Execute uma carga de CPU em ST e MT na mesma execução para ver o escalonamento."
            : "Razão = média MT / média ST. Eficiência = razão ÷ threads. Com SMT (2 threads por núcleo), 50–75% é normal: as duas threads dividem o mesmo núcleo.";
    }

    // ---------------- Histórico ----------------
    private async Task LoadHistoryAsync()
    {
        try
        {
            var items = await Task.Run(async () =>
            {
                var recs = await _db.ListAsync<BenchRecord>(Collections.Benchmarks, 500);
                var list = new List<BenchHistoryItem>();
                foreach (var rec in recs)
                {
                    if (string.IsNullOrEmpty(rec.Json)) continue;
                    try { if (BenchmarkJson.Deserialize(rec.Json) is { } r) list.Add(new BenchHistoryItem(rec, r)); }
                    catch (Exception) { /* versão incompatível: ignora */ }
                }
                return list;
            });
            History.Clear();
            foreach (var h in items.OrderByDescending(h => h.Record.At)) History.Add(h);
            ComputeScaling();
            RefreshAfterChoices();
        }
        catch (Exception ex) { App.Log(ex, "Histórico de benchmarks"); App.Notify.Warning("Histórico indisponível", ex.Message); }
    }

    partial void OnSelectedHistoryChanged(BenchHistoryItem? value)
    {
        if (value is null) return;
        Results.Clear();
        Results.Add(BuildCard(value.Result));
        ResultsTitle = $"Execução salva de {value.Record.At.LocalDateTime:g}";
    }

    [RelayCommand]
    private async Task DeleteHistory(BenchHistoryItem? item)
    {
        if (item is null) return;
        if (!await App.Dialogs.ConfirmAsync("Excluir resultado", $"Excluir \"{item.Display}\" do histórico?", confirmText: "Excluir")) return;
        try
        {
            await _db.DeleteAsync(Collections.Benchmarks, item.Record.Id);
            History.Remove(item);
            if (BeforeItem == item) BeforeItem = null;
            if (AfterItem == item) AfterItem = null;
            ComputeScaling();
            RefreshAfterChoices();
        }
        catch (Exception ex) { App.Notify.Error("Falha ao excluir", ex.Message); }
    }

    partial void OnBeforeItemChanged(BenchHistoryItem? value) => RefreshAfterChoices();

    private void RefreshAfterChoices()
    {
        var keep = AfterItem;
        AfterChoices.Clear();
        if (BeforeItem is { } b)
            foreach (var h in History.Where(h => h.BenchmarkId == b.BenchmarkId && h != b)) AfterChoices.Add(h);
        AfterItem = keep is not null && AfterChoices.Contains(keep) ? keep : AfterChoices.FirstOrDefault();
    }

    [RelayCommand]
    private void Compare()
    {
        if (BeforeItem is null || AfterItem is null) { App.Notify.Info("Escolha duas execuções", "Selecione \"antes\" e \"depois\" do mesmo teste."); return; }
        var before = BeforeItem.Result; var after = AfterItem.Result;
        if (BeforeItem.Record.At > AfterItem.Record.At) (before, after) = (after, before);
        var cmp = BenchmarkComparer.Compare(before, after);
        CompareRows.Clear(); CompareWarnings.Clear();
        foreach (var w in cmp.Warnings) CompareWarnings.Add(w);
        CompareVerdict = cmp.Verdict;
        if (!cmp.Comparable || cmp.Primary is null)
        {
            CompareBrush = "B.Danger";
            CompareHeadline = "Não comparável";
        }
        else
        {
            var p = cmp.Primary;
            bool better = after.HigherIsBetter ? p.DeltaPercent > 0 : p.DeltaPercent < 0;
            if (!p.Significant) { CompareBrush = "B.TextMuted"; CompareHeadline = "Sem diferença comprovada"; }
            else if (!cmp.ConfidentChange) { CompareBrush = "B.Warning"; CompareHeadline = better ? "Melhora provável, mas condições diferentes" : "Piora provável, mas condições diferentes"; }
            else { CompareBrush = better ? "B.Success" : "B.Danger"; CompareHeadline = better ? "Melhora estatisticamente significativa" : "Piora estatisticamente significativa"; }
            CompareRows.Add(Row(after.Name + " (principal)", after.Unit, after.HigherIsBetter, p));
            foreach (var m in cmp.Metrics) CompareRows.Add(Row(m.Name, m.Unit, m.HigherIsBetter, m.Comparison));
        }
        HasComparison = true;
    }

    private static CompareRow Row(string name, string unit, bool higherIsBetter, ComparisonResult c)
    {
        bool better = higherIsBetter ? c.DeltaPercent > 0 : c.DeltaPercent < 0;
        string brush = !c.Significant ? "B.TextMuted" : better ? "B.Success" : "B.Danger";
        return new CompareRow(name, $"{BenchFmt.Value(c.Before.Mean)} {unit}", $"{BenchFmt.Value(c.After.Mean)} {unit}",
            double.IsFinite(c.DeltaPercent) ? $"{c.DeltaPercent:+0.0;-0.0;0.0}%" : "—",
            double.IsFinite(c.PValue) ? (c.PValue < 0.001 ? "< 0,001" : c.PValue.ToString("0.000", CultureInfo.CurrentCulture)) : "—",
            c.Verdict, brush);
    }
}
