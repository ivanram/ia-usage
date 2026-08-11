; Per-user installer for ClaudeUsageTray. PrivilegesRequired=lowest defaults
; the target to %LocalAppData%\Programs, never Program Files — that's the
; whole point: a folder the user already owns by default, so the diagnostic
; log and the app's own in-place self-update (UpdateService.CopyWithRetry)
; both keep working without ever needing admin rights. Program Files is
; still reachable if the user insists (see NextButtonClick below), but only
; after being warned it means a UAC prompt now and on every future update.
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
; everyone right back in an admin-only folder by default. Program Files is
; still reachable, but only through the explicit warn-and-relaunch-elevated
; path in NextButtonClick below, never as an easy default.
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
// Not exposed by Pascal Script by default; imported so the elevated-handoff
// path (see ElevateSelfToDir) can force this process to disappear
// immediately instead of relying on Abort, which was observed to leave the
// original non-elevated wizard window sitting open behind the new one.
procedure ExitProcess(uExitCode: UINT);
external 'ExitProcess@kernel32.dll stdcall';

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

// One last dig on the Finished page if the app ended up in Program Files
// after all — purely cosmetic, {app} is already fixed by this point.
procedure CurPageChanged(CurPageID: Integer);
var
  AppDir, ProgramFiles, ProgramFilesX86: string;
begin
  if CurPageID = wpFinished then
  begin
    AppDir := Lowercase(AddBackslash(ExpandConstant('{app}')));
    ProgramFiles := Lowercase(AddBackslash(ExpandConstant('{pf}')));
    ProgramFilesX86 := Lowercase(AddBackslash(ExpandConstant('{pf32}')));
    if (Pos(ProgramFiles, AppDir) = 1) or (Pos(ProgramFilesX86, AppDir) = 1) then
      WizardForm.FinishedLabel.Caption := WizardForm.FinishedLabel.Caption + #13#10#13#10 +
        'La app ha sido iNsTaLaDa eN pRoGrAm FiLeSSssss. Disfruta de requerir permisos de administrador cada vez, ¡pardillo!';
  end;
end;

function InitializeUninstall(): Boolean;
var
  ResultCode: Integer;
