using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Rbm.Core;
using Rbm.Security;

namespace Rbm.Gaming.PresentMon;

/// <summary>Executável do PresentMon (console) inspecionado.</summary>
/// <param name="Path">Caminho absoluto.</param>
/// <param name="Version">Versão (da FileVersionInfo ou do nome do arquivo); null se desconhecida.</param>
/// <param name="Signature">Resultado Authenticode.</param>
/// <param name="Sha256">SHA-256 (hex maiúsculo).</param>
/// <param name="IsInToolsDirectory">Está dentro de RbmPaths.Tools.</param>
/// <param name="RecordedSha256">SHA-256 registrado no download (presentmon.json), se houver.</param>
public sealed record PresentMonTool(string Path, Version? Version, SignatureInfo Signature, string Sha256, bool IsInToolsDirectory, string? RecordedSha256)
{
    /// <summary>Assinatura válida da Intel (requisito para executar).</summary>
    public bool IsTrusted => PresentMonLocator.IsTrustedSignature(Signature);

    /// <summary>O arquivo mudou desde o download registrado.</summary>
    public bool HashMismatch => RecordedSha256 is not null && !string.Equals(RecordedSha256, Sha256, StringComparison.OrdinalIgnoreCase);

    /// <summary>Versão principal (1 ou 2); 2 quando desconhecida (as versões atuais são 2.x).</summary>
    public int MajorVersion => Version?.Major ?? 2;
}

/// <summary>Registro gravado em Tools\presentmon.json após um download verificado.</summary>
public sealed record PresentMonInstallRecord(string FileName, string Version, string Sha256, string? Signer, string SourceUrl, string ReleaseTag, DateTimeOffset InstalledAt);

/// <summary>Localiza e inspeciona o PresentMon (pasta RbmPaths.Tools ou caminho escolhido pelo usuário).</summary>
public static partial class PresentMonLocator
{
    public const string InstallRecordFileName = "presentmon.json";

