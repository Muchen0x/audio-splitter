using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace StereoSplitter.Devices
{
    /// <summary>
    /// 主端点 + 各输出设备端点的统一编排。
    ///
    /// <para>为什么输出侧按**设备**而不是按"左/右声道"绑定: Windows 的端点音量本身就是
    /// 设备级的一个值 (MasterVolumeLevelScalar), 一台设备只有一个。所以能被 UI 双向绑定、
    /// 能在停止时还原的, 只能是"该设备唯一那张卡片"; 同一台设备挂多张卡片时, 那些卡片的
    /// 音量只能进混音矩阵 (见 LaneRouting.ResolveEndpointLanes)。</para>
    ///
    /// <para>键是 deviceId: 同一设备重复 BindSink 会复用同一个绑定对象, 不会重复订阅通知。</para>
    /// </summary>
    sealed class ChannelBindingSet : IDisposable
    {
        readonly DeviceCatalog catalog;
        readonly Action<string> log;
        // deviceId → 端点绑定 (顺序按插入顺序, 便于日志稳定输出)
        readonly Dictionary<string, EndpointVolumeBinding> sinks =
            new Dictionary<string, EndpointVolumeBinding>(StringComparer.Ordinal);
        readonly List<string> sinkOrder = new List<string>();

        public EndpointVolumeBinding Master { get; private set; }

        public bool AnyBound
        {
            get { return Master != null || sinks.Count > 0; }
        }

        public int SinkCount { get { return sinks.Count; } }

        public ChannelBindingSet(DeviceCatalog catalog, Action<string> log)
        {
            this.catalog = catalog;
            this.log = log;
        }

        // ---------------- 主端点 (捕获源) ----------------

        public EndpointVolumeBinding BindMaster(string deviceId, Action<AudioVolumeNotificationData> onNotify)
        {
            Master = Rebind(Master, deviceId, "主", onNotify);
            return Master;
        }

        // ---------------- 输出端点 (按设备) ----------------

        /// <summary>绑定一台输出设备的端点。同一设备已绑定且有效 → 直接复用。</summary>
        public EndpointVolumeBinding BindSink(string deviceId, string role, Action<AudioVolumeNotificationData> onNotify)
        {
            if (string.IsNullOrEmpty(deviceId)) return null;

            EndpointVolumeBinding current;
            if (sinks.TryGetValue(deviceId, out current) && current != null && current.IsValid)
                return current;

            if (current != null)
            {
                current.RestoreVolume();
                current.RestoreMuteIfApplied();
                current.Dispose();
                sinks.Remove(deviceId);
                sinkOrder.Remove(deviceId);
            }

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
            sinks[deviceId] = binding;
            sinkOrder.Add(deviceId);
            return binding;
        }

        /// <summary>取某设备的输出端点绑定 (未绑定 → null)。</summary>
        public EndpointVolumeBinding SinkOf(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId)) return null;
            EndpointVolumeBinding b;
            return sinks.TryGetValue(deviceId, out b) ? b : null;
        }

        /// <summary>按插入顺序返回全部输出端点绑定。</summary>
        public IEnumerable<EndpointVolumeBinding> Sinks()
        {
            for (int i = 0; i < sinkOrder.Count; i++)
            {
                EndpointVolumeBinding b;
                if (sinks.TryGetValue(sinkOrder[i], out b) && b != null) yield return b;
            }
        }

        /// <summary>解除全部输出端点绑定 (主端点不动)。restore=true 时还原音量/静音。</summary>
        public void UnbindSinks(bool restore)
        {
            for (int i = 0; i < sinkOrder.Count; i++)
            {
                EndpointVolumeBinding b;
                if (sinks.TryGetValue(sinkOrder[i], out b)) UnbindOne(b, restore);
            }
            sinks.Clear();
            sinkOrder.Clear();
        }

        // ---------------- 写入 ----------------

        /// <summary>按 UI 权威值写入主端点 (主音量 + 捕获消音)。</summary>
        public void WriteMaster(int masterPercent, bool captureMute)
        {
            if (Master == null) return;
            Master.WriteVolume(masterPercent / 100f);
            // 主静音只作用于采样层, 不写捕获端点的 Mute; 捕获端点只认 chkMuteCapture
            if (Master.ReadMuted() != captureMute) Master.WriteMute(captureMute);
        }

        // ---------------- 内部 ----------------

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
            UnbindSinks(restore);
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

        public void Dispose()
        {
            UnbindAll(false);
        }
    }
}
