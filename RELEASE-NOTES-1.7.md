# EqualTune 1.7

Device recovery, a manual preamp, a smarter import, 100 downloadable presets, and a layout that fits any window size.

## New
- **Device disconnect and reconnect:** if your headphones or earbuds turn off, are unplugged or drop out while EqualTune is routing, EqualTune moves your sound to the output you used before (or your Windows default) and keeps playing, with no error box. It tries a few times while Windows settles, and tells you if it can't. Device changes are now noticed within about a second.
- **Manual preamp:** a preamp slider (-24 to +24 dB) with a **Reset** button that fades back to 0 smoothly. The preamp is saved with your presets and exports, and an imported Equalizer APO or AutoEq `Preamp:` line sets it for you.
- **Clipping warning:** a clear "Clipping detected" warning appears when your boosts need more headroom.
- **Import preview:** before an imported curve is applied, EqualTune shows how closely it matches your 12 bands and says plainly that the match is an approximation.
- **More import formats:** Equalizer APO / AutoEq filter lines, Room EQ Wizard filter settings, AutoEq GraphicEQ files, and frequency/gain tables saved as CSV or text. REW `.mdat` project files can't be read; export the filter settings or measurement as text instead.
- **Optional preset packs:** 100 presets to download from equaltune.app, ready to import: 37 headphones, 33 earphones (including recent models) and 30 genres. Headphone and earphone presets are based on AutoEq results; the licence is in the download.
- **Layout fits any window size:** the top bar and pages adapt as you resize the window, without wrapping or overlapping.

## Changed
- **Auto-preamp is now off by default**, so you start with the preamp you set. Turn Auto-preamp on if you want EqualTune to pick it for you. Your saved choice is kept.
- **Top bar in the vertical-bar skins:** buttons keep the same size and equal spacing in every skin, they no longer move when you resize the window, and Support stays at the top right.
- **Website and README:** new screenshots, a live demo video and a preset packs section.
- **Per-app profiles:** unchanged.

## Notes
- Windows 10 and 11 (64-bit). The installer is not code-signed, so choose **More info**, then **Run anyway**.
- EqualTune 1.6 settings and presets carry over.
