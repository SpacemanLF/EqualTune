# Releasing EqualTune

## One-time setup
1. Install **Inno Setup 6.3 or newer** (free): https://jrsoftware.org/isdl.php
2. Put the project on GitHub (repo: `SpacemanLF/EqualTune`). From the project folder:
   ```powershell
   git init -b main
   git add .
   git commit -m "EqualTune 1.0.0"
   git remote add origin https://github.com/SpacemanLF/EqualTune.git
   git push -u origin main
   ```
   `.gitignore` already leaves out build output, logs and the `backup-*` folders.

## Every release
1. Change `<Version>` (and `AssemblyVersion`/`FileVersion`/`InformationalVersion`) in `EqualTune.AudioLab.csproj`, e.g. `1.0.1`.
2. Build the installer:
   ```powershell
   .\build-installer.ps1
   ```
   Result: `installer\Output\EqualTune-Setup-1.0.1.exe`
3. Test it on a PC (or VM) — install, launch, uninstall.
4. On GitHub: **Releases → Draft a new release**
   - Tag: `v1.0.1` (must match the version, with a leading `v`)
   - Title and notes: what changed
   - Attach `EqualTune-Setup-1.0.1.exe` (the file name must start with `EqualTune-Setup` and end in `.exe`)
   - Publish
5. Installed copies of EqualTune find the new release within about a day (or when the user presses **Check for updates** in Settings → About). The user approves, the app downloads the installer and runs it silently, and EqualTune restarts.

## What the installer does
- Lets the user pick the install folder (any drive); default is `C:\Program Files\EqualTune`.
- Installs the .NET 10 Desktop Runtime and Microsoft WebView2 **only if they are missing** (downloaded from Microsoft).
- Optionally installs **VB-CABLE**: downloads VB-Audio's official package and starts their own installer, after a page explaining that VB-CABLE is donationware with a button to their donation page. VB-CABLE is not bundled or modified.
- Creates Start menu (and optional desktop) shortcuts and a `logs` folder next to the app.
- Uninstall: closes EqualTune (which puts Windows sound back on your real output), removes the startup entry, and asks whether to delete saved settings. VB-CABLE is left installed because other apps may use it.

## Notes
- The installer is **not code-signed**, so Windows SmartScreen may show "Windows protected your PC" the first time. Users click **More info → Run anyway**. Signing can be added later.
- The in-app updater only downloads installers from `github.com` over HTTPS.
