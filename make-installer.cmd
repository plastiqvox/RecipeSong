@echo off
rem RecipeSong 인스톨러 만들기: 더블클릭하면 installer\out 에 RecipeSong-Setup-<버전>.exe 를 만들고 그 폴더를 연다
chcp 65001 >nul
cd /d "%~dp0"
powershell -NoProfile -ExecutionPolicy Bypass -File installer\build.ps1
if errorlevel 1 (
    echo.
    echo 인스톨러를 만들지 못했어요. 위 오류를 확인하세요.
    pause
    exit /b 1
)
explorer installer\out
rem explorer 는 성공해도 1 을 돌려주므로 성공으로 끝낸다
exit /b 0
