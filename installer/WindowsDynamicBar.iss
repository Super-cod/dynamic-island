; Windows Dynamic Bar - Inno Setup script
; Builds: release\WindowsDynamicBar-Setup.exe  (per-user, no admin required)

#define MyAppName "Windows Dynamic Bar"
#define MyAppVersion "1.1.0"
#define MyAppPublisher "Supercell"
#define MyAppURL "https://github.com/Super-cod/dynamic-island"
#define MyAppExeName "WindowsDynamicBar.exe"
#define MyPublishDir "..\src\WindowsDynamicBar\bin\x64\Release\net8.0-windows10.0.19041.0\win-x64\publish"
#define MyIcon "..\src\WindowsDynamicBar\Assets\app.ico"

[Setup]
AppId={{9F2B6E3D-5C41-4A7E-9B08-D2E67F3A1C58}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
AppCopyright=Copyright © 2026 Supercell. All rights reserved except as expressly permitted by the accompanying licence.
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

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    UserDataDir := ExpandConstant('{userappdata}\WindowsDynamicBar');
    if DirExists(UserDataDir) then
    begin
      if MsgBox('Do you also want to remove all saved settings, clipboard history, and local cached data?', mbConfirmation, MB_YESNO) = IDYES then
      begin
        DelTree(UserDataDir, True, True, True);
      end;
    end;
  end;
end;
