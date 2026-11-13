# Pesquisa de tweaks do RBM Optimizer

Documento de evidências que sustenta o catálogo de otimizações do RBM Optimizer (`src/Rbm.Optimization/Catalog/*.json`). Para cada técnica investigada, inclusive as que ficaram só neste documento, registramos origem, documentação oficial, comportamento real, compatibilidade, resultados publicados, relatos contraditórios, regressões possíveis, procedimento de benchmark e reversão exata.

Data da pesquisa: novembro de 2026. Sistema de referência para leituras locais: Windows 11 Pro 26200 (25H2), AMD Ryzen 5 5500, desktop.

## Princípios

1. **Honestidade acima de marketing.** Só afirmamos ganho quando há medição publicada ou mecanismo documentado. Sem dados, o impacto é `Unknown`.
2. **Sem evidência, fica o padrão do Windows.** `recommendedOption` é sempre `null` para notas C, D e E.
3. **Privacidade é separada de desempenho.** Itens de privacidade (`privacy: true`, categoria *Privacidade*) não prometem ganho de desempenho e não entram nos perfis de desempenho.
4. **Segurança não é moeda de troca.** Nenhum item reduz proteções (Defender, firewall, VBS/HVCI, mitigações de CPU, Windows Update). Mitigações de Spectre/Meltdown aparecem só aqui, como documentação.
5. **Alvos estáticos apenas.** Configurações por dispositivo ou por interface (GUIDs de placa de rede, instâncias PCI) ficam fora do JSON; o app tem ferramentas próprias para isso.

### Escala de evidência

| Nota | Significado | Pode ser recomendado? |
|:-:|---|:-:|
| A | Benefício demonstrado ou comportamento oficialmente documentado e verificável | Sim |
| B | Plausível ou dependente do sistema; mecanismo documentado, ganho varia | Sim, com ressalvas |
| C | Experimental; evidência insuficiente ou contraditória | Não |
| D | Sem benefício demonstrado; placebo | Não (só documentação) |
| E | Obsoleto, inseguro ou contraindicado | Não (o app bloqueia a aplicação) |

### Como medir (procedimento geral)

Vale para todos os itens. As seções individuais só acrescentam o que é específico.

- Reinicie antes de cada bateria de testes, aguarde 5 min ociosos e feche apps de fundo não essenciais.
- Faça no mínimo 3 execuções por estado e compare médias **e** dispersão. Diferenças menores que a variação entre execuções contam como ruído.
- Em jogos, use CapFrameX ou PresentMon (média, 1% low, 0,1% low, frametime), sempre com a mesma cena ou benchmark interno e resolução/qualidade fixas.
- Para latência de sistema, use LatencyMon (10 min, DPC/ISR máximos) e WPR/WPA quando precisar de causa raiz.
- Para energia e temperatura, use HWiNFO (potência do pacote, clocks efetivos, temperatura) e, se possível, um medidor de tomada.
- Alterne a ordem dos estados (A-B-B-A) para neutralizar aquecimento e cache.

---

## Tabela-resumo

Legenda: **Rec.?** = existe opção recomendada no catálogo (✔ = sim, com a opção; — = manter padrão). **doc** = técnica documentada só aqui (sem JSON).

| Id | Nota | Rec.? | Risco |
|---|:-:|---|---|
| `cpu.power.boostmode` | B | — | Low |
| `cpu.power.maxstate` | B | — | Low |
| `cpu.power.epp` | B | — (perfis usam `ac-performance`) | Low |
| `cpu.hybrid.schedpolicy` | C | — | Medium |
| `gpu.hags` | B | — (perfil visual usa `on`) | Low |
| `gpu.windowed.optimizations` | B | ✔ `on` | Low |
| `gpu.mpo.disable` | C | — | Low |
| `gpu.tdr.delay` | D | — | Medium |
| `memory.largesystemcache` | E | — | Medium |
| `memory.svchost.splitthreshold` | D | — | Low |
| `memory.prefetcher` | D | — | Low |
| `memory.sysmain` | C | — | Low |
| `storage.ntfs.lastaccess` | B | ✔ `disabled` | Minimal |
| `storage.ntfs.8dot3` | B | — | Low |
| `storage.trim` | A | ✔ `enabled` (= padrão) | Minimal |
| `storage.optimize.task` | A | ✔ `enabled` (= padrão) | Minimal |
| `storage.wsearch` | C | — | Low |
| `storage.disk.idle` | B | — | Minimal |
| `storage.nvme.idletimeout` | C | — | Low |
| `storage.ntfs.memoryusage` | C | — | Low |
| `windows.visual.menushowdelay` | B | — | Minimal |
| `windows.visual.minanimate` | B | — | Minimal |
| `windows.visual.taskbaranimations` | B | — | Minimal |
| `windows.visual.transparency` | B | — | Minimal |
| `windows.backgroundapps.win10` | B | — | Low |
| `windows.backgroundapps.policy` | B | — | Low |
| `windows.notifications.toast` | B | — | Low |
| `windows.widgets.policy` | B | — | Minimal |
| `windows.startup.delay` | B | — | Minimal |
| `windows.faststartup` | B | — | Low |
| `windows.explorer.showfileext` | B | ✔ `show` | Minimal |
| `windows.explorer.launchto` | B | — | Minimal |
| `gaming.gamemode` | B | ✔ `on` (= padrão) | Minimal |
| `gaming.gamedvr.capture` | B | — | Minimal |
| `gaming.gamedvr.backgroundrecording` | B | ✔ `off` (= padrão) | Minimal |
| `gaming.gamedvr.policy` | B | — | Minimal |
| `gaming.fso.global` | C | — | Low |
| `gaming.mmcss.systemresponsiveness` | D | — | Low |
| `gaming.mmcss.gamestask` | D | — | Low |
| `gaming.mouse.acceleration` | B | — | Minimal |
| `network.mmcss.throttlingindex` | D | — | Low |
| `network.qos.nonbesteffortlimit` | D | — | Low |
| `network.deliveryoptimization` | B | — | Minimal |
| `network.wifi.powersaving` | B | — | Low |
| `power.usb.selectivesuspend` | B | — | Low |
| `power.pcie.aspm` | B | — | Low |
| `power.sleep.timeout` | B | — | Minimal |
| `privacy.telemetry.level` | A | ✔ `required` | Low |
| `privacy.diagtrack.service` | B | — | Low |
| `privacy.advertisingid` | A | ✔ `disabled` | Minimal |
| `privacy.tailoredexperiences` | A | ✔ `disabled` | Minimal |
| `privacy.activityhistory` | A | ✔ `disabled` | Minimal |
| `privacy.search.web` | C | — | Minimal |
| `privacy.feedback.frequency` | A | ✔ `never` | Minimal |
| `privacy.location` | A | — | Low |
| `privacy.consumerfeatures` | B | ✔ `disabled` | Minimal |
| `privacy.consumer.silentapps` | B | ✔ `disabled` | Minimal |
| `privacy.consumer.startsuggestions` | B | ✔ `disabled` | Minimal |
| `privacy.consumer.tips` | B | ✔ `disabled` | Minimal |
| `privacy.clearpagefile` | B | — | Low |
| `privacy.ceip.consolidator` | C | — | Minimal |
| `privacy.inking.typing` | B | ✔ `disabled` | Minimal |
| `privacy.start.trackprogs` | B | — | Minimal |
| `lab.bcd.useplatformclock` | E | — | High |
| `lab.bcd.useplatformtick` | C | — | Medium |
| `lab.bcd.disabledynamictick` | C | — | Medium |
| `lab.bcd.tscsyncpolicy` | D | — | Medium |
| `lab.timer.globalrequests` | C | — | Medium |
| `lab.cpu.win32priorityseparation` | C | — | Low |
| `lab.cpu.powerthrottling` | C | — | Low |
| `lab.cpu.coreparking` | C | — | Low |
| `lab.cpu.minstate` | D | — | Low |
| `lab.cpu.idledisable` | E | — | High |
| `lab.process.csrsspriority` | E | — | High |
| `lab.memory.disablepagingexecutive` | D | — | Low |
| *Compressão de memória (MMAgent)* | doc (D para desligar) | — | Low |
| *Plano "Desempenho Máximo"* | doc (C) | — | Low |
| *Política de agendamento de threads curtas (SHORTSCHEDPOLICY)* | doc (C) | — | Medium |
| *AHCI Link Power Management (HIPM/DIPM)* | doc (C) | — | Low |
| *Arquivo de hibernação (`powercfg -h`)* | doc (B) | — | Low |
| *Nagle (TcpAckFrequency / TCPNoDelay)* | doc (D) | — | Low |
| *Auto-tuning do TCP (`netsh`)* | doc (D/E para desativar) | — | Medium |
| *Moderação de interrupções / RSS da placa de rede* | doc (B, por dispositivo) | — | Low |
| *Modo MSI de interrupções* | doc (C, por dispositivo) | — | Medium |
| *Mitigações Spectre/Meltdown (FeatureSettingsOverride)* | doc (E) | — | High |
| *Cache de shaders* | doc (B, driver) | — | Minimal |
| *VRR em janela / Auto HDR (tokens de DirectXUserGlobalSettings)* | doc (B) | — | Low |
| *Efeitos visuais "Ajustar para melhor desempenho" (VisualFXSetting)* | doc (B) | — | Minimal |
| *Botão de Widgets (TaskbarDa)* | doc (protegido por UCPD) | — | Low |
| *Compatibility Appraiser (CompatTelRunner)* | doc (não aplicar) | — | Medium |
| *Serviços Xbox* | doc (D) | — | Low |

---

## CPU

### `cpu.power.boostmode` — Modo de boost do processador (B)

