using System.Globalization;
using System.Windows.Media;
using Rbm.Optimization.SpaceAnalysis;
using Rbm.UI.Infrastructure;

namespace Rbm.UI.Pages.Space;

/// <summary>Linha da lista "Maiores itens" (pasta atual).</summary>
public sealed class SpaceItemRow
{
    public SpaceItemRow(SpaceNode node, long parentSize, int folderIndex, int index)
    {
        Node = node;
        Percent = parentSize > 0 ? node.Size * 100.0 / parentSize : 0;
        Swatch = TreemapPalette.Swatch(node, node.IsFolder ? folderIndex : index);
    }

    public SpaceNode Node { get; }
    public string Name => Node.Name;
    public long Size => Node.Size;
    public string SizeText => Node.IsInaccessible ? "sem acesso" : Node.Kind == SpaceNodeKind.Link ? "link" : Fmt.Bytes(Node.Size);
    public double Percent { get; }
    public string PercentText => Percent.ToString(Percent >= 10 ? "0" : "0.0", CultureInfo.CurrentCulture) + "%";
    public long Files => Node.FileCount;
    public string FilesText => Node.Kind == SpaceNodeKind.File ? "—" : Node.FileCount.ToString("N0", CultureInfo.CurrentCulture);
    public Brush Swatch { get; }
    /// <summary>Ícone Segoe Fluent: pasta, arquivo, agregados, link, cadeado.</summary>
    public string Icon => Node.Kind switch
    {
        SpaceNodeKind.Folder => Node.IsInaccessible ? "" : "",
        SpaceNodeKind.File => "",
        SpaceNodeKind.Link => "",
        _ => "",
    };
    public string Tip => Node.Kind switch
    {
        SpaceNodeKind.Folder when Node.IsInaccessible => "Pasta não listada (acesso negado). Use “Escanear como administrador”.",
        SpaceNodeKind.Folder when Node.IsTruncated => "Detalhes desta pasta não foram incluídos (árvore podada). Duplo clique não disponível.",
        SpaceNodeKind.Folder => "Duplo clique para abrir",
        SpaceNodeKind.SmallFiles => $"{Node.FileCount:N0} arquivos menores desta pasta, somados",
        SpaceNodeKind.Link => "Junção/link simbólico: não seguido (conta 0 para não duplicar espaço)",
        SpaceNodeKind.Other => "Itens menores agrupados",
        _ => FileCategories.Label(FileCategories.Classify(Node.Name)),
    };
}

/// <summary>Item da trilha de navegação.</summary>
public sealed record SpaceCrumb(SpaceNode Node, string Title, bool IsLast);

/// <summary>Atalho para uma unidade fixa (valores reais de DriveInfo).</summary>
public sealed record SpaceDrive(string Root, string Title, string Detail, double UsedPercent, string FreeText = "", string TotalText = "", bool IsLow = false)
{
    public string Letter => Root.TrimEnd('\\');
    public string UsedText => UsedPercent.ToString("0", CultureInfo.CurrentCulture) + "% usado";
    /// <summary>Frase curta: "37 GB livres de 464 GB".</summary>
    public string FreeLine => string.IsNullOrEmpty(FreeText) ? Detail : $"{FreeText} livres de {TotalText}";
}
