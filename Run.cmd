@echo off
chcp 65001 >nul 2>&1
echo === OPC DA Client 启动 ===
echo.

REM 检查是否以管理员运行
net session >nul 2>&1
if errorlevel 1 (
    echo 检测到非管理员权限，正在请求提升...
    echo.
    powershell -Command "Start-Process '%~f0' -Verb RunAs"
    exit /b
)

echo [管理员模式]
echo.

REM 查找可执行文件
set "EXE_PATH="

if exist "%~dp0bin\x86\Release\OpcDaClient.exe" (
    set "EXE_PATH=%~dp0bin\x86\Release\OpcDaClient.exe"
    echo 使用 Release 版本
) else if exist "%~dp0bin\x86\Debug\OpcDaClient.exe" (
    set "EXE_PATH=%~dp0bin\x86\Debug\OpcDaClient.exe"
    echo 使用 Debug 版本
) else (
    echo 未找到 OpcDaClient.exe
    echo 请先运行 Build.cmd 进行构建
    pause
    exit /b 1
)

echo 启动: %EXE_PATH%
echo.
"%EXE_PATH%"
pause