- **Origem:** configuração oficial oculta (PERFBOOSTMODE), popularizada por guias de temperatura de notebooks e por matérias recentes sobre "configurações secretas de boost" (Guru3D, Neowin).
- **Documentação oficial:** Microsoft Learn, *PERFBOOSTMODE*. GUID `be337238-0d82-4146-a960-4f3749d470c7`, subgrupo `54533251-82be-4824-96c1-47b60b740d00`. Índices: 0 Desativado, 1 Habilitado, 2 Agressivo, 3 Habilitado eficiente, 4 Agressivo eficiente. Em CPPC não autônomo, "Habilitado" escolhe um alvo de boost e "Agressivo" escolhe o máximo.
- **Comportamento real:** com 0 o turbo some e o desempenho mono-thread cai na proporção clock base/boost. Em CPUs com CPPC autônomo (HWP), 1 e 2 se comportam praticamente igual.
- **Compatibilidade:** Windows 10 e 11, x86/x64. Leitura local no Win11 26200: o padrão do Equilibrado é AC=2/DC=2 (`PowerReadACDefaultIndex`).
- **Resultados publicados:** a queda de desempenho e de temperatura ao desligar o boost é bem conhecida. Não existe ganho acima do padrão (Agressivo).
- **Contraditório:** nenhum relevante. As matérias de 2026 apresentam o recurso como "aumento de desempenho", mas o padrão já é o modo mais agressivo.
- **Regressões:** perda de desempenho com qualquer opção diferente do padrão.
- **Benchmark:** Cinebench single e multi-core com HWiNFO (clock efetivo, temperatura, potência); na bateria, compare a autonomia.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 be337238-0d82-4146-a960-4f3749d470c7 2`, `powercfg /setdcvalueindex SCHEME_CURRENT 54533251-82be-4824-96c1-47b60b740d00 be337238-0d82-4146-a960-4f3749d470c7 2`, `powercfg /setactive SCHEME_CURRENT`.

### `cpu.power.maxstate` — Estado máximo do processador (B)

- **Origem:** truque de fóruns de notebooks: 99% desliga o turbo.
- **Documentação oficial:** PROCTHROTTLEMAX (`bc5038f7-23e0-4960-96da-33abaf5935ec`), percentual do desempenho máximo.
- **Comportamento real:** em muitos sistemas Intel, 99% impede frequências de turbo. Em CPPC autônomo funciona como teto de desempenho, nem sempre como desligamento exato do turbo.
- **Compatibilidade:** Windows 10 e 11.
- **Resultados publicados:** redução expressiva de temperatura e ruído, com perda de desempenho. Nunca há ganho acima de 100%.
- **Contraditório:** em algumas plataformas AMD o efeito de 99% é inconsistente. Para isso, prefira `cpu.power.boostmode=disabled`.
- **Regressões:** perda de desempenho.
- **Benchmark:** Cinebench R23 multi-core por 10 min com potência e temperatura registradas.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMAX 100` e `/setdcvalueindex ... 100`, depois `powercfg /setactive SCHEME_CURRENT`.

### `cpu.power.epp` — Preferência de energia/desempenho (B)

- **Origem:** configuração oficial oculta (PERFEPP), popular em guias de latência e no ThrottleStop.
- **Documentação oficial:** Microsoft Learn, *PerfEnergyPreference*. GUID `36687f9e-e3a5-4dbf-b1dc-15eb381c6863`. Com CPPC v2 autônomo, o valor (0–100) é gravado no registrador EPP como `valor*255/100`. 0 favorece desempenho; 100 favorece economia.
- **Comportamento real:** controla a agressividade com que o próprio hardware sobe a frequência. Valores menores significam rampa mais rápida e mais consumo em uso leve.
- **Compatibilidade:** CPUs com HWP/CPPC autônomo (Intel Skylake+; AMD Zen 2+ com CPPC no firmware).
- **Padrão:** o valor genérico do Equilibrado é 33 (AC) / 50 (DC). Um caso no Microsoft Q&A usa 33 como restauração. **Pacotes PPM de fabricantes alteram esse padrão:** na máquina de referência (AMD), o padrão lido é AC=0/DC=50. Por isso a reversão do app usa o snapshot.
- **Resultados publicados:** o efeito na rampa de frequência é mensurável. Em jogos, o ganho tende a ser pequeno porque a CPU já fica carregada.
- **Contraditório:** benefício pequeno ou nulo quando a CPU já está em carga sustentada.
- **Regressões:** mais consumo e calor; menor autonomia se aplicado na bateria (o catálogo só aplica na tomada).
- **Benchmark:** tempo de tarefas interativas curtas, potência ociosa por 5 min e 1% low em jogos.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PERFEPP <valor do snapshot>` (genérico: 33), depois `powercfg /setactive SCHEME_CURRENT`.

### `cpu.hybrid.schedpolicy` — Política de agendamento heterogêneo (C)

- **Origem:** guias de Alder Lake e Raptor Lake que tentam forçar jogos em P-cores.
- **Documentação oficial:** Microsoft Learn, *SchedulingPolicy* (SCHEDPOLICY, `93b8b6dc-0698-4d1c-9ee4-0644e900c85d`), aplicada a threads longas. Valores: 0 Todos, 1 Performáticos, 2 Preferir performáticos, 3 Eficientes, 4 Preferir eficientes, 5 Automático (o SO decide por configuração e QoS).
- **Comportamento real:** em CPUs híbridas, restringe ou prefere a classe de núcleo para threads longas. Em CPUs não híbridas não tem efeito.
- **Compatibilidade:** Windows 11 (Thread Director). No Windows 10 o suporte é limitado.
- **Resultados publicados:** não há benchmark independente consistente. Os ganhos relatados aparecem em jogos específicos mal classificados.
- **Contraditório:** "Somente performáticos" reduz o desempenho multi-thread e aumenta o consumo.
- **Regressões:** perda multi-thread; pior eficiência.
- **Benchmark:** CapFrameX em 3 jogos mais Cinebench multi-core, comparando Automático com Preferir performáticos.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR SCHEDPOLICY 5`, o mesmo para DC, depois `/setactive`.
- **Item relacionado só documentado:** SHORTSCHEDPOLICY (`bae08b81-2d5e-4688-ad6a-13243356654b`), threads curtas, mesmos índices. Não entrou no JSON porque o padrão observado varia por plataforma (AC=2/DC=5 na máquina AMD de referência, onde o item nem se aplica) e não há evidência de ganho.

---

## GPU

### `gpu.hags` — Agendamento de GPU acelerado por hardware (B)

- **Origem:** recurso do Windows 10 2004 (WDDM 2.7).
- **Documentação oficial:** não há página do Microsoft Learn sobre o valor de registro. O toggle oficial fica em Configurações > Sistema > Vídeo > Gráficos. Valor: `HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers\HwSchMode` (2 = ligado, 1 = desligado; ausente = decisão do sistema).
- **Comportamento real:** o agendamento de trabalhos da GPU passa a um processador de agendamento na própria GPU. É requisito do DLSS Frame Generation da NVIDIA.
- **Compatibilidade:** GPU e driver compatíveis (NVIDIA GTX 10+, AMD RX 5000+, Intel Arc); Windows 10 2004+.
- **Resultados publicados:** BabelTechReviews (26 jogos, RTX 3080) e PCWorld encontraram diferenças pequenas e dependentes do jogo; usuários do ElevenForum relatam ganhos desprezíveis.
- **Contraditório:** relatos isolados de stutter e de problemas com captura em certas combinações de driver.
- **Regressões:** desligar remove o Frame Generation.
- **Benchmark:** PresentMon/CapFrameX mais latência PC (FrameView/Reflex), reiniciando entre os estados.
- **Reversão:** `reg delete "HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers" /v HwSchMode /f`, ou o valor do snapshot. Reinicie.

### `gpu.windowed.optimizations` — Otimizações para jogos em janela (B, recomendado `on`)

- **Origem:** recurso do Windows 11 22H2, anunciado no blog do DirectX em fevereiro de 2022 (build Insider 22545).
- **Documentação oficial:** blog *Updates in Graphics and Gaming* e artigo de suporte *Optimizations for windowed games in Windows 11*. Jogos DX10/DX11 em janela ou janela sem bordas migram do modelo blt para flip model, que "geralmente resulta em menor latência" e libera Auto HDR e VRR em janela. DX12 não é afetado.
- **Valor de registro:** `HKCU\Software\Microsoft\DirectX\UserGpuPreferences\DirectXUserGlobalSettings` (REG_SZ) com o token `SwapEffectUpgradeEnable=1;`.
- **Comportamento real:** muda o PresentMode observado no PresentMon de *Composed: Copy with GPU GDI* para *Independent Flip/Composed Flip*.
- **Compatibilidade:** Windows 11 22H2+ (build 22621). Ligar o Auto HDR liga este recurso automaticamente.
- **Resultados publicados:** a vantagem de latência do flip model sobre o blt model é documentada pela Microsoft. Os ganhos específicos variam por jogo.
- **Contraditório:** o estado padrão varia entre máquinas segundo relatos do ElevenForum; o artigo de suporte trata o recurso como algo a ligar manualmente.
- **Regressões:** tearing sem V-Sync/VRR acima da taxa do monitor. Há exclusão por app.
- **Limitação do schema:** a string pode conter outros tokens (`AutoHDREnable`, `VRROptimizeEnable`). O catálogo regrava a string inteira (ver *Limitações*).
- **Benchmark:** PresentMon em um jogo DX11 sem bordas, verificando PresentMode, latência e 1% low.
- **Reversão:** restaure a string do snapshot, ou `reg delete "HKCU\Software\Microsoft\DirectX\UserGpuPreferences" /v DirectXUserGlobalSettings /f` se ela não existia.

### `gpu.mpo.disable` — Desativar Multiplane Overlay (C)

- **Origem:** contorno para flicker e telas pretas com MPO (r/Amd, r/nvidia). A NVIDIA publicou `.reg` de desativar e restaurar.
- **Documentação oficial:** `OverlayTestMode=5` em `HKLM\SOFTWARE\Microsoft\Windows\Dwm` não está na documentação pública do DWM.
- **Comportamento real:** o DWM deixa de usar planos de overlay de hardware e compõe tudo.
- **Compatibilidade:** Windows 10 e 11.
- **Resultados publicados:** corrige sintomas específicos (multi-monitor com taxas diferentes, VRR). Sem sintoma, não há benefício; guias citam apenas o custo extra de composição.
- **Contraditório:** parte do flicker pós-driver tem outras causas, como refresh ou sincronização.
- **Regressões:** maior custo de composição e de consumo; perda de caminhos eficientes de vídeo.
- **Benchmark:** reproduza o sintoma com e sem o ajuste.
- **Reversão:** `reg delete "HKLM\SOFTWARE\Microsoft\Windows\Dwm" /v OverlayTestMode /f` e reinicie.

