# Builds EqualTune and packs the installer.
#   1. dotnet publish  -> .\publish        (small build; the installer fetches the .NET runtime only if it is missing)
#   2. Inno Setup      -> .\installer\Output\EqualTune-Setup-<version>.exe
# Run from the project folder:  .\build-installer.ps1
param([Alias('Version')][string]$OverrideVersion)   # optional: .\build-installer.ps1 -Version 1.0.1  (overrides the version in the project file, handy for testing updates)
$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

$csproj = Get-Content .\EqualTune.AudioLab.csproj -Raw
if ($csproj -notmatch '<Version>([^<]+)</Version>') { throw 'Could not find <Version> in the project file.' }
$version = $Matches[1]
$extra = @()
if ($OverrideVersion) { $version = $OverrideVersion; $extra = @("-p:Version=$OverrideVersion", "-p:AssemblyVersion=$OverrideVersion.0", "-p:FileVersion=$OverrideVersion.0", "-p:InformationalVersion=$OverrideVersion") }
Write-Host "Building EqualTune $version" -ForegroundColor Cyan

if (Test-Path .\publish) { Remove-Item .\publish -Recurse -Force }
dotnet publish .\EqualTune.AudioLab.csproj -c Release -r win-x64 --self-contained false -o .\publish @extra
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish failed.' }

# Look for ISCC.exe: on PATH, in the usual install folders (any "Inno Setup*" version), then in the Windows uninstall registry.
$iscc = $null
if ($env:ISCC_PATH -and (Test-Path $env:ISCC_PATH)) { $iscc = $env:ISCC_PATH }
if (-not $iscc) { $iscc = (Get-Command ISCC.exe -ErrorAction SilentlyContinue).Source }
if (-not $iscc) {
  $roots = @("$env:LOCALAPPDATA\Programs", ${env:ProgramFiles(x86)}, $env:ProgramFiles) | Where-Object { $_ -and (Test-Path $_) }
  $iscc = $roots | ForEach-Object { Get-ChildItem -Path $_ -Directory -Filter 'Inno Setup*' -ErrorAction SilentlyContinue } |
          ForEach-Object { Join-Path $_.FullName 'ISCC.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) {
  $keys = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*', 'HKCU:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*'
  $iscc = Get-ItemProperty $keys -ErrorAction SilentlyContinue | Where-Object { $_.DisplayName -like 'Inno Setup*' -and $_.InstallLocation } |
          ForEach-Object { Join-Path $_.InstallLocation 'ISCC.exe' } | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $iscc) { throw 'Inno Setup (ISCC.exe) was not found. Install it from https://jrsoftware.org/isdl.php, or run this script with the path:  $env:ISCC_PATH = "C:\path\to\ISCC.exe"' }

& $iscc "/DMyAppVersion=$version" .\installer\EqualTune.iss
if ($LASTEXITCODE -ne 0) { throw 'Inno Setup failed.' }

Write-Host "`nDone: installer\Output\EqualTune-Setup-$version.exe" -ForegroundColor Green
