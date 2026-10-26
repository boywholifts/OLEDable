# OLEDable (formerly HydroShift Recorder) – to do

## Done in 2.1 (needs a build and a test on the PC)
- Library thumbnails are PNG at 640×293 with accurate RGB conversion, which should fix the banding in the blacks. The old cached JPGs are simply ignored.
- "Grey background" badge removed.
- "Smooth to 60 fps" is greyed out for videos that are already 60 fps. Recordings keep it, because cadence detection still decides for them.
- Edit in progress:
  - A sidebar "Editing: <name>" item takes you back to the editor exactly as you left it. Its × closes the edit.
  - Changes are autosaved (1.5 s debounce, and on leaving the editor or closing the app) for videos that already have a stored session.
  - Opening another video asks first if there are unexported changes.
  - Opening the video that's already in the editor just returns to it.
- Record page live view: one long-running ffmpeg stream at 30 fps (GPU scaling via scale_d3d11 when available, CPU scaling otherwise), replacing the screenshot every 2 s.
- Converted folder:
  - Exports go to `<Library>\Converted`; the location can be changed in Settings.
  - The library lists both folders.
  - The first time, the app offers to move existing exports there.
  - Re-exporting a video updates it wherever it is.
- AI enhance (Real-ESRGAN animevideov3, ncnn/Vulkan):
  - "DETAIL > AI enhance" switch in the editor, on automatically for sources smaller than the panel.
  - The preview shows the enhanced frame.
  - Export: crop to the panel's shape → ESRGAN 2/3/4× → (fit → RIFE if smoothing) → OLED → encode.
  - Downloads about 50 MB the first time.
- Installer (Inno Setup):
  - `installer\OLEDable.iss`, built with `build-installer.cmd` (needs `winget install JRSoftware.InnoSetup`) or by GitHub Actions.
  - Pages: library folder, converted subfolder name, ffmpeg (found / download / browse), RIFE (download now / later / existing folder), Real-ESRGAN (now / later).
  - Writes the settings JSON before first launch. Per-user install, no admin prompt.

## Done in 2.2 (needs a build and a test on the PC)
- Library chips:
  - Converted = everything the app exported (recordings included).
  - Recordings = exports that started as a recording.
  - Originals (replaces "Not tuned yet") = source videos.
- Editor values you can type:
  - Click a slider's number, or double-click the slider, to type a value. Enter applies, Esc cancels, the arrow keys nudge (Shift ×10).
  - The mouse wheel over a slider nudges it.
  - Trim times can be typed too (click the "0:00 – 0:23" text).
- Progress and estimates:
  - Every step shows "· 42% · about 1 min 10 s left (3 min for the whole export)".
  - Step speeds are learned per PC and saved in the settings (`Rates`).
  - The editor shows "Takes about …" under the name.
  - Progress on the taskbar button.
  - The AI preview shows a progress badge, and the other filmstrip frames are enhanced in the background.
- AI enhance controls:
  - Style chips: Animated, Realistic, plus any community model in `realesrgan\models`.
  - Strength slider (blends with a plain upscale; instant in the preview).
  - Clean up first.
  - More options: Quality mode (TTA), Sharpen a little, Open models folder.
  - Remembered as defaults for new videos.
  - Realistic downloads its model if it's missing.
- Installer 2.2 (needs Inno Setup 6.6+):
  - Dark mode follows Windows (`WizardStyle=modern dynamic`).
  - Express (default) or Advanced.
  - The library page is one line, and the Converted page is gone (always `<Library>\Converted`).
  - Tools are found automatically: if found, they're used; Advanced offers to pick a different one.
- Themed pop-ups (Dialog.cs) replace the white Windows message boxes.
- The workflow reads the version from the csproj (now 2.2.0).

## Done after 2.2 (needs a build and a test)
- Filmstrip clicks select the frame (handled on the strip itself). Tested and working.
- Realistic and other 4× models: the input is shrunk so the output is 2× the panel, which is about 4× faster. Estimates now count megapixels.
- Model download only adds missing files (no "file in use" error).
- Public repo prep: README (work-in-progress note, not-affiliated line, privacy, signing policy), MIT LICENSE; removed snapshot test scripts.
- In-app updates from GitHub Releases:
  - Stable/Dev channel in Settings, plus automatic check (at most every 3 h), Check now, and the current version.
  - A sidebar card with Update now, What's new, and skip (×).
  - Update now downloads the setup, runs it silently, and the installer reopens the app.
  - Local `dist` builds never prompt by themselves.
  - Workflow: every push to main publishes a dev pre-release `vX.Y.Z-dev.N`; a `vX.Y.Z` tag publishes a stable release.

## 2.3 (in development)
- Output sizes: HydroShift (2288 × 1048), Original size, Upscale 2×/4× (same shape), Custom (presets: 4K, 1440p, 1080p, ultrawide, Fold 8 Ultra inner/cover, phone; or typed).
  - Fit/crop, the glow (scales with output size), AI enhance sizing and the encode follow the output. Only the HydroShift output keeps the 30 MB cap.
- Still images (PNG, JPEG, WebP, BMP, TIFF): open, tune with the live preview, AI enhance, export as PNG. Images default to Original size.
  - Library lists images; exported PNGs reopen their session by file name (PNG can't carry the app's tag).
- The editor preview takes the output's shape; images hide the filmstrip, trim, motion and noise cleanup.
- Installer: RIFE (about 430 MB) downloads on first use by default.
- Wallpaper Engine page (sidebar): every Workshop wallpaper and your own projects, with previews, type badges, search and filters.
  - Video: copy into `<Library>\Wallpaper Engine` and open in the editor.
  - Scene: RePKG (downloaded on first use) unpacks it; "Extract still image" takes the largest picture, "Extract video" a video layer if there is one.
  - Done: "Record animation…" opens a window (size, length, frame rate); play the scene in a Wallpaper Engine pop-out window (`wallpaper64.exe -control openWallpaper -file … -playInWindow … -width -height -borderless`) and capture that window.

- Record moved out of the sidebar: "Record screen" on the Library page records any monitor.
- Renamed to **OLEDable**: exe `OLEDable.exe`, settings `OLEDable.json` (copied from `HydroShift-Recorder.json` on first launch), installer `installer\OLEDable.iss` (same AppId, so it updates old installs in place and removes the old exe and shortcuts), default library `Videos\OLEDable` for new installs. Updater points at `boywholifts/oledable` (rename the GitHub repo). Internal names (namespace, `.hydroshift` folder, `hsr|` tag) are unchanged.

## Release routine
1. Push changes; the Dev channel gets `X.Y.Z-dev.N`. Test it.
2. When it's good: GitHub → Actions → Build EXE → **Run workflow** → tick "Publish as a stable release". It uses the csproj `<Version>`, creates tag `vX.Y.Z` and the release; everyone on Stable is offered it.
3. Bump `<Version>` in the csproj to the next version, so later dev builds count as newer than the release.
- v2.2.0 released 2026-10-23 (first stable); csproj now 2.3.0.

## Still open
- Library tiles: remove the "· True black" tag (LibraryPage.xaml, the AccentText line under the name). Converted items are already marked "Converted", so it adds nothing.
- Make a recording with the HydroShift in second screen mode (end-to-end).
- Benchmark AI enhance speed on the 5090 and tune `-j` threads if needed.
- Code signing: on hold. Options are Azure Trusted Signing (~$10/mo) or SignPath Foundation (free for open source); meanwhile, report false positives to Malwarebytes.
- Push to GitHub, including `.github/workflows/main.yml` (it now builds the installer too). That file can't be written to the PC folder from here.
