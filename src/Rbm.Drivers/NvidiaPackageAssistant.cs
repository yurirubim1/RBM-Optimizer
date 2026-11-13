using System.Text.RegularExpressions;
using System.Xml;
using Rbm.Core;
using Rbm.Security;

namespace Rbm.Drivers;

public enum NvidiaComponentRole
{
    /// <summary>Necessário para a GPU funcionar.</summary>
    Essential,
    /// <summary>Recomendado para a maioria dos usuários.</summary>
    Recommended,
    /// <summary>Opcional: só necessário para o recurso que fornece.</summary>
    Optional,
    /// <summary>Usado pelo próprio instalador (não é "instalado" como recurso).</summary>
    Installer,
    /// <summary>Não catalogado pelo RBM Optimizer.</summary>
    Unknown,
}

/// <summary>Conhecimento público sobre um componente do pacote NVIDIA (documentação; pode variar por versão do pacote).</summary>
public sealed record NvidiaComponentInfo(string Pattern, string DisplayNamePt, NvidiaComponentRole Role, string DescriptionPt, string DependencyNotesPt);

/// <summary>Componente encontrado na pasta extraída, com versão lida do .nvi (se houver) e assinatura de catálogos.</summary>
public sealed record NvidiaComponent(
    string FolderName,
    NvidiaComponentInfo Info,
    string? NviVersion,
    long SizeBytes,
    int CatalogCount,
    int TrustedCatalogCount);