### `gpu.tdr.delay` — TdrDelay (D)

- **Origem:** guias de renderização e GPGPU (Puget Systems) e "correções" de crash de driver.
- **Documentação oficial:** Microsoft Learn, *TDR registry keys*. As chaves são para desenvolvimento e depuração; usuários finais e apps não devem alterá-las. TdrDelay padrão = 2 s.
- **Comportamento real:** a GPU pode ficar sem responder por mais tempo antes da recuperação. Não acelera nada.
- **Resultados publicados:** útil apenas para cargas de computação longas em desenvolvimento.
- **Regressões:** congelamentos mais longos; mascara instabilidade.
- **Reversão:** `reg delete "HKLM\SYSTEM\CurrentControlSet\Control\GraphicsDrivers" /v TdrDelay /f` e reinicie.

### Cache de shaders (doc, B, controlado pelo driver)

- O tamanho e a limpeza do cache de shaders são controlados pelos painéis da NVIDIA ("Shader Cache Size") e da AMD ("Shader Cache"), não por um registro estático do Windows. Limpar o cache só corrige corrupção; após limpar, o primeiro jogo terá mais stutter de compilação. Aumentar o tamanho (ex.: 10 GB ou ilimitado na NVIDIA) reduz recompilações em bibliotecas grandes. É benefício plausível, mas fora do escopo do motor de tweaks. O DirectX também mantém um cache em `%LOCALAPPDATA%\D3DSCache` que a Limpeza de Disco remove.

---

## Memória

### `memory.largesystemcache` — LargeSystemCache (E)

- **Origem:** guias de "otimização" da era Windows XP.
- **Documentação oficial:** ajuste de servidor de arquivos (Windows 2000/2003). Blog da Microsoft (Vista/2008): "Should I change this key? No." A equipe *Ask the Performance Team* documentou problemas causados por cache de sistema excessivo.
- **Comportamento real:** o gerenciador de memória favorece o cache de arquivos sobre o working set dos processos.
- **Compatibilidade:** o valor ainda é lido. Não há UI desde o Vista.
- **Regressões:** menos memória para apps e jogos; relatos históricos de instabilidade com drivers antigos.
- **Reversão:** `reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management" /v LargeSystemCache /t REG_DWORD /d 0 /f` e reinicie.

### `memory.svchost.splitthreshold` — Limite de separação do svchost (D)

- **Origem:** guias de debloat desde 2017 ("menos processos = mais rápido").
- **Documentação oficial:** Microsoft Learn, *Service host grouping in Windows 10*. Desde o 1703, com RAM acima do limite (padrão 0x380000 KB ≈ 3,5 GB), cada serviço roda em seu próprio svchost, por confiabilidade, segurança e diagnóstico.
- **Comportamento real:** com o limite acima da RAM, os serviços voltam a ser agrupados. A economia é de dezenas de MB.
- **Resultados publicados:** nenhum benchmark de ganho. A NinjaOne afirma que, em geral, não é preciso mudar.
- **Regressões:** a falha de um serviço derruba o grupo inteiro.
- **Reversão:** `reg add "HKLM\SYSTEM\CurrentControlSet\Control" /v SvcHostSplitThresholdInKB /t REG_DWORD /d 3670016 /f` e reinicie.

### `memory.prefetcher` — EnablePrefetcher (D)

- **Origem:** "tweaks para SSD" da era Windows 7.
- **Documentação/comportamento:** `PrefetchParameters\EnablePrefetcher`: 0 desligado, 1 apps, 2 boot, 3 ambos (padrão). O Windows adapta o comportamento quando o disco de sistema é SSD.
- **Resultados publicados:** nenhum ganho medido ao desligar. O argumento de desgaste do SSD não tem medição. No DeskModder há relato de que o valor 0 não teve efeito no 1809.
- **Regressões:** abertura de apps e boot mais lentos em HDD.
- **Reversão:** `reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\PrefetchParameters" /v EnablePrefetcher /t REG_DWORD /d 3 /f`.

### `memory.sysmain` — Serviço SysMain (C)

- **Origem:** guias desde o Vista (Superfetch). O serviço foi renomeado para SysMain no build 17763.
- **Comportamento real:** pré-carrega na lista de standby os dados de apps usados com frequência e participa do prefetch e da compressão de memória.
- **Resultados publicados:** nenhum benchmark rigoroso. Os relatos de alívio do uso de disco em HDD durante o boot são anedóticos.
- **Contraditório:** Tom's Hardware e TheWindowsClub dizem que o ganho em SSD é imperceptível; outros dizem que o Windows já se adapta ao SSD.
- **Regressões:** apps frequentes abrem mais devagar; a compressão de memória pode parar.
- **Reversão:** `sc config SysMain start= auto` e `sc start SysMain`.

### Compressão de memória e page combining (doc; desligar = D)

- **Documentação oficial:** cmdlets `Enable-MMAgent`/`Disable-MMAgent -MemoryCompression` e `-PageCombining` (Microsoft Learn). Não existe valor de registro estático equivalente e suportado, por isso o item não está no JSON.
- **Comportamento real:** a compressão guarda páginas comprimidas no processo *Memory Compression* em vez de paginar para o disco. Isso reduz I/O e custa um pouco de CPU.
- **Resultados publicados:** não encontramos benchmark controlado que mostre ganho ao desligar. Há um artigo da TweakTown sobre o tema ("I turned off memory compression on my PC and the numbers told a different story"), mas não conseguimos acessar o conteúdo e não o usamos como evidência. Pelo mecanismo, desligar a compressão aumenta a paginação para o disco quando falta RAM.
- **Recomendação:** manter o padrão (ligado no Windows 10/11 cliente).
- **Reversão:** `Enable-MMAgent -MemoryCompression` e reinicie.

### Arquivo de paginação (doc)

- Desativar ou fixar o pagefile é um mito antigo. Sem pagefile, a carga confirmada (commit) fica limitada à RAM: apps falham com "memória insuficiente" e não há dumps de falha. Recomendação: "Gerenciado pelo sistema". Não está no JSON porque a configuração é por volume (`PagingFiles`, REG_MULTI_SZ) e depende do hardware.

---

## Armazenamento

### `storage.ntfs.lastaccess` — Atualização do "último acesso" (B, recomendado `disabled`)

- **Origem:** recomendação do próprio time de sistema de arquivos da Microsoft (blog Filecab, 2006).
- **Documentação oficial:** `fsutil behavior` (Microsoft Learn): desabilitar "melhora a velocidade de acesso a arquivos e diretórios" e pode afetar Backup e Remote Storage. Valor: `NtfsDisableLastAccessUpdate`. O MS-FSA descreve o modo gerenciado pelo sistema.
- **Valores:** 0x80000000 | n = gerenciado pelo sistema (n = 0 habilitado, 1 desabilitado); 0x80000002 = gerenciado pelo sistema e habilitado (padrão desde o 1803, confirmado localmente como 2147483650); 1 = desabilitado pelo usuário.
- **Comportamento real:** no modo gerenciado, o Windows já desativa as atualizações em volumes grandes. Em sistemas atuais o ganho costuma ser pequeno.
- **Regressões:** quebra ferramentas que usam o atime (backup incremental, arquivamento, regras de limpeza por "não aberto há X dias").
- **Benchmark:** enumere uma árvore de 200 mil arquivos e meça tempo e gravações.
- **Reversão:** `fsutil behavior set disablelastaccess 2`, ou `reg add "HKLM\SYSTEM\CurrentControlSet\Control\FileSystem" /v NtfsDisableLastAccessUpdate /t REG_DWORD /d 2147483650 /f`. Reinicie.

### `storage.ntfs.8dot3` — Nomes curtos 8.3 (B)

- **Documentação oficial:** `fsutil behavior` (Microsoft Learn). Com 8.3 ativo, o NTFS cria uma segunda entrada e precisa procurar nomes curtos ao criar arquivos. Valor: `NtfsDisable8dot3NameCreation` (0 todos, 1 nenhum, 2 por volume [padrão], 3 todos exceto o volume do sistema).
- **Comportamento real:** o custo é relevante em diretórios com dezenas de milhares de arquivos de nomes parecidos e desprezível no uso doméstico.
- **Regressões:** instaladores e apps muito antigos que dependem de caminhos 8.3. Só afeta arquivos novos.
- **Benchmark:** crie 100 mil arquivos numa pasta e cronometre.
- **Reversão:** `fsutil 8dot3name set 2`, ou `reg add ... /v NtfsDisable8dot3NameCreation /t REG_DWORD /d 2 /f`.

### `storage.trim` — TRIM (A, recomendado = padrão)

- **Documentação oficial:** `fsutil behavior` (Microsoft Learn). TRIM vem ativo por padrão no NTFS e não exige reinício. Valor: `HKLM\SYSTEM\CurrentControlSet\Control\FileSystem\DisableDeleteNotification` = 0.
- **Por que existe:** detectar e corrigir sistemas onde TRIM foi desligado por "otimizadores".
- **Reversão / verificação:** `fsutil behavior query DisableDeleteNotify`; `fsutil behavior set disabledeletenotify NTFS 0`.

### `storage.optimize.task` — Otimização agendada (A, recomendado = padrão)

- **Origem do mito:** "desligue a desfragmentação se tiver SSD".
- **Documentação/explicação oficial:** Scott Hanselman, com o time de armazenamento da Microsoft, em *The real and complete story – Does Windows defragment your SSD?*. O Storage Optimizer executa Retrim periodicamente e só desfragmenta SSD em condições específicas (instantâneos de volume), de forma controlada.
- **Regressões ao desligar:** perde o Retrim periódico.
- **Reversão:** `schtasks /Change /TN "\Microsoft\Windows\Defrag\ScheduledDefrag" /ENABLE`.

