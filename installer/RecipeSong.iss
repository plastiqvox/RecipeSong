; RecipeSong 인스톨러 (Inno Setup 6). 직접 컴파일하지 말고 installer\build.ps1 로 만든다 (버전·스테이지 경로를 넘겨준다).
; 사용자 폴더(%LOCALAPPDATA%\Programs\RecipeSong)에 관리자 권한 없이 설치한다:
; 소리 분석 서비스가 ml\data 에 학습 결과를 쓰고 ml\.venv 에 Python 환경을 만들어서, Program Files 에는 둘 수 없다.

#ifndef AppVersion
  #error build.ps1 로 컴파일하세요 (AppVersion 이 없습니다)
#endif

[Setup]
AppId={{6F0B7C1E-3A52-4C8E-9D1F-5B2A7E4C9A31}
AppName=RecipeSong
AppVersion={#AppVersion}
AppVerName=RecipeSong {#AppVersion}
AppPublisher=RecipeSong
DefaultDirName={autopf}\RecipeSong
DefaultGroupName=RecipeSong
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=RecipeSong-Setup-{#AppVersion}
SetupIconFile={#Root}\src\RecipeSong.Wpf\Assets\app.ico
UninstallDisplayIcon={app}\RecipeSong.Wpf.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
; 업데이트 설치 때 켜 둔 앱을 닫는다
CloseApplications=yes
; Inno Setup 6.5+ 는 RedirectionGuard 를 켜고, 설치 중에 띄운 프로세스(uv sync, 마지막의 앱 실행)가 이를 물려받는다.
; 그러면 uv 가 관리하는 Python 정션(cpython-3.12-… → cpython-3.12.x-…)을 따라가지 못해 소리 분석 서비스가 뜨지 않는다 (os error 448).
; 관리자 권한 없이 사용자 폴더에만 설치하므로 막을 권한 상승이 없다
RedirectionGuard=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[CustomMessages]
korean.MlEnv=소리 분석 환경 설치 (Python · PyTorch, 약 3GB 내려받기, 인터넷 필요)
english.MlEnv=Install the sound analysis environment (Python · PyTorch, about 3 GB download, needs internet)
korean.MlEnvGroup=소리 분석:
english.MlEnvGroup=Sound analysis:
korean.MlEnvStatus=소리 분석 환경을 설치하는 중... (몇 분 걸려요)
english.MlEnvStatus=Installing the sound analysis environment... (takes a few minutes)
korean.Launch=RecipeSong 실행
english.Launch=Launch RecipeSong

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
; 건너뛰어도 앱이 처음 소리 분석 서비스를 띄울 때 같은 환경을 만든다 (화면 없이 기다리게 되므로 여기서 하는 게 낫다)
Name: "mlenv"; Description: "{cm:MlEnv}"; GroupDescription: "{cm:MlEnvGroup}"

[Files]
Source: "{#Stage}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Root}\installer\THIRD-PARTY.txt"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
; 학습 결과·캐시. 업데이트·제거 때 지우지 않는다 (다시 만들려면 처음 학습부터 해야 한다)
Name: "{app}\ml\data"; Flags: uninsneveruninstall

[Icons]
Name: "{autoprograms}\RecipeSong"; Filename: "{app}\RecipeSong.Wpf.exe"
Name: "{autodesktop}\RecipeSong"; Filename: "{app}\RecipeSong.Wpf.exe"; Tasks: desktopicon

[Run]
; 콘솔 창을 보이게 돌린다 (내려받기 진행이 보이게). 실패하면 창이 바로 닫히지 않게 pause
Filename: "{cmd}"; Parameters: "/c """"{app}\uv.exe"" sync --no-dev --directory ""{app}\ml"" || pause"""; StatusMsg: "{cm:MlEnvStatus}"; Tasks: mlenv
Filename: "{app}\RecipeSong.Wpf.exe"; Description: "{cm:Launch}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; Python 환경은 수 GB 이고 다시 만들 수 있어서 같이 지운다 (학습 결과 ml\data 는 남긴다)
Type: filesandordirs; Name: "{app}\ml\.venv"
Type: filesandordirs; Name: "{app}\ml\__pycache__"