/// <summary>
/// Relatório do pacote NVIDIA. <c>SupportsInstalledGpu</c>: o ID da GPU instalada consta nos INFs do Display.Driver
/// (true/false), ou null se não verificado.
/// </summary>
public sealed record NvidiaPackageReport(
    string Folder,
    bool LooksLikeNvidiaPackage,
    string? PackageVersion,
    IReadOnlyList<NvidiaComponent> Components,
    bool? SupportsInstalledGpu,
    string? InstalledGpuShortId,
    IReadOnlyList<string> RecommendationsPt,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Assistente para pacotes de driver NVIDIA EXTRAÍDOS pelo usuário (ex.: com 7-Zip). Somente leitura: nunca altera, remove ou
/// reempacota arquivos (isso invalidaria a assinatura). Produz explicação dos componentes e recomenda a
/// "Instalação personalizada" do instalador oficial. Descrições baseadas em conhecimento público (documentação NVIDIA,
/// comportamento observado de ferramentas como NVCleanstall); a composição do pacote muda entre versões.
/// </summary>
public static partial class NvidiaPackageAssistant
{
    public static IReadOnlyList<NvidiaComponentInfo> KnownComponents { get; } = new NvidiaComponentInfo[]
    {
        new("Display.Driver", "Driver de vídeo", NvidiaComponentRole.Essential,
            "Driver gráfico (kernel + modo usuário: DirectX, Vulkan, OpenGL, OpenCL/CUDA). É o único componente indispensável.",
            "Base de todos os demais. No modelo DCH, o Painel de Controle NVIDIA é um app da Microsoft Store instalado/vinculado a partir deste driver."),
        new("Display.NvContainer", "Contêiner do driver (NVIDIA Display Container LS)", NvidiaComponentRole.Recommended,
            "Serviço que hospeda plugins do driver usados pelo Painel de Controle NVIDIA e por alguns recursos do sistema.",
            "Sem ele, o Painel de Controle NVIDIA pode não abrir ou perder funções. Relatos divergem sobre ser totalmente dispensável — mantenha, salvo motivo concreto."),
        new("NvContainer", "NVIDIA Container (runtime compartilhado)", NvidiaComponentRole.Optional,
            "Runtime usado por aplicativos NVIDIA (NVIDIA App/GeForce Experience, telemetria, sobreposição).",
            "Necessário se instalar NVIDIA App/GeForce Experience. Não é necessário para jogos funcionarem."),
        new("HDAudio", "Áudio HD (HDMI/DisplayPort)", NvidiaComponentRole.Optional,
            "Driver de áudio pela saída HDMI/DisplayPort da placa de vídeo.",
            "Necessário somente se o som sair pelo monitor/TV/receiver conectado à GPU."),
        new("PhysX", "PhysX System Software", NvidiaComponentRole.Recommended,
            "Bibliotecas de física usadas por alguns jogos (principalmente antigos).",
            "Jogos sem PhysX não são afetados; alguns jogos instalam a própria cópia. Instalação inofensiva."),
        new("NvApp", "NVIDIA App", NvidiaComponentRole.Optional,
            "Aplicativo de configurações, otimização de jogos, atualizações de driver, sobreposição e gravação.",
            "Depende de NvContainer e componentes relacionados (MessageBus, FrameViewSDK). Opcional."),
        new("NvApp.MessageBus", "NVIDIA App — barramento de mensagens", NvidiaComponentRole.Optional,
            "Comunicação interna entre componentes do NVIDIA App.", "Só necessário com o NVIDIA App."),
        new("GFExperience", "GeForce Experience (legado)", NvidiaComponentRole.Optional,
            "Aplicativo anterior ao NVIDIA App (otimização, ShadowPlay, atualizações).", "Opcional; substituído pelo NVIDIA App em pacotes recentes."),
        new("ShadowPlay", "ShadowPlay / Share (gravação)", NvidiaComponentRole.Optional,
            "Captura de vídeo e replay instantâneo.", "Só funciona com NVIDIA App/GeForce Experience."),
        new("FrameViewSDK", "FrameView SDK", NvidiaComponentRole.Optional,
            "Métricas de desempenho (FPS/latência) para a sobreposição do NVIDIA App.", "Só necessário para a sobreposição de desempenho."),
        new("NvTelemetry", "Telemetria NVIDIA", NvidiaComponentRole.Optional,
            "Coleta de dados de uso/diagnóstico.", "Pode ser desmarcado sem afetar jogos. (Em pacotes recentes a telemetria pode estar integrada a outros componentes.)"),
        new("NvBackend", "Backend do GeForce Experience", NvidiaComponentRole.Optional, "Serviço de suporte do GeForce Experience.", "Só com GeForce Experience."),
        new("NvVAD", "Dispositivo de áudio virtual NVIDIA", NvidiaComponentRole.Optional,
            "Áudio virtual para streaming/gravação (ShadowPlay/Broadcast).", "Só com recursos de gravação/streaming."),
        new("NvCamera", "Ansel / filtros de jogo", NvidiaComponentRole.Optional, "Captura de fotos em jogo e filtros (Freestyle).", "Só com NVIDIA App/GeForce Experience."),
        new("NvModuleTracker", "Rastreador de módulos", NvidiaComponentRole.Optional, "Componente auxiliar dos aplicativos NVIDIA.", "Só com NVIDIA App/GeForce Experience."),
        new("NvDLISR", "DLISR (super-resolução de captura)", NvidiaComponentRole.Optional, "Recurso de IA para captura/Ansel.", "Opcional."),
        new("ShieldWirelessController", "Controle SHIELD sem fio", NvidiaComponentRole.Optional, "Driver do controle NVIDIA SHIELD.", "Só se usar esse controle."),
        new("PPC", "Driver USB-C (VirtualLink)", NvidiaComponentRole.Optional,
            "Controlador USB Type-C presente em algumas placas RTX 20.", "Só se a placa tiver porta USB-C e você usá-la."),
        new("NVPCF", "Platform Controller Framework", NvidiaComponentRole.Recommended,
            "Gerenciamento de energia (Dynamic Boost) em notebooks.", "Relevante em notebooks; em desktops normalmente não se aplica."),
        new("Display.Optimus", "Optimus (notebooks)", NvidiaComponentRole.Recommended, "Alternância de GPU integrada/dedicada em notebooks.", "Não remova em notebooks com Optimus."),
        new("Display.Update", "Atualizador (legado)", NvidiaComponentRole.Optional, "Componente de atualização de versões antigas.", "Opcional."),
        new("Display.Nview", "nView Desktop Manager (legado)", NvidiaComponentRole.Optional, "Gerenciador de áreas de trabalho de versões antigas/Quadro.", "Opcional."),
        new("3DVision", "3D Vision (legado)", NvidiaComponentRole.Optional, "Suporte a óculos 3D; descontinuado.", "Opcional."),
        new("Update.Core", "Núcleo de atualização (GeForce Experience)", NvidiaComponentRole.Optional, "Atualizações do GeForce Experience.", "Só com GeForce Experience."),
        new("MSVCRT", "Runtimes Visual C++", NvidiaComponentRole.Installer, "Bibliotecas usadas por componentes NVIDIA.", "Gerenciado pelo instalador."),
        new("NVI2", "Motor do instalador NVIDIA", NvidiaComponentRole.Installer, "Executa a instalação.", "Necessário para instalar; não é um recurso."),
        new("NvContainer.MessageBus", "Barramento de mensagens do contêiner", NvidiaComponentRole.Optional, "Comunicação entre serviços NVIDIA.", "Acompanha NvContainer/NVIDIA App."),
    };

    private static readonly NvidiaComponentInfo UnknownInfo = new("*", "Componente não catalogado", NvidiaComponentRole.Unknown,
        "O RBM Optimizer não tem descrição para este componente.", "Na dúvida, mantenha o padrão do instalador oficial.");

    public static NvidiaComponentInfo Describe(string folderName) =>
        KnownComponents.FirstOrDefault(c => string.Equals(c.Pattern, folderName, StringComparison.OrdinalIgnoreCase))
        ?? KnownComponents.Where(c => folderName.StartsWith(c.Pattern + ".", StringComparison.OrdinalIgnoreCase))
                          .OrderByDescending(c => c.Pattern.Length).FirstOrDefault()
        ?? UnknownInfo;

    /// <summary>
    /// Analisa uma pasta de pacote NVIDIA já extraída. <paramref name="installedGpu"/> (opcional) é o dispositivo de vídeo NVIDIA
    /// do inventário: o ID PCI dele é procurado nos INFs do Display.Driver para confirmar compatibilidade (dado real).
    /// </summary>
    public static Task<NvidiaPackageReport> AnalyzeAsync(string folder, DeviceRecord? installedGpu = null, CancellationToken ct = default) =>
        Task.Run(() => Analyze(folder, installedGpu, ct), ct);

    public static NvidiaPackageReport Analyze(string folder, DeviceRecord? installedGpu = null, CancellationToken ct = default)
    {
        var warnings = new List<string>();
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder) || !Directory.Exists(folder))
            return new(folder, false, null, [], null, null, [], ["Pasta inválida ou inexistente."]);
        string root = Path.GetFullPath(folder);
        var dirs = Directory.GetDirectories(root).Select(Path.GetFileName).OfType<string>().OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToArray();
        bool hasDisplayDriver = dirs.Any(d => d.Equals("Display.Driver", StringComparison.OrdinalIgnoreCase));
        bool hasSetup = File.Exists(Path.Combine(root, "setup.exe")) || File.Exists(Path.Combine(root, "setup.cfg"));
        bool looksNvidia = hasDisplayDriver && hasSetup;
        if (!looksNvidia) warnings.Add("A pasta não parece um pacote NVIDIA extraído (esperado: setup.exe/setup.cfg e Display.Driver).");

        var components = new List<NvidiaComponent>();
        foreach (var name in dirs)
        {
            ct.ThrowIfCancellationRequested();
            string dir = Path.Combine(root, name);
            long size = 0; int cats = 0, trusted = 0;
            try
            {
                foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                {
                    size += new FileInfo(f).Length;
                    if (f.EndsWith(".cat", StringComparison.OrdinalIgnoreCase))
                    {
                        cats++;
                        if (SignatureVerifier.Verify(f).IsTrusted) trusted++;
                    }
                }
            }
            catch (Exception ex) { warnings.Add($"{name}: leitura parcial ({ex.Message})."); }
            components.Add(new NvidiaComponent(name, Describe(name), ReadNviVersion(dir), size, cats, trusted));
        }

        string? pkgVersion = components.FirstOrDefault(c => c.FolderName.Equals("Display.Driver", StringComparison.OrdinalIgnoreCase))?.NviVersion;
        bool? supports = null; string? shortId = null;
        if (installedGpu?.PrimaryHardwareId is { VendorId: "10DE", DeviceId: { } dev } hw && hasDisplayDriver)
        {
            shortId = hw.ShortId;
            supports = DisplayDriverListsDevice(Path.Combine(root, "Display.Driver"), dev, ct);
            if (supports == false) warnings.Add($"O ID da GPU instalada ({shortId}) não aparece nos INFs do Display.Driver: este pacote provavelmente não suporta sua placa (ex.: pacote de notebook/estação de trabalho).");
        }
        else if (installedGpu is not null)
            warnings.Add("A GPU informada não é NVIDIA (VEN_10DE) ou não tem ID de hardware legível; compatibilidade não verificada.");

        int untrusted = components.Sum(c => c.CatalogCount - c.TrustedCatalogCount);
        if (untrusted > 0) warnings.Add($"{untrusted} catálogo(s) sem assinatura confiável — o pacote pode ter sido alterado. Baixe novamente do site oficial.");

        var rec = new List<string>
        {
            "Use o instalador OFICIAL (setup.exe do próprio pacote) → \"Personalizada (Avançada)\" e marque somente os componentes desejados. O RBM Optimizer não modifica nem reempacota o pacote (a assinatura seria invalidada).",
            "Configuração mínima comum: Driver de vídeo + PhysX (+ Áudio HD se usar som pelo HDMI/DisplayPort). NVIDIA App/GeForce Experience e componentes relacionados são opcionais.",
            "\"Executar uma instalação limpa\" no modo personalizado restaura as configurações do driver; normalmente resolve resíduos sem precisar do DDU.",
            OfficialSources.NewestNotAlwaysBestPt,
        };
        if (components.Any(c => c.Info.Role == NvidiaComponentRole.Unknown))
            rec.Add("Há componentes não catalogados: na dúvida, mantenha-os como o instalador oficial sugerir.");
        return new NvidiaPackageReport(root, looksNvidia, pkgVersion, components, supports, shortId, rec, warnings);
    }

    /// <summary>Lê o atributo "version" do primeiro .nvi na raiz do componente (XML do instalador NVIDIA). Null se ausente.</summary>
    internal static string? ReadNviVersion(string componentDir)
    {
        try
        {
            foreach (var nvi in Directory.EnumerateFiles(componentDir, "*.nvi", SearchOption.TopDirectoryOnly).Take(3))
            {
                if (new FileInfo(nvi).Length > 4 * 1024 * 1024) continue;
                var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null };
                using var xr = XmlReader.Create(nvi, settings);
                while (xr.Read())
                    if (xr.NodeType == XmlNodeType.Element)
                    {
                        var v = xr.GetAttribute("version");
                        if (!string.IsNullOrWhiteSpace(v)) return v.Trim();
                        break;
                    }
            }
        }
        catch { }
        return null;
    }

    [GeneratedRegex(@"VEN_10DE&DEV_(?<d>[0-9A-F]{4})", RegexOptions.IgnoreCase)] private static partial Regex NvDevRx();

    /// <summary>True se algum INF do Display.Driver lista PCI\VEN_10DE&amp;DEV_xxxx; false se nenhum lista; null se não há INF legível.</summary>
    internal static bool? DisplayDriverListsDevice(string displayDriverDir, string deviceId, CancellationToken ct)
    {
        bool anyInf = false;
        foreach (var inf in Directory.EnumerateFiles(displayDriverDir, "*.inf", SearchOption.TopDirectoryOnly))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                if (new FileInfo(inf).Length > InfFile.MaxInfBytes) continue;
                using var sr = new StreamReader(inf, System.Text.Encoding.Latin1, detectEncodingFromByteOrderMarks: true);
                string text = sr.ReadToEnd();
                anyInf = true;
                foreach (Match m in NvDevRx().Matches(text))
                    if (string.Equals(m.Groups["d"].Value, deviceId, StringComparison.OrdinalIgnoreCase)) return true;
            }
            catch { }
        }
        return anyInf ? false : null;
    }
}

