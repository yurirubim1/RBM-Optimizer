# RBM Optimizer — Arquitetura

> Projeto privado para Windows 10/11 x64. Projetos de código: prefixo `Rbm.*`.

## 1. Decisões de tecnologia

| Decisão | Escolha | Motivo |
|---|---|---|
| Linguagem/runtime | C# / .NET 10 LTS | Suporte longo, P/Invoke maduro, desempenho, ferramentas oficiais. |
| UI | WPF (MVVM, CommunityToolkit.Mvvm) | Desktop não empacotado, controle visual total, estável. O WinUI 3 exigiria o Windows App SDK e um empacotamento mais complexo, sem ganho real aqui. |
| Gráficos | Controles próprios (`OnRender`) | Custo mínimo para atualização em tempo real e nenhuma dependência extra. |
| Persistência | SQLite (Microsoft.Data.Sqlite, WAL) | Local, transacional, sem servidor. |
| Logs | Serilog (arquivo) | Estruturado, com rotação diária. |
| Telemetria | PDH nativo (`PdhAddEnglishCounter`) com curingas | Uma consulta por amostra, independente do idioma do Windows. |
| Hardware | WMI/CIM, DXGI, SMBIOS, `GetLogicalProcessorInformationEx` | APIs documentadas, sem drivers de kernel. |
| Sensores | LibreHardwareMonitorLib 0.9.6 (MPL-2.0), opcional | GPU por ADL/NVAPI em modo usuário. Para CPU usa o driver PawnIO, que o usuário instala por conta própria. O RBM não instala drivers. |
| ETW | Microsoft.Diagnostics.Tracing.TraceEvent | Para DPC/ISR no processo elevado. |
| FPS | PresentMon (Intel, MIT), baixado sob demanda e com assinatura verificada | Usa ETW, sem injeção, compatível com anticheat. |
| Instalador | WiX 5 (MS-RL) | É compilado com o `dotnet build`, sem instalação adicional. |

## 2. Projetos e dependências

```
Rbm.Core          contratos, modelos, estatística, protocolo do agente, ProcessRunner
 ├─ Rbm.Security  Authenticode (embutido + catálogo), listas de proteção, segurança do canal, AgentClient
 ├─ Rbm.Data      SQLite: coleções de documentos JSON, auditoria, configurações, histórico
 ├─ Rbm.Hardware  inventário (WMI/DXGI/SMBIOS), topologia de CPU, sensores LHM
 │   └─ Rbm.Monitoring   TelemetryService (PDH)
 ├─ Rbm.Optimization     catálogo de tweaks (JSON embutido), provedores, executor transacional, limpeza, análise de espaço
 │   └─ Rbm.Restore      ChangeOrchestrator (journal, rollback, lotes, divergência, backup), pontos de restauração
 ├─ Rbm.Diagnostics      regras baseadas em evidência + índice de condição
 ├─ Rbm.Drivers          inventário SetupAPI/CfgMgr, driver store, backup/instalação/troca, NVIDIA/DDU (orientação)
 ├─ Rbm.Processes        amostrador NtQuerySystemInformation, ações, regras, inicialização/serviços/tarefas (Autoruns-like)
 ├─ Rbm.Network          adaptadores, propriedades avançadas, ping/jitter/perda, rota segmentada, DNS
 ├─ Rbm.Latency          DPC/ISR via ETW, timer resolution, dispositivos de entrada, polling rate, MSI
 ├─ Rbm.Benchmarks       CPU/memória/armazenamento/responsividade com metodologia reproduzível
 ├─ Rbm.Gaming           bibliotecas de jogos, PresentMon, análise de frames, correlação de stutter
 ├─ Rbm.Agent (RBM.Agent.exe)  processo auxiliar elevado
 └─ Rbm.UI (RBMOptimizer.exe)  interface WPF (sem privilégios)
tests/Rbm.Tests   xUnit v3 (executável)
installer/         WiX 5 (MSI)
```

## 3. Modelo de privilégios

```
RBMOptimizer.exe (asInvoker, usuário)  ──named pipe (ACL + PID + token)──▶  RBM.Agent.exe (requireAdministrator)
```

1. **A UI nunca roda elevada.** Ela lê o estado do sistema e aplica sozinha só as alterações de escopo do usuário (HKCU).
2. **O agente é iniciado sob demanda.** O Windows exibe o UAC na primeira operação privilegiada, e o agente encerra junto com a UI.
3. **Autenticação do canal:**
   - O pipe tem nome aleatório, e a ACL só permite o SID do usuário da UI e o SYSTEM.
   - O agente aceita **um único cliente**, e o PID dele precisa ser o do processo que o iniciou (`GetNamedPipeClientProcessId`).
   - O executável do processo pai precisa estar **no mesmo diretório** do agente. Instalado em Program Files, esse diretório só é gravável por administradores.
   - Toda mensagem carrega um token de 256 bits, comparado em tempo constante.
