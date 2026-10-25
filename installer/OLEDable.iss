; OLEDable installer (formerly HydroShift Recorder) (Inno Setup 6.6 or newer, for the dark mode that follows Windows)
; Build: run build-installer.cmd in the project folder, or let the GitHub workflow do it.
; Sets everything up before the first launch: the library folder (with its Converted subfolder), ffmpeg,
; RIFE smoothing and Real-ESRGAN AI enhance, and writes the choices into the app's settings file.
; Express (default) uses the tools it finds and downloads the rest; Advanced shows every choice.

#if Ver < EncodeVer(6,6,0)
  #error Inno Setup 6.6 or newer is needed (dark mode). Update it with: winget upgrade JRSoftware.InnoSetup
#endif

#ifndef AppVersion
  #define AppVersion "2.2.0"
#endif

[Setup]
AppId={{6B2F0E5C-8C1E-4B7E-9A51-2D1C4F7E9B13}
AppName=OLEDable
AppVersion={#AppVersion}
AppPublisher=OLEDable
DefaultDirName={localappdata}\Programs\OLEDable
; per-user install: no admin prompt, and the app folder stays writable (settings and tools live next to the exe)
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=..\dist-installer
OutputBaseFilename=OLEDable-Setup-{#AppVersion}
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\OLEDable.exe
UninstallDisplayName=OLEDable
; light or dark, following the Windows setting
WizardStyle=modern dynamic
Compression=lzma2
SolidCompression=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\dist\OLEDable.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\OLEDable.exe.config"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "unpack.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\OLEDable"; Filename: "{app}\OLEDable.exe"
Name: "{autodesktop}\OLEDable"; Filename: "{app}\OLEDable.exe"; Tasks: desktopicon

[InstallDelete]
; the app used to be called HydroShift Recorder: remove the old exe and shortcuts when updating
Type: files; Name: "{app}\HydroShiftRecorder.exe"
Type: files; Name: "{app}\HydroShiftRecorder.exe.config"
Type: files; Name: "{autoprograms}\HydroShift Recorder.lnk"
Type: files; Name: "{autodesktop}\HydroShift Recorder.lnk"

[Run]
Filename: "{app}\OLEDable.exe"; Description: "Open OLEDable"; Flags: nowait postinstall skipifsilent
; an update started from inside the app runs silently: reopen the app when it's done
Filename: "{app}\OLEDable.exe"; Flags: nowait; Check: WizardSilent

[UninstallDelete]
; tools the installer or the app downloaded; the library folder and its videos are never touched
Type: filesandordirs; Name: "{app}\ffmpeg"
Type: filesandordirs; Name: "{app}\rife"
Type: filesandordirs; Name: "{app}\realesrgan"
Type: files; Name: "{app}\OLEDable.json"
Type: files; Name: "{app}\HydroShift-Recorder.json"

[Code]
const
  FfmpegUrl = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip';
  RifeUrl   = 'https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip';
  EsrganUrl = 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip';

var
  ModePage: TInputOptionWizardPage;
  LibPage: TInputDirWizardPage;
  FfPage, RifePage, EsrPage: TInputOptionWizardPage;
  FfFilePage: TInputFileWizardPage;
  RifeDirPage: TInputDirWizardPage;
  DownloadPage: TDownloadWizardPage;
  { what each option on the tool pages does: 'found', 'download', 'later' or 'browse' }
  FfActs, RifeActs, EsrActs: TArrayOfString;
  FoundFfmpeg, FoundRife, FoundEsr: String;

function SettingsFile: String;
begin
  Result := AddBackslash(WizardDirValue) + 'OLEDable.json';
end;

{ An update keeps the existing settings, so the setup pages are only for a first install.
  Installs from before the rename have HydroShift-Recorder.json; the app copies it over on first launch. }
function IsUpdate: Boolean;
begin
  Result := FileExists(SettingsFile) or FileExists(AddBackslash(WizardDirValue) + 'HydroShift-Recorder.json');
end;

function Advanced: Boolean;
begin
  Result := ModePage.SelectedValueIndex = 1;
end;

{ ---------------------------------------------------------------- finding tools that are already there }

function FirstExisting(Candidates: TArrayOfString): String;
var
  I: Integer;
begin
  Result := '';
  for I := 0 to GetArrayLength(Candidates) - 1 do
    if (Candidates[I] <> '') and FileExists(Candidates[I]) then
    begin
      Result := Candidates[I];
      Exit;
    end;
end;

function FindFfmpeg: String;
var
  C: TArrayOfString;
begin
  Result := FileSearch('ffmpeg.exe', GetEnv('PATH'));
  if Result <> '' then Exit;
  SetArrayLength(C, 5);
  C[0] := ExpandConstant('{localappdata}\Microsoft\WinGet\Links\ffmpeg.exe');
  C[1] := ExpandConstant('{localappdata}\Programs\OLEDable\ffmpeg\ffmpeg.exe');
  C[2] := ExpandConstant('{localappdata}\Programs\HydroShift Recorder\ffmpeg\ffmpeg.exe');
  C[3] := 'C:\ffmpeg\bin\ffmpeg.exe';
  C[4] := ExpandConstant('{commonpf}\ffmpeg\bin\ffmpeg.exe');
  Result := FirstExisting(C);
end;

{ The folder holding Exe: on PATH, or where this app (or its portable version) keeps it. }
function FindToolDir(Exe, Sub: String): String;
var
  C: TArrayOfString;
begin
  Result := FileSearch(Exe, GetEnv('PATH'));
  if Result = '' then
  begin
    SetArrayLength(C, 4);
    C[0] := ExpandConstant('{localappdata}\Programs\OLEDable\' + Sub + '\' + Exe);
    C[1] := ExpandConstant('{localappdata}\OLEDable\' + Sub + '\' + Exe);
    C[2] := ExpandConstant('{localappdata}\Programs\HydroShift Recorder\' + Sub + '\' + Exe);
    C[3] := ExpandConstant('{localappdata}\HydroShift Recorder\' + Sub + '\' + Exe);
    Result := FirstExisting(C);
  end;
  if Result <> '' then Result := ExtractFileDir(Result);
end;

procedure AddOpt(Page: TInputOptionWizardPage; var Acts: TArrayOfString; Text, Act: String);
var
  N: Integer;
begin
  Page.Add(Text);
  N := GetArrayLength(Acts);
  SetArrayLength(Acts, N + 1);
  Acts[N] := Act;
end;

{ The chosen action for a tool. Express always takes the first option: use what was found, else download. }
function Choice(Page: TInputOptionWizardPage; Acts: TArrayOfString): String;
begin
  if Advanced then Result := Acts[Page.SelectedValueIndex] else Result := Acts[0];
end;

{ ---------------------------------------------------------------- pages }

procedure InitializeWizard;
begin
  FoundFfmpeg := FindFfmpeg;
  FoundRife := FindToolDir('rife-ncnn-vulkan.exe', 'rife');
  FoundEsr := FindToolDir('realesrgan-ncnn-vulkan.exe', 'realesrgan');

  ModePage := CreateInputOptionPage(wpWelcome, 'Setup type', '',
    'Express puts your library in Videos\OLEDable, uses the tools already on this PC and downloads ffmpeg and AI enhance if needed ' +
    '(RIFE smoothing is downloaded the first time a video needs it). ' +
    'Advanced lets you choose the folders and each tool.', True, False);
  ModePage.Add('Express (recommended)');
  ModePage.Add('Advanced');
  ModePage.SelectedValueIndex := 0;

  LibPage := CreateInputDirPage(wpSelectDir, 'Library folder', '',
    'Choose your library location (your video files are stored here).', False, 'OLEDable');
  LibPage.Add('');
  LibPage.Values[0] := ExpandConstant('{%USERPROFILE}') + '\Videos\OLEDable';

  FfPage := CreateInputOptionPage(LibPage.ID, 'ffmpeg', '', 'ffmpeg records and converts the videos.', True, False);
  if FoundFfmpeg <> '' then AddOpt(FfPage, FfActs, 'Use the ffmpeg found on this PC (' + FoundFfmpeg + ')', 'found');
  AddOpt(FfPage, FfActs, 'Download ffmpeg (about 100 MB, from gyan.dev)', 'download');
  AddOpt(FfPage, FfActs, 'Use a different ffmpeg.exe', 'browse');
  FfPage.SelectedValueIndex := 0;

  FfFilePage := CreateInputFilePage(FfPage.ID, 'ffmpeg', '', 'Choose ffmpeg.exe (ffprobe.exe should be in the same folder).');
  FfFilePage.Add('', 'ffmpeg.exe|ffmpeg.exe|Programs|*.exe', '.exe');

  RifePage := CreateInputOptionPage(FfFilePage.ID, 'Smoothing (RIFE)', '',
    'RIFE fills in frames to make videos smooth 60 fps. It runs on your graphics card.', True, False);
  { RIFE is a big download (about 430 MB, all its models), so by default the app fetches it the first time a
    video needs smoothing; Express takes the first option }
  if FoundRife <> '' then AddOpt(RifePage, RifeActs, 'Use RIFE found on this PC (' + FoundRife + ')', 'found');
  AddOpt(RifePage, RifeActs, 'Download it the first time it''s needed (recommended)', 'later');
  AddOpt(RifePage, RifeActs, 'Download RIFE now (about 430 MB)', 'download');
  AddOpt(RifePage, RifeActs, 'Use a different RIFE folder', 'browse');
  RifePage.SelectedValueIndex := 0;

  RifeDirPage := CreateInputDirPage(RifePage.ID, 'Smoothing (RIFE)', '',
    'Choose the folder with rife-ncnn-vulkan.exe and the rife-v4.6 model folder.', False, '');
  RifeDirPage.Add('');

  EsrPage := CreateInputOptionPage(RifeDirPage.ID, 'AI enhance (Real-ESRGAN)', '',
    'Real-ESRGAN redraws small videos at a higher resolution for a sharper picture. It runs on your graphics card.', True, False);
  if FoundEsr <> '' then AddOpt(EsrPage, EsrActs, 'Use Real-ESRGAN found on this PC (' + FoundEsr + ')', 'found');
  AddOpt(EsrPage, EsrActs, 'Download Real-ESRGAN now (about 50 MB)', 'download');
  AddOpt(EsrPage, EsrActs, 'Download it the first time it''s needed', 'later');
  EsrPage.SelectedValueIndex := 0;

  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading the tools...', nil);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if (PageID = ModePage.ID) or (PageID = wpSelectDir) or (PageID = LibPage.ID) or (PageID = FfPage.ID) or
     (PageID = FfFilePage.ID) or (PageID = RifePage.ID) or (PageID = RifeDirPage.ID) or (PageID = EsrPage.ID) then
  begin
    if IsUpdate then
      Result := PageID <> wpSelectDir   { an update only shows where it's installed }
    else if PageID = ModePage.ID then
      Result := False
    else if not Advanced then
      Result := True
    else if PageID = FfFilePage.ID then
      Result := Choice(FfPage, FfActs) <> 'browse'
    else if PageID = RifeDirPage.ID then
      Result := Choice(RifePage, RifeActs) <> 'browse';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = FfFilePage.ID) and not FileExists(FfFilePage.Values[0]) then
  begin
    MsgBox('Choose the ffmpeg.exe file.', mbError, MB_OK);
    Result := False;
  end
  else if (CurPageID = RifeDirPage.ID) and not FileExists(AddBackslash(RifeDirPage.Values[0]) + 'rife-ncnn-vulkan.exe') then
  begin
    MsgBox('That folder doesn''t contain rife-ncnn-vulkan.exe.', mbError, MB_OK);
    Result := False;
  end
  else if (CurPageID = wpReady) and not IsUpdate then
  begin
    DownloadPage.Clear;
    if Choice(FfPage, FfActs) = 'download' then DownloadPage.Add(FfmpegUrl, 'ffmpeg.zip', '');
    if Choice(RifePage, RifeActs) = 'download' then DownloadPage.Add(RifeUrl, 'rife.zip', '');
    if Choice(EsrPage, EsrActs) = 'download' then DownloadPage.Add(EsrganUrl, 'realesrgan.zip', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Result := False
        else
          Result := MsgBox('A download failed or was stopped: ' + GetExceptionMessage + #13#10#13#10 +
                           'Continue anyway? The app downloads RIFE and Real-ESRGAN itself when needed; ffmpeg can be installed later with: winget install Gyan.FFmpeg',
                           mbConfirmation, MB_YESNO) = IDYES;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

{ ---------------------------------------------------------------- installing }

{ Unpacks a downloaded zip into Dest, keeping only the folder that contains Exe. }
function Unpack(ZipName, Dest, Exe: String): Boolean;
var
  Zip, Ps: String;
  Code: Integer;
begin
  Result := False;
  Zip := ExpandConstant('{tmp}\' + ZipName);
  if not FileExists(Zip) then Exit;
  ExtractTemporaryFile('unpack.ps1');
  Ps := '-NoProfile -ExecutionPolicy Bypass -File "' + ExpandConstant('{tmp}\unpack.ps1') + '" -Zip "' + Zip +
        '" -Dest "' + Dest + '" -Exe "' + Exe + '"';
  if Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'), Ps, '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Result := (Code = 0) and FileExists(AddBackslash(Dest) + Exe);
end;

function JsonStr(S: String): String;
begin
  StringChangeEx(S, '\', '\\', True);
  StringChangeEx(S, '"', '\"', True);
  Result := '"' + S + '"';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Lib, Ff, Rife, Esr, Act: String;
  Lines: TArrayOfString;
begin
  if (CurStep <> ssPostInstall) or IsUpdate then Exit;

  WizardForm.StatusLabel.Caption := 'Setting up the library and tools...';
  if Advanced then
    Lib := RemoveBackslashUnlessRoot(LibPage.Values[0])
  else
    Lib := ExpandConstant('{%USERPROFILE}') + '\Videos\OLEDable';
  ForceDirectories(Lib);
  ForceDirectories(Lib + '\Converted');   { exports go here; the app's default, so it follows the library if it moves }

  Ff := '';
  Act := Choice(FfPage, FfActs);
  if Act = 'found' then Ff := FoundFfmpeg
  else if Act = 'browse' then Ff := FfFilePage.Values[0]
  else if (Act = 'download') and Unpack('ffmpeg.zip', ExpandConstant('{app}\ffmpeg'), 'ffmpeg.exe') then
    Ff := ExpandConstant('{app}\ffmpeg\ffmpeg.exe');

  Rife := '';
  Act := Choice(RifePage, RifeActs);
  if Act = 'found' then Rife := FoundRife
  else if Act = 'browse' then Rife := RemoveBackslashUnlessRoot(RifeDirPage.Values[0])
  else if (Act = 'download') and Unpack('rife.zip', ExpandConstant('{app}\rife'), 'rife-ncnn-vulkan.exe') then
    Rife := ExpandConstant('{app}\rife');

  Esr := '';
  Act := Choice(EsrPage, EsrActs);
  if Act = 'found' then Esr := FoundEsr
  else if (Act = 'download') and Unpack('realesrgan.zip', ExpandConstant('{app}\realesrgan'), 'realesrgan-ncnn-vulkan.exe') then
    Esr := ExpandConstant('{app}\realesrgan');

  SetArrayLength(Lines, 1);
  Lines[0] := '{"OutDir":' + JsonStr(Lib) + ',"ConvertedDir":"","ConvertedOffered":true' +
              ',"FfmpegPath":' + JsonStr(Ff) + ',"RifeDir":' + JsonStr(Rife) + ',"EsrganDir":' + JsonStr(Esr) + '}';
  SaveStringsToUTF8File(SettingsFile, Lines, False);
end;
