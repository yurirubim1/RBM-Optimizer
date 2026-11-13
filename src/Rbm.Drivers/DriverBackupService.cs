using System.Globalization;
using System.Text.Json;
using Rbm.Core;
using Rbm.Security;

namespace Rbm.Drivers;

public sealed record BackupFileEntry(string RelativePath, string Sha256, long SizeBytes);

public sealed record BackupPackageEntry(
    string PublishedName,
    string? OriginalName,
    string? Provider,
    string? ClassName,
    string? Version,
    DateTime? Date,
    string? Signer,
    string? DriverPackageId,
    string Folder,
    bool Exported,
    string? Error);

/// <summary>Manifesto gravado em cada backup (rbm-drivers-manifest.json).</summary>
public sealed record DriverBackupManifest(
    int SchemaVersion,
    DateTimeOffset CreatedAt,
    string MachineName,
    string OsVersion,
    string Scope,
    IReadOnlyList<BackupPackageEntry> Packages,
    DriverSnapshot? Devices,
    IReadOnlyList<BackupFileEntry> Files);

public sealed record DriverExportResult(
    bool Success,
    string? BackupFolder,
    string? ManifestPath,
    int ExportedCount,
    int FailedCount,
    IReadOnlyList<string> Messages);

public sealed record BackupFileCheck(string RelativePath, bool Exists, bool HashMatches);

public sealed record BackupVerificationResult(
    bool Success,
    DriverBackupManifest? Manifest,
    IReadOnlyList<BackupFileCheck> Files,
    IReadOnlyList<PackageSignatureCheck> Packages,
    IReadOnlyList<string> UnlistedFiles,
    IReadOnlyList<string> Messages);

/// <summary>
/// Backup de drivers de terceiros com <c>pnputil /export-driver</c> + manifesto (dispositivos, pacotes, SHA-256 de .inf/.cat),
/// e verificação de um backup existente (hashes + assinatura do catálogo + INF ∈ catálogo).
/// <para>Elevação: no Windows 11 26200 o export funcionou SEM elevação (os arquivos do store são legíveis por usuários).
/// O mapeamento para o agente (AgentOps.DriverExport) existe para builds/políticas em que o export exija administrador.</para>
/// </summary>
public sealed class DriverBackupService
{
    public const string ManifestFileName = "rbm-drivers-manifest.json";
    public const int ManifestSchema = 1;
    private static readonly TimeSpan ExportTimeout = TimeSpan.FromMinutes(3);

    private readonly DriverStoreService _store = new();
    private readonly DeviceInventoryService _inventory = new();

