using Rbm.Core;
using Rbm.Core.Tweaks;
using Rbm.Processes;
using Rbm.Security;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rbm.UI.Pages.SystemPages;

/// <summary>Textos/tons comuns às linhas da página Inicialização e Serviços.</summary>
public static class StartupText
{
    public static string Recommendation(AutostartRecommendationKind k) => k switch
    {
        AutostartRecommendationKind.Keep => "Manter",
        AutostartRecommendationKind.OptionalSafeToDisable => "Pode desativar",
        AutostartRecommendationKind.InvestigateUnsigned => "Verificar",
        _ => "Sem sugestão",
    };

    /// <summary>Explicação curta e sem jargão da recomendação (a razão técnica completa fica nos detalhes técnicos).</summary>
    public static string PlainReason(AutostartRecommendation r, bool isService = false)
    {
        switch (r.Kind)
        {
            case AutostartRecommendationKind.OptionalSafeToDisable:
                var app = r.KnownAppId is { } id ? AutostartAdvisor.KnownApps.FirstOrDefault(a => a.Id == id) : null;
                return app is null
                    ? "Inicializador ou atualizador conhecido — pode desativar; o programa funciona normalmente quando você abrir."
                    : $"{app.DisplayName} — pode desativar. {app.Note} O programa continua funcionando quando você abrir.";
            case AutostartRecommendationKind.Keep:
                return isService
                    ? "Parte do Windows ou protegido pelo RBM — mantenha como está."
                    : "Parte do Windows ou item protegido — mantenha ativo.";
            case AutostartRecommendationKind.InvestigateUnsigned:
                return "Não foi possível confirmar quem fez este item (sem assinatura digital válida ou o arquivo não existe mais). Verifique se você reconhece o programa.";
            default:
                return "Sem dados suficientes para sugerir. Desative só se você não usa este programa.";
        }
    }

    public static bool IsSuggestion(AutostartRecommendationKind k) =>
        k is AutostartRecommendationKind.OptionalSafeToDisable or AutostartRecommendationKind.InvestigateUnsigned;

    /// <summary>Tom para o conversor global LevelBrush (bool true = verde; Severity = azul/amarelo/cinza).</summary>
    public static object RecommendationTone(AutostartRecommendationKind k) => k switch
    {
        AutostartRecommendationKind.Keep => true,
        AutostartRecommendationKind.OptionalSafeToDisable => Severity.Low,
        AutostartRecommendationKind.InvestigateUnsigned => Severity.Medium,
        _ => Severity.Info,
    };

    public static string Signature(PublisherInfo? p) =>
        p is null ? "—" : p.Signature is { } s ? s.Display : p.Exists ? "Não verificada" : "Arquivo não encontrado";

    public static object SignatureTone(PublisherInfo? p) => p?.Signature?.Status switch
    {
        SignatureStatus.Valid => true,
        null => Severity.Info,
        SignatureStatus.FileNotFound => Severity.Medium,
        _ => Severity.Medium,
    };

    public static string Publisher(PublisherInfo? p) => p?.PublisherDisplay ?? "—";

    public static string RunState(ServiceRunState s) => s switch
    {
        ServiceRunState.Running => "Em execução",
        ServiceRunState.Stopped => "Parado",
        ServiceRunState.StartPending => "Iniciando…",
        ServiceRunState.StopPending => "Parando…",
        ServiceRunState.Paused => "Pausado",
        ServiceRunState.PausePending => "Pausando…",
        ServiceRunState.ContinuePending => "Retomando…",
        _ => "Desconhecido",
    };

    public static string StartMode(ServiceStartMode? m) => m is { } v ? ServiceManager.Display(v) : "Indisponível";

    public static string TaskState(ScheduledTaskState s) => s switch
    {
        ScheduledTaskState.Ready => "Pronta",
        ScheduledTaskState.Running => "Em execução",
        ScheduledTaskState.Disabled => "Desativada",
        ScheduledTaskState.Queued => "Na fila",
        _ => "Desconhecido",
    };

    public static string Category(AutorunCategory c) => c switch
    {
        AutorunCategory.Winlogon => "Winlogon",
        AutorunCategory.ShellExtension => "Extensão do Explorer",
        AutorunCategory.BootDriver => "Driver de boot/sistema",
        _ => c.ToString(),
    };

