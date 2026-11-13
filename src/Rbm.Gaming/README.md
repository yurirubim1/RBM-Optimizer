# Rbm.Gaming — Central de Desempenho em Jogos (RBM Optimizer)

Biblioteca (sem UI) do back-end da Central de Desempenho em Jogos do **RBM Optimizer**.
Usa somente dados reais: nenhum FPS é estimado ou inventado. A captura usa **apenas ETW via PresentMon**
(nada é injetado no processo do jogo, então é seguro para anti-cheat).

## Recursos

| Área | O que faz | Classe/arquivo |
|---|---|---|
| Biblioteca de jogos | Steam (registro → `libraryfolders.vdf` → `appmanifest_*.acf`), Epic (`*.item`), GOG (registro), Xbox/Microsoft Store (`.GamingRoot` + `MicrosoftGame.config`), EA app / Ubisoft Connect / Battle.net (entradas de desinstalação, melhor esforço), entradas manuais. Deduplicação por executável/pasta. | `Library/*` |
| Parser VDF | KeyValues texto da Valve: escapes, comentários, tokens sem aspas, condicionais `[$X]`, chaves duplicadas, BOM, arquivo truncado. | `VdfParser` |
| Executável principal | Heurística: descarta launchers/crash reporters/instaladores/redistribuíveis/anti-cheat, prioriza `-Shipping` (Unreal), pasta `_Data` (Unity), nome ≈ pasta, tamanho. Confiança explícita (Alta = manifesto). Guarda o executável de lançamento da loja separado do processo que renderiza (ex.: Fortnite → `FortniteClient-Win64-Shipping.exe`, lançado por `FortniteBootstrapper.exe`). | `ExecutableResolver` |
| Perfis | `GameProfile` imutável + `games.json` em `RbmPaths.DataRoot` com escrita atômica (temp + `File.Replace`, `.bak`), quarentena de arquivo corrompido. | `Profiles/*` |
| Preferência de GPU | Lê/grava `HKCU\Software\Microsoft\DirectX\UserGpuPreferences` (`GpuPreference=0/1/2;`), preserva outros pares (ex.: `SwapEffectUpgradeEnable`), devolve o estado anterior para desfazer. **Sem elevação.** | `GpuPreferenceStore` |
| PresentMon | Localiza em `RbmPaths.Tools` ou em caminho escolhido; baixa a release oficial (`GameTechDev/PresentMon` → `releases/latest` → `PresentMon-X.Y.Z-x64.exe`), confere tamanho, SHA-256 do GitHub e assinatura Authenticode válida da **Intel**; registra o SHA-256 em `presentmon.json`. | `PresentMon/*` |
| Captura | `CaptureAsync` com validação estrita; detecta se o usuário está em "Usuários de log de desempenho". | `PresentMonCapture` |
| Análise de quadros | CSV do 1.x, 2.x com `--v1_metrics`, 2.0–2.2 / `--v2_metrics` e 2.3+ (padrão), detectado pelo cabeçalho. Métricas, stutter, histograma, modos de apresentação, tearing/V-Sync, heurística CPU×GPU, FPS por segundo. | `Frames/*` |
| Correlação | Stutters × telemetria (`TelemetrySnapshot`, ~1 s): contagens, taxa e *lift* versus as janelas sem stutter. Sempre "associação temporal", nunca "causa". Função pura. | `StutterCorrelator` |
| Overlays | Processos conhecidos (Discord, overlay do Steam, NVIDIA/AMD, Game Bar, RTSS, Afterburner, OBS, Medal, Overwolf, Razer Cortex etc.) com notas em pt-BR. Também lê o estado do Game DVR/Game Bar (somente leitura). | `OverlayDetector` |
| Sessões | `FrameSession` / `FrameSessionSummary` (condições informadas pelo chamador). Comparação antes/depois com `Stats.Compare` sobre o FPS por segundo; recusa sessões com menos de 20 s ou com resolução/configuração diferente (ou quando o usuário marca que algo mudou). | `Sessions/*` |

## Definições das métricas

* **FPS médio** = nº de quadros ÷ tempo total (soma dos frametimes). Não é a média dos FPS instantâneos.
* **1% low / 0,1% low** = 1000 ÷ frametime no percentil 99 / 99,9 (interpolação linear R-7, a mesma de `Stats.Percentile`).
  Também há a variante **"média dos piores"**: 1000 ÷ média dos 1% (0,1%) maiores frametimes.
  O 0,1% low só é marcado como confiável com ≥ 1000 quadros.
* **Frametime** = `MsBetweenPresents` (1.x e 2.3+) ou `FrameTime` (2.0–2.2 / `--v2_metrics`).
  O "FPS exibido" usa `MsBetweenDisplayChange` / `DisplayedTime` dos quadros exibidos.
