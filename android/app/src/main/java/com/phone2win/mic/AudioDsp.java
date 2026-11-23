package com.phone2win.mic;

public final class AudioDsp {
    private static final boolean NATIVE_LOADED;

    static {
        boolean loaded = false;
        try {
            System.loadLibrary("audiodsp");
            loaded = true;
        } catch (Throwable ignored) {
            loaded = false;
        }
        NATIVE_LOADED = loaded;
    }

    private AudioDsp() {
    }

    private static native String nativeGetAbiName();

    private static native void nativeResetState();

    private static native float nativeProcessPcm16LE(
            byte[] buf,
            int offset,
            int lengthBytes,
            float gain,
            float noiseGateThreshold);

    public static String getAbiInfo() {
        if (NATIVE_LOADED) {
            try {
                return "Native JNI: " + nativeGetAbiName();
            } catch (Throwable ignored) {
            }
        }
        return "Java Fallback";
    }

    public static void resetState() {
        if (NATIVE_LOADED) {
            try {
                nativeResetState();
            } catch (Throwable ignored) {
            }
        }
    }

    public static float processPcm16LE(
            byte[] buf,
            int offset,
            int lengthBytes,
            float gain,
            float noiseGateThreshold) {
        if (NATIVE_LOADED) {
            try {
                return nativeProcessPcm16LE(buf, offset, lengthBytes, gain, noiseGateThreshold);
            } catch (Throwable ignored) {
            }
        }
        return javaFallbackProcess(buf, offset, lengthBytes, gain, noiseGateThreshold);
    }

    private static float javaFallbackProcess(
            byte[] buf,
            int offset,
            int lengthBytes,
            float gain,
            float noiseGateThreshold) {
        int sampleCount = lengthBytes / 2;
        if (sampleCount <= 0) {
            return 0f;
        }
        double sumSq = 0.0;
        for (int i = 0; i < sampleCount; i++) {
            int idx = offset + i * 2;
            short sample = (short) ((buf[idx] & 0xFF) | ((buf[idx + 1] & 0xFF) << 8));
            double norm = sample / 32768.0;
            sumSq += norm * norm;
        }
        float rms = (float) Math.sqrt(sumSq / sampleCount);
        float effGain = gain;
        if (noiseGateThreshold > 0.0005f && rms < noiseGateThreshold) {
            effGain *= 0.06f;
        }

        double outSumSq = 0.0;
        boolean modify = Math.abs(effGain - 1.0f) > 0.005f;
        for (int i = 0; i < sampleCount; i++) {
            int idx = offset + i * 2;
            short sample = (short) ((buf[idx] & 0xFF) | ((buf[idx + 1] & 0xFF) << 8));
            float val = sample;
            if (modify) {
                val = sample * effGain;
                if (val > 28000f) {
                    val = 28000f + 4767f * (float) Math.tanh((val - 28000f) / 4767f);
                } else if (val < -28000f) {
                    val = -28000f + 4767f * (float) Math.tanh((val + 28000f) / 4767f);
                }
                int clamped = Math.max(-32768, Math.min(32767, (int) val));
                buf[idx] = (byte) (clamped & 0xFF);
                buf[idx + 1] = (byte) ((clamped >> 8) & 0xFF);
            }
            double norm = val / 32768.0;
            outSumSq += norm * norm;
        }
        float outRms = (float) Math.sqrt(outSumSq / sampleCount);
        return Math.min(1.0f, outRms * 2.6f);
    }
}
