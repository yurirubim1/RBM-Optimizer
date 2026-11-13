# Rbm.Latency — latência de kernel, timer, entrada e interrupções (RBM Optimizer)

Biblioteca (sem UI) do RBM Optimizer para **medir** latência de drivers (DPC/ISR), indicadores de interrupção,
resolução do timer, dispositivos de entrada (USB/HID), taxa de polling e modo de interrupção (MSI) de dispositivos PCI.
Somente medições reais; nada é alterado no sistema por este módulo.

> **Importante — o que isto NÃO mede.** DPC/ISR medem quanto tempo drivers passam em rotinas de kernel de alta
> prioridade. Isso **não** é latência de entrada (clique-até-imagem). Nenhum software rodando no próprio PC mede a
> latência mouse-até-fóton: isso exige hardware externo (NVIDIA LDAT, monitor com Reflex Latency Analyzer ou câmera de
> alta velocidade filmando mouse e tela). Os textos exibidos ao usuário repetem esse aviso (`LatencyTexts.NotInputLag`).

## Recursos

| # | Recurso | Elevação | Classe principal |
|---|---------|----------|------------------|
| 1 | Analisador DPC/ISR por ETW de kernel (por driver, por CPU, linha do tempo, top N, veredito, achados) | **Sim** (RBM.Agent) | `DpcIsrCaptureService` |
| 2 | Indicadores por contadores de desempenho (% DPC, % interrupção, DPCs/s, interrupções/s) | Não | `InterruptCounterSampler` |
| 3 | Resolução do timer (NtQueryTimerResolution) + explicação do comportamento do Windows 10 2004+/11 | Não | `TimerResolutionInspector.Query` |
| 3b | Processos pedindo resolução alta (`powercfg /energy`) | **Sim** (RBM.Agent) | `TimerResolutionInspector.CollectTimerRequestsAsync` |
| 4 | Dispositivos de entrada + cadeia USB + suspensão seletiva (por dispositivo e global) | Não | `InputDeviceInspector` |
| 5 | Estimador de taxa de polling + auxiliares de Raw Input | Não | `PollingRateEstimator`, `RawInputHelper` |
| 6 | Modo de interrupção (MSI/linha), afinidade e IRQs de dispositivos PCI | Não | `InterruptModeInspector` |

Todos os resultados são `record`s imutáveis, serializáveis com `System.Text.Json` (`AgentJson.Options`) — testado por
ida e volta no caso do `LatencyReport`. Contadores não contêm `NaN` (valores ausentes = 0 com `Count = 0`).

## Metodologia

### 1. DPC/ISR (ETW de kernel)

* **Sessão:** `TraceEventSession` (TraceEvent 4.0.0) com nome **privado** `RBM-Latency-Kernel` em modo *system logger*
  (Windows 8+), para não disputar a sessão global "NT Kernel Logger" com LatencyMon/xperf/ProcMon. Se a sessão privada
  falhar, usa o "NT Kernel Logger" **somente se estiver livre** — nunca encerra sessões de terceiros.
* **Palavras-chave:** `DeferedProcedureCalls | Interrupt | ImageLoad | Process` (+ `MemoryHardFaults`, opcional).
  Eventos: `PerfInfoDPC`, `PerfInfoTimerDPC`, `PerfInfoThreadedDPC` (todos contados como DPC) e `PerfInfoISR`.
* **Duração de cada execução:** `DPCTraceData.ElapsedTimeMSec` / `ISRTraceData.ElapsedTimeMSec`. Verificado por
  inspeção do IL do TraceEvent 4.0.0: `ElapsedTimeMSec = TimeStampRelativeMSec − QPCTimeToRelMSec(InitialTimeQPC)`,
  ou seja, *timestamp do evento − InitialTime* (o evento é emitido ao fim da rotina; `InitialTime` é a entrada).
  Durações negativas, `NaN` ou > 1 s são descartadas e contadas em `InvalidSamples`.
* **Atribuição a módulos:** endereço da rotina (`Routine`) → faixa `[ImageBase, ImageBase+ImageSize)` dos eventos
  `ImageDCStart` (rundown dos drivers já carregados) e `ImageLoad`. Reserva: `EnumDeviceDrivers` (faixas aproximadas,
  tamanho = distância até a próxima base); faixas exatas têm prioridade. Rotinas sem módulo → `(desconhecido)` e
  `UnresolvedPercent`.
