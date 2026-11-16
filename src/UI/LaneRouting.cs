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
    ///  4. **音量分两处**: 左右两张"端点卡片"的音量仍写设备端点（与设备音量双向同步，行为不变）；
    ///     其余卡片的音量乘进混音矩阵的绑定增益 —— 端点音量是共享的，多路同设备时必然打架。
    ///  5. **时钟组**: 两路（左右分离）共享一个校正比例；三路起每台设备独立，否则个体水位
    ///     发散会让设备间对齐随时间漂移（见 docs/多设备声道路由设计.md §5.5）。
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
        /// 卡片 → 内核模型。
        /// <para>srcCh: 捕获源声道数（0 = 还不知道，按立体声上混；启动后拿到真值应重建一次并热替换）。</para>
        /// <para>endpointVolA/B: 音量走设备端点的两张卡片（左右主输出），可为 null。</para>
        /// <para>delayConflict: 同一设备上出现不同延迟时置 true（调用方据此打一条警告）。</para>
        /// </summary>
        public static RoutingModel Build(IList<OutputLane> lanes, int specIndex, int srcCh,
            OutputLane endpointVolA, OutputLane endpointVolB, Func<string, string> deviceNameOf,
            out bool delayConflict)
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

            // ---- 按设备分组 → 每台设备一路 sink ----
            var sinkOfDevice = new Dictionary<string, SinkSpec>(StringComparer.Ordinal);
            var physCount = new Dictionary<string, int>(StringComparer.Ordinal);
            bool shareClock = lanes.Count <= 2;      // 两路时代: 左右共享 ratio（偏音消除）

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

                // 音量/静音: 端点卡片由设备音量负责（这里必须 1.0，否则双份衰减）；其余进矩阵
                bool endpointVolume = ReferenceEquals(lane, endpointVolA) || ReferenceEquals(lane, endpointVolB);
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
