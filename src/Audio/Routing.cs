using System;
using System.Collections.Generic;
using System.Text;

namespace StereoSplitter
{
    // ==================================================================================
    // 编辑期模型: 源声道 --Taps--> 逻辑声道 --Bind--> (设备, 物理声道)
    //
    // 运行时只吃 Compile() 折叠出来的矩阵, 三层结构不进逐样本循环 —— 这是整套多设备
    // 路由的设计支点: 逻辑声道带来可读性与可视化, 但不带来任何运行时代价。
    // ==================================================================================

    enum WorkMode
    {
        SplitLr,        // 左右分离: 2 声道, 各送一台单声道设备 (原行为)
        MirrorStereo,   // 同源并播: 2 声道, 送全部设备
        Surround,       // 拼装环绕: N 台 2 声道设备拼 N×2
        MonoBroadcast,  // 单声广播: 1 个混音声道送全部设备
        Custom          // 自定义: 不动清单
    }

    enum ChannelKind { FL, FR, C, LFE, SL, SR, BL, BR, Generic }

    enum ChannelLayout { Mono, Stereo, Quad, Surround51, Surround71, Custom }

    enum IssueSev { Error, Warn, Info }

    struct SourceTap
    {
        public int Src;
        public float Gain;
        public SourceTap(int src, float gain) { Src = src; Gain = gain; }
    }

    sealed class LogicalChannel
    {
        public string Key;
        public string Name;
        public ChannelKind Kind;
        public int Order;
        public List<SourceTap> Taps = new List<SourceTap>();
        public float Gain = 1f;
        public bool Mute;

        public LogicalChannel(string key, string name, ChannelKind kind, params SourceTap[] taps)
        {
            Key = key; Name = name; Kind = kind;
            if (taps != null) Taps.AddRange(taps);
        }

        public int BindingCount { get; set; }

        public LogicalChannel Clone()
        {
            var c = new LogicalChannel(Key, Name, Kind);
            c.Taps.AddRange(Taps);
            c.Gain = Gain; c.Mute = Mute; c.Order = Order;
            return c;
        }
    }

    sealed class ChannelBinding
    {
        public string ChannelKey;
        public string SinkKey;
        public int Phys;
        public float Gain = 1f;

        public ChannelBinding(string channelKey, string sinkKey, int phys, float gain)
        {
            ChannelKey = channelKey; SinkKey = sinkKey; Phys = phys; Gain = gain;
        }
    }

    sealed class RoutingIssue
    {
        public IssueSev Severity;
        public string Where;
        public string Message;
        public RoutingIssue(IssueSev sev, string where, string message)
        {
            Severity = sev; Where = where; Message = message;
        }
        public override string ToString() { return Message; }
    }

    /// <summary>一路输出的配置 (设备无关部分)。设备解析在引擎里做。</summary>
    sealed class SinkSpec
    {
        public string Key;
        public string Label;
        public string DeviceId;
        public int OutChannels = 1;    // 该路实际使用几个物理声道 (1 ~ 端点声道数)
        public int DelayMs;
        public string ClockGroupId = "";   // 空 = 独立组
        /// <summary>0 = 不建链路。停用只影响启动, 配置 (含绑定) 原样保留。</summary>
        public bool Enabled = true;
        /// <summary>友好名缓存: 设备 ID 变了 (重装/换口) 时按名字二级匹配。</summary>
        public string CachedName = "";

        public SinkSpec(string key, string label, string deviceId, int outChannels, int delayMs)
        {
            Key = key; Label = label; DeviceId = deviceId;
            OutChannels = outChannels < 1 ? 1 : outChannels;
            DelayMs = delayMs;
        }

        public SinkSpec Clone()
        {
            var s = new SinkSpec(Key, Label, DeviceId, OutChannels, DelayMs);
            s.ClockGroupId = ClockGroupId;
            s.Enabled = Enabled;
            s.CachedName = CachedName;
            return s;
        }
    }

    /// <summary>编译产物: 每路一张 [outCh, srcCh] 矩阵, 运行时只读。</summary>
    sealed class CompiledRouting
    {
        public string[] SinkKeys;
        public int[] SinkOutCh;
        public float[][,] SinkMatrix;

        public int IndexOf(string sinkKey)
        {
            if (SinkKeys == null) return -1;
            for (int i = 0; i < SinkKeys.Length; i++)
                if (string.Equals(SinkKeys[i], sinkKey, StringComparison.Ordinal)) return i;
            return -1;
        }
    }

