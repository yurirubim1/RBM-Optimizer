# Rbm.Network — Diagnóstico de rede (RBM Optimizer)

Biblioteca (sem UI) de diagnóstico de rede para Windows 10/11 x64. Tudo é **medição real**: quando um dado não está
disponível, o resultado traz o motivo explícito (nunca um valor estimado). Toda operação lenta é `async` com
`CancellationToken`, os resultados são `record`s imutáveis e as APIs podem ser chamadas de threads de fundo.

> Este módulo **não promete redução de ping** por ajustes de registro/driver. Ele mede, localiza onde está o problema
> (rede local, provedor, internet, servidor) e apresenta fatos.

## Funcionalidades

| Área | O que faz | Fonte de dados |
|---|---|---|
| Adaptadores | Lista adaptadores ativos e inativos: nome, descrição, tipo (Ethernet/Wi-Fi/virtual…), estado, velocidade negociada, duplex, MAC, IPv4/IPv6, gateways, DNS, DHCP, MTU, driver (versão/data/provedor/arquivo/INF/assinatura), contadores de erro/descarte | `MSFT_NetAdapter`, `MSFT_NetAdapterStatisticsSettingData` (root\StandardCimv2), `System.Net.NetworkInformation`, `Win32_PnPSignedDriver` |
| Wi-Fi | SSID, BSSID, qualidade do sinal (%), RSSI (dBm), canal, frequência/banda, PHY (802.11n/ac/ax/be), taxas RX/TX, autenticação/cifra | Native Wifi API (`WlanQueryInterface`, `WlanGetNetworkBssList`); fallback `netsh wlan show interfaces` (parser pt-BR/inglês, tolerante a acentos corrompidos) |
| Propriedades avançadas (leitura) | Todas as propriedades do driver com nome, valor atual, valores válidos (lista ou faixa numérica), padrão; resumo de RSS, RSC IPv4/IPv6, moderação de interrupção, controle de fluxo, EEE/Green Ethernet, LSO, buffers, velocidade/duplex; gerenciamento de energia ("permitir que o computador desligue este dispositivo") | `MSFT_NetAdapterAdvancedPropertySettingData`, `…RssSettingData`, `…RscSettingData`, `…PowerManagementSettingData` (com fallback para `PnPCapabilities` no registro) |
| Propriedades avançadas (escrita, elevada) | Altera **uma** propriedade para um valor **declarado válido pelo driver**, registra o valor anterior (rollback) e confirma por releitura | CIM `ModifyInstance` (o mesmo que `Set-NetAdapterAdvancedProperty`), sem PowerShell |
| Latência | ICMP (Ping) e TCP connect para vários alvos em paralelo; min/média/mediana/máx, P95/P99, desvio-padrão, jitter (média de \|Δ\| e RFC 3550), perda %, timeouts, maior sequência de perdas; modo finito ou contínuo (streaming) | `System.Net.NetworkInformation.Ping`, `Socket.ConnectAsync` |
| Alvos padrão | Gateway padrão, primeiro salto do provedor (descoberto por traceroute curto), 1.1.1.1, 8.8.8.8, 9.9.9.9 e alvos do usuário (ex.: servidores de jogos, via ICMP ou TCP) | `GetBestInterface` (iphlpapi) |
| Caminho | Traceroute (TTL crescente, até 30 saltos), depois rodadas de medição por salto no estilo mtr; segmentação **rede local / provedor / internet / destino** com latência incremental, jitter e perda por segmento; interpretação de priorização de ICMP | Ping com TTL |
| DNS | Tempo de resolução por servidor (configurados + públicos + personalizados) com pacotes DNS montados manualmente (UDP/53, ID aleatório, registro A), primeira consulta e consulta repetida (cache) separadas; resolvedor do sistema medido à parte | Socket UDP, `Dns.GetHostAddressesAsync` |
| Diagnóstico | Achados estruturados (`Rbm.Core.Diagnostics.Finding`): título, evidências, causa provável, gravidade, confiança, recomendação em pt-BR | Regras puras sobre os dados acima |

## API pública (pontos de entrada)

