@echo off
chcp 65001 >nul
setlocal

cd /d "%~dp0"

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)

if not exist "%CSC%" (
    echo [错误] 未找到 Windows 自带的 .NET Framework 4.x 编译器 csc.exe！
    pause
    exit /b 1
)

echo [信息] 使用编译器: %CSC%
echo [信息] 正在编译 Phone2WinMic.exe (AnyCPU, 兼容 32/64 位 Windows)...

"%CSC%" /nologo /target:winexe /platform:anycpu /optimize+ /codepage:65001 /utf8output ^
    /out:"Phone2WinMic.exe" ^
    /r:System.dll ^
    /r:System.Core.dll ^
    /r:System.Drawing.dll ^
    /r:System.Windows.Forms.dll ^
    "Phone2WinMic.cs"

if errorlevel 1 (
    echo [错误] 编译失败，请查看上方报错信息。
    pause
    exit /b 1
)

echo [成功] 已生成: %~dp0Phone2WinMic.exe
echo [提示] 接下来可运行 setup_autostart_and_firewall.bat 配置防火墙与开机自启。
pause
