# EqualTune 1.8

Know that it is working: a health check, a delay readout, and an easier way to find and use presets.

## New
- **Audio health check** (Devices page): shows whether sound is reaching EqualTune and leaving it, whether it is clipping, and how much delay EqualTune adds. It tells you what to try when something is wrong, and has a test tone, an EQ on/off button and Restart route.
- **Delay readout:** the equalizer page shows how much delay EqualTune adds while routing. This is EqualTune only: Bluetooth and some outputs add more of their own.
- **Preset browser:** browse the 100 preset-pack presets (37 headphones, 33 earphones, 30 genres) on the Presets page and press Use. The list is downloaded from equaltune.app only when you press Browse, and nothing is installed until you use a preset.
- **Preset strength:** a Strength slider turns the current preset down toward flat, or back up to 100%. Changing a band yourself starts again from 100%.
- **Preset for each output:** on the Devices page, choose a preset that loads whenever you switch to an output. Leave it on "Remember my last EQ" to keep your own tweaks.
- **Why this preset is active:** a small label next to the preset picker appears when an app rule or an output preset chose the preset. Click an app-rule label to go back to your own sound until you switch apps.
- **No EQ (flat) for an app:** pick it in an app's rule to turn the EQ off while you use that app.

## Improved
- **Lower delay:** EqualTune now adds roughly 65 ms of delay instead of about 215 ms (Bluetooth and some outputs add more of their own).
- **Clipping badge:** it now only shows when peaks are well past full scale and clears about a second after they stop, instead of staying on.
- **Output presets:** switching to an output with a preset always loads the full preset and resets Strength to 100%.
- **Delay display:** it stays in a fixed spot next to Match loudness, and you can hide it in Settings.
- **Tidier layout:** the Loud, Very loud and Clipping badges always sit on the second row, and Settings checkboxes line up on the right.
- **Preset packs:** the preset list can be hidden and shown, and scrolls in its own box.

## Notes
- Windows 10 and 11 (64-bit). The installer is not code-signed, so choose **More info**, then **Run anyway**.
- Your 1.7 settings and presets carry over.
