using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using Rbm.Core;
using Rbm.Core.Telemetry;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Rbm.UI.Infrastructure;

/// <summary>Base de todas as páginas.</summary>
public abstract partial class PageViewModel : ObservableObject
{
    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string? _busyText;
    public abstract string Key { get; }
    public virtual string Title => Loc.T("nav." + Key);
    public virtual string Subtitle => Loc.T("sub." + Key);
    /// <summary>Chamado ao entrar na página (UI thread).</summary>
    public virtual Task OnNavigatedToAsync() => Task.CompletedTask;
    /// <summary>Chamado ao sair da página (UI thread).</summary>
    public virtual void OnNavigatedFrom() { }

    protected async Task RunBusy(string text, Func<Task> work)
    {
        IsBusy = true; BusyText = text;
        try { await work(); }
        catch (Exception ex) { App.Notify.Error("Erro", ex.Message); App.Log(ex, $"Página {Key}: {text}"); }
        finally { IsBusy = false; BusyText = null; }
    }
}

public sealed record NavItem(string Key, string Icon, string Group, Type ViewModelType, bool Advanced = false)
{
    public string Title => Loc.T("nav." + Key);
}

/// <summary>Notificações não intrusivas (toasts) exibidas no canto da janela.</summary>
public sealed partial class Notifier : ObservableObject
{
    public ObservableCollection<Toast> Items { get; } = new();

    public void Info(string title, string message) => Push(title, message, "info");
    public void Success(string title, string message) => Push(title, message, "success");
    public void Warning(string title, string message) => Push(title, message, "warning");
    public void Error(string title, string message) => Push(title, message, "error");

    private void Push(string title, string message, string kind)
    {
        Ui.Post(() =>
        {
            var t = new Toast(title, message, kind);
            t.CloseCommand = new RelayCommand(() => Items.Remove(t));
            Items.Add(t);
            while (Items.Count > 4) Items.RemoveAt(0);
            var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(kind == "error" ? 9 : 5) };
            timer.Tick += (_, _) => { timer.Stop(); Items.Remove(t); };
            timer.Start();
        });
    }
}

public sealed class Toast
{
    public Toast(string title, string message, string kind) { Title = title; Message = message; Kind = kind; }
    public string Title { get; }
    public string Message { get; }
    public string Kind { get; }
    public string Icon => Kind switch { "success" => "", "warning" => "", "error" => "", _ => "" };
    public IRelayCommand? CloseCommand { get; set; }
}

/// <summary>Modal de confirmação proporcional ao risco (exibido como overlay na janela principal).</summary>
public sealed partial class DialogService : ObservableObject
{
    private TaskCompletionSource<bool>? _tcs;
    [ObservableProperty] private bool _isOpen;
    [ObservableProperty] private string _title = "";
    [ObservableProperty] private string _message = "";
    [ObservableProperty] private string? _details;
    [ObservableProperty] private string _confirmText = "Confirmar";
    [ObservableProperty] private bool _isDanger;
    [ObservableProperty] private string? _requiredPhrase;
    [ObservableProperty] private string _typedPhrase = "";
    [ObservableProperty] private bool _showCancel = true;

    public bool CanConfirm => string.IsNullOrEmpty(RequiredPhrase) || string.Equals(TypedPhrase.Trim(), RequiredPhrase, StringComparison.OrdinalIgnoreCase);
    partial void OnTypedPhraseChanged(string value) => OnPropertyChanged(nameof(CanConfirm));
    partial void OnRequiredPhraseChanged(string? value) => OnPropertyChanged(nameof(CanConfirm));

    /// <summary>
    /// Pede confirmação. <paramref name="requiredPhrase"/> exige digitar uma palavra (operações de alto risco).
    /// </summary>
    public Task<bool> ConfirmAsync(string title, string message, string? details = null, string confirmText = "Confirmar", bool danger = false, string? requiredPhrase = null)
    {
        _tcs?.TrySetResult(false);
        _tcs = new TaskCompletionSource<bool>();
        Title = title; Message = message; Details = details; ConfirmText = confirmText; IsDanger = danger;
        RequiredPhrase = requiredPhrase; TypedPhrase = ""; ShowCancel = true;
        IsOpen = true;
        return _tcs.Task;
    }

    public Task AlertAsync(string title, string message, string? details = null)
    {
        var t = ConfirmAsync(title, message, details, "OK");
        ShowCancel = false;
        return t;
    }

    [RelayCommand]
    private void Confirm() { if (!CanConfirm) return; IsOpen = false; _tcs?.TrySetResult(true); }

    [RelayCommand]
    private void Cancel() { IsOpen = false; _tcs?.TrySetResult(false); }
}

/// <summary>Tarefas longas em andamento (exibidas na barra de status).</summary>
public sealed partial class TaskTracker : ObservableObject
{
    public ObservableCollection<string> Running { get; } = new();
    public string Summary => Running.Count == 0 ? "Nenhuma tarefa em andamento" : Running.Count == 1 ? Running[0] : $"{Running.Count} tarefas em andamento";
    public bool Any => Running.Count > 0;

    public IDisposable Begin(string name)
    {
        Ui.Post(() => { Running.Add(name); Changed(); });
        return new Scope(() => Ui.Post(() => { Running.Remove(name); Changed(); }));
    }
    private void Changed() { OnPropertyChanged(nameof(Summary)); OnPropertyChanged(nameof(Any)); }

    private sealed class Scope(Action a) : IDisposable { public void Dispose() => a(); }
}

public static class Ui
{
    public static Dispatcher D => Application.Current.Dispatcher;
    public static void Post(Action a)
    {
        if (Application.Current is null) return;
        if (D.CheckAccess()) a(); else D.BeginInvoke(a, DispatcherPriority.Background);
    }
}

/// <summary>
/// Ponte entre a telemetria (thread de fundo) e a UI: guarda séries circulares prontas para os gráficos
/// e notifica na thread de UI. Ouvintes só existem enquanto a página está visível.
/// </summary>
public sealed class LiveTelemetry
{
    private readonly ITelemetryService _svc;
    public const int Capacity = 120;
    private readonly Dictionary<string, Queue<double>> _series = new();
    public TelemetrySnapshot? Latest { get; private set; }
    public event Action<TelemetrySnapshot>? Updated;

    public LiveTelemetry(ITelemetryService svc)
    {
        _svc = svc;
        foreach (var s in svc.History) Record(s);
        svc.Sampled += (_, s) => Ui.Post(() => { Record(s); Latest = s; Updated?.Invoke(s); });
    }

    private void Push(string key, double v)
    {
        if (!_series.TryGetValue(key, out var q)) _series[key] = q = new Queue<double>(Capacity + 1);
        q.Enqueue(double.IsFinite(v) ? v : 0);
        while (q.Count > Capacity) q.Dequeue();
    }

    private void Record(TelemetrySnapshot s)
    {
        Push("cpu", s.CpuTotalPercent);
        for (int i = 0; i < s.CpuPerCorePercent.Length; i++) Push("core" + i, s.CpuPerCorePercent[i]);
        Push("clock", s.CpuEffectiveClockMHz.Available ? s.CpuEffectiveClockMHz.Value : 0);
        Push("ram", s.MemUsedPercent);
        Push("commit", s.CommitLimitBytes > 0 ? s.CommitBytes * 100.0 / s.CommitLimitBytes : 0);
        Push("hardfaults", s.HardFaultsPerSec);
        Push("dpc", s.DpcTimePercent);
        Push("isr", s.InterruptTimePercent);
        if (s.CpuTemperatureC.Available) Push("cputemp", s.CpuTemperatureC.Value);
        var g = s.Gpus.OrderByDescending(x => x.DedicatedTotalBytes).FirstOrDefault();
        if (g is not null)
        {
            Push("gpu", g.Utilization3DPercent);
            Push("gpumax", g.UtilizationMaxEnginePercent);
            Push("vram", g.DedicatedTotalBytes > 0 ? g.DedicatedUsedBytes * 100.0 / g.DedicatedTotalBytes : 0);
            if (g.TemperatureC.Available) Push("gputemp", g.TemperatureC.Value);
            if (g.HotspotC.Available) Push("gpuhot", g.HotspotC.Value);
            if (g.PowerW.Available) Push("gpupower", g.PowerW.Value);
            if (g.CoreClockMHz.Available) Push("gpuclock", g.CoreClockMHz.Value);
        }
        Push("diskread", s.Disks.Sum(d => d.ReadBytesPerSec) / 1048576.0);
        Push("diskwrite", s.Disks.Sum(d => d.WriteBytesPerSec) / 1048576.0);
        Push("diskactive", s.Disks.Select(d => d.ActivePercent).DefaultIfEmpty(0).Max());
        foreach (var d in s.Disks) Push("disk:" + d.Instance, d.ActivePercent);
        Push("netrx", s.NetRxBytesPerSec * 8 / 1e6);
        Push("nettx", s.NetTxBytesPerSec * 8 / 1e6);
    }

    public double[] Series(string key) => _series.TryGetValue(key, out var q) ? q.ToArray() : Array.Empty<double>();
    public bool Has(string key) => _series.ContainsKey(key);
    public IReadOnlyList<TelemetrySnapshot> History => _svc.History;
}
