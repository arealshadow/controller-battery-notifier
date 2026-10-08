; ---------------------------------------------------------------------------
; Controller Battery Notifier - Inno Setup script
; Build with:  ISCC.exe ControllerBatteryNotifier.iss
; Prerequisite: dotnet publish -c Release -r win-x64 --self-contained
;               (output in installer\publish\)
; ---------------------------------------------------------------------------

#define MyAppName "Controller Battery Notifier"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "CBA"
#define MyAppExeName "ControllerBatteryNotifier.exe"
; Relative to this .iss file — the publish folder sits next to it.
#define PublishDir "publish\"

[Setup]
; NOTE: AppId must stay constant across versions so updates replace the old install.
AppId={{D9A7B2C4-1E5F-4B6A-9C3D-8F2E7A1B0C42}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
; Classic behavior: installs into C:\Program Files (elevated by default).
; {autopf} = "Program Files" when elevated, user "Programs" folder otherwise.
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; UAC prompt on launch -> full admin install into Program Files.
PrivilegesRequired=admin
; Offer the option to install for the current user only (no elevation).
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=output
OutputBaseFilename={#MyAppName}-Setup-{#MyAppVersion}
; Installer wizard icon — the same file the app EXE uses. Replace
; src\ControllerBatteryNotifier\Assets\app.ico to change it everywhere at once.
SetupIconFile=..\src\ControllerBatteryNotifier\Assets\app.ico
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExeName}
; Self-contained app: .NET 9 Desktop Runtime is bundled - no prerequisites.

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
; Optional shortcuts. The Start Menu shortcut is created by default and can be
; unchecked; the desktop icon is always created (Inno in this setup silently
; skips task-conditional desktop icons, so it is kept unconditional).
Name: "startmenuicon"; Description: "Create a &start menu shortcut"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "{#PublishDir}*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: startmenuicon
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"; Tasks: startmenuicon
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,Controller Battery Notifier}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Remove the app's settings folder (%APPDATA%\ControllerBatteryNotifier).
Type: filesandordirs; Name: "{userappdata}\{#MyAppName}"

[RegDeleteValue]
; Remove the "Start with Windows" autostart entry so no dead path survives.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; KeyName: "ControllerBatteryNotifier"
