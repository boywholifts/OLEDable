# HydroShift Recorder

A small Windows app that makes seamless-looping videos for the **Lian Li HydroShift II OLED Curved** AIO display (2288 × 1048, 60 Hz). You upload the result as a custom theme in L-Connect 3, so the screen can play it on its own instead of relying on the secondary-screen mode.

- **Record** whatever is playing on the HydroShift, such as a Wallpaper Engine scene, straight from the display.
- **Convert** a video or a Wallpaper Engine video wallpaper (drag in the file, its folder or its `project.json`). It's cropped to 2288 × 1048 and, if it's below 60 fps, smoothed to 60 fps with RIFE AI frame interpolation on your GPU.
- **Seamless loop**: the end crossfades into the start.
- **True black**: near-black pixels are pushed to pure black so OLED pixels switch fully off.

## Requirements

- Windows 10/11 (uses the built-in .NET Framework 4.8)
- ffmpeg: `winget install Gyan.FFmpeg`
- The HydroShift enabled as a secondary screen in L-Connect 3 (needed for recording only)

## Use

Download `HydroShiftRecorder.exe` from [Releases](../../releases) and put it in its own folder. Settings and the one-time RIFE download are stored next to it, or in `%LOCALAPPDATA%\HydroShift Recorder` if that folder isn't writable.

## Build

GitHub Actions builds the exe on every push to `main`; download it from the run's **Artifacts**. Pushing a tag like `v1.0.0` publishes a Release with the exe attached.

To build locally: `dotnet build HydroShiftRecorder.csproj -c Release`

## Credits

- [RIFE / rife-ncnn-vulkan](https://github.com/nihui/rife-ncnn-vulkan) (MIT), downloaded on first use
- [FFmpeg](https://ffmpeg.org)
