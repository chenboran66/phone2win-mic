#include <jni.h>
#include <stdint.h>
#include <math.h>

static float g_gateEnv = 1.0f;

JNIEXPORT jstring JNICALL
Java_com_phone2win_mic_AudioDsp_nativeGetAbiName(JNIEnv *env, jclass clazz) {
    (void)clazz;
#if defined(__aarch64__)
    const char *abi = "arm64-v8a (64-bit)";
#elif defined(__arm__)
    const char *abi = "armeabi-v7a (32-bit)";
#elif defined(__x86_64__)
    const char *abi = "x86_64 (64-bit)";
#elif defined(__i386__)
    const char *abi = "x86 (32-bit)";
#else
    const char *abi = "unknown";
#endif
    return (*env)->NewStringUTF(env, abi);
}

JNIEXPORT void JNICALL
Java_com_phone2win_mic_AudioDsp_nativeResetState(JNIEnv *env, jclass clazz) {
    (void)env;
    (void)clazz;
    g_gateEnv = 1.0f;
}

JNIEXPORT jfloat JNICALL
Java_com_phone2win_mic_AudioDsp_nativeProcessPcm16LE(
        JNIEnv *env,
        jclass clazz,
        jbyteArray buf,
        jint offset,
        jint lengthBytes,
        jfloat gain,
        jfloat noiseGateThreshold) {
    (void)clazz;
    if (buf == NULL || offset < 0 || lengthBytes < 2) {
        return 0.0f;
    }

    jsize arrLen = (*env)->GetArrayLength(env, buf);
    if (offset + lengthBytes > arrLen) {
        lengthBytes = arrLen - offset;
    }
    int sampleCount = lengthBytes / 2;
    if (sampleCount <= 0) {
        return 0.0f;
    }

    jbyte *raw = (*env)->GetByteArrayElements(env, buf, NULL);
    if (raw == NULL) {
        return 0.0f;
    }

    jbyte *data = raw + offset;

    // 1. Compute input RMS for noise gate decision
    double inputSumSq = 0.0;
    for (int i = 0; i < sampleCount; i++) {
        int idx = i * 2;
        uint16_t lo = (uint8_t)data[idx];
        uint16_t hi = (uint8_t)data[idx + 1];
        int16_t sample = (int16_t)(lo | (hi << 8));
        double norm = (double)sample / 32768.0;
        inputSumSq += norm * norm;
    }
    float inputRms = (float)sqrt(inputSumSq / (double)sampleCount);

    // 2. Smooth noise gate envelope (fast attack, smooth release)
    float targetEnv = 1.0f;
    if (noiseGateThreshold > 0.0005f) {
        if (inputRms >= noiseGateThreshold) {
            targetEnv = 1.0f;
        } else {
            // Attenuate background noise instead of abrupt hard mute
            targetEnv = 0.06f;
        }
    }
    if (targetEnv > g_gateEnv) {
        g_gateEnv = g_gateEnv * 0.25f + targetEnv * 0.75f;
    } else {
        g_gateEnv = g_gateEnv * 0.80f + targetEnv * 0.20f;
    }

    float effectiveGain = gain * g_gateEnv;
    int needModify = (fabsf(effectiveGain - 1.0f) > 0.005f);

    // 3. Apply gain + soft limiter and compute output RMS
    double outSumSq = 0.0;
    for (int i = 0; i < sampleCount; i++) {
        int idx = i * 2;
        uint16_t lo = (uint8_t)data[idx];
        uint16_t hi = (uint8_t)data[idx + 1];
        int16_t sample = (int16_t)(lo | (hi << 8));
        float val = (float)sample;

        if (needModify) {
            val *= effectiveGain;
            if (val > 28000.0f) {
                val = 28000.0f + 4767.0f * tanhf((val - 28000.0f) / 4767.0f);
            } else if (val < -28000.0f) {
                val = -28000.0f + 4767.0f * tanhf((val + 28000.0f) / 4767.0f);
            }

            int clamped = (int)val;
            if (clamped > 32767) clamped = 32767;
            else if (clamped < -32768) clamped = -32768;

            int16_t outSample = (int16_t)clamped;
            data[idx] = (jbyte)(outSample & 0xFF);
            data[idx + 1] = (jbyte)((outSample >> 8) & 0xFF);
        }

        double norm = (double)val / 32768.0;
        outSumSq += norm * norm;
    }

    (*env)->ReleaseByteArrayElements(env, buf, raw, needModify ? 0 : JNI_ABORT);

    float outRms = (float)sqrt(outSumSq / (double)sampleCount);
    float level = outRms * 2.6f;
    if (level > 1.0f) level = 1.0f;
    if (level < 0.0f) level = 0.0f;
    return level;
}
