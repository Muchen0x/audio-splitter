using System;
using System.Collections.Generic;

namespace StereoSplitter
{
    /// <summary>
    /// 把界面模型（输出卡片 = 一台设备，卡片里一行 = 一个物理声道）翻译成内核的 RoutingModel
    /// （LogicalChannel 抽头 + ChannelBinding 绑定 + SinkSpec 设备）。
    ///
    /// 翻译规则（每一条都对应一个曾经的坑）：
    ///  1. **一张卡片 = 一台设备**: 同设备的多张卡片在界面上就会被合并成一张多声道卡片，
    ///     这里的分组只是兜底 —— 内核不能对同一端点开两条 WasapiOut（会互相抢占，
    ///     其中一路静音），所以"分组"不是优化，是正确性前提。
    ///  2. **卡片里的行 = 物理声道**: 行序 p0/p1/… 就是设备物理声道号（PhysIndex），
    ///     空行不产生 lane → 该物理声道静音。抽头由 Windows 端点声道顺序表决定。
    ///  3. **延迟是设备级**: 内核每路一条延迟线，合并时取最小（宁可少补也不多补），
    ///     不一致时置 delayConflict 由调用方打印。
    ///  4. **音量归属**: 卡片主音量 = 设备端点音量（端点不可用时回落成增益，见 Flatten）；
    ///     多声道各行音量是矩阵增益。端点音量是设备级单值，所以只有卡片主音量能写它。
    ///  5. **时钟组**: 立体声/单声道规格下所有设备共享一个校正比例（送的是同一对相关声，
    ///     各自独立算 ratio 会偏音）；多声道规格下每台设备独立成组，否则个体水位发散会让
    ///     设备间对齐随时间漂移（见 docs/多设备声道路由设计.md §5.5）。
    /// </summary>
    static class LaneRouting
    {
        /// <summary>声道下拉里「多声道」项的哨兵键 / 显示文字。</summary>
        public const string MultiKey = "*multi*";
        public const string MultiText = "多声道…";

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

        // ---------------------------------------------------------------- 卡片 → lane

        /// <summary>
        /// 卡片模型 → 内核 lane 列表 (启动/热更新/存盘前的唯一出口)。
        /// <para>音量在这里就折算成矩阵增益: 端点承载的卡片写 1.0（否则双份衰减），
        /// 端点不可用（HDMI 无音量接口/被独占）的卡片把主音量乘进来。</para>
        /// </summary>
        /// <param name="endpointAvailable">该设备的端点音量是否可用; null = 全部可用。</param>
        public static List<OutputLane> Flatten(IList<OutputCardModel> cards, Func<string, bool> endpointAvailable)
        {
            var list = new List<OutputLane>();
            if (cards == null) return list;

            for (int i = 0; i < cards.Count; i++)
            {
                OutputCardModel c = cards[i];
                string dev = c.DeviceId ?? "";
                bool ep = dev.Length > 0 && (endpointAvailable == null || endpointAvailable(dev));

                if (!c.Multi)
                {
                    ChannelSlot s = c.Slots.Count > 0 ? c.Slots[0] : null;
                    bool empty = s == null || string.IsNullOrEmpty(s.ChannelKey);
                    var lane = new OutputLane
                    {
                        ChannelKey = empty ? "" : s.ChannelKey,
                        DeviceId = dev,
                        DelayMs = Math.Max(0, c.DelayMs),
                        PhysIndex = 0,
                        // F-05: 空声道也要一条"静音占位 lane"撑住 sink 与物理声道数。
                        // ChannelKey="" → Build 不产生绑定 → Compile 全零矩阵, 但该路仍存在。
                        Volume = empty ? 0 : 100,
                        Mute = empty
                    };
                    if (!ep && !empty) { lane.Volume = Math.Max(0, Math.Min(100, c.Volume)); lane.Mute = c.Mute; }
                    // 未选设备 + 空声道没必要造一个 dev="" 的假 sink (会触发 ValidateSinks 报错)。
                    if (!empty || dev.Length > 0) list.Add(lane);
                    continue;
                }

                for (int p = 0; p < c.Slots.Count; p++)
                {
                    ChannelSlot s = c.Slots[p];
                    bool empty = s == null || string.IsNullOrEmpty(s.ChannelKey);
                    // F-05: 保留空行占位 (有设备时), 使 OutChannels 取自卡片实际行数而非最后一个非空 lane。
                    // 仅当设备都没选时跳过, 避免建出 dev="" 的虚假 sink。
                    if (empty && dev.Length == 0) continue;
                    int vol = empty ? 0 : Math.Max(0, Math.Min(100, s.Volume));
                    // 端点不可用 → 主音量的衰减只能落到矩阵上 (先乘后舍, 少一次量化)
                    if (!ep && !empty) vol = (int)Math.Round(vol * Math.Max(0, Math.Min(100, c.Volume)) / 100.0);
                    list.Add(new OutputLane
                    {
                        ChannelKey = empty ? "" : s.ChannelKey,
                        DeviceId = dev,
                        DelayMs = Math.Max(0, c.DelayMs),
                        Volume = vol,
                        Mute = empty ? true : (!ep && c.Mute),
                        PhysIndex = p
                    });
                }
            }
            return list;
        }

