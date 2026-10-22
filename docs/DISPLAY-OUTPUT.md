# Stereo display output

The five supported games share two engines. The Display output selector in F6 / Stereo 3D applies to Odyssey, Breath of the Wild, Tears of the Kingdom, Wonder, and 3D World + Bowser's Fury. Settings are saved per game and reload during gameplay.

| Selection | Layout and display setup |
| --- | --- |
| Direct LeiaSR panel | Existing native panel weaving; requires LeiaSR runtime. |
| Half side-by-side | Left eye on the left, right eye on the right; each eye squeezed horizontally. |
| Full side-by-side | Each eye keeps its aspect ratio. For 1920x1080 per eye, use a 3840x1080 drawable window or display mode. |
| Half over-under | Left eye on top, right eye below; each eye squeezed vertically. |
| Full over-under | Each eye keeps its aspect ratio. For 1920x1080 per eye, use a 1920x2160 drawable window or display mode. |
| Row interlaced | Left eye on the top row, right eye on the next row, alternating. Use native display resolution and 1:1 physical pixels. |

Swap left and right eyes reverses the views; for interlaced output it also reverses row parity. Window position, borders and OS scaling can change the parity relative to a passive display, so use fullscreen and adjust the swap setting for the display. Over-under is also called top/bottom.

The full formats use the current drawable surface. Selecting them does not change Windows display modes or force a larger surface. A conventional 1920x1080 fullscreen surface cannot hold two 1920x1080 eye images. Choose a compatible output surface/display mode for full-resolution transport. Internal game resolution remains a separate emulator setting.

Recordings keep the existing SBS format before display weaving, including when the display uses over-under or interlacing. They follow the selected eye order. Generic output modes bypass native panel weaving and the Samsung converter conflict check.

HDMI frame packing is unavailable. The current SDL/OpenGL window and native Leia bridge do not implement HDMI stereo mode negotiation, stereo swap-chain presentation, or HDMI timing/signaling. A tall top/bottom framebuffer does not provide that functionality. Adding true frame packing requires a native presentation backend and hardware/driver validation.

Validation: the two engines compile and publish for Windows x64. GPU checks cover all six choices, both eye orders, recording layout, odd/even dimensions, row orientation, eye-target dimensions and graphics state (48 cases). Actual-engine tests cover each layout and eye-order transition in both engines. These checks do not establish compatibility with every 3D display, or gameplay performance in all five games.

Reproduce the GPU checks on Windows with an OpenGL 4.5 GPU and SDL2.dll beside the test executable (or on PATH):

    dotnet run --project release-tools/OutputChecks -c Release -- path/to/published/engine

Run once against the library/Mario engine directory and once against engines/zelda. Without an engine argument it runs only the 48 standalone compositor cases.
