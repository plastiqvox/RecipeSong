# RecipeSong 인스톨러 만들기: 앱(.NET 런타임 포함) · ml 코드 · uv.exe 를 모아 Inno Setup 으로 묶는다.
# 결과: installer\out\RecipeSong-Setup-<버전>.exe
# 필요: .NET 9 SDK, Inno Setup 6 (winget install JRSoftware.InnoSetup), uv
param(
    [string]$Iscc = "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$out = Join-Path $PSScriptRoot 'out'
$stage = Join-Path $out 'stage'

if (-not (Test-Path $Iscc)) {
    $Iscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (-not (Test-Path $Iscc)) { throw 'Inno Setup 6 이 없습니다: winget install JRSoftware.InnoSetup' }
}

# 버전은 src\Directory.Build.props 하나에서 (앱 설정 창 표시와 같은 값)
[xml]$props = Get-Content (Join-Path $root 'src\Directory.Build.props')
$version = "$($props.Project.PropertyGroup.Version)".Trim()

if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }

# 앱: 받는 사람이 .NET 을 따로 설치하지 않게 런타임을 같이 넣는다
dotnet publish (Join-Path $root 'src\RecipeSong.Wpf') -c Release -r win-x64 --self-contained true -o (Join-Path $stage 'app') --nologo
if ($LASTEXITCODE) { throw 'dotnet publish 실패' }

# ml: 코드와 의존성 목록만. data(학습 결과)와 .venv(Python 환경)는 설치한 PC 에서 만든다
$ml = New-Item -ItemType Directory -Force (Join-Path $stage 'app\ml')
Copy-Item (Join-Path $root 'ml\*.py'), (Join-Path $root 'ml\pyproject.toml'), (Join-Path $root 'ml\uv.lock') $ml

# uv: 따로 설치하지 않아도 소리 분석 환경을 만들 수 있게 앱 옆에 둔다 (MIT / Apache-2.0)
$uv = (Get-Command uv.exe -ErrorAction SilentlyContinue).Source
if (-not $uv) { $uv = Join-Path $env:USERPROFILE '.local\bin\uv.exe' }
if (-not (Test-Path $uv)) { throw 'uv.exe 를 찾지 못했습니다' }
Copy-Item $uv (Join-Path $stage 'app\uv.exe')

& $Iscc "/DAppVersion=$version" "/DStage=$(Join-Path $stage 'app')" "/DRoot=$root" "/O$out" (Join-Path $PSScriptRoot 'RecipeSong.iss')
if ($LASTEXITCODE) { throw 'Inno Setup 실패' }
Get-Item (Join-Path $out "RecipeSong-Setup-$version.exe")
