using System;
using NAudio.CoreAudioApi;

namespace StereoSplitter.Devices
{
    /// <summary>
    /// 主/左/右三条端点绑定的统一编排。
    /// 三处绑定逻辑完全同构, 旧实现里重复了三遍 (BindDefaultVolumeSync / BindOneChannel / UnbindAllVolumeSync),
    /// 这里收成一个 Rebind。
    /// </summary>
    sealed class ChannelBindingSet : IDisposable
    {
        readonly DeviceCatalog catalog;
        readonly Action<string> log;

        public EndpointVolumeBinding Master { get; private set; }
        public EndpointVolumeBinding Left { get; private set; }
        public EndpointVolumeBinding Right { get; private set; }

        public bool AnyBound
        {
            get { return Master != null || Left != null || Right != null; }
        }

        public ChannelBindingSet(DeviceCatalog catalog, Action<string> log)
        {
            this.catalog = catalog;
            this.log = log;
        }

        public EndpointVolumeBinding BindMaster(string deviceId, Action<AudioVolumeNotificationData> onNotify)
        {
            Master = Rebind(Master, deviceId, "主", onNotify);
            return Master;
        }

        public EndpointVolumeBinding BindLeft(string deviceId, Action<AudioVolumeNotificationData> onNotify)
        {
            Left = Rebind(Left, deviceId, "左", onNotify);
            return Left;
        }

        public EndpointVolumeBinding BindRight(string deviceId, Action<AudioVolumeNotificationData> onNotify)
        {
            Right = Rebind(Right, deviceId, "右", onNotify);
            return Right;
        }

        /// <summary>
        /// 绑定一个端点: 同设备且仍有效 → 复用; 换设备 → 先还原旧设备静音再释放; 空 ID → 解绑。
        /// </summary>
        EndpointVolumeBinding Rebind(EndpointVolumeBinding current, string deviceId, string role,
            Action<AudioVolumeNotificationData> onNotify)
        {
            if (current != null && current.IsValid && current.DeviceId == deviceId)
                return current;

            if (current != null)
            {
                // 换绑: 旧端点如果是被我们静音的, 先恢复, 避免残留
                current.RestoreMuteIfApplied();
                current.Dispose();
                current = null;
            }

            if (string.IsNullOrEmpty(deviceId)) return null;

            MMDevice dev = catalog.Open(deviceId);
            if (dev == null)
            {
                log("[错误] 无法打开" + role + "端点设备: " + catalog.NameOf(deviceId) + " (ID 已失效, 建议刷新设备)");
                return null;
            }

            var binding = new EndpointVolumeBinding(dev, role, log);
            if (!binding.IsValid)
            {
                binding.Dispose();
                return null;
            }

            binding.Subscribe(onNotify);
            return binding;
        }

        /// <summary>
        /// 解除全部绑定。<paramref name="restore"/> = true 时还原端点音量/静音 (停止/失败/退出用)。
        /// </summary>
        public void UnbindAll(bool restore)
        {
            UnbindOne(Master, restore);
            Master = null;
            UnbindOne(Left, restore);
            Left = null;
            UnbindOne(Right, restore);
            Right = null;
        }

        static void UnbindOne(EndpointVolumeBinding b, bool restore)
        {
            if (b == null) return;
            if (restore)
            {
                b.RestoreVolume();
                b.RestoreMuteIfApplied();
            }
            b.Dispose();
        }

        /// <summary>按 UI 权威值写入三个端点 (启动/重绑后调用)。</summary>
        public void WriteValues(int masterPercent, int leftPercent, int rightPercent,
            bool captureMute, bool leftMute, bool rightMute)
        {
            if (Master != null) Master.WriteVolume(masterPercent / 100f);
            if (Left != null) Left.WriteVolume(leftPercent / 100f);
            if (Right != null) Right.WriteVolume(rightPercent / 100f);

            // 主静音只作用于采样层, 不写捕获端点的 Mute; 捕获端点只认 chkMuteCapture
            if (Master != null && Master.ReadMuted() != captureMute) Master.WriteMute(captureMute);
            if (Left != null && Left.ReadMuted() != leftMute) Left.WriteMute(leftMute);
            if (Right != null && Right.ReadMuted() != rightMute) Right.WriteMute(rightMute);
        }

        public void Dispose()
        {
            UnbindAll(false);
        }
    }
}
