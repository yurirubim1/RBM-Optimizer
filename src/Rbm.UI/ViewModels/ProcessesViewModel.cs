using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Hardware;
using Rbm.Processes;
using Rbm.Processes.Agent;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.SystemPages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

/// <summary>
/// Gerenciador de processos: tabela ao vivo (2 s enquanto visível), árvore, detalhes, ações com fallback para o
/// RBM Agent, regras por executável (watcher persistente) e análise de impacto em segundo plano.
/// </summary>
public sealed partial class ProcessesViewModel : PageViewModel
{
    private const int WindowCapacity = 150; // 5 min a 2 s
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(2);

    private readonly ISystemInventory _inventory;
    private readonly ISettingsStore _settings;
    private readonly ProcessActions _actions = new();
    private readonly ProcessDetailsProvider _details = new();
    private readonly ProcessRuleStore _ruleStore = new();
    private readonly Dictionary<ProcessKey, ProcessRow> _rowMap = new();
    private readonly Queue<ProcessSnapshot> _window = new();
    private ProcessSampler? _sampler;
    private GpuProcessUsageReader? _gpuReader;
    private bool _gpuInitTried;
    private CancellationTokenSource? _loopCts;
    private CancellationTokenSource? _detailsCts;
    private ProcessSnapshot? _lastSnapshot;
    private ProcessRuleWatcher? _watcher;
    private volatile IReadOnlyList<ProcessRule> _activeRules = Array.Empty<ProcessRule>();
    private ProcessRuleSet _ruleSet = new();
    private bool _rulesLoadFailed;
    private bool _initialized;
    private int _tick;
    private ulong _systemMask;

    public override string Key => "processes";

