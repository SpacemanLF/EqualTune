# EqualTune 1.0

EqualTune is a system-wide audio equalizer for Windows. A WPF window hosts the HTML interface in WebView2, and a native WASAPI engine does the audio work.

## What it does

- **Equalizer:** up to 12 live bands (bell, shelves, high/low-pass, notch and more) with frequency, gain and Q, drawn on a live response graph. Basic mode uses four tone bands; Advanced adds and removes bands.
- **System-wide routing:** apps play into the VB-CABLE virtual device, EqualTune applies the EQ and plays the result on your headphones or speakers. Switching the listening output while routing keeps the route running.
- **Volume:** the Windows volume slider (and mute) controls the routed sound. `Ctrl+Alt+↑ / ↓` change the Windows volume in 5% steps.
- **Presets:** 14 built-in presets (4- and 8-band versions), your own saved presets, and a Presets page to rename, delete, import and export them. Equalizer APO / AutoEq text files can be imported and exported.
- **Per-output and per-app EQ:** each output device remembers its own EQ, and EqualTune can switch presets automatically for the app you are using.
- **Sound tools:** auto-preamp with a clip warning, level-matched A/B compare, limiter, channel balance, equal loudness and attenuation.
- **Live spectrum**, a different visual style for each of 16 skins, custom skins, background images and fonts.
- **Global hotkeys:** `Ctrl+Alt+E` EQ on/off · `Ctrl+Alt+B` A/B compare · `Ctrl+Alt+←/→` previous/next preset · `Ctrl+Alt+↑/↓` volume.
- **Safe output restore:** if EqualTune stops or crashes, Windows sound is switched back to your real output.

## Where things are kept

| What | Where |
| --- | --- |
| Settings, presets, skins, app rules | `%LOCALAPPDATA%\EqualTune\WebView2` |
| Output-restore state | `%LOCALAPPDATA%\EqualTune\output-guard.json` |
| Log files | `logs` folder next to the app (falls back to `%LOCALAPPDATA%\EqualTune\logs` if that folder is read-only); the last 14 days are kept |

Only one copy of EqualTune runs at a time: starting it again brings the open window to the front.

## VB-CABLE

System-wide routing needs VB-CABLE, a donationware virtual audio device by [VB-Audio Software](https://vb-audio.com/Cable/). EqualTune does not include or replace it. Donating to VB-Audio is optional and goes to them. Settings → About has a link to their page.

## Supporting EqualTune

EqualTune is free to use. Settings → About and the sidebar have an optional **Support EqualTune** button that opens https://buymeacoffee.com/equaltune (set in `AppInfo.DonateUrl`).

## Install and update

Download `EqualTune-Setup-x.y.z.exe` from the [Releases](https://github.com/SpacemanLF/EqualTune/releases) page and run it. You can choose the install folder. Setup installs the .NET Desktop Runtime and WebView2 only when they are missing, and can install VB-CABLE for you.

EqualTune checks GitHub for new versions about once a day (Settings → About; this can be turned off) and updates only after you approve. Windows may show a SmartScreen warning because the installer is not code-signed yet: choose **More info → Run anyway**.

To build the installer yourself, see [RELEASING.md](RELEASING.md).

## Build and run

Requires the .NET 10 SDK and the Windows Desktop Runtime.

```powershell
dotnet build -c Release
dotnet run --project .\EqualTune.AudioLab.csproj
```

The audio engine uses Windows' built-in Core Audio COM and WASAPI APIs directly. The only package dependency is Microsoft's WebView2 SDK.

The app icon is `Assets/EqualTune.ico` (source: `Assets/EqualTune-icon-1024.png`). The same icon is used for the window, the EXE and the installer.

## Notes

- The routed sound uses the shared-mode mix format; 32-bit float and 16-bit PCM are supported.
- Playback loopback (capturing a normal output instead of the virtual cable) is a diagnostic preview: the original sound keeps playing alongside the processed copy.
