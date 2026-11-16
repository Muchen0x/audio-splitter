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
        /// <summary>该路音量 (0~2, 1 = 原始电平)。乘进编译矩阵的每一格,
        /// 不动 Windows 端点音量 —— 端点音量是共享的, 多路同设备时彼此会打架。</summary>
        public float Volume = 1f;
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
            s.Volume = Volume;
            s.ClockGroupId = ClockGroupId;
            s.Enabled = Enabled;
            s.CachedName = CachedName;
            return s;
        }

        /// <summary>音量夹到 [0,2]: ini 里手写的 999 或负数不该把矩阵推出量程。</summary>
        public static float ClampVolume(float v)
        {
            if (v < 0f) return 0f;
            if (v > 2f) return 2f;
            return v;
        }
        public float EffectiveVolume { get { return ClampVolume(Volume); } }
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
        /// <summary>
        /// 按模式生成默认清单与绑定。custom 不动。返回是否发生了改动。
        /// <para>srcCh = 捕获源实际声道数。**模板必须知道它**: 真 5.1 源进来时中置/低音/环绕
        /// 有各自的声道, 若还按 2 声道上混, 后置放的是前置内容、中置没有对白 —— 听感"像有声音"
        /// 所以极难发现。0 或 2 表示按立体声上混 (旧行为, 逐系数不变)。</para>
        /// </summary>
        public bool ApplyModeTemplate(WorkMode mode, IList<SinkSpec> sinks, int srcCh = 0)
        {
            if (mode == WorkMode.Custom) { Mode = mode; return false; }
            Mode = mode;
            switch (mode)
            {
                case WorkMode.SplitLr: BuildSplitLr(sinks, srcCh); return true;
                case WorkMode.MirrorStereo: BuildMirror(sinks, srcCh); return true;
                case WorkMode.Surround: BuildSurround(Layout, sinks, srcCh); return true;
                case WorkMode.MonoBroadcast: BuildMono(sinks, srcCh); return true;
            }
            return false;
        }

        /// <summary>
        /// Windows 端点的声道顺序表 (KSAUDIO_SPEAKER_*): 按声道数给出各语义声道的源索引,
        /// -1 = 该源里没有这个声道。1ch 时全部落到 s0。
        /// <para>1:FC 2:FL,FR 3:FL,FR,FC 4:FL,FR,BL,BR 5:FL,FR,FC,BL,BR
        /// 6:FL,FR,FC,LFE,BL,BR 7:FL,FR,FC,LFE,BC,SL,SR 8:FL,FR,FC,LFE,BL,BR,SL,SR</para>
        /// </summary>
        /// <summary>对外可见: 界面层的"卡片 → 声道抽头"翻译 (LaneRouting) 要复用同一张映射表,
        /// 否则模板与界面会各写一套扬声器位顺序, 迟早不一致。</summary>
        public struct SourceMap
        {
            public int FL, FR, C, LFE, SL, SR, BL, BR;

            public bool HasCenter { get { return C >= 0; } }
            /// <summary>源里完全没有环绕 (≤3ch) → 需要上混。</summary>
            public bool SurroundMissing { get { return SL < 0 && BL < 0; } }
            /// <summary>5.1 的后环绕: 优先取 SL, 4ch 源退回 BL。</summary>
            public int SurL { get { return SL >= 0 ? SL : BL; } }
            public int SurR { get { return SR >= 0 ? SR : BR; } }
        }

        public static SourceMap MapOf(int srcCh)
        {
            var m = new SourceMap { C = -1, LFE = -1, SL = -1, SR = -1, BL = -1, BR = -1 };
            m.FL = 0;
            m.FR = srcCh >= 2 ? 1 : 0;
            switch (srcCh)
            {
                case 3: m.C = 2; break;
                case 4: m.BL = 2; m.BR = 3; break;
                case 5: m.C = 2; m.BL = 3; m.BR = 4; break;
                case 6: m.C = 2; m.LFE = 3; m.SL = 4; m.SR = 5; break;
                // 6.1 的 s4 是背中置, 没有对应的输出声道, 忽略 (不硬塞进环绕)
                case 7: m.C = 2; m.LFE = 3; m.SL = 5; m.SR = 6; break;
                default:
                    if (srcCh >= 8) { m.C = 2; m.LFE = 3; m.BL = 4; m.BR = 5; m.SL = 6; m.SR = 7; }
                    break;
            }
            return m;
        }

        /// <summary>日志/UI 用: 这个源有几个声道、算不算"真多声道源"。</summary>
        public static string DescribeSource(int srcCh)
        {
            if (srcCh <= 0) return "声道数未知";
            if (srcCh <= 2) return srcCh + " 声道（立体声上混）";
            var m = MapOf(srcCh);
            var sb = new StringBuilder();
            sb.Append(srcCh).Append(" 声道直通:");
            sb.Append(" L=s").Append(m.FL);
            sb.Append(" R=s").Append(m.FR);
            if (m.HasCenter) sb.Append(" C=s").Append(m.C);
            if (m.LFE >= 0) sb.Append(" LFE=s").Append(m.LFE);
            if (m.SurL >= 0) sb.Append(" 环绕=s").Append(m.SurL).Append('/').Append(m.SurR);
            if (m.BL >= 0 && m.SL >= 0) sb.Append(" 后置=s").Append(m.BL).Append('/').Append(m.BR);
            return sb.ToString();
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

        void BuildSplitLr(IList<SinkSpec> sinks, int srcCh)
        {
            Channels.Clear(); Bindings.Clear();
            Layout = ChannelLayout.Stereo;
            var l = new LogicalChannel("ch0", "左", ChannelKind.FL, new SourceTap(0, 1f));
            var r = new LogicalChannel("ch1", "右", ChannelKind.FR, new SourceTap(1, 1f));
            // 真多声道源: 中置折进左右。不折就会丢掉全部对白人声 (5.1 电影里最明显),
            // 且这一步不花任何运行时开销 —— 只是多一项矩阵系数。
            // 环绕与 LFE 不折: 本模式是"两路各分一只音箱", 再塞进来必然削波。
            var map = MapOf(srcCh);
            if (map.HasCenter && map.C != 0 && map.C != 1)
            {
                l.Taps.Add(new SourceTap(map.C, 0.707f));
                r.Taps.Add(new SourceTap(map.C, 0.707f));
            }
            Channels.Add(l); Channels.Add(r);
            if (sinks == null) return;
            for (int i = 0; i < sinks.Count && i < 2; i++)
            {
                sinks[i].OutChannels = 1;
                Bindings.Add(new ChannelBinding(Channels[i].Key, sinks[i].Key, 0, 1f));
            }
        }

        void BuildMirror(IList<SinkSpec> sinks, int srcCh)
        {
            Channels.Clear(); Bindings.Clear();
            Layout = ChannelLayout.Stereo;
            var l = new LogicalChannel("ch0", "左", ChannelKind.FL, new SourceTap(0, 1f));
            var r = new LogicalChannel("ch1", "右", ChannelKind.FR, new SourceTap(1, 1f));
            var map = MapOf(srcCh);
            if (map.HasCenter && map.C != 0 && map.C != 1)
            {
                l.Taps.Add(new SourceTap(map.C, 0.707f));
                r.Taps.Add(new SourceTap(map.C, 0.707f));
            }
            Channels.Add(l); Channels.Add(r);
            if (sinks == null) return;
            for (int i = 0; i < sinks.Count; i++)
            {
                sinks[i].OutChannels = 2;
                Bindings.Add(new ChannelBinding("ch0", sinks[i].Key, 0, 1f));
                Bindings.Add(new ChannelBinding("ch1", sinks[i].Key, 1, 1f));
            }
        }

        void BuildMono(IList<SinkSpec> sinks, int srcCh)
        {
            Channels.Clear(); Bindings.Clear();
            Layout = ChannelLayout.Mono;
            var c = new LogicalChannel("ch0", "单声道", ChannelKind.C,
                new SourceTap(0, 0.5f), new SourceTap(1, 0.5f));
            var map = MapOf(srcCh);
            if (map.HasCenter && map.C != 0 && map.C != 1)
                c.Taps.Add(new SourceTap(map.C, 0.35f));
            Channels.Add(c);
            if (sinks == null) return;
            for (int i = 0; i < sinks.Count; i++)
            {
                int n = sinks[i].OutChannels < 2 ? 1 : 2;
                sinks[i].OutChannels = n;
                for (int p = 0; p < n; p++)
                    Bindings.Add(new ChannelBinding("ch0", sinks[i].Key, p, 1f));
            }
        }

        void BuildSurround(ChannelLayout layout, IList<SinkSpec> sinks, int srcCh)
        {
            Channels.Clear(); Bindings.Clear();
            if (layout != ChannelLayout.Surround51 && layout != ChannelLayout.Surround71 && layout != ChannelLayout.Quad)
                layout = ChannelLayout.Surround51;
            Layout = layout;

            var map = MapOf(srcCh);
            switch (layout)
            {
                case ChannelLayout.Quad:
                    if (map.BL >= 0)
                    {
                        // 4ch 及以上的四声道直通 (4ch 源本身就是 FL,FR,BL,BR)
                        AddCh("FL", "左前置", ChannelKind.FL, T(map.FL, 1f));
                        AddCh("FR", "右前置", ChannelKind.FR, T(map.FR, 1f));
                        AddCh("BL", "左后置", ChannelKind.BL, T(map.BL, 1f));
                        AddCh("BR", "右后置", ChannelKind.BR, T(map.BR, 1f));
                    }
                    else
                    {
                        AddCh("FL", "左前置", ChannelKind.FL, T(0, 1f));
                        AddCh("FR", "右前置", ChannelKind.FR, T(1, 1f));
                        AddCh("BL", "左后置", ChannelKind.BL, T(0, 0.7f));
                        AddCh("BR", "右后置", ChannelKind.BR, T(1, 0.7f));
                    }
                    break;

                case ChannelLayout.Surround71:
                    if (map.SL >= 0 && map.BL >= 0)
                    {
                        // 真 7.1 源: 侧环与后置各就各位
                        AddCh("FL", "左前置", ChannelKind.FL, T(map.FL, 1f));
                        AddCh("FR", "右前置", ChannelKind.FR, T(map.FR, 1f));
                        AddCh("C", "中置", ChannelKind.C, T(map.C, 1f));
                        AddCh("LFE", "低音", ChannelKind.LFE, T(map.LFE, 1f));
                        AddCh("SL", "左环绕", ChannelKind.SL, T(map.SL, 1f));
                        AddCh("SR", "右环绕", ChannelKind.SR, T(map.SR, 1f));
                        AddCh("BL", "左后置", ChannelKind.BL, T(map.BL, 1f));
                        AddCh("BR", "右后置", ChannelKind.BR, T(map.BR, 1f));
                    }
                    else if (map.SL >= 0)
                    {
                        // 5.1 源扩成 7.1: 后置由环绕按 -3dB 派生 (不新增信息, 只是把声场铺开)
                        AddCh("FL", "左前置", ChannelKind.FL, T(map.FL, 1f));
                        AddCh("FR", "右前置", ChannelKind.FR, T(map.FR, 1f));
                        AddCh("C", "中置", ChannelKind.C, T(map.C, 1f));
                        AddCh("LFE", "低音", ChannelKind.LFE, T(map.LFE, 1f));
                        AddCh("SL", "左环绕", ChannelKind.SL, T(map.SL, 1f));
                        AddCh("SR", "右环绕", ChannelKind.SR, T(map.SR, 1f));
                        AddCh("BL", "左后置", ChannelKind.BL, T(map.SL, 0.707f));
                        AddCh("BR", "右后置", ChannelKind.BR, T(map.SR, 0.707f));
                    }
                    else
                    {
                        AddCh("FL", "左前置", ChannelKind.FL, T(0, 1f));
                        AddCh("FR", "右前置", ChannelKind.FR, T(1, 1f));
                        AddCh("C", "中置", ChannelKind.C, new SourceTap(0, 0.5f), new SourceTap(1, 0.5f));
                        AddCh("LFE", "低音", ChannelKind.LFE, new SourceTap(0, 0.35f), new SourceTap(1, 0.35f));
                        AddCh("SL", "左环绕", ChannelKind.SL, T(0, 0.7f));
                        AddCh("SR", "右环绕", ChannelKind.SR, T(1, 0.7f));
                        AddCh("BL", "左后置", ChannelKind.BL, T(0, 0.7f));
                        AddCh("BR", "右后置", ChannelKind.BR, T(1, 0.7f));
                    }
                    break;

                default:   // 5.1
                    if (map.SL >= 0)
                    {
                        // 真 5.1 (6ch 源的 s4/s5 是后环绕): 直通, 不重算任何系数
                        AddCh("FL", "左前置", ChannelKind.FL, T(map.FL, 1f));
                        AddCh("FR", "右前置", ChannelKind.FR, T(map.FR, 1f));
                        AddCh("C", "中置", ChannelKind.C, T(map.C, 1f));
                        AddCh("LFE", "低音", ChannelKind.LFE, T(map.LFE, 1f));
                        AddCh("SL", "左环绕", ChannelKind.SL, T(map.SurL, 1f));
                        AddCh("SR", "右环绕", ChannelKind.SR, T(map.SurR, 1f));
                    }
                    else if (map.BL >= 0 || map.HasCenter)
                    {
                        // 3/4/5ch 源: 有直通的直通, 缺的由前置派生 (4ch 的 s2/s3 就是后置, 直接当环绕用)
                        AddCh("FL", "左前置", ChannelKind.FL, T(map.FL, 1f));
                        AddCh("FR", "右前置", ChannelKind.FR, T(map.FR, 1f));
                        if (map.HasCenter)
                            AddCh("C", "中置", ChannelKind.C, T(map.C, 1f));
                        else
                            AddCh("C", "中置", ChannelKind.C, new SourceTap(map.FL, 0.5f), new SourceTap(map.FR, 0.5f));
                        AddCh("LFE", "低音", ChannelKind.LFE, new SourceTap(map.FL, 0.35f), new SourceTap(map.FR, 0.35f));
                        AddCh("SL", "左环绕", ChannelKind.SL, map.BL >= 0 ? T(map.BL, 1f) : T(map.FL, 0.7f));
                        AddCh("SR", "右环绕", ChannelKind.SR, map.BR >= 0 ? T(map.BR, 1f) : T(map.FR, 0.7f));
                    }
                    else
                    {
                        AddCh("FL", "左前置", ChannelKind.FL, T(0, 1f));
                        AddCh("FR", "右前置", ChannelKind.FR, T(1, 1f));
                        AddCh("C", "中置", ChannelKind.C, new SourceTap(0, 0.5f), new SourceTap(1, 0.5f));
                        AddCh("LFE", "低音", ChannelKind.LFE, new SourceTap(0, 0.35f), new SourceTap(1, 0.35f));
                        AddCh("SL", "左环绕", ChannelKind.SL, T(0, 0.7f));
                        AddCh("SR", "右环绕", ChannelKind.SR, T(1, 0.7f));
                    }
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
                    // srcCh <= 0 表示"还不知道源有几个声道"(启动前拿不到端点格式), 此时不判,
                    // 否则会把合法的 6ch 配置全部误报成错误。
                    if (srcCh > 0 && (tap.Src < 0 || tap.Src >= srcCh))
                        issues.Add(new RoutingIssue(IssueSev.Error, c.Key,
                            c.Name + " 引用了不存在的源声道 " + tap.Src + "（当前捕获源只有 " + srcCh + " 声道，" +
                            "点一次当前模式按钮可按新源重建清单）"));
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

            // 峰值增益: 同一物理声道的系数绝对值之和 > 1 时, 大音量峰值会被硬限幅。
            // SinkRuntime 里有 clamp 兜底 (不破音), 但它救不了"已经削平的波形", 所以要提前讲。
            for (int i = 0; i < sinks.Count; i++)
            {
                var s = sinks[i];
                if (!s.Enabled) continue;
                float vol = s.EffectiveVolume;
                if (vol <= 0.001f)
                {
                    issues.Add(new RoutingIssue(IssueSev.Warn, s.Key,
                        s.Label + " 的音量为 0，这一路会完全静音（可在设备行右侧把音量拉回来）"));
                    continue;
                }
                for (int p = 0; p < s.OutChannels; p++)
                {
                    float sum = 0f;
                    for (int b = 0; b < Bindings.Count; b++)
                    {
                        var bind = Bindings[b];
                        if (bind.SinkKey != s.Key || bind.Phys != p) continue;
                        var ch = FindChannel(bind.ChannelKey);
                        if (ch == null || ch.Mute) continue;
                        float g = bind.Gain * ch.Gain * vol;
                        for (int t = 0; t < ch.Taps.Count; t++) sum += Math.Abs(g * ch.Taps[t].Gain);
                    }
                    if (sum > 1.01f)
                        issues.Add(new RoutingIssue(IssueSev.Info, s.Key,
                            s.Label + " 的物理声道 " + (p + 1) + " 峰值增益 " + sum.ToString("0.00") +
                            "，大音量时会限幅（把本路音量拉到 " + (int)(s.EffectiveVolume / sum * 100) +
                            "% 以内，或在声道浮层把系数降到 " + (1f / sum).ToString("0.00") + " 以内）"));
                }
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
                float vol = sinks[s].EffectiveVolume;

                for (int b = 0; b < Bindings.Count; b++)
                {
                    var bind = Bindings[b];
                    if (bind.SinkKey != sinks[s].Key) continue;
                    if (bind.Phys < 0 || bind.Phys >= oc) continue;
                    var ch = FindChannel(bind.ChannelKey);
                    if (ch == null || ch.Mute) continue;
                    float g = bind.Gain * ch.Gain * vol;
                    for (int t = 0; t < ch.Taps.Count; t++)
                    {
                        var tap = ch.Taps[t];
                        if (tap.Src < 0) continue;
                        // 单声道捕获: 旧实现就是 `channel % 1 = 0`, 右路靠它才不会整条静音,
                        // 所以 srcCh==1 时保留折叠。
                        // srcCh>1 时越界一律丢弃, **不能取模**: 6ch 配置撞上 2ch 源时,
                        // s2%2=s0、s3%2=s1 会让中置/低音/环绕全部变成满音量的 L/R ——
                        // 三台设备都在响、听着"没问题", 实际全是错的。宁可静音, 由 Validate 报错说清。
                        int src;
                        if (srcCh <= 0) src = tap.Src;
                        else if (tap.Src < srcCh) src = tap.Src;
                        else if (srcCh == 1) src = 0;
                        else continue;
                        if (src >= m.GetLength(1)) continue;
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
