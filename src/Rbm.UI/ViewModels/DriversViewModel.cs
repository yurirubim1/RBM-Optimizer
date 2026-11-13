using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows.Data;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Diagnostics;
using Rbm.Data;
using Rbm.Drivers;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.SystemPages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

/// <summary>
/// Central de drivers: inventário (SetupAPI), fontes oficiais, Driver Store, backup/verificação, instalação e troca de versão
/// (via RBM.Agent), fotografias com diff, assistente NVIDIA e orientação DDU. Nada é removido nem executado automaticamente.
/// </summary>
public sealed partial class DriversViewModel : PageViewModel
{
    public const string AllClasses = "Todas as classes";
    private static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");
    private static readonly Guid DisplayClass = new("4d36e968-e325-11ce-bfc1-08002be10318");

    private readonly ISystemInventory _system;
    private readonly DeviceInventoryService _inventorySvc = new();
    private readonly DriverStoreService _storeSvc = new();
    private readonly DriverBackupService _backup = new();
    private readonly DriverInstallService _install = new();
    private DeviceInventory? _inventory;
    private DriverStoreInventory? _store;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _opCts;
    private bool _loaded;

    public override string Key => "drivers";

    public DriversViewModel(ISystemInventory system)
    {
        _system = system;
        DevicesView = CollectionViewSource.GetDefaultView(Devices);
        DevicesView.Filter = FilterDevice;
        PackagesView = CollectionViewSource.GetDefaultView(Packages);
        PackagesView.Filter = FilterPackage;
        PropertyChanged += (_, e) => { if (e.PropertyName == nameof(IsBusy) && _inventory is null) UpdateHero(); };
    }

    // ================= Abas =================
    /// <summary>Visão padrão: saúde geral + dispositivos com problema + pontos de atenção.</summary>
    [ObservableProperty] private bool _tabSummary = true;
    [ObservableProperty] private bool _tabDevices;
    [ObservableProperty] private bool _tabProblems;

    [RelayCommand] private void ShowSummary() => TabSummary = true;
    [RelayCommand] private void ShowAllDevices() => TabDevices = true;
    [RelayCommand] private void ShowBackup() => TabBackup = true;
    [RelayCommand] private void ShowHistory() => TabHistory = true;

    // ================= Resumo (hero) =================
    [ObservableProperty] private string _heroTitle = "Verificando seus dispositivos…";
    [ObservableProperty] private string _heroText = "Lendo dispositivos, drivers e as versões guardadas pelo Windows. Leva alguns segundos.";
    [ObservableProperty] private string _heroChipText = "Lendo";
    /// <summary>ok | warn | danger | info</summary>
    [ObservableProperty] private string _heroLevel = "info";
    [ObservableProperty] private bool _hasProblems;
    [ObservableProperty] private bool _selectedIsOk;
    public ObservableCollection<InfoRow> TechRows { get; } = new();

    private void UpdateHero()
    {
        if (_inventory is null)
        {
            HeroTitle = IsBusy ? "Verificando seus dispositivos…" : "Inventário ainda não lido";
            HeroText = IsBusy ? "Lendo dispositivos, drivers e as versões guardadas pelo Windows. Leva alguns segundos." : "Use “Atualizar inventário” para ler os dispositivos.";
            HeroChipText = IsBusy ? "Lendo" : "Sem dados"; HeroLevel = "info"; HasProblems = false;
            return;
        }
        int n = ProblemReports.Count;
        HasProblems = n > 0;
        HeroTitle = n == 0 ? "Nenhum dispositivo com problema" : n == 1 ? "1 dispositivo precisa de atenção" : $"{n} dispositivos precisam de atenção";
        var parts = new List<string> { $"{PresentCount} dispositivos conectados", $"{ThirdPartyCount} usam driver do fabricante (não vêm com o Windows)" };
        if (UnsignedCount > 0) parts.Add($"{UnsignedCount} sem assinatura digital válida");
        HeroText = string.Join(" · ", parts) + ". Antes de atualizar algum driver, faça um backup.";
        if (n > 0) { HeroLevel = "warn"; HeroChipText = n == 1 ? "1 problema" : $"{n} problemas"; }
        else if (UnsignedCount > 0) { HeroLevel = "warn"; HeroChipText = "Drivers sem assinatura"; }
        else { HeroLevel = "ok"; HeroChipText = "Tudo funcionando"; }
    }

    [ObservableProperty] private bool _tabStore;
    [ObservableProperty] private bool _tabBackup;
    [ObservableProperty] private bool _tabHistory;
    [ObservableProperty] private bool _tabNvidia;

    // ================= Resumo =================
    [ObservableProperty] private bool _hasInventory;
    [ObservableProperty] private string _inventoryInfo = "Inventário ainda não coletado.";
    [ObservableProperty] private string _storeInfo = "";
    [ObservableProperty] private int _presentCount;
    [ObservableProperty] private int _problemCount;
    [ObservableProperty] private int _thirdPartyCount;
    [ObservableProperty] private int _unsignedCount;
    [ObservableProperty] private int _packageCount;
    [ObservableProperty] private int _duplicateCount;
    [ObservableProperty] private string _visibleCountText = "";

    // ================= Dispositivos =================
    public ObservableCollection<DriverDeviceRow> Devices { get; } = new();
    public ICollectionView DevicesView { get; }
    public ObservableCollection<string> Classes { get; } = new() { AllClasses };
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private bool _filterProblems;
    [ObservableProperty] private bool _filterThirdParty;
    [ObservableProperty] private bool _filterUnsigned;
    [ObservableProperty] private bool _showNotPresent;
    [ObservableProperty] private string _selectedClass = AllClasses;

    partial void OnSearchTextChanged(string value) => RefreshDeviceFilter();
    partial void OnFilterProblemsChanged(bool value) => RefreshDeviceFilter();
    partial void OnFilterThirdPartyChanged(bool value) => RefreshDeviceFilter();
    partial void OnFilterUnsignedChanged(bool value) => RefreshDeviceFilter();
    partial void OnShowNotPresentChanged(bool value) => RefreshDeviceFilter();
    partial void OnSelectedClassChanged(string value) => RefreshDeviceFilter();

