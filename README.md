# Ryujinx Stereo — Odyssey experimental build

Standalone Windows x64 emulator with same-state geometric stereoscopic rendering and left/right side-by-side output. Tested with Super Mario Odyssey v1.0.0 on an NVIDIA RTX 5070.

**[Download the standalone Windows build](https://github.com/AlonBorodAHub/Ryujinx-Stereo/releases/tag/v0.1.0-experimental)**

Extract the whole ZIP and run **Ryujinx Stereo.exe**. Configure your own controller and provide your own game, console keys and firmware. Depth and Vk3DVision are not required.

The renderer includes corrections for Odyssey character shading, ground shadows, fog/waterfall silhouettes and screen-depth HUD, plus layered framebuffers and eligible single-draw stereo. This is geometric stereo from one game state, not depth-map reconstruction.

Experimental: other games and AMD/Intel GPUs are unverified, first-use shader compilation can hitch, and stable 60 fps throughout the game is not established.

Upstream emulator code is MIT licensed. This unofficial adaptation is based on [Leuconoe/Ryubing](https://github.com/Leuconoe/Ryubing), commit 80ab0f96b95dc13f5bdb9f365a3363d25040e236. See LICENSE.txt and the dependency notices inside the release archive. Original upstream workflows are preserved with .disabled extensions; no inherited publishing workflows are active.

## Complete setup and usage

Ryujinx Stereo — Odyssey experimental build 0.1.0 (Windows x64)

Start here
1. Extract the ENTIRE ZIP to a writable folder. Do not run inside the ZIP.
2. Open "Ryujinx Stereo.exe". This launcher enables the tested stereo renderer.
3. Supply your own console keys, firmware and game. None are included here.
   Use File > Open Ryujinx Folder to find this package's portable data folder.
   Put your own prod.keys in its system folder. Install your own firmware
   through the emulator's firmware installation menu.
4. Open Settings > Input and configure Player 1 for your own controller.
   The package initially uses the standard keyboard profile, so it cannot
   accidentally select someone else's disconnected controller.
5. Add your game directory or open your game through the File menu.
6. Put your 3D display or viewing software into left/right side-by-side mode.
   The emulator supplies the SBS image; your display software handles viewing.

Depth, Vk3DVision and a separate .NET installation are not required.
Use Windows 10 20H1 or later, x64, with a modern OpenGL driver. The bundled
native libraries use Windows system dependencies. This build was tested
on Windows 11 with an RTX 5070.
The archive is unsigned. No driver installer or Windows repair tool is included.

Launching
Always use "Ryujinx Stereo.exe". The executable inside emulator is the
underlying application and does not enable the stereo environment itself.
You can also drag a game file onto the launcher, or pass a quoted game path:
  "Ryujinx Stereo.exe" --fullscreen "D:\Your Games\Your Game.nsp"
Only run one emulator instance at a time. Configure controllers before play.
F11 toggles fullscreen; Alt+F4 closes the emulator. Normal input and graphics
settings remain available in the emulator UI. Do not switch the backend to
Vulkan: this stereo implementation is in the OpenGL backend.

What this build does
Renders both eyes from the same emulated game state, with geometric stereo,
then presents left/right SBS. It is not depth-map reconstruction or a
conversion of consecutive guest frames into two eyes. The tested Odyssey
shader corrections cover character shading, ground shadows, fog/waterfall
silhouettes and the screen-depth HUD rule. Other games are untested.

Tested defaults
- OpenGL; 1.5x internal resolution; no additional anti-aliasing.
- Stereo separation 0.02028; convergence 600.
- Alternating eye order, layered framebuffers, eye-layer texture sampling
  and eligible single-draw stereo enabled.
- Both shadows and effect corrections enabled; diagnostics disabled.
- Shader and translation caches enabled, initially EMPTY.
- Automatic upstream updates disabled so they do not replace this build.

Stereo controls
emulator\StereoSettings.json stores the stereo settings. Separation and
convergence can be changed while running; they reload after about 120
presentations. runtime_resolution_scale supports 1.0 to 2.0. The graphics
settings page must retain OpenGL. Lower separation if the depth is excessive.
If instanced-eye rendering causes a new visual problem, set instanced_eyes
to false. This keeps geometric stereo but uses the two-draw fallback.
For a driver-specific array-sampling problem, close the emulator and launch
with the optional command-line switch --safe-stereo. That uses the tested
two-draw layered renderer without eye-array sampling and a separate cache.

Limitations
- Experimental, unofficial adaptation, tested with Super Mario Odyssey v1.0.0
  and an NVIDIA RTX 5070. AMD/Intel graphics and other games are unverified.
- Correct stereo in every game or shader is not guaranteed.
- Fresh shader/translation caches can cause long first loads and first-use
  hitches. No game-derived shader cache is distributed. Keep your own cache.
- The tested scene averaged about 60 fps in both the baseline and optimized
  paths; measured pacing improved. Stable 60 fps throughout the game is NOT
  established. Results on other CPUs and GPUs will differ.
- No game, firmware, encryption keys, saves, personal profiles, recording,
  controller pairing tools or Depth assets are included.
- The package does not change Bluetooth pairings or USB/Windows drivers.
- Closing the emulator normally lets it finish writing settings and caches.

Keyboard fallback (upstream defaults)
Left stick: W/A/S/D. Right stick: I/J/K/L. D-pad: arrow keys.
Controller A/B/X/Y: Z/X/C/V. Plus/Minus: +/-. L/ZL: E/Q. R/ZR: U/O.
Stick clicks: F/H. Select and configure a physical controller in Settings.

Sharing and source
Share the original ZIP, with its source and notices. Share no portable
profile that you have used: it can contain your own keys, firmware and saves.
The package includes the full modified emulator source, a baseline patch,
launcher source, build instructions and dependency notices in source/ and
licenses/. Third-party DLLs remain replaceable. Upstream emulator code is
MIT licensed; individual dependencies retain their own licenses.
This is an unofficial build based on Leuconoe/Ryubing commit
80ab0f96b95dc13f5bdb9f365a3363d25040e236, not an official Ryujinx release.

Bug reports
Include the game/update version, GPU and driver, stereo settings, a description
of which eye/effect fails, and a trimmed emulator log after checking it for
personal information. Logs are in emulator\portable\Logs after you run it.
Never attach keys, firmware or game files.

