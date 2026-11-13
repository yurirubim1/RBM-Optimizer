namespace Rbm.UI.Infrastructure;

/// <summary>
/// Localização simples: pt-BR (padrão) e inglês. Cobre navegação, cabeçalhos e textos comuns.
/// Conteúdo técnico (catálogo, diagnósticos) usa o campo bilíngue quando disponível.
/// </summary>
public static class Loc
{
    public static string Language { get; set; } = "pt-BR";
    public static bool IsEnglish => Language.StartsWith("en", StringComparison.OrdinalIgnoreCase);

    public static string T(string key) =>
        (IsEnglish ? En : Pt).TryGetValue(key, out var v) ? v : Pt.TryGetValue(key, out var p) ? p : key;

    private static readonly Dictionary<string, string> Pt = new()
    {
        // Grupos da barra lateral
        ["grp.home"] = "INÍCIO", ["grp.optimize"] = "OTIMIZAR", ["grp.clean"] = "LIMPAR", ["grp.mypc"] = "MEU PC",
        ["grp.tools"] = "FERRAMENTAS", ["grp.system"] = "SISTEMA",
        // Páginas
        ["nav.dashboard"] = "Painel", ["nav.diagnostics"] = "Saúde do PC", ["nav.recommend"] = "Otimizar em 1 clique",
        ["sub.recommend"] = "Escolha Leve, Média ou Pesada — o RBM faz o resto e tudo pode ser desfeito",
        ["nav.optimizations"] = "Otimizações", ["nav.profiles"] = "Perfis prontos", ["nav.gaming"] = "Jogos e FPS",
        ["nav.cleanup"] = "Limpeza", ["nav.space"] = "Espaço em disco",
        ["nav.cpu"] = "Processador", ["nav.gpu"] = "Placa de vídeo", ["nav.memory"] = "Memória RAM", ["nav.storage"] = "Discos",
        ["nav.power"] = "Energia e temperatura",
        ["nav.monitoring"] = "Monitor em tempo real", ["nav.processes"] = "Processos", ["nav.startup"] = "Inicialização e serviços",
        ["nav.drivers"] = "Drivers", ["nav.network"] = "Rede e internet", ["nav.latency"] = "Latência",
        ["nav.benchmarks"] = "Testes de desempenho",
        ["nav.history"] = "Histórico e desfazer", ["nav.settings"] = "Configurações",
        // Subtítulos (o "para quê")
        ["sub.dashboard"] = "Como seu computador está agora e o que vale a pena fazer",
        ["sub.diagnostics"] = "Problemas encontrados, explicados em linguagem simples, com o que fazer",
        ["sub.optimizations"] = "Ajustes do Windows com explicação, risco e opção de desfazer",
        ["sub.profiles"] = "Conjuntos de ajustes prontos para cada uso — veja tudo antes de aplicar",
        ["sub.gaming"] = "Seus jogos, medição de FPS e travadas",
        ["sub.cleanup"] = "Libere espaço apagando apenas arquivos temporários seguros",
        ["sub.space"] = "Veja o que está ocupando o disco — clique numa pasta para entrar nela",
        ["sub.cpu"] = "Uso, velocidade e núcleos do processador",
        ["sub.gpu"] = "Uso, memória de vídeo e temperatura da placa de vídeo",
        ["sub.memory"] = "Quanto de RAM está em uso e quem está usando",
        ["sub.storage"] = "Saúde, espaço e velocidade dos discos",
        ["sub.power"] = "Plano de energia e temperaturas",
        ["sub.monitoring"] = "Gráficos detalhados de todos os componentes",
        ["sub.processes"] = "Programas em execução, prioridade e regras automáticas",
        ["sub.startup"] = "O que inicia com o Windows — desative sem apagar",
        ["sub.drivers"] = "Drivers instalados, problemas, backup e fontes oficiais",
        ["sub.network"] = "Conexão, ping, oscilação (jitter) e DNS",
        ["sub.latency"] = "Atrasos causados por drivers e dispositivos de entrada",
        ["sub.benchmarks"] = "Meça o desempenho antes e depois de mudar algo",
        ["sub.history"] = "Tudo o que foi alterado — desfaça quando quiser",
        ["sub.settings"] = "Preferências do aplicativo",
        // Status
        ["status.monitoring"] = "Monitorando", ["status.paused"] = "Monitoramento pausado",
        ["status.agentOn"] = "Permissão de administrador ativa", ["status.agentOff"] = "Sem permissão de administrador",
        ["nav.advancedToggle"] = "Ferramentas avançadas",
    };

    private static readonly Dictionary<string, string> En = new()
    {
        ["grp.home"] = "HOME", ["grp.optimize"] = "OPTIMIZE", ["grp.clean"] = "CLEAN UP", ["grp.mypc"] = "MY PC",
        ["grp.tools"] = "TOOLS", ["grp.system"] = "SYSTEM",
        ["nav.dashboard"] = "Overview", ["nav.diagnostics"] = "PC Health", ["nav.recommend"] = "One-click Optimize",
        ["sub.recommend"] = "Pick Light, Medium or Heavy — RBM does the rest and everything can be undone",
        ["nav.optimizations"] = "Optimizations", ["nav.profiles"] = "Presets", ["nav.gaming"] = "Games & FPS",
        ["nav.cleanup"] = "Cleanup", ["nav.space"] = "Disk Space",
        ["nav.cpu"] = "Processor", ["nav.gpu"] = "Graphics", ["nav.memory"] = "Memory", ["nav.storage"] = "Drives",
        ["nav.power"] = "Power & Temperature",
        ["nav.monitoring"] = "Live Monitor", ["nav.processes"] = "Processes", ["nav.startup"] = "Startup & Services",
        ["nav.drivers"] = "Drivers", ["nav.network"] = "Network", ["nav.latency"] = "Latency",
        ["nav.benchmarks"] = "Benchmarks",
        ["nav.history"] = "History & Undo", ["nav.settings"] = "Settings",
        ["sub.dashboard"] = "How your PC is doing and what is worth doing",
        ["sub.diagnostics"] = "Problems found, explained plainly, with what to do",
        ["sub.optimizations"] = "Windows tweaks with explanation, risk and undo",
        ["sub.profiles"] = "Ready-made tweak sets — review everything before applying",
        ["sub.gaming"] = "Your games, FPS measurement and stutter",
        ["sub.cleanup"] = "Free up space by removing only safe temporary files",
        ["sub.space"] = "See what is using your disk — click a folder to open it",
        ["sub.cpu"] = "Processor usage, speed and cores",
        ["sub.gpu"] = "Graphics usage, video memory and temperature",
        ["sub.memory"] = "How much RAM is in use and by whom",
        ["sub.storage"] = "Drive health, space and speed",
        ["sub.power"] = "Power plan and temperatures",
        ["sub.monitoring"] = "Detailed charts for every component",
        ["sub.processes"] = "Running programs, priority and automatic rules",
        ["sub.startup"] = "What starts with Windows — disable without deleting",
        ["sub.drivers"] = "Installed drivers, problems, backup and official sources",
        ["sub.network"] = "Connection, ping, jitter and DNS",
        ["sub.latency"] = "Delays caused by drivers and input devices",
        ["sub.benchmarks"] = "Measure performance before and after changes",
        ["sub.history"] = "Everything that was changed — undo anytime",
        ["sub.settings"] = "Application preferences",
        ["status.monitoring"] = "Monitoring", ["status.paused"] = "Monitoring paused",
        ["status.agentOn"] = "Administrator access on", ["status.agentOff"] = "No administrator access",
        ["nav.advancedToggle"] = "Advanced tools",
    };
}