    /// <summary>
    /// Exporta todos os pacotes de terceiros (<paramref name="publishedName"/> null ou "*") ou um único "oemNN.inf"
    /// para uma subpasta com data/hora dentro de <paramref name="destinationFolder"/>.
    /// Valida a pasta (absoluta, local fixa, fora de pastas do sistema, sem links). Requer elevação apenas se o Windows exigir —
    /// pode ser executado via RBM.Agent (AgentOps.DriverExport).
    /// </summary>
    public async Task<DriverExportResult> ExportAsync(string destinationFolder, string? publishedName, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        var msgs = new List<string>();
        var folder = DriverInputValidator.ValidateBackupFolder(destinationFolder);
        if (!folder.Success) return new(false, null, null, 0, 0, [folder.Message]);
        var name = DriverInputValidator.ValidatePublishedName(publishedName, allowAll: true);
        if (!name.Success) return new(false, null, null, 0, 0, [name.Message]);

        var store = await _store.EnumerateAsync(ct).ConfigureAwait(false);
        msgs.AddRange(store.Warnings);
        IReadOnlyList<DriverPackage> targets = name.Value is null
            ? store.Packages
            : store.Find(name.Value) is { } one ? [one] : [];
        if (targets.Count == 0)
            return new(false, null, null, 0, 0, [.. msgs, name.Value is null ? "Nenhum pacote de terceiro encontrado no Driver Store." : $"Pacote {name.Value} não existe no Driver Store."]);

        string stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        string root = Path.Combine(folder.Value!, $"RBM-Drivers-{stamp}");
        if (Directory.Exists(root) || File.Exists(root))
            return new(false, null, null, 0, 0, ["A pasta de backup com este horário já existe; tente novamente."]);
        Directory.CreateDirectory(root);

        var entries = new List<BackupPackageEntry>();
        int ok = 0, fail = 0;
        foreach (var p in targets)
        {
            ct.ThrowIfCancellationRequested();
            string sub = PackageFolderName(p);
            string dest = Path.Combine(root, sub);
            Directory.CreateDirectory(dest);
            progress?.Report($"Exportando {p.PublishedName} ({p.OriginalName})...");
            var r = await ProcessRunner.RunAsync(DriverStoreService.PnpUtilPath, ["/export-driver", p.PublishedName, dest], ExportTimeout, ct, ProcessRunner.OemEncoding)
                .ConfigureAwait(false);
            bool exported = r.Success && Directory.EnumerateFiles(dest, "*.inf", SearchOption.AllDirectories).Any();
            string? err = exported ? null : $"pnputil código {r.ExitCode}{(r.TimedOut ? " (tempo esgotado)" : "")}: {FirstLines(r.StdOut + r.StdErr)}";
            if (exported) ok++; else fail++;
            entries.Add(new BackupPackageEntry(p.PublishedName, p.OriginalName, p.Provider, p.ClassName, p.Version, p.Date, p.Signer,
                p.DriverPackageId, sub, exported, err));
        }

        progress?.Report("Calculando hashes e gravando o manifesto...");
        DriverSnapshot? devices = null;
        try { devices = DriverSnapshot.FromInventory(_inventory.Collect(new InventoryOptions { ResolveSignatures = false, AllowWmiFallback = true }, ct)); }
        catch (Exception ex) when (ex is not OperationCanceledException) { msgs.Add($"Inventário de dispositivos indisponível para o manifesto: {ex.Message}"); }

        var files = HashPackageFiles(root, ct);
        var manifest = new DriverBackupManifest(ManifestSchema, DateTimeOffset.Now, Environment.MachineName, Environment.OSVersion.VersionString,
            name.Value ?? "*", entries, devices, files);
        string manifestPath = Path.Combine(root, ManifestFileName);
        await File.WriteAllTextAsync(manifestPath, JsonSerializer.Serialize(manifest, DriverSnapshot.JsonOptions), ct).ConfigureAwait(false);

        msgs.Add($"{ok} pacote(s) exportado(s){(fail > 0 ? $", {fail} com falha" : "")} em {root}.");
        return new DriverExportResult(fail == 0 && ok > 0, root, manifestPath, ok, fail, msgs);
    }

    /// <summary>
    /// Verifica um backup: manifesto legível, cada arquivo listado existe com o mesmo SHA-256, cada .cat tem assinatura
    /// confiável (<see cref="SignatureVerifier"/>) e cada .inf consta no seu catálogo. Somente leitura; não exige elevação.
    /// </summary>
    public Task<BackupVerificationResult> VerifyAsync(string backupFolder, CancellationToken ct = default) =>
        Task.Run(() => Verify(backupFolder, ct), ct);

    public static BackupVerificationResult Verify(string backupFolder, CancellationToken ct = default)
    {
        var msgs = new List<string>();
        if (string.IsNullOrWhiteSpace(backupFolder) || !Path.IsPathFullyQualified(backupFolder) || !Directory.Exists(backupFolder))
            return Empty("Pasta de backup inválida ou inexistente.");
        string root = Path.GetFullPath(backupFolder);
        string manifestPath = Path.Combine(root, ManifestFileName);
        if (!File.Exists(manifestPath)) return Empty($"Manifesto {ManifestFileName} não encontrado.");

        DriverBackupManifest? manifest;
        try
        {
            var fi = new FileInfo(manifestPath);
            if (fi.Length > 64 * 1024 * 1024) return Empty("Manifesto grande demais.");
            manifest = JsonSerializer.Deserialize<DriverBackupManifest>(File.ReadAllText(manifestPath), DriverSnapshot.JsonOptions);
        }
        catch (Exception ex) { return Empty($"Manifesto ilegível: {ex.Message}"); }
        if (manifest is null || manifest.SchemaVersion != ManifestSchema || manifest.Files is null)
            return Empty("Manifesto inválido ou de versão não suportada.");

        var fileChecks = new List<BackupFileCheck>();
        var listed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in manifest.Files)
        {
            ct.ThrowIfCancellationRequested();
            string? full = SafeCombine(root, f.RelativePath);
            if (full is null) { fileChecks.Add(new(f.RelativePath, false, false)); msgs.Add($"Caminho inválido no manifesto: {f.RelativePath}"); continue; }
            listed.Add(full);
            bool exists = File.Exists(full);
            bool match = exists && string.Equals(SignatureVerifier.Sha256(full), f.Sha256, StringComparison.OrdinalIgnoreCase);
            fileChecks.Add(new(f.RelativePath, exists, match));
        }

