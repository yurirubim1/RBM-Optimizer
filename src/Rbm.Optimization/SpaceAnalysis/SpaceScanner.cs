using System.Collections.Concurrent;
using System.Diagnostics;

namespace Rbm.Optimization.SpaceAnalysis;

/// <summary>Modo de medição do tamanho.</summary>
public enum SpaceSizeMode
{
    /// <summary>Tamanho lógico (o que o Explorer mostra em "Tamanho"). Padrão.</summary>
    Logical = 0,
    /// <summary>Espaço alocado em disco (arredondado ao cluster; real para compactados/esparsos/OneDrive).</summary>
    Allocated = 1,
}

public sealed record SpaceScanOptions
{
    public SpaceSizeMode SizeMode { get; init; } = SpaceSizeMode.Logical;
    /// <summary>Arquivos mantidos individualmente por pasta (os maiores); o resto vira "(arquivos menores)".</summary>
    public int MaxFilesPerFolder { get; init; } = 48;
    /// <summary>Threads de varredura (limitado). Padrão: metade dos núcleos lógicos, entre 2 e 6.</summary>
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 2, 6);
    /// <summary>Limite de tempo; ao estourar, devolve resultado parcial (pastas pendentes marcadas como truncadas).</summary>
    public TimeSpan? TimeLimit { get; init; }
    /// <summary>Habilita SeBackupPrivilege nas threads de varredura (efetivo somente no agente elevado).</summary>
    public bool UseBackupPrivilege { get; init; }
    /// <summary>Máximo de caminhos inacessíveis guardados (a contagem total é sempre exata).</summary>
    public int MaxInaccessibleSamples { get; init; } = 500;
}

/// <summary>Progresso da varredura (emitido a ~10 Hz).</summary>
public readonly record struct SpaceScanProgress(long Files, long Folders, long Bytes, int Inaccessible, string? CurrentPath, TimeSpan Elapsed);

public sealed class SpaceScanResult
{
    public required SpaceNode Root { get; init; }
    public required SpaceSizeMode SizeMode { get; init; }
    public long TotalBytes => Root.Size;
    public long Files => Root.FileCount;
    public long Folders => Root.FolderCount;
    public int InaccessibleCount { get; init; }
    public IReadOnlyList<string> InaccessiblePaths { get; init; } = [];
    /// <summary>Junções/links simbólicos encontrados e NÃO seguidos (contam 0).</summary>
    public int LinkCount { get; init; }
    public TimeSpan Duration { get; init; }
    /// <summary>Interrompida pelo limite de tempo (resultado parcial).</summary>
    public bool Partial { get; init; }
    /// <summary>Árvore podada para transporte (algumas pastas sem detalhes).</summary>
    public bool Pruned { get; init; }
    /// <summary>Resultado veio do agente elevado (administrador).</summary>
    public bool Elevated { get; init; }
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Varredura recursiva rápida de espaço em disco: FindFirstFileExW (FindExInfoBasic + LARGE_FETCH), N threads com pilha
/// de trabalho compartilhada (paralelismo limitado em todos os níveis), sem seguir junções/links simbólicos, contando pastas
/// inacessíveis. Somente leitura: nunca abre, altera ou apaga arquivos.
/// </summary>
public static class SpaceScanner
{
    public const string SmallFilesName = "(arquivos menores)";
    public const string OthersName = "(outros)";

    private static readonly Comparison<SpaceNode> BySizeDesc = static (a, b) => b.Size.CompareTo(a.Size);