    partial class RoutingModel
    {
        public WorkMode Mode = WorkMode.SplitLr;
        public ChannelLayout Layout = ChannelLayout.Stereo;
        public List<LogicalChannel> Channels = new List<LogicalChannel>();
        public List<ChannelBinding> Bindings = new List<ChannelBinding>();

        public int ChannelCount { get { return Channels.Count; } }

        /// <summary>深拷贝 (配置 → 运行期快照; 运行中改模型不影响已编译的矩阵)。</summary>
        public RoutingModel Clone()
        {
            var m = new RoutingModel { Mode = Mode, Layout = Layout };
            for (int i = 0; i < Channels.Count; i++) m.Channels.Add(Channels[i].Clone());
            for (int i = 0; i < Bindings.Count; i++)
            {
                var b = Bindings[i];
                m.Bindings.Add(new ChannelBinding(b.ChannelKey, b.SinkKey, b.Phys, b.Gain));
            }
            for (int i = 0; i < SinkSpecs.Count; i++) m.SinkSpecs.Add(SinkSpecs[i].Clone());
            return m;
        }

        /// <summary>只留启用的路 (引擎/编译都吃这个列表)。顺序不变, 与 ini 索引一致。</summary>
        public List<SinkSpec> EnabledSinks()
        {
            var list = new List<SinkSpec>();
            for (int i = 0; i < SinkSpecs.Count; i++)
                if (SinkSpecs[i].Enabled) list.Add(SinkSpecs[i]);
            return list;
        }

        /// <summary>
        /// 两路时代的下拉框仍影响 s0/s1 —— P2 阶段 UI 只有这两个控件,
        /// 因此把它们视为 s0/s1 的设备/延迟权威源 (P3 换成设备列表后删除此桥接)。
        /// </summary>
        public void ApplyTwoSinkLegacy(string leftId, int leftDelay, string rightId, int rightDelay)
        {
            EnsureLegacySinks();
            var l = SinkSpecs[0];
            var r = SinkSpecs[1];
            l.DeviceId = leftId ?? ""; l.Label = "左"; l.DelayMs = leftDelay;
            r.DeviceId = rightId ?? ""; r.Label = "右"; r.DelayMs = rightDelay;
        }

        void EnsureLegacySinks()
        {
            while (SinkSpecs.Count < 2)
            {
                int n = SinkSpecs.Count;
                var s = new SinkSpec("s" + n, n == 0 ? "左" : "右", "", n == 0 ? 1 : 1, 0);
                s.ClockGroupId = "g0";
                SinkSpecs.Add(s);
            }
            // 旧配置回退到左右分离时, 逻辑声道可能还没建 (ini 里只有设备名)
            if (Channels.Count == 0) ApplyModeTemplate(WorkMode.SplitLr, SinkSpecs);
        }

        public LogicalChannel FindChannel(string key)
        {
            for (int i = 0; i < Channels.Count; i++)
                if (Channels[i].Key == key) return Channels[i];
            return null;
        }

        // ---------------------------------------------------------------- 模板
        /// <summary>按模式生成默认清单与绑定。custom 不动。返回是否发生了改动。</summary>
        public bool ApplyModeTemplate(WorkMode mode, IList<SinkSpec> sinks)
        {
            if (mode == WorkMode.Custom) { Mode = mode; return false; }
            Mode = mode;
            switch (mode)
            {
                case WorkMode.SplitLr: BuildSplitLr(sinks); return true;
                case WorkMode.MirrorStereo: BuildMirror(sinks); return true;
                case WorkMode.Surround: BuildSurround(Layout, sinks); return true;
                case WorkMode.MonoBroadcast: BuildMono(sinks); return true;
            }
            return false;
        }

        /// <summary>原行为的等价表达: 两个逻辑声道各绑一台单声道设备。</summary>
        public static RoutingModel SplitLr(string leftKey, string leftDev, string leftLabel, int leftDelay,
                                           string rightKey, string rightDev, string rightLabel, int rightDelay)
        {
            var m = new RoutingModel { Mode = WorkMode.SplitLr, Layout = ChannelLayout.Stereo };
            m.Channels.Add(new LogicalChannel("ch0", "左", ChannelKind.FL, new SourceTap(0, 1f)));
            m.Channels.Add(new LogicalChannel("ch1", "右", ChannelKind.FR, new SourceTap(1, 1f)));
            m.Bindings.Add(new ChannelBinding("ch0", leftKey, 0, 1f));
            m.Bindings.Add(new ChannelBinding("ch1", rightKey, 0, 1f));
            m.SinkSpecs = new List<SinkSpec>
            {
                new SinkSpec(leftKey, leftLabel, leftDev, 1, leftDelay) { ClockGroupId = "g0" },
                new SinkSpec(rightKey, rightLabel, rightDev, 1, rightDelay) { ClockGroupId = "g0" }
            };
            return m;
        }

