using Rbm.Core;

namespace Rbm.Gaming.Library;

/// <summary>Origem da detecção de um jogo instalado.</summary>
public enum GameSource
{
    Unknown = 0,
    Steam,
    Epic,
    Gog,
    Xbox,
    EaApp,
    UbisoftConnect,
    BattleNet,
    Manual,
}

/// <summary>Resultado da resolução do executável principal de um jogo.</summary>
/// <param name="Path">Caminho absoluto do executável (null quando não foi possível determinar).</param>
/// <param name="Confidence">Confiança da escolha. Alta = informado pelo manifesto da loja.</param>
/// <param name="Reason">Explicação (pt-BR) de como o executável foi escolhido.</param>
/// <param name="Alternatives">Outros candidatos plausíveis (para o usuário escolher manualmente).</param>
public sealed record ExecutableResolution(string? Path, Confidence Confidence, string Reason, IReadOnlyList<string> Alternatives)
{
    public static ExecutableResolution None(string reason) => new(null, Confidence.Low, reason, []);
    public bool IsResolved => Path is not null;
}

/// <summary>Jogo detectado no computador (somente dados locais; nenhuma conta é consultada).</summary>
/// <param name="Id">Identificador estável: "steam:730", "epic:Fortnite", "gog:1207658924", "xbox:Nome", "manual:HASH"...</param>
/// <param name="Name">Nome de exibição.</param>
/// <param name="Source">Loja/origem principal.</param>
/// <param name="SourceId">Identificador da loja (appid, AppName...), quando houver.</param>
/// <param name="InstallDirectory">Pasta de instalação (absoluta).</param>
/// <param name="Executable">Processo principal do jogo (o que renderiza: alvo do PresentMon e da preferência de GPU) + confiança.</param>
/// <param name="LaunchExecutablePath">Executável que a loja manda abrir (pode ser launcher/bootstrapper), quando informado.</param>
/// <param name="SizeOnDiskBytes">Tamanho informado pelo manifesto (Steam), quando disponível.</param>
/// <param name="AlsoFoundIn">Outras origens que listaram o mesmo jogo (após deduplicação).</param>
/// <param name="Notes">Observações da detecção (pt-BR).</param>
public sealed record DetectedGame(
    string Id,
    string Name,
    GameSource Source,
    string? SourceId,
    string? InstallDirectory,
    ExecutableResolution Executable,
    string? LaunchExecutablePath,
    long? SizeOnDiskBytes,
    IReadOnlyList<GameSource> AlsoFoundIn,
    IReadOnlyList<string> Notes)
{
    /// <summary>Nome do processo (ex.: "Game.exe") para captura de FPS, quando resolvido.</summary>
    public string? ProcessName => Executable.Path is { } p ? System.IO.Path.GetFileName(p) : null;
}

/// <summary>Saída de um scanner individual.</summary>
public sealed record GameScanOutput(GameSource Source, IReadOnlyList<DetectedGame> Games, IReadOnlyList<string> Warnings, bool SourcePresent);

/// <summary>Resultado consolidado da varredura de todas as lojas.</summary>
public sealed record GameLibraryScanResult(IReadOnlyList<DetectedGame> Games, IReadOnlyList<GameScanOutput> PerSource, DateTimeOffset ScannedAt)
{
    public IEnumerable<string> AllWarnings => PerSource.SelectMany(s => s.Warnings.Select(w => $"[{s.Source}] {w}"));
}

/// <summary>Uma fonte de jogos instalados.</summary>
public interface IGameLibraryScanner
{
    GameSource Source { get; }
    Task<GameScanOutput> ScanAsync(CancellationToken ct = default);
}