* **Stutter** = frametime > **2×** a mediana dos **30** quadros anteriores **E** a diferença é > **8 ms**
  (`StutterOptions`, configurável). Stutters a menos de 1 s um do outro formam um agrupamento.
* **Heurística CPU × GPU** (só com `CPUBusy`/`GPUBusy` do 2.x): por quadro, "GPU" se GPUBusy ≥ 85% do frametime;
  senão "CPU" se CPUBusy ≥ 85%; senão "limitado" (V-Sync/limitador). O veredito exige ≥ 60% dos quadros,
  senão é "misto". Sem as colunas, o resultado é **"Indeterminado"**. Com V-Sync ligado, um veredito "CPU"
  cai para confiança baixa (apps que esperam o vsync antes do `Present()` inflam o CPUBusy).

## API principal (resumo)

```csharp
// Biblioteca
var scan = await new GameLibraryService().ScanAsync(manualEntries, ct);   // GameLibraryScanResult
OperationResult<DetectedGame> m = GameLibraryService.CreateManualEntry(@"C:\Jogos\x.exe");

// Perfis + GPU
var store = new GameProfileStore();                       // RbmPaths.DataRoot\games.json
await store.UpsertAsync(GameProfile.FromDetected(game), ct);
await store.AddSessionAsync(profileId, session.ToSummary(), ct: ct);
OperationResult<GpuPreferenceReading> prev = GpuPreferenceStore.Set(exe, GpuPreference.HighPerformance);
GpuPreferenceStore.Restore(prev.Value!);                  // desfazer

// PresentMon
var dl   = await new PresentMonDownloader(httpClient).DownloadLatestAsync(progress, ct);
var tool = PresentMonLocator.Find(userChosenPath: null);  // OperationResult<PresentMonTool>
FrameCaptureCapability cap = PresentMonCapture.GetCapability();
var res  = await PresentMonCapture.CaptureAsync(new FrameCaptureRequest("jogo.exe", 60, tool.Value!.Path), outputDir, ct);

// Análise → sessão → correlação
var an = await GamingPerformanceCenter.AnalyzeCsvAsync(res.Value!.CsvPath!, "jogo.exe");
var (session, corr) = GamingPerformanceCenter.BuildSession(gameId, name, res.Value.StartedAt, an.Value!,
    new SessionConditions(PowerPlanName: "...", AppliedTweakIds: tweakIds, Resolution: "1920x1080"), res.Value.CsvPath, telemetryHistory);
OperationResult<SessionComparison> cmp = SessionComparer.Compare(before, after);

// Overlays
IReadOnlyList<OverlayFinding> o = OverlayDetector.DetectRunning();
GameDvrState dvr = OverlayDetector.ReadGameDvrState();
```

Funções puras para testes: `VdfParser.Parse`, `SteamLibraryScanner.ParseLibraryFolders/ParseAppManifest`,
`EpicLibraryScanner.ParseManifest`, `XboxLibraryScanner.ParseGamingRoot/ParseMicrosoftGameConfig`,
`UninstallRegistryScanner.Classify`, `ExecutableResolver.Choose`, `GameLibraryService.Deduplicate`,
`PresentMonCsvParser.Parse*`, `FrameAnalyzer.*`, `StutterCorrelator.Correlate`, `SessionComparer.Compare`,
`PresentMonDownloader.ParseRelease/SelectConsoleAsset`, `PresentMonLocator.BuildCaptureArguments`, `OverlayDetector.Match`.

## PresentMon: versões e flags verificadas

Verificado com o **PresentMon 2.6.0** (release de 21/09/2026, `PresentMon-2.6.0-x64.exe`, assinado por *Intel Corporation*;
SHA-256 `B2A706BC…88F1AF`) executando `--help` e capturas reais neste PC:

* 2.x: `--process_name N --output_file F --timed S --terminate_after_timed --terminate_on_proc_exit --stop_existing_session --no_console_stats --session_name RBMOptimizer_FrameCapture`
  (também existem `--v1_metrics`, `--v2_metrics`, `--terminate_existing_session`, `--restart_as_admin`, `--qpc_time`, `--date_time`).
* 1.x (README 1.9.2): mesmas opções com um hífen, `-no_top` no lugar de `--no_console_stats`.
  `-terminate_existing` (limpeza após cancelamento) não foi verificado num binário 1.x.
* Cabeçalhos reais do 2.6.0: padrão (`TimeInMs, MsBetweenPresents, MsBetweenDisplayChange, MsUntilDisplayed, MsCPUBusy, MsCPUWait, MsGPUBusy, MsGPUWait…`),
  `--v2_metrics` (`CPUStartTime` **em ms**, `FrameTime, CPUBusy, CPUWait, GPUBusy, GPUWait, DisplayLatency, DisplayedTime…`),
  `--v1_metrics` (`TimeInSeconds, msBetweenPresents, Dropped…`). A unidade da coluna de tempo é sempre conferida contra a soma dos frametimes.

