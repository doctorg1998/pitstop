; Pitstop installer: per-user, no administrator rights needed.
; Build: dotnet publish src/Pitstop.App -p:PublishProfile=win-x64, then
;        "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\pitstop.iss   (or Program Files (x86) on CI)

#define AppName "Pitstop"
#ifndef AppVersion
  #define AppVersion "1.1.0"
#endif
#define SourceDir "..\publish\win-x64"

[Setup]
AppId={{6E3B9C1A-5D2F-4E7B-9A41-8C0F2D7B3E15}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Giovanni Sanchez
AppPublisherURL=https://doctorg1998.github.io
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=Pitstop-Setup-{#AppVersion}
SetupIconFile=..\src\Pitstop.App\pitstop.ico
UninstallDisplayIcon={app}\Pitstop.exe
UninstallDisplayName={#AppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; Ask the user to exit Pitstop first (exiting reverts any active profile). Matches the app's Local\Pitstop.Tray mutex.
AppMutex=Pitstop.Tray

[Tasks]
Name: "autostart"; Description: "Start Pitstop when I sign in to Windows"; Flags: unchecked
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\Pitstop.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Pitstop.exe"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start with Windows" menu item uses.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Pitstop"; ValueData: """{app}\Pitstop.exe"" --tray"; Tasks: autostart
; Always remove it on uninstall, even if it was turned on later from the tray menu.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; ValueName: "Pitstop"; Flags: uninsdeletevalue

[Run]
Filename: "{app}\Pitstop.exe"; Description: "Launch Pitstop now"; Flags: nowait postinstall skipifsilent

; Settings, logs and reports in %LOCALAPPDATA%\Pitstop are the user's data and are kept on uninstall.
