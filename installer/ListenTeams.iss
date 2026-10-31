; ListenTeams installer (Inno Setup 6.3+).
; Build: dotnet publish src\ListenTeams -c Release -r win-x64 --self-contained -o publish
;        ISCC.exe /DAppVersion=1.0.0 installer\ListenTeams.iss
; Installs for the current user only (no administrator rights needed), with everything
; required: the app, its .NET runtime, and the speech recognition model.

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "ListenTeams"
#define AppExe "ListenTeams.exe"

[Setup]
AppId={{7C1D2CBB-C745-4AF5-9551-5CA291C68AF1}
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
OutputBaseFilename=ListenTeams-Setup
SetupIconFile=..\assets\listenteams.ico
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
Name: "autostart"; Description: "Lancer ListenTeams à l'ouverture de session (recommandé : il attend vos appels en arrière-plan)"; GroupDescription: "Démarrage :"
Name: "desktopicon"; Description: "Créer une icône sur le Bureau"; GroupDescription: "Raccourcis :"; Flags: unchecked

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
Filename: "{app}\{#AppExe}"; Description: "Lancer ListenTeams maintenant"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopListenTeams"

[UninstallDelete]
; Logs and models downloaded by the app. Recordings (Documents\ListenTeams) and settings are kept.
Type: filesandordirs; Name: "{localappdata}\{#AppName}"

[Code]
const
  ModelBaseUrl = 'https://huggingface.co/ggerganov/whisper.cpp/resolve/main/';

var
  AppsPage: TInputOptionWizardPage;
  ModelPage: TInputOptionWizardPage;
  DownloadPage: TDownloadWizardPage;
  Downloaded: Boolean;

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

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  AppsPage := CreateInputOptionPage(wpSelectTasks,
    'Applications à enregistrer',
    'Quels appels ListenTeams doit-il enregistrer automatiquement ?',
    'Cochez Teams, Discord ou les deux. Vous pourrez changer ce choix à tout moment ' +
    '(clic droit sur l''icône ListenTeams → Applications à enregistrer).',
    False, False);
  AppsPage.Add('Microsoft Teams (application de bureau)');
  AppsPage.Add('Discord (application de bureau, salons vocaux et appels)');
  AppsPage.Values[0] := True;
  AppsPage.Values[1] := True;

  ModelPage := CreateInputOptionPage(AppsPage.ID,
    'Modèle de transcription',
    'Choisissez la précision de la transcription des appels.',
    'La transcription est faite sur ce PC, après chaque appel. Le modèle est téléchargé une seule fois ; ' +
    'vous pourrez en changer plus tard dans les paramètres de ListenTeams.',
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
begin
  Result := True;
  if (CurPageID = AppsPage.ID) and not (AppsPage.Values[0] or AppsPage.Values[1]) then
  begin
    MsgBox('Choisissez au moins une application (Teams ou Discord).', mbError, MB_OK);
    Result := False;
    Exit;
  end;
  if CurPageID <> wpReady then
    Exit;

  Downloaded := False;
  FileName := ModelFile('');
  if (FileName = '') or FileExists(ExpandConstant('{app}\models\') + FileName) then
    Exit;

  DownloadPage.Clear;
  DownloadPage.Add(ModelBaseUrl + FileName, FileName, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      Downloaded := True;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox('Le modèle de transcription n''a pas pu être téléchargé :' + #13#10 + GetExceptionMessage + #13#10#13#10 +
          'L''installation continue : ListenTeams le téléchargera automatiquement après le premier appel.',
          mbInformation, MB_OK, IDOK);
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Apps: String;
begin
  // ListenTeams reads this choice at its next start, then removes the value.
  if CurStep = ssPostInstall then
  begin
    Apps := '';
    if AppsPage.Values[0] then
      Apps := 'Teams';
    if AppsPage.Values[1] then
    begin
      if Apps <> '' then
        Apps := Apps + ',';
      Apps := Apps + 'Discord';
    end;
    RegWriteStringValue(HKEY_CURRENT_USER, 'Software\{#AppName}', 'Apps', Apps);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  // The app can also enable automatic start from its settings.
  if CurUninstallStep = usPostUninstall then
  begin
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', '{#AppName}');
    RegDeleteKeyIncludingSubkeys(HKEY_CURRENT_USER, 'Software\{#AppName}');
  end;
end;
