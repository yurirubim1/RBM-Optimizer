namespace Rbm.Optimization.SpaceAnalysis;

/// <summary>Tipo de um nó da árvore de espaço.</summary>
public enum SpaceNodeKind : byte
{
    /// <summary>Pasta real (pode ter filhos).</summary>
    Folder = 0,
    /// <summary>Arquivo individual.</summary>
    File = 1,
    /// <summary>Agregado de arquivos pequenos de uma pasta (além do top-N por pasta).</summary>
    SmallFiles = 2,
    /// <summary>Agregado "outros" (itens menores omitidos para manter a árvore/visualização enxuta).</summary>
    Other = 3,
    /// <summary>Ponto de nova análise que aponta para outro lugar (junção, link simbólico): não seguido, conta 0.</summary>
    Link = 4,
}

/// <summary>
/// Nó compacto da árvore de espaço. A raiz guarda o caminho completo em <see cref="Name"/>; os demais guardam só o nome.
/// Tamanhos/contagens são do subárvore inteiro.
/// </summary>
public sealed class SpaceNode
{
    private static readonly SpaceNode[] None = [];

    public SpaceNode(string name, SpaceNodeKind kind, SpaceNode? parent)
    {
        Name = name; Kind = kind; Parent = parent;
    }

    public string Name { get; }
    public SpaceNodeKind Kind { get; }
    public SpaceNode? Parent { get; internal set; }
    /// <summary>Bytes (lógicos ou alocados, conforme o modo da varredura).</summary>
    public long Size { get; internal set; }
    /// <summary>Arquivos no subárvore (para agregados: quantos arquivos foram agrupados).</summary>
    public long FileCount { get; internal set; }
    /// <summary>Subpastas no subárvore.</summary>
    public long FolderCount { get; internal set; }
    /// <summary>A pasta não pôde ser listada (acesso negado ou erro de E/S). Tamanho real desconhecido.</summary>
    public bool IsInaccessible { get; internal set; }
    /// <summary>Os filhos foram omitidos (árvore podada ou varredura interrompida pelo limite de tempo).</summary>
    public bool IsTruncated { get; internal set; }
    /// <summary>Filhos ordenados por tamanho decrescente.</summary>
    public SpaceNode[] Children { get; internal set; } = None;

    public bool IsFolder => Kind == SpaceNodeKind.Folder;
    public bool IsAggregate => Kind is SpaceNodeKind.SmallFiles or SpaceNodeKind.Other;
    public bool CanDrill => Kind == SpaceNodeKind.Folder && Children.Length > 0;

    public int Depth { get { int d = 0; for (var p = Parent; p is not null; p = p.Parent) d++; return d; } }

    /// <summary>Caminho completo. Para agregados e links devolve o caminho do próprio item/pasta-pai conforme o caso.</summary>
    public string FullPath
    {
        get
        {
            if (Parent is null) return Name;
            if (IsAggregate) return Parent.FullPath;
            return Path.Combine(Parent.FullPath, Name);
        }
    }

    /// <summary>Caminho que existe no disco para "mostrar no Explorer" (agregados → pasta-pai).</summary>
    public string ExplorerPath => IsAggregate && Parent is not null ? Parent.FullPath : FullPath;

    /// <summary>Cria um nó agregado (somente exibição), sem alterar a árvore.</summary>
    public static SpaceNode CreateAggregate(string name, SpaceNode parent, long size, long files, long folders) =>
        new(name, SpaceNodeKind.Other, parent) { Size = size, FileCount = files, FolderCount = folders };

    public override string ToString() => $"{Name} ({Size:N0} B)";
}

/// <summary>Categoria de arquivo por extensão (usada para colorir o mapa).</summary>
public enum FileCategory : byte { Other, Video, Image, Audio, Archive, Executable, Document, DiskImage, Code, System }

public static class FileCategories
{
    private static readonly Dictionary<string, FileCategory> Map = Build();

    private static Dictionary<string, FileCategory> Build()
    {
        var d = new Dictionary<string, FileCategory>(StringComparer.OrdinalIgnoreCase);
        void Add(FileCategory c, params string[] exts) { foreach (var e in exts) d["." + e] = c; }
        Add(FileCategory.Video, "mp4", "mkv", "avi", "mov", "wmv", "webm", "flv", "m4v", "mpg", "mpeg", "ts", "m2ts", "vob", "3gp");
        Add(FileCategory.Image, "jpg", "jpeg", "png", "gif", "bmp", "tif", "tiff", "webp", "heic", "heif", "raw", "cr2", "nef", "arw", "dng", "psd", "svg", "ico", "dds", "tga", "exr");
        Add(FileCategory.Audio, "mp3", "wav", "flac", "aac", "ogg", "m4a", "wma", "opus", "aiff", "mid");
        Add(FileCategory.Archive, "zip", "rar", "7z", "tar", "gz", "tgz", "bz2", "xz", "zst", "cab", "lz4", "pak", "nupkg", "apk", "jar");
        Add(FileCategory.Executable, "exe", "dll", "msi", "msix", "appx", "sys", "drv", "ocx", "scr", "com", "bat", "cmd", "ps1", "so", "bin", "node");
        Add(FileCategory.Document, "pdf", "doc", "docx", "xls", "xlsx", "ppt", "pptx", "odt", "ods", "odp", "txt", "rtf", "md", "csv", "epub");
        Add(FileCategory.DiskImage, "iso", "img", "vhd", "vhdx", "vmdk", "vdi", "qcow2", "wim", "esd", "dmg");
        Add(FileCategory.Code, "cs", "cpp", "c", "h", "hpp", "js", "ts", "tsx", "py", "java", "go", "rs", "json", "xml", "xaml", "html", "css", "pdb", "lib", "obj", "o", "a", "ilk", "ipch");
        Add(FileCategory.System, "log", "etl", "evtx", "tmp", "dmp", "mdmp", "db", "sqlite", "dat", "cache", "edb", "chk", "pf", "msp", "cat", "mui");
        return d;
    }

    public static FileCategory Classify(string fileName)
    {
        if (fileName.Equals("pagefile.sys", StringComparison.OrdinalIgnoreCase) || fileName.Equals("hiberfil.sys", StringComparison.OrdinalIgnoreCase)
            || fileName.Equals("swapfile.sys", StringComparison.OrdinalIgnoreCase)) return FileCategory.System;
        var ext = Path.GetExtension(fileName);
        return ext.Length > 1 && Map.TryGetValue(ext, out var c) ? c : FileCategory.Other;
    }

    public static string Label(FileCategory c) => c switch
    {
        FileCategory.Video => "Vídeo",
        FileCategory.Image => "Imagem",
        FileCategory.Audio => "Áudio",
        FileCategory.Archive => "Compactado",
        FileCategory.Executable => "Executável/biblioteca",
        FileCategory.Document => "Documento",
        FileCategory.DiskImage => "Imagem de disco/VM",
        FileCategory.Code => "Código/build",
        FileCategory.System => "Sistema/log/cache",
        _ => "Outros arquivos",
    };
}
