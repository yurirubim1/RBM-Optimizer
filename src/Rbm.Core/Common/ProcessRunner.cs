using System.Diagnostics;
using System.Text;

namespace Rbm.Core;

public sealed record ProcessRunResult(int ExitCode, string StdOut, string StdErr, bool TimedOut)
{
    public bool Success => !TimedOut && ExitCode == 0;
}

/// <summary>
/// Executa ferramentas do sistema (caminho absoluto em System32) com argumentos em lista
/// (sem shell, sem interpolação de linha de comando), timeout e captura de saída.
/// </summary>
public static class ProcessRunner
{
    public static string System32(string exe) => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), exe);

    public static async Task<ProcessRunResult> RunAsync(string fileName, IEnumerable<string> args, TimeSpan timeout, CancellationToken ct = default, Encoding? encoding = null)
    {
        var psi = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = encoding ?? Encoding.UTF8,
            StandardErrorEncoding = encoding ?? Encoding.UTF8,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var p = new Process { StartInfo = psi };
        var so = new StringBuilder(); var se = new StringBuilder();
        p.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (so) so.AppendLine(e.Data); };
        p.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (se) se.AppendLine(e.Data); };
        p.Start();
        p.BeginOutputReadLine(); p.BeginErrorReadLine();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout);
        try
        {
            await p.WaitForExitAsync(cts.Token).ConfigureAwait(false);
            p.WaitForExit(); // garante flush dos handlers assíncronos
            return new ProcessRunResult(p.ExitCode, so.ToString(), se.ToString(), false);
        }
        catch (OperationCanceledException)
        {
            try { p.Kill(entireProcessTree: true); } catch { }
            if (ct.IsCancellationRequested) throw;
            return new ProcessRunResult(-1, so.ToString(), se.ToString(), true);
        }
    }

    public static ProcessRunResult Run(string fileName, IEnumerable<string> args, TimeSpan timeout, Encoding? encoding = null) =>
        RunAsync(fileName, args, timeout, CancellationToken.None, encoding).GetAwaiter().GetResult();

    /// <summary>Codificação OEM do console (ferramentas como bcdedit/schtasks escrevem nela).</summary>
    public static Encoding OemEncoding
    {
        get
        {
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(System.Globalization.CultureInfo.CurrentCulture.TextInfo.OEMCodePage);
            }
            catch { return Encoding.UTF8; }
        }
    }
}
