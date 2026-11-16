using System;
using System.Collections.Generic;

namespace StereoSplitter
{
    /// <summary>
    /// 把界面模型（输出卡片 = 一路"声道 → 设备"）翻译成内核的 RoutingModel
    /// （LogicalChannel 抽头 + ChannelBinding 绑定 + SinkSpec 设备）。
    ///
    /// 翻译规则（每一条都对应一个曾经的坑）：
    ///  1. **按设备分组**: 同一台设备上的多张卡片合成**一路 sink**，卡片的先后顺序就是该设备的
    ///     物理声道 0/1/2…。内核不能对同一端点开两条 WasapiOut（会互相抢占，其中一路静音），
    ///     所以"分组"不是优化，是正确性前提。
    ///  2. **抽头按扬声器位取**: 卡片的声道键（FL/FR/C/LFE/SL/SR/BL/BR）就是逻辑声道键，
    ///     抽头由 Windows 端点声道顺序表（KSAUDIO_SPEAKER_*）决定。真 5.1 源进来时中置/低音/
    ///     环绕各取各的源声道；源不够时才按前置派生（与 RoutingModel 模板同一套系数）。
    ///  3. **延迟是设备级**: 内核每路一条延迟线，所以同一设备的多张卡片只有一个延迟
    ///     （取最小，宁可少补也不多补），不一致时记一条警告由调用方打印。
    ///  4. **音量按设备归属**: 一台设备只挂一张卡 → 该卡音量写设备端点（与系统音量条双向同步、
    ///     停止时还原）；同一台设备挂多张卡 → 这些卡的音量乘进混音矩阵的绑定增益。
    ///     依据是 Windows 端点音量本身就是设备级的一个值，多路共设备时写端点必然互相覆盖。
    ///  5. **时钟组**: 立体声/单声道规格下所有设备共享一个校正比例（送的是同一对相关声，
    ///     各自独立算 ratio 会偏音）；多声道规格下每台设备独立成组，否则个体水位发散会让
    ///     设备间对齐随时间漂移（见 docs/多设备声道路由设计.md §5.5）。
    /// </summary>
    static class LaneRouting
    {
        /// <summary>规格 → 声道清单（键 + 显示名）。键与 Windows 扬声器位对应。</summary>
        public static List<KeyValuePair<string, string>> SpecChannels(int specIndex)
        {
            switch (specIndex)
            {
                case 0: return Pairs(new string[] { "M" }, new string[] { "单声道" });
                case 2: return Pairs(new string[] { "FL", "FR", "LFE" }, new string[] { "左", "右", "低音" });
                case 3: return Pairs(new string[] { "FL", "FR", "BL", "BR" }, new string[] { "左前", "右前", "左后", "右后" });
                case 4: return Pairs(new string[] { "FL", "FR", "C", "LFE", "SL", "SR" }, new string[] { "左前", "右前", "中置", "低音", "左环绕", "右环绕" });
                default: return Pairs(new string[] { "FL", "FR" }, new string[] { "左", "右" });
            }
        }

        static List<KeyValuePair<string, string>> Pairs(string[] keys, string[] names)
        {
            var list = new List<KeyValuePair<string, string>>();
            for (int i = 0; i < keys.Length; i++) list.Add(new KeyValuePair<string, string>(keys[i], names[i]));
            return list;
        }

        public static string DisplayName(int specIndex, string key)
        {
            var chans = SpecChannels(specIndex);
            for (int i = 0; i < chans.Count; i++) if (chans[i].Key == key) return chans[i].Value;
            return key;
        }

        /// <summary>
        /// 音量"归设备端点"的那几张卡片 = 独占一台设备的卡片。
        /// <para>规则: 按 DeviceId 分组, 组内只有 1 张卡 → 该卡走端点; 组内 ≥2 张 → 谁都不走端点。</para>
        /// <para>为什么不能多张卡共享端点: Windows 端点音量是设备级单值, 两张卡各写各的会互相覆盖
        /// （旧实现里"左右选同一设备"时就是两滑条抢一个端点），所以同设备多卡只能走混音增益。</para>
        /// </summary>
        public static List<OutputLane> ResolveEndpointLanes(IList<OutputLane> lanes)
        {
            return ResolveEndpointLanes(lanes, null);
        }

        /// <param name="skipDevices">端点打不开的设备 (如没有音量控制的 HDMI): 其卡片退回混音增益。</param>
        public static List<OutputLane> ResolveEndpointLanes(IList<OutputLane> lanes, ICollection<string> skipDevices)
        {
            var result = new List<OutputLane>();
            if (lanes == null || lanes.Count == 0) return result;

            var count = new Dictionary<string, int>(StringComparer.Ordinal);
            for (int i = 0; i < lanes.Count; i++)
            {
                string d = lanes[i].DeviceId ?? "";
                if (d.Length == 0) continue;
                int n;
                count.TryGetValue(d, out n);
                count[d] = n + 1;
            }
            for (int i = 0; i < lanes.Count; i++)
            {
                string d = lanes[i].DeviceId ?? "";
                if (d.Length == 0) continue;
                if (skipDevices != null && skipDevices.Contains(d)) continue;
                if (count[d] == 1) result.Add(lanes[i]);
            }
            return result;
        }

