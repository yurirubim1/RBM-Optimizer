using System.Collections.Concurrent;
using Rbm.Core;
using Rbm.Processes.Native;
using Rbm.Security;
using static Rbm.Processes.Native.NativeMethods;

namespace Rbm.Processes;

/// <summary>Detalhes de um processo obtidos sob demanda. Campos null = indisponível (motivo em <see cref="Unavailable"/>).</summary>
public sealed record ProcessDetails(
    int Pid,
    long CreateTime,
    string Name,
    string? ImagePath,
    string? CompanyName,
    string? FileDescription,
    string? FileVersion,
    string? ProductName,
    string? CommandLine,
    string? UserName,
    bool? IsElevated,
    ProcessPriority? Priority,
    ulong? AffinityMask,
    ulong? SystemAffinityMask,
    IReadOnlyList<string> Unavailable)
{
    public ProcessKey Key => new(Pid, CreateTime);
}

public sealed record ProcessDetailsOptions
{
    /// <summary>Lê a linha de comando (pode falhar sem elevação para processos protegidos).</summary>
    public bool IncludeCommandLine { get; init; } = true;
    /// <summary>Lê usuário e elevação do token (falha comum para processos de outros usuários sem elevação).</summary>
    public bool IncludeUser { get; init; } = true;
}

/// <summary>
/// Detalhes sob demanda com cache por PID + hora de criação (partes estáticas: caminho, versão, linha de comando,
/// usuário). Prioridade e afinidade são relidas a cada chamada (mudam em tempo de execução). A assinatura é
/// separada (<see cref="GetSignatureAsync"/>) por ser cara; o cache é por caminho.
/// </summary>
public sealed class ProcessDetailsProvider
{
    private sealed record StaticPart(string Name, string? ImagePath, string? Company, string? Description, string? Version, string? Product,
        string? CommandLine, string? User, bool? Elevated, List<string> Notes);

    private readonly ConcurrentDictionary<ProcessKey, StaticPart> _cache = new();
    private const int MaxCacheEntries = 4096;

    /// <summary>Lê os detalhes. <paramref name="createTime"/> (FILETIME) evita confundir com outro processo que reutilizou o PID.</summary>
    public OperationResult<ProcessDetails> GetDetails(int pid, long createTime, ProcessDetailsOptions? options = null)
    {
        options ??= new ProcessDetailsOptions();
        if (pid is 0 or 4)
            return OperationResult<ProcessDetails>.Ok(new ProcessDetails(pid, createTime, pid == 0 ? "Idle" : "System", null, null, null, null, null,
                null, "NT AUTHORITY\\SYSTEM", null, null, null, null, ["Pseudo-processo do kernel: sem imagem, linha de comando ou prioridade ajustável."]));

        using var h = ProcessNative.Open(pid, PROCESS_QUERY_LIMITED_INFORMATION, out int openErr);
        if (h.IsInvalid)
        {
            if (openErr == Win32Messages.ERROR_INVALID_PARAMETER)
                return OperationResult<ProcessDetails>.Fail("O processo não existe mais.", ProcessErrorCodes.NotFound);
            // Sem handle: devolve o que a enumeração oferece.
            var raw = SystemProcessEnumerator.Find(pid);
            if (raw is null || raw.Value.CreateTime != createTime)
                return OperationResult<ProcessDetails>.Fail("O processo não existe mais.", ProcessErrorCodes.NotFound);
            return OperationResult<ProcessDetails>.Ok(new ProcessDetails(pid, createTime, raw.Value.Name, null, null, null, null, null, null, null, null, null, null, null,
                [$"Não foi possível abrir o processo ({Win32Messages.Describe(openErr)}). Detalhes indisponíveis sem elevação."]));
        }

        long? actualCreate = ProcessNative.GetCreateTime(h);
        if (actualCreate is { } ac && createTime != 0 && ac != createTime)
            return OperationResult<ProcessDetails>.Fail("O PID foi reutilizado por outro processo; atualize a lista.", ProcessErrorCodes.PidReused);

        var key = new ProcessKey(pid, createTime);
        if (!_cache.TryGetValue(key, out var st) || (options.IncludeCommandLine && st.CommandLine is null && !st.Notes.Any(n => n.StartsWith("Linha de comando")))
            || (options.IncludeUser && st.User is null && !st.Notes.Any(n => n.StartsWith("Usuário"))))
        {
            st = ReadStatic(h, pid, options);
            if (_cache.Count > MaxCacheEntries) _cache.Clear();
            _cache[key] = st;
        }

        var notes = new List<string>(st.Notes);
        ProcessPriority? prio = null;
        uint pc = GetPriorityClass(h);
        if (pc != 0) prio = ProcessPriorityMap.FromWin32(pc);
        else notes.Add("Prioridade indisponível.");
        ulong? aff = null, sysAff = null;
        if (ProcessNative.GetAffinity(h) is { } a) { aff = a.processMask; sysAff = a.systemMask; }
        else notes.Add("Afinidade indisponível.");

        return OperationResult<ProcessDetails>.Ok(new ProcessDetails(pid, createTime, st.Name, st.ImagePath, st.Company, st.Description, st.Version, st.Product,
            st.CommandLine, st.User, st.Elevated, prio, aff, sysAff, notes));
    }

    /// <summary>Verifica a assinatura do executável (cara: rode fora da UI). Cache por caminho.</summary>
    public static Task<SignatureInfo> GetSignatureAsync(string imagePath, CancellationToken ct = default) =>
        Task.Run(() => FileMetadataCache.Get(imagePath, verifySignature: true).Signature ?? new SignatureInfo(SignatureStatus.Error, null, false, null), ct);

    /// <summary>Descarta do cache processos que já terminaram.</summary>
    public void Prune(IEnumerable<ProcessKey> alive)
    {
        var set = alive as ISet<ProcessKey> ?? alive.ToHashSet();
        foreach (var k in _cache.Keys) if (!set.Contains(k)) _cache.TryRemove(k, out _);
    }

    private static StaticPart ReadStatic(ProcessHandle h, int pid, ProcessDetailsOptions options)
    {
        var notes = new List<string>();
        string? path = ProcessNative.GetImagePath(h);
        string name = path is not null ? Path.GetFileName(path) : SystemProcessEnumerator.Find(pid)?.Name ?? $"PID {pid}";
        string? company = null, desc = null, ver = null, product = null;
        if (path is null) notes.Add("Caminho da imagem indisponível.");
        else
        {
            var meta = FileMetadataCache.Get(path, verifySignature: false);
            company = meta.CompanyName; desc = meta.FileDescription; ver = meta.FileVersion; product = meta.ProductName;
            if (!meta.Exists) notes.Add("Arquivo do executável não encontrado no disco.");
        }

        string? cmd = null;
        if (options.IncludeCommandLine)
        {
            cmd = ProcessNative.GetCommandLine(h, out int status);
            if (cmd is null) notes.Add($"Linha de comando indisponível sem elevação (NTSTATUS 0x{status:X8}).");
        }

        string? user = null; bool? elevated = null;
        if (options.IncludeUser)
        {
            var (u, e, err) = ProcessNative.GetTokenInfo(h);
            user = u; elevated = e;
            if (u is null) notes.Add($"Usuário indisponível ({(err != 0 ? Win32Messages.Describe(err) : "falha ao ler o token")}).");
        }
        return new StaticPart(name, path, company, desc, ver, product, cmd, user, elevated, notes);
    }
}
