package com.phone2win.mic;

import android.Manifest;
import android.app.Notification;
import android.app.NotificationChannel;
import android.app.NotificationManager;
import android.app.PendingIntent;
import android.app.Service;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.content.pm.ServiceInfo;
import android.media.AudioFormat;
import android.media.AudioRecord;
import android.media.MediaRecorder;
import android.media.audiofx.AutomaticGainControl;
import android.media.audiofx.NoiseSuppressor;
import android.net.wifi.WifiManager;
import android.os.Build;
import android.os.IBinder;
import android.os.PowerManager;
import android.os.Process;
import android.os.SystemClock;

import java.net.DatagramPacket;
import java.net.DatagramSocket;
import java.net.Inet4Address;
import java.net.InetAddress;
import java.net.InterfaceAddress;
import java.net.NetworkInterface;
import java.net.SocketTimeoutException;
import java.nio.charset.StandardCharsets;
import java.util.ArrayList;
import java.util.Enumeration;
import java.util.HashSet;
import java.util.List;
import java.util.Set;

public class MicForegroundService extends Service {
    public static final String ACTION_START = "com.phone2win.mic.ACTION_START";
    public static final String ACTION_STOP = "com.phone2win.mic.ACTION_STOP";
    public static final String ACTION_REDISCOVER = "com.phone2win.mic.ACTION_REDISCOVER";
    public static final String EXTRA_FROM_BOOT = "from_boot";

    public static final String CHANNEL_SERVICE_ID = "phone2win_mic_service";
    public static final String CHANNEL_PROMPT_ID = "phone2win_mic_prompt";
    private static final int NOTIF_SERVICE_ID = 1001;
    public static final int NOTIF_BOOT_PROMPT_ID = 1002;

    public static final int STATE_STOPPED = 0;
    public static final int STATE_DISCOVERING = 1;
    public static final int STATE_STREAMING = 2;
    public static final int STATE_ERROR = 3;

    // Shared real-time status read by MainActivity
    public static volatile int currentState = STATE_STOPPED;
    public static volatile String currentStatusText = "未启动";
    public static volatile String connectedPcText = "未连接";
    public static volatile float currentAudioLevel = 0f;
    public static volatile long packetsSent = 0L;
    public static volatile int liveGainPercent = 100;
    public static volatile int liveNoiseGatePercent = 0;

    private volatile boolean running = false;
    private volatile boolean forceRediscover = false;

    private PowerManager.WakeLock wakeLock;
    private WifiManager.WifiLock wifiLock;

    private DatagramSocket udpSocket;
    private AudioRecord audioRecord;
    private NoiseSuppressor noiseSuppressor;
    private AutomaticGainControl agc;

    private Thread audioThread;
    private Thread recvThread;

    private volatile InetAddress targetAddress;
    private volatile int targetPort = Protocol.DEFAULT_PORT;
    private volatile String targetHostName = "";
    private volatile long lastAckElapsedMs = 0L;