### `storage.wsearch` — Indexação (C)

- **Comportamento real:** o serviço WSearch (Automático, início atrasado) mantém o índice usado pelo Iniciar, pelo Explorador e pelo Outlook.
- **Resultados publicados:** não há medições confiáveis de ganho; o argumento de desgaste do SSD é fraco. Relatos de uso de disco de 99% (HardwareCanucks) apontam problemas pontuais de índice, não custo intrínseco.
- **Alternativa melhor:** excluir pastas grandes da indexação.
- **Regressões:** busca lenta e incompleta; Outlook e OneNote sem pesquisa rápida.
- **Reversão:** `sc config WSearch start= delayed-auto` e `sc start WSearch`.

### `storage.disk.idle` — Desligar HDD após inatividade (B)

- **Documentação:** DISKIDLE (`6738e2c4-e8a5-4a42-b16a-e040e769756e`, subgrupo `0012ee47-9041-4b5d-9b77-535fba8b1442`). Padrão: 1200 s na tomada e 600 s na bateria (confirmado via API).
- **Comportamento real:** o spin-up de um HDD leva alguns segundos. Não afeta NVMe.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK DISKIDLE 1200` e `/setactive`.

### `storage.nvme.idletimeout` — Economia ociosa do NVMe (C)

- **Documentação oficial:** Microsoft Learn, *Power management for storage hardware devices – NVMe*. Após o *idle timeout*, o StorNVMe escolhe o estado mais profundo cujo ENLAT+EXLAT cabe na tolerância configurada. GUIDs: Primary NVMe Idle Timeout `d639518a-e56d-4345-8af2-b9f32fb26109` (padrão lido: 200/100 ms); tolerância primária `fc95af4d-40e7-4b6d-835a-56d131dbc80e` (15/50); tolerância secundária `dbc9e238-6de9-49e3-92cd-8c2b4946b472`; timeout secundário `d3d55efd-c1ff-424e-9dc3-441be7833010` (2000/1000).
- **Resultados publicados:** nenhum benchmark independente mostra ganho perceptível. A latência de saída dos estados é de µs a poucos ms, só no primeiro I/O após ociosidade.
- **Regressões:** SSD mais quente e maior consumo; pode afetar o Modern Standby.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT SUB_DISK d639518a-e56d-4345-8af2-b9f32fb26109 200`, depois `/setactive`.

### `storage.ntfs.memoryusage` — NtfsMemoryUsage (C)

- **Documentação oficial:** `fsutil behavior memoryusage`. O valor 2 pode ajudar com muitos arquivos abertos repetidamente e pode reduzir o desempenho geral se a memória já estiver disputada.
- **Reversão:** `fsutil behavior set memoryusage 1` (ou apague o valor) e reinicie.

### AHCI Link Power Management – HIPM/DIPM (doc, C)

- Configuração oculta `0b2d69d7-a2a1-449c-9680-f91c70521c60` (0 Ativo, 1 HIPM, 2 HIPM+DIPM, 3 DIPM, 4 Mais baixo). Afeta apenas SATA (AHCI). Os padrões variam por plataforma e não há evidência de ganho perceptível. Ficou fora do JSON por não termos um padrão genérico confirmado. Reversão: valor do snapshot via `powercfg /setacvalueindex`.

---

## Windows

### `windows.visual.menushowdelay`, `windows.visual.minanimate`, `windows.visual.taskbaranimations` — Atrasos e animações (B)

- **Documentação oficial:** `SystemParametersInfo` (Microsoft Learn): SPI_SETMENUSHOWDELAY define o tempo, em ms, antes de exibir um submenu; SPI_SETANIMATION controla as animações de janela. Valores de registro: `HKCU\Control Panel\Desktop\MenuShowDelay` (REG_SZ, padrão "400"), `HKCU\Control Panel\Desktop\WindowMetrics\MinAnimate` (REG_SZ "1"), `HKCU\...\Explorer\Advanced\TaskbarAnimations` (DWORD 1).
- **Comportamento real:** o efeito é exatamente o atraso ou a animação removida. A sensação de agilidade melhora; FPS e throughput não mudam.
- **Regressões:** com 0 ms, submenus podem abrir por acidente.
- **Observação:** gravar direto no registro só vale após sair e entrar de novo na sessão (ou reiniciar o Explorer), porque não há broadcast de `WM_SETTINGCHANGE`. Ver *Limitações*.
- **Reversão:** `reg add "HKCU\Control Panel\Desktop" /v MenuShowDelay /t REG_SZ /d 400 /f`; `reg add "HKCU\Control Panel\Desktop\WindowMetrics" /v MinAnimate /t REG_SZ /d 1 /f`; `reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v TaskbarAnimations /t REG_DWORD /d 1 /f`.

### `windows.visual.transparency` — Transparência (B)

- O acrílico e o mica são compostos pelo DWM na GPU. O Windows desliga a transparência na Economia de bateria, sinal de que ela tem custo de energia. Em iGPUs fracas, desligar é um alívio plausível; em GPUs dedicadas, o efeito em jogos é desprezível. Não há benchmark rigoroso.
- **Reversão:** `reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize" /v EnableTransparency /t REG_DWORD /d 1 /f`.

### Efeitos visuais "Ajustar para melhor desempenho" (doc, B)

- `HKCU\...\Explorer\VisualEffects\VisualFXSetting` (0 automático, 1 aparência, 2 desempenho, 3 personalizado) só registra a escolha da UI. Os efeitos reais ficam em `UserPreferencesMask` (REG_BINARY, máscara de bits que muda entre builds) e só valem por meio de `SystemParametersInfo`. Por isso não há alvo estático confiável. O catálogo cobre os componentes individuais (MinAnimate, TaskbarAnimations, MenuShowDelay, transparência).

### `windows.backgroundapps.win10` e `windows.backgroundapps.policy` — Apps em segundo plano (B)

- **Documentação oficial:** *Enterprise background activity controls* (Microsoft Learn) e Policy CSP Privacy (`LetAppsRunInBackground`: 0 o usuário decide, 1 forçar permitir, 2 forçar negar). O guia de conexões do Windows avisa que, com "forçar negar", a pesquisa e a Cortana podem não funcionar como esperado.
- **Windows 10:** chave global do usuário `BackgroundAccessApplications\GlobalUserDisabled`. O interruptor global não existe no Windows 11 (só por app), por isso o item Win10 tem `maxBuild` 19045.
- **Comportamento real:** afeta só apps UWP/Store; programas Win32 continuam iguais. O ganho é proporcional à quantidade de apps Store com tarefas de fundo, geralmente poucos.
- **Regressões:** notificações e sincronização de apps Store param com o app fechado.
- **Reversão:** `reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\AppPrivacy" /v LetAppsRunInBackground /f`; `reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\BackgroundAccessApplications" /v GlobalUserDisabled /f`.

### `windows.notifications.toast` — Notificações (B, sem efeito de desempenho)

- Interruptor global `PushNotifications\ToastEnabled`. O benefício é só de foco. O Windows 11 já ativa "Não incomodar" automaticamente em jogos em tela cheia.
- **Reversão:** `reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\PushNotifications" /v ToastEnabled /t REG_DWORD /d 1 /f` e saia/entre na sessão.

### `windows.widgets.policy` — Widgets (B) e botão TaskbarDa (doc)

- **Política usada:** `HKLM\SOFTWARE\Policies\Microsoft\Dsh\AllowNewsAndInterests = 0` (política "Permitir widgets"). Desliga o painel e o botão.
- **TaskbarDa (doc):** `HKCU\...\Explorer\Advanced\TaskbarDa` ficou protegido pelo driver **UCPD** (User Choice Protection Driver) em atualizações recentes. Gravações externas retornam "acesso negado" até em prompt elevado (ElevenForum, oofhours.com, Microsoft Q&A de novembro de 2025: "now system-managed"). Os contornos da comunidade desativam o UCPD, um componente de proteção, e por isso **não** são usados pelo RBM Optimizer.
- **Resultados:** a economia é a memória dos processos WebView2 do painel. Não há ganho de FPS em PCs com RAM suficiente.
- **Reversão:** `reg delete "HKLM\SOFTWARE\Policies\Microsoft\Dsh" /v AllowNewsAndInterests /f`.

### `windows.startup.delay` — Atraso de apps de inicialização (B)

- `HKCU\...\Explorer\Serialize\StartupDelayInMSec = 0` remove o atraso (cerca de 10 s) que o Explorer aplica aos itens das chaves Run e da pasta Inicializar. Não há documentação oficial; o mecanismo é amplamente reproduzido.
- **Efeito real:** troca de prioridade. Os itens de inicialização abrem antes, mas a área de trabalho pode demorar mais a responder.
- **Reversão:** `reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Serialize" /v StartupDelayInMSec /f`.

### `windows.faststartup` — Inicialização rápida (B)

- **Comportamento:** com `HiberbootEnabled = 1`, o comando Desligar hiberna a sessão do kernel (hiberfil.sys). Reiniciar sempre faz boot completo. Requer hibernação habilitada.
- **Prós:** boot a frio mais rápido, sobretudo em HDD.
- **Contras:** o tempo de atividade do kernel se acumula; volumes NTFS ficam bloqueados para dual boot; alguns drivers não reinicializam corretamente.
- **Reversão:** `reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Power" /v HiberbootEnabled /t REG_DWORD /d 1 /f`.

### `windows.explorer.showfileext` — Mostrar extensões (B, recomendado `show`)

- **Evidência:** MITRE ATT&CK T1036.007 (extensão dupla) recomenda desativar a ocultação de extensões conhecidas; o CybSafe classifica o comportamento como redutor de risco de malware. Não há efeito de desempenho.
- **Reversão:** `reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v HideFileExt /t REG_DWORD /d 1 /f`.

### `windows.explorer.launchto` — Abrir em "Este Computador" (B, preferência)

