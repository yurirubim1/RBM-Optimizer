using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Rbm.Core;

namespace Rbm.UI.Infrastructure;

public sealed class BoolToVisibility : IValueConverter
{
    public bool Invert { get; set; }
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        bool b = value switch { bool x => x, int i => i > 0, string s => !string.IsNullOrWhiteSpace(s), null => false, _ => true };
        if (Invert) b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public sealed class InverseBool : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => value is bool b ? !b : value;
}

/// <summary>Bytes → texto legível (KB/MB/GB).</summary>
public sealed class BytesConverter : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => Fmt.Bytes(System.Convert.ToDouble(value ?? 0));
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Gravidade/risco/grau → pincel do tema.</summary>
public sealed class LevelBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        string key = value switch
        {
            Severity s => s switch { Severity.Critical or Severity.High => "B.Danger", Severity.Medium => "B.Warning", Severity.Low => "B.Info", _ => "B.TextMuted" },
            RiskLevel r => r switch { RiskLevel.High => "B.Danger", RiskLevel.Medium => "B.Warning", RiskLevel.Low => "B.Info", _ => "B.Success" },
            EvidenceGrade g => g switch { EvidenceGrade.A => "B.Success", EvidenceGrade.B => "B.Cyan", EvidenceGrade.C => "B.Warning", EvidenceGrade.D => "B.TextMuted", _ => "B.Danger" },
            Confidence cf => cf switch { Confidence.High => "B.Success", Confidence.Medium => "B.Warning", _ => "B.TextMuted" },
            ImpactLevel i => i switch { ImpactLevel.High => "B.Success", ImpactLevel.Medium => "B.Cyan", ImpactLevel.Low => "B.Info", _ => "B.TextMuted" },
            bool b => b ? "B.Success" : "B.Danger",
            _ => "B.TextMuted",
        };
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

/// <summary>Score 0–100 → pincel (verde/ciano/amarelo/vermelho).</summary>
public sealed class ScoreBrush : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c)
    {
        if (value is not double d || double.IsNaN(d)) return Application.Current.TryFindResource("B.TextMuted")!;
        string key = d >= 85 ? "B.Success" : d >= 65 ? "B.Cyan" : d >= 45 ? "B.Warning" : "B.Danger";
        return Application.Current.TryFindResource(key)!;
    }
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}

public static class Fmt
{
    public static string Bytes(double b)
    {
        if (double.IsNaN(b)) return "—";
        string[] u = { "B", "KB", "MB", "GB", "TB" };
        int i = 0;
        while (Math.Abs(b) >= 1024 && i < u.Length - 1) { b /= 1024; i++; }
        return $"{b:0.#} {u[i]}";
    }
    public static string Rate(double bytesPerSec) => Bytes(bytesPerSec) + "/s";
    public static string Pt(this Severity s) => s switch { Severity.Critical => "Crítico", Severity.High => "Alto", Severity.Medium => "Médio", Severity.Low => "Baixo", _ => "Info" };
    public static string Pt(this RiskLevel r) => r switch { RiskLevel.High => "Alto", RiskLevel.Medium => "Médio", RiskLevel.Low => "Baixo", _ => "Mínimo" };
    public static string Pt(this Confidence c) => c switch { Confidence.High => "Alta", Confidence.Medium => "Média", _ => "Baixa" };
    public static string Pt(this ImpactLevel i) => i switch { ImpactLevel.High => "Alto", ImpactLevel.Medium => "Médio", ImpactLevel.Low => "Baixo", ImpactLevel.Negligible => "Desprezível", _ => "Desconhecido" };
    public static string Pt(this RestartRequirement r) => r switch { RestartRequirement.Reboot => "Reinicialização", RestartRequirement.SignOut => "Sair da conta", RestartRequirement.ExplorerRestart => "Reiniciar Explorer", RestartRequirement.AppRestart => "Reiniciar app", _ => "Nenhuma" };
    public static string GradeText(EvidenceGrade g) => g switch
    {
        EvidenceGrade.A => "A — benefício demonstrado", EvidenceGrade.B => "B — plausível, depende do sistema", EvidenceGrade.C => "C — experimental",
        EvidenceGrade.D => "D — sem benefício demonstrado", _ => "E — obsoleto/contraindicado",
    };
    public static string Duration(TimeSpan t) => t.TotalDays >= 1 ? $"{(int)t.TotalDays}d {t.Hours}h {t.Minutes}min" : t.TotalHours >= 1 ? $"{t.Hours}h {t.Minutes}min" : $"{t.Minutes}min {t.Seconds}s";
}

/// <summary>Enums do domínio → texto pt-BR.</summary>
public sealed class PtText : IValueConverter
{
    public object Convert(object value, Type t, object p, CultureInfo c) => value switch
    {
        Severity s => s.Pt(),
        RiskLevel r => r.Pt(),
        Confidence cf => cf.Pt(),
        ImpactLevel i => i.Pt(),
        RestartRequirement rr => rr.Pt(),
        EffortLevel e => e switch { EffortLevel.Trivial => "Trivial", EffortLevel.Low => "Baixo", EffortLevel.Medium => "Médio", _ => "Alto" },
        EvidenceGrade g => Fmt.GradeText(g),
        bool b => b ? "Sim" : "Não",
        null => "—",
        _ => value.ToString() ?? "",
    };
    public object ConvertBack(object value, Type t, object p, CultureInfo c) => throw new NotSupportedException();
}