    // ---- Detalhes ----
    [ObservableProperty] private DriverDeviceRow? _selectedDevice;
    [ObservableProperty] private bool _hasSelection;
    [ObservableProperty] private string? _problemTitle;
    [ObservableProperty] private string? _problemAction;
    [ObservableProperty] private string _guidanceCategory = "";
    public ObservableCollection<InfoRow> DetailRows { get; } = new();
    public ObservableCollection<string> HardwareIds { get; } = new();
    public ObservableCollection<string> CompatibleIds { get; } = new();
    public ObservableCollection<OfficialSource> Sources { get; } = new();
    public ObservableCollection<CatalogSearchLink> CatalogLinks { get; } = new();
    public ObservableCollection<string> GuidanceNotes { get; } = new();

    // ---- Troca de versão ----
    public ObservableCollection<DriverCandidateRow> Candidates { get; } = new();
    [ObservableProperty] private string? _candidatesStatus;
    [ObservableProperty] private bool _canSearchCandidates;
    [ObservableProperty] private string? _candidatesUnavailableReason;

    // ================= Problemas =================
    public ObservableCollection<UnknownDeviceReport> ProblemReports { get; } = new();
    public ObservableCollection<Finding> Findings { get; } = new();

    // ================= Driver Store =================
    public ObservableCollection<DriverPackageRow> Packages { get; } = new();
    public ICollectionView PackagesView { get; }
    public ObservableCollection<DriverDuplicateRow> Duplicates { get; } = new();
    [ObservableProperty] private string _storeSearch = "";
    [ObservableProperty] private bool _storeOnlyUnused;
    [ObservableProperty] private string _storeSummary = "";
    partial void OnStoreSearchChanged(string value) => PackagesView.Refresh();
    partial void OnStoreOnlyUnusedChanged(bool value) => PackagesView.Refresh();

    // ================= Backup / instalação =================
    public ObservableCollection<string> BackupMessages { get; } = new();
    [ObservableProperty] private bool _isExporting;
    [ObservableProperty] private string? _backupStatus;
    [ObservableProperty] private string? _lastBackupFolder;
    [ObservableProperty] private bool _offerAgentExport;
    [ObservableProperty] private string? _pendingExportFolder;

    public ObservableCollection<string> VerifyMessages { get; } = new();
    [ObservableProperty] private bool _isVerifying;
    [ObservableProperty] private string? _verifyStatus;
    [ObservableProperty] private bool? _verifyOk;

    public ObservableCollection<string> InstallMessages { get; } = new();
    [ObservableProperty] private string? _installInfPath;
    [ObservableProperty] private bool _installValid;
    [ObservableProperty] private string? _installHeaderText;
    [ObservableProperty] private string? _installSignatureText;
    [ObservableProperty] private bool _isInstalling;
    public bool CanInstall => InstallValid && !IsInstalling;
    public bool HasInstallPath => InstallInfPath is not null;
    partial void OnInstallValidChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnIsInstallingChanged(bool value) => OnPropertyChanged(nameof(CanInstall));
    partial void OnInstallInfPathChanged(string? value) => OnPropertyChanged(nameof(HasInstallPath));

    // ================= Histórico =================
    public ObservableCollection<DriverSnapshotItem> Snapshots { get; } = new();
    public ObservableCollection<DriverSnapshotItem> CompareTargets { get; } = new();
    public ObservableCollection<DriverChangeRow> DiffRows { get; } = new();
    [ObservableProperty] private DriverSnapshotItem? _selectedSnapshot;
    [ObservableProperty] private DriverSnapshotItem? _selectedCompareTarget;
    [ObservableProperty] private string? _diffSummary;
    [ObservableProperty] private string _historyStatus = "";

    // ================= NVIDIA / DDU =================
    public ObservableCollection<NvidiaComponentRow> NvidiaComponents { get; } = new();
    public ObservableCollection<string> NvidiaRecommendations { get; } = new();
    public ObservableCollection<string> NvidiaWarnings { get; } = new();
    [ObservableProperty] private bool _hasNvidiaReport;
    [ObservableProperty] private bool _isAnalyzingNvidia;
    [ObservableProperty] private string? _nvidiaSummary;
    [ObservableProperty] private string _nvidiaGpuText = "";

    public ObservableCollection<string> DduReasons { get; } = new();
    public IReadOnlyList<string> DduSteps => DduAdvisor.Steps;
    public IReadOnlyList<string> DduWarnings => DduAdvisor.Warnings;
    public string DduOfficialPage => DduAdvisor.OfficialPage;
    public string DduDownloadPage => DduAdvisor.OfficialDownloadPage;
    [ObservableProperty] private bool _dduAdvisable;
    [ObservableProperty] private object _dduLevel = Severity.Info;
    [ObservableProperty] private string _dduVerdict = "Aguardando inventário.";
    [ObservableProperty] private bool _userReportsIssues;
    partial void OnUserReportsIssuesChanged(bool value) => EvaluateDdu();

    public string NewestNotAlwaysBest => OfficialSources.NewestNotAlwaysBestPt;
    public bool AgentConnected => AgentCall.IsConnected;

    // ================= Ciclo de vida =================
    public override async Task OnNavigatedToAsync()
    {
        OnPropertyChanged(nameof(AgentConnected));
        if (!_loaded) { _loaded = true; await LoadAsync(); await LoadSnapshotsAsync(); }
    }

    public override void OnNavigatedFrom()
    {
        // Leituras longas (inventário, análise de pacote) são canceladas ao sair; operações elevadas em curso não.
        _loadCts?.Cancel();
        if (IsBusy) _loaded = false;
    }

    [RelayCommand]
    private Task Refresh() => LoadAsync();

