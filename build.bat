@echo off
chcp 65001 >nul
title DeepSeek Harness 一键编译
cd /d "%~dp0"

echo 正在检查 .NET SDK ...
dotnet --version >nul 2>&1
if errorlevel 1 (
    echo.
    echo [错误] 未找到 .NET SDK！Windows 不自带，需要先安装 .NET 7 SDK：
    echo   https://dotnet.microsoft.com/download/dotnet/7.0
    echo 安装时勾选 ".NET Desktop Development" 或装完运行：
    echo   dotnet workload install desktop
    pause
    exit /b 1
)

echo 正在编译 ...
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true
if errorlevel 1 (
    echo.
    echo [失败] 编译出错，请查看上方错误信息。
    pause
    exit /b 1
)

copy /y "bin\Release\net7.0-windows\win-x64\publish\DshLauncher.exe" "%~dp0dsh-launcher.exe" >nul
if errorlevel 1 (
    echo [提示] 复制产物到根目录失败（可能 dsh-launcher.exe 正被占用，请先关闭运行中的启动器）。
    pause
    exit /b 1
)

echo.
echo ============================================
echo   编译完成！
echo   产物：%~dp0dsh-launcher.exe
echo ============================================
pause
