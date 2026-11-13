using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using Rbm.Benchmarks.Framework;
using Microsoft.Win32.SafeHandles;

namespace Rbm.Benchmarks.Storage;

/// <summary>
/// Desempenho de um volume com arquivo temporário sem cache do Windows (FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH).
/// ID: storage.volume. Métrica principal: leitura sequencial 1 MiB QD1 (MB/s). Testes de gravação são opt-in.
/// </summary>
public sealed class StorageBenchmark : IterativeBenchmark
{
    public const string TempFolderName = "RBMBench";
    public const string TempFilePrefix = "rbmbench_";
    public const int SeqBlock = 1024 * 1024;
    public const int RandBlock = 4096;
    public const int HighQueueDepth = 32;

    public override string Id => "storage.volume";
    public override string Name => "Armazenamento — volume (sequencial e aleatório 4 KiB)";
    public override BenchmarkCategory Category => BenchmarkCategory.Storage;
    public override string Unit => BenchmarkUnits.MegabytesPerSecond;
    public override bool HigherIsBetter => true;
    public override bool WritesToStorage => true;

    public override string Methodology =>
        "Arquivo temporário dedicado (padrão 1 GiB, configurável) em <raiz>\\RBMBench, ou %TEMP%\\RBMBench na unidade do sistema, aberto com " +
        "FILE_FLAG_NO_BUFFERING | FILE_FLAG_WRITE_THROUGH | FILE_FLAG_OVERLAPPED | FILE_FLAG_DELETE_ON_CLOSE (sem cache do Windows; o arquivo é apagado " +
        "mesmo se o processo for encerrado). Buffers alinhados a 4 KiB (NativeMemory.AlignedAlloc), conteúdo pseudoaleatório incompressível (semente fixa). " +
        "Exige espaço livre ≥ 2× o tamanho + 1 GiB. Preparação: o arquivo é gravado uma vez por inteiro (necessário para que as leituras atinjam o dispositivo). " +
        "Por iteração: (1) leitura sequencial em blocos de 1 MiB, QD1, até ler o arquivo inteiro ou 3 s; (2) leitura aleatória 4 KiB QD1 por 2 s; " +
        "(3) leitura aleatória 4 KiB QD32 (32 threads, uma E/S sobreposta pendente cada) por 2 s. Offsets aleatórios alinhados a 4 KiB, xorshift64* com semente fixa por iteração/thread. " +
        "Com gravação habilitada (opt-in): as mesmas medições de gravação. MB = 10^6 bytes; latência por E/S medida com Stopwatch (média e P99, em µs). " +
        "Limitações: a região testada (1 GiB) costuma caber no cache SLC/DRAM do SSD, como em ferramentas populares; em HDD mede só o início da faixa do arquivo. " +
        "O arquivo é sempre removido (finally, cancelamento e DELETE_ON_CLOSE).";

    private static readonly MetricDefinition[] s_metrics =
    {
        new("seq.read.mbps", "Leitura sequencial 1 MiB QD1", BenchmarkUnits.MegabytesPerSecond, true),
        new("rnd4k.qd1.read.iops", "Leitura aleatória 4 KiB QD1", BenchmarkUnits.Iops, true),
        new("rnd4k.qd1.read.mbps", "Leitura aleatória 4 KiB QD1", BenchmarkUnits.MegabytesPerSecond, true),
        new("rnd4k.qd1.read.lat.avg", "Leitura aleatória 4 KiB QD1 — latência média", BenchmarkUnits.Microseconds, false),
        new("rnd4k.qd1.read.lat.p99", "Leitura aleatória 4 KiB QD1 — latência P99", BenchmarkUnits.Microseconds, false),
        new("rnd4k.qd32.read.iops", "Leitura aleatória 4 KiB QD32", BenchmarkUnits.Iops, true),
        new("rnd4k.qd32.read.mbps", "Leitura aleatória 4 KiB QD32", BenchmarkUnits.MegabytesPerSecond, true),
        new("rnd4k.qd32.read.lat.avg", "Leitura aleatória 4 KiB QD32 — latência média", BenchmarkUnits.Microseconds, false),
        new("rnd4k.qd32.read.lat.p99", "Leitura aleatória 4 KiB QD32 — latência P99", BenchmarkUnits.Microseconds, false),
        new("seq.write.mbps", "Gravação sequencial 1 MiB QD1", BenchmarkUnits.MegabytesPerSecond, true),
        new("rnd4k.qd1.write.iops", "Gravação aleatória 4 KiB QD1", BenchmarkUnits.Iops, true),
        new("rnd4k.qd1.write.mbps", "Gravação aleatória 4 KiB QD1", BenchmarkUnits.MegabytesPerSecond, true),
        new("rnd4k.qd1.write.lat.avg", "Gravação aleatória 4 KiB QD1 — latência média", BenchmarkUnits.Microseconds, false),
        new("rnd4k.qd1.write.lat.p99", "Gravação aleatória 4 KiB QD1 — latência P99", BenchmarkUnits.Microseconds, false),
        new("rnd4k.qd32.write.iops", "Gravação aleatória 4 KiB QD32", BenchmarkUnits.Iops, true),
        new("rnd4k.qd32.write.mbps", "Gravação aleatória 4 KiB QD32", BenchmarkUnits.MegabytesPerSecond, true),
        new("rnd4k.qd32.write.lat.avg", "Gravação aleatória 4 KiB QD32 — latência média", BenchmarkUnits.Microseconds, false),
        new("rnd4k.qd32.write.lat.p99", "Gravação aleatória 4 KiB QD32 — latência P99", BenchmarkUnits.Microseconds, false),
    };

