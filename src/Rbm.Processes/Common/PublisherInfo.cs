using System.Collections.Concurrent;
using System.Diagnostics;
using Rbm.Security;

namespace Rbm.Processes;

/// <summary>
/// Metadados de um arquivo executável: versão (FileVersionInfo, não verificado) e assinatura Authenticode
/// (verificada, opcional por ser cara). <see cref="Signature"/> null = assinatura não verificada.
/// </summary>
public sealed record PublisherInfo(
    string Path,
    bool Exists,
    string? CompanyName,
    string? FileDescription,
    string? FileVersion,
    string? ProductName,
    SignatureInfo? Signature)
{
    public bool SignatureVerified => Signature is not null;

    /// <summary>Assinatura válida de um certificado da própria Microsoft (exclui WHQL de terceiros).</summary>
    public bool IsVerifiedMicrosoft => Signature is { Status: SignatureStatus.Valid } s && IsMicrosoftSigner(s.Signer);

    /// <summary>Editor para exibição: signatário verificado ou empresa dos metadados marcada como não verificada.</summary>
    public string PublisherDisplay =>
        Signature is { Status: SignatureStatus.Valid, Signer: { } signer } ? signer
        : !string.IsNullOrWhiteSpace(CompanyName) ? $"{CompanyName} (não verificado)"
        : Exists ? "Desconhecido" : "Arquivo não encontrado";

    /// <summary>
    /// Signatários da Microsoft. "Microsoft Windows Hardware Compatibility Publisher" (WHQL) assina drivers de
    /// TERCEIROS, então não conta como componente da Microsoft.
    /// </summary>
    public static bool IsMicrosoftSigner(string? signer) =>
        !string.IsNullOrWhiteSpace(signer)
        && (signer.Equals("Microsoft", StringComparison.OrdinalIgnoreCase) || signer.StartsWith("Microsoft ", StringComparison.OrdinalIgnoreCase))
        && !signer.Contains("Hardware Compatibility", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Cache de metadados por caminho (invalidado pela data de modificação). A verificação de assinatura usa
/// <see cref="SignatureVerifier"/> (que também mantém cache) e roda fora da thread chamadora nas variantes assíncronas.
/// </summary>
public static class FileMetadataCache
{
    private sealed record Entry(DateTime Stamp, PublisherInfo Info);
    private static readonly ConcurrentDictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

    public static PublisherInfo Get(string path, bool verifySignature)
    {
        DateTime stamp;
        bool exists;
        try { exists = File.Exists(path); stamp = exists ? File.GetLastWriteTimeUtc(path) : DateTime.MinValue; }
        catch { exists = false; stamp = DateTime.MinValue; }
        if (!exists) return new PublisherInfo(path, false, null, null, null, null, verifySignature ? new SignatureInfo(SignatureStatus.FileNotFound, null, false, null) : null);

        if (Cache.TryGetValue(path, out var e) && e.Stamp == stamp && (!verifySignature || e.Info.Signature is not null))
            return e.Info;

        PublisherInfo info = e is not null && e.Stamp == stamp ? e.Info : ReadVersionInfo(path);
        if (verifySignature && info.Signature is null)
            info = info with { Signature = SignatureVerifier.Verify(path) };
        Cache[path] = new Entry(stamp, info);
        return info;
    }

    public static Task<PublisherInfo> GetAsync(string path, bool verifySignature, CancellationToken ct = default) =>
        Task.Run(() => Get(path, verifySignature), ct);

    /// <summary>Lê vários arquivos em paralelo limitado (baixo impacto: padrão 2 threads).</summary>
    public static async Task<IReadOnlyDictionary<string, PublisherInfo>> GetManyAsync(
        IEnumerable<string> paths, bool verifySignatures, int maxParallel = 2, CancellationToken ct = default)
    {
        var distinct = paths.Where(p => !string.IsNullOrWhiteSpace(p)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var result = new ConcurrentDictionary<string, PublisherInfo>(StringComparer.OrdinalIgnoreCase);
        await Parallel.ForEachAsync(distinct, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallel), CancellationToken = ct },
            (p, _) => { result[p] = Get(p, verifySignatures); return ValueTask.CompletedTask; }).ConfigureAwait(false);
        return result;
    }

    private static PublisherInfo ReadVersionInfo(string path)
    {
        try
        {
            var v = FileVersionInfo.GetVersionInfo(path);
            return new PublisherInfo(path, true, Clean(v.CompanyName), Clean(v.FileDescription), Clean(v.FileVersion), Clean(v.ProductName), null);
        }
        catch { return new PublisherInfo(path, true, null, null, null, null, null); }
    }

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}