    public static bool Matches(string q, params string?[] fields)
    {
        if (string.IsNullOrWhiteSpace(q)) return true;
        foreach (var f in fields)
            if (f is not null && f.Contains(q, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

public sealed partial class StartupRow : ObservableObject
{
    public StartupRow(StartupEntry e)
    {
        Entry = e;
        _isEnabled = e.State == StartupEntryState.Enabled;
    }
    public StartupEntry Entry { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText), nameof(StateTone), nameof(ToggleText), nameof(ShortToggleText), nameof(Suggestion), nameof(SuggestionTone))] private bool _isEnabled;

    /// <summary>Sugestão considerando o estado atual (ex.: "Recomendado desativar" só enquanto ainda está ativo).</summary>
    public string Suggestion => Entry.Recommendation.Kind == AutostartRecommendationKind.OptionalSafeToDisable
        ? (IsEnabled ? "Recomendado desativar" : "Opcional")
        : StartupText.Recommendation(Entry.Recommendation.Kind);
    public object SuggestionTone => Entry.Recommendation.Kind == AutostartRecommendationKind.OptionalSafeToDisable && !IsEnabled
        ? Severity.Info : StartupText.RecommendationTone(Entry.Recommendation.Kind);
    public string PlainReason => StartupText.PlainReason(Entry.Recommendation);
    public string ShortToggleText => IsEnabled ? "Desativar" : "Ativar";
    public string PublisherLine => Entry.Publisher?.PublisherDisplay ?? "Fabricante não identificado";

    public string Name => Entry.Name;
    public string Location => Entry.LocationDisplay;
    public string Command => Entry.Command ?? "—";
    public string Publisher => StartupText.Publisher(Entry.Publisher);
    public string Signature => StartupText.Signature(Entry.Publisher);
    public object SignatureTone => StartupText.SignatureTone(Entry.Publisher);
    public string Recommendation => StartupText.Recommendation(Entry.Recommendation.Kind);
    public object RecommendationTone => StartupText.RecommendationTone(Entry.Recommendation.Kind);
    public string RecommendationReason => Entry.Recommendation.Reason;
    public bool IsVerifiedMicrosoft => Entry.Publisher?.IsVerifiedMicrosoft == true;
    public string StateText => Entry.State == StartupEntryState.NotApplicable ? "Sempre executa" : IsEnabled ? "Ativo" : "Desativado";
    public object StateTone => Entry.State == StartupEntryState.NotApplicable ? Severity.Info : IsEnabled ? true : Severity.Info;
    public string ToggleText => IsEnabled ? "Desativar na inicialização" : "Ativar na inicialização";
    public bool CanToggle => Entry.CanToggle;
    public string AdminNote => Entry.ToggleRequiresElevation
        ? "Este item vale para todos os usuários do PC: o Windows vai pedir permissão de administrador (UAC). Dá para desfazer em “Alterações”."
        : "Vale só para a sua conta e não precisa de administrador. Dá para desfazer em “Alterações”.";
    public string AdminNoteTech => Entry.ToggleRequiresElevation ? "Local de máquina (HKLM/pasta comum): a alteração passa pelo RBM Agent." : "Local do usuário (HKCU/pasta do usuário): alterado sem privilégios.";
    public string NoToggleReason => Entry.Location is StartupLocation.HkcuRunOnce or StartupLocation.HklmRunOnce or StartupLocation.HklmRunOnce32
        ? "Entradas RunOnce executam uma única vez e o Windows não consulta o estado StartupApproved para elas, então não há forma reversível de desabilitá-las. O RBM não exclui entradas."
        : "Entradas definidas por Política (Policies\\Explorer\\Run) são impostas pelo administrador/GPO e não podem ser desabilitadas de forma reversível. O RBM não exclui entradas.";
    public string DisabledAt => Entry.DisabledAtUtc is { } d ? $"Desabilitado em {SysUi.When(d)}" : "";
    public string Key => $"{Entry.Location}|{Entry.Name}";
}

public sealed partial class ServiceRow : ObservableObject
{
    public ServiceRow(ServiceEntry e)
    {
        Entry = e;
        _status = e.Status;
        _startMode = e.StartMode;
    }
    public ServiceEntry Entry { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StatusText), nameof(IsRunning), nameof(IsStopped), nameof(StatusTone))] private ServiceRunState _status;
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StartModeText))] private ServiceStartMode? _startMode;

    public string Name => Entry.Name;
    public string DisplayName => Entry.DisplayName;
    public string Description => string.IsNullOrWhiteSpace(Entry.Description) ? "Sem descrição." : Entry.Description!;
    public string StatusText => StartupText.RunState(Status);
    public object StatusTone => Status == ServiceRunState.Running ? true : Severity.Info;
    public bool IsRunning => Status == ServiceRunState.Running;
    public bool IsStopped => Status == ServiceRunState.Stopped;
    public string StartModeText => StartupText.StartMode(StartMode) + (Entry.IsTriggerStart == true ? " · gatilho" : "");
    public string Publisher => StartupText.Publisher(Entry.Publisher);
    public string Signature => StartupText.Signature(Entry.Publisher);
    public object SignatureTone => StartupText.SignatureTone(Entry.Publisher);
    public string Recommendation => StartupText.Recommendation(Entry.Recommendation.Kind);
    public object RecommendationTone => StartupText.RecommendationTone(Entry.Recommendation.Kind);
    public string RecommendationReason => Entry.Recommendation.Reason;
    public string PlainReason => StartupText.PlainReason(Entry.Recommendation, isService: true);
    public string PublisherLine => Entry.Publisher?.PublisherDisplay ?? (Entry.IsMicrosoft == true ? "Microsoft" : "Fabricante não identificado");
    public string Account => Entry.Account ?? "—";
    public string Path => Entry.EffectivePath ?? Entry.Command ?? "—";
    public bool IsVerifiedMicrosoft => Entry.IsMicrosoft == true;
    public bool IsProtected => Entry.IsProtected || Protection.IsProtectedService(Entry.Name);
    public bool CanChange => !IsProtected && !Entry.IsUserService && StartMode is not (ServiceStartMode.Boot or ServiceStartMode.System);
    public string ProtectionReason => IsProtected
        ? "Serviço essencial ou de segurança (lista de proteção do RBM): o tipo de inicialização não pode ser alterado e ele não pode ser parado por aqui. Desativá-lo pode quebrar atualizações, segurança, rede ou o próprio Windows."
        : Entry.IsUserService
            ? "Serviço por usuário (instância criada pelo Windows a partir de um modelo): altere o modelo pelo Windows, não a instância."
            : "Modo Boot/Sistema é exclusivo de drivers e não é alterado pelo RBM.";
    public string Dependencies => Entry.Dependencies.Count == 0 ? "Nenhuma" : string.Join(", ", Entry.Dependencies);
    public string Dependents => Entry.Dependents.Count == 0 ? "Nenhum" : string.Join(", ", Entry.Dependents);
    public string Key => Entry.Name;
}

