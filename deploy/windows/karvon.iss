; Karvon Studio's installer (Inno Setup 6), made by deploy/windows/package.ps1, which passes the version and the folders.
; A per-user install by default (no administrator needed), Karvon.exe in the Start menu, its folder on the user's PATH
; for `karvon`, and an uninstaller.
#define AppName "Karvon Studio"

[Setup]
AppId={{6F1E2C3A-8B5D-4E7F-9A0B-1C2D3E4F5A6B}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Karvon Studio
AppPublisherURL=https://github.com/karvon-studio/karvon
DefaultDirName={autopf}\Karvon Studio
DefaultGroupName={#AppName}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=Karvon-Studio-{#AppVersion}-Setup
SetupIconFile={#IconFile}
UninstallDisplayIcon={app}\Karvon.exe
Compression=lzma2
SolidCompression=yes
ChangesEnvironment=yes
WizardStyle=modern

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\Karvon.exe"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\Karvon.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "addtopath"; Description: "Add Karvon Studio to the PATH (karvon in a terminal)"; Flags: checkedonce

[Registry]
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NotOnPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\Karvon.exe"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
function NotOnPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Path := '';
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;
