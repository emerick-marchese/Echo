; Echo installer (Inno Setup 6.3+).
; Build: dotnet publish src\Echo -c Release -r win-x64 --self-contained -o publish
;        ISCC.exe /DAppVersion=2.0.0 installer\Echo.iss
; Installs for the current user only (no administrator rights needed), with everything
; required: the app, its .NET runtime, the speech recognition model, and the WebView2
; runtime if Windows does not have it yet. Replaces ListenTeams (Echo's former name).

#ifndef AppVersion
  #define AppVersion "2.0.0"
#endif
#define AppName "Echo"
#define AppExe "Echo.exe"
#define LegacyAppId "{7C1D2CBB-C745-4AF5-9551-5CA291C68AF1}"
#define WebView2Client "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}"

[Setup]
AppId={{3FB2F769-B6CC-4DBD-83C1-2AA7353D807F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
VersionInfoVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
; Windows 10 version 2004 or later: needed to capture the sound of the calling app alone.
MinVersion=10.0.19041
OutputDir=Output
OutputBaseFilename=Echo-Setup
SetupIconFile=..\assets\echo.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=force
RestartApplications=no
ShowLanguageDialog=no

[Languages]
Name: "french"; MessagesFile: "compiler:Languages\French.isl"

[Tasks]
Name: "autostart"; Description: "Lancer Echo à l'ouverture de session (discrètement, près de l'horloge)"; GroupDescription: "Démarrage :"
Name: "desktopicon"; Description: "Créer une icône sur le Bureau"; GroupDescription: "Raccourcis :"

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Speech recognition model downloaded during setup (see [Code]).
Source: "{tmp}\{code:ModelFile}"; DestDir: "{app}\models"; Flags: external skipifsourcedoesntexist ignoreversion; Check: ModelDownloaded

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --background"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "Lancer Echo maintenant"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopEcho"

[UninstallDelete]
; Logs, interface cache and models downloaded by the app. Recordings and settings are kept.
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

[Code]
const
  ModelBaseUrl = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2Setup = 'MicrosoftEdgeWebview2Setup.exe';

var
  ModelPage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  Downloaded: Boolean;
  WebView2Downloaded: Boolean;

function ModelFileAt(Index: Integer): String;
begin
  case Index of
    0: Result := 'ggml-large-v3-turbo-q5_0.bin';
    1: Result := 'ggml-small-q5_1.bin';
    2: Result := 'ggml-base-q5_1.bin';
  else
    Result := '';
  end;
end;

function ModelFile(Param: String): String;
begin
  Result := ModelFileAt(ModelPage.SelectedValueIndex);
end;

function ModelDownloaded: Boolean;
begin
  Result := Downloaded;
end;

{ ---------- ListenTeams (former name) ---------- }

function LegacyUninstallKey: String;
begin
  Result := 'Software\Microsoft\Windows\CurrentVersion\Uninstall\{#LegacyAppId}_is1';
end;

function LegacyInstallDir: String;
begin
  if not RegQueryStringValue(HKCU, LegacyUninstallKey, 'InstallLocation', Result) then
    Result := '';
end;

{ Model already on the PC from ListenTeams: it is copied instead of downloaded again. }
function LegacyModelPath(FileName: String): String;
var
  Dir: String;
begin
  Result := '';
  Dir := LegacyInstallDir;
  if (Dir <> '') and FileExists(AddBackslash(Dir) + 'models\' + FileName) then
    Result := AddBackslash(Dir) + 'models\' + FileName
  else if FileExists(ExpandConstant('{localappdata}\ListenTeams\models\') + FileName) then
    Result := ExpandConstant('{localappdata}\ListenTeams\models\') + FileName;
end;

procedure CopyLegacyModels;
var
  I: Integer;
  Source, Target: String;
begin
  for I := 0 to 2 do
  begin
    Source := LegacyModelPath(ModelFileAt(I));
    Target := ExpandConstant('{app}\models\') + ModelFileAt(I);
    if (Source <> '') and not FileExists(Target) then
    begin
      ForceDirectories(ExpandConstant('{app}\models'));
      if FileCopy(Source, Target, False) then
        Log('Modèle repris de ListenTeams : ' + Source);
    end;
  end;
end;

procedure UninstallListenTeams;
var
  Uninstaller: String;
  ResultCode: Integer;
begin
  if not RegQueryStringValue(HKCU, LegacyUninstallKey, 'UninstallString', Uninstaller) then
    Exit;
  Uninstaller := RemoveQuotes(Uninstaller);
  if FileExists(Uninstaller) then
  begin
    Log('Désinstallation de ListenTeams : ' + Uninstaller);
    Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

{ ---------- WebView2 (displays the Echo window) ---------- }

function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM32, 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{#WebView2Client}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, 'Software\Microsoft\EdgeUpdate\Clients\{#WebView2Client}', 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

{ ---------- Wizard ---------- }

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  ModelPage := CreateInputOptionPage(wpSelectTasks,
    'Modèle de transcription',
    'Choisissez la précision de la transcription des appels.',
    'La transcription est faite sur ce PC, après chaque appel. Le modèle est téléchargé une seule fois ; ' +
    'vous pourrez en changer plus tard dans les paramètres d''Echo.',
    True, False);
  ModelPage.Add('Précis — recommandé (547 Mo)');
  ModelPage.Add('Équilibré — pour PC modeste (181 Mo)');
  ModelPage.Add('Rapide — qualité réduite (57 Mo)');
  ModelPage.Add('Ne rien télécharger maintenant (téléchargement automatique après le premier appel)');
  ModelPage.SelectedValueIndex := 0;

  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), @OnDownloadProgress);
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  FileName: String;
  NeedModel: Boolean;
begin
  Result := True;
  if CurPageID <> wpReady then
    Exit;

  Downloaded := False;
  WebView2Downloaded := False;
  FileName := ModelFile('');
  NeedModel := (FileName <> '') and not FileExists(ExpandConstant('{app}\models\') + FileName) and (LegacyModelPath(FileName) = '');

  DownloadPage.Clear;
  if NeedModel then
    DownloadPage.Add(ModelBaseUrl + FileName, FileName, '');
  if not WebView2Installed then
    DownloadPage.Add(WebView2Url, WebView2Setup, '');
  if not NeedModel and WebView2Installed then
    Exit;

  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      Downloaded := NeedModel;
      WebView2Downloaded := not WebView2Installed;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox('Un téléchargement a échoué :' + #13#10 + GetExceptionMessage + #13#10#13#10 +
          'L''installation continue : le modèle de transcription sera téléchargé par Echo après le premier appel.',
          mbInformation, MB_OK, IDOK);
      Downloaded := NeedModel and FileExists(ExpandConstant('{tmp}\') + FileName);
      WebView2Downloaded := FileExists(ExpandConstant('{tmp}\') + WebView2Setup);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ResultCode: Integer;
begin
  if CurStep = ssInstall then
  begin
    CopyLegacyModels;
    UninstallListenTeams;
  end;

  if (CurStep = ssPostInstall) and WebView2Downloaded and not WebView2Installed then
  begin
    WizardForm.StatusLabel.Caption := 'Installation de Microsoft Edge WebView2…';
    Exec(ExpandConstant('{tmp}\') + WebView2Setup, '/silent /install', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // The app can also enable automatic start from its settings.
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
end;
