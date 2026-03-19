@echo off
chcp 65001 >nul 2>&1
echo === OPC DA Client 构建脚本 (Win7 32位兼容) ===
echo.

REM 查找 MSBuild（兼容 32位/64位 Windows）
set "MSBUILD_PATH="

REM === 优先查找 VS 2019 ===
REM 32位 Windows: ProgramFiles 直接就是 32位路径
REM 64位 Windows: ProgramFiles(x86) 是 32位路径
if defined ProgramFiles(x86) (
    set "VS_ROOT=%ProgramFiles(x86)%\Microsoft Visual Studio"
) else (
    set "VS_ROOT=%ProgramFiles%\Microsoft Visual Studio"
)

for %%V in (2019 2017) do (
    for %%E in (Enterprise Professional Community BuildTools) do (
        if exist "%VS_ROOT%\%%V\%%E\MSBuild\Current\Bin\MSBuild.exe" (
            set "MSBUILD_PATH=%VS_ROOT%\%%V\%%E\MSBuild\Current\Bin\MSBuild.exe"
            echo 找到 MSBuild: VS %%V %%E
            goto :found
        )
        REM VS 2017 的 MSBuild 路径不同
        if exist "%VS_ROOT%\%%V\%%E\MSBuild\15.0\Bin\MSBuild.exe" (
            set "MSBUILD_PATH=%VS_ROOT%\%%V\%%E\MSBuild\15.0\Bin\MSBuild.exe"
            echo 找到 MSBuild: VS %%V %%E
            goto :found
        )
    )
)

REM === 回退: MSBuild 14.0 (VS 2015) ===
if defined ProgramFiles(x86) (
    set "MSBUILD14=%ProgramFiles(x86)%\MSBuild\14.0\Bin\MSBuild.exe"
) else (
    set "MSBUILD14=%ProgramFiles%\MSBuild\14.0\Bin\MSBuild.exe"
)
if exist "%MSBUILD14%" (
    set "MSBUILD_PATH=%MSBUILD14%"
    echo 找到 MSBuild 14.0 (VS 2015)
    goto :found
)

REM === 最终回退: .NET Framework 自带 MSBuild ===
set "NETFX_MSBUILD=%SystemRoot%\Microsoft.NET\Framework\v4.0.30319\MSBuild.exe"
if exist "%NETFX_MSBUILD%" (
    set "MSBUILD_PATH=%NETFX_MSBUILD%"
    echo 找到 .NET Framework MSBuild
    echo 注意: .NET Framework MSBuild 可能不支持 SDK 风格项目
    echo 建议安装 Visual Studio 2017 或更高版本
    goto :found
)

echo 错误: 未找到 MSBuild!
echo 请安装以下任意一项:
echo   - Visual Studio 2019 (推荐, 最后支持 Win7 的版本)
echo   - Visual Studio 2017
echo   - Build Tools for Visual Studio
pause
exit /b 1

:found
echo 使用: %MSBUILD_PATH%
echo.

REM 检查 .NET Framework 版本
echo 检查 .NET Framework...
reg query "HKLM\SOFTWARE\Microsoft\NET Framework Setup\NDP\v4\Full" /v Release >nul 2>&1
if errorlevel 1 (
    echo 警告: 未检测到 .NET Framework 4.5+
    echo 请安装 .NET Framework 4.5.2 或更高版本
    echo 下载地址: https://dotnet.microsoft.com/download/dotnet-framework
    pause
    exit /b 1
)
echo .NET Framework 4.5+ 已安装

REM 检查 OPC Core Components
echo 检查 OPC Core Components...
reg query "HKCR\CLSID\{28E68F91-8D75-11D1-8DC3-3C302A000000}" >nul 2>&1
if errorlevel 1 (
    echo 警告: 未检测到 OPC Core Components
    echo 请安装 OPC Core Components (x86 版本)
    echo.
) else (
    echo OPC Core Components 已安装
)

echo.
echo 开始构建 Release 版本...
"%MSBUILD_PATH%" OpcDaClient.sln /p:Configuration=Release /p:Platform=x86 /v:minimal /restore
if errorlevel 1 (
    echo.
    echo 构建失败! 常见原因:
    echo   1. 未安装 OPC Core Components (x86)
    echo   2. MSBuild 版本过低 (需要 VS 2017+)
    echo   3. .NET Framework 4.5.2 未安装
    pause
    exit /b 1
)

echo.
echo === 构建成功! ===
echo.
echo 输出文件: bin\x86\Release\OpcDaClient.exe
echo.
echo 运行前请确保:
echo   1. 已安装 OPC Core Components (x86)
echo   2. 至少安装一个 OPC DA 服务器
echo   3. 以管理员身份运行 OpcDaClient.exe
echo.
pause