        var pkgChecks = new List<PackageSignatureCheck>();
        foreach (var inf in Directory.EnumerateFiles(root, "*.inf", SearchOption.AllDirectories))
        {
            ct.ThrowIfCancellationRequested();
            pkgChecks.Add(CatalogVerifier.VerifyPackage(inf));
        }

        var unlisted = Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories)
            .Where(p => IsHashedKind(p) && !listed.Contains(p))
            .Select(p => Path.GetRelativePath(root, p)).ToArray();

        int missing = fileChecks.Count(c => !c.Exists), mismatch = fileChecks.Count(c => c.Exists && !c.HashMatches);
        int untrusted = pkgChecks.Count(c => !c.IsTrusted);
        if (missing > 0) msgs.Add($"{missing} arquivo(s) do manifesto ausente(s).");
        if (mismatch > 0) msgs.Add($"{mismatch} arquivo(s) com hash diferente do manifesto (alterados/corrompidos).");
        if (untrusted > 0) msgs.Add($"{untrusted} pacote(s) sem assinatura confiável ou com INF fora do catálogo.");
        if (unlisted.Length > 0) msgs.Add($"{unlisted.Length} arquivo(s) .inf/.cat presentes mas não listados no manifesto.");
        bool success = missing == 0 && mismatch == 0 && untrusted == 0 && unlisted.Length == 0 && fileChecks.Count > 0;
        if (success) msgs.Add($"Backup íntegro: {fileChecks.Count} arquivo(s) conferido(s), {pkgChecks.Count} pacote(s) com catálogo assinado.");
        return new BackupVerificationResult(success, manifest, fileChecks, pkgChecks, unlisted, msgs);

        BackupVerificationResult Empty(string m) => new(false, null, [], [], [], [m]);
    }

    // ------------------------------------------------------------------

    private static IReadOnlyList<BackupFileEntry> HashPackageFiles(string root, CancellationToken ct)
    {
        var list = new List<BackupFileEntry>();
        foreach (var f in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).Where(IsHashedKind).OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            list.Add(new BackupFileEntry(Path.GetRelativePath(root, f), SignatureVerifier.Sha256(f), new FileInfo(f).Length));
        }
        return list;
    }

    private static bool IsHashedKind(string path) =>
        path.EndsWith(".inf", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".cat", StringComparison.OrdinalIgnoreCase);

    /// <summary>Combina caminho relativo do manifesto garantindo que o resultado fique dentro da pasta do backup.</summary>
    public static string? SafeCombine(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative) || relative.Contains(':')) return null;
        if (relative.Split('\\', '/').Any(s => s is ".." or ".")) return null;
        string full = Path.GetFullPath(Path.Combine(root, relative));
        return DriverInputValidator.IsUnder(DriverInputValidator.Normalize(full), DriverInputValidator.Normalize(root)) ? full : null;
    }

    internal static string PackageFolderName(DriverPackage p)
    {
        string orig = Path.GetFileNameWithoutExtension(p.OriginalName ?? "pacote");
        var safe = new string(orig.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        if (safe.Length > 60) safe = safe[..60];
        return $"{Path.GetFileNameWithoutExtension(p.PublishedName)}_{safe}";
    }

    private static string FirstLines(string s) =>
        string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Take(4));
}
