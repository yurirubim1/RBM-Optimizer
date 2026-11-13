using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace Rbm.UI.Controls;

/// <summary>Série de um gráfico de linhas.</summary>
public sealed class ChartSeries
{
    public string Name { get; init; } = "";
    public double[] Values { get; init; } = Array.Empty<double>();
    public Brush Stroke { get; init; } = Brushes.DeepSkyBlue;
    public bool Fill { get; init; } = true;
}

internal static class ChartUtil
{
    public static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    public static readonly Typeface FaceBold = new(new FontFamily("Segoe UI Variable Display, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
    public static readonly Brush Grid = Freeze(new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF)));
    public static readonly Brush Label = Freeze(new SolidColorBrush(Color.FromRgb(0x5D, 0x66, 0x7A)));
    public static readonly Brush Track = Freeze(new SolidColorBrush(Color.FromRgb(0x1F, 0x25, 0x32)));
    public static T Freeze<T>(T f) where T : Freezable { f.Freeze(); return f; }

    public static FormattedText Text(string s, double size, Brush brush, Visual v, bool bold = false) =>
        new(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, brush, VisualTreeHelper.GetDpi(v).PixelsPerDip);

    public static Brush FillFor(Brush stroke)
    {
        var c = stroke is SolidColorBrush sb ? sb.Color : stroke is GradientBrush gb && gb.GradientStops.Count > 0 ? gb.GradientStops[0].Color : Colors.DeepSkyBlue;
        var g = new LinearGradientBrush(Color.FromArgb(0x55, c.R, c.G, c.B), Color.FromArgb(0x00, c.R, c.G, c.B), 90);
        g.Freeze();
        return g;
    }
}