- `LaunchTo = 1`. É uma preferência de usabilidade. Os relatos de que a página Início abre mais devagar com Microsoft 365/OneDrive são anedóticos.
- **Reversão:** `reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced" /v LaunchTo /f`.

---

## Gaming

### `gaming.gamemode` — Modo de Jogo (B, recomendado = padrão `on`)

- **Comportamento:** prioriza o jogo em primeiro plano e evita instalação de drivers pelo Windows Update e notificações de reinício durante a sessão. Valor: `HKCU\Software\Microsoft\GameBar\AutoGameModeEnabled` (ausente = 1).
- **Resultados publicados:** o teste da XDA citado pela iqondigital mediu +2,7–3,3% de média em Fortnite, TLOU II e Hogwarts Legacy. A windowsnews.ai mostra perdas em títulos pesados de CPU (ex.: 64,6 → 61,8 FPS em um deles). Há também o teste da Noticias3D (RTX 3090 e i7-10700K).
- **Conclusão:** manter ligado (padrão). Desligar apenas para um jogo específico que piore.
- **Reversão:** `reg add "HKCU\Software\Microsoft\GameBar" /v AutoGameModeEnabled /t REG_DWORD /d 1 /f`.

### `gaming.gamedvr.backgroundrecording` — Gravação em segundo plano (B, recomendado = padrão `off`)

- `HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR\HistoricalCaptureEnabled`. Desligado por padrão. Quando ligado, codifica vídeo continuamente durante todo o jogo.
- **Resultados:** a magnitude do custo varia com o codificador de hardware. Os números publicados (ex.: "200–400 MB de RAM, +18–23 ms") não são verificáveis e não foram usados.
- **Reversão:** `reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR" /v HistoricalCaptureEnabled /t REG_DWORD /d 0 /f`.

### `gaming.gamedvr.capture` e `gaming.gamedvr.policy` — Capturas do Game Bar (B)

- Valores por usuário `AppCaptureEnabled` e `GameConfigStore\GameDVR_Enabled`. A política de máquina é `HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR\AllowGameDVR = 0`.
- **Evidência:** com a gravação em segundo plano desligada, o custo do recurso ocioso é pequeno e os relatos de ganho são anedóticos. Mantido como opção para quem usa outro software de gravação.
- **Reversão:** `reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\GameDVR" /v AppCaptureEnabled /t REG_DWORD /d 1 /f`; `reg add "HKCU\System\GameConfigStore" /v GameDVR_Enabled /t REG_DWORD /d 1 /f`; `reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\GameDVR" /v AllowGameDVR /f`.

### `gaming.fso.global` — Desativar FSO globalmente (C)

- **Origem:** Guru3D, Blur Busters, winutil ("Disable Fullscreen Optimizations") e scripts no PowerShell Gallery.
- **Valores:** `GameConfigStore`: `GameDVR_FSEBehaviorMode = 2`, `GameDVR_HonorUserFSEBehaviorMode = 1`, `GameDVR_DXGIHonorFSEWindowsCompatible = 1`, `GameDVR_FSEBehavior = 2`.
- **Documentação oficial:** inexistente. Uma resposta no Microsoft Q&A interpreta esses valores como limites de recursos do Game DVR, não como liga/desliga de FSO.
- **Contraditório:** no Guru3D, um usuário relata que só a combinação completa afeta jogos antigos; outro relata FSO ainda ativo. No Blur Busters há a recomendação de **não** desativar FSO em jogos DX12 modernos.
- **Regressões:** alt-tab mais lento e overlays ausentes; o winutil alerta que o gerenciamento de cor deixa de funcionar em tela cheia exclusiva.
- **Observação:** o padrão de `GameDVR_FSEBehaviorMode` varia entre builds (0 na máquina de referência; 2 em outras instalações). O catálogo aceita os dois como padrão.
- **Reversão:** restaure os 4 valores do snapshot.

### `gaming.mmcss.systemresponsiveness` — SystemResponsiveness (D)

- **Documentação oficial (decisiva):** Microsoft Learn, *Multimedia Class Scheduler Service* (revisão de 2025). Valores não múltiplos de 10 são arredondados para baixo; **valores abaixo de 10 e acima de 100 são ajustados para 20**; 100 desabilita o MMCSS. Logo, o popular `SystemResponsiveness = 0` equivale ao padrão (20). Documentação MSDN mais antiga dizia que 0 era tratado como 10; em ambos os casos o "0" não faz o que os guias prometem.
- **Escopo real:** só afeta threads registradas no MMCSS (Áudio, Pro Audio, Captura...), não o jogo como um todo.
- **Reversão:** `reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile" /v SystemResponsiveness /t REG_DWORD /d 20 /f`.

### `gaming.mmcss.gamestask` — Prioridades da tarefa "Games" (D)

- **Documentação oficial:** mesma página do MMCSS. "GPU Priority … This priority is not yet used." "SFIO Priority … This value is not used." Priority e Scheduling Category só valem para threads que chamam `AvSetMmThreadCharacteristics("Games")`, e poucos jogos fazem isso. Com Scheduling Category High, as threads registradas vão para a faixa 23–26, acima de quase tudo, o que pode prejudicar áudio e entrada.
- **Padrões lidos:** GPU Priority 8, Priority 2, Scheduling Category "Medium", SFIO Priority "Normal".
- **Reversão:** grave os 4 valores padrão acima.

### `gaming.mouse.acceleration` — Aceleração do mouse (B)

- SPI_SETMOUSE (limiar 1, limiar 2, aceleração) corresponde a `HKCU\Control Panel\Mouse` MouseSpeed/MouseThreshold1/MouseThreshold2: padrão "1"/"6"/"10"; desligado "0"/"0"/"0".
- **Efeito:** consistência de mira (1:1), não FPS. Jogos com raw input já ignoram a opção.
- **Reversão:** grave "1", "6", "10" e saia/entre na sessão.

### Serviços Xbox (doc, D)

- Desativar XblAuthManager, XblGameSave, XboxNetApiSvc e GamingServices não traz ganho medido (são serviços sob demanda, Manual) e quebra Game Pass, saves na nuvem e conquistas. Não incluído.

---

## Rede

### `network.mmcss.throttlingindex` — NetworkThrottlingIndex (D)

- **Mecanismo:** com reprodução multimídia registrada no MMCSS, o processamento de pacotes recebidos não multimídia é limitado (padrão 10 ≈ 10 mil pacotes/s, cerca de 15 Mbps com MTU 1500).
- **Pesquisa medida:** djdallmann/GamingPCSetup (xperf + iperf, Windows 10 1909, Intel Gigabit CT): a afirmação de que desativar melhora a latência "não é totalmente verdadeira"; "a latência de DPC do NDIS.sys aumenta notavelmente quando desativado".
- **Contraditório:** no Blur Busters há relatos subjetivos opostos (70 "bom", 10 e 0xFFFFFFFF "ruins"), sem medição. O SpeedGuide recomenda 0xFFFFFFFF para vazão.
- **Conclusão:** jogos online usam dezenas de pacotes por segundo, então o limite não interfere no ping. Placebo ou contraproducente.
- **Reversão:** `reg add "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile" /v NetworkThrottlingIndex /t REG_DWORD /d 10 /f`.

### `network.qos.nonbesteffortlimit` — "Os 20% reservados" (D)

- **Evidência:** Raymond Chen (Microsoft, *The Old New Thing*, 23/11/2006), "Placebo setting: QoS bandwidth reservation": o valor é um **teto** de reserva, e a banda não usada fica disponível para todo o tráfego. Zerar pode causar falhas em apps que dependem de reserva.
- **Reversão:** `reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\Psched" /v NonBestEffortLimit /f`.

### `network.deliveryoptimization` — Otimização de Entrega (B)

- **Documentação oficial:** *Delivery Optimization reference* e a enumeração `DownloadMode` (0 CdnOnly, 1 Lan [padrão], 2 Group, 3 Internet, 99 Simple, 100 Bypass). As atualizações continuam chegando com qualquer modo; o 0 apenas desliga o P2P.
- **Benefício:** em conexões com upload fraco ou franquia de dados.
- **Reversão:** `reg delete "HKLM\SOFTWARE\Policies\Microsoft\Windows\DeliveryOptimization" /v DODownloadMode /f`.

### `network.wifi.powersaving` — Economia de energia do Wi‑Fi (B)

- **Documentação oficial:** KB 928152 (Microsoft). Na bateria, "Economia média" ativa o modo de economia 802.11; a solução documentada é "Desempenho máximo" (índice 0) na bateria, ao custo de autonomia. A Intel alerta que mudar o padrão pode causar problemas de conectividade e que, nos adaptadores dela, Baixa e Média se comportam igual. Alguns guias de terceiros invertem o mapeamento dos índices; prevalece o da Microsoft (0 = Desempenho máximo), coerente com o padrão lido (AC=0, DC=2).
- **Reversão:** `powercfg /setdcvalueindex SCHEME_CURRENT 19cbb8fa-5279-450e-9fac-8a3d5fedd0c1 12bbebe6-58d6-4636-95bb-3217ef867c1a 2` e `/setactive`.

### Nagle — TcpAckFrequency / TCPNoDelay (doc, D)

- **Por que não está no JSON:** os valores ficam em `HKLM\SYSTEM\CurrentControlSet\Services\Tcpip\Parameters\Interfaces\{GUID-da-interface}`, por interface, e não podem ser alvos estáticos.
- **Comportamento real:** TcpAckFrequency=1 desativa o ACK atrasado; TCPNoDelay desativa o algoritmo de Nagle (aplicação incerta no Windows moderno, já que o socket define `TCP_NODELAY`). A maioria dos jogos competitivos usa **UDP**, que não tem Nagle nem ACK atrasado. Jogos TCP sensíveis (alguns MMOs antigos) já usam `TCP_NODELAY` no próprio socket.
- **Resultados publicados:** não encontramos medições controladas. Os relatos de ganho são de MMOs antigos (WoW, era 2008–2012).
- **Regressões:** mais pacotes ACK e um pouco mais de CPU e tráfego.
- **Reversão:** apague os valores na chave da interface.

