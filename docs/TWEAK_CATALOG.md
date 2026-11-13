# Catálogo de tweaks do RBM Optimizer

> Arquivo gerado por `build/validate-catalog.ps1 -GenerateCatalogMd` a partir de `src/Rbm.Optimization/Catalog/tweaks.*.json`. Não edite manualmente; a justificativa de cada item está em [TWEAK_RESEARCH.md](TWEAK_RESEARCH.md).

Total: **75** tweaks. Por nota: A=8, B=38, C=15, D=10, E=4. Por categoria: Advanced Lab=12, Armazenamento=8, CPU=4, Energia=3, Gaming=8, GPU=4, Memória=4, Privacidade=16, Rede=4, Windows=12.

Legenda de notas: **A** benefício demonstrado · **B** plausível/dependente do sistema · **C** experimental/evidência insuficiente · **D** sem benefício demonstrado/placebo · **E** obsoleto/inseguro/contraindicado (o app bloqueia a aplicação). "—" em *Recomendado* = manter o padrão do Windows.

## CPU

| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |
|---|---|:-:|---|---|---|---|---|
| `cpu.power.boostmode` | Modo de boost do processador | B | Low | Medium | None | `aggressive` | — |
| `cpu.power.maxstate` | Estado máximo do processador | B | Low | Medium | None | `default` | — |
| `cpu.power.epp` | Preferência de energia/desempenho (EPP) | B | Low | Low | None | `default` | — |
| `cpu.hybrid.schedpolicy` | Política de agendamento heterogêneo (P-cores/E-cores) | C | Medium | Unknown | None | `automatic` | — |

## GPU

| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |
|---|---|:-:|---|---|---|---|---|
| `gpu.hags` | Agendamento de GPU acelerado por hardware (HAGS) | B | Low | Low | Reboot | `default` | — |
| `gpu.windowed.optimizations` | Otimizações para jogos em janela | B | Low | Low | AppRestart | `default` | `on` |
| `gpu.mpo.disable` | Desativar Multiplane Overlay (MPO) | C | Low | Unknown | Reboot | `default` | — |
| `gpu.tdr.delay` | Tempo limite de TDR da GPU (TdrDelay) | D | Medium | Negligible | Reboot | `default` | — |

## Memória

| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |
|---|---|:-:|---|---|---|---|---|
| `memory.largesystemcache` | Cache de sistema grande (LargeSystemCache) | E | Medium | Negligible | Reboot | `default` | — |
| `memory.svchost.splitthreshold` | Limite de separação de processos svchost | D | Low | Negligible | Reboot | `default` | — |
| `memory.prefetcher` | Prefetcher de inicialização e aplicativos | D | Low | Negligible | Reboot | `default` | — |
| `memory.sysmain` | Serviço SysMain (antigo Superfetch) | C | Low | Unknown | Reboot | `default` | — |

## Armazenamento

| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |
|---|---|:-:|---|---|---|---|---|
| `storage.ntfs.lastaccess` | Atualização do 'último acesso' do NTFS | B | Minimal | Low | Reboot | `default` | `disabled` |
| `storage.ntfs.8dot3` | Criação de nomes curtos 8.3 | B | Low | Low | Reboot | `default` | — |
| `storage.trim` | TRIM / notificações de exclusão (NTFS) | A | Minimal | Medium | None | `enabled` | `enabled` |
| `storage.optimize.task` | Otimização agendada de unidades (Retrim/Desfragmentação) | A | Minimal | Low | None | `enabled` | `enabled` |
| `storage.wsearch` | Serviço de indexação (Windows Search) | C | Low | Unknown | None | `default` | — |
| `storage.disk.idle` | Desligar disco rígido após inatividade | B | Minimal | Low | None | `default` | — |
| `storage.nvme.idletimeout` | Tempo para economia de energia do NVMe (APST) | C | Low | Unknown | None | `default` | — |
| `storage.ntfs.memoryusage` | Uso de memória do NTFS (NtfsMemoryUsage) | C | Low | Unknown | Reboot | `default` | — |

## Windows

| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |
|---|---|:-:|---|---|---|---|---|
| `windows.visual.menushowdelay` | Atraso de abertura de submenus | B | Minimal | Negligible | SignOut | `default` | — |
| `windows.visual.minanimate` | Animação ao minimizar/maximizar janelas | B | Minimal | Negligible | SignOut | `default` | — |
| `windows.visual.taskbaranimations` | Animações da barra de tarefas | B | Minimal | Negligible | ExplorerRestart | `default` | — |
| `windows.visual.transparency` | Efeitos de transparência | B | Minimal | Negligible | ExplorerRestart | `default` | — |
| `windows.backgroundapps.win10` | Aplicativos em segundo plano (Windows 10, chave global) | B | Low | Low | SignOut | `default` | — |
| `windows.backgroundapps.policy` | Política: apps da Store em segundo plano | B | Low | Low | SignOut | `default` | — |
| `windows.notifications.toast` | Notificações de aplicativos (toasts) | B | Low | Negligible | SignOut | `default` | — |
| `windows.widgets.policy` | Widgets (Notícias e interesses) | B | Minimal | Low | SignOut | `default` | — |
| `windows.startup.delay` | Atraso de programas de inicialização | B | Minimal | Low | SignOut | `default` | — |
| `windows.faststartup` | Inicialização rápida (Fast Startup) | B | Low | Low | Reboot | `default` | — |
| `windows.explorer.showfileext` | Mostrar extensões de arquivos | B | Minimal | Negligible | ExplorerRestart | `default` | `show` |
| `windows.explorer.launchto` | Explorador abre em 'Este Computador' | B | Minimal | Negligible | ExplorerRestart | `default` | — |

## Gaming

| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |
|---|---|:-:|---|---|---|---|---|
| `gaming.gamemode` | Modo de Jogo | B | Minimal | Low | AppRestart | `on` | `on` |
| `gaming.gamedvr.capture` | Capturas do Xbox Game Bar (Game DVR) | B | Minimal | Negligible | AppRestart | `default` | — |
| `gaming.gamedvr.backgroundrecording` | Gravação em segundo plano ('Gravar o que aconteceu') | B | Minimal | Low | AppRestart | `off` | `off` |
| `gaming.gamedvr.policy` | Política: desativar Game DVR para todos | B | Minimal | Negligible | SignOut | `default` | — |
| `gaming.fso.global` | Otimizações de tela cheia (FSO) — desativar globalmente | C | Low | Unknown | SignOut | `default` | — |
| `gaming.mmcss.systemresponsiveness` | MMCSS: SystemResponsiveness | D | Low | Negligible | Reboot | `default` | — |
| `gaming.mmcss.gamestask` | MMCSS: prioridades da tarefa 'Games' | D | Low | Negligible | Reboot | `default` | — |
| `gaming.mouse.acceleration` | Aceleração do mouse ('Aprimorar precisão do ponteiro') | B | Minimal | Low | SignOut | `default` | — |

## Rede

| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |
|---|---|:-:|---|---|---|---|---|
| `network.mmcss.throttlingindex` | NetworkThrottlingIndex (limitação de rede do MMCSS) | D | Low | Negligible | Reboot | `default` | — |
| `network.qos.nonbesteffortlimit` | Largura de banda reservável do QoS ('os 20%') | D | Low | Negligible | Reboot | `default` | — |
| `network.deliveryoptimization` | Otimização de Entrega (compartilhamento P2P de atualizações) | B | Minimal | Low | None | `default` | — |
| `network.wifi.powersaving` | Economia de energia do adaptador Wi‑Fi | B | Low | Low | None | `default` | — |

## Energia

| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |
|---|---|:-:|---|---|---|---|---|
| `power.usb.selectivesuspend` | Suspensão seletiva USB | B | Low | Low | None | `default` | — |
| `power.pcie.aspm` | Gerenciamento de energia do link PCI Express (ASPM) | B | Low | Unknown | None | `default` | — |
| `power.sleep.timeout` | Suspender após inatividade | B | Minimal | Negligible | None | `default` | — |

## Privacidade

| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |
|---|---|:-:|---|---|---|---|---|
| `privacy.telemetry.level` | Nível de dados de diagnóstico (telemetria) | A | Low | Negligible | None | `default` | `required` |
| `privacy.diagtrack.service` | Serviço de telemetria (DiagTrack) | B | Low | Negligible | Reboot | `default` | — |
| `privacy.advertisingid` | ID de publicidade | A | Minimal | Negligible | SignOut | `default` | `disabled` |
| `privacy.tailoredexperiences` | Experiências personalizadas com dados de diagnóstico | A | Minimal | Negligible | SignOut | `default` | `disabled` |
| `privacy.activityhistory` | Histórico de atividades | A | Minimal | Negligible | SignOut | `default` | `disabled` |
| `privacy.search.web` | Resultados da web (Bing) na pesquisa do Windows | C | Minimal | Negligible | SignOut | `default` | — |
| `privacy.feedback.frequency` | Frequência de solicitações de comentários | A | Minimal | Negligible | None | `default` | `never` |
| `privacy.location` | Serviço de localização (dispositivo inteiro) | A | Low | Negligible | Reboot | `default` | — |
| `privacy.consumerfeatures` | Experiências do consumidor da Microsoft | B | Minimal | Negligible | SignOut | `default` | `disabled` |
| `privacy.consumer.silentapps` | Instalação automática de apps sugeridos | B | Minimal | Negligible | SignOut | `default` | `disabled` |
| `privacy.consumer.startsuggestions` | Sugestões de apps no menu Iniciar | B | Minimal | Negligible | ExplorerRestart | `default` | `disabled` |
| `privacy.consumer.tips` | Dicas e sugestões do Windows | B | Minimal | Negligible | SignOut | `default` | `disabled` |
| `privacy.clearpagefile` | Limpar arquivo de paginação ao desligar | B | Low | Medium | Reboot | `default` | — |
| `privacy.ceip.consolidator` | Tarefa do Programa de Aperfeiçoamento (CEIP Consolidator) | C | Minimal | Negligible | None | `default` | — |
| `privacy.inking.typing` | Personalização de escrita à tinta e digitação | B | Minimal | Negligible | SignOut | `default` | `disabled` |
| `privacy.start.trackprogs` | Rastrear aberturas de apps para o Iniciar | B | Minimal | Negligible | ExplorerRestart | `default` | — |

## Advanced Lab

| Id | Nome | Nota | Risco | Impacto | Reinício | Padrão | Recomendado |
|---|---|:-:|---|---|---|---|---|
| `lab.bcd.useplatformclock` | Forçar HPET como relógio de desempenho (useplatformclock) | E | High | Medium | Reboot | `default` | — |
| `lab.bcd.useplatformtick` | Tick do relógio de plataforma (useplatformtick) | C | Medium | Unknown | Reboot | `default` | — |
| `lab.bcd.disabledynamictick` | Desativar tick dinâmico (disabledynamictick) | C | Medium | Unknown | Reboot | `default` | — |
| `lab.bcd.tscsyncpolicy` | Política de sincronização do TSC (tscsyncpolicy) | D | Medium | Unknown | Reboot | `default` | — |
| `lab.timer.globalrequests` | Resolução de timer global (GlobalTimerResolutionRequests) | C | Medium | Unknown | Reboot | `default` | — |
| `lab.cpu.win32priorityseparation` | Win32PrioritySeparation (quantum e boost de primeiro plano) | C | Low | Unknown | None | `default` | — |
| `lab.cpu.powerthrottling` | Desativar Power Throttling (EcoQoS) globalmente | C | Low | Unknown | Reboot | `default` | — |
| `lab.cpu.coreparking` | Desativar estacionamento de núcleos (core parking) | C | Low | Unknown | None | `default` | — |
| `lab.cpu.minstate` | Estado mínimo do processador em 100% | D | Low | Negligible | None | `default` | — |
| `lab.cpu.idledisable` | Desativar estados ociosos do processador (C-states) | E | High | Unknown | None | `default` | — |
| `lab.process.csrsspriority` | Prioridade alta para csrss.exe via IFEO | E | High | Unknown | Reboot | `default` | — |
| `lab.memory.disablepagingexecutive` | Manter kernel e drivers na RAM (DisablePagingExecutive) | D | Low | Negligible | Reboot | `default` | — |

