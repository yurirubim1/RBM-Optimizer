using System.Globalization;
using System.Text.RegularExpressions;
using Rbm.Core;

namespace Rbm.Network.Adapters;

/// <summary>
/// Catálogo factual das palavras-chave avançadas mais relevantes. As descrições explicam o que o recurso faz;
/// nenhuma delas promete redução de ping — o efeito real depende do driver, da carga e do caminho de rede.
/// </summary>
public static class AdvancedKeywords
{
    public const string Rss = "*RSS";
    public const string NumRssQueues = "*NumRssQueues";
    public const string RscIPv4 = "*RscIPv4";
    public const string RscIPv6 = "*RscIPv6";
    public const string InterruptModeration = "*InterruptModeration";
    public const string FlowControl = "*FlowControl";
    public const string Eee = "*EEE";
    public const string SpeedDuplex = "*SpeedDuplex";
    public const string ReceiveBuffers = "*ReceiveBuffers";
    public const string TransmitBuffers = "*TransmitBuffers";
    public const string LsoV2IPv4 = "*LsoV2IPv4";
    public const string LsoV2IPv6 = "*LsoV2IPv6";
    public const string JumboPacket = "*JumboPacket";

    private static readonly Dictionary<string, AdvancedKeywordInfo> Map = new AdvancedKeywordInfo[]
    {
        new(Rss, "Desempenho", "Receive Side Scaling: distribui o processamento de recepção entre vários núcleos da CPU.", false),
        new(NumRssQueues, "Desempenho", "Número máximo de filas RSS (núcleos usados para recepção).", false),
        new(RscIPv4, "Offload", "Receive Segment Coalescing (IPv4): agrupa segmentos TCP recebidos para reduzir uso de CPU; pode aumentar levemente a latência de pacotes individuais.", false),
        new(RscIPv6, "Offload", "Receive Segment Coalescing (IPv6): igual ao IPv4, para tráfego IPv6.", false),
        new(InterruptModeration, "Desempenho", "Moderação de interrupções: agrupa interrupções para reduzir uso de CPU; desativar reduz a espera por pacote em microssegundos, ao custo de mais interrupções.", false),
        new(FlowControl, "Enlace", "Controle de fluxo Ethernet (quadros PAUSE 802.3x) entre a placa e o switch/roteador.", false),
        new(Eee, "Energia", "Energy-Efficient Ethernet (802.3az): coloca o enlace em baixo consumo quando ocioso; acordar o enlace leva microssegundos.", true),
        new("EnableGreenEthernet", "Energia", "Green Ethernet (Realtek): reduz potência conforme o comprimento do cabo/ociosidade.", true),
        new("AdvancedEEE", "Energia", "EEE avançado (Realtek): variante proprietária de economia de energia do enlace.", true),
        new("GigaLite", "Energia", "Gigabit Lite (Realtek): modo de economia que pode limitar o enlace a 500 Mbps.", true),
        new("PowerSavingMode", "Energia", "Modo de economia de energia do fabricante.", true),
        new("AutoDisableGigabit", "Energia", "Desativa o gigabit automaticamente (ex.: na bateria), forçando 100 Mbps.", true),
        new("*SelectiveSuspend", "Energia", "Suspensão seletiva (dispositivos USB): permite suspender o adaptador quando ocioso.", true),
        new("ULPMode", "Energia", "Ultra Low Power (Intel): economia de energia quando o cabo está desconectado.", true),
        new(SpeedDuplex, "Enlace", "Velocidade e duplex do enlace. \"Auto\" é o recomendado; forçar um valor pode causar incompatibilidade de duplex.", false),
        new(ReceiveBuffers, "Buffers", "Buffers de recepção: memória para pacotes recebidos aguardando processamento.", false),
        new(TransmitBuffers, "Buffers", "Buffers de transmissão: memória para pacotes aguardando envio.", false),
        new(LsoV2IPv4, "Offload", "Large Send Offload v2 (IPv4): a placa segmenta envios TCP grandes, poupando CPU.", false),
        new(LsoV2IPv6, "Offload", "Large Send Offload v2 (IPv6).", false),
        new(JumboPacket, "Enlace", "Tamanho máximo do quadro (Jumbo Frames). Só funciona se toda a rede local suportar o mesmo tamanho.", false),
        new("*PMARPOffload", "Energia", "Responde ARP pela placa durante suspensão.", false),
        new("*PMNSOffload", "Energia", "Responde Neighbor Solicitation (IPv6) pela placa durante suspensão.", false),
        new("*WakeOnMagicPacket", "Energia", "Permite ligar/despertar o PC por Magic Packet.", false),
        new("*WakeOnPattern", "Energia", "Permite despertar o PC por padrões de tráfego.", false),
        new("*PriorityVLANTag", "Enlace", "Marcação 802.1p/802.1Q (prioridade e VLAN).", false),
    }.ToDictionary(k => k.Keyword, StringComparer.OrdinalIgnoreCase);

    public static AdvancedKeywordInfo? Lookup(string keyword) => Map.TryGetValue(keyword, out var k) ? k : null;