        // ---------------------------------------------------------------- 合并同设备卡片

        /// <summary>
        /// 把指向同一台设备的多张卡片合并成一张多声道卡片（就地改列表）。
        /// <para>合并是**响度无损**的: 原来的每张卡片变成新卡片的一行（物理声道），声道照旧;
        /// 设备集合不变 → 声音不变。合并后该设备只剩一张卡片, 主音量才有唯一归属（端点音量）。</para>
        /// <para>注意音量归属: 单声道卡片的音量**就是**该设备的端点音量（镜像同一个值），
        /// 不是逐声道增益。所以合并时单声道来源的行增益一律回到 100 ——
        /// 端点音量交给合并后卡片的主音量唯一承载。不这么做就会双份衰减
        /// (12% 端点 × 12% 增益 = 1.44%)。原本就是多声道卡片的行增益是真增益, 照旧保留。</para>
        /// </summary>
        /// <returns>合并掉了几张卡片 (0 = 没有同设备重复)。</returns>
        public static int MergeSameDevice(IList<OutputCardModel> cards)
        {
            return MergeSameDevice(cards, null);
        }

        /// <summary>
        /// 同 <see cref="MergeSameDevice(IList{OutputCardModel})"/>，但合并时知道每台设备的端点音量是否可用。
        /// <para>F-08：端点不可用时主音量既写不进端点、也不写进矩阵 carrier，必须在合并那一刻把
        /// 每张来源卡的"有效主音量"（静音→0）折进它自己的行增益，否则合并后统一乘新主音量，
        /// 原本静音的那一路会突然按别人的音量发声。</para>
        /// </summary>
        /// <param name="endpointAvailable">null = 全部端点可用（旧行为）。</param>
        public static int MergeSameDevice(IList<OutputCardModel> cards, Func<string, bool> endpointAvailable)
        {
            if (cards == null || cards.Count < 2) return 0;

            var kept = new List<OutputCardModel>();
            var byDevice = new Dictionary<string, OutputCardModel>(StringComparer.Ordinal);
            int removed = 0;

            for (int i = 0; i < cards.Count; i++)
            {
                OutputCardModel c = cards[i];
                string dev = c.DeviceId ?? "";
                if (dev.Length == 0 || !byDevice.ContainsKey(dev))
                {
                    if (dev.Length > 0) byDevice[dev] = c;
                    kept.Add(c);
                    continue;
                }

                // 同一台设备的端点可用性对两张卡相同，取一次即可
                bool ep = endpointAvailable == null || endpointAvailable(dev);
                MergeInto(byDevice[dev], c, ep);
                removed++;
            }

            if (removed == 0) return 0;
            cards.Clear();
            for (int i = 0; i < kept.Count; i++) cards.Add(kept[i]);
            return removed;
        }

        /// <summary>把 <paramref name="src"/> 的各行追加到 <paramref name="host"/>。</summary>
        static void MergeInto(OutputCardModel host, OutputCardModel src, bool endpointAvailable)
        {
            if (host.Slots.Count == 0) host.Slots.Add(new ChannelSlot());
            bool hostWasSingle = !host.Multi;
            host.Multi = true;

            if (endpointAvailable)
            {
                // 端点承载主音量：单声道来源的行音量只是端点镜像，回 100 交给端点唯一承载（避免双衰减）
                if (hostWasSingle)
                    for (int i = 0; i < host.Slots.Count; i++) host.Slots[i].Volume = 100;
            }
            else
            {
                // F-08：端点不可用 → 把 host 的有效主音量（静音→0）折进它现有的每一行。
                // 单声道卡的行音量是主音量镜像，直接用主音量；多声道卡的行是真增益，按主音量缩放。
                int hostEff = host.Mute ? 0 : ClampVol(host.Volume);
                for (int i = 0; i < host.Slots.Count; i++)
                    host.Slots[i].Volume = hostWasSingle
                        ? hostEff
                        : (int)Math.Round(ClampVol(host.Slots[i].Volume) * hostEff / 100.0);
            }

            if (src.DelayMs < host.DelayMs) host.DelayMs = src.DelayMs;   // 设备级延迟取最小

            if (src.Multi)
            {
                int srcEff = endpointAvailable ? 100 : (src.Mute ? 0 : ClampVol(src.Volume));
                for (int i = 0; i < src.Slots.Count; i++)
                {
                    ChannelSlot s = Copy(src.Slots[i]);
                    if (!endpointAvailable)
                        s.Volume = (int)Math.Round(ClampVol(s.Volume) * srcEff / 100.0);
                    host.Slots.Add(s);
                }
            }
            else
            {
                ChannelSlot s = Copy(src.Slots.Count > 0 ? src.Slots[0] : new ChannelSlot());
                // 单声道来源的行音量是端点镜像：端点可用→回 100；不可用→折进有效主音量
                s.Volume = endpointAvailable ? 100 : (src.Mute ? 0 : ClampVol(src.Volume));
                host.Slots.Add(s);
            }

            if (!endpointAvailable)
            {
                // 主音量与静音都已折进各行 → 合并后主音量中性化，Flatten 的 !ep 分支不再二次衰减。
                // 绝不无条件 Mute=false：静音语义已体现在各行增益里，这里的 Mute 只是不再生效的中性值。
                host.Volume = 100;
                host.Mute = false;
            }
        }

