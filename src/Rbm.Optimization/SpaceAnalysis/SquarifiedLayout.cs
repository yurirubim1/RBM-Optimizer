namespace Rbm.Optimization.SpaceAnalysis;

/// <summary>Retângulo simples (sem dependência de WPF).</summary>
public readonly record struct TreemapRect(double X, double Y, double Width, double Height)
{
    public double Area => Width * Height;
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public double AspectRatio => Width <= 0 || Height <= 0 ? double.PositiveInfinity : Math.Max(Width / Height, Height / Width);
    public static readonly TreemapRect Empty = new(0, 0, 0, 0);
}

/// <summary>
/// Treemap "squarified" (Bruls, Huizing &amp; van Wijk, 2000): agrupa itens (em ordem decrescente) em faixas ao longo do
/// lado menor do espaço livre, aceitando um novo item na faixa enquanto isso não piorar a pior razão de aspecto.
/// Função pura: tamanhos + retângulo → retângulos (mesma ordem da entrada). Áreas proporcionais, sem sobreposição.
/// </summary>
public static class SquarifiedLayout
{
    public static TreemapRect[] Layout(IReadOnlyList<double> sizes, TreemapRect bounds)
    {
        int count = sizes.Count;
        var result = new TreemapRect[count];
        if (count == 0) return result;

        // Índices dos itens positivos, ordenados por tamanho decrescente (estável).
        var order = new List<int>(count);
        double total = 0;
        for (int i = 0; i < count; i++)
        {
            double s = sizes[i];
            if (s > 0 && double.IsFinite(s)) { order.Add(i); total += s; }
            else result[i] = new TreemapRect(bounds.X, bounds.Y, 0, 0);
        }
        if (order.Count == 0 || bounds.Width <= 0 || bounds.Height <= 0 || total <= 0)
        {
            foreach (var i in order) result[i] = new TreemapRect(bounds.X, bounds.Y, 0, 0);
            return result;
        }
        order.Sort((a, b) => { int c = sizes[b].CompareTo(sizes[a]); return c != 0 ? c : a.CompareTo(b); });

        double scale = bounds.Width * bounds.Height / total;
        var areas = new double[order.Count];
        for (int k = 0; k < order.Count; k++) areas[k] = sizes[order[k]] * scale;

        double x = bounds.X, y = bounds.Y, w = bounds.Width, h = bounds.Height;
        int start = 0;
        while (start < areas.Length)
        {
            double side = Math.Min(w, h);
            // Cresce a faixa enquanto a pior razão de aspecto não piorar.
            int end = start + 1;
            double rowSum = areas[start], rowMin = areas[start], rowMax = areas[start];
            double worst = Worst(rowSum, rowMin, rowMax, side);
            while (end < areas.Length)
            {
                double a = areas[end];
                double s2 = rowSum + a, mn = Math.Min(rowMin, a), mx = Math.Max(rowMax, a);
                double w2 = Worst(s2, mn, mx, side);
                if (w2 > worst) break;
                rowSum = s2; rowMin = mn; rowMax = mx; worst = w2; end++;
            }
            bool last = end >= areas.Length;

            if (w >= h)
            {
                // Faixa vertical à esquerda: largura = soma / altura.
                double stripW = last ? w : Math.Min(w, rowSum / h);
                double cy = y;
                for (int k = start; k < end; k++)
                {
                    double ih = k == end - 1 ? y + h - cy : areas[k] / stripW;
                    result[order[k]] = new TreemapRect(x, cy, stripW, Math.Max(0, ih));
                    cy += ih;
                }
                x += stripW; w -= stripW;
            }
            else
            {
                // Faixa horizontal no topo: altura = soma / largura.
                double stripH = last ? h : Math.Min(h, rowSum / w);
                double cx = x;
                for (int k = start; k < end; k++)
                {
                    double iw = k == end - 1 ? x + w - cx : areas[k] / stripH;
                    result[order[k]] = new TreemapRect(cx, y, Math.Max(0, iw), stripH);
                    cx += iw;
                }
                y += stripH; h -= stripH;
            }
            w = Math.Max(0, w); h = Math.Max(0, h);
            start = end;
        }
        return result;
    }

    /// <summary>Pior razão de aspecto de uma faixa com soma <paramref name="sum"/> ao longo de <paramref name="side"/>.</summary>
    private static double Worst(double sum, double min, double max, double side)
    {
        if (sum <= 0 || side <= 0 || min <= 0) return double.PositiveInfinity;
        double s2 = sum * sum, w2 = side * side;
        return Math.Max(w2 * max / s2, s2 / (w2 * min));
    }
}
