package com.phone2win.mic;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.Context;
import android.content.Intent;
import android.content.pm.PackageManager;
import android.graphics.Color;
import android.net.Uri;
import android.os.Build;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.os.PowerManager;
import android.provider.Settings;
import android.view.View;
import android.widget.AdapterView;
import android.widget.ArrayAdapter;
import android.widget.Button;
import android.widget.CheckBox;
import android.widget.CompoundButton;
import android.widget.EditText;
import android.widget.ProgressBar;
import android.widget.SeekBar;
import android.widget.Spinner;
import android.widget.TextView;
import android.widget.Toast;

import java.util.ArrayList;
import java.util.List;

public class MainActivity extends Activity {
    public static final String EXTRA_AUTO_START = "extra_auto_start";
    private static final int REQ_PERMISSIONS = 1001;

    private static final int[] SAMPLE_RATES = new int[]{48000, 44100, 16000};
    private static final String[] SAMPLE_RATE_LABELS = new String[]{
            "48000 Hz (推荐，匹配 VB-Cable)",
            "44100 Hz (标准 CD 音质)",
            "16000 Hz (省流，适合老旧手机/弱网)"
    };
    private static final String[] AUDIO_SOURCE_LABELS = new String[]{
            "语音通话 (VOICE_COMMUNICATION，推荐)",
            "原始麦克风 (MIC，高保真)",
            "语音识别 (VOICE_RECOGNITION，人声清晰)"
    };

    private TextView tvStatus;
    private TextView tvPcInfo;
    private TextView tvAbiInfo;
    private ProgressBar pbLevel;
    private Button btnToggle;
    private Button btnRediscover;
    private Button btnBatteryOpt;
    private CheckBox cbAutoDiscover;
    private EditText etPcIp;
    private EditText etPort;
    private Spinner spSampleRate;
    private Spinner spAudioSource;
    private TextView tvGainVal;
    private SeekBar sbGain;
    private TextView tvGateVal;
    private SeekBar sbGate;
    private CheckBox cbHwNs;
    private CheckBox cbHwAgc;
    private CheckBox cbAutoBoot;

    private final Handler uiHandler = new Handler(Looper.getMainLooper());
    private boolean pendingStartAfterPerm = false;
    private boolean initDone = false;

    private final Runnable uiRefreshTask = new Runnable() {
        @Override
        public void run() {
            updateLiveStatus();
            uiHandler.postDelayed(this, 150);
        }
    };

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        setContentView(R.layout.activity_main);
        MicForegroundService.ensureNotificationChannels(this);

        bindViews();
        loadSavedSettings();
        bindListeners();
        initDone = true;