    public ProcessesViewModel(ISystemInventory inventory, ISettingsStore settings)
    {
        _inventory = inventory; _settings = settings;
        View = new ListCollectionView(Rows) { IsLiveSorting = true, Filter = FilterRow };
        View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Cpu), ListSortDirection.Descending));
        int n = Math.Min(Environment.ProcessorCount, 64);
        _systemMask = n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
        PriorityOptions = new[] { ProcessPriority.Idle, ProcessPriority.BelowNormal, ProcessPriority.Normal, ProcessPriority.AboveNormal, ProcessPriority.High }
            .Select(p => new PriorityOption(p, ProcessPriorityMap.Display(p))).ToList();
        RulePriorityOptions = new[] { new PriorityOption(null, "Não alterar") }.Concat(PriorityOptions).ToList();
        AffinityPresetOptions = new[] { AffinityPreset.PerformanceCoresOnly, AffinityPreset.ExcludeEfficiencyCores, AffinityPreset.OneThreadPerCore, AffinityPreset.FirstCacheDomain }
            .Select(p => new AffinityPresetOption(p, ProcessText.Preset(p))).ToList();
        RulePresetOptions = Enum.GetValues<AffinityPreset>().Select(p => new AffinityPresetOption(p, ProcessText.Preset(p))).ToList();
        EditPriority = RulePriorityOptions[0];
        EditPreset = RulePresetOptions[0];
        _loadingSettings = true;
        WatcherEnabled = settings.Get("processes.rules.enabled", false);
        _loadingSettings = false;
    }

    private readonly bool _loadingSettings;

    // =====================================================================
    // Abas
    // =====================================================================
    /// <summary>Visão simples padrão: os programas mais pesados agora.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsListTab))] private bool _isTabTop = true;
    /// <summary>Tabela completa (ordenável, árvore, busca).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(IsListTab))] private bool _isTabProcesses;
    [ObservableProperty] private bool _isTabRules;
    [ObservableProperty] private bool _isTabImpact;

    /// <summary>Abas que mostram o painel de detalhes do processo selecionado.</summary>
    public bool IsListTab => IsTabTop || IsTabProcesses;

    partial void OnIsTabImpactChanged(bool value) { if (value) _ = AnalyzeImpactAsync(); }

    [RelayCommand] private void ShowTop() => IsTabTop = true;
    [RelayCommand] private void ShowAll() => IsTabProcesses = true;
    [RelayCommand] private void ShowImpact() => IsTabImpact = true;
    [RelayCommand] private void ShowRules() => IsTabRules = true;

    /// <summary>Seleciona o processo mais pesado de um grupo da lista "Mais pesados".</summary>
    [RelayCommand]
    private void SelectGroup(ProcessGroupRow? g)
    {
        if (g?.Primary is { } p && _rowMap.ContainsKey(p.Key)) SelectedRow = p;
    }

    // =====================================================================
    // Resumo (hero) e "mais pesados"
    // =====================================================================
    private const int TopSlots = 8;
    public ObservableCollection<ProcessGroupRow> TopCpu { get; } = new(Enumerable.Range(0, TopSlots).Select(_ => new ProcessGroupRow()));
    public ObservableCollection<ProcessGroupRow> TopMemory { get; } = new(Enumerable.Range(0, TopSlots).Select(_ => new ProcessGroupRow()));
    [ObservableProperty] private string _heroTitle = "Lendo os programas em execução…";
    [ObservableProperty] private string _heroText = "A lista é atualizada a cada 2 segundos enquanto esta página está aberta.";
    [ObservableProperty] private string _heroChipText = "Coletando";
    /// <summary>ok | warn | danger | info — escolhe a cor do chip.</summary>
    [ObservableProperty] private string _heroLevel = "info";
    [ObservableProperty] private string _detailUsage = "";
    [ObservableProperty] private bool _showTechColumns;
    [ObservableProperty] private bool _hasTopCpu;
    [ObservableProperty] private bool _cpuMeasuring = true;

    private readonly Dictionary<string, string?> _descCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _descPending = new(StringComparer.OrdinalIgnoreCase);

    private static string CpuPct(double v) => v < 0.05 ? "0%" : v.ToString(v < 10 ? "0.0" : "0", CultureInfo.GetCultureInfo("pt-BR")) + "%";

    private void UpdateSummary(ProcessSnapshot snap)
    {
        var groups = Rows.GroupBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .Select(g =>
            {
                var list = g.ToList();
                return (Name: g.Key, Rows: list, Cpu: list.Where(r => r.Cpu > 0).Sum(r => r.Cpu), Mem: list.Sum(r => r.WorkingSet));
            }).ToList();

        string Title((string Name, List<ProcessRow> Rows, double Cpu, long Mem) g)
        {
            var best = g.Rows.FirstOrDefault(r => !string.IsNullOrWhiteSpace(r.Description));
            return best?.DisplayName ?? g.Rows[0].DisplayName;
        }

        void Fill(ObservableCollection<ProcessGroupRow> slots, IEnumerable<(string Name, List<ProcessRow> Rows, double Cpu, long Mem)> ordered, bool byCpu)
        {
            var top = ordered.Take(TopSlots).ToList();
            double max = top.Count == 0 ? 1 : Math.Max(byCpu ? top.Max(t => t.Cpu) : top.Max(t => (double)t.Mem), 1e-9);
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                if (i >= top.Count) { s.IsUsed = false; s.Primary = null; continue; }
                var g = top[i];
                var primary = byCpu ? g.Rows.OrderByDescending(r => r.Cpu).First() : g.Rows.OrderByDescending(r => r.WorkingSet).First();
                s.Primary = primary;
                s.Title = Title(g);
                bool sameName = string.Equals(s.Title, g.Name, StringComparison.OrdinalIgnoreCase);
                s.Subtitle = g.Rows.Count > 1 ? (sameName ? $"{g.Rows.Count} processos" : $"{g.Name} · {g.Rows.Count} processos") : sameName ? "" : g.Name;
                s.ValueText = byCpu ? CpuPct(g.Cpu) : Fmt.Bytes(g.Mem);
                s.Share = byCpu ? Math.Clamp(g.Cpu / 100.0, 0, 1) : Math.Clamp(g.Mem / max, 0, 1);
                s.IsProtected = g.Rows.Any(r => r.IsProtected);
                s.ToolTip = primary.ImagePath ?? g.Name;
                s.IsUsed = true;
                QueueDescription(primary);
            }
        }

        if (snap.RatesAvailable)
            Fill(TopCpu, groups.Where(g => g.Cpu >= 0.05).OrderByDescending(g => g.Cpu), byCpu: true);
        else foreach (var s in TopCpu) s.IsUsed = false;
        Fill(TopMemory, groups.OrderByDescending(g => g.Mem), byCpu: false);
        HasTopCpu = TopCpu.Any(s => s.IsUsed);
        CpuMeasuring = !snap.RatesAvailable;

        double cpuSum = Rows.Where(r => r.Cpu > 0).Sum(r => r.Cpu);
        HeroTitle = $"{Rows.Count} processos em execução, de {groups.Count} programas diferentes";
        var topCpu = groups.Where(g => g.Cpu >= 0.05).OrderByDescending(g => g.Cpu).FirstOrDefault();
        var topMem = groups.OrderByDescending(g => g.Mem).FirstOrDefault();
        var parts = new List<string>();
        if (!snap.RatesAvailable) parts.Add("Medindo o uso de CPU (leva 2 segundos)…");
        else if (topCpu.Rows is not null) parts.Add($"O que mais usa CPU agora: {Title(topCpu)} ({CpuPct(topCpu.Cpu)}).");
        else parts.Add("Nenhum programa está usando a CPU de forma perceptível agora.");
        if (topMem.Rows is not null) parts.Add($"O que mais usa memória: {Title(topMem)} ({Fmt.Bytes(topMem.Mem)}).");
        HeroText = string.Join(" ", parts);
        if (!snap.RatesAvailable) { HeroLevel = "info"; HeroChipText = "Medindo…"; }
        else if (cpuSum >= 85) { HeroLevel = "danger"; HeroChipText = $"CPU muito ocupada · programas usam {CpuPct(cpuSum)}"; }
        else if (cpuSum >= 50) { HeroLevel = "warn"; HeroChipText = $"CPU moderada · programas usam {CpuPct(cpuSum)}"; }
        else { HeroLevel = "ok"; HeroChipText = $"CPU tranquila · programas usam {CpuPct(cpuSum)}"; }
    }

    /// <summary>Resolve a descrição do arquivo (nome amigável) uma única vez por caminho, fora da UI.</summary>
    private void QueueDescription(ProcessRow row)
    {
        if (row.Description is not null || row.ImagePath is not { } path) return;
        if (_descCache.TryGetValue(path, out var cached)) { if (cached is not null) row.Description = cached; return; }
        if (!_descPending.Add(path)) return;
        _ = Task.Run(() =>
        {
            try
            {
                var d = System.Diagnostics.FileVersionInfo.GetVersionInfo(path).FileDescription?.Trim();
                return string.IsNullOrWhiteSpace(d) ? null : d;
            }
            catch { return null; }
        }).ContinueWith(t => Ui.Post(() =>
        {
            _descPending.Remove(path);
            _descCache[path] = t.Result;
            if (t.Result is null) return;
            foreach (var r in Rows) if (string.Equals(r.ImagePath, path, StringComparison.OrdinalIgnoreCase)) r.Description = t.Result;
        }), TaskScheduler.Default);
    }

    // =====================================================================
    // Tabela
    // =====================================================================
    public ObservableCollection<ProcessRow> Rows { get; } = new();
    public ListCollectionView View { get; }

    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _isTreeView;
    [ObservableProperty] private string _statusText = "Coletando processos…";
    [ObservableProperty] private string? _statusDetail;
    [ObservableProperty] private string? _gpuNote;
    [ObservableProperty] private ProcessRow? _selectedRow;

    partial void OnSearchTextChanged(string value) => View.Refresh();

    partial void OnIsTreeViewChanged(bool value)
    {
        View.SortDescriptions.Clear();
        if (value)
        {
            if (_lastSnapshot is { } s) ApplyTree(s);
            View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.TreeOrder), ListSortDirection.Ascending));
        }
        else
        {
            foreach (var r in Rows) r.Depth = 0;
            View.SortDescriptions.Add(new SortDescription(nameof(ProcessRow.Cpu), ListSortDirection.Descending));
        }
        OnPropertyChanged(nameof(CanSortColumns));
    }

    public bool CanSortColumns => !IsTreeView;

    private bool FilterRow(object o)
    {
        if (o is not ProcessRow r) return false;
        var q = SearchText?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return r.Name.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.DisplayName.Contains(q, StringComparison.OrdinalIgnoreCase)
            || r.Pid.ToString(CultureInfo.InvariantCulture) == q
            || (r.ImagePath?.Contains(q, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    // =====================================================================
    // Ciclo de vida
    // =====================================================================
    public override async Task OnNavigatedToAsync()
    {
        if (!_initialized)
        {
            _initialized = true;
            BuildCores();
            await LoadRulesAsync();
            if (WatcherEnabled) StartWatcher();
        }
        // Retorno à página: taxas recomeçam (evita "média" de um intervalo longo) e a janela de impacto é reiniciada.
        _sampler?.Reset();
        _window.Clear();
        _loopCts?.Cancel();
        _loopCts = new CancellationTokenSource();
        _ = LoopAsync(_loopCts.Token);
    }

    public override void OnNavigatedFrom()
    {
        _loopCts?.Cancel();
        _loopCts = null;
        _detailsCts?.Cancel();
        // O watcher de regras continua ativo (se habilitado) mesmo fora da página.
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        bool first = true;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var (snap, gpu) = await Task.Run(() =>
                {
                    _sampler ??= new ProcessSampler();
                    if (!_gpuInitTried)
                    {
                        _gpuInitTried = true;
                        try { _gpuReader = new GpuProcessUsageReader(); } catch (Exception ex) { App.Log(ex, "GPU por processo"); }
                    }
                    var s = _sampler.Capture();
                    GpuUsageSnapshot? g = null;
                    try { g = _gpuReader is { IsAvailable: true } r ? r.Read() : null; } catch (Exception ex) { App.Log(ex, "Leitura GPU por processo"); }
                    return (s, g);
                }, ct);
                if (ct.IsCancellationRequested) break;
                Apply(snap, gpu);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                App.Log(ex, "Amostragem de processos");
                StatusText = "Falha na amostragem: " + ex.Message;
            }
            try { await Task.Delay(first ? TimeSpan.FromSeconds(1) : SampleInterval, ct); }
            catch (OperationCanceledException) { break; }
            first = false;
        }
    }

    private void Apply(ProcessSnapshot snap, GpuUsageSnapshot? gpu)
    {
        _lastSnapshot = snap;
        bool gpuOk = gpu is { Available: true };
        var seen = new HashSet<ProcessKey>();
        foreach (var e in snap.Processes)
        {
            if (e.Pid == 0) continue; // "Processo ocioso do sistema" distorce a ordenação por CPU
            seen.Add(e.Key);
            if (!_rowMap.TryGetValue(e.Key, out var row))
            {
                row = new ProcessRow(e);
                if (row.ImagePath is { } ip && _descCache.TryGetValue(ip, out var desc) && desc is not null) row.Description = desc;
                _rowMap[e.Key] = row;
                Rows.Add(row);
            }
            else row.Update(e);
            row.SetGpu(gpuOk ? (gpu!.ByPid.TryGetValue(e.Pid, out var u) ? u.MaxEnginePercent : 0) : null);
        }
        if (seen.Count != _rowMap.Count)
        {
            foreach (var dead in _rowMap.Keys.Where(k => !seen.Contains(k)).ToList())
            {
                var r = _rowMap[dead];
                _rowMap.Remove(dead);
                Rows.Remove(r);
                if (ReferenceEquals(SelectedRow, r)) DetailNotes = "O processo foi encerrado.";
            }
        }
        if (IsTreeView) ApplyTree(snap);

        _window.Enqueue(snap);
        while (_window.Count > WindowCapacity) _window.Dequeue();

        GpuNote = gpuOk ? null : "Uso de GPU por processo indisponível neste PC (" + (gpu?.Error ?? _gpuReader?.InitializationError ?? "contador PDH \"GPU Engine\" ausente") + ").";
        StatusText = $"{Rows.Count} processos · atualiza a cada 2 s"
            + (snap.RatesAvailable ? "" : " · medindo CPU e disco…");
        StatusDetail = $"Coleta em {snap.CollectionMilliseconds:0.0} ms" + (snap.Warning is { } w ? " · " + w : "");
        if (SelectedRow is { } sel && _rowMap.ContainsKey(sel.Key))
        {
            if (sel.CreateTimeUtc is { } st) DetailUptime = Fmt.Duration(DateTime.UtcNow - st);
            UpdateDetailUsage(sel);
        }
        UpdateSummary(snap);

        _tick++;
        if (IsTabImpact && _tick % 5 == 0) _ = AnalyzeImpactAsync();
    }

    private void ApplyTree(ProcessSnapshot snap)
    {
        var list = snap.Processes.Where(p => p.Pid != 0).ToList();
        int order = 0;
        foreach (var node in ProcessTreeBuilder.Flatten(ProcessTreeBuilder.Build(list)))
        {
            if (!_rowMap.TryGetValue(node.Process.Key, out var row)) continue;
            row.Depth = Math.Min(node.Depth, 12);
            row.ParentMissing = node.ParentMissing;
            row.TreeOrder = order++;
        }
    }

    // =====================================================================
    // Detalhes
    // =====================================================================
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _detailName = "";
    [ObservableProperty] private string _detailTitle = "";

    private void UpdateDetailUsage(ProcessRow r)
    {
        var parts = new List<string>();
        parts.Add(r.Cpu >= 0 ? $"{CpuPct(r.Cpu)} da CPU" : "CPU: medindo…");
        parts.Add($"{Fmt.Bytes(r.WorkingSet)} de memória");
        if (r.Io > 0) parts.Add($"disco {r.IoText}");
        if (r.Gpu > 0.05) parts.Add($"GPU {r.GpuText}");
        DetailUsage = "Agora: " + string.Join(" · ", parts);
    }
    [ObservableProperty] private string _detailPid = "";
    [ObservableProperty] private string _detailPath = "—";
    [ObservableProperty] private string _detailSignature = "—";
    [ObservableProperty] private string _detailCompany = "—";
    [ObservableProperty] private string _detailDescription = "—";
    [ObservableProperty] private string _detailVersion = "—";
    [ObservableProperty] private string _detailStart = "—";
    [ObservableProperty] private string _detailUptime = "—";
    [ObservableProperty] private string _detailPriority = "—";
    [ObservableProperty] private string _detailAffinity = "—";
    [ObservableProperty] private string _detailUser = "—";
    [ObservableProperty] private string _detailCommandLine = "—";
    [ObservableProperty] private string? _detailNotes;
    [ObservableProperty] private string? _protectionText;
    [ObservableProperty] private bool _canAct;
    [ObservableProperty] private bool _hasImagePath;
    [ObservableProperty] private PriorityOption? _selectedPriority;
    [ObservableProperty] private AffinityPresetOption? _selectedAffinityPreset;
    [ObservableProperty] private string? _affinityNote;

    public IReadOnlyList<PriorityOption> PriorityOptions { get; }
    public IReadOnlyList<AffinityPresetOption> AffinityPresetOptions { get; }
    public ObservableCollection<CoreToggle> Cores { get; } = new();

    partial void OnSelectedRowChanged(ProcessRow? value) => _ = LoadDetailsAsync(value);

    partial void OnSelectedAffinityPresetChanged(AffinityPresetOption? value)
    {
        if (value is null) return;
        var comp = AffinityPresets.Compute(value.Value, _inventory.Current?.Cpu.Cores, _systemMask);
        AffinityNote = comp.Note;
        if (comp.Mask is { } m) SetCoreMask(m);
    }

    private void BuildCores()
    {
        Cores.Clear();
        var cores = _inventory.Current?.Cpu.Cores ?? new List<CoreInfo>();
        bool hybrid = cores.Select(c => c.EfficiencyClass).Distinct().Count() > 1;
        bool multiL3 = cores.Select(c => c.L3Domain).Distinct().Count() > 1;
        byte maxClass = cores.Count > 0 ? cores.Max(c => c.EfficiencyClass) : (byte)0;
        int n = Math.Min(Environment.ProcessorCount, 64);
        for (int i = 0; i < n; i++)
        {
            var core = cores.FirstOrDefault(c => c.Group == 0 && (c.Mask & (1UL << i)) != 0);
            string label = i.ToString(CultureInfo.InvariantCulture);
            if (core is not null && hybrid) label += core.EfficiencyClass == maxClass ? " P" : " E";
            if (core is not null && multiL3) label += $" · L3 {core.L3Domain}";
            Cores.Add(new CoreToggle(i, label));
        }
    }

    private void SetCoreMask(ulong mask)
    {
        foreach (var c in Cores) c.IsChecked = (mask & (1UL << c.Index)) != 0;
    }

    private ulong CoreMask() => Cores.Where(c => c.IsChecked).Aggregate(0UL, (m, c) => m | (1UL << c.Index));

    private async Task LoadDetailsAsync(ProcessRow? row)
    {
        _detailsCts?.Cancel();
        var cts = _detailsCts = new CancellationTokenSource();
        HasSelection = row is not null;
        if (row is null) { CanAct = false; return; }

        DetailName = row.Name;
        DetailTitle = row.DisplayName;
        UpdateDetailUsage(row);
        QueueDescription(row);
        DetailPid = $"PID {row.Pid} · pai {row.ParentText}";
        DetailPath = row.ImagePath ?? "—";
        HasImagePath = row.ImagePath is not null;
        DetailSignature = "—"; DetailCompany = "—"; DetailDescription = "—"; DetailVersion = "—";
        DetailPriority = "—"; DetailAffinity = "—"; DetailUser = "—"; DetailCommandLine = "—"; DetailNotes = null;
        DetailStart = row.CreateTimeUtc is { } st ? st.ToLocalTime().ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.GetCultureInfo("pt-BR")) : "—";
        DetailUptime = row.CreateTimeUtc is { } st2 ? Fmt.Duration(DateTime.UtcNow - st2) : "—";
        ProtectionText = row.IsSelf ? "Este é o próprio RBM Optimizer: encerrar ou alterar por aqui não é permitido."
            : row.IsCritical ? $"\"{row.Name}\" é um processo crítico do Windows. Encerrar, mudar prioridade ou afinidade pode travar o sistema, derrubar a sessão ou desativar a proteção; por isso as ações estão bloqueadas."
            : row.IsManualOnly ? $"\"{row.Name}\" afeta a sessão (interface, áudio ou impressão). As ações são permitidas, mas exigem confirmação explícita e podem reiniciar a interface."
            : null;
        CanAct = !row.IsProtected;
        SelectedAffinityPreset = null; AffinityNote = null;

        var r = await Task.Run(() => _details.GetDetails(row.Pid, row.CreateTime));
        if (cts.IsCancellationRequested || !ReferenceEquals(SelectedRow, row)) return;
        if (!r.Success || r.Value is not { } d)
        {
            DetailNotes = r.Message;
            if (r.ErrorCode is ProcessErrorCodes.NotFound or ProcessErrorCodes.PidReused) CanAct = false;
            return;
        }
        DetailPath = d.ImagePath ?? "Indisponível";
        HasImagePath = d.ImagePath is not null;
        DetailCompany = d.CompanyName ?? "—";
        DetailDescription = d.FileDescription ?? d.ProductName ?? "—";
        if (!string.IsNullOrWhiteSpace(d.FileDescription ?? d.ProductName)) DetailTitle = (d.FileDescription ?? d.ProductName)!;
        DetailVersion = d.FileVersion ?? "—";
        DetailPriority = ProcessText.Priority(d.Priority);
        SelectedPriority = PriorityOptions.FirstOrDefault(p => p.Value == d.Priority);
        if (d.SystemAffinityMask is { } sm && sm != 0) _systemMask = sm;
        if (d.AffinityMask is { } am)
        {
            DetailAffinity = (am & _systemMask) == _systemMask ? $"Todas as CPUs ({ProcessText.Mask(am)})" : ProcessText.Mask(am);
            SetCoreMask(am);
        }
        else SetCoreMask(_systemMask);
        DetailUser = d.UserName is null ? "—" : d.UserName + (d.IsElevated == true ? " · elevado" : d.IsElevated == false ? " · não elevado" : "");
        DetailCommandLine = d.CommandLine ?? "—";
        DetailNotes = d.Unavailable.Count > 0 ? string.Join(" ", d.Unavailable) : null;

        if (d.ImagePath is { } path)
        {
            DetailSignature = "Verificando assinatura…";
            try
            {
                var sig = await ProcessDetailsProvider.GetSignatureAsync(path, cts.Token);
                if (!cts.IsCancellationRequested && ReferenceEquals(SelectedRow, row)) DetailSignature = sig.Display;
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { DetailSignature = "Falha na verificação: " + ex.Message; }
        }
        else DetailSignature = "Indisponível (caminho desconhecido)";
    }

    [RelayCommand]
    private void RevealImage()
    {
        var p = SelectedRow?.ImagePath ?? (File.Exists(DetailPath) ? DetailPath : null);
        if (p is not null) SysUi.RevealFile(p);
    }

    [RelayCommand]
    private Task RefreshDetails() => LoadDetailsAsync(SelectedRow);

    [RelayCommand]
    private void SelectAllCores() => SetCoreMask(_systemMask);

    // =====================================================================
    // Ações
    // =====================================================================
    private bool Guard(ProcessRow? r)
    {
        if (r is null) return false;
        if (r.IsProtected)
        {
            App.Notify.Warning("Ação bloqueada", ProtectionText ?? "Processo protegido.");
            return false;
        }
        return true;
    }

    private static string ManualWarning(ProcessRow r) => r.IsManualOnly
        ? $"\n\nAtenção: \"{r.Name}\" afeta a sessão — a interface, o áudio ou a impressão podem reiniciar ou parar." : "";

    [RelayCommand]
    private async Task EndProcess()
    {
        var r = SelectedRow;
        if (!Guard(r)) return;
        if (!await App.Dialogs.ConfirmAsync("Encerrar processo",
                $"Encerrar \"{r!.Name}\" (PID {r.Pid})? Alterações não salvas nesse programa serão perdidas." + ManualWarning(r),
                r.ImagePath, "Encerrar", danger: true)) return;
        var res = await Task.Run(() => _actions.Kill(r.Pid, r.CreateTime, userConfirmed: true));
        if (res.Success)
        {
            App.Notify.Success("Processo encerrado", res.Message);
            await SysUi.AuditAsync("process.kill", $"{r.Name} (PID {r.Pid})", true);
        }
        else if (res.ErrorCode == ProcessErrorCodes.AccessDenied) await KillViaAgentAsync(r, tree: false);
        else
        {
            App.Notify.Error("Não foi possível encerrar", res.Message);
            await SysUi.AuditAsync("process.kill", $"{r.Name} (PID {r.Pid}): {res.Message}", false);
        }
    }

    [RelayCommand]
    private async Task EndTree()
    {
        var r = SelectedRow;
        if (!Guard(r) || _lastSnapshot is not { } snap) return;
        var node = ProcessTreeBuilder.Flatten(ProcessTreeBuilder.Build(snap.Processes.Where(p => p.Pid != 0).ToList()))
            .FirstOrDefault(n => n.Process.Key == r!.Key);
        var desc = node is null ? new List<ProcessTreeNode>() : ProcessTreeBuilder.Flatten(node.Children).ToList();
        var protectedDesc = desc.Where(d => !ProcessGuard.IsAutomationAllowed(d.Process.Pid, d.Process.Name)).ToList();
        string details = desc.Count == 0 ? "Nenhum processo filho no momento."
            : "Filhos que serão encerrados:\n" + string.Join("\n", desc.Take(15).Select(d => $"• {d.Process.Name} (PID {d.Process.Pid})"))
              + (desc.Count > 15 ? $"\n… e mais {desc.Count - 15}" : "")
              + (protectedDesc.Count > 0 ? $"\n\n{protectedDesc.Count} filho(s) protegido(s) serão mantidos." : "");
        if (!await App.Dialogs.ConfirmAsync("Encerrar árvore de processos",
                $"Encerrar \"{r!.Name}\" (PID {r.Pid}) e {desc.Count} processo(s) filho(s)? Alterações não salvas em todos eles serão perdidas." + ManualWarning(r),
                details, "Encerrar árvore", danger: true, requiredPhrase: "ENCERRAR")) return;
        var res = await Task.Run(() => _actions.KillTree(r.Pid, confirmTree: true, r.CreateTime, userConfirmed: true));
        string skipped = res.Value is { Skipped.Count: > 0 } o ? " Mantidos: " + string.Join("; ", o.Skipped.Take(5)) : "";
        if (res.Success)
        {
            App.Notify.Success("Árvore encerrada", res.Message + skipped);
            await SysUi.AuditAsync("process.killTree", $"{r.Name} (PID {r.Pid}): {res.Message}", true);
        }
        else if (res.ErrorCode == ProcessErrorCodes.AccessDenied) await KillViaAgentAsync(r, tree: true);
        else
        {
            App.Notify.Error("Encerramento parcial ou falho", res.Message + skipped);
            await SysUi.AuditAsync("process.killTree", $"{r.Name} (PID {r.Pid}): {res.Message}", false);
        }
    }

    private static Task<bool> OfferAgentAsync(ProcessRow r, string action) => App.Dialogs.ConfirmAsync("Acesso negado",
        $"O Windows negou {action} \"{r.Name}\" sem privilégios de administrador (processo elevado ou de outro usuário). " +
        "Deseja executar pelo RBM Agent? " + (AgentCall.IsConnected ? "O agente elevado já está ativo." : "O Windows pedirá confirmação (UAC)."),
        "A operação passa pela mesma validação de segurança no agente e é registrada na auditoria.", "Executar como administrador");

    private async Task KillViaAgentAsync(ProcessRow r, bool tree)
    {
        if (!await OfferAgentAsync(r, tree ? "encerrar a árvore de" : "encerrar")) return;
        var resp = await AgentCall.InvokeAsync(AgentOps.ProcessKill,
            new ProcessKillCommand(r.Pid, tree, TreeConfirmed: tree, UserConfirmed: true, ExpectedCreateTime: r.CreateTime),
            tree ? "Encerrar árvore" : "Encerrar processo");
        var body = AgentCall.Read<ProcessActionResponse>(resp);
        if (resp is not null)
        {
            App.Notify.Success(tree ? "Árvore encerrada (agente)" : "Processo encerrado (agente)", body?.Message ?? "Concluído.");
            await SysUi.AuditAsync(tree ? "process.killTree.agent" : "process.kill.agent", $"{r.Name} (PID {r.Pid}): {body?.Message}", true);
        }
    }

    [RelayCommand]
    private async Task ApplyPriority()
    {
        var r = SelectedRow;
        if (!Guard(r) || SelectedPriority?.Value is not { } prio) return;
        if (prio == ProcessPriority.High || r!.IsManualOnly)
        {
            string risk = prio == ProcessPriority.High
                ? "Prioridade Alta faz o processo ganhar a CPU antes dos demais; se ele usar muita CPU, outros programas (e a própria interface) podem ficar lentos."
                : "";
            if (!await App.Dialogs.ConfirmAsync("Alterar prioridade",
                    $"Definir a prioridade de \"{r!.Name}\" como {ProcessPriorityMap.Display(prio)}? Vale só até o processo ser reiniciado. {risk}" + ManualWarning(r),
                    confirmText: "Alterar")) return;
        }
        var res = await Task.Run(() => _actions.SetPriority(r!.Pid, prio, r.CreateTime, userConfirmed: true));
        if (res.Success)
        {
            App.Notify.Success("Prioridade alterada", res.Message);
            await SysUi.AuditAsync("process.setPriority", $"{r!.Name} (PID {r.Pid}): {ProcessText.Priority(res.Value?.Previous)} → {prio}", true);
            await LoadDetailsAsync(r);
        }
        else if (res.ErrorCode == ProcessErrorCodes.AccessDenied)
        {
            if (!await OfferAgentAsync(r!, "alterar a prioridade de")) return;
            var resp = await AgentCall.InvokeAsync(AgentOps.ProcessSetPriority,
                new ProcessPriorityCommand(r!.Pid, prio.ToString(), null, UserConfirmed: true, ExpectedCreateTime: r.CreateTime), "Alterar prioridade");
            if (resp is not null)
            {
                App.Notify.Success("Prioridade alterada (agente)", AgentCall.Read<ProcessActionResponse>(resp)?.Message ?? "Concluído.");
                await SysUi.AuditAsync("process.setPriority.agent", $"{r.Name} (PID {r.Pid}) → {prio}", true);
                await LoadDetailsAsync(r);
            }
        }
        else App.Notify.Error("Não foi possível alterar a prioridade", res.Message);
    }

    [RelayCommand]
    private async Task ApplyAffinity()
    {
        var r = SelectedRow;
        if (!Guard(r)) return;
        ulong mask = CoreMask();
        if (!ProcessGuard.ValidateAffinityMask(mask, _systemMask, out var err)) { App.Notify.Warning("Afinidade inválida", err!); return; }
        if (!await App.Dialogs.ConfirmAsync("Alterar afinidade",
                $"Restringir \"{r!.Name}\" a {ProcessText.Mask(mask)}? A alteração vale até o processo ser reiniciado. " +
                "Limitar núcleos pode reduzir o desempenho do programa; em jogos, use só quando houver um motivo medido (ex.: CCD com V-Cache)." + ManualWarning(r),
                AffinityNote, "Aplicar afinidade")) return;
        var res = await Task.Run(() => _actions.SetAffinity(r.Pid, mask, r.CreateTime, userConfirmed: true));
        if (res.Success)
        {
            App.Notify.Success("Afinidade alterada", res.Message);
            await SysUi.AuditAsync("process.setAffinity", $"{r.Name} (PID {r.Pid}): 0x{res.Value?.Previous:X} → 0x{mask:X}", true);
            await LoadDetailsAsync(r);
        }
        else if (res.ErrorCode == ProcessErrorCodes.AccessDenied)
        {
            if (!await OfferAgentAsync(r, "alterar a afinidade de")) return;
            var resp = await AgentCall.InvokeAsync(AgentOps.ProcessSetPriority,
                new ProcessPriorityCommand(r.Pid, null, unchecked((long)mask), UserConfirmed: true, ExpectedCreateTime: r.CreateTime), "Alterar afinidade");
            if (resp is not null)
            {
                App.Notify.Success("Afinidade alterada (agente)", AgentCall.Read<ProcessActionResponse>(resp)?.Message ?? "Concluído.");
                await SysUi.AuditAsync("process.setAffinity.agent", $"{r.Name} (PID {r.Pid}) → 0x{mask:X}", true);
                await LoadDetailsAsync(r);
            }
        }
        else App.Notify.Error("Não foi possível alterar a afinidade", res.Message);
    }

    // =====================================================================
    // Regras
    // =====================================================================
    public ObservableCollection<ProcessRuleRow> RuleRows { get; } = new();
    public ObservableCollection<RuleActionLog> RuleLog { get; } = new();
    public IReadOnlyList<PriorityOption> RulePriorityOptions { get; }
    public IReadOnlyList<AffinityPresetOption> RulePresetOptions { get; }
    public string RulesFile => _ruleStore.FilePath;

    [ObservableProperty] private ProcessRuleRow? _selectedRuleRow;
    [ObservableProperty] private string? _rulesMessage;
    [ObservableProperty] private bool _watcherEnabled;
    [ObservableProperty] private string _watcherStatus = "Desativado — nenhuma regra é aplicada.";
    [ObservableProperty] private string? _editId;
    [ObservableProperty] private string _editName = "";
    [ObservableProperty] private bool _editEnabled = true;
    [ObservableProperty] private PriorityOption _editPriority = null!;
    [ObservableProperty] private AffinityPresetOption _editPreset = null!;
    [ObservableProperty] private string _editMask = "";
    [ObservableProperty] private string _editExceptions = "";
    [ObservableProperty] private string _editNote = "";
    [ObservableProperty] private string? _editErrors;
    [ObservableProperty] private string? _editPreview;
    [ObservableProperty] private bool _isEditingExisting;

    public bool IsCustomMask => EditPreset.Value == AffinityPreset.CustomMask;

    partial void OnEditNameChanged(string value) => UpdateEditPreview();
    partial void OnEditPriorityChanged(PriorityOption value) => UpdateEditPreview();
    partial void OnEditPresetChanged(AffinityPresetOption value) { OnPropertyChanged(nameof(IsCustomMask)); UpdateEditPreview(); }
    partial void OnEditMaskChanged(string value) => UpdateEditPreview();
    partial void OnEditExceptionsChanged(string value) => UpdateEditPreview();
    partial void OnEditEnabledChanged(bool value) => UpdateEditPreview();

    partial void OnSelectedRuleRowChanged(ProcessRuleRow? value)
    {
        if (value is null) return;
        var r = value.Rule;
        EditId = r.Id;
        IsEditingExisting = true;
        EditName = r.ExecutableName;
        EditEnabled = r.Enabled;
        EditPriority = RulePriorityOptions.FirstOrDefault(p => p.Value == r.Priority) ?? RulePriorityOptions[0];
        EditPreset = RulePresetOptions.FirstOrDefault(p => p.Value == r.Affinity) ?? RulePresetOptions[0];
        EditMask = r.AffinityMask is { } m ? $"0x{m:X}" : "";
        EditExceptions = string.Join(Environment.NewLine, r.Exceptions);
        EditNote = r.Note ?? "";
        UpdateEditPreview();
    }

    partial void OnWatcherEnabledChanged(bool value)
    {
        if (_loadingSettings) return;
        _settings.Set("processes.rules.enabled", value);
        if (value) StartWatcher(); else _ = StopWatcherAsync();
    }

    private async Task LoadRulesAsync()
    {
        var r = await Task.Run(() => _ruleStore.Load());
        if (!r.Success || r.Value is null)
        {
            _rulesLoadFailed = true;
            RulesMessage = r.Message + " As regras não serão salvas até o arquivo ser corrigido.";
            _ruleSet = new ProcessRuleSet();
        }
        else
        {
            _rulesLoadFailed = false;
            _ruleSet = r.Value;
            RulesMessage = r.Message == "OK" || r.Message.StartsWith("Nenhuma") ? null : r.Message;
        }
        RefreshRuleRows();
    }

    private void RefreshRuleRows()
    {
        _activeRules = _ruleSet.Rules.ToList();
        RuleRows.Clear();
        foreach (var r in _ruleSet.Rules)
        {
            string aff = r.Affinity == AffinityPreset.None ? "—"
                : r.Affinity == AffinityPreset.CustomMask && r.AffinityMask is { } m ? ProcessText.Mask(m) : ProcessText.Preset(r.Affinity);
            RuleRows.Add(new ProcessRuleRow(r, r.Priority is { } p ? ProcessPriorityMap.Display(p) : "—", aff,
                r.Exceptions.Count == 0 ? "—" : $"{r.Exceptions.Count} exceção(ões)", ProcessRuleValidator.Validate(r)));
        }
    }

    private ProcessRule BuildRule(out string? maskError)
    {
        maskError = null;
        ulong? mask = null;
        if (EditPreset.Value == AffinityPreset.CustomMask)
        {
            var t = EditMask.Trim();
            if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
            if (ulong.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var m)) mask = m;
            else if (t.Length > 0) maskError = "Máscara inválida: use hexadecimal (ex.: 0xFF).";
        }
        return new ProcessRule
        {
            Id = EditId ?? Guid.NewGuid().ToString("N"),
            ExecutableName = EditName.Trim(),
            Enabled = EditEnabled,
            Priority = EditPriority.Value,
            Affinity = EditPreset.Value,
            AffinityMask = mask,
            Exceptions = EditExceptions.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries),
            Note = string.IsNullOrWhiteSpace(EditNote) ? null : EditNote.Trim(),
        };
    }

    private void UpdateEditPreview()
    {
        if (EditPriority is null || EditPreset is null) return; // construção
        var rule = BuildRule(out var maskError);
        var errors = ProcessRuleValidator.Validate(rule).ToList();
        if (maskError is not null) errors.Insert(0, maskError);
        EditErrors = errors.Count > 0 ? string.Join("\n", errors.Select(e => "• " + e)) : null;
        var parts = new List<string>();
        if (rule.Affinity != AffinityPreset.None)
        {
            var comp = AffinityPresets.Compute(rule.Affinity, _inventory.Current?.Cpu.Cores, _systemMask, rule.AffinityMask);
            parts.Add(comp.Mask is { } m ? $"Afinidade: {ProcessText.Mask(m)}. {comp.Note}" : $"Afinidade: {comp.Note}");
        }
        if (rule.ExecutableName.Length > 0)
        {
            int running = Rows.Count(r => ProcessRuleMatcher.NameMatches(rule.ExecutableName, r.Name));
            parts.Add($"{running} processo(s) em execução correspondem ao nome.");
        }
        EditPreview = parts.Count > 0 ? string.Join(" ", parts) : null;
    }

    [RelayCommand]
    private void NewRule()
    {
        SelectedRuleRow = null;
        EditId = null;
        IsEditingExisting = false;
        EditName = SelectedRow is { IsProtected: false } s ? s.Name : "";
        EditEnabled = true;
        EditPriority = RulePriorityOptions[0];
        EditPreset = RulePresetOptions[0];
        EditMask = ""; EditExceptions = ""; EditNote = "";
        UpdateEditPreview();
    }

    [RelayCommand]
    private async Task SaveRule()
    {
        if (_rulesLoadFailed) { App.Notify.Warning("Regras não salvas", RulesMessage ?? "Arquivo de regras inválido."); return; }
        var rule = BuildRule(out var maskError);
        if (maskError is not null || ProcessRuleValidator.Validate(rule).Count > 0)
        {
            App.Notify.Warning("Regra inválida", EditErrors ?? maskError ?? "Corrija os campos destacados.");
            return;
        }
        var list = _ruleSet.Rules.ToList();
        int i = list.FindIndex(r => r.Id == rule.Id);
        if (i >= 0) list[i] = rule; else list.Add(rule);
        if (await PersistAsync(list))
        {
            EditId = rule.Id;
            IsEditingExisting = true;
            App.Notify.Success("Regra salva", $"{rule.ExecutableName}: " + (WatcherEnabled ? "aplicada a processos novos e aos que já estão em execução." : "ative a aplicação automática para que ela tenha efeito."));
            await SysUi.AuditAsync("process.rule.save", $"{rule.ExecutableName} ({rule.Id})", true);
        }
    }

    [RelayCommand]
    private async Task DeleteRule()
    {
        if (EditId is not { } id || _ruleSet.Rules.FirstOrDefault(r => r.Id == id) is not { } rule) return;
        if (!await App.Dialogs.ConfirmAsync("Excluir regra", $"Excluir a regra para \"{rule.ExecutableName}\"? Processos já ajustados mantêm a prioridade/afinidade atual até serem reiniciados.",
                confirmText: "Excluir", danger: true)) return;
        if (await PersistAsync(_ruleSet.Rules.Where(r => r.Id != id).ToList()))
        {
            NewRule();
            App.Notify.Success("Regra excluída", rule.ExecutableName);
            await SysUi.AuditAsync("process.rule.delete", $"{rule.ExecutableName} ({rule.Id})", true);
        }
    }

    private async Task<bool> PersistAsync(List<ProcessRule> rules)
    {
        var set = _ruleSet with { Rules = rules };
        var res = await Task.Run(() => _ruleStore.Save(set));
        if (!res.Success) { App.Notify.Error("Falha ao salvar regras", res.Message); return false; }
        _ruleSet = set;
        RefreshRuleRows();
        return true;
    }

    private void StartWatcher()
    {
        if (_watcher is null)
        {
            _watcher = new ProcessRuleWatcher(() => _activeRules, _inventory.Current?.Cpu.Cores, new ProcessRuleWatcherOptions(), null, ElevatedFallbackAsync);
            _watcher.ActionLogged += (_, log) => Ui.Post(() =>
            {
                RuleLog.Insert(0, log);
                while (RuleLog.Count > 200) RuleLog.RemoveAt(RuleLog.Count - 1);
            });
        }
        _watcher.Start();
        WatcherStatus = "Ativo — verifica processos novos a cada 2 s (continua ativo fora desta página).";
    }

    private async Task StopWatcherAsync()
    {
        if (_watcher is not null) await _watcher.StopAsync();
        WatcherStatus = "Desativado — nenhuma regra é aplicada.";
    }

    /// <summary>Fallback elevado do watcher: só usa o agente se ele JÁ estiver conectado (nunca dispara UAC em segundo plano).</summary>
    private static async Task<OperationResult> ElevatedFallbackAsync(ProcessPriorityRequest req, CancellationToken ct)
    {
        var client = AgentCall.Client;
        if (!client.IsConnected)
            return OperationResult.Fail("Acesso negado; o RBM Agent não está ativo, então a regra não foi aplicada a este processo.", ProcessErrorCodes.AccessDenied);
        try
        {
            var r = await client.InvokeAsync(AgentOps.ProcessSetPriority, req, null, ct);
            return r.Ok ? OperationResult.Ok("Aplicado pelo RBM Agent.") : OperationResult.Fail(r.Error ?? "O agente recusou a operação.", ProcessErrorCodes.Failed);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return OperationResult.Fail("Falha ao contatar o agente: " + ex.Message, ProcessErrorCodes.Failed); }
    }

    [RelayCommand]
    private void ClearRuleLog() => RuleLog.Clear();

    [RelayCommand]
    private Task ReloadRules() => LoadRulesAsync();

    // =====================================================================
    // Impacto em segundo plano
    // =====================================================================
    public ObservableCollection<ProcessImpactRow> ImpactRows { get; } = new();
    [ObservableProperty] private string _impactSummary = "A análise usa as medições feitas enquanto esta página está aberta (até 5 min).";
    [ObservableProperty] private int _impactLeaks;
    [ObservableProperty] private string? _impactNotes;
    [ObservableProperty] private bool _impactBusy;

    [RelayCommand]
    private async Task AnalyzeImpactAsync()
    {
        if (ImpactBusy) return;
        var window = _window.ToList();
        if (window.Count < 2)
        {
            ImpactSummary = "Aguardando medições: deixe a página aberta por alguns segundos.";
            return;
        }
        ImpactBusy = true;
        try
        {
            var report = await Task.Run(() => BackgroundImpactAnalyzer.Analyze(window, new ImpactAnalysisOptions { TopN = 15 }));
            ImpactRows.Clear();
            foreach (var i in report.ByImpact.Take(25))
                ImpactRows.Add(new ProcessImpactRow(i.Name, i.Key.Pid, i.ImagePath, i.SampleCount, Fmt.Duration(i.ObservedFor),
                    Math.Round(i.AvgCpuPercent, 1), Math.Round(i.PeakCpuPercent, 1), Fmt.Rate(i.AvgIoBytesPerSec),
                    ProcessText.SlopeMb(i.WorkingSetSlopeBytesPerMin), ProcessText.SlopeMb(i.PrivateBytesSlopeBytesPerMin),
                    Fmt.Bytes(i.PrivateBytesEnd), i.PossibleLeak, Math.Round(i.ImpactScore, 1), string.Join(" ", i.Notes)));
            int leaks = report.ByImpact.Count(i => i.PossibleLeak);
            var span = report.To - report.From;
            ImpactSummary = $"Últimos {Fmt.Duration(span)} ({report.From.ToLocalTime():HH:mm:ss}–{report.To.ToLocalTime():HH:mm:ss}, {report.SnapshotCount} medições) · "
                + (leaks > 0 ? $"{leaks} programa(s) com memória crescendo sem parar (possível vazamento)" : "nenhum programa com memória crescendo de forma suspeita");
            ImpactLeaks = leaks;
            ImpactNotes = report.Notes.Count > 0 ? string.Join(" ", report.Notes) : null;
        }
        catch (Exception ex) { App.Log(ex, "Análise de impacto"); App.Notify.Error("Falha na análise de impacto", ex.Message); }
        finally { ImpactBusy = false; }
    }
}