```csharp
// Adaptadores (sem elevação)
Task<NetworkInventory> NetworkAdapterInventory.EnumerateAsync(AdapterQueryOptions? options = null, CancellationToken ct = default);
Task<IReadOnlyDictionary<string, AdapterCounters>> NetworkAdapterInventory.ReadCountersAsync(CancellationToken ct = default);
AdapterCounterDelta AdapterCounterDelta.Compute(AdapterCounters before, AdapterCounters after);
Task<WifiQueryResult> WifiInspector.QueryAllAsync(CancellationToken ct = default);

// Propriedades avançadas
Task<IReadOnlyList<AdapterAdvancedSettings>> AdvancedPropertyService.ReadAllAsync(CancellationToken ct = default);
Task<AdapterAdvancedSettings?> AdvancedPropertyService.ReadAsync(string interfaceGuid, CancellationToken ct = default);
Task<AdvancedPropertySetResponse> AdvancedPropertyService.SetAsync(AdvancedPropertySetRequest req, CancellationToken ct = default); // ELEVADA
OperationResult AdvancedPropertyValidator.ValidateShape(AdvancedPropertySetRequest? req);
OperationResult AdvancedPropertyValidator.ValidateValue(AdvancedProperty prop, string newValue);

// Latência
Task<DefaultTargetsResult> DefaultTargets.DiscoverAsync(IEnumerable<LatencyTarget>? custom = null, bool discoverIspHop = true, CancellationToken ct = default);
Task<LatencyTestResult> LatencyTester.RunAsync(LatencyTestOptions o, IProgress<LatencySample>? progress = null, CancellationToken ct = default);
IAsyncEnumerable<LatencySample> LatencyTester.StreamAsync(LatencyTestOptions o, CancellationToken ct = default); // Count = null → contínuo
new LatencyAccumulator(targets, maxSamplesPerTarget).Add(sample) / .Snapshot();                          // estatísticas ao vivo
IReadOnlyList<LatencyHistoryRecord> LatencyHistoryRecord.From(LatencyTestResult r, string? adapter, string? kind); // para persistir

// Caminho
Task<TracerouteResult> PathAnalyzer.TraceAsync(string host, TracerouteOptions? o = null, IProgress<TraceHop>? p = null, CancellationToken ct = default);
Task<PathAnalysis> PathAnalyzer.AnalyzeAsync(string host, PathAnalysisOptions? o = null, IProgress<PathAnalysis>? p = null, CancellationToken ct = default);
PathAnalysis PathInterpreter.Interpret(...);  // puro
IReadOnlyList<PathSegment> HopClassifier.Classify(...); AddressScope HopClassifier.ScopeOf(string ip);

// DNS
Task<DnsBenchmarkResult> DnsBenchmark.RunAsync(DnsBenchmarkOptions? o = null, IProgress<DnsQueryResult>? p = null, CancellationToken ct = default);
byte[] DnsPacket.BuildQuery(ushort id, string name, ushort qtype = DnsType.A, bool rd = true);
bool DnsPacket.TryParse(ReadOnlySpan<byte> data, out DnsMessage? msg, out string? error);

// Diagnóstico (puro)
IReadOnlyList<Finding> NetworkDiagnostics.Evaluate(NetworkDiagnosticInput input);

// Agente (elevado)
Task<object?> NetworkAgentHandler.HandleAsync(string op, JsonElement? payload, CancellationToken ct = default);
```

### Exemplo: teste contínuo com estatísticas ao vivo

```csharp
var targets = await DefaultTargets.DiscoverAsync(new[] { new LatencyTarget("meu.servidor.de.jogo", "Servidor", Method: ProbeMethod.Tcp, TcpPort: 443) }, ct: ct);
var opts = new LatencyTestOptions { Targets = targets.Targets, Count = null, Interval = TimeSpan.FromSeconds(1) };
var acc = new LatencyAccumulator(opts.Targets);
await foreach (var s in LatencyTester.StreamAsync(opts, ct)) { acc.Add(s); /* UI: acc.Snapshot() */ }
```

## Regras de diagnóstico