begin
  Exec('taskkill.exe', '/IM {#MyAppExeName} /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := True;
end;

// Custom two-command-link dialog instead of TaskDialogMsgBox: the built-in
// TaskDialogMsgBox puts the UAC shield glyph on a fixed button slot with no
// way to choose which one gets it, and it landed on the wrong ("No")
// button. Building it by hand with TNewButton lets ElevationRequired be
// set on the actual elevate-accepting button, matching Windows' own
// convention (same glyph Control Panel uses for "Change settings").
function ShowSalmeronDialog(): Boolean;
var
  Form: TSetupForm;
  MsgText: TNewStaticText;
  YesButton, NoButton: TNewButton;
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
      'Si instalas aquí, Windows te va a pedir permisos de administrador ahora mismo, y te los va a volver a pedir cada vez que la aplicación se actualice sola a partir de ahora, porque esa carpeta nunca va a ser tuya.' + #13#10#13#10 +
      '¿ES ESO LO QUE QUIERES, SALMERÓN?';
    MsgText.Parent := Form;
    MsgText.AdjustHeight;

    YesButton := TNewButton.Create(Form);
    YesButton.Style := bsCommandLink;
    YesButton.ElevationRequired := not IsAdmin();
    YesButton.Caption := 'Sí, quiero';
    YesButton.Font.Size := MulDiv(YesButton.Font.Size, 12, 9);
    YesButton.Left := ScaleX(10);
    YesButton.Top := MsgText.Top + MsgText.Height + ScaleY(16);
    YesButton.Width := Form.ClientWidth - ScaleX(20);
    YesButton.ModalResult := mrYes;
    YesButton.Default := True;
    YesButton.Parent := Form;
    YesButton.AdjustHeightIfCommandLink;

    NoButton := TNewButton.Create(Form);
    NoButton.Style := bsCommandLink;
    NoButton.Caption := 'No, esto se me fue de las manos';
    NoButton.Font.Size := MulDiv(NoButton.Font.Size, 12, 9);
    NoButton.Left := ScaleX(10);
    NoButton.Top := YesButton.Top + YesButton.Height + ScaleY(8);
    NoButton.Width := Form.ClientWidth - ScaleX(20);
    NoButton.ModalResult := mrNo;
    NoButton.Cancel := True;
    NoButton.Parent := Form;
    NoButton.AdjustHeightIfCommandLink;

    Form.ClientHeight := NoButton.Top + NoButton.Height + ScaleY(10);
    Form.FlipAndCenterIfNeeded(True, WizardForm, False);

    Result := Form.ShowModal() = mrYes;
  finally
    Form.Free();
  end;
end;

// Windows refuses to 'runas'-elevate a process's OWN currently-running exe
// image directly (confirmed via isolated testing: elevating {srcexe} in
// place fails, elevating an unrelated exe like notepad.exe from the exact
// same ShellExec call works fine). The standard workaround other installers
// use (Chrome, etc.) is to copy the exe elsewhere first and elevate THAT
// copy instead. Inno's own CopyFile also turned out to fail specifically on
// a self-copy of {srcexe} (confirmed: an external copy of the exact same
// running file works fine, so it's not a Windows/AV lock) — worked around
// here by shelling out to cmd's own "copy" instead of using CopyFile.
function ElevateSelfToDir(const TargetDir: string): Boolean;
var
  CopyPath: string;
  ExecOk: Boolean;
  ResultCode: Integer;
begin
  Result := False;
  CopyPath := ExpandConstant('{tmp}') + '\ClaudeUsageTraySetup_elevated.exe';
  if not Exec(ExpandConstant('{cmd}'), '/c copy /Y "' + ExpandConstant('{srcexe}') + '" "' + CopyPath + '"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) or not FileExists(CopyPath) then
    Exit;
  // /CLAUDEELEVATED=1 marks this as a continuation of an already-answered
  // "install to Program Files?" prompt, so the elevated instance knows not
  // to ask again — see the check at the top of NextButtonClick.
  ExecOk := ShellExec('runas', CopyPath, '/DIR="' + TargetDir + '" /CLAUDEELEVATED=1', ExtractFileDir(CopyPath), SW_SHOWNORMAL, ewNoWait, ResultCode);
  if ExecOk then
  begin
    Result := True;
    WizardForm.Hide;
    ExitProcess(0);
  end
  else
    MsgBox('No se han podido conceder permisos de administrador (código de error ' + IntToStr(ResultCode) + '). Elige otra carpeta, o inténtalo de nuevo.', mbError, MB_OK);
end;

// The "Select Destination Location" page lets the user Browse... to ANY
// folder despite PrivilegesRequired=lowest — Inno doesn't filter that
// dialog by writability. Picking Program Files (or Program Files (x86))
// used to sail through this page and only fail later, deep in file
// extraction, with a bare Win32 "Access is denied" (error 5) and no
// indication of why. Intercept it right here instead: warn what it costs,
// and if the user still wants it, hand off to the elevated copy (see
// ElevateSelfToDir above), which terminates this process itself once the
// elevated one is confirmed launched.
//
// The /CLAUDEELEVATED=1 check matters for the relaunched instance: without
// it, the elevated copy would hit this same page with the same
// Program-Files path pre-filled via /DIR and ask the whole question again,
// even though the user already answered it once in the instance that
// launched this one.
function NextButtonClick(CurPageID: Integer): Boolean;
var
  ChosenDir, ProgramFiles, ProgramFilesX86: string;
  WantsToProceed: Boolean;
begin
  Result := True;
  if CurPageID = wpSelectDir then
  begin
    ChosenDir := Lowercase(AddBackslash(WizardDirValue()));
    ProgramFiles := Lowercase(AddBackslash(ExpandConstant('{pf}')));
    ProgramFilesX86 := Lowercase(AddBackslash(ExpandConstant('{pf32}')));
    if (Pos(ProgramFiles, ChosenDir) = 1) or (Pos(ProgramFilesX86, ChosenDir) = 1) then
    begin
      if IsAdmin() and (ExpandConstant('{param:CLAUDEELEVATED|0}') = '1') then
        Result := True
      else
      begin
        WantsToProceed := ShowSalmeronDialog();
        if not WantsToProceed then
          Result := False
        else if IsAdmin() then
          Result := True
        else
        begin
          ElevateSelfToDir(WizardDirValue());
          Result := False;
        end;
      end;
    end;
  end;
end;
