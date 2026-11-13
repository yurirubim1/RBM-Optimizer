using System.IO;
using Rbm.Processes;
using Rbm.Security;
using Rbm.UI.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Rbm.UI.Pages.SystemPages;

/// <summary>Linha da tabela de processos. Atualizada no lugar a cada amostra (preserva seleção e rolagem).</summary>
public sealed partial class ProcessRow : ObservableObject
{
    public ProcessRow(ProcessEntry e)
    {
        Key = e.Key;
        Pid = e.Pid;
        Name = e.Name;
        CreateTime = e.CreateTime;
        CreateTimeUtc = e.CreateTimeUtc;
        string bare = Path.GetFileNameWithoutExtension(e.Name);
        IsManualOnly = Protection.ManualOnlyProcesses.Contains(bare);
        IsCritical = !IsManualOnly && (e.Pid is 0 or 4 || Protection.IsCriticalProcess(e.Name));
        IsSelf = e.Pid == Environment.ProcessId;
        Update(e);
    }

    public ProcessKey Key { get; }
    public int Pid { get; }
    public string Name { get; }
    public long CreateTime { get; }
    public DateTime? CreateTimeUtc { get; }
    /// <summary>Processo crítico do Windows (ações bloqueadas).</summary>
    public bool IsCritical { get; }
    /// <summary>Processo que afeta a sessão (explorer, dwm…): ações exigem confirmação explícita.</summary>
    public bool IsManualOnly { get; }
    public bool IsSelf { get; }
    public bool IsProtected => IsCritical || IsSelf;

    [ObservableProperty] private int _parentPid;
    [ObservableProperty] private string? _imagePath;
    [ObservableProperty] private double _cpu = -1;
    [ObservableProperty] private string _cpuText = "—";
    [ObservableProperty] private long _workingSet;
    [ObservableProperty] private long _privateBytes;
    [ObservableProperty] private double _io = -1;
    [ObservableProperty] private string _ioText = "—";
    [ObservableProperty] private double _gpu = -1;
    [ObservableProperty] private string _gpuText = "—";
    [ObservableProperty] private int _threads;
    [ObservableProperty] private int _handles;
    [ObservableProperty] private int _depth;
    [ObservableProperty] private int _treeOrder;
    [ObservableProperty] private bool _parentMissing;
    /// <summary>Descrição do arquivo (FileDescription), resolvida em segundo plano — nome amigável do programa.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(DisplayName))] private string? _description;

    /// <summary>Nome amigável: descrição do arquivo, nome conhecido do Windows ou o nome do executável.</summary>
    public string DisplayName => !string.IsNullOrWhiteSpace(Description) ? Description! : ProcessText.KnownName(Name) ?? Name;

    public string ParentText => ParentPid > 0 ? ParentPid.ToString() : "—";
    partial void OnParentPidChanged(int value) => OnPropertyChanged(nameof(ParentText));
    partial void OnDepthChanged(int value) => OnPropertyChanged(nameof(Indent));
    /// <summary>Recuo do nome no modo árvore.</summary>
    public System.Windows.Thickness Indent => new(Depth * 12, 0, 0, 0);

    public void Update(ProcessEntry e)
    {
        ParentPid = e.ParentPid;
        if (e.ImagePath is not null) ImagePath = e.ImagePath;
        Cpu = e.CpuPercent ?? -1;
        CpuText = e.CpuPercent is { } c ? (c < 0.05 ? "0" : c.ToString(c < 10 ? "0.0" : "0")) + "%" : "—";
        WorkingSet = e.WorkingSetBytes;
        PrivateBytes = e.PrivateBytes;
        Io = e.IoTotalBytesPerSec ?? -1;
        IoText = e.IoTotalBytesPerSec is { } io ? (io < 1 ? "0" : Fmt.Rate(io)) : "—";
        Threads = e.Threads;
        Handles = e.Handles;
    }

    /// <summary>GPU (maior engine, como o Gerenciador de Tarefas). null = contador indisponível.</summary>
    public void SetGpu(double? percent)
    {
        if (percent is not { } g) { Gpu = -1; GpuText = "—"; return; }
        Gpu = g;
        GpuText = g < 0.05 ? "0" : g.ToString("0.#") + "%";
    }
}

