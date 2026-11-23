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
    ///
    /// <para>还原基线 (<see cref="baselines"/>) 独立于绑定对象存在: 记录的是"本次分流开始时该设备
    /// 的音量/静音", 只有**还原被设备接受**之后才删除。这样即使中途重绑、或者设备拒绝写入,
    /// 停止时用的仍是真正的原始值, 而不是我们自己写进去的值 (否则会把 20% 还原成 20%)。</para>
    /// </summary>
    sealed class ChannelBindingSet : IDisposable
    {
        readonly DeviceCatalog catalog;
        readonly Action<string> log;
        // deviceId → 端点绑定 (顺序按插入顺序, 便于日志稳定输出)
        readonly Dictionary<string, EndpointVolumeBinding> sinks =
            new Dictionary<string, EndpointVolumeBinding>(StringComparer.Ordinal);
        readonly List<string> sinkOrder = new List<string>();

        // deviceId → 本次分流开始时的音量/静音 (还原目标)
        sealed class Baseline
        {
            public float Volume;
            public bool Mute;
        }
        readonly Dictionary<string, Baseline> baselines =
            new Dictionary<string, Baseline>(StringComparer.Ordinal);

        /// <summary>还有多少台设备的音量没有还原成功 (残留基线)。</summary>
        public int PendingRestoreCount { get { return baselines.Count; } }

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
                // 旧对象已失效 → 先把写过的音量还原回去再释放 (失效时 IsValid=false, 内部会跳过)
                RestoreWithLog(current);
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

            AdoptOrRecordBaseline(binding);
            if (!binding.Subscribe(onNotify))
            {
                // F-23: 通知订阅失败 → 绑定已 Failed, 绝不能加入 sinks 当作可用 (否则读写全静默无操作,
                // 上层以为端点音量可用, 实际卡片音量/静音完全失效)。释放对象并清掉本次新建的基线。
                log("[警告] " + role + " 端点通知订阅失败: " + catalog.NameOf(deviceId) +
                    " (该设备音量改走混音增益)");
                binding.Dispose();
                baselines.Remove(deviceId);
                return null;
            }
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

        /// <summary>
        /// 只解除**不在** <paramref name="wanted"/> 里的输出端点绑定 (restore=true 时还原音量/静音)。
        ///
        /// <para>用途: 运行中"某张卡片换了设备"只需要动离开集合的那一台。全量 <see cref="UnbindSinks"/>
        /// 的做法是先把**所有**设备还原一遍 (每台 4 次 COM 调用: 读音量 → 写音量 → 写静音 → 回读),
        /// 再把它们 Dispose 掉、随后又逐个 <c>catalog.Open</c> 重新激活端点并重新订阅通知 —— 实际变化的
        /// 只有 1 台, 代价却是 O(N)。N 台输出设备时这一步能卡住 UI 线程几百毫秒。</para>
        ///
        /// <para>留下的绑定保持**原对象**: 通知订阅不断、<see cref="baselines"/> 一个字节都不动,
        /// 因此"停止时还原到分流开始前的音量"这一语义不受影响 (基线本来就只在该设备真正离开时
        /// 才由 <see cref="RestoreWithLog"/> 消费掉)。</para>
        /// </summary>
        public void RetainSinks(ICollection<string> wanted, bool restore)
        {
            if (wanted == null) { UnbindSinks(restore); return; }

            // 先收集再删除: 不能在遍历 sinkOrder 的同时 Remove 它
            List<string> drop = null;
            for (int i = 0; i < sinkOrder.Count; i++)
                if (!wanted.Contains(sinkOrder[i]))
                {
                    if (drop == null) drop = new List<string>();
                    drop.Add(sinkOrder[i]);
                }
            if (drop == null) return;

            for (int i = 0; i < drop.Count; i++)
            {
                string id = drop[i];
                EndpointVolumeBinding b;
                if (sinks.TryGetValue(id, out b)) UnbindOne(b, restore);
                sinks.Remove(id);
                sinkOrder.Remove(id);
            }
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
        /// 首次绑定时记录"进入时原值"; 已记录过则沿用旧基线 (不取当前值, 否则会被我们写过的值污染)。
        /// </summary>
        void AdoptOrRecordBaseline(EndpointVolumeBinding binding)
        {
            if (binding == null || !binding.IsValid) return;
            Baseline bl;
            if (baselines.TryGetValue(binding.DeviceId, out bl))
            {
                binding.AdoptBaseline(bl.Volume, bl.Mute);
                return;
            }
            baselines[binding.DeviceId] = new Baseline { Volume = binding.VolumeEntered, Mute = binding.MuteEntered };
        }

        /// <summary>
        /// 还原一个端点的音量/静音并回读确认。
        /// 回读与目标不一致 → 记警告并**保留基线**, 下一次停止/重绑会再试一次
        /// (有些 USB 音箱/DAC 会把音量量化到离散档位, 一次写不进并不代表值丢了)。
        /// </summary>
        void RestoreWithLog(EndpointVolumeBinding b)
        {
            if (b == null || !b.IsValid) return;

            int beforeVol = Pct(b.ReadVolume());
            bool beforeMute = b.ReadMuted();
            int wantVol = Pct(b.VolumeEntered);
            bool wantMute = b.MuteEntered;

            b.RestoreVolume();
            b.RestoreMuteIfApplied();

            int afterVol = Pct(b.ReadVolume());
            bool afterMute = b.ReadMuted();
            bool ok = Math.Abs(afterVol - wantVol) <= 1 && afterMute == wantMute;

            string line = "[还原] " + b.Role + " " + b.DeviceName + ": 音量 " +
                beforeVol + "% → " + afterVol + "%" + (afterMute ? "（静音）" : "（未静音）");
            if (!ok)
            {
                log(line + "  ← 目标 " + wantVol + "%" + (wantMute ? "（静音）" : "") +
                    "；设备未接受该写入（驱动侧量化/只读），已留存基线，下次停止时重试");
                return;
            }
            log(line);
            baselines.Remove(b.DeviceId);
        }

        static int Pct(float scalar)
        {
            return Math.Max(0, Math.Min(100, (int)Math.Round(scalar * 100)));
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
                // 换绑: 旧端点写过的音量/静音先还原, 否则那台设备会永久停在"分流时的音量"
                RestoreWithLog(current);
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

            AdoptOrRecordBaseline(binding);
            if (!binding.Subscribe(onNotify))
            {
                // F-23: 与主端点同理 —— 订阅失败不得返回可用绑定
                log("[警告] " + role + " 端点通知订阅失败: " + catalog.NameOf(deviceId));
                binding.Dispose();
                baselines.Remove(deviceId);
                return null;
            }
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

        void UnbindOne(EndpointVolumeBinding b, bool restore)
        {
            if (b == null) return;
            if (restore) RestoreWithLog(b);
            b.Dispose();
        }

        public void Dispose()
        {
            UnbindAll(false);
        }
    }
}
