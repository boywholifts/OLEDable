<#
HydroShift Recorder
Records whatever is playing on the Lian Li HydroShift II OLED Curved screen, or converts a video
(including Wallpaper Engine video wallpapers), and saves a seamless-looping 2288x1048 MP4
ready to upload in L-Connect 3. Videos under 60 fps can be smoothed to 60 fps with RIFE (AI frame
interpolation, downloaded automatically on first use).

Run it:              right-click > Run with PowerShell   (or the shortcut below)
Add a shortcut:      powershell -ExecutionPolicy Bypass -File .\HydroShift-Recorder.ps1 -Install
Needs ffmpeg:        winget install Gyan.FFmpeg
#>
param([switch]$Install)

$PS51 = "$env:WINDIR\System32\WindowsPowerShell\v1.0\powershell.exe"

# Works both as a .ps1 and as an .exe compiled with PS2EXE (where $PSScriptRoot is empty)
$SelfPath = [Diagnostics.Process]::GetCurrentProcess().MainModule.FileName
$IsExe = $SelfPath -notmatch '\\(powershell|pwsh)\.exe$'
$AppDir = if ($IsExe) { Split-Path $SelfPath } else { $PSScriptRoot }
$LaunchArgs = "-NoProfile -ExecutionPolicy Bypass -STA -WindowStyle Hidden -File `"$PSCommandPath`""

if ($Install) {
    $ws = New-Object -ComObject WScript.Shell
    foreach ($dir in @([Environment]::GetFolderPath('Desktop'), [Environment]::GetFolderPath('Programs'))) {
        $l = $ws.CreateShortcut((Join-Path $dir 'HydroShift Recorder.lnk'))
        if ($IsExe) { $l.TargetPath = $SelfPath } else { $l.TargetPath = $PS51; $l.Arguments = $LaunchArgs }
        $l.WorkingDirectory = $AppDir
        $l.Save()
    }
    Write-Host 'Added "HydroShift Recorder" to your Desktop and Start menu.'
    return
}

# WPF needs a single-threaded apartment (PowerShell 7 isn't), so relaunch in Windows PowerShell if needed
if (-not $IsExe -and [Threading.Thread]::CurrentThread.GetApartmentState() -ne 'STA') {
    Start-Process $PS51 $LaunchArgs
    return
}

Add-Type -AssemblyName PresentationFramework, PresentationCore, WindowsBase, System.Windows.Forms, System.IO.Compression.FileSystem

# FfJob: runs ffmpeg/RIFE in the background and reads progress without blocking the window.
# Dl: downloads a file on a background thread with progress.
if (-not ('FfJob' -as [type])) {
Add-Type @'
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
public class FfJob {
    Process p; StringBuilder err = new StringBuilder(); long outUs = 0;
    public FfJob(string exe, string args) {
        var si = new ProcessStartInfo(exe, args);
        si.UseShellExecute = false; si.CreateNoWindow = true;
        si.RedirectStandardError = true; si.RedirectStandardOutput = true; si.RedirectStandardInput = true;
        p = new Process(); p.StartInfo = si;
        p.ErrorDataReceived += OnLine; p.OutputDataReceived += OnLine;
        p.Start(); p.BeginErrorReadLine(); p.BeginOutputReadLine();
    }
    void OnLine(object s, DataReceivedEventArgs e) {
        if (e.Data == null) return;
        if (e.Data.StartsWith("out_time_us=") || e.Data.StartsWith("out_time_ms=")) {
            long v; if (long.TryParse(e.Data.Substring(12), out v)) outUs = v; return;
        }
        if (Regex.IsMatch(e.Data, "^[a-z_0-9]+=")) return;
        lock (err) { err.AppendLine(e.Data); if (err.Length > 8000) err.Remove(0, err.Length - 8000); }
    }
    public bool Done { get { return p.HasExited; } }
    public int Code { get { return p.ExitCode; } }
    public double Seconds { get { return outUs / 1e6; } }
    public string Errors { get { lock (err) return err.ToString(); } }
    public void Quit() { try { p.StandardInput.Write("q"); p.StandardInput.Flush(); } catch {} }
    public void Kill() { try { if (!p.HasExited) p.Kill(); } catch {} }
    public bool Wait(int ms) { return p.WaitForExit(ms); }
    public void Drain() { try { p.WaitForExit(); } catch {} }
}
public class Dl {
    public long Got = 0, Total = 0; public volatile bool Done = false, Cancel = false; public string Error = null;
    public Dl(string url, string path) {
        var t = new Thread(() => Run(url, path)); t.IsBackground = true; t.Start();
    }
    void Run(string url, string path) {
        try {
            ServicePointManager.SecurityProtocol = SecurityProtocolType.Tls12;
            var req = (HttpWebRequest)WebRequest.Create(url);
            req.UserAgent = "HydroShift-Recorder";
            using (var resp = req.GetResponse()) {
                Total = resp.ContentLength;
                using (var s = resp.GetResponseStream()) using (var f = File.Create(path)) {
                    var buf = new byte[1 << 20]; int n;
                    while ((n = s.Read(buf, 0, buf.Length)) > 0) {
                        if (Cancel) throw new Exception("cancelled");
                        f.Write(buf, 0, n); Got += n;
                    }
                }
            }
        } catch (Exception e) { Error = e.Message; }
        Done = true;
    }
}
'@
}

# ---------- settings ----------
$SettingsPath = Join-Path $AppDir 'HydroShift-Recorder.json'
$S = [ordered]@{
    Seconds = 45; Fps = 60; Loop = $true; TrueBlack = $true; Countdown = $true; Smooth = $true; Crossfade = 1
    OutDir = (Join-Path $HOME 'Videos\HydroShift'); Encoder = 'nvenc'; Adapter = -1; Output = -1
}
if (Test-Path $SettingsPath) {
    try {
        $saved = Get-Content $SettingsPath -Raw | ConvertFrom-Json
        foreach ($p in $saved.PSObject.Properties) { if ($S.Contains($p.Name)) { $S[$p.Name] = $p.Value } }
    } catch {}
}
# reuse the display found by the old command-line script, if any
$oldCfg = Join-Path $AppDir 'hydroshift-capture.json'
if ($S.Adapter -lt 0 -and (Test-Path $oldCfg)) {
    try { $o = Get-Content $oldCfg -Raw | ConvertFrom-Json; if ($o.Method -eq 'dda') { $S.Adapter = [int]$o.Adapter; $S.Output = [int]$o.Output } } catch {}
}
function Save-Settings { try { $S | ConvertTo-Json | Set-Content -Path $SettingsPath -Encoding UTF8 } catch {} }

# ---------- tools ----------
function Find-Tool([string]$name) {
    $c = Get-Command $name -ErrorAction SilentlyContinue
    if ($c) { return $c.Source }
    $p = Join-Path $env:LOCALAPPDATA "Microsoft\WinGet\Links\$name.exe"
    if (Test-Path $p) { return $p }
    return $null
}
$FF = Find-Tool 'ffmpeg'
$FFPROBE = Find-Tool 'ffprobe'
$RifeDir = Join-Path $AppDir 'rife'
$RifeExe = Join-Path $RifeDir 'rife-ncnn-vulkan.exe'
$RifeUrl = 'https://github.com/nihui/rife-ncnn-vulkan/releases/download/20221029/rife-ncnn-vulkan-20221029-windows.zip'
$Inv = [Globalization.CultureInfo]::InvariantCulture

$Fit   = 'scale=2288:1048:force_original_aspect_ratio=increase:flags=lanczos,crop=2288:1048,setsar=1'
$ToYuv = 'scale=out_color_matrix=bt709:out_range=tv,format=yuv420p'
$Tags  = '-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv'
$EncFinal = "-c:v libx264 -preset medium -crf 18 -pix_fmt yuv420p $Tags -movflags +faststart"

function Q([string]$p) { '"' + $p + '"' }
function Num([double]$v) { $v.ToString('0.####', $script:Inv) }
function Start-Ff([string]$a) { New-Object FfJob -ArgumentList $script:FF, $a }
function Grab-Args([int]$a, [int]$o, [string]$png) {
    "-y -hide_banner -loglevel error -init_hw_device d3d11va=hs:$a -filter_hw_device hs -filter_complex `"ddagrab=output_idx=${o}:framerate=1:draw_mouse=0,hwdownload,format=bgra`" -frames:v 1 $(Q $png)"
}
function Get-ImageSize([string]$png) {
    $f = [System.Windows.Media.Imaging.BitmapFrame]::Create((New-Object Uri $png),
        [System.Windows.Media.Imaging.BitmapCreateOptions]::IgnoreImageCache, [System.Windows.Media.Imaging.BitmapCacheOption]::OnLoad)
    , @($f.PixelWidth, $f.PixelHeight)
}
function Test-HsSize($w, $h) { ($w -eq 2288 -and $h -eq 1048) -or ($w -eq 1048 -and $h -eq 2288) }
function Parse-Rate([string]$r) {
    if ($r -match '^(\d+)/(\d+)$' -and [double]$Matches[2] -gt 0) { return [double]$Matches[1] / [double]$Matches[2] }
    return 0
}
function Get-VideoInfo([string]$file) {
    if (-not $script:FFPROBE) { return $null }
    try {
        # run ffprobe hidden (a console app launched from a windowless exe would flash a console window)
        $p = New-Object FfJob -ArgumentList $script:FFPROBE, "-v error -select_streams v:0 -show_entries stream=avg_frame_rate,r_frame_rate:format=duration -of json $(Q $file)"
        if (-not $p.Wait(15000)) { $p.Kill() }
        $p.Drain()
        $j = $p.Errors | ConvertFrom-Json
        $st = @($j.streams)[0]
        if (-not $st) { return $null }
        $fps = Parse-Rate $st.avg_frame_rate
        if ($fps -le 0 -or $fps -gt 240) { $fps = Parse-Rate $st.r_frame_rate }
        $dur = 0.0
        [void][double]::TryParse("$($j.format.duration)", [Globalization.NumberStyles]::Float, $script:Inv, [ref]$dur)
        return @{ Fps = $fps; Duration = $dur }
    } catch { return $null }
}
function Last-Line([string]$text) {
    $l = $text -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -Last 1
    if ($l) { $l.Trim() } else { 'unknown error' }
}
function Fmt([double]$sec) { $s = [int][math]::Floor([math]::Max(0, $sec)); '{0}:{1:00}' -f [int][math]::Floor($s / 60), ($s % 60) }
function Brush([string]$hex) { [System.Windows.Media.BrushConverter]::new().ConvertFromString($hex) }
function Find-WorkshopDir {
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
        $libs = @($steam)
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) {
            $libs += [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"') | ForEach-Object { $_.Groups[1].Value -replace '\\\\', '\' }
        }
        foreach ($l in $libs) { $p = Join-Path $l 'steamapps\workshop\content\431960'; if (Test-Path $p) { return $p } }
    } catch {}
    return $null
}