        /// <summary>该卡片是否独占其设备（= 音量/静音写设备端点）。</summary>
        public static bool IsEndpointLane(IList<OutputLane> lanes, OutputLane lane)
        {
            if (lane == null) return false;
            string d = lane.DeviceId ?? "";
            if (d.Length == 0) return false;
            int n = 0;
            for (int i = 0; i < lanes.Count; i++)
                if ((lanes[i].DeviceId ?? "") == d) n++;
            return n == 1;
        }

        /// <summary>
        /// 卡片 → 内核模型。
        /// <para>srcCh: 捕获源声道数（0 = 还不知道，按立体声上混；启动后拿到真值应重建一次并热替换）。</para>
        /// <para>音量归属由 <see cref="ResolveEndpointLanes"/> 内部决定, 调用方不必传。</para>
        /// <para>delayConflict: 同一设备上出现不同延迟时置 true（调用方据此打一条警告）。</para>
        /// </summary>
        public static RoutingModel Build(IList<OutputLane> lanes, int specIndex, int srcCh,
            Func<string, string> deviceNameOf, out bool delayConflict)
        {
            return Build(lanes, specIndex, srcCh, deviceNameOf, null, out delayConflict);
        }

        /// <param name="skipDevices">端点音量不可用的设备: 其卡片按混音增益处理, 不再双份衰减。</param>
        public static RoutingModel Build(IList<OutputLane> lanes, int specIndex, int srcCh,
            Func<string, string> deviceNameOf, ICollection<string> skipDevices, out bool delayConflict)
        {
            delayConflict = false;
            var model = new RoutingModel
            {
                Mode = WorkMode.Custom,          // 清单由界面决定，不让模板反推
                Layout = LayoutOf(specIndex)
            };

            // ---- 逻辑声道: 规格里的每个声道一个, 抽头按扬声器位取 ----
            // srcCh=0 (还没启动, 拿不到捕获格式) 按立体声假设: 与两路时代的上混结果一致。
            // 启动后拿到真实声道数会用真值重建一次并热替换 (AudioEngine.SetRouting)。
            int effectiveSrc = srcCh <= 0 ? 2 : srcCh;
            var chans = SpecChannels(specIndex);
            var map = RoutingModel.MapOf(effectiveSrc);
            for (int i = 0; i < chans.Count; i++)
            {
                var ch = new LogicalChannel(chans[i].Key, chans[i].Value, KindOf(chans[i].Key));
                ch.Order = i;
                AddTaps(ch, chans[i].Key, map, srcCh, specIndex);
                model.Channels.Add(ch);
            }

            if (lanes == null || lanes.Count == 0) return model;

            // 音量归属: 独占一台设备的卡片走端点, 同设备多卡走混音增益 (见 ResolveEndpointLanes)
            var endpointLanes = ResolveEndpointLanes(lanes, skipDevices);

            // ---- 按设备分组 → 每台设备一路 sink ----
            var sinkOfDevice = new Dictionary<string, SinkSpec>(StringComparer.Ordinal);
            var physCount = new Dictionary<string, int>(StringComparer.Ordinal);
            // 共享 ratio 的时机: 所有设备送的都是**同一对相关声** (立体声/单声道规格, 哪怕接了 3 台)
            // → 必须同组, 否则各自算 ratio 会让插值方向不一致、长期平均增益有差 → 偏音。
            // 多声道规格 (2.1/四声道/5.1) 每台设备各持**不同**声道 → 必须独立组, 否则个体水位发散
            // 会让设备间对齐随时间漂移 (docs/多设备声道路由设计.md §5.5)。
            bool shareClock = specIndex <= 1;

            for (int i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                string dev = lane.DeviceId ?? "";
                SinkSpec sink;
                int phys;
                if (sinkOfDevice.TryGetValue(dev, out sink))
                {
                    phys = physCount[dev];
                    // 设备级延迟: 取最小（少补总比多补好：多补会把这一路推后于其它设备）
                    if (lane.DelayMs != sink.DelayMs)
                    {
                        delayConflict = true;
                        if (lane.DelayMs < sink.DelayMs) sink.DelayMs = lane.DelayMs;
                    }
                }
                else
                {
                    string key = "s" + sinkOfDevice.Count;
                    string label = DisplayName(specIndex, lane.ChannelKey);
                    // 同名标签会让日志分不清是哪一路 → 同显式补序号
                    foreach (var s in sinkOfDevice.Values)
                        if (s.Label == label) label = label + (sinkOfDevice.Count + 1);
                    sink = new SinkSpec(key, label, dev, 1, Math.Max(0, lane.DelayMs));
                    sink.Volume = 1f;                 // 音量进绑定增益 / 端点，不走这一层
                    sink.ClockGroupId = shareClock ? "g0" : "";
                    sink.CachedName = deviceNameOf == null ? "" : (deviceNameOf(dev) ?? "");
                    sinkOfDevice[dev] = sink;
                    physCount[dev] = 0;
                    model.SinkSpecs.Add(sink);
                    phys = 0;
                }
                physCount[dev] = phys + 1;
                sink.OutChannels = physCount[dev];

                // 音量/静音: 独占该设备的卡片由端点音量负责（矩阵必须 1.0，否则双份衰减）；
                // 同设备多卡进矩阵（端点音量是设备级单值，多路写会互相覆盖）
                bool endpointVolume = endpointLanes.Count > 0 && endpointLanes.Contains(lane);
                float gain = 1f;
                if (!endpointVolume)
                {
                    int vol = Math.Max(0, Math.Min(100, lane.Volume));
                    gain = lane.Mute ? 0f : vol / 100f;
                }
                model.Bindings.Add(new ChannelBinding(lane.ChannelKey, sink.Key, phys, gain));
            }

            return model;
        }

