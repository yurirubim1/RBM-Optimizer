# Rbm.Drivers — Central de Drivers do RBM Optimizer

Biblioteca (sem UI) para inventário, diagnóstico, backup, instalação e reversão de drivers no Windows 10/11 x64.
Somente dados reais: quando algo não pode ser obtido, o resultado diz explicitamente que está indisponível e por quê.
O RBM Optimizer **nunca** remove drivers automaticamente, nunca executa o DDU e nunca baixa drivers.

## Funcionalidades

| # | Recurso | Classe principal | Elevação |
|---|---------|------------------|----------|
| 1 | Inventário de dispositivos (nome, classe, fabricante, IDs de hardware/compatíveis, instância, INF/oemNN.inf, versão, data, provedor, assinante + estado da assinatura, código de problema com descrição/ação em pt-BR, presente/não presente, "desconhecido") | `DeviceInventoryService` | Não |
| 2 | Pacotes de terceiros no Driver Store, mapa pacote ↔ dispositivo, versões duplicadas | `DriverStoreService` | Não |
| 3 | Dispositivos desconhecidos/com problema + fabricante pelo ID (tabela PCI/USB/ACPI embutida) + busca no Catálogo do Microsoft Update | `OfficialSources.UnknownAndProblemDevices` | Não |
| 4 | Fonte oficial por fabricante/categoria (mapa estático, sem scraping) + "mais novo nem sempre é melhor" | `OfficialSources.GuidanceFor` | Não |
| 5 | Backup (`pnputil /export-driver`) + manifesto JSON (dispositivos, pacotes, SHA-256 de .inf/.cat) e verificação do backup | `DriverBackupService` | Ver abaixo |
| 6 | Instalação validada (`pnputil /add-driver /install`), verificação pós-instalação, versões anteriores no store e troca de versão | `DriverInstallService` | **Sim** (instalar/trocar) |
| 7 | Histórico: fotografia dispositivo→driver e diff (adicionado/removido/atualizado/revertido/alterado) | `DriverSnapshot`, `DriverSnapshotComparer` | Não |
| 8 | Assistente de pacote NVIDIA extraído + orientação DDU | `NvidiaPackageAssistant`, `DduAdvisor` | Não |
| 9 | Achados de diagnóstico (`Rbm.Core.Diagnostics.Finding`) | `DriverDiagnostics`, `DriverDiagnosticRule` | Não |

## API pública (resumo)