    public override IReadOnlyList<MetricDefinition> SecondaryMetrics => s_metrics;

    /// <summary>Pasta padrão do arquivo temporário para um volume (ex.: "D:\" → "D:\RBMBench"; unidade do sistema → %TEMP%\RBMBench).</summary>
    public static string ResolveDirectory(string? volume)
    {
        string systemRoot = Path.GetPathRoot(Environment.SystemDirectory) ?? "C:\\";
        string root = string.IsNullOrWhiteSpace(volume) ? systemRoot : NormalizeRoot(volume);
        return string.Equals(root, systemRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.Combine(Path.GetTempPath(), TempFolderName)
            : Path.Combine(root, TempFolderName);
    }

    private static string NormalizeRoot(string volume)
    {
        string v = volume.Trim();
        if (v.Length == 1 && char.IsLetter(v[0])) v += ":";
        if (v.Length == 2 && v[1] == ':') v += "\\";
        return Path.GetPathRoot(Path.GetFullPath(v)) ?? v;
    }

    internal override BenchmarkSession CreateSession(BenchmarkOptions options) => new Session(options);

    private sealed unsafe class Session(BenchmarkOptions o) : BenchmarkSession
    {

        private string? _dir, _path;
        private bool _ownsDir;
        private SafeFileHandle? _handle;
        private IoContext? _seq;
        private IoContext[] _rand = Array.Empty<IoContext>();
        private WorkerGroup? _qd1, _qdN;
        private long _size;
        private long _bytesWritten;
        private int _iteration;
        private bool _latencyTruncated;

        public override void Prepare(SessionContext ctx)
        {
            _size = o.StorageFileBytes / SeqBlock * SeqBlock;
            _dir = o.StorageDirectory is { Length: > 0 } d ? Path.GetFullPath(d) : ResolveDirectory(o.StorageVolume);
            _ownsDir = o.StorageDirectory is null or { Length: 0 };

            string root = Path.GetPathRoot(_dir) ?? throw new BenchmarkFailureException("Caminho de destino inválido.");
            DriveInfo drive;
            try { drive = new DriveInfo(root); }
            catch (Exception ex) { throw new BenchmarkFailureException($"Volume inválido: {root} ({ex.Message})."); }
            if (!drive.IsReady) throw new BenchmarkFailureException($"O volume {root} não está pronto.");
            if (drive.DriveType is not (DriveType.Fixed or DriveType.Removable))
                throw new BenchmarkFailureException($"Tipo de unidade não suportado para o teste: {drive.DriveType} ({root}).");
            long need = 2 * _size + o.StorageFreeSpaceMarginBytes;
            if (drive.AvailableFreeSpace < need)
                throw new BenchmarkFailureException(
                    $"Espaço livre insuficiente em {root}: necessário {need / (double)BenchmarkOptions.GiB:0.00} GiB (2× o arquivo + margem), " +
                    $"disponível {drive.AvailableFreeSpace / (double)BenchmarkOptions.GiB:0.00} GiB.");

            Directory.CreateDirectory(_dir);
            CleanupStaleFiles(_dir);
            _path = Path.Combine(_dir, $"{TempFilePrefix}{Guid.NewGuid():N}.tmp");
            // CreateFileW direto (e não File.OpenHandle): o handle NÃO é associado ao IOCP do pool de threads do .NET,
            // pois as conclusões são aguardadas pelo evento de cada OVERLAPPED.
            _handle = StorageNative.CreateFileW(_path, StorageNative.GENERIC_READ | StorageNative.GENERIC_WRITE, 0, 0,
                StorageNative.CREATE_NEW,
                StorageNative.FILE_FLAG_NO_BUFFERING | StorageNative.FILE_FLAG_WRITE_THROUGH | StorageNative.FILE_FLAG_OVERLAPPED | StorageNative.FILE_FLAG_DELETE_ON_CLOSE,
                0);
            if (_handle.IsInvalid)
            {
                int err = Marshal.GetLastPInvokeError();
                _handle.Dispose();
                _handle = null;
                throw new BenchmarkFailureException($"Não foi possível criar o arquivo de teste em {_dir}: código Win32 {err}.");
            }

            _seq = new IoContext(SeqBlock);
            _rand = new IoContext[HighQueueDepth];
            for (int i = 0; i < _rand.Length; i++) _rand[i] = new IoContext(RandBlock);
            _qd1 = new WorkerGroup(1, "RBMBench IO QD1");
            _qdN = new WorkerGroup(HighQueueDepth, "RBMBench IO QD32");

            // Preenchimento: grava o arquivo inteiro uma vez (dados incompressíveis).
            long lastReport = 0;
            for (long off = 0; off < _size; off += SeqBlock)
            {
                ctx.Ct.ThrowIfCancellationRequested();
                _seq.Stamp((ulong)off);
                _seq.Execute(_handle, off, write: true);
                _bytesWritten += SeqBlock;
                if (off - lastReport >= 128 * BenchmarkOptions.MiB)
                {
                    lastReport = off;
                    ctx.Detail($"Criando arquivo de teste: {off * 100 / _size}%...");
                }
            }

            Parameters["fileBytes"] = _size.ToString(CultureInfo.InvariantCulture);
            Parameters["writeTests"] = o.StorageIncludeWriteTests ? "1" : "0";
            Parameters["seqSeconds"] = o.StorageSequentialDuration.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
            Parameters["randSeconds"] = o.StorageRandomDuration.TotalSeconds.ToString("R", CultureInfo.InvariantCulture);
            Parameters["volume"] = root.ToUpperInvariant();
            Info["fileSystem"] = drive.DriveFormat;
            Info["directory"] = _dir;
        }

        public override IterationMeasurement Measure(SessionContext ctx, bool warmup)
        {
            _iteration++;
            var m = new Dictionary<string, double>();
            ctx.Detail("Leitura sequencial 1 MiB...");
            double seqRead = Sequential(ctx.Ct, write: false);
            m["seq.read.mbps"] = seqRead;
            ctx.Detail("Leitura aleatória 4 KiB QD1...");
            Random(ctx.Ct, _qd1!, write: false, "rnd4k.qd1.read", m);
            ctx.Detail("Leitura aleatória 4 KiB QD32...");
            Random(ctx.Ct, _qdN!, write: false, "rnd4k.qd32.read", m);
            if (o.StorageIncludeWriteTests)
            {
                ctx.Detail("Gravação sequencial 1 MiB...");
                m["seq.write.mbps"] = Sequential(ctx.Ct, write: true);
                ctx.Detail("Gravação aleatória 4 KiB QD1...");
                Random(ctx.Ct, _qd1!, write: true, "rnd4k.qd1.write", m);
                ctx.Detail("Gravação aleatória 4 KiB QD32...");
                Random(ctx.Ct, _qdN!, write: true, "rnd4k.qd32.write", m);
            }
            return new IterationMeasurement(seqRead, m);
        }

        private double Sequential(CancellationToken ct, bool write)
        {
            long limit = Timing.MicrosecondsToTicks(o.StorageSequentialDuration.TotalMilliseconds * 1000);
            long bytes = 0;
            long t0 = Stopwatch.GetTimestamp();
            for (long off = 0; off < _size; off += SeqBlock)
            {
                ct.ThrowIfCancellationRequested();
                if (write) _seq!.Stamp((ulong)(off ^ ((long)_iteration << 40)));
                _seq!.Execute(_handle!, off, write);
                bytes += SeqBlock;
                if (write) Interlocked.Add(ref _bytesWritten, SeqBlock);
                if (Stopwatch.GetTimestamp() - t0 >= limit) break;
            }
            double sec = Timing.TicksToSeconds(Stopwatch.GetTimestamp() - t0);
            return bytes / sec / 1e6;
        }

        private void Random(CancellationToken ct, WorkerGroup group, bool write, string key, Dictionary<string, double> m)
        {
            int threads = group.Count;
            long blocks = _size / RandBlock;
            int cap = threads == 1 ? 1 << 20 : 1 << 16;
            var lat = new int[threads][];
            var counts = new long[threads];
            for (int i = 0; i < threads; i++) lat[i] = new int[cap];
            long durTicks = Timing.MicrosecondsToTicks(o.StorageRandomDuration.TotalMilliseconds * 1000);
            ulong seedBase = (ulong)(_iteration * 7919 + (write ? 1_000_003 : 0) + threads * 104_729);
            long deadline = Stopwatch.GetTimestamp() + durTicks;
            double seconds = group.Run(t =>
            {
                var io = _rand[t];
                var rng = new XorShift64Star(0xD15C_0000_0000UL ^ (seedBase * 0x9E3779B97F4A7C15UL) ^ (ulong)(t + 1) * 0xBF58476D1CE4E5B9UL);
                var mine = lat[t];
                long n = 0;
                while (Stopwatch.GetTimestamp() < deadline)
                {
                    ct.ThrowIfCancellationRequested();
                    long off = (long)rng.NextBelow((ulong)blocks) * RandBlock;
                    if (write) io.Stamp((ulong)off ^ rng.NextUInt64());
                    long s = Stopwatch.GetTimestamp();
                    io.Execute(_handle!, off, write);
                    long e = Stopwatch.GetTimestamp() - s;
                    if (n < mine.Length) mine[n] = (int)Math.Min(e, int.MaxValue);
                    n++;
                }
                counts[t] = n;
            });
            long ops = counts.Sum();
            if (write) Interlocked.Add(ref _bytesWritten, ops * RandBlock);
            int recorded = (int)Math.Min(int.MaxValue, Enumerable.Range(0, threads).Sum(i => Math.Min(counts[i], (long)cap)));
            if (recorded < ops) _latencyTruncated = true;
            var all = new double[recorded];
            int k = 0;
            for (int i = 0; i < threads; i++)
            {
                int c = (int)Math.Min(counts[i], cap);
                for (int j = 0; j < c; j++) all[k++] = Timing.TicksToMicroseconds(lat[i][j]);
            }
            var d = Distribution.Of(all);
            m[key + ".iops"] = ops / seconds;
            m[key + ".mbps"] = ops * (double)RandBlock / seconds / 1e6;
            m[key + ".lat.avg"] = d.Mean;
            m[key + ".lat.p99"] = d.P99;
        }

        public override void Complete(SessionContext ctx)
        {
            if (_latencyTruncated)
                Warnings.Add("Parte das latências individuais não foi registrada (limite de amostras); IOPS considera todas as operações.");
        }

        private static void CleanupStaleFiles(string dir)
        {
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, TempFilePrefix + "*.tmp"))
                {
                    try { File.Delete(f); } catch (Exception) { /* em uso por outra execução */ }
                }
            }
            catch (Exception) { }
        }

        public override void Dispose()
        {
            _qd1?.Dispose();
            _qdN?.Dispose();
            _handle?.Dispose(); // DELETE_ON_CLOSE apaga o arquivo aqui
            _seq?.Dispose();
            foreach (var r in _rand) r.Dispose();

            Info["bytesWritten"] = _bytesWritten.ToString(CultureInfo.InvariantCulture);
            if (o.StorageIncludeWriteTests)
                Warnings.Add($"Testes de gravação habilitados: {_bytesWritten / (double)BenchmarkOptions.GiB:0.00} GiB gravados no dispositivo nesta execução (inclui a preparação). Em SSDs, gravações consomem vida útil — use com moderação.");
            else if (_bytesWritten > 0)
                Info["note"] = "Somente a preparação do arquivo gravou dados (necessária para leituras reais).";

            if (_path is not null)
            {
                try { if (File.Exists(_path)) File.Delete(_path); }
                catch (Exception ex) { Warnings.Add($"Não foi possível apagar o arquivo temporário '{_path}': {ex.Message}"); }
            }
            if (_ownsDir && _dir is not null)
            {
                try { if (Directory.Exists(_dir) && !Directory.EnumerateFileSystemEntries(_dir).Any()) Directory.Delete(_dir); }
                catch (Exception) { /* pasta em uso: inofensivo */ }
            }
        }
    }

    /// <summary>Buffer alinhado + OVERLAPPED + evento próprios de uma thread de E/S.</summary>
    private sealed unsafe class IoContext : IDisposable
    {
        private readonly NativeBuffer _buf;
        private NativeOverlapped* _ov;
        private nint _event;
        private readonly int _size;

        public IoContext(int size)
        {
            _size = size;
            _buf = new NativeBuffer(size, 4096);
            new XorShift64Star(0xB10C_DA7AUL ^ (ulong)size).Fill(new Span<byte>(_buf.Pointer, size));
            _ov = (NativeOverlapped*)NativeMemory.AllocZeroed((nuint)sizeof(NativeOverlapped));
            _event = StorageNative.CreateEventW(0, true, false, 0);
            if (_event == 0) throw new BenchmarkFailureException("Falha ao criar evento de E/S.");
        }

        /// <summary>Altera os primeiros 8 bytes do bloco (evita deduplicação/compressão de blocos idênticos).</summary>
        public void Stamp(ulong v) => *(ulong*)_buf.Pointer = v * 0x9E3779B97F4A7C15UL;

        public void Execute(SafeFileHandle h, long offset, bool write)
        {
            *_ov = default;
            _ov->OffsetLow = (int)(offset & 0xFFFFFFFF);
            _ov->OffsetHigh = (int)(offset >> 32);
            _ov->EventHandle = _event | 1; // bit baixo: não enfileira conclusão em porta de conclusão (IOCP)
            bool ok = write
                ? StorageNative.WriteFile(h, _buf.Pointer, (uint)_size, null, _ov)
                : StorageNative.ReadFile(h, _buf.Pointer, (uint)_size, null, _ov);
            if (!ok)
            {
                int err = Marshal.GetLastPInvokeError();
                if (err != StorageNative.ERROR_IO_PENDING)
                    throw new BenchmarkFailureException($"Erro de E/S ({(write ? "gravação" : "leitura")}) no offset {offset}: código Win32 {err}.");
            }
            if (!StorageNative.GetOverlappedResult(h, _ov, out uint transferred, true))
                throw new BenchmarkFailureException($"Erro de E/S ({(write ? "gravação" : "leitura")}) no offset {offset}: código Win32 {Marshal.GetLastPInvokeError()}.");
            if (transferred != (uint)_size)
                throw new BenchmarkFailureException($"E/S incompleta no offset {offset}: {transferred} de {_size} bytes.");
        }

        public void Dispose()
        {
            if (_event != 0) { StorageNative.CloseHandle(_event); _event = 0; }
            if (_ov != null) { NativeMemory.Free(_ov); _ov = null; }
            _buf.Dispose();
        }
    }
}

internal static unsafe partial class StorageNative
{
    public const int ERROR_IO_PENDING = 997;
    public const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000, CREATE_NEW = 1;
    public const uint FILE_FLAG_WRITE_THROUGH = 0x80000000, FILE_FLAG_OVERLAPPED = 0x40000000,
        FILE_FLAG_NO_BUFFERING = 0x20000000, FILE_FLAG_DELETE_ON_CLOSE = 0x04000000;

    [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFileW(string fileName, uint desiredAccess, uint shareMode, nint securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, nint templateFile);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle file, byte* buffer, uint bytesToRead, uint* bytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool WriteFile(SafeFileHandle file, byte* buffer, uint bytesToWrite, uint* bytesWritten, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetOverlappedResult(SafeFileHandle file, NativeOverlapped* overlapped, out uint transferred, [MarshalAs(UnmanagedType.Bool)] bool wait);

    [LibraryImport("kernel32.dll", SetLastError = true, EntryPoint = "CreateEventW")]
    public static partial nint CreateEventW(nint attributes, [MarshalAs(UnmanagedType.Bool)] bool manualReset, [MarshalAs(UnmanagedType.Bool)] bool initialState, nint name);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);
}
