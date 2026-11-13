using Rbm.Optimization.SpaceAnalysis;
using Xunit;

namespace Rbm.Tests.SpaceAnalysis;

public class SquarifiedLayoutTests
{
    private const double Eps = 1e-6;

    public static TheoryData<double[], double, double> Cases => new()
    {
        { new double[] { 6, 6, 4, 3, 2, 2, 1 }, 6, 4 }, // exemplo do artigo
        { new double[] { 100 }, 300, 200 },
        { new double[] { 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1, 1 }, 400, 400 },
        { new double[] { 1000, 1, 1, 1, 1, 0.5, 0.25 }, 800, 300 },
        { Enumerable.Range(1, 300).Select(i => 1e9 / (i * i)).ToArray(), 1200, 700 },
        { new double[] { 3, 9, 1, 27, 5, 2 }, 50, 500 },
    };

    [Theory, MemberData(nameof(Cases))]
    public void AreasAreProportional_InsideBounds_NoOverlap(double[] sizes, double width, double height)
    {
        var bounds = new TreemapRect(10, 20, width, height);
        var rects = SquarifiedLayout.Layout(sizes, bounds);
        Assert.Equal(sizes.Length, rects.Length);
        double total = sizes.Sum();
        double sumArea = 0;
        for (int i = 0; i < rects.Length; i++)
        {
            var r = rects[i];
            Assert.True(r.Width >= 0 && r.Height >= 0);
            Assert.True(r.X >= bounds.X - Eps && r.Y >= bounds.Y - Eps, $"item {i} fora (origem)");
            Assert.True(r.Right <= bounds.Right + Eps && r.Bottom <= bounds.Bottom + Eps, $"item {i} fora (fim)");
            double expected = sizes[i] / total * bounds.Area;
            Assert.True(Math.Abs(r.Area - expected) <= Math.Max(1e-6, expected * 1e-6), $"item {i}: área {r.Area} ≠ {expected}");
            sumArea += r.Area;
        }
        Assert.Equal(bounds.Area, sumArea, 4);
        for (int i = 0; i < rects.Length; i++)
            for (int j = i + 1; j < rects.Length; j++)
                Assert.True(Overlap(rects[i], rects[j]) <= 1e-6, $"sobreposição entre {i} e {j}");
    }

    [Fact]
    public void EqualSizesInSquare_ProduceSquares()
    {
        var rects = SquarifiedLayout.Layout(Enumerable.Repeat(1.0, 16).ToArray(), new TreemapRect(0, 0, 400, 400));
        Assert.All(rects, r => Assert.True(r.AspectRatio < 1.01, $"aspecto {r.AspectRatio}"));
    }

    [Fact]
    public void AspectRatios_AreReasonable_ForTypicalDistribution()
    {
        var rnd = new Random(42);
        var sizes = Enumerable.Range(0, 200).Select(_ => Math.Pow(rnd.NextDouble(), 3) * 1e6 + 1).ToArray();
        var rects = SquarifiedLayout.Layout(sizes, new TreemapRect(0, 0, 1000, 600));
        double mean = rects.Average(r => r.AspectRatio);
        Assert.True(mean < 2.5, $"razão média {mean}");
        // Squarified é bem melhor que fatiar em tiras (slice-and-dice), que daria razões enormes aqui.
        Assert.True(rects.Max(r => r.AspectRatio) < 40, $"pior razão {rects.Max(r => r.AspectRatio)}");
    }

    [Fact]
    public void ZeroAndNegativeSizes_GetEmptyRects_AndOrderIsPreserved()
    {
        var rects = SquarifiedLayout.Layout(new double[] { 0, 5, -3, 15, double.NaN }, new TreemapRect(0, 0, 100, 100));
        Assert.Equal(0, rects[0].Area);
        Assert.Equal(0, rects[2].Area);
        Assert.Equal(0, rects[4].Area);
        Assert.Equal(2500, rects[1].Area, 6);
        Assert.Equal(7500, rects[3].Area, 6);
    }

    [Fact]
    public void EmptyInputOrBounds_DoesNotThrow()
    {
        Assert.Empty(SquarifiedLayout.Layout(Array.Empty<double>(), new TreemapRect(0, 0, 10, 10)));
        var r = SquarifiedLayout.Layout(new double[] { 1, 2 }, new TreemapRect(0, 0, 0, 10));
        Assert.All(r, x => Assert.Equal(0, x.Area));
    }

    private static double Overlap(TreemapRect a, TreemapRect b)
    {
        double w = Math.Min(a.Right, b.Right) - Math.Max(a.X, b.X);
        double h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Y, b.Y);
        return w > 0 && h > 0 ? w * h : 0;
    }
}
