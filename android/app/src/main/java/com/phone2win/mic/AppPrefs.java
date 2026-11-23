package com.phone2win.mic;

import android.content.Context;
import android.content.SharedPreferences;

public final class AppPrefs {
    private static final String PREF_NAME = "phone2win_mic_prefs";

    private static final String KEY_AUTO_DISCOVER = "auto_discover";
    private static final String KEY_MANUAL_IP = "manual_ip";
    private static final String KEY_LAST_PC_IP = "last_pc_ip";
    private static final String KEY_PORT = "port";
    private static final String KEY_SAMPLE_RATE = "sample_rate";
    private static final String KEY_AUDIO_SOURCE = "audio_source";
    private static final String KEY_GAIN_PERCENT = "gain_percent";
    private static final String KEY_NOISE_GATE = "noise_gate";
    private static final String KEY_HW_NS = "hw_ns";
    private static final String KEY_HW_AGC = "hw_agc";
    private static final String KEY_AUTO_BOOT = "auto_boot";

    private AppPrefs() {
    }

    private static SharedPreferences sp(Context ctx) {
        return ctx.getApplicationContext().getSharedPreferences(PREF_NAME, Context.MODE_PRIVATE);
    }

    public static boolean isAutoDiscover(Context ctx) {
        return sp(ctx).getBoolean(KEY_AUTO_DISCOVER, true);
    }

    public static void setAutoDiscover(Context ctx, boolean val) {
        sp(ctx).edit().putBoolean(KEY_AUTO_DISCOVER, val).apply();
    }

    public static String getManualIp(Context ctx) {
        return sp(ctx).getString(KEY_MANUAL_IP, "");
    }

    public static void setManualIp(Context ctx, String ip) {
        sp(ctx).edit().putString(KEY_MANUAL_IP, ip == null ? "" : ip.trim()).apply();
    }

    public static String getLastPcIp(Context ctx) {
        return sp(ctx).getString(KEY_LAST_PC_IP, "");
    }

    public static void setLastPcIp(Context ctx, String ip) {
        sp(ctx).edit().putString(KEY_LAST_PC_IP, ip == null ? "" : ip.trim()).apply();
    }

    public static int getPort(Context ctx) {
        return sp(ctx).getInt(KEY_PORT, Protocol.DEFAULT_PORT);
    }

    public static void setPort(Context ctx, int port) {
        if (port < 1024 || port > 65535) {
            port = Protocol.DEFAULT_PORT;
        }
        sp(ctx).edit().putInt(KEY_PORT, port).apply();
    }

    public static int getSampleRate(Context ctx) {
        return sp(ctx).getInt(KEY_SAMPLE_RATE, 48000);
    }

    public static void setSampleRate(Context ctx, int sampleRate) {
        sp(ctx).edit().putInt(KEY_SAMPLE_RATE, sampleRate).apply();
    }

    // 0 = VOICE_COMMUNICATION, 1 = MIC, 2 = VOICE_RECOGNITION
    public static int getAudioSourceIndex(Context ctx) {
        return sp(ctx).getInt(KEY_AUDIO_SOURCE, 0);
    }

    public static void setAudioSourceIndex(Context ctx, int index) {
        sp(ctx).edit().putInt(KEY_AUDIO_SOURCE, index).apply();
    }

    public static int getGainPercent(Context ctx) {
        return sp(ctx).getInt(KEY_GAIN_PERCENT, 100);
    }

    public static void setGainPercent(Context ctx, int percent) {
        sp(ctx).edit().putInt(KEY_GAIN_PERCENT, percent).apply();
    }

    // 0 .. 100 -> mapped to 0.000 .. 0.100 RMS threshold
    public static int getNoiseGatePercent(Context ctx) {
        return sp(ctx).getInt(KEY_NOISE_GATE, 0);
    }

    public static void setNoiseGatePercent(Context ctx, int percent) {
        sp(ctx).edit().putInt(KEY_NOISE_GATE, percent).apply();
    }

    public static boolean isHwNsEnabled(Context ctx) {
        return sp(ctx).getBoolean(KEY_HW_NS, true);
    }

    public static void setHwNsEnabled(Context ctx, boolean val) {
        sp(ctx).edit().putBoolean(KEY_HW_NS, val).apply();
    }

    public static boolean isHwAgcEnabled(Context ctx) {
        return sp(ctx).getBoolean(KEY_HW_AGC, false);
    }

    public static void setHwAgcEnabled(Context ctx, boolean val) {
        sp(ctx).edit().putBoolean(KEY_HW_AGC, val).apply();
    }

    public static boolean isAutoStartOnBoot(Context ctx) {
        return sp(ctx).getBoolean(KEY_AUTO_BOOT, false);
    }

    public static void setAutoStartOnBoot(Context ctx, boolean val) {
        sp(ctx).edit().putBoolean(KEY_AUTO_BOOT, val).apply();
    }
}
