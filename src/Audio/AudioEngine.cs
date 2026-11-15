using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    class AudioEngine : IDisposable
    {
        WasapiLoopbackCapture loopback;
        WasapiOut leftOut, rightOut;
        BufferedWaveProvider leftBuf, rightBuf;
        DriftCorrectingProvider leftProvider, rightProvider;
        MMDevice leftDev, rightDev, captureDev;
        // 设备 ID: MMDevice 在 Start 所在线程(后台)自建, 不跨线程传递 (NAudio #214)
        readonly string leftDevId, rightDevId;
        readonly int driftMode, fixedPpm;
        Action<string> log;
        volatile int sampleRate;
        DelayLine leftLine, rightLine;
        bool splitErrLogged = false;
        // 起播时捕获源无数据 → 首帧到达时补一条"就绪"日志 (NoAudioYet 场景)
        volatile bool silentStartup = false;
        int firstDataLogged = 0;
        // 采集回调置位, 预缓冲等待用它替代 20ms 忙等轮询
        AutoResetEvent dataReady;
        // 输出缓冲溢出告警限流 (DiscardOnBufferOverflow 会静默丢块, 不记就等于丢音无迹可查)
        int lastOverflowWarnTick;

        // ---- 告警去重: 左右两路 provider 各自限流, 同一现象必然打两条 ----
        readonly object logGate = new object();
        readonly Dictionary<string, int> logCategoryTick = new Dictionary<string, int>();

        /// <summary>按告警类别 1.5s 去重: 左右两路同时触发同一类告警时只留第一条。
        /// 只用于 provider, 启动/停止等一次性日志仍走原 log, 避免被吞。</summary>
        void LogThrottled(string msg)
        {
            if (msg == null || log == null) return;
            string key = CategoryKey(msg);
            int now = Environment.TickCount;
            lock (logGate)
            {
                int last;
                if (logCategoryTick.TryGetValue(key, out last) && unchecked(now - last) < 1500) return;
                logCategoryTick[key] = now;
                if (logCategoryTick.Count > 64) logCategoryTick.Clear();   // 防无界增长
            }
            try { log(msg); } catch { }
        }

        /// <summary>"[左] [警告] 缓冲水位偏低: 调节水位 28ms …" → "[警告] 缓冲水位偏低"。
        /// 开头的声道标记必须跳过 —— 左右标签不同, 计入类别键会让去重完全失效。</summary>
        static string CategoryKey(string msg)
        {
            int start = 0;
            if (msg.Length > 3 && msg[0] == '[' && msg[2] == ']') start = 3;   // "[左]" / "[右]"
            int n = start;
            while (n < msg.Length && n < start + 40)
            {
                char c = msg[n];
                if (c == ':' || c == '(' || c == '+' || c == '-' || (c >= '0' && c <= '9')) break;
                n++;
            }
            return msg.Substring(start, n - start).TrimEnd();
        }

        // 长期运行看门狗: 输出/捕获线程会因设备休眠、被独占、拔插而"悄悄停止",
        // 不订阅这些事件时界面会一直显示"运行中"却早已无声 (实测有过 71 分钟)
        volatile bool stopping;             // 主动 Stop 期间这些事件也会触发 → 屏蔽
        bool disposed;                      // Dispose 幂等守卫
        int stallNotified;                  // Stalled 只上报一次 (左右常同时停)
        int lastCaptureTick;                // 最近一次收到捕获数据的 TickCount (0 = 尚未收到)
        /// <summary>链路意外中断 (参数为人话原因)。在音频线程触发, 订阅方须自行回 UI 线程。</summary>
        public event Action<AudioEngine, string> Stalled;

        // 起播余量: 覆盖输出端起播时对缓冲的预拉取。
        // 实测(蓝牙音箱)起播后峰值抽走约 80ms; USB 有线音箱应更小, 取 80ms 匹配。
        // 多填的这部分由起播瞬态消耗, 稳态由时钟环回落到 driftTargetMs, 不增加稳态延迟。
        const int PrePullGuardMs = 80;

        public AudioEngine(string leftDevId, string rightDevId, Action<string> log, int driftMode, int fixedPpm)
        {
            this.leftDevId = leftDevId;
            this.rightDevId = rightDevId;
            this.log = log;
            this.driftMode = driftMode;
            this.fixedPpm = fixedPpm;
        }

        // Start 实际捕获的设备 ID (取启动时的系统默认输出, 可能与点开始时的选择不同)
        public string CaptureId
        {
            get
            {
                try { return captureDev != null ? captureDev.ID : null; } catch { return null; }
            }
        }

        /// <summary>最近一次收到捕获数据的时刻 (Environment.TickCount), 0 = 起播后尚未收到。</summary>
        public int LastCaptureTick
        {
            get { return lastCaptureTick; }
        }

        /// <summary>输出线程累计渲染样本数。看门狗用它判断输出线程是否还在被调用:
        /// 比看水位可靠 —— 水位在连通时也可能短暂不动, 而渲染计数冻结必然是停摆。
        /// 必须左右分开看: 只看总和的话, 一侧停摆另一侧照常涨, 总和仍在增长 → 单只音箱
        /// 休眠/被独占这种"无声但界面正常"的故障永远检不出来。</summary>
        public long LeftRenderedSamples
        {
            get { return leftProvider == null ? 0 : leftProvider.RenderedSamples; }
        }

        public long RightRenderedSamples
        {
            get { return rightProvider == null ? 0 : rightProvider.RenderedSamples; }
        }

        /// <summary>单侧当前瞬时水位 (ms), 仅供看门狗日志描述现场。</summary>
        public int LeftBufferMs
        {
            get { return leftProvider == null ? 0 : leftProvider.BufferedMs; }
        }

        public int RightBufferMs
        {
            get { return rightProvider == null ? 0 : rightProvider.BufferedMs; }
        }

        /// <summary>启动结果: 预缓冲是否真的拿到了数据 (旧实现超时也当成功, 导致空缓冲起播)。</summary>
        public enum StartOutcome
        {
            /// <summary>左右缓冲均达到目标水位。</summary>
            Ready,
            /// <summary>超时但已有部分数据, 开头可能欠载。</summary>
            Insufficient,
            /// <summary>超时且完全没有数据 (捕获源当前静音/无声源)。</summary>
            NoAudioYet
        }

        public StartOutcome Start(float gain, float master, int prebufferMs, int outLatencyMs, int leftDelayMs, int rightDelayMs,
            Func<bool> aborted, string expectedCaptureId)
        {
            // 三个 MMDevice 都在本线程创建: 跨线程使用 UI 线程创建的 MMDevice 会 QI IMMDevice 失败 E_NOINTERFACE (NAudio #214)
            var enumerator = new MMDeviceEnumerator();
            try
            {
                // 启动前校验: 设备可能在这几秒内被拔出, 用陈旧 ID 建 WasapiOut 只会抛 "尚未实现" 这类鬼话
                // 一次枚举同时校验左右 (原实现对左/右各枚举了一遍全量设备)
                string missing = FindMissingChannel(enumerator, leftDevId, rightDevId);
                if (missing != null)
                    throw new AudioDeviceException(missing + "不可用（可能已拔出或被禁用），请刷新设备后重试");

                leftDev = enumerator.GetDevice(leftDevId);
                rightDev = enumerator.GetDevice(rightDevId);
                captureDev = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            }
            finally { try { enumerator.Dispose(); } catch { } }

            string captureName = DeviceCatalog.SafeName(captureDev);
            log("[启动] 捕获源: " + captureName);
            if (expectedCaptureId != null && CaptureId != null && CaptureId != expectedCaptureId)
                log("[警告] 当前系统默认输出与预期捕获设备不一致 (预期 " + expectedCaptureId + ")，已按当前默认输出捕获");

            loopback = new WasapiLoopbackCapture(captureDev);
            var wf = loopback.WaveFormat;
            sampleRate = wf.SampleRate;
            log("[启动] 格式: " + wf.SampleRate + "Hz / " + wf.Encoding + " / " + wf.BitsPerSample + "bit / " + wf.Channels + "ch");

            // 端点传输类型: 蓝牙 A2DP 自成一条链路, 偶发"咔哒"多出在链路层而不是缓冲层,
            // 写进日志才能在排查时立刻区分责任方
            string lt = DeviceCatalog.TransportName(leftDev), rt = DeviceCatalog.TransportName(rightDev);
            log("[启动] 端点传输: 左=" + DeviceCatalog.TransportText(lt) + " / 右=" + DeviceCatalog.TransportText(rt));
            if ((DeviceCatalog.IsBluetooth(lt) || DeviceCatalog.IsBluetooth(rt)) && outLatencyMs < 20)
                log("[提示] 蓝牙端点使用 " + outLatencyMs + "ms 输出缓冲偏紧；若链路上偶发\"咔哒\"，把「延迟」档位调到 25ms/20ms 或更高");

            var outFormat = WaveFormat.CreateIeeeFloatWaveFormat(wf.SampleRate, 1);
            // 时钟环稳态目标: 档位越小目标越低, 上限 120ms。
            // +40 安全余量: 校正环稳态摆动 std 8ms (shared ratio 后更小) + 块锯齿 18ms
            // = ±42ms 极端 (3σ)。+40 时最低档 (极限 45ms) 最低水位 3ms, 接近 holding 闸门
            // 15%×45=6.75ms, 但极限档本标"频繁断音"且 starveStreak>=2 防抖过滤单次;
            // 其余档位余量充足 (最低 13-78ms vs 阈值 8-18ms)。
            int driftTargetMs = Math.Min(120, Math.Max(prebufferMs, outLatencyMs) + 40);
            int driftTargetSamples = (int)((long)wf.SampleRate * driftTargetMs / 1000);
            // 起播填充目标 = 稳态目标 + 余量 (见 PrePullGuardMs 注释)
            int fillTargetMs = driftTargetMs + PrePullGuardMs;
            int fillTargetSamples = (int)((long)wf.SampleRate * fillTargetMs / 1000);
            leftBuf = new BufferedWaveProvider(outFormat) { BufferDuration = TimeSpan.FromMilliseconds(1000), DiscardOnBufferOverflow = true };
            rightBuf = new BufferedWaveProvider(outFormat) { BufferDuration = TimeSpan.FromMilliseconds(1000), DiscardOnBufferOverflow = true };

            leftOut = new WasapiOut(leftDev, AudioClientShareMode.Shared, true, outLatencyMs);
            rightOut = new WasapiOut(rightDev, AudioClientShareMode.Shared, true, outLatencyMs);
            // 共享校正决策: 左右 provider 通过同一个 DriftCorrectionShared 协调 ratio,
            // 消除"独立算 ratio -> 插值方向不对称 -> 偏音"的根因
            var shared = new DriftCorrectionShared(driftTargetSamples, wf.SampleRate, LogThrottled);
            leftProvider = new DriftCorrectingProvider(leftBuf, driftTargetSamples, driftMode, fixedPpm, LogThrottled, "左", true, shared);
            rightProvider = new DriftCorrectingProvider(rightBuf, driftTargetSamples, driftMode, fixedPpm, LogThrottled, "右", false, shared);
            // Play 之前设好增益/主音量, 否则首帧以满音量(1.0)出声造成跳变
            leftProvider.Gain = gain;
            rightProvider.Gain = gain;
            leftProvider.Master = master;
            rightProvider.Master = master;
            leftOut.Init(leftProvider);
            rightOut.Init(rightProvider);

            // 输出/捕获线程意外退出 → 看门狗接管 (设备休眠、被独占、被移除都会走到这里)。
            // 主动 Stop 时也会触发, 由 stopping 标记屏蔽, 否则每次停止都会被当成故障。
            leftOut.PlaybackStopped += delegate(object s, StoppedEventArgs e) { OnOutputStopped("左", e); };
            rightOut.PlaybackStopped += delegate(object s, StoppedEventArgs e) { OnOutputStopped("右", e); };
            loopback.RecordingStopped += delegate(object s, StoppedEventArgs e) { OnCaptureStopped(e); };

            // 左右独立延迟线: 输入是交错多声道原始数据 → 按 blockAlign(帧) 算目标字节并天然对齐
            // (旧算法按 mono float 4字节算 → 延迟减半; &~3 未对齐帧 → 44100Hz 等场景字节错位出破音)
            int blockAlign = Math.Max(1, wf.BlockAlign);
            leftLine = new DelayLine((int)Math.Min(int.MaxValue, (long)wf.SampleRate * leftDelayMs / 1000) * blockAlign,
                (buf, off, len) => Split(buf, off, len, 0, leftBuf));
            rightLine = new DelayLine((int)Math.Min(int.MaxValue, (long)wf.SampleRate * rightDelayMs / 1000) * blockAlign,
                (buf, off, len) => Split(buf, off, len, 1, rightBuf));

            dataReady = new AutoResetEvent(false);
            loopback.DataAvailable += (s, e) =>
            {
                // 采样线程裸异常会直接杀进程 → 捕获并去重记一条
                try
                {
                    leftLine.Write(e.Buffer, e.BytesRecorded);
                    rightLine.Write(e.Buffer, e.BytesRecorded);
                    // 捕获活性时间戳: 看门狗用它区分"源本来就静音"和"捕获线程已死"
                    if (e.BytesRecorded > 0) lastCaptureTick = Environment.TickCount;
                    if (silentStartup && e.BytesRecorded > 0 && Interlocked.Exchange(ref firstDataLogged, 1) == 0)
                        log("[启动] 音频流已就绪 (捕获源开始有数据)");
                }
                catch (Exception ex)
                {
                    if (!splitErrLogged)
                    {
                        splitErrLogged = true;
                        log("[错误] 采样数据处理异常: " + ex.Message);
                    }
                }
                // 唤醒预缓冲等待 (事件在 Dispose 里等采集线程退出后才释放, 此处无释放竞态)
                try
                {
                    var ev = dataReady;
                    if (ev != null) ev.Set();
                }
                catch (ObjectDisposedException) { }
                catch { }
            };

            loopback.StartRecording();

            int fillBytes = fillTargetSamples * 4;
            int targetMs = driftTargetSamples * 1000 / wf.SampleRate;
            // 等待上限随填充目标与延迟补偿动态放宽 (延迟 2000ms 时固定 2s 会超时)
            int maxWaitMs = fillTargetMs + Math.Max(leftDelayMs, rightDelayMs) + 1000;
            int waitStart = Environment.TickCount;
            while (leftBuf.BufferedBytes < fillBytes || rightBuf.BufferedBytes < fillBytes)
            {
                // 关窗中止检查: FormClosing 置 startAbort 后最多 20ms 即退出
                if (aborted != null && aborted()) throw new OperationCanceledException("启动已取消");
                if (Environment.TickCount - waitStart >= maxWaitMs) break;
                var ev = dataReady;
                if (ev != null) ev.WaitOne(20); else Thread.Sleep(20);
            }

            // 先取水位再修剪: 修剪会把快路压回目标值, 之后再判断就永远"达标"
            int gotL = leftBuf.BufferedBytes;
            int gotR = rightBuf.BufferedBytes;

            // 修剪快路多攒的水位: 等待以慢路为终点时, 无延迟的快路会多攒数据, 抵消延迟线的时差
            // 两 buf 统一修剪到填充阈值 → 净声道差 = 延迟线的 (L - R)
            TrimTo(leftBuf, fillBytes);
            TrimTo(rightBuf, fillBytes);

            StartOutcome outcome;
            if (gotL >= fillBytes && gotR >= fillBytes)
            {
                outcome = StartOutcome.Ready;
                log("[启动] 预缓冲完成 (L=" + gotL + " R=" + gotR + " 字节, 填充目标 " + fillTargetMs +
                    "ms / 时钟目标 " + targetMs + "ms / 档位 " + prebufferMs + "ms / 输出 " + outLatencyMs + "ms)");
            }
            else if (gotL == 0 || gotR == 0)
            {
                outcome = StartOutcome.NoAudioYet;
                log("[警告] 捕获源 " + captureName + " 在 " + (maxWaitMs / 1000.0).ToString("0.0") +
                    " 秒内没有音频数据 (L=" + gotL + " R=" + gotR + " 字节)，已按静音启动，播放声音后会自动补齐");
            }
            else
            {
                outcome = StartOutcome.Insufficient;
                log("[警告] 预缓冲不足 (L=" + gotL + " R=" + gotR + " / 目标 " + fillBytes +
                    " 字节)，开头可能出现欠载");
            }

            silentStartup = (outcome == StartOutcome.NoAudioYet);
            log("[启动] 时钟漂移补偿: " + (driftMode == 0 ? "关闭" : driftMode == 2 ? "固定 " + fixedPpm + "ppm" : "自适应") + ", 比例上限 ±0.50%");
            if (leftDelayMs > 0 || rightDelayMs > 0)
                log("[启动] 延迟补偿: 左=" + leftDelayMs + "ms 右=" + rightDelayMs + "ms");

            if (aborted != null && aborted()) throw new OperationCanceledException("启动已取消");

            leftOut.Play();
            rightOut.Play();
            log("[启动] 输出 L=" + DeviceCatalog.SafeName(leftDev));
            log("[启动] 输出 R=" + DeviceCatalog.SafeName(rightDev));
            return outcome;
        }

        // 一次枚举同时校验左右声道设备是否仍在活动输出列表中; 返回缺失的声道名, 都正常返回 null
        static string FindMissingChannel(MMDeviceEnumerator enumerator, string leftId, string rightId)
        {
            if (string.IsNullOrEmpty(leftId)) return "左声道设备";
            if (string.IsNullOrEmpty(rightId)) return "右声道设备";
            bool leftOk = false, rightOk = false;
            try
            {
                var coll = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                for (int i = 0; i < coll.Count; i++)
                {
                    string id = null;
                    try { id = coll[i].ID; } catch { }
                    if (id == leftId) leftOk = true;
                    if (id == rightId) rightOk = true;
                    if (leftOk && rightOk) break;
                }
            }
            catch { }
            if (!leftOk) return "左声道设备";
            if (!rightOk) return "右声道设备";
            return null;
        }

        public void SetMaster(float m)
        {
            // 主音量走采样层: WasapiOut.Volume 实际是设备端点音量，会与左右滑条的端点绑定冲突
            if (leftProvider != null) leftProvider.Master = m;
            if (rightProvider != null) rightProvider.Master = m;
        }

        public void SetGain(float gain)
        {
            if (leftProvider != null) leftProvider.Gain = gain;
            if (rightProvider != null) rightProvider.Gain = gain;
        }

        public void SetDriftMode(int mode, int fixedPpm)
        {
            if (leftProvider != null) leftProvider.SetMode(mode, fixedPpm);
            if (rightProvider != null) rightProvider.SetMode(mode, fixedPpm);
        }

        public string BufferStatus()
        {
            try
            {
                if (leftProvider == null || rightProvider == null || sampleRate <= 0) return "-";
                int minL = leftProvider.TakeMinBufferedMs();
                int minR = rightProvider.TakeMinBufferedMs();
                // 调节水位在前: 它是校正环真正稳定的量, 也是判断是否需要扩缓冲的依据。
                // 瞬时值紧随其后: 含块粒度锯齿, 只用于看单次回调的抖动幅度。
                // 校正只显示一个值: shared ratio 后左右必然相等。
                // 水位差: shared ratio 的副作用指标, 同型号 USB 音箱应 < 5ms。
                int regL = leftProvider.RegulatedMs, regR = rightProvider.RegulatedMs;
                string s = "调节 L=" + regL + "ms R=" + regR + "ms 差=" + (regL - regR) + "ms" +
                    " 瞬时 L=" + leftProvider.BufferedMs + "ms R=" + rightProvider.BufferedMs + "ms" +
                    " 校正=" + Ppm(leftProvider.RatioPpm) +
                    " 最低 L=" + minL + "ms R=" + minR + "ms";
                long ul = leftProvider.UnderrunSamples, ur = rightProvider.UnderrunSamples;
                if (ul > 0 || ur > 0) s += " 欠载 L=" + ul + " R=" + ur;
                // 只在真出现时才显示: 迟到 = WasapiOut 没按时取数据(设备侧可能已欠载), 卡顿 = 音频线程被抢占
                string tl = leftProvider.TimingInfo, tr = rightProvider.TimingInfo;
                if (tl != null) s += " 左" + tl;
                if (tr != null) s += " 右" + tr;
                return s;
            }
            catch { return "-"; }
        }

        static string Ppm(double v)
        {
            return v.ToString("+0;-0;0") + "ppm";
        }

        // 停止事件在音频线程触发; stopping 期间(自己停的)一律忽略
        void OnOutputStopped(string side, StoppedEventArgs e)
        {
            if (stopping) return;
            string why = "输出" + side + "声道已停止";
            if (e != null && e.Exception != null) why += ": " + e.Exception.Message;
            else why += "（设备休眠/被独占/已移除）";
            RaiseStalled(why);
        }

        void OnCaptureStopped(StoppedEventArgs e)
        {
            if (stopping) return;
            string why = "捕获已停止";
            if (e != null && e.Exception != null) why += ": " + e.Exception.Message;
            RaiseStalled(why);
        }

        // 只上报第一次: 左右两路往往同时停, 后续还可能连环触发
        void RaiseStalled(string why)
        {
            if (Interlocked.Exchange(ref stallNotified, 1) != 0) return;
            var handler = Stalled;
            if (handler != null) { try { handler(this, why); } catch { } }
            else if (log != null) { try { log("[错误] " + why); } catch { } }  // 无人接管时才自己记, 避免与订阅方重复
        }

        public void Stop()
        {
            stopping = true; // 先置位: 下面的 Stop 会同步触发 Stopped 事件
            if (loopback != null) { try { loopback.StopRecording(); } catch { } }
            if (leftOut != null) { try { leftOut.Stop(); } catch { } }
            if (rightOut != null) { try { rightOut.Stop(); } catch { } }
            if (leftLine != null) leftLine.Clear();
            if (rightLine != null) rightLine.Clear();
        }

        static void TrimTo(BufferedWaveProvider buf, int targetBytes)
        {
            int excess = buf.BufferedBytes - targetBytes;
            if (excess <= 0) return;
            var tmp = new byte[4096];          // 固定小缓冲: 原先按 excess 分配, 起播时一次几百 KB
            while (excess > 0)
            {
                int got = buf.Read(tmp, 0, Math.Min(excess, tmp.Length));
                if (got <= 0) break;
                excess -= got;
            }
        }

        // Split 输出缓冲复用: 只在采样线程(DataAvailable→emit)访问, 无并发
        byte[] splitOut;
        float[] splitF;
        float[] splitSrcF;

        void Split(byte[] data, int offset, int bytes, int channel, BufferedWaveProvider target)
        {
            var f = loopback.WaveFormat;
            int bps = f.BitsPerSample / 8;
            int ch = f.Channels;
            int frame = bps * ch;
            if (frame <= 0 || bytes < frame) return;
            // 单声道捕获无第二声道 → 取模回 ch0 重复 (越界读会抛异常杀死进程)
            if (ch > 0) channel = channel % ch;
            int frames = bytes / frame;

            // 复用缓冲: 旧实现每样本 BitConverter.GetBytes 分配 byte[4] (~9.6万次/秒 → GC 抖动)
            if (splitOut == null || splitOut.Length < frames * 4) splitOut = new byte[frames * 4];
            if (splitF == null || splitF.Length < frames) splitF = new float[frames];

            bool isFloat = f.Encoding == WaveFormatEncoding.IeeeFloat;
            bool is16 = f.BitsPerSample == 16;
            if (isFloat && f.BitsPerSample == 32)
            {
                // 常用路径 (WASAPI 混音格式): 整块复制到 float[] 后按帧取声道,
                // 避免逐样本 BitConverter.ToSingle (与逐样本读取结果完全一致)
                int total = frames * ch;
                if (splitSrcF == null || splitSrcF.Length < total) splitSrcF = new float[total];
                Buffer.BlockCopy(data, offset, splitSrcF, 0, total * 4);
                for (int i = 0; i < frames; i++)
                    splitF[i] = splitSrcF[i * ch + channel];
            }
            else if (is16)
            {
                for (int i = 0; i < frames; i++)
                    splitF[i] = BitConverter.ToInt16(data, offset + i * frame + channel * bps) / 32768f;
            }
            else if (isFloat)
            {
                // 非 32bit 浮点: 保持原逐样本逻辑
                for (int i = 0; i < frames; i++)
                    splitF[i] = BitConverter.ToSingle(data, offset + i * frame + channel * bps);
            }
            else
            {
                for (int i = 0; i < frames; i++) splitF[i] = 0f;
            }
            Buffer.BlockCopy(splitF, 0, splitOut, 0, frames * 4);
            // 输出缓冲已满 → DiscardOnBufferOverflow 会整块静默丢弃 (听起来是断音), 限流记一条
            int need = frames * 4;
            if (target.BufferedBytes + need > target.BufferLength && WarnOverflow())
                log("[警告] 输出缓冲已满 (" + (target == leftBuf ? "左" : "右") + "声道 " + target.BufferedBytes + "/" +
                    target.BufferLength + " 字节)，本块已丢弃；输出设备可能卡顿或时钟校正跟不上");
            target.AddSamples(splitOut, 0, need);
        }

        // 溢出告警限流: 同一类事件 60 秒最多一条, 避免采样线程刷屏
        bool WarnOverflow()
        {
            int now = Environment.TickCount;
            if (lastOverflowWarnTick != 0 && now - lastOverflowWarnTick < 60000) return false;
            lastOverflowWarnTick = now;
            return true;
        }

        public void Dispose()
        {
            // 幂等: GiveUp / StopEngine / FormClosing 三条路径都会走到, 重复释放会抛
            // ObjectDisposedException, 而调用方多为 catch{} 兜底 → 真实故障被掩盖
            if (disposed) return;
            disposed = true;

            try { Stop(); } catch { }

            if (loopback != null) { try { loopback.Dispose(); } catch { } loopback = null; }
            // 采集线程已退出 (StopRecording 等待其结束) → 此时释放等待句柄无竞态
            if (dataReady != null)
            {
                try { dataReady.Close(); } catch { }
                dataReady = null;
            }
            if (leftOut != null) { try { leftOut.Dispose(); } catch { } leftOut = null; }
            if (rightOut != null) { try { rightOut.Dispose(); } catch { } rightOut = null; }
            if (captureDev != null)
            {
                try { captureDev.Dispose(); } catch { }
                captureDev = null;
            }
            // 左右设备由本引擎自建 → 由本引擎释放 (原 UI 所有的设备不再传入)
            if (leftDev != null)
            {
                try { leftDev.Dispose(); } catch { }
                leftDev = null;
            }
            if (rightDev != null)
            {
                try { rightDev.Dispose(); } catch { }
                rightDev = null;
            }
        }
    }
}
