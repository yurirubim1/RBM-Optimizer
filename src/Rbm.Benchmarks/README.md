# Rbm.Benchmarks — motor de benchmarks do RBM Optimizer

Biblioteca (sem UI) que mede desempenho de CPU, memória, armazenamento e responsividade do agendador com
**unidades físicas/contáveis** (Mops/s, MB/s, GB/s, IOPS, ns, µs, eventos/s) e metodologia reproduzível.
Não há "pontuações" inventadas nem máquinas de referência: compare sempre **a mesma máquina, antes × depois**.

## API pública

```csharp
IReadOnlyList<IBenchmark> all = BenchmarkCatalog.CreateAll();      // ou BenchmarkCatalog.Get("cpu.sort.st")
BenchmarkResult r = await bench.RunAsync(new BenchmarkOptions(), progress, ct);   // nunca lança por erro de medição
IReadOnlyList<BenchmarkResult> rs = await BenchmarkSuite.RunAsync(all, options, progress, ct);
CpuSuiteResult cpu = await CpuSuite.RunAsync(options, progress, ct);              // ST+MT + escalonamento MT/ST
BenchmarkComparison cmp = BenchmarkComparer.Compare(antes, depois);               // Welch + comparabilidade
BenchmarkIndexResult idx = BenchmarkIndex.Compute(linhaDeBase, atual);            // média geométrica vs. SUA linha de base
string json = BenchmarkJson.Serialize(r); var back = BenchmarkJson.Deserialize(json);
TestConditions c = ConditionsProbe.Capture(TimeSpan.FromSeconds(3), ct);
```

- `IBenchmark`: `Id`, `Name` (pt-BR), `Category`, `Unit`, `HigherIsBetter`, `Methodology`, `IsImplemented`,
  `WritesToStorage`, `SecondaryMetrics`, `RunAsync(BenchmarkOptions, IProgress<BenchmarkProgress>?, CancellationToken)`.
- Execução em **thread dedicada de prioridade Normal** (nunca na thread de UI); `RunAsync` retorna imediatamente uma `Task`.
- `BenchmarkProgress`: fase (`Conditions`, `Preparing`, `Warmup`, `Measuring`, `Finishing`), iteração, fração 0..1, mensagem pt-BR.
  Use um `Progress<T>` criado na UI para receber na thread de UI.
- `BenchmarkResult` (record imutável): `Status` (`Completed`/`Cancelled`/`Failed`/`NotImplemented`), `Samples` (uma por iteração
  medida), `Summary` (`StatSummary` do Core), `Metrics` (métricas secundárias, cada uma com amostras e resumo), `Unit`,
  `Conditions`, `StartedAt`, `Duration`, `IterationWallSeconds`, `UnstableConditions`, `Warnings`, `Message`, `Methodology`,
  `Parameters` (definem a carga — precisam ser iguais para comparar), `Info` (checksums, bytes gravados…), `EngineVersion`.
- **Cancelamento**: verificado entre iterações e dentro dos laços longos (por unidade de trabalho, por E/S, a cada bloco de
  2^20 acessos). Resultado `Cancelled` tem `Samples` vazio e `Summary` vazio — nunca um parcial apresentado como completo.
  Toda sessão é liberada em `finally` (buffers nativos, threads, arquivo temporário, `timeEndPeriod`).
- **Persistência**: use **sempre** `BenchmarkJson.Options` (permite `NaN` das estatísticas vazias; enums como texto).
  Round-trip verificado em teste.

### Opções (`BenchmarkOptions`, record imutável)
`WarmupIterations` (1), `MeasuredIterations` (5), `WorkScale` (1.0 — multiplica a quantidade de trabalho, não os tamanhos de dados),
`ThreadCount` (null = `Environment.ProcessorCount`), `CaptureConditions` (true), `ConditionsSampleWindow` (3 s),
`MemoryBandwidthBufferBytes` (512 MiB), `MemoryLatencyBufferBytes` (256 MiB), `CacheLatencyBufferBytes` (256 KiB),
`StorageVolume` (null = unidade do sistema), `StorageDirectory`, `StorageFileBytes` (1 GiB), `StorageIncludeWriteTests` (**false**),
`StorageSequentialDuration` (3 s), `StorageRandomDuration` (2 s), `StorageFreeSpaceMarginBytes` (1 GiB),
`SleepSamplesPerIteration` (100), `WakeSamplesPerIteration` (500), `JitterDurationPerIteration` (1 s), `JitterThresholdMicroseconds` (50).

