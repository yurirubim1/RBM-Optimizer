using Rbm.Core;

namespace Rbm.Drivers;

/// <summary>
/// Códigos CM_PROB_* (cfg.h) = ConfigManagerErrorCode do WMI = "Código NN" do Gerenciador de Dispositivos.
/// Descrições baseadas na documentação pública da Microsoft ("Device Manager error codes").
/// </summary>
public static class ProblemCodes
{
    public const int NotConfigured = 1;
    public const int FailedStart = 10;
    public const int NeedRestart = 14;
    public const int Disabled = 22;
    public const int FailedInstall = 28;
    public const int FailedAdd = 31;
    public const int DriverFailedLoad = 39;
    public const int FailedPostStart = 43;
    public const int Phantom = 45;
    public const int DriverBlocked = 48;
    public const int UnsignedDriver = 52;

    private const string Reinstall = "Reinstale o driver obtido da fonte oficial (fabricante do dispositivo/placa-mãe/notebook ou Windows Update). Se começou após uma atualização, use \"Reverter driver\".";
    private const string Restart = "Reinicie o computador.";
    private const string CheckHw = "Verifique conexões/cabos, teste em outra porta e atualize o BIOS/UEFI pelo site do fabricante da placa-mãe.";

    private static readonly Dictionary<int, ProblemCodeInfo> Table = new[]
    {
        P(1, "CM_PROB_NOT_CONFIGURED", "O dispositivo não está configurado corretamente (sem driver ou configuração).", "Instale o driver correto; use os IDs de hardware para identificar o fabricante.", Severity.Medium),
        P(2, "CM_PROB_DEVLOADER_FAILED", "Falha ao carregar o enumerador/carregador do dispositivo.", Reinstall, Severity.Medium),
        P(3, "CM_PROB_OUT_OF_MEMORY", "O driver pode estar corrompido ou o sistema está com pouca memória/recursos.", "Feche programas, reinicie e, se persistir, reinstale o driver.", Severity.Medium),
        P(4, "CM_PROB_ENTRY_IS_WRONG_TYPE", "Configuração do dispositivo inválida no registro.", Reinstall, Severity.Medium),
        P(5, "CM_PROB_LACKED_ARBITRATOR", "O driver solicitou um recurso que o Windows não sabe gerenciar.", Reinstall, Severity.Medium),
        P(6, "CM_PROB_BOOT_CONFIG_CONFLICT", "Conflito de recursos de inicialização com outro dispositivo.", CheckHw, Severity.Medium),
        P(7, "CM_PROB_FAILED_FILTER", "Falha ao carregar um driver de filtro.", Reinstall, Severity.Medium),
        P(8, "CM_PROB_DEVLOADER_NOT_FOUND", "Carregador do dispositivo não encontrado.", Reinstall, Severity.Medium),
        P(9, "CM_PROB_INVALID_DATA", "O Windows não consegue identificar o hardware (dados de configuração inválidos).", "Atualize o BIOS/UEFI e o driver pelo fabricante; pode indicar defeito de hardware.", Severity.Medium),
        P(10, "CM_PROB_FAILED_START", "O dispositivo não pode ser iniciado.", "Atualize/reinstale o driver pela fonte oficial; se começou após atualização, reverta o driver. " + CheckHw, Severity.High),
        P(11, "CM_PROB_LIAR", "O dispositivo falhou (comportamento inconsistente).", CheckHw, Severity.Medium),
        P(12, "CM_PROB_NORMAL_CONFLICT", "Não há recursos livres suficientes (conflito de IRQ/memória/E/S).", "Desative um dispositivo conflitante ou revise as configurações do BIOS/UEFI.", Severity.Medium),
        P(13, "CM_PROB_NOT_VERIFIED", "O Windows não conseguiu verificar os recursos do dispositivo.", Reinstall, Severity.Low),
        P(14, "CM_PROB_NEED_RESTART", "O dispositivo só funcionará após reiniciar o computador.", Restart, Severity.Low),
        P(15, "CM_PROB_REENUMERATION", "Conflito de recursos de re-enumeração.", Restart, Severity.Low),
        P(16, "CM_PROB_PARTIAL_LOG_CONF", "O Windows não identificou todos os recursos usados pelo dispositivo.", "Verifique a documentação do fabricante e atualize BIOS/driver.", Severity.Low),
        P(17, "CM_PROB_UNKNOWN_RESOURCE", "O INF do driver solicita um tipo de recurso desconhecido.", Reinstall, Severity.Medium),
        P(18, "CM_PROB_REINSTALL", "Os drivers deste dispositivo precisam ser reinstalados.", Reinstall, Severity.Medium),
        P(19, "CM_PROB_REGISTRY", "Informações de configuração no registro estão incompletas ou danificadas.", "Desinstale o dispositivo no Gerenciador de Dispositivos e procure alterações de hardware; reinstale o software do fabricante.", Severity.Medium),
        P(20, "CM_PROB_VXDLDR", "Falha do carregador VxD (legado).", Reinstall, Severity.Low),
        P(21, "CM_PROB_WILL_BE_REMOVED", "O Windows está removendo este dispositivo.", "Aguarde alguns segundos e atualize a lista; reinicie se persistir.", Severity.Low),
        P(22, "CM_PROB_DISABLED", "O dispositivo está desativado.", "Se não foi intencional, ative-o no Gerenciador de Dispositivos.", Severity.Info),
        P(23, "CM_PROB_DEVLOADER_NOT_READY", "Carregador do dispositivo não está pronto.", Restart, Severity.Low),
        P(24, "CM_PROB_DEVICE_NOT_THERE", "O dispositivo não está presente, não funciona corretamente ou não tem todos os drivers.", CheckHw + " Em seguida, reinstale o driver.", Severity.Medium),
        P(25, "CM_PROB_MOVED", "O Windows está configurando o dispositivo.", Restart, Severity.Low),
        P(26, "CM_PROB_TOO_EARLY", "O Windows ainda está configurando o dispositivo.", Restart, Severity.Low),
        P(27, "CM_PROB_NO_VALID_LOG_CONF", "O Windows não consegue especificar os recursos do dispositivo.", "Consulte o fabricante; pode exigir atualização de BIOS/UEFI.", Severity.Medium),
        P(28, "CM_PROB_FAILED_INSTALL", "Os drivers deste dispositivo não estão instalados.", "Identifique o dispositivo pelos IDs de hardware e instale o driver pela fonte oficial (fabricante ou Windows Update/Catálogo Microsoft).", Severity.Medium),
        P(29, "CM_PROB_HARDWARE_DISABLED", "O dispositivo está desativado pelo firmware (BIOS/UEFI).", "Ative o dispositivo no BIOS/UEFI, se desejado.", Severity.Info),
        P(30, "CM_PROB_CANT_SHARE_IRQ", "O dispositivo usa uma IRQ que outro dispositivo também usa.", "Revise configurações de IRQ no BIOS/UEFI.", Severity.Medium),
        P(31, "CM_PROB_FAILED_ADD", "O dispositivo não funciona: o Windows não carregou os drivers necessários.", Reinstall, Severity.High),
        P(32, "CM_PROB_DISABLED_SERVICE", "O serviço do driver está desativado (tipo de inicialização desabilitado).", "Reinstale o driver; não altere o tipo de inicialização de serviços de driver manualmente.", Severity.Medium),
        P(33, "CM_PROB_TRANSLATION_FAILED", "O Windows não determinou quais recursos o dispositivo requer.", CheckHw, Severity.Medium),
        P(34, "CM_PROB_NO_SOFTCONFIG", "O Windows não determina as configurações do dispositivo.", "Consulte a documentação do fabricante.", Severity.Medium),
        P(35, "CM_PROB_BIOS_TABLE", "O firmware (BIOS/UEFI) não contém informações suficientes para configurar o dispositivo.", "Atualize o BIOS/UEFI pelo site do fabricante da placa-mãe.", Severity.Medium),
        P(36, "CM_PROB_IRQ_TRANSLATION_FAILED", "Falha de tradução de IRQ (PCI ↔ ISA).", "Revise configurações de IRQ no BIOS/UEFI.", Severity.Medium),
        P(37, "CM_PROB_FAILED_DRIVER_ENTRY", "O Windows não consegue inicializar o driver.", Reinstall, Severity.High),
        P(38, "CM_PROB_DRIVER_FAILED_PRIOR_UNLOAD", "Uma instância anterior do driver ainda está na memória.", Restart, Severity.Low),
        P(39, "CM_PROB_DRIVER_FAILED_LOAD", "O Windows não consegue carregar o driver (pode estar corrompido ou ausente).", Reinstall, Severity.High),
        P(40, "CM_PROB_DRIVER_SERVICE_KEY_INVALID", "As informações do serviço do driver no registro estão ausentes ou incorretas.", Reinstall, Severity.Medium),
        P(41, "CM_PROB_LEGACY_SERVICE_NO_DEVICES", "O driver foi carregado, mas o dispositivo não foi encontrado.", "Se o hardware for não-PnP, reinstale o software do fabricante; caso contrário, verifique o hardware.", Severity.Medium),
        P(42, "CM_PROB_DUPLICATE_DEVICE", "Dispositivo duplicado detectado.", Restart, Severity.Medium),
        P(43, "CM_PROB_FAILED_POST_START", "O Windows parou o dispositivo porque ele relatou problemas.", "Reinstale/reverta o driver pela fonte oficial; para GPU, considere remoção limpa (DDU) em modo de segurança. " + CheckHw, Severity.High),
        P(44, "CM_PROB_HALTED", "Um aplicativo ou serviço desligou o dispositivo.", Restart, Severity.Medium),
        P(45, "CM_PROB_PHANTOM", "O dispositivo não está conectado no momento (registro de dispositivo antigo).", "Nenhuma ação necessária se o dispositivo foi removido intencionalmente.", Severity.Info),
        P(46, "CM_PROB_SYSTEM_SHUTDOWN", "Indisponível porque o Windows está sendo desligado.", "Nenhuma ação necessária.", Severity.Info),
        P(47, "CM_PROB_HELD_FOR_EJECT", "Preparado para remoção segura.", "Desconecte e reconecte o dispositivo, ou reinicie.", Severity.Info),
        P(48, "CM_PROB_DRIVER_BLOCKED", "O driver foi bloqueado por problemas conhecidos com o Windows.", "Obtenha uma versão compatível do driver no site do fabricante ou via Windows Update.", Severity.High),
        P(49, "CM_PROB_REGISTRY_TOO_LARGE", "O hive do sistema excedeu o limite de tamanho.", "Remova dispositivos antigos não usados (com cuidado) e reinicie.", Severity.High),
        P(50, "CM_PROB_SETPROPERTIES_FAILED", "O Windows não aplicou todas as propriedades do dispositivo.", Reinstall, Severity.Medium),
        P(51, "CM_PROB_WAITING_ON_DEPENDENCY", "O dispositivo aguarda outro dispositivo do qual depende.", "Resolva primeiro os problemas dos outros dispositivos (ex.: chipset/barramento).", Severity.Low),
        P(52, "CM_PROB_UNSIGNED_DRIVER", "O Windows não verificou a assinatura digital do driver.", "Instale um driver assinado obtido da fonte oficial. Não desative a verificação de assinatura.", Severity.High),
        P(53, "CM_PROB_USED_BY_DEBUGGER", "Dispositivo reservado pelo depurador do kernel.", "Desative a depuração de kernel (bcdedit /debug off) se não for intencional.", Severity.Low),
        P(54, "CM_PROB_DEVICE_RESET", "O dispositivo falhou e está sendo reiniciado.", "Aguarde; se repetir, atualize/reverta o driver e verifique o hardware.", Severity.Medium),
        P(55, "CM_PROB_CONSOLE_LOCKED", "Dispositivo indisponível enquanto a sessão está bloqueada.", "Nenhuma ação necessária.", Severity.Info),
        P(56, "CM_PROB_NEED_CLASS_CONFIG", "O Windows ainda está configurando a classe do dispositivo (rede).", Restart, Severity.Low),
        P(57, "CM_PROB_GUEST_ASSIGNMENT_FAILED", "Falha ao atribuir o dispositivo a uma máquina virtual convidada.", "Revise a configuração do hipervisor.", Severity.Low),
    }.ToDictionary(p => p.Code);

    private static ProblemCodeInfo P(int code, string sym, string desc, string action, Severity sev) => new(code, sym, desc, action, sev);

    /// <summary>Descrição do código (null para 0). Códigos desconhecidos retornam texto genérico explícito.</summary>
    public static ProblemCodeInfo? Describe(int code)
    {
        if (code == 0) return null;
        return Table.TryGetValue(code, out var p)
            ? p
            : new ProblemCodeInfo(code, $"CM_PROB_{code}", $"Código de problema {code} não catalogado pelo RBM Optimizer.",
                "Consulte \"Códigos de erro do Gerenciador de Dispositivos\" na documentação da Microsoft.", Severity.Medium);
    }

    public static IReadOnlyCollection<ProblemCodeInfo> All => Table.Values;
}