public sealed partial class TaskRow : ObservableObject
{
    public TaskRow(ScheduledTaskEntry e)
    {
        Entry = e;
        _enabled = e.Enabled;
    }
    public ScheduledTaskEntry Entry { get; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(StateText), nameof(StateTone), nameof(ToggleText), nameof(CanToggle))] private bool _enabled;

    public string Name => Entry.Name;
    public string Folder => Entry.Folder;
    public string Path => Entry.Path;
    public string StateText => Enabled ? StartupText.TaskState(Entry.State == ScheduledTaskState.Disabled ? ScheduledTaskState.Ready : Entry.State) : "Desativada";
    public object StateTone => Enabled ? true : Severity.Info;
    public string LastRun => Entry.LastRunTime is { } t ? $"{SysUi.When(t)} · {Entry.LastResultDisplay}" : "Nunca executada";
    public string NextRun => SysUi.When(Entry.NextRunTime);
    public string Triggers => Entry.Triggers.Count == 0 ? "—" : string.Join("; ", Entry.Triggers);
    public string Actions => Entry.Actions.Count == 0 ? "—" : string.Join("; ", Entry.Actions.Select(a => (a.ResolvedPath ?? a.Path ?? a.ClassId ?? a.Kind) + (string.IsNullOrWhiteSpace(a.Arguments) ? "" : " " + a.Arguments)));
    public string Author => Entry.Author ?? "—";
    public string Publisher => StartupText.Publisher(Entry.Publisher);
    public string Signature => StartupText.Signature(Entry.Publisher);
    public object SignatureTone => StartupText.SignatureTone(Entry.Publisher);
    public string Recommendation => StartupText.Recommendation(Entry.Recommendation.Kind);
    public object RecommendationTone => StartupText.RecommendationTone(Entry.Recommendation.Kind);
    public string RecommendationReason => Entry.Recommendation.Reason;
    public string PlainReason => StartupText.PlainReason(Entry.Recommendation);
    public bool IsVerifiedMicrosoft => Entry.Publisher?.IsVerifiedMicrosoft == true || (Entry.Publisher is null && Entry.IsMicrosoftFolder && Entry.Actions.All(a => a.Kind != "Exec"));
    public bool IsProtected => Entry.IsProtected || ScheduledTaskManager.IsProtectedTask(Entry.Path);
    /// <summary>Tarefas protegidas podem ser HABILITADAS (restaurar), nunca desabilitadas.</summary>
    public bool CanToggle => !(IsProtected && Enabled);
    public string ToggleText => Enabled ? "Desativar tarefa" : "Ativar tarefa";
    public string ProtectionReason => "Tarefa de atualização/segurança do Windows (Windows Update, Defender, TPM, licenciamento…). O RBM não a desabilita: isso deixaria o sistema sem correções ou proteção.";
    public string Key => Entry.Path;
}