        /// <summary>与清单配套的设备规格 (P1 由 SplitLr 工厂填充; P2 起从 ini 读)。</summary>
        public List<SinkSpec> SinkSpecs = new List<SinkSpec>();

        void BuildSplitLr(IList<SinkSpec> sinks)
        {
            Channels.Clear(); Bindings.Clear();
            Layout = ChannelLayout.Stereo;
            Channels.Add(new LogicalChannel("ch0", "左", ChannelKind.FL, new SourceTap(0, 1f)));
            Channels.Add(new LogicalChannel("ch1", "右", ChannelKind.FR, new SourceTap(1, 1f)));
            if (sinks == null) return;
            for (int i = 0; i < sinks.Count && i < 2; i++)
            {
                sinks[i].OutChannels = 1;
                Bindings.Add(new ChannelBinding(Channels[i].Key, sinks[i].Key, 0, 1f));
            }
        }

        void BuildMirror(IList<SinkSpec> sinks)
        {
            Channels.Clear(); Bindings.Clear();
            Layout = ChannelLayout.Stereo;
            Channels.Add(new LogicalChannel("ch0", "左", ChannelKind.FL, new SourceTap(0, 1f)));
            Channels.Add(new LogicalChannel("ch1", "右", ChannelKind.FR, new SourceTap(1, 1f)));
            if (sinks == null) return;
            for (int i = 0; i < sinks.Count; i++)
            {
                sinks[i].OutChannels = 2;
                Bindings.Add(new ChannelBinding("ch0", sinks[i].Key, 0, 1f));
                Bindings.Add(new ChannelBinding("ch1", sinks[i].Key, 1, 1f));
            }
        }

        void BuildMono(IList<SinkSpec> sinks)
        {
            Channels.Clear(); Bindings.Clear();
            Layout = ChannelLayout.Mono;
            Channels.Add(new LogicalChannel("ch0", "单声道", ChannelKind.C,
                new SourceTap(0, 0.5f), new SourceTap(1, 0.5f)));
            if (sinks == null) return;
            for (int i = 0; i < sinks.Count; i++)
            {
                int n = sinks[i].OutChannels < 2 ? 1 : 2;
                sinks[i].OutChannels = n;
                for (int p = 0; p < n; p++)
                    Bindings.Add(new ChannelBinding("ch0", sinks[i].Key, p, 1f));
            }
        }