/// <summary>Orientação sobre remoção profunda de driver de vídeo com DDU. O RBM Optimizer NUNCA executa o DDU.</summary>
public sealed record DduGuidance(
    bool Advisable,
    IReadOnlyList<string> ReasonsPt,
    IReadOnlyList<string> StepsPt,
    IReadOnlyList<string> WarningsPt,
    string OfficialPage,
    string OfficialDownloadPage);

public static class DduAdvisor
{
    public const string OfficialPage = "https://www.wagnardsoft.com/";
    public const string OfficialDownloadPage = "https://www.wagnardsoft.com/DDU/download";

    public static IReadOnlyList<string> Steps { get; } = new[]
    {
        "Baixe ANTES o driver novo no site oficial do fabricante da GPU (NVIDIA/AMD/Intel) e o DDU somente no site oficial da Wagnardsoft.",
        "Crie um ponto de restauração e exporte seus drivers (backup do RBM Optimizer).",
        "Desconecte a internet (ou ative no DDU a opção que impede o Windows Update de instalar drivers de vídeo automaticamente).",
        "Reinicie no Modo de Segurança (Configurações → Sistema → Recuperação → Inicialização avançada → Solução de problemas → Opções avançadas → Configurações de inicialização → 4).",
        "Execute o DDU, selecione o tipo de dispositivo \"GPU\" e o fabricante, e use \"Limpar e reiniciar\".",
        "Após reiniciar, instale o driver baixado (no caso NVIDIA, preferencialmente \"Personalizada\") e só então reconecte a internet.",
    };

