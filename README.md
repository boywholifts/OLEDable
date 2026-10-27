# OLEDable

> **Work in progress.** This is a personal hobby project, shared as-is. Features, settings and file formats may change between versions, and there's no support schedule. Bug reports are welcome, but replies and fixes happen when they happen.

A Windows app that turns any wallpaper, video or still, into a **true-black OLED version**. The grey "haze" in dark areas becomes pure black, so OLED pixels switch fully off: deeper contrast, less glow, less burn-in risk. Wallpaper Engine wallpapers can be imported in a click, and the result can keep its original size, be upscaled, or fit any screen you choose.

It started as **HydroShift Recorder**, a tool for the Lian Li HydroShift II OLED Curved AIO display, and it still makes seamless-looping wallpapers for that screen (see [below](#for-the-lian-li-hydroshift)).

## What it does

- **True black:** a live before/after preview pushes the haze to pure black. The black level is detected for each video or image, and you can fine-tune shadows, color, brightness, highlights and glow.
- **Any size:** keep the original size, upscale 2× or 4× keeping the shape, or fill a size you choose (4K, 1440p, ultrawide, a phone or foldable screen…).
- **Videos and still images:** MP4, MKV, WebM… and PNG, JPEG, WebP, BMP, TIFF. Images are saved as PNG.
- **Wallpaper Engine:** see every wallpaper you've downloaded from the Workshop.
  - Video wallpapers are copied into your library in one click.
  - Scenes can be unpacked (with [RePKG](https://github.com/notscuffed/repkg)) for their background image or, when a scene plays a video, that video; or recorded as a video: Wallpaper Engine plays the scene in a window at the size you pick, and the app records that window.
  - Wallpaper Engine's own files are never changed.
- **Record a screen:** record any of your monitors with a live preview.
- **AI enhance:** Real-ESRGAN redraws small or soft wallpapers at a higher resolution for a sharper picture. You can choose the style and the strength, and add community models.
- **Smooth to 60 fps:** videos with fewer frames get real in-between frames from RIFE, running on your GPU.
- **Seamless loop:** the end crossfades into the start.
- **Library:** browse your wallpapers, see which are already converted, and click one to edit it.
- **Updates in the app:** a Stable channel, or Dev for test builds.
- Light, dark and true-black themes.

## For the Lian Li HydroShift

Pick the **HydroShift** output size and the app makes a 2288 × 1048 video, cropped to fill the panel and kept under L-Connect's 30 MB upload limit. Upload it as a custom theme in L-Connect 3 and the screen plays it on its own, without secondary-screen mode.

*Not affiliated with or endorsed by Lian Li. "HydroShift" and "L-Connect" are Lian Li's names for their products. Not affiliated with Wallpaper Engine either.*

## Install

Download `OLEDable-Setup-x.y.z.exe` from [Releases](../../releases) and run it.

- **Express** puts your library in `Videos\OLEDable`, uses ffmpeg, RIFE and Real-ESRGAN if they're already on your PC, and downloads ffmpeg and Real-ESRGAN if not (RIFE downloads the first time a video needs it).
- **Advanced** lets you choose each of these.

It installs for your user only (no admin prompt) and can be removed from Windows' *Apps* settings. The uninstaller never touches your library folder.

Already have HydroShift Recorder? Update as usual (in the app, or by running the new setup). It becomes OLEDable and keeps your settings, library and tools.

**Needs:** Windows 10 or 11 and a GPU with Vulkan support for RIFE and AI enhance (any recent NVIDIA, AMD or Intel GPU). ffmpeg 8 or newer is recommended for recording Wallpaper Engine scenes.

## Build it yourself

GitHub Actions builds the app and the installer on every push to `main` and publishes it as a Dev pre-release.

Locally (needs the .NET 8 SDK; the app itself targets .NET Framework 4.8, which is built into Windows):

```
build.cmd             # app → dist\OLEDable.exe
build-installer.cmd   # installer → dist-installer\ (needs Inno Setup 6.6+)
```

## Privacy

The app has no telemetry, no accounts and no analytics. It only goes online to check this repository's Releases for updates (you can turn that off in Settings) and to download the tools it uses, from their official release pages, when you choose to (in the installer, or the first time a feature needs one):

- ffmpeg: [gyan.dev](https://www.gyan.dev/ffmpeg/builds/)
- RIFE (about 430 MB, downloaded the first time a video needs smoothing): [github.com/nihui/rife-ncnn-vulkan](https://github.com/nihui/rife-ncnn-vulkan/releases)
- Real-ESRGAN: [github.com/xinntao/Real-ESRGAN](https://github.com/xinntao/Real-ESRGAN/releases)
- RePKG (the first time you unpack a scene): [github.com/notscuffed/repkg](https://github.com/notscuffed/repkg/releases)

Settings and edits stay on your PC: next to the app, and in a hidden `.oledable` folder inside your library.

## Code signing policy

Builds are signed with [Azure Artifact Signing](https://learn.microsoft.com/azure/artifact-signing/) (publisher: **Jesse Sievers**). Only builds made by this repository's GitHub Actions from its `main` branch are signed, and stable releases are published by hand by the maintainer.

- Author, reviewer and approver: [@boywholifts](https://github.com/boywholifts)

## Credits

- [RIFE / rife-ncnn-vulkan](https://github.com/nihui/rife-ncnn-vulkan) (MIT): frame interpolation
- [Real-ESRGAN / realesrgan-ncnn-vulkan](https://github.com/xinntao/Real-ESRGAN) (BSD-3-Clause): AI upscaling
- [RePKG](https://github.com/notscuffed/repkg) (MIT): unpacking Wallpaper Engine scenes, downloaded on first use
- [FFmpeg](https://ffmpeg.org) (LGPL/GPL): video work, downloaded separately and not bundled
- [Inno Setup](https://jrsoftware.org/isinfo.php): installer

## License

[MIT](LICENSE)