        void BuildSurround(ChannelLayout layout, IList<SinkSpec> sinks)
        {
            Channels.Clear(); Bindings.Clear();
            if (layout != ChannelLayout.Surround51 && layout != ChannelLayout.Surround71 && layout != ChannelLayout.Quad)
                layout = ChannelLayout.Surround51;
            Layout = layout;

            // 2 声道源的上混系数 (5.1/7.1); 若捕获是真 6ch, P4 再按 srcCh 换另一套
            switch (layout)
            {
                case ChannelLayout.Quad:
                    AddCh("FL", "左前置", ChannelKind.FL, T(0, 1f));
                    AddCh("FR", "右前置", ChannelKind.FR, T(1, 1f));
                    AddCh("BL", "左后置", ChannelKind.BL, T(0, 0.7f));
                    AddCh("BR", "右后置", ChannelKind.BR, T(1, 0.7f));
                    break;
                case ChannelLayout.Surround71:
                    AddCh("FL", "左前置", ChannelKind.FL, T(0, 1f));
                    AddCh("FR", "右前置", ChannelKind.FR, T(1, 1f));
                    AddCh("C", "中置", ChannelKind.C, new SourceTap(0, 0.5f), new SourceTap(1, 0.5f));
                    AddCh("LFE", "低音", ChannelKind.LFE, new SourceTap(0, 0.35f), new SourceTap(1, 0.35f));
                    AddCh("SL", "左环绕", ChannelKind.SL, T(0, 0.7f));
                    AddCh("SR", "右环绕", ChannelKind.SR, T(1, 0.7f));
                    AddCh("BL", "左后置", ChannelKind.BL, T(0, 0.7f));
                    AddCh("BR", "右后置", ChannelKind.BR, T(1, 0.7f));
                    break;
                default:
                    AddCh("FL", "左前置", ChannelKind.FL, T(0, 1f));
                    AddCh("FR", "右前置", ChannelKind.FR, T(1, 1f));
                    AddCh("C", "中置", ChannelKind.C, new SourceTap(0, 0.5f), new SourceTap(1, 0.5f));
                    AddCh("LFE", "低音", ChannelKind.LFE, new SourceTap(0, 0.35f), new SourceTap(1, 0.35f));
                    AddCh("SL", "左环绕", ChannelKind.SL, T(0, 0.7f));
                    AddCh("SR", "右环绕", ChannelKind.SR, T(1, 0.7f));
                    break;
            }

            if (sinks == null) return;
            // 按设备顺序两两一组: 每台设备吃 2 个声道
            int ci = 0;
            for (int i = 0; i < sinks.Count && ci < Channels.Count; i++)
            {
                sinks[i].OutChannels = 2;
                Bindings.Add(new ChannelBinding(Channels[ci].Key, sinks[i].Key, 0, 1f));
                ci++;
                if (ci < Channels.Count)
                {
                    Bindings.Add(new ChannelBinding(Channels[ci].Key, sinks[i].Key, 1, 1f));
                    ci++;
                }
            }
        }

        void AddCh(string key, string name, ChannelKind kind, params SourceTap[] taps)
        {
            var c = new LogicalChannel(key, name, kind);
            c.Taps.AddRange(taps);
            c.Order = Channels.Count;
            Channels.Add(c);
        }

        static SourceTap T(int src, float g) { return new SourceTap(src, g); }

        /// <summary>一键绑定: 清空绑定后按清单顺序重排到给定设备。</summary>
        public void AutoBind(IList<SinkSpec> sinks)
        {
            Bindings.Clear();
            if (sinks == null || sinks.Count == 0) return;
            // 按设备顺序逐个填满它们的物理声道: 3 台 2ch 设备 + 6 声道 → 每台各吃一对
            int ci = 0;
            for (int s = 0; s < sinks.Count && ci < Channels.Count; s++)
            {
                int oc = Math.Max(1, sinks[s].OutChannels);
                for (int p = 0; p < oc && ci < Channels.Count; p++, ci++)
                    Bindings.Add(new ChannelBinding(Channels[ci].Key, sinks[s].Key, p, 1f));
            }
        }

