@echo off
setlocal
set DOTNET=%USERPROFILE%\.dotnet\dotnet.exe
set ROOT=%~dp0

echo [1/3] Building app (Release)...
"%DOTNET%" build "%ROOT%src\RonghuiEarbuds.App" -c Release --nologo -v q
if errorlevel 1 (
    echo App build FAILED.
    exit /b 1
)

echo [2/3] Publishing installer as single exe (embeds app payload)...
REM framework-dependent：自包含单文件达 153MB（WPF 无法裁剪），改为依赖系统引导安装 .NET 运行时
"%DOTNET%" publish "%ROOT%setup\RonghuiEarbuds.Setup" -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -p:DebugType=none -p:DebugSymbols=false -o "%ROOT%dist" --nologo -v q
if errorlevel 1 (
    echo Installer publish FAILED.
    exit /b 1
)

echo.
echo Done. Installer: %ROOT%dist\RonghuiEarbuds.Setup.exe
echo Ship this single exe - the app payload is embedded inside.
