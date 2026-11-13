using Rbm.Security;

namespace Rbm.Processes;

public enum AutostartRecommendationKind { Keep, OptionalSafeToDisable, InvestigateUnsigned, Unknown }

/// <summary>Recomendação conservadora (nunca aplicada automaticamente).</summary>
public sealed record AutostartRecommendation(AutostartRecommendationKind Kind, string Reason, string? KnownAppId = null)
{
    public static readonly AutostartRecommendation NotEvaluated = new(AutostartRecommendationKind.Unknown, "Não avaliado.");
}

public enum AutostartKind { StartupEntry, Service, ScheduledTask, Driver, ShellExtension, Winlogon }

/// <summary>Fatos objetivos usados na recomendação.</summary>
public sealed record AutostartFacts(
    AutostartKind Kind,
    string Name,
    string? Command,
    string? ImagePath,
    bool TargetExists,
    PublisherInfo? Publisher,
    bool IsProtected = false);

/// <summary>Inicializador/atualizador conhecido de terceiros (ou da Microsoft, mas opcional — ex.: OneDrive, Teams).</summary>
public sealed record KnownAutostartApp(string Id, string DisplayName, IReadOnlyList<string> FileNames, IReadOnlyList<string> PathFragments, string Note);

/// <summary>
/// Recomendação baseada em fatos verificáveis: assinatura (Authenticode), existência do arquivo e lista de
/// inicializadores conhecidos. Ordem: protegido → órfão → assinatura inválida/ausente → app conhecido →
/// Microsoft → demais (Unknown). Nunca aplica nada.
/// </summary>
public static class AutostartAdvisor
{
    private const string OptionalSuffix = " Desativar só impede o início automático com o Windows; o programa funciona normalmente quando aberto.";

