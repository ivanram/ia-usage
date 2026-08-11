; Per-user installer for ClaudeUsageTray. PrivilegesRequired=lowest keeps
; the target under %LocalAppData%\Programs, never Program Files — that's
; the whole point: a folder the user already owns, so the diagnostic log
; and the app's own in-place self-update (UpdateService.CopyWithRetry)
; both keep working without ever needing admin rights.
;
; AppVersion is passed in from the build script via /DMyAppVersion=X.Y.Z
; so this file doesn't need hand-editing on every release.

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif

#define MyAppName "ClaudeUsageTray"
#define MyAppPublisher "Ivan Ramirez"
#define MyAppExeName "ClaudeUsageTray.exe"
#define MyAppURL "https://github.com/ivanram/ia-usage"

[Setup]
; Fixed GUID — must never change across releases, it's what lets the
; installer recognize "this is an upgrade" instead of a second install.
AppId={{B7B2E6C1-8B1A-4C9A-9C7D-2E9F6B2A6E31}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}/releases
DefaultDirName={localappdata}\Programs\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
; No override option on purpose: letting someone pick "install for all
; users" would put it right back in an admin-only folder like Program
; Files, which is the exact bug this installer exists to avoid.
PrivilegesRequired=lowest
OutputBaseFilename=ClaudeUsageTraySetup
OutputDir=..\Releases
SetupIconFile=ClaudeUsageTray.Wpf\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
CloseApplications=yes
RestartApplications=no

[Languages]
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "autostart"; Description: "Iniciar {#MyAppName} con Windows"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "ClaudeUsageTray.Wpf\publish_installer\ClaudeUsageTray.exe"; DestDir: "{app}"; DestName: "{#MyAppExeName}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon
Name: "{userstartup}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: autostart

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#MyAppName}}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: files; Name: "{userstartup}\{#MyAppName}.lnk"
; diagnostico_inicio.txt is written by the app itself at runtime, not
; installed by [Files], so Inno Setup doesn't know to remove it on its
; own — without this it'd be the one file left behind, keeping {app}
; from disappearing on uninstall.
Type: files; Name: "{app}\diagnostico_inicio.txt"
Type: dirifempty; Name: "{app}"

[Code]
procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  // The app enforces a single running instance via a named mutex; a
  // leftover process from a previous install would otherwise hold the
  // exe file locked and make the [Files] copy above fail outright.
  if CurStep = ssInstall then
  begin
    Exec('taskkill.exe', '/IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;