/// <summary>Mini gráfico de linha (sparkline) para cards de métricas.</summary>
public sealed class SparkChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(double[]), typeof(SparkChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(SparkChart), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(SparkChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(SparkChart), new FrameworkPropertyMetadata(60, FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var v = Values;
        if (v is null || v.Length < 2 || w <= 0 || h <= 0) return;
        double max = double.IsNaN(Maximum) ? Math.Max(1e-9, v.Max() * 1.15) : Maximum;
        int cap = Math.Max(Capacity, v.Length);
        double step = w / (cap - 1);
        double x0 = w - (v.Length - 1) * step;
        var line = new StreamGeometry();
        var area = new StreamGeometry();
        using (var g = line.Open())
        using (var a = area.Open())
        {
            for (int i = 0; i < v.Length; i++)
            {
                double y = h - Math.Clamp(v[i] / max, 0, 1) * (h - 2) - 1;
                var p = new Point(x0 + i * step, y);
                if (i == 0) { g.BeginFigure(p, false, false); a.BeginFigure(new Point(p.X, h), true, true); a.LineTo(p, false, false); }
                else { g.LineTo(p, true, true); a.LineTo(p, false, false); }
            }
            a.LineTo(new Point(w, h), false, false);
        }
        line.Freeze(); area.Freeze();
        dc.DrawGeometry(ChartUtil.FillFor(Stroke), null, area);
        dc.DrawGeometry(null, new Pen(Stroke, 1.6) { LineJoin = PenLineJoin.Round }, line);
    }
}

/// <summary>Gráfico de linhas com várias séries, grade e eixo Y.</summary>
public sealed class LineChart : FrameworkElement
{
    public static readonly DependencyProperty SeriesProperty = DependencyProperty.Register(nameof(Series), typeof(IReadOnlyList<ChartSeries>), typeof(LineChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(LineChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CapacityProperty = DependencyProperty.Register(nameof(Capacity), typeof(int), typeof(LineChart), new FrameworkPropertyMetadata(120, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty UnitProperty = DependencyProperty.Register(nameof(Unit), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelFormatProperty = DependencyProperty.Register(nameof(LabelFormat), typeof(string), typeof(LineChart), new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));

    public IReadOnlyList<ChartSeries>? Series { get => (IReadOnlyList<ChartSeries>?)GetValue(SeriesProperty); set => SetValue(SeriesProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public int Capacity { get => (int)GetValue(CapacityProperty); set => SetValue(CapacityProperty, value); }
    public string Unit { get => (string)GetValue(UnitProperty); set => SetValue(UnitProperty, value); }
    public string LabelFormat { get => (string)GetValue(LabelFormatProperty); set => SetValue(LabelFormatProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        if (w < 60 || h < 40) return;
        var series = Series ?? Array.Empty<ChartSeries>();
        double dataMax = series.SelectMany(s => s.Values).Where(double.IsFinite).DefaultIfEmpty(0).Max();
        double max = double.IsNaN(Maximum) ? NiceMax(dataMax) : Maximum;
        const double left = 44, bottom = 6, top = 6;
        double pw = w - left, ph = h - top - bottom;
        var gridPen = new Pen(ChartUtil.Grid, 1); gridPen.Freeze();

        for (int i = 0; i <= 4; i++)
        {
            double y = top + ph - ph * i / 4.0;
            dc.DrawLine(gridPen, new Point(left, Math.Round(y) + 0.5), new Point(w, Math.Round(y) + 0.5));
            var label = (max * i / 4.0).ToString(LabelFormat, CultureInfo.CurrentCulture) + Unit;
            var ft = ChartUtil.Text(label, 10, ChartUtil.Label, this);
            dc.DrawText(ft, new Point(left - ft.Width - 6, y - ft.Height / 2));
        }

        int cap = Math.Max(2, Capacity);
        double step = pw / (cap - 1);
        foreach (var s in series)
        {
            var v = s.Values;
            if (v.Length < 2) continue;
            int n = Math.Min(v.Length, cap);
            int start = v.Length - n;
            double x0 = left + pw - (n - 1) * step;
            var line = new StreamGeometry(); var area = new StreamGeometry();
            using (var g = line.Open())
            using (var a = area.Open())
            {
                for (int i = 0; i < n; i++)
                {
                    double val = double.IsFinite(v[start + i]) ? v[start + i] : 0;
                    var p = new Point(x0 + i * step, top + ph - Math.Clamp(val / max, 0, 1) * ph);
                    if (i == 0) { g.BeginFigure(p, false, false); a.BeginFigure(new Point(p.X, top + ph), true, true); a.LineTo(p, false, false); }
                    else { g.LineTo(p, true, true); a.LineTo(p, false, false); }
                }
                a.LineTo(new Point(left + pw, top + ph), false, false);
            }
            line.Freeze(); area.Freeze();
            if (s.Fill) dc.DrawGeometry(ChartUtil.FillFor(s.Stroke), null, area);
            dc.DrawGeometry(null, new Pen(s.Stroke, 1.7) { LineJoin = PenLineJoin.Round }, line);
        }
    }

    public static double NiceMax(double v)
    {
        if (v <= 0 || !double.IsFinite(v)) return 1;
        double exp = Math.Pow(10, Math.Floor(Math.Log10(v)));
        double f = v / exp;
        double nice = f <= 1 ? 1 : f <= 2 ? 2 : f <= 2.5 ? 2.5 : f <= 5 ? 5 : 10;
        return nice * exp;
    }
}

/// <summary>Gauge circular (arco de 270°).</summary>
public sealed class RingGauge : FrameworkElement
{
    public static readonly DependencyProperty ValueProperty = DependencyProperty.Register(nameof(Value), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(0.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(100.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty StrokeProperty = DependencyProperty.Register(nameof(Stroke), typeof(Brush), typeof(RingGauge), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(nameof(Text), typeof(string), typeof(RingGauge), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty CaptionProperty = DependencyProperty.Register(nameof(Caption), typeof(string), typeof(RingGauge), new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ThicknessProperty = DependencyProperty.Register(nameof(Thickness), typeof(double), typeof(RingGauge), new FrameworkPropertyMetadata(9.0, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty IsAvailableProperty = DependencyProperty.Register(nameof(IsAvailable), typeof(bool), typeof(RingGauge), new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.AffectsRender));

    public double Value { get => (double)GetValue(ValueProperty); set => SetValue(ValueProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Stroke { get => (Brush)GetValue(StrokeProperty); set => SetValue(StrokeProperty, value); }
    public string Text { get => (string)GetValue(TextProperty); set => SetValue(TextProperty, value); }
    public string Caption { get => (string)GetValue(CaptionProperty); set => SetValue(CaptionProperty, value); }
    public double Thickness { get => (double)GetValue(ThicknessProperty); set => SetValue(ThicknessProperty, value); }
    public bool IsAvailable { get => (bool)GetValue(IsAvailableProperty); set => SetValue(IsAvailableProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double size = Math.Min(ActualWidth, ActualHeight);
        if (size < 20) return;
        var c = new Point(ActualWidth / 2, ActualHeight / 2);
        double r = size / 2 - Thickness / 2 - 1;
        const double start = 135, sweep = 270;
        DrawArc(dc, c, r, start, sweep, new Pen(ChartUtil.Track, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        double frac = IsAvailable && Maximum > 0 ? Math.Clamp(Value / Maximum, 0, 1) : 0;
        if (frac > 0.001)
            DrawArc(dc, c, r, start, sweep * frac, new Pen(Stroke, Thickness) { StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round });
        var main = ChartUtil.Text(IsAvailable ? Text : "—", size * 0.2, Brushes.White, this, bold: true);
        dc.DrawText(main, new Point(c.X - main.Width / 2, c.Y - main.Height / 2 - (string.IsNullOrEmpty(Caption) ? 0 : size * 0.05)));
        if (!string.IsNullOrEmpty(Caption))
        {
            var cap = ChartUtil.Text(Caption, Math.Max(9, size * 0.085), ChartUtil.Label, this);
            dc.DrawText(cap, new Point(c.X - cap.Width / 2, c.Y + main.Height / 2 - size * 0.04));
        }
    }

    private static void DrawArc(DrawingContext dc, Point c, double r, double startDeg, double sweepDeg, Pen pen)
    {
        if (sweepDeg <= 0) return;
        Point P(double deg) { double rad = deg * Math.PI / 180; return new Point(c.X + r * Math.Cos(rad), c.Y + r * Math.Sin(rad)); }
        var g = new StreamGeometry();
        using (var ctx = g.Open())
        {
            ctx.BeginFigure(P(startDeg), false, false);
            ctx.ArcTo(P(startDeg + sweepDeg), new Size(r, r), 0, sweepDeg > 180, SweepDirection.Clockwise, true, false);
        }
        g.Freeze();
        dc.DrawGeometry(null, pen, g);
    }
}

/// <summary>Gráfico de barras verticais (distribuições, histogramas, por núcleo).</summary>
public sealed class BarChart : FrameworkElement
{
    public static readonly DependencyProperty ValuesProperty = DependencyProperty.Register(nameof(Values), typeof(double[]), typeof(BarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty LabelsProperty = DependencyProperty.Register(nameof(Labels), typeof(string[]), typeof(BarChart), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty MaximumProperty = DependencyProperty.Register(nameof(Maximum), typeof(double), typeof(BarChart), new FrameworkPropertyMetadata(double.NaN, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty FillProperty = DependencyProperty.Register(nameof(Fill), typeof(Brush), typeof(BarChart), new FrameworkPropertyMetadata(Brushes.DeepSkyBlue, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ShowValuesProperty = DependencyProperty.Register(nameof(ShowValues), typeof(bool), typeof(BarChart), new FrameworkPropertyMetadata(false, FrameworkPropertyMetadataOptions.AffectsRender));
    public static readonly DependencyProperty ValueFormatProperty = DependencyProperty.Register(nameof(ValueFormat), typeof(string), typeof(BarChart), new FrameworkPropertyMetadata("0", FrameworkPropertyMetadataOptions.AffectsRender));

    public double[]? Values { get => (double[]?)GetValue(ValuesProperty); set => SetValue(ValuesProperty, value); }
    public string[]? Labels { get => (string[]?)GetValue(LabelsProperty); set => SetValue(LabelsProperty, value); }
    public double Maximum { get => (double)GetValue(MaximumProperty); set => SetValue(MaximumProperty, value); }
    public Brush Fill { get => (Brush)GetValue(FillProperty); set => SetValue(FillProperty, value); }
    public bool ShowValues { get => (bool)GetValue(ShowValuesProperty); set => SetValue(ShowValuesProperty, value); }
    public string ValueFormat { get => (string)GetValue(ValueFormatProperty); set => SetValue(ValueFormatProperty, value); }

    protected override void OnRender(DrawingContext dc)
    {
        double w = ActualWidth, h = ActualHeight;
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(0, 0, w, h));
        var v = Values;
        if (v is null || v.Length == 0 || w <= 0 || h <= 0) return;
        bool labels = Labels is { Length: > 0 };
        double labelH = labels ? 16 : 0, valueH = ShowValues ? 14 : 0;
        double max = double.IsNaN(Maximum) ? Math.Max(1e-9, v.Max()) : Maximum;
        double slot = w / v.Length, bw = Math.Max(2, slot * 0.68);
        double ph = h - labelH - valueH;
        for (int i = 0; i < v.Length; i++)
        {
            double x = i * slot + (slot - bw) / 2;
            dc.DrawRoundedRectangle(ChartUtil.Track, null, new Rect(x, valueH, bw, ph), 3, 3);
            double bh = Math.Clamp(v[i] / max, 0, 1) * ph;
            if (bh > 0.5) dc.DrawRoundedRectangle(Fill, null, new Rect(x, valueH + ph - bh, bw, bh), 3, 3);
            if (ShowValues)
            {
                var ft = ChartUtil.Text(v[i].ToString(ValueFormat, CultureInfo.CurrentCulture), 9.5, ChartUtil.Label, this);
                dc.DrawText(ft, new Point(x + bw / 2 - ft.Width / 2, Math.Max(0, valueH + ph - bh - ft.Height)));
            }
            if (labels && i < Labels!.Length && (v.Length <= 24 || i % Math.Ceiling(v.Length / 16.0) == 0))
            {
                var ft = ChartUtil.Text(Labels[i], 9.5, ChartUtil.Label, this);
                dc.DrawText(ft, new Point(x + bw / 2 - ft.Width / 2, h - labelH + 2));
            }
        }
    }
}
