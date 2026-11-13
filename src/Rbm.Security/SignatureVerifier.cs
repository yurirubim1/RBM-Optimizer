using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;

namespace Rbm.Security;

public enum SignatureStatus { Valid, Unsigned, Invalid, Untrusted, Expired, Revoked, FileNotFound, Error }

public sealed record SignatureInfo(SignatureStatus Status, string? Signer, bool CatalogSigned, string? Detail)
{
    public bool IsTrusted => Status == SignatureStatus.Valid;
    public string Display => Status switch
    {
        SignatureStatus.Valid => $"Assinado: {Signer ?? "?"}{(CatalogSigned ? " (catálogo)" : "")}",
        SignatureStatus.Unsigned => "Não assinado",
        SignatureStatus.FileNotFound => "Arquivo não encontrado",
        _ => $"Assinatura inválida ({Status})",
    };
}

/// <summary>
/// Verificação Authenticode via WinVerifyTrust. Tenta assinatura embutida e, se ausente,
/// assinatura por catálogo (arquivos do sistema). Não faz verificação online de revogação
/// (WTD_REVOKE_NONE) para funcionar offline e não vazar dados — limitação documentada.
/// </summary>
public static class SignatureVerifier
{
    private static readonly Guid WintrustActionGenericVerifyV2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    private static readonly Dictionary<string, (DateTime stamp, SignatureInfo info)> Cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object CacheLock = new();

    public static SignatureInfo Verify(string path)
    {
        try
        {
            if (!File.Exists(path)) return new(SignatureStatus.FileNotFound, null, false, null);
            var stamp = File.GetLastWriteTimeUtc(path);
            lock (CacheLock)
                if (Cache.TryGetValue(path, out var c) && c.stamp == stamp) return c.info;

            var info = VerifyEmbedded(path);
            if (info.Status == SignatureStatus.Unsigned)
            {
                var cat = VerifyCatalog(path);
                if (cat is not null) info = cat;
            }
            lock (CacheLock) Cache[path] = (stamp, info);
            return info;
        }
        catch (Exception ex)
        {
            return new(SignatureStatus.Error, null, false, ex.Message);
        }
    }

