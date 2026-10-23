# 0.6.0-experimental - 2026-10-23

- Add Mario Kart 8 Deluxe, Link’s Awakening, Metroid Dread and Kirby profiles.
- Correct Dread shadow reconstruction and add UI pop-out.
- Correct Kirby shadow receivers and depth-disabled camera mesh masks that caused dark silhouettes beside the character.
- Expose per-game UI pop-out beside depth/convergence for all profiles, preserving existing values and adding a zero default for new games.
- Align dynamically indexed glyph text with button panels in both engines.
- Preserve existing Wonder, 3D World and Zelda renderer paths; version shader caches for new rewrites.
- Keep 4K60 SBS recording unchanged. Reported Link’s Awakening recording slowdown did not reproduce in the latest user test; root cause remains unconfirmed.
- Game-specific 60 fps patches are external and not bundled. Stable whole-game 60 fps remains unverified.

# 0.5.0-experimental - 2026-10-22

- Add TOTK 1.0.0 routing, a 2x stereo profile and HUD pop-out control.
- Include verified Link lighting, tree occlusion, cloud-depth and fade-edge corrections.
- Support depth/convergence controls and L3+R3 exit while another window has focus.
- Disable normal TOTK background capture and profiling; retain manual recording.
- Document the external DynamicFPS UI Blackscreen Fix needed for stable quick menus.
- Add per-game half/full side-by-side, half/full over-under, row interlacing and eye swapping for all five games.
- Keep recordings in SBS and generic display output independent of native panel weaving.
- Include GPU output regression checks and document full-resolution surface requirements; HDMI frame packing remains unavailable.
- Preserve previously published game profiles. Whole-game 60 FPS remains unverified.

# 0.4.0-experimental — 2026-10-20

- Add Mario 3D World + Bowser's Fury's native stereo profile and separate-eye launch path.

- Set 100% depth / 2250 convergence as its initial and reset defaults.

- Correct 3D World's deferred shadow-receiver reconstruction for both eyes, with an isolated shader-cache version.

- GPU tests pass; final visual confirmation of the shadow change and separate Bowser's Fury validation remain pending.

# 0.3.0-experimental — 2026-10-20

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