## IDs de benchmark (20)

| ID | Unidade | Melhor |
|---|---|---|
| `cpu.integer.st` / `.mt` | Mops/s (palavras de 64 bits) | maior |
| `cpu.float.st` / `.mt` | Mops/s (iterações z←z²+c) | maior |
| `cpu.compression.st` / `.mt` | MB/s de entrada | maior |
| `cpu.sort.st` / `.mt` | M elementos/s | maior |
| `cpu.sha256.st` / `.mt` | MB/s | maior |
| `mem.bandwidth.read` / `.write` / `.copy` | GB/s | maior |
| `mem.latency.cache` / `mem.latency.dram` | ns por acesso | menor |
| `storage.volume` | MB/s (principal: leitura seq.) + IOPS/MB/s/latência média e P99 (µs) | — |
| `sched.sleep` | µs (mediana do Sleep(1) com timeBeginPeriod(1)) | menor |
| `sched.wakeup` | µs (P99 ocioso) | menor |
| `sched.jitter` | lacunas > 50 µs por segundo | menor |
| `gpu.external` | — (**não implementado**, descritor honesto) | — |

## Estrutura de execução e condições de teste

1. **Condições** (`TestConditions`, antes do início): plano de energia (nome/GUID via `PowerGetActiveScheme` + `PowerReadFriendlyName`),
   tomada/bateria (`GetSystemPowerStatus`), carga de CPU do sistema nos 3 s anteriores (`GetSystemTimes`; o processo fica ocioso),
   processos usando > 5% da CPU total nessa janela, prioridade do processo, versão/build/UBR do Windows, resolução do timer
   (`NtQueryTimerResolution`), nome da CPU, nº de processadores lógicos, versão do .NET, se o JIT está otimizado e se há depurador.
2. **Preparação** (fora do tempo medido): geração de dados com xorshift64* de semente fixa, buffers tocados, checksum de referência.
3. **Aquecimento** (padrão 1 iteração, descartada): compilação em camadas do JIT (laços críticos usam `AggressiveOptimization`),
   caches e estados de energia.
4. **N iterações medidas** (padrão 5), cada uma cronometrada com `Stopwatch`.
5. **Sinalização "condições instáveis"**: carga de fundo > 10% **ou** CV das amostras > 5% → `UnstableConditions = true` + aviso.

## Metodologia por teste

### CPU (ST e MT)
Cada carga tem uma *unidade de trabalho* fixa com **checksum** (o JIT não pode eliminar o trabalho; resultado divergente
invalida a execução e sugere instabilidade de hardware — overclock/undervolt, memória, temperatura).
- **Inteiros**: buffer de 256 KiB (32 768 palavras de 64 bits, cabe no L2); hash de 4 vias (xor, mul 64×64, xorshift, rotação, soma).
  Unidade = 1 passagem; 16 384 por iteração e por thread.
- **Ponto flutuante**: Mandelbrot 320×240, x∈[−2,2; 1,0], y∈[−1,2; 1,2], máx. 256 iterações, double escalar (determinístico). 48 grades.
- **Compressão**: 2 MiB de texto pseudoaleatório compressível (vocabulário fixo de 512 palavras, ~3:1) com `DeflateStream`
  (`CompressionLevel.Optimal`); vazão em bytes de entrada. Descompressão validada na preparação. 12 compressões.
- **Ordenação**: 1 000 000 de `int` (semente fixa), cópia + `Array.Sort`. 8 ordenações.
- **SHA-256**: 4 MiB com `SHA256.HashData` (CNG). **Usa as extensões SHA da CPU (SHA-NI)** quando presentes: mede a via acelerada.
- **MT**: as mesmas cargas em N threads dedicadas (padrão `Environment.ProcessorCount`), cada thread com o mesmo nº de unidades
  da versão ST (particionamento estático, estado de saída por thread); vazão = trabalho total / tempo de parede.
  `CpuSuite` reporta **escalonamento MT/ST** = média MT / média ST e eficiência = razão / threads (com SMT, ~50–75% é normal).

