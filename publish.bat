@echo off
chcp 65001 >nul
title LMP - Publish

echo.
echo ╔══════════════════════════════════════╗
echo ║     LMP - Publish (Self-Contained)   ║
echo ╚══════════════════════════════════════╝
echo.

set OUTPUT=publish
set RUNTIME=win-x64

echo [1/3] Cleaning previous build...
if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"

echo [2/3] Publishing...
dotnet publish LMP.csproj ^
    -c Release ^
    -r %RUNTIME% ^
    --self-contained true ^
    -p:PublishReadyToRun=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o "%OUTPUT%"

if %ERRORLEVEL% NEQ 0 (
    echo.
    echo ✗ Publish failed!
    pause
    exit /b 1
)

echo [3/3] Cleaning junk files...
del /q "%OUTPUT%\*.xml" 2>nul
del /q "%OUTPUT%\*.pdb" 2>nul
del /q "%OUTPUT%\*.config" 2>nul

:: Подсчёт размера
for /f "tokens=3" %%a in ('dir "%OUTPUT%" /-c 2^>nul ^| find "File(s)"') do set SIZE=%%a
set /a SIZE_MB=%SIZE:~0,-6%

echo.
echo ╔══════════════════════════════════════╗
echo ║  ✓ Publish complete!                 ║
echo ╠══════════════════════════════════════╣
echo ║  Output: .\%OUTPUT%\                   ║
echo ║  Run:    %OUTPUT%\LMP.exe              ║
echo ╚══════════════════════════════════════╝
echo.

pause