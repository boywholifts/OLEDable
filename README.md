# HydroShift Recorder

A small Windows app that makes seamless-looping videos for the **Lian Li HydroShift II OLED Curved** AIO display (2288 × 1048, 60 Hz), so its custom theme can play on its own instead of relying on the secondary-screen mode.

- **Record** whatever is playing on the HydroShift, such as a Wallpaper Engine scene, straight from the display.
- **Convert** a video or a Wallpaper Engine video wallpaper: it's cropped to 2288 × 1048 and, if it's below 60 fps, smoothed to 60 fps with RIFE AI frame interpolation on your GPU.
- **Seamless loop**: the end crossfades into the start.
- **True black**: near-black pixels are pushed to pure black so OLED pixels switch fully off.

## Requirements

- Windows 10/11
- ffmpeg: `winget install Gyan.FFmpeg`
- The HydroShift enabled as a secondary screen in L-Connect 3 (needed for recording only)

## Run it

**Exe:** download `HydroShiftRecorder.exe` from [Releases](../../releases) and run it. Settings and the RIFE download are stored next to the exe.

**Script:** `powershell -ExecutionPolicy Bypass -File .\HydroShift-Recorder.ps1`
To add a Desktop/Start menu shortcut, append `-Install`.

## Build

GitHub Actions builds the exe with [PS2EXE](https://github.com/MScholtes/PS2EXE) on every push to `main`. You can download it from the run's **Artifacts**. Pushing a tag like `v1.0.0` publishes a Release with the exe attached.

## Credits

- [RIFE / rife-ncnn-vulkan](https://github.com/nihui/rife-ncnn-vulkan) (MIT), downloaded on first use
- [FFmpeg](https://ffmpeg.org)
