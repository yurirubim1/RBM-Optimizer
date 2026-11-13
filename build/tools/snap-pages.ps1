# Captura telas de páginas do RBM SEM abrir janela visível (build de dev abre fora da tela; render interno via RenderTargetBitmap).
# Uso: powershell -File build/tools/snap-pages.ps1 -OutDir <pasta> [-Pages dashboard,cpu,...] [-DelayMs 9000]
param([Parameter(Mandatory)][string]$OutDir, [string[]]$Pages = @(), [int]$DelayMs = 9000, [string]$Artifacts = "$env:LOCALAPPDATA\RbmOptimizer.Build\artifacts")
$env:DOTNET_ROOT = "$env:LOCALAPPDATA\Microsoft\dotnet"
$exe = Join-Path $Artifacts "bin\Rbm.UI\debug_win-x64\RBMOptimizer.exe"
New-Item -ItemType Directory -Force $OutDir | Out-Null
$Pages = @($Pages | ForEach-Object { $_ -split "," } | Where-Object { $_ })
foreach ($pg in $Pages) {
  $png = Join-Path $OutDir "$pg.png"; Remove-Item $png -ErrorAction SilentlyContinue
  $env:RBM_UI_SNAPSHOT = $png; $env:RBM_UI_SNAPSHOT_DELAY = "$DelayMs"
  $p = Start-Process $exe -ArgumentList @("--offscreen", "--page", $pg) -PassThru -WindowStyle Hidden
  $deadline = (Get-Date).AddMilliseconds($DelayMs + 15000)
  while (-not (Test-Path $png) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
  Start-Sleep -Milliseconds 300
  Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue
  "{0,-14} {1}" -f $pg, $(if (Test-Path $png) { "ok" } else { "SEM CAPTURA" })
}
Remove-Item Env:RBM_UI_SNAPSHOT, Env:RBM_UI_SNAPSHOT_DELAY -ErrorAction SilentlyContinue
