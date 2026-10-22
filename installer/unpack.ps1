# Used by the installer: unpacks a downloaded tool zip and keeps only the folder that holds the tool's exe
# (ffmpeg's bin folder, RIFE's or Real-ESRGAN's main folder), copied into -Dest.
param([string]$Zip, [string]$Dest, [string]$Exe)
$ErrorActionPreference = 'Stop'
$tmp = Join-Path ([IO.Path]::GetTempPath()) ('hsr_unpack_' + [Guid]::NewGuid().ToString('N').Substring(0, 8))
try {
    Expand-Archive -LiteralPath $Zip -DestinationPath $tmp -Force
    $found = Get-ChildItem -LiteralPath $tmp -Recurse -Filter $Exe | Select-Object -First 1
    if (-not $found) { exit 2 }
    New-Item -ItemType Directory -Force -Path $Dest | Out-Null
    Copy-Item -Path (Join-Path $found.DirectoryName '*') -Destination $Dest -Recurse -Force
    # Real-ESRGAN's zip carries demo files that aren't needed
    foreach ($junk in 'input.jpg', 'input2.jpg', 'onepiece_demo.mp4') { Remove-Item -LiteralPath (Join-Path $Dest $junk) -ErrorAction SilentlyContinue }
    exit 0
}
catch { exit 1 }
finally { Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue }
