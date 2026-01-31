@echo off
chcp 65001 >nul
title LMP - Release Build

echo.
echo ╔══════════════════════════════════════╗
echo ║     LMP - Release Build              ║
echo ╚══════════════════════════════════════╝
echo.

dotnet build LMP.csproj -c Release -p:DebugType=None -p:DebugSymbols=false

if %ERRORLEVEL% EQU 0 (
    echo.
    echo ✓ Build successful!
    echo   Output: bin\Release\net10.0\
) else (
    echo.
    echo ✗ Build failed!
)

echo.
pause