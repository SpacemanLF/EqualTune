# EqualTune 1.8

Know that it is working: a health check, a delay readout, and an easier way to find and use presets.

## New
- **Audio health check** (Devices page): shows whether sound is reaching EqualTune and leaving it, whether it is clipping, and how much delay EqualTune adds. It tells you what to try when something is wrong, and has a test tone, an EQ on/off button and Restart route.
- **Delay readout:** the equalizer page shows how much delay EqualTune adds while routing. This is EqualTune only: Bluetooth and some outputs add more of their own. You can hide it in Settings.
- **Preset browser:** browse the 100 preset-pack presets (headphones, earphones and genres) on the Presets page and press Use. The list is downloaded from equaltune.app only when you press Browse, and nothing is installed until you use a preset. You can hide the list again with one click.
- **Preset strength:** a Strength slider turns the current preset down toward flat, or back up to 100%. Changing a band yourself starts again from 100%.
- **Preset for each output:** on the Devices page, choose a preset that loads whenever you switch to an output. Leave it on "Remember my last EQ" to keep your own tweaks.
- **Why this preset is active:** a small label above the graph appears when an app rule or an output preset chose the current preset. Click an app-rule label to go back to your own sound until you switch apps.
- **No EQ (flat) for an app:** pick it in an app's rule to turn the EQ off while you use that app.

## Improved
- **Lower delay:** EqualTune now adds roughly 65 ms of delay instead of about 215 ms.
- **Clipping badge:** it now only shows when peaks are well past full scale and clears about a second after they stop, instead of staying on.
- **Output presets:** switching to an output with a preset always loads the full preset and resets Strength to 100%.
- **Tidier equalizer bar:** the Loud, Very loud and Clipping badges sit on their own row on the left, and the delay, Match loudness and A/B button stay on the right.
- **Graph points:** the numbered points are the same size in every skin and easier to read, and they no longer look stretched after the window layout changes.
- **Paper skin:** the cursor over the graph is now visible on light backgrounds.
- **Settings:** the checkboxes line up on the right of every row.

## Notes
- Windows 10 and 11 (64-bit). The installer is not code-signed, so choose **More info**, then **Run anyway**.
- Your 1.7 settings and presets carry over.