# ---------- window ----------
[xml]$xaml = @'
<Window xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
        xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
        Title="HydroShift Recorder" Width="484" SizeToContent="Height"
        WindowStyle="None" AllowsTransparency="True" Background="Transparent"
        ResizeMode="NoResize" WindowStartupLocation="CenterScreen" AllowDrop="True"
        FontFamily="Segoe UI Variable Display, Segoe UI" Foreground="#FFF5F5F7"
        UseLayoutRounding="True">
  <Window.Resources>
    <SolidColorBrush x:Key="Accent" Color="#FFBF5AF2"/>
    <SolidColorBrush x:Key="Card" Color="#FF2C2C2E"/>
    <SolidColorBrush x:Key="Sub" Color="#FF8E8E93"/>
    <SolidColorBrush x:Key="Line" Color="#FF3A3A3C"/>

    <Style x:Key="Knob" TargetType="Thumb">
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Thumb">
        <Ellipse Width="20" Height="20" Fill="White">
          <Ellipse.Effect><DropShadowEffect BlurRadius="6" ShadowDepth="1" Opacity="0.35"/></Ellipse.Effect>
        </Ellipse>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="FillRepeat" TargetType="RepeatButton">
      <Setter Property="Focusable" Value="False"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton">
        <Border Background="Transparent"><Border Height="4" CornerRadius="2" Background="#FFBF5AF2" VerticalAlignment="Center"/></Border>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="EmptyRepeat" TargetType="RepeatButton">
      <Setter Property="Focusable" Value="False"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RepeatButton">
        <Border Background="Transparent"/>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="ThinSlider" TargetType="Slider">
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Slider">
        <Grid Height="24">
          <Border Height="4" CornerRadius="2" Background="#FF48484A" VerticalAlignment="Center"/>
          <Track x:Name="PART_Track">
            <Track.DecreaseRepeatButton><RepeatButton Command="Slider.DecreaseLarge" Style="{StaticResource FillRepeat}"/></Track.DecreaseRepeatButton>
            <Track.IncreaseRepeatButton><RepeatButton Command="Slider.IncreaseLarge" Style="{StaticResource EmptyRepeat}"/></Track.IncreaseRepeatButton>
            <Track.Thumb><Thumb Style="{StaticResource Knob}"/></Track.Thumb>
          </Track>
        </Grid>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="Switch" TargetType="ToggleButton">
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ToggleButton">
        <Grid Width="46" Height="28" Background="Transparent">
          <Border x:Name="Track" CornerRadius="14" Background="#FF48484A"/>
          <Ellipse x:Name="Dot" Width="24" Height="24" Fill="White" HorizontalAlignment="Left" Margin="2,0,0,0"/>
        </Grid>
        <ControlTemplate.Triggers>
          <Trigger Property="IsChecked" Value="True">
            <Setter TargetName="Track" Property="Background" Value="#FFBF5AF2"/>
            <Setter TargetName="Dot" Property="HorizontalAlignment" Value="Right"/>
            <Setter TargetName="Dot" Property="Margin" Value="0,0,2,0"/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="Seg" TargetType="RadioButton">
      <Setter Property="Foreground" Value="#FFF5F5F7"/>
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="RadioButton">
        <Border x:Name="b" CornerRadius="7" Padding="14,4" Background="Transparent">
          <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsChecked" Value="True"><Setter TargetName="b" Property="Background" Value="#FF636366"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="Pill" TargetType="Button">
      <Setter Property="Foreground" Value="#FFF5F5F7"/>
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
        <Border x:Name="b" CornerRadius="8" Background="#FF3A3A3C" Padding="12,6">
          <ContentPresenter HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter TargetName="b" Property="Background" Value="#FF48484A"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="Link" TargetType="Button">
      <Setter Property="Foreground" Value="#FFBF5AF2"/>
      <Setter Property="FontSize" Value="13"/>
      <Setter Property="Cursor" Value="Hand"/>
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="Button">
        <Border Background="Transparent" Padding="2"><ContentPresenter/></Border>
        <ControlTemplate.Triggers>
          <Trigger Property="IsMouseOver" Value="True"><Setter Property="Opacity" Value="0.75"/></Trigger>
          <Trigger Property="IsEnabled" Value="False"><Setter Property="Opacity" Value="0.35"/></Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
    <Style x:Key="Thin" TargetType="ProgressBar">
      <Setter Property="Template"><Setter.Value><ControlTemplate TargetType="ProgressBar">
        <Grid Height="4">
          <Border x:Name="PART_Track" CornerRadius="2" Background="#FF3A3A3C"/>
          <Border x:Name="PART_Indicator" CornerRadius="2" Background="#FFBF5AF2" HorizontalAlignment="Left"/>
        </Grid>
      </ControlTemplate></Setter.Value></Setter>
    </Style>
  </Window.Resources>

  <Grid Margin="14">
    <Border CornerRadius="16" Background="#FF1C1C1E">
      <Border.Effect><DropShadowEffect BlurRadius="28" ShadowDepth="6" Opacity="0.55" Color="Black"/></Border.Effect>
    </Border>
    <Border CornerRadius="16" Background="#FF1C1C1E" BorderBrush="#22FFFFFF" BorderThickness="1">
      <StackPanel Margin="20,0,20,18">

        <Grid x:Name="TitleBar" Height="44" Background="Transparent">
          <StackPanel Orientation="Horizontal" HorizontalAlignment="Left" VerticalAlignment="Center">
            <Ellipse x:Name="BtnClose" Width="12" Height="12" Fill="#FFFF5F57" Cursor="Hand" Margin="0,0,8,0" ToolTip="Close"/>
            <Ellipse x:Name="BtnMin" Width="12" Height="12" Fill="#FFFEBC2E" Cursor="Hand" Margin="0,0,8,0" ToolTip="Minimize"/>
            <Ellipse Width="12" Height="12" Fill="#FF3A3A3C"/>
          </StackPanel>
          <TextBlock Text="HydroShift Recorder" HorizontalAlignment="Center" VerticalAlignment="Center"
                     FontSize="13" FontWeight="SemiBold" Foreground="{StaticResource Sub}"/>
        </Grid>

        <Grid x:Name="PreviewArea" Height="190" Margin="0,4,0,0" Cursor="Hand" ToolTip="Click to refresh the preview. Drop a video here to convert it.">
          <Border x:Name="Preview" CornerRadius="12" Background="Black" BorderBrush="#FF2C2C2E" BorderThickness="1"/>
          <TextBlock x:Name="PreviewHint" Text="Looking for your HydroShift…" HorizontalAlignment="Center"
                     VerticalAlignment="Center" Foreground="{StaticResource Sub}" FontSize="13"/>
          <Border x:Name="RecPill" Visibility="Collapsed" HorizontalAlignment="Left" VerticalAlignment="Top"
                  Margin="10" CornerRadius="10" Background="#CC000000" Padding="9,3">
            <StackPanel Orientation="Horizontal">
              <Ellipse x:Name="RecDot" Width="8" Height="8" Fill="#FFFF453A" VerticalAlignment="Center" Margin="0,0,6,0"/>
              <TextBlock x:Name="RecTime" Text="0:00" FontSize="12" FontWeight="SemiBold"/>
            </StackPanel>
          </Border>
          <TextBlock x:Name="PreviewInfo" HorizontalAlignment="Right" VerticalAlignment="Bottom"
                     Margin="0,0,12,8" FontSize="11" Foreground="#B3FFFFFF"/>
        </Grid>

        <Border x:Name="NameCard" Background="{StaticResource Card}" CornerRadius="12" Margin="0,16,0,0" Padding="14,10">
          <Grid>
            <TextBox x:Name="NameBox" Background="Transparent" BorderThickness="0" Foreground="#FFF5F5F7"
                     CaretBrush="White" FontSize="15" Padding="0"/>
            <TextBlock x:Name="NamePh" Text="Name (optional)" Foreground="#FF636366" FontSize="15"
                       Margin="2,0,0,0" IsHitTestVisible="False"/>
          </Grid>
        </Border>

        <Border x:Name="SettingsCard" Background="{StaticResource Card}" CornerRadius="12" Margin="0,12,0,0">
          <StackPanel>
            <StackPanel Margin="16,12,16,8">
              <Grid>
                <TextBlock Text="Length" FontSize="15" VerticalAlignment="Center"/>
                <TextBlock x:Name="LenText" Text="45 s" HorizontalAlignment="Right" Foreground="{StaticResource Sub}"
                           FontSize="15" Cursor="IBeam" ToolTip="Click to type an exact length, like 90 or 1:30"/>
                <TextBox x:Name="LenBox" Visibility="Collapsed" HorizontalAlignment="Right" Width="80" TextAlignment="Right"
                         Background="#FF1C1C1E" BorderThickness="0" Foreground="#FFF5F5F7" CaretBrush="White"
                         FontSize="15" Padding="4,0"/>
              </Grid>
              <Slider x:Name="LenSlider" Style="{StaticResource ThinSlider}" Minimum="5" Maximum="300"
                      IsMoveToPointEnabled="True" Margin="0,6,0,0"/>
            </StackPanel>
            <Border Height="1" Background="{StaticResource Line}" Margin="16,0,0,0"/>

            <Grid Margin="16,9">
              <TextBlock Text="Frame rate" FontSize="15" VerticalAlignment="Center"/>
              <Border HorizontalAlignment="Right" Background="#FF1C1C1E" CornerRadius="9" Padding="2">
                <StackPanel Orientation="Horizontal">
                  <RadioButton x:Name="Fps30" Content="30" GroupName="fps" Style="{StaticResource Seg}"/>
                  <RadioButton x:Name="Fps60" Content="60" GroupName="fps" Style="{StaticResource Seg}"/>
                </StackPanel>
              </Border>
            </Grid>
            <Border Height="1" Background="{StaticResource Line}" Margin="16,0,0,0"/>

            <Grid Margin="16,9">
              <StackPanel VerticalAlignment="Center">
                <TextBlock Text="Seamless loop" FontSize="15"/>
                <TextBlock Text="Crossfades the end into the start" FontSize="12" Foreground="{StaticResource Sub}"/>
              </StackPanel>
              <ToggleButton x:Name="LoopSw" Style="{StaticResource Switch}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
            </Grid>
            <Border Height="1" Background="{StaticResource Line}" Margin="16,0,0,0"/>

            <Grid Margin="16,9">
              <StackPanel VerticalAlignment="Center">
                <TextBlock Text="True black" FontSize="15"/>
                <TextBlock Text="Turns near-black pixels fully off" FontSize="12" Foreground="{StaticResource Sub}"/>
              </StackPanel>
              <ToggleButton x:Name="BlackSw" Style="{StaticResource Switch}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
            </Grid>
            <Border Height="1" Background="{StaticResource Line}" Margin="16,0,0,0"/>

            <Grid Margin="16,9">
              <StackPanel VerticalAlignment="Center">
                <TextBlock Text="Smooth to 60 fps" FontSize="15"/>
                <TextBlock Text="AI frame interpolation for converted videos" FontSize="12" Foreground="{StaticResource Sub}"/>
              </StackPanel>
              <ToggleButton x:Name="SmoothSw" Style="{StaticResource Switch}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
            </Grid>
            <Border Height="1" Background="{StaticResource Line}" Margin="16,0,0,0"/>

            <Grid Margin="16,9">
              <StackPanel VerticalAlignment="Center">
                <TextBlock Text="Countdown" FontSize="15"/>
                <TextBlock Text="3 seconds before recording starts" FontSize="12" Foreground="{StaticResource Sub}"/>
              </StackPanel>
              <ToggleButton x:Name="CountSw" Style="{StaticResource Switch}" HorizontalAlignment="Right" VerticalAlignment="Center"/>
            </Grid>
            <Border Height="1" Background="{StaticResource Line}" Margin="16,0,0,0"/>

            <Grid x:Name="SaveRow" Margin="16,11" Background="Transparent" Cursor="Hand" ToolTip="Choose where videos are saved">
              <TextBlock Text="Save to" FontSize="15"/>
              <StackPanel Orientation="Horizontal" HorizontalAlignment="Right">
                <TextBlock x:Name="SaveText" Foreground="{StaticResource Sub}" FontSize="15" MaxWidth="250" TextTrimming="CharacterEllipsis"/>
                <TextBlock Text="›" Foreground="{StaticResource Sub}" FontSize="18" Margin="8,-3,0,0"/>
              </StackPanel>
            </Grid>
          </StackPanel>
        </Border>

        <Grid x:Name="RecBtn" Width="76" Height="76" Margin="0,22,0,0" Cursor="Hand" Background="Transparent" ToolTip="Record">
          <Ellipse Stroke="#FFF5F5F7" StrokeThickness="3.5"/>
          <Border x:Name="RecInner" Width="60" Height="60" CornerRadius="30" Background="#FFFF453A"
                  HorizontalAlignment="Center" VerticalAlignment="Center"/>
          <TextBlock x:Name="RecCount" FontSize="26" FontWeight="SemiBold" HorizontalAlignment="Center" VerticalAlignment="Center"/>
        </Grid>
        <TextBlock x:Name="Status" Text="Ready" HorizontalAlignment="Center" Margin="0,12,0,0"
                   Foreground="{StaticResource Sub}" FontSize="13" TextAlignment="Center" TextWrapping="Wrap"/>
        <ProgressBar x:Name="Prog" Style="{StaticResource Thin}" Margin="60,10,60,0" Minimum="0" Maximum="1" Visibility="Hidden"/>

        <Border x:Name="ResultCard" Visibility="Collapsed" Background="{StaticResource Card}" CornerRadius="12" Margin="0,14,0,0" Padding="16,10">
          <Grid>
            <StackPanel VerticalAlignment="Center" Margin="0,0,170,0">
              <TextBlock x:Name="ResultName" FontSize="14" TextTrimming="CharacterEllipsis"/>
              <TextBlock x:Name="ResultInfo" FontSize="12" Foreground="{StaticResource Sub}"/>
            </StackPanel>
            <StackPanel Orientation="Horizontal" HorizontalAlignment="Right" VerticalAlignment="Center">
              <Button x:Name="PlayBtn" Content="Play" Style="{StaticResource Pill}" Margin="0,0,8,0"/>
              <Button x:Name="ShowBtn" Content="Show in Folder" Style="{StaticResource Pill}"/>
            </StackPanel>
          </Grid>
        </Border>

        <Grid Margin="0,16,0,0">
          <Button x:Name="OpenBtn" Content="Open folder" Style="{StaticResource Link}" HorizontalAlignment="Left"/>
          <Button x:Name="ConvertBtn" Content="Convert a video…" Style="{StaticResource Link}" HorizontalAlignment="Center"/>
          <Button x:Name="RedetectBtn" Content="Find display again" Style="{StaticResource Link}" HorizontalAlignment="Right"/>
        </Grid>
      </StackPanel>
    </Border>
  </Grid>