public sealed class OtherRow
{
    public OtherRow(AutorunEntry e) => Entry = e;
    public AutorunEntry Entry { get; }
    public string Category => StartupText.Category(Entry.Category);
    public string Location => Entry.Location;
    public string Name => Entry.Name;
    public string Image => Entry.ImagePath ?? Entry.Command ?? "—";
    public string Command => Entry.Command ?? "—";
    public string Publisher => StartupText.Publisher(Entry.Publisher);
    public string Signature => StartupText.Signature(Entry.Publisher);
    public object SignatureTone => StartupText.SignatureTone(Entry.Publisher);
    public string Recommendation => StartupText.Recommendation(Entry.Recommendation.Kind);
    public object RecommendationTone => StartupText.RecommendationTone(Entry.Recommendation.Kind);
    public string RecommendationReason => Entry.Recommendation.Reason;
    public string PlainReason => StartupText.PlainReason(Entry.Recommendation);
    public bool IsVerifiedMicrosoft => Entry.IsVerifiedMicrosoft;
    public string Deviation => Entry.IsDeviation ? "Difere do padrão do Windows" : "Padrão";
    public object DeviationTone => Entry.IsDeviation ? Severity.High : true;
    public string Note => Entry.Note ?? "";
}

/// <summary>
/// Alteração registrada em RbmDatabase ("startup_changes"). Kind: startup | serviceStart | serviceControl | task.
/// Key1 = local (startup) / nome do serviço / caminho da tarefa; Key2 = nome da entrada (startup).
/// </summary>
public sealed record StartupChangeRecord(
    string Id,
    DateTimeOffset At,
    string Kind,
    string Target,
    string Key1,
    string? Key2,
    string Previous,
    string New,
    string Via,
    bool CanRevert,
    bool Reverted = false,
    DateTimeOffset? RevertedAt = null)
{
    public const string Collection = "startup_changes";

    public string KindText => Kind switch
    {
        "startup" => "Inicialização",
        "serviceStart" => "Tipo de inicialização do serviço",
        "serviceControl" => "Estado do serviço",
        "task" => "Tarefa agendada",
        _ => Kind,
    };
    public string WhenText => SysUi.When(At);
    public string ChangeText => $"{Pretty(Previous)} → {Pretty(New)}";
    public string StatusText => Reverted ? $"Revertida em {SysUi.When(RevertedAt)}" : CanRevert ? "Ativa" : "Sem reversão (reinício)";
    public bool ShowRevert => CanRevert && !Reverted;

    private string Pretty(string v) => Kind switch
    {
        "startup" or "task" => v == "True" ? "ativado" : v == "False" ? "desativado" : v,
        "serviceStart" => Enum.TryParse<ServiceStartMode>(v, out var m) ? ServiceManager.Display(m) : v,
        "serviceControl" => Enum.TryParse<ServiceRunState>(v, out var s) ? StartupText.RunState(s) : v,
        _ => v,
    };
}
