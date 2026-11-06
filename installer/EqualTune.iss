; EqualTune installer — build with build-installer.ps1 (needs Inno Setup 6.3 or newer: https://jrsoftware.org/isdl.php)
#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef MyAppVersionInfo
  #define MyAppVersionInfo MyAppVersion
#endif
#define MyAppName "EqualTune"
#define MyAppExe "EqualTune.AudioLab.exe"

[Setup]
AppId={{E365B2F5-6E77-4BBE-8058-B77DEAF4E074}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=EqualTune
AppPublisherURL=https://github.com/SpacemanLF/EqualTune
AppSupportURL=https://github.com/SpacemanLF/EqualTune/issues
AppUpdatesURL=https://github.com/SpacemanLF/EqualTune/releases
VersionInfoVersion={#MyAppVersionInfo}
DefaultDirName={autopf}\EqualTune
DefaultGroupName=EqualTune
; The install folder page is always shown so the app can go on any drive.
DisableDirPage=no
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Administrator rights are needed to install the VB-CABLE audio driver.
PrivilegesRequired=admin
SetupIconFile=..\Assets\EqualTune.ico
UninstallDisplayIcon={app}\{#MyAppExe}
OutputDir=Output
OutputBaseFilename=EqualTune-Setup-{#MyAppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked
Name: "vbcable"; Description: "Install VB-CABLE, the free virtual audio device EqualTune needs for system-wide EQ"; Check: VBCableMissing

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: recursesubdirs ignoreversion createallsubdirs

[Dirs]
; The log folder lives next to the app, and normal users may write to it.
Name: "{app}\logs"; Permissions: users-modify

[UninstallDelete]
Type: filesandordirs; Name: "{app}\logs"

[Icons]
Name: "{group}\EqualTune"; Filename: "{app}\{#MyAppExe}"
Name: "{group}\Uninstall EqualTune"; Filename: "{uninstallexe}"
Name: "{autodesktop}\EqualTune"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
; Prerequisites downloaded earlier (only when they were missing).
Filename: "{tmp}\windowsdesktop-runtime-win-x64.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing the .NET Desktop Runtime..."; Check: RuntimeDownloaded; Flags: waituntilterminated
Filename: "{tmp}\MicrosoftEdgeWebview2Setup.exe"; Parameters: "/silent /install"; StatusMsg: "Installing Microsoft WebView2..."; Check: WebView2Downloaded; Flags: waituntilterminated
; Start EqualTune: a visible launch when the user ticks the box, and an automatic restart after a silent in-app update.
Filename: "{app}\{#MyAppExe}"; Description: "Launch EqualTune"; Flags: nowait postinstall skipifsilent runasoriginaluser
Filename: "{app}\{#MyAppExe}"; Flags: nowait runasoriginaluser; Check: WizardSilent

[Code]
const
  RuntimeUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  VBCableUrl = 'https://download.vb-audio.com/Download_CABLE/VBCABLE_Driver_Pack45.zip';
  VBAudioUrl = 'https://vb-audio.com/Cable/';

var
  DownloadPage: TDownloadWizardPage;
  DonatePage: TWizardPage;
  HasVBCable: Boolean;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

function DotNetDesktopInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then
  begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
        begin
          Result := True;
          Break;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result := False;
  if RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
    Result := (Version <> '') and (Version <> '0.0.0.0');
  if not Result then
    if RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}', 'pv', Version) then
      Result := (Version <> '') and (Version <> '0.0.0.0');
end;

function DetectVBCable: Boolean;
var
  ResultCode: Integer;
begin
  Result := Exec('powershell.exe',
    '-NoProfile -ExecutionPolicy Bypass -Command "if (Get-CimInstance Win32_SoundDevice | Where-Object { .Name -like ''*CABLE*'' -or .Name -like ''*VB-Audio*'' }) { exit 0 } else { exit 1 }"',
    '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);
end;

function VBCableMissing: Boolean;
begin
  Result := not HasVBCable;
end;

function WantsVBCable: Boolean;
begin
  Result := False;
  if not HasVBCable then
    Result := WizardIsTaskSelected('vbcable');
end;

function RuntimeDownloaded: Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\windowsdesktop-runtime-win-x64.exe'));
end;

function WebView2Downloaded: Boolean;
begin
  Result := FileExists(ExpandConstant('{tmp}\MicrosoftEdgeWebview2Setup.exe'));
end;

procedure OpenVBAudioClick(Sender: TObject);
var
  ErrorCode: Integer;
begin
  ShellExec('open', VBAudioUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
end;

procedure InitializeWizard;
var
  Info: TNewStaticText;
  Button: TNewButton;
begin
  HasVBCable := DetectVBCable;
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);

  DonatePage := CreateCustomPage(wpSelectTasks, 'Support VB-Audio', 'VB-CABLE is donationware');
  Info := TNewStaticText.Create(DonatePage);
  Info.Parent := DonatePage.Surface;
  Info.WordWrap := True;
  Info.Width := DonatePage.SurfaceWidth;
  Info.Caption :=
    'EqualTune uses VB-CABLE, a free virtual audio device made by VB-Audio Software, to equalize everything your PC plays.' + #13#10 + #13#10 +
    'VB-CABLE is donationware: it is free to use, and VB-Audio welcomes contributions from people who find it useful. ' +
    'Donating is completely optional, and the money goes to VB-Audio, not to EqualTune.' + #13#10 + #13#10 +
    'Setup will download the official VB-CABLE package from VB-Audio and start its own installer. ' +
    'Click "Install Driver" in that window. Windows may ask you to restart afterwards.';
  Button := TNewButton.Create(DonatePage);
  Button.Parent := DonatePage.Surface;
  Button.Top := Info.Top + ScaleY(150);
  Button.Width := ScaleX(240);
  Button.Height := ScaleY(26);
  Button.Caption := 'Open the VB-Audio page (donate)';
  Button.OnClick := @OpenVBAudioClick;
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := False;
  if PageID = DonatePage.ID then
    Result := not WantsVBCable;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Count: Integer;
begin
  Result := True;
  if CurPageID = wpReady then
  begin
    DownloadPage.Clear;
    Count := 0;
    if not DotNetDesktopInstalled then
    begin
      DownloadPage.Add(RuntimeUrl, 'windowsdesktop-runtime-win-x64.exe', '');
      Count := Count + 1;
    end;
    if not WebView2Installed then
    begin
      DownloadPage.Add(WebView2Url, 'MicrosoftEdgeWebview2Setup.exe', '');
      Count := Count + 1;
    end;
    if WantsVBCable then
    begin
      DownloadPage.Add(VBCableUrl, 'VBCABLE_Driver_Pack45.zip', '');
      Count := Count + 1;
    end;
    if Count > 0 then
    begin
      DownloadPage.Show;
      try
        try
          DownloadPage.Download;
        except
          if DownloadPage.AbortedByUser then
            Log('Download cancelled by the user.')
          else
            SuppressibleMsgBox('A required component could not be downloaded:' + #13#10 + GetExceptionMessage + #13#10#13#10 + 'Check your internet connection and run Setup again.', mbCriticalError, MB_OK, IDOK);
          Result := False;
        end;
      finally
        DownloadPage.Hide;
      end;
    end;
  end;
end;

procedure QuitRunningEqualTune(AppDir: String);
var
  ResultCode: Integer;
  Exe: String;
begin
  Exe := AddBackslash(AppDir) + '{#MyAppExe}';
  if FileExists(Exe) then
  begin
    Exec(Exe, '--quit', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
    Sleep(2500);
    Exec('taskkill.exe', '/F /IM {#MyAppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);   // clears the small background helper too
    Sleep(500);
    Exec(Exe, '--restore-output', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);   // puts Windows sound back if the app could not do it itself
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  QuitRunningEqualTune(ExpandConstant('{app}'));
  Result := '';
end;

procedure InstallVBCable;
var
  ResultCode: Integer;
  Folder, Setup: String;
begin
  if not FileExists(ExpandConstant('{tmp}\VBCABLE_Driver_Pack45.zip')) then Exit;
  Folder := ExpandConstant('{tmp}\vbcable');
  ForceDirectories(Folder);
  WizardForm.StatusLabel.Caption := 'Unpacking VB-CABLE...';
  Exec('powershell.exe', '-NoProfile -ExecutionPolicy Bypass -Command "Expand-Archive -LiteralPath ''' + ExpandConstant('{tmp}\VBCABLE_Driver_Pack45.zip') + ''' -DestinationPath ''' + Folder + ''' -Force"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Setup := Folder + '\VBCABLE_Setup_x64.exe';
  if FileExists(Setup) then
  begin
    WizardForm.StatusLabel.Caption := 'Starting the VB-CABLE installer. Click "Install Driver" in its window...';
    Exec(Setup, '', Folder, SW_SHOWNORMAL, ewWaitUntilTerminated, ResultCode);
  end
  else
    MsgBox('Setup could not unpack the VB-CABLE package. You can install it from ' + VBAudioUrl + ' and run EqualTune again.', mbInformation, MB_OK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    if WantsVBCable then
      InstallVBCable;
end;

function InitializeUninstall: Boolean;
begin
  QuitRunningEqualTune(ExpandConstant('{app}'));   // EqualTune puts Windows sound back on your real output as it exits
  Result := True;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode: Integer;
begin
  if CurUninstallStep = usUninstall then
    Exec(ExpandConstant('{app}\{#MyAppExe}'), '--remove-startup', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if CurUninstallStep = usPostUninstall then
    if MsgBox('Also remove your EqualTune settings, presets and skins?' + #13#10#13#10 + 'Choose No to keep them in case you reinstall.', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant('{localappdata}\EqualTune'), True, True, True);
end;