* **Agregação (memória constante):** por (tipo, rotina) um histograma fino (1 µs até 1 ms; 10 µs até 10 ms; 100 µs até
  100 ms) com contagem/soma/mín/máx exatos; P50/P99 estimados com essa resolução. Depois, rotinas são somadas por módulo.
  Histograma do relatório: `<50, 50–100, 100–250, 250–500, 500–1000, 1000–2000, >2000 µs` (limite inferior inclusivo).
* **Saídas:** estatísticas gerais DPC e ISR; por módulo (DPC e ISR separados, % do tempo de captura); por CPU (contagem,
  total, máx, % do tempo de parede); linha do tempo por segundo (máx DPC/ISR, contagens, totais) para gráficos; top N
  execuções com horário e CPU; page faults "hard" por processo (opcional); eventos perdidos.

#### Veredito (heurística estilo LatencyMon — `LatencyThresholds`, ajustável)

| Faixa por execução | Leitura |
|---|---|
| < 500 µs | normal para áudio em tempo real e jogos |
| 500–1000 µs | atenção se recorrente |
| > 1000 µs (1 ms) | pode causar estalos de áudio com buffers pequenos |
| > 2000 µs (2 ms), recorrente | problemático |

* **Problemático:** execuções ≥ 2000 µs em ≥ 3 segundos distintos.
* **Atenção:** qualquer execução ≥ 1000 µs, ou execuções ≥ 500 µs em ≥ 3 segundos distintos.
* **Bom:** caso contrário. **Inconclusivo:** nenhum evento recebido.

#### Achados (`Rbm.Core.Diagnostics.Finding`)

* `latency.dpcisr.overall` — veredito Atenção/Problemático, com evidências numéricas.
* `latency.dpcisr.module` — até 5 módulos com pico ≥ 1 ms ou ≥ 10 execuções ≥ 500 µs. Sempre `CorrelationOnly = true`
  e causa provável iniciada por **"Associação, não causa confirmada"**; dica por categoria (rede, GPU, armazenamento,
  áudio, USB, ACPI/plataforma). `Wdf01000.sys` (framework KMDF) e `ntoskrnl.exe` recebem confiança baixa porque o
  driver real pode estar por trás deles.
* `latency.dpcisr.cpu0` — informativo, quando ≥ 60% do tempo DPC/ISR está no CPU 0.
* `latency.capture.eventsLost` — informativo, quando o buffer ETW descartou eventos.

### 2. Indicadores sem elevação

PDH com `PdhAddEnglishCounterW` (funciona com Windows em português): `\Processor(_Total)\% DPC Time`,
`% Interrupt Time`, `DPCs Queued/sec`, `Interrupts/sec` e por CPU `\Processor(*)\% DPC Time`/`% Interrupt Time`.
Interpretação **indicativa**: média (% DPC + % interrupção) < 1% baixa, 1–3% moderada, > 3% alta; picos por CPU > 10%
destacados. Não mostra picos em µs nem o driver.

### 3. Resolução do timer

`NtQueryTimerResolution` (ntdll; não documentada no SDK, mas estável e usada pelo ClockRes da Sysinternals). Unidades de
100 ns → ms. A API chama de *Minimum* a resolução mais **grossa** (ex.: 15,625 ms) e de *Maximum* a mais **fina** (0,5 ms);
o modelo usa `CoarsestMs`/`FinestMs`/`CurrentMs`. Também lê (sem alterar) `GlobalTimerResolutionRequests`.

