namespace Rbm.Security;

/// <summary>
/// Listas de proteção usadas por automações e pelo agente. Itens aqui NUNCA são alterados
/// por regras automáticas e exigem confirmação reforçada (ou são bloqueados) em ações manuais.
/// </summary>
public static class Protection
{
    /// <summary>Processos críticos: encerrar causa BSOD, logoff ou instabilidade grave.</summary>
    public static readonly IReadOnlySet<string> CriticalProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "Registry", "smss", "csrss", "wininit", "winlogon", "services", "lsass", "lsaiso",
        "svchost", "dwm", "fontdrvhost", "Memory Compression", "Secure System", "LsaIso", "sihost", "ctfmon",
        "MsMpEng", "NisSrv", "SecurityHealthService", "MpDefenderCoreService", "explorer", "conhost", "spoolsv",
        "audiodg", "WUDFHost", "dllhost", "RuntimeBroker", "StartMenuExperienceHost", "ShellExperienceHost",
    };

    /// <summary>Processos que só podem ser encerrados manualmente com confirmação (reiniciam sozinhos ou afetam a sessão).</summary>
    public static readonly IReadOnlySet<string> ManualOnlyProcesses = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "explorer", "dwm", "audiodg", "spoolsv", "StartMenuExperienceHost", "ShellExperienceHost", "RuntimeBroker",
    };

    /// <summary>Serviços essenciais ou de segurança: nunca desabilitados pelo RBM.</summary>
    public static readonly IReadOnlySet<string> ProtectedServices = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        // Segurança
        "WinDefend", "WdNisSvc", "Sense", "SecurityHealthService", "mpssvc", "BFE", "wscsvc", "SgrmBroker", "WdBoot", "WdFilter",
        "CryptSvc", "KeyIso", "SamSs", "VaultSvc", "EFS", "BDESVC", "TPM", "TrustedInstaller", "Appinfo", "AppIDSvc",
        // Atualizações (não desabilitar indiscriminadamente)
        "wuauserv", "UsoSvc", "WaaSMedicSvc", "BITS", "DoSvc",
        // Núcleo do sistema
        "RpcSs", "RpcEptMapper", "DcomLaunch", "PlugPlay", "Power", "LSM", "ProfSvc", "Schedule", "EventLog",
        "Winmgmt", "BrokerInfrastructure", "CoreMessagingRegistrar", "SystemEventsBroker", "TimeBrokerSvc",
        "StateRepository", "gpsvc", "UserManager", "Themes", "AudioSrv", "AudioEndpointBuilder", "Dhcp", "Dnscache",
        "nsi", "NlaSvc", "netprofm", "LanmanWorkstation", "Netman", "WlanSvc", "Wcmsvc", "iphlpsvc", "msiserver",
        "camsvc", "DeviceInstall", "DsmSvc", "FontCache", "ShellHWDetection", "Winmgmt", "W32Time", "VSS", "swprv",
        "CoreUIRegistrar", "seclogon", "sppsvc", "ClipSVC", "LicenseManager", "AppXSvc", "TokenBroker",
    };

    public static bool IsCriticalProcess(string name) => CriticalProcesses.Contains(Path.GetFileNameWithoutExtension(name));
    public static bool IsProtectedService(string name) => ProtectedServices.Contains(name);

    /// <summary>Prioridades permitidas. REALTIME é bloqueado (pode congelar o sistema).</summary>
    public static readonly IReadOnlySet<string> AllowedPriorities = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "Idle", "BelowNormal", "Normal", "AboveNormal", "High",
    };
}
