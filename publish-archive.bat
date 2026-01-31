@echo off
chcp 65001 >nul
title LMP - Publish + Archive

echo.
echo ╔══════════════════════════════════════╗
echo ║     LMP - Publish + 7z Archive       ║
echo ╚══════════════════════════════════════╝
echo.

set OUTPUT=publish
set RUNTIME=win-x64
set ARCHIVE_NAME=LMP-Release.7z

:: Проверка 7z
where 7z >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    echo ✗ 7-Zip not found in PATH!
    echo   Install from: https://www.7-zip.org/
    pause
    exit /b 1
)

echo [1/4] Cleaning...
if exist "%OUTPUT%" rmdir /s /q "%OUTPUT%"
if exist "%ARCHIVE_NAME%" del /q "%ARCHIVE_NAME%"

echo [2/4] Publishing...
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

echo [3/4] Cleaning junk...
del /q "%OUTPUT%\*.xml" 2>nul
del /q "%OUTPUT%\*.pdb" 2>nul
del /q "%OUTPUT%\*.config" 2>nul

echo [4/4] Creating archive...
cd "%OUTPUT%"
7z a -t7z -mx=9 -mfb=273 -ms=on "..\%ARCHIVE_NAME%" . >nul
cd ..

:: Размер архива
for %%A in ("%ARCHIVE_NAME%") do set SIZE=%%~zA
set /a SIZE_MB=%SIZE% / 1048576

echo.
echo ╔══════════════════════════════════════╗
echo ║  ✓ Archive created!                  ║
echo ╠══════════════════════════════════════╣
echo ║  File: %ARCHIVE_NAME%                ║
echo ║  Size: ~%SIZE_MB% MB                       ║
echo ╚══════════════════════════════════════╝
echo.

pause