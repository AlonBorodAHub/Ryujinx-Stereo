# Tears of the Kingdom

Tested game: 1.0.0, title ID `0100f2c0115b6000`. Windows x64, NVIDIA RTX 5070,
Samsung Odyssey 3D G90XF, OpenGL with direct LeiaSR output, 2x resolution, FXAA off.
Other game versions, scenes and GPUs have not been comprehensively validated.

## Setup

Supply your own game, keys and firmware. Start it from the Ryujinx Stereo library
so the Zelda engine and included TOTK profile are selected. Set anti-aliasing to
None for the tested configuration. The profile keeps 2x resolution and starts at
the saved 20% depth / 17 convergence defaults. F6 exposes HUD pop-out, initially 2%.

For the tested 60 FPS configuration, install your own version-compatible
DynamicFPS 1.5.4 and UI Blackscreen Fix 1.0.1 through the game's mod directory.
The UI patch's game-1.0.0 asset is `romfs/UI/LayoutArchive/Common.Product.100.Nin_NX_NVN.blarc.zs`.
Avoid overlapping controller UI mods that replace the same archive.
The patch is external and is not included in the emulator or release ZIP.

- [UI Blackscreen Fix documentation and compatibility](https://github.com/teqnic/TOTK-Mods-collection2/tree/main/Mods/FPS/UI%20Blackscreen%20Fix%20(Already%20in%20Controller%20UI%20Mods))
- [Upstream explanation of the DynamicFPS requirement](https://github.com/hoverbike1/TOTK-Mods-collection/blob/main/CHANGELOG.md)

UI fix credits: MarethyuX, Dziggy and ShiftyArmadillo; DynamicFPS: ChucksFeedAndSeed.
The black-background correction is their game mod, not a new emulator copy fix.

## Confirmed in the user's test scenes

- Stereo scene rendering and improved 2x image quality.
- Link lighting no longer jumps to harsh patches at the lake camera angles.
- Tree shading matches between eyes after occlusion reconstruction correction.
- Clouds and particles render in both eyes without opposite-side silhouette cutouts.
- Solid fade panels cover the viewport edges.
- D-pad Up/Right menus show gameplay behind them without flickering or doubled UI
  with the external UI patch installed.

Compilation and GPU checks supplement visual testing; this is not a whole-game
60 FPS or visual-correctness guarantee. New shader compilation can still hitch.
Shader caches are local and not distributed. Temporary menu transfer/draw probes
are removed; normal TOTK startup disables background capture, dumping and profiling.

The release preserves earlier games' published profiles. Source in `zelda-src/`
contains TOTK-specific shader matches and the working threshold/histogram behavior.
