@echo off
chcp 65001 >nul
title DeepSeek Harness 更新
echo ============================================
echo   DeepSeek Harness 一键更新
echo ============================================
echo.
echo 更新期间请先关闭启动器窗口（否则代码文件可能被占用）。
echo.
echo [0/4] 检查代码目录是否为 git 仓库 ...
cd /d D:\deepseek-harness
git rev-parse --is-inside-work-tree >nul 2>&1
if errorlevel 1 (
    echo.
    echo [提示] 当前源码目录不是 git 仓库，无法用 git 自动更新。
    echo 请手动到 GitHub 下载最新源码包：
    echo   https://github.com/deepseek-ai/deepseek-harness
    echo 下载后解压覆盖到 D:\deepseek-harness 即可（你的数据在
    echo D:\dsh-launcher\home-clean，不受影响）。
    echo.
    echo 若希望以后用本脚本一键更新，可先执行：
    echo   git init ^&^& git remote add origin https://github.com/deepseek-ai/deepseek-harness.git
    pause
    exit /b 1
)
echo.
echo [1/4] 拉取最新代码 ...
git pull
if errorlevel 1 (
    echo.
    echo [失败] 拉取代码出错，请检查网络或 git 状态。
    pause
    exit /b 1
)
echo.
echo [2/4] 安装依赖 ...
call pnpm install
if errorlevel 1 (
    echo.
    echo [失败] 依赖安装出错。
    pause
    exit /b 1
)
echo.
echo [3/4] 构建 ...
call pnpm run build
if errorlevel 1 (
    echo.
    echo [失败] 构建出错。
    pause
    exit /b 1
)
echo.
echo ============================================
echo   更新完成！现在可以正常打开启动器使用。
echo   你的聊天记录与配置在 D:\dsh-launcher\home-clean，
echo   不受本次更新影响，无需备份。
echo ============================================
pause