```csharp
// 1. Inventário (SetupAPI/CfgMgr32; fallback WMI). Roda em Task.Run — seguro fora da UI.
Task<DeviceInventory> DeviceInventoryService.CollectAsync(InventoryOptions options, CancellationToken ct);
DeviceRecord? DeviceInventoryService.CollectOne(string instanceId, bool resolveSignature = true);
static DriverSignature DeviceInventoryService.VerifyInfSignature(string infPath);   // SetupVerifyInfFile, cache por INF
ProblemCodeInfo? ProblemCodes.Describe(int code);                                    // CM_PROB_* 1..57 em pt-BR

// 2. Driver Store
Task<DriverStoreInventory> DriverStoreService.EnumerateAsync(CancellationToken ct);  // pnputil XML → texto (fallback)
static DriverStoreAnalysis DriverStoreService.Analyze(DeviceInventory, DriverStoreInventory);
static IReadOnlyList<DriverPackage> PnpUtilParser.ParseXml(string) / ParseText(string);

// 3/4. Identificação e fontes oficiais
static HardwareIdInfo HardwareIdParser.Parse(string hardwareId);                    // VEN/DEV/SUBSYS/VID/PID/ACPI + fabricante
static IReadOnlyList<UnknownDeviceReport> OfficialSources.UnknownAndProblemDevices(DeviceInventory, string? boardManufacturer = null);
static DriverSourceGuidance OfficialSources.GuidanceFor(DeviceRecord, string? boardManufacturer = null);
static IReadOnlyList<CatalogSearchLink> OfficialSources.CatalogSearches(DeviceRecord);

// 5. Backup
Task<DriverExportResult> DriverBackupService.ExportAsync(string destinationFolder, string? publishedName /* null/"*" = todos */,
                                                         IProgress<string>? progress, CancellationToken ct);
Task<BackupVerificationResult> DriverBackupService.VerifyAsync(string backupFolder, CancellationToken ct);
static PackageSignatureCheck CatalogVerifier.VerifyPackage(string infPath);        // .cat confiável + INF ∈ catálogo

// 6. Instalação / reversão
static InstallValidation DriverInstallService.ValidateForInstall(string? infPath); // pré-checagem não elevada
Task<DriverInstallResult> DriverInstallService.InstallAsync(string? infPath, CancellationToken ct);              // ELEVADO
Task<IReadOnlyList<RollbackCandidate>> DriverInstallService.FindAlternativeVersionsAsync(DeviceRecord, DriverStoreInventory, CancellationToken ct);
static SwitchPreview DriverInstallService.PreviewSwitch(string instanceId, string storeInfPath);                  // não elevado
Task<DriverSwitchResult> DriverInstallService.SwitchToPackageAsync(string? instanceId, string? publishedName, CancellationToken ct); // ELEVADO

// 7. Histórico
static DriverSnapshot DriverSnapshot.FromInventory(DeviceInventory);  // ToJson()/FromJson() para persistir
static DriverSnapshotDiff DriverSnapshotComparer.Diff(DriverSnapshot older, DriverSnapshot newer);
DriverVersion (record struct, 4 partes, IComparable) + DriverVersionMath.Compare / CompareLikePnp / TryParseDateAndVersion

// 8. NVIDIA / DDU
Task<NvidiaPackageReport> NvidiaPackageAssistant.AnalyzeAsync(string folder, DeviceRecord? installedGpu, CancellationToken ct);
static DduGuidance DduAdvisor.Evaluate(DeviceInventory, DriverStoreInventory? store = null, bool userReportsPersistentIssues = false);

// 9. Diagnóstico
static IReadOnlyList<Finding> DriverDiagnostics.Analyze(DeviceInventory, DriverStoreInventory? store = null, DateTime? now = null);
class DriverDiagnosticRule : IDiagnosticRule   // usa context.Extra["DeviceInventory"/"DriverStoreInventory"] se fornecidos

// Agente (RBM.Agent)
DriverAgentOperations.ExportAsync(DriverExportRequest)   → DriverExportResponse   // AgentOps.DriverExport
DriverAgentOperations.InstallAsync(DriverInstallRequest) → DriverInstallResponse  // AgentOps.DriverInstall
DriverAgentOperations.SwitchAsync(DriverSwitchRequest)   → DriverSwitchResponse   // PROPOSTO: "driver.switch"
```

## Elevação e segurança

- **Requer elevação — executado via RBM.Agent**: `DriverInstallService.InstallAsync` (`AgentOps.DriverInstall`) e
  `DriverInstallService.SwitchToPackageAsync` (proposto `driver.switch`).
- **Backup**: no Windows 11 build 26200 o `pnputil /export-driver` funcionou **sem** elevação (os arquivos do Driver Store
  são legíveis por usuários). A UI pode chamar `DriverBackupService` diretamente; `AgentOps.DriverExport` fica para
  builds/políticas em que o Windows exija administrador.
