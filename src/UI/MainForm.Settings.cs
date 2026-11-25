using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    partial class MainForm
    {
        // ==================== 设置持久化 (AppPaths.IniPath: 便携优先, 不可写退 LOCALAPPDATA) ====================

        /// <summary>
        /// R-04: ini 的位置不再硬编码在 exe 目录。可写就用 exe 目录 (便携, 行为不变);
        /// 装在 Program Files 这类不可写目录时退到 %LOCALAPPDATA%\AudioSplitter,
        /// 否则每次启动都读不到配置、每次保存都失败 —— 表现为"设置每次重启归零"。
        /// </summary>
        string IniPath
        {
            get { return Infrastructure.AppPaths.IniPath; }
        }
        Infrastructure.SettingsStore settings;
        // F-34: 配置读取失败 → 进入只读恢复模式, 禁止把默认值/半成品写回原文件。
        bool settingsLoadFailed;

        void LoadSettings()
        {
            settings.Load();
            // ini 损坏/被占用时 Load 会静默回默认值: 这里留痕, 否则用户看到的是"设置全丢了"却查不到原因
            settingsLoadFailed = settings.LastLoadError != null;
            if (settingsLoadFailed)
                Log("[警告] 配置文件读取失败(" + settings.LastLoadError +
                    ")，本次以默认值启动且不会覆盖原文件（配置进入只读恢复模式）: " + IniPath);

            chkMinimizeToTray.Checked = GetInt("MinimizeToTray", 0) != 0;
            cmbLatency.SelectedIndex = Math.Max(0, Math.Min(4, GetInt("LatencyProfile", 0)));
            int wi = GetInt("WaterIntervalSec", 30);
            if (wi != 0) wi = Math.Max(1, Math.Min((int)nudWaterInterval.Maximum, wi)); // 0=关闭; 其余 1~上限秒原样用 (上限已收到99)
            nudWaterInterval.Value = wi;

            int mode = Math.Max(0, Math.Min(2, GetInt("DriftMode", 1)));
            chkDrift.Checked = mode != 0;
            driftMode = mode;
            fixedPpm = Math.Max(-5000, Math.Min(5000, GetInt("FixedRatioPpm", 600)));

            int gain = Math.Max(0, Math.Min(300, GetInt("Gain", 100)));
            volGain.Value = gain;
            lblVolGain.Text = gain + "%";
            pendingGain = gain / 100f;

            // 输出卡片 (规格 + 各路输出)
            LoadLanes();
        }


        int GetInt(string key, int def)
        {
            return settings.GetInt(key, def);
        }

        string GetStr(string key)
        {
            return settings.GetStr(key);
        }

        string DeviceIdAt(int idx)
        {
            return catalog.IdAt(idx);
        }

        void SaveSettings()
        {
            // 初始化阶段(读ini/Bind回读/覆盖回写)不落盘, 防止把ini冲成端点回读值
            if (loadingSettings) return;
            // F-34: 配置读取失败 → 只读恢复模式, 不写盘 (否则默认值覆盖原配置)
            if (settingsLoadFailed) { WarnSettingsReadOnly(); return; }
            // 字典立即同步: RefreshDevices 等按字典恢复选中, 不能等写盘
            if (!SyncSettingsDict()) return;   // 同步失败 → 本次不写盘, 避免缺键配置落盘
            // 文件写盘 debounce 300ms: 拖动滑条时避免每次移动都写盘
            if (saveDebounce != null)
            {
                saveDebounce.Stop();
                saveDebounce.Start();
            }
        }

        void FlushSettings()
        {
            if (loadingSettings) return;
            if (settingsLoadFailed) { WarnSettingsReadOnly(); return; }
            if (!SyncSettingsDict()) return;

            string err;
            if (settings.WriteAll(out err))
            {
                lastSaveWarnTick = 0;      // 恢复正常后允许下次重新告警
                return;
            }

            // 失败限流: 60 秒最多一条, 避免拖动滑条时刷屏
            WarnSettingsWrite(err);
        }

        void WarnSettingsReadOnly()
        {
            if (lastSaveWarnTick != 0 && unchecked(Environment.TickCount - lastSaveWarnTick) < 60000) return;
            lastSaveWarnTick = Environment.TickCount;
            Log("[警告] 配置读取曾失败，当前为只读恢复模式，改动不会写盘以免覆盖原文件 (" + IniPath + ")");
        }

        void WarnSettingsWrite(string err)
        {
            int now = Environment.TickCount;
            if (lastSaveWarnTick != 0 && unchecked(now - lastSaveWarnTick) < 60000) return;
            lastSaveWarnTick = now;
            Log("[警告] 设置写入失败: " + err + "；本次运行的改动可能不会保留 (" + IniPath + ")");
        }

        /// <summary>F-34: 返回本次控件→字典同步是否完整成功; false 时调用方必须放弃写盘。</summary>
        bool SyncSettingsDict()
        {
            try
            {
                settings.Put("MinimizeToTray", chkMinimizeToTray.Checked ? "1" : "0");
                settings.Put("LatencyProfile", Math.Max(0, cmbLatency.SelectedIndex).ToString());
                settings.Put("Spec", cmbSpec.SelectedIndex < 0 ? "1" : cmbSpec.SelectedIndex.ToString());
                SyncLanesFromCards();
                // Out* 键的编解码收敛在 LaneSettingsCodec (纯数据, 不触控件)
                Infrastructure.LaneSettingsCodec.Encode(settings, models);
                settings.Put("WaterIntervalSec", ((int)nudWaterInterval.Value).ToString());
                settings.Put("Gain", volGain.Value.ToString());
                // 兼容键: 左右 = FL/FR 输出卡片的设备 (旧 ini 与外部工具仍按这两个键读)
                settings.Put("DeviceLeft", LeftDeviceId() ?? "");
                settings.Put("DeviceRight", RightDeviceId() ?? "");
                settings.Put("DeviceDefault", DeviceIdAt(cmbDefault.SelectedIndex));
                OutputCardControl vcl = LeftCard(), vcr = RightCard();
                settings.Put("VolumeLeft", (vcl != null ? vcl.volTrack.Value : 100).ToString());
                settings.Put("VolumeRight", (vcr != null ? vcr.volTrack.Value : 100).ToString());
                settings.Put("VolumeMaster", volMaster.Value.ToString());
                settings.Put("MuteLeft", (vcl != null && vcl.btnMute.Muted) ? "1" : "0");
                settings.Put("MuteRight", (vcr != null && vcr.btnMute.Muted) ? "1" : "0");
                settings.Put("MuteMaster", btnMuteMaster.Muted ? "1" : "0");
                settings.Put("MuteCapture", chkMuteCapture.Checked ? "1" : "0");
                settings.Put("DriftMode", driftMode.ToString());
                settings.Put("FixedRatioPpm", fixedPpm.ToString());
                return true;
            }
            catch (Exception ex)
            {
                // F-34: 同步失败 → 返回 false 让调用方放弃写盘, 否则缺键配置会覆盖完整原文件。
                // 每会话只提示一次, 防止持续异常时刷屏。
                if (!syncDictWarned)
                {
                    syncDictWarned = true;
                    Log("[警告] 保存设置时读取控件状态失败: " + ex.Message + "（本次已放弃写盘，避免缺键配置覆盖原文件）");
                }
                return false;
            }
        }

        void RestoreDeviceSelection(string key, RoundedCombo combo, string label)
        {
            string id = GetStr(key);
            if (string.IsNullOrEmpty(id)) return;
            int idx = catalog.IndexOf(id);
            if (idx >= 0) combo.SelectedIndex = idx;
            else Log("[初始化] 未找到已保存的" + label + "设备，使用默认选择");
        }

        // ini 音量在启动时由 WriteUIValuesToDevice 统一写入端点 (Load 期尚未绑定端点, 此处只回填 UI)
        void ApplyIniVolumeOverrides()
        {
            bool any = false;
            volSyncing = true;
            try
            {
                if (settings.Has("VolumeMaster"))
                {
                    int v = Math.Max(0, Math.Min(100, GetInt("VolumeMaster", 100)));
                    volMaster.Value = v;
                    lblVolMaster.Text = v + "%";
                    any = true;
                }
                if (settings.Has("MuteMaster"))
                {
                    bool m = GetInt("MuteMaster", 0) != 0;
                    btnMuteMaster.Muted = m;
                    any = true;
                }
                if (settings.Has("MuteCapture"))
                {
                    bool m = GetInt("MuteCapture", 0) != 0;
                    chkMuteCapture.Checked = m;
                    any = true;
                }
            }
            finally
            {
                volSyncing = false;
            }

            // 存量设置音量为0 → 补勾静音 (与拖到0自动静音规则一致)
            if (volMaster.Value == 0) btnMuteMaster.Muted = true;
            for (int i = 0; i < models.Count; i++)
                if (models[i].Volume == 0) models[i].Mute = true;

            if (any)
                Log("[初始化] 设置已加载: 主=" + volMaster.Value + "%/" + (btnMuteMaster.Muted ? "静音" : "开") +
                    " 输出 " + models.Count + " 张卡片 (" + LaneSummary() + ")" +
                    " 增益=" + volGain.Value + "%" +
                    (chkMuteCapture.Checked ? " 捕获消音=开" : ""));
        }
    }
}