    /// <summary>Palavras-chave cuja alteração é bloqueada mesmo com valor válido (identidade/VLAN: podem cortar o acesso).</summary>
    public static bool IsWriteBlocked(string keyword)
    {
        var k = keyword.TrimStart('*');
        return k.Equals("NetworkAddress", StringComparison.OrdinalIgnoreCase)
            || k.Contains("VlanId", StringComparison.OrdinalIgnoreCase)
            || k.Equals("VLANID", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Velocidade máxima (bps) declarada pelos valores válidos de *SpeedDuplex. Usa os códigos padronizados NDIS
    /// e, como reforço, o texto exibido (ex.: "1.0 Gbps Full Duplex", "2,5 Gbps"). Null se não houver dados.
    /// </summary>
    public static long? MaxSpeedFromSpeedDuplex(AdvancedProperty? speedDuplex)
    {
        if (speedDuplex is null) return null;
        long max = 0;
        foreach (var opt in speedDuplex.ValidOptions)
        {
            long fromText = ParseSpeedText(opt.DisplayValue);
            long fromCode = int.TryParse(opt.RegistryValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) ? SpeedFromNdisCode(code) : 0;
            max = Math.Max(max, Math.Max(fromText, fromCode));
        }
        return max > 0 ? max : null;
    }

    /// <summary>Códigos padronizados NDIS de *SpeedDuplex (documentados) → bps.</summary>
    public static long SpeedFromNdisCode(int code) => code switch
    {
        1 or 2 => 10_000_000,
        3 or 4 => 100_000_000,
        5 or 6 => 1_000_000_000,
        7 => 10_000_000_000,
        8 => 20_000_000_000,
        9 => 40_000_000_000,
        10 => 100_000_000_000,
        _ => 0,
    };

    private static readonly Regex SpeedRx = new(@"(\d+(?:[.,]\d+)?)\s*(G|M)(?:bps|b/s|bit|\b)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static long ParseSpeedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return 0;
        long max = 0;
        foreach (Match m in SpeedRx.Matches(text))
        {
            if (!double.TryParse(m.Groups[1].Value.Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) continue;
            double mult = char.ToUpperInvariant(m.Groups[2].Value[0]) == 'G' ? 1e9 : 1e6;
            max = Math.Max(max, (long)(v * mult));
        }
        return max;
    }
}

/// <summary>Validação estrita (pura) de pedidos de alteração de propriedades avançadas.</summary>
public static class AdvancedPropertyValidator
{
    private static readonly Regex KeywordRx = new(@"^\*?[A-Za-z0-9_]{1,64}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static readonly Regex ValueRx = new(@"^-?[A-Za-z0-9_.]{1,32}$", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Valida o formato do pedido (antes de consultar o driver).</summary>
    public static OperationResult ValidateShape(AdvancedPropertySetRequest? req)
    {
        if (req is null) return OperationResult.Fail("Pedido vazio.", "invalid_request");
        if (!Guid.TryParse(req.InterfaceGuid, out _)) return OperationResult.Fail("GUID de interface inválido.", "invalid_guid");
        if (string.IsNullOrEmpty(req.RegistryKeyword) || !KeywordRx.IsMatch(req.RegistryKeyword))
            return OperationResult.Fail("Palavra-chave inválida.", "invalid_keyword");
        if (string.IsNullOrEmpty(req.NewRegistryValue) || !ValueRx.IsMatch(req.NewRegistryValue))
            return OperationResult.Fail("Valor inválido (formato).", "invalid_value");
        if (req.ExpectedCurrentValue is not null && !ValueRx.IsMatch(req.ExpectedCurrentValue))
            return OperationResult.Fail("Valor esperado inválido (formato).", "invalid_value");
        if (AdvancedKeywords.IsWriteBlocked(req.RegistryKeyword))
            return OperationResult.Fail("Esta propriedade não pode ser alterada pelo RBM Optimizer (endereço de rede/VLAN).", "blocked_keyword");
        return OperationResult.Ok();
    }

    /// <summary>Valida o novo valor contra os valores aceitos que o próprio driver declara.</summary>
    public static OperationResult ValidateValue(AdvancedProperty prop, string newValue)
    {
        if (prop.ValueKind != AdvancedValueKind.FreeText && prop.DefaultRegistryValue is { Length: > 0 } def &&
            string.Equals(def, newValue, StringComparison.Ordinal))
            return OperationResult.Ok("Valor padrão do driver.");

        switch (prop.ValueKind)
        {
            case AdvancedValueKind.Enumeration:
                if (prop.ValidOptions.Any(o => string.Equals(o.RegistryValue, newValue, StringComparison.Ordinal)))
                    return OperationResult.Ok();
                return OperationResult.Fail(
                    $"Valor \"{newValue}\" não está entre os aceitos pelo driver: {string.Join(", ", prop.ValidOptions.Select(o => $"{o.RegistryValue} ({o.DisplayValue})"))}.",
                    "value_not_allowed");

            case AdvancedValueKind.Numeric when prop.Range is { } r:
                int radix = r.Base == 16 ? 16 : 10;
                long v;
                try { v = Convert.ToInt64(newValue, radix); }
                catch { return OperationResult.Fail($"Valor numérico inválido para base {radix}.", "value_not_allowed"); }
                if (v < r.Min || v > r.Max)
                    return OperationResult.Fail($"Valor {v} fora da faixa do driver ({r.Min}–{r.Max}).", "value_out_of_range");
                if (r.Step > 1 && (v - r.Min) % r.Step != 0)
                    return OperationResult.Fail($"Valor {v} não respeita o passo {r.Step} a partir de {r.Min}.", "value_out_of_range");
                return OperationResult.Ok();

            default:
                return OperationResult.Fail("Propriedade de texto livre ou sem valores declarados pelo driver: alteração não permitida.", "free_text_blocked");
        }
    }
}
