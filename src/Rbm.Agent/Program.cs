using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Rbm.Core;
using Rbm.Core.Agent;
using Rbm.Security;
using Serilog;

namespace Rbm.Agent;

/// <summary>
/// RBM Agent — processo auxiliar elevado.
/// • Só é iniciado pela UI (UAC); encerra quando a UI encerra.
/// • Aceita UM cliente: o processo pai (PID verificado via GetNamedPipeClientProcessId)
///   cujo executável esteja no mesmo diretório do agente.
/// • Pipe com ACL restrita ao usuário da UI. Token de 256 bits em toda mensagem.
/// • Só executa operações da allow-list (<see cref="AgentOps"/>), com validação no próprio agente.
/// • Auditoria em %ProgramData%\RbmOptimizer\audit.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string? pipe = Arg(args, "--pipe"), token = Arg(args, "--token");
        int parentPid = int.TryParse(Arg(args, "--parent"), out var pp) ? pp : 0;
        if (pipe is null || token is null || parentPid <= 0 || !pipe.StartsWith("rbm-agent-", StringComparison.Ordinal) || token.Length != 64)
            return 2;

        Directory.CreateDirectory(RbmPaths.AgentAudit);
        Log.Logger = new LoggerConfiguration()
            .WriteTo.File(Path.Combine(RbmPaths.AgentAudit, "agent-.log"), rollingInterval: RollingInterval.Day, retainedFileCountLimit: 60,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}")
            .CreateLogger();

        try
        {
            if (!ChannelSecurity.IsElevated()) { Log.Error("Agente iniciado sem elevação."); return 3; }
            var parent = Process.GetProcessById(parentPid);
            var parentPath = ChannelSecurity.GetProcessImagePath(parentPid);
            var agentDir = Path.GetFullPath(AppContext.BaseDirectory).TrimEnd('\\');
            if (parentPath is null || !string.Equals(Path.GetDirectoryName(parentPath)?.TrimEnd('\\'), agentDir, StringComparison.OrdinalIgnoreCase))
            {
                Log.Error("Processo pai {Pid} ({Path}) fora do diretório do agente {Dir}. Recusado.", parentPid, parentPath, agentDir);
                return 4;
            }
            var userSid = GetProcessUserSid(parentPid) ?? WindowsIdentity.GetCurrent().User!;
            Log.Information("Agente iniciado. Pai={Pid} {Path} Usuário={Sid}", parentPid, parentPath, userSid);

            using var cts = new CancellationTokenSource();
            // Encerra quando a UI encerrar.
            _ = Task.Run(async () => { try { await parent.WaitForExitAsync(cts.Token); } catch { } cts.Cancel(); });
            RunServer(pipe, token, parentPid, userSid, cts).GetAwaiter().GetResult();
            Log.Information("Agente encerrado normalmente.");
            return 0;
        }
        catch (Exception ex)
        {
            Log.Fatal(ex, "Falha fatal no agente");
            return 1;
        }
        finally { Log.CloseAndFlush(); }
    }

    private static async Task RunServer(string pipeName, string token, int parentPid, SecurityIdentifier userSid, CancellationTokenSource cts)
    {
        var security = ChannelSecurity.CreatePipeSecurity(userSid);
        using var server = NamedPipeServerStreamAcl.Create(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.WriteThrough, 0, 0, security);

        using (var connectTimeout = CancellationTokenSource.CreateLinkedTokenSource(cts.Token))
        {
            connectTimeout.CancelAfter(TimeSpan.FromSeconds(60));
            await server.WaitForConnectionAsync(connectTimeout.Token);
        }
        int clientPid = ChannelSecurity.GetClientProcessId(server);
        if (clientPid != parentPid)
        {
            Log.Error("Cliente PID {Client} ≠ pai {Parent}. Conexão recusada.", clientPid, parentPid);
            return;
        }

        var dispatcher = new Dispatcher(cts);
        while (!cts.IsCancellationRequested && server.IsConnected)
        {
            AgentRequest? req;
            try { req = await AgentFraming.ReadAsync<AgentRequest>(server, cts.Token); }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { Log.Warning(ex, "Mensagem inválida"); break; }
            if (req is null) break;

            AgentResponse resp;
            if (!ChannelSecurity.TokenEquals(req.Token, token))
            {
                Log.Warning("Token inválido. Encerrando.");
                break;
            }
            if (!AgentOps.All.Contains(req.Op))
            {
                Log.Warning("Operação fora da allow-list: {Op}", req.Op);
                resp = new AgentResponse(req.Id, false, "Operação não permitida.", null);
            }
            else
            {
                var sw = Stopwatch.StartNew();
                resp = await dispatcher.HandleAsync(req, cts.Token);
                Log.Information("AUDIT op={Op} ok={Ok} ms={Ms} err={Err} payload={Payload}", req.Op, resp.Ok, sw.ElapsedMilliseconds, resp.Error, Truncate(req.Payload?.GetRawText(), 2000));
            }
            try { await AgentFraming.WriteAsync(server, resp, cts.Token); }
            catch { break; }
        }
    }

    private static string? Truncate(string? s, int max) => s is null ? null : s.Length <= max ? s : s[..max] + "…";

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static SecurityIdentifier? GetProcessUserSid(int pid)
    {
        IntPtr hProc = OpenProcess(0x1000, false, (uint)pid);
        if (hProc == IntPtr.Zero) return null;
        try
        {
            if (!OpenProcessToken(hProc, 0x0008 /* TOKEN_QUERY */, out var hTok)) return null;
            try { using var id = new WindowsIdentity(hTok); return id.User; }
            finally { CloseHandle(hTok); }
        }
        finally { CloseHandle(hProc); }
    }

    [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
    [DllImport("advapi32.dll", SetLastError = true)] private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr h);
}