### Memória
- **Largura de banda**: buffer nativo alinhado (`NativeMemory.AlignedAlloc`) de 512 MiB (≫ L3), dividido em fatias contíguas
  por thread; laços `Vector<T>` (256 bits com AVX2), 4 vetores por passo. Leitura = soma validada; escrita = stores normais
  (com RFO — ferramentas com stores não temporais mostram números maiores); cópia = metade origem/metade destino, contada na
  convenção STREAM (lidos + gravados). GB = 10^9 bytes. Exige memória livre ≥ buffer + 512 MiB.
- **Latência**: perseguição de ponteiros dependente (`p = *p`) sobre linhas de 64 B em um **único ciclo aleatório (Sattolo)**;
  256 MiB para DRAM (8 M acessos/iteração) e 256 KiB para o ponto de cache L2 (64 M acessos). ns/acesso = tempo / acessos.
  Posição final da cadeia validada entre iterações.

### Armazenamento (`storage.volume`)
Arquivo temporário em `<raiz>\RBMBench` (ou `%TEMP%\RBMBench` na unidade do sistema), aberto via `CreateFileW` com
`FILE_FLAG_NO_BUFFERING | WRITE_THROUGH | OVERLAPPED | DELETE_ON_CLOSE`; buffers alinhados a 4 KiB; dados incompressíveis.
Exige espaço livre ≥ 2× tamanho + 1 GiB. Preparação grava o arquivo inteiro uma vez (senão leituras não atingiriam o dispositivo).
Por iteração: leitura sequencial 1 MiB QD1 (arquivo inteiro ou 3 s), leitura aleatória 4 KiB QD1 (2 s) e QD32 (32 threads,
uma E/S sobreposta pendente cada, 2 s). Com **opt-in** (`StorageIncludeWriteTests`) as mesmas medições em gravação, com aviso
de bytes gravados (desgaste de SSD). Latência por E/S via `Stopwatch` → média e P99 (µs). O arquivo é sempre removido
(`finally`, cancelamento e `DELETE_ON_CLOSE` mesmo se o processo morrer); arquivos órfãos `rbmbench_*.tmp` são limpos.

### Responsividade
- **`sched.sleep`**: duração real de `Thread.Sleep(1)` com a resolução padrão e com `timeBeginPeriod(1)`
  (`timeEndPeriod(1)` sempre em `finally`). Mediana/P99/máx por modo.
- **`sched.wakeup`**: evento do kernel; a sinalizadora espera 100–600 µs (sequência fixa), marca o tempo e chama `Set()`;
  a thread em espera marca o tempo ao acordar. 500 rodadas ocioso + 500 com carga em todas as threads lógicas. P50/P99/máx.
- **`sched.jitter`**: laço ocupado lendo `Stopwatch`; conta lacunas > 50 µs. **Indicador indireto** (preempção, DPC/ISR, SMI,
  energia) — **não** é medição de latência de DPC.

### GPU
**Não implementado de propósito.** Sem pilha de renderização própria não há benchmark sintético de GPU confiável e
multi-fabricante; `gpu.external` devolve `NotImplemented` com a explicação e o procedimento recomendado:
capturar tempo de quadro em jogos reais com PresentMon (módulo Gaming), mesma cena/configuração, ≥ 3 execuções, Welch.

## Comparação e índice
`BenchmarkComparer.Compare` usa `Stats.Compare` (Welch bicaudal; só declara mudança se p < 0,05 **e** |Δ| ≥ 2%) respeitando
`HigherIsBetter`, para a métrica principal e cada métrica secundária comum. **Não comparável** se: IDs diferentes, execução não
concluída, versão da metodologia diferente, parâmetros de carga diferentes ou CPU diferente. **Avisos** (e `ConfidentChange = false`)
se mudou plano de energia, tomada/bateria, build do Windows, versão do .NET, nº de processadores lógicos, resolução do timer
(responsividade), execução sem JIT otimizado, condições instáveis ou < 3 amostras.
`BenchmarkIndex`: média geométrica das razões média-atual/média-base (invertida quando menor é melhor) contra a **linha de base do
próprio usuário**; 1,00 = igual.

## Limitações
- Código gerenciado (.NET JIT): mede o que um programa típico alcança, não o pico teórico; resultados dependem da versão do .NET
  (registrada nas condições).
- Latência de DRAM inclui faltas de TLB (páginas de 4 KiB): fica acima do valor "puro" de ferramentas com páginas grandes.
- Armazenamento: 1 GiB costuma caber no cache SLC/DRAM do SSD; em HDD mede apenas a faixa inicial do arquivo; leituras exigem
  gravar o arquivo uma vez. QD32 usa 32 threads com E/S sobreposta (não um único submissor).
