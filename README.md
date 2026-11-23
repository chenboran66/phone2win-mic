# Phone2Win-Mic

将 Android 手机麦克风通过局域网 Wi-Fi 低延迟传输到 Windows，作为电脑无线麦克风使用。

## 架构概览

```
[Android 手机]
  AudioRecord (48kHz/16kHz 16-bit Mono PCM)
    -> JNI C 音频处理 (噪声门限 / 增益 / 软限幅)
    -> ForegroundService + WakeLock + WifiLock (支持关屏运行)
    -> UDP 54321 (自动发现电脑 + 低延迟音频流 + 心跳重连)
          │
          ▼ 局域网 Wi-Fi
[Windows 10/11 守护进程 (Phone2WinMic.exe)]
  纯 C# (.NET 4.8 自带 csc.exe 编译，零第三方依赖)
    -> UDP 54321 接收 + 局域网发现应答
    -> 自适应抖动缓冲 (防时钟漂移) + 软件增益限幅
    -> WinMM waveOut 写入 "CABLE Input (VB-Audio Virtual Cable)"
          │
          ▼
[VB-CABLE 虚拟麦克风]
  任意软件选择麦克风: "CABLE Output (VB-Audio Virtual Cable)"
```

## 快速开始

### 1. Windows 端部署
1. 下载并安装免费虚拟声卡驱动 **VB-CABLE Virtual Audio Device**（安装后建议重启电脑）：
   - 官网：`https://vb-audio.com/Cable/`
   - 安装完成后，在 Windows 声音设置中确保播放设备里存在 `CABLE Input`，录制设备里存在 `CABLE Output`。
2. 进入 `windows/` 目录，双击运行 `build.bat`（自动调用 Win10 自带的 `.NET 4.8 csc.exe` 编译生成 `Phone2WinMic.exe`）。
3. 右键或双击运行 `setup_autostart_and_firewall.bat`：
   - 自动添加 Windows 防火墙 UDP `54321` 入站放行规则；
   - 自动写入当前用户开机自启；
   - 自动在后台托盘启动 `Phone2WinMic.exe`。
4. 在微信 / Discord / OBS / Zoom 等软件的音频设置中，将输入设备（麦克风）选为 **`CABLE Output (VB-Audio Virtual Cable)`**。

### 2. Android 端编译（GitHub Actions 云端自动构建）
1. 将本仓库推送到 GitHub。
2. 打开 GitHub 仓库的 **Actions** 页面，工作流 `Build Android APK` 会自动触发（也可手动点 `Run workflow`）。
3. 编译完成后，在 Artifacts 中可直接下载已签名的 APK：
   - `Phone2WinMic-arm64-v8a-release.apk`（现代 64 位安卓手机）
   - `Phone2WinMic-armeabi-v7a-release.apk`（旧款 32 位安卓手机）
   - `Phone2WinMic-universal-release.apk`（32+64 位合体通用版，新旧通吃）
4. 安装到手机后：
   - 点击 **忽略电池优化**（确保锁屏/关屏不断流）；
   - 开启 **自动发现电脑**（同一 Wi-Fi 下无需手动输入 IP）；
   - 按需开启 **开机自动启动**，点击 **开始传输** 即可。

> **关于 Android 14+ 开机自启说明**：Android 14 及以上系统禁止应用在开机广播中直接从后台启动 `microphone` 类型前台服务。本应用在 Android 5~13 上开机直接自动开始拾音；在 Android 14+ 上若被系统拦截，会自动弹出高优先级通知，解锁后点一下通知即可立刻启动（若手机系统允许自启动与后台弹出界面，可在系统设置中放行）。

## 协议说明 (UDP 54321)

固定 16 字节 Little-Endian 头部 + 可选负载：
- `0..3`: Magic `"P2WM"` (`0x50 0x32 0x57 0x4D`)
- `4`: Version (`1`)
- `5`: Type (`1`=Audio PCM, `2`=Heartbeat, `3`=DiscoverReq, `4`=DiscoverResp, `5`=HeartbeatAck, `6`=Stop)
- `6`: Channels (`1`=Mono, `2`=Stereo)
- `7`: BitsPerSample (`16`)
- `8..11`: Sequence (`uint32`)
- `12..15`: SampleRate (`uint32`, 如 `48000` / `16000`)
- `16..end`: 负载（Audio 包为 16-bit LE PCM，48kHz 时每包 10ms = 960 字节，远小于 1500 MTU 避免分片）
