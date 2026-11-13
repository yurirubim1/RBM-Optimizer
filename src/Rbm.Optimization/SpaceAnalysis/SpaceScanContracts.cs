using System.Text;
using System.Text.Json.Serialization;

namespace Rbm.Optimization.SpaceAnalysis;

/// <summary>Payload de <c>storage.scan</c> (UI → agente elevado).</summary>
/// <param name="Path">Pasta/unidade absoluta em disco local fixo (sem \\?\, \\.\ ou UNC).</param>
/// <param name="MaxNodes">Orçamento de nós na resposta (limitado a 1 000–100 000; padrão 50 000).</param>
/// <param name="AllocatedSize">true = espaço alocado em disco; false (padrão) = tamanho lógico.</param>
/// <param name="TimeLimitSeconds">Limite de tempo da varredura no agente (60–3600 s; padrão 1 500 s). Ao estourar, devolve parcial.</param>
public sealed record StorageScanRequest(string Path, int MaxNodes = 50_000, bool AllocatedSize = false, int TimeLimitSeconds = 1_500);

/// <summary>Nó da árvore achatada (índice do pai &lt; índice do filho). Nomes curtos para manter o JSON pequeno.</summary>
public sealed class StorageScanNode
{
    /// <summary>Índice do pai (-1 na raiz).</summary>
    [JsonPropertyName("p")] public int Parent { get; set; }
    [JsonPropertyName("n")] public string Name { get; set; } = "";
    [JsonPropertyName("s")] public long Size { get; set; }
    [JsonPropertyName("f")] public long Files { get; set; }
    [JsonPropertyName("d")] public long Folders { get; set; }
    /// <summary><see cref="SpaceNodeKind"/> como inteiro.</summary>
    [JsonPropertyName("k")] public int Kind { get; set; }
    /// <summary>Bits: 1 = inacessível, 2 = filhos omitidos (truncado).</summary>
    [JsonPropertyName("x")] public int Flags { get; set; }
}

/// <summary>Resposta de <c>storage.scan</c>: árvore podada e achatada + resumo.</summary>
public sealed record StorageScanResponse(
    string Root,
    bool AllocatedSize,
    long TotalBytes,
    long Files,
    long Folders,
    int InaccessibleCount,
    List<string> InaccessiblePaths,
    int LinkCount,
    double DurationMs,
    bool Partial,
    bool Pruned,
    List<StorageScanNode> Nodes);

/// <summary>Conversão árvore ↔ forma achatada com poda (orçamento de nós e de bytes).</summary>
public static class SpaceTreeCodec
{
    public const int FlagInaccessible = 1;
    public const int FlagTruncated = 2;

