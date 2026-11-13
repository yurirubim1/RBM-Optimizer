using System.Diagnostics;
using System.Text.Json;
using Rbm.Core.Agent;
using Rbm.Optimization.SpaceAnalysis;
using Xunit;

namespace Rbm.Tests.SpaceAnalysis;

/// <summary>Árvore temporária pequena e conhecida (somente em %TEMP%).</summary>
public sealed class TempTree : IDisposable
{
    public string Root { get; } = Path.Combine(Path.GetTempPath(), "rbm-space-tests-" + Guid.NewGuid().ToString("N")[..8]);

    public TempTree()
    {
        Directory.CreateDirectory(Root);
        Write("a.bin", 1000);
        Write("b.mp4", 5000);
        Write(@"sub1\x.zip", 2000);
        Write(@"sub1\deep\y.png", 300);
        Write(@"sub1\deep\deeper\z.txt", 7);
        Directory.CreateDirectory(Path.Combine(Root, "empty"));
        for (int i = 0; i < 10; i++) Write($@"many\f{i:00}.dat", 100 + i);
    }

    public void Write(string rel, int bytes)
    {
        var p = Path.Combine(Root, rel);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
    }

    public bool TryCreateJunction(string rel, string target)
    {
        try
        {
            var psi = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{Path.Combine(Root, rel)}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
            using var p = Process.Start(psi)!;
            p.WaitForExit(10_000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    public void Dispose()
    {
        try
        {
            // Remove junções primeiro (sem seguir o destino).
            foreach (var d in Directory.EnumerateDirectories(Root, "*", SearchOption.TopDirectoryOnly))
                if (new DirectoryInfo(d).LinkTarget is not null) Directory.Delete(d);
            Directory.Delete(Root, true);
        }
        catch { }
    }
}

public class SpaceScannerTests
{
    private static long ExpectedTotal => 1000 + 5000 + 2000 + 300 + 7 + Enumerable.Range(0, 10).Sum(i => 100 + i);

    [Fact]
    public async Task Scan_ComputesTotals_SortsChildren()
    {
        using var t = new TempTree();
        var r = await SpaceScanner.ScanAsync(t.Root, ct: TestContext.Current.CancellationToken);
        Assert.Equal(ExpectedTotal, r.TotalBytes);
        Assert.Equal(15, r.Files);
        Assert.Equal(5, r.Folders); // sub1, deep, deeper, empty, many
        Assert.Equal(0, r.InaccessibleCount);
        Assert.False(r.Partial);
        var kids = r.Root.Children;
        for (int i = 1; i < kids.Length; i++) Assert.True(kids[i - 1].Size >= kids[i].Size);
        Assert.Equal("b.mp4", kids[0].Name);
        var sub1 = kids.Single(k => k.Name == "sub1");
        Assert.Equal(2307, sub1.Size);
        Assert.Equal(3, sub1.FileCount);
        Assert.Equal(2, sub1.FolderCount);
        Assert.Equal(Path.Combine(t.Root, "sub1", "deep"), sub1.Children.Single(c => c.Name == "deep").FullPath);
        Assert.Same(r.Root, sub1.Parent);
    }

    [Fact]
    public async Task Scan_AggregatesSmallFilesBeyondTopN()
    {
        using var t = new TempTree();
        var r = await SpaceScanner.ScanAsync(t.Root, new SpaceScanOptions { MaxFilesPerFolder = 3 }, ct: TestContext.Current.CancellationToken);
        var many = r.Root.Children.Single(c => c.Name == "many");
        Assert.Equal(4, many.Children.Length);
        Assert.Equal(new[] { "f09.dat", "f08.dat", "f07.dat" }, many.Children.Where(c => c.Kind == SpaceNodeKind.File).Select(c => c.Name).ToArray());
        var small = many.Children.Single(c => c.Kind == SpaceNodeKind.SmallFiles);
        Assert.Equal(7, small.FileCount);
        Assert.Equal(Enumerable.Range(0, 7).Sum(i => 100 + i), small.Size);
        Assert.Equal(10, many.FileCount);
        Assert.Equal(ExpectedTotal, r.TotalBytes); // agregação não perde bytes
    }

    [Fact]
    public async Task Scan_DoesNotFollowJunctions()
    {
        using var t = new TempTree();
        using var target = new TempTree(); // destino com conteúdo próprio
        Assert.SkipUnless(t.TryCreateJunction("link", target.Root), "mklink /J indisponível neste ambiente");
        var r = await SpaceScanner.ScanAsync(t.Root, ct: TestContext.Current.CancellationToken);
        Assert.Equal(ExpectedTotal, r.TotalBytes);
        Assert.Equal(1, r.LinkCount);
        var link = r.Root.Children.Single(c => c.Name == "link");
        Assert.Equal(SpaceNodeKind.Link, link.Kind);
        Assert.Equal(0, link.Size);
    }

    [Fact]
    public async Task Scan_AllocatedMode_IsAtLeastLogicalForNonCompressedFiles()
    {
        using var t = new TempTree();
        var r = await SpaceScanner.ScanAsync(t.Root, new SpaceScanOptions { SizeMode = SpaceSizeMode.Allocated }, ct: TestContext.Current.CancellationToken);
        Assert.Equal(SpaceSizeMode.Allocated, r.SizeMode);
        Assert.True(r.TotalBytes >= ExpectedTotal);
        Assert.Equal(0, r.TotalBytes % 512);
    }

    [Fact]
    public async Task Scan_ReportsProgress_AndHonorsCancellation()
    {
        using var t = new TempTree();
        var reports = new List<SpaceScanProgress>();
        var progress = new SyncProgress<SpaceScanProgress>(reports.Add);
        await SpaceScanner.ScanAsync(t.Root, progress: progress, ct: TestContext.Current.CancellationToken);
        Assert.NotEmpty(reports);
        Assert.Equal(15, reports[^1].Files);

        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SpaceScanner.ScanAsync(t.Root, ct: cts.Token));
    }

    [Theory]
    [InlineData(@"\\?\C:\Windows")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\server\share")]
    [InlineData(@"relative\path")]
    [InlineData("")]
    [InlineData(@"C:\this\does\not\exist\rbm")]
    public void ValidatePath_RejectsUnsafeOrInvalidPaths_ForAgent(string path) =>
        Assert.NotNull(SpaceScanner.ValidatePath(path, requireLocalFixed: true, out _));

    [Fact]
    public void ValidatePath_AcceptsLocalFolder()
    {
        using var t = new TempTree();
        Assert.Null(SpaceScanner.ValidatePath(t.Root + "\\", requireLocalFixed: true, out var full));
        Assert.Equal(t.Root, full);
    }

    [Fact]
    public async Task AgentHandler_RejectsDevicePath()
    {
        await Assert.ThrowsAsync<ArgumentException>(() => SpaceScanner.ScanForAgentAsync(new StorageScanRequest(@"\\.\C:"), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task AgentHandler_RoundTripsThroughAgentJson()
    {
        using var t = new TempTree();
        var resp = await SpaceScanner.ScanForAgentAsync(new StorageScanRequest(t.Root, 1000), TestContext.Current.CancellationToken);
        var json = JsonSerializer.SerializeToElement(resp, AgentJson.Options);
        var back = json.Deserialize<StorageScanResponse>(AgentJson.Options)!;
        var r = SpaceTreeCodec.FromResponse(back);
        Assert.True(r.Elevated);
        Assert.Equal(ExpectedTotal, r.TotalBytes);
        Assert.Equal(15, r.Files);
        Assert.Equal(2307, r.Root.Children.Single(c => c.Name == "sub1").Size);
        Assert.Equal(Path.Combine(t.Root, "sub1", "deep", "deeper"),
            r.Root.Children.Single(c => c.Name == "sub1").Children.Single(c => c.Name == "deep").Children.Single(c => c.Name == "deeper").FullPath);
        // payload da requisição também precisa sobreviver ao JSON do canal (parâmetros opcionais).
        var req = JsonSerializer.SerializeToElement(new { path = @"C:\x" }, AgentJson.Options).Deserialize<StorageScanRequest>(AgentJson.Options)!;
        Assert.Equal(50_000, req.MaxNodes);
    }

    private sealed class SyncProgress<T>(Action<T> a) : IProgress<T> { public void Report(T value) { lock (this) a(value); } }
}

public class SpaceTreeCodecTests
{
    /// <summary>Árvore sintética grande (sem disco).</summary>
    private static SpaceScanResult Synthetic(int fanout, int depth)
    {
        var root = new SpaceNode(@"C:\", SpaceNodeKind.Folder, null);
        var rnd = new Random(7);
        void Build(SpaceNode n, int d)
        {
            var kids = new List<SpaceNode>();
            for (int i = 0; i < fanout; i++)
            {
                if (d < depth) { var f = new SpaceNode("pasta" + i, SpaceNodeKind.Folder, n); Build(f, d + 1); kids.Add(f); }
                else kids.Add(new SpaceNode($"arquivo{i}.bin", SpaceNodeKind.File, n) { Size = rnd.Next(1, 1_000_000), FileCount = 1 });
            }
            n.Children = kids.ToArray();
        }
        Build(root, 1);
        SpaceScanner.Finalize(root);
        return new SpaceScanResult { Root = root, SizeMode = SpaceSizeMode.Logical };
    }

    [Fact]
    public void Prune_RespectsNodeBudget_PreservesTotals_AndStaysSmall()
    {
        var r = Synthetic(fanout: 12, depth: 5); // ~270k nós
        var resp = SpaceTreeCodec.ToResponse(r, maxNodes: 5_000);
        Assert.True(resp.Nodes.Count <= 5_000, $"{resp.Nodes.Count} nós");
        Assert.True(resp.Pruned);
        var json = JsonSerializer.SerializeToUtf8Bytes(resp, AgentJson.Options);
        Assert.True(json.Length < 2 * 1024 * 1024, $"{json.Length} bytes");
        var back = SpaceTreeCodec.FromResponse(JsonSerializer.Deserialize<StorageScanResponse>(json, AgentJson.Options)!);
        Assert.Equal(r.TotalBytes, back.TotalBytes);
        Assert.Equal(r.Files, back.Files);
        // Em cada nível, a soma dos filhos (incluindo "(outros)") continua igual ao pai quando o nó não foi truncado.
        foreach (var n in Walk(back.Root))
            if (n.Children.Length > 0) Assert.Equal(n.Size, n.Children.Sum(c => c.Size));
        Assert.Contains(Walk(back.Root), n => n.IsTruncated);
    }

    [Fact]
    public void DeepChain_SerializesWithoutHittingJsonDepthLimit()
    {
        var root = new SpaceNode(@"C:\deep", SpaceNodeKind.Folder, null);
        var cur = root;
        for (int i = 0; i < 300; i++)
        {
            var c = new SpaceNode("n" + i, SpaceNodeKind.Folder, cur);
            cur.Children = [c];
            cur = c;
        }
        cur.Children = [new SpaceNode("leaf.txt", SpaceNodeKind.File, cur) { Size = 42, FileCount = 1 }];
        SpaceScanner.Finalize(root);
        var resp = SpaceTreeCodec.ToResponse(new SpaceScanResult { Root = root, SizeMode = SpaceSizeMode.Logical });
        var json = JsonSerializer.Serialize(resp, AgentJson.Options);
        var back = SpaceTreeCodec.FromResponse(JsonSerializer.Deserialize<StorageScanResponse>(json, AgentJson.Options)!);
        Assert.Equal(42, back.TotalBytes);
        Assert.Equal(301, back.Root.Depth + Walk(back.Root).Max(n => n.Depth));
    }

    [Fact]
    public void FromResponse_RejectsBadParentIndex()
    {
        var bad = new StorageScanResponse(@"C:\", false, 0, 0, 0, 0, [], 0, 0, false, false,
            [new StorageScanNode { Parent = -1, Name = "C:\\" }, new StorageScanNode { Parent = 5, Name = "x" }]);
        Assert.Throws<InvalidDataException>(() => SpaceTreeCodec.FromResponse(bad));
    }

    [Theory]
    [InlineData("filme.MKV", FileCategory.Video)]
    [InlineData("foto.jpeg", FileCategory.Image)]
    [InlineData("backup.7z", FileCategory.Archive)]
    [InlineData("app.exe", FileCategory.Executable)]
    [InlineData("pagefile.sys", FileCategory.System)]
    [InlineData("disk.vhdx", FileCategory.DiskImage)]
    [InlineData("semextensao", FileCategory.Other)]
    public void FileCategories_Classify(string name, FileCategory expected) => Assert.Equal(expected, FileCategories.Classify(name));

    private static IEnumerable<SpaceNode> Walk(SpaceNode root)
    {
        var s = new Stack<SpaceNode>(); s.Push(root);
        while (s.Count > 0) { var n = s.Pop(); yield return n; foreach (var c in n.Children) s.Push(c); }
    }
}