</Window>
'@

$win = [Windows.Markup.XamlReader]::Load((New-Object System.Xml.XmlNodeReader $xaml))
$ui = @{}
foreach ($n in 'TitleBar','BtnClose','BtnMin','PreviewArea','Preview','PreviewHint','RecPill','RecDot','RecTime','PreviewInfo',
               'NameCard','NameBox','NamePh','SettingsCard','LenText','LenBox','LenSlider','Fps30','Fps60','LoopSw','BlackSw',
               'SmoothSw','CountSw','SaveRow','SaveText','RecBtn','RecInner','RecCount','Status','Prog','ResultCard',
               'ResultName','ResultInfo','PlayBtn','ShowBtn','OpenBtn','ConvertBtn','RedetectBtn') {
    $ui[$n] = $win.FindName($n)
}
$Red = Brush '#FFFF453A'; $Grey = Brush '#FF8E8E93'; $Purple = Brush '#FFBF5AF2'

# ---------- state ----------
# idle | countdown | recording | download | prep | rife | processing
$script:State = 'idle'
$script:Job = $null; $script:PreviewJob = $null; $script:Dl = $null; $script:Cancelled = $false
$script:Raw = Join-Path $env:TEMP 'hsr_raw.mp4'
$script:WorkDir = $null; $script:OutFile = $null; $script:TickN = 0; $script:SyncLen = $false

