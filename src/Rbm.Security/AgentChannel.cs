using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Rbm.Core;
using Rbm.Core.Agent;

namespace Rbm.Security;

/// <summary>Enquadramento: int32 LE (tamanho) + JSON UTF-8. Limite de 16 MiB por mensagem.</summary>
public static class AgentFraming
{
    public const int MaxMessageBytes = 16 * 1024 * 1024;

    public static async Task WriteAsync<T>(Stream s, T message, CancellationToken ct)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(message, AgentJson.Options);
        if (payload.Length > MaxMessageBytes) throw new InvalidOperationException("Mensagem excede o limite.");
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, payload.Length);
        await s.WriteAsync(header, ct).ConfigureAwait(false);
        await s.WriteAsync(payload, ct).ConfigureAwait(false);
        await s.FlushAsync(ct).ConfigureAwait(false);
    }

    public static async Task<T?> ReadAsync<T>(Stream s, CancellationToken ct)
    {
        var header = new byte[4];
        if (!await ReadExactAsync(s, header, ct).ConfigureAwait(false)) return default;
        int len = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (len <= 0 || len > MaxMessageBytes) throw new InvalidDataException("Tamanho de mensagem inválido.");
        var buf = new byte[len];
        if (!await ReadExactAsync(s, buf, ct).ConfigureAwait(false)) return default;
        return JsonSerializer.Deserialize<T>(buf, AgentJson.Options);
    }

    private static async Task<bool> ReadExactAsync(Stream s, byte[] buf, CancellationToken ct)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = await s.ReadAsync(buf.AsMemory(read), ct).ConfigureAwait(false);
            if (n == 0) return false;
            read += n;
        }
        return true;
    }
}

/// <summary>
/// Cliente do agente elevado (lado da UI). Inicia o Rbm.Agent sob demanda (o manifesto do agente exige
/// administrador, então o Windows exibe o UAC), conecta ao pipe e serializa requisições.
/// </summary>
public sealed class AgentClient : IAgentClient, IDisposable
{
    private readonly string _agentPath;
    private readonly SemaphoreSlim _callLock = new(1, 1);
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private NamedPipeClientStream? _pipe;
    private Process? _process;
    private string _token = "";

    public bool IsConnected => _pipe is { IsConnected: true } && _process is { HasExited: false };
    public event EventHandler? ConnectionChanged;
    public string? LastError { get; private set; }

    public AgentClient(string? agentPath = null)
    {
        _agentPath = agentPath ?? Path.Combine(AppContext.BaseDirectory, "RBM.Agent.exe");
    }

    public async Task<bool> EnsureConnectedAsync(CancellationToken ct = default)
    {
        if (IsConnected) return true;
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsConnected) return true;
            Cleanup();
            if (!File.Exists(_agentPath)) { LastError = $"RBM.Agent.exe não encontrado em {_agentPath}."; return false; }

            var pipeName = ChannelSecurity.NewPipeName();
            _token = ChannelSecurity.NewToken();
            var psi = new ProcessStartInfo(_agentPath)
            {
                UseShellExecute = true, // necessário para o prompt de UAC
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = $"--pipe {pipeName} --parent {Environment.ProcessId} --token {_token}",
            };
            try { _process = Process.Start(psi); }
            catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
            {
                LastError = "Elevação cancelada pelo usuário (UAC).";
                return false;
            }
            if (_process is null) { LastError = "Falha ao iniciar o agente."; return false; }

            var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(30));
            while (true)
            {
                try { await pipe.ConnectAsync(500, cts.Token).ConfigureAwait(false); break; }
                catch (TimeoutException)
                {
                    if (_process.HasExited) { LastError = $"O agente encerrou (código {_process.ExitCode})."; pipe.Dispose(); return false; }
                }
                catch (OperationCanceledException) { LastError = "Tempo esgotado aguardando o agente."; pipe.Dispose(); return false; }
            }
            _pipe = pipe;
            var ping = await InvokeCoreAsync(AgentOps.Ping, null, TimeSpan.FromSeconds(10), ct).ConfigureAwait(false);
            if (!ping.Ok) { LastError = ping.Error; Cleanup(); return false; }
            LastError = null;
            ConnectionChanged?.Invoke(this, EventArgs.Empty);
            return true;
        }
        finally { _connectLock.Release(); }
    }

    public async Task<AgentResponse> InvokeAsync(string op, object? payload, TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!AgentOps.All.Contains(op)) return new AgentResponse("", false, $"Operação desconhecida: {op}", null);
        if (!await EnsureConnectedAsync(ct).ConfigureAwait(false))
            return new AgentResponse("", false, LastError ?? "Agente indisponível.", null);
        return await InvokeCoreAsync(op, payload, timeout ?? TimeSpan.FromMinutes(2), ct).ConfigureAwait(false);
    }

    private async Task<AgentResponse> InvokeCoreAsync(string op, object? payload, TimeSpan timeout, CancellationToken ct)
    {
        await _callLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var pipe = _pipe ?? throw new InvalidOperationException("Sem conexão.");
            var id = Guid.NewGuid().ToString("N");
            JsonElement? el = payload is null ? null : JsonSerializer.SerializeToElement(payload, payload.GetType(), AgentJson.Options);
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            await AgentFraming.WriteAsync(pipe, new AgentRequest(id, op, el, _token), cts.Token).ConfigureAwait(false);
            var resp = await AgentFraming.ReadAsync<AgentResponse>(pipe, cts.Token).ConfigureAwait(false);
            if (resp is null) { Cleanup(); ConnectionChanged?.Invoke(this, EventArgs.Empty); return new AgentResponse(id, false, "Conexão com o agente encerrada.", null); }
            if (resp.Id != id)
            {
                // Resposta de outra requisição (canal dessincronizado): descarta a conexão por segurança.
                Cleanup(); ConnectionChanged?.Invoke(this, EventArgs.Empty);
                return new AgentResponse(id, false, "Resposta fora de sequência do agente; a conexão foi reiniciada. Tente novamente.", null);
            }
            return resp;
        }
        catch (OperationCanceledException)
        {
            // Timeout OU cancelamento pelo usuário: o fluxo do pipe fica dessincronizado (a resposta
            // pendente chegaria na próxima chamada). Encerra o agente e a conexão; a próxima operação reinicia.
            try { _process?.Kill(); } catch { }
            Cleanup(); ConnectionChanged?.Invoke(this, EventArgs.Empty);
            if (ct.IsCancellationRequested) throw;
            return new AgentResponse("", false, "Tempo esgotado na operação do agente.", null);
        }
        catch (IOException ex)
        {
            Cleanup(); ConnectionChanged?.Invoke(this, EventArgs.Empty);
            return new AgentResponse("", false, "Falha de comunicação com o agente: " + ex.Message, null);
        }
        finally { _callLock.Release(); }
    }

    public async Task ShutdownAsync()
    {
        if (!IsConnected) { Cleanup(); return; }
        try { await InvokeCoreAsync(AgentOps.Shutdown, null, TimeSpan.FromSeconds(3), CancellationToken.None).ConfigureAwait(false); } catch { }
        Cleanup();
        ConnectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Cleanup()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _process?.Dispose();
        _process = null;
    }

    public void Dispose() => Cleanup();
}
