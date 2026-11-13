using System.Windows.Media;
using Rbm.Optimization.SpaceAnalysis;

namespace Rbm.UI.Pages.Space;

/// <summary>Cores do mapa de espaço (paleta RBM: pastas em azuis/violetas; arquivos por categoria).</summary>
public static class TreemapPalette
{
    public static readonly Color Surface = Color.FromRgb(0x12, 0x16, 0x1E);
    public static readonly Color Text = Color.FromRgb(0xE8, 0xEC, 0xF4);
    public static readonly Color TextDark = Color.FromRgb(0x0A, 0x0C, 0x11);
    public static readonly Color Muted = Color.FromRgb(0x8C, 0x96, 0xAA);

    private static readonly Color[] FolderHues =
    [
        Color.FromRgb(0x2F, 0x7B, 0xFF), // azul elétrico (accent)
        Color.FromRgb(0x8B, 0x5C, 0xF6), // violeta
        Color.FromRgb(0x63, 0x66, 0xF1), // índigo
        Color.FromRgb(0x3B, 0x9E, 0xFF), // azul claro
        Color.FromRgb(0xA8, 0x55, 0xF7), // púrpura
        Color.FromRgb(0x4F, 0x5B, 0xD5), // cobalto
        Color.FromRgb(0x7C, 0x8C, 0xFF), // lavanda
        Color.FromRgb(0x6D, 0x28, 0xD9), // violeta escuro
    ];

    public static readonly Color SmallFiles = Color.FromRgb(0x5D, 0x66, 0x7A);
    public static readonly Color Others = Color.FromRgb(0x3F, 0x48, 0x59);
    public static readonly Color Link = Color.FromRgb(0x32, 0x3B, 0x4D);
    public static readonly Color Inaccessible = Color.FromRgb(0xF5, 0x9E, 0x0B);

    public static Color Category(FileCategory c) => c switch
    {
        FileCategory.Video => Color.FromRgb(0xF5, 0x9E, 0x0B),
        FileCategory.Image => Color.FromRgb(0x22, 0xC5, 0x5E),
        FileCategory.Audio => Color.FromRgb(0xEC, 0x48, 0x99),
        FileCategory.Archive => Color.FromRgb(0xF9, 0x73, 0x16),
        FileCategory.Executable => Color.FromRgb(0x2D, 0xD4, 0xBF),
        FileCategory.Document => Color.FromRgb(0xFD, 0xE0, 0x47),
        FileCategory.DiskImage => Color.FromRgb(0xEF, 0x44, 0x44),
        FileCategory.Code => Color.FromRgb(0xA3, 0xE6, 0x35),
        FileCategory.System => Color.FromRgb(0x64, 0x74, 0x8B),
        _ => Color.FromRgb(0x8C, 0x96, 0xAA),
    };

    public static Color FolderHue(int index) => FolderHues[((index % FolderHues.Length) + FolderHues.Length) % FolderHues.Length];

    /// <summary>Cor-base de um nó (índice = posição entre irmãos, usado para variar o tom das pastas).</summary>
    public static Color BaseColor(SpaceNode n, int siblingIndex) => n.Kind switch
    {
        SpaceNodeKind.Folder => n.IsInaccessible ? Inaccessible : FolderHue(siblingIndex),
        SpaceNodeKind.File => Category(FileCategories.Classify(n.Name)),
        SpaceNodeKind.SmallFiles => SmallFiles,
        SpaceNodeKind.Link => Link,
        _ => Others,
    };

    public static Color Mix(Color a, Color b, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return Color.FromRgb((byte)(a.R + (b.R - a.R) * t), (byte)(a.G + (b.G - a.G) * t), (byte)(a.B + (b.B - a.B) * t));
    }

    public static double Luminance(Color c) => (0.2126 * c.R + 0.7152 * c.G + 0.0722 * c.B) / 255.0;

    private static readonly Dictionary<Color, SolidColorBrush> Brushes = new();
    private static readonly Dictionary<(Color, double), Pen> Pens = new();

    public static SolidColorBrush Brush(Color c)
    {
        if (Brushes.TryGetValue(c, out var b)) return b;
        b = new SolidColorBrush(c); b.Freeze();
        if (Brushes.Count < 4096) Brushes[c] = b;
        return b;
    }

    public static Pen Pen(Color c, double thickness)
    {
        if (Pens.TryGetValue((c, thickness), out var p)) return p;
        p = new Pen(Brush(c), thickness); p.Freeze();
        if (Pens.Count < 4096) Pens[(c, thickness)] = p;
        return p;
    }

    /// <summary>Amostra de cor para listas/legenda (mesmo tom usado no mapa).</summary>
    public static SolidColorBrush Swatch(SpaceNode n, int siblingIndex) => Brush(Mix(Surface, BaseColor(n, siblingIndex), 0.8));

    public static IReadOnlyList<LegendEntry> Legend { get; } = BuildLegend();

    private static List<LegendEntry> BuildLegend()
    {
        var l = new List<LegendEntry>
        {
            new("Pastas", new LinearGradientBrush(FolderHues[0], FolderHues[1], 0) { }.AsFrozen()),
        };
        foreach (var c in new[] { FileCategory.Video, FileCategory.Image, FileCategory.Audio, FileCategory.Archive, FileCategory.Executable,
                     FileCategory.DiskImage, FileCategory.Document, FileCategory.Code, FileCategory.System, FileCategory.Other })
            l.Add(new(FileCategories.Label(c), Brush(Category(c))));
        l.Add(new("Arquivos menores agrupados", Brush(SmallFiles)));
        return l;
    }

    private static T AsFrozen<T>(this T f) where T : System.Windows.Freezable { f.Freeze(); return f; }
}

public sealed record LegendEntry(string Label, Brush Brush);
