; AEC Audio Router のインストーラ。tools/installer/build-installer.ps1 から ISCC に渡して作る。
; ユーザーごとのインストール（管理者権限なし）で %LocalAppData%\Programs\AecAudioRouter に入れる。
; 中身は self-contained の publish（PublishDir）。.NET ランタイムは別に入れなくてよい。
; 設定（%LocalAppData%\AecAudioRouter\settings.json）はアンインストールでも消さない。

#ifndef AppVersion
  #define AppVersion "0.2.3"
#endif
#ifndef PublishDir
  #define PublishDir "..\.work\publish"
#endif
#ifndef OutputDir
  #define OutputDir "..\.work\installer"
#endif

[Setup]
; Updater.cs の UninstallKey と同じ値（変えない）
AppId={{84EEF39B-A016-4925-8D16-B80D564559E5}
AppName=AEC Audio Router
AppVersion={#AppVersion}
AppVerName=AEC Audio Router {#AppVersion}
AppPublisher=toru
AppPublisherURL=https://github.com/yamaotoru7/AEC-Audio-Router
DefaultDirName={localappdata}\Programs\AecAudioRouter
DefaultGroupName=AEC Audio Router
DisableProgramGroupPage=yes
DisableDirPage=auto
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir={#OutputDir}
OutputBaseFilename=AecAudioRouter-{#AppVersion}-setup
SetupIconFile=..\src\AecAudioRouter\Assets\AppIcon.ico
UninstallDisplayIcon={app}\AecAudioRouter.exe
LicenseFile=..\LICENSE
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes

[Languages]
Name: "japanese"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

; 更新では古い版のファイルを先に消す。上書きだけだと、新しい版で無くなった DLL やモデル
; （Rust 無しのビルドでの deepfilter.dll など）が残り、アプリがそれを読み込んでしまう。
; このアプリが入っているフォルダのときだけ、このアプリが置くものだけを消す。
[InstallDelete]
Type: files; Name: "{app}\*.dll"; Check: IsAppFolder
Type: filesandordirs; Name: "{app}\models"; Check: IsAppFolder
Type: filesandordirs; Name: "{app}\licenses"; Check: IsAppFolder

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\AEC Audio Router"; Filename: "{app}\AecAudioRouter.exe"
Name: "{autodesktop}\AEC Audio Router"; Filename: "{app}\AecAudioRouter.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\AecAudioRouter.exe"; Description: "{cm:LaunchProgram,AEC Audio Router}"; Flags: nowait postinstall skipifsilent
; アプリの更新（Updater.cs）は /VERYSILENT /RELAUNCH=1 で起動する。入れ終わったら起動し直す
Filename: "{app}\AecAudioRouter.exe"; Flags: nowait; Check: RelaunchAfterUpdate

[Code]
function IsAppFolder: Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\AecAudioRouter.exe'));
end;

function RelaunchAfterUpdate: Boolean;
begin
  Result := WizardSilent and (ExpandConstant('{param:RELAUNCH|0}') = '1');
end;
