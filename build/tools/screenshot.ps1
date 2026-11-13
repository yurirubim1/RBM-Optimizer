# Captura a janela principal de um processo (mesmo que esteja coberta), via PrintWindow.
# Uso: powershell -File build/tools/screenshot.ps1 -ProcessId <pid> -Out <arquivo.png>
param([Parameter(Mandatory)][int]$ProcessId, [Parameter(Mandatory)][string]$Out)
Add-Type -AssemblyName System.Drawing
Add-Type @"
using System; using System.Runtime.InteropServices;
public static class PW {
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  public struct RECT { public int L, T, R, B; }
}
"@
[PW]::SetProcessDPIAware() | Out-Null
$p = Get-Process -Id $ProcessId -ErrorAction Stop
for ($i = 0; $i -lt 20 -and $p.MainWindowHandle -eq 0; $i++) { Start-Sleep -Milliseconds 500; $p.Refresh() }
$h = $p.MainWindowHandle
$r = New-Object PW+RECT; [PW]::GetWindowRect($h, [ref]$r) | Out-Null
$bmp = New-Object System.Drawing.Bitmap ($r.R - $r.L), ($r.B - $r.T)
$g = [System.Drawing.Graphics]::FromImage($bmp); $hdc = $g.GetHdc()
[PW]::PrintWindow($h, $hdc, 2) | Out-Null   # PW_RENDERFULLCONTENT
$g.ReleaseHdc($hdc); $bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
"saved $($bmp.Width)x$($bmp.Height) -> $Out"
