using System.Diagnostics;
using Rbm.Core;
using Microsoft.Win32;

namespace Rbm.Gaming.Overlays;

public enum OverlayCategory { Overlay, Recorder, Monitoring, Launcher, Peripheral, Wallpaper }

/// <summary>Software conhecido que injeta overlay, grava a tela ou consulta sensores durante o jogo.</summary>
public sealed record KnownOverlay(string Product, OverlayCategory Category, IReadOnlyList<string> ProcessNames, string ImpactPt, Confidence Confidence);

/// <summary>Processo em execução que corresponde a um software conhecido.</summary>
public sealed record OverlayFinding(string Product, OverlayCategory Category, string ProcessName, IReadOnlyList<int> Pids, string ImpactPt, string TestSuggestionPt, Confidence Confidence);

/// <summary>Estado de Game DVR / Game Bar (somente leitura). null = valor ausente (padrão do Windows).</summary>
public sealed record GameDvrState(int? GameDvrEnabled, int? AppCaptureEnabled, int? AutoGameModeEnabled, int? PolicyAllowGameDvr, string InterpretationPt);

/// <summary>
/// Detecção de overlays/gravadores/hooks em execução. Somente leitura: não encerra nada.
/// Remover um item é um TESTE a fazer (comparando capturas antes/depois), não uma correção garantida.
/// </summary>
public static class OverlayDetector
{
    public const string DisclaimerPt =
        "Fechar ou desativar estes programas é um teste a realizar (capture antes e depois), não uma correção garantida. " +
        "Muitos têm impacto desprezível na maioria dos sistemas.";

    public static readonly IReadOnlyList<KnownOverlay> Catalog =
    [
        new("Discord (overlay no jogo)", OverlayCategory.Overlay, ["Discord", "DiscordPTB", "DiscordCanary", "DiscordHookHelper64", "DiscordHookHelper"],
            "O overlay do Discord injeta um hook de renderização; em alguns jogos causa queda de FPS, stutter ou conflitos com outros overlays. O processo Discord em si é leve; o impacto vem do overlay (Configurações → Jogos → Overlay).", Confidence.Medium),
        new("Steam Overlay", OverlayCategory.Overlay, ["gameoverlayui", "gameoverlayui64"],
            "Overlay do Steam ativo no jogo. Impacto geralmente pequeno; gravação de jogo do Steam (Game Recording) aumenta o uso de GPU/disco.", Confidence.Medium),
        new("NVIDIA App / GeForce Experience (overlay e ShadowPlay)", OverlayCategory.Recorder, ["NVIDIA Share", "NVIDIA Overlay", "nvsphelper64", "NVIDIA app"],
            "Overlay e gravação instantânea (replay) da NVIDIA. O replay mantém o codificador de vídeo ativo e grava no disco continuamente.", Confidence.Medium),
        new("NVIDIA Container", OverlayCategory.Launcher, ["nvcontainer"],
            "Serviço de suporte do driver/App da NVIDIA. Normal estar em execução; listado só como contexto.", Confidence.Low),
        new("AMD Software: Adrenalin (overlay/gravação)", OverlayCategory.Recorder, ["AMDRSServ", "RadeonSoftware", "AMDRSSrcExt", "amdow"],
            "Overlay de métricas, gravação/replay instantâneo e streaming da AMD. O replay instantâneo mantém o codificador ativo; o overlay de métricas amostra sensores periodicamente.", Confidence.Medium),
        new("Xbox Game Bar", OverlayCategory.Overlay, ["GameBar", "GameBarFTServer", "GameBarPresenceWriter", "XboxGameBarWidgets"],
            "A Game Bar com 'Capturas em segundo plano' (gravar o que aconteceu) mantém gravação contínua. Sem isso, o impacto costuma ser baixo.", Confidence.Medium),
        new("RivaTuner Statistics Server (RTSS)", OverlayCategory.Monitoring, ["RTSS", "RTSSHooksLoader64", "RTSSHooksLoader", "EncoderServer64", "EncoderServer"],
            "Injeta hooks em todos os jogos para OSD e limitador de FPS. O limitador muda o ritmo de quadros (pode melhorar ou piorar); conflitos com outros overlays são comuns.", Confidence.Medium),
        new("MSI Afterburner", OverlayCategory.Monitoring, ["MSIAfterburner"],
            "Monitoramento de sensores em intervalo curto pode causar picos de DPC/stutter em alguns sistemas; o OSD depende do RTSS.", Confidence.Low),
        new("OBS Studio", OverlayCategory.Recorder, ["obs64", "obs32", "obs"],
            "Captura de jogo/tela e codificação de vídeo consomem GPU (e CPU no x264). A captura de jogo usa hook no processo.", Confidence.Medium),
        new("Streamlabs Desktop", OverlayCategory.Recorder, ["Streamlabs OBS", "Streamlabs Desktop"],
            "Gravação/streaming (base OBS): uso de GPU/CPU para captura e codificação.", Confidence.Medium),
        new("Medal.tv", OverlayCategory.Recorder, ["Medal", "MedalEncoder"],
            "Gravação contínua de clipes (replay): codificador de vídeo ativo e escrita em disco constantes.", Confidence.Medium),
        new("Overwolf / Outplayed", OverlayCategory.Overlay, ["Overwolf", "OverwolfBrowser", "OverwolfHelper", "OverwolfHelper64", "Outplayed"],
            "Plataforma de overlays de terceiros (injeta hook) e gravação de clipes; pode adicionar uso de CPU/GPU e conflitos.", Confidence.Medium),
        new("Razer Cortex", OverlayCategory.Launcher, ["RazerCortex", "Razer Cortex", "CortexLauncherService"],
            "Ferramenta de 'boost' que encerra/ajusta processos ao iniciar jogos e pode exibir overlay de FPS. Efeitos variam.", Confidence.Low),
        new("EA app (overlay no jogo)", OverlayCategory.Overlay, ["IGOProxy64", "IGOProxy"],
            "Overlay no jogo da EA (In-Game Overlay) injetado no processo.", Confidence.Low),
        new("Ubisoft Connect (overlay)", OverlayCategory.Overlay, ["UplayWebCore", "upc"],
            "Overlay e serviços do Ubisoft Connect; o overlay injeta hook no jogo.", Confidence.Low),
        new("Bandicam", OverlayCategory.Recorder, ["bdcam", "bdcam64"],
            "Gravação de tela/jogo com hook de captura; consome CPU/GPU ao gravar.", Confidence.Medium),
        new("Action! (Mirillis)", OverlayCategory.Recorder, ["Action_x64"],
            "Gravação de jogo com hook e codificação de vídeo.", Confidence.Low),
        new("Fraps", OverlayCategory.Monitoring, ["fraps", "fraps64"],
            "Ferramenta antiga de FPS/gravação com hook; pode conflitar com APIs modernas.", Confidence.Low),
        new("HWiNFO", OverlayCategory.Monitoring, ["HWiNFO64", "HWiNFO32"],
            "Leitura de sensores em intervalo curto (especialmente SMBus/EC) pode causar picos de latência/DPC em alguns sistemas.", Confidence.Low),
        new("Wallpaper Engine", OverlayCategory.Wallpaper, ["wallpaper64", "wallpaper32"],
            "Papel de parede animado usa GPU; normalmente pausa em tela cheia, mas em janela/sem bordas ou multimonitor pode continuar renderizando.", Confidence.Medium),
        new("Lively Wallpaper", OverlayCategory.Wallpaper, ["Lively", "Livelywpf"],
            "Papel de parede animado; pode usar GPU enquanto o jogo roda em janela/sem bordas.", Confidence.Low),
        new("Corsair iCUE", OverlayCategory.Peripheral, ["iCUE", "Corsair.Service"],
            "Software de periféricos/RGB; algumas versões causaram picos de CPU/DPC ao consultar sensores.", Confidence.Low),
        new("SignalRGB", OverlayCategory.Peripheral, ["SignalRgb", "SignalRgbLauncher"],
            "Efeitos RGB sincronizados com tela/jogo usam CPU/GPU continuamente.", Confidence.Low),
        new("ASUS Armoury Crate", OverlayCategory.Peripheral, ["ArmouryCrate", "ArmouryCrate.Service", "ArmourySocketServer"],
            "Serviços de placa-mãe/RGB; relatos de uso de CPU em segundo plano em algumas versões.", Confidence.Low),
    ];

    /// <summary>Casa nomes de processos (sem ou com .exe) com o catálogo. Função pura.</summary>
    public static IReadOnlyList<OverlayFinding> Match(IEnumerable<(string Name, int Pid)> processes)
    {
        var index = new Dictionary<string, KnownOverlay>(StringComparer.OrdinalIgnoreCase);
        foreach (var k in Catalog) foreach (var n in k.ProcessNames) index.TryAdd(n, k);
        var hits = new Dictionary<(string product, string proc), List<int>>();
        foreach (var (name, pid) in processes)
        {
            string n = name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
            if (!index.TryGetValue(n, out var k)) continue;
            string canonical = k.ProcessNames.First(p => string.Equals(p, n, StringComparison.OrdinalIgnoreCase));
            var key = (k.Product, canonical);
            if (!hits.TryGetValue(key, out var pids)) hits[key] = pids = [];
            pids.Add(pid);
        }
        return hits.Select(h =>
        {
            var k = Catalog.First(c => c.Product == h.Key.product);
            string test = k.Category switch
            {
                OverlayCategory.Recorder => $"Teste: desative a gravação/replay ou feche '{k.Product}', capture novamente e compare.",
                OverlayCategory.Overlay => $"Teste: desative o overlay do '{k.Product}' para este jogo, capture novamente e compare.",
                OverlayCategory.Monitoring => $"Teste: feche '{k.Product}' (ou aumente o intervalo de leitura), capture novamente e compare.",
                OverlayCategory.Wallpaper => $"Teste: pause '{k.Product}' durante o jogo e compare.",
                _ => $"Teste: feche '{k.Product}' e compare capturas antes/depois.",
            };
            return new OverlayFinding(k.Product, k.Category, h.Key.proc, h.Value.Distinct().Order().ToList(), k.ImpactPt, test, k.Confidence);
        }).OrderBy(f => f.Category).ThenBy(f => f.Product).ToList();
    }

    /// <summary>Lista overlays/gravadores em execução agora.</summary>
    public static IReadOnlyList<OverlayFinding> DetectRunning()
    {
        var list = new List<(string, int)>();
        foreach (var p in Process.GetProcesses())
        {
            try { list.Add((p.ProcessName, p.Id)); }
            catch (InvalidOperationException) { }
            finally { p.Dispose(); }
        }
        return Match(list);
    }

    /// <summary>Lê o estado de Game DVR / Game Bar (HKCU, somente leitura).</summary>
    public static GameDvrState ReadGameDvrState()
    {
        int? Dword(RegistryKey root, string path, string name)
        {
            try
            {
                using var k = root.OpenSubKey(path);
                return k?.GetValue(name) is int i ? i : null;
            }
            catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { return null; }
        }
        int? dvr = Dword(Registry.CurrentUser, @"System\GameConfigStore", "GameDVR_Enabled");
        int? cap = Dword(Registry.CurrentUser, @"Software\Microsoft\Windows\CurrentVersion\GameDVR", "AppCaptureEnabled");
        int? gm = Dword(Registry.CurrentUser, @"Software\Microsoft\GameBar", "AutoGameModeEnabled");
        int? pol = Dword(Registry.LocalMachine, @"SOFTWARE\Policies\Microsoft\Windows\GameDVR", "AllowGameDVR");
        return new GameDvrState(dvr, cap, gm, pol, Interpret(dvr, cap, gm, pol));
    }

    /// <summary>Interpretação (pt-BR) dos valores. Função pura.</summary>
    public static string Interpret(int? gameDvrEnabled, int? appCaptureEnabled, int? autoGameMode, int? policyAllow)
    {
        var parts = new List<string>();
        if (policyAllow == 0) parts.Add("Uma política do sistema desativa o Game DVR.");
        if (appCaptureEnabled == 0 || gameDvrEnabled == 0)
            parts.Add("Captura de jogos (Game DVR) desativada.");
        else if (appCaptureEnabled == 1 || gameDvrEnabled == 1)
            parts.Add("Captura de jogos (Game DVR) habilitada. Só há gravação contínua se 'Gravar o que aconteceu' (captura em segundo plano) estiver ligado em Configurações → Jogos → Capturas.");
        else parts.Add("Captura de jogos no padrão do Windows (valores ausentes).");
        parts.Add(autoGameMode switch
        {
            0 => "Modo de Jogo desativado.",
            1 => "Modo de Jogo ativado.",
            _ => "Modo de Jogo no padrão do Windows (ativado).",
        });
        return string.Join(" ", parts);
    }
}