    private static SignatureInfo VerifyEmbedded(string path)
    {
        var fileInfo = new WINTRUST_FILE_INFO { cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(), pcwszFilePath = path };
        IntPtr pFile = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_FILE_INFO>());
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = 2, // WTD_UI_NONE
                fdwRevocationChecks = 0, // WTD_REVOKE_NONE
                dwUnionChoice = 1, // WTD_CHOICE_FILE
                pUnion = pFile,
                dwStateAction = 0,
                dwProvFlags = 0x00001000 | 0x00000080, // WTD_CACHE_ONLY_URL_RETRIEVAL | WTD_REVOCATION_CHECK_NONE
            };
            int hr = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
            var status = MapHr(hr);
            string? signer = status is SignatureStatus.Unsigned ? null : TryGetSigner(path);
            return new(status, signer, false, hr == 0 ? null : $"0x{hr:X8}");
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    private static SignatureInfo? VerifyCatalog(string path)
    {
        IntPtr hCatAdmin = IntPtr.Zero;
        if (!CryptCATAdminAcquireContext2(out hCatAdmin, IntPtr.Zero, "SHA256", IntPtr.Zero, 0)) return null;
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            uint hashSize = 0;
            if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, fs.SafeFileHandle.DangerousGetHandle(), ref hashSize, null, 0) || hashSize == 0) return null;
            var hash = new byte[hashSize];
            if (!CryptCATAdminCalcHashFromFileHandle2(hCatAdmin, fs.SafeFileHandle.DangerousGetHandle(), ref hashSize, hash, 0)) return null;

            IntPtr hCatInfo = CryptCATAdminEnumCatalogFromHash(hCatAdmin, hash, hashSize, 0, IntPtr.Zero);
            if (hCatInfo == IntPtr.Zero) return null;
            try
            {
                var ci = new CATALOG_INFO { cbStruct = (uint)Marshal.SizeOf<CATALOG_INFO>() };
                if (!CryptCATCatalogInfoFromContext(hCatInfo, ref ci, 0)) return null;
                string memberTag = Convert.ToHexString(hash);

                var catInfo = new WINTRUST_CATALOG_INFO
                {
                    cbStruct = (uint)Marshal.SizeOf<WINTRUST_CATALOG_INFO>(),
                    pcwszCatalogFilePath = ci.wszCatalogFile,
                    pcwszMemberTag = memberTag,
                    pcwszMemberFilePath = path,
                    pbCalculatedFileHash = Marshal.AllocHGlobal(hash.Length),
                    cbCalculatedFileHash = (uint)hash.Length,
                    hCatAdmin = hCatAdmin,
                };
                Marshal.Copy(hash, 0, catInfo.pbCalculatedFileHash, hash.Length);
                IntPtr pCat = Marshal.AllocHGlobal(Marshal.SizeOf<WINTRUST_CATALOG_INFO>());
                try
                {
                    Marshal.StructureToPtr(catInfo, pCat, false);
                    var data = new WINTRUST_DATA
                    {
                        cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                        dwUIChoice = 2,
                        fdwRevocationChecks = 0,
                        dwUnionChoice = 2, // WTD_CHOICE_CATALOG
                        pUnion = pCat,
                        dwProvFlags = 0x00000080,
                    };
                    int hr = WinVerifyTrust(IntPtr.Zero, WintrustActionGenericVerifyV2, ref data);
                    string? signer = null;
                    try { signer = new X509Certificate2(X509Certificate.CreateFromSignedFile(ci.wszCatalogFile)).GetNameInfo(X509NameType.SimpleName, false); } catch { }
                    return new(MapHr(hr), signer, true, hr == 0 ? null : $"0x{hr:X8}");
                }
                finally
                {
                    Marshal.FreeHGlobal(catInfo.pbCalculatedFileHash);
                    Marshal.FreeHGlobal(pCat);
                }
            }
            finally { CryptCATAdminReleaseCatalogContext(hCatAdmin, hCatInfo, 0); }
        }
        catch { return null; }
        finally { CryptCATAdminReleaseContext(hCatAdmin, 0); }
    }

    private static string? TryGetSigner(string path)
    {
        try
        {
#pragma warning disable SYSLIB0057
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
#pragma warning restore SYSLIB0057
            return cert.GetNameInfo(X509NameType.SimpleName, false);
        }
        catch { return null; }
    }

    private static SignatureStatus MapHr(int hr) => unchecked((uint)hr) switch
    {
        0 => SignatureStatus.Valid,
        0x800B0100 => SignatureStatus.Unsigned,          // TRUST_E_NOSIGNATURE
        0x800B0003 => SignatureStatus.Unsigned,          // TRUST_E_SUBJECT_FORM_UNKNOWN
        0x800B0101 => SignatureStatus.Expired,           // CERT_E_EXPIRED
        0x800B010C => SignatureStatus.Revoked,           // CERT_E_REVOKED
        0x800B0109 => SignatureStatus.Untrusted,         // CERT_E_UNTRUSTEDROOT
        0x800B0111 => SignatureStatus.Untrusted,         // TRUST_E_EXPLICIT_DISTRUST
        0x80096010 => SignatureStatus.Invalid,           // TRUST_E_BAD_DIGEST
        _ => SignatureStatus.Invalid,
    };

    public static string Sha256(string path)
    {
        using var fs = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(fs));
    }

    #region Interop
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WINTRUST_CATALOG_INFO
    {
        public uint cbStruct;
        public uint dwCatalogVersion;
        public string pcwszCatalogFilePath;
        public string pcwszMemberTag;
        public string pcwszMemberFilePath;
        public IntPtr hMemberFile;
        public IntPtr pbCalculatedFileHash;
        public uint cbCalculatedFileHash;
        public IntPtr pcCatalogContext;
        public IntPtr hCatAdmin;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pUnion;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CATALOG_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string wszCatalogFile;
    }

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode)]
    private static extern int WinVerifyTrust(IntPtr hwnd, [MarshalAs(UnmanagedType.LPStruct)] Guid pgActionID, ref WINTRUST_DATA pWVTData);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATAdminAcquireContext2(out IntPtr phCatAdmin, IntPtr pgSubsystem, string pwszHashAlgorithm, IntPtr pStrongHashPolicy, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern bool CryptCATAdminCalcHashFromFileHandle2(IntPtr hCatAdmin, IntPtr hFile, ref uint pcbHash, byte[]? pbHash, uint dwFlags);

    [DllImport("wintrust.dll", SetLastError = true)]
    private static extern IntPtr CryptCATAdminEnumCatalogFromHash(IntPtr hCatAdmin, byte[] pbHash, uint cbHash, uint dwFlags, IntPtr phPrevCatInfo);

    [DllImport("wintrust.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptCATCatalogInfoFromContext(IntPtr hCatInfo, ref CATALOG_INFO psCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseCatalogContext(IntPtr hCatAdmin, IntPtr hCatInfo, uint dwFlags);

    [DllImport("wintrust.dll")]
    private static extern bool CryptCATAdminReleaseContext(IntPtr hCatAdmin, uint dwFlags);
    #endregion
}
