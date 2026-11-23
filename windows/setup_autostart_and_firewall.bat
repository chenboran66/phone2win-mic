@echo off
chcp 65001 >nul
setlocal

cd /d "%~dp0"
set "EXE_PATH=%~dp0Phone2WinMic.exe"

if not exist "%EXE_PATH%" (
    echo [信息] 未发现 Phone2WinMic.exe，正在自动编译...
    call "%~dp0build.bat"
    if not exist "%EXE_PATH%" (
        echo [错误] 编译未产出可执行文件，脚本终止。
        pause
        exit /b 1
    )
)

echo [1/3] 正在配置当前用户开机静默自启 (HKCU\...\Run)...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v "Phone2WinMic" /t REG_SZ /d "\"%EXE_PATH%\" --minimized" /f >nul
if errorlevel 1 (
    echo [警告] 写入开机自启注册表失败。
) else (
    echo [成功] 已启用开机自动后台启动。
)

net session >nul 2>&1
if %errorlevel% neq 0 (
    echo [2/3] 配置 Windows 防火墙需要管理员权限，正在请求 UAC 提权...
    powershell -NoProfile -Command "Start-Process -FilePath 'netsh' -ArgumentList 'advfirewall firewall delete rule name=\"Phone2Win-Mic UDP 54321\"' -Verb RunAs -WindowStyle Hidden -Wait" >nul 2>&1
    powershell -NoProfile -Command "Start-Process -FilePath 'netsh' -ArgumentList 'advfirewall firewall add rule name=\"Phone2Win-Mic UDP 54321\" dir=in action=allow protocol=UDP localport=54321 profile=any' -Verb RunAs -WindowStyle Hidden -Wait"
    echo [信息] 已请求添加 UDP 54321 入站放行规则。
) else (
    echo [2/3] 正在配置 Windows 防火墙放行 UDP 54321...
    netsh advfirewall firewall delete rule name="Phone2Win-Mic UDP 54321" >nul 2>&1
    netsh advfirewall firewall add rule name="Phone2Win-Mic UDP 54321" dir=in action=allow protocol=UDP localport=54321 profile=any >nul
    if errorlevel 1 (
        echo [警告] 防火墙规则添加失败，请手动放行 UDP 54321。
    ) else (
        echo [成功] 已放行 Windows 防火墙入站 UDP 54321。
    )
)

echo [3/3] 正在后台启动 Phone2WinMic.exe ...
start "" "%EXE_PATH%"

echo.
echo [完成] Windows 守护端已就绪！请查看桌面右下角系统托盘麦克风图标。
echo       (双击托盘图标可打开实时电平与局域网 IP 监控窗口)
pause
