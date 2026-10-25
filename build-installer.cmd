@echo off
rem Builds the app, then the installer (dist-installer\OLEDable-Setup-*.exe).
rem Needs Inno Setup 6:  winget install JRSoftware.InnoSetup
cd /d "%~dp0"
if not exist logs mkdir logs
set LOG=logs\installer.txt
echo === installer %date% %time% === > %LOG%
call build.cmd
findstr /c:"BUILD OK" logs\build.txt >nul || (echo App build failed, see logs\build.txt >> %LOG% & goto :end)
set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" set "ISCC=%ProgramFiles%\Inno Setup 6\ISCC.exe"
if not exist "%ISCC%" (echo Inno Setup 6 not found. Install it with: winget install JRSoftware.InnoSetup >> %LOG% & goto :end)
"%ISCC%" installer\OLEDable.iss >> %LOG% 2>&1
if errorlevel 1 (echo INSTALLER FAILED >> %LOG%) else (echo INSTALLER OK >> %LOG%)
:end
echo === done === >> %LOG%
type %LOG%
timeout /t 5 >nul
