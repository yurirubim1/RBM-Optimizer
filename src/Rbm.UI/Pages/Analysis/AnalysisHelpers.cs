using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Rbm.Core.Agent;
using Rbm.Security;

namespace Rbm.UI.Pages.Analysis;

/// <summary>Valor == parâmetro → true (usado com RadioButton "Segment" para abas). ConvertBack devolve o parâmetro.</summary>
public sealed class EqualsToBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => string.Equals(value?.ToString(), p?.ToString(), StringComparison.Ordinal);
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is true ? p : Binding.DoNothing;
}

/// <summary>Valor == parâmetro → Visible.</summary>
public sealed class EqualsToVisibility : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) =>
        string.Equals(value?.ToString(), p?.ToString(), StringComparison.Ordinal) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Chave de pincel do tema ("B.Success" etc.) → Brush.</summary>
public sealed class BrushKeyConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => AnalysisUi.Brush(value as string ?? "B.TextMuted");
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public static class AnalysisUi
{
    public static Brush Brush(string key) => Application.Current?.TryFindResource(key) as Brush ?? Brushes.Gray;

    /// <summary>Microssegundos → texto (µs abaixo de 1 ms; ms acima).</summary>
    public static string Us(double us) =>
        !double.IsFinite(us) ? "—" : us >= 1000 ? $"{us / 1000:0.00} ms" : $"{us:0.#} µs";

    public static string Num(double v, string fmt = "0.##") => double.IsFinite(v) ? v.ToString(fmt, CultureInfo.CurrentCulture) : "—";

    public static string? AgentError(IAgentClient agent) => (agent as AgentClient)?.LastError;

    /// <summary>Aba inicial opcional ("--tab chave" ou variável RBM_UI_TAB), usada apenas para verificação visual.</summary>
    public static string? StartupTab(params string[] allowed)
    {
        var args = Environment.GetCommandLineArgs();
        int i = Array.IndexOf(args, "--tab");
        var tab = i >= 0 && i + 1 < args.Length ? args[i + 1] : Environment.GetEnvironmentVariable("RBM_UI_TAB");
        return tab is not null && allowed.Contains(tab) ? tab : null;
    }

    /// <summary>
    /// Chama uma operação do agente de forma que o usuário possa "parar de aguardar" sem dessincronizar o pipe.
    /// O agente processa uma requisição por vez e não tem cancelamento remoto: ao abandonar, a operação continua
    /// no processo elevado até terminar (limitada pelo próprio timeout) e o resultado é descartado.
    /// Retorna null quando o usuário abandonou a espera.
    /// </summary>
    public static async Task<AgentResponse?> InvokeAbandonableAsync(IAgentClient agent, string op, object payload, TimeSpan timeout, CancellationToken stopWaiting)
    {
        var call = agent.InvokeAsync(op, payload, timeout);
        var gate = new TaskCompletionSource();
        using (stopWaiting.Register(() => gate.TrySetResult()))
        {
            var first = await Task.WhenAny(call, gate.Task);
            if (first != call)
            {
                _ = call.ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted);
                return null;
            }
        }
        return await call;
    }
}
