using System.Runtime.InteropServices;

namespace Rbm.Optimization.SpaceAnalysis;

/// <summary>P/Invoke mínimo para enumeração rápida (FindFirstFileExW + FIND_FIRST_EX_LARGE_FETCH).</summary>
internal static unsafe class NativeFind
{
    public const uint AttrDirectory = 0x10;
    public const uint AttrReparsePoint = 0x400;
    public const uint AttrSparse = 0x200;
    public const uint AttrCompressed = 0x800;

    public const int ErrorFileNotFound = 2;
    public const int ErrorPathNotFound = 3;
    public const int ErrorAccessDenied = 5;
    public const int ErrorNoMoreFiles = 18;

    private const int FindExInfoBasic = 1;
    private const int FindExSearchNameMatch = 0;
    private const int FindFirstExLargeFetch = 2;
    private static readonly IntPtr Invalid = new(-1);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    public struct Win32FindData
    {
        public uint dwFileAttributes;
        // FILETIME = 2×DWORD (alinhamento de 4 bytes; não usar long aqui).
        public uint ftCreationLow, ftCreationHigh;
        public uint ftLastAccessLow, ftLastAccessHigh;
        public uint ftLastWriteLow, ftLastWriteHigh;
        public uint nFileSizeHigh;
        public uint nFileSizeLow;
        /// <summary>Quando FILE_ATTRIBUTE_REPARSE_POINT está presente, contém a etiqueta (reparse tag).</summary>
        public uint dwReserved0;
        public uint dwReserved1;
        public fixed char cFileName[260];
        public fixed char cAlternateFileName[14];
        // Campos obsoletos (dwFileType/dwCreatorType/wFinderFlags) não existem no Windows.

        public readonly long Length => ((long)nFileSizeHigh << 32) | nFileSizeLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern IntPtr FindFirstFileExW(string lpFileName, int fInfoLevelId, out Win32FindData lpFindFileData, int fSearchOp, IntPtr lpSearchFilter, int dwAdditionalFlags);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindNextFileW(IntPtr hFindFile, out Win32FindData lpFindFileData);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool FindClose(IntPtr hFindFile);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    private static extern uint GetCompressedFileSizeW(string lpFileName, out uint lpFileSizeHigh);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceW(string lpRootPathName, out uint sectorsPerCluster, out uint bytesPerSector, out uint freeClusters, out uint totalClusters);

    /// <summary>Abre a enumeração de "dir\*". Retorna IntPtr.Zero e o código de erro Win32 em caso de falha.</summary>
    public static IntPtr Open(string dirLongPath, out Win32FindData data, out int error)
    {
        var pattern = dirLongPath.EndsWith('\\') ? dirLongPath + "*" : dirLongPath + "\\*";
        var h = FindFirstFileExW(pattern, FindExInfoBasic, out data, FindExSearchNameMatch, IntPtr.Zero, FindFirstExLargeFetch);
        if (h == Invalid || h == IntPtr.Zero) { error = Marshal.GetLastWin32Error(); return IntPtr.Zero; }
        error = 0;
        return h;
    }

    public static bool IsDots(ref Win32FindData d)
    {
        fixed (char* n = d.cFileName)
            return n[0] == '.' && (n[1] == '\0' || (n[1] == '.' && n[2] == '\0'));
    }

    public static string Name(ref Win32FindData d)
    {
        fixed (char* n = d.cFileName) return new string(n);
    }

    /// <summary>
    /// Etiquetas "name surrogate" (bit 29) apontam para outra entidade nomeada: junções, links simbólicos, links WSL etc.
    /// Esses nunca são seguidos. Etiquetas de nuvem (OneDrive), dedup e WOF NÃO são substitutas e são contadas normalmente.
    /// </summary>
    public static bool IsNameSurrogate(uint tag) => (tag & 0x20000000) != 0;

    /// <summary>Tamanho alocado real (compactado/esparso/placeholder de nuvem). -1 em caso de erro.</summary>
    public static long CompressedSize(string longPath)
    {
        uint lo = GetCompressedFileSizeW(longPath, out uint hi);
        if (lo == 0xFFFFFFFF && Marshal.GetLastWin32Error() != 0) return -1;
        return ((long)hi << 32) | lo;
    }

    public static long ClusterSize(string root)
    {
        try
        {
            var vol = Path.GetPathRoot(root);
            if (!string.IsNullOrEmpty(vol) && GetDiskFreeSpaceW(vol.EndsWith('\\') ? vol : vol + "\\", out var spc, out var bps, out _, out _))
                return Math.Max(512L, (long)spc * bps);
        }
        catch { }
        return 4096;
    }

    /// <summary>Converte "C:\x" → "\\?\C:\x" e "\\srv\share" → "\\?\UNC\srv\share" para suportar caminhos longos.</summary>
    public static string ToLongPath(string fullPath)
    {
        if (fullPath.StartsWith(@"\\?\", StringComparison.Ordinal)) return fullPath;
        if (fullPath.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + fullPath[2..];
        return @"\\?\" + fullPath;
    }

    // ---- SeBackupPrivilege por thread (somente no agente elevado) ----
    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ImpersonateSelf(int impersonationLevel);

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RevertToSelf();

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenThreadToken(IntPtr thread, uint access, [MarshalAs(UnmanagedType.Bool)] bool openAsSelf, out IntPtr token);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr h);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool LookupPrivilegeValueW(string? system, string name, out long luid);

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct TokenPrivileges { public uint Count; public long Luid; public uint Attributes; }

    [DllImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AdjustTokenPrivileges(IntPtr token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges newState, uint len, IntPtr prev, IntPtr retLen);

    /// <summary>
    /// Habilita SeBackupPrivilege apenas na thread atual (via token de personificação próprio), permitindo listar pastas
    /// protegidas por ACL (somente leitura). Retorna true se a thread deve chamar <see cref="EndBackupIntent"/> ao final.
    /// Sem efeito em processos sem o privilégio (UI sem elevação).
    /// </summary>
    public static bool BeginBackupIntent()
    {
        try
        {
            if (!ImpersonateSelf(2 /* SecurityImpersonation */)) return false;
            if (OpenThreadToken(GetCurrentThread(), 0x0020 | 0x0008 /* ADJUST_PRIVILEGES | QUERY */, false, out var tok))
            {
                try
                {
                    if (LookupPrivilegeValueW(null, "SeBackupPrivilege", out var luid))
                    {
                        var tp = new TokenPrivileges { Count = 1, Luid = luid, Attributes = 0x2 /* ENABLED */ };
                        AdjustTokenPrivileges(tok, false, ref tp, 0, IntPtr.Zero, IntPtr.Zero);
                    }
                }
                finally { CloseHandle(tok); }
            }
            return true;
        }
        catch { return false; }
    }

    public static void EndBackupIntent() { try { RevertToSelf(); } catch { } }
}
