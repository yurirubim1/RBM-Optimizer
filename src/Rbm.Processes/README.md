# Rbm.Processes — Gerenciador de processos e de inicialização (RBM Optimizer)

Biblioteca (sem UI) com:

- **A. Gerenciador de processos**: amostragem de baixo custo, detalhes sob demanda, árvore, ações (encerrar, prioridade, afinidade), motor de regras e análise de impacto em segundo plano.
- **B. Inicialização e serviços** (inspirado no Autoruns): entradas de logon com StartupApproved, serviços, tarefas agendadas, outros locais de autostart (somente leitura) e recomendações conservadoras.

Todos os dados são **reais**. Quando algo não pode ser lido, o campo fica `null` e o motivo vai em uma nota pt-BR. Nada é estimado.

## Visão geral da API

| Área | Tipo | Ponto de entrada |
|---|---|---|
| Amostragem | `ProcessSampler` (implementa `IProcessMonitor`) | `Capture()` → `ProcessSnapshot`; `ToProcessSamples()` para o contrato do Core |
| Enumeração bruta | `SystemProcessEnumerator` | `Enumerate()`, `Snapshot()`, `Find(pid)` |
| Taxas (puro) | `ProcessRateCalculator` | `Compute(...)` |
| Detalhes | `ProcessDetailsProvider` | `GetDetails(pid, createTime)`, `GetSignatureAsync(path)` |
| GPU por processo | `GpuProcessUsageReader` | `Read()` → `GpuUsageSnapshot` |
| Árvore | `ProcessTreeBuilder` | `Build(entries)`, `ResolveParents(...)`, `GetDescendants(...)`, `Flatten(...)` |
| Ações | `ProcessActions` / `ProcessGuard` | `Kill`, `KillTree(pid, confirmTree, …)`, `SetPriority`, `SetAffinity` |
| Regras | `ProcessRule`, `ProcessRuleStore`, `ProcessRuleWatcher`, `AffinityPresets` | `Load/Save`, `Start/StopAsync`, `ScanOnceAsync` |
| Impacto | `BackgroundImpactAnalyzer` | `Analyze(snapshots)` → `BackgroundImpactReport` |
| Inicialização | `StartupManager`, `StartupApproved` | `EnumerateAsync`, `SetEnabled` (HKCU), `SetEnabledElevated` (HKLM) |
| Serviços | `ServiceManager` | `EnumerateAsync`, `SetStartModeElevated`, `ControlElevatedAsync`/`ControlAsync` |
| Tarefas | `ScheduledTaskManager` | `EnumerateAsync`, `SetEnabledAsync` (sem elevação), `SetEnabledElevated` |
| Outros autostarts | `AutorunsScanner` | `ScanAsync` (Winlogon, extensões de shell, drivers de boot), `HideVerifiedMicrosoft(...)` |
| Recomendações | `AutostartAdvisor` | `Recommend(AutostartFacts)` → `Keep / OptionalSafeToDisable / InvestigateUnsigned / Unknown` |
| Agente | `Rbm.Processes.Agent.ProcessesAgentHandlers` | um método por operação elevada (veja abaixo) |

### A1. Amostragem
- Uma chamada a `NtQuerySystemInformation(SystemProcessInformation)` por amostra (buffer nativo e nomes reaproveitados entre chamadas).
- Medido nesta máquina (Ryzen 5 5500, Win11 26200, ~300 processos): **~4 ms por amostra** (enumeração bruta ~3,7 ms).
- CPU% = Δ(kernel+usuário) ÷ (intervalo × processadores lógicos) × 100, igual ao Gerenciador de Tarefas. IO = bytes de leitura/escrita/outros por segundo.
- **Primeira amostra: taxas `null`** (não 0). Processos criados entre duas amostras têm a taxa calculada desde a criação.
- Identidade = `ProcessKey(Pid, CreateTime)`: protege contra reutilização de PID.
- Se a chamada nativa falhar, cai para `System.Diagnostics.Process` (sem PID pai; CPU/IO indisponíveis para processos protegidos) e informa em `ProcessSnapshot.Warning`.
- `ToProcessSamples()` converte para `ProcessSample` do Core; como o contrato não aceita null, taxas indisponíveis viram 0 — consulte `RatesAvailable`.