| RuleId | Dispara quando | Gravidade |
|---|---|---|
| `net.wifi.weak-signal` | Qualidade < 40% ou RSSI < −72 dBm (muito fraco: < 25% / < −80 dBm) | Média/Alta |
| `net.link.below-capability` | Ethernet física negociada abaixo da capacidade (capacidade vinda dos valores válidos de `*SpeedDuplex`; fallback: descrição "GbE/Gigabit" com confiança média) | Média (≤100 Mbps em placa gigabit) / Baixa |
| `net.loss.local` | Perda ≥ 1% no gateway (≥ 10 amostras) | Média / Alta (≥ 5%) |
| `net.loss.remote-only` | Gateway limpo, perda em ≥ 2 alvos remotos (evita confundir com limitação de ICMP de um único servidor) | Média/Alta |
| `net.jitter.lan` | Jitter até o gateway ≥ 5 ms ou P95 − mediana ≥ 15 ms | Média (Wi-Fi) / Baixa (cabo) |
| `net.power.saving-desktop` | Desktop com "permitir desligar o dispositivo", EEE, Green Ethernet, GigaLite etc. ativos. Impacto marcado como **desconhecido**; recomenda medir antes/depois | Baixa |
| `net.errors.increasing` | Erros/descartes aumentaram entre duas leituras de contadores | Alta/Média (erros) / Baixa (só descartes) |
| `net.driver.age` / `net.driver.unsigned` | Data do driver ≥ 3 anos (informativo) / driver não assinado | Info / Média |
| `net.path.real-loss` | O teste de caminho encontrou perda real (persistente) e o segmento de origem | Média/Alta |

Os limiares ficam em `NetworkDiagnosticThresholds` (ajustáveis).

## Interpretação do caminho (priorização de ICMP)

Roteadores tratam respostas ICMP "TTL excedido" no plano de controle, com baixa prioridade e limite de taxa. Por isso:

- **Perda só em um salto intermediário, que não aparece nos saltos seguintes** → `IcmpRateLimitedLoss`: o roteador
  economiza CPU. **Não é perda real** e não afeta o tráfego que passa por ele.
- **Perda que começa em um salto e persiste até o destino** → `RealLossOrigin` (primeiro salto) e `RealLossPropagated`
  (seguintes). O segmento do salto de origem diz onde está o problema.
- **Atraso só em um salto** (≥ 15 ms e ≥ 1,3× acima dos saltos seguintes) → `ControlPlaneDelay` (resposta lenta do roteador).
- **Salto sem resposta, mas os seguintes respondem** → `NoIcmpResponse` (comum, sem problema).

Segmentação (heurística, sem consulta de ASN): TTL 1 = roteador (ou provedor, se o gateway for público/PPPoE);
saltos privados/CGNAT antes do primeiro salto público = provedor; os 2 primeiros saltos públicos = provedor, estendido
enquanto o DNS reverso tiver o mesmo domínio; salto com DNS reverso de ponto de troca de tráfego (`*.ix.br`, DE-CIX,
AMS-IX…) e os seguintes = internet; destino alcançado = destino. Segmentos nunca "voltam". O resultado informa a
latência acrescentada por segmento (`IncrementalMs`), o jitter e a perda do salto representativo.

## Operações que exigem elevação

**Requer elevação — executado via RBM.Agent.** Propostas de operação (ainda não registradas em `Rbm.Core.Agent.AgentOps`):

| Operação proposta | Payload | Resposta | Uso |
|---|---|---|---|
| `net.advprop.set` | `AdvancedPropertySetRequest(InterfaceGuid, RegistryKeyword, NewRegistryValue, ExpectedCurrentValue?)` | `AdvancedPropertySetResponse` (inclui `PreviousRegistryValue`, `Verified`, `RollbackRequest`) | Alterar uma propriedade avançada |
| `net.advprop.read` | `AdvancedPropertyReadRequest(InterfaceGuid)` | `AdapterAdvancedSettings` | Leitura elevada (o WMI de energia falha sem elevação em alguns drivers) |

O despachante `NetworkAgentHandler.HandleAsync(op, payload, ct)` já desserializa com `AgentJson.Options` e valida tudo.

Validação estrita da escrita (no agente, antes de tocar no WMI):
1. GUID válido; palavra-chave `^\*?[A-Za-z0-9_]{1,64}$`; valor `^-?[A-Za-z0-9_.]{1,32}$` (impede injeção na consulta WQL).
2. Palavras-chave bloqueadas: `NetworkAddress` (MAC) e VLAN ID; propriedades de texto livre também são bloqueadas.
3. O processo precisa estar elevado (senão: `not_elevated`).
4. A propriedade precisa existir no adaptador; com `ExpectedCurrentValue`, o valor atual precisa coincidir (senão `drift`, nada é escrito).
5. O valor precisa estar na lista `ValidRegistryValues` do driver ou, para numéricos, na faixa `Min..Max` com o passo declarado (ou ser o padrão do driver).
6. Após aplicar, o valor é relido para confirmar (`Verified`).

