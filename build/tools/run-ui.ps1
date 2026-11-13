# Inicia o RBM Optimizer a partir de uma pasta de artifacts e (opcional) navega direto para uma página.
# Uso: powershell -File build/tools/run-ui.ps1 [-Artifacts <dir>] [-Page <key>]  → imprime o PID
param([string]$Artifacts = "$env:LOCALAPPDATA\RbmOptimizer.Build\artifacts", [string]$Page = "")
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$exe = Join-Path $Artifacts "bin\Rbm.UI\debug_win-x64\RBMOptimizer.exe"
$args = @("--offscreen"); if ($Page) { $args += @("--page", $Page) }
$p = Start-Process $exe -ArgumentList $args -PassThru
$p.Id