        // ---------------------------------------------------------------- 校验
        /// <summary>启动前校验。返回列表里 Error 会导致禁止启动, Warn 只提示。</summary>
        public List<RoutingIssue> Validate(int srcCh, IList<SinkSpec> sinks)
        {
            var issues = new List<RoutingIssue>();
            if (Channels.Count == 0)
                issues.Add(new RoutingIssue(IssueSev.Error, "", "没有任何声道"));

            // 声道的抽头必须落在捕获源范围内
            for (int i = 0; i < Channels.Count; i++)
            {
                var c = Channels[i];
                for (int t = 0; t < c.Taps.Count; t++)
                {
                    var tap = c.Taps[t];
                    if (tap.Src < 0 || tap.Src >= srcCh)
                        issues.Add(new RoutingIssue(IssueSev.Error, c.Key,
                            c.Name + " 引用了不存在的源声道 " + tap.Src + "（当前捕获源只有 " + srcCh + " 声道）"));
                }
                if (!HasBinding(c.Key))
                    issues.Add(new RoutingIssue(IssueSev.Warn, c.Key, c.Name + " 未分配（该声道将静音）"));
            }

            if (sinks == null || sinks.Count == 0)
            {
                issues.Add(new RoutingIssue(IssueSev.Error, "", "没有启用任何输出设备"));
                return issues;
            }

            int enabled = 0;
            for (int i = 0; i < sinks.Count; i++) if (sinks[i].Enabled) enabled++;
            if (enabled == 0)
            {
                issues.Add(new RoutingIssue(IssueSev.Error, "", "没有启用任何输出设备"));
                return issues;
            }

            // 绑定必须指向存在的设备与合法的物理声道
            for (int i = 0; i < Bindings.Count; i++)
            {
                var b = Bindings[i];
                var sink = FindSink(sinks, b.SinkKey);
                if (sink == null)
                {
                    issues.Add(new RoutingIssue(IssueSev.Warn, b.ChannelKey, "绑定指向的设备不在启用列表里"));
                    continue;
                }
                if (!sink.Enabled)
                {
                    // 停用不是删除: 绑定留在 ini 里, 重新启用即恢复
                    issues.Add(new RoutingIssue(IssueSev.Info, b.ChannelKey, "指向已停用的 " + sink.Label + "（配置保留）"));
                    continue;
                }
                if (b.Phys < 0 || b.Phys >= sink.OutChannels)
                    issues.Add(new RoutingIssue(IssueSev.Error, b.ChannelKey,
                        "物理声道 " + b.Phys + " 超出该路可用声道数 " + sink.OutChannels));
            }

            // 同一物理声道被多个逻辑声道占用 → 求和, 可能削波
            for (int i = 0; i < sinks.Count; i++)
            {
                if (!sinks[i].Enabled) continue;
                for (int p = 0; p < sinks[i].OutChannels; p++)
                {
                    int n = 0;
                    for (int b = 0; b < Bindings.Count; b++)
                        if (Bindings[b].SinkKey == sinks[i].Key && Bindings[b].Phys == p) n++;
                    if (n > 1)
                        issues.Add(new RoutingIssue(IssueSev.Warn, sinks[i].Key,
                            sinks[i].Label + " 的物理声道 " + p + " 被 " + n + " 个声道同时占用（会相加，注意削波）"));
                }
            }

            // 同一逻辑声道绑到多台设备 → 同空间并联, 会有梳状滤波
            for (int i = 0; i < Channels.Count; i++)
            {
                int targets = 0;
                for (int b = 0; b < Bindings.Count; b++)
                {
                    if (Bindings[b].ChannelKey != Channels[i].Key) continue;
                    var s = FindSink(sinks, Bindings[b].SinkKey);
                    if (s != null && s.Enabled) targets++;
                }
                if (targets > 1)
                    issues.Add(new RoutingIssue(IssueSev.Info, Channels[i].Key,
                        Channels[i].Name + " 同时送到 " + targets + " 台设备（若在同一空间，会因到达时间差产生梳状滤波）"));
            }

            return issues;
        }

        /// <summary>
        /// 设备层校验 (与捕获声道无关), 启动前跑。会就地修正 OutChannels 的失配值。
        /// 返回列表里 Error 必须挡住启动。
        /// </summary>
        public List<RoutingIssue> ValidateSinks(string captureId, Func<string, int> endpointChannels)
        {
            var issues = new List<RoutingIssue>();
            var seenId = new Dictionary<string, string>();
            int enabled = 0;

            for (int i = 0; i < SinkSpecs.Count; i++)
            {
                var s = SinkSpecs[i];
                if (!s.Enabled) continue;
                enabled++;

                if (string.IsNullOrEmpty(s.DeviceId))
                {
                    issues.Add(new RoutingIssue(IssueSev.Error, s.Key, s.Label + " 没有选择输出设备"));
                    continue;
                }
                if (!string.IsNullOrEmpty(captureId) && s.DeviceId == captureId)
                {
                    issues.Add(new RoutingIssue(IssueSev.Error, s.Key,
                        s.Label + " 指向捕获设备本身（会造成回环）"));
                    continue;
                }
                string other;
                if (seenId.TryGetValue(s.DeviceId, out other))
                {
                    issues.Add(new RoutingIssue(IssueSev.Error, s.Key,
                        s.Label + " 与 " + other + " 是同一台设备（同一端点开两路会互相抢占，其中一路会静音）"));
                    continue;
                }
                seenId[s.DeviceId] = s.Label;

                if (endpointChannels != null)
                {
                    int actual = endpointChannels(s.DeviceId);
                    // 只能收窄, 不能放宽: OutChannels 是"这一路用几个物理声道", 不是端点总声道数。
                    // 左右分离就是拿 2ch 音箱的 1 个声道 (单声道送进去会被系统铺到两只喇叭),
                    // 若改成 2ch, 未绑定的 p1 会静音 → 听感少一半。
                    if (actual > 0 && s.OutChannels > actual)
                    {
                        issues.Add(new RoutingIssue(IssueSev.Warn, s.Key,
                            s.Label + " 的声道数已从 " + s.OutChannels + " 收窄到端点实际值 " + actual));
                        s.OutChannels = actual;
                    }
                }
            }

            if (enabled == 0)
                issues.Add(new RoutingIssue(IssueSev.Error, "", "没有启用任何输出设备"));

            // 调整声道数后可能把绑定挤出范围, 复检一次
            for (int i = 0; i < Bindings.Count; i++)
            {
                var s = FindSink(SinkSpecs, Bindings[i].SinkKey);
                if (s == null || !s.Enabled) continue;
                if (Bindings[i].Phys >= s.OutChannels)
                    issues.Add(new RoutingIssue(IssueSev.Error, Bindings[i].ChannelKey,
                        s.Label + " 的物理声道 " + Bindings[i].Phys + " 已超出端点实际声道数 " + s.OutChannels));
            }
            return issues;
        }

