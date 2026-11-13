using Rbm.Core.Telemetry;

namespace Rbm.Processes;

/// <summary>Identidade estável de um processo: PID + hora de criação (FILETIME UTC). Protege contra reutilização de PID.</summary>
public readonly record struct ProcessKey(int Pid, long CreateTime)
{
    public override string ToString() => $"{Pid}@{CreateTime}";
}

/// <summary>Dados brutos de um processo numa única chamada a NtQuerySystemInformation (sem deltas).</summary>
public readonly record struct RawProcessInfo(
    int Pid, int ParentPid, string Name, long CreateTime, int SessionId,
    int Threads, int Handles, int BasePriority,
    long WorkingSetBytes, long PrivateBytes, long PeakWorkingSetBytes,
    long KernelTime100ns, long UserTime100ns,
    long ReadTransferBytes, long WriteTransferBytes, long OtherTransferBytes,
    bool CountersAvailable)
{
    public ProcessKey Key => new(Pid, CreateTime);
}

/// <summary>
/// Processo numa amostra. Campos de taxa (CPU%, IO/s) são null quando ainda não há amostra anterior
/// do mesmo processo (primeira amostra) ou quando o contador não está disponível — nunca são estimados.
/// </summary>
public sealed record ProcessEntry(
    int Pid,
    int ParentPid,
    string Name,
    long CreateTime,
    int SessionId,
    int Threads,
    int Handles,
    int BasePriority,
    long WorkingSetBytes,
    long PrivateBytes,
    long PeakWorkingSetBytes,
    TimeSpan KernelTime,
    TimeSpan UserTime,
    long ReadTransferBytes,
    long WriteTransferBytes,
    long OtherTransferBytes,
    double? CpuPercent,
    double? IoReadBytesPerSec,
    double? IoWriteBytesPerSec,
    double? IoOtherBytesPerSec,
    string? ImagePath)
{
    public ProcessKey Key => new(Pid, CreateTime);

    /// <summary>Hora de criação em UTC (null para Idle/System e processos sem hora conhecida).</summary>
    public DateTime? CreateTimeUtc => CreateTime > 0 ? DateTime.FromFileTimeUtc(CreateTime) : null;

    public double? IoTotalBytesPerSec => IoReadBytesPerSec is { } r && IoWriteBytesPerSec is { } w ? r + w : null;
}

/// <summary>Resultado de uma amostragem de todos os processos.</summary>
public sealed record ProcessSnapshot(
    DateTimeOffset Timestamp,
    IReadOnlyList<ProcessEntry> Processes,
    // Intervalo desde a amostra anterior (null na primeira amostra: taxas indisponíveis).
    TimeSpan? Interval,
    int LogicalProcessors,
    // "NtQuerySystemInformation" ou "System.Diagnostics.Process (fallback)".
    string Source,
    double CollectionMilliseconds,
    string? Warning)
{
    public bool RatesAvailable => Interval is not null;

    /// <summary>
    /// Converte para o contrato <see cref="ProcessSample"/> do Core. Atenção: o contrato não aceita null,
    /// então taxas indisponíveis (primeira amostra) viram 0 — use <see cref="RatesAvailable"/> para distinguir.
    /// </summary>
    public IReadOnlyList<ProcessSample> ToProcessSamples()
    {
        var list = new ProcessSample[Processes.Count];
        for (int i = 0; i < list.Length; i++)
        {
            var p = Processes[i];
            list[i] = new ProcessSample(p.Pid, p.Name, p.CpuPercent ?? 0, p.WorkingSetBytes, p.PrivateBytes,
                p.IoReadBytesPerSec ?? 0, p.IoWriteBytesPerSec ?? 0, p.Threads, p.Handles, p.ParentPid,
                p.ImagePath, p.CreateTimeUtc?.ToLocalTime(), p.BasePriority);
        }
        return list;
    }
}

public sealed record ProcessSamplerOptions
{
    /// <summary>Resolve o caminho da imagem (QueryFullProcessImageName) apenas para processos novos; resultado em cache.</summary>
    public bool ResolveImagePaths { get; init; } = true;
}
