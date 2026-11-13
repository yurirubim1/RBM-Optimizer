using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Rbm.Optimization.SpaceAnalysis;
using Rbm.UI.Infrastructure;

namespace Rbm.UI.Pages.Space;

/// <summary>
/// Treemap "squarified" desenhado em duas camadas (DrawingVisual): base em cache (recalculada só quando a raiz ou o tamanho
/// mudam) e sobreposição leve (hover, seleção e dica). Mostra 2 níveis: filhos da pasta atual e, dentro das pastas grandes,
/// os netos. Clique em pasta → <see cref="DrillCommand"/>; botão direito / botão "voltar" do mouse → <see cref="UpCommand"/>.
/// </summary>
public sealed class TreemapControl : FrameworkElement
{
    public static readonly DependencyProperty RootProperty = DependencyProperty.Register(nameof(Root), typeof(SpaceNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, (d, _) => ((TreemapControl)d).OnRootChanged()));
    public static readonly DependencyProperty SelectedNodeProperty = DependencyProperty.Register(nameof(SelectedNode), typeof(SpaceNode), typeof(TreemapControl),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, _) => ((TreemapControl)d).DrawOverlay()));
    public static readonly DependencyProperty DrillCommandProperty = DependencyProperty.Register(nameof(DrillCommand), typeof(ICommand), typeof(TreemapControl));
    public static readonly DependencyProperty UpCommandProperty = DependencyProperty.Register(nameof(UpCommand), typeof(ICommand), typeof(TreemapControl));

    public SpaceNode? Root { get => (SpaceNode?)GetValue(RootProperty); set => SetValue(RootProperty, value); }
    public SpaceNode? SelectedNode { get => (SpaceNode?)GetValue(SelectedNodeProperty); set => SetValue(SelectedNodeProperty, value); }
    public ICommand? DrillCommand { get => (ICommand?)GetValue(DrillCommandProperty); set => SetValue(DrillCommandProperty, value); }
    public ICommand? UpCommand { get => (ICommand?)GetValue(UpCommandProperty); set => SetValue(UpCommandProperty, value); }

    private const int MaxTopItems = 400;
    private const int MaxNestedItems = 120;
    private const double MinTileArea = 10;    // px² abaixo disso vai para "(outros)"
    private const double NestMinW = 72, NestMinH = 54, HeaderH = 19;

    private sealed record Tile(SpaceNode Node, Rect Rect, int Level, Tile? Owner, Color Color);

    private readonly VisualCollection _visuals;
    private readonly DrawingVisual _base = new();
    private readonly DrawingVisual _overlay = new();
    private readonly List<Tile> _tiles = new();
    private Tile? _hover;
    private Point _mouse;

    private static readonly Typeface Face = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
    private static readonly Typeface FaceBold = new(new FontFamily("Segoe UI Variable Text, Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public TreemapControl()
    {
        _visuals = new VisualCollection(this) { _base, _overlay };
        ClipToBounds = true;
        Focusable = false;
        SnapsToDevicePixels = true;
    }

    protected override int VisualChildrenCount => _visuals.Count;
    protected override Visual GetVisualChild(int index) => _visuals[index];

    protected override void OnRender(DrawingContext dc) =>
        dc.DrawRectangle(Brushes.Transparent, null, new Rect(RenderSize)); // área clicável

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        Rebuild();
    }

    private void OnRootChanged()
    {
        _hover = null;
        Rebuild();
        if (Root is not null)
        {
            var a = new DoubleAnimation(0.35, 1, TimeSpan.FromMilliseconds(200)) { EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut } };
            BeginAnimation(OpacityProperty, a);
        }
    }

    // ---------------- Layout ----------------

    /// <summary>Filhos visíveis (positivos) até o limite; os menores viram um agregado "(outros)" só de exibição.</summary>
    private static List<SpaceNode> Pick(SpaceNode node, double areaPx, int maxItems)
    {
        var list = new List<SpaceNode>();
        long total = 0;
        foreach (var c in node.Children) if (c.Size > 0) total += c.Size;
        if (total <= 0 || areaPx <= 0) return list;
        long restSize = 0, restFiles = 0, restFolders = 0;
        int restCount = 0;
        SpaceNode? lastRest = null;
        foreach (var c in node.Children)
        {
            if (c.Size <= 0) continue;
            if (list.Count < maxItems && c.Size / (double)total * areaPx >= MinTileArea) list.Add(c);
            else { restSize += c.Size; restFiles += c.FileCount; restFolders += c.FolderCount + (c.IsFolder ? 1 : 0); restCount++; lastRest = c; }
        }
        if (restSize > 0)
        {
            if (restCount == 1 && lastRest is not null) list.Add(lastRest);
            else list.Add(SpaceNode.CreateAggregate($"(outros: {restCount:N0} itens)", node, restSize, restFiles, restFolders));
        }
        return list;
    }

    private void Rebuild()
    {
        _tiles.Clear();
        var root = Root;
        double w = ActualWidth, h = ActualHeight;
        if (root is not null && w >= 24 && h >= 24)
        {
            var bounds = new Rect(0, 0, w, h);
            var items = Pick(root, bounds.Width * bounds.Height, MaxTopItems);
            var rects = SquarifiedLayout.Layout(items.Select(i => (double)i.Size).ToArray(), new TreemapRect(0, 0, w, h));
            int folderIdx = 0;
            for (int i = 0; i < items.Count; i++)
            {
                var n = items[i];
                var r = Inset(rects[i], 1.5);
                if (r.Width < 1 || r.Height < 1) continue;
                var tile = new Tile(n, r, 1, null, TreemapPalette.BaseColor(n, n.IsFolder ? folderIdx++ : i));
                _tiles.Add(tile);
                if (n.IsFolder && n.Children.Length > 0 && r.Width >= NestMinW && r.Height >= NestMinH)
                {
                    var inner = new Rect(r.X + 3, r.Y + HeaderH, r.Width - 6, r.Height - HeaderH - 3);
                    var sub = Pick(n, inner.Width * inner.Height, MaxNestedItems);
                    var subRects = SquarifiedLayout.Layout(sub.Select(s => (double)s.Size).ToArray(), new TreemapRect(inner.X, inner.Y, inner.Width, inner.Height));
                    for (int j = 0; j < sub.Count; j++)
                    {
                        var sr = Inset(subRects[j], 1);
                        if (sr.Width < 1 || sr.Height < 1) continue;
                        var s = sub[j];
                        var color = s.IsFolder ? TreemapPalette.Mix(tile.Color, Colors.White, 0.12 * (j % 3)) : TreemapPalette.BaseColor(s, j);
                        _tiles.Add(new Tile(s, sr, 2, tile, color));
                    }
                }
            }
        }
        DrawBase();
        DrawOverlay();
    }

    private static Rect Inset(TreemapRect r, double d)
    {
        double x = r.X + d, y = r.Y + d, w = r.Width - 2 * d, hh = r.Height - 2 * d;
        if (w < 1 || hh < 1) { x = r.X; y = r.Y; w = Math.Max(0, r.Width - 0.5); hh = Math.Max(0, r.Height - 0.5); }
        return new Rect(x, y, Math.Max(0, w), Math.Max(0, hh));
    }

    // ---------------- Desenho ----------------

    private double Dip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private FormattedText Text(string s, double size, Color color, bool bold, double maxWidth)
    {
        var ft = new FormattedText(s, CultureInfo.CurrentCulture, FlowDirection.LeftToRight, bold ? FaceBold : Face, size, TreemapPalette.Brush(color), Dip)
        { MaxLineCount = 1, Trimming = TextTrimming.CharacterEllipsis };
        ft.MaxTextWidth = Math.Max(1, maxWidth);
        return ft;
    }

    private void DrawBase()
    {
        using var dc = _base.RenderOpen();
        var root = Root;
        if (root is null) return;
        if (_tiles.Count == 0)
        {
            if (ActualWidth > 40)
            {
                var msg = root.IsInaccessible ? "Esta pasta não pôde ser lida (acesso negado). Tente “Escanear como administrador”."
                    : root.IsTruncated ? "Detalhes desta pasta não foram incluídos (árvore podada pelo agente)."
                    : "Pasta vazia (ou só com itens de 0 bytes / links não seguidos).";
                var ft = Text(msg, 13, TreemapPalette.Muted, false, ActualWidth - 40);
                dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
            }
            return;
        }

        foreach (var t in _tiles)
        {
            var r = t.Rect;
            bool nested = t.Level == 1 && _tiles.Count > 0 && t.Node.IsFolder && r.Width >= NestMinW && r.Height >= NestMinH && t.Node.Children.Length > 0;
            double mix = t.Level == 1 ? (nested ? 0.16 : t.Node.IsFolder ? 0.42 : 0.55) : (t.Node.IsFolder ? 0.38 : 0.5);
            if (t.Node.IsAggregate) mix = 0.45;
            var fill = TreemapPalette.Mix(TreemapPalette.Surface, t.Color, mix);
            var border = TreemapPalette.Mix(TreemapPalette.Surface, t.Color, t.Level == 1 ? 0.75 : 0.62);
            double radius = t.Level == 1 ? 4 : 2.5;
            dc.DrawRoundedRectangle(TreemapPalette.Brush(fill), TreemapPalette.Pen(border, 1), r, radius, radius);
            if (!nested && r.Width > 8 && r.Height > 8)
            {
                // Realce superior sutil (dá volume sem gradiente por tile).
                var hl = TreemapPalette.Mix(fill, Colors.White, 0.10);
                dc.DrawRectangle(TreemapPalette.Brush(hl), null, new Rect(r.X + 1, r.Y + 1, Math.Max(0, r.Width - 2), Math.Min(2, r.Height / 4)));
            }
            DrawLabel(dc, t, r, nested, fill);
        }
    }

    private void DrawLabel(DrawingContext dc, Tile t, Rect r, bool nested, Color fill)
    {
        var textColor = TreemapPalette.Luminance(fill) > 0.6 ? TreemapPalette.TextDark : TreemapPalette.Text;
        var sub = TreemapPalette.Mix(textColor, fill, 0.35);
        string size = Fmt.Bytes(t.Node.Size);
        if (t.Level == 1)
        {
            if (nested)
            {
                var head = Text(t.Node.Name, 11.5, textColor, true, r.Width - 10);
                dc.DrawText(head, new Point(r.X + 6, r.Y + 2));
                double used = head.WidthIncludingTrailingWhitespace;
                if (r.Width - used > 70)
                {
                    var s = Text(size, 11, sub, false, r.Width - used - 18);
                    dc.DrawText(s, new Point(r.X + 6 + used + 8, r.Y + 2.5));
                }
                return;
            }
            if (r.Width < 34 || r.Height < 17) return;
            double fs = r.Width > 160 && r.Height > 70 ? 13 : 11.5;
            var name = Text(t.Node.Name, fs, textColor, true, r.Width - 10);
            dc.DrawText(name, new Point(r.X + 5, r.Y + 4));
            if (r.Height >= 34)
            {
                var s = Text(size, 11, sub, false, r.Width - 10);
                dc.DrawText(s, new Point(r.X + 5, r.Y + 5 + name.Height));
            }
            return;
        }
        if (r.Width < 46 || r.Height < 15) return;
        var n2 = Text(t.Node.Name, 10, textColor, false, r.Width - 6);
        dc.DrawText(n2, new Point(r.X + 3, r.Y + 2));
        if (r.Height >= 29)
        {
            var s2 = Text(size, 9.5, sub, false, r.Width - 6);
            dc.DrawText(s2, new Point(r.X + 3, r.Y + 2 + n2.Height));
        }
    }

    private void DrawOverlay()
    {
        using var dc = _overlay.RenderOpen();
        if (Root is null || _tiles.Count == 0) return;

        var sel = SelectedNode;
        if (sel is not null)
        {
            var st = _tiles.LastOrDefault(t => ReferenceEquals(t.Node, sel));
            if (st is not null) dc.DrawRoundedRectangle(null, TreemapPalette.Pen(Color.FromRgb(0x22, 0xD3, 0xEE), 2), Inflate(st.Rect, 0.5), 4, 4);
        }

        var h = _hover;
        if (h is null) return;
        if (h.Owner is not null)
            dc.DrawRoundedRectangle(null, TreemapPalette.Pen(Color.FromArgb(0x70, 0xFF, 0xFF, 0xFF), 1), h.Owner.Rect, 4, 4);
        dc.DrawRoundedRectangle(TreemapPalette.Brush(Color.FromArgb(0x1C, 0xFF, 0xFF, 0xFF)), TreemapPalette.Pen(Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF), 1.5), h.Rect, 4, 4);
        DrawTip(dc, h);
    }

    private static Rect Inflate(Rect r, double d) => new(r.X - d, r.Y - d, r.Width + 2 * d, r.Height + 2 * d);

    private void DrawTip(DrawingContext dc, Tile t)
    {
        var n = t.Node;
        var root = Root!;
        double maxW = 360;
        var lines = new List<FormattedText>();
        string kind = n.Kind switch
        {
            SpaceNodeKind.Folder => n.IsInaccessible ? "Pasta inacessível" : "Pasta",
            SpaceNodeKind.File => FileCategories.Label(FileCategories.Classify(n.Name)),
            SpaceNodeKind.SmallFiles => "Arquivos menores agrupados",
            SpaceNodeKind.Link => "Link/junção (não seguido)",
            _ => "Itens menores agrupados",
        };
        lines.Add(Text(n.Name, 12.5, TreemapPalette.Text, true, maxW));
        lines.Add(Text(kind + " · " + n.ExplorerPath, 11, TreemapPalette.Muted, false, maxW));
        string pctRoot = root.Size > 0 ? (n.Size * 100.0 / root.Size).ToString("0.0", CultureInfo.CurrentCulture) + "% da pasta atual" : "";
        lines.Add(Text($"{Fmt.Bytes(n.Size)}  ·  {pctRoot}", 12, TreemapPalette.Text, false, maxW));
        if (t.Owner is not null && t.Owner.Node.Size > 0)
            lines.Add(Text((n.Size * 100.0 / t.Owner.Node.Size).ToString("0.0", CultureInfo.CurrentCulture) + "% de " + t.Owner.Node.Name, 11, TreemapPalette.Muted, false, maxW));
        if (n.Kind != SpaceNodeKind.File)
            lines.Add(Text($"{n.FileCount:N0} arquivos" + (n.FolderCount > 0 ? $" · {n.FolderCount:N0} pastas" : ""), 11, TreemapPalette.Muted, false, maxW));
        string hint = n.CanDrill ? "Clique para abrir · botão direito volta" : n.IsTruncated ? "Detalhes não incluídos (árvore podada)" : n.Kind == SpaceNodeKind.File ? "Clique para selecionar" : "";
        if (hint.Length > 0) lines.Add(Text(hint, 10.5, Color.FromRgb(0x22, 0xD3, 0xEE), false, maxW));

        double tw = lines.Max(l => l.Width) + 20, th = lines.Sum(l => l.Height + 1) + 14;
        double x = _mouse.X + 16, y = _mouse.Y + 18;
        if (x + tw > ActualWidth - 4) x = Math.Max(4, _mouse.X - tw - 12);
        if (y + th > ActualHeight - 4) y = Math.Max(4, _mouse.Y - th - 12);
        dc.DrawRoundedRectangle(TreemapPalette.Brush(Color.FromArgb(0xF4, 0x1F, 0x25, 0x32)), TreemapPalette.Pen(Color.FromRgb(0x32, 0x3B, 0x4D), 1), new Rect(x, y, tw, th), 6, 6);
        double cy = y + 7;
        foreach (var l in lines) { dc.DrawText(l, new Point(x + 10, cy)); cy += l.Height + 1; }
    }

    // ---------------- Interação ----------------

    private Tile? HitTile(Point p)
    {
        for (int i = _tiles.Count - 1; i >= 0; i--)
            if (_tiles[i].Rect.Contains(p)) return _tiles[i];
        return null;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        _mouse = e.GetPosition(this);
        var hit = HitTile(_mouse);
        _hover = hit;
        Cursor = hit is not null && (hit.Node.CanDrill || hit.Owner?.Node.CanDrill == true || hit.Node.Kind == SpaceNodeKind.File) ? Cursors.Hand : null;
        DrawOverlay();
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        base.OnMouseLeave(e);
        _hover = null;
        DrawOverlay();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        var hit = HitTile(e.GetPosition(this));
        if (hit is null) return;
        var n = hit.Node;
        if (n.CanDrill) { Execute(DrillCommand, n); e.Handled = true; return; }
        if (n.Kind == SpaceNodeKind.File) { SelectedNode = n; e.Handled = true; return; }
        // Agregado/pasta vazia dentro de uma pasta: abre a pasta dona (se houver).
        if (hit.Owner is { } o && o.Node.CanDrill) { Execute(DrillCommand, o.Node); e.Handled = true; return; }
        SelectedNode = n.IsAggregate ? null : n;
        e.Handled = true;
    }

    protected override void OnMouseRightButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseRightButtonUp(e);
        Execute(UpCommand, null);
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (e.ChangedButton == MouseButton.XButton1) { Execute(UpCommand, null); e.Handled = true; }
    }

    private static void Execute(ICommand? cmd, object? arg)
    {
        if (cmd?.CanExecute(arg) == true) cmd.Execute(arg);
    }
}