### Auto-tuning do TCP — `netsh int tcp set global autotuninglevel` (doc, D; desativar pode ser E)

- **Não está no JSON:** é um comando `netsh`/`Set-NetTCPSetting`, não um alvo de registro estático suportado.
- **Comportamento:** ajusta dinamicamente a janela de recepção. `disabled` limita a janela a 64 KB, o que derruba a vazão em links de alta latência ou alta banda. É recomendado apenas para diagnosticar roteadores problemáticos. O padrão `normal` deve ser mantido.
- **Reversão:** `netsh int tcp set global autotuninglevel=normal`.

### Moderação de interrupções, RSS e offloads da placa de rede (doc, B por dispositivo)

- **Por que não está no JSON:** são propriedades avançadas do driver (`HKLM\SYSTEM\CurrentControlSet\Control\Class\{4d36e972-...}\00XX`), específicas de cada dispositivo.
- **Pesquisa medida:** djdallmann (Intel CT, xperf). A moderação afeta mais a latência de DPC que a de ISR. As interrupções vão de cerca de 92 mil (Extreme) a 2,65 milhões (Off). "Medium" teve o melhor equilíbrio para jogos. A própria página tem inconsistência entre texto e dados (90% vs 30% de DPCs ≤ 1 µs). No I225-V, a tabela RSS estava ausente por falha da INF.
- **Recomendação:** tratar na ferramenta de dispositivos do app; medir antes com WPR/WPA.

---

## Energia

### `power.usb.selectivesuspend` — Suspensão seletiva USB (B)

- **Documentação oficial:** *USB selective suspend* (Microsoft Learn) e o blog *Demystifying USB selective suspend* (Microsoft USB team). O driver de hub suspende portas individuais ociosas. GUID `48e6b7a6-50f5-4782-a5d4-53bb8f07e226` (subgrupo `2a737441-1930-4402-8d77-b2bebba308a3`), padrão 1/1.
- **Benefício:** corrige desconexões e atrasos de periféricos específicos (DACs, controles). HIDs em uso não são suspensos.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT 2a737441-1930-4402-8d77-b2bebba308a3 48e6b7a6-50f5-4782-a5d4-53bb8f07e226 1` (o mesmo para DC) e `/setactive`.

### `power.pcie.aspm` — ASPM do PCI Express (B)

- **Comportamento:** 0 desligado, 1 L0s (moderado), 2 L1 (máximo). Só tem efeito se o firmware delega ASPM ao SO (_OSC). Padrão lido no Equilibrado: AC=1/DC=2.
- **Relatos:** TechPowerUp e AnandTech registram correções de stutter e instabilidade com ASPM desligado. Também há casos em que não resolveu (r/AMDHelp, onde a causa eram C-states no BIOS). Não há benchmark controlado.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT 501a4d13-42af-4429-9fd1-a8218c268e20 ee12f906-d277-404b-b6da-e5fa1a576df5 1` e `/setactive`.

### `power.sleep.timeout` — Suspender após (B)

- STANDBYIDLE `29f6c1db-86da-48c5-9fdb-f2b67b1f44da`. Padrão lido no Windows 11 26200: 900 s na tomada e 600 s na bateria. Builds antigas usavam 1800/900. O benefício é funcional (renders longos), não de desempenho.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT SUB_SLEEP STANDBYIDLE 900` e `/setactive`.

### Plano "Desempenho Máximo" / Ultimate Performance (doc, C)

- É criado com `powercfg -duplicatescheme e9a42b02-d5df-448d-aa00-03f14749eb61`. Equivale ao Alto desempenho com ajustes adicionais (sem economia de disco/PCIe e estado mínimo do processador alto). Não é alvo do motor porque trocar o esquema ativo não é um "setting". O RBM Optimizer aplica ajustes individuais ao esquema ativo, o que é mais granular e reversível. Benefício em desktops modernos com CPPC: não demonstrado. Custo: consumo ocioso maior.

### Arquivo de hibernação — `powercfg -h off` (doc, B)

- Libera alguns GB do `hiberfil.sys` (cerca de 40% da RAM por padrão) e desativa a Inicialização rápida e a hibernação. Não é desempenho; é espaço em disco. É um comando, não um alvo de registro suportado (o `HibernateEnabled` em `Control\Power` não remove o arquivo de forma consistente).
- **Reversão:** `powercfg -h on`.

---

## Privacidade

Todos os itens abaixo usam `privacy: true` e não fazem afirmações de desempenho. Fonte principal: Microsoft Learn, *Manage connections from Windows operating system components to Microsoft services*.

| Item | Alvo | Nota | Observações |
|---|---|:-:|---|
| `privacy.telemetry.level` | `HKLM\SOFTWARE\Policies\Microsoft\Windows\DataCollection\AllowTelemetry` | A | 0 (Segurança) só no Enterprise/Education; Home/Pro tratam como 1. O Windows 11 não usa o nível 2. Recomendado: 1. Reversão: apagar o valor. |
| `privacy.diagtrack.service` | Serviço DiagTrack | B | Desempenho: nenhum ganho demonstrado. Efeitos colaterais: Insider, diagnóstico. Reversão: `sc config DiagTrack start= auto`. |
| `privacy.advertisingid` | `HKLM\SOFTWARE\Policies\Microsoft\Windows\AdvertisingInfo\DisabledByGroupPolicy=1` | A | Reversão: apagar o valor. |
| `privacy.tailoredexperiences` | `HKCU\SOFTWARE\Policies\Microsoft\Windows\CloudContent\DisableTailoredExperiencesWithDiagnosticData=1` | A | Reversão: apagar. |
| `privacy.activityhistory` | `HKLM\...\Windows\System`: EnableActivityFeed, PublishUserActivities, UploadUserActivities = 0 | A | Reversão: apagar os 3 valores. |
| `privacy.search.web` | `HKCU\SOFTWARE\Policies\Microsoft\Windows\Explorer\DisableSearchBoxSuggestions=1` + `HKCU\...\Search\BingSearchEnabled=0` | C | Eficácia contraditória no Windows 11 (Pureinfotech e comentários no Woshub dizem que não remove os resultados da web em builds recentes). Reversão: apagar ambos. |
| `privacy.feedback.frequency` | `HKCU\Software\Microsoft\Siuf\Rules` NumberOfSIUFInPeriod=0, PeriodInNanoSeconds=0 | A | "Automaticamente" = apagar ambos (documentado). |
| `privacy.location` | `HKLM\...\LocationAndSensors\DisableLocation=1` | A | Não recomendado por padrão (quebra o fuso automático e o Encontrar meu dispositivo). |
| `privacy.consumerfeatures` | `HKLM\...\CloudContent\DisableWindowsConsumerFeatures=1` | B | Efeito pleno apenas no Enterprise/Education. |
| `privacy.consumer.silentapps` | `HKCU\...\ContentDeliveryManager\SilentInstalledAppsEnabled=0` | B | Documentado pela comunidade (ElevenForum, tippsling, Microsoft Q&A). Pode não remover atalhos já provisionados. |
| `privacy.consumer.startsuggestions` | `...\SystemPaneSuggestionsEnabled=0` | B | Efeito principal no Windows 10. |
| `privacy.consumer.tips` | `...\SubscribedContent-338389Enabled=0` | B | IDs SubscribedContent são internos da Microsoft. |
| `privacy.clearpagefile` | `Memory Management\ClearPageFileAtShutdown=1` | B | Política de segurança; desligamento muito mais lento (a documentação antiga cita +30 min em cenários grandes). O BitLocker protege melhor. Não recomendado por padrão. |
| `privacy.ceip.consolidator` | Tarefa `\Microsoft\Windows\Customer Experience Improvement Program\Consolidator` | C | Relevância residual incerta; a tarefa pode não existir em builds novas. |
| `privacy.inking.typing` | `HKCU\Software\Microsoft\InputPersonalization` RestrictImplicitInkCollection/TextCollection=1 | B | Corresponde à política "Desativar aprendizado automático". |
| `privacy.start.trackprogs` | `HKCU\...\Explorer\Advanced\Start_TrackProgs=0` | B | Dados ficam locais; remove "Mais usados". |

### Compatibility Appraiser / CompatTelRunner (doc, não aplicar)

- A tarefa `\Microsoft\Windows\Application Experience\Microsoft Compatibility Appraiser` às vezes causa picos de CPU e disco. Porém, ela alimenta as avaliações de compatibilidade (*safeguard holds*) das atualizações de recursos. Desativar pode levar a atualizações em hardware ou software incompatível, o que contraria a regra de não reduzir a segurança das atualizações. Não incluída.

---

## Advanced Lab (experimental)

Os itens do Lab são aplicados **somente individualmente**, com snapshot e o procedimento de benchmark indicado. Itens com nota E têm a aplicação bloqueada pelo app e servem de documentação.

### `lab.bcd.useplatformclock` — Forçar HPET (E)

- **Documentação oficial:** BCDEdit /set (Microsoft Learn): "Forces the use of the platform clock as the system's performance counter. **This option should only be used for debugging.**"
- **Comportamento real:** o QPC deixa de usar o TSC invariante (10 MHz) e passa ao HPET (14,318/19,2/24 MHz conforme a placa). Ler o HPET é uma operação de E/S muito mais cara que `rdtsc`.
- **Resultados publicados:** TimerBench/Blur Busters mostram a troca de frequência. O artigo do Overclockers.at, "The HPET bug: what it is and what it isn't", discute as regressões. O Melody's Tweaks esclarece equívocos sobre HPET/TSC/PMT.
- **Contraditório:** guias antigos (Neowin, 2012) prometiam FPS com HPET forçado, sem comparação válida. Muitos confundem "apagar useplatformclock" com "desativar o HPET". Apagar apenas devolve a escolha ao Windows, que é o estado correto.
- **Reversão:** `bcdedit /deletevalue {current} useplatformclock` e reinicie.

### `lab.bcd.useplatformtick` — Tick de plataforma (C)

- Microsoft: "Forces the clock to be backed by a platform source, no synthetic timers are allowed… only for debugging." Os relatos são mistos: melhora subjetiva em alguns casos e perda single-core desde o 1803 em outros (Blur Busters). Não há benchmark controlado.
- **Reversão:** `bcdedit /deletevalue {current} useplatformtick`.

### `lab.bcd.disabledynamictick` — Desativar tick dinâmico (C)

- Microsoft: "Enables and disables dynamic timer tick feature… only for debugging." Recomendado pelo *Calypto's Latency Guide*, sem medições publicadas de frametime ou latência de entrada. Custo esperado: mais interrupções de clock em ociosidade, mais consumo e alguns graus a mais.
- **Reversão:** `bcdedit /deletevalue {current} disabledynamictick`.

### `lab.bcd.tscsyncpolicy` — Política de sincronização do TSC (D)

- Microsoft: "Controls the times stamp counter synchronization policy. This option should only be used for debugging. Can be Default, Legacy or Enhanced." Em CPUs modernas o TSC é invariante e sincronizado por hardware. Não há evidência de ganho; as explicações de fórum sobre "Enhanced" não são oficiais.
- **Reversão:** `bcdedit /deletevalue {current} tscsyncpolicy`.

### `lab.timer.globalrequests` — GlobalTimerResolutionRequests (C)

- **Documentação oficial:** `timeBeginPeriod` (Microsoft Learn). Desde o Windows 10 2004 a função "no longer affects global timer resolution". No Windows 11, processos com janelas ocultas, minimizadas ou inaudíveis perdem a garantia de resolução. A Microsoft alerta que resoluções altas "can also reduce overall system performance… prevent the CPU power management system from entering power-saving modes".
- **Valor:** `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel\GlobalTimerResolutionRequests = 1`, apenas Windows 11 e Server 2022+. Não é documentado pela Microsoft. Fontes: README do valleyofdoom/TimerResolution ("should only be used for debugging purposes"), PCGamingWiki e Wagnardsoft.
- **Comportamento real:** sozinho não faz nada. Só torna globais as solicitações de resolução feitas por outros processos (ex.: SetTimerResolution).
- **Resultados publicados:** a precisão de `Sleep(1)` melhora com resolução de 0,5 ms (ferramenta MeasureSleep), o que pode ajudar limitadores de FPS que usam Sleep. Um comentário no Guru3D diz que não melhora latências. Não há estudo controlado.
- **Reversão:** `reg delete "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\kernel" /v GlobalTimerResolutionRequests /f` e reinicie.

### `lab.cpu.win32priorityseparation` — Win32PrioritySeparation (C)

- **Documentação:** TechNet (Windows 2000, *Cc976120*) e o blog Microsoft *Master your quantum*. Bitmask de 6 bits **AABBCC**:
  - **AA** (bits 4–5), duração do quantum: 1 = longo, 2 = curto, 0/3 = padrão (curto no cliente, longo no Server).
  - **BB** (bits 2–3), variável ou fixo: 1 = variável, 2 = fixo, 0/3 = padrão (variável no cliente, fixo no Server).
  - **CC** (bits 0–1), boost do quantum de primeiro plano: índice na tabela de quanta, 0 = 1x, 1 = 2x, 2 = 3x. Com quantum **fixo**, a tabela tem entradas iguais e o boost é ignorado.
- **Equivalências** (cliente):

| Decimal | Hex | Binário | Significado | Equivale a |
|---:|---:|---|---|---|
| 2 | 0x02 | 00 00 10 | padrão (curto, variável), 3x | **38** — é o padrão |
| 38 | 0x26 | 10 01 10 | curto, variável, 3x | 2 (padrão) |
| 36 | 0x24 | 10 01 00 | curto, variável, 1x | — |
| 40 | 0x28 | 10 10 00 | curto, fixo | 42 |
| 42 | 0x2A | 10 10 10 | curto, fixo (boost ignorado) | 40 |
| 24 | 0x18 | 01 10 00 | longo, fixo ("Serviços em segundo plano") | 26 |
| 26 | 0x1A | 01 10 10 | longo, fixo (boost ignorado) | 24 |
| 28 | 0x1C | 01 11 00 | longo, variável (padrão), 1x | — |
| 22 | 0x16 | 01 01 10 | longo, variável, 3x | — |

- **Resultados publicados:** há vídeos de benchmark da comunidade (ex.: Khorvie Tech) e ferramentas para alternar valores (LuSlower/win32ps-changer), mas não encontramos metodologia controlada e reprodutível nem estudo independente que mostre redução de latência. Uma issue no GitHub pergunta se 0x28 seria "o mais rápido" e não há confirmação. **38 é idêntico ao padrão**, e muitos "ganhos" relatados com 38 são placebo por definição.
- **Regressões:** quantum fixo ou longo reduz o favorecimento do primeiro plano e pode piorar a responsividade com carga de fundo.
- **Reversão:** `reg add "HKLM\SYSTEM\CurrentControlSet\Control\PriorityControl" /v Win32PrioritySeparation /t REG_DWORD /d 2 /f` (efeito imediato).

### `lab.cpu.powerthrottling` — PowerThrottlingOff (C)

- **Documentação/mecanismo:** EcoQoS (blog Sustainable Software da Microsoft). Processos e threads de fundo podem rodar em modo de eficiência (a Microsoft cita "até 90%" menos consumo de CPU nesse trabalho). A chave `HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling\PowerThrottlingOff=1` não existe por padrão. Um guia cita um caminho de política alternativo, não confirmado na documentação oficial.
- **Resultados:** o primeiro plano não é limitado, então não há ganho demonstrado em jogos. No Microsoft Q&A há casos de apps prejudicados pelo modo de eficiência; o Gerenciador de Tarefas tem uma opção por processo, preferível.
- **Reversão:** `reg delete "HKLM\SYSTEM\CurrentControlSet\Control\Power\PowerThrottling" /v PowerThrottlingOff /f` e reinicie.

### `lab.cpu.coreparking` — Core parking (C)

- **Documentação oficial:** CPMINCORES (Microsoft Learn); 100% desativa o algoritmo. A Microsoft orienta alterar parâmetros de core parking apenas com o fabricante do processador. O blog antigo *Tweaking CPU Core Parking* traz a referência de comandos.
- **Padrão:** varia por plataforma. Na máquina AMD de referência o padrão do Equilibrado já é 100% na tomada (pacote PPM do fabricante); o genérico usado no catálogo é 10/10.
- **Resultados:** relatos anedóticos (ParkControl). Não há benchmark independente.
- **Reversão:** valor do snapshot via `powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR CPMINCORES <v>` e `/setactive`.

### `lab.cpu.minstate` — Estado mínimo de 100% (D)

- Com CPPC/HWP a rampa é feita pelo hardware em escala de ms ou menos. Não há ganho medido. O calor extra reduz o boost sustentado.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR PROCTHROTTLEMIN 5` (ou o valor do snapshot) e `/setactive`.