### A2. Detalhes sob demanda
Cache por PID + hora de criação (caminho, versão, linha de comando, usuário, elevação); prioridade e afinidade são relidas a cada chamada. A assinatura é separada (`GetSignatureAsync`, cache por caminho). Sem elevação, processos protegidos (lsass, csrss, MsMpEng…) retornam só nome/PID com nota explicando o motivo.

GPU: contador PDH `\GPU Engine(*)\Utilization Percentage` (sem WMI). `MaxEnginePercent` equivale à coluna "GPU" do Gerenciador de Tarefas; `SumPercent` soma todas as engines e pode passar de 100%. Se o contador não existir, `Available = false` e o erro vem preenchido.

### A4. Ações e proteções
- Tempo real **nunca** é aplicado (`ProcessPriority.RealTime` existe só para leitura).
- Processos críticos (`Protection.CriticalProcesses`) são sempre recusados. Os da lista `ManualOnlyProcesses` (explorer, dwm, audiodg…) exigem `userConfirmed: true`.
- PIDs 0 e 4 são recusados. O próprio processo não pode ser encerrado.
- Afinidade: máscara diferente de zero e contida na máscara do sistema.
- `KillTree` exige `confirmTree: true` e encerra das folhas para a raiz. Descendentes protegidos são mantidos e listados em `Skipped`.
- `expectedCreateTime` (FILETIME) é conferido antes e depois de abrir o handle.
- Acesso negado retorna `ErrorCode = "AccessDenied"` com a sugestão de usar o agente RBM.

### A5. Regras (estilo Process Lasso)
- Arquivo: `%LOCALAPPDATA%\RBMOptimizer\process-rules.json` (via `RbmPaths.DataRoot`). Gravação atômica: arquivo temporário, flush e substituição.
- `ExecutableName`: aceita o nome com ou sem `.exe` e curingas. Nomes exatos têm precedência. `Exceptions`: padrões de caminho (curinga, caminho exato ou pasta).
- Presets de afinidade a partir de `CpuInfo.Cores`: `PerformanceCoresOnly`, `ExcludeEfficiencyCores`, `OneThreadPerCore`, `FirstCacheDomain`, `CustomMask`. Em CPUs não híbridas (ex.: Ryzen 5 5500), os presets P/E equivalem a "todos os núcleos", e a nota avisa.
- O validador recusa: processos críticos, curingas amplos (`*`, `*.exe`), tempo real, caminhos no lugar do nome e regras sem nenhuma ação. Regras inválidas editadas à mão são carregadas **desabilitadas**.
- `ProcessRuleWatcher`:
  - varre a cada 2 s (configurável, mínimo 500 ms) e aplica a regra **uma vez** por processo (PID + hora de criação);
  - se as regras mudarem, os processos já vistos são reavaliados (sem reaplicar);
  - mantém um log em anel e dispara o evento `ActionLogged`;
  - aceita `elevatedFallback` para enviar ao agente quando o acesso é negado.
- `BackgroundImpactAnalyzer` calcula, por processo:
  - CPU média e de pico;
  - IO médio;
  - inclinação de crescimento do working set e da memória privada (MB/min, regressão linear);
  - monotonicidade do crescimento.

  Marca "possível vazamento" só com janela ≥ 2 min, ≥ 6 amostras, crescimento ≥ 50 MB, ≥ 5 MB/min e ≥ 80% dos intervalos em alta.

### B1. Inicialização (StartupApproved)
- Locais lidos:
  - Run e RunOnce em HKCU e HKLM;
  - Run e RunOnce em HKLM\WOW6432Node;
  - `Policies\Explorer\Run` (HKCU e HKLM);
  - pastas Inicializar do usuário e comum. Os `.lnk` são resolvidos por IShellLink e, se falhar, pelo parser MS-SHLLINK.
