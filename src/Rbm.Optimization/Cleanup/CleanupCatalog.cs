using Rbm.Core;

namespace Rbm.Optimization.Cleanup;

/// <summary>
/// Allow-list fixa de categorias de limpeza. Nenhum caminho vem da UI nem do canal do agente: as raízes são
/// montadas aqui a partir de pastas conhecidas do Windows.
/// <para>
/// Cache do Delivery Optimization: OMITIDO de propósito. O único meio oficial documentado é o cmdlet
/// <c>Delete-DeliveryOptimizationCache</c> (PowerShell) ou Configurações → Armazenamento → Arquivos temporários;
/// não há API nativa documentada e apagar a pasta do serviço DoSvc diretamente não é suportado. Ver <see cref="DeliveryOptimizationNote"/>.
/// </para>
/// </summary>
public static class CleanupCatalog
{
    public static readonly TimeSpan TempMinAge = TimeSpan.FromHours(24);

    public const string DeliveryOptimizationNote =
        "Cache do Delivery Optimization não incluído: o Windows não oferece API nativa documentada para limpá-lo. " +
        "Os meios oficiais são Configurações → Sistema → Armazenamento → Arquivos temporários (\"Arquivos de Otimização de Entrega\") " +
        "ou o cmdlet Delete-DeliveryOptimizationCache. O RBM não apaga a pasta do serviço diretamente.";

    private static string LocalAppData => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    private static string ProgramData => Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
    private static string WindowsDir => Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    private static (IReadOnlyList<string>, string?) Under(string? baseDir, params string[] relatives)
    {
        if (string.IsNullOrWhiteSpace(baseDir)) return (Array.Empty<string>(), "Pasta base indisponível neste contexto.");
        return (relatives.Select(r => Path.Combine(baseDir, r)).ToArray(), null);
    }

    public static IReadOnlyList<CleanupCategory> All { get; } = new CleanupCategory[]
    {
        new()
        {
            Id = "user.temp", Name = "Arquivos temporários do usuário",
            Description = "Pasta %TEMP% do usuário. Somente arquivos sem alteração há mais de 24 h; arquivos em uso são ignorados.",
            Risk = RiskLevel.Minimal, RequiresAdmin = false,
            SideEffects = "Nenhum esperado. Instaladores em andamento usam arquivos recentes, que são mantidos pelo filtro de 24 h.",
            Options = new() { MinAge = TempMinAge },
            ResolveRoots = ResolveUserTemp,
        },
        new()
        {
            Id = "user.crashdumps", Name = "Despejos de falha de aplicativos",
            Description = "%LOCALAPPDATA%\\CrashDumps: arquivos .dmp gerados quando um programa trava.",
            Risk = RiskLevel.Low, RequiresAdmin = false,
            SideEffects = "Os despejos deixam de estar disponíveis para diagnosticar travamentos já ocorridos.",
            ResolveRoots = () => Under(LocalAppData, "CrashDumps"),
        },
        new()
        {
            Id = "shaders.directx", Name = "Cache de shaders do DirectX",
            Description = "%LOCALAPPDATA%\\D3DSCache: shaders compilados pelo DirectX. Arquivos em uso são ignorados.",
            Risk = RiskLevel.Low, RequiresAdmin = false,
            SideEffects = "Na primeira execução de cada jogo os shaders são recompilados: telas de carregamento mais longas e engasgos (stutter) temporários.",
            ResolveRoots = () => Under(LocalAppData, "D3DSCache"),
        },
        new()
        {
            Id = "shaders.amd", Name = "Cache de shaders AMD",
            Description = "%LOCALAPPDATA%\\AMD\\DxCache, DxcCache e GLCache (driver Radeon).",
            Risk = RiskLevel.Low, RequiresAdmin = false,
            SideEffects = "Na primeira execução de cada jogo os shaders são recompilados: engasgos (stutter) temporários até o cache ser refeito.",
            ResolveRoots = () => Under(LocalAppData, @"AMD\DxCache", @"AMD\DxcCache", @"AMD\GLCache"),
        },
        new()
        {
            Id = "shaders.nvidia", Name = "Cache de shaders NVIDIA",
            Description = "%LOCALAPPDATA%\\NVIDIA\\DXCache e GLCache (driver GeForce).",
            Risk = RiskLevel.Low, RequiresAdmin = false,
            SideEffects = "Na primeira execução de cada jogo os shaders são recompilados: engasgos (stutter) temporários até o cache ser refeito.",
            ResolveRoots = () => Under(LocalAppData, @"NVIDIA\DXCache", @"NVIDIA\GLCache"),
        },
        new()
        {
            Id = "explorer.thumbcache", Name = "Cache de miniaturas do Explorer",
            Description = "Arquivos thumbcache_*.db do Explorer. Os que estão em uso pelo Explorer são ignorados.",
            Risk = RiskLevel.Minimal, RequiresAdmin = false,
            SideEffects = "Miniaturas de imagens e vídeos são geradas novamente ao abrir as pastas (um pouco mais lento na primeira vez).",
            Options = new() { FilePattern = "thumbcache_*.db", Recurse = false, RemoveEmptyDirectories = false },
            ResolveRoots = () => Under(LocalAppData, @"Microsoft\Windows\Explorer"),
        },
        new()
        {
            Id = "recyclebin", Name = "Lixeira",
            Description = "Esvazia a Lixeira do usuário atual em todas as unidades (API do Shell).",
            Risk = RiskLevel.Medium, RequiresAdmin = false, Kind = CleanupKind.RecycleBin, Irreversible = true,
            SideEffects = "Os itens da Lixeira são excluídos definitivamente e não podem ser restaurados.",
        },
        new()
        {
            Id = "system.wer", Name = "Relatórios de erros do Windows (WER)",
            Description = "%ProgramData%\\Microsoft\\Windows\\WER\\ReportQueue e ReportArchive.",
            Risk = RiskLevel.Low, RequiresAdmin = true,
            SideEffects = "Relatórios ainda não enviados à Microsoft são descartados e o Monitor de Confiabilidade perde os detalhes técnicos desses eventos.",
            ResolveRoots = () => Under(ProgramData, @"Microsoft\Windows\WER\ReportQueue", @"Microsoft\Windows\WER\ReportArchive"),
        },
        new()
        {
            Id = "system.temp", Name = "Temporários do Windows",
            Description = "C:\\Windows\\Temp. Somente arquivos sem alteração há mais de 24 h; arquivos em uso são ignorados.",
            Risk = RiskLevel.Low, RequiresAdmin = true,
            SideEffects = "Nenhum esperado. Atualizações e instaladores em andamento usam arquivos recentes, que são mantidos pelo filtro de 24 h.",
            Options = new() { MinAge = TempMinAge },
            ResolveRoots = () => Under(WindowsDir, "Temp"),
        },
    };

    public static CleanupCategory? Get(string? id) => id is null ? null : All.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.Ordinal));

    /// <summary>%TEMP% do usuário — aceito somente se estiver dentro do perfil (TEMP apontando para C:\ etc. é recusado).</summary>
    private static (IReadOnlyList<string>, string?) ResolveUserTemp()
    {
        string temp;
        try { temp = Path.GetFullPath(Path.GetTempPath()); }
        catch (Exception ex) { return (Array.Empty<string>(), "Não foi possível obter %TEMP%: " + ex.Message); }
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (string.IsNullOrEmpty(profile) || !CleanupEngine.IsStrictlyUnder(temp, profile))
            return (Array.Empty<string>(), $"%TEMP% aponta para \"{temp}\", fora do perfil do usuário; ignorado por segurança.");
        return (new[] { temp }, null);
    }
}