function Set-Status([string]$t) { $ui.Status.Text = $t }
function Flush-UI { $win.Dispatcher.Invoke([Action] {}, [System.Windows.Threading.DispatcherPriority]::Background) }
function Update-LenText { $v = [int]$S.Seconds; $ui.LenText.Text = if ($v -lt 60) { "$v s" } else { Fmt $v } }
function Set-Length([int]$v) {
    $v = [math]::Max(5, [math]::Min(600, $v))
    $S.Seconds = $v
    $script:SyncLen = $true
    if ($v -gt $ui.LenSlider.Maximum) { $ui.LenSlider.Maximum = $v }
    $ui.LenSlider.Value = $v
    $script:SyncLen = $false
    Update-LenText
}
function Parse-Length([string]$t) {
    $t = $t.Trim().ToLower()
    if ($t -match '^(\d+):(\d{1,2})$') { return [int]$Matches[1] * 60 + [int]$Matches[2] }
    if ($t -match '^(\d+)\s*m(in)?$') { return [int]$Matches[1] * 60 }
    if ($t -match '^(\d+)\s*s?(ec)?$') { return [int]$Matches[1] }
    return $null
}
function Commit-LenBox {
    if ($ui.LenBox.Visibility -ne 'Visible') { return }
    $v = Parse-Length $ui.LenBox.Text
    if ($null -ne $v) { Set-Length $v; Save-Settings }
    $ui.LenBox.Visibility = 'Collapsed'; $ui.LenText.Visibility = 'Visible'
}

function Set-Busy([bool]$busy) {
    foreach ($c in $ui.SettingsCard, $ui.NameCard) { $c.IsEnabled = -not $busy; $c.Opacity = if ($busy) { 0.5 } else { 1 } }
    $ui.RedetectBtn.IsEnabled = -not $busy
    $ui.ConvertBtn.IsEnabled = -not $busy
}
function Set-RecLook([string]$look) {
    $r = $ui.RecInner
    if ($look -eq 'circle') { $r.Width = 60; $r.Height = 60; $r.CornerRadius = New-Object System.Windows.CornerRadius 30; $r.Background = $Red; $ui.RecBtn.ToolTip = 'Record' }
    else { $r.Width = 28; $r.Height = 28; $r.CornerRadius = New-Object System.Windows.CornerRadius 7
           $r.Background = if ($look -eq 'grey') { $Grey } else { $Red }
           $ui.RecBtn.ToolTip = if ($look -eq 'grey') { 'Cancel' } else { 'Stop and keep what was recorded' } }
}
function Show-Pill([string]$text, $brush) { $ui.RecPill.Visibility = 'Visible'; $ui.RecTime.Text = $text; $ui.RecDot.Fill = $brush; $ui.RecDot.Opacity = 1 }
function Remove-Work {
    Remove-Item $script:Raw -ErrorAction SilentlyContinue
    if ($script:WorkDir -and (Test-Path $script:WorkDir)) { Remove-Item $script:WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
}
function Reset-Idle([string]$msg) {
    $script:State = 'idle'; $script:Job = $null; $script:Dl = $null
    Remove-Work
    Set-Busy $false; Set-RecLook 'circle'
    $ui.RecCount.Text = ''; $ui.RecPill.Visibility = 'Collapsed'; $ui.Prog.Visibility = 'Hidden'; $ui.Prog.Value = 0
    Set-Status $msg
    Refresh-Preview
}
function Read-Controls {
    $S.Fps = if ($ui.Fps30.IsChecked) { 30 } else { 60 }
    $S.Loop = [bool]$ui.LoopSw.IsChecked
    $S.TrueBlack = [bool]$ui.BlackSw.IsChecked
    $S.Smooth = [bool]$ui.SmoothSw.IsChecked
    $S.Countdown = [bool]$ui.CountSw.IsChecked
}
function Get-OutPath([string]$fallback) {
    New-Item -ItemType Directory -Force -Path $S.OutDir | Out-Null
    $n = $ui.NameBox.Text
    if (-not $n.Trim()) { $n = $fallback }
    $n = ($n -replace '[\\/:*?"<>|]', '').Trim()
    if (-not $n) { $n = 'HydroShift ' + (Get-Date -Format 'yyyy-MM-dd HH.mm') }
    $p = Join-Path $S.OutDir "$n.mp4"; $i = 2
    while (Test-Path $p) { $p = Join-Path $S.OutDir "$n ($i).mp4"; $i++ }
    $p
}

# ---------- preview / detection ----------
function Show-Image([string]$png, [string]$label) {
    $b = New-Object System.Windows.Media.Imaging.BitmapImage
    $b.BeginInit(); $b.CacheOption = 'OnLoad'; $b.CreateOptions = 'IgnoreImageCache'; $b.UriSource = New-Object Uri $png; $b.EndInit(); $b.Freeze()
    $br = New-Object System.Windows.Media.ImageBrush $b; $br.Stretch = 'UniformToFill'
    $ui.Preview.Background = $br
    $ui.PreviewHint.Visibility = 'Collapsed'
    $ui.PreviewInfo.Text = '{0}  ·  {1} × {2}' -f $label, $b.PixelWidth, $b.PixelHeight
}
function Refresh-Preview {
    if (-not $script:FF -or $S.Adapter -lt 0 -or $script:PreviewJob -or $script:State -ne 'idle') { return }
    $script:PreviewPng = Join-Path $env:TEMP ('hsr_prev_{0}.png' -f [guid]::NewGuid().ToString('N').Substring(0, 8))
    $script:PreviewJob = Start-Ff (Grab-Args $S.Adapter $S.Output $script:PreviewPng)
}
function Find-HydroShift {
    $png = Join-Path $env:TEMP 'hsr_probe.png'
    foreach ($a in 0..3) {
        foreach ($o in 0..5) {
            Remove-Item $png -ErrorAction SilentlyContinue
            $j = Start-Ff (Grab-Args $a $o $png)
            if (-not $j.Wait(10000)) { $j.Kill() }
            $j.Drain()
            if (-not (Test-Path $png)) { break }
            $sz = Get-ImageSize $png
            if (Test-HsSize $sz[0] $sz[1]) { $S.Adapter = $a; $S.Output = $o; Save-Settings; return $png }
        }
    }
    return $null
}
function Invoke-Detect {
    if (-not $script:FF) { return }
    $ui.Preview.Background = [System.Windows.Media.Brushes]::Black
    $ui.PreviewHint.Visibility = 'Visible'; $ui.PreviewHint.Text = 'Looking for your HydroShift…'; $ui.PreviewInfo.Text = ''
    Set-Status 'Looking for your HydroShift…'
    Flush-UI
    $png = Find-HydroShift
    if ($png) { Show-Image $png 'HydroShift'; Set-Status 'Ready' }
    else {
        $S.Adapter = -1; Save-Settings
        $ui.PreviewHint.Text = "Couldn't find a 2288 × 1048 display"
        Set-Status 'Turn on the HydroShift as a secondary screen in L-Connect 3, then click "Find display again". You can still convert videos.'
    }
}

# ---------- shared final step: loop + true black + encode ----------
# $prefix: extra filters to run first (ending with a comma), $D: input duration in seconds
function Get-Finish([string]$prefix, [double]$D) {
    $pre = $prefix
    if ($S.TrueBlack) { $pre += "format=rgb24,lutrgb=r='if(lt(val,10),0,val)':g='if(lt(val,10),0,val)':b='if(lt(val,10),0,val)'," }
    $C = [double]$S.Crossfade
    if ($S.Loop -and $D -gt (2 * $C + 1)) {
        $cs = Num $C; $off = Num ($D - 2 * $C)
        $fc = "[0:v]${pre}${ToYuv},split[a][b];[a]trim=start=${cs},setpts=PTS-STARTPTS[m];[b]trim=end=${cs},setpts=PTS-STARTPTS[h];" +
              "[m][h]xfade=transition=fade:duration=${cs}:offset=${off},format=yuv420p[v]"
        return @{ Fc = $fc; Expected = $D - $C; Label = 'Making it loop' }
    }
    return @{ Fc = "[0:v]${pre}${ToYuv}[v]"; Expected = $D; Label = 'Finishing' }
}
function Start-Final([string]$inputArgs, $f, [string]$fallbackName, [double]$outFps) {
    $script:OutFile = Get-OutPath $fallbackName
    $script:Expected = $f.Expected; $script:ProcLabel = $f.Label; $script:OutFps = $outFps
    $a = "-y -hide_banner -loglevel error -nostats -progress pipe:2 $inputArgs -filter_complex `"$($f.Fc)`" -map `"[v]`" -an $EncFinal $(Q $script:OutFile)"
    $script:Job = Start-Ff $a
    $script:State = 'processing'
    Set-RecLook 'grey'; $ui.Prog.Value = 0
}
function Finish-Processing {
    $j = $script:Job; $j.Drain()
    if ($script:Cancelled) { Remove-Item $script:OutFile -ErrorAction SilentlyContinue; Reset-Idle 'Cancelled'; return }
    if ($j.Code -ne 0 -or -not (Test-Path $script:OutFile)) { Reset-Idle ('Processing failed: ' + (Last-Line $j.Errors)); return }
    $fi = Get-Item $script:OutFile
    $ui.ResultName.Text = $fi.Name
    $ui.ResultInfo.Text = '{0:0.0} MB  ·  {1}  ·  {2:0} fps' -f ($fi.Length / 1MB), (Fmt $script:Expected), $script:OutFps
    $ui.ResultCard.Visibility = 'Visible'
    $ui.NameBox.Text = ''
    Reset-Idle 'Saved. Upload it in L-Connect 3.'
}

# ---------- recording ----------
function Start-Recording {
    Remove-Item $script:Raw -ErrorAction SilentlyContinue
    $enc = if ($S.Encoder -eq 'nvenc') { '-c:v h264_nvenc -preset p4 -rc vbr -cq 16 -b:v 0' } else { '-c:v libx264 -preset ultrafast -crf 14' }
    $a = "-y -hide_banner -loglevel error -nostats -progress pipe:2 -init_hw_device d3d11va=hs:$($S.Adapter) -filter_hw_device hs " +
         "-filter_complex `"ddagrab=output_idx=$($S.Output):framerate=$($S.Fps):draw_mouse=0,hwdownload,format=bgra,$ToYuv`" " +
         "-t $($S.Seconds) $enc $Tags $(Q $script:Raw)"
    $script:Cancelled = $false
    $script:Job = Start-Ff $a
    $script:State = 'recording'
    Set-Busy $true; Set-RecLook 'square'; $ui.RecCount.Text = ''
    Show-Pill '0:00' $Red; $ui.Prog.Visibility = 'Visible'; $ui.Prog.Value = 0
}
function Finish-Recording {
    $j = $script:Job; $j.Drain()
    if ($script:Cancelled) { Reset-Idle 'Cancelled'; return }
    $ok = (Test-Path $script:Raw) -and ((Get-Item $script:Raw).Length -gt 50KB)
    if (-not $ok) {
        if ($S.Encoder -eq 'nvenc') {
            # no NVIDIA encoder available: remember that and retry on the CPU
            $S.Encoder = 'x264'; Save-Settings
            Set-Status 'Switching to the CPU encoder and trying again…'
            Start-Recording; return
        }
        Reset-Idle ('Recording failed: ' + (Last-Line $j.Errors)); return
    }
    $ui.RecPill.Visibility = 'Collapsed'
    $info = Get-VideoInfo $script:Raw
    $D = if ($info -and $info.Duration -gt 0) { $info.Duration } else { [double]$S.Seconds }
    Start-Final "-i $(Q $script:Raw)" (Get-Finish '' $D) '' $S.Fps
}

# ---------- converting a video ----------
function Pick-Video {
    $d = New-Object Microsoft.Win32.OpenFileDialog
    $d.Title = 'Choose a video to convert for the HydroShift'
    $d.Filter = 'Videos|*.mp4;*.webm;*.mov;*.mkv;*.avi;*.wmv;*.gif|Wallpaper Engine project|project.json|All files|*.*'
    $we = Find-WorkshopDir
    if ($we) { $d.InitialDirectory = $we }
    if ($d.ShowDialog($win)) { Start-Convert $d.FileName }
}
function Start-Convert([string]$path) {
    if ($script:State -ne 'idle' -or -not $script:FF) { return }
    Commit-LenBox; Read-Controls; Save-Settings
    $title = $null; $pj = $null
    if (Test-Path $path -PathType Container) { $pj = Join-Path $path 'project.json' }
    elseif ([IO.Path]::GetFileName($path) -ieq 'project.json') { $pj = $path }
    if ($pj) {
        if (-not (Test-Path $pj)) { Set-Status "That folder isn't a Wallpaper Engine wallpaper."; return }
        try { $proj = Get-Content $pj -Raw | ConvertFrom-Json } catch { Set-Status "Couldn't read that wallpaper's project.json."; return }
        $type = "$($proj.type)".ToLower(); $title = "$($proj.title)"
        if ($type -ne 'video') {
            Set-Status "“$title” is a $type wallpaper, not a video. Set it on the HydroShift in Wallpaper Engine and press Record instead."
            return
        }
        $path = Join-Path (Split-Path $pj) "$($proj.file)"
    }
    if (-not (Test-Path $path -PathType Leaf)) { Set-Status "Couldn't find that video file."; return }
    $info = Get-VideoInfo $path
    if (-not $info) { Set-Status "ffmpeg couldn't read that file. Is it a video?"; return }

    $script:Src = $path
    $script:SrcFps = $info.Fps
    $script:ConvT = if ($info.Duration -gt 0) { [math]::Min($info.Duration, [double]$S.Seconds) } else { [double]$S.Seconds }
    $script:SrcName = if ($title) { $title } else { [IO.Path]::GetFileNameWithoutExtension($path) }
    $script:Cancelled = $false
    $ui.ResultCard.Visibility = 'Collapsed'
    Set-Busy $true; Set-RecLook 'grey'
    $ui.Prog.Visibility = 'Visible'; $ui.Prog.Value = 0
    Show-Pill 'Converting' $Purple

    $needRife = $S.Smooth -and $script:SrcFps -gt 0 -and $script:SrcFps -lt 58
    if (-not $needRife) {
        # already 60 fps (or smoothing is off): one pass straight to the final file
        $fpsFilter = if ($script:SrcFps -gt 61) { 'fps=60,' } else { '' }
        $outFps = if ($script:SrcFps -gt 61) { 60 } else { $script:SrcFps }
        Start-Final "-t $(Num $script:ConvT) -i $(Q $script:Src)" (Get-Finish "$Fit,$fpsFilter" $script:ConvT) $script:SrcName $outFps
        return
    }

    # temporary frames live next to the output folder, so "Save to" also picks the drive used
    New-Item -ItemType Directory -Force -Path $S.OutDir | Out-Null
    $script:WorkDir = Join-Path $S.OutDir '.hydroshift-temp'
    $needGB = ($script:ConvT * ($script:SrcFps + 60) * 4MB) / 1GB
    try {
        $root = [IO.Path]::GetPathRoot((Resolve-Path $S.OutDir).ProviderPath)
        $freeGB = (New-Object IO.DriveInfo $root).AvailableFreeSpace / 1GB
        if ($freeGB -lt $needGB) {
            Reset-Idle ('Smoothing this clip needs about {0:0} GB free on {1} (only {2:0} GB free). Pick another "Save to" folder or shorten Length.' -f $needGB, $root, $freeGB)
            return
        }
    } catch {}

    if (Test-Path $script:RifeExe) { Start-Prep } else { Start-RifeDownload }
}
function Start-RifeDownload {
    $script:Zip = Join-Path $env:TEMP 'rife-ncnn-vulkan.zip'
    Remove-Item $script:Zip -ErrorAction SilentlyContinue
    $script:Dl = New-Object Dl -ArgumentList $script:RifeUrl, $script:Zip
    $script:State = 'download'
}
function Extract-Rife {
    $z = [IO.Compression.ZipFile]::OpenRead($script:Zip)
    try {
        foreach ($e in $z.Entries) {
            $parts = $e.FullName -split '/', 2
            if ($parts.Count -lt 2 -or -not $parts[1] -or $parts[1].EndsWith('/')) { continue }
            $rel = $parts[1]
            if ($rel -match '^(rife-ncnn-vulkan\.exe|vcomp140\.dll|LICENSE|README\.md|rife-v4\.6/.+)$') {
                $dest = Join-Path $script:RifeDir ($rel -replace '/', '\')
                New-Item -ItemType Directory -Force -Path (Split-Path $dest) | Out-Null
                [IO.Compression.ZipFileExtensions]::ExtractToFile($e, $dest, $true)
            }
        }
    } finally { $z.Dispose() }
}
function Start-Prep {
    if (Test-Path $script:WorkDir) { Remove-Item $script:WorkDir -Recurse -Force -ErrorAction SilentlyContinue }
    $script:InDir = Join-Path $script:WorkDir 'in'; $script:OutDir2 = Join-Path $script:WorkDir 'out'
    New-Item -ItemType Directory -Force -Path $script:InDir, $script:OutDir2 | Out-Null
    try { (Get-Item $script:WorkDir -Force).Attributes += 'Hidden' } catch {}
    $vf = "$Fit,fps=$(Num $script:SrcFps)"
    $a = "-y -hide_banner -loglevel error -nostats -progress pipe:2 -t $(Num $script:ConvT) -i $(Q $script:Src) -vf `"$vf`" -an $(Q (Join-Path $script:InDir '%08d.png'))"
    $script:Job = Start-Ff $a
    $script:State = 'prep'
}
function Finish-Prep {
    $j = $script:Job; $j.Drain()
    if ($script:Cancelled) { Reset-Idle 'Cancelled'; return }
    $frames = [IO.Directory]::GetFiles($script:InDir, '*.png')
    if ($j.Code -ne 0 -or $frames.Length -lt 2) { Reset-Idle ('Preparing frames failed: ' + (Last-Line $j.Errors)); return }
    [array]::Sort($frames)
    try { Show-Image $frames[0] 'Converting' } catch {}
    $script:NOut = [int][math]::Round($frames.Length * 60 / $script:SrcFps)
    $a = "-i $(Q $script:InDir) -o $(Q $script:OutDir2) -n $($script:NOut) -m $(Q (Join-Path $script:RifeDir 'rife-v4.6')) -j 2:2:2"
    $script:Job = New-Object FfJob -ArgumentList $script:RifeExe, $a
    $script:State = 'rife'; $script:TickN = 0
}
function Finish-Rife {
    $j = $script:Job; $j.Drain()
    if ($script:Cancelled) { Reset-Idle 'Cancelled'; return }
    $n = [IO.Directory]::GetFiles($script:OutDir2, '*.png').Length
    if ($j.Code -ne 0 -or $n -lt [math]::Floor($script:NOut * 0.9)) { Reset-Idle ('Smoothing failed: ' + (Last-Line $j.Errors)); return }
    $D = $n / 60.0
    Start-Final "-framerate 60 -i $(Q (Join-Path $script:OutDir2 '%08d.png'))" (Get-Finish '' $D) $script:SrcName 60
}

# ---------- buttons / ticks ----------
function Cancel-All {
    switch ($script:State) {
        'countdown' { Reset-Idle 'Cancelled' }
        'download' { $script:Cancelled = $true; $script:Dl.Cancel = $true }
        { $_ -in 'recording', 'prep', 'rife', 'processing' } { $script:Cancelled = $true; $script:Job.Kill() }
    }
}
function On-RecClick {
    switch ($script:State) {
        'idle' {
            if (-not $script:FF) { return }
            if ($S.Adapter -lt 0) { Invoke-Detect; return }
            Commit-LenBox; Read-Controls; Save-Settings
            $ui.ResultCard.Visibility = 'Collapsed'
            if ($S.Countdown) {
                $script:CountEnd = (Get-Date).AddSeconds(3); $script:State = 'countdown'; Set-Busy $true
                $ui.RecBtn.ToolTip = 'Cancel'
            } else { Start-Recording }
        }
        'recording' { $script:Job.Quit(); Set-Status 'Finishing up…' }
        default { Cancel-All }
    }
}
function On-Tick {
    if ($script:PreviewJob -and $script:PreviewJob.Done) {
        $p = $script:PreviewPng; $script:PreviewJob = $null
        if ($script:State -eq 'idle') {
            if (Test-Path $p) { try { Show-Image $p 'HydroShift' } catch {} }
            else {
                $ui.Preview.Background = [System.Windows.Media.Brushes]::Black
                $ui.PreviewHint.Visibility = 'Visible'; $ui.PreviewHint.Text = "Can't see the HydroShift right now"
                Set-Status 'If you changed displays, click "Find display again".'
            }
        }
        Remove-Item $p -ErrorAction SilentlyContinue
    }
    switch ($script:State) {
        'countdown' {
            $left = [math]::Ceiling(($script:CountEnd - (Get-Date)).TotalSeconds)
            if ($left -le 0) { Start-Recording } else { $ui.RecCount.Text = "$left"; Set-Status 'Get ready…' }
        }
        'recording' {
            $t = $script:Job.Seconds
            $ui.Prog.Value = [math]::Min(1, $t / $S.Seconds)
            $ui.RecTime.Text = Fmt $t
            $ui.RecDot.Opacity = if ((Get-Date).Millisecond -lt 500) { 1 } else { 0.25 }
            Set-Status ('Recording  ·  {0} of {1}   ·   click ■ to stop early' -f (Fmt $t), (Fmt $S.Seconds))
            if ($script:Job.Done) { Finish-Recording }
        }
        'download' {
            $dl = $script:Dl
            if ($dl.Total -gt 0) { $ui.Prog.Value = $dl.Got / $dl.Total }
            Set-Status ('Downloading RIFE (one time only)…  {0:0} of {1:0} MB   ·   click ■ to cancel' -f ($dl.Got / 1MB), ($dl.Total / 1MB))
            if ($dl.Done) {
                if ($script:Cancelled) { Remove-Item $script:Zip -ErrorAction SilentlyContinue; Reset-Idle 'Cancelled'; return }
                if ($dl.Error) { Remove-Item $script:Zip -ErrorAction SilentlyContinue; Reset-Idle ('Download failed: ' + $dl.Error); return }
                Set-Status 'Unpacking RIFE…'; Flush-UI
                try { Extract-Rife } catch { Remove-Item $script:Zip -ErrorAction SilentlyContinue; Reset-Idle ('Unpacking failed: ' + $_.Exception.Message); return }
                Remove-Item $script:Zip -ErrorAction SilentlyContinue
                Start-Prep
            }
        }
        'prep' {
            if ($script:ConvT -gt 0) { $ui.Prog.Value = [math]::Min(1, $script:Job.Seconds / $script:ConvT) }
            Set-Status ('Preparing frames…  {0:0}%   ·   click ■ to cancel' -f ($ui.Prog.Value * 100))
            if ($script:Job.Done) { Finish-Prep }
        }
        'rife' {
            $script:TickN++
            if ($script:TickN % 5 -eq 0) {
                $n = [IO.Directory]::GetFiles($script:OutDir2, '*.png').Length
                $ui.Prog.Value = [math]::Min(1, $n / [math]::Max(1, $script:NOut))
                Set-Status ('Smoothing to 60 fps…  {0:N0} of {1:N0} frames   ·   click ■ to cancel' -f $n, $script:NOut)
            }
            if ($script:Job.Done) { Finish-Rife }
        }
        'processing' {
            if ($script:Expected -gt 0) { $ui.Prog.Value = [math]::Min(1, $script:Job.Seconds / $script:Expected) }
            Set-Status ('{0}…  {1:0}%   ·   click ■ to cancel' -f $script:ProcLabel, ($ui.Prog.Value * 100))
            if ($script:Job.Done) { Finish-Processing }
        }
    }
}

# ---------- wire up ----------
if ([int]$S.Seconds -gt $ui.LenSlider.Maximum) { $ui.LenSlider.Maximum = [int]$S.Seconds }
Set-Length ([int]$S.Seconds)
if ([int]$S.Fps -eq 30) { $ui.Fps30.IsChecked = $true } else { $ui.Fps60.IsChecked = $true }
$ui.LoopSw.IsChecked = [bool]$S.Loop
$ui.BlackSw.IsChecked = [bool]$S.TrueBlack
$ui.SmoothSw.IsChecked = [bool]$S.Smooth
$ui.CountSw.IsChecked = [bool]$S.Countdown
$ui.SaveText.Text = $S.OutDir

$ui.LenSlider.Add_ValueChanged({
    if ($script:SyncLen) { return }
    $S.Seconds = [int]([math]::Max(5, [math]::Round($ui.LenSlider.Value / 5) * 5))
    Update-LenText
})
$ui.LenSlider.Add_PreviewMouseLeftButtonUp({ Save-Settings })
$ui.LenText.Add_MouseLeftButtonUp({
    $ui.LenBox.Text = "$($S.Seconds)"
    $ui.LenText.Visibility = 'Collapsed'; $ui.LenBox.Visibility = 'Visible'
    $ui.LenBox.Focus() | Out-Null; $ui.LenBox.SelectAll()
})
$ui.LenBox.Add_KeyDown({ param($s, $e)
    if ($e.Key -eq 'Return') { Commit-LenBox; $e.Handled = $true }
    elseif ($e.Key -eq 'Escape') { $ui.LenBox.Visibility = 'Collapsed'; $ui.LenText.Visibility = 'Visible'; $e.Handled = $true }
})
$ui.LenBox.Add_LostKeyboardFocus({ Commit-LenBox })

$ui.NameBox.Add_TextChanged({ $ui.NamePh.Visibility = if ($ui.NameBox.Text) { 'Hidden' } else { 'Visible' } })
$ui.TitleBar.Add_MouseLeftButtonDown({ $win.DragMove() })
$ui.BtnClose.Add_MouseLeftButtonDown({ param($s, $e) $e.Handled = $true; $win.Close() })
$ui.BtnMin.Add_MouseLeftButtonDown({ param($s, $e) $e.Handled = $true; $win.WindowState = 'Minimized' })
$ui.RecBtn.Add_MouseLeftButtonUp({ On-RecClick })
$ui.PreviewArea.Add_MouseLeftButtonUp({ Refresh-Preview })
$ui.RedetectBtn.Add_Click({ Invoke-Detect })
$ui.ConvertBtn.Add_Click({ Pick-Video })
$ui.OpenBtn.Add_Click({ New-Item -ItemType Directory -Force -Path $S.OutDir | Out-Null; Start-Process explorer.exe (Q $S.OutDir) })
$ui.PlayBtn.Add_Click({ if ($script:OutFile -and (Test-Path $script:OutFile)) { Start-Process $script:OutFile } })
$ui.ShowBtn.Add_Click({ if ($script:OutFile -and (Test-Path $script:OutFile)) { Start-Process explorer.exe "/select,$(Q $script:OutFile)" } })
$ui.SaveRow.Add_MouseLeftButtonUp({
    $d = New-Object System.Windows.Forms.FolderBrowserDialog
    $d.Description = 'Choose where HydroShift videos are saved'
    if (Test-Path $S.OutDir) { $d.SelectedPath = $S.OutDir }
    if ($d.ShowDialog() -eq 'OK') { $S.OutDir = $d.SelectedPath; $ui.SaveText.Text = $S.OutDir; Save-Settings }
})
foreach ($c in $ui.LoopSw, $ui.BlackSw, $ui.SmoothSw, $ui.CountSw, $ui.Fps30, $ui.Fps60) { $c.Add_Click({ Read-Controls; Save-Settings }) }

# drop a video, a Wallpaper Engine wallpaper folder, or its project.json onto the window
$win.Add_Drop({ param($s, $e)
    $files = $e.Data.GetData([System.Windows.DataFormats]::FileDrop)
    if ($files) { Start-Convert $files[0] }
})
$win.Add_KeyDown({ param($s, $e)
    if ($e.Key -eq 'Escape') { Cancel-All }
    elseif ($e.Key -eq 'F5') { Refresh-Preview }
})
$win.Add_Activated({ Refresh-Preview })
$win.Add_ContentRendered({
    if (-not $script:FF) {
        $ui.PreviewHint.Text = 'ffmpeg is not installed'
        Set-Status 'Open PowerShell and run:  winget install Gyan.FFmpeg   then reopen this app.'
        $ui.RecBtn.Opacity = 0.35; $ui.RecBtn.IsEnabled = $false; $ui.ConvertBtn.IsEnabled = $false
    } elseif ($S.Adapter -ge 0) { Refresh-Preview; Set-Status 'Ready' }
    else { Invoke-Detect }
})
$win.Add_Closing({
    $timer.Stop()
    if ($script:Job) { $script:Job.Kill(); $script:Job.Drain() }
    if ($script:PreviewJob) { $script:PreviewJob.Kill() }
    if ($script:Dl) { $script:Dl.Cancel = $true }
    if ($script:State -eq 'processing' -and $script:OutFile) { Remove-Item $script:OutFile -ErrorAction SilentlyContinue }
    Remove-Work
    Read-Controls; Save-Settings
})

$timer = New-Object System.Windows.Threading.DispatcherTimer
$timer.Interval = [TimeSpan]::FromMilliseconds(100)
$timer.Add_Tick({ On-Tick })
$timer.Start()

[void]$win.ShowDialog()