### `lab.cpu.idledisable` — Desativar C-states (E)

- IDLEDISABLE `5d76a2ca-e8c0-402f-a133-2158492d58ad`. A CPU fica em polling permanente: alto consumo e calor, e menor boost oportunista. É usado só em nichos de latência extrema, com resfriamento dedicado. Contraindicado.
- **Reversão:** `powercfg /setacvalueindex SCHEME_CURRENT SUB_PROCESSOR IDLEDISABLE 0` (o mesmo para DC) e `/setactive`.

### `lab.process.csrsspriority` — Prioridade do csrss via IFEO (E)

- `IFEO\csrss.exe\PerfOptions` CpuPriorityClass=4 e IoPriority=3. O csrss é um processo protegido e crítico. Não há documentação de PerfOptions para processos protegidos nem medição de ganho. Grava em IFEO, chave usada por malware para persistência e por isso monitorada por EDRs. Contraindicado.
- **Reversão:** `reg delete "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\csrss.exe\PerfOptions" /f` (apague apenas a subchave PerfOptions criada).

### `lab.memory.disablepagingexecutive` — DisablePagingExecutive (D)

- É herança da era XP. Com RAM suficiente, a paginação do executivo é rara. Não há benchmark moderno.
- **Reversão:** `reg add "HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management" /v DisablePagingExecutive /t REG_DWORD /d 0 /f` e reinicie.

### Mitigações Spectre/Meltdown — FeatureSettingsOverride (doc, E)

- **Valores:** `HKLM\SYSTEM\CurrentControlSet\Control\Session Manager\Memory Management\FeatureSettingsOverride` e `FeatureSettingsOverrideMask`. O valor 3/3 desativa as mitigações de CVE-2017-5715 (Spectre v2) e CVE-2017-5754 (Meltdown), conforme a resposta no Microsoft Q&A que cita a orientação oficial (KB4072698/KB4073119).
- **Por que não está no JSON:** reduz a segurança. O ganho existe em cargas com muitas chamadas de sistema em CPUs antigas (pré-2018), mas expõe o sistema a vazamento de memória entre processos e entre processo e kernel. A regra do RBM Optimizer é nunca reduzir segurança. Scanners de vulnerabilidade sinalizam o valor como achado.
- **Reversão** (caso o usuário tenha aplicado com outra ferramenta): apague ambos os valores e reinicie.

### Modo MSI de interrupções (doc, C, por dispositivo)

- O modo MSI fica em `HKLM\SYSTEM\CurrentControlSet\Enum\PCI\<VEN_DEV...>\<instância>\Device Parameters\Interrupt Management\MessageSignaledInterruptProperties\MSISupported`. O caminho é por instância de dispositivo, então não pode ser alvo estático. Drivers modernos de GPU e NVMe já usam MSI/MSI-X. Forçar MSI em dispositivos sem suporte pode impedir o boot. Fica para a ferramenta de dispositivos do app, com verificação de suporte do dispositivo.

---

## Limitações do schema e sugestões para o motor