        static int ClampVol(int v) { return Math.Max(0, Math.Min(100, v)); }

        static ChannelSlot Copy(ChannelSlot s)
        {
            return new ChannelSlot
            {
                ChannelKey = s == null ? "" : s.ChannelKey,
                Volume = s == null ? 100 : s.Volume
            };
        }

        // ---------------------------------------------------------------- 卡片 → 内核模型

        /// <summary>
        /// 卡片模型 → 内核模型。
        /// <para>srcCh: 捕获源声道数（0 = 还不知道，按立体声上混；启动后拿到真值会重建一次并热替换）。</para>
        /// <para>delayConflict: 同一设备上出现不同延迟时置 true（调用方据此打一条警告）。</para>
        /// </summary>
        public static RoutingModel Build(IList<OutputLane> lanes, int specIndex, int srcCh,
            Func<string, string> deviceNameOf, out bool delayConflict)
        {
            delayConflict = false;
            var model = new RoutingModel
            {
                Mode = WorkMode.Custom,          // 清单由界面决定，不让模板反推
                Layout = LayoutOf(specIndex)
            };

            // ---- 逻辑声道: 规格里的每个声道一个, 抽头按扬声器位取 ----
            // srcCh=0 (还没启动, 拿不到捕获格式) 按立体声假设: 与两路时代的上混结果一致。
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

            // 共享 ratio 的时机: 所有设备送的都是**同一对相关声** (立体声/单声道规格, 哪怕接了 3 台)
            // → 必须同组, 否则各自算 ratio 会让插值方向不一致、长期平均增益有差 → 偏音。
            // 多声道规格 (2.1/四声道/5.1) 每台设备各持**不同**声道 → 必须独立组, 否则个体水位发散
            // 会让设备间对齐随时间漂移 (docs/多设备声道路由设计.md §5.5)。
            bool shareClock = specIndex <= 1;

            var sinkOfDevice = new Dictionary<string, SinkSpec>(StringComparer.Ordinal);
            var usedPhys = new Dictionary<string, HashSet<int>>(StringComparer.Ordinal);

            for (int i = 0; i < lanes.Count; i++)
            {
                var lane = lanes[i];
                string dev = lane.DeviceId ?? "";
                SinkSpec sink;
                if (sinkOfDevice.TryGetValue(dev, out sink))
                {
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
                    string label = string.IsNullOrEmpty(lane.ChannelKey)
                        ? "输出" + (sinkOfDevice.Count + 1)
                        : DisplayName(specIndex, lane.ChannelKey);
                    // 同名标签会让日志分不清是哪一路 → 显式补序号
                    foreach (var s in sinkOfDevice.Values)
                        if (s.Label == label) label = label + (sinkOfDevice.Count + 1);
                    sink = new SinkSpec(key, label, dev, 0, Math.Max(0, lane.DelayMs));
                    sink.Volume = 1f;                 // 音量进绑定增益 / 端点，不走这一层
                    sink.ClockGroupId = shareClock ? "g0" : "";
                    sink.CachedName = deviceNameOf == null ? "" : (deviceNameOf(dev) ?? "");
                    sinkOfDevice[dev] = sink;
                    usedPhys[dev] = new HashSet<int>();
                    model.SinkSpecs.Add(sink);
                }

                // 物理声道: 卡片行号优先, 冲突/未指定时取该设备的最小空闲号
                HashSet<int> used = usedPhys[dev];
                int phys = lane.PhysIndex;
                if (phys < 0 || used.Contains(phys))
                {
                    phys = 0;
                    while (used.Contains(phys)) phys++;
                }
                used.Add(phys);
                if (phys + 1 > sink.OutChannels) sink.OutChannels = phys + 1;

                float gain = lane.Mute ? 0f : Math.Max(0, Math.Min(100, lane.Volume)) / 100f;
                model.Bindings.Add(new ChannelBinding(lane.ChannelKey, sink.Key, phys, gain));
            }

            // 没有任何 lane 的设备不参与输出 (OutChannels 会停在 0)
            for (int i = model.SinkSpecs.Count - 1; i >= 0; i--)
                if (model.SinkSpecs[i].OutChannels <= 0) model.SinkSpecs.RemoveAt(i);

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
