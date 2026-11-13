using Rbm.Core;

namespace Rbm.Drivers;

/// <summary>
/// Validação ESTRITA de entradas vindas da UI para operações que rodam no RBM.Agent (elevado).
/// Toda operação elevada deste módulo chama estes métodos antes de executar qualquer coisa.
/// </summary>
public static class DriverInputValidator
{
    public const int MaxPathLength = 240;

    /// <summary>Raízes onde o agente nunca grava nem lê pacotes do usuário.</summary>
    public static IReadOnlyList<string> ProtectedRoots { get; } = BuildProtectedRoots();

    private static IReadOnlyList<string> BuildProtectedRoots()
    {
        var list = new List<string>();
        void Add(Environment.SpecialFolder f) { var p = Environment.GetFolderPath(f); if (!string.IsNullOrEmpty(p)) list.Add(Normalize(p)); }
        Add(Environment.SpecialFolder.Windows);
        Add(Environment.SpecialFolder.ProgramFiles);
        Add(Environment.SpecialFolder.ProgramFilesX86);
        Add(Environment.SpecialFolder.CommonApplicationData);
        foreach (var d in DriveInfo.GetDrives().Select(d => d.RootDirectory.FullName))
        {
            list.Add(Normalize(Path.Combine(d, "$Recycle.Bin")));
            list.Add(Normalize(Path.Combine(d, "System Volume Information")));
            list.Add(Normalize(Path.Combine(d, "Recovery")));
        }
        return list.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Pasta de destino de backup: absoluta, local, unidade FIXA, existente, não raiz, fora de pastas do sistema,
    /// sem junções/links simbólicos no caminho e, se dentro de C:\Users, somente no perfil do usuário atual.
    /// </summary>
    public static OperationResult<string> ValidateBackupFolder(string? folder)
    {
        var basic = ValidateLocalPath(folder, mustBeDirectory: true);
        if (!basic.Success) return basic;
        string full = basic.Value!;
        if (Path.GetPathRoot(full) is { } root && Normalize(root).Equals(Normalize(full), StringComparison.OrdinalIgnoreCase))
            return OperationResult<string>.Fail("Escolha uma pasta, não a raiz da unidade.", "folder.root");
        return OperationResult<string>.Ok(full);
    }

    /// <summary>INF a instalar: caminho local absoluto, extensão .inf, existente, até 16 MB, sem links no caminho, fora de pastas protegidas
    /// (exceto o Driver Store, aceito somente por <see cref="ValidateStoreInf"/>).</summary>
    public static OperationResult<string> ValidateInstallInf(string? infPath)
    {
        var basic = ValidateLocalPath(infPath, mustBeDirectory: false);
        if (!basic.Success) return basic;
        string full = basic.Value!;
        if (!full.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            return OperationResult<string>.Fail("O arquivo precisa ter extensão .inf.", "inf.extension");
        var fi = new FileInfo(full);
        if (fi.Length == 0 || fi.Length > InfFile.MaxInfBytes)
            return OperationResult<string>.Fail("Tamanho de INF inválido (vazio ou maior que 16 MB).", "inf.size");
        return OperationResult<string>.Ok(full);
    }

    /// <summary>INF dentro do Driver Store (FileRepository\&lt;pacote&gt;\&lt;arquivo&gt;.inf), usado para troca/reversão de versão.</summary>
    public static OperationResult<string> ValidateStoreInf(string? infPath)
    {
        if (string.IsNullOrWhiteSpace(infPath) || infPath.Length > MaxPathLength)
            return OperationResult<string>.Fail("Caminho inválido.", "path.invalid");
        string full;
        try { full = Path.GetFullPath(infPath); } catch { return OperationResult<string>.Fail("Caminho inválido.", "path.invalid"); }
        string? dir = Path.GetDirectoryName(full);
        string? parent = dir is null ? null : Path.GetDirectoryName(dir);
        if (parent is null || !Normalize(parent).Equals(Normalize(DriverStoreRules.FileRepository), StringComparison.OrdinalIgnoreCase))
            return OperationResult<string>.Fail("O INF precisa estar diretamente em uma pasta de pacote do Driver Store.", "store.location");
        if (!DriverStoreRules.IsSafePackageId(Path.GetFileName(dir)) || !DriverStoreRules.IsSafeFileName(Path.GetFileName(full)) ||
            !full.EndsWith(".inf", StringComparison.OrdinalIgnoreCase))
            return OperationResult<string>.Fail("Nome de pacote/INF do Driver Store inválido.", "store.name");
        if (!File.Exists(full)) return OperationResult<string>.Fail("INF do Driver Store não encontrado.", "store.missing");
        if (HasLinkInChain(full)) return OperationResult<string>.Fail("Caminho contém link simbólico/junção.", "path.reparse");
        return OperationResult<string>.Ok(full);
    }

    /// <summary>Nome publicado "oemNN.inf" (ou null/"*" para todos, quando <paramref name="allowAll"/>).</summary>
    public static OperationResult<string?> ValidatePublishedName(string? name, bool allowAll)
    {
        if (name is null || name == "*")
            return allowAll ? OperationResult<string?>.Ok(null) : OperationResult<string?>.Fail("Informe um pacote oemNN.inf.", "oem.required");
        return DriverStoreRules.IsPublishedOemInf(name)
            ? OperationResult<string?>.Ok(name.ToLowerInvariant())
            : OperationResult<string?>.Fail("Nome publicado inválido (esperado oemNN.inf).", "oem.invalid");
    }

    /// <summary>ID de instância PnP: tamanho limitado, sem caracteres de controle, enumerador conhecido.</summary>
    public static OperationResult<string> ValidateInstanceId(string? id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 400 || id.Any(c => char.IsControl(c) || c is '"' or '*' or '?' or '<' or '>' or '|' or '/'))
            return OperationResult<string>.Fail("ID de instância inválido.", "instance.invalid");
        if (id.IndexOf('\\') <= 0) return OperationResult<string>.Fail("ID de instância inválido (sem enumerador).", "instance.invalid");
        return OperationResult<string>.Ok(id.Trim());
    }

    // ------------------------------------------------------------------

    private static OperationResult<string> ValidateLocalPath(string? path, bool mustBeDirectory)
    {
        if (string.IsNullOrWhiteSpace(path) || path.Length > MaxPathLength)
            return OperationResult<string>.Fail("Caminho vazio ou longo demais.", "path.invalid");
        if (path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith("//", StringComparison.Ordinal))
            return OperationResult<string>.Fail("Caminhos de rede/dispositivo (\\\\...) não são permitidos.", "path.unc");
        if (!Path.IsPathFullyQualified(path) || path.Length < 3 || path[1] != ':')
            return OperationResult<string>.Fail("Informe um caminho absoluto com letra de unidade.", "path.relative");
        if (path.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || path.IndexOf(':', 2) >= 0)
            return OperationResult<string>.Fail("O caminho contém caracteres inválidos (incluindo fluxos alternativos \":\").", "path.chars");
        var segments = path.Split('\\', '/');
        if (segments.Any(s => s is "." or ".."))
            return OperationResult<string>.Fail("O caminho não pode conter \".\" ou \"..\".", "path.traversal");

        string full;
        try { full = Path.GetFullPath(path); } catch { return OperationResult<string>.Fail("Caminho inválido.", "path.invalid"); }

        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(full)!);
            if (!drive.IsReady || drive.DriveType != DriveType.Fixed)
                return OperationResult<string>.Fail("Use uma unidade local fixa (não removível, rede ou óptica).", "path.drive");
        }
        catch { return OperationResult<string>.Fail("Unidade inválida.", "path.drive"); }