        bool HasBinding(string channelKey)
        {
            for (int i = 0; i < Bindings.Count; i++)
                if (Bindings[i].ChannelKey == channelKey) return true;
            return false;
        }

        static SinkSpec FindSink(IList<SinkSpec> sinks, string key)
        {
            if (sinks == null) return null;
            for (int i = 0; i < sinks.Count; i++)
                if (sinks[i].Key == key) return sinks[i];
            return null;
        }

        // ---------------------------------------------------------------- 编译
        /// <summary>三层折叠成一层: matrix[sink][phys][src] = Σ bind.Gain × tap.Gain。
        /// 运行时只读这个矩阵, 逻辑声道层不参与逐样本运算。</summary>
        public CompiledRouting Compile(IList<SinkSpec> sinks, int srcCh)
        {
            var r = new CompiledRouting();
            if (sinks == null) sinks = new List<SinkSpec>();
            int n = sinks.Count;
            r.SinkKeys = new string[n];
            r.SinkOutCh = new int[n];
            r.SinkMatrix = new float[n][,];
            for (int s = 0; s < n; s++)
            {
                r.SinkKeys[s] = sinks[s].Key;
                int oc = Math.Max(1, sinks[s].OutChannels);
                r.SinkOutCh[s] = oc;
                var m = new float[oc, Math.Max(1, srcCh)];
                r.SinkMatrix[s] = m;

                for (int b = 0; b < Bindings.Count; b++)
                {
                    var bind = Bindings[b];
                    if (bind.SinkKey != sinks[s].Key) continue;
                    if (bind.Phys < 0 || bind.Phys >= oc) continue;
                    var ch = FindChannel(bind.ChannelKey);
                    if (ch == null || ch.Mute) continue;
                    float g = bind.Gain * ch.Gain;
                    for (int t = 0; t < ch.Taps.Count; t++)
                    {
                        var tap = ch.Taps[t];
                        if (tap.Src < 0) continue;
                        // 越界折叠而非丢弃: 单声道捕获时旧实现就是 `channel % ch` 回落到 ch0,
                        // 这里保持一致 —— 否则单声道源会让右路整条静音 (听感是"少一半")。
                        // 真正的配置错误仍由 Validate 报 Error 挡住启动。
                        int src = srcCh > 0 ? tap.Src % srcCh : 0;
                        m[bind.Phys, src] += g * tap.Gain;
                    }
                }
            }
            // 记录每声道绑了几条 (UI 状态点用)
            RefreshBindingCounts();
            return r;
        }

        void RefreshBindingCounts()
        {
            for (int i = 0; i < Channels.Count; i++) Channels[i].BindingCount = 0;
            for (int b = 0; b < Bindings.Count; b++)
            {
                var c = FindChannel(Bindings[b].ChannelKey);
                if (c != null) c.BindingCount++;
            }
        }

        /// <summary>调试/日志用: 矩阵的非零项写成 "p0←s0*1.00"。</summary>
        public static string Describe(float[,] m)
        {
            var sb = new StringBuilder();
            int oc = m.GetLength(0), sc = m.GetLength(1);
            for (int p = 0; p < oc; p++)
            {
                bool any = false;
                for (int s = 0; s < sc; s++)
                {
                    if (Math.Abs(m[p, s]) < 1e-6f) continue;
                    if (!any) { sb.Append("p").Append(p).Append("←"); any = true; }
                    else sb.Append('+');
                    sb.Append('s').Append(s).Append('*').Append(m[p, s].ToString("0.00"));
                }
                if (any) sb.Append(' ');
            }
            return sb.ToString().TrimEnd();
        }
    }
}
