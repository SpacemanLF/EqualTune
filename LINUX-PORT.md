# EqualTune Linux port - workspace notes

This folder is a separate copy of the Windows project, used to prepare the Linux (and later macOS) versions.
Work here on the `linux-port` git branch. Do not push to `main` until the Windows app has been re-tested.

## Layout
- `EqualTune.AudioLab.csproj` + `*.cs` + `MainWindow.xaml*` - the Windows app (WPF + WebView2), unchanged in behaviour.
- `core/` - platform-independent code (EQ math, band model). Used by the Windows app and the new shell.
- `shell/` - cross-platform window (Photino) hosting the same `Assets/EqualizerPrototype.html`. Fake engine only for now.
- `Assets/EqualizerPrototype.html` - the one shared interface file. A small bridge script at the top of the page lets it talk to the shell.

## Status
- Step 0 (started): `Dsp.cs` and `AudioEqBand.cs` moved into `core/`.
- Step 1 (started): shell prototype opens the real interface with sample devices.

- Step 2 (done in the VM): Linux audio engine. `core/EqProcessor.cs` (shared EQ chain), `shell/PulseCtl.cs` (pactl + libpulse-simple),
  `shell/LinuxRouter.cs` (record -> EQ -> play), `shell/LinuxBackend.cs` (interface messages, status, settings). Creates a virtual output
  called "EqualTune", moves app streams onto it, restores everything on stop. `dotnet run -- --fake` keeps the old sample backend.
  Tested in a VirtualBox Ubuntu 22.04 VM (needs >= 4 CPUs / 6 GB RAM to be smooth). Test knobs: EQUALTUNE_BLOCK=<frames>, WEBKIT_DISABLE_COMPOSITING_MODE=1.

## Next
- Volume keys while routing (virtual output volume is not applied yet), tray icon, global hotkeys, per-app auto skins.
- Move the remaining portable logic (settings, updater, app controller) out of `MainWindow.xaml.cs` into `core/`.
- Put audio, device switching, tray, autostart and hotkeys behind interfaces; keep the Windows versions as they are.
- Linux audio engine (PipeWire/PulseAudio virtual sink), then tray, autostart and an AppImage.

## Known risks
- Presets and skins live in the web view's localStorage; the shell must keep that storage between runs (check on Linux).
- The interface CSS uses `color-mix()`; older WebKitGTK versions may not support it.
