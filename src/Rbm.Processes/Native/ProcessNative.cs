using System.Runtime.InteropServices;
using System.Security.Principal;
using static Rbm.Processes.Native.NativeMethods;

namespace Rbm.Processes.Native;

/// <summary>Consultas pontuais a um processo (todas toleram falha e devolvem null + erro Win32).</summary>
internal static unsafe class ProcessNative
{
    public static ProcessHandle Open(int pid, uint access, out int error)
    {
        var h = OpenProcess(access, false, pid);
        error = h.IsInvalid ? Marshal.GetLastPInvokeError() : 0;
        return h;
    }

    public static string? GetImagePath(ProcessHandle h)
    {
        char* buf = stackalloc char[1024];
        uint size = 1024;
        if (QueryFullProcessImageNameW(h, 0, buf, ref size)) return new string(buf, 0, (int)size);
        // Caminhos > 1024 (raro): tenta com buffer maior.
        var big = new char[32768];
        size = (uint)big.Length;
        fixed (char* p = big)
            return QueryFullProcessImageNameW(h, 0, p, ref size) ? new string(p, 0, (int)size) : null;
    }

    public static string? GetImagePath(int pid)
    {
        using var h = Open(pid, PROCESS_QUERY_LIMITED_INFORMATION, out _);
        return h.IsInvalid ? null : GetImagePath(h);
    }

    /// <summary>Hora de criação como FILETIME (UTC, 100 ns) ou null.</summary>
    public static long? GetCreateTime(ProcessHandle h) =>
        GetProcessTimes(h, out long c, out _, out _, out _) ? c : null;

    /// <summary>Linha de comando via NtQueryInformationProcess(ProcessCommandLineInformation) — exige só QUERY_LIMITED (Win 8.1+).</summary>
    public static string? GetCommandLine(ProcessHandle h, out int status)
    {
        uint len = 4096;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var buffer = new byte[len];
            fixed (byte* p = buffer)
            {
                status = NtQueryInformationProcess(h, ProcessCommandLineInformation, p, len, out uint ret);
                if (status == 0)
                {
                    var us = (UNICODE_STRING*)p;
                    return us->Buffer == 0 || us->Length == 0 ? "" : new string((char*)us->Buffer, 0, us->Length / 2);
                }
                if (status != STATUS_INFO_LENGTH_MISMATCH && status != STATUS_BUFFER_TOO_SMALL) return null;
                len = Math.Max(ret, len * 2);
            }
        }
        status = STATUS_INFO_LENGTH_MISMATCH;
        return null;
    }

    /// <summary>Usuário (DOMÍNIO\nome) e elevação do token. Exige abrir o token (falha comum sem elevação p/ outros usuários).</summary>
    public static (string? user, bool? elevated, int error) GetTokenInfo(ProcessHandle h)
    {
        if (!OpenProcessToken(h, TOKEN_QUERY, out nint token)) return (null, null, Marshal.GetLastPInvokeError());
        try
        {
            string? user = null; bool? elevated = null;
            byte* buf = stackalloc byte[256];
            if (GetTokenInformation(token, TokenUser, buf, 256, out _))
            {
                nint sidPtr = *(nint*)buf; // TOKEN_USER.User.Sid
                try
                {
                    var sid = new SecurityIdentifier(sidPtr);
                    try { user = sid.Translate(typeof(NTAccount)).Value; } catch { user = sid.Value; }
                }
                catch { }
            }
            int elev;
            if (GetTokenInformation(token, TokenElevation, &elev, 4, out _)) elevated = elev != 0;
            return (user, elevated, 0);
        }
        finally { CloseHandle(token); }
    }

    public static (ulong processMask, ulong systemMask)? GetAffinity(ProcessHandle h) =>
        GetProcessAffinityMask(h, out nuint pm, out nuint sm) ? ((ulong)pm, (ulong)sm) : null;

    /// <summary>Máscara de afinidade do sistema (grupo de processadores atual).</summary>
    public static ulong SystemAffinityMask
    {
        get
        {
            if (GetProcessAffinityMask(GetCurrentProcess(), out _, out nuint sm)) return sm;
            int n = Math.Min(Environment.ProcessorCount, 64);
            return n >= 64 ? ulong.MaxValue : (1UL << n) - 1;
        }
    }

    public static int LogicalProcessorCount
    {
        get
        {
            try
            {
                uint n = GetActiveProcessorCount(ALL_PROCESSOR_GROUPS);
                if (n > 0) return (int)n;
            }
            catch { }
            return Environment.ProcessorCount;
        }
    }
}