- Toda operação elevada revalida o payload (`DriverInputValidator`):
  - Pastas/arquivos: caminho absoluto com letra de unidade, sem UNC/`\\?\`, sem `.`/`..`, sem fluxos `:`, unidade **fixa**,
    fora de Windows/Arquivos de Programas/ProgramData/$Recycle.Bin/System Volume Information/Recovery, dentro de `C:\Users`
    somente no perfil do próprio usuário, sem junções/links simbólicos em nenhum ponto do caminho, raiz de unidade proibida.
  - INF: extensão `.inf`, até 16 MB, catálogo declarado no `[Version]` e presente ao lado, catálogo com assinatura
    Authenticode confiável (`SignatureVerifier`) e hash do INF (SHA-1/SHA-256) presente entre os membros do catálogo.
  - Nome publicado: `^oem\d{1,5}\.inf$`. ID de instância: tamanho e caracteres restritos.
  - Troca de versão: a UI envia apenas `InstanceId` + `oemNN.inf`; o caminho no `FileRepository` é resolvido pelo próprio
    agente via `pnputil` e revalidado.
- Processos: somente `%windir%\System32\pnputil.exe` via `ProcessRunner` (ArgumentList, sem shell).
- Por que a troca de versão usa `DiInstallDevice` e não `pnputil /add-driver oemNN.inf /install`: o pnputil só instala
  quando o pacote é "melhor" pelo ranking do PnP (data mais recente vence). Com a versão nova ainda no store, ele **não**
  volta para a anterior. `DiInstallDevice` aplica o nó escolhido (lista montada só com aquele INF, `DI_ENUMSINGLEINF`)
  a **um** dispositivo. A pré-visualização (`PreviewSwitch`) usa a mesma correspondência do PnP sem elevação.

### Teste manual reproduzível (prompt elevado)

O shell de desenvolvimento não é elevado; as rotas abaixo foram projetadas e validadas até o ponto que exige administrador.

1. Abra um PowerShell **como administrador** e compile o harness/console que referencia `Rbm.Drivers` (ou use o RBM.Agent).
2. Backup: `ExportAsync("D:\Backups", null)` → conferir pasta `RBM-Drivers-AAAAMMDD-HHMMSS`, um subdiretório por pacote
   (`oemNN_nomeoriginal`) e `rbm-drivers-manifest.json`. Em seguida `VerifyAsync(pasta)` → `Success = true`.
3. Instalação: extraia um pacote oficial (ex.: driver de chipset AMD), rode `ValidateForInstall(inf)` (deve ser `Ok`) e
   `InstallAsync(inf)`; conferir `ExitCode` 0/3010/259 e `DeviceChanges` (re-enumeração real). `pnputil /enum-drivers`
   deve listar o novo `oemNN.inf`.
4. Reversão: em um dispositivo com duas versões no store (neste PC: "Realtek(R) Audio" oem24 → oem4, "Voice Clarity"
   oem55 → oem29), rode `FindAlternativeVersionsAsync`, depois `SwitchToPackageAsync(instanceId, "oem4.inf")`; conferir
   `After.Version` e o Gerenciador de Dispositivos. Para voltar, repita com o pacote original (ex.: `oem24.inf`).
5. Negativos: INF alterado (acrescente um comentário) → `ValidateForInstall` recusa ("hash do INF não consta no catálogo");
   pasta `C:\Windows\Temp`, `\\servidor\x`, `C:\Users\Public` → recusadas.

## Limitações

- Assinatura: verificação offline (sem revogação online), como no `SignatureVerifier` do Rbm.Security.
- `pnputil /format xml` existe no Windows 11 recente; no Windows 10 o parser texto (independente de idioma) é usado e não
  fornece a pasta do pacote no store nem a lista de dispositivos por pacote → sem `StoreInfPath`, sem troca de versão.
- O mapa de fabricantes (PCI/USB/ACPI) é compacto, não exaustivo; código ausente → `null` (nunca palpite).
- URLs oficiais são estáticas (revisadas em 11/2026) e devem ser revisadas periodicamente. O RBM Optimizer não compara a
  versão instalada com "a mais recente" — não consulta a internet.
- O assistente NVIDIA documenta componentes com base em conhecimento público; a composição do pacote varia entre versões.
  Componentes não catalogados aparecem como "Desconhecido". Nada é modificado.
- Se o UAC for aceito com uma conta de administrador diferente da do usuário (usuário padrão), a regra "somente o próprio
  perfil" passa a valer para o perfil do administrador.
- `InstallAsync` atribui as mudanças pela diferença antes/depois de todo o inventário; mudanças simultâneas de outra origem
  (ex.: Windows Update) também apareceriam.

## Como foi verificado (Win11 Pro 26200, Ryzen 5 5500, RX 6600)

- Build: `dotnet build src/Rbm.Drivers/Rbm.Drivers.csproj` → 0 erros, 0 avisos.
- 90 testes xUnit v3 (`tests/Rbm.Tests/Drivers`) executados em projeto isolado: 90/90 aprovados (parsers com amostras
  reais pt-BR/XML, versões, IDs de hardware, códigos de problema, diff de fotografias, validação, análise do store,
  diagnóstico, NVIDIA, fontes oficiais).
- Harness com dados reais desta máquina (sem elevação):
  - Inventário: 263 dispositivos (178 presentes) em ~0,35 s sem assinaturas; ~1,0–1,4 s com assinaturas (frio), ~0,4 s com cache.
  - Driver Store: 64 pacotes via XML em ~0,7 s; parser texto pt-BR idêntico ao XML nos 64; 18 grupos com versões duplicadas.
  - 64/64 pacotes do store: catálogo confiável e INF presente no catálogo.
  - Versões alternativas detectadas pela correspondência real do PnP (ex.: Realtek Audio oem24 → oem4 v6.0.9240.1).
  - Backup de `oem47.inf` + verificação íntegra; após alterar o INF, a verificação acusou hash divergente e INF fora do catálogo.
- Não executado (exige administrador): `InstallAsync`, `SwitchToPackageAsync` (DiInstallDevice) — ver teste manual acima.
