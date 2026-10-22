@echo off
rem One-time setup: installs the .NET 8 SDK (to build the app) and checks ffmpeg.
rem Results go to logs\setup.txt so Claude can read them.
cd /d "%~dp0"
if not exist logs mkdir logs
set LOG=logs\setup.txt
echo === setup %date% %time% === > %LOG%

where dotnet >nul 2>&1 && dotnet --list-sdks >> %LOG% 2>&1
dotnet --list-sdks 2>nul | findstr /b "8." >nul
if errorlevel 1 (
  echo Installing the .NET 8 SDK from Microsoft. Windows may ask for permission...
  echo --- installing .NET 8 SDK --- >> %LOG%
  winget install --id Microsoft.DotNet.SDK.8 -e --silent --accept-source-agreements --accept-package-agreements >> %LOG% 2>&1
) else (
  echo .NET 8 SDK already installed. >> %LOG%
)

echo --- ffmpeg --- >> %LOG%
where ffmpeg >> %LOG% 2>&1
ffmpeg -hide_banner -version 2>&1 | findstr /b "ffmpeg" >> %LOG%
ffmpeg -hide_banner -encoders 2>nul | findstr "nvenc" >> %LOG%
echo --- gpu --- >> %LOG%
nvidia-smi --query-gpu=name,driver_version --format=csv,noheader >> %LOG% 2>&1
echo --- cpu --- >> %LOG%
echo %NUMBER_OF_PROCESSORS% logical processors >> %LOG%
echo === done === >> %LOG%
echo Setup finished. This window closes in 5 seconds.
timeout /t 5 >nul
