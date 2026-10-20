# Ryujinx Stereo

Experimental Windows x64 emulator with geometric stereo for **Super Mario Odyssey**, **The Legend of Zelda: Breath of the Wild**, and **Super Mario Bros. Wonder**. Both eyes render the same emulated game state.

**[Download 0.3.0 experimental](https://github.com/AlonBorodAHub/Ryujinx-Stereo/releases/tag/v0.3.0-experimental)** · [Changes](CHANGELOG.md) · [Build instructions](release-tools/BUILDING.txt)

## Setup

1. Extract the entire ZIP to a writable folder and run **Ryujinx Stereo.exe**. It opens the normal game library in a window with menus visible.
2. Supply your own games, console keys and firmware. Configure your controller in Options > Settings > Input and add your games folder. Portable data starts with a clean keyboard configuration.
3. Double-click Odyssey, Zelda or Wonder. The library selects that game's renderer and stereo profile. Close gameplay to return to the library.
4. Open **Stereo 3D > Depth & convergence** or press **F6** to adjust depth and convergence. Settings are stored in `profiles/<title-id>.json` and reload during play. F11 toggles fullscreen.

Stereo requires OpenGL. Defaults include 1.5× internal rendering, FXAA and a hidden console. Configure physical controllers locally; the tested controller stopped drifting at left/right stick deadzones of 0.32/0.20.

## Display output

The supplied profiles enable direct LeiaSR panel output, tested on a Samsung Odyssey 3D G90XF. Install the display's supported LeiaSR runtime and make its runtime DLL directory available on PATH before launch. Our bridge DLL and source are included; the proprietary LeiaSR SDK/runtime are not bundled.

Stop Samsung's separate video conversion during direct panel output to avoid converting twice. dEPTH is optional; its integration is a separate project and is not included here.

For another SBS viewer, set `direct_panel_output` to `false` in the game's profile before launch and configure your viewer for left/right SBS. Other display models, AMD/Intel GPUs and other games are unverified.

## Record gameplay

- **F9:** start recording the completed stereo image before native panel weaving.
- **F10:** finish the desktop MP4, close gameplay and show the saved file in Explorer.

The Stereo 3D menu offers the same commands. Unique filenames are `<game> SBS 4K60 <date-time>.mp4`. Video is **3840×2160 H.264, constant 60 fps**, with AAC system-output audio (including other audible applications); no microphone is recorded. NVIDIA NVENC requires a compatible NVIDIA GPU.

Provide FFmpeg with `h264_nvenc` support as `tools/ffmpeg.exe` beside the emulator, on PATH, or through the existing Jellyfin installation at `Program Files/Jellyfin/Server/ffmpeg.exe`. The FFmpeg executable is not bundled; emulator decoder DLLs are separate dependencies.

Windowed capture scales to the file dimensions without adding source detail. Frames repeat if gameplay runs below 60 fps. The monitor keeps native 3D while files contain clean SBS views. Failures keep gameplay open and retain diagnostics under `%TEMP%/ryujinx-sbs-record-<id>`.

## Validation and limits

The user confirmed stereo, shadows, fog/smoke, colors and smoother native presentation in both games on an RTX 5070, and confirmed F10 closed Zelda and showed an MP4. Separate GPU tests verified decoded eye order/orientation, 4K60 H.264, audible AAC, audio/video duration and graphics-state preservation. See [VALIDATION.txt](VALIDATION.txt).

Stable 60 fps throughout either game is not established. First-use shader compilation can hitch. Zelda's game-specific 60 fps mod is not distributed here; configure your own compatible game/mod setup. No games, firmware, keys, saves, caches, personal controller profiles, recordings or Samsung/dEPTH assets are included.

## Source and attribution

`src/` contains the library/Mario engine; `zelda-src/` contains the separate Zelda engine. Both link `stereo-ui/` and `stereo-recording/`. `native/LeiaBridge/` contains our C++ bridge. Separate renderers preserve each game's validated shader fixes behind one library.

This unofficial adaptation is based on [Leuconoe/Ryubing](https://github.com/Leuconoe/Ryubing), commit `80ab0f96b95dc13f5bdb9f365a3363d25040e236`. Emulator source is MIT licensed; dependency licenses remain separate. See LICENSE.txt, distribution/legal/THIRDPARTY.md and release notices. The legacy `native-stereo.patch` records 0.1.0; current source and Git history are authoritative for 0.2.0. Upstream publishing workflows remain disabled.


## New in 0.3.0

Super Mario Bros. Wonder joins Odyssey and Breath of the Wild. Wonder 1.0.1
has game-specific orthographic stereo corrections for terrain, characters,
coins, music blocks, effects, dialogue and UI. UI is placed at the gameplay
plane; a small symmetric edge crop conceals the exposed stereo margin.
Wonder uses separate-eye rendering because the paired-eye optimization was
unstable in level transitions. Its profile starts at 14% depth and 625
convergence. The overworld has no separate depth adjustment.

In-game controls (game window focused):

| Action | Keyboard | Controller |
| --- | --- | --- |
| Decrease/increase convergence | Ctrl+Left / Ctrl+Right | Both bumpers + D-pad Left / Right |
| Increase/decrease depth | Ctrl+Up / Ctrl+Down | Both bumpers + D-pad Up / Down |
| Reset depth and convergence | Ctrl+R | Both bumpers + B |

Use F6 > Keyboard shortcuts to change these keyboard bindings. Adjustments
briefly show both values at the top of the screen. The in-game reset uses the
profile's saved default_separation and default_convergence values. Included
Odyssey defaults are 100% / 1775; Zelda defaults are 70% / 12. Settings remain
per game. Back up existing profiles before replacing them during an upgrade.

F9/F10 recording also works through Wonder's shared recorder: F9 starts a
3840x2160 SBS MP4 with system-output audio; F10 finalizes it on the desktop,
then closes gameplay and shows the file. FFmpeg/NVENC requirements above
still apply. Other applications' system audio can also be recorded.

Wonder's shader-analysis cache reduces compilation overhead. Encountering a
new shader can still cause a hitch, and a changed shader-cache version needs
one warm-up run. This remains experimental: tested scenes look correct, but
all levels, cameras, effects and hardware have not been validated. Temporary
Wonder frame-capture probes are excluded from this release.
