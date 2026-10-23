@echo off
rem Builds HydroShiftRecorder.exe into dist\ . Output goes to logs\build.txt so Claude can read it.
cd /d "%~dp0"
if not exist logs mkdir logs
set LOG=logs\build.txt
echo === build %date% %time% === > %LOG%
rem winget installs may not be on PATH in an Explorer-launched window yet
set "PATH=%PATH%;%ProgramFiles%\dotnet"
rem close a running dev build so the new exe can be copied over it
powershell -NoProfile -Command "Get-Process HydroShiftRecorder -ErrorAction SilentlyContinue | Where-Object { $_.Path -like '%~dp0dist\*' } | Stop-Process -Force"
timeout /t 1 >nul
echo Building HydroShift Recorder...
dotnet build HydroShiftRecorder.csproj -c Release -o dist -nologo -v minimal >> %LOG% 2>&1
if errorlevel 1 (echo BUILD FAILED >> %LOG%) else (echo BUILD OK >> %LOG%)
echo === done === >> %LOG%
timeout /t 3 >nul
