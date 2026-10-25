# HydroShift Recorder

> **Work in progress.** This is a personal hobby project, shared as-is. Features, settings and file formats may change between versions, and there's no support schedule. Bug reports are welcome, but replies and fixes happen when they happen.

A Windows app that makes seamless-looping, true-black videos for the **Lian Li HydroShift II OLED Curved** AIO display (2288 × 1048). You upload the result as a custom theme in L-Connect 3, so the screen plays it on its own instead of relying on secondary-screen mode.

*Not affiliated with or endorsed by Lian Li. "HydroShift" and "L-Connect" are Lian Li's names for their products.*

## What it does

- **Library:** browse the videos in a folder, see which are already converted, and click one to edit it.
- **Record a screen:** for anything that isn't a Wallpaper Engine wallpaper, record one of your screens (the HydroShift or any monitor) with a live preview.
- **Convert** any video or Wallpaper Engine video wallpaper. It's cropped to 2288 × 1048 to fit the panel.
- **Wallpaper Engine browser:** see every wallpaper you've downloaded from the Workshop. Video wallpapers are copied into your library in one click; scenes can be unpacked (with [RePKG](https://github.com/notscuffed/repkg)) for their background image or a video inside them. Wallpaper Engine's own files are never changed.
- **Any wallpaper, not just the HydroShift:** videos and still images (PNG, JPEG, WebP…) can keep their original size, be upscaled 2× or 4× keeping their shape, or fill a size you choose (4K, a phone screen…). Images are saved as PNG.
- **True black:** a live before/after preview pushes the grey "haze" to pure black so the OLED pixels switch fully off. The black level is detected for each video, and you can fine-tune shadows, color, brightness, highlights and glow.
- **Smooth to 60 fps:** videos or recordings with fewer frames get real in-between frames from RIFE, running on your GPU.
- **AI enhance:** Real-ESRGAN redraws small or soft videos at a higher resolution for a sharper picture. You can choose the style and the strength, and add community models.
- **Seamless loop:** the end crossfades into the start.
- Light, dark and true-black themes.

## Install

Download `HydroShiftRecorder-Setup-x.y.z.exe` from [Releases](../../releases) and run it.

- **Express** puts your library in `Videos\HydroShift`, uses ffmpeg, RIFE and Real-ESRGAN if they're already on your PC, and downloads them if not.
- **Advanced** lets you choose each of these.

It installs for your user only (no admin prompt) and can be removed from Windows' *Apps* settings. The uninstaller never touches your library folder.

**Needs:** Windows 10 or 11 and a GPU with Vulkan support for RIFE and AI enhance (any recent NVIDIA, AMD or Intel GPU). Recording Wallpaper Engine scenes uses its pop-out window, so the HydroShift doesn't need secondary screen mode; ffmpeg 8 or newer is recommended for that.

## Build it yourself

GitHub Actions builds the app and the installer on every push to `main`; you can download them from the run's **Artifacts**. Pushing a tag like `v2.2.0` publishes a Release.

Locally (needs the .NET 8 SDK; the app itself targets .NET Framework 4.8, which is built into Windows):

```
build.cmd             # app → dist\HydroShiftRecorder.exe
build-installer.cmd   # installer → dist-installer\ (needs Inno Setup 6.6+)
```

## Privacy

The app has no telemetry, no accounts and no analytics. It only goes online to download the tools it uses, from their official release pages, when you choose to (in the installer, or the first time a feature needs one):

- ffmpeg: [gyan.dev](https://www.gyan.dev/ffmpeg/builds/)
- RIFE (about 430 MB, downloaded the first time a video needs smoothing): [github.com/nihui/rife-ncnn-vulkan](https://github.com/nihui/rife-ncnn-vulkan/releases)
- Real-ESRGAN: [github.com/xinntao/Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN/releases)

Settings and edits stay on your PC: next to the app, and in a hidden `.hydroshift` folder inside your library.

## Code signing policy

*Planned:* release builds are to be signed through the [SignPath Foundation](https://signpath.org) (free code signing for open-source projects; code signing certificate by SignPath Foundation). Only builds made by this repository's GitHub Actions from its source are signed, and every release is approved by hand by the maintainer.

- Author, reviewer and approver: [@boywholifts](https://github.com/boywholifts)

## Credits

- [RIFE / rife-ncnn-vulkan](https://github.com/nihui/rife-ncnn-vulkan) (MIT): frame interpolation
- [Real-ESRGAN / realesrgan-ncnn-vulkan](https://github.com/xinntao/Real-ESRGAN) (BSD-3-Clause): AI upscaling
- [RePKG](https://github.com/notscuffed/repkg) (MIT): unpacking Wallpaper Engine scenes, downloaded on first use
- [FFmpeg](https://ffmpeg.org) (LGPL/GPL): video work, downloaded separately and not bundled
- [Inno Setup](https://jrsoftware.org/isinfo.php): installer

## License

[MIT](LICENSE)
