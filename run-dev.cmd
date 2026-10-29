@echo off
rem RecipeSong 개발판 실행: 더블클릭하면 저장소 코드를 빌드해서 바로 켠다 (설치 없이)
rem 설치판과 같은 캐시·설정(%LOCALAPPDATA%\RecipeSong, %APPDATA%\RecipeSong)을 쓰므로 설치판이 켜져 있으면 먼저 닫는다.
rem 소리 분석은 저장소의 ml 폴더(ml\.venv)를 쓴다.
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -Command "if (Get-Process RecipeSong.Wpf -ErrorAction SilentlyContinue) { exit 1 }"
if errorlevel 1 (
    echo RecipeSong 이 이미 켜져 있어요. 같은 캐시를 서로 덮어쓰지 않게 먼저 닫고 다시 실행하세요.
    pause
    exit /b 1
)
dotnet run --project src\RecipeSong.Wpf
if errorlevel 1 (
    echo.
    echo 빌드하거나 실행하지 못했어요. 위 오류를 확인하세요.
    pause
    exit /b 1
)
exit /b 0