        handleLaunchIntent(getIntent());
    }

    @Override
    protected void onNewIntent(Intent intent) {
        super.onNewIntent(intent);
        setIntent(intent);
        handleLaunchIntent(intent);
    }

    private void handleLaunchIntent(Intent intent) {
        if (intent != null && intent.getBooleanExtra(EXTRA_AUTO_START, false)) {
            intent.removeExtra(EXTRA_AUTO_START);
            if (MicForegroundService.currentState == MicForegroundService.STATE_STOPPED
                    || MicForegroundService.currentState == MicForegroundService.STATE_ERROR) {
                attemptStartStreaming();
            }
        }
    }

    @Override
    protected void onResume() {
        super.onResume();
        updateBatteryButtonState();
        updateLiveStatus();
        uiHandler.removeCallbacks(uiRefreshTask);
        uiHandler.post(uiRefreshTask);
    }

    @Override
    protected void onPause() {
        super.onPause();
        saveTextInputs();
        uiHandler.removeCallbacks(uiRefreshTask);
    }

    private void bindViews() {
        tvStatus = (TextView) findViewById(R.id.tv_status);
        tvPcInfo = (TextView) findViewById(R.id.tv_pc_info);
        tvAbiInfo = (TextView) findViewById(R.id.tv_abi_info);
        pbLevel = (ProgressBar) findViewById(R.id.pb_level);
        btnToggle = (Button) findViewById(R.id.btn_toggle);
        btnRediscover = (Button) findViewById(R.id.btn_rediscover);
        btnBatteryOpt = (Button) findViewById(R.id.btn_battery_opt);
        cbAutoDiscover = (CheckBox) findViewById(R.id.cb_auto_discover);
        etPcIp = (EditText) findViewById(R.id.et_pc_ip);
        etPort = (EditText) findViewById(R.id.et_port);
        spSampleRate = (Spinner) findViewById(R.id.sp_sample_rate);
        spAudioSource = (Spinner) findViewById(R.id.sp_audio_source);
        tvGainVal = (TextView) findViewById(R.id.tv_gain_val);
        sbGain = (SeekBar) findViewById(R.id.sb_gain);
        tvGateVal = (TextView) findViewById(R.id.tv_gate_val);
        sbGate = (SeekBar) findViewById(R.id.sb_gate);
        cbHwNs = (CheckBox) findViewById(R.id.cb_hw_ns);
        cbHwAgc = (CheckBox) findViewById(R.id.cb_hw_agc);
        cbAutoBoot = (CheckBox) findViewById(R.id.cb_auto_boot);

        tvAbiInfo.setText("架构引擎: " + AudioDsp.getAbiInfo() + "  |  Android " + Build.VERSION.RELEASE);

        ArrayAdapter<String> srAdapter = new ArrayAdapter<String>(
                this, android.R.layout.simple_spinner_item, SAMPLE_RATE_LABELS);
        srAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        spSampleRate.setAdapter(srAdapter);

        ArrayAdapter<String> srcAdapter = new ArrayAdapter<String>(
                this, android.R.layout.simple_spinner_item, AUDIO_SOURCE_LABELS);
        srcAdapter.setDropDownViewResource(android.R.layout.simple_spinner_dropdown_item);
        spAudioSource.setAdapter(srcAdapter);
    }

    private void loadSavedSettings() {
        boolean autoDiscover = AppPrefs.isAutoDiscover(this);
        cbAutoDiscover.setChecked(autoDiscover);
        etPcIp.setText(AppPrefs.getManualIp(this));
        etPcIp.setEnabled(!autoDiscover);
        etPort.setText(String.valueOf(AppPrefs.getPort(this)));

        int sr = AppPrefs.getSampleRate(this);
        int srIdx = 0;
        for (int i = 0; i < SAMPLE_RATES.length; i++) {
            if (SAMPLE_RATES[i] == sr) {
                srIdx = i;
                break;
            }
        }
        spSampleRate.setSelection(srIdx);

        int srcIdx = AppPrefs.getAudioSourceIndex(this);
        if (srcIdx < 0 || srcIdx >= AUDIO_SOURCE_LABELS.length) {
            srcIdx = 0;
        }
        spAudioSource.setSelection(srcIdx);

        int gain = AppPrefs.getGainPercent(this);
        if (gain < 50) gain = 50;
        if (gain > 300) gain = 300;
        sbGain.setMax(250);
        sbGain.setProgress(gain - 50);
        tvGainVal.setText(gain + "%");
        MicForegroundService.liveGainPercent = gain;

        int gate = AppPrefs.getNoiseGatePercent(this);
        if (gate < 0) gate = 0;
        if (gate > 100) gate = 100;
        sbGate.setMax(100);
        sbGate.setProgress(gate);
        tvGateVal.setText(gate == 0 ? "关闭" : (gate + "%"));
        MicForegroundService.liveNoiseGatePercent = gate;

        cbHwNs.setChecked(AppPrefs.isHwNsEnabled(this));
        cbHwAgc.setChecked(AppPrefs.isHwAgcEnabled(this));
        cbAutoBoot.setChecked(AppPrefs.isAutoStartOnBoot(this));
    }

    private void bindListeners() {
        btnToggle.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                saveTextInputs();
                boolean active = (MicForegroundService.currentState == MicForegroundService.STATE_DISCOVERING
                        || MicForegroundService.currentState == MicForegroundService.STATE_STREAMING);
                if (active) {
                    MicForegroundService.stopStreaming(MainActivity.this);
                } else {
                    attemptStartStreaming();
                }
            }
        });

        btnRediscover.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                boolean active = (MicForegroundService.currentState == MicForegroundService.STATE_DISCOVERING
                        || MicForegroundService.currentState == MicForegroundService.STATE_STREAMING);
                if (active) {
                    MicForegroundService.triggerRediscover(MainActivity.this);
                    Toast.makeText(MainActivity.this, "正在重新广播搜索电脑...", Toast.LENGTH_SHORT).show();
                } else {
                    Toast.makeText(MainActivity.this, "请先点击“开始传输”", Toast.LENGTH_SHORT).show();
                }
            }
        });

        btnBatteryOpt.setOnClickListener(new View.OnClickListener() {
            @Override
            public void onClick(View v) {
                requestBatteryOptimizationExemption();
            }
        });

        cbAutoDiscover.setOnCheckedChangeListener(new CompoundButton.OnCheckedChangeListener() {
            @Override
            public void onCheckedChanged(CompoundButton buttonView, boolean isChecked) {
                AppPrefs.setAutoDiscover(MainActivity.this, isChecked);
                etPcIp.setEnabled(!isChecked);
            }
        });

        spSampleRate.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override
            public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (!initDone) return;
                if (position >= 0 && position < SAMPLE_RATES.length) {
                    AppPrefs.setSampleRate(MainActivity.this, SAMPLE_RATES[position]);
                }
            }

            @Override
            public void onNothingSelected(AdapterView<?> parent) {
            }
        });

        spAudioSource.setOnItemSelectedListener(new AdapterView.OnItemSelectedListener() {
            @Override
            public void onItemSelected(AdapterView<?> parent, View view, int position, long id) {
                if (!initDone) return;
                AppPrefs.setAudioSourceIndex(MainActivity.this, position);
            }

            @Override
            public void onNothingSelected(AdapterView<?> parent) {
            }
        });

        sbGain.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override
            public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) {
                int gain = progress + 50;
                tvGainVal.setText(gain + "%");
                MicForegroundService.liveGainPercent = gain;
                if (fromUser) {
                    AppPrefs.setGainPercent(MainActivity.this, gain);
                }
            }

            @Override
            public void onStartTrackingTouch(SeekBar seekBar) {
            }

            @Override
            public void onStopTrackingTouch(SeekBar seekBar) {
            }
        });

        sbGate.setOnSeekBarChangeListener(new SeekBar.OnSeekBarChangeListener() {
            @Override
            public void onProgressChanged(SeekBar seekBar, int progress, boolean fromUser) {
                tvGateVal.setText(progress == 0 ? "关闭" : (progress + "%"));
                MicForegroundService.liveNoiseGatePercent = progress;
                if (fromUser) {
                    AppPrefs.setNoiseGatePercent(MainActivity.this, progress);
                }
            }

            @Override
            public void onStartTrackingTouch(SeekBar seekBar) {
            }

            @Override
            public void onStopTrackingTouch(SeekBar seekBar) {
            }
        });

        cbHwNs.setOnCheckedChangeListener(new CompoundButton.OnCheckedChangeListener() {
            @Override
            public void onCheckedChanged(CompoundButton buttonView, boolean isChecked) {
                AppPrefs.setHwNsEnabled(MainActivity.this, isChecked);
            }
        });

        cbHwAgc.setOnCheckedChangeListener(new CompoundButton.OnCheckedChangeListener() {
            @Override
            public void onCheckedChanged(CompoundButton buttonView, boolean isChecked) {
                AppPrefs.setHwAgcEnabled(MainActivity.this, isChecked);
            }
        });

        cbAutoBoot.setOnCheckedChangeListener(new CompoundButton.OnCheckedChangeListener() {
            @Override
            public void onCheckedChanged(CompoundButton buttonView, boolean isChecked) {
                AppPrefs.setAutoStartOnBoot(MainActivity.this, isChecked);
                if (isChecked && Build.VERSION.SDK_INT >= 34) {
                    Toast.makeText(
                            MainActivity.this,
                            "已开启。Android 14+ 若系统拦截开机自启，会发送通知供一键开启",
                            Toast.LENGTH_LONG).show();
                }
            }
        });
    }

    private void saveTextInputs() {
        AppPrefs.setManualIp(this, etPcIp.getText().toString());
        try {
            int port = Integer.parseInt(etPort.getText().toString().trim());
            AppPrefs.setPort(this, port);
        } catch (NumberFormatException ignored) {
            AppPrefs.setPort(this, Protocol.DEFAULT_PORT);
        }
    }

    private void attemptStartStreaming() {
        saveTextInputs();
        List<String> needed = new ArrayList<String>();
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            if (checkSelfPermission(Manifest.permission.RECORD_AUDIO) != PackageManager.PERMISSION_GRANTED) {
                needed.add(Manifest.permission.RECORD_AUDIO);
            }
        }
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (checkSelfPermission(Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED) {
                needed.add(Manifest.permission.POST_NOTIFICATIONS);
            }
        }

        if (!needed.isEmpty()) {
            pendingStartAfterPerm = true;
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
                requestPermissions(needed.toArray(new String[needed.size()]), REQ_PERMISSIONS);
            }
            return;
        }

        MicForegroundService.startStreaming(this, false);
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == REQ_PERMISSIONS && pendingStartAfterPerm) {
            pendingStartAfterPerm = false;
            boolean hasMic = (Build.VERSION.SDK_INT < Build.VERSION_CODES.M)
                    || (checkSelfPermission(Manifest.permission.RECORD_AUDIO) == PackageManager.PERMISSION_GRANTED);
            if (hasMic) {
                MicForegroundService.startStreaming(this, false);
            } else {
                Toast.makeText(this, "需要麦克风录音权限才能工作", Toast.LENGTH_LONG).show();
            }
        }
    }

    @SuppressLint("BatteryLife")
    private void requestBatteryOptimizationExemption() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            Toast.makeText(this, "当前旧版系统无需额外关闭电池优化", Toast.LENGTH_SHORT).show();
            return;
        }
        PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
        if (pm != null && pm.isIgnoringBatteryOptimizations(getPackageName())) {
            Toast.makeText(this, "已在电池优化白名单中，关屏可稳定传输", Toast.LENGTH_SHORT).show();
            return;
        }
        try {
            Intent intent = new Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS);
            intent.setData(Uri.parse("package:" + getPackageName()));
            startActivity(intent);
        } catch (Throwable ignored) {
            try {
                startActivity(new Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS));
            } catch (Throwable ignored2) {
                Toast.makeText(this, "请在系统电池设置中允许本应用无限制后台运行", Toast.LENGTH_LONG).show();
            }
        }
    }

    private void updateBatteryButtonState() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.M) {
            PowerManager pm = (PowerManager) getSystemService(Context.POWER_SERVICE);
            if (pm != null && pm.isIgnoringBatteryOptimizations(getPackageName())) {
                btnBatteryOpt.setText("关屏保活已就绪 (已忽略电池优化)");
                btnBatteryOpt.setEnabled(false);
                return;
            }
        }
        btnBatteryOpt.setText("关闭电池优化 (推荐：防关屏断流)");
        btnBatteryOpt.setEnabled(true);
    }

    private void updateLiveStatus() {
        int st = MicForegroundService.currentState;
        tvStatus.setText("状态: " + MicForegroundService.currentStatusText);

        if (st == MicForegroundService.STATE_STREAMING) {
            tvStatus.setTextColor(Color.parseColor("#1B8A3E"));
            btnToggle.setText("停止传输");
            tvPcInfo.setText("目标电脑: " + MicForegroundService.connectedPcText
                    + "  |  已发包: " + MicForegroundService.packetsSent);
        } else if (st == MicForegroundService.STATE_DISCOVERING) {
            tvStatus.setTextColor(Color.parseColor("#1967D2"));
            btnToggle.setText("停止传输");
            tvPcInfo.setText("目标电脑: " + MicForegroundService.connectedPcText);
        } else if (st == MicForegroundService.STATE_ERROR) {
            tvStatus.setTextColor(Color.parseColor("#C62828"));
            btnToggle.setText("开始传输");
            tvPcInfo.setText("目标电脑: 未连接");
        } else {
            tvStatus.setTextColor(Color.parseColor("#444444"));
            btnToggle.setText("开始传输");
            tvPcInfo.setText("目标电脑: 未连接");
        }

        int lvl = (int) (MicForegroundService.currentAudioLevel * 100f);
        if (lvl < 0) lvl = 0;
        if (lvl > 100) lvl = 100;
        pbLevel.setProgress(lvl);
    }
}
