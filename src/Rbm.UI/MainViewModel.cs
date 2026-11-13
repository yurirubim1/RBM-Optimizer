using System.Collections.ObjectModel;
using Rbm.Core;
using Rbm.Core.Hardware;
using Rbm.Core.Tweaks;
using Rbm.Security;
using Rbm.UI.Infrastructure;
using Rbm.UI.Views;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Extensions.DependencyInjection;

namespace Rbm.UI;

public sealed record NavGroup(string Title, IReadOnlyList<NavItem> Items);

public sealed partial class MainViewModel : ObservableObject
{
    private readonly IServiceProvider _sp;
    private readonly ISystemInventory _inventory;
    private readonly ITelemetryService _telemetry;
    private readonly AgentClient _agent;
    private readonly IChangeJournal _journal;

    public ObservableCollection<NavGroup> Groups { get; } = new();
    public Notifier Notifier => App.Notify;
    public DialogService Dialog => App.Dialogs;
    public TaskTracker Tasks => App.Tasks;
    public string Version => App.Version;

    [ObservableProperty] private PageViewModel? _currentPage;
    [ObservableProperty] private NavItem? _currentItem;
    [ObservableProperty] private string _breadcrumb = "";
    [ObservableProperty] private bool _isInitializing = true;
    [ObservableProperty] private string _initText = "Detectando hardware…";
    [ObservableProperty] private bool _monitoringActive;
    [ObservableProperty] private bool _agentConnected;
    [ObservableProperty] private int _pendingRestart;
    [ObservableProperty] private string _quickStats = "";
    [ObservableProperty] private string _machineLine = "";

    public MainViewModel(IServiceProvider sp, ISystemInventory inventory, ITelemetryService telemetry, AgentClient agent, IChangeJournal journal, LiveTelemetry live)
    {
        _sp = sp; _inventory = inventory; _telemetry = telemetry; _agent = agent; _journal = journal;
        _settingsStore = sp.GetRequiredService<ISettingsStore>();
        _simpleMode = _settingsStore.Get("ui.simpleMode", true);
        RebuildGroups();
        _agent.ConnectionChanged += (_, _) => Ui.Post(() => AgentConnected = _agent.IsConnected);
        live.Updated += s => QuickStats = $"CPU {s.CpuTotalPercent:0}%  ·  RAM {s.MemUsedPercent:0}%  ·  GPU {s.Gpus.Select(g => g.Utilization3DPercent).DefaultIfEmpty(0).Max():0}%";
        _ = InitializeAsync();
    }

    private readonly ISettingsStore _settingsStore;

    /// <summary>Páginas mostradas no modo simples (as demais continuam acessíveis por links e pelo modo completo).</summary>
    public static readonly HashSet<string> SimpleKeys = new() { "dashboard", "recommend", "diagnostics", "cleanup", "space", "gaming", "history", "settings" };

    /// <summary>Modo simples: barra lateral só com o essencial.</summary>
    [ObservableProperty] private bool _simpleMode;

    partial void OnSimpleModeChanged(bool value)
    {
        _settingsStore.Set("ui.simpleMode", value);
        RebuildGroups();
    }

    private void RebuildGroups()
    {
        Groups.Clear();
        var items = PageRegistry.Items.Where(i => !SimpleMode || SimpleKeys.Contains(i.Key));
        foreach (var g in items.GroupBy(i => i.Group))
            Groups.Add(new NavGroup(Loc.T("grp." + g.Key), g.ToList()));
    }

    public SystemProfile? Profile => _inventory.Current;

    private async Task InitializeAsync()
    {
        try
        {
            using var _ = App.Tasks.Begin("Detectando hardware");
            var p = await _inventory.CollectAsync();
            MachineLine = $"{p.Cpu.Name} · {p.PrimaryGpu?.Name ?? "GPU"} · {p.Memory.TotalPhysicalBytes / 1073741824.0:0} GB";
            OnPropertyChanged(nameof(Profile));
            _telemetry.Start();
            MonitoringActive = true;
            if (p.CollectionWarnings.Count > 0)
                App.Notify.Warning("Coleta parcial", $"{p.CollectionWarnings.Count} fonte(s) indisponível(is). Detalhes no log.");
            await RefreshPendingRestartAsync();
        }
        catch (Exception ex)
        {
            App.Log(ex, "Inicialização");
            App.Notify.Error("Falha na detecção de hardware", ex.Message);
        }
        finally
        {
            IsInitializing = false;
            var args = Environment.GetCommandLineArgs();
            int i = Array.IndexOf(args, "--page");
            Navigate(i >= 0 && i + 1 < args.Length && PageRegistry.Items.Any(x => x.Key == args[i + 1]) ? args[i + 1] : "dashboard");
        }
    }

    public async Task RefreshPendingRestartAsync()
    {
        try
        {
            var active = await _journal.ActiveChangesAsync();
            var boot = _inventory.Current?.Os.LastBoot ?? DateTime.MinValue;
            PendingRestart = active.Count(r => r.Status == ChangeStatus.PendingRestart && r.Restart == RestartRequirement.Reboot && r.At.LocalDateTime > boot);
        }
        catch { }
    }

    [RelayCommand]
    public void Navigate(string key)
    {
        var item = PageRegistry.Items.FirstOrDefault(i => i.Key == key);
        if (item is null) return;
        CurrentPage?.OnNavigatedFrom();
        var vm = (PageViewModel)_sp.GetRequiredService(item.ViewModelType);
        CurrentItem = item;
        CurrentPage = vm;
        Breadcrumb = $"RBM Optimizer  ›  {Loc.T("grp." + item.Group)}  ›  {item.Title}";
        _ = vm.OnNavigatedToAsync();
    }

    [RelayCommand]
    private void ToggleMonitoring()
    {
        if (_telemetry.IsRunning) { _telemetry.Stop(); MonitoringActive = false; }
        else { _telemetry.Start(); MonitoringActive = true; }
    }

    [RelayCommand]
    private async Task ConnectAgent()
    {
        if (_agent.IsConnected)
        {
            if (await App.Dialogs.ConfirmAsync("Encerrar agente elevado", "O agente privilegiado será encerrado. Operações administrativas pedirão o UAC novamente.", confirmText: "Encerrar"))
                await _agent.ShutdownAsync();
            AgentConnected = _agent.IsConnected;
            return;
        }
        using var _ = App.Tasks.Begin("Iniciando agente elevado");
        var ok = await _agent.EnsureConnectedAsync();
        AgentConnected = ok;
        if (ok) App.Notify.Success("Agente elevado ativo", "Operações administrativas da allow-list estão disponíveis. Tudo é auditado.");
        else App.Notify.Warning("Agente não iniciado", _agent.LastError ?? "Elevação recusada.");
    }
}
