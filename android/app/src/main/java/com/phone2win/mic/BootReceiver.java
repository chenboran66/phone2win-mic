package com.phone2win.mic;

import android.Manifest;
import android.content.BroadcastReceiver;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.os.Build;

public class BootReceiver extends BroadcastReceiver {
    @Override
    public void onReceive(Context context, Intent intent) {
        if (context == null || intent == null) {
            return;
        }
        String action = intent.getAction();
        if (action == null) {
            return;
        }

        if (!AppPrefs.isAutoStartOnBoot(context)) {
            return;
        }

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            if (context.checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                MicForegroundService.showBootPromptNotification(context);
                return;
            }
        }

        try {
            MicForegroundService.startStreaming(context, true);
        } catch (Throwable ignored) {
            // Android 14+ may block microphone foreground service directly from BOOT_COMPLETED;
            // fall back cleanly to an actionable high-priority notification.
            MicForegroundService.showBootPromptNotification(context);
        }
    }
}