        static ChannelLayout LayoutOf(int specIndex)
        {
            switch (specIndex)
            {
                case 0: return ChannelLayout.Mono;
                case 2: return ChannelLayout.Custom;      // 2.1 没有对应枚举值，按自定义处理
                case 3: return ChannelLayout.Quad;
                case 4: return ChannelLayout.Surround51;
                default: return ChannelLayout.Stereo;
            }
        }

        static ChannelKind KindOf(string key)
        {
            switch (key)
            {
                case "FL": return ChannelKind.FL;
                case "FR": return ChannelKind.FR;
                case "C": return ChannelKind.C;
                case "LFE": return ChannelKind.LFE;
                case "SL": return ChannelKind.SL;
                case "SR": return ChannelKind.SR;
                case "BL": return ChannelKind.BL;
                case "BR": return ChannelKind.BR;
                default: return ChannelKind.Generic;
            }
        }

        /// <summary>
        /// 声道键 → 源抽头。源声道不够（srcCh ≤ 2 或对应位缺失）时按前置派生，
        /// 系数与 RoutingModel.BuildSurround 的上混分支保持一致。
        /// </summary>
        static void AddTaps(LogicalChannel ch, string key, RoutingModel.SourceMap map, int srcCh, int specIndex)
        {
            float l = 1f, r = 1f;
            switch (key)
            {
                case "M":
                    ch.Taps.Add(new SourceTap(map.FL, 0.5f));
                    ch.Taps.Add(new SourceTap(map.FR, 0.5f));
                    if (map.HasCenter) ch.Taps.Add(new SourceTap(map.C, 0.35f));
                    return;

                case "FL":
                    ch.Taps.Add(new SourceTap(map.FL, l));
                    // 立体声规格遇上真多声道源: 中置折进左右，否则对白人声整条丢掉
                    if (specIndex == 1 && map.HasCenter) ch.Taps.Add(new SourceTap(map.C, 0.707f));
                    return;

                case "FR":
                    ch.Taps.Add(new SourceTap(map.FR, r));
                    if (specIndex == 1 && map.HasCenter) ch.Taps.Add(new SourceTap(map.C, 0.707f));
                    return;

                case "C":
                    if (map.HasCenter) ch.Taps.Add(new SourceTap(map.C, 1f));
                    else { ch.Taps.Add(new SourceTap(map.FL, 0.5f)); ch.Taps.Add(new SourceTap(map.FR, 0.5f)); }
                    return;

                case "LFE":
                    if (map.LFE >= 0) ch.Taps.Add(new SourceTap(map.LFE, 1f));
                    else { ch.Taps.Add(new SourceTap(map.FL, 0.35f)); ch.Taps.Add(new SourceTap(map.FR, 0.35f)); }
                    return;

                case "SL":
                    if (map.SurL >= 0) ch.Taps.Add(new SourceTap(map.SurL, 1f));
                    else ch.Taps.Add(new SourceTap(map.FL, 0.7f));
                    return;

                case "SR":
                    if (map.SurR >= 0) ch.Taps.Add(new SourceTap(map.SurR, 1f));
                    else ch.Taps.Add(new SourceTap(map.FR, 0.7f));
                    return;

                case "BL":
                    if (map.BL >= 0) ch.Taps.Add(new SourceTap(map.BL, 1f));
                    else ch.Taps.Add(new SourceTap(map.FL, 0.7f));
                    return;

                case "BR":
                    if (map.BR >= 0) ch.Taps.Add(new SourceTap(map.BR, 1f));
                    else ch.Taps.Add(new SourceTap(map.FR, 0.7f));
                    return;

                default:
                    ch.Taps.Add(new SourceTap(map.FL, 0.5f));
                    ch.Taps.Add(new SourceTap(map.FR, 0.5f));
                    return;
            }
        }
    }
}