## Elevação

| Operação | Elevação |
|---|---|
| Detecção de jogos, perfis, preferência de GPU (HKCU), download do PresentMon, análise, correlação, overlays, Game DVR | **Não** |
| `PresentMonCapture.CaptureAsync` | **Sim**, via op do agente `frames.capture` (`AgentOps.FrameCapture`). **Não** é preciso se o usuário estiver em *Usuários de log de desempenho* (S-1-5-32-559). Neste PC ele está, e a captura sem elevação funcionou para processos do próprio usuário. |

Validação em `CaptureAsync`/`ValidateRequest`, pensada para rodar no RBM.Agent:

* Nome do processo: sem pasta (`\ / :`), termina em `.exe`, começa com letra ou número (impede injeção de opção `-x`),
  aceita só letras, dígitos e ` _.-+()[]!&'~,#`, no máximo 128 caracteres.
* Duração de 1 a 600 s. O PresentMon precisa de caminho absoluto local (sem UNC), nome `PresentMon*.exe` e
  **assinatura válida da Intel sempre**, inclusive em `RbmPaths.Tools`, porque essa pasta é gravável sem privilégio.
* Anti-TOCTOU: o binário é copiado para uma pasta temporária aleatória, a cópia fica aberta sem compartilhamento
  de escrita/exclusão, é verificada (sem cache de assinatura, porque o caminho é novo) e é executada a partir dela.
* A pasta de saída precisa ser absoluta, local e não ser link/junção. O nome do CSV é gerado aleatoriamente.
  Execução via `ProcessRunner` com `ArgumentList` (sem shell). Ao cancelar, a sessão ETW é encerrada.

## Limitações

* Steam não informa o executável no `.acf` (o executável fica no `appinfo.vdf` binário), então ele vem de heurística, com confiança média ou baixa.
* Xbox/Microsoft Store: a pasta `Content` pode ser protegida. Jogos UWP antigos (WindowsApps) não são listados.
  EA/Ubisoft/Battle.net dependem das entradas de desinstalação (melhor esforço).
* PresentMon sem elevação não enxerga processos de outra conta ou muito curtos. Jogos sob outra conta exigem o agente.
* A correlação tem incerteza de ±1 amostra (relógio do PresentMon × telemetria) e mostra **associação**, não causa.
* O FPS por segundo é autocorrelacionado, então o p-valor da comparação tende a ser otimista (há ressalva no resultado).
  A heurística CPU×GPU não substitui um profiler. Com V-Sync ou limitador ativo ela é pouco conclusiva.
* Uma mudança de patamar de FPS (ex.: troca de cena) gera até cerca de 15 stutters até a mediana de 30 quadros se adaptar.
* O download consulta a API pública do GitHub (60 requisições/h sem login) e repete até 3 vezes em erros 5xx.

## Verificação

* Build isolado: `dotnet build src/Rbm.Gaming/Rbm.Gaming.csproj -p:ArtifactsPath="$LOCALAPPDATA/RbmOptimizer.Build/wt-gaming"` → 0 avisos, 0 erros.
* Testes (xUnit v3, `tests/Rbm.Tests/Gaming/`): **73/73** aprovados num projeto descartável que referencia só Core + Gaming.
  Os fixtures em `Gaming/Fixtures` são trechos **reais** de capturas do PresentMon 2.6.0 neste PC (layouts padrão, v2 e v1) e
  uma versão no layout de colunas do 1.9.2. Os valores esperados de FPS foram calculados de forma independente (awk).
  Os testes encontram os fixtures pelo `CallerFilePath`. Para copiá-los para a saída, adicione ao `Rbm.Tests.csproj`:
  `<None Include="Gaming\Fixtures\**" CopyToOutputDirectory="PreserveNewest" />` (opcional).
* Teste ponta a ponta neste PC (Win11 26200, Ryzen 5 5500, RX 6600, sem elevação): detectou Steam (Aimlabs), Epic (Fortnite,
  Rocket League, ambos com o processo real em vez do launcher), Xbox (Minecraft) e Battle.net (Overwatch). Também foram verificados:
  o download com assinatura Intel válida, uma captura real de 3 s (`msedge.exe`, 177 quadros, ~60 FPS, *Composed: Flip*, V-Sync 1),
  a sessão salva em `games.json`, os overlays (Discord, Game Bar, AMD Adrenalin), o Game DVR e o ciclo
  definir/ler/restaurar da preferência de GPU num executável fictício (o registro ficou limpo depois).
