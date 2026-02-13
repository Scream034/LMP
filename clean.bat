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

echo Removing test bin/obj...
if exist "LMP.Tests\bin" rmdir /s /q "LMP.Tests\bin"
if exist "LMP.Tests\obj" rmdir /s /q "LMP.Tests\obj"

echo Removing .vs folder...
if exist ".vs" rmdir /s /q ".vs"

echo Removing user-specific files...
del /q "*.user" 2>nul
del /q "*.suo" 2>nul

echo Removing NuGet cache...
if exist ".nuget" rmdir /s /q ".nuget"

echo Removing TestResults...
if exist "TestResults" rmdir /s /q "TestResults"

echo.
echo ✓ Clean complete!
echo.

pause