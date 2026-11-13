using System.Diagnostics;
using System.IO.Enumeration;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.Principal;
using System.Text.RegularExpressions;
using Rbm.Core.Agent;

namespace Rbm.Optimization.Cleanup;

/// <summary>
/// Motor de limpeza segura.
/// <list type="bullet">
/// <item>Análise (<c>dryRun = true</c>) só lê: tamanho e contagem, nada é excluído.</item>
/// <item>Nunca segue junções/links simbólicos (pontos de nova análise são contados e ignorados).</item>
/// <item>Todo item precisa estar estritamente dentro da raiz da categoria; a raiz nunca é removida.</item>
/// <item>Arquivos em uso/bloqueados/sem permissão são ignorados e contados em <c>Skipped</c>.</item>
/// <item>Raízes vêm somente de <see cref="CleanupCatalog"/> (allow-list), nunca do chamador.</item>
/// </list>
/// </summary>
public static partial class CleanupEngine
{
    public static IReadOnlyList<CleanupCategory> Categories => CleanupCatalog.All;

    public static Task<CleanupCategoryResult> AnalyzeAsync(CleanupCategory category, CancellationToken ct = default) =>
        Task.Run(() => Execute(category, dryRun: true, ct), ct);

    public static Task<CleanupCategoryResult> CleanAsync(CleanupCategory category, CancellationToken ct = default) =>
        Task.Run(() => Execute(category, dryRun: false, ct), ct);

    /// <summary>
    /// Requer elevação — executado via RBM.Agent (op <c>cleanup.run</c>, payload <see cref="CleanupRequest"/> →
    /// <see cref="CleanupRunResponse"/>). Valida o payload inteiro: ids no formato esperado, existentes na allow-list e
    /// marcados como administrativos. Categorias do perfil do usuário são recusadas (no agente o "perfil" seria o do
    /// administrador). Lança <see cref="ArgumentException"/> para payload inválido.
    /// </summary>
    public static CleanupRunResponse RunElevated(CleanupRequest req, CancellationToken ct)
    {
        if (req?.CategoryIds is null || req.CategoryIds.Count == 0) throw new ArgumentException("Nenhuma categoria de limpeza informada.");
        if (req.CategoryIds.Count > CleanupCatalog.All.Count) throw new ArgumentException("Categorias demais na requisição.");
        var cats = new List<CleanupCategory>();
        foreach (var id in req.CategoryIds.Distinct(StringComparer.Ordinal))
        {
            if (id is null || !IdRx().IsMatch(id)) throw new ArgumentException("Identificador de categoria inválido.");
            var c = CleanupCatalog.Get(id) ?? throw new ArgumentException($"Categoria de limpeza desconhecida: {id}");
            if (!c.RequiresAdmin)
                throw new ArgumentException($"A categoria \"{c.Name}\" usa pastas do perfil do usuário e é executada pela interface, sem elevação.");
            cats.Add(c);
        }
        var results = new List<CleanupCategoryResult>(cats.Count);
        foreach (var c in cats) results.Add(Execute(c, req.DryRun, ct));
        return new CleanupRunResponse(req.DryRun, IsElevated(), results);
    }

    [GeneratedRegex("^[a-z0-9.]{1,40}$", RegexOptions.CultureInvariant)]
    private static partial Regex IdRx();

    public static bool IsElevated()
    {
        try
        {
            using var id = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    /// <summary>Executa (ou simula) uma categoria no contexto do processo atual.</summary>
    public static CleanupCategoryResult Execute(CleanupCategory c, bool dryRun, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        if (c.Kind == CleanupKind.RecycleBin) return RecycleBin(c, dryRun, sw);

        var (roots, unavailable) = c.ResolveRoots();
        if (unavailable is not null) return CleanupCategoryResult.Unavailable(c, dryRun, unavailable);

        var notes = new List<string>();
        long bytes = 0; int files = 0, skipped = 0, recent = 0, reparse = 0, inaccessible = 0, dirs = 0, existing = 0;
        foreach (var root in roots)
        {
            ct.ThrowIfCancellationRequested();
            if (!Directory.Exists(root)) continue;
            existing++;
            var r = ProcessFolder(root, c.Options, dryRun, ct);
            if (r.Error is not null) notes.Add(r.Error);
            bytes += r.Bytes; files += r.Files; skipped += r.Skipped; recent += r.RecentKept;
            reparse += r.ReparsePointsSkipped; inaccessible += r.Inaccessible; dirs += r.DirectoriesRemoved;
        }
        if (existing == 0)
            return CleanupCategoryResult.Unavailable(c, dryRun, "Pasta não encontrada neste PC (nada a limpar).");
        if (inaccessible > 0)
            notes.Add(c.RequiresAdmin && !IsElevated()
                ? $"{inaccessible} item(ns) sem permissão de leitura: análise parcial sem elevação."
                : $"{inaccessible} item(ns) não puderam ser lidos.");
        if (reparse > 0) notes.Add($"{reparse} junção(ões)/link(s) ignorado(s) — nunca são seguidos.");
        if (recent > 0) notes.Add($"{recent} arquivo(s) recente(s) mantido(s) (menos de {c.Options.MinAge?.TotalHours:0} h).");
        if (!dryRun && skipped > 0) notes.Add($"{skipped} arquivo(s) em uso ou bloqueado(s) mantido(s).");
        return new CleanupCategoryResult(c.Id, c.Name, dryRun, true, bytes, files, skipped, recent, reparse, inaccessible, dirs, notes, sw.Elapsed.TotalMilliseconds);
    }

    // ------------------------------------------------------------------ Pastas

    /// <summary>
    /// Varre (e opcionalmente limpa) uma raiz. A raiz precisa passar por <see cref="IsSafeRoot"/> e não pode ser
    /// uma junção/link. Público para testes com pastas temporárias.
    /// </summary>
    public static FolderScanResult ProcessFolder(string root, CleanupScanOptions options, bool dryRun, CancellationToken ct)
    {
        string full;
        try { full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)); }
        catch (Exception ex) { return Error(root, "Caminho inválido: " + ex.Message); }
        if (!IsSafeRoot(full, out var why)) return Error(full, why!);

        var di = new DirectoryInfo(full);
        try
        {
            if (!di.Exists) return Error(full, "Pasta não encontrada.");
            if ((di.Attributes & FileAttributes.ReparsePoint) != 0) return Error(full, $"\"{full}\" é uma junção/link simbólico; ignorada por segurança.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
        {
            return Error(full, "Sem acesso à pasta: " + ex.Message);
        }

        DateTime? cutoff = options.MinAge is { } age ? (options.NowUtc ?? DateTime.UtcNow) - age : null;
        var w = new Walker(full, options, dryRun, cutoff, ct);
        w.Walk(di, 0);
        return new FolderScanResult(full, w.Bytes, w.Files, w.Skipped, w.Recent, w.Reparse, w.Inaccessible, w.Dirs, null);

        static FolderScanResult Error(string r, string msg) => new(r, 0, 0, 0, 0, 0, 0, 0, msg);
    }

    private sealed class Walker(string root, CleanupScanOptions o, bool dryRun, DateTime? cutoff, CancellationToken ct)
    {
        public long Bytes; public int Files, Skipped, Recent, Reparse, Inaccessible, Dirs;
        private static readonly EnumerationOptions EnumOpts = new()
        {
            RecurseSubdirectories = false, IgnoreInaccessible = false, AttributesToSkip = 0, ReturnSpecialDirectories = false,
        };

        public void Walk(DirectoryInfo dir, int depth)
        {
            ct.ThrowIfCancellationRequested();
            List<FileSystemInfo> entries;
            try { entries = dir.EnumerateFileSystemInfos("*", EnumOpts).ToList(); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { Inaccessible++; return; }

            foreach (var e in entries)
            {
                ct.ThrowIfCancellationRequested();
                FileAttributes attr;
                try { attr = e.Attributes; }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Inaccessible++; continue; }
                // Nunca segue nem remove junções/links simbólicos (arquivo ou pasta).
                if ((attr & FileAttributes.ReparsePoint) != 0) { Reparse++; continue; }
                // Defesa em profundidade: o item precisa estar estritamente dentro da raiz.
                if (!IsStrictlyUnder(e.FullName, root)) { Inaccessible++; continue; }

                if (e is DirectoryInfo sub)
                {
                    if (!o.Recurse) continue;
                    if (depth + 1 > o.MaxDepth) { Inaccessible++; continue; }
                    Walk(sub, depth + 1);
                    if (!dryRun && o.RemoveEmptyDirectories) TryRemoveEmpty(sub);
                }
                else if (e is FileInfo f)
                {
                    if (o.FilePattern is { } pat && !FileSystemName.MatchesSimpleExpression(pat, f.Name)) continue;
                    long len; DateTime newest;
                    try
                    {
                        len = f.Length;
                        // Cópias preservam a data de gravação: considera também a criação para não pegar arquivos recém-extraídos.
                        newest = f.LastWriteTimeUtc > f.CreationTimeUtc ? f.LastWriteTimeUtc : f.CreationTimeUtc;
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Inaccessible++; continue; }
                    if (cutoff is { } c && newest > c) { Recent++; continue; }
                    if (dryRun) { Bytes += len; Files++; continue; }
                    if (TryDelete(f)) { Bytes += len; Files++; } else Skipped++;
                }
            }
        }

        private static bool TryDelete(FileInfo f)
        {
            bool clearedReadOnly = false;
            try
            {
                f.Refresh();
                if (!f.Exists) return false;
                if ((f.Attributes & FileAttributes.ReparsePoint) != 0) return false;
                if ((f.Attributes & FileAttributes.ReadOnly) != 0) { f.Attributes &= ~FileAttributes.ReadOnly; clearedReadOnly = true; }
                f.Delete();
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException)
            {
                if (clearedReadOnly) { try { f.Attributes |= FileAttributes.ReadOnly; } catch { } }
                return false;
            }
        }

        private void TryRemoveEmpty(DirectoryInfo d)
        {
            try
            {
                d.Refresh();
                if (!d.Exists || (d.Attributes & FileAttributes.ReparsePoint) != 0) return;
                // Pastas criadas recentemente podem estar sendo preenchidas por um instalador.
                if (cutoff is { } c && d.CreationTimeUtc > c) return;
                if (d.EnumerateFileSystemInfos("*", EnumOpts).Any()) return;
                d.Delete(recursive: false);
                Dirs++;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or SecurityException) { }
        }
    }

    /// <summary>true se <paramref name="path"/> está estritamente dentro de <paramref name="root"/> (comparação de caminhos completos).</summary>
    public static bool IsStrictlyUnder(string path, string root)
    {
        try
        {
            string p = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string r = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)) + Path.DirectorySeparatorChar;
            return p.Length > r.Length && p.StartsWith(r, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Raiz aceitável: caminho local completo (sem UNC/dispositivo), não é raiz de unidade nem pasta de sistema/perfil
    /// em si, e fica dentro de uma das bases permitidas (perfil do usuário, %windir%\Temp, WER do ProgramData).
    /// </summary>
    public static bool IsSafeRoot(string fullPath, out string? reason)
    {
        reason = null;
        if (string.IsNullOrWhiteSpace(fullPath) || !Path.IsPathFullyQualified(fullPath) || fullPath.StartsWith(@"\\", StringComparison.Ordinal))
        { reason = "Caminho precisa ser local e completo."; return false; }
        string p = Path.TrimEndingDirectorySeparator(fullPath);
        string? driveRoot = Path.GetPathRoot(p);
        if (driveRoot is null || string.Equals(Path.TrimEndingDirectorySeparator(driveRoot), p, StringComparison.OrdinalIgnoreCase))
        { reason = "A raiz de uma unidade nunca é limpa."; return false; }

        static string F(Environment.SpecialFolder f) => Environment.GetFolderPath(f);
        string profile = F(Environment.SpecialFolder.UserProfile);
        string windir = F(Environment.SpecialFolder.Windows);
        string programData = F(Environment.SpecialFolder.CommonApplicationData);
        var forbidden = new[]
        {
            windir, F(Environment.SpecialFolder.System), F(Environment.SpecialFolder.ProgramFiles), F(Environment.SpecialFolder.ProgramFilesX86),
            programData, profile, F(Environment.SpecialFolder.LocalApplicationData), F(Environment.SpecialFolder.ApplicationData),
            string.IsNullOrEmpty(profile) ? "" : Path.GetDirectoryName(profile) ?? "",
        };
        if (forbidden.Any(f => !string.IsNullOrEmpty(f) && string.Equals(Path.TrimEndingDirectorySeparator(f), p, StringComparison.OrdinalIgnoreCase)))
        { reason = $"\"{p}\" é uma pasta do sistema/perfil e nunca é limpa por inteiro."; return false; }

        var allowedBases = new[]
        {
            profile,
            string.IsNullOrEmpty(windir) ? "" : Path.Combine(windir, "Temp"),
            string.IsNullOrEmpty(programData) ? "" : Path.Combine(programData, @"Microsoft\Windows\WER"),
        };
        bool ok = allowedBases.Any(b => !string.IsNullOrEmpty(b) &&
            (string.Equals(Path.TrimEndingDirectorySeparator(b), p, StringComparison.OrdinalIgnoreCase) || IsStrictlyUnder(p, b)));
        if (!ok) reason = $"\"{p}\" está fora das áreas permitidas para limpeza.";
        return ok;
    }

    // ------------------------------------------------------------------ Lixeira

    [StructLayout(LayoutKind.Sequential)]
    private struct SHQUERYRBINFO { public int cbSize; public long i64Size; public long i64NumItems; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHQueryRecycleBinW")]
    private static extern int SHQueryRecycleBin(string? pszRootPath, ref SHQUERYRBINFO pSHQueryRBInfo);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, EntryPoint = "SHEmptyRecycleBinW")]
    private static extern int SHEmptyRecycleBin(IntPtr hwnd, string? pszRootPath, uint dwFlags);

    private const uint SHERB_NOCONFIRMATION = 0x1, SHERB_NOPROGRESSUI = 0x2, SHERB_NOSOUND = 0x4;

    /// <summary>Tamanho e quantidade de itens na Lixeira do usuário atual (todas as unidades). Null se a consulta falhar.</summary>
    public static (long Bytes, long Items)? QueryRecycleBin(out string? error)
    {
        var info = new SHQUERYRBINFO { cbSize = Marshal.SizeOf<SHQUERYRBINFO>() };
        int hr = SHQueryRecycleBin(null, ref info);
        if (hr < 0) { error = $"SHQueryRecycleBin falhou (0x{hr:X8})."; return null; }
        error = null;
        return (info.i64Size, info.i64NumItems);
    }

    private static CleanupCategoryResult RecycleBin(CleanupCategory c, bool dryRun, Stopwatch sw)
    {
        var before = QueryRecycleBin(out var err);
        if (before is not { } b) return CleanupCategoryResult.Unavailable(c, dryRun, err ?? "Lixeira indisponível.");
        var notes = new List<string>();
        if (dryRun || b.Items == 0)
        {
            if (b.Items == 0) notes.Add("A Lixeira já está vazia.");
            return new(c.Id, c.Name, dryRun, true, dryRun ? b.Bytes : 0, (int)Math.Min(b.Items, int.MaxValue), 0, 0, 0, 0, 0, notes, sw.Elapsed.TotalMilliseconds);
        }
        int hr = SHEmptyRecycleBin(IntPtr.Zero, null, SHERB_NOCONFIRMATION | SHERB_NOPROGRESSUI | SHERB_NOSOUND);
        var after = QueryRecycleBin(out _) ?? (0, 0);
        long freed = Math.Max(0, b.Bytes - after.Bytes);
        long removed = Math.Max(0, b.Items - after.Items);
        if (hr < 0) notes.Add($"SHEmptyRecycleBin retornou 0x{hr:X8}.");
        if (after.Items > 0) notes.Add($"{after.Items} item(ns) não puderam ser removidos (em uso ou sem permissão).");
        return new(c.Id, c.Name, false, true, freed, (int)Math.Min(removed, int.MaxValue), (int)Math.Min(after.Items, int.MaxValue),
            0, 0, 0, 0, notes, sw.Elapsed.TotalMilliseconds);
    }
}
