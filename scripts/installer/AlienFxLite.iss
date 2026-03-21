#ifndef AppVersion
  #error AppVersion define is required.
#endif
#ifndef PublishDir
  #error PublishDir define is required.
#endif
#ifndef OutputDir
  #error OutputDir define is required.
#endif
#ifndef RepoRoot
  #error RepoRoot define is required.
#endif

#define AppName "AlienFx Lite"
#define AppExeName "AlienFxLite.exe"

[Setup]
AppId={{65E52B25-16EA-47A8-B6DB-BE5A9B289D4A}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=sabino
AppPublisherURL=https://github.com/sabino/alienfx-lite
AppSupportURL=https://github.com/sabino/alienfx-lite/issues
AppUpdatesURL=https://github.com/sabino/alienfx-lite/releases
DefaultDirName={autopf}\AlienFx Lite
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile={#RepoRoot}\LICENSE
OutputDir={#OutputDir}
OutputBaseFilename=AlienFxLite-Setup-win-x64-v{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\{#AppExeName}

[Tasks]
Name: desktopicon; Description: "Create a &desktop shortcut"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Parameters: "--install-service --binary-path ""{app}\{#AppExeName}"""; Flags: runhidden waituntilterminated; StatusMsg: "Installing the AlienFx Lite broker service..."
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{app}\{#AppExeName}"; Parameters: "--uninstall-service"; Flags: runhidden waituntilterminated skipifdoesntexist

[UninstallDelete]
Type: filesandordirs; Name: "{commonappdata}\AlienFxLite"
Type: filesandordirs; Name: "{userappdata}\AlienFxLite"

[Code]
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{cmd}'), '/C sc stop AlienFxLiteService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{cmd}'), '/C sc delete AlienFxLiteService', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(1500);
  Result := '';
end;
