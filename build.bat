@echo off
chcp 65001 >nul
setlocal EnableDelayedExpansion

title LMP Build System

echo.
echo ╔══════════════════════════════════════════════╗
echo ║           LMP Build System                   ║
echo ╚══════════════════════════════════════════════╝
echo.

set MODE=%1
set PAUSE=1

:: Если передан второй параметр nopause — не ставим паузу в конце
if /i "%2"=="nopause" set PAUSE=0
if /i "%MODE%"=="" set MODE=debug

:: Git версия
where git >nul 2>&1
if %ERRORLEVEL% NEQ 0 (
    set COMMIT_COUNT=0
    set GIT_HASH=local
) else (
    for /f %%i in ('git rev-list --count HEAD 2^>nul') do set COMMIT_COUNT=%%i
    for /f %%i in ('git rev-parse --short=7 HEAD 2^>nul') do set GIT_HASH=%%i
    if "!COMMIT_COUNT!"=="" set COMMIT_COUNT=0
    if "!GIT_HASH!"=="" set GIT_HASH=local
)

set BASE_VERSION=1.0.!COMMIT_COUNT!
set FULL_VERSION=!BASE_VERSION!+!GIT_HASH!

echo Mode: %MODE%
echo Version: !BASE_VERSION!  (Hash: !GIT_HASH!)
echo.

if /i "%MODE%"=="clean" goto :CLEAN
if /i "%MODE%"=="debug" goto :DEBUG
if /i "%MODE%"=="optimized" goto :OPTIMIZED
if /i "%MODE%"=="release" goto :RELEASE
if /i "%MODE%"=="publish" goto :PUBLISH
if /i "%MODE%"=="restore" goto :RESTORE

echo Unknown mode "%MODE%".
echo Available: debug ^| optimized ^| release ^| publish ^| restore ^| clean
goto :END

:RESTORE
echo Performing full restore...
dotnet restore --force --force-evaluate
goto :SUCCESS

:DEBUG
echo Building Debug...
dotnet build LMP.csproj -c Debug -p:Version=!BASE_VERSION! -p:InformationalVersion=!FULL_VERSION!
goto :CHECK

:OPTIMIZED
echo Building Optimized Debug...
dotnet build LMP.csproj -c Debug ^
    -p:Version=!BASE_VERSION! ^
    -p:InformationalVersion=!FULL_VERSION!-opt ^
    -p:Optimize=true ^
    -p:DebugType=embedded
goto :CHECK

:RELEASE
echo Building Release...
dotnet build LMP.csproj -c Release ^
    -p:Version=!BASE_VERSION! ^
    -p:DebugType=None ^
    -p:DebugSymbols=false
goto :CHECK

:PUBLISH
echo Publishing self-contained Release...

if exist "publish" rmdir /s /q "publish"

dotnet publish LMP.csproj -c Release -r win-x64 --self-contained true ^
    -p:Version=!BASE_VERSION! ^
    -p:PublishSingleFile=true ^
    -p:PublishReadyToRun=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -p:DebugType=None ^
    -p:DebugSymbols=false ^
    -o "./publish"

if %ERRORLEVEL% NEQ 0 (
    echo ✗ dotnet publish failed!
    goto :FAIL
)

:: Удаляем мусор
del /q "publish\*.xml" 2>nul
del /q "publish\*.pdb" 2>nul
del /q "publish\*.config" 2>nul

echo ✓ Publish completed successfully into ./publish folder
goto :END

:CLEAN
echo Cleaning project...
if exist "bin" rmdir /s /q "bin"
if exist "obj" rmdir /s /q "obj"
if exist "publish" rmdir /s /q "publish"
del /q "*.7z" 2>nul
echo ✓ Clean complete!
goto :END

:CHECK
if %ERRORLEVEL% NEQ 0 goto :FAIL

:SUCCESS
echo.
echo ✓ Build successful!
goto :END

:FAIL
echo.
echo ✗ Build failed!
exit /b 1

:END
if %PAUSE%==1 (
    echo.
    pause
)