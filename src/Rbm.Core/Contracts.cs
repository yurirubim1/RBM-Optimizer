using Rbm.Core.Hardware;
using Rbm.Core.Telemetry;
using Rbm.Core.Tweaks;

namespace Rbm.Core;

/// <summary>Coleta do inventário de hardware/sistema.</summary>
public interface ISystemInventory
{
    SystemProfile? Current { get; }
    Task<SystemProfile> CollectAsync(CancellationToken ct = default);
}

/// <summary>Serviço de telemetria em tempo real (amostragem periódica de baixo overhead).</summary>
public interface ITelemetryService : IDisposable
{
    TelemetrySnapshot? Latest { get; }
    /// <summary>Janela circular das últimas amostras (mais antiga primeiro).</summary>
    IReadOnlyList<TelemetrySnapshot> History { get; }
    TimeSpan Interval { get; set; }
    bool IsRunning { get; }
    event EventHandler<TelemetrySnapshot>? Sampled;
    void Start();
    void Stop();
}

/// <summary>Fonte complementar de sensores (temperaturas, potência, clocks).</summary>
public interface ISensorProvider : IDisposable
{
    string Name { get; }
    bool IsAvailable { get; }
    string StatusMessage { get; }
    void Initialize(SystemProfile profile);
    void Enrich(TelemetrySnapshot snapshot);
}

/// <summary>Amostragem de processos.</summary>
public interface IProcessMonitor
{
    IReadOnlyList<ProcessSample> Sample();
}

/// <summary>Leitura/escrita de um tipo de alvo de configuração.</summary>
public interface ISettingProvider
{
    bool CanHandle(SettingTarget target);
    SettingValue Read(SettingTarget target);
    /// <summary>Escreve <paramref name="value"/> no alvo (usado tanto para aplicar quanto para reverter).</summary>
    void Write(SettingTarget target, SettingValue value);
    /// <summary>Resolve referências dinâmicas (ex.: esquema de energia "active").</summary>
    SettingTarget Resolve(SettingTarget target) => target;
}

/// <summary>Persistência do histórico de alterações (implementado em Rbm.Data).</summary>
public interface IChangeJournal
{
    Task SaveAsync(TweakChangeRecord record, CancellationToken ct = default);
    Task<IReadOnlyList<TweakChangeRecord>> ListAsync(int limit = 500, CancellationToken ct = default);
    Task<TweakChangeRecord?> GetAsync(Guid changeId, CancellationToken ct = default);
    /// <summary>Última alteração ativa (não revertida) de cada tweak.</summary>
    Task<IReadOnlyList<TweakChangeRecord>> ActiveChangesAsync(CancellationToken ct = default);
}

/// <summary>Configurações do aplicativo (chave/valor persistido).</summary>
public interface ISettingsStore
{
    T Get<T>(string key, T fallback);
    void Set<T>(string key, T value);
}

/// <summary>Caminhos de dados locais do aplicativo.</summary>
public static class RbmPaths
{
    public static string DataRoot { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RBMOptimizer");
    public static string Logs => Ensure(Path.Combine(DataRoot, "logs"));
    public static string Backups => Ensure(Path.Combine(DataRoot, "backups"));
    public static string Reports => Ensure(Path.Combine(DataRoot, "reports"));
    public static string Tools => Ensure(Path.Combine(DataRoot, "tools"));
    public static string Database => Path.Combine(Ensure(DataRoot), "rbm.db");
    /// <summary>Logs de auditoria do agente (gravados em ProgramData, área controlada por administradores).</summary>
    public static string AgentAudit => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "RBMOptimizer", "audit");

    public static string Ensure(string dir) { Directory.CreateDirectory(dir); return dir; }
}
