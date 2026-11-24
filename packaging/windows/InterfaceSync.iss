; Interface Sync installer (Inno Setup 6). Per user: no administrator rights, installs to
; %LOCALAPPDATA%\Programs\Interface Sync, adds a Start menu entry and an uninstaller in Apps & features.
; Uninstalling asks whether to also remove the settings, backups and logs (%LOCALAPPDATA%\InterfaceSync);
; the answer defaults to No, which is also what a silent uninstall does (unless given /REMOVEDATA).
;
; Built by CI:  ISCC /DAppName="Interface Sync" /DAppId=InterfaceSync /DAppVersion=3.0.0 /DSourceDir=<publish> /O<out> InterfaceSync.iss
; Names and version come from Directory.Build.props.

#ifndef AppName
  #error Pass /DAppName (ProductDisplayName from Directory.Build.props)
#endif
#ifndef AppId
  #error Pass /DAppId (ProductId)
#endif
#ifndef AppVersion
  #error Pass /DAppVersion
#endif
#ifndef SourceDir
  #error Pass /DSourceDir (the folder holding the published exe)
#endif

[Setup]
; Never change AppId: Windows uses it to recognise upgrades and the uninstaller.
AppId={{CA182A62-B7B7-4430-BA89-B0C0D2C05F61}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName} contributors
AppPublisherURL=https://github.com/ZyphirWHO/WoW_Forever_Google_Drive_Sync
AppSupportURL=https://github.com/ZyphirWHO/WoW_Forever_Google_Drive_Sync/issues
AppCopyright=Copyright (c) 2026 {#AppName} contributors. MIT License.
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DisableDirPage=auto
DisableProgramGroupPage=yes
DisableReadyPage=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppId}.exe
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputBaseFilename={#AppId}-Setup-{#AppVersion}
SetupIconFile=..\..\src\InterfaceSync.App\Icons\app.ico
WizardStyle=modern
WizardSizePercent=100
WizardImageFile=wizard-100.bmp,wizard-200.bmp
WizardSmallImageFile=small-100.bmp,small-200.bmp
Compression=lzma2/max
SolidCompression=yes
; Interface Sync holds this mutex while running: setup and uninstall ask the player to quit it first.
AppMutex=Local\{#AppId}
CloseApplications=yes
RestartApplications=no
SetupLogging=yes

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
WelcomeLabel2=This installs [name/ver] for you only (no administrator rights needed).%n%nIt syncs your World of Warcraft interface settings between your computers through your own cloud folder. Your game itself is never touched.
FinishedLabel=[name] is installed. Open it to choose your game versions and cloud folder; it then runs quietly by the clock.

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppId}.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\guides\windows.html"; DestDir: "{app}"; DestName: "How to open {#AppName}.html"; Flags: ignoreversion
Source: "..\..\assets\fonts\OFL-Cinzel.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppId}.exe"; Comment: "Sync your interface settings between computers"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppId}.exe"; Tasks: desktopicon

[Registry]
; "Start with Windows" is switched on in the app (setup asks). Uninstalling removes it, and Task Manager's on/off flag.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "{#AppId}"; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run"; ValueType: none; ValueName: "{#AppId}"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppId}.exe"; Description: "Open {#AppName} now"; Flags: nowait postinstall skipifsilent
; An update started from the app runs silently: open the app again afterwards, with its status window.
Filename: "{app}\{#AppId}.exe"; Parameters: "--updated"; Flags: nowait skipifnotsilent

[Code]
{ Uninstall: offer to remove this computer's settings, backups and logs too. No is the default (and what
  /SUPPRESSMSGBOXES answers), so nothing is lost by accident. /REMOVEDATA removes them without asking.
  Only %LOCALAPPDATA%\{#AppId} is removed: the cloud folder and the game's settings are never touched. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Data: String;
  Remove: Boolean;
begin
  if CurUninstallStep <> usPostUninstall then Exit;
  Data := ExpandConstant('{localappdata}\{#AppId}');
  if not DirExists(Data) then Exit;
  Remove := Pos('/REMOVEDATA', Uppercase(GetCmdTail)) > 0;
  if not Remove then
    Remove := SuppressibleMsgBox('Also remove {#AppName}''s settings, backups and logs from this computer?' + #13#10#13#10 +
      'They are in ' + Data + '. Choose No to keep them for a later reinstall.' + #13#10#13#10 +
      'Your cloud folder and your game''s settings are not touched either way.',
      mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) = IDYES;
  if Remove then
    if not DelTree(Data, True, True, True) then
      SuppressibleMsgBox('Some files in ' + Data + ' could not be removed. You can delete the folder yourself.', mbInformation, MB_OK, IDOK);
end;
