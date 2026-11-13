using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;

namespace Rbm.Security;

/// <summary>
/// Segurança do canal UI ↔ agente:
/// 1) Pipe com ACL que permite apenas o SID do usuário interativo (e SYSTEM);
/// 2) O agente confere o PID do cliente (GetNamedPipeClientProcessId) = PID do processo que o iniciou;
/// 3) O executável do cliente deve estar no mesmo diretório do agente;
/// 4) Token aleatório de 256 bits em todas as mensagens (comparação em tempo constante).
/// </summary>
public static class ChannelSecurity
{
    public static string NewToken() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));

    public static string NewPipeName() => "rbm-agent-" + Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant();

    public static bool TokenEquals(string a, string b) =>
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a ?? ""), Encoding.UTF8.GetBytes(b ?? ""));

    public static PipeSecurity CreatePipeSecurity(SecurityIdentifier allowedUser)
    {
        var ps = new PipeSecurity();
        ps.SetAccessRuleProtection(true, false);
        ps.AddAccessRule(new PipeAccessRule(allowedUser, PipeAccessRights.ReadWrite | PipeAccessRights.Synchronize, AccessControlType.Allow));
        ps.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        // O próprio dono (processo elevado) precisa de controle total para criar instâncias.
        var self = WindowsIdentity.GetCurrent().User;
        if (self is not null)
            ps.AddAccessRule(new PipeAccessRule(self, PipeAccessRights.FullControl, AccessControlType.Allow));
        return ps;
    }

    public static int GetClientProcessId(NamedPipeServerStream server)
    {
        if (!GetNamedPipeClientProcessId(server.SafePipeHandle.DangerousGetHandle(), out uint pid))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        return (int)pid;
    }

    /// <summary>Caminho completo do executável de um processo (funciona entre níveis de integridade).</summary>
    public static string? GetProcessImagePath(int pid)
    {
        IntPtr h = OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
        if (h == IntPtr.Zero) return null;
        try
        {
            var sb = new StringBuilder(1024);
            uint size = (uint)sb.Capacity;
            return QueryFullProcessImageName(h, 0, sb, ref size) ? sb.ToString() : null;
        }
        finally { CloseHandle(h); }
    }

    public static bool IsElevated()
    {
        using var id = WindowsIdentity.GetCurrent();
        return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetNamedPipeClientProcessId(IntPtr pipe, out uint clientProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool QueryFullProcessImageName(IntPtr hProcess, int flags, StringBuilder exeName, ref uint size);

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr h);
}
