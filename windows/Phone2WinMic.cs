using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace Phone2WinMic
{
    internal static class Program
    {
        private const string MutexName = "Global\\Phone2WinMic_SingleInstance_Mutex_v1";

        [STAThread]
        private static void Main(string[] args)
        {
            bool createdNew;
            using (Mutex mutex = new Mutex(true, MutexName, out createdNew))
            {
                if (!createdNew)
                {
                    return;
                }

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);

                bool startMinimized = false;
                if (args != null)
                {
                    for (int i = 0; i < args.Length; i++)
                    {
                        if (string.Equals(args[i], "--minimized", StringComparison.OrdinalIgnoreCase) ||
                            string.Equals(args[i], "-m", StringComparison.OrdinalIgnoreCase))
                        {
                            startMinimized = true;
                        }
                    }
                }

                using (TrayDaemonContext context = new TrayDaemonContext(startMinimized))
                {
                    Application.Run(context);
                }
            }
        }
    }

    internal static class Protocol
    {
        public const int HeaderSize = 16;
        public const byte Version = 1;

        public const byte PktAudio = 1;
        public const byte PktHeartbeat = 2;
        public const byte PktDiscoverReq = 3;
        public const byte PktDiscoverResp = 4;
        public const byte PktHeartbeatAck = 5;
        public const byte PktStop = 6;

        public static bool IsValidHeader(byte[] data, int length)
        {
            if (data == null || length < HeaderSize)
                return false;
            return data[0] == (byte)'P' &&
                   data[1] == (byte)'2' &&
                   data[2] == (byte)'W' &&
                   data[3] == (byte)'M' &&
                   data[4] == Version;
        }

        public static uint ReadUInt32LE(byte[] data, int offset)
        {
            return (uint)(data[offset] |
                         (data[offset + 1] << 8) |
                         (data[offset + 2] << 16) |
                         (data[offset + 3] << 24));
        }

        public static void WriteUInt32LE(byte[] data, int offset, uint value)
        {
            data[offset] = (byte)(value & 0xFF);
            data[offset + 1] = (byte)((value >> 8) & 0xFF);
            data[offset + 2] = (byte)((value >> 16) & 0xFF);
            data[offset + 3] = (byte)((value >> 24) & 0xFF);
        }

        public static byte[] BuildPacket(byte pktType, uint seq, uint sampleRate, byte channels, byte bitsPerSample, byte[] payload)
        {
            int payloadLen = (payload != null) ? payload.Length : 0;
            byte[] buf = new byte[HeaderSize + payloadLen];
            buf[0] = (byte)'P';
            buf[1] = (byte)'2';
            buf[2] = (byte)'W';
            buf[3] = (byte)'M';
            buf[4] = Version;
            buf[5] = pktType;
            buf[6] = channels;
            buf[7] = bitsPerSample;
            WriteUInt32LE(buf, 8, seq);
            WriteUInt32LE(buf, 12, sampleRate);
            if (payloadLen > 0)
            {
                Buffer.BlockCopy(payload, 0, buf, HeaderSize, payloadLen);
            }
            return buf;
        }
    }

    internal sealed class AppConfig
    {
        public int Port;
        public string DeviceNameHint;
        public int GainPercent;
        public int TargetBufferMs;
        public bool Muted;

        private readonly string _configPath;

        public AppConfig()
        {
            Port = 54321;
            DeviceNameHint = "CABLE Input";
            GainPercent = 100;
            TargetBufferMs = 40;
            Muted = false;
            _configPath = ResolveConfigPath();
        }

        private static string ResolveConfigPath()
        {
            try
            {
                string exeDir = AppDomain.CurrentDomain.BaseDirectory;
                string localPath = Path.Combine(exeDir, "config.ini");
                if (File.Exists(localPath))
                    return localPath;

                // Test write permission in executable directory
                string probe = Path.Combine(exeDir, ".write_test.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return localPath;
            }
            catch
            {
                string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
                string dir = Path.Combine(appData, "Phone2WinMic");
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                return Path.Combine(dir, "config.ini");
            }
        }

        public void Load()
        {
            try
            {
                if (!File.Exists(_configPath))
                    return;

                string[] lines = File.ReadAllLines(_configPath, Encoding.UTF8);
                for (int i = 0; i < lines.Length; i++)
                {
                    string line = lines[i].Trim();
                    if (line.Length == 0 || line.StartsWith("#") || line.StartsWith(";"))
                        continue;

                    int idx = line.IndexOf('=');
                    if (idx <= 0)
                        continue;

                    string key = line.Substring(0, idx).Trim();
                    string val = line.Substring(idx + 1).Trim();

                    int intVal;
                    if (string.Equals(key, "Port", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out intVal) && intVal >= 1024 && intVal <= 65535)
                            Port = intVal;
                    }
                    else if (string.Equals(key, "DeviceNameHint", StringComparison.OrdinalIgnoreCase))
                    {
                        if (val.Length > 0)
                            DeviceNameHint = val;
                    }
                    else if (string.Equals(key, "GainPercent", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out intVal) && intVal >= 10 && intVal <= 400)
                            GainPercent = intVal;
                    }
                    else if (string.Equals(key, "TargetBufferMs", StringComparison.OrdinalIgnoreCase))
                    {
                        if (int.TryParse(val, out intVal) && intVal >= 20 && intVal <= 200)
                            TargetBufferMs = intVal;
                    }
                    else if (string.Equals(key, "Muted", StringComparison.OrdinalIgnoreCase))
                    {
                        Muted = (val == "1" || string.Equals(val, "true", StringComparison.OrdinalIgnoreCase));
                    }
                }
            }
            catch
            {
                // Ignore malformed config
            }
        }

        public void Save()
        {
            try
            {
                StringBuilder sb = new StringBuilder();
                sb.AppendLine("# Phone2Win-Mic Configuration");
                sb.AppendLine("Port=" + Port);
                sb.AppendLine("DeviceNameHint=" + DeviceNameHint);
                sb.AppendLine("GainPercent=" + GainPercent);
                sb.AppendLine("TargetBufferMs=" + TargetBufferMs);
                sb.AppendLine("Muted=" + (Muted ? "1" : "0"));
                File.WriteAllText(_configPath, sb.ToString(), Encoding.UTF8);
            }
            catch
            {
                // Ignore save errors
            }
        }
    }

    internal static class AutoStartHelper
    {
        private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "Phone2WinMic";

        public static bool IsEnabled()
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false))
                {
                    if (key == null)
                        return false;
                    object val = key.GetValue(ValueName);
                    return val != null && !string.IsNullOrEmpty(val.ToString());
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool SetEnabled(bool enable)
        {
            try
            {
                using (RegistryKey key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                {
                    if (key == null)
                        return false;

                    if (enable)
                    {
                        string exePath = Application.ExecutablePath;
                        key.SetValue(ValueName, string.Format("\"{0}\" --minimized", exePath));
                    }
                    else
                    {
                        if (key.GetValue(ValueName) != null)
                            key.DeleteValue(ValueName, false);
                    }
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }
    }

    internal sealed class AudioDeviceInfo
    {
        public int DeviceId;
        public string Name;

        public AudioDeviceInfo(int deviceId, string name)
        {
            DeviceId = deviceId;
            Name = name;
        }

        public override string ToString()
        {
            return Name;
        }
    }

    internal static class WinMmNative
    {
        public const uint MMSYSERR_NOERROR = 0;
        public const ushort WAVE_FORMAT_PCM = 1;
        public const uint CALLBACK_EVENT = 0x00050000;
        public const uint WHDR_DONE = 0x00000001;
        public const int WAVE_MAPPER = -1;

        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct WAVEFORMATEX
        {
            public ushort wFormatTag;
            public ushort nChannels;
            public uint nSamplesPerSec;
            public uint nAvgBytesPerSec;
            public ushort nBlockAlign;
            public ushort wBitsPerSample;
            public ushort cbSize;
        }

        [StructLayout(LayoutKind.Sequential, Pack = 1, CharSet = CharSet.Unicode)]
        public struct WAVEOUTCAPSW
        {
            public ushort wMid;
            public ushort wPid;
            public uint vDriverVersion;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string szPname;
            public uint dwFormats;
            public ushort wChannels;
            public ushort wReserved1;
            public uint dwSupport;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct WAVEHDR
        {
            public IntPtr lpData;
            public uint dwBufferLength;
            public uint dwBytesRecorded;
            public IntPtr dwUser;
            public uint dwFlags;
            public uint dwLoops;
            public IntPtr lpNext;
            public IntPtr reserved;
        }

        [DllImport("winmm.dll")]
        public static extern uint waveOutGetNumDevs();

        [DllImport("winmm.dll", CharSet = CharSet.Unicode, EntryPoint = "waveOutGetDevCapsW")]
        public static extern uint waveOutGetDevCaps(IntPtr uDeviceID, out WAVEOUTCAPSW pwoc, uint cbwoc);

        [DllImport("winmm.dll")]
        public static extern uint waveOutOpen(
            out IntPtr phwo,
            int uDeviceID,
            ref WAVEFORMATEX pwfx,
            IntPtr dwCallback,
            IntPtr dwInstance,
            uint fdwOpen);

        [DllImport("winmm.dll")]
        public static extern uint waveOutPrepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern uint waveOutUnprepareHeader(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern uint waveOutWrite(IntPtr hwo, IntPtr pwh, uint cbwh);

        [DllImport("winmm.dll")]
        public static extern uint waveOutReset(IntPtr hwo);

        [DllImport("winmm.dll")]
        public static extern uint waveOutClose(IntPtr hwo);

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool DestroyIcon(IntPtr hIcon);

        public static List<AudioDeviceInfo> EnumerateOutputDevices()
        {
            List<AudioDeviceInfo> list = new List<AudioDeviceInfo>();
            uint count = waveOutGetNumDevs();
            uint capsSize = (uint)Marshal.SizeOf(typeof(WAVEOUTCAPSW));

            for (int i = 0; i < (int)count; i++)
            {
                WAVEOUTCAPSW caps;
                if (waveOutGetDevCaps(new IntPtr(i), out caps, capsSize) == MMSYSERR_NOERROR)
                {
                    string name = (caps.szPname != null) ? caps.szPname.Trim() : ("Device #" + i);
                    list.Add(new AudioDeviceInfo(i, name));
                }
            }
            return list;
        }
    }

    internal sealed class ByteRingBuffer
    {
        private readonly byte[] _buffer;
        private int _readPos;
        private int _writePos;
        private int _count;
        private readonly object _sync = new object();

        public ByteRingBuffer(int capacity)
        {
            _buffer = new byte[capacity];
        }

        public int Count
        {
            get
            {
                lock (_sync)
                {
                    return _count;
                }
            }
        }

        public void Clear()
        {
            lock (_sync)
            {
                _readPos = 0;
                _writePos = 0;
                _count = 0;
            }
        }

        public int Write(byte[] src, int offset, int length, int maxAllowedBytes, int trimToBytes, int frameAlign)
        {
            int dropped = 0;
            lock (_sync)
            {
                // Enforce latency cap / clock drift compensation
                if (maxAllowedBytes > 0 && (_count + length) > maxAllowedBytes)
                {
                    int target = trimToBytes;
                    if (target < 0) target = 0;
                    int toDrop = (_count + length) - target;
                    if (frameAlign > 1)
                        toDrop = (toDrop / frameAlign) * frameAlign;

                    if (toDrop > _count)
                    {
                        dropped = _count;
                        _readPos = 0;
                        _writePos = 0;
                        _count = 0;
                    }
                    else if (toDrop > 0)
                    {
                        _readPos = (_readPos + toDrop) % _buffer.Length;
                        _count -= toDrop;
                        dropped = toDrop;
                    }
                }

                // If still exceeding physical capacity, drop oldest
                int overflow = (_count + length) - _buffer.Length;
                if (overflow > 0)
                {
                    if (frameAlign > 1)
                        overflow = ((overflow + frameAlign - 1) / frameAlign) * frameAlign;
                    if (overflow >= _count)
                    {
                        dropped += _count;
                        _readPos = 0;
                        _writePos = 0;
                        _count = 0;
                    }
                    else
                    {
                        _readPos = (_readPos + overflow) % _buffer.Length;
                        _count -= overflow;
                        dropped += overflow;
                    }
                }

                if (length > _buffer.Length)
                {
                    offset += (length - _buffer.Length);
                    length = _buffer.Length;
                }

                int firstPart = Math.Min(length, _buffer.Length - _writePos);
                Buffer.BlockCopy(src, offset, _buffer, _writePos, firstPart);
                int remaining = length - firstPart;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(src, offset + firstPart, _buffer, 0, remaining);
                }
                _writePos = (_writePos + length) % _buffer.Length;
                _count += length;
            }
            return dropped;
        }

        public int Read(byte[] dst, int offset, int length)
        {
            lock (_sync)
            {
                int toRead = Math.Min(length, _count);
                if (toRead <= 0)
                    return 0;

                int firstPart = Math.Min(toRead, _buffer.Length - _readPos);
                Buffer.BlockCopy(_buffer, _readPos, dst, offset, firstPart);
                int remaining = toRead - firstPart;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(_buffer, 0, dst, offset + firstPart, remaining);
                }
                _readPos = (_readPos + toRead) % _buffer.Length;
                _count -= toRead;
                return toRead;
            }
        }
    }

    internal sealed class WaveOutPlayer : IDisposable
    {
        private const int BufferCount = 6;
        private const int MaxInFlight = 3;
        private const int FrameMs = 10;

        private readonly object _deviceLock = new object();
        private readonly AutoResetEvent _waveEvent = new AutoResetEvent(false);
        private readonly AutoResetEvent _dataEvent = new AutoResetEvent(false);
        private readonly ByteRingBuffer _ringBuffer = new ByteRingBuffer(48000 * 2 * 2); // ~1 sec stereo 48kHz

        private IntPtr _hWaveOut = IntPtr.Zero;
        private IntPtr[] _hdrPtrs;
        private IntPtr[] _dataPtrs;
        private bool[] _inFlight;
        private int _bufferBytes;
        private int _sampleRate = 48000;
        private int _channels = 1;
        private int _deviceId = -2;
        private string _deviceName = "";

        private volatile bool _running;
        private volatile bool _muted;
        private volatile int _gainPercent = 100;
        private volatile int _targetBufferMs = 40;
        private volatile float _currentLevel;
        private volatile int _underrunCount;
        private volatile int _driftDropCount;
        private volatile bool _deviceOpenOk;
        private volatile string _lastError = "";

        private Thread _playThread;

        private static readonly int HdrSize = Marshal.SizeOf(typeof(WinMmNative.WAVEHDR));
        private static readonly int FlagsOffset = Marshal.OffsetOf(typeof(WinMmNative.WAVEHDR), "dwFlags").ToInt32();
        private static readonly int LenOffset = Marshal.OffsetOf(typeof(WinMmNative.WAVEHDR), "dwBufferLength").ToInt32();

        public bool IsDeviceOpen { get { return _deviceOpenOk; } }
        public string ActiveDeviceName { get { return _deviceName; } }
        public string LastError { get { return _lastError; } }
        public int SampleRate { get { return _sampleRate; } }
        public int Channels { get { return _channels; } }
        public float CurrentLevel { get { return _currentLevel; } }
        public int UnderrunCount { get { return _underrunCount; } }
        public int DriftDropCount { get { return _driftDropCount; } }

        public int BufferedMs
        {
            get
            {
                int bytesPerSec = _sampleRate * _channels * 2;
                if (bytesPerSec <= 0) return 0;
                return (int)((_ringBuffer.Count * 1000L) / bytesPerSec);
            }
        }

        public bool Muted
        {
            get { return _muted; }
            set { _muted = value; }
        }

        public int GainPercent
        {
            get { return _gainPercent; }
            set { _gainPercent = value; }
        }

        public int TargetBufferMs
        {
            get { return _targetBufferMs; }
            set { _targetBufferMs = value; }
        }

        public void Start(int deviceId, string deviceName, int sampleRate, int channels)
        {
            OpenDevice(deviceId, deviceName, sampleRate, channels);
            if (_playThread == null)
            {
                _running = true;
                _playThread = new Thread(PlaybackLoop);
                _playThread.Name = "WaveOutPlaybackThread";
                _playThread.IsBackground = true;
                _playThread.Priority = ThreadPriority.AboveNormal;
                _playThread.Start();
            }
        }

        public bool OpenDevice(int deviceId, string deviceName, int sampleRate, int channels)
        {
            lock (_deviceLock)
            {
                CloseDeviceInternal();

                _deviceId = deviceId;
                _deviceName = (deviceName != null) ? deviceName : "";
                _sampleRate = (sampleRate >= 8000 && sampleRate <= 96000) ? sampleRate : 48000;
                _channels = (channels == 2) ? 2 : 1;
                _ringBuffer.Clear();

                if (deviceId < -1)
                {
                    _deviceOpenOk = false;
                    _lastError = "未找到可用的虚拟声卡输出设备 (VB-Cable)";
                    return false;
                }

                WinMmNative.WAVEFORMATEX wfx = new WinMmNative.WAVEFORMATEX();
                wfx.wFormatTag = WinMmNative.WAVE_FORMAT_PCM;
                wfx.nChannels = (ushort)_channels;
                wfx.nSamplesPerSec = (uint)_sampleRate;
                wfx.wBitsPerSample = 16;
                wfx.nBlockAlign = (ushort)(_channels * 2);
                wfx.nAvgBytesPerSec = (uint)(_sampleRate * wfx.nBlockAlign);
                wfx.cbSize = 0;

                IntPtr eventHandle = _waveEvent.SafeWaitHandle.DangerousGetHandle();
                IntPtr hwo;
                uint res = WinMmNative.waveOutOpen(
                    out hwo,
                    deviceId,
                    ref wfx,
                    eventHandle,
                    IntPtr.Zero,
                    WinMmNative.CALLBACK_EVENT);

                if (res != WinMmNative.MMSYSERR_NOERROR)
                {
                    _deviceOpenOk = false;
                    _lastError = string.Format("waveOutOpen 失败 (错误码 {0})", res);
                    return false;
                }

                _hWaveOut = hwo;
                _bufferBytes = (_sampleRate * FrameMs / 1000) * _channels * 2;
                if (_bufferBytes < 320) _bufferBytes = 320;

                _hdrPtrs = new IntPtr[BufferCount];
                _dataPtrs = new IntPtr[BufferCount];
                _inFlight = new bool[BufferCount];

                for (int i = 0; i < BufferCount; i++)
                {
                    _dataPtrs[i] = Marshal.AllocHGlobal(_bufferBytes);
                    _hdrPtrs[i] = Marshal.AllocHGlobal(HdrSize);

                    WinMmNative.WAVEHDR hdr = new WinMmNative.WAVEHDR();
                    hdr.lpData = _dataPtrs[i];
                    hdr.dwBufferLength = (uint)_bufferBytes;
                    hdr.dwBytesRecorded = 0;
                    hdr.dwUser = IntPtr.Zero;
                    hdr.dwFlags = 0;
                    hdr.dwLoops = 0;
                    hdr.lpNext = IntPtr.Zero;
                    hdr.reserved = IntPtr.Zero;

                    Marshal.StructureToPtr(hdr, _hdrPtrs[i], false);
                    uint prep = WinMmNative.waveOutPrepareHeader(_hWaveOut, _hdrPtrs[i], (uint)HdrSize);
                    if (prep != WinMmNative.MMSYSERR_NOERROR)
                    {
                        CloseDeviceInternal();
                        _deviceOpenOk = false;
                        _lastError = string.Format("waveOutPrepareHeader 失败 (错误码 {0})", prep);
                        return false;
                    }
                    _inFlight[i] = false;
                }

                _deviceOpenOk = true;
                _lastError = "";
                return true;
            }
        }

        public void EnsureFormat(int sampleRate, int channels)
        {
            if (sampleRate != _sampleRate || channels != _channels)
            {
                OpenDevice(_deviceId, _deviceName, sampleRate, channels);
            }
        }

        public void ResetBuffer()
        {
            _ringBuffer.Clear();
            _currentLevel = 0f;
        }

        public void PushPcm(byte[] data, int offset, int length)
        {
            if (!_deviceOpenOk || length <= 0)
                return;

            int frameAlign = _channels * 2;
            int validLen = (length / frameAlign) * frameAlign;
            if (validLen <= 0)
                return;

            int bytesPerMs = (_sampleRate * frameAlign) / 1000;
            int targetBytes = Math.Max(_bufferBytes * 2, _targetBufferMs * bytesPerMs);
            int maxBytes = Math.Max(targetBytes * 3, targetBytes + (60 * bytesPerMs));

            int dropped = _ringBuffer.Write(data, offset, validLen, maxBytes, targetBytes, frameAlign);
            if (dropped > 0)
            {
                _driftDropCount++;
            }
            _dataEvent.Set();
        }

        private void PlaybackLoop()
        {
            WaitHandle[] waitHandles = new WaitHandle[] { _waveEvent, _dataEvent };
            byte[] tempBuf = new byte[8192];
            bool streaming = false;
            int silentFrames = 0;

            while (_running)
            {
                bool submittedAny = false;

                lock (_deviceLock)
                {
                    if (_deviceOpenOk && _hWaveOut != IntPtr.Zero && _hdrPtrs != null)
                    {
                        // Reclaim completed buffers
                        int activeInFlight = 0;
                        for (int i = 0; i < BufferCount; i++)
                        {
                            if (_inFlight[i])
                            {
                                uint flags = (uint)Marshal.ReadInt32(_hdrPtrs[i], FlagsOffset);
                                if ((flags & WinMmNative.WHDR_DONE) != 0)
                                {
                                    _inFlight[i] = false;
                                }
                                else
                                {
                                    activeInFlight++;
                                }
                            }
                        }

                        int frameAlign = _channels * 2;
                        int bytesPerMs = (_sampleRate * frameAlign) / 1000;
                        int preRollBytes = Math.Max(_bufferBytes * 2, _targetBufferMs * bytesPerMs);

                        if (!streaming)
                        {
                            if (_ringBuffer.Count >= preRollBytes)
                            {
                                streaming = true;
                                silentFrames = 0;
                            }
                            else
                            {
                                _currentLevel *= 0.8f;
                            }
                        }

                        if (streaming)
                        {
                            if (tempBuf.Length < _bufferBytes)
                                tempBuf = new byte[_bufferBytes];

                            while (activeInFlight < MaxInFlight)
                            {
                                int slot = FindFreeSlot();
                                if (slot < 0)
                                    break;

                                int available = _ringBuffer.Count;
                                if (available >= _bufferBytes)
                                {
                                    _ringBuffer.Read(tempBuf, 0, _bufferBytes);
                                    silentFrames = 0;
                                    ProcessAudioInPlace(tempBuf, _bufferBytes);
                                }
                                else if (available > 0)
                                {
                                    int read = _ringBuffer.Read(tempBuf, 0, available);
                                    Array.Clear(tempBuf, read, _bufferBytes - read);
                                    silentFrames++;
                                    _underrunCount++;
                                    ProcessAudioInPlace(tempBuf, _bufferBytes);
                                }
                                else
                                {
                                    silentFrames++;
                                    if (silentFrames > 25)
                                    {
                                        // 250ms of no packets -> return to idle waiting state
                                        streaming = false;
                                        _currentLevel = 0f;
                                        break;
                                    }
                                    Array.Clear(tempBuf, 0, _bufferBytes);
                                    _currentLevel *= 0.7f;
                                }

                                Marshal.Copy(tempBuf, 0, _dataPtrs[slot], _bufferBytes);
                                Marshal.WriteInt32(_hdrPtrs[slot], LenOffset, _bufferBytes);

                                uint res = WinMmNative.waveOutWrite(_hWaveOut, _hdrPtrs[slot], (uint)HdrSize);
                                if (res == WinMmNative.MMSYSERR_NOERROR)
                                {
                                    _inFlight[slot] = true;
                                    activeInFlight++;
                                    submittedAny = true;
                                }
                                else
                                {
                                    _lastError = string.Format("waveOutWrite 错误 ({0})", res);
                                    break;
                                }
                            }
                        }
                    }
                }

                if (!submittedAny)
                {
                    WaitHandle.WaitAny(waitHandles, 25);
                }
            }
        }

        private int FindFreeSlot()
        {
            for (int i = 0; i < BufferCount; i++)
            {
                if (!_inFlight[i])
                    return i;
            }
            return -1;
        }

        private void ProcessAudioInPlace(byte[] buf, int length)
        {
            if (_muted)
            {
                Array.Clear(buf, 0, length);
                _currentLevel = 0f;
                return;
            }

            float gain = _gainPercent / 100.0f;
            bool applyGain = (Math.Abs(gain - 1.0f) > 0.005f);
            double sumSq = 0.0;
            int sampleCount = length / 2;

            for (int i = 0; i < length; i += 2)
            {
                short sample = (short)(buf[i] | (buf[i + 1] << 8));
                float val = sample;

                if (applyGain)
                {
                    val = sample * gain;
                    // Smooth soft limiter above 28000 to prevent harsh digital clipping
                    if (val > 28000f)
                        val = 28000f + 4767f * (float)Math.Tanh((val - 28000f) / 4767f);
                    else if (val < -28000f)
                        val = -28000f + 4767f * (float)Math.Tanh((val + 28000f) / 4767f);

                    int clamped = (int)val;
                    if (clamped > 32767) clamped = 32767;
                    else if (clamped < -32768) clamped = -32768;

                    short outSample = (short)clamped;
                    buf[i] = (byte)(outSample & 0xFF);
                    buf[i + 1] = (byte)((outSample >> 8) & 0xFF);
                }

                double norm = val / 32768.0;
                sumSq += norm * norm;
            }

            if (sampleCount > 0)
            {
                float rms = (float)Math.Sqrt(sumSq / sampleCount);
                // Smooth peak-ish level meter scaled for speech visibility
                float display = Math.Min(1.0f, rms * 2.5f);
                _currentLevel = _currentLevel * 0.4f + display * 0.6f;
            }
        }

        private void CloseDeviceInternal()
        {
            if (_hWaveOut != IntPtr.Zero)
            {
                try { WinMmNative.waveOutReset(_hWaveOut); } catch { }

                if (_hdrPtrs != null)
                {
                    for (int i = 0; i < _hdrPtrs.Length; i++)
                    {
                        if (_hdrPtrs[i] != IntPtr.Zero)
                        {
                            try { WinMmNative.waveOutUnprepareHeader(_hWaveOut, _hdrPtrs[i], (uint)HdrSize); } catch { }
                            Marshal.FreeHGlobal(_hdrPtrs[i]);
                            _hdrPtrs[i] = IntPtr.Zero;
                        }
                        if (_dataPtrs != null && _dataPtrs[i] != IntPtr.Zero)
                        {
                            Marshal.FreeHGlobal(_dataPtrs[i]);
                            _dataPtrs[i] = IntPtr.Zero;
                        }
                    }
                }

                try { WinMmNative.waveOutClose(_hWaveOut); } catch { }
                _hWaveOut = IntPtr.Zero;
            }

            _hdrPtrs = null;
            _dataPtrs = null;
            _inFlight = null;
            _deviceOpenOk = false;
        }

        public void Dispose()
        {
            _running = false;
            _waveEvent.Set();
            _dataEvent.Set();

            if (_playThread != null)
            {
                try { _playThread.Join(1000); } catch { }
                _playThread = null;
            }

            lock (_deviceLock)
            {
                CloseDeviceInternal();
            }

            _waveEvent.Close();
            _dataEvent.Close();
        }
    }

    internal sealed class UdpMicReceiver : IDisposable
    {
        private const int SIO_UDP_CONNRESET = -1744830452; // 0x9800000C

        private readonly WaveOutPlayer _player;
        private readonly Action<string> _logger;
        private readonly int _port;

        private UdpClient _udp;
        private Thread _recvThread;
        private volatile bool _running;

        private readonly object _statsLock = new object();
        private string _clientEndpoint = "";
        private DateTime _lastPacketUtc = DateTime.MinValue;
        private long _packetsReceived;
        private long _packetsLost;
        private long _bytesReceived;
        private uint _lastSeq;
        private bool _hasSeq;
        private string _socketError = "";

        public UdpMicReceiver(int port, WaveOutPlayer player, Action<string> logger)
        {
            _port = port;
            _player = player;
            _logger = logger;
        }

        public int Port { get { return _port; } }
        public string SocketError { get { return _socketError; } }

        public bool IsConnected
        {
            get
            {
                lock (_statsLock)
                {
                    if (_lastPacketUtc == DateTime.MinValue)
                        return false;
                    return (DateTime.UtcNow - _lastPacketUtc).TotalSeconds < 3.5;
                }
            }
        }

        public void GetStats(out string clientEp, out long pkts, out long lost, out long bytes)
        {
            lock (_statsLock)
            {
                clientEp = _clientEndpoint;
                pkts = _packetsReceived;
                lost = _packetsLost;
                bytes = _bytesReceived;
            }
        }

        public bool Start()
        {
            try
            {
                _udp = new UdpClient();
                _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                _udp.EnableBroadcast = true;
                _udp.Client.ReceiveBufferSize = 262144;
                try
                {
                    _udp.Client.IOControl(SIO_UDP_CONNRESET, new byte[] { 0 }, null);
                }
                catch
                {
                    // Non-critical
                }
                _udp.Client.Bind(new IPEndPoint(IPAddress.Any, _port));

                _running = true;
                _socketError = "";
                _recvThread = new Thread(ReceiveLoop);
                _recvThread.Name = "UdpMicReceiveThread";
                _recvThread.IsBackground = true;
                _recvThread.Priority = ThreadPriority.AboveNormal;
                _recvThread.Start();

                Log(string.Format("UDP 监听已启动: 0.0.0.0:{0}", _port));
                return true;
            }
            catch (Exception ex)
            {
                _socketError = ex.Message;
                Log("UDP 监听失败: " + ex.Message);
                return false;
            }
        }

        private void ReceiveLoop()
        {
            IPEndPoint remoteEP = new IPEndPoint(IPAddress.Any, 0);
            DateTime lastAckUtc = DateTime.MinValue;

            while (_running)
            {
                try
                {
                    byte[] data = _udp.Receive(ref remoteEP);
                    if (!Protocol.IsValidHeader(data, data.Length))
                        continue;

                    byte pktType = data[5];
                    byte channels = data[6];
                    byte bits = data[7];
                    uint seq = Protocol.ReadUInt32LE(data, 8);
                    uint sampleRate = Protocol.ReadUInt32LE(data, 12);

                    if (pktType == Protocol.PktDiscoverReq)
                    {
                        string info = Environment.MachineName + "|" + _port;
                        byte[] payload = Encoding.UTF8.GetBytes(info);
                        byte[] resp = Protocol.BuildPacket(Protocol.PktDiscoverResp, seq, 48000, 1, 16, payload);
                        _udp.Send(resp, resp.Length, remoteEP);
                        Log(string.Format("已响应手机自动发现: {0}", remoteEP.Address));
                    }
                    else if (pktType == Protocol.PktHeartbeat)
                    {
                        byte[] ack = Protocol.BuildPacket(Protocol.PktHeartbeatAck, seq, (uint)_player.SampleRate, (byte)_player.Channels, 16, null);
                        _udp.Send(ack, ack.Length, remoteEP);
                        lock (_statsLock)
                        {
                            _clientEndpoint = remoteEP.Address.ToString();
                            _lastPacketUtc = DateTime.UtcNow;
                        }
                    }
                    else if (pktType == Protocol.PktStop)
                    {
                        _player.ResetBuffer();
                        lock (_statsLock)
                        {
                            _lastPacketUtc = DateTime.MinValue;
                            _hasSeq = false;
                        }
                        Log(string.Format("手机主动停止传输: {0}", remoteEP.Address));
                    }
                    else if (pktType == Protocol.PktAudio)
                    {
                        if (bits != 16)
                            continue;

                        int payloadLen = data.Length - Protocol.HeaderSize;
                        if (payloadLen <= 0)
                            continue;

                        int sr = (int)sampleRate;
                        int ch = (channels == 2) ? 2 : 1;
                        if (sr >= 8000 && sr <= 96000)
                        {
                            _player.EnsureFormat(sr, ch);
                        }

                        _player.PushPcm(data, Protocol.HeaderSize, payloadLen);

                        DateTime now = DateTime.UtcNow;
                        bool firstConnect = false;
                        lock (_statsLock)
                        {
                            if (_lastPacketUtc == DateTime.MinValue || (now - _lastPacketUtc).TotalSeconds > 3.5)
                            {
                                firstConnect = true;
                                _hasSeq = false;
                            }

                            if (_hasSeq && seq > _lastSeq + 1 && (seq - _lastSeq) < 1000)
                            {
                                _packetsLost += (seq - _lastSeq - 1);
                            }
                            _lastSeq = seq;
                            _hasSeq = true;
                            _packetsReceived++;
                            _bytesReceived += payloadLen;
                            _clientEndpoint = remoteEP.Address.ToString();
                            _lastPacketUtc = now;
                        }

                        if (firstConnect)
                        {
                            Log(string.Format("已连接手机: {0} ({1} Hz, {2}ch)", remoteEP.Address, sr, ch));
                        }

                        // Periodically ACK so Android knows PC is still alive & reachable
                        if ((now - lastAckUtc).TotalMilliseconds >= 1000)
                        {
                            lastAckUtc = now;
                            byte[] ack = Protocol.BuildPacket(Protocol.PktHeartbeatAck, seq, sampleRate, channels, 16, null);
                            try { _udp.Send(ack, ack.Length, remoteEP); } catch { }
                        }
                    }
                }
                catch (SocketException)
                {
                    if (!_running) break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    if (_running)
                        Log("接收异常: " + ex.Message);
                }
            }
        }

        private void Log(string msg)
        {
            if (_logger != null)
                _logger(msg);
        }

        public void Dispose()
        {
            _running = false;
            if (_udp != null)
            {
                try { _udp.Close(); } catch { }
                _udp = null;
            }
            if (_recvThread != null)
            {
                try { _recvThread.Join(1000); } catch { }
                _recvThread = null;
            }
        }
    }

    internal static class NetworkInfoHelper
    {
        public static List<string> GetLocalIPv4Addresses()
        {
            List<string> results = new List<string>();
            try
            {
                NetworkInterface[] nics = NetworkInterface.GetAllNetworkInterfaces();
                for (int i = 0; i < nics.Length; i++)
                {
                    NetworkInterface nic = nics[i];
                    if (nic.OperationalStatus != OperationalStatus.Up)
                        continue;
                    if (nic.NetworkInterfaceType == NetworkInterfaceType.Loopback ||
                        nic.NetworkInterfaceType == NetworkInterfaceType.Tunnel)
                        continue;

                    IPInterfaceProperties props = nic.GetIPProperties();
                    UnicastIPAddressInformationCollection addrs = props.UnicastAddresses;
                    for (int j = 0; j < addrs.Count; j++)
                    {
                        IPAddress ip = addrs[j].Address;
                        if (ip.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ip))
                        {
                            string s = ip.ToString();
                            if (!s.StartsWith("169.254."))
                            {
                                results.Add(string.Format("{0} ({1})", s, nic.Name));
                            }
                        }
                    }
                }
            }
            catch
            {
                // Ignore
            }
            if (results.Count == 0)
                results.Add("未检测到局域网 IP");
            return results;
        }
    }

    internal static class TrayIconFactory
    {
        public static Icon CreateMicIcon(Color bgColor, bool muted)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);

                using (SolidBrush bg = new SolidBrush(bgColor))
                {
                    g.FillEllipse(bg, 1, 1, 30, 30);
                }

                // Microphone capsule
                using (SolidBrush white = new SolidBrush(Color.White))
                {
                    g.FillRectangle(white, 12, 7, 8, 11);
                    g.FillEllipse(white, 12, 5, 8, 6);
                    g.FillEllipse(white, 12, 15, 8, 6);
                }

                // Stand arc & base
                using (Pen whitePen = new Pen(Color.White, 2.2f))
                {
                    g.DrawArc(whitePen, 9, 12, 14, 11, 0, 180);
                    g.DrawLine(whitePen, 16, 23, 16, 26);
                    g.DrawLine(whitePen, 12, 26, 20, 26);
                }

                if (muted)
                {
                    using (Pen slash = new Pen(Color.FromArgb(230, 40, 40), 3.0f))
                    {
                        g.DrawLine(slash, 6, 6, 26, 26);
                    }
                }

                IntPtr hIcon = bmp.GetHicon();
                Icon icon = (Icon)Icon.FromHandle(hIcon).Clone();
                WinMmNative.DestroyIcon(hIcon);
                return icon;
            }
        }
    }

    internal sealed class StatusForm : Form
    {
        private readonly TrayDaemonContext _ctx;
        private readonly Label _lblState;
        private readonly Label _lblIp;
        private readonly Label _lblStats;
        private readonly ProgressBar _levelBar;
        private readonly ComboBox _cmbDevice;
        private readonly ComboBox _cmbGain;
        private readonly ComboBox _cmbBuffer;
        private readonly CheckBox _chkMute;
        private readonly CheckBox _chkAutoStart;
        private readonly TextBox _txtLog;
        private readonly Button _btnRescan;

        private bool _updatingUi;

        public StatusForm(TrayDaemonContext ctx)
        {
            _ctx = ctx;

            Text = "Phone2Win-Mic Windows 守护端";
            ClientSize = new Size(460, 430);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            Font = new Font("Microsoft YaHei UI", 9f);

            _lblState = new Label();
            _lblState.Location = new Point(14, 12);
            _lblState.Size = new Size(432, 24);
            _lblState.Font = new Font("Microsoft YaHei UI", 10f, FontStyle.Bold);
            Controls.Add(_lblState);

            _lblIp = new Label();
            _lblIp.Location = new Point(14, 40);
            _lblIp.Size = new Size(432, 36);
            Controls.Add(_lblIp);

            _lblStats = new Label();
            _lblStats.Location = new Point(14, 80);
            _lblStats.Size = new Size(432, 22);
            Controls.Add(_lblStats);

            Label lblMeter = new Label();
            lblMeter.Text = "输入电平:";
            lblMeter.Location = new Point(14, 108);
            lblMeter.Size = new Size(72, 22);
            Controls.Add(lblMeter);

            _levelBar = new ProgressBar();
            _levelBar.Location = new Point(90, 107);
            _levelBar.Size = new Size(356, 18);
            _levelBar.Minimum = 0;
            _levelBar.Maximum = 100;
            Controls.Add(_levelBar);

            Label lblDev = new Label();
            lblDev.Text = "输出设备:";
            lblDev.Location = new Point(14, 140);
            lblDev.Size = new Size(72, 22);
            Controls.Add(lblDev);

            _cmbDevice = new ComboBox();
            _cmbDevice.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbDevice.Location = new Point(90, 137);
            _cmbDevice.Size = new Size(276, 24);
            _cmbDevice.SelectedIndexChanged += OnDeviceChanged;
            Controls.Add(_cmbDevice);

            _btnRescan = new Button();
            _btnRescan.Text = "刷新";
            _btnRescan.Location = new Point(372, 136);
            _btnRescan.Size = new Size(74, 26);
            _btnRescan.Click += delegate { _ctx.RescanDevices(true); RefreshDeviceList(); };
            Controls.Add(_btnRescan);

            Label lblGain = new Label();
            lblGain.Text = "音量增益:";
            lblGain.Location = new Point(14, 174);
            lblGain.Size = new Size(72, 22);
            Controls.Add(lblGain);

            _cmbGain = new ComboBox();
            _cmbGain.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbGain.Location = new Point(90, 171);
            _cmbGain.Size = new Size(110, 24);
            _cmbGain.Items.AddRange(new object[] { "50%", "80%", "100%", "150%", "200%", "300%" });
            _cmbGain.SelectedIndexChanged += OnGainChanged;
            Controls.Add(_cmbGain);

            Label lblBuf = new Label();
            lblBuf.Text = "抗抖动缓冲:";
            lblBuf.Location = new Point(214, 174);
            lblBuf.Size = new Size(80, 22);
            Controls.Add(lblBuf);

            _cmbBuffer = new ComboBox();
            _cmbBuffer.DropDownStyle = ComboBoxStyle.DropDownList;
            _cmbBuffer.Location = new Point(296, 171);
            _cmbBuffer.Size = new Size(150, 24);
            _cmbBuffer.Items.AddRange(new object[] { "极速 (20ms)", "均衡 (40ms)", "稳健 (80ms)", "弱网 (120ms)" });
            _cmbBuffer.SelectedIndexChanged += OnBufferChanged;
            Controls.Add(_cmbBuffer);

            _chkMute = new CheckBox();
            _chkMute.Text = "静音输出";
            _chkMute.Location = new Point(90, 206);
            _chkMute.Size = new Size(110, 22);
            _chkMute.CheckedChanged += OnMuteChanged;
            Controls.Add(_chkMute);

            _chkAutoStart = new CheckBox();
            _chkAutoStart.Text = "Windows 开机自动静默启动";
            _chkAutoStart.Location = new Point(214, 206);
            _chkAutoStart.Size = new Size(230, 22);
            _chkAutoStart.CheckedChanged += OnAutoStartChanged;
            Controls.Add(_chkAutoStart);

            Label lblLog = new Label();
            lblLog.Text = "运行日志 (关闭此窗口将最小化到右下角托盘继续后台运行):";
            lblLog.Location = new Point(14, 236);
            lblLog.Size = new Size(432, 20);
            Controls.Add(lblLog);

            _txtLog = new TextBox();
            _txtLog.Multiline = true;
            _txtLog.ReadOnly = true;
            _txtLog.ScrollBars = ScrollBars.Vertical;
            _txtLog.Location = new Point(14, 258);
            _txtLog.Size = new Size(432, 158);
            _txtLog.BackColor = Color.White;
            Controls.Add(_txtLog);

            FormClosing += OnFormClosing;

            RefreshDeviceList();
            SyncControlsFromConfig();
            UpdateLiveView();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
            }
        }

        public void RefreshDeviceList()
        {
            _updatingUi = true;
            try
            {
                _cmbDevice.Items.Clear();
                List<AudioDeviceInfo> devices = _ctx.GetDevices();
                int selected = -1;
                for (int i = 0; i < devices.Count; i++)
                {
                    _cmbDevice.Items.Add(devices[i]);
                    if (devices[i].Name == _ctx.Player.ActiveDeviceName)
                        selected = i;
                }
                if (selected >= 0)
                    _cmbDevice.SelectedIndex = selected;
            }
            finally
            {
                _updatingUi = false;
            }
        }

        public void SyncControlsFromConfig()
        {
            _updatingUi = true;
            try
            {
                string gStr = _ctx.Config.GainPercent + "%";
                int gIdx = _cmbGain.Items.IndexOf(gStr);
                _cmbGain.SelectedIndex = (gIdx >= 0) ? gIdx : 2;

                int ms = _ctx.Config.TargetBufferMs;
                if (ms <= 25) _cmbBuffer.SelectedIndex = 0;
                else if (ms <= 55) _cmbBuffer.SelectedIndex = 1;
                else if (ms <= 100) _cmbBuffer.SelectedIndex = 2;
                else _cmbBuffer.SelectedIndex = 3;

                _chkMute.Checked = _ctx.Config.Muted;
                _chkAutoStart.Checked = AutoStartHelper.IsEnabled();
            }
            finally
            {
                _updatingUi = false;
            }
        }

        public void UpdateLiveView()
        {
            if (!Visible)
                return;

            string clientEp;
            long pkts, lost, bytes;
            _ctx.Receiver.GetStats(out clientEp, out pkts, out lost, out bytes);

            if (!_ctx.Player.IsDeviceOpen)
            {
                _lblState.Text = "状态: 虚拟声卡未就绪 - " + _ctx.Player.LastError;
                _lblState.ForeColor = Color.Firebrick;
            }
            else if (_ctx.Receiver.IsConnected)
            {
                _lblState.Text = string.Format("状态: 正在接收手机音频 ({0}){1}",
                    clientEp, _ctx.Player.Muted ? " [已静音]" : "");
                _lblState.ForeColor = _ctx.Player.Muted ? Color.DarkGray : Color.ForestGreen;
            }
            else
            {
                _lblState.Text = "状态: 守护中，等待手机连接...";
                _lblState.ForeColor = Color.SteelBlue;
            }

            List<string> ips = NetworkInfoHelper.GetLocalIPv4Addresses();
            _lblIp.Text = string.Format("监听端口: UDP {0}  |  本机局域网 IP:\r\n{1}",
                _ctx.Config.Port, string.Join(" ;  ", ips.ToArray()));

            _lblStats.Text = string.Format("格式: {0}Hz {1}ch | 缓冲: {2}ms | 收包: {3} | 丢包: {4}",
                _ctx.Player.SampleRate,
                _ctx.Player.Channels,
                _ctx.Player.BufferedMs,
                pkts,
                lost);

            int level = (int)(_ctx.Player.CurrentLevel * 100f);
            if (level < 0) level = 0;
            if (level > 100) level = 100;
            _levelBar.Value = level;

            string logText = _ctx.GetLogText();
            if (_txtLog.Text != logText)
            {
                _txtLog.Text = logText;
                _txtLog.SelectionStart = _txtLog.Text.Length;
                _txtLog.ScrollToCaret();
            }
        }

        private void OnDeviceChanged(object sender, EventArgs e)
        {
            if (_updatingUi) return;
            AudioDeviceInfo info = _cmbDevice.SelectedItem as AudioDeviceInfo;
            if (info != null)
            {
                _ctx.SelectOutputDevice(info);
            }
        }

        private void OnGainChanged(object sender, EventArgs e)
        {
            if (_updatingUi) return;
            string s = _cmbGain.SelectedItem as string;
            if (s != null)
            {
                int val;
                if (int.TryParse(s.Replace("%", ""), out val))
                    _ctx.SetGain(val);
            }
        }

        private void OnBufferChanged(object sender, EventArgs e)
        {
            if (_updatingUi) return;
            int[] values = new int[] { 20, 40, 80, 120 };
            int idx = _cmbBuffer.SelectedIndex;
            if (idx >= 0 && idx < values.Length)
                _ctx.SetTargetBufferMs(values[idx]);
        }

        private void OnMuteChanged(object sender, EventArgs e)
        {
            if (_updatingUi) return;
            _ctx.SetMuted(_chkMute.Checked);
        }

        private void OnAutoStartChanged(object sender, EventArgs e)
        {
            if (_updatingUi) return;
            _ctx.SetAutoStart(_chkAutoStart.Checked);
        }
    }

    internal sealed class TrayDaemonContext : ApplicationContext
    {
        public readonly AppConfig Config;
        public readonly WaveOutPlayer Player;
        public readonly UdpMicReceiver Receiver;

        private readonly NotifyIcon _trayIcon;
        private readonly ContextMenuStrip _menu;
        private readonly ToolStripMenuItem _miStatus;
        private readonly ToolStripMenuItem _miDevices;
        private readonly ToolStripMenuItem _miGain;
        private readonly ToolStripMenuItem _miMute;
        private readonly ToolStripMenuItem _miAutoStart;

        private readonly Icon _iconWaiting;
        private readonly Icon _iconConnected;
        private readonly Icon _iconMuted;
        private readonly Icon _iconError;

        private readonly System.Windows.Forms.Timer _uiTimer;
        private readonly object _logLock = new object();
        private readonly Queue<string> _logs = new Queue<string>();

        private List<AudioDeviceInfo> _devices = new List<AudioDeviceInfo>();
        private StatusForm _statusForm;
        private int _lastIconState = -1;

        public TrayDaemonContext(bool startMinimized)
        {
            Config = new AppConfig();
            Config.Load();

            Player = new WaveOutPlayer();
            Player.GainPercent = Config.GainPercent;
            Player.TargetBufferMs = Config.TargetBufferMs;
            Player.Muted = Config.Muted;

            Receiver = new UdpMicReceiver(Config.Port, Player, AppendLog);

            _iconWaiting = TrayIconFactory.CreateMicIcon(Color.FromArgb(40, 120, 200), false);
            _iconConnected = TrayIconFactory.CreateMicIcon(Color.FromArgb(40, 165, 70), false);
            _iconMuted = TrayIconFactory.CreateMicIcon(Color.FromArgb(120, 120, 120), true);
            _iconError = TrayIconFactory.CreateMicIcon(Color.FromArgb(200, 50, 50), false);

            _menu = new ContextMenuStrip();

            _miStatus = new ToolStripMenuItem("状态: 初始化中...");
            _miStatus.Enabled = false;
            _menu.Items.Add(_miStatus);
            _menu.Items.Add(new ToolStripSeparator());

            ToolStripMenuItem miOpen = new ToolStripMenuItem("打开状态窗口");
            miOpen.Click += delegate { ShowStatusWindow(); };
            _menu.Items.Add(miOpen);

            _miDevices = new ToolStripMenuItem("输出设备 (VB-Cable)");
            _menu.Items.Add(_miDevices);

            _miGain = new ToolStripMenuItem("音量增益");
            int[] gains = new int[] { 50, 80, 100, 150, 200, 300 };
            for (int i = 0; i < gains.Length; i++)
            {
                int g = gains[i];
                ToolStripMenuItem item = new ToolStripMenuItem(g + "%");
                item.Tag = g;
                item.Click += delegate { SetGain(g); };
                _miGain.DropDownItems.Add(item);
            }
            _menu.Items.Add(_miGain);

            _miMute = new ToolStripMenuItem("静音输出");
            _miMute.CheckOnClick = false;
            _miMute.Click += delegate { SetMuted(!Config.Muted); };
            _menu.Items.Add(_miMute);

            ToolStripMenuItem miRescan = new ToolStripMenuItem("重新扫描音频设备");
            miRescan.Click += delegate { RescanDevices(true); };
            _menu.Items.Add(miRescan);

            _menu.Items.Add(new ToolStripSeparator());

            _miAutoStart = new ToolStripMenuItem("开机自动启动");
            _miAutoStart.CheckOnClick = false;
            _miAutoStart.Click += delegate { SetAutoStart(!AutoStartHelper.IsEnabled()); };
            _menu.Items.Add(_miAutoStart);

            ToolStripMenuItem miExit = new ToolStripMenuItem("退出守护进程");
            miExit.Click += delegate { ExitThread(); };
            _menu.Items.Add(miExit);

            _trayIcon = new NotifyIcon();
            _trayIcon.Icon = _iconWaiting;
            _trayIcon.Text = "Phone2Win-Mic";
            _trayIcon.ContextMenuStrip = _menu;
            _trayIcon.Visible = true;
            _trayIcon.DoubleClick += delegate { ShowStatusWindow(); };

            RescanDevices(false);
            Receiver.Start();

            SystemEvents.PowerModeChanged += OnPowerModeChanged;

            _uiTimer = new System.Windows.Forms.Timer();
            _uiTimer.Interval = 200;
            _uiTimer.Tick += OnUiTick;
            _uiTimer.Start();

            UpdateMenuChecks();

            if (!Player.IsDeviceOpen)
            {
                _trayIcon.ShowBalloonTip(5000, "Phone2Win-Mic",
                    "未检测到 VB-Cable (CABLE Input)，请先安装 VB-CABLE 虚拟声卡驱动，或在托盘菜单中手动切换设备。",
                    ToolTipIcon.Warning);
            }
            else if (!startMinimized)
            {
                ShowStatusWindow();
            }
        }

        public List<AudioDeviceInfo> GetDevices()
        {
            return new List<AudioDeviceInfo>(_devices);
        }

        public void RescanDevices(bool forceReopen)
        {
            _devices = WinMmNative.EnumerateOutputDevices();
            RebuildDeviceMenu();

            AudioDeviceInfo match = FindPreferredDevice(_devices, Config.DeviceNameHint);
            if (match != null)
            {
                if (forceReopen || !Player.IsDeviceOpen || Player.ActiveDeviceName != match.Name)
                {
                    Player.Start(match.DeviceId, match.Name, Player.SampleRate, Player.Channels);
                    if (Player.IsDeviceOpen)
                    {
                        AppendLog("已绑定音频输出设备: " + match.Name);
                    }
                    else
                    {
                        AppendLog("打开设备失败: " + Player.LastError);
                    }
                }
            }
            else
            {
                Player.Start(-2, "", Player.SampleRate, Player.Channels);
                AppendLog("未找到包含 \"" + Config.DeviceNameHint + "\" 的输出设备 (请确认已安装 VB-Cable)");
            }

            UpdateMenuChecks();
            if (_statusForm != null && !_statusForm.IsDisposed)
                _statusForm.RefreshDeviceList();
        }

        private static AudioDeviceInfo FindPreferredDevice(List<AudioDeviceInfo> list, string hint)
        {
            if (list == null || list.Count == 0)
                return null;

            // 1. Exact or substring match on configured hint (default: "CABLE Input")
            if (!string.IsNullOrEmpty(hint))
            {
                for (int i = 0; i < list.Count; i++)
                {
                    if (list[i].Name.IndexOf(hint, StringComparison.OrdinalIgnoreCase) >= 0)
                        return list[i];
                }
            }

            // 2. Fallback search for VB-Audio CABLE Input
            for (int i = 0; i < list.Count; i++)
            {
                if (list[i].Name.IndexOf("CABLE Input", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    list[i].Name.IndexOf("VB-Audio", StringComparison.OrdinalIgnoreCase) >= 0)
                    return list[i];
            }

            return null;
        }

        private void RebuildDeviceMenu()
        {
            _miDevices.DropDownItems.Clear();
            if (_devices.Count == 0)
            {
                ToolStripMenuItem empty = new ToolStripMenuItem("(无可用音频输出设备)");
                empty.Enabled = false;
                _miDevices.DropDownItems.Add(empty);
                return;
            }

            for (int i = 0; i < _devices.Count; i++)
            {
                AudioDeviceInfo dev = _devices[i];
                ToolStripMenuItem item = new ToolStripMenuItem(dev.Name);
                item.Tag = dev;
                item.Checked = (dev.Name == Player.ActiveDeviceName);
                item.Click += delegate { SelectOutputDevice(dev); };
                _miDevices.DropDownItems.Add(item);
            }
        }

        public void SelectOutputDevice(AudioDeviceInfo dev)
        {
            if (dev == null) return;
            Config.DeviceNameHint = dev.Name;
            Config.Save();
            Player.Start(dev.DeviceId, dev.Name, Player.SampleRate, Player.Channels);
            AppendLog("已切换输出设备: " + dev.Name);
            RebuildDeviceMenu();
            if (_statusForm != null && !_statusForm.IsDisposed)
                _statusForm.RefreshDeviceList();
        }

        public void SetGain(int gainPercent)
        {
            Config.GainPercent = gainPercent;
            Config.Save();
            Player.GainPercent = gainPercent;
            AppendLog("音量增益调整为: " + gainPercent + "%");
            UpdateMenuChecks();
            if (_statusForm != null && !_statusForm.IsDisposed)
                _statusForm.SyncControlsFromConfig();
        }

        public void SetTargetBufferMs(int ms)
        {
            Config.TargetBufferMs = ms;
            Config.Save();
            Player.TargetBufferMs = ms;
            AppendLog("目标抗抖动缓冲调整为: " + ms + "ms");
            if (_statusForm != null && !_statusForm.IsDisposed)
                _statusForm.SyncControlsFromConfig();
        }

        public void SetMuted(bool muted)
        {
            Config.Muted = muted;
            Config.Save();
            Player.Muted = muted;
            AppendLog(muted ? "已静音输出" : "已取消静音");
            UpdateMenuChecks();
            if (_statusForm != null && !_statusForm.IsDisposed)
                _statusForm.SyncControlsFromConfig();
        }

        public void SetAutoStart(bool enable)
        {
            bool ok = AutoStartHelper.SetEnabled(enable);
            if (ok)
                AppendLog(enable ? "已启用开机自动启动" : "已关闭开机自动启动");
            else
                AppendLog("修改开机自启失败");
            UpdateMenuChecks();
            if (_statusForm != null && !_statusForm.IsDisposed)
                _statusForm.SyncControlsFromConfig();
        }

        private void UpdateMenuChecks()
        {
            _miMute.Checked = Config.Muted;
            _miAutoStart.Checked = AutoStartHelper.IsEnabled();

            for (int i = 0; i < _miGain.DropDownItems.Count; i++)
            {
                ToolStripMenuItem item = _miGain.DropDownItems[i] as ToolStripMenuItem;
                if (item != null && item.Tag is int)
                    item.Checked = ((int)item.Tag == Config.GainPercent);
            }

            for (int i = 0; i < _miDevices.DropDownItems.Count; i++)
            {
                ToolStripMenuItem item = _miDevices.DropDownItems[i] as ToolStripMenuItem;
                if (item != null && item.Tag is AudioDeviceInfo)
                {
                    AudioDeviceInfo dev = (AudioDeviceInfo)item.Tag;
                    item.Checked = (dev.Name == Player.ActiveDeviceName);
                }
            }
        }

        private void ShowStatusWindow()
        {
            if (_statusForm == null || _statusForm.IsDisposed)
            {
                _statusForm = new StatusForm(this);
                _statusForm.Icon = _iconWaiting;
            }
            _statusForm.SyncControlsFromConfig();
            _statusForm.RefreshDeviceList();
            if (!_statusForm.Visible)
                _statusForm.Show();
            if (_statusForm.WindowState == FormWindowState.Minimized)
                _statusForm.WindowState = FormWindowState.Normal;
            _statusForm.Activate();
        }

        private void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            if (e.Mode == PowerModes.Resume)
            {
                AppendLog("检测到系统从休眠唤醒，正在恢复音频设备...");
                // Re-open waveOut after sleep resume
                ThreadPool.QueueUserWorkItem(delegate
                {
                    Thread.Sleep(1500);
                    RescanDevices(true);
                });
            }
        }

        private void OnUiTick(object sender, EventArgs e)
        {
            int state;
            string tip;

            if (!Player.IsDeviceOpen || !string.IsNullOrEmpty(Receiver.SocketError))
            {
                state = 3;
                tip = "Phone2Win-Mic: 未就绪 (点击查看)";
                _miStatus.Text = !string.IsNullOrEmpty(Receiver.SocketError)
                    ? ("UDP 错误: " + Receiver.SocketError)
                    : "未找到 VB-Cable 输出设备";
            }
            else if (Config.Muted)
            {
                state = 2;
                tip = "Phone2Win-Mic: 已静音";
                _miStatus.Text = "状态: 已静音 (" + Player.ActiveDeviceName + ")";
            }
            else if (Receiver.IsConnected)
            {
                state = 1;
                string ep;
                long pkts, lost, bytes;
                Receiver.GetStats(out ep, out pkts, out lost, out bytes);
                tip = string.Format("Phone2Win-Mic: 已连接 {0}", ep);
                _miStatus.Text = string.Format("接收中: {0} ({1}Hz)", ep, Player.SampleRate);
            }
            else
            {
                state = 0;
                tip = string.Format("Phone2Win-Mic: 守护中 (UDP {0})", Config.Port);
                _miStatus.Text = string.Format("等待手机连接 (UDP {0})", Config.Port);
            }

            if (tip.Length > 63)
                tip = tip.Substring(0, 63);
            if (_trayIcon.Text != tip)
                _trayIcon.Text = tip;

            if (state != _lastIconState)
            {
                _lastIconState = state;
                if (state == 1) _trayIcon.Icon = _iconConnected;
                else if (state == 2) _trayIcon.Icon = _iconMuted;
                else if (state == 3) _trayIcon.Icon = _iconError;
                else _trayIcon.Icon = _iconWaiting;
            }

            if (_statusForm != null && !_statusForm.IsDisposed && _statusForm.Visible)
            {
                _statusForm.UpdateLiveView();
            }
        }

        public void AppendLog(string msg)
        {
            string line = string.Format("[{0}] {1}", DateTime.Now.ToString("HH:mm:ss"), msg);
            lock (_logLock)
            {
                _logs.Enqueue(line);
                while (_logs.Count > 80)
                    _logs.Dequeue();
            }
        }

        public string GetLogText()
        {
            lock (_logLock)
            {
                StringBuilder sb = new StringBuilder();
                foreach (string line in _logs)
                {
                    sb.AppendLine(line);
                }
                return sb.ToString();
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
                if (_uiTimer != null)
                {
                    _uiTimer.Stop();
                    _uiTimer.Dispose();
                }
                if (_statusForm != null && !_statusForm.IsDisposed)
                {
                    _statusForm.Dispose();
                }
                Receiver.Dispose();
                Player.Dispose();
                if (_trayIcon != null)
                {
                    _trayIcon.Visible = false;
                    _trayIcon.Dispose();
                }
                if (_menu != null) _menu.Dispose();
                if (_iconWaiting != null) _iconWaiting.Dispose();
                if (_iconConnected != null) _iconConnected.Dispose();
                if (_iconMuted != null) _iconMuted.Dispose();
                if (_iconError != null) _iconError.Dispose();
            }
            base.Dispose(disposing);
        }
    }
}