    public static readonly IReadOnlyList<KnownAutostartApp> KnownApps =
    [
        new("steam", "Steam", ["steam.exe"], [], "Jogos e atualizações continuam disponíveis ao abrir o Steam."),
        new("discord", "Discord", ["discord.exe"], [@"\discord\"], "Você só deixa de aparecer online até abrir o Discord."),
        new("epic", "Epic Games Launcher", ["EpicGamesLauncher.exe", "EpicWebHelper.exe"], [], "Jogos da Epic continuam abrindo pelo launcher."),
        new("spotify", "Spotify", ["Spotify.exe", "SpotifyLauncher.exe"], [], "Nenhum efeito além de não abrir sozinho."),
        new("onedrive", "OneDrive", ["OneDrive.exe"], [], "A sincronização de arquivos fica pausada até o OneDrive ser aberto."),
        new("teams", "Microsoft Teams", ["ms-teams.exe", "Teams.exe", "ms-teamsupdate.exe"], [], "Notificações só chegam com o Teams aberto."),
        new("adobe-updater", "Atualizadores da Adobe", ["AdobeARM.exe", "AdobeGCClient.exe", "CCXProcess.exe", "Creative Cloud.exe", "AdobeUpdateService.exe", "acrotray.exe", "AdobeCollabSync.exe"], [],
            "As atualizações da Adobe continuam sendo oferecidas ao abrir os aplicativos."),
        new("battlenet", "Battle.net", ["Battle.net.exe", "Battle.net Launcher.exe"], [], "Jogos da Blizzard continuam abrindo pelo launcher."),
        new("ea", "EA app / Origin", ["EADesktop.exe", "EALauncher.exe", "Origin.exe", "EABackgroundService.exe"], [], "Jogos da EA continuam abrindo pelo launcher."),
        new("ubisoft", "Ubisoft Connect", ["upc.exe", "UbisoftConnect.exe"], [], "Jogos da Ubisoft continuam abrindo pelo launcher."),
        new("gog", "GOG Galaxy", ["GalaxyClient.exe"], [], "Jogos do GOG continuam abrindo pelo cliente."),
        new("riot-client", "Riot Client", ["RiotClientServices.exe"], [], "O cliente abre ao iniciar o jogo (o anti-cheat Vanguard é um item separado)."),
        new("skype", "Skype", ["Skype.exe"], [], "Chamadas só são recebidas com o Skype aberto."),
        new("zoom", "Zoom", ["Zoom.exe"], [], "Reuniões abrem normalmente pelo link."),
        new("dropbox", "Dropbox", ["Dropbox.exe"], [], "A sincronização fica pausada até o Dropbox ser aberto."),
        new("googledrive", "Google Drive", ["GoogleDriveFS.exe"], [], "A sincronização fica pausada até o Google Drive ser aberto."),
        new("whatsapp", "WhatsApp", ["WhatsApp.exe"], [], "Mensagens só chegam no PC com o app aberto."),
        new("telegram", "Telegram", ["Telegram.exe"], [], "Mensagens só chegam no PC com o app aberto."),
        new("edge-boost", "Inicialização rápida do Microsoft Edge", [], ["--no-startup-window"],
            "O Edge abre um pouco mais devagar na primeira vez."),
    ];

    public static AutostartRecommendation Recommend(AutostartFacts f)
    {
        if (f.IsProtected)
            return new(AutostartRecommendationKind.Keep, "Item essencial ou de segurança protegido pelo RBM Optimizer.");
        if (string.IsNullOrWhiteSpace(f.ImagePath))
            return new(AutostartRecommendationKind.Unknown, "Não foi possível identificar o arquivo executado por esta entrada.");
        if (!f.TargetExists)
            return new(AutostartRecommendationKind.InvestigateUnsigned,
                $"O arquivo \"{f.ImagePath}\" não existe: entrada órfã (programa desinstalado) ou caminho inválido. Investigue antes de remover.");

        var sig = f.Publisher?.Signature;
        if (sig is not null)
        {
            switch (sig.Status)
            {
                case SignatureStatus.Unsigned:
                    return new(AutostartRecommendationKind.InvestigateUnsigned,
                        "Executável sem assinatura digital: não é possível confirmar o fabricante. Verifique a origem do programa.");
                case SignatureStatus.Invalid or SignatureStatus.Untrusted or SignatureStatus.Revoked or SignatureStatus.Expired:
                    return new(AutostartRecommendationKind.InvestigateUnsigned,
                        $"Assinatura digital com problema ({sig.Status}). Arquivos alterados ou de origem desconhecida merecem análise.");
                case SignatureStatus.FileNotFound:
                    return new(AutostartRecommendationKind.InvestigateUnsigned, "O arquivo apontado não foi encontrado (entrada órfã).");
                case SignatureStatus.Error:
                    return new(AutostartRecommendationKind.Unknown, "Falha ao verificar a assinatura digital.");
            }
        }

        if (f.Kind != AutostartKind.Driver && MatchKnown(f) is { } app)
        {
            string verified = sig is { Status: SignatureStatus.Valid } ? $" Assinado por {sig.Signer}." : " (assinatura não verificada)";
            return new(AutostartRecommendationKind.OptionalSafeToDisable,
                $"{app.DisplayName}: inicializador/atualizador conhecido.{verified} {app.Note}{OptionalSuffix}", app.Id);
        }

        if (sig is { Status: SignatureStatus.Valid })
        {
            if (f.Publisher!.IsVerifiedMicrosoft)
                return new(AutostartRecommendationKind.Keep, $"Componente assinado pela Microsoft ({sig.Signer}).");
            return new(AutostartRecommendationKind.Unknown,
                $"Assinado por {sig.Signer ?? "fabricante identificado"}. Sem dados objetivos para recomendar: desative só se não usar o recurso.");
        }
        return new(AutostartRecommendationKind.Unknown, "Assinatura não verificada; sem dados suficientes para recomendar.");
    }

    public static KnownAutostartApp? MatchKnown(AutostartFacts f)
    {
        string file = f.ImagePath is null ? "" : Path.GetFileName(f.ImagePath);
        string hay = $"{f.ImagePath} {f.Command}";
        foreach (var app in KnownApps)
        {
            if (file.Length > 0 && app.FileNames.Any(n => n.Equals(file, StringComparison.OrdinalIgnoreCase))) return app;
            if (app.PathFragments.Any(p => hay.Contains(p, StringComparison.OrdinalIgnoreCase))) return app;
        }
        return null;
    }
}
