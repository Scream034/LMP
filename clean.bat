@echo off
chcp 65001 >nul
title LMP - Clean

echo.
echo ╔══════════════════════════════════════╗
echo ║     LMP - Clean Project              ║
echo ╚══════════════════════════════════════╝
echo.

echo Removing bin...
if exist "bin" rmdir /s /q "bin"

echo Removing obj...
if exist "obj" rmdir /s /q "obj"

echo Removing publish...
if exist "publish" rmdir /s /q "publish"

echo Removing archives...
del /q "*.7z" 2>nul

echo.
echo ✓ Clean complete!
echo.

pause