    /// <summary>
    /// Valida o caminho. Sempre rejeita caminhos de dispositivo (\\?\, \\.\). Com <paramref name="requireLocalFixed"/>,
    /// exige unidade local fixa, rejeita UNC e rejeita raiz que seja junção/link. Retorna mensagem de erro (pt-BR) ou null.
    /// </summary>
    public static string? ValidatePath(string? path, bool requireLocalFixed, out string fullPath)
    {
        fullPath = "";
        if (string.IsNullOrWhiteSpace(path)) return "Informe uma pasta ou unidade.";
        path = path.Trim();
        if (path.IndexOf('\0') >= 0 || path.Length > 32000) return "Caminho inválido.";
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)
            || path.StartsWith("//?/", StringComparison.Ordinal) || path.StartsWith("//./", StringComparison.Ordinal))
            return "Caminhos de dispositivo (\\\\?\\, \\\\.\\) não são aceitos.";
        bool unc = path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal);
        if (unc && requireLocalFixed) return "Caminhos de rede (UNC) não são aceitos na varredura elevada.";
        if (!Path.IsPathFullyQualified(path)) return "O caminho precisa ser absoluto (ex.: C:\\Users).";
        try { fullPath = Path.GetFullPath(path); }
        catch (Exception ex) { return "Caminho inválido: " + ex.Message; }
        var root = Path.GetPathRoot(fullPath) ?? "";
        if (fullPath.Length > root.Length) fullPath = fullPath.TrimEnd('\\', '/');
        if (requireLocalFixed)
        {
            try
            {
                var di = new DriveInfo(root);
                if (di.DriveType != DriveType.Fixed) return $"A unidade {root} não é um disco local fixo.";
                if (!di.IsReady) return $"A unidade {root} não está pronta.";
            }
            catch (Exception ex) { return "Unidade inválida: " + ex.Message; }
        }
        if (!Directory.Exists(fullPath)) return "Pasta não encontrada: " + fullPath;
        if (requireLocalFixed && fullPath.Length > root.Length)
        {
            try { if (new DirectoryInfo(fullPath).LinkTarget is not null) return "A pasta escolhida é uma junção/link simbólico; escolha o destino real."; }
            catch { }
        }
        return null;
    }

    /// <summary>Varre <paramref name="path"/> (em threads dedicadas; não bloqueia o chamador).</summary>
    public static async Task<SpaceScanResult> ScanAsync(string path, SpaceScanOptions? options = null, IProgress<SpaceScanProgress>? progress = null, CancellationToken ct = default)
    {
        var err = ValidatePath(path, requireLocalFixed: false, out var full);
        if (err is not null) throw new ArgumentException(err, nameof(path));
        options ??= new SpaceScanOptions();
        var st = new ScanState(options, ct, NativeFind.ToLongPath(full), full);
        var root = new SpaceNode(full, SpaceNodeKind.Folder, null);
        st.Push(root, st.RootLongPath);

        int workers = Math.Clamp(options.MaxParallelism, 1, 16);
        var tasks = new Task[workers];
        for (int i = 0; i < workers; i++)
            tasks[i] = Task.Factory.StartNew(st.Worker, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

        using var reporterCts = new CancellationTokenSource();
        var reporter = progress is null ? Task.CompletedTask : ReportLoopAsync(st, progress, reporterCts.Token);
        try { await Task.WhenAll(tasks).ConfigureAwait(false); }
        finally
        {
            reporterCts.Cancel();
            try { await reporter.ConfigureAwait(false); } catch (OperationCanceledException) { }
        }
        ct.ThrowIfCancellationRequested();
        progress?.Report(st.Snapshot());

        Finalize(root);
        return new SpaceScanResult
        {
            Root = root,
            SizeMode = options.SizeMode,
            InaccessibleCount = st.Inaccessible,
            InaccessiblePaths = st.InaccessiblePaths.ToArray(),
            LinkCount = st.Links,
            Duration = st.Clock.Elapsed,
            Partial = st.TimedOut,
        };
    }

    /// <summary>
    /// Handler do agente elevado para <c>storage.scan</c>. Revalida o payload (caminho absoluto, unidade local fixa, sem
    /// \\?\, \\.\ ou UNC, raiz que não seja link), varre com SeBackupPrivilege por thread (somente leitura) e devolve a
    /// árvore podada/achatada. Lança <see cref="ArgumentException"/> para payload inválido.
    /// </summary>
    public static async Task<StorageScanResponse> ScanForAgentAsync(StorageScanRequest req, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(req);
        var err = ValidatePath(req.Path, requireLocalFixed: true, out var full);
        if (err is not null) throw new ArgumentException(err);
        int maxNodes = Math.Clamp(req.MaxNodes <= 0 ? 50_000 : req.MaxNodes, 1_000, 100_000);
        int limit = Math.Clamp(req.TimeLimitSeconds <= 0 ? 1_500 : req.TimeLimitSeconds, 60, 3_600);
        var result = await ScanAsync(full, new SpaceScanOptions
        {
            SizeMode = req.AllocatedSize ? SpaceSizeMode.Allocated : SpaceSizeMode.Logical,
            TimeLimit = TimeSpan.FromSeconds(limit),
            UseBackupPrivilege = true,
        }, null, ct).ConfigureAwait(false);
        return SpaceTreeCodec.ToResponse(result, maxNodes);
    }

    private static async Task ReportLoopAsync(ScanState st, IProgress<SpaceScanProgress> progress, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
        while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false)) progress.Report(st.Snapshot());
    }

    /// <summary>Pós-ordem iterativa: soma tamanhos/contagens de baixo para cima e ordena filhos por tamanho.</summary>
    internal static void Finalize(SpaceNode root)
    {
        var stack = new Stack<(SpaceNode Node, bool Expanded)>();
        stack.Push((root, false));
        while (stack.Count > 0)
        {
            var (n, expanded) = stack.Pop();
            if (!expanded)
            {
                stack.Push((n, true));
                foreach (var c in n.Children)
                    if (c.Kind == SpaceNodeKind.Folder && c.Children.Length > 0) stack.Push((c, false));
                continue;
            }
            long size = 0, files = 0, folders = 0;
            foreach (var c in n.Children)
            {
                size += c.Size; files += c.FileCount;
                folders += c.FolderCount + (c.Kind == SpaceNodeKind.Folder ? 1 : 0);
            }
            n.Size = size; n.FileCount = files; n.FolderCount = folders;
            if (n.Children.Length > 1) Array.Sort(n.Children, BySizeDesc);
        }
    }

    internal static string DisplayPath(string longPath) =>
        longPath.StartsWith(@"\\?\UNC\", StringComparison.Ordinal) ? @"\\" + longPath[8..]
        : longPath.StartsWith(@"\\?\", StringComparison.Ordinal) ? longPath[4..] : longPath;

    /// <summary>Estado compartilhado entre as threads de varredura.</summary>
    private sealed class ScanState(SpaceScanOptions opt, CancellationToken ct, string rootLongPath, string rootPath)
    {
        private readonly Stack<(SpaceNode Node, string Path)> _work = new();
        private readonly object _lock = new();
        private readonly long _clusterSize = opt.SizeMode == SpaceSizeMode.Allocated ? NativeFind.ClusterSize(rootPath) : 1;
        private int _pending;
        private long _files, _folders, _bytes;
        private int _inaccessible, _links, _samples;
        private volatile string? _current;
        private volatile bool _timedOut;

        public string RootLongPath { get; } = rootLongPath;
        public Stopwatch Clock { get; } = Stopwatch.StartNew();
        public ConcurrentQueue<string> InaccessiblePaths { get; } = new();
        public int Inaccessible => Volatile.Read(ref _inaccessible);
        public int Links => Volatile.Read(ref _links);
        public bool TimedOut => _timedOut;

        public SpaceScanProgress Snapshot() => new(Interlocked.Read(ref _files), Interlocked.Read(ref _folders), Interlocked.Read(ref _bytes),
            Inaccessible, _current is { } c ? DisplayPath(c) : null, Clock.Elapsed);

        public void Push(SpaceNode node, string path)
        {
            Interlocked.Increment(ref _pending);
            lock (_lock) _work.Push((node, path));
        }

        private bool TryPop(out (SpaceNode Node, string Path) item)
        {
            lock (_lock) return _work.TryPop(out item);
        }

        public void Worker()
        {
            try { Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; } catch { }
            bool backup = opt.UseBackupPrivilege && NativeFind.BeginBackupIntent();
            var heap = new PriorityQueue<SpaceNode, long>();
            var kids = new List<SpaceNode>(64);
            var subdirs = new List<(SpaceNode, string)>(32);
            try
            {
                while (true)
                {
                    if (ct.IsCancellationRequested) return;
                    if (!TryPop(out var item))
                    {
                        if (Volatile.Read(ref _pending) == 0) return;
                        Thread.Sleep(1);
                        continue;
                    }
                    try
                    {
                        if (!_timedOut && opt.TimeLimit is { } lim && Clock.Elapsed > lim) _timedOut = true;
                        if (_timedOut) item.Node.IsTruncated = true;
                        else ProcessDirectory(item.Node, item.Path, heap, kids, subdirs);
                    }
                    finally { Interlocked.Decrement(ref _pending); }
                }
            }
            finally { if (backup) NativeFind.EndBackupIntent(); }
        }

        private void MarkInaccessible(SpaceNode node, string path)
        {
            node.IsInaccessible = true;
            Interlocked.Increment(ref _inaccessible);
            if (Interlocked.Increment(ref _samples) <= opt.MaxInaccessibleSamples) InaccessiblePaths.Enqueue(DisplayPath(path));
        }

        private void ProcessDirectory(SpaceNode node, string path, PriorityQueue<SpaceNode, long> heap, List<SpaceNode> kids, List<(SpaceNode, string)> subdirs)
        {
            _current = path;
            var h = NativeFind.Open(path, out var fd, out int error);
            if (h == IntPtr.Zero)
            {
                if (error is not (NativeFind.ErrorFileNotFound or NativeFind.ErrorNoMoreFiles or NativeFind.ErrorPathNotFound))
                    MarkInaccessible(node, path);
                Interlocked.Increment(ref _folders);
                return;
            }

            int maxFiles = Math.Max(1, opt.MaxFilesPerFolder);
            long localBytes = 0, smallBytes = 0, smallCount = 0, localFiles = 0;
            int localLinks = 0, n = 0;
            try
            {
                do
                {
                    if ((++n & 0xFFF) == 0 && ct.IsCancellationRequested) break;
                    if (NativeFind.IsDots(ref fd)) continue;
                    uint attr = fd.dwFileAttributes;
                    bool surrogate = (attr & NativeFind.AttrReparsePoint) != 0 && NativeFind.IsNameSurrogate(fd.dwReserved0);
                    if ((attr & NativeFind.AttrDirectory) != 0)
                    {
                        var name = NativeFind.Name(ref fd);
                        if (surrogate) { kids.Add(new SpaceNode(name, SpaceNodeKind.Link, node)); localLinks++; continue; }
                        var child = new SpaceNode(name, SpaceNodeKind.Folder, node);
                        kids.Add(child);
                        subdirs.Add((child, path.EndsWith('\\') ? path + name : path + "\\" + name));
                        continue;
                    }
                    if (surrogate) { kids.Add(new SpaceNode(NativeFind.Name(ref fd), SpaceNodeKind.Link, node)); localLinks++; continue; }

                    long size = fd.Length;
                    string? fileName = null;
                    if (opt.SizeMode == SpaceSizeMode.Allocated)
                    {
                        if ((attr & (NativeFind.AttrCompressed | NativeFind.AttrSparse | NativeFind.AttrReparsePoint)) != 0)
                        {
                            fileName = NativeFind.Name(ref fd);
                            long a = NativeFind.CompressedSize(path.EndsWith('\\') ? path + fileName : path + "\\" + fileName);
                            if (a >= 0) size = a;
                        }
                        else if (size > 0) size = (size + _clusterSize - 1) / _clusterSize * _clusterSize;
                    }
                    localFiles++; localBytes += size;

                    // Top-N por pasta sem alocar nome para arquivos que não entram.
                    if (heap.Count < maxFiles)
                        heap.Enqueue(MakeFile(fileName ?? NativeFind.Name(ref fd), size, node), size);
                    else if (heap.TryPeek(out _, out long min) && size > min)
                    {
                        var evicted = heap.EnqueueDequeue(MakeFile(fileName ?? NativeFind.Name(ref fd), size, node), size);
                        smallBytes += evicted.Size; smallCount++;
                    }
                    else { smallBytes += size; smallCount++; }
                }
                while (NativeFind.FindNextFileW(h, out fd));
            }
            finally { NativeFind.FindClose(h); }

            while (heap.TryDequeue(out var f, out _)) kids.Add(f);
            if (smallCount > 0) kids.Add(new SpaceNode(SmallFilesName, SpaceNodeKind.SmallFiles, node) { Size = smallBytes, FileCount = smallCount });
            node.Children = kids.Count == 0 ? [] : kids.ToArray();
            kids.Clear();

            foreach (var (c, p) in subdirs) Push(c, p);
            subdirs.Clear();

            Interlocked.Add(ref _files, localFiles);
            Interlocked.Add(ref _bytes, localBytes);
            Interlocked.Increment(ref _folders);
            if (localLinks > 0) Interlocked.Add(ref _links, localLinks);
        }

        private static SpaceNode MakeFile(string name, long size, SpaceNode parent) =>
            new(name, SpaceNodeKind.File, parent) { Size = size, FileCount = 1 };
    }
}