- Threads não são fixadas a núcleos; o agendador do Windows decide (é o comportamento real de aplicativos).
- Sem elevação: alguns processos protegidos não entram na contagem de "processos > 5%".
- Compare apenas na mesma máquina e em condições equivalentes.

## Resultados medidos nesta máquina (amostra)
Ryzen 5 5500 (6C/12T), 32 GB, NVMe ADATA LEGEND 860 (C:), Windows 11 Pro 10.0.26200.9711, plano "Desempenho Máximo", tomada,
.NET 10.0.14, build Release. Execução reduzida: 1 aquecimento + 3 iterações, janela de condições 1 s; armazenamento com arquivo de
256 MiB, sequencial 2 s, aleatório 1 s, **gravação habilitada**. **Atenção:** a máquina estava ocupada (outros builds, Roblox Studio,
OneDrive): carga de fundo de 15–45% em todas as execuções → todas foram corretamente marcadas como "condições instáveis".
Os números são ilustrativos, não uma referência.

| Teste | Média | CV |
|---|---|---|
| cpu.integer ST / MT | 1 219 / 7 008 Mops/s (5,75×) | 2,5% / 7,0% |
| cpu.float ST / MT | 403 / 3 391 Mops/s (8,41×) | 2,2% / 7,9% |
| cpu.compression ST / MT | 66,5 / 468,6 MB/s (7,05×) | 1,3% / 1,8% |
| cpu.sort ST / MT | 17,2 / 107,3 M elementos/s (6,25×) | 3,5% / 12,2% |
| cpu.sha256 ST / MT | 1 998 / 17 009 MB/s (8,51×) | 0,1% / 8,6% |
| mem.bandwidth read / write / copy | 32,4 / 10,3 / 15,7 GB/s | 4,8% / 3,3% / 7,4% |
| mem.latency cache (256 KiB) / dram (256 MiB) | 4,4 / 106,4 ns | 4,9% / 2,6% |
| storage seq. leitura 1 MiB QD1 | 470–498 MB/s (amostras 338–726, muito ruidoso) | ~32% |
| storage 4K leitura QD1 / QD32 | ~7 300 / ~150 000 IOPS; lat. média 159–200 µs, P99 ~3,4 ms (QD1) | — |
| storage seq. gravação / 4K gravação QD1 / QD32 | 1 058 MB/s / 13 139 IOPS / 102 284 IOPS | — |
| sched.sleep (padrão / timeBeginPeriod(1)) | mediana 15 163 µs / 2 000 µs (P99 2 520 µs) | 0,0% |
| sched.wakeup ocioso / sob carga | P50 7,4 / 4,2 µs; P99 18,3 / 10,8 µs; máx 94 / 40 µs | 10,7% |
| sched.jitter | 51 lacunas > 50 µs/s; maior 1 654 µs; 0,68% do tempo | 24% |

Observações: a gravação sequencial superou a leitura porque o SSD absorve escritas no cache e porque a carga concorrente de E/S
de outros processos afetou as leituras QD1. O despertar sob carga foi *menor* que ocioso: com todos os núcleos ativos não há
saída de estados de energia profundos (C-states). O Sleep(1) com timer de 1 ms dura ~2 ms (arredondamento para o próximo tick).
A execução de armazenamento gravou ~3,6 GiB no total (inclui preparação), e a pasta temporária ficou vazia/removida ao final.

## Testes
`tests/Rbm.Tests/Benchmarks/` (xUnit v3, 34 testes): checksums e referências independentes dos núcleos (hash, Mandelbrot
particionado, Deflate ida e volta, ordenação, SHA-256 com vetor conhecido), ciclo de Sattolo, perseguição de ponteiros vs.
caminhada gerenciada, kernels SIMD, executor (amostras/resumo, CV instável, cancelamento sem parcial + Dispose, falha como
resultado, opções inválidas, progresso, fora da thread chamadora), captura de condições (e cancelamento), catálogo/GPU,
armazenamento (conclusão, sem opt-in, cancelamento na preparação e na medição sem deixar arquivos, espaço insuficiente),
comparação (flutuação pequena ≠ ganho, direção, plano de energia, não comparáveis), índice e round-trip JSON com NaN.
