using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using StereoSplitter.Infrastructure;

namespace StereoSplitter
{
    /// <summary>
    /// 路由内核自检 (命令行: SplitterGUI.exe --selftest)。
    ///
    /// 为什么需要它: 多路路由的正确性全部落在"卡片 → 抽头 → 矩阵"这一次翻译上, 而它的错误
    /// 形态极其安静 —— 三台设备都在响、听起来"没问题", 实际中置放的是前置内容。开音频设备
    /// 才能发现的那种错, 用矩阵断言几行就能钉死。
    ///
    /// 只跑纯计算 (RoutingModel / LaneRouting / ini 读写), 不打开任何端点, 因此可以在
    /// 没有声卡的机器、CI、以及正在用音响听歌的时候跑。
    /// </summary>
    static class RoutingSelfTest
    {
        static readonly StringBuilder sb = new StringBuilder();
        static int failed;

        public static int Run(string logDir)
        {
            sb.Length = 0;
            failed = 0;

            CaseTwoLanesTwoDevices();
            CaseTwoLanesOneDevice();
            CaseThreeDevicesShareClock();
            CaseSurroundThreeDevices();
            CaseSurroundOneDevice();
            CaseTwoPointOne();
            CaseMono();
            CaseStereoFromSurroundSource();
            CaseVolumeAndMute();
            CaseMergeSameDevice();
            CaseMergeVolumeOwnership();
            CaseFlattenVolumeRules();
            CaseSparsePhysSlots();
            CaseDelayConflict();
            CaseValidateWarnings();
            CaseIniRoundTrip();

            sb.AppendLine();
            sb.AppendLine(failed == 0 ? "结果: 全部通过 (" + CaseCount + " 项)" : "结果: " + failed + " 项失败 / " + CaseCount + " 项");
            string text = sb.ToString();
            try
            {
                if (string.IsNullOrEmpty(logDir)) logDir = AppDomain.CurrentDomain.BaseDirectory;
                Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "selftest-routing.txt"), text, Encoding.UTF8);
            }
            catch { }
            try { Console.Write(text); } catch { }
            return failed == 0 ? 0 : 1;
        }

        static int CaseCount;

        // ---------------------------------------------------------------- 用例

        /// <summary>经典两路: 左右各一台设备, 抽头必须是纯 L / 纯 R。</summary>
        static void CaseTwoLanesTwoDevices()
        {
            Begin("两路 / 两台设备 (split-lr 原行为)");
            var lanes = new List<OutputLane>
            {
                Lane("FL", "devA", 0, 100),
                Lane("FR", "devB", 30, 100)
            };
            bool conflict;
            var m = LaneRouting.Build(lanes, 1, 0, null, out conflict);

            Check("2 路", m.SinkSpecs.Count == 2);
            Check("每路 1ch", m.SinkSpecs[0].OutChannels == 1 && m.SinkSpecs[1].OutChannels == 1);
            Check("延迟 0/30", m.SinkSpecs[0].DelayMs == 0 && m.SinkSpecs[1].DelayMs == 30);
            Check("共享时钟组", m.SinkSpecs[0].ClockGroupId == "g0" && m.SinkSpecs[1].ClockGroupId == "g0");
            Check("无延迟冲突", !conflict);

            var c = m.Compile(m.SinkSpecs, 2);
            Check("左 = 纯 s0", Matrix(c.SinkMatrix[0], 1f, 0f));
            Check("右 = 纯 s1", Matrix(c.SinkMatrix[1], 0f, 1f));
        }

        /// <summary>两台卡片接到同一台设备: 必须合成**一路 2ch sink** (同端点开两条会互抢)。</summary>
        static void CaseTwoLanesOneDevice()
        {
            Begin("两路 / 同一台设备 (立体声直出)");
            var lanes = new List<OutputLane>
            {
                Lane("FL", "devA", 0, 100),
                Lane("FR", "devA", 0, 100)
            };
            bool conflict;
            var m = LaneRouting.Build(lanes, 1, 0, null, out conflict);

            Check("合成 1 路", m.SinkSpecs.Count == 1);
            Check("2ch", m.SinkSpecs[0].OutChannels == 2);
            var c = m.Compile(m.SinkSpecs, 2);
            Check("p0 = s0", c.SinkMatrix[0][0, 0] == 1f && c.SinkMatrix[0][0, 1] == 0f);
            Check("p1 = s1", c.SinkMatrix[0][1, 0] == 0f && c.SinkMatrix[0][1, 1] == 1f);
        }

        /// <summary>5.1 / 三台 2ch 设备 / 真 6ch 源: 每台吃一对, 中置与低音各就各位。</summary>
        static void CaseSurroundThreeDevices()
        {
            Begin("5.1 / 三台设备 / 6ch 源");
            var lanes = new List<OutputLane>
            {
                Lane("FL", "devA", 0, 100),
                Lane("FR", "devA", 0, 100),
                Lane("C", "devB", 0, 100),
                Lane("LFE", "devB", 0, 100),
                Lane("SL", "devC", 0, 100),
                Lane("SR", "devC", 0, 100)
            };
            bool conflict;
            var m = LaneRouting.Build(lanes, 4, 6, null, out conflict);

            Check("3 路", m.SinkSpecs.Count == 3);
            Check("每路 2ch", m.SinkSpecs[0].OutChannels == 2 && m.SinkSpecs[2].OutChannels == 2);
            Check("多声道规格独立时钟组", m.SinkSpecs[1].ClockGroupId == "" && m.SinkSpecs[2].ClockGroupId == "");

            var c = m.Compile(m.SinkSpecs, 6);
            Check("devA p0←s0 / p1←s1",
                One(c.SinkMatrix[0], 0, 0, 1f) && One(c.SinkMatrix[0], 1, 1, 1f));
            Check("devB p0←s2 (中置) / p1←s3 (低音)",
                One(c.SinkMatrix[1], 0, 2, 1f) && One(c.SinkMatrix[1], 1, 3, 1f));
            Check("devC p0←s4 / p1←s5",
                One(c.SinkMatrix[2], 0, 4, 1f) && One(c.SinkMatrix[2], 1, 5, 1f));
        }

        /// <summary>5.1 / 一块 6ch 声卡: 矩阵必须是单位阵 (直通)。</summary>
        static void CaseSurroundOneDevice()
        {
            Begin("5.1 / 单块 6ch 声卡");
            var lanes = new List<OutputLane>();
            string[] keys = { "FL", "FR", "C", "LFE", "SL", "SR" };
            for (int i = 0; i < keys.Length; i++) lanes.Add(Lane(keys[i], "devA", 0, 100));
            bool conflict;
            var m = LaneRouting.Build(lanes, 4, 6, null, out conflict);

            Check("1 路 6ch", m.SinkSpecs.Count == 1 && m.SinkSpecs[0].OutChannels == 6);
            var c = m.Compile(m.SinkSpecs, 6);
            bool identity = true;
            for (int p = 0; p < 6; p++)
                for (int s = 0; s < 6; s++)
                    if (Math.Abs(c.SinkMatrix[0][p, s] - (p == s ? 1f : 0f)) > 1e-6f) identity = false;
            Check("单位阵", identity);
        }

        /// <summary>2.1 / 两台设备 / 立体声源: 低音由前置各 -9dB 派生。</summary>
        static void CaseTwoPointOne()
        {
            Begin("2.1 / 立体声源");
            var lanes = new List<OutputLane>
            {
                Lane("FL", "devA", 0, 100),
                Lane("FR", "devA", 0, 100),
                Lane("LFE", "devB", 0, 100)
            };
            bool conflict;
            var m = LaneRouting.Build(lanes, 2, 2, null, out conflict);

            Check("2 路", m.SinkSpecs.Count == 2);
            Check("devB 1ch", m.SinkSpecs[1].OutChannels == 1);
            var c = m.Compile(m.SinkSpecs, 2);
            Check("低音 = 0.35 L + 0.35 R",
                Math.Abs(c.SinkMatrix[1][0, 0] - 0.35f) < 1e-6f && Math.Abs(c.SinkMatrix[1][0, 1] - 0.35f) < 1e-6f);
        }

        static void CaseMono()
        {
            Begin("单声道 / 单路");
            var lanes = new List<OutputLane> { Lane("M", "devA", 0, 100) };
            bool conflict;
            var m = LaneRouting.Build(lanes, 0, 2, null, out conflict);
            var c = m.Compile(m.SinkSpecs, 2);
            Check("M = 0.5 L + 0.5 R",
                Math.Abs(c.SinkMatrix[0][0, 0] - 0.5f) < 1e-6f && Math.Abs(c.SinkMatrix[0][0, 1] - 0.5f) < 1e-6f);
        }

        /// <summary>关键回归: 立体声源 + 5.1 规格 → 上混系数 (源里没有中置, 不能取模到 L/R)。</summary>
        static void CaseStereoFromSurroundSource()
        {
            Begin("5.1 规格 / 立体声源 (上混)");
            var lanes = new List<OutputLane>
            {
                Lane("FL", "devA", 0, 100), Lane("FR", "devA", 0, 100),
                Lane("C", "devB", 0, 100), Lane("LFE", "devB", 0, 100)
            };
            bool conflict;
            var m = LaneRouting.Build(lanes, 4, 2, null, out conflict);
            var c = m.Compile(m.SinkSpecs, 2);
            Check("中置 = 0.5 L + 0.5 R",
                Math.Abs(c.SinkMatrix[1][0, 0] - 0.5f) < 1e-6f && Math.Abs(c.SinkMatrix[1][0, 1] - 0.5f) < 1e-6f);
            Check("低音 = 0.35 L + 0.35 R",
                Math.Abs(c.SinkMatrix[1][1, 0] - 0.35f) < 1e-6f);
        }

        /// <summary>音量: 进矩阵的增益直接来自 lane.Volume / Mute (端点承载的卡片由 Flatten 写 1.0)。</summary>
        static void CaseVolumeAndMute()
        {
            Begin("音量/静音进矩阵");
            var lanes = new List<OutputLane>
            {
                Lane("FL", "devA", 0, 50),
                Lane("C", "devB", 0, 60),
                Lane("LFE", "devB", 0, 0)
            };
            lanes[2].Mute = true;
            bool conflict;
            var m = LaneRouting.Build(lanes, 4, 6, null, out conflict);
            var c = m.Compile(m.SinkSpecs, 6);
            Check("FL 增益 0.5", Math.Abs(c.SinkMatrix[0][0, 0] - 0.5f) < 1e-6f);
            Check("中置 增益 0.6", Math.Abs(c.SinkMatrix[1][0, 2] - 0.6f) < 1e-6f);
            Check("静音 低音 = 0", Math.Abs(c.SinkMatrix[1][1, 3]) < 1e-6f);
        }

        /// <summary>
        /// 同设备的两张卡片 → 合并成一张多声道卡片。
        /// 合并必须无损: 各行声道/音量照旧、延迟取最小、送进内核的形状与合并前一致。
        /// </summary>
        static void CaseMergeSameDevice()
        {
            Begin("同设备卡片合并");
            var cards = new List<OutputCardModel>
            {
                Card("devA", 30, false, "FL", 40),
                Card("devB", 0, false, "C", 70),
                Card("devA", 10, false, "FR", 60)     // 与第 1 张同设备
            };
            int merged = LaneRouting.MergeSameDevice(cards);
            Check("合并 1 张", merged == 1 && cards.Count == 2);
            Check("devA 变多声道 2 行", cards[0].Multi && cards[0].Slots.Count == 2);
            Check("行序 FL/FR", cards[0].Slots[0].ChannelKey == "FL" && cards[0].Slots[1].ChannelKey == "FR");
            // 单声道来源的音量是"端点音量镜像", 不是行增益 → 合并后端点由主音量唯一承载, 行增益回 100
            // (照抄 40/60 就会变成 端点×增益 双份衰减)
            Check("单声道来源行增益复位 100", cards[0].Slots[0].Volume == 100 && cards[0].Slots[1].Volume == 100);
            Check("延迟取最小 10ms", cards[0].DelayMs == 10);
            Check("另一台不受影响", !cards[1].Multi && cards[1].DeviceId == "devB");

            bool conflict;
            var m = LaneRouting.Build(LaneRouting.Flatten(cards, null), 4, 6, null, out conflict);
            Check("2 路 sink", m.SinkSpecs.Count == 2);
            Check("devA 2ch", m.SinkSpecs[0].OutChannels == 2);
        }

        /// <summary>
        /// 合并时的音量归属: 多声道来源的行增益是"真增益", 合并后必须原样保留;
        /// 单声道来源的行音量是端点镜像, 必须复位 100。混着来才不会双衰减也不会丢调节。
        /// </summary>
        static void CaseMergeVolumeOwnership()
        {
            Begin("合并音量归属");
            var host = Card("devA", 0, true, "FL", 100);
            host.Slots[0].Volume = 80;                       // p0: 真增益 80%
            host.Slots.Add(new ChannelSlot { ChannelKey = "FR", Volume = 55 });
            var single = Card("devA", 0, false, "BL", 12);   // 端点镜像 12%
            var cards = new List<OutputCardModel> { host, single };

            int merged = LaneRouting.MergeSameDevice(cards);
            Check("合并 1 张", merged == 1 && cards.Count == 1);
            Check("3 行", cards[0].Slots.Count == 3);
            Check("多声道来源增益保留 80/55",
                cards[0].Slots[0].Volume == 80 && cards[0].Slots[1].Volume == 55);
            Check("单声道来源增益复位 100", cards[0].Slots[2].Volume == 100);
            Check("行序 FL/FR/BL", cards[0].Slots[2].ChannelKey == "BL");
        }

        /// <summary>
        /// 音量归属: 端点可用 → 卡片主音量不进矩阵 (写 1.0, 否则双份衰减);
        /// 多声道各行音量进矩阵; 端点不可用 → 主音量乘进矩阵。规则错了会静默双衰减或音量完全失效。
        /// </summary>
        static void CaseFlattenVolumeRules()
        {
            Begin("Flatten 音量归属");

            var single = new List<OutputCardModel> { Card("devA", 0, false, "FL", 40) };
            Check("端点可用 → 矩阵 1.0", LaneRouting.Flatten(single, null)[0].Volume == 100);

            var noEp = new List<OutputCardModel> { Card("devA", 0, false, "FL", 40) };
            Check("端点不可用 → 矩阵 40", LaneRouting.Flatten(noEp, delegate(string d) { return false; })[0].Volume == 40);

            var multi = new List<OutputCardModel> { Card("devA", 0, true, "FL", 80) };
            multi[0].Slots.Add(new ChannelSlot { ChannelKey = "FR", Volume = 55 });
            var lanes = LaneRouting.Flatten(multi, null);
            Check("多声道 → 2 行", lanes.Count == 2);
            Check("行音量 80/55", lanes[0].Volume == 80 && lanes[1].Volume == 55);
            Check("物理声道 0/1", lanes[0].PhysIndex == 0 && lanes[1].PhysIndex == 1);

            multi[0].Volume = 50;   // 主音量 50% + 端点不可用 → 行音量各乘 50%
            var both = LaneRouting.Flatten(multi, delegate(string d) { return false; });
            Check("40/28 (行×主)", both[0].Volume == 40 && both[1].Volume == 28);
        }

        /// <summary>多声道卡片里的空行 → 该物理声道必须静音, 但声道数不能被压缩。</summary>
        static void CaseSparsePhysSlots()
        {
            Begin("空行 / 稀疏物理声道");
            var cards = new List<OutputCardModel> { Card("devA", 0, true, "FL", 100) };
            cards[0].Slots.Add(new ChannelSlot { ChannelKey = "", Volume = 100 });      // p1 空
            cards[0].Slots.Add(new ChannelSlot { ChannelKey = "LFE", Volume = 100 });   // p2
            var lanes = LaneRouting.Flatten(cards, null);
            Check("2 行有内容", lanes.Count == 2);

            bool conflict;
            var m = LaneRouting.Build(lanes, 4, 6, null, out conflict);
            Check("仍是 3ch (p1 留空不被压缩)", m.SinkSpecs[0].OutChannels == 3);
            var c = m.Compile(m.SinkSpecs, 6);
            Check("p1 全零", Math.Abs(c.SinkMatrix[0][1, 0]) < 1e-6f && Math.Abs(c.SinkMatrix[0][1, 3]) < 1e-6f);
            Check("p2 ← 低音", Math.Abs(c.SinkMatrix[0][2, 3] - 1f) < 1e-6f);
        }

        /// <summary>立体声规格接三台设备 (如双单声道并播): 全部共享一个 ratio 才不会偏音。</summary>
        static void CaseThreeDevicesShareClock()
        {
            Begin("立体声规格 / 三台设备 → 共享时钟");
            var lanes = new List<OutputLane>
            {
                Lane("FL", "devA", 0, 100),
                Lane("FR", "devA", 0, 100),
                Lane("FL", "devB", 20, 100),
                Lane("FR", "devB", 20, 100),
                Lane("FL", "devC", 40, 100)
            };
            bool conflict;
            var m = LaneRouting.Build(lanes, 1, 0, null, out conflict);
            Check("3 路", m.SinkSpecs.Count == 3);
            Check("全部 g0",
                m.SinkSpecs[0].ClockGroupId == "g0" && m.SinkSpecs[1].ClockGroupId == "g0" &&
                m.SinkSpecs[2].ClockGroupId == "g0");
        }

        /// <summary>同一设备上两张卡片延迟不同: 取最小并报冲突 (内核每路只有一条延迟线)。</summary>
        static void CaseDelayConflict()
        {
            Begin("同设备延迟冲突");
            var lanes = new List<OutputLane>
            {
                Lane("FL", "devA", 0, 100),
                Lane("FR", "devA", 40, 100)
            };
            bool conflict;
            var m = LaneRouting.Build(lanes, 1, 2, null, out conflict);
            Check("报冲突", conflict);
            Check("取最小 0ms", m.SinkSpecs[0].DelayMs == 0);
        }

        /// <summary>5.1 规格只接两路: 其余声道必须是 Warn (静音) 而不是 Error。</summary>
        static void CaseValidateWarnings()
        {
            Begin("校验: 未分配声道只警告");
            var lanes = new List<OutputLane>
            {
                Lane("FL", "devA", 0, 100),
                Lane("FR", "devB", 0, 100)
            };
            bool conflict;
            var m = LaneRouting.Build(lanes, 4, 6, null, out conflict);
            var issues = m.Validate(6, m.EnabledSinks());
            bool anyError = false, anyWarn = false;
            for (int i = 0; i < issues.Count; i++)
            {
                if (issues[i].Severity == IssueSev.Error) anyError = true;
                if (issues[i].Severity == IssueSev.Warn) anyWarn = true;
            }
            Check("无 Error", !anyError);
            Check("有 Warn", anyWarn);
        }

        /// <summary>ini 往返: 三台设备 / 6 声道 / 延迟 / 绑定增益都要活下来。</summary>
        static void CaseIniRoundTrip()
        {
            Begin("ini 往返");
            var lanes = new List<OutputLane>
            {
                Lane("FL", "devA", 0, 100), Lane("FR", "devA", 12, 100),
                Lane("C", "devB", 25, 60), Lane("LFE", "devB", 25, 100),
                Lane("SL", "devC", 40, 80), Lane("SR", "devC", 40, 100)
            };
            bool conflict;
            var m = LaneRouting.Build(lanes, 4, 6, null, out conflict);

            string dir = Path.Combine(Path.GetTempPath(), "ss-selftest-" + Guid.NewGuid().ToString("N"));
            string file = Path.Combine(dir, "test.ini");
            RoutingModel back = null;
            try
            {
                Directory.CreateDirectory(dir);
                var store = new SettingsStore(file);
                m.SaveToIni(store);
                string err;
                Check("写盘", store.WriteAll(out err));

                var store2 = new SettingsStore(file);
                store2.Load();
                bool migrated;
                back = RoutingModel.LoadFromIni(store2, out migrated);
                Check("不触发迁移", !migrated);
            }
            finally { try { Directory.Delete(dir, true); } catch { } }

            if (back == null) { Check("回读", false); return; }
            Check("3 路", back.SinkSpecs.Count == 3);
            Check("6 声道", back.Channels.Count == 6);
            Check("6 绑定", back.Bindings.Count == 6);
            Check("延迟保留", back.SinkSpecs[2].DelayMs == 40);
            Check("中置增益保留 (C 卡 60% → 已进绑定增益)",
                Math.Abs(back.Bindings[2].Gain - 0.6f) < 1e-3f);
        }

        // ---------------------------------------------------------------- 工具

        static OutputLane Lane(string key, string dev, int delay, int vol)
        {
            return new OutputLane { ChannelKey = key, DeviceId = dev, DelayMs = delay, Volume = vol };
        }

        /// <summary>一张卡片模型。单声道卡片的主音量就是那一路音量 (界面里两者始终相等)。</summary>
        static OutputCardModel Card(string dev, int delay, bool multi, string ch, int vol)
        {
            var m = new OutputCardModel { DeviceId = dev, DelayMs = delay, Multi = multi, Volume = multi ? 100 : vol };
            m.Slots.Add(new ChannelSlot { ChannelKey = ch, Volume = vol });
            return m;
        }

        static void Begin(string name)
        {
            CaseCount++;
            sb.AppendLine("== " + name);
        }

        static void Check(string what, bool ok)
        {
            if (!ok) failed++;
            sb.AppendLine((ok ? "   ok   " : "   FAIL ") + what);
        }

        /// <summary>矩阵某一行是否等于给定的系数序列。</summary>
        static bool Matrix(float[,] m, params float[] row)
        {
            if (m == null) return false;
            for (int s = 0; s < row.Length && s < m.GetLength(1); s++)
                if (Math.Abs(m[0, s] - row[s]) > 1e-6f) return false;
            return true;
        }

        /// <summary>m[phys, src] 是否为 v (其余项忽略, 由调用方组合判断)。</summary>
        static bool One(float[,] m, int phys, int src, float v)
        {
            return m != null && Math.Abs(m[phys, src] - v) < 1e-6f;
        }
    }
}