- Estado: valor de 12 bytes em `...\Explorer\StartupApproved\{Run|Run32|StartupFolder}`.
  - Byte 0 = 0x02/0x06 → habilitado; 0x03/0x07 → desabilitado.
  - Bytes 4–11 = FILETIME em que foi desabilitado.
  - O bit 0x04 é preservado.
- Habilitar/desabilitar grava **somente** em StartupApproved (reversível, igual ao Gerenciador de Tarefas). **Nada é excluído.**
- RunOnce e Policies não são alternáveis (`CanToggle = false`).
- A entrada precisa existir no local de origem, o que impede gravar valores arbitrários.

### B2. Serviços
SCM via P/Invoke. Para cada serviço:
- nome, nome de exibição e descrição;
- estado e PID;
- modo de inicialização, incluindo `AutomaticDelayed`, e se é iniciado por gatilho;
- conta, comando, executável e `ServiceDll` (serviços hospedados no svchost);
- dependências e dependentes;
- editor/assinatura, `IsProtected` e `IsMicrosoft`.

Se o SCM negar a leitura da configuração, os dados vêm do registro, com nota. Alterar o modo de inicialização recusa serviços protegidos, drivers e os modos Boot/Sistema, e devolve o modo anterior para reversão. Parar ou reiniciar serviços protegidos é recusado. Dependentes em execução **não** são parados automaticamente.

### B3. Tarefas agendadas
- COM `Schedule.Service` (late binding), recursivo, incluindo tarefas ocultas.
- Para cada tarefa: estado, habilitada, última execução e resultado (texto pt-BR), próxima execução, gatilhos (logon/boot/ocioso/horário…), ações (executável + argumentos; manipuladores COM resolvidos via CLSID), autor, oculta e assinatura do executável.
- Habilitar/desabilitar só altera o sinalizador `Enabled`. Tarefas de atualização/segurança (`ProtectedTaskPrefixes`) não podem ser desabilitadas. **Nunca exclui.**

### B4. Outros locais (somente leitura)
- Winlogon `Shell`/`Userinit` (HKLM e HKCU), com `IsDeviation` quando o valor difere do padrão.
- `ContextMenuHandlers` de `*`, `Directory`, `Directory\Background`, `Folder` e `Drive` (CLSID → InprocServer32).
- Drivers de boot/sistema (Type 1/2, Start 0/1).

Filtro `HideVerifiedMicrosoft`: só oculta itens com assinatura Microsoft **verificada**. O signatário WHQL "Microsoft Windows Hardware Compatibility Publisher" assina drivers de **terceiros** e por isso não conta como Microsoft.

### B5. Recomendações
As regras são avaliadas nesta ordem:
1. Item protegido → `Keep`.
2. Arquivo inexistente → `InvestigateUnsigned` (entrada órfã).
3. Sem assinatura ou assinatura inválida → `InvestigateUnsigned`.
4. Inicializador/atualizador conhecido → `OptionalSafeToDisable`, com nota. Inclui Steam, Discord, Epic, Spotify, OneDrive, Teams, Adobe, Battle.net, EA, Ubisoft e outros. Um nome conhecido **sem assinatura** cai antes, no passo 3 (possível imitação).
5. Assinado pela Microsoft → `Keep`.
6. Demais casos → `Unknown`, com o signatário.

**Nunca aplicado automaticamente.**

## Elevação — mapeamento para o agente (RBM.Agent)

Todos os métodos abaixo **requerem elevação e são executados via RBM.Agent**. Eles revalidam o payload inteiro, porque os dados chegam da UI e não são confiáveis.

