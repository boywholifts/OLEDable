; HydroShift Recorder installer (Inno Setup 6.1 or newer)
; Build: run build-installer.cmd in the project folder, or let the GitHub workflow do it.
; Sets everything up before the first launch: the library folder (and its converted-videos subfolder),
; ffmpeg, RIFE smoothing and AI enhance, and writes the choices into the app's settings file.

#ifndef AppVersion
  #define AppVersion "2.1.0"
#endif

[Setup]
AppId={{6B2F0E5C-8C1E-4B7E-9A51-2D1C4F7E9B13}
AppName=HydroShift Recorder
AppVersion={#AppVersion}
AppPublisher=HydroShift Recorder
DefaultDirName={localappdata}\Programs\HydroShift Recorder
; per-user install: no admin prompt, and the app folder stays writable (settings and tools live next to the exe)
PrivilegesRequired=lowest
DisableProgramGroupPage=yes
OutputDir=..\dist-installer
OutputBaseFilename=HydroShiftRecorder-Setup-{#AppVersion}
SetupIconFile=..\assets\icon.ico
UninstallDisplayIcon={app}\HydroShiftRecorder.exe
UninstallDisplayName=HydroShift Recorder
WizardStyle=modern
Compression=lzma2
SolidCompression=yes

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"

[Files]
Source: "..\dist\HydroShiftRecorder.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\HydroShiftRecorder.exe.config"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\dist\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "unpack.ps1"; DestDir: "{tmp}"; Flags: dontcopy

[Icons]
Name: "{autoprograms}\HydroShift Recorder"; Filename: "{app}\HydroShiftRecorder.exe"
Name: "{autodesktop}\HydroShift Recorder"; Filename: "{app}\HydroShiftRecorder.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\HydroShiftRecorder.exe"; Description: "Open HydroShift Recorder"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; tools the installer or the app downloaded; the library folder and its videos are never touched
Type: filesandordirs; Name: "{app}\ffmpeg"
Type: filesandordirs; Name: "{app}\rife"
Type: filesandordirs; Name: "{app}\realesrgan"
Type: files; Name: "{app}\HydroShift-Recorder.json"

[Code]
const
  FfmpegUrl = 'https://www.gyan.dev/ffmpeg/builds/ffmpeg-release-essentials.zip';
  RifeUrl   = 'https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip';
  EsrganUrl = 'https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip';

var
  LibPage: TInputDirWizardPage;
  ConvPage: TInputQueryWizardPage;
  FfPage: TInputOptionWizardPage;
  FfFilePage: TInputFileWizardPage;
  RifePage: TInputOptionWizardPage;
  RifeDirPage: TInputDirWizardPage;
  EsrPage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  FoundFfmpeg: String;

function SettingsFile: String;
begin
  Result := AddBackslash(WizardDirValue) + 'HydroShift-Recorder.json';
end;

{ An update keeps the existing settings, so the setup pages are only for a first install. }
function IsUpdate: Boolean;
begin
  Result := FileExists(SettingsFile);
end;

function FindFfmpeg: String;
var
  Links: String;
begin
  Result := FileSearch('ffmpeg.exe', GetEnv('PATH'));
  if Result = '' then
  begin
    Links := ExpandConstant('{localappdata}\Microsoft\WinGet\Links');
    if FileExists(Links + '\ffmpeg.exe') then Result := Links + '\ffmpeg.exe';
  end;
end;

procedure InitializeWizard;
begin
  LibPage := CreateInputDirPage(wpSelectDir, 'Library folder',
    'Where should your HydroShift videos live?',
    'The app''s library shows the videos in this folder. It''s created now if it doesn''t exist.',
    False, 'HydroShift');
  LibPage.Add('Library folder:');
  LibPage.Values[0] := ExpandConstant('{%USERPROFILE}') + '\Videos\HydroShift';

  ConvPage := CreateInputQueryPage(LibPage.ID, 'Converted videos',
    'Finished videos get their own folder inside the library',
    'Originals and recordings stay in the library folder; everything you export goes into this subfolder.');
  ConvPage.Add('Subfolder name:', False);
  ConvPage.Values[0] := 'Converted';

  FoundFfmpeg := FindFfmpeg;
  FfPage := CreateInputOptionPage(ConvPage.ID, 'ffmpeg',
    'ffmpeg records and converts the videos',
    'Choose which ffmpeg the app should use.', True, False);
  if FoundFfmpeg <> '' then
    FfPage.Add('Use the ffmpeg already on this PC (' + FoundFfmpeg + ')')
  else
    FfPage.Add('Use the ffmpeg already on this PC (none found)');
  FfPage.Add('Download ffmpeg now (about 100 MB, from gyan.dev)');
  FfPage.Add('Choose ffmpeg.exe myself');
  if FoundFfmpeg <> '' then FfPage.SelectedValueIndex := 0 else FfPage.SelectedValueIndex := 1;

  FfFilePage := CreateInputFilePage(FfPage.ID, 'ffmpeg',
    'Where is ffmpeg.exe?', 'ffprobe.exe should be in the same folder.');
  FfFilePage.Add('ffmpeg.exe:', 'ffmpeg.exe|ffmpeg.exe|Programs|*.exe', '.exe');

  RifePage := CreateInputOptionPage(FfFilePage.ID, 'Smoothing (RIFE)',
    'RIFE fills in frames to make videos smooth 60 fps',
    'It runs on your graphics card.', True, False);
  RifePage.Add('Download RIFE now (about 30 MB)');
  RifePage.Add('Download it the first time it''s needed');
  RifePage.Add('Use a RIFE folder I already have');
  RifePage.SelectedValueIndex := 0;

  RifeDirPage := CreateInputDirPage(RifePage.ID, 'Smoothing (RIFE)',
    'Where is RIFE?', 'Choose the folder that contains rife-ncnn-vulkan.exe and the rife-v4.6 model folder.',
    False, '');
  RifeDirPage.Add('RIFE folder:');

  EsrPage := CreateInputOptionPage(RifeDirPage.ID, 'AI enhance (Real-ESRGAN)',
    'Real-ESRGAN redraws small videos at a higher resolution, for a sharper picture',
    'It runs on your graphics card.', True, False);
  EsrPage.Add('Download Real-ESRGAN now (about 50 MB)');
  EsrPage.Add('Download it the first time it''s needed');
  EsrPage.SelectedValueIndex := 0;

  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), 'Downloading the tools you chose...', nil);
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if ((PageID = LibPage.ID) or (PageID = ConvPage.ID) or (PageID = FfPage.ID) or (PageID = FfFilePage.ID) or
      (PageID = RifePage.ID) or (PageID = RifeDirPage.ID) or (PageID = EsrPage.ID)) and IsUpdate then
    Result := True
  else if PageID = FfFilePage.ID then
    Result := FfPage.SelectedValueIndex <> 2
  else if PageID = RifeDirPage.ID then
    Result := RifePage.SelectedValueIndex <> 2;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = ConvPage.ID) and (Trim(ConvPage.Values[0]) = '') then
  begin
    MsgBox('Enter a name for the converted videos folder.', mbError, MB_OK);
    Result := False;
  end
  else if (CurPageID = FfPage.ID) and (FfPage.SelectedValueIndex = 0) and (FoundFfmpeg = '') then
  begin
    MsgBox('No ffmpeg was found on this PC. Download it, or choose ffmpeg.exe yourself.', mbError, MB_OK);
    Result := False;
  end
  else if (CurPageID = FfFilePage.ID) and not FileExists(FfFilePage.Values[0]) then
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
    if FfPage.SelectedValueIndex = 1 then DownloadPage.Add(FfmpegUrl, 'ffmpeg.zip', '');
    if RifePage.SelectedValueIndex = 0 then DownloadPage.Add(RifeUrl, 'rife.zip', '');
    if EsrPage.SelectedValueIndex = 0 then DownloadPage.Add(EsrganUrl, 'realesrgan.zip', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Result := False
        else
          Result := MsgBox('A download failed: ' + GetExceptionMessage + #13#10#13#10 +
                           'Continue anyway? The app downloads RIFE and Real-ESRGAN itself when needed; ffmpeg can be installed later with: winget install Gyan.FFmpeg',
                           mbConfirmation, MB_YESNO) = IDYES;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

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
  Lib, ConvName, Conv, Ff, Rife, Esr: String;
  Lines: TArrayOfString;
begin
  if (CurStep <> ssPostInstall) or IsUpdate then Exit;

  WizardForm.StatusLabel.Caption := 'Setting up the library and tools…';
  Lib := RemoveBackslashUnlessRoot(LibPage.Values[0]);
  ConvName := Trim(ConvPage.Values[0]);
  ForceDirectories(Lib);
  ForceDirectories(Lib + '\' + ConvName);
  { "Converted" is the app's default and follows the library if it's moved later }
  if CompareText(ConvName, 'Converted') = 0 then Conv := '' else Conv := Lib + '\' + ConvName;

  Ff := '';
  case FfPage.SelectedValueIndex of
    0: Ff := FoundFfmpeg;
    1: if Unpack('ffmpeg.zip', ExpandConstant('{app}\ffmpeg'), 'ffmpeg.exe') then Ff := ExpandConstant('{app}\ffmpeg\ffmpeg.exe');
    2: Ff := FfFilePage.Values[0];
  end;

  Rife := '';
  if RifePage.SelectedValueIndex = 0 then
  begin
    if Unpack('rife.zip', ExpandConstant('{app}\rife'), 'rife-ncnn-vulkan.exe') then Rife := ExpandConstant('{app}\rife');
  end
  else if RifePage.SelectedValueIndex = 2 then
    Rife := RemoveBackslashUnlessRoot(RifeDirPage.Values[0]);

  Esr := '';
  if EsrPage.SelectedValueIndex = 0 then
    if Unpack('realesrgan.zip', ExpandConstant('{app}\realesrgan'), 'realesrgan-ncnn-vulkan.exe') then Esr := ExpandConstant('{app}\realesrgan');

  SetArrayLength(Lines, 1);
  Lines[0] := '{"OutDir":' + JsonStr(Lib) + ',"ConvertedDir":' + JsonStr(Conv) + ',"ConvertedOffered":true' +
              ',"FfmpegPath":' + JsonStr(Ff) + ',"RifeDir":' + JsonStr(Rife) + ',"EsrganDir":' + JsonStr(Esr) + '}';
  SaveStringsToUTF8File(SettingsFile, Lines, False);
end;
