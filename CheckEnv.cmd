@echo off
chcp 65001 >nul 2>&1
echo ============================================
echo   OPC DA Client 环境检查 (Win7 32位)
echo ============================================
echo.

REM 1. 系统信息
echo [1] 操作系统
for /f "tokens=2*" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\Windows NT\CurrentVersion" /v ProductName 2^>nul ^| findstr ProductName') do echo     %%b
for /f "tokens=*" %%a in ('wmic os get OSArchitecture /value 2^>nul ^| findstr =') do echo     %%a
echo.

REM 2. .NET Framework 版本
echo [2] .NET Framework
set "NETFX_OK=0"
for /f "tokens=2*" %%a in ('reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release 2^>nul ^| findstr Release') do (
    set "NETFX_RELEASE=%%b"
    set "NETFX_OK=1"
)
if "%NETFX_OK%"=="1" (
    echo     已安装 .NET Framework 4.x (Release: %NETFX_RELEASE%)
    REM 379893 = 4.5.2, 394802 = 4.6.2, 461808 = 4.7.2
    if %NETFX_RELEASE% GEQ 379893 (
        echo     [OK] 版本 >= 4.5.2，满足要求
    ) else (
        echo     [警告] 版本低于 4.5.2，请更新
        echo     下载: https://dotnet.microsoft.com/download/dotnet-framework
    )
) else (
    echo     [错误] 未安装 .NET Framework 4.x
    echo     请安装 .NET Framework 4.5.2
)
echo.

REM 3. OPC Core Components
echo [3] OPC Core Components
reg query "HKCR\CLSID\{28E68F91-8D75-11D1-8DC3-3C302A000000}" >nul 2>&1
if errorlevel 1 (
    echo     [错误] 未安装 OPC Core Components
    echo     请安装 OPC Core Components (x86 版本)
) else (
    echo     [OK] OPCAutomation COM 已注册
)
echo.

REM 4. 已注册的 OPC 服务器
echo [4] 已注册的 OPC DA 服务器
set "OPC_FOUND=0"

REM 检查常见 OPC 服务器
for %%S in (
    "Matrikon.OPC.Simulation.1"
    "Matrikon.OPC.Simulation"
    "KEPware.KEPServerEx.V6"
    "KEPware.KEPServerEx.V5"
    "Schneider-Aut.OFS"
    "RSLinx OPC Server"
    "Graybox.Simulator.1"
    "OPC.SimaticHMI.CoRtHmiRTm.1"
    "XWOpcDaServer.SimpleServer"
) do (
    reg query "HKCR\%%~S\CLSID" >nul 2>&1
    if not errorlevel 1 (
        echo     [发现] %%~S
        set "OPC_FOUND=1"
    )
)

if "%OPC_FOUND%"=="0" (
    echo     [警告] 未发现已注册的 OPC DA 服务器
    echo     请安装 OPC DA 服务器 (如 Matrikon OPC Simulation Server)
)
echo.

REM 5. DCOM 状态
echo [5] DCOM 服务
sc query RpcSs | findstr "RUNNING" >nul 2>&1
if errorlevel 1 (
    echo     [警告] RPC 服务未运行，DCOM 可能不可用
) else (
    echo     [OK] RPC 服务运行中
)
echo.

REM 6. 管理员权限
echo [6] 管理员权限
net session >nul 2>&1
if errorlevel 1 (
    echo     [警告] 当前非管理员权限
    echo     运行 OPC 客户端需要管理员权限
) else (
    echo     [OK] 当前为管理员权限
)
echo.

echo ============================================
echo   检查完成
echo ============================================
pause