Comportamento documentado pela Microsoft ([timeBeginPeriod — Remarks](https://learn.microsoft.com/windows/win32/api/timeapi/nf-timeapi-timebeginperiod)):

* Antes do Windows 10 2004, `timeBeginPeriod` era **global**.
* A partir do Windows 10 2004 "esta função não afeta mais a resolução global": processos que a chamam recebem a maior
  precisão pedida por qualquer processo; os que não chamam não têm garantia acima da resolução padrão.
* No Windows 11, processos com janela totalmente encoberta, minimizada ou invisível/inaudível perdem essa garantia
  (ver [SetProcessInformation](https://learn.microsoft.com/windows/win32/api/processthreadsapi/nf-processthreadsapi-setprocessinformation),
  `PROCESS_POWER_THROTTLING_IGNORE_TIMER_RESOLUTION`).
* `GlobalTimerResolutionRequests` é descrito por fonte de terceiros (Bruce Dawson, "Windows Timer Resolution: The Great
  Rule Change", 2020), não na página da API.

`CollectTimerRequestsAsync` (**elevado**) roda `powercfg /energy /output <temp> /duration N` (10..120 s, padrão 60 s —
**bloqueia durante toda a duração**), extrai "Platform Timer Resolution:Outstanding Timer Request" (PID, caminho,
período) e "Current Timer Resolution", e apaga o HTML temporário. O parser achata o HTML em células e reconhece rótulos
em inglês e português.

### 4. Dispositivos de entrada

WMI `Win32_PnPEntity` (classes `Mouse`, `Keyboard`, `HIDClass`, `XnaComposite`, `XboxComposite`) → cadeia de pais por
`CM_Get_Parent` (coleção HID → interface USB → dispositivo composto → hub raiz → controlador host). Para cada nó USB e
para o próprio dispositivo lê `Device Parameters`: `SelectiveSuspendEnabled`, `EnhancedPowerManagementEnabled`,
`AllowIdleIrpInD3`, `DeviceSelectiveSuspended`, `SelectiveSuspendOn` (DWORD, QWORD ou binário de 1 byte) e interpreta.
Configuração global: `PowerReadACValueIndex`/`PowerReadDCValueIndex` no plano ativo, subgrupo
`2a737441-1930-4402-8d77-b2bebba308a3`, configuração `48e6b7a6-50f5-4782-a5d4-53bb8f07e226` (0 = desativada, 1 = ativada).
`HostControllerInstanceId` permite cruzar com o relatório de MSI.

### 5. Taxa de polling

A UI registra Raw Input e, a cada `WM_INPUT` de movimento, chama `estimator.Add(hDevice, Stopwatch.GetTimestamp())`.
Intervalos > 50 ms são pausas (descartadas); janela móvel de 2000 intervalos; taxa = 1000 / **mediana**; relata
média, P1, P99, desvio, estabilidade (% de intervalos a ±20% da mediana), taxa padrão mais próxima
(125/250/500/1000/2000/4000/8000 Hz, distância logarítmica) e confiança. Detecta eventos agrupados (fila da UI atrasada).
Mede a **taxa de relatórios durante movimento**, não latência.

### 6. Modo de interrupção (MSI)

Registro `Enum\PCI\<id>\<inst>\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties`
(`MSISupported`, `MessageNumberLimit`) e `...\Affinity Policy` (`DevicePolicy`, `DevicePriority`,
`AssignmentSetOverride`), conforme [Enabling MSI in the Registry](https://learn.microsoft.com/windows-hardware/drivers/kernel/enabling-message-signaled-interrupts-in-the-registry).
Modo efetivo pelas IRQs de `Win32_PnPAllocatedResource`: valores ≥ 0x80000000 (negativos como int32) = MSI. Nome e
classe vêm do cfgmgr32 (texto já resolvido), com reserva no registro/ClassGUID. Somente leitura — alterar MSI é papel do
motor de ajustes.

## API pública (resumo)

```csharp
// 1 — elevado (RBM.Agent)
new DpcIsrCaptureService().CaptureAsync(DpcIsrCaptureOptions options, IProgress<LatencyCaptureProgress>? progress = null, CancellationToken ct = default) : Task<LatencyReport>
DpcIsrCaptureService.Validate(DpcIsrCaptureOptions)          // Seconds 5..300, TopExecutions 1..500, BufferSizeMB 16..512
DpcIsrCaptureService.StopStaleSessions() : int               // só sessões "RBM-Latency-*" / "Rbm-Latency-*"
LatencyAgentHandlers.CaptureAsync(LatencyCaptureRequest?, CancellationToken) : Task<LatencyReport>
LatencyAgentHandlers.ValidateCapture(LatencyCaptureRequest?) : DpcIsrCaptureOptions
// lógica pura (testável sem ETW)
new LatencyAggregator(int topN = 50).Add(in KernelLatencySample) / AddHardFault(pid, ms) / Build(KernelModuleMap, LatencyReportContext) : LatencyReport
LatencyAssessment.Assess(...) : LatencyAssessmentResult

// 2
new InterruptCounterSampler().SampleAsync(int seconds /*1..600*/, TimeSpan? interval = null, CancellationToken ct = default) : Task<InterruptCounterSummary>

// 3
TimerResolutionInspector.Query() : TimerResolutionInfo
TimerResolutionInspector.CollectTimerRequestsAsync(int seconds = 60 /*10..120*/, CancellationToken ct = default) : Task<TimerRequestsReport>   // elevado
LatencyAgentHandlers.TimerRequestsAsync(TimerRequestsCaptureRequest?, CancellationToken) : Task<TimerRequestsReport>                         // elevado
EnergyReportTimerParser.Parse(string html) : EnergyTimerParseResult

// 4
InputDeviceInspector.InspectAsync(bool includeAllHid = false, CancellationToken ct = default) : Task<InputDevicesReport>
InputDeviceInspector.ReadGlobalSelectiveSuspend() : UsbSelectiveSuspendGlobal

// 5
new PollingRateEstimator(long qpcFrequency, int windowSize = 2000, double pauseThresholdMs = 50, int minIntervals = 100)
  .Add(IntPtr|long device, long qpcTimestamp) / GetEstimate(device) : PollingRateEstimate? / GetAllEstimates() / Reset()
RawInputHelper.Register(IntPtr hwnd, RawInputDeviceKinds kinds, bool inputSink = true) / Unregister(kinds)
RawInputHelper.TryParse(IntPtr lParam, out RawInputEventInfo) / TryGetDeviceHandle(lParam, out IntPtr) / GetDeviceName(IntPtr) / ListDevices()

// 6
InterruptModeInspector.InspectAsync(bool includeBridges = false, bool includeNotPresent = false, CancellationToken ct = default) : Task<InterruptModeReport>
```

## Elevação e operações do agente

| Operação | Payload | Resposta | Validação | Timeout sugerido no cliente |
|---|---|---|---|---|
| `latency.capture` (já existe em `AgentOps`) | `LatencyCaptureRequest(int Seconds)` | `LatencyReport` | 5..300 s | Seconds + 30 s |
| `latency.timerRequests` (**proposta**; constante em `LatencyAgentOps`) | `TimerRequestsCaptureRequest(int Seconds)` | `TimerRequestsReport` | 10..120 s | Seconds + 150 s |

Os métodos elevados verificam `ChannelSecurity.IsElevated()` e lançam `UnauthorizedAccessException` (pt-BR) se não
estiverem elevados. A sessão ETW é parada e descartada em qualquer saída (fim, cancelamento, exceção). Se o agente cair
no meio de uma captura, a sessão *system logger* continua ativa até ser parada: a próxima captura chama
`StopStaleSessions()` (apenas nomes `RBM-Latency-*`/`Rbm-Latency-*`). Para incorporar a nova operação, o Core precisa de
`AgentOps.LatencyTimerRequests = "latency.timerRequests"` (+ entrada em `AgentOps.All`) e do record de payload.

## Limitações

* DPC/ISR ≠ latência de entrada; mouse-até-fóton exige hardware externo (LDAT, Reflex Analyzer, câmera de alta velocidade).
* A atribuição por endereço indica onde o tempo foi gasto, não quem causou: KMDF (`Wdf01000.sys`), `ndis.sys` e timers do
  kernel podem mascarar o driver real. Por isso os achados são "associação, não causa confirmada".
* A latência de agendamento de threads (interrupt-to-process do LatencyMon) não é medida.
* Captura em tempo real pode perder eventos sob carga extrema (reportado em `EventsLost`); máximos continuam válidos como
  limite inferior. O rundown inicial pode gerar picos no 1º segundo.
* Contadores de desempenho são médias por intervalo (indicativos).
* O parser do `powercfg /energy` foi validado com HTML sintético em inglês e rótulos em português supostos; em outros
  idiomas pode não reconhecer as entradas (avisa em `Warnings`).
* Taxa de polling depende de quando a UI processa `WM_INPUT`: para ≥ 2000 Hz use uma thread dedicada com janela
  message-only. Mouses só reportam com movimento.
* Valores de suspensão seletiva são interpretados (somente leitura); a semântica exata pode variar por driver.

## Verificação

* **Build isolado** (Git Bash): `source build/env.sh && dotnet build src/Rbm.Latency/Rbm.Latency.csproj -p:ArtifactsPath="$LOCALAPPDATA/RbmOptimizer.Build/wt-latency"` → 0 erros, 0 avisos.
* **Testes** (`tests/Rbm.Tests/Latency/`, namespace `Rbm.Tests.Latency`): 133 testes xUnit v3 — histograma/limites,
  percentis do histograma fino, mapa de módulos, agregação DPC/ISR com eventos sintéticos (por módulo, por CPU, linha do
  tempo, top N, inválidos, vereditos Bom/Atenção/Problemático/Inconclusivo, achados e redação), JSON ida e volta,
  validação de payloads, sessões órfãs, estimador de polling (125…8000 Hz, pausas, relatórios perdidos, janela,
  agrupamento), suspensão seletiva, IRQ/MSI/afinidade, parser do `powercfg /energy`, categorias de drivers e consulta real
  de NtQueryTimerResolution. Executados num projeto descartável que referencia Core + Latency (o projeto de testes
  compartilhado é editado em paralelo).
* **Smoke sem elevação** (Win11 Pro 26200, Ryzen 5 5500, RX 6600): timer 15,625/0,5/atual 1 ms; contadores em pt-BR OK;
  7 dispositivos de entrada com cadeia até o xHCI AMD; 13 dispositivos PCI (RX 6600, Realtek GbE, NVMe, AHCI, 3× xHCI em
  MSI; áudio HD em linha — confere com o Gerenciador de Dispositivos); captura DPC/ISR e `powercfg /energy` recusadas
  corretamente sem elevação.

### Teste manual reproduzível da captura elevada

A captura ETW de kernel não pôde ser executada no ambiente de desenvolvimento (shell sem elevação). Para validar:

1. Crie um console descartável (fora do repositório) referenciando `src/Rbm.Core` e `src/Rbm.Latency`
   (`net10.0-windows`, `x64`, `win-x64`) com:
   ```csharp
   using System.Text.Json; using Rbm.Core.Agent; using Rbm.Latency;
   Console.OutputEncoding = System.Text.Encoding.UTF8;
   var cts = new CancellationTokenSource(); Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
   var r = await new DpcIsrCaptureService().CaptureAsync(new DpcIsrCaptureOptions(Seconds: 30),
       new Progress<LatencyCaptureProgress>(p => Console.WriteLine($"{p.ElapsedSeconds:0}s DPC={p.DpcEvents} ISR={p.IsrEvents}")), cts.Token);
   Console.WriteLine($"{r.Verdict}: {r.VerdictText}\nSessão={r.SessionName} perdidos={r.EventsLost} não-resolvido={r.UnresolvedPercent:0.#}%");
   foreach (var m in r.Modules.Take(10)) Console.WriteLine($"{m.Module,-20} DPC n={m.Dpc.Count} máx={m.Dpc.MaxUs:0} P99={m.Dpc.P99Us:0} | ISR n={m.Isr.Count} máx={m.Isr.MaxUs:0}");
   foreach (var w in r.Warnings) Console.WriteLine("AVISO " + w);
   File.WriteAllText("latency-report.json", JsonSerializer.Serialize(r, AgentJson.Options));
   ```
2. Abra um terminal **como administrador** e execute `dotnet run`. Durante os 30 s: toque um áudio/vídeo e faça um
   download (estimula `ndis.sys`/`HDAudBus.sys`/GPU).
3. Esperado: veredito ≠ Inconclusivo; DPCs na casa de dezenas de milhares; módulos como `ndis.sys`, `tcpip.sys`,
   `amdkmdag.sys`/`dxgkrnl.sys`, `storport.sys`, `HDAudBus.sys`, `USBXHCI.SYS`, `ACPI.sys` listados;
   `UnresolvedPercent` < 5% e nenhum aviso de rundown ausente; `logman query -ets` durante a captura mostra
   `RBM-Latency-Kernel`.
4. Cancelamento: repita e pressione Ctrl+C após ~5 s → deve lançar `OperationCanceledException` e
   `logman query -ets` **não** deve mais listar `RBM-Latency-Kernel`.
5. Conflito: com LatencyMon aberto e medindo, a captura deve funcionar pela sessão privada (o LatencyMon continua
   intacto). Compare a ordem de grandeza dos máximos por driver com o LatencyMon (aba *Drivers*, coluna *Highest
   execution*).
6. `powercfg /energy` (opcional): no mesmo console elevado,
   `var t = await TimerResolutionInspector.CollectTimerRequestsAsync(60);` com um navegador tocando vídeo → espera-se
   ao menos um pedido (ex.: período 10000 = 1 ms) e `PlatformCurrentResolutionMs` preenchido.