    [GeneratedRegex(@"PresentMon[-_ ]?v?(\d+(?:\.\d+){1,3})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VersionInName();

    /// <summary>Assinatura válida cujo assinante contém "Intel".</summary>
    public static bool IsTrustedSignature(SignatureInfo s) =>
        s.Status == SignatureStatus.Valid && s.Signer is not null && s.Signer.Contains("Intel", StringComparison.OrdinalIgnoreCase);

    /// <summary>Extrai a versão do nome do arquivo (ex.: "PresentMon-2.6.0-x64.exe" → 2.6.0).</summary>
    public static Version? VersionFromFileName(string fileName)
    {
        var m = VersionInName().Match(System.IO.Path.GetFileName(fileName));
        return m.Success && Version.TryParse(m.Groups[1].Value, out var v) ? v : null;
    }

    public static bool IsInsideDirectory(string path, string directory)
    {
        string full = System.IO.Path.GetFullPath(path);
        string dir = System.IO.Path.GetFullPath(directory).TrimEnd('\\') + "\\";
        return full.StartsWith(dir, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Versão pelo recurso de versão do arquivo (ProductVersion/FileVersion) ou, em último caso, pelo nome.</summary>
    public static Version? ReadVersion(string path, string? fallbackFileName = null)
    {
        try
        {
            var fvi = FileVersionInfo.GetVersionInfo(path);
            if (!string.IsNullOrWhiteSpace(fvi.ProductVersion) && Version.TryParse(fvi.ProductVersion.Split(' ', '+', '-')[0], out var pv)) return pv;
            if (fvi.FileMajorPart > 0) return new Version(fvi.FileMajorPart, fvi.FileMinorPart, fvi.FileBuildPart);
        }
        catch (FileNotFoundException) { }
        return VersionFromFileName(fallbackFileName ?? path);
    }

    /// <summary>Inspeciona um executável (versão, assinatura, hash). Não executa nada.</summary>
    public static OperationResult<PresentMonTool> Inspect(string path, string? toolsDirectory = null)
    {
        if (string.IsNullOrWhiteSpace(path) || !System.IO.Path.IsPathFullyQualified(path)) return OperationResult<PresentMonTool>.Fail("Caminho do PresentMon precisa ser absoluto.", "invalid_path");
        string full;
        try { full = System.IO.Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return OperationResult<PresentMonTool>.Fail(ex.Message, "invalid_path"); }
        if (!full.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) return OperationResult<PresentMonTool>.Fail("O PresentMon precisa ser um .exe.", "invalid_path");
        if (!File.Exists(full)) return OperationResult<PresentMonTool>.Fail("PresentMon não encontrado.", "not_found");
        toolsDirectory ??= RbmPaths.Tools;

        Version? version = ReadVersion(full);

        var sig = SignatureVerifier.Verify(full);
        string sha;
        try { sha = SignatureVerifier.Sha256(full); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return OperationResult<PresentMonTool>.Fail($"Não foi possível ler o arquivo: {ex.Message}", "io_error"); }

        bool inTools = IsInsideDirectory(full, toolsDirectory);
        string? recorded = null;
        if (inTools && ReadInstallRecord(toolsDirectory) is { } rec && string.Equals(rec.FileName, System.IO.Path.GetFileName(full), StringComparison.OrdinalIgnoreCase))
            recorded = rec.Sha256;
        return OperationResult<PresentMonTool>.Ok(new PresentMonTool(full, version, sig, sha, inTools, recorded));
    }

    /// <summary>
    /// Procura o PresentMon: primeiro <paramref name="userChosenPath"/> (se informado), depois RbmPaths.Tools
    /// (PresentMon*.exe, nível raiz e um subnível). Prefere o de maior versão com assinatura confiável.
    /// </summary>
    public static OperationResult<PresentMonTool> Find(string? userChosenPath = null, string? toolsDirectory = null)
    {
        toolsDirectory ??= RbmPaths.Tools;
        if (!string.IsNullOrWhiteSpace(userChosenPath)) return Inspect(userChosenPath, toolsDirectory);
        if (!Directory.Exists(toolsDirectory)) return OperationResult<PresentMonTool>.Fail("PresentMon não encontrado. Baixe-o pelo aplicativo ou escolha o executável.", "not_found");

        var files = new List<string>();
        try
        {
            files.AddRange(Directory.EnumerateFiles(toolsDirectory, "PresentMon*.exe"));
            foreach (var sub in Directory.EnumerateDirectories(toolsDirectory))
                files.AddRange(Directory.EnumerateFiles(sub, "PresentMon*.exe"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return OperationResult<PresentMonTool>.Fail(ex.Message, "io_error"); }
        // Ignora binários auxiliares (serviço, overlay) — só o console.
        files = files.Where(f => !System.IO.Path.GetFileName(f).Contains("Service", StringComparison.OrdinalIgnoreCase) &&
                                 !System.IO.Path.GetFileName(f).Contains("Overlay", StringComparison.OrdinalIgnoreCase)).ToList();
        if (files.Count == 0) return OperationResult<PresentMonTool>.Fail("PresentMon não encontrado. Baixe-o pelo aplicativo ou escolha o executável.", "not_found");

        var inspected = files.Select(f => Inspect(f, toolsDirectory)).Where(r => r.Success).Select(r => r.Value!).ToList();
        var best = inspected.OrderByDescending(t => t.IsTrusted && !t.HashMismatch).ThenByDescending(t => t.Version).FirstOrDefault();
        return best is null ? OperationResult<PresentMonTool>.Fail("Nenhum PresentMon válido encontrado.", "not_found") : OperationResult<PresentMonTool>.Ok(best);
    }

    public static PresentMonInstallRecord? ReadInstallRecord(string? toolsDirectory = null)
    {
        string p = System.IO.Path.Combine(toolsDirectory ?? RbmPaths.Tools, InstallRecordFileName);
        try { return File.Exists(p) ? JsonSerializer.Deserialize<PresentMonInstallRecord>(File.ReadAllText(p), AgentJsonLike) : null; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return null; }
    }

    internal static readonly JsonSerializerOptions AgentJsonLike = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    /// <summary>
    /// Monta os argumentos da linha de comando (lista, sem shell) conforme a versão principal.
    /// 2.x: --process_name X --output_file F --timed N --terminate_after_timed --terminate_on_proc_exit --stop_existing_session --no_console_stats --session_name S.
    /// 1.x: mesmas opções com um hífen (-no_top no lugar de --no_console_stats).
    /// </summary>
    public static IReadOnlyList<string> BuildCaptureArguments(int majorVersion, string processName, string outputFile, int seconds, string sessionName)
    {
        string secs = seconds.ToString(CultureInfo.InvariantCulture);
        if (majorVersion <= 1)
            return ["-process_name", processName, "-output_file", outputFile, "-timed", secs, "-terminate_after_timed", "-terminate_on_proc_exit",
                    "-stop_existing_session", "-no_top", "-session_name", sessionName];
        return ["--process_name", processName, "--output_file", outputFile, "--timed", secs, "--terminate_after_timed", "--terminate_on_proc_exit",
                "--stop_existing_session", "--no_console_stats", "--session_name", sessionName];
    }

    /// <summary>Argumentos para encerrar uma sessão ETW órfã (após cancelamento).</summary>
    public static IReadOnlyList<string> BuildTerminateSessionArguments(int majorVersion, string sessionName) =>
        majorVersion <= 1 ? ["-terminate_existing", "-session_name", sessionName] : ["--terminate_existing_session", "--session_name", sessionName];
}
