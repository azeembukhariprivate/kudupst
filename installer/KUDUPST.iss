; KUDUPST installer (Inno Setup 6)
; Installs for the current Windows user only, so installing never asks for administrator rights.

#ifndef MyAppVersion
  #define MyAppVersion "1.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\KUDUPST"
#endif

#define MyAppName "KUDUPST"
#define MyAppExe "KUDUPST.exe"
#define MyAppUrl "https://github.com/azeembukhariprivate/kudupst"

[Setup]
AppId={{72358240-0504-4C87-9BB0-9C42E8ED2569}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher=Azeem Bukhari
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}/issues
AppUpdatesURL={#MyAppUrl}/releases
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=Output
OutputBaseFilename=KUDUPST-{#MyAppVersion}-setup
SetupIconFile=..\src\KUDUPST\Assets\KUDUPST.ico
UninstallDisplayIcon={app}\{#MyAppExe}
UninstallDisplayName={#MyAppName}
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
VersionInfoVersion={#MyAppVersion}
VersionInfoCompany=Azeem Bukhari
VersionInfoProductName={#MyAppName}
VersionInfoDescription={#MyAppName} Setup

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked
Name: "contextmenu"; Description: "Add ""Open with KUDUPST"" to the right-click menu of .pst, .ost, .msg and .eml files"
Name: "doubleclick"; Description: "Open .pst, .ost, .msg and .eml files in KUDUPST when they're double-clicked"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; Excludes: "self-test.log,screenshots\*"

[Icons]
Name: "{autoprograms}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#MyAppExe}"; Parameters: "--register-shell"; Tasks: contextmenu; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExe}"; Parameters: "--register-open"; Tasks: doubleclick; Flags: runhidden waituntilterminated
Filename: "{app}\{#MyAppExe}"; Description: "Open KUDUPST now"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#MyAppExe}"; Parameters: "--unregister"; Flags: runhidden waituntilterminated; RunOnceId: "KUDUPSTUnregister"

[UninstallDelete]
; Search indexes and the email viewer's data.
Type: filesandordirs; Name: "{localappdata}\KUDUPST"

[Registry]
; Remove saved settings when uninstalling.
Root: HKCU; Subkey: "Software\KUDUPST"; Flags: uninsdeletekey dontcreatekey
