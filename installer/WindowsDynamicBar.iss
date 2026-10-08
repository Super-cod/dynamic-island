; Windows Dynamic Bar - Inno Setup script
; Builds: release\WindowsDynamicBar-Setup.exe  (per-user, no admin required)

#define MyAppName "Windows Dynamic Bar"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "Windows Dynamic Bar Project"
#define MyAppExeName "WindowsDynamicBar.exe"
#define MyPublishDir "..\src\WindowsDynamicBar\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish"
#define MyIcon "..\src\WindowsDynamicBar\Assets\app.ico"

[Setup]
AppId={{9F2B6E3D-5C41-4A7E-9B08-D2E67F3A1C58}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
VersionInfoVersion={#MyAppVersion}
VersionInfoDescription={#MyAppName} installer
DefaultDirName={autopf}\WindowsDynamicBar
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
OutputDir=..\release
OutputBaseFilename=WindowsDynamicBar-Setup
SetupIconFile={#MyIcon}
UninstallDisplayIcon={app}\{#MyAppExeName}
UninstallDisplayName={#MyAppName}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=force
RestartApplications=no
ChangesAssociations=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"
Name: "startup"; Description: "Start automatically when Windows starts"; GroupDescription: "Startup:"

[Files]
Source: "{#MyPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "WindowsDynamicBar"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent
