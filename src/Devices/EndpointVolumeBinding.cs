using System;
using NAudio.CoreAudioApi;

namespace StereoSplitter.Devices
{
    /// <summary>
    /// 单个端点的音量/静音绑定。
    ///
    /// 与旧实现的关键差别: 持有一个**自己新建**的 MMDevice (catalog.Open(id)),
    /// 因此可以安全地在自己 Dispose 时释放, 不会波及设备列表里的其它实例,
    /// 也不会出现 "COM 对象与其基础 RCW 分开后就不能再使用"。
    ///
    /// 所有 COM 调用都走 Fail 兜底: 设备被拔/端点失效时标记 Failed 并抛一次 Invalidated,
    /// 由上层解绑并提示, 不再把异常泄漏到调用栈深处。
    /// </summary>
    sealed class EndpointVolumeBinding : IDisposable
    {
        readonly Action<string> log;
        readonly string role;
        MMDevice device;
        AudioEndpointVolume vol;
        AudioEndpointVolumeNotificationDelegate handler;

        /// <summary>绑定时的原静音状态 (解绑/停止时按需还原)。</summary>
        public bool MuteEntered { get; private set; }
        /// <summary>静音是否被我们改动过 (未改动则解绑时无需还原)。</summary>
        public bool MuteApplied { get; private set; }
        /// <summary>应用已知的端点静音基线; 通知里与之不等 = 外部改动。</summary>
        public bool LastMuteWeWrote { get; set; }
        /// <summary>绑定时的原音量 (停止时无条件还原)。</summary>
        public float VolumeEntered { get; private set; }

        public string DeviceId { get; private set; }
        public string DeviceName { get; private set; }
        public string Role { get { return role; } }
        public bool Failed { get; private set; }

        public bool IsValid
        {
            get { return vol != null && device != null && !Failed; }
        }

        public event Action<EndpointVolumeBinding> Invalidated;

        /// <param name="device">由 DeviceCatalog.Open 新建的独立实例, 所有权转移给本对象</param>
        public EndpointVolumeBinding(MMDevice device, string role, Action<string> log)
        {
            this.log = log;
            this.role = role;
            this.device = device;

            try
            {
                DeviceId = device.ID;
                DeviceName = DeviceCatalog.SafeName(device);
                vol = device.AudioEndpointVolume;
                MuteEntered = vol.Mute;
                LastMuteWeWrote = MuteEntered;
                MuteApplied = false;
                VolumeEntered = vol.MasterVolumeLevelScalar;
            }
            catch (Exception ex)
            {
                DeviceId = device != null ? TryId(device) : null;
                DeviceName = DeviceCatalog.SafeName(device);
                Fail(ex);
            }
        }

        static string TryId(MMDevice d)
        {
            try { return d.ID; } catch { return null; }
        }

        /// <summary>F-23: 订阅通知的结果。false = 订阅失败(对象已 Failed), 调用方不得把它当可用绑定。</summary>
        public bool Subscribe(Action<AudioVolumeNotificationData> onNotify)
        {
            if (!IsValid || onNotify == null) return IsValid && onNotify == null;  // 无回调: 视为"无需订阅"的成功
            try
            {
                handler = delegate (AudioVolumeNotificationData data)
                {
                    try { onNotify(data); }
                    catch (Exception ex)
                    {
                        // 业务侧异常在通知线程上吞掉, 但要留痕 (与 Fail() 走同一 log 通道, 那里也允许在通知线程调)
                        try { if (log != null) log("[警告] 端点音量通知处理失败(" + (DeviceName ?? "?") + "): " + ex.Message); } catch { }
                    }
                };
                vol.OnVolumeNotification += handler;
                return IsValid;
            }
            catch (Exception ex) { Fail(ex); return false; }
        }

        // ---------------- 读写 ----------------

        public float ReadVolume()
        {
            if (!IsValid) return 0f;
            try { return vol.MasterVolumeLevelScalar; }
            catch (Exception ex) { Fail(ex); return 0f; }
        }

        public bool ReadMuted()
        {
            if (!IsValid) return false;
            try { return vol.Mute; }
            catch (Exception ex) { Fail(ex); return false; }
        }

        /// <summary>写端点音量 (0..1)。失败返回 false 并置 Failed。</summary>
        public bool WriteVolume(float scalar)
        {
            if (!IsValid) return false;
            try
            {
                vol.MasterVolumeLevelScalar = scalar;
                return true;
            }
            catch (Exception ex) { Fail(ex); return false; }
        }

        /// <summary>写端点静音, 同步维护 LastMuteWeWrote / MuteApplied 基线。</summary>
        public bool WriteMute(bool mute)
        {
            if (!IsValid) return false;
            try
            {
                vol.Mute = mute;
                LastMuteWeWrote = vol.Mute;
                MuteApplied = (vol.Mute != MuteEntered);
                return true;
            }
            catch (Exception ex) { Fail(ex); return false; }
        }

        // ---------------- 还原 ----------------

        /// <summary>还原绑定前的端点音量 (无条件写回, 与原实现一致)。</summary>
        public void RestoreVolume()
        {
            if (!IsValid) return;
            try { vol.MasterVolumeLevelScalar = VolumeEntered; }
            catch (Exception ex) { Fail(ex); }
        }

        /// <summary>
        /// 还原静音: 当前值与基线不同才写 (相同则不碰, 避免无意义的设备改动)。
        /// 判据用"当前 != 基线"而不是 MuteApplied 旗标 —— 用户/系统在运行期间改过静音时,
        /// 旗标是 false, 但那恰恰是需要还原的情形。
        /// </summary>
        public void RestoreMuteIfApplied()
        {
            if (!IsValid) return;
            try
            {
                if (vol.Mute != MuteEntered) vol.Mute = MuteEntered;
                LastMuteWeWrote = vol.Mute;
                MuteApplied = false;
            }
            catch (Exception ex) { Fail(ex); }
        }

        /// <summary>
        /// 沿用外部记录下来的"进入时原值"作为还原基线 (不自取当前值)。
        /// 用于: 运行中重绑 / 上一次还原没被设备接受时重试 —— 否则基线会被
        /// 我们自己写过的值污染, 变成"把 20% 还原成 20%"。
        /// </summary>
        public void AdoptBaseline(float volume, bool mute)
        {
            VolumeEntered = volume;
            MuteEntered = mute;
            LastMuteWeWrote = mute;
            MuteApplied = false;
        }

        /// <summary>
        /// 复位基线: 重新以当前设备值为"进入时的原值"。
        /// 用于换绑/重绑场景, 避免把上一次绑定前的旧值当成本次还原目标。
        /// </summary>
        public void RebaseEnteredState()
        {
            if (!IsValid) return;
            try
            {
                MuteEntered = vol.Mute;
                LastMuteWeWrote = MuteEntered;
                MuteApplied = false;
                VolumeEntered = vol.MasterVolumeLevelScalar;
            }
            catch (Exception ex) { Fail(ex); }
        }

        // ---------------- 失效 ----------------

        void Fail(Exception ex)
        {
            if (Failed) return;
            Failed = true;
            if (log != null)
                log("[警告] " + role + "端点操作失败(" + (DeviceName ?? "?") + "): " + ex.Message + "；该端点绑定已失效");
            var h = Invalidated;
            if (h != null) { try { h(this); } catch { } }
        }

        public void Dispose()
        {
            if (vol != null && handler != null)
            {
                try { vol.OnVolumeNotification -= handler; } catch { }
            }
            handler = null;
            vol = null;

            // 自建实例 → 可以安全释放 (旧实现不敢 Dispose, 正是因为它与设备列表共享)
            if (device != null)
            {
                try { device.Dispose(); } catch { }
                device = null;
            }
        }
    }
}
