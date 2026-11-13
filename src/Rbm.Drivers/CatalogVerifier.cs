using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Rbm.Security;
using static Rbm.Drivers.Native.NativeMethods;

namespace Rbm.Drivers;

/// <summary>
/// Resultado da verificação de um pacote (INF + catálogo .cat).
/// <c>InfInCatalog</c>: true/false quando o catálogo pôde ser enumerado; null se indeterminado.
/// </summary>
public sealed record PackageSignatureCheck(
    string InfPath,
    string? CatalogPath,
    SignatureInfo? CatalogSignature,
    bool? InfInCatalog,
    bool IsTrusted,
    string MessagePt);

/// <summary>
/// Verificação offline de pacotes de driver: assinatura Authenticode do catálogo (<see cref="SignatureVerifier"/>, sem
/// revogação online) e presença do hash do INF entre os membros do catálogo (CryptCATEnumerateMember).
/// Somente leitura.
/// </summary>
public static class CatalogVerifier
{
    public static PackageSignatureCheck VerifyPackage(string infPath)
    {
        InfHeader header;
        try { header = InfFile.ReadHeader(infPath); }
        catch (Exception ex) { return Fail(infPath, null, null, null, $"INF ilegível: {ex.Message}"); }

        if (string.IsNullOrWhiteSpace(header.CatalogFile))
            return Fail(infPath, null, null, null, "O INF não declara CatalogFile: pacote sem catálogo não pode ser verificado (instalação bloqueada).");
        if (!DriverStoreRules.IsSafeFileName(header.CatalogFile) || !header.CatalogFile.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
            return Fail(infPath, null, null, null, "Nome de catálogo inválido no INF.");

        string dir = Path.GetDirectoryName(Path.GetFullPath(infPath))!;
        string catPath = Path.Combine(dir, header.CatalogFile);
        if (!File.Exists(catPath))
            return Fail(infPath, catPath, null, null, $"Catálogo \"{header.CatalogFile}\" não encontrado ao lado do INF.");

        var sig = SignatureVerifier.Verify(catPath);
        if (!sig.IsTrusted)
            return Fail(infPath, catPath, sig, null, $"Catálogo sem assinatura confiável: {sig.Display}.");

        bool? member = CatalogContainsFile(catPath, infPath);
        if (member == false)
            return Fail(infPath, catPath, sig, false, "O hash do INF não consta no catálogo (arquivo alterado após a assinatura).");

        string msg = member == true
            ? $"Catálogo assinado e confiável ({sig.Signer ?? "assinante desconhecido"}); INF consta no catálogo."
            : $"Catálogo assinado e confiável ({sig.Signer ?? "assinante desconhecido"}); não foi possível enumerar os membros do catálogo — o Windows revalidará na instalação.";
        return new PackageSignatureCheck(infPath, catPath, sig, member, true, msg);
    }

    private static PackageSignatureCheck Fail(string inf, string? cat, SignatureInfo? sig, bool? member, string msg) =>
        new(inf, cat, sig, member, false, msg);

    /// <summary>
    /// Verifica se o hash (SHA-1 ou SHA-256, arquivo plano) do arquivo é membro do catálogo.
    /// Retorna null quando o catálogo não pôde ser aberto/enumerado.
    /// </summary>
    public static bool? CatalogContainsFile(string catalogPath, string filePath)
    {
        var tags = ReadMemberTags(catalogPath);
        if (tags is null || tags.Count == 0) return null;
        byte[] bytes;
        try { bytes = File.ReadAllBytes(filePath); } catch { return null; }
        string sha1 = Convert.ToHexString(SHA1.HashData(bytes));
        string sha256 = Convert.ToHexString(SHA256.HashData(bytes));
        return tags.Contains(sha1) || tags.Contains(sha256);
    }

    /// <summary>Tags (hash hex) dos membros do catálogo; null se não puder abrir.</summary>
    public static HashSet<string>? ReadMemberTags(string catalogPath)
    {
        IntPtr h = CryptCATOpen(catalogPath, CRYPTCAT_OPEN_EXISTING, IntPtr.Zero, 0, 0);
        if (h == IntPtr.Zero || h == InvalidHandle) return null;
        try
        {
            var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr m = IntPtr.Zero;
            int guard = 0;
            while ((m = CryptCATEnumerateMember(h, m)) != IntPtr.Zero && guard++ < 200_000)
            {
                // CRYPTCATMEMBER { DWORD cbStruct; LPWSTR pwszReferenceTag; ... } — em x64 o ponteiro fica no offset 8.
                IntPtr pTag = Marshal.ReadIntPtr(m, IntPtr.Size);
                if (pTag != IntPtr.Zero && Marshal.PtrToStringUni(pTag) is { Length: > 0 } tag) tags.Add(tag);
            }
            return tags;
        }
        catch { return null; }
        finally { CryptCATClose(h); }
    }
}