4. **Allow-list:** o agente só executa as operações listadas em `AgentOps.All`, e cada handler revalida o payload.
5. **Tweaks:** o agente só aplica IDs do **catálogo compilado nele mesmo**. A UI nunca envia caminhos de Registro. Na reversão, o agente confirma que cada alvo pertence ao manifesto (`CatalogValidator.ValidateRevertTargets`).
6. **Itens protegidos:**
   - processos críticos (não podem ser encerrados) e prioridade REALTIME (bloqueada);
   - serviços de segurança, atualização e núcleo, que nunca são desabilitados;
   - elementos BCD fora da allow-list (hypervisor, VBS, nx e assinatura);
   - técnicas grau E, que não podem ser aplicadas.
7. **Auditoria:** cada operação é registrada em `%ProgramData%\RBMOptimizer\audit\agent-*.log`, e as ações da UI vão para a tabela `audit` do SQLite.

## 4. Motor de otimizações

- **Manifesto** (`TweakManifest`): ID, textos pt/en, categoria, evidência (grau A–E), confiança, risco, impacto, efeitos colaterais, reinício, dependências/conflitos, compatibilidade (build, fabricante de CPU/GPU, formato, CPU híbrida, bateria), opções e referências.
- **Alvos fechados:** `RegistryTarget` (HKLM/HKCU, com valores equivalentes), `PowerSettingTarget` (PowrProf, esquema ativo resolvido no momento), `ServiceTarget` (SCM), `BcdTarget` (allow-list) e `ScheduledTaskTarget` (schtasks em XML).
- **Leitura:** o estado é determinado pela opção que corresponde ao valor real. Os resultados possíveis são *personalizado*, *não aplicável* ou *requer elevação*.
- **Transação** (`TweakExecutor.Apply`):
  1. snapshot de **todos** os alvos;
  2. escrita;
  3. releitura e verificação;
  4. em qualquer falha, rollback em ordem inversa, restaurando só o que de fato mudou.
- **Orquestração** (`ChangeOrchestrator`): divide os alvos entre o agente e a UI. Se a parte local falhar, a parte elevada é desfeita para manter a atomicidade. O resultado é gravado no journal (`TweakChangeRecord`) com o valor anterior e o novo, usuário, status e necessidade de reinício.
- **Reversão:** individual ou por lote. Uma alteração externa só é sobrescrita depois de confirmação explícita.
- **Backup:** arquivo JSON com SHA-256. Na importação, a integridade, os tweaks e os alvos são validados contra o catálogo.
- **Pontos de restauração:** criados via WMI pelo agente. O limite do Windows de 1 ponto por 24 h é detectado e informado.

## 5. Telemetria e diagnóstico

- **TelemetryService:** roda em thread dedicada, com 1 amostra por segundo (configurável). Os contadores PDH cobrem CPU (utility, performance, performance limit, DPC/ISR), memória (commit, cache, standby, hard faults), GPU Engine/Adapter Memory por LUID, PhysicalDisk e Network Interface. O histórico circular guarda 10 minutos.
- **LiveTelemetry (UI):** séries circulares prontas para os gráficos, despachadas na thread de UI. As páginas só assinam enquanto estão visíveis.
- **DiagnosticEngine:** regras isoladas (a falha de uma não derruba as outras). Cada achado traz descrição, evidência, causa provável, confiança, gravidade, impacto, solução, riscos, reinício, reversibilidade e a marcação "associação, não causa".
- **Índice de condição:** cada fator mensurável vira uma nota por limiares documentados. A média é ponderada só com os fatores disponíveis, e a confiança é a fração do peso que tem dados. Não é um benchmark.

## 6. Dados locais

| Caminho | Conteúdo |
|---|---|
| `%LOCALAPPDATA%\RBMOptimizer\rbm.db` | Histórico de alterações, benchmarks, sessões de jogo, diagnósticos, snapshots de drivers, histórico de rede, auditoria e configurações |
| `%LOCALAPPDATA%\RBMOptimizer\logs` | Logs da UI (Serilog, 14 dias) |
| `%LOCALAPPDATA%\RBMOptimizer\backups` | Backups exportados |
| `%LOCALAPPDATA%\RBMOptimizer\tools` | PresentMon baixado (com assinatura verificada) |
| `%ProgramData%\RBMOptimizer\audit` | Auditoria do agente elevado |

## 7. Build, testes e distribuição

- `build/env.sh` (Git Bash) aponta para o SDK .NET 10 instalado no perfil do usuário.
- A saída de build fica **fora do OneDrive**, em `%LOCALAPPDATA%\RbmOptimizer.Build`, para evitar locks de sincronização.
- `build/build.ps1` executa, em ordem:
  1. validação do catálogo;
  2. testes;
  3. publicação self-contained de RBM.Agent + RBMOptimizer em `dist\app`;
  4. geração do MSI WiX em `dist\RBM-Optimizer-Setup.msi`.
- **Testes:** `tests/Rbm.Tests`, xUnit v3. O executável é rodado diretamente, porque o `dotnet test` com VSTest não é suportado no .NET 10.