    /// <summary>
    /// Poda gulosa: expande primeiro as pastas maiores (fila de prioridade) até esgotar o orçamento. Cada pasta expandida
    /// inclui no máximo <paramref name="maxChildrenPerNode"/> filhos; o restante vira "(outros)". Pastas não expandidas
    /// ficam marcadas como truncadas. O JSON resultante fica bem abaixo do limite de 16 MiB do canal.
    /// </summary>
    public static StorageScanResponse ToResponse(SpaceScanResult r, int maxNodes = 50_000, int maxChildrenPerNode = 200, long maxApproxBytes = 10L * 1024 * 1024)
    {
        maxNodes = Math.Max(16, maxNodes);
        maxChildrenPerNode = Math.Max(4, maxChildrenPerNode);
        var nodes = new List<StorageScanNode>(Math.Min(maxNodes, 4096));
        long bytes = 0;
        bool pruned = false;

        StorageScanNode Add(SpaceNode n, int parent)
        {
            var sn = new StorageScanNode
            {
                Parent = parent, Name = n.Name, Size = n.Size, Files = n.FileCount, Folders = n.FolderCount, Kind = (int)n.Kind,
                Flags = (n.IsInaccessible ? FlagInaccessible : 0) | (n.IsTruncated ? FlagTruncated : 0),
            };
            nodes.Add(sn);
            bytes += Estimate(n.Name);
            return sn;
        }

        Add(r.Root, -1);
        var queue = new PriorityQueue<(SpaceNode Node, int Index), long>();
        if (r.Root.Children.Length > 0) queue.Enqueue((r.Root, 0), -r.Root.Size);

        while (queue.TryDequeue(out var item, out _))
        {
            var kids = item.Node.Children;
            int room = maxNodes - nodes.Count;
            if (room < 2 || bytes >= maxApproxBytes) { nodes[item.Index].Flags |= FlagTruncated; pruned = true; continue; }
            int take = Math.Min(kids.Length, maxChildrenPerNode);
            if (take + (kids.Length > take ? 1 : 0) > room) take = room - 1;
            for (int i = 0; i < take; i++)
            {
                var c = kids[i];
                Add(c, item.Index);
                if (c.Kind == SpaceNodeKind.Folder && c.Children.Length > 0) queue.Enqueue((c, nodes.Count - 1), -c.Size);
            }
            if (kids.Length > take)
            {
                pruned = true;
                long s = 0, f = 0, d = 0;
                for (int i = take; i < kids.Length; i++)
                {
                    s += kids[i].Size; f += kids[i].FileCount;
                    d += kids[i].FolderCount + (kids[i].Kind == SpaceNodeKind.Folder ? 1 : 0);
                }
                nodes.Add(new StorageScanNode { Parent = item.Index, Name = SpaceScanner.OthersName, Size = s, Files = f, Folders = d, Kind = (int)SpaceNodeKind.Other });
                bytes += 80;
            }
        }

        return new StorageScanResponse(r.Root.Name, r.SizeMode == SpaceSizeMode.Allocated, r.TotalBytes, r.Files, r.Folders, r.InaccessibleCount,
            r.InaccessiblePaths.Take(200).ToList(), r.LinkCount, r.Duration.TotalMilliseconds, r.Partial, pruned, nodes);
    }

    private static long Estimate(string name) => 72 + Encoding.UTF8.GetByteCount(name) + 8;

    /// <summary>Reconstrói a árvore a partir da resposta do agente (valida índices; tolera dados incoerentes).</summary>
    public static SpaceScanResult FromResponse(StorageScanResponse resp)
    {
        var list = resp.Nodes ?? [];
        if (list.Count == 0 || list[0].Parent != -1) throw new InvalidDataException("Resposta da varredura sem raiz.");
        var built = new SpaceNode[list.Count];
        var counts = new int[list.Count];
        for (int i = 1; i < list.Count; i++)
        {
            int p = list[i].Parent;
            if (p < 0 || p >= i) throw new InvalidDataException("Árvore da varredura com índice de pai inválido.");
            counts[p]++;
        }
        for (int i = 0; i < list.Count; i++)
        {
            var s = list[i];
            var kind = Enum.IsDefined(typeof(SpaceNodeKind), (byte)s.Kind) ? (SpaceNodeKind)s.Kind : SpaceNodeKind.Other;
            var parent = i == 0 ? null : built[s.Parent];
            var n = new SpaceNode(i == 0 ? resp.Root : s.Name ?? "", kind, parent)
            {
                Size = Math.Max(0, s.Size), FileCount = Math.Max(0, s.Files), FolderCount = Math.Max(0, s.Folders),
                IsInaccessible = (s.Flags & FlagInaccessible) != 0, IsTruncated = (s.Flags & FlagTruncated) != 0,
            };
            if (counts[i] > 0) n.Children = new SpaceNode[counts[i]];
            built[i] = n;
        }
        var fill = new int[list.Count];
        for (int i = 1; i < list.Count; i++)
        {
            int p = list[i].Parent;
            built[p].Children[fill[p]++] = built[i];
        }
        foreach (var n in built)
            if (n.Children.Length > 1) Array.Sort(n.Children, static (a, b) => b.Size.CompareTo(a.Size));

        return new SpaceScanResult
        {
            Root = built[0],
            SizeMode = resp.AllocatedSize ? SpaceSizeMode.Allocated : SpaceSizeMode.Logical,
            InaccessibleCount = resp.InaccessibleCount,
            InaccessiblePaths = resp.InaccessiblePaths ?? [],
            LinkCount = resp.LinkCount,
            Duration = TimeSpan.FromMilliseconds(Math.Max(0, resp.DurationMs)),
            Partial = resp.Partial,
            Pruned = resp.Pruned,
            Elevated = true,
        };
    }
}