    public static IReadOnlyList<string> Warnings { get; } = new[]
    {
        "O RBM Optimizer nunca executa o DDU automaticamente nem remove drivers sozinho.",
        "Não é necessário usar DDU a cada atualização: a instalação limpa do instalador oficial resolve a maioria dos casos.",
        "Se usar BitLocker, tenha a chave de recuperação em mãos antes de entrar no Modo de Segurança.",
        "Use somente o site oficial da Wagnardsoft; versões em sites de terceiros podem estar alteradas.",
    };

    /// <summary>
    /// Avalia, com base no inventário real, se a remoção profunda é aconselhável: GPU com código de problema (43, 31, 10...),
    /// troca de fabricante de GPU (drivers de vídeo de outro fabricante ainda instalados em dispositivos não presentes),
    /// ou múltiplos pacotes de vídeo antigos. <paramref name="userReportsPersistentIssues"/>: o usuário relata falhas persistentes
    /// de instalação/tela preta mesmo após instalação limpa.
    /// </summary>
    public static DduGuidance Evaluate(DeviceInventory inventory, DriverStoreInventory? store = null, bool userReportsPersistentIssues = false)
    {
        var reasons = new List<string>();
        var displays = inventory.Devices.Where(d => string.Equals(d.ClassName, "Display", StringComparison.OrdinalIgnoreCase)).ToArray();
        foreach (var d in displays.Where(d => d.HasProblem && !d.IsDisabled))
            reasons.Add($"{d.Name}: código {d.ProblemCode} — {d.Problem?.DescriptionPt}");

        var presentVendors = displays.Where(d => d.IsPresent).Select(d => d.PrimaryHardwareId?.VendorId).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
        var ghostVendors = displays.Where(d => !d.IsPresent && d.Driver is { IsThirdParty: true }).Select(d => d.PrimaryHardwareId?.VendorId).OfType<string>()
            .Where(v => !presentVendors.Contains(v) && v is "10DE" or "1002" or "8086").Distinct().ToArray();
        foreach (var v in ghostVendors)
            reasons.Add($"Há driver de vídeo de outro fabricante ({VendorDatabase.LookupPci(v)}) registrado para uma GPU que não está mais presente — típico após troca de placa.");

        if (store is not null)
        {
            var displayGuid = new Guid("4d36e968-e325-11ce-bfc1-08002be10318");
            int displayPkgs = store.Packages.Count(p => p.ClassGuid == displayGuid && !p.IsExtension);
            if (displayPkgs >= 3) reasons.Add($"{displayPkgs} pacotes de driver de vídeo no Driver Store (versões antigas acumuladas).");
        }
        if (userReportsPersistentIssues) reasons.Add("Problemas persistentes relatados mesmo após instalação limpa.");

        bool advisable = reasons.Count > 0 && (displays.Any(d => d.HasProblem && !d.IsDisabled) || ghostVendors.Length > 0 || userReportsPersistentIssues);
        if (reasons.Count == 0) reasons.Add("Nenhum indício nos dados coletados: remoção profunda não é necessária.");
        else if (!advisable) reasons.Add("Indício fraco: prefira a \"instalação limpa\" do instalador oficial antes de considerar o DDU.");
        return new DduGuidance(advisable, reasons, Steps, Warnings, OfficialPage, OfficialDownloadPage);
    }
}
