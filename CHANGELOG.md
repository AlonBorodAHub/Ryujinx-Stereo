# 0.3.0-experimental � 2026-10-20

- Add Wonder 1.0.1 stereo camera corrections, terrain/effect alignment, gameplay-plane text/UI, and stereo-edge cropping.
- Use stable separate-eye rendering for Wonder and version its shader cache.
- Cache shader dependency analysis to reduce shader compilation work.
- Add controller and customizable keyboard depth/convergence adjustments, reset-to-profile defaults, and a transient value overlay.
- Include updated Odyssey, Zelda and Wonder defaults; retain 4K60 SBS recording for all three titles.
- Exclude temporary Wonder draw-capture probes from public source and binaries.
- Overworld-specific depth and previously attempted Odyssey dialogue/effect changes are not included.

# Changes

## 0.2.0 experimental — 2026-10-19

- One windowed game library with per-game renderers for Super Mario Odyssey and Breath of the Wild.
- Stereo 3D menu and F6 controls for depth and convergence, saved separately for each game.
- Direct LeiaSR panel output, avoiding the extra Samsung window-conversion path on the tested display.
- Zelda shader corrections for ground shadows, menu character lighting, fog and smoke, with renderer and transfer optimizations.
- F9 records pre-weave left/right SBS at 3840×2160 and constant 60 fps, with system audio. F10 finishes the desktop MP4, then closes gameplay and selects the file in Explorer.
- Fullscreen foreground-only recording hotkeys; recording failures leave gameplay open.
- Clean release defaults include FXAA, hidden console and 1.5× internal rendering. Configure physical controllers locally; the tested controller needed left/right deadzones of 0.32/0.20.

The capture file cadence does not guarantee that every source frame is unique. Renderer performance depends on the game scene and hardware. Native panel output and recording were tested on Windows with an NVIDIA RTX 5070 and Samsung G90XF; other hardware is unverified.