1. **Padrão do esquema de energia (sentinela "schemeDefault").** Os padrões de energia variam por plataforma: pacotes PPM de fabricantes alteram CPMINCORES, PERFEPP e PROCTHROTTLEMIN. Exemplos lidos via `PowerReadACDefaultIndex` na máquina AMD de referência: CPMINCORES AC=100 (genérico 10), PERFEPP AC=0 (genérico 33), PROCTHROTTLEMIN AC=0 (genérico 5). Sugestão: permitir em `PowerSettingTarget` um modo "restaurar o padrão do esquema" resolvido em tempo de execução por `PowerReadACDefaultIndex`/`PowerReadDCDefaultIndex`, e usá-lo na opção `default`. Hoje a opção padrão usa valores genéricos documentados e a reversão real depende do snapshot.
2. **Semântica de `ac`/`dc` nulos na escrita.** O catálogo usa `dc: null` para "não alterar a bateria" em opções só de tomada. O motor deve tratar `null` como "não escrever" na aplicação e como curinga na leitura, o que já é o comportamento de `Matches`.
3. **Tokens dentro de uma string.** `DirectXUserGlobalSettings` guarda vários tokens `chave=valor;` (SwapEffectUpgradeEnable, VRROptimizeEnable, AutoHDREnable). Sugestão: um alvo `registryToken` (hive, path, name, token, valor) que leia e altere só o token, preservando os demais. Isso também permitiria catalogar VRR em janela e Auto HDR.
4. **DWORD sem sinal.** `NtfsDisableLastAccessUpdate` = 0x80000002 = 2147483650. O leitor deve tratar REG_DWORD como `uint`; lido como `int` (-2147483646), `SemanticEquals` não reconheceria o valor.
5. **Aplicação imediata de preferências de UI.** Valores como MenuShowDelay e MouseSpeed só valem após sair e entrar de novo na sessão, a menos que o app chame `SystemParametersInfo(..., SPIF_SENDCHANGE)`. Sugestão: um alvo `spi` ou um gancho de pós-aplicação.
6. **Comandos sem equivalente de registro.** Compressão de memória (`Enable-/Disable-MMAgent`), hibernação (`powercfg -h`), auto-tuning do TCP (`netsh`), troca de esquema de energia e `fsutil` por volume (8dot3 por volume, ReFS TRIM) exigiriam um provedor de "comandos" com leitura/verificação própria.
7. **Alvos por dispositivo ou por interface.** Nagle por interface, propriedades avançadas de NIC, modo MSI e `PagingFiles` por volume ficam para a ferramenta de dispositivos.
8. **Valores protegidos (UCPD).** `TaskbarDa` e outras preferências protegidas retornam "acesso negado". O motor deve reportar `NotApplicable`/erro de forma clara, sem tentar contornar.
9. **Serviço com início atrasado.** `WSearch` usa `AutomaticDelayed` (Start=2 + DelayedAutostart=1). O provedor de serviço precisa ler e gravar os dois valores.
10. **Tarefas agendadas inexistentes.** `privacy.ceip.consolidator` pode não existir em builds novas. O motor deve classificar o caso como `NotApplicable`, não como erro.

---

## Referências

Oficiais (Microsoft):
- https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-perfboostmode
- https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-perf-state-engine-perfenergypreference
- https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/configuration-for-hetero-power-scheduling-schedulingpolicy
- https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/configuration-for-hetero-power-scheduling-shortschedulingpolicy
- https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/options-for-core-parking-cpmincores
- https://learn.microsoft.com/en-us/windows-hardware/customize/power-settings/static-configuration-options-for-core-parking
- https://learn.microsoft.com/en-us/archive/blogs/klince/tweaking-cpu-core-parking
- https://learn.microsoft.com/en-us/windows/win32/procthread/multimedia-class-scheduler-service
- https://learn.microsoft.com/en-us/windows/win32/api/avrt/nf-avrt-avquerysystemresponsiveness
- https://learn.microsoft.com/en-us/windows/win32/api/timeapi/nf-timeapi-timebeginperiod
- https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-systemparametersinfow
- https://learn.microsoft.com/en-us/windows-hardware/drivers/devtest/bcdedit--set
- https://learn.microsoft.com/en-us/windows-hardware/drivers/display/tdr-registry-keys
- https://learn.microsoft.com/en-us/windows-server/administration/windows-commands/fsutil-behavior
- https://learn.microsoft.com/en-us/openspecs/windows_protocols/MS-FSA/4e3695bd-7574-4f24-a223-b4679c065b63
- https://techcommunity.microsoft.com/blog/filecab/disabling-last-access-time-in-windows-vista-to-improve-ntfs-performance/423328
- https://learn.microsoft.com/en-us/windows-hardware/design/component-guidelines/power-management-for-storage-hardware-devices-nvme
- https://learn.microsoft.com/windows-hardware/drivers/usbcon/usb-selective-suspend
- https://techcommunity.microsoft.com/blog/microsoftusbblog/demystifying-usb-selective-suspend/270736
- https://learn.microsoft.com/windows/application-management/svchost-service-refactoring
- https://learn.microsoft.com/en-us/archive/blogs/sankim/largesystemcache
- https://techcommunity.microsoft.com/t5/ask-the-performance-team/prf-memory-management-large-system-cache-issues/ba-p/373756
- https://learn.microsoft.com/en-us/powershell/module/mmagent/enable-mmagent
- https://technet.microsoft.com/en-us/library/jj852257.aspx
- https://learn.microsoft.com/en-us/windows/privacy/manage-connections-from-windows-operating-system-components-to-microsoft-services
- https://learn.microsoft.com/windows/client-management/mdm/policy-csp-privacy
- https://learn.microsoft.com/nl-nl/windows/application-management/enterprise-background-activity-controls
- https://learn.microsoft.com/en-us/windows/deployment/do/waas-delivery-optimization-reference
- https://learn.microsoft.com/en-us/windows/win32/delivery_optimization/do-enumerations
- https://support.microsoft.com/kb/928152
- https://devblogs.microsoft.com/directx/updates-in-graphics-and-gaming/
- https://support.microsoft.com/en-us/topic/3f006843-2c7e-4ed0-9a5e-f9389e535952
- https://devblogs.microsoft.com/sustainable-software/introducing-ecoqos/
- https://devblogs.microsoft.com/oldnewthing/20061123-14/?p=28923
- https://learn.microsoft.com/en-us/archive/blogs/embedded/master-your-quantum
- https://technet.microsoft.com/library/Cc976120
- https://www.hanselman.com/blog/the-real-and-complete-story-does-windows-defragment-your-ssd
- Microsoft Q&A: https://learn.microsoft.com/en-us/answers/a/7368116 · https://learn.microsoft.com/en-us/answers/questions/4052040/efficiency-mode-causing-performance-issue · https://learn.microsoft.com/en-us/answers/questions/3966055/set-processor-performance-energy-preference-policy · https://learn.microsoft.com/en-ca/answers/questions/5638954/protected-registry-keys · https://learn.microsoft.com/en-us/answers/a/890672

Pesquisa independente e comunidade:
- https://djdallmann.github.io/GamingPCSetup/CONTENT/RESEARCH/NETWORK
- https://github.com/valleyofdoom/TimerResolution
- https://community.pcgamingwiki.com/files/file/2563-global-timer-resolution-requests-windows-11
- https://www.wagnardsoft.com/forums/viewtopic.php?p=11479
- https://docs.google.com/document/d/1c2-lUJq74wuYK1WrA_bIvgb89dUN0sj8-hO3vqmrau4/mobilebasic (Calypto's Latency Guide)
- https://forums.blurbusters.com/viewtopic.php?p=88181 · https://forums.blurbusters.com/viewtopic.php?p=106464 · https://forums.blurbusters.com/viewtopic.php?p=61984 · https://forums.blurbusters.com/viewtopic.php?p=64396
- https://overclockers.at/printable/number-crunching/the-hpet-bug-what-it-is-and-what-it-isnt_251222/page_4
- https://sites.google.com/view/melodystweaks/misconceptions-about-timers-hpet-tsc-pmt
- https://forums.tomshardware.com/threads/what-does-the-command-bcdedit-set-tscsyncpolicy-legacy-do.3668385
- https://www.github.com/LuSlower/win32ps-changer
- https://recapio.com/digest/boost-pc-performance-win32-priority-separation-benchmarks-by-khorvie-tech
- https://babeltechreviews.com/?p=27289 · https://www.pcworld.com/article/2339130/should-you-enable-hardware-accelerated-gpu-scheduling-in-windows-11.html · https://www.elevenforum.com/t/my-results-with-windows-hardware-acceleration-gpu-scheduling.5179/
- https://iqondigital.com/learn/pc-optimization/game-mode · https://windowsnews.ai/article/windows-11-game-mode-can-hurt-performance-in-cpu-heavy-games-tests-show-try-this-alternative.441277 · https://www.noticias3d.com/noticia/85396/windows-11-game-mode-activado-desactivado-hay-diferencia.html
- https://winutil.christitus.com/dev/tweaks/z--advanced-tweaks---caution/disablefso · https://winutil.christitus.com/dev/tweaks/customize-preferences/multiplaneoverlay/
- https://tier1settings.com/disable-mpo-to-fix-stutter-and-flickering/
- https://oofhours.com/2025/05/02/what-is-windows-11s-new-ucpd-feature/
- https://attack.mitre.org/techniques/T1036/007/
- https://www.pugetsystems.com/labs/hpc/Working-around-TDR-in-Windows-for-a-better-GPU-computing-experience-777/
- https://tweaks.com/windows/37125/qos-myth/ · https://tweaks.com/windows/37573/incorrect-tweak-enable-large-system-cache/
- https://www.techpowerup.com/forums/threads/high-latencymon-reporting.276538
- https://intel.com/content/www/us/en/support/articles/000046272/wireless/legacy-intel-wireless-products.html
- https://www.tenforums.com/tutorials/139015-enable-disable-ntfs-last-access-time-stamp-updates-windows-10-a-post2090389.html
- https://www.elevenforum.com/t/enable-or-disable-bing-web-suggested-search-results-in-windows-search-on-windows-11.5309/ · https://pureinfotech.com/disable-search-web-results-windows-11/ · https://woshub.com/disable-web-search-windows-start-menu/
- https://r-pufky.github.io/docs/operating-systems/windows/10/21H2/settings/privacy/ink-and-typing-personalization.html