    private async Task LoadAsync()
    {
        _loadCts?.Cancel();
        _loadCts = new CancellationTokenSource();
        var ct = _loadCts.Token;
        await RunBusy("Lendo dispositivos, drivers e Driver Store…", async () =>
        {
            using var _ = App.Tasks.Begin("Inventário de drivers");
            try
            {
                var storeTask = _storeSvc.EnumerateAsync(ct);
                var inv = await _inventorySvc.CollectAsync(InventoryOptions.Default, ct);
                _inventory = inv;
                ApplyInventory();
                try { _store = await storeTask; }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _store = null;
                    App.Log(ex, "Driver Store");
                    StoreInfo = "Driver Store indisponível: " + ex.Message;
                }
                ApplyStore();
                ApplyProblemsAndFindings();
                EvaluateDdu();
                UpdateCurrentCompareTarget();
            }
            catch (OperationCanceledException) { _loaded = false; }
        });
    }

    private void ApplyInventory()
    {
        var inv = _inventory!;
        var keep = SelectedDevice?.Device.InstanceId;
        Devices.Clear();
        foreach (var d in inv.Devices.OrderBy(d => d.ClassName ?? "~", StringComparer.CurrentCultureIgnoreCase).ThenBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
            Devices.Add(new DriverDeviceRow(d));

        var cls = inv.Devices.Select(d => d.ClassName).OfType<string>().Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(c => c, StringComparer.CurrentCultureIgnoreCase).ToList();
        var sel = SelectedClass;
        Classes.Clear(); Classes.Add(AllClasses);
        foreach (var c in cls) Classes.Add(c);
        SelectedClass = Classes.Contains(sel) ? sel : AllClasses;

        PresentCount = inv.Present.Count();
        ProblemCount = inv.ProblemDevices.Count(d => !d.IsDisabled);
        ThirdPartyCount = inv.Present.Count(d => d.Driver?.IsThirdParty == true);
        UnsignedCount = inv.Present.Count(d => d.Driver?.Signature.State is DriverSignatureState.Unsigned or DriverSignatureState.Invalid);
        HasInventory = true;
        InventoryInfo = $"{inv.Devices.Count} dispositivos ({PresentCount} conectados) · fonte {inv.Source} · {inv.Elapsed.TotalSeconds:0.0} s · {inv.CollectedAt.ToLocalTime():HH:mm:ss}"
            + (inv.Warnings.Count > 0 ? " · avisos: " + string.Join(" | ", inv.Warnings) : "");
        RefreshDeviceFilter();
        SelectedDevice = keep is null ? null : Devices.FirstOrDefault(r => string.Equals(r.Device.InstanceId, keep, StringComparison.OrdinalIgnoreCase));
    }

    private void ApplyStore()
    {
        Packages.Clear(); Duplicates.Clear();
        if (_store is null || _inventory is null)
        {
            PackageCount = 0; DuplicateCount = 0;
            StoreSummary = StoreInfo.Length > 0 ? StoreInfo : "Driver Store não lido.";
            UpdateCandidateAvailability();
            return;
        }
        var an = DriverStoreService.Analyze(_inventory, _store);
        var oldDup = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var g in an.Duplicates)
        {
            foreach (var p in g.Packages.Skip(1))
                if (!g.InUsePublishedNames.Contains(p.PublishedName, StringComparer.OrdinalIgnoreCase)) oldDup.Add(p.PublishedName);
            var lines = g.Packages.Select((p, i) =>
                $"{p.PublishedName} · v{p.Version ?? "?"}" + (p.Date is { } d ? $" ({d.ToString("dd/MM/yyyy", Pt)})" : "") +
                (i == 0 ? " · mais nova" : "") +
                (g.InUsePublishedNames.Contains(p.PublishedName, StringComparer.OrdinalIgnoreCase) ? " · EM USO" : "")).ToList();
            Duplicates.Add(new DriverDuplicateRow(g.OriginalName, $"{g.Packages.Count} versões no store · {g.InUsePublishedNames.Count} em uso", lines));
        }
        foreach (var p in _store.Packages.OrderBy(p => p.ClassName ?? "~").ThenBy(p => p.OriginalName ?? p.PublishedName))
        {
            int n = an.DevicesByPackage.TryGetValue(p.PublishedName, out var devs) ? devs.Count : 0;
            Packages.Add(new DriverPackageRow(p, n, oldDup.Contains(p.PublishedName)));
        }
        PackageCount = _store.Packages.Count;
        DuplicateCount = an.Duplicates.Count;
        StoreInfo = $"{_store.Packages.Count} pacotes de terceiros · fonte {_store.Source}" + (_store.Warnings.Count > 0 ? " · " + string.Join(" | ", _store.Warnings) : "");
        StoreSummary = $"{_store.Packages.Count} pacotes · {an.Duplicates.Count} drivers com mais de uma versão · {an.Unreferenced.Count} sem dispositivo associado · {oldDup.Count} versões antigas sem uso.";
        PackagesView.Refresh();
        UpdateCandidateAvailability();
    }

    private void ApplyProblemsAndFindings()
    {
        ProblemReports.Clear(); Findings.Clear();
        if (_inventory is null) return;
        foreach (var r in OfficialSources.UnknownAndProblemDevices(_inventory, BoardManufacturer)) ProblemReports.Add(r);
        foreach (var f in DriverDiagnostics.Analyze(_inventory, _store).OrderByDescending(f => f.Severity)) Findings.Add(f);
        UpdateHero();
    }

    private string? BoardManufacturer => _system.Current?.Board.Manufacturer;

    // ================= Filtros =================
    private bool FilterDevice(object o)
    {
        if (o is not DriverDeviceRow r) return false;
        var d = r.Device;
        if (!ShowNotPresent && !d.IsPresent) return false;
        if (FilterProblems && !r.HasProblem) return false;
        if (FilterThirdParty && !r.IsThirdParty) return false;
        if (FilterUnsigned && !r.IsUnsigned) return false;
        if (!string.IsNullOrEmpty(SelectedClass) && SelectedClass != AllClasses && !string.Equals(d.ClassName, SelectedClass, StringComparison.OrdinalIgnoreCase)) return false;
        var q = SearchText?.Trim();
        return string.IsNullOrEmpty(q) || r.SearchBlob.Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    private void RefreshDeviceFilter()
    {
        DevicesView.Refresh();
        int visible = DevicesView.Cast<object>().Count();
        VisibleCountText = $"{visible} de {Devices.Count} exibidos";
    }

    private bool FilterPackage(object o)
    {
        if (o is not DriverPackageRow r) return false;
        if (StoreOnlyUnused && r.DeviceCount > 0) return false;
        var q = StoreSearch?.Trim();
        if (string.IsNullOrEmpty(q)) return true;
        return (r.PublishedName + " " + r.OriginalName + " " + r.Provider + " " + r.ClassName + " " + r.Version).Contains(q, StringComparison.OrdinalIgnoreCase);
    }

    [RelayCommand]
    private void ClearFilters()
    {
        SearchText = ""; FilterProblems = false; FilterThirdParty = false; FilterUnsigned = false; ShowNotPresent = false; SelectedClass = AllClasses;
    }

    [RelayCommand]
    private void ShowDevice(DeviceRecord? d)
    {
        if (d is null) return;
        ClearFilters();
        if (!d.IsPresent) ShowNotPresent = true;
        SelectedDevice = Devices.FirstOrDefault(r => string.Equals(r.Device.InstanceId, d.InstanceId, StringComparison.OrdinalIgnoreCase));
        TabDevices = true;
    }

    // ================= Detalhes =================
    partial void OnSelectedDeviceChanged(DriverDeviceRow? value)
    {
        DetailRows.Clear(); TechRows.Clear(); HardwareIds.Clear(); CompatibleIds.Clear(); Sources.Clear(); CatalogLinks.Clear(); GuidanceNotes.Clear();
        Candidates.Clear(); CandidatesStatus = null;
        HasSelection = value is not null;
        ProblemTitle = null; ProblemAction = null;
        SelectedIsOk = value is not null && value.Device.IsPresent && !value.HasProblem && !value.Device.IsDisabled;
        if (value is null) { UpdateCandidateAvailability(); return; }
        var d = value.Device;
        var drv = d.Driver;
        // Informações em linguagem simples
        DetailRows.Add(new("Situação", value.StatusText, d.IsPresent ? (d.IsStarted ? "Conectado e funcionando" : "Conectado") : (d.LastArrival is { } la ? $"Última conexão: {SysUi.When(la)}" : "Não conectado")));
        DetailRows.Add(new("Tipo", d.ClassName ?? "—"));
        DetailRows.Add(new("Fabricante", d.Manufacturer ?? "—", d.PrimaryHardwareId?.VendorName is { } vn ? $"Chip (pelo ID): {vn}" : null));
        if (drv is null)
            DetailRows.Add(new("Driver", "Nenhum driver instalado"));
        else
        {
            DetailRows.Add(new("Driver de", drv.Provider ?? "—", drv.IsThirdParty ? "Instalado pelo fabricante (não veio com o Windows)" : "Driver que já vem com o Windows"));
            DetailRows.Add(new("Versão", drv.Version ?? "—", drv.Date is { } dt ? $"Data: {dt.ToString("dd/MM/yyyy", Pt)}" : null));
            DetailRows.Add(new("Assinatura", drv.Signature.SignerKindPt, drv.Signature.Signer));
        }
        // Detalhes técnicos
        TechRows.Add(new("ID de instância", d.InstanceId));
        if (d.ClassGuid is { } cg) TechRows.Add(new("GUID da classe", cg.ToString("B")));
        if (d.Service is { } svc) TechRows.Add(new("Serviço", svc));
        if (d.LocationInfo is { } loc) TechRows.Add(new("Local", loc));
        if (drv is not null)
        {
            TechRows.Add(new("INF", drv.InfName, drv.InfPath));
            var sigDetail = string.Join(" · ", new[] { drv.Signature.CatalogFile, drv.Signature.Detail }.Where(s => !string.IsNullOrWhiteSpace(s)));
            if (sigDetail.Length > 0) TechRows.Add(new("Catálogo", sigDetail));
            if (drv.MatchingDeviceId is { } mid) TechRows.Add(new("ID correspondente", mid, drv.InfSection is { } sec ? $"Seção {sec}" + (drv.Rank is { } rk ? $" · rank 0x{rk:X}" : "") : null));
        }
        foreach (var h in d.HardwareIds) HardwareIds.Add(h);
        foreach (var c in d.CompatibleIds) CompatibleIds.Add(c);
        if (d.ProblemCode != 0)
        {
            ProblemTitle = d.Problem is { } p ? $"Código {p.Code} ({p.Symbol}): {p.DescriptionPt}" : $"Código de problema {d.ProblemCode}";
            ProblemAction = d.Problem?.SuggestedActionPt;
        }
        else if (d.IsUnknown)
        {
            ProblemTitle = "Dispositivo desconhecido: nenhum driver instalado.";
            ProblemAction = "Procure o driver pelo ID de hardware no Catálogo do Microsoft Update ou no site do fabricante (links abaixo).";
        }
        var g = OfficialSources.GuidanceFor(d, BoardManufacturer);
        GuidanceCategory = $"Categoria: {CategoryPt(g.Category)}" + (g.ChipVendor is { } cv ? $" · chip: {cv}" : "") + (g.BoardVendor is { } bv ? $" · placa/OEM: {bv}" : "");
        foreach (var s in g.Sources) Sources.Add(s);
        foreach (var l in OfficialSources.CatalogSearches(d)) CatalogLinks.Add(l);
        foreach (var n in g.NotesPt.Where(n => n != OfficialSources.NewestNotAlwaysBestPt)) GuidanceNotes.Add(n);
        UpdateCandidateAvailability();
    }

    private static string CategoryPt(DriverCategory c) => c switch
    {
        DriverCategory.Gpu => "Placa de vídeo", DriverCategory.Chipset => "Chipset", DriverCategory.Network => "Rede cabeada",
        DriverCategory.WiFi => "Wi-Fi", DriverCategory.Audio => "Áudio", DriverCategory.Bluetooth => "Bluetooth",
        DriverCategory.Storage => "Armazenamento", DriverCategory.Peripheral => "Periférico", DriverCategory.Motherboard => "Placa-mãe",
        _ => "Outros",
    };

    private void UpdateCandidateAvailability()
    {
        var d = SelectedDevice?.Device;
        string? reason = d is null ? null
            : !d.IsPresent ? "A troca de versão só é possível com o dispositivo conectado."
            : _store is null ? "Driver Store não lido; atualize o inventário."
            : _store.Source != "pnputil-xml" ? "Este Windows não fornece o pnputil em XML: a pasta dos pacotes no store é desconhecida, então a troca de versão não está disponível."
            : null;
        CandidatesUnavailableReason = reason;
        CanSearchCandidates = d is not null && reason is null;
    }

    [RelayCommand]
    private void OpenUrl(string? url) => SysUi.OpenUrl(url);

    [RelayCommand]
    private void OpenFolder(string? path) => SysUi.OpenFolder(path);

    [RelayCommand]
    private void CopyText(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { System.Windows.Clipboard.SetText(text); App.Notify.Info("Copiado", text.Length > 80 ? text[..80] + "…" : text); }
        catch (Exception ex) { App.Notify.Error("Área de transferência indisponível", ex.Message); }
    }

    // ================= Troca de versão (driver.switch) =================
    [RelayCommand]
    private async Task FindAlternatives()
    {
        var row = SelectedDevice;
        if (row is null || _store is null || !CanSearchCandidates) return;
        Candidates.Clear();
        CandidatesStatus = "Testando pacotes do Driver Store com a correspondência real do PnP…";
        _opCts?.Cancel(); _opCts = new CancellationTokenSource();
        try
        {
            using var _ = App.Tasks.Begin("Procurando versões alternativas");
            var list = await _install.FindAlternativeVersionsAsync(row.Device, _store, _opCts.Token);
            if (SelectedDevice != row) return;
            foreach (var c in list) Candidates.Add(new DriverCandidateRow(c));
            CandidatesStatus = list.Count == 0
                ? "Nenhuma outra versão compatível no Driver Store. Para voltar a uma versão antiga, instale-a a partir de um backup ou do pacote oficial."
                : $"{list.Count} versão(ões) compatível(is) no Driver Store. A troca afeta somente este dispositivo.";
        }
        catch (OperationCanceledException) { CandidatesStatus = null; }
        catch (Exception ex) { CandidatesStatus = "Falha: " + ex.Message; App.Log(ex, "FindAlternativeVersions"); }
    }

    [RelayCommand]
    private async Task SwitchTo(DriverCandidateRow? cand)
    {
        var row = SelectedDevice;
        if (cand is null || row is null) return;
        var c = cand.Candidate;
        var preview = await Task.Run(() => c.Package.StoreInfPath is { } inf ? DriverInstallService.PreviewSwitch(c.InstanceId, inf) : new SwitchPreview(false, null, "Caminho do pacote no store desconhecido."));
        if (!preview.Possible)
        {
            App.Notify.Warning("Troca de versão indisponível", preview.MessagePt);
            return;
        }
        string details =
            $"Dispositivo: {row.Name}\nAtual: {c.Current?.InfName ?? "—"} v{c.Current?.Version ?? "?"}\nNovo: {c.Package.PublishedName} v{c.Node.Version} ({c.Node.Provider})\n\n" +
            "Riscos: a tela pode piscar ou o dispositivo ficar indisponível por alguns segundos; o Windows pode pedir reinicialização; " +
            "o Windows Update pode reinstalar a versão mais nova depois. Recomendado: exportar um backup e criar um ponto de restauração antes.\n" +
            "Para desfazer, repita a troca escolhendo o pacote atual (" + (c.Current?.InfName ?? "—") + ").";
        if (!await App.Dialogs.ConfirmAsync("Trocar versão do driver", $"Aplicar o pacote {c.Package.PublishedName} a \"{row.Name}\" via RBM Agent (administrador)?",
                details, "Trocar driver", danger: true, requiredPhrase: "TROCAR"))
            return;

        IsBusy = true; BusyText = "Trocando o driver (RBM Agent)…";
        try
        {
            using var _ = App.Tasks.Begin("Troca de versão de driver");
            var resp = await AgentCall.InvokeAsync(AgentOps.DriverSwitch, new DriverSwitchRequest(c.InstanceId, c.Package.PublishedName), "Trocar versão do driver", TimeSpan.FromMinutes(6));
            OnPropertyChanged(nameof(AgentConnected));
            if (resp is null) { await SysUi.AuditAsync("drivers.switch", $"{c.InstanceId} → {c.Package.PublishedName}: não executado (agente indisponível/recusou)", false); return; }
            var r = AgentCall.Read<DriverSwitchResponse>(resp);
            bool ok = r?.Success == true;
            string msg = r is null ? "Resposta vazia do agente." : string.Join(" ", r.Messages);
            await SysUi.AuditAsync("drivers.switch", $"{row.Name} [{c.InstanceId}]: {r?.Before?.InfName} v{r?.Before?.Version} → {r?.After?.InfName} v{r?.After?.Version}. {msg}", ok);
            if (ok)
            {
                App.Notify.Success("Driver trocado", $"{row.Name}: agora {r!.After?.InfName} v{r.After?.Version}." + (r.RebootRequired ? " Reinicie o Windows para concluir." : ""));
                await LoadAsync();
            }
            else App.Notify.Error("A troca não foi concluída", msg);
        }
        finally { IsBusy = false; BusyText = null; }
    }

    // ================= Backup (export / verify) =================
    [RelayCommand]
    private async Task ExportBackup()
    {
        var folder = SysUi.PickFolder("Escolha a pasta onde o backup dos drivers será criado");
        if (folder is null) return;
        BackupMessages.Clear(); OfferAgentExport = false; PendingExportFolder = null; LastBackupFolder = null;
        IsExporting = true; BackupStatus = "Exportando pacotes de terceiros (pnputil /export-driver)…";
        _opCts?.Cancel(); _opCts = new CancellationTokenSource();
        var progress = new Progress<string>(AddBackupMessage);
        try
        {
            using var _ = App.Tasks.Begin("Backup de drivers");
            var r = await _backup.ExportAsync(folder, null, progress, _opCts.Token);
            foreach (var m in r.Messages) AddBackupMessage(m);
            await SysUi.AuditAsync("drivers.export", $"{folder}: {r.ExportedCount} exportado(s), {r.FailedCount} falha(s) → {r.BackupFolder}", r.Success);
            if (r.Success)
            {
                LastBackupFolder = r.BackupFolder;
                BackupStatus = $"Backup concluído: {r.ExportedCount} pacote(s) exportado(s)" + (r.FailedCount > 0 ? $", {r.FailedCount} falha(s)" : "") + ". Use \"Verificar backup\" para conferir a integridade.";
                App.Notify.Success("Backup de drivers concluído", $"{r.ExportedCount} pacote(s) em {r.BackupFolder}");
            }
            else
            {
                BackupStatus = $"O backup sem privilégios falhou ({r.ExportedCount} exportado(s), {r.FailedCount} falha(s)). Você pode tentar novamente com o RBM Agent (administrador).";
                PendingExportFolder = folder;
                OfferAgentExport = true;
            }
        }
        catch (OperationCanceledException) { BackupStatus = "Backup cancelado."; }
        catch (Exception ex) { BackupStatus = "Falha no backup: " + ex.Message; App.Log(ex, "Backup de drivers"); PendingExportFolder = folder; OfferAgentExport = true; }
        finally { IsExporting = false; }
    }

    private void AddBackupMessage(string m)
    {
        BackupMessages.Add(m);
        while (BackupMessages.Count > 300) BackupMessages.RemoveAt(0);
    }

    [RelayCommand]
    private async Task ExportViaAgent()
    {
        var folder = PendingExportFolder;
        if (folder is null) return;
        if (!await App.Dialogs.ConfirmAsync("Backup via RBM Agent", "Repetir a exportação com privilégios de administrador?",
                $"Destino: {folder}\nSomente leitura do Driver Store: nada é alterado no sistema. O agente valida a pasta (local fixa, fora de pastas do sistema, sem links).",
                "Exportar como administrador"))
            return;
        IsExporting = true; BackupStatus = "Exportando via RBM Agent…";
        try
        {
            using var _ = App.Tasks.Begin("Backup de drivers (agente)");
            var resp = await AgentCall.InvokeAsync(AgentOps.DriverExport, new DriverExportRequest(folder, null), "Backup via agente", TimeSpan.FromMinutes(6));
            OnPropertyChanged(nameof(AgentConnected));
            if (resp is null) { BackupStatus = "Backup via agente não executado."; return; }
            var r = AgentCall.Read<DriverExportResponse>(resp);
            if (r is null) { BackupStatus = "Resposta vazia do agente."; return; }
            foreach (var m in r.Messages) AddBackupMessage(m);
            await SysUi.AuditAsync("drivers.export.agent", $"{folder}: {r.Exported} exportado(s), {r.Failed} falha(s) → {r.BackupFolder}", r.Success);
            if (r.Success)
            {
                LastBackupFolder = r.BackupFolder; OfferAgentExport = false;
                BackupStatus = $"Backup concluído via agente: {r.Exported} pacote(s)" + (r.Failed > 0 ? $", {r.Failed} falha(s)" : "") + ".";
                App.Notify.Success("Backup de drivers concluído", r.BackupFolder ?? folder);
            }
            else BackupStatus = $"O agente também não concluiu o backup ({r.Failed} falha(s)). Veja as mensagens abaixo.";
        }
        finally { IsExporting = false; }
    }

    [RelayCommand]
    private async Task VerifyBackup()
    {
        var folder = SysUi.PickFolder("Escolha a pasta do backup (RBM-Drivers-…) a verificar", LastBackupFolder);
        if (folder is null) return;
        VerifyMessages.Clear(); VerifyOk = null;
        IsVerifying = true; VerifyStatus = "Conferindo hashes SHA-256 e catálogos assinados…";
        try
        {
            using var _ = App.Tasks.Begin("Verificação de backup de drivers");
            var r = await _backup.VerifyAsync(folder);
            int missing = r.Files.Count(f => !f.Exists), bad = r.Files.Count(f => f.Exists && !f.HashMatches);
            int untrusted = r.Packages.Count(p => !p.IsTrusted);
            VerifyOk = r.Success;
            VerifyStatus = (r.Success ? "Backup íntegro. " : "Backup com problemas. ") +
                $"{r.Files.Count} arquivo(s) no manifesto · {missing} ausente(s) · {bad} com hash divergente · {r.Packages.Count} pacote(s), {untrusted} sem catálogo confiável · {r.UnlistedFiles.Count} arquivo(s) fora do manifesto" +
                (r.Manifest is { } m ? $" · criado em {SysUi.When(m.CreatedAt)} em {m.MachineName}" : "");
            foreach (var f in r.Files.Where(f => !f.Exists || !f.HashMatches).Take(50)) VerifyMessages.Add((f.Exists ? "Hash divergente: " : "Ausente: ") + f.RelativePath);
            foreach (var p in r.Packages.Where(p => !p.IsTrusted).Take(50)) VerifyMessages.Add($"{Path.GetFileName(p.InfPath)}: {p.MessagePt}");
            foreach (var u in r.UnlistedFiles.Take(30)) VerifyMessages.Add("Fora do manifesto: " + u);
            foreach (var msg in r.Messages) VerifyMessages.Add(msg);
        }
        catch (Exception ex) { VerifyOk = false; VerifyStatus = "Falha na verificação: " + ex.Message; App.Log(ex, "Verificar backup"); }
        finally { IsVerifying = false; }
    }

    // ================= Instalação de INF (driver.install) =================
    [RelayCommand]
    private async Task PickInf()
    {
        var path = SysUi.PickFile("Escolha o arquivo .inf do pacote de driver (extraído do pacote oficial)", "Informações de instalação (*.inf)|*.inf");
        if (path is null) return;
        InstallInfPath = path; InstallValid = false; InstallMessages.Clear(); InstallHeaderText = null; InstallSignatureText = "Validando…";
        try
        {
            var v = await Task.Run(() => DriverInstallService.ValidateForInstall(path));
            InstallValid = v.Ok;
            if (v.Header is { } h)
                InstallHeaderText = $"Classe {h.Class ?? "—"} · provedor {h.Provider ?? "—"} · versão {h.Version ?? "—"}" +
                    (h.Date is { } d ? $" ({d.ToString("dd/MM/yyyy", Pt)})" : "") + $" · catálogo {h.CatalogFile ?? "não declarado"}";
            InstallSignatureText = v.Signature?.MessagePt ?? (v.Ok ? "Assinatura verificada." : "Assinatura não verificada.");
            foreach (var m in v.Messages) InstallMessages.Add(m);
        }
        catch (Exception ex) { InstallValid = false; InstallSignatureText = "Falha na validação: " + ex.Message; App.Log(ex, "ValidateForInstall"); }
    }

    [RelayCommand]
    private async Task InstallInf()
    {
        var path = InstallInfPath;
        if (path is null || !InstallValid) return;
        string details =
            $"INF: {path}\n{InstallHeaderText}\n{InstallSignatureText}\n\n" +
            "Riscos: o pacote é adicionado ao Driver Store e instalado nos dispositivos compatíveis (pnputil /add-driver /install). " +
            "Vídeo, rede ou áudio podem ficar indisponíveis por alguns segundos; pode ser necessário reiniciar. Um driver inadequado pode causar instabilidade.\n" +
            "Antes: exporte um backup (aba atual) e crie um ponto de restauração. Para desfazer, use \"Procurar versões\" no dispositivo e volte ao pacote anterior.";
        if (!await App.Dialogs.ConfirmAsync("Instalar driver", "Instalar este pacote de driver via RBM Agent (administrador)?", details, "Instalar", danger: true))
            return;
        IsInstalling = true; InstallMessages.Clear();
        try
        {
            using var _ = App.Tasks.Begin("Instalando driver");
            var resp = await AgentCall.InvokeAsync(AgentOps.DriverInstall, new DriverInstallRequest(path), "Instalar driver", TimeSpan.FromMinutes(12));
            OnPropertyChanged(nameof(AgentConnected));
            if (resp is null) { await SysUi.AuditAsync("drivers.install", $"{path}: não executado (agente indisponível/recusou)", false); return; }
            var r = AgentCall.Read<DriverInstallResponse>(resp);
            bool ok = r?.Success == true;
            if (r is not null)
            {
                foreach (var m in r.Messages) InstallMessages.Add(m);
                foreach (var c in r.DeviceChanges) InstallMessages.Add($"{c.DeviceName}: {c.DescriptionPt}");
            }
            await SysUi.AuditAsync("drivers.install", $"{path} → {r?.PublishedName ?? "—"} (código {r?.ExitCode}); {r?.DeviceChanges.Count ?? 0} dispositivo(s) alterado(s)", ok);
            if (ok)
            {
                App.Notify.Success("Driver instalado", $"Pacote {r!.PublishedName ?? ""} instalado." + (r.RebootRequired ? " Reinicie o Windows para concluir." : ""));
                await LoadAsync();
            }
            else App.Notify.Error("Instalação não concluída", r is null ? "Resposta vazia do agente." : $"Código {r.ExitCode}. Veja as mensagens na página.");
        }
        finally { IsInstalling = false; }
    }

    // ================= Histórico (fotografias) =================
    private async Task LoadSnapshotsAsync()
    {
        try
        {
            var list = await SysUi.Db.ListAsync<DriverSnapshot>(Collections.DriverSnapshots, 60);
            Snapshots.Clear();
            foreach (var s in list.OrderByDescending(s => s.CapturedAt))
                Snapshots.Add(new DriverSnapshotItem($"{SysUi.When(s.CapturedAt)} · {s.Entries.Count} entradas · {s.MachineName}", s, false));
            HistoryStatus = Snapshots.Count == 0 ? "Nenhuma fotografia salva ainda. Salve uma antes de atualizar drivers para comparar depois." : $"{Snapshots.Count} fotografia(s) salva(s).";
            UpdateCurrentCompareTarget();
            SelectedSnapshot ??= Snapshots.FirstOrDefault();
        }
        catch (Exception ex) { HistoryStatus = "Falha ao ler o histórico: " + ex.Message; App.Log(ex, "Driver snapshots"); }
    }

    private void UpdateCurrentCompareTarget()
    {
        var prevSel = SelectedCompareTarget;
        CompareTargets.Clear();
        if (_inventory is not null)
            CompareTargets.Add(new DriverSnapshotItem($"Estado atual ({_inventory.CollectedAt.ToLocalTime():HH:mm})", DriverSnapshot.FromInventory(_inventory), true));
        foreach (var s in Snapshots) CompareTargets.Add(s);
        SelectedCompareTarget = prevSel is { IsCurrent: false } ? CompareTargets.FirstOrDefault(t => t.Snapshot.CapturedAt == prevSel.Snapshot.CapturedAt) ?? CompareTargets.FirstOrDefault() : CompareTargets.FirstOrDefault();
    }

    [RelayCommand]
    private async Task SaveSnapshot()
    {
        if (_inventory is null) { App.Notify.Warning("Sem inventário", "Aguarde a leitura dos dispositivos."); return; }
        try
        {
            var snap = DriverSnapshot.FromInventory(_inventory);
            await SysUi.Db.UpsertAsync(Collections.DriverSnapshots, snap.CapturedAt.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture), snap, createdAt: snap.CapturedAt);
            App.Notify.Success("Fotografia salva", $"{snap.Entries.Count} dispositivos/drivers registrados.");
            await LoadSnapshotsAsync();
            SelectedSnapshot = Snapshots.FirstOrDefault(s => s.Snapshot.CapturedAt == snap.CapturedAt);
        }
        catch (Exception ex) { App.Notify.Error("Falha ao salvar a fotografia", ex.Message); App.Log(ex, "SaveSnapshot"); }
    }

    [RelayCommand]
    private void CompareSnapshots()
    {
        var a = SelectedSnapshot; var b = SelectedCompareTarget;
        DiffRows.Clear();
        if (a is null || b is null) { DiffSummary = "Selecione a fotografia e o alvo da comparação."; return; }
        if (!b.IsCurrent && a.Snapshot.CapturedAt == b.Snapshot.CapturedAt) { DiffSummary = "Escolha duas fotografias diferentes."; return; }
        var (older, newer) = a.Snapshot.CapturedAt <= b.Snapshot.CapturedAt ? (a.Snapshot, b.Snapshot) : (b.Snapshot, a.Snapshot);
        var diff = DriverSnapshotComparer.Diff(older, newer);
        foreach (var c in diff.Changes.OrderBy(c => c.Kind).ThenBy(c => c.DeviceName, StringComparer.CurrentCultureIgnoreCase)) DiffRows.Add(new DriverChangeRow(c));
        DiffSummary = diff.Changes.Count == 0
            ? $"Nenhuma diferença de drivers entre {SysUi.When(older.CapturedAt)} e {SysUi.When(newer.CapturedAt)}."
            : $"De {SysUi.When(older.CapturedAt)} para {SysUi.When(newer.CapturedAt)}: {diff.Count(DriverChangeKind.Added)} adicionado(s), {diff.Count(DriverChangeKind.Removed)} removido(s), " +
              $"{diff.Count(DriverChangeKind.Upgraded)} atualizado(s), {diff.Count(DriverChangeKind.Downgraded)} revertido(s), {diff.Count(DriverChangeKind.Changed)} alterado(s).";
    }

    // ================= NVIDIA / DDU =================
    private DeviceRecord? InstalledNvidiaGpu => _inventory?.Devices.FirstOrDefault(d => d.IsPresent && (d.ClassGuid == DisplayClass || string.Equals(d.ClassName, "Display", StringComparison.OrdinalIgnoreCase))
        && string.Equals(d.PrimaryHardwareId?.VendorId, "10DE", StringComparison.OrdinalIgnoreCase));

    [RelayCommand]
    private async Task AnalyzeNvidia()
    {
        var folder = SysUi.PickFolder("Escolha a pasta do pacote NVIDIA já extraído (contém Display.Driver, setup.exe…)");
        if (folder is null) return;
        IsAnalyzingNvidia = true; HasNvidiaReport = false;
        NvidiaComponents.Clear(); NvidiaRecommendations.Clear(); NvidiaWarnings.Clear();
        _opCts?.Cancel(); _opCts = new CancellationTokenSource();
        try
        {
            using var _ = App.Tasks.Begin("Analisando pacote NVIDIA");
            var gpu = InstalledNvidiaGpu;
            var r = await NvidiaPackageAssistant.AnalyzeAsync(folder, gpu, _opCts.Token);
            foreach (var c in r.Components) NvidiaComponents.Add(new NvidiaComponentRow(c));
            foreach (var s in r.RecommendationsPt) NvidiaRecommendations.Add(s);
            foreach (var w in r.Warnings) NvidiaWarnings.Add(w);
            string compat = r.SupportsInstalledGpu switch
            {
                true => $"o pacote declara suporte à GPU instalada ({r.InstalledGpuShortId}).",
                false => $"ATENÇÃO: o ID da GPU instalada ({r.InstalledGpuShortId}) não aparece nos INFs do driver.",
                null => gpu is null ? "nenhuma GPU NVIDIA conectada para verificar compatibilidade." : "compatibilidade indeterminada.",
            };
            NvidiaSummary = r.LooksLikeNvidiaPackage
                ? $"Pacote NVIDIA {r.PackageVersion ?? "(versão não identificada)"} · {r.Components.Count} componente(s) · {compat}"
                : "A pasta não parece um pacote NVIDIA extraído (Display.Driver não encontrado). Extraia o instalador oficial com 7-Zip ou use a pasta C:\\NVIDIA\\DisplayDriver\\<versão>.";
            HasNvidiaReport = true;
        }
        catch (OperationCanceledException) { NvidiaSummary = "Análise cancelada."; }
        catch (Exception ex) { NvidiaSummary = "Falha na análise: " + ex.Message; App.Log(ex, "NVIDIA package"); }
        finally { IsAnalyzingNvidia = false; }
    }

    private void EvaluateDdu()
    {
        DduReasons.Clear();
        var gpus = _inventory?.Devices.Where(d => d.IsPresent && (d.ClassGuid == DisplayClass || string.Equals(d.ClassName, "Display", StringComparison.OrdinalIgnoreCase))).ToList();
        NvidiaGpuText = gpus is null ? "" : gpus.Count == 0 ? "Nenhum adaptador de vídeo detectado."
            : "Vídeo instalado: " + string.Join(" · ", gpus.Select(g => $"{g.Name} (driver {g.Driver?.Version ?? "—"})"));
        if (_inventory is null) { DduVerdict = "Aguardando inventário."; DduAdvisable = false; return; }
        var g = DduAdvisor.Evaluate(_inventory, _store, UserReportsIssues);
        DduAdvisable = g.Advisable;
        DduLevel = g.Advisable ? (object)Severity.Medium : true;
        DduVerdict = g.Advisable
            ? "Há indícios de que uma remoção profunda (DDU) pode ajudar. Siga os passos com atenção — o RBM Optimizer não executa o DDU."
            : "Remoção profunda NÃO é necessária pelos dados coletados. Prefira a instalação limpa do instalador oficial.";
        foreach (var r in g.ReasonsPt) DduReasons.Add(r);
    }
}
