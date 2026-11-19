using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
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
            CaseDriftProviderOverfill();
            CasePcmRingBufferShortRead();
            CaseClockGroupAlignment();
            CaseLifecycleGate();
            CaseLogClassify();
            CaseSessionLogClear();

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

        /// <summary>
        /// 缓冲堆积削峰 + 小环计数保护: 模拟"设备瞬间卡住 200ms, 数据在缓冲里堆成 300ms"。
        ///
        /// 这是"总是突然没声"的主路径回归: 以前这种堆积只能靠 ±1% 的校正慢慢排 (10ms/秒),
        /// 排不完就被看门狗判定链路中断 → 重建整条链路、两路静音约 1 秒。现在要求 provider
        /// 自己把水位压回目标附近, 且全程不抛异常 (count 扣成负数会让 Read 越界)。
        /// 纯内存运算, 不开任何音频端点。
        /// </summary>
        static void CaseDriftProviderOverfill()
        {
            Begin("缓冲堆积削峰 (设备瞬断 200ms)");
            const int sr = 48000, ch = 2;
            const int targetMs = 65;                     // 与「极低」档的时钟目标一致
            int targetFrames = sr * targetMs / 1000;
            int blockFrames = 20 * sr / 1000;             // 每次 20ms, 模拟捕获回调粒度

            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sr, ch);
            var buf = new PcmRingBuffer(fmt, 1000);
            var group = new ClockGroup(targetFrames, sr, null);
            group.MemberCount = 1;
            var p = new DriftCorrectingProvider(buf, targetFrames, ch, 1, 0, null, "T", 0, group);

            // 0.1 的直流: 既能验"削峰后仍在出声", 又不受插值影响
            var block = new byte[blockFrames * ch * 4];
            var blockF = new float[blockFrames * ch];
            for (int i = 0; i < blockF.Length; i++) blockF[i] = 0.1f;
            Buffer.BlockCopy(blockF, 0, block, 0, block.Length);

            int backlog = sr * 300 / 1000;                // 一次性堆 300ms (卡住 200ms 的典型量)
            var big = new byte[backlog * ch * 4];
            var bigF = new float[backlog * ch];
            for (int i = 0; i < bigF.Length; i++) bigF[i] = 0.1f;
            Buffer.BlockCopy(bigF, 0, big, 0, big.Length);
            buf.AddSamples(big, 0, big.Length);

            var outBuf = new float[blockFrames * ch];
            bool threw = false;
            int maxMs = 0;
            double lastRms = -1;
            try
            {
                for (int n = 0; n < 120; n++)         // 2.4 秒的播放
                {
                    buf.AddSamples(block, 0, block.Length);   // 实时补进来的 20ms
                    p.Read(outBuf, 0, outBuf.Length);
                    lastRms = p.TakeLevelRms();
                    // 前 3 块里会跑第一次削峰, 之后水位必须稳在目标附近
                    if (n >= 3)
                    {
                        int ms = p.BufferedMs;
                        if (ms > maxMs) maxMs = ms;
                    }
                }
            }
            catch (Exception) { threw = true; }

            Check("全程不抛异常 (小环计数未变负)", !threw);
            Check("削峰已触发", p.TrimCount >= 1);
            Check("削峰不反复 (只丢一次)", p.TrimCount == 1);
            Check("水位压在 " + maxMs + "ms ≤ 目标 2 倍", maxMs <= targetMs * 2);
            Check("削峰后水位贴近目标 (" + p.BufferedMs + "ms)", Math.Abs(p.BufferedMs - targetMs) <= 25);
            Check("削峰后仍非静音 (RMS " + lastRms.ToString("0.00") + ")", lastRms > 0.05);
        }

        /// <summary>
        /// F-09 地基回归: BufferedWaveProvider 默认 ReadFully=true 会把"没读到"补成
        /// 一段全零并返回请求的完整长度 —— Fill() 于是把补零当真实音频帧灌进小环,
        /// Discard() 于是把"想丢的量"当成"真丢掉的量"记账。这里用哨兵字节和 FIFO
        /// 序号把这两类失真直接钉死, 不需要声卡。
        /// </summary>
        static void CasePcmRingBufferShortRead()
        {
            Begin("PcmRingBuffer 短读语义与 Discard 边界 (F-09)");
            const int sr = 48000, ch = 2;
            const int frameBytes = ch * 4;
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sr, ch);
            var buf = new PcmRingBuffer(fmt, 1000);
            var rd = new byte[64 * frameBytes];

            // ① 空缓冲: 返回 0, 且绝不在目标数组尾部补零 (修复前会返回 512 且整片清零)
            FillSentinel(rd);
            int got = buf.Read(rd, 0, rd.Length);
            Check("空缓冲 Read 返回 0 (实测 " + got + ")", got == 0);
            Check("空缓冲不补零 (哨兵完好)", SentinelIntact(rd, 0));
            Check("空缓冲 Discard 返回 0", buf.Discard(16 * frameBytes) == 0);

            // ② 少于请求量: 只返回真实存量, 内容正确, 尾部不补零
            buf.Clear(); FillSentinel(rd);
            var ten = MakeFrames(10, ch, 1f);
            buf.AddSamples(ten, 0, ten.Length);
            got = buf.Read(rd, 0, 64 * frameBytes);
            Check("短读返回真实存量 (10 帧 = " + 10 * frameBytes + " 字节, 实测 " + got + ")", got == 10 * frameBytes);
            Check("短读内容为真实写入值", FramesMatch(rd, 10, ch, 1f));
            Check("短读尾部不补零 (哨兵完好)", SentinelIntact(rd, 10 * frameBytes));
            Check("短读后缓冲清空", buf.BufferedBytes == 0);

            // ③ 请求少于存量: 足额返回且剩下的数据连续、不串位
            buf.Clear(); FillSentinel(rd);
            var twenty = MakeFrames(20, ch, 100f);
            buf.AddSamples(twenty, 0, twenty.Length);
            got = buf.Read(rd, 0, 5 * frameBytes);
            Check("请求少于存量时足额返回", got == 5 * frameBytes);
            Check("读到的前 5 帧序号正确", FramesMatch(rd, 5, ch, 100f));
            Check("剩余 15 帧", buf.BufferedBytes == 15 * frameBytes);
            var rest = new byte[15 * frameBytes];
            int gotRest = buf.Read(rest, 0, rest.Length);
            Check("余下数据序号连续 (无补零插入导致的错位)",
                gotRest == 15 * frameBytes && FramesMatch(rest, 15, ch, 105f));

            // ④ Discard 大于 / 等于 / 小于存量, 以及跨 scratch 分块的大额夹取
            buf.Clear(); buf.AddSamples(ten, 0, ten.Length);
            Check("Discard 请求 > 存量: 只返回实际存量",
                buf.Discard(64 * frameBytes) == 10 * frameBytes);
            Check("... 且缓冲清空", buf.BufferedBytes == 0);

            buf.Clear(); buf.AddSamples(ten, 0, ten.Length);
            Check("Discard 请求 = 存量: 返回全额", buf.Discard(10 * frameBytes) == 10 * frameBytes);
            Check("... 且缓冲清空", buf.BufferedBytes == 0);

            buf.Clear(); buf.AddSamples(twenty, 0, twenty.Length);
            Check("Discard 请求 < 存量: 返回请求量", buf.Discard(7 * frameBytes) == 7 * frameBytes);
            Check("... 剩余 13 帧", buf.BufferedBytes == 13 * frameBytes);
            var after = new byte[13 * frameBytes]; FillSentinel(after);
            int gotAfter = buf.Read(after, 0, after.Length);
            Check("... 丢的是最旧数据, 余下仍按 FIFO 顺序",
                gotAfter == 13 * frameBytes && FramesMatch(after, 13, ch, 107f));

            buf.Clear();
            var big = MakeFrames(3000, ch, 1f);       // 24000 字节 > 3 个 scratch 分块
            buf.AddSamples(big, 0, big.Length);
            Check("大额 Discard 夹取生效且跨分块累计正确",
                buf.Discard(40000) == 3000 * frameBytes && buf.BufferedBytes == 0);

            // ⑤ 环绕回卷: 反复写读让内部读写游标绕圈, 校验顺序与内容始终正确
            var wrap = new PcmRingBuffer(fmt, 20);   // 20ms ≈ 960 帧, 40 轮必然多次回卷
            var wrapRd = new byte[100 * frameBytes];
            bool orderOk = true;
            for (int round = 0; round < 40; round++)
            {
                float start = round * 100f + 1f;
                var block = MakeFrames(100, ch, start);
                wrap.AddSamples(block, 0, block.Length);
                FillSentinel(wrapRd);
                int g = wrap.Read(wrapRd, 0, wrapRd.Length);
                if (g != 100 * frameBytes || !FramesMatch(wrapRd, 100, ch, start) ||
                    !SentinelIntact(wrapRd, 100 * frameBytes))
                {
                    orderOk = false;
                    break;
                }
            }
            Check("40 轮写读跨环形回卷仍保持 FIFO 且不补零", orderOk);
        }

        /// <summary>每帧各通道写入同一个可识别的序号值 (start + f), 便于校验 FIFO 顺序。</summary>
        static byte[] MakeFrames(int frames, int ch, float start)
        {
            var b = new byte[frames * ch * 4];
            for (int f = 0; f < frames; f++)
            {
                var four = BitConverter.GetBytes(start + f);
                for (int c = 0; c < ch; c++)
                    Buffer.BlockCopy(four, 0, b, (f * ch + c) * 4, 4);
            }
            return b;
        }

        static bool FramesMatch(byte[] b, int frames, int ch, float start)
        {
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < ch; c++)
                    if (Math.Abs(BitConverter.ToSingle(b, (f * ch + c) * 4) - (start + f)) > 1e-4f)
                        return false;
            return true;
        }

        static void FillSentinel(byte[] b)
        {
            for (int i = 0; i < b.Length; i++) b[i] = 0xCC;
        }

        static bool SentinelIntact(byte[] b, int from)
        {
            for (int i = from; i < b.Length; i++) if (b[i] != 0xCC) return false;
            return true;
        }

        /// <summary>
        /// F-01/F-04 回归: 共享组里只削一路会永久错开相关声对的捕获时间轴; 端点缺失的成员
        /// 若留在组里, 旧 holding 会让健康路一直 -6dB、旧 err 会带偏共享 ratio。
        /// 全部用纯对象驱动, 不打开任何端点。
        /// </summary>
        static void CaseClockGroupAlignment()
        {
            Begin("共享时钟组: 组级同步削峰与离线成员隔离 (F-01/F-04)");
            const int sr = 48000, ch = 2, targetMs = 65;
            int targetFrames = sr * targetMs / 1000;
            int keep = (int)(targetFrames * 1.3);
            int backlog = sr * 300 / 1000;
            int drop = backlog - keep;
            var fmt = NAudio.Wave.WaveFormat.CreateIeeeFloatWaveFormat(sr, ch);
            var big = MakeFrames(backlog, ch, 1f);

            // ① 统一丢弃量: 所有活跃成员丢相同帧数, 削完两路水位必须相等
            var g1 = new ClockGroup(targetFrames, sr, null); g1.MemberCount = 2;
            var b1 = new PcmRingBuffer(fmt, 1000);
            var p1 = new DriftCorrectingProvider(b1, targetFrames, ch, 1, 0, null, "L", 0, g1);
            var b2 = new PcmRingBuffer(fmt, 1000);
            var p2 = new DriftCorrectingProvider(b2, targetFrames, ch, 1, 0, null, "R", 1, g1);
            b1.AddSamples(big, 0, big.Length);
            b2.AddSamples(big, 0, big.Length);
            int before1 = p1.BufferedFrames, before2 = p2.BufferedFrames;
            int d = g1.TrimGroup(drop, keep);
            Check("组级削峰返回统一丢弃量 (" + d + " 帧)", d == drop);
            Check("两路各丢相同帧数", before1 - p1.BufferedFrames == d && before2 - p2.BufferedFrames == d);
            Check("削峰后两路水位相等 (时间轴未错位)", p1.BufferedFrames == p2.BufferedFrames);
            Check("两路各记账一次削峰", p1.TrimCount == 1 && p2.TrimCount == 1);

            // ② 夹取: 任一成员余量不足时全组都不削, 绝不把健康路削空或只削一部分
            var g2 = new ClockGroup(targetFrames, sr, null); g2.MemberCount = 2;
            var b3 = new PcmRingBuffer(fmt, 1000);
            var p3 = new DriftCorrectingProvider(b3, targetFrames, ch, 1, 0, null, "L", 0, g2);
            var b4 = new PcmRingBuffer(fmt, 1000);
            var p4 = new DriftCorrectingProvider(b4, targetFrames, ch, 1, 0, null, "R", 1, g2);
            b3.AddSamples(big, 0, big.Length);   // 只有 L 堆积, R 是空的健康路
            int before3 = p3.BufferedFrames;
            Check("任一成员会被削到 keep 以下 → 全组不削", g2.TrimGroup(drop, keep) == 0);
            Check("... 且两路缓冲均未被改动", p3.BufferedFrames == before3 && p4.BufferedFrames == 0);

            // ③ 集成: provider 的 TrimOverfill 确实走组级路径 (不是只削发起方)
            var g3 = new ClockGroup(targetFrames, sr, null); g3.MemberCount = 2;
            var b5 = new PcmRingBuffer(fmt, 1000);
            var p5 = new DriftCorrectingProvider(b5, targetFrames, ch, 1, 0, null, "L", 0, g3);
            var b6 = new PcmRingBuffer(fmt, 1000);
            var p6 = new DriftCorrectingProvider(b6, targetFrames, ch, 1, 0, null, "R", 1, g3);
            b5.AddSamples(big, 0, big.Length);
            b6.AddSamples(big, 0, big.Length);
            int blockFrames = 20 * sr / 1000;
            var block = new byte[blockFrames * ch * 4];
            var blockF = new float[blockFrames * ch];
            for (int i = 0; i < blockF.Length; i++) blockF[i] = 0.1f;
            Buffer.BlockCopy(blockF, 0, block, 0, block.Length);
            var outBuf = new float[blockFrames * ch];
            for (int n = 0; n < 20; n++)
            {
                b5.AddSamples(block, 0, block.Length);
                b6.AddSamples(block, 0, block.Length);
                p5.Read(outBuf, 0, outBuf.Length);
                p6.Read(outBuf, 0, outBuf.Length);
            }
            Check("两路都发生削峰 (组级广播生效)", p5.TrimCount >= 1 && p6.TrimCount >= 1);
            int diffMs = Math.Abs(p5.BufferedMs - p6.BufferedMs);
            Check("削峰后两路水位差 ≤5ms (实测 " + diffMs + "ms)", diffMs <= 5);

            // ④ 离线成员隔离: 清 holding、清旧 err、排除出共享平均
            var g4 = new ClockGroup(targetFrames, sr, null); g4.MemberCount = 2;
            g4.NotifyHolding(0, true);
            Check("停用前 OtherHolding(1) = true", g4.OtherHolding(1));
            g4.Update(0, targetFrames * 3);       // 提交一个极大的误差
            Check("离线前误差确实带偏共享 ratio", Math.Abs(g4.RatioPpm) > 100);
            g4.DeactivateMember(0);
            Check("停用后 ActiveCount = 1", g4.ActiveCount == 1);
            Check("停用后 OtherHolding(1) = false (健康路不再误降 6dB)", !g4.OtherHolding(1));
            g4.Update(1, targetFrames);
            Check("离线成员旧 err 不再参与平均 (ppm≈" + g4.RatioPpm.ToString("0") + ")",
                Math.Abs(g4.RatioPpm) < 100);
            g4.ActivateMember(0);
            Check("重新激活后 ActiveCount = 2", g4.ActiveCount == 2);

            // ⑤ 组内只剩一个活跃成员 → 退化为单路削峰, 不能因为同伴离线就停止自愈
            var g5 = new ClockGroup(targetFrames, sr, null); g5.MemberCount = 2;
            var b7 = new PcmRingBuffer(fmt, 1000);
            var p7 = new DriftCorrectingProvider(b7, targetFrames, ch, 1, 0, null, "L", 0, g5);
            var b8 = new PcmRingBuffer(fmt, 1000);
            var p8 = new DriftCorrectingProvider(b8, targetFrames, ch, 1, 0, null, "R", 1, g5);
            g5.DeactivateMember(1);
            b7.AddSamples(big, 0, big.Length);
            for (int n = 0; n < 20; n++)
            {
                b7.AddSamples(block, 0, block.Length);
                p7.Read(outBuf, 0, outBuf.Length);
            }
            Check("只剩一个活跃成员仍能单路削峰自愈", p7.TrimCount >= 1);
            Check("离线成员不参与削峰", p8.TrimCount == 0);

            // ⑥ 只有一路堆积时组级必须"全组不削": 单独削掉一路旧数据正是 F-01 的根因。
            // 两路同步堆积时分不出新旧实现 (各自单路削也会都削), 这个非对称场景才能钉死。
            // 组级不削后堆积留给看门狗判据②(水位>200ms 持续 10s)兜底重建。
            var g6 = new ClockGroup(targetFrames, sr, null); g6.MemberCount = 2;
            var b9 = new PcmRingBuffer(fmt, 1000);
            var p9 = new DriftCorrectingProvider(b9, targetFrames, ch, 1, 0, null, "L", 0, g6);
            var b10 = new PcmRingBuffer(fmt, 1000);
            var p10 = new DriftCorrectingProvider(b10, targetFrames, ch, 1, 0, null, "R", 1, g6);
            b9.AddSamples(big, 0, big.Length);   // 只有 L 堆了 300ms, R 保持健康低水位
            for (int n = 0; n < 4; n++)
            {
                b9.AddSamples(block, 0, block.Length);
                b10.AddSamples(block, 0, block.Length);
                p9.Read(outBuf, 0, outBuf.Length);
                p10.Read(outBuf, 0, outBuf.Length);
            }
            Check("只有一路堆积时组级不削峰 (L=" + p9.TrimCount + ", R=" + p10.TrimCount + ")",
                p9.TrimCount == 0 && p10.TrimCount == 0);
        }

        /// <summary>
        /// F-03: 重建 / Dispose 生命周期互斥。生产代码里 AudioEngine 与每路 SinkRuntime 各持一把
        /// LifecycleGate, 这里直接并发压这把门, 不打开任何音频端点。
        /// 重点不是"锁存在", 而是两个容易写错的时序: 取消标志必须早于取锁置位、
        /// 释放必须在同一把锁内且只认领一次。
        /// </summary>
        static void CaseLifecycleGate()
        {
            Begin("F-03 生命周期门: 重建/Dispose 互斥 + 资源失败注入");

            // ① 在途重建中调用 Shutdown: 必须等它退出才释放
            var gate = new LifecycleGate();
            var entered = new ManualResetEventSlim(false);
            var letGo = new ManualResetEventSlim(false);
            var closerDone = new ManualResetEventSlim(false);
            int bodyRan = 0, released = 0;

            var rebuild = new Thread((ThreadStart)delegate
            {
                gate.Run(delegate
                {
                    Interlocked.Increment(ref bodyRan);
                    entered.Set();
                    letGo.Wait(3000);
                    return true;
                }, delegate { return false; });
            });
            rebuild.IsBackground = true;
            rebuild.Start();
            Check("在途重建已进入临界区", entered.Wait(2000) && gate.InFlight == 1);

            var closer = new Thread((ThreadStart)delegate
            {
                gate.Shutdown(delegate { Interlocked.Increment(ref released); });
                closerDone.Set();
            });
            closer.IsBackground = true;
            closer.Start();
            Thread.Sleep(40);
            Check("在途重建未退出时 Dispose 不得释放资源",
                !closerDone.IsSet && Interlocked.CompareExchange(ref released, 0, 0) == 0);
            Check("取消标志已在取锁前置位(在途能立刻看到)", gate.IsShutdown);

            letGo.Set();
            Check("重建线程按时退出", rebuild.Join(3000));
            Check("Shutdown 等到重建退出后才完成", closer.Join(3000) && closerDone.IsSet);
            Check("释放恰好执行一次且无残留操作",
                Interlocked.CompareExchange(ref released, 0, 0) == 1 && gate.InFlight == 0);

            // ② 关闭后不得复活, 重复调用不得重复释放
            Check("关闭后新重建一律被拒绝",
                !gate.Run(delegate { Interlocked.Increment(ref bodyRan); return true; }, delegate { return false; })
                && Interlocked.CompareExchange(ref bodyRan, 0, 0) == 1);
            Check("重复 Shutdown 不重复释放",
                !gate.Shutdown(delegate { Interlocked.Increment(ref released); })
                && Interlocked.CompareExchange(ref released, 0, 0) == 1);

            // ③ 取消标志必须先于取锁置位: 在途等待循环靠它快速退出, 否则关窗会被拖满超时
            var g2 = new LifecycleGate();
            var t2 = new Thread((ThreadStart)delegate
            {
                g2.Run(delegate
                {
                    int guard = 0;
                    while (!g2.IsShutdown && guard++ < 400) Thread.Sleep(5);
                    return true;
                }, delegate { return false; });
            });
            t2.IsBackground = true;
            t2.Start();
            Thread.Sleep(25);
            int t0 = Environment.TickCount;
            bool closed = g2.Shutdown(null);
            bool quick = t2.Join(1000);
            Check("取消置位后 Shutdown 能取到锁", closed);
            Check("在途等待循环收到取消标志后 1 秒内退出",
                quick && unchecked(Environment.TickCount - t0) < 1000);

            // ④ 资源失败注入: 装配失败时只回收局部作用域, 不把半成品提交到公开字段
            var g3 = new LifecycleGate();
            int created = 0, recycled = 0, committed = 0, publicRef = 0;
            g3.Run(delegate
            {
                Interlocked.Increment(ref created);          // BuildPipeline: 局部创建端点/管线
                bool initOk = false;                        // 注入: Init 阶段端点被独占
                if (!initOk) { Interlocked.Increment(ref recycled); return false; }
                Interlocked.Increment(ref committed);
                Interlocked.Increment(ref publicRef);
                return true;
            }, delegate { return false; });
            Check("资源装配失败时局部完整回收",
                Interlocked.CompareExchange(ref created, 0, 0) == 1 &&
                Interlocked.CompareExchange(ref recycled, 0, 0) == 1);
            Check("失败路径不把半成品提交到公开字段",
                Interlocked.CompareExchange(ref committed, 0, 0) == 0 &&
                Interlocked.CompareExchange(ref publicRef, 0, 0) == 0);
            Check("失败后生命周期锁仍可正常关闭",
                g3.Shutdown(null) && !g3.Run(delegate { return true; }, delegate { return false; }));

            // ⑤ body 抛异常: InFlight 必须复位, 锁必须释放
            var g4 = new LifecycleGate();
            bool threw = false;
            try
            {
                g4.Run<bool>(delegate { throw new InvalidOperationException("注入: 端点打开异常"); },
                             delegate { return false; });
            }
            catch (InvalidOperationException) { threw = true; }
            Check("注入异常后 InFlight 归零", threw && g4.InFlight == 0);
            var t4 = new Thread((ThreadStart)delegate { g4.Shutdown(null); });
            t4.IsBackground = true;
            t4.Start();
            Check("注入异常后 Shutdown 不死锁", t4.Join(1000));

            // ⑥ 并发压力: 8 路重建 + 一次关闭, 计数守恒且释放恰好一次
            var g5 = new LifecycleGate();
            int ran = 0, rejected = 0, disposeCount = 0;
            var threads = new Thread[8];
            for (int i = 0; i < threads.Length; i++)
            {
                threads[i] = new Thread((ThreadStart)delegate
                {
                    for (int k = 0; k < 60; k++)
                    {
                        g5.Run(delegate { Interlocked.Increment(ref ran); return true; },
                               delegate { Interlocked.Increment(ref rejected); return false; });
                        Thread.Yield();
                    }
                });
                threads[i].IsBackground = true;
                threads[i].Start();
            }
            Thread.Sleep(5);
            Check("并发关闭只认领一次",
                g5.Shutdown(delegate { Interlocked.Increment(ref disposeCount); }));
            Check("并发关闭不重复释放",
                !g5.Shutdown(delegate { Interlocked.Increment(ref disposeCount); }));
            bool allJoined = true;
            for (int i = 0; i < threads.Length; i++) allJoined &= threads[i].Join(3000);
            Check("并发重建线程全部退出", allJoined);
            Check("每次重建要么执行要么被拒绝",
                Interlocked.CompareExchange(ref ran, 0, 0) +
                Interlocked.CompareExchange(ref rejected, 0, 0) == 8 * 60);
            Check("关闭后释放恰好一次且无残留操作",
                Interlocked.CompareExchange(ref disposeCount, 0, 0) == 1 && g5.InFlight == 0);
        }

        /// <summary>
        /// 日志分级: 多路 provider 的输出行带着路标记前缀 ("[左] [警告] …")。
        /// 只认行首 "[警告]" 的旧实现会把这类关键告警判成 INFO —— INFO 走 2 秒批量刷盘,
        /// 崩溃时最后 2 秒正好是"为什么断音"的证据, 全丢。
        /// </summary>
        static void CaseLogClassify()
        {
            Begin("日志分级 (带路标记前缀)");
            Check("[错误] → ERROR", Infrastructure.SessionLog.Classify("[错误] 输出左已停止") == Infrastructure.LogLevel.Error);
            Check("[左] [警告] → WARNING",
                Infrastructure.SessionLog.Classify("[左] [警告] 缓冲水位偏低: 调节水位 12ms") == Infrastructure.LogLevel.Warning);
            Check("[右] [错误] → ERROR",
                Infrastructure.SessionLog.Classify("[右] [错误] 校正触顶") == Infrastructure.LogLevel.Error);
            Check("[提示] → INFO", Infrastructure.SessionLog.Classify("[提示] 缓冲堆积到 230ms") == Infrastructure.LogLevel.Info);
            Check("普通行 → INFO", Infrastructure.SessionLog.Classify("分流已启动 2 路") == Infrastructure.LogLevel.Info);
            Check("null → INFO", Infrastructure.SessionLog.Classify(null) == Infrastructure.LogLevel.Info);
        }

        /// <summary>
        /// 清空日志后继续写: 截断文件必须同时把写指针复位, 否则新行写在旧偏移处,
        /// 文件前部留一片 NUL —— 用文本工具打开就是"日志开头一堆乱码"。
        /// </summary>
        static void CaseSessionLogClear()
        {
            Begin("清空日志后写指针复位");
            string dir = Path.Combine(Path.GetTempPath(), "ss-logtest-" + Guid.NewGuid().ToString("N"));
            string text = null;
            try
            {
                var log = new Infrastructure.SessionLog(dir);
                for (int i = 0; i < 200; i++)
                    log.Write("10:00:00.000", Infrastructure.LogLevel.Info, "旧内容 " + i + " 占位占位占位占位");
                log.Clear();
                log.Write("10:00:01.000", Infrastructure.LogLevel.Error, "清空后的第一行");
                log.Dispose();
                text = File.ReadAllText(log.FilePath);
            }
            catch (Exception ex) { Check("读写日志: " + ex.Message, false); }
            finally { try { Directory.Delete(dir, true); } catch { } }

            if (text == null) return;
            Check("文件以新内容开头", text.StartsWith("10:00:01.000"));
            Check("没有 NUL 空洞", text.IndexOf('\0') < 0);
            Check("旧内容已清空", text.IndexOf("旧内容", StringComparison.Ordinal) < 0);
        }

        static OutputLane Lane(string key, string dev, int delay, int vol)        {
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
