# Renders every page of the built app in every theme to PNGs (QA), using the test library on H:.
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$exe = Join-Path $here 'dist\HydroShiftRecorder.exe'
$lib = 'H:\HydroShiftBench\out'
$snap = 'H:\HydroShiftBench\snap'
New-Item -ItemType Directory -Force $lib | Out-Null
if (-not (Test-Path "$lib\Neon Red City JPN.mp4")) { Copy-Item 'H:\HydroShiftBench\neon.mp4' "$lib\Neon Red City JPN.mp4" }
if (-not (Test-Path "$lib\red-silhouette.mp4") -and (Test-Path "$env:USERPROFILE\Videos\HydroShift\red-silhouette.mp4")) {
  Copy-Item "$env:USERPROFILE\Videos\HydroShift\red-silhouette.mp4" "$lib\red-silhouette.mp4"
}
if (Test-Path $snap) { Remove-Item $snap -Recurse -Force }
New-Item -ItemType Directory -Force $snap | Out-Null
$p = Start-Process $exe -ArgumentList '--snapshot', $snap -PassThru
if (-not $p.WaitForExit(180000)) { $p.Kill(); 'timed out' | Set-Content "$snap\timeout.txt" }
Get-ChildItem $snap | Select-Object Name, Length | Out-String | Set-Content "$snap\list.txt"
