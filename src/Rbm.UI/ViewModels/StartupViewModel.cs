using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Windows.Data;
using System.Windows.Threading;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Core.Tweaks;
using Rbm.Processes;
using Rbm.Processes.Agent;
using Rbm.UI.Infrastructure;
using Rbm.UI.Pages.SystemPages;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.ViewModels;

public sealed record StartModeOption(ServiceStartMode Mode, string Text)
{
    public override string ToString() => Text;
}

public sealed partial class StartupViewModel : PageViewModel
{
    public override string Key => "startup";

    private readonly StartupManager _startup = new();
    private readonly ServiceManager _services = new();
    private readonly ScheduledTaskManager _tasks = new();
    private readonly AutorunsScanner _autoruns = new();
    private CancellationTokenSource? _loadCts;
    private readonly DispatcherTimer _elapsedTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private readonly Stopwatch _loadWatch = new();
    private readonly bool[] _loaded = new bool[4];
    private readonly string[] _loadInfo = { "", "", "", "" };

    public ObservableCollection<StartupRow> StartupItems { get; } = new();
    public ObservableCollection<ServiceRow> ServiceItems { get; } = new();
    public ObservableCollection<TaskRow> TaskItems { get; } = new();
    public ObservableCollection<OtherRow> OtherItems { get; } = new();
    public ObservableCollection<StartupChangeRecord> Changes { get; } = new();

    public ICollectionView StartupView { get; }
    public ICollectionView ServiceView { get; }
    public ICollectionView TaskView { get; }
    public ICollectionView OtherView { get; }

    public IReadOnlyList<StartModeOption> StartModes { get; } = new[] { ServiceStartMode.Automatic, ServiceStartMode.AutomaticDelayed, ServiceStartMode.Manual, ServiceStartMode.Disabled }
        .Select(m => new StartModeOption(m, ServiceManager.Display(m))).ToList();