## Perfis

| Id | Nome | Tipo | Condição | Tweaks (opção) |
|---|---|---|---|---|
| `default` | Padrão do Windows | system | — | (vazio) |
| `safe` | Otimização Segura | system | — | `storage.trim`=enabled<br>`storage.optimize.task`=enabled<br>`gaming.gamedvr.backgroundrecording`=off<br>`gaming.gamemode`=on<br>`storage.ntfs.lastaccess`=disabled |
| `balanced` | Otimização Balanceada | system | — | `storage.trim`=enabled<br>`storage.optimize.task`=enabled<br>`gaming.gamedvr.backgroundrecording`=off<br>`gaming.gamemode`=on<br>`storage.ntfs.lastaccess`=disabled<br>`gpu.windowed.optimizations`=on<br>`windows.visual.menushowdelay`=fast |
| `competitive` | Gaming Competitivo | system | — | `storage.trim`=enabled<br>`storage.optimize.task`=enabled<br>`gaming.gamedvr.backgroundrecording`=off<br>`gaming.gamemode`=on<br>`storage.ntfs.lastaccess`=disabled<br>`gpu.windowed.optimizations`=on<br>`windows.visual.menushowdelay`=fast<br>`cpu.power.epp`=ac-performance<br>`power.usb.selectivesuspend`=disabled-ac<br>`power.pcie.aspm`=off-ac<br>`gaming.gamedvr.capture`=disabled<br>`gaming.mouse.acceleration`=off |
| `workstation` | Estação de Trabalho | system | — | `storage.trim`=enabled<br>`storage.optimize.task`=enabled<br>`storage.ntfs.lastaccess`=disabled<br>`storage.ntfs.8dot3`=except-system<br>`storage.disk.idle`=never-ac<br>`power.sleep.timeout`=never-ac<br>`cpu.power.epp`=ac-performance<br>`windows.faststartup`=disabled<br>`windows.explorer.showfileext`=show |
| `laptop` | Notebook | system | Laptop | `storage.trim`=enabled<br>`storage.optimize.task`=enabled<br>`gaming.gamedvr.backgroundrecording`=off<br>`gaming.gamemode`=on<br>`storage.ntfs.lastaccess`=disabled<br>`cpu.power.boostmode`=battery-moderate |
| `experimental` | Experimental (Advanced Tweaks Lab) | system | — | (vazio) |
| `gaming-competitive` | Jogos: Competitivo | gaming | — | `gaming.gamemode`=on<br>`gaming.gamedvr.backgroundrecording`=off<br>`gaming.gamedvr.capture`=disabled<br>`gpu.windowed.optimizations`=on<br>`cpu.power.epp`=ac-performance<br>`power.usb.selectivesuspend`=disabled-ac<br>`power.pcie.aspm`=off-ac<br>`gaming.mouse.acceleration`=off |
| `gaming-balanced` | Jogos: Balanceado | gaming | — | `gaming.gamemode`=on<br>`gaming.gamedvr.backgroundrecording`=off<br>`gpu.windowed.optimizations`=on |
| `gaming-visual` | Jogos: Qualidade Visual Máxima | gaming | — | `gaming.gamemode`=on<br>`gaming.gamedvr.backgroundrecording`=off<br>`gpu.hags`=on<br>`gpu.windowed.optimizations`=on |
| `gaming-lowend` | Jogos: PC Modesto | gaming | — | `gaming.gamemode`=on<br>`gaming.gamedvr.backgroundrecording`=off<br>`gaming.gamedvr.capture`=disabled<br>`windows.visual.transparency`=off<br>`windows.widgets.policy`=disabled<br>`windows.backgroundapps.policy`=force-deny<br>`windows.visual.taskbaranimations`=off<br>`windows.visual.minanimate`=off |
| `gaming-laptop` | Jogos: Notebook Gamer | gaming | Laptop | `gaming.gamemode`=on<br>`gaming.gamedvr.backgroundrecording`=off<br>`gaming.gamedvr.capture`=disabled<br>`gpu.windowed.optimizations`=on<br>`network.wifi.powersaving`=max-performance |

