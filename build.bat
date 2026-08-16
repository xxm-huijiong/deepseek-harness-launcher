@echo off
setlocal
title dsh-launcher build script

rem ============================================================
rem  dsh-launcher build script  (ASCII-safe, any locale)
rem
rem  Usage:
rem    build.bat          -> framework-dependent single-file exe
rem                         (needs .NET 7 Desktop Runtime on the
rem                          target machine; output ~1.6 MB)
rem    build.bat self     -> self-contained single-file exe
rem                         (bundles the runtime, runs on any
rem                          machine without .NET; output ~70 MB)
rem
rem  Output: dsh-launcher.exe (or dsh-launcher-standalone.exe)
rem          in this folder - that is the file to run.
rem  NOTE: bin\Release\...\DshLauncher.exe is an intermediate
rem        build artifact, NOT the runnable product.
rem ============================================================

set SELF=0
if /i "%~1"=="self" set SELF=1

echo [1/3] checking .NET SDK ...
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo [ERROR] .NET SDK not found. Install .NET 7 SDK from:
    echo   https://dotnet.microsoft.com/download/dotnet/7.0
    echo   Select the ".NET Desktop Development" workload during install.
    pause
    exit /b 1
)

echo [2/3] publishing ...
if %SELF%==1 (
    dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
) else (
    dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
)
if errorlevel 1 (
    echo [ERROR] publish failed. See the messages above.
    pause
    exit /b 1
)

echo [3/3] copying result ...
if %SELF%==1 (
    copy /y "bin\Release\net7.0-windows\win-x64\publish\DshLauncher.exe" "dsh-launcher-standalone.exe" >nul
) else (
    copy /y "bin\Release\net7.0-windows\win-x64\publish\DshLauncher.exe" "dsh-launcher.exe" >nul
)
if errorlevel 1 (
    echo [ERROR] copy failed. Close the running launcher first, then retry.
    pause
    exit /b 1
)

echo.
echo ============================================
echo   Build OK.
if %SELF%==1 (
    echo   Output: %~dp0dsh-launcher-standalone.exe
    echo   Self-contained: runs without installing .NET.
) else (
    echo   Output: %~dp0dsh-launcher.exe
    echo   Framework-dependent: target needs .NET 7 Desktop Runtime.
    echo   For machines without .NET, run:  build.bat self
)
echo ============================================
pause
