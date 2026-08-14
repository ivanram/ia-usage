; Per-user installer for ClaudeUsageTray. PrivilegesRequired=lowest defaults
; the target to %LocalAppData%\Programs, never Program Files — that's the
; whole point: a folder the user already owns by default, so the diagnostic
; log and the app's own in-place self-update (UpdateService.CopyWithRetry)
; both keep working without ever needing admin rights. Program Files is
; blocked outright (see NextButtonClick below) — no elevate-and-install-
; anyway escape hatch. That used to exist, and it's exactly how a real
; install went silently broken: the self-updater's plain-user File.Copy
; can never overwrite an admin-owned folder, no matter how the FIRST
; install got there.
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
; Explicit (not relying on the compiler default) — this page must actually
; appear, otherwise the NextButtonClick guard below that blocks Program
; Files never gets a chance to run.
DisableDirPage=no
; No generic "install for all users" checkbox on purpose — that would put
; everyone right back in an admin-only folder by default, which is now
; blocked outright anyway (see NextButtonClick below).
PrivilegesRequired=lowest
OutputBaseFilename=ClaudeUsageTraySetup
OutputDir=..\Releases
SetupIconFile=ClaudeUsageTray.Wpf\app.ico
UninstallDisplayIcon={app}\{#MyAppExeName}
WizardImageFile=installer_wizard.bmp
WizardSmallImageFile=installer_wizard_small.bmp
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

// Single-button dialog, not a Yes/No choice — Program Files used to be
// reachable via an elevate-and-install-anyway escape hatch (ShowSalmeronDialog
// used to return a bool, ElevateSelfToDir did the elevated re-launch), which
// is exactly the class of install this app can no longer support: it broke
// silently (a real friend's self-update just stopped working, invisibly,
// for a whole release) because the auto-updater's plain-user File.Copy can
// never overwrite an admin-owned folder, elevated first install or not. So
// there's no "Sí, quiero" anymore — Program Files is off the table, full
// stop, and NextButtonClick below always rejects the page instead of
// branching on the answer.
procedure ShowSalmeronDialog();
var
  Form: TSetupForm;
  MsgText: TNewStaticText;
  OkButton: TNewButton;
begin
  Form := CreateCustomForm(ScaleX(460), ScaleY(120), False, True);
  try
    Form.Caption := '¡Ajá, Salmerón!';

    MsgText := TNewStaticText.Create(Form);
    MsgText.Left := ScaleX(10);
    MsgText.Top := ScaleY(10);
    MsgText.Width := Form.ClientWidth - ScaleX(20);
    MsgText.AutoSize := False;
    MsgText.WordWrap := True;
    MsgText.Caption :=
      '¿Así que todavía estás dando por culo con esa carpeta, eh? ¿Crees que puedes derrotarme? ¡Imposible! YO SOY CLAUDIO DÉCIMO MERIDIO, comandante de los ejércitos del norte, general de las legiones félix, fiel servidor del verdadero emperador... y esa carpeta no es para ti.' + #13#10#13#10 +
      'Esta carpeta necesita permisos de administrador para escribir en ella, y eso rompe la actualización automática de la aplicación de forma silenciosa — así que ya no se puede elegir, ni siquiera si insistes. Elige otra carpeta (la que viene puesta por defecto funciona perfectamente sin pedirte nada).';
    MsgText.Parent := Form;
    MsgText.AdjustHeight;

    OkButton := TNewButton.Create(Form);
    OkButton.Caption := 'Entendido';
    OkButton.Left := Form.ClientWidth - ScaleX(90);
    OkButton.Top := MsgText.Top + MsgText.Height + ScaleY(16);
    OkButton.Width := ScaleX(80);
    OkButton.ModalResult := mrOk;
    OkButton.Default := True;
    OkButton.Cancel := True;
    OkButton.Parent := Form;

    Form.ClientHeight := OkButton.Top + OkButton.Height + ScaleY(10);
    Form.FlipAndCenterIfNeeded(True, WizardForm, False);

    Form.ShowModal();
  finally
    Form.Free();
  end;
end;

// The "Select Destination Location" page lets the user Browse... to ANY
// folder despite PrivilegesRequired=lowest — Inno doesn't filter that
// dialog by writability. Picking Program Files (or Program Files (x86))
// used to sail through this page and only fail later, deep in file
// extraction, with a bare Win32 "Access is denied" (error 5) and no
// indication of why. Intercept it right here instead and reject the page
// outright — see ShowSalmeronDialog for why there's no "install anyway"
// path left.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  ChosenDir, ProgramFiles, ProgramFilesX86: string;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    ChosenDir := Lowercase(AddBackslash(WizardDirValue()));
    ProgramFiles := Lowercase(AddBackslash(ExpandConstant('{pf}')));
    ProgramFilesX86 := Lowercase(AddBackslash(ExpandConstant('{pf32}')));
    if (Pos(ProgramFiles, ChosenDir) = 1) or (Pos(ProgramFilesX86, ChosenDir) = 1) then
    begin
      ShowSalmeronDialog();
      Result := False;
    end;
  end;
end;