        string norm = Normalize(full);
        foreach (var p in ProtectedRoots)
            if (IsUnder(norm, p))
                return OperationResult<string>.Fail("Pastas do sistema (Windows, Arquivos de Programas, ProgramData etc.) não são permitidas.", "path.protected");

        // Dentro de C:\Users: somente no perfil do usuário que executa (o agente roda com o mesmo usuário, elevado).
        string profile = Normalize(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        string? usersRoot = Path.GetDirectoryName(profile);
        if (usersRoot is not null && IsUnder(norm, Normalize(usersRoot)) && !IsUnder(norm, profile))
            return OperationResult<string>.Fail("Somente pastas dentro do seu próprio perfil de usuário são permitidas em C:\\Users.", "path.otherprofile");

        if (mustBeDirectory ? !Directory.Exists(full) : !File.Exists(full))
            return OperationResult<string>.Fail(mustBeDirectory ? "A pasta não existe." : "O arquivo não existe.", "path.missing");
        if (HasLinkInChain(full))
            return OperationResult<string>.Fail("O caminho contém link simbólico ou junção (não permitido em operações elevadas).", "path.reparse");
        return OperationResult<string>.Ok(full);
    }

    /// <summary>True se o próprio item ou qualquer pasta ancestral é link simbólico/junção (ponto de montagem).</summary>
    public static bool HasLinkInChain(string fullPath)
    {
        try
        {
            FileSystemInfo? cur = File.Exists(fullPath) ? new FileInfo(fullPath) : new DirectoryInfo(fullPath);
            while (cur is not null)
            {
                if (cur.Exists && cur.LinkTarget is not null) return true;
                cur = cur switch
                {
                    FileInfo f => f.Directory,
                    DirectoryInfo d => d.Parent,
                    _ => null,
                };
            }
            return false;
        }
        catch { return true; }
    }

    internal static string Normalize(string p) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(p)).ToUpperInvariant();

    internal static bool IsUnder(string normalizedPath, string normalizedRoot) =>
        normalizedPath.Equals(normalizedRoot, StringComparison.OrdinalIgnoreCase) ||
        normalizedPath.StartsWith(normalizedRoot + "\\", StringComparison.OrdinalIgnoreCase);
}
