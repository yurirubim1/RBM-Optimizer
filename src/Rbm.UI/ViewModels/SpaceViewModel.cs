using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows.Threading;
using Rbm.Core.Agent;
using Rbm.Optimization.SpaceAnalysis;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.Space;
using Rbm.UI.Pages.SystemPages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI.ViewModels;

/// <summary>
/// Análise de espaço: varre uma pasta/unidade (sem elevação, em threads de fundo; ou pelo agente elevado) e mostra um treemap
/// navegável. Somente leitura: nada é apagado aqui (a limpeza segura fica na página Limpeza).
/// </summary>
public sealed partial class SpaceViewModel : PageViewModel
{
    public override string Key => "space";

    private const int MaxListItems = 300;
    private CancellationTokenSource? _cts;
    private int _scanId;
    private bool _elevatedPending;
    private DispatcherTimer? _elapsedTimer;
    private Stopwatch? _elevatedClock;

    public ObservableCollection<SpaceDrive> Drives { get; } = new();
    public ObservableCollection<SpaceCrumb> Crumbs { get; } = new();
    public ObservableCollection<SpaceItemRow> Items { get; } = new();
    public IReadOnlyList<LegendEntry> Legend => TreemapPalette.Legend;

    [ObservableProperty] private string _targetPath = "";
    [ObservableProperty] private bool _useAllocated;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanStart))] private bool _isScanning;
    [ObservableProperty] private bool _isElevatedRunning;
    [ObservableProperty] private string _progressText = "";
    [ObservableProperty] private string _progressPath = "";

    [ObservableProperty] [NotifyPropertyChangedFor(nameof(HasResult))] private SpaceScanResult? _result;
    [ObservableProperty] [NotifyPropertyChangedFor(nameof(CanGoUp))] private SpaceNode? _current;
    [ObservableProperty] private SpaceNode? _selected;
    [ObservableProperty] private SpaceItemRow? _selectedRow;

    [ObservableProperty] private string _totalText = "—";
    [ObservableProperty] private string _modeText = "";
    [ObservableProperty] private string _filesText = "—";
    [ObservableProperty] private string _foldersText = "—";
    [ObservableProperty] private string _inaccessibleText = "—";
    [ObservableProperty] private string _inaccessibleTip = "";
    [ObservableProperty] private bool _hasInaccessible;
    [ObservableProperty] private string _durationText = "—";
    [ObservableProperty] private string _sourceText = "";
    [ObservableProperty] private string _resultNote = "";

    [ObservableProperty] private string _currentTitle = "";
    [ObservableProperty] private string _currentDetail = "";

    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string _selName = "";
    [ObservableProperty] private string _selPath = "";
    [ObservableProperty] private string _selSize = "";
    [ObservableProperty] private string _selShare = "";
    [ObservableProperty] private string _selCounts = "";
    [ObservableProperty] private string _selKind = "";

    // ---- Resumo em linguagem simples (banner do topo) ----
    [ObservableProperty] private string _heroTitle = "Escolha um disco para ver o que ocupa espaço";
    [ObservableProperty] private string _heroText = "Clique em um disco abaixo. A análise só lê o disco — nada é apagado nesta página.";
    [ObservableProperty] private string _heroChip = "Somente leitura";
    [ObservableProperty] private string _heroTone = "info";
    /// <summary>Mostra o aviso "há pastas sem acesso" com a ação de varrer como administrador.</summary>
    [ObservableProperty] private bool _suggestElevated;

    public bool HasResult => Result is not null;
    public bool CanStart => !IsScanning;
    public bool CanGoUp => Current?.Parent is not null;

    public string ElevationExplanation =>
        "Sem administrador, o Windows não deixa listar algumas pastas — por exemplo System Volume Information (pontos de restauração e " +
        "cópias de sombra), perfis de outros usuários e partes de Windows\\, ProgramData\\ e WindowsApps. Elas entram como “inacessíveis” " +
        "e o tamanho delas fica fora do total. A varredura como administrador roda no RBM Agent (pede UAC no primeiro uso), " +
        "é somente leitura e devolve uma árvore resumida (até 50 mil itens; os menores são agrupados).";

    // ---------------- Ciclo de vida ----------------

    public override async Task OnNavigatedToAsync()
    {
        try
        {
            var drives = await Task.Run(LoadDrives);
            Drives.Clear();
            foreach (var d in drives) Drives.Add(d);
            if (string.IsNullOrWhiteSpace(TargetPath) && drives.Count > 0) TargetPath = drives[0].Root;
        }
        catch (Exception ex) { App.Log(ex, "Análise de espaço: listar unidades"); }
        UpdateHero();
    }

    partial void OnIsScanningChanged(bool value) => UpdateHero();
    partial void OnIsElevatedRunningChanged(bool value) => UpdateHero();

    /// <summary>Frase do topo a partir do estado real (unidades, varredura em andamento, último resultado).</summary>
    private void UpdateHero()
    {
        if (IsScanning)
        {
            HeroTitle = $"Analisando {TargetPath.Trim()}…";
            HeroText = IsElevatedRunning ? "Varredura como administrador em andamento. Pode levar alguns minutos em discos grandes."
                                         : "Lendo pastas e arquivos. Você pode continuar usando o PC normalmente.";
            HeroChip = "Analisando…"; HeroTone = "info";
            return;
        }
        if (Result is { } r)
        {
            HeroTitle = $"O que ocupa espaço em {r.Root.Name}";
            var top = r.Root.Children.Where(c => c.Size > 0 && !c.IsAggregate).MaxBy(c => c.Size);
            HeroText = (top is not null && r.TotalBytes > 0
                           ? $"{Fmt.Bytes(r.TotalBytes)} analisados. O maior item é “{top.Name}”, com {Fmt.Bytes(top.Size)} ({(top.Size * 100.0 / r.TotalBytes).ToString("0", CultureInfo.CurrentCulture)}% do total). "
                           : $"{Fmt.Bytes(r.TotalBytes)} analisados. ")
                       + "Clique num bloco do mapa para entrar na pasta.";
            SuggestElevated = r.InaccessibleCount > 0 && !r.Elevated;
            (HeroChip, HeroTone) = r.Elevated ? ("Varredura completa (administrador)", "ok")
                : r.InaccessibleCount > 0 ? ($"{r.InaccessibleCount:N0} pasta(s) sem acesso", "warn")
                : ("Varredura completa", "ok");
            return;
        }
        SuggestElevated = false;
        HeroTitle = "Escolha um disco para ver o que ocupa espaço";
        HeroText = Drives.Count > 0 ? "Clique em um disco abaixo. A análise só lê o disco — nada é apagado nesta página."
                                    : "Use “Escolher pasta…” para analisar uma pasta. A análise só lê — nada é apagado nesta página.";
        var low = Drives.FirstOrDefault(d => d.IsLow);
        (HeroChip, HeroTone) = low is not null ? ($"Disco {low.Letter} quase cheio", "warn") : ("Somente leitura", "info");
    }

    /// <summary>Ao sair: cancela a varredura local (mantém o último resultado). A varredura elevada não pode ser cancelada no agente e segue em segundo plano.</summary>
    public override void OnNavigatedFrom()
    {
        if (_cts is { IsCancellationRequested: false } && !IsElevatedRunning) _cts.Cancel();
    }

    private static List<SpaceDrive> LoadDrives()
    {
        var list = new List<SpaceDrive>();
        foreach (var d in DriveInfo.GetDrives())
        {
            try
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                long total = d.TotalSize, free = d.TotalFreeSpace;
                string label = string.IsNullOrWhiteSpace(d.VolumeLabel) ? "Disco local" : d.VolumeLabel;
                list.Add(new SpaceDrive(d.Name, $"{label} ({d.Name.TrimEnd('\\')})", $"{Fmt.Bytes(free)} livres de {Fmt.Bytes(total)}",
                    total > 0 ? (total - free) * 100.0 / total : 0, Fmt.Bytes(free), Fmt.Bytes(total), total > 0 && free * 10 < total));
            }
            catch { /* unidade indisponível */ }
        }
        return list;
    }

    // ---------------- Varredura ----------------

    [RelayCommand]
    private void Browse()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Escolha a pasta ou unidade para analisar", Multiselect = false };
        try { if (Directory.Exists(TargetPath)) dlg.InitialDirectory = TargetPath; } catch { }
        if (dlg.ShowDialog(App.Current.MainWindow) == true && !string.IsNullOrWhiteSpace(dlg.FolderName))
        {
            TargetPath = dlg.FolderName;
            if (!IsScanning) _ = ScanAsync();
        }
    }

    [RelayCommand]
    private Task ScanDrive(SpaceDrive? d)
    {
        if (d is null || IsScanning) return Task.CompletedTask;
        TargetPath = d.Root;
        return ScanAsync();
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (IsScanning) return;
        var err = SpaceScanner.ValidatePath(TargetPath, requireLocalFixed: false, out var full);
        if (err is not null) { App.Notify.Warning("Análise de espaço", err); return; }

        int id = ++_scanId;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsScanning = true; IsBusy = true; BusyText = "Analisando espaço…";
        ProgressText = "Iniciando…"; ProgressPath = full;
        var progress = new Progress<SpaceScanProgress>(p => { if (id == _scanId && IsScanning) ShowProgress(p); });
        using var tracker = App.Tasks.Begin("Análise de espaço: " + full);
        try
        {
            var opts = new SpaceScanOptions { SizeMode = UseAllocated ? SpaceSizeMode.Allocated : SpaceSizeMode.Logical };
            var r = await SpaceScanner.ScanAsync(full, opts, progress, ct);
            if (id != _scanId) return;
            ApplyResult(r);
            App.Notify.Success("Análise concluída", $"{Fmt.Bytes(r.TotalBytes)} em {r.Files:N0} arquivos ({Fmt.Duration(r.Duration)}).");
        }
        catch (OperationCanceledException)
        {
            App.Notify.Info("Análise cancelada", Result is null ? "Nenhum resultado foi gerado." : "O resultado anterior foi mantido.");
        }
        catch (Exception ex)
        {
            App.Log(ex, "Análise de espaço");
            App.Notify.Error("Análise de espaço", ex.Message);
        }
        finally
        {
            if (id == _scanId) { IsScanning = false; IsBusy = false; BusyText = null; }
            _cts?.Dispose(); _cts = null;
        }
    }

    private void ShowProgress(SpaceScanProgress p)
    {
        ProgressText = $"{p.Files:N0} arquivos · {p.Folders:N0} pastas · {Fmt.Bytes(p.Bytes)}" +
            (p.Inaccessible > 0 ? $" · {p.Inaccessible:N0} inacessíveis" : "") + $" · {p.Elapsed.TotalSeconds:0} s";
        ProgressPath = p.CurrentPath ?? "";
    }

    [RelayCommand]
    private void Cancel()
    {
        if (IsElevatedRunning)
        {
            // O canal do agente não suporta cancelamento: abandona o resultado; o agente termina sozinho (com limite de tempo).
            _scanId++;
            StopElevatedUi();
            IsScanning = false; IsBusy = false; BusyText = null;
            App.Notify.Info("Análise elevada abandonada", "O RBM Agent terminará a varredura em segundo plano e o resultado será descartado. Uma nova varredura elevada só fica disponível depois disso.");
            return;
        }
        _cts?.Cancel();
    }

    [RelayCommand]
    private async Task ScanElevatedAsync()
    {
        if (IsScanning) return;
        if (_elevatedPending)
        {
            App.Notify.Info("Análise elevada", "Uma varredura elevada anterior ainda está terminando no RBM Agent. Tente novamente em instantes.");
            return;
        }
        var err = SpaceScanner.ValidatePath(TargetPath, requireLocalFixed: true, out var full);
        if (err is not null) { App.Notify.Warning("Análise como administrador", err); return; }
        if (!await App.Dialogs.ConfirmAsync("Escanear como administrador", ElevationExplanation,
                $"Pasta: {full}\nPode levar vários minutos em unidades grandes; o progresso detalhado não é transmitido pelo agente.", "Escanear como administrador"))
            return;

        int id = ++_scanId;
        IsScanning = true; IsBusy = true; BusyText = "Análise elevada em andamento…";
        IsElevatedRunning = true; _elevatedPending = true;
        ProgressPath = full;
        _elevatedClock = Stopwatch.StartNew();
        ProgressText = "Aguardando o RBM Agent (administrador)…";
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) => ProgressText = $"Varredura elevada no RBM Agent · {Fmt.Duration(_elevatedClock?.Elapsed ?? TimeSpan.Zero)} (sem progresso detalhado)";
        _elapsedTimer.Start();
        using var tracker = App.Tasks.Begin("Análise de espaço (administrador): " + full);
        try
        {
            var req = new StorageScanRequest(full, 50_000, UseAllocated, 25 * 60);
            // Sem CancellationToken: cancelar no meio deixaria o canal do agente dessincronizado.
            var resp = await AgentCall.InvokeAsync(AgentOps.StorageScan, req, "Análise de espaço (administrador)", TimeSpan.FromMinutes(30));
            if (id != _scanId) return; // abandonada
            var data = AgentCall.Read<StorageScanResponse>(resp);
            if (data is null) return;
            var r = await Task.Run(() => SpaceTreeCodec.FromResponse(data));
            ApplyResult(r);
            App.Notify.Success("Análise elevada concluída", $"{Fmt.Bytes(r.TotalBytes)} em {r.Files:N0} arquivos ({Fmt.Duration(r.Duration)}).");
        }
        catch (Exception ex)
        {
            App.Log(ex, "Análise de espaço elevada");
            if (id == _scanId) App.Notify.Error("Análise como administrador", ex.Message);
        }
        finally
        {
            _elevatedPending = false;
            if (id == _scanId) { StopElevatedUi(); IsScanning = false; IsBusy = false; BusyText = null; }
        }
    }

    private void StopElevatedUi()
    {
        _elapsedTimer?.Stop(); _elapsedTimer = null;
        IsElevatedRunning = false;
    }

    // ---------------- Resultado / navegação ----------------

    private void ApplyResult(SpaceScanResult r)
    {
        Result = r;
        TotalText = Fmt.Bytes(r.TotalBytes);
        ModeText = r.SizeMode == SpaceSizeMode.Allocated ? "espaço alocado em disco" : "tamanho lógico dos arquivos";
        FilesText = r.Files.ToString("N0", CultureInfo.CurrentCulture);
        FoldersText = r.Folders.ToString("N0", CultureInfo.CurrentCulture);
        HasInaccessible = r.InaccessibleCount > 0;
        InaccessibleText = r.InaccessibleCount.ToString("N0", CultureInfo.CurrentCulture);
        var tip = "Pastas que o Windows não deixou listar (acesso negado ou erro de leitura). O conteúdo delas NÃO está no total." +
                  (r.Elevated ? "" : " Use “Escanear como administrador” para incluir a maior parte delas.");
        if (r.InaccessiblePaths.Count > 0)
            tip += "\n\n" + string.Join("\n", r.InaccessiblePaths.Take(20)) + (r.InaccessibleCount > 20 ? $"\n… e mais {r.InaccessibleCount - 20:N0}" : "");
        InaccessibleTip = tip;
        double secs = r.Duration.TotalSeconds;
        DurationText = secs < 60 ? secs.ToString(secs < 10 ? "0.0" : "0", CultureInfo.CurrentCulture) + " s" : Fmt.Duration(r.Duration);
        SourceText = r.Elevated ? "Varredura elevada (administrador)" : "Varredura padrão (sem administrador)";
        var notes = new List<string>();
        if (r.LinkCount > 0) notes.Add($"{r.LinkCount:N0} junções/links não seguidos (contam 0 para não duplicar espaço)");
        if (r.Partial) notes.Add("varredura interrompida pelo limite de tempo — resultado parcial");
        if (r.Pruned) notes.Add("árvore resumida pelo agente: itens pequenos agrupados e pastas profundas sem detalhes");
        ResultNote = notes.Count == 0 ? "" : string.Join(" · ", notes);
        Navigate(r.Root);
        UpdateHero();
    }

    private void Navigate(SpaceNode node)
    {
        Selected = null;
        Current = node;
        Crumbs.Clear();
        var chain = new List<SpaceNode>();
        for (var n = node; n is not null; n = n.Parent) chain.Add(n);
        chain.Reverse();
        for (int i = 0; i < chain.Count; i++) Crumbs.Add(new SpaceCrumb(chain[i], chain[i].Name, i == chain.Count - 1));

        Items.Clear();
        int folderIdx = 0, idx = 0;
        foreach (var c in node.Children)
        {
            if (Items.Count >= MaxListItems) break;
            // Mesma regra de cor do mapa: pastas usam o índice entre as pastas visíveis (tamanho > 0).
            Items.Add(new SpaceItemRow(c, node.Size, c.IsFolder && c.Size > 0 ? folderIdx : 0, idx));
            if (c.IsFolder && c.Size > 0) folderIdx++;
            idx++;
        }
        CurrentTitle = node.Name;
        CurrentDetail = $"{Fmt.Bytes(node.Size)} · {node.FileCount:N0} arquivos · {node.FolderCount:N0} pastas" +
                        (node.Children.Length > MaxListItems ? $" · mostrando os {MaxListItems} maiores de {node.Children.Length:N0}" : "");
    }

    [RelayCommand]
    private void Drill(SpaceNode? node)
    {
        if (node is null || !node.CanDrill) return;
        Navigate(node);
    }

    [RelayCommand]
    private void Up()
    {
        if (Current?.Parent is { } p) Navigate(p);
    }

    [RelayCommand]
    private void GoCrumb(SpaceCrumb? c)
    {
        if (c is not null && !ReferenceEquals(c.Node, Current)) Navigate(c.Node);
    }

    partial void OnSelectedRowChanged(SpaceItemRow? value)
    {
        if (value is not null && !ReferenceEquals(value.Node, Selected)) Selected = value.Node;
    }

    partial void OnSelectedChanged(SpaceNode? value)
    {
        var row = value is null ? null : Items.FirstOrDefault(r => ReferenceEquals(r.Node, value));
        if (!ReferenceEquals(row, SelectedRow)) SelectedRow = row;
        HasSelection = value is not null;
        if (value is null) return;
        SelName = value.Name;
        SelPath = value.ExplorerPath;
        SelSize = value.IsInaccessible ? "Sem acesso (tamanho desconhecido)" : Fmt.Bytes(value.Size) + (Result?.SizeMode == SpaceSizeMode.Allocated ? " em disco" : "");
        long parent = value.Parent?.Size ?? 0;
        long total = Result?.TotalBytes ?? 0;
        SelShare = (parent > 0 ? (value.Size * 100.0 / parent).ToString("0.0", CultureInfo.CurrentCulture) + "% da pasta" : "") +
                   (total > 0 && value.Parent?.Parent is not null ? " · " + (value.Size * 100.0 / total).ToString("0.0", CultureInfo.CurrentCulture) + "% do total" : "");
        SelCounts = value.Kind == SpaceNodeKind.File ? "" : $"{value.FileCount:N0} arquivos · {value.FolderCount:N0} pastas";
        SelKind = value.Kind switch
        {
            SpaceNodeKind.Folder => value.IsInaccessible ? "Pasta inacessível" : value.IsTruncated ? "Pasta (detalhes não incluídos)" : "Pasta",
            SpaceNodeKind.File => FileCategories.Label(FileCategories.Classify(value.Name)),
            SpaceNodeKind.SmallFiles => "Arquivos menores agrupados",
            SpaceNodeKind.Link => "Junção/link simbólico (não seguido)",
            _ => "Itens agrupados",
        };
    }

    // ---------------- Ações (somente leitura) ----------------

    [RelayCommand]
    private void OpenSelectedInExplorer() => OpenInExplorer(Selected);

    [RelayCommand]
    private void OpenCurrentInExplorer() => OpenInExplorer(Current);

    private static void OpenInExplorer(SpaceNode? n)
    {
        if (n is null) return;
        var path = n.ExplorerPath;
        try
        {
            bool isFile = File.Exists(path), isDir = Directory.Exists(path);
            if (!isFile && !isDir)
            {
                App.Notify.Warning("Item não encontrado", "Ele pode ter sido movido ou apagado depois da análise: " + path);
                return;
            }
            // Pasta atual/agregado → abre a própria pasta; arquivo ou subpasta → abre a pasta-pai com o item selecionado.
            string args = isDir && (n.IsAggregate || n.Parent is null) ? $"\"{path}\"" : $"/select,\"{path}\"";
            Process.Start(new ProcessStartInfo("explorer.exe", args) { UseShellExecute = true });
        }
        catch (Exception ex) { App.Notify.Error("Não foi possível abrir o Explorer", ex.Message); }
    }

    [RelayCommand]
    private void GoCleanup() => App.Services.GetRequiredService<MainViewModel>().Navigate("cleanup");
}
