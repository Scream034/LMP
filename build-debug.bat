@echo off
chcp 65001 >nul
title LMP - Debug Build

echo.
echo ╔══════════════════════════════════════╗
echo ║     LMP - Debug Build                ║
echo ╚══════════════════════════════════════╝
echo.

dotnet build LMP.csproj -c Debug

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ✓ Build successful!
    echo   Output: bin\Debug\net10.0\
) else (
    echo.
    echo ✗ Build failed!
)

echo.
pause