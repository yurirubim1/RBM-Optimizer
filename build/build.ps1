<#
  RBM Optimizer — build Release + testes + publicação + instalador MSI.
  Uso: powershell -ExecutionPolicy Bypass -File build/build.ps1 [-SkipTests] [-SkipInstaller]
  Saída: dist\app (pasta portátil, self-contained win-x64) e dist\RBM-Optimizer-Setup.msi
#>
param([switch]$SkipTests, [switch]$SkipInstaller, [string]$Version = "0.2.0")
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$env:PATH = "$env:DOTNET_ROOT;$env:PATH"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$artifacts = "$env:LOCALAPPDATA\RbmOptimizer.Build\release"
$dist = Join-Path $root "dist"; $app = Join-Path $dist "app"
if (Test-Path $app) { Remove-Item $app -Recurse -Force }
New-Item -ItemType Directory -Force $app | Out-Null

Write-Host "==> Validação do catálogo" -ForegroundColor Cyan
& powershell -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root "build\validate-catalog.ps1")
if ($LASTEXITCODE -ne 0) { throw "Catálogo inválido" }

if (-not $SkipTests) {
  Write-Host "==> Testes" -ForegroundColor Cyan
  dotnet build (Join-Path $root "tests\Rbm.Tests\Rbm.Tests.csproj") -c Release -p:ArtifactsPath=$artifacts -v q
  if ($LASTEXITCODE -ne 0) { throw "Falha ao compilar os testes" }
  & "$artifacts\bin\Rbm.Tests\release_win-x64\Rbm.Tests.exe" -noColor
  if ($LASTEXITCODE -ne 0) { throw "Testes falharam" }
}

Write-Host "==> Publicando agente e UI (Release, self-contained win-x64)" -ForegroundColor Cyan
foreach ($proj in "src\Rbm.Agent\Rbm.Agent.csproj", "src\Rbm.UI\Rbm.UI.csproj") {
  dotnet publish (Join-Path $root $proj) -c Release -r win-x64 --self-contained true -p:Version=$Version -p:ArtifactsPath=$artifacts -p:PublishReadyToRun=true -o $app -v q
  if ($LASTEXITCODE -ne 0) { throw "Falha ao publicar $proj" }
}
Get-ChildItem $app -Filter *.pdb | Remove-Item -Force
foreach ($exe in "RBMOptimizer.exe", "RBM.Agent.exe") { if (-not (Test-Path (Join-Path $app $exe))) { throw "$exe ausente na publicação" } }
Write-Host ("    {0} arquivos, {1:N0} MB" -f (Get-ChildItem $app -Recurse -File).Count, ((Get-ChildItem $app -Recurse -File | Measure-Object Length -Sum).Sum / 1MB))

if (-not $SkipInstaller) {
  Write-Host "==> Instalador MSI (WiX 5)" -ForegroundColor Cyan
  dotnet build (Join-Path $root "installer\RBM.Installer.wixproj") -c Release -p:PublishDir=$app -p:ProductVersion=$Version -v q
  if ($LASTEXITCODE -ne 0) { throw "Falha ao gerar o instalador" }
  $msi = Get-ChildItem (Join-Path $root "installer\bin") -Recurse -Filter "RBM-Optimizer-Setup.msi" | Sort-Object LastWriteTime -Descending | Select-Object -First 1
  Copy-Item $msi.FullName (Join-Path $dist "RBM-Optimizer-Setup.msi") -Force
  Write-Host "    $dist\RBM-Optimizer-Setup.msi"
}
Write-Host "==> Concluído" -ForegroundColor Green
