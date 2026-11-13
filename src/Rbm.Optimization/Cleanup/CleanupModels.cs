using System.Text.Json.Serialization;
using Rbm.Core;

namespace Rbm.Optimization.Cleanup;

/// <summary>Como uma categoria é limpa.</summary>
public enum CleanupKind
{
    /// <summary>Arquivos sob uma ou mais pastas-raiz (com filtros de idade/padrão).</summary>
    Files,
    /// <summary>Lixeira do usuário atual (SHQueryRecycleBin / SHEmptyRecycleBin).</summary>
    RecycleBin,
}

/// <summary>Filtros aplicados a uma pasta-raiz.</summary>
public sealed record CleanupScanOptions
{
    /// <summary>Só arquivos cuja última gravação é mais antiga que isto (null = qualquer idade).</summary>
    public TimeSpan? MinAge { get; init; }
    /// <summary>Padrão de nome (ex.: "thumbcache_*.db"). Null = todos.</summary>
    public string? FilePattern { get; init; }
    /// <summary>Desce em subpastas (nunca segue junções/links simbólicos).</summary>
    public bool Recurse { get; init; } = true;
    /// <summary>Remove subpastas que ficaram vazias (nunca a própria raiz).</summary>
    public bool RemoveEmptyDirectories { get; init; } = true;
    /// <summary>Profundidade máxima de recursão (proteção contra árvores patológicas).</summary>
    public int MaxDepth { get; init; } = 48;
    /// <summary>Relógio (testes). Null = agora.</summary>
    public DateTime? NowUtc { get; init; }
}

/// <summary>
/// Categoria de limpeza da allow-list. As raízes são resolvidas no contexto do processo que executa
/// (UI = perfil do usuário; agente = contexto elevado), por isso categorias de perfil nunca rodam no agente.
/// </summary>
public sealed class CleanupCategory
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required RiskLevel Risk { get; init; }
    public required bool RequiresAdmin { get; init; }
    /// <summary>Efeitos colaterais em pt-BR (exibidos antes da confirmação).</summary>
    public required string SideEffects { get; init; }
    public CleanupKind Kind { get; init; } = CleanupKind.Files;
    /// <summary>Exige confirmação forte (dados do usuário irrecuperáveis).</summary>
    public bool Irreversible { get; init; }
    public CleanupScanOptions Options { get; init; } = new();
    /// <summary>Resolve as raízes. Retorna (raízes, motivo de indisponibilidade).</summary>
    [JsonIgnore] public Func<(IReadOnlyList<string> Roots, string? Unavailable)> ResolveRoots { get; init; } = () => (Array.Empty<string>(), null);
}

/// <summary>Resultado de uma categoria (análise = simulação sem exclusão; limpeza = exclusão real).</summary>
public sealed record CleanupCategoryResult(
    string CategoryId,
    string Name,
    bool DryRun,
    bool Available,
    // Análise: bytes/arquivos elegíveis. Limpeza: bytes/arquivos efetivamente removidos.
    long Bytes,
    int Files,
    // Arquivos elegíveis que não puderam ser removidos (em uso/bloqueados/acesso negado).
    int Skipped,
    // Arquivos mantidos por serem recentes (filtro de idade).
    int RecentKept,
    // Junções/links simbólicos encontrados e ignorados (nunca seguidos).
    int ReparsePointsSkipped,
    // Pastas/arquivos que não puderam ser lidos.
    int Inaccessible,
    int DirectoriesRemoved,
    List<string> Notes,
    double ElapsedMs)
{
    public static CleanupCategoryResult Unavailable(CleanupCategory c, bool dryRun, string reason) =>
        new(c.Id, c.Name, dryRun, false, 0, 0, 0, 0, 0, 0, 0, new List<string> { reason }, 0);
}

/// <summary>Resposta de <c>cleanup.run</c> (agente) e de execuções locais.</summary>
public sealed record CleanupRunResponse(bool DryRun, bool Elevated, List<CleanupCategoryResult> Results)
{
    [JsonIgnore] public long TotalBytes => Results.Sum(r => r.Bytes);
    [JsonIgnore] public int TotalFiles => Results.Sum(r => r.Files);
    [JsonIgnore] public int TotalSkipped => Results.Sum(r => r.Skipped);
}

/// <summary>Resultado da varredura de uma raiz.</summary>
public sealed record FolderScanResult(
    string Root,
    long Bytes,
    int Files,
    int Skipped,
    int RecentKept,
    int ReparsePointsSkipped,
    int Inaccessible,
    int DirectoriesRemoved,
    string? Error);