    [ObservableProperty, NotifyPropertyChangedFor(nameof(TabStartup), nameof(TabServices), nameof(TabTasks), nameof(TabOther), nameof(LoadInfo), nameof(TabHint), nameof(TabTech), nameof(CanEditTab))]
    private int _tabIndex;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FiltersActive))] private string _search = "";
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FiltersActive))] private bool _hideMicrosoft = true;
    /// <summary>Mostra só itens com sugestão (pode desativar / verificar).</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(FiltersActive))] private bool _onlySuggestions;
    [ObservableProperty] private bool _isLoading;

    // ---- Resumo (hero) e estado vazio ----
    [ObservableProperty] private string _heroTitle = "Verificando o que inicia com o Windows…";
    [ObservableProperty] private string _heroText = "Lendo os programas de inicialização e conferindo quem publicou cada um.";
    [ObservableProperty] private string _heroChipText = "Lendo";
    /// <summary>ok | warn | danger | info</summary>
    [ObservableProperty] private string _heroLevel = "info";
    [ObservableProperty] private bool _hasSuggestions;
    [ObservableProperty] private bool _isListEmpty;
    public bool FiltersActive => !string.IsNullOrWhiteSpace(Search) || OnlySuggestions || HideMicrosoft;
    /// <summary>A aba "Avançado" é somente leitura.</summary>
    public bool CanEditTab => TabIndex != 3;

    partial void OnOnlySuggestionsChanged(bool value) => RefreshViews();
    partial void OnIsLoadingChanged(bool value) => UpdateEmpty();

    [RelayCommand]
    private void ReviewSuggestions()
    {
        TabIndex = 0;
        Search = "";
        OnlySuggestions = true;
    }

    [RelayCommand]
    private void ClearFilters()
    {
        Search = "";
        OnlySuggestions = false;
        HideMicrosoft = false;
    }

    private void UpdateEmpty()
    {
        var v = TabIndex switch { 0 => StartupView, 1 => ServiceView, 2 => TaskView, _ => OtherView };
        IsListEmpty = _loaded[TabIndex] && !IsLoading && !v.Cast<object>().Any();
    }

    private void UpdateHero()
    {
        if (!_loaded[0])
        {
            HeroTitle = "Verificando o que inicia com o Windows…";
            HeroText = "Lendo os programas de inicialização e conferindo quem publicou cada um.";
            HeroChipText = IsLoading ? "Lendo" : "Não lido"; HeroLevel = "info"; HasSuggestions = false;
            return;
        }
        int starts = StartupItems.Count(r => r.IsEnabled || r.Entry.State == StartupEntryState.NotApplicable);
        int optional = StartupItems.Count(r => r.IsEnabled && r.Entry.Recommendation.Kind == AutostartRecommendationKind.OptionalSafeToDisable);
        int check = StartupItems.Count(r => (r.IsEnabled || r.Entry.State == StartupEntryState.NotApplicable) && r.Entry.Recommendation.Kind == AutostartRecommendationKind.InvestigateUnsigned);
        HeroTitle = starts == 1 ? "1 programa inicia com o Windows" : $"{starts} programas iniciam com o Windows";
        var parts = new List<string>();
        parts.Add(optional switch
        {
            0 => "Nenhum deles está na lista de desativação segura.",
            1 => "1 pode ser desativado com segurança — ele continua funcionando quando você abrir.",
            _ => $"{optional} podem ser desativados com segurança — eles continuam funcionando quando você abrir.",
        });
        if (check > 0) parts.Add(check == 1 ? "1 merece uma verificação (quem fez não foi confirmado)." : $"{check} merecem uma verificação (quem fez não foi confirmado).");
        HeroText = string.Join(" ", parts);
        HasSuggestions = optional + check > 0;
        if (check > 0) { HeroLevel = "warn"; HeroChipText = check == 1 ? "1 item para verificar" : $"{check} itens para verificar"; }
        else if (optional > 0) { HeroLevel = "info"; HeroChipText = optional == 1 ? "1 sugestão" : $"{optional} sugestões"; }
        else { HeroLevel = "ok"; HeroChipText = "Tudo em ordem"; }
    }
    [ObservableProperty] private string _loadingText = "";
    [ObservableProperty] private StartupRow? _selectedStartup;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StartModeHint))] private ServiceRow? _selectedService;
    [ObservableProperty] private TaskRow? _selectedTask;
    [ObservableProperty] private OtherRow? _selectedOther;
    [ObservableProperty] private ServiceStartMode _pendingStartMode = ServiceStartMode.Manual;
    [ObservableProperty] private string _startupSummary = "";
    [ObservableProperty] private string _serviceSummary = "";
    [ObservableProperty] private string _taskSummary = "";
    [ObservableProperty] private string _otherSummary = "";
    [ObservableProperty] private string _startupTabText = "Inicialização";
    [ObservableProperty] private string _serviceTabText = "Serviços";
    [ObservableProperty] private string _taskTabText = "Tarefas agendadas";
    [ObservableProperty] private string _otherTabText = "Outros";
    [ObservableProperty] private bool _hasChanges;

    public bool TabStartup { get => TabIndex == 0; set { if (value) TabIndex = 0; } }
    public bool TabServices { get => TabIndex == 1; set { if (value) TabIndex = 1; } }
    public bool TabTasks { get => TabIndex == 2; set { if (value) TabIndex = 2; } }
    public bool TabOther { get => TabIndex == 3; set { if (value) TabIndex = 3; } }
    public string LoadInfo => _loadInfo[TabIndex];
    public bool AgentConnected => AgentCall.IsConnected;

    public string TabHint => TabIndex switch
    {
        0 => "Programas que abrem sozinhos quando você entra no Windows. Desativar não apaga nada: o programa continua instalado e dá para reativar quando quiser.",
        1 => "Serviços rodam em segundo plano para o Windows e para programas. Mude só se souber o que o serviço faz — os essenciais ficam protegidos.",
        2 => "Tarefas que o Windows ou programas executam sozinhos (ao ligar o PC, em horários…). Desativar só pausa a tarefa; ela não é apagada.",
        _ => "Outros pontos onde programas podem se iniciar. Somente leitura: o RBM apenas mostra e destaca o que foge do padrão do Windows.",
    };

    public string TabTech => TabIndex switch
    {
        0 => "Programas iniciados no logon (Run/RunOnce, Políticas e pastas Inicializar). Desabilitar usa o mesmo mecanismo reversível do Gerenciador de Tarefas (StartupApproved); nada é excluído.",
        1 => "Serviços Win32 via Service Control Manager. Alterar o tipo de inicialização e iniciar/parar exige o RBM Agent (UAC). Serviços essenciais/de segurança são protegidos.",
        2 => "Tarefas do Agendador (inclui ocultas). Desabilitar só altera o sinalizador \"Habilitada\"; nunca exclui. Tarefas de atualização/segurança não são desabilitadas.",
        _ => "Outros locais de autostart (estilo Autoruns): Winlogon, extensões de menu do Explorer e drivers de boot/sistema. Somente leitura — aqui o RBM apenas mostra; alterações nesses locais exigem ferramentas específicas.",
    };

    public string StartModeHint => SelectedService is { } s && s.Entry.IsTriggerStart == true
        ? "Este serviço também é iniciado por gatilho (evento/dispositivo): mesmo em Manual o Windows pode iniciá-lo quando necessário."
        : "";

    public StartupViewModel()
    {
        StartupView = MakeView(StartupItems, o => o is StartupRow r && Visible(r.IsVerifiedMicrosoft, r.Entry.Recommendation.Kind, r.Name, r.Command, r.Publisher, r.Location));
        ServiceView = MakeView(ServiceItems, o => o is ServiceRow r && Visible(r.IsVerifiedMicrosoft, r.Entry.Recommendation.Kind, r.Name, r.DisplayName, r.Publisher, r.Path));
        TaskView = MakeView(TaskItems, o => o is TaskRow r && Visible(r.IsVerifiedMicrosoft, r.Entry.Recommendation.Kind, r.Path, r.Actions, r.Publisher, r.Author));
        OtherView = MakeView(OtherItems, o => o is OtherRow r && Visible(r.IsVerifiedMicrosoft, r.Entry.Recommendation.Kind, r.Name, r.Image, r.Publisher, r.Location));
        _elapsedTimer.Tick += (_, _) => LoadingText = $"{_loadingBase} {_loadWatch.Elapsed.TotalSeconds:0.0} s";
        AgentCall.Client.ConnectionChanged += (_, _) => Ui.Post(() => OnPropertyChanged(nameof(AgentConnected)));
    }

    private string _loadingBase = "";

    private static ICollectionView MakeView<T>(ObservableCollection<T> source, Predicate<object> filter)
    {
        var v = CollectionViewSource.GetDefaultView(source);
        v.Filter = filter;
        return v;
    }

    private bool Visible(bool verifiedMs, AutostartRecommendationKind kind, params string?[] fields) =>
        !(HideMicrosoft && verifiedMs) && (!OnlySuggestions || StartupText.IsSuggestion(kind)) && StartupText.Matches(Search.Trim(), fields);

    partial void OnSearchChanged(string value) => RefreshViews();
    partial void OnHideMicrosoftChanged(bool value) => RefreshViews();

    private void RefreshViews()
    {
        StartupView.Refresh(); ServiceView.Refresh(); TaskView.Refresh(); OtherView.Refresh();
        UpdateSummaries();
    }

    partial void OnTabIndexChanged(int value) { UpdateEmpty(); _ = EnsureLoadedAsync(value); }

    partial void OnSelectedServiceChanged(ServiceRow? value)
    {
        if (value?.StartMode is { } m && StartModes.Any(o => o.Mode == m)) PendingStartMode = m;
    }

    public override async Task OnNavigatedToAsync()
    {
        OnPropertyChanged(nameof(AgentConnected));
        await LoadChangesAsync();
        await EnsureLoadedAsync(TabIndex);
    }

    public override void OnNavigatedFrom() => _loadCts?.Cancel();

    // ======================= Carregamento =======================

    private Task EnsureLoadedAsync(int tab) => _loaded[tab] ? Task.CompletedTask : LoadTabAsync(tab);

    [RelayCommand]
    private Task Refresh() => LoadTabAsync(TabIndex);

    [RelayCommand]
    private void CancelLoad() => _loadCts?.Cancel();

    private async Task LoadTabAsync(int tab)
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        var ct = cts.Token;
        string[] names = { "entradas de inicialização", "serviços", "tarefas agendadas", "outros locais de autostart" };
        string[] typical = { "~2 s", "~3 s", "~4 s", "~4 s" };
        _loadingBase = $"Lendo {names[tab]} e verificando assinaturas digitais (típico {typical[tab]}) —";
        LoadingText = _loadingBase;
        IsLoading = true;
        if (tab == 0) UpdateHero();
        _loadWatch.Restart();
        _elapsedTimer.Start();
        using var task = App.Tasks.Begin("Lendo " + names[tab]);
        try
        {
            int count;
            switch (tab)
            {
                case 0:
                {
                    var list = await Task.Run(() => _startup.EnumerateAsync(verifySignatures: true, ct), ct);
                    ct.ThrowIfCancellationRequested();
                    Replace(StartupItems, list.OrderBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Select(e => new StartupRow(e)));
                    count = list.Count;
                    break;
                }
                case 1:
                {
                    var r = await Task.Run(() => _services.EnumerateAsync(new ServiceQueryOptions(), ct), ct);
                    ct.ThrowIfCancellationRequested();
                    if (!r.Success || r.Value is null) throw new InvalidOperationException(r.Message);
                    Replace(ServiceItems, r.Value.OrderBy(s => s.DisplayName, StringComparer.CurrentCultureIgnoreCase).Select(s => new ServiceRow(s)));
                    count = r.Value.Count;
                    break;
                }
                case 2:
                {
                    var r = await Task.Run(() => _tasks.EnumerateAsync(verifySignatures: true, ct), ct);
                    ct.ThrowIfCancellationRequested();
                    if (!r.Success || r.Value is null) throw new InvalidOperationException(r.Message);
                    Replace(TaskItems, r.Value.OrderBy(t => t.Path, StringComparer.CurrentCultureIgnoreCase).Select(t => new TaskRow(t)));
                    count = r.Value.Count;
                    break;
                }
                default:
                {
                    var list = await Task.Run(() => _autoruns.ScanAsync(new AutorunScanOptions(), ct), ct);
                    ct.ThrowIfCancellationRequested();
                    Replace(OtherItems, list.OrderBy(e => e.Category).ThenBy(e => e.Name, StringComparer.CurrentCultureIgnoreCase).Select(e => new OtherRow(e)));
                    count = list.Count;
                    break;
                }
            }
            _loaded[tab] = true;
            _loadInfo[tab] = $"{count} itens · lidos às {DateTime.Now:HH:mm} em {_loadWatch.Elapsed.TotalSeconds:0.0} s (com verificação de assinatura digital)";
        }
        catch (OperationCanceledException)
        {
            _loadInfo[tab] = "Leitura cancelada. Use \"Atualizar\" para tentar de novo.";
        }
        catch (Exception ex)
        {
            _loadInfo[tab] = "Falha na leitura: " + ex.Message;
            App.Log(ex, $"Inicialização e Serviços: aba {tab}");
            App.Notify.Error("Falha ao ler " + names[tab], ex.Message);
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts))
            {
                _elapsedTimer.Stop();
                _loadWatch.Stop();
                IsLoading = false;
            }
            OnPropertyChanged(nameof(LoadInfo));
            UpdateSummaries();
        }
    }

    private static void Replace<T>(ObservableCollection<T> target, IEnumerable<T> items)
    {
        target.Clear();
        foreach (var i in items) target.Add(i);
    }

    private void UpdateSummaries()
    {
        static int Visible(ICollectionView v) => v.Cast<object>().Count();
        StartupTabText = _loaded[0] ? $"Programas ao iniciar ({Visible(StartupView)})" : "Programas ao iniciar";
        ServiceTabText = _loaded[1] ? $"Serviços ({Visible(ServiceView)})" : "Serviços";
        TaskTabText = _loaded[2] ? $"Tarefas agendadas ({Visible(TaskView)})" : "Tarefas agendadas";
        OtherTabText = _loaded[3] ? $"Avançado ({Visible(OtherView)})" : "Avançado";

        string Shown(ICollectionView v, int total) => Visible(v) == total ? $"{total} itens" : $"Mostrando {Visible(v)} de {total}";
        StartupSummary = $"{Shown(StartupView, StartupItems.Count)} · {StartupItems.Count(r => r.IsEnabled)} ativos · {StartupItems.Count(r => r.Entry.Recommendation.Kind == AutostartRecommendationKind.OptionalSafeToDisable && r.IsEnabled)} podem ser desativados · {StartupItems.Count(r => r.Entry.Recommendation.Kind == AutostartRecommendationKind.InvestigateUnsigned)} para verificar";
        ServiceSummary = $"{Shown(ServiceView, ServiceItems.Count)} · {ServiceItems.Count(r => r.IsRunning)} em execução · {ServiceItems.Count(r => r.StartMode is ServiceStartMode.Automatic or ServiceStartMode.AutomaticDelayed)} iniciam sozinhos · {ServiceItems.Count(r => r.IsProtected)} protegidos · {ServiceItems.Count(r => r.Entry.Recommendation.Kind == AutostartRecommendationKind.InvestigateUnsigned)} para verificar";
        TaskSummary = $"{Shown(TaskView, TaskItems.Count)} · {TaskItems.Count(r => r.Enabled)} ativas · {TaskItems.Count(r => r.Entry.TriggerKinds.Contains(TaskTriggerKind.Logon) || r.Entry.TriggerKinds.Contains(TaskTriggerKind.Boot))} rodam ao ligar/entrar · {TaskItems.Count(r => r.Entry.Recommendation.Kind == AutostartRecommendationKind.InvestigateUnsigned)} para verificar";
        OtherSummary = $"{Shown(OtherView, OtherItems.Count)} · {OtherItems.Count(r => r.Entry.IsDeviation)} fora do padrão do Windows · {OtherItems.Count(r => r.Entry.Recommendation.Kind == AutostartRecommendationKind.InvestigateUnsigned)} para verificar";
        UpdateHero();
        UpdateEmpty();
    }

    // ======================= Inicialização =======================

    [RelayCommand]
    private async Task ToggleStartup(StartupRow? row)
    {
        row ??= SelectedStartup;
        if (row is null || !row.CanToggle) return;
        bool target = !row.IsEnabled;
        if (!target && !await App.Dialogs.ConfirmAsync("Desativar na inicialização",
                $"\"{row.Name}\" deixará de abrir sozinho com o Windows a partir da próxima vez que você entrar. O programa continua instalado.",
                $"Local: {row.Location}\nComando: {row.Command}\n\nReversível: o RBM grava apenas o estado StartupApproved (o mesmo do Gerenciador de Tarefas) e registra a alteração em \"Alterações\". Nada é excluído." +
                (row.Entry.ToggleRequiresElevation ? "\n\nLocal de máquina: o RBM Agent pedirá o UAC." : ""),
                "Desativar"))
            return;

        var res = await SetStartupAsync(row.Entry.Location, row.Name, target);
        if (res is null) return;
        row.IsEnabled = res.CurrentEnabled;
        if (res.PreviousEnabled != res.CurrentEnabled)
            await RecordAsync("startup", row.Name, row.Entry.Location.ToString(), row.Name, res.PreviousEnabled.ToString(), res.CurrentEnabled.ToString(),
                row.Entry.ToggleRequiresElevation ? "agente" : "direto", canRevert: true);
        App.Notify.Success("Inicialização", $"\"{row.Name}\" {(res.CurrentEnabled ? "ativado" : "desativado")}.");
        UpdateSummaries();
    }

    private async Task<StartupToggleResponse?> SetStartupAsync(StartupLocation location, string name, bool enabled)
    {
        if (!StartupManager.IsToggleable(location)) { App.Notify.Warning("Inicialização", "Este local não pode ser alterado de forma reversível."); return null; }
        if (!StartupManager.RequiresElevation(location))
        {
            var r = await Task.Run(() => _startup.SetEnabled(location, name, enabled));
            if (!r.Success || r.Value is null) { App.Notify.Error("Inicialização", r.Message); return null; }
            return r.Value;
        }
        var resp = await AgentCall.InvokeAsync(AgentOps.StartupSetEnabled, new StartupToggleRequest(location.ToString(), name, enabled), "Inicialização");
        if (resp is null) return null;
        // A resposta do agente é StartupToggleResponse; se vier vazia, assume o valor pedido (o agente só retorna Ok após gravar).
        return AgentCall.Read<StartupToggleResponse>(resp) ?? new StartupToggleResponse(location.ToString(), name, !enabled, enabled);
    }

    // ======================= Serviços =======================

    [RelayCommand]
    private async Task ApplyStartMode()
    {
        var row = SelectedService;
        if (row is null) return;
        var mode = PendingStartMode;
        if (row.StartMode == mode) { App.Notify.Info("Serviço", $"\"{row.DisplayName}\" já está em {ServiceManager.Display(mode)}."); return; }
        if (!row.CanChange) { App.Notify.Warning("Serviço protegido", row.ProtectionReason); return; }
        var req = new ServiceStartRequest(row.Name, mode);
        // O agente (service.setStart) grava diretamente: a validação de proteção acontece AQUI antes do envio.
        var v = ServiceManager.ValidateStartModeRequest(req);
        if (!v.Success) { App.Notify.Warning("Alteração recusada", v.Message); return; }

        bool disabling = mode == ServiceStartMode.Disabled;
        var runningDependents = ServiceItems.Where(s => row.Entry.Dependents.Contains(s.Name, StringComparer.OrdinalIgnoreCase) && s.IsRunning).Select(s => s.DisplayName).ToList();
        string details = $"Serviço: {row.DisplayName} ({row.Name})\nAtual: {row.StartModeText}  →  Novo: {ServiceManager.Display(mode)}\n" +
                         $"Recomendação: {row.Recommendation} — {row.RecommendationReason}\n" +
                         $"Dependentes: {row.Dependents}" +
                         (runningDependents.Count > 0 ? $"\nDependentes em execução: {string.Join(", ", runningDependents)}" : "") +
                         "\n\nO estado atual (em execução/parado) não muda agora; vale a partir do próximo boot ou início. Reversível em \"Alterações\". Requer o RBM Agent (UAC).";
        bool ok = await App.Dialogs.ConfirmAsync(
            disabling ? "Desativar serviço" : "Alterar tipo de inicialização",
            disabling
                ? $"Desativar \"{row.DisplayName}\" impede que ele seja iniciado, inclusive por outros programas e serviços que dependem dele."
                : $"Alterar a inicialização de \"{row.DisplayName}\" para {ServiceManager.Display(mode)}?",
            details, disabling ? "Desativar" : "Aplicar", danger: disabling,
            requiredPhrase: disabling && row.Entry.Dependents.Count > 0 ? "DESATIVAR" : null);
        if (!ok) return;

        var res = await SetServiceStartAsync(req);
        if (res is null) return;
        row.StartMode = res.Current;
        if (res.Previous != res.Current)
            await RecordAsync("serviceStart", row.DisplayName, row.Name, null, res.Previous.ToString(), res.Current.ToString(), "agente", canRevert: true);
        App.Notify.Success("Serviço", $"\"{row.DisplayName}\": {ServiceManager.Display(res.Previous)} → {ServiceManager.Display(res.Current)}.");
        UpdateSummaries();
    }

    private static async Task<ServiceStartResponse?> SetServiceStartAsync(ServiceStartRequest req)
    {
        var v = ServiceManager.ValidateStartModeRequest(req);
        if (!v.Success) { App.Notify.Warning("Alteração recusada", v.Message); return null; }
        var resp = await AgentCall.InvokeAsync(AgentOps.ServiceSetStart, req, "Tipo de inicialização do serviço");
        if (resp is null) return null;
        return AgentCall.Read<ServiceStartResponse>(resp) ?? new ServiceStartResponse(req.Mode, req.Mode);
    }

    [RelayCommand] private Task StartService() => ControlSelectedAsync(ServiceControlAction.Start);
    [RelayCommand] private Task StopService() => ControlSelectedAsync(ServiceControlAction.Stop);
    [RelayCommand] private Task RestartService() => ControlSelectedAsync(ServiceControlAction.Restart);

    private async Task ControlSelectedAsync(ServiceControlAction action)
    {
        var row = SelectedService;
        if (row is null) return;
        var req = new ServiceControlRequest(row.Name, action);
        var v = ServiceManager.ValidateControlRequest(req);
        if (!v.Success) { App.Notify.Warning("Ação recusada", v.Message); return; }
        if (action == ServiceControlAction.Start && row.StartMode == ServiceStartMode.Disabled)
        {
            App.Notify.Warning("Serviço desativado", "Um serviço desativado não pode ser iniciado. Altere o tipo de inicialização primeiro.");
            return;
        }
        var runningDependents = ServiceItems.Where(s => row.Entry.Dependents.Contains(s.Name, StringComparer.OrdinalIgnoreCase) && s.IsRunning).Select(s => s.DisplayName).ToList();
        string verb = action switch { ServiceControlAction.Start => "Iniciar", ServiceControlAction.Stop => "Parar", _ => "Reiniciar" };
        string msg = action == ServiceControlAction.Start
            ? $"Iniciar \"{row.DisplayName}\" agora?"
            : $"{verb} \"{row.DisplayName}\" interrompe o que ele estiver fazendo agora. Programas que dependem dele podem falhar até que volte a rodar.";
        string details = $"Serviço: {row.Name}\nEstado atual: {row.StatusText}\nConta: {row.Account}\n" +
                         (runningDependents.Count > 0 ? $"Dependentes em execução (NÃO são parados automaticamente; a ação pode ser recusada): {string.Join(", ", runningDependents)}\n" : "") +
                         "Requer o RBM Agent (UAC).";
        if (!await App.Dialogs.ConfirmAsync($"{verb} serviço", msg, details, verb, danger: action != ServiceControlAction.Start)) return;

        var res = await ControlServiceAsync(req);
        if (res is null) return;
        row.Status = res.Current;
        if (action != ServiceControlAction.Restart && res.Previous != res.Current)
            await RecordAsync("serviceControl", row.DisplayName, row.Name, null, res.Previous.ToString(), res.Current.ToString(), "agente", canRevert: true);
        else if (action == ServiceControlAction.Restart)
            await RecordAsync("serviceControl", row.DisplayName, row.Name, null, "Restart", res.Current.ToString(), "agente", canRevert: false);
        App.Notify.Success("Serviço", $"\"{row.DisplayName}\": {StartupText.RunState(res.Current)}.");
        UpdateSummaries();
    }

    private static async Task<ServiceControlResponse?> ControlServiceAsync(ServiceControlRequest req)
    {
        using var _ = App.Tasks.Begin("Controlando serviço " + req.ServiceName);
        var resp = await AgentCall.InvokeAsync(AgentOps.ServiceControl, req, "Controle do serviço", TimeSpan.FromSeconds(60));
        if (resp is null) return null;
        var r = AgentCall.Read<ServiceControlResponse>(resp);
        if (r is null) App.Notify.Warning("Controle do serviço", "O agente concluiu, mas não informou o estado final. Use \"Atualizar\".");
        return r;
    }

    // ======================= Tarefas =======================

    [RelayCommand]
    private async Task ToggleTask(TaskRow? row)
    {
        row ??= SelectedTask;
        if (row is null) return;
        bool target = !row.Enabled;
        if (!target && row.IsProtected) { App.Notify.Warning("Tarefa protegida", row.ProtectionReason); return; }
        if (!target && !await App.Dialogs.ConfirmAsync("Desativar tarefa agendada",
                $"\"{row.Name}\" não será mais executada pelo Agendador até ser habilitada de novo.",
                $"Caminho: {row.Path}\nGatilhos: {row.Triggers}\nAções: {row.Actions}\nRecomendação: {row.Recommendation} — {row.RecommendationReason}\n\nSó o sinalizador \"Habilitada\" muda; a tarefa não é excluída. Reversível em \"Alterações\".",
                "Desativar"))
            return;

        var res = await SetTaskAsync(row.Path, target, askBeforeAgent: true);
        if (res is null) return;
        var (r, via) = res.Value;
        row.Enabled = r.CurrentEnabled;
        if (r.PreviousEnabled != r.CurrentEnabled)
            await RecordAsync("task", row.Name, row.Path, null, r.PreviousEnabled.ToString(), r.CurrentEnabled.ToString(), via, canRevert: true);
        App.Notify.Success("Tarefa agendada", $"\"{row.Name}\" {(r.CurrentEnabled ? "ativada" : "desativada")}.");
        UpdateSummaries();
    }

    private async Task<(TaskToggleResponse Response, string Via)?> SetTaskAsync(string path, bool enabled, bool askBeforeAgent)
    {
        var direct = await _tasks.SetEnabledAsync(path, enabled);
        if (direct.Success && direct.Value is not null) return (direct.Value, "direto");
        if (direct.ErrorCode != ProcessErrorCodes.AccessDenied) { App.Notify.Error("Tarefa agendada", direct.Message); return null; }
        if (askBeforeAgent && !await App.Dialogs.ConfirmAsync("Requer administrador",
                "Esta tarefa pertence ao sistema ou a outro usuário e só pode ser alterada com privilégios de administrador.",
                $"Tarefa: {path}\nO RBM Agent será usado (pedido de UAC no primeiro uso). A operação é auditada.", "Usar o RBM Agent"))
            return null;
        var resp = await AgentCall.InvokeAsync(AgentOps.TaskSetEnabled, new TaskToggleRequest(path, enabled), "Tarefa agendada");
        if (resp is null) return null;
        return (AgentCall.Read<TaskToggleResponse>(resp) ?? new TaskToggleResponse(path, !enabled, enabled), "agente");
    }

    // ======================= Detalhes / utilidades =======================

    [RelayCommand]
    private void RevealPath(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == "—") return;
        SysUi.RevealFile(path);
    }

    // ======================= Alterações e reversão =======================

    private async Task LoadChangesAsync()
    {
        try
        {
            var list = await SysUi.Db.ListAsync<StartupChangeRecord>(StartupChangeRecord.Collection, 200);
            Changes.Clear();
            foreach (var c in list) Changes.Add(c);
            HasChanges = Changes.Count > 0;
        }
        catch (Exception ex) { App.Log(ex, "Alterações de inicialização"); }
    }

    private async Task RecordAsync(string kind, string target, string key1, string? key2, string previous, string current, string via, bool canRevert)
    {
        var rec = new StartupChangeRecord(Guid.NewGuid().ToString("N"), DateTimeOffset.Now, kind, target, key1, key2, previous, current, via, canRevert);
        try { await SysUi.Db.UpsertAsync(StartupChangeRecord.Collection, rec.Id, rec, tag: kind); }
        catch (Exception ex) { App.Log(ex, "Registro de alteração"); App.Notify.Warning("Histórico", "A alteração foi aplicada, mas não pôde ser registrada no banco local: " + ex.Message); }
        await SysUi.AuditAsync("startup." + kind, $"{target} [{key1}{(key2 is null ? "" : "/" + key2)}]: {previous} → {current} (via {via})", true);
        Changes.Insert(0, rec);
        HasChanges = true;
    }

    [RelayCommand]
    private async Task Revert(StartupChangeRecord? rec)
    {
        if (rec is null || !rec.ShowRevert) return;
        if (!await App.Dialogs.ConfirmAsync("Reverter alteração", $"Restaurar \"{rec.Target}\" para o estado anterior?",
                $"{rec.KindText}: {rec.ChangeText}\nRegistrada em {rec.WhenText} (via {rec.Via}).\nA reversão aplica o valor anterior pelo mesmo caminho (direto ou RBM Agent).",
                "Reverter"))
            return;

        bool ok = false;
        string detail = "";
        switch (rec.Kind)
        {
            case "startup" when Enum.TryParse<StartupLocation>(rec.Key1, out var loc) && rec.Key2 is { } name:
            {
                var r = await SetStartupAsync(loc, name, rec.Previous == bool.TrueString);
                if (r is not null)
                {
                    ok = true; detail = $"{r.PreviousEnabled} → {r.CurrentEnabled}";
                    foreach (var row in StartupItems.Where(x => x.Key == $"{loc}|{name}")) row.IsEnabled = r.CurrentEnabled;
                }
                break;
            }
            case "serviceStart" when Enum.TryParse<ServiceStartMode>(rec.Previous, out var mode):
            {
                var r = await SetServiceStartAsync(new ServiceStartRequest(rec.Key1, mode));
                if (r is not null)
                {
                    ok = true; detail = $"{r.Previous} → {r.Current}";
                    foreach (var row in ServiceItems.Where(x => x.Key == rec.Key1)) row.StartMode = r.Current;
                }
                break;
            }
            case "serviceControl" when Enum.TryParse<ServiceRunState>(rec.Previous, out var state) && state is ServiceRunState.Running or ServiceRunState.Stopped:
            {
                var action = state == ServiceRunState.Running ? ServiceControlAction.Start : ServiceControlAction.Stop;
                var req = new ServiceControlRequest(rec.Key1, action);
                var v = ServiceManager.ValidateControlRequest(req);
                if (!v.Success) { App.Notify.Warning("Reversão recusada", v.Message); return; }
                var r = await ControlServiceAsync(req);
                if (r is not null)
                {
                    ok = true; detail = $"{r.Previous} → {r.Current}";
                    foreach (var row in ServiceItems.Where(x => x.Key == rec.Key1)) row.Status = r.Current;
                }
                break;
            }
            case "task":
            {
                var r = await SetTaskAsync(rec.Key1, rec.Previous == bool.TrueString, askBeforeAgent: false);
                if (r is { } x)
                {
                    ok = true; detail = $"{x.Response.PreviousEnabled} → {x.Response.CurrentEnabled}";
                    foreach (var row in TaskItems.Where(t => t.Key == rec.Key1)) row.Enabled = x.Response.CurrentEnabled;
                }
                break;
            }
            default:
                App.Notify.Warning("Reversão", "Registro em formato não reconhecido; nada foi alterado.");
                return;
        }

        await SysUi.AuditAsync("startup.revert", $"{rec.Target} [{rec.Kind}] {rec.ChangeText}: {(ok ? detail : "falhou")}", ok);
        if (!ok) return;
        var updated = rec with { Reverted = true, RevertedAt = DateTimeOffset.Now };
        try { await SysUi.Db.UpsertAsync(StartupChangeRecord.Collection, updated.Id, updated, tag: updated.Kind, createdAt: updated.At); }
        catch (Exception ex) { App.Log(ex, "Registro de reversão"); }
        int i = Changes.IndexOf(rec);
        if (i >= 0) Changes[i] = updated;
        App.Notify.Success("Alteração revertida", $"\"{rec.Target}\" voltou ao estado anterior.");
        UpdateSummaries();
    }
}