**Aviso ao usuário:** ao aplicar, o driver reinicia o adaptador: **a rede cai por alguns segundos** (`AdvancedPropertyService.ResetWarning`).
Rollback: envie `response.RollbackRequest` (valor anterior, com `ExpectedCurrentValue` = valor aplicado).

## Limitações conhecidas

- **Resolução do ICMP:** o Windows informa o RTT do eco em ms inteiros (rede local aparece como 0–1 ms). Para respostas
  "TTL excedido" o Windows informa 0, por isso traceroute/caminho usam tempo medido localmente (Stopwatch), que inclui um
  pequeno overhead de agendamento. TCP é medido com Stopwatch (sub-ms).
- **ICMP ≠ tráfego do jogo:** roteadores e servidores podem despriorizar ou bloquear ICMP; use alvos TCP para hosts que
  descartam ICMP. Um RST (porta fechada) também conta como RTT válido.
- **Divisão provedor/internet é heurística** (sem banco de ASN). Saltos privados após o roteador podem ser NAT duplo.
- **Wi-Fi no Windows 11 24H2+:** SSID/BSSID exigem a permissão de Localização para aplicativos de área de trabalho;
  sem ela o motivo é retornado e apenas RSSI/canal são lidos. A banda inferida só pelo canal é marcada como incerta
  (canais 1–233 também existem em 6 GHz). **Wi-Fi não foi validado ao vivo** (a máquina de desenvolvimento não tem Wi-Fi);
  os tamanhos das estruturas nativas são verificados em teste e o parser do netsh é testado com saídas pt-BR/inglês.
- **Energia:** `MSFT_NetAdapterPowerManagementSettingData` falhou sem elevação no hardware de teste ("Falha genérica");
  o fallback lê `PnPCapabilities` (ausente/0 = permitido; bits 0x18 = não permitido). Use `net.advprop.read` para a leitura elevada.
- **DNS:** a "primeira consulta" pode já estar no cache do resolvedor (domínios populares). Respostas truncadas (TC)
  são reportadas, mas a repetição via TCP não é feita. Somente registros A (IPv4). Nada é alterado nas configurações de DNS.
- **Data do driver** é a do INF; drivers da Microsoft têm datas fixas (ex.: 2006) que não refletem a versão.
- **Descartes de recepção** incluem pacotes de protocolos não usados; só erros são tratados como sinal forte.
- `Win32_PnPSignedDriver` é lento (~1–3 s); desligue com `AdapterQueryOptions(IncludeDriverSignature: false)` se precisar.
- A escrita de propriedades avançadas foi verificada apenas no caminho de recusa (shell sem elevação). A escrita real
  ainda precisa ser testada pelo RBM.Agent elevado.

## Verificação

- Build isolado: `source build/env.sh && dotnet build src/Rbm.Network/Rbm.Network.csproj -p:ArtifactsPath="$LOCALAPPDATA/RbmOptimizer.Build/wt-network"`
  → 0 erros, 0 avisos.
- Testes xUnit v3 em `tests/Rbm.Tests/Network/` (102 casos): matemática de jitter/perda/percentis, acumulador, registro de
  histórico, codificação/decodificação DNS (compressão, CNAME, NXDOMAIN/SERVFAIL, TC, laço de ponteiros, truncamento),
  escopo de IP (privado/CGNAT/link-local/IPv6), classificação de saltos (CGNAT, IXP, PPPoE, monotonia), interpretação de
  perda real vs limitação de ICMP, latência incremental por segmento, parser netsh pt-BR/inglês/mojibake, banda Wi-Fi,
  tamanhos das estruturas WLAN x64, validação de propriedades avançadas (injeção, faixa/passo, texto livre, bloqueios),
  rollback, e todas as regras de diagnóstico. Executados num projeto descartável que referencia Core + Network: 102/102 aprovados.
- Teste ao vivo (Win11 26200, sem elevação): inventário real (Realtek GbE negociado em 100 Mbps, Radmin VPN como virtual),
  31 propriedades avançadas lidas, escrita recusada com `not_elevated`, gateway + salto CGNAT do provedor descobertos,
  latência ICMP/TCP para 6 alvos, modo contínuo cancelado corretamente, caminho até 1.1.1.1 com perda de 70% no salto do
  IX.br corretamente classificada como limitação de ICMP, benchmark DNS de 7 resolvedores, e achados reais
  (`net.link.below-capability`, `net.power.saving-desktop`).