    public static void startStreaming(Context ctx, boolean fromBoot) {
        Intent intent = new Intent(ctx, MicForegroundService.class);
        intent.setAction(ACTION_START);
        intent.putExtra(EXTRA_FROM_BOOT, fromBoot);
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            ctx.startForegroundService(intent);
        } else {
            ctx.startService(intent);
        }
    }

    public static void stopStreaming(Context ctx) {
        Intent intent = new Intent(ctx, MicForegroundService.class);
        intent.setAction(ACTION_STOP);
        ctx.startService(intent);
    }

    public static void triggerRediscover(Context ctx) {
        Intent intent = new Intent(ctx, MicForegroundService.class);
        intent.setAction(ACTION_REDISCOVER);
        ctx.startService(intent);
    }

    public static void ensureNotificationChannels(Context ctx) {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            NotificationManager nm = (NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm == null) {
                return;
            }
            NotificationChannel serviceCh = new NotificationChannel(
                    CHANNEL_SERVICE_ID,
                    "无线麦克风后台传输",
                    NotificationManager.IMPORTANCE_LOW);
            serviceCh.setDescription("保持关屏时持续通过 Wi-Fi 传输手机麦克风音频");
            serviceCh.setShowBadge(false);
            nm.createNotificationChannel(serviceCh);

            NotificationChannel promptCh = new NotificationChannel(
                    CHANNEL_PROMPT_ID,
                    "开机启动提醒",
                    NotificationManager.IMPORTANCE_HIGH);
            promptCh.setDescription("当系统限制开机后台启动麦克风时提醒一键开启");
            nm.createNotificationChannel(promptCh);
        }
    }

    public static void showBootPromptNotification(Context ctx) {
        ensureNotificationChannels(ctx);
        NotificationManager nm = (NotificationManager) ctx.getSystemService(Context.NOTIFICATION_SERVICE);
        if (nm == null) {
            return;
        }
        Intent launchIntent = new Intent(ctx, MainActivity.class);
        launchIntent.addFlags(Intent.FLAG_ACTIVITY_NEW_TASK | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        launchIntent.putExtra(MainActivity.EXTRA_AUTO_START, true);

        int piFlags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            piFlags |= PendingIntent.FLAG_IMMUTABLE;
        }
        PendingIntent pi = PendingIntent.getActivity(ctx, 200, launchIntent, piFlags);

        Notification.Builder builder = (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
                ? new Notification.Builder(ctx, CHANNEL_PROMPT_ID)
                : new Notification.Builder(ctx);

        builder.setSmallIcon(R.drawable.ic_mic_notif)
                .setContentTitle("Phone2Win Mic 开机已就绪")
                .setContentText("受 Android 14+ 后台权限限制，点击此处立即开始麦克风传输")
                .setAutoCancel(true)
                .setContentIntent(pi);

        try {
            nm.notify(NOTIF_BOOT_PROMPT_ID, builder.build());
        } catch (Throwable ignored) {
        }
    }

    @Override
    public IBinder onBind(Intent intent) {
        return null;
    }

    @Override
    public void onCreate() {
        super.onCreate();
        ensureNotificationChannels(this);
    }

    @Override
    public int onStartCommand(Intent intent, int flags, int startId) {
        String action = (intent != null && intent.getAction() != null) ? intent.getAction() : ACTION_START;

        if (ACTION_STOP.equals(action)) {
            stopWorkerAndSelf();
            return START_NOT_STICKY;
        }

        if (ACTION_REDISCOVER.equals(action)) {
            if (running) {
                targetAddress = null;
                lastAckElapsedMs = 0L;
                forceRediscover = true;
                currentState = STATE_DISCOVERING;
                currentStatusText = "正在重新搜索局域网电脑...";
                connectedPcText = "搜索中...";
                updateServiceNotification("正在重新搜索局域网电脑...");
            }
            return START_STICKY;
        }

        boolean fromBoot = intent != null && intent.getBooleanExtra(EXTRA_FROM_BOOT, false);

        if (!hasAudioPermission()) {
            currentState = STATE_ERROR;
            currentStatusText = "缺少录音权限，请在应用内授权";
            if (fromBoot) {
                showBootPromptNotification(this);
            }
            stopSelf();
            return START_NOT_STICKY;
        }

        if (!enterForegroundSafely(fromBoot)) {
            stopSelf();
            return START_NOT_STICKY;
        }

        if (running) {
            return START_STICKY;
        }

        liveGainPercent = AppPrefs.getGainPercent(this);
        liveNoiseGatePercent = AppPrefs.getNoiseGatePercent(this);

        acquireLocks();
        startStreamingWorkers();

        return START_STICKY;
    }

    private boolean hasAudioPermission() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            return true;
        }
        return checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED;
    }

    private boolean enterForegroundSafely(boolean fromBoot) {
        try {
            Notification notif = buildServiceNotification("正在启动无线麦克风服务...");
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
                startForeground(NOTIF_SERVICE_ID, notif, ServiceInfo.FOREGROUND_SERVICE_TYPE_MICROPHONE);
            } else {
                startForeground(NOTIF_SERVICE_ID, notif);
            }
            return true;
        } catch (Throwable t) {
            currentState = STATE_ERROR;
            currentStatusText = "前台服务启动受限: " + t.getMessage();
            if (fromBoot) {
                showBootPromptNotification(this);
            }
            return false;
        }
    }

    private Notification buildServiceNotification(String contentText) {
        int piFlags = PendingIntent.FLAG_UPDATE_CURRENT;
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            piFlags |= PendingIntent.FLAG_IMMUTABLE;
        }

        Intent openIntent = new Intent(this, MainActivity.class);
        openIntent.addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP | Intent.FLAG_ACTIVITY_CLEAR_TOP);
        PendingIntent openPi = PendingIntent.getActivity(this, 100, openIntent, piFlags);

        Intent stopIntent = new Intent(this, MicForegroundService.class);
        stopIntent.setAction(ACTION_STOP);
        PendingIntent stopPi = PendingIntent.getService(this, 101, stopIntent, piFlags);

        Notification.Builder builder = (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O)
                ? new Notification.Builder(this, CHANNEL_SERVICE_ID)
                : new Notification.Builder(this);

        builder.setSmallIcon(R.drawable.ic_mic_notif)
                .setContentTitle("Phone2Win 无线麦克风运行中")
                .setContentText(contentText)
                .setOngoing(true)
                .setContentIntent(openPi)
                .addAction(android.R.drawable.ic_media_pause, "停止", stopPi);

        return builder.build();
    }

    private void updateServiceNotification(String contentText) {
        try {
            NotificationManager nm = (NotificationManager) getSystemService(Context.NOTIFICATION_SERVICE);
            if (nm != null && running) {
                nm.notify(NOTIF_SERVICE_ID, buildServiceNotification(contentText));
            }
        } catch (Throwable ignored) {
        }
    }

    @SuppressWarnings("deprecation")
    private void acquireLocks() {
        try {
            PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm != null && wakeLock == null) {
                wakeLock = pm.newWakeLock(PowerManager.PARTIAL_WAKE_LOCK, "Phone2WinMic:AudioWakeLock");
                wakeLock.setReferenceCounted(false);
                wakeLock.acquire();
            }
        } catch (Throwable ignored) {
        }

        try {
            WifiManager wm = (WifiManager) getApplicationContext().getSystemService(Context.WIFI_SERVICE);
            if (wm != null && wifiLock == null) {
                int mode = (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q)
                        ? WifiManager.WIFI_MODE_FULL_LOW_LATENCY
                        : WifiManager.WIFI_MODE_FULL_HIGH_PERF;
                wifiLock = wm.createWifiLock(mode, "Phone2WinMic:WifiLock");
                wifiLock.setReferenceCounted(false);
                wifiLock.acquire();
            }
        } catch (Throwable ignored) {
        }
    }

    private void releaseLocks() {
        if (wakeLock != null) {
            try {
                if (wakeLock.isHeld()) {
                    wakeLock.release();
                }
            } catch (Throwable ignored) {
            }
            wakeLock = null;
        }
        if (wifiLock != null) {
            try {
                if (wifiLock.isHeld()) {
                    wifiLock.release();
                }
            } catch (Throwable ignored) {
            }
            wifiLock = null;
        }
    }

    private void startStreamingWorkers() {
        running = true;
        packetsSent = 0L;
        currentAudioLevel = 0f;
        targetAddress = null;
        targetHostName = "";
        lastAckElapsedMs = 0L;
        forceRediscover = true;

        final boolean autoDiscover = AppPrefs.isAutoDiscover(this);
        final String manualIp = AppPrefs.getManualIp(this);
        final int configPort = AppPrefs.getPort(this);
        final int sampleRate = AppPrefs.getSampleRate(this);
        final int audioSourceIdx = AppPrefs.getAudioSourceIndex(this);
        final boolean enableHwNs = AppPrefs.isHwNsEnabled(this);
        final boolean enableHwAgc = AppPrefs.isHwAgcEnabled(this);

        targetPort = configPort;

        try {
            udpSocket = new DatagramSocket();
            udpSocket.setBroadcast(true);
            udpSocket.setSoTimeout(400);
            udpSocket.setSendBufferSize(65536);
        } catch (Throwable t) {
            currentState = STATE_ERROR;
            currentStatusText = "UDP Socket 创建失败: " + t.getMessage();
            stopWorkerAndSelf();
            return;
        }

        // Start UDP receiver thread (handles discovery responses & heartbeat ACKs)
        recvThread = new Thread(new Runnable() {
            @Override
            public void run() {
                receiveAckLoop();
            }
        }, "P2W-UdpRecv");
        recvThread.start();

        // Start audio capture & streaming thread
        audioThread = new Thread(new Runnable() {
            @Override
            public void run() {
                audioCaptureLoop(autoDiscover, manualIp, configPort, sampleRate, audioSourceIdx, enableHwNs, enableHwAgc);
            }
        }, "P2W-AudioSend");
        audioThread.start();
    }

    private void receiveAckLoop() {
        byte[] recvBuf = new byte[512];
        DatagramPacket pkt = new DatagramPacket(recvBuf, recvBuf.length);

        while (running) {
            DatagramSocket sock = udpSocket;
            if (sock == null || sock.isClosed()) {
                break;
            }
            try {
                pkt.setLength(recvBuf.length);
                sock.receive(pkt);
                int len = pkt.getLength();
                if (!Protocol.isValidHeader(recvBuf, len)) {
                    continue;
                }
                byte type = recvBuf[5];
                if (type == Protocol.PKT_DISCOVER_RESP) {
                    InetAddress pcAddr = pkt.getAddress();
                    int pcPort = targetPort;
                    String hostName = pcAddr.getHostAddress();
                    if (len > Protocol.HEADER_SIZE) {
                        String payload = new String(recvBuf, Protocol.HEADER_SIZE, len - Protocol.HEADER_SIZE, StandardCharsets.UTF_8);
                        int sep = payload.indexOf('|');
                        if (sep > 0) {
                            hostName = payload.substring(0, sep);
                            try {
                                int parsed = Integer.parseInt(payload.substring(sep + 1).trim());
                                if (parsed >= 1024 && parsed <= 65535) {
                                    pcPort = parsed;
                                }
                            } catch (NumberFormatException ignored) {
                            }
                        } else if (!payload.isEmpty()) {
                            hostName = payload;
                        }
                    }

                    targetAddress = pcAddr;
                    targetPort = pcPort;
                    targetHostName = hostName;
                    lastAckElapsedMs = SystemClock.elapsedRealtime();
                    AppPrefs.setLastPcIp(this, pcAddr.getHostAddress());

                    currentState = STATE_STREAMING;
                    connectedPcText = hostName + " (" + pcAddr.getHostAddress() + ":" + pcPort + ")";
                    currentStatusText = "正在实时传输音频 (支持关屏运行)";
                    updateServiceNotification("已连接电脑: " + hostName + " (" + pcAddr.getHostAddress() + ")");
                } else if (type == Protocol.PKT_HEARTBEAT_ACK) {
                    lastAckElapsedMs = SystemClock.elapsedRealtime();
                    if (currentState != STATE_STREAMING && targetAddress != null) {
                        currentState = STATE_STREAMING;
                        String ip = targetAddress.getHostAddress();
                        String label = targetHostName.isEmpty() ? ip : (targetHostName + " (" + ip + ")");
                        connectedPcText = label;
                        currentStatusText = "正在实时传输音频 (支持关屏运行)";
                        updateServiceNotification("已连接电脑: " + label);
                    }
                }
            } catch (SocketTimeoutException ignored) {
            } catch (Throwable ignored) {
                if (!running) {
                    break;
                }
            }
        }
    }

    private void audioCaptureLoop(
            boolean autoDiscover,
            String manualIp,
            int configPort,
            int sampleRate,
            int audioSourceIdx,
            boolean enableHwNs,
            boolean enableHwAgc) {
        try {
            Process.setThreadPriority(Process.THREAD_PRIORITY_URGENT_AUDIO);
        } catch (Throwable ignored) {
        }

        AudioDsp.resetState();

        // Pick audio source
        int audioSource = MediaRecorder.AudioSource.VOICE_COMMUNICATION;
        if (audioSourceIdx == 1) {
            audioSource = MediaRecorder.AudioSource.MIC;
        } else if (audioSourceIdx == 2) {
            audioSource = MediaRecorder.AudioSource.VOICE_RECOGNITION;
        }

        // Frame duration: 10ms for 48000/44100 Hz (<1000 bytes, below 1500 MTU), 20ms for 16000 Hz (640 bytes)
        int frameMs = (sampleRate <= 16000) ? 20 : 10;
        int samplesPerFrame = (sampleRate * frameMs) / 1000;
        int frameBytes = samplesPerFrame * 2; // 16-bit mono

        int minBufSize = AudioRecord.getMinBufferSize(
                sampleRate,
                AudioFormat.CHANNEL_IN_MONO,
                AudioFormat.ENCODING_PCM_16BIT);
        if (minBufSize <= 0) {
            currentState = STATE_ERROR;
            currentStatusText = "设备不支持该采样率 (" + sampleRate + " Hz)，请切换采样率重试";
            stopWorkerAndSelf();
            return;
        }
        int recordBufSize = Math.max(minBufSize * 2, frameBytes * 8);

        try {
            audioRecord = new AudioRecord(
                    audioSource,
                    sampleRate,
                    AudioFormat.CHANNEL_IN_MONO,
                    AudioFormat.ENCODING_PCM_16BIT,
                    recordBufSize);
        } catch (Throwable t) {
            currentState = STATE_ERROR;
            currentStatusText = "初始化麦克风失败: " + t.getMessage();
            stopWorkerAndSelf();
            return;
        }

        if (audioRecord.getState() != AudioRecord.STATE_INITIALIZED) {
            currentState = STATE_ERROR;
            currentStatusText = "麦克风初始化未就绪，可能正被其他应用占用";
            stopWorkerAndSelf();
            return;
        }

        attachHardwareEffects(audioRecord.getAudioSessionId(), enableHwNs, enableHwAgc);

        try {
            audioRecord.startRecording();
        } catch (Throwable t) {
            currentState = STATE_ERROR;
            currentStatusText = "无法开始录音: " + t.getMessage();
            stopWorkerAndSelf();
            return;
        }

        // Resolve initial manual IP if auto-discover is disabled
        if (!autoDiscover) {
            try {
                if (manualIp == null || manualIp.trim().isEmpty()) {
                    currentState = STATE_ERROR;
                    currentStatusText = "请填写电脑 IP 或勾选“自动发现局域网电脑”";
                    stopWorkerAndSelf();
                    return;
                }
                InetAddress addr = InetAddress.getByName(manualIp.trim());
                targetAddress = addr;
                targetPort = configPort;
                targetHostName = manualIp.trim();
                currentState = STATE_DISCOVERING;
                connectedPcText = manualIp.trim() + ":" + configPort + " (等待回应)";
                currentStatusText = "已向指定 IP 推送音频...";
                updateServiceNotification("目标电脑: " + manualIp.trim() + ":" + configPort);
            } catch (Throwable t) {
                currentState = STATE_ERROR;
                currentStatusText = "无效的电脑 IP: " + manualIp;
                stopWorkerAndSelf();
                return;
            }
        } else {
            currentState = STATE_DISCOVERING;
            currentStatusText = "正在局域网自动搜索 Windows 守护端...";
            connectedPcText = "搜索中...";
            updateServiceNotification("正在局域网自动搜索 Windows 电脑...");
        }

        byte[] packetBuf = new byte[Protocol.HEADER_SIZE + frameBytes];
        int seq = 0;
        long lastProbeMs = 0L;
        long lastHeartbeatMs = 0L;

        while (running) {
            int offset = 0;
            while (running && offset < frameBytes) {
                int read = audioRecord.read(packetBuf, Protocol.HEADER_SIZE + offset, frameBytes - offset);
                if (read > 0) {
                    offset += read;
                } else if (read < 0) {
                    SystemClock.sleep(10);
                    break;
                }
            }
            if (!running || offset < frameBytes) {
                continue;
            }

            // Native 32/64-bit C DSP: noise gate + gain + soft limiter + level meter
            float gain = liveGainPercent / 100.0f;
            float gateThresh = liveNoiseGatePercent / 1000.0f; // 0..100 -> 0.000..0.100
            currentAudioLevel = AudioDsp.processPcm16LE(packetBuf, Protocol.HEADER_SIZE, frameBytes, gain, gateThresh);

            long now = SystemClock.elapsedRealtime();

            if (autoDiscover) {
                boolean ackTimedOut = (targetAddress != null && lastAckElapsedMs > 0 && (now - lastAckElapsedMs) > 5500L);
                if (ackTimedOut) {
                    targetAddress = null;
                    currentState = STATE_DISCOVERING;
                    currentStatusText = "电脑暂时离线，正在自动重连...";
                    connectedPcText = "重连搜索中...";
                    updateServiceNotification("电脑离线，正在自动重连...");
                }

                if (forceRediscover || targetAddress == null) {
                    if (forceRediscover || (now - lastProbeMs) >= 1500L) {
                        forceRediscover = false;
                        lastProbeMs = now;
                        sendDiscoveryProbes(configPort, sampleRate);
                    }
                }
            } else {
                // Manual IP mode: send periodic heartbeat so UI detects if PC is online
                if ((now - lastHeartbeatMs) >= 2000L && targetAddress != null) {
                    lastHeartbeatMs = now;
                    sendControl(Protocol.PKT_HEARTBEAT, seq, sampleRate, targetAddress, targetPort);
                    if (lastAckElapsedMs > 0 && (now - lastAckElapsedMs) > 5500L) {
                        currentState = STATE_DISCOVERING;
                        connectedPcText = targetHostName + ":" + targetPort + " (电脑未回应)";
                        currentStatusText = "推送中，等待 Windows 守护端上线...";
                    }
                }
            }

            InetAddress dest = targetAddress;
            if (dest != null) {
                seq++;
                Protocol.writeHeader(packetBuf, Protocol.PKT_AUDIO, seq, sampleRate, (byte) 1, (byte) 16);
                try {
                    DatagramPacket sendPkt = new DatagramPacket(packetBuf, packetBuf.length, dest, targetPort);
                    DatagramSocket sock = udpSocket;
                    if (sock != null && !sock.isClosed()) {
                        sock.send(sendPkt);
                        packetsSent++;
                    }
                } catch (Throwable ignored) {
                }
            }
        }

        // Send graceful STOP notification to Windows
        InetAddress dest = targetAddress;
        if (dest != null) {
            sendControl(Protocol.PKT_STOP, seq + 1, sampleRate, dest, targetPort);
            sendControl(Protocol.PKT_STOP, seq + 2, sampleRate, dest, targetPort);
        }

        cleanupAudio();
    }

    private void attachHardwareEffects(int audioSessionId, boolean enableNs, boolean enableAgc) {
        if (enableNs) {
            try {
                if (NoiseSuppressor.isAvailable()) {
                    noiseSuppressor = NoiseSuppressor.create(audioSessionId);
                    if (noiseSuppressor != null) {
                        noiseSuppressor.setEnabled(true);
                    }
                }
            } catch (Throwable ignored) {
            }
        }
        if (enableAgc) {
            try {
                if (AutomaticGainControl.isAvailable()) {
                    agc = AutomaticGainControl.create(audioSessionId);
                    if (agc != null) {
                        agc.setEnabled(true);
                    }
                }
            } catch (Throwable ignored) {
            }
        }
    }

    private void sendDiscoveryProbes(int port, int sampleRate) {
        byte[] req = Protocol.buildControlPacket(Protocol.PKT_DISCOVER_REQ, 0, sampleRate);
        List<InetAddress> targets = collectDiscoveryTargets();
        DatagramSocket sock = udpSocket;
        if (sock == null || sock.isClosed()) {
            return;
        }
        for (int i = 0; i < targets.size(); i++) {
            try {
                DatagramPacket pkt = new DatagramPacket(req, req.length, targets.get(i), port);
                sock.send(pkt);
            } catch (Throwable ignored) {
            }
        }
    }

    private List<InetAddress> collectDiscoveryTargets() {
        List<InetAddress> result = new ArrayList<InetAddress>();
        Set<String> seen = new HashSet<String>();

        // 1. Last known PC IP (fast unicast fallback even if router blocks broadcast)
        String lastIp = AppPrefs.getLastPcIp(this);
        if (lastIp != null && !lastIp.isEmpty()) {
            try {
                InetAddress addr = InetAddress.getByName(lastIp);
                if (seen.add(addr.getHostAddress())) {
                    result.add(addr);
                }
            } catch (Throwable ignored) {
            }
        }

        // 2. Active Wi-Fi / LAN subnet broadcast addresses (e.g. 192.168.1.255)
        try {
            Enumeration<NetworkInterface> nics = NetworkInterface.getNetworkInterfaces();
            while (nics != null && nics.hasMoreElements()) {
                NetworkInterface nic = nics.nextElement();
                if (!nic.isUp() || nic.isLoopback()) {
                    continue;
                }
                List<InterfaceAddress> addrs = nic.getInterfaceAddresses();
                for (int i = 0; i < addrs.size(); i++) {
                    InterfaceAddress ia = addrs.get(i);
                    InetAddress addr = ia.getAddress();
                    InetAddress bcast = ia.getBroadcast();
                    if (addr instanceof Inet4Address && bcast != null) {
                        if (seen.add(bcast.getHostAddress())) {
                            result.add(bcast);
                        }
                    }
                }
            }
        } catch (Throwable ignored) {
        }

        // 3. Global broadcast 255.255.255.255
        try {
            InetAddress globalBcast = InetAddress.getByName("255.255.255.255");
            if (seen.add(globalBcast.getHostAddress())) {
                result.add(globalBcast);
            }
        } catch (Throwable ignored) {
        }

        return result;
    }

    private void sendControl(byte type, int seq, int sampleRate, InetAddress addr, int port) {
        try {
            DatagramSocket sock = udpSocket;
            if (sock != null && !sock.isClosed() && addr != null) {
                byte[] pktData = Protocol.buildControlPacket(type, seq, sampleRate);
                sock.send(new DatagramPacket(pktData, pktData.length, addr, port));
            }
        } catch (Throwable ignored) {
        }
    }

    private void cleanupAudio() {
        if (noiseSuppressor != null) {
            try {
                noiseSuppressor.release();
            } catch (Throwable ignored) {
            }
            noiseSuppressor = null;
        }
        if (agc != null) {
            try {
                agc.release();
            } catch (Throwable ignored) {
            }
            agc = null;
        }
        if (audioRecord != null) {
            try {
                if (audioRecord.getRecordingState() == AudioRecord.RECORDSTATE_RECORDING) {
                    audioRecord.stop();
                }
            } catch (Throwable ignored) {
            }
            try {
                audioRecord.release();
            } catch (Throwable ignored) {
            }
            audioRecord = null;
        }
    }

    private void stopWorkerAndSelf() {
        running = false;
        if (udpSocket != null) {
            try {
                udpSocket.close();
            } catch (Throwable ignored) {
            }
            udpSocket = null;
        }
        if (audioThread != null) {
            try {
                audioThread.join(800);
            } catch (Throwable ignored) {
            }
            audioThread = null;
        }
        if (recvThread != null) {
            try {
                recvThread.join(500);
            } catch (Throwable ignored) {
            }
            recvThread = null;
        }
        cleanupAudio();
        releaseLocks();

        if (currentState != STATE_ERROR) {
            currentState = STATE_STOPPED;
            currentStatusText = "已停止";
        }
        connectedPcText = "未连接";
        currentAudioLevel = 0f;

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.N) {
            stopForeground(STOP_FOREGROUND_REMOVE);
        } else {
            stopForeground(true);
        }
        stopSelf();
    }

    @Override
    public void onDestroy() {
        running = false;
        if (udpSocket != null) {
            try {
                udpSocket.close();
            } catch (Throwable ignored) {
            }
            udpSocket = null;
        }
        cleanupAudio();
        releaseLocks();
        if (currentState != STATE_ERROR) {
            currentState = STATE_STOPPED;
            currentStatusText = "已停止";
        }
        currentAudioLevel = 0f;
        super.onDestroy();
    }
}