| Operação (AgentOps) | Payload → resposta | Método |
|---|---|---|
| `process.setPriority` (existe) | `ProcessPriorityRequest` ou `ProcessPriorityCommand` (superconjunto) → `ProcessActionResponse` | `ProcessesAgentHandlers.ProcessSetPriority` |
| `process.kill` (existe) | `ProcessKillRequest` ou `ProcessKillCommand` (superconjunto) → `ProcessActionResponse` | `ProcessesAgentHandlers.ProcessKill` |
| `service.setStart` (existe) | `ServiceStartRequest` → `ServiceStartResponse` | `ProcessesAgentHandlers.ServiceSetStart` |
| `startup.setEnabled` (existe) | `StartupToggleRequest` → `StartupToggleResponse` (proposta) | `ProcessesAgentHandlers.StartupSetEnabled` |
| `service.control` (**proposta**) | `ServiceControlRequest` → `ServiceControlResponse` | `ProcessesAgentHandlers.ServiceControlAsync` |
| `task.setEnabled` (**proposta**) | `TaskToggleRequest` → `TaskToggleResponse` | `ProcessesAgentHandlers.TaskSetEnabled` |

Os registros propostos estão em `Agent/ProposedAgentContracts.cs`. `ProcessKillCommand` e `ProcessPriorityCommand` são superconjuntos compatíveis em JSON dos registros do Core e acrescentam:
- `TreeConfirmed` (só no kill);
- `UserConfirmed`;
- `ExpectedCreateTime`.

Com o contrato atual do Core:
- `Tree = true` conta como confirmação explícita da árvore;
- processos "somente manual" são recusados no agente, porque o contrato não traz a confirmação do usuário.

## Limitações conhecidas
- **Somente x64.** Os offsets nativos são verificados; se não conferirem, o modo alternativo é usado.
- Sem elevação: caminho, linha de comando e usuário de processos protegidos/PPL ficam indisponíveis. Isso é explícito, nunca estimado.
- Afinidade e presets consideram só o **grupo de processadores 0** (até 64 lógicos).
- A verificação de assinatura é cara. Medido aqui:
  - inicialização: ~1,9 s;
  - serviços: ~2,7 s com assinaturas (~1,2 s sem);
  - tarefas: ~4 s;
  - outros autostarts: ~3,8 s.

  Tudo é assíncrono, usa cache e paralelismo de 2. Não faz verificação online de revogação (limitação de `SignatureVerifier`).
- Atalhos "anunciados" (MSI) podem ficar sem destino resolvível (há uma nota).
- `ProcessRuleWatcher` sem elevação não altera processos elevados; nesse caso use `elevatedFallback`.
- A recomendação é heurística e conservadora: um item `Unknown` não é necessariamente seguro de desativar.

## Verificação
- Build isolado: `dotnet build src/Rbm.Processes/Rbm.Processes.csproj -p:ArtifactsPath="$LOCALAPPDATA/RbmOptimizer.Build/wt-processes"` → 0 avisos, 0 erros.
- Testes (`tests/Rbm.Tests/Processes/`, xUnit v3, 71 casos): StartupApproved (codificação/decodificação/validação), parser de linha de comando, parser `.lnk`, curingas, instâncias de GPU, árvore (órfãos/PID reutilizado/ciclos/pós-ordem), cálculo de taxas, prioridade/afinidade/guardas, validação dos handlers do agente, serviços, caminhos de tarefa, regras (correspondência, validação, presets, armazenamento atômico), análise de impacto e recomendações. Também há um teste no sistema real (amostragem do próprio processo). Execução em projeto descartável referenciando Core, Security e Processes: **71/71 aprovados**.
- Smoke test somente leitura nesta máquina:
  - 304 processos, amostra em ~4 ms;
  - GPU por PID disponível;
  - encerrar ou alterar a prioridade do lsass → `Protected`;
  - 24 entradas de inicialização, com estados conferindo com o Gerenciador de Tarefas;
  - 300 serviços (`AutomaticDelayed` e gatilho detectados);
  - 192 tarefas;
  - 159 outros autostarts.