/// <summary>Processador lógico selecionável no editor de afinidade.</summary>
public sealed partial class CoreToggle : ObservableObject
{
    public CoreToggle(int index, string label) { Index = index; Label = label; }
    public int Index { get; }
    public string Label { get; }
    [ObservableProperty] private bool _isChecked;
}

public sealed record PriorityOption(ProcessPriority? Value, string Label)
{
    public override string ToString() => Label;
}

public sealed record AffinityPresetOption(AffinityPreset Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>Linha da lista de regras (somente exibição).</summary>
public sealed record ProcessRuleRow(ProcessRule Rule, string PriorityText, string AffinityText, string ExceptionsText, IReadOnlyList<string> Errors)
{
    public bool IsValid => Errors.Count == 0;
    public string Status => !IsValid ? "Inválida (desabilitada)" : Rule.Enabled ? "Ativa" : "Desabilitada";
}

/// <summary>Linha do relatório de impacto em segundo plano.</summary>
public sealed record ProcessImpactRow(
    string Name, int Pid, string? ImagePath, int Samples, string ObservedFor,
    double AvgCpu, double PeakCpu, string AvgIo, string WsGrowth, string PrivateGrowth, string PrivateEnd,
    bool PossibleLeak, double Score, string Notes);

/// <summary>
/// "Vaga" fixa da lista de mais pesados (atualizada no lugar a cada amostra para não piscar).
/// Agrupa as instâncias do mesmo executável, como o Gerenciador de Tarefas.
/// </summary>
public sealed partial class ProcessGroupRow : ObservableObject
{
    [ObservableProperty] private bool _isUsed;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _subtitle = "";
    [ObservableProperty] private string _valueText = "";
    [ObservableProperty] private double _share;
    [ObservableProperty] private bool _isProtected;
    [ObservableProperty] private string? _toolTip;
    /// <summary>Instância mais pesada do grupo (selecionada ao clicar).</summary>
    public ProcessRow? Primary { get; set; }
}

/// <summary>Ponte para bindings em elementos fora da árvore visual (ex.: visibilidade de colunas do DataGrid).</summary>
public sealed class BindingProxy : System.Windows.Freezable
{
    public static readonly System.Windows.DependencyProperty DataProperty =
        System.Windows.DependencyProperty.Register(nameof(Data), typeof(object), typeof(BindingProxy));
    public object? Data { get => GetValue(DataProperty); set => SetValue(DataProperty, value); }
    protected override System.Windows.Freezable CreateInstanceCore() => new BindingProxy();
}

public static class ProcessText
{
    /// <summary>Processos do Windows sem arquivo/descrição legível.</summary>
    public static string? KnownName(string exe) => exe switch
    {
        "System" => "Núcleo do Windows (System)",
        "Registry" => "Registro do Windows",
        "Memory Compression" => "Compressão de memória do Windows",
        "Secure System" => "Sistema seguro do Windows",
        _ => null,
    };

    public static string Priority(ProcessPriority? p) => p is { } v ? ProcessPriorityMap.Display(v) : "—";

    public static string Preset(AffinityPreset p) => p switch
    {
        AffinityPreset.None => "Não alterar",
        AffinityPreset.CustomMask => "Máscara personalizada",
        AffinityPreset.PerformanceCoresOnly => "Somente núcleos de desempenho (P-cores)",
        AffinityPreset.ExcludeEfficiencyCores => "Excluir núcleos de eficiência (E-cores)",
        AffinityPreset.OneThreadPerCore => "Um thread por núcleo (sem SMT)",
        AffinityPreset.FirstCacheDomain => "Primeiro domínio L3 (CCD/V-Cache)",
        _ => p.ToString(),
    };

    public static string Mask(ulong mask)
    {
        var cpus = new List<string>();
        int i = 0, start = -1;
        for (; i <= 64; i++)
        {
            bool on = i < 64 && (mask & (1UL << i)) != 0;
            if (on && start < 0) start = i;
            if (!on && start >= 0) { cpus.Add(start == i - 1 ? $"{start}" : $"{start}–{i - 1}"); start = -1; }
        }
        return cpus.Count == 0 ? "nenhuma CPU" : "CPUs " + string.Join(", ", cpus);
    }

    public static string SlopeMb(double bytesPerMin) => double.IsFinite(bytesPerMin)
        ? (bytesPerMin >= 0 ? "+" : "") + (bytesPerMin / 1048576.0).ToString("0.0") + " MB/min" : "—";
}
