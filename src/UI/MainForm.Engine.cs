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
    partial class MainForm
    {
        void BtnStart_Click(object sender, EventArgs e)
        {
            if (running)
            {
                StopEngine();
                return;
            }

            if (cmbDefault.SelectedIndex < 0)
            {
                MessageBox.Show("请选择捕获输出设备。", "提示");
                return;
            }

            // 记录点开始时的系统默认输出 (启动失败时回滚 AutoConfigure 的修改)
            preStartDefaultId = catalog.DefaultRenderId();

            SetAppState(AppState.Starting);

            // 捕获主音量 UI 当前值 (AutoConfigure 的 Bind 会用设备真值回填覆盖, 须提前存下)
            snapM = volMaster.Value;
            snapMm = chkMuteMaster.Checked;
            snapMc = chkMuteCapture.Checked;
            hasSnap = true;

            // 点击开始时自动配置: 设默认输出 + 绑定音量 + 自动选左右 + 回环检查
            // 同步跑在 UI 线程, 含 3 次 SetDefaultEndpoint (100~500ms) → 先显示等待光标再执行
            var prevCursor = Cursor;
            bool cfgOk;
            Cursor = Cursors.WaitCursor;
            Update();                       // 只重绘, 不处理输入 (DoEvents 会有重入风险)
            try { cfgOk = AutoConfigure(); }
            finally { Cursor = prevCursor; }

            if (!cfgOk)
            {
                StartFailed();
                MessageBox.Show("自动配置失败，已中止启动。\n详见日志。", "启动中止");
                return;
            }

            if (models.Count == 0 || cards.Count == 0)
            {
                StartFailed();
                MessageBox.Show("没有可用的输出卡片，请先添加至少一张输出卡片。", "提示");
                return;
            }
            // 每张卡片（= 一台设备）都必须选好设备; 同设备多路已合并成一张多声道卡片
            for (int i = 0; i < cards.Count && i < models.Count; i++)
            {
                if (cards[i].cmbDevice.SelectedIndex < 0)
                {
                    StartFailed();
                    MessageBox.Show("第 " + (i + 1) + " 张卡片（" + CardRole(models[i]) +
                        "）还没有选择设备。", "提示");
                    return;
                }
            }

            int defIdx = catalog.IndexOf(catalog.DefaultRenderId());
            // 回环检查 (逐张): 指向捕获设备 → 采集自己的输出, 立刻啸叫
            for (int i = 0; i < cards.Count && i < models.Count; i++)
            {
                if (cards[i].cmbDevice.SelectedIndex != defIdx) continue;
                var others = new List<string>();
                for (int k = 0; k < Devices.Count; k++)
                    if (k != defIdx) others.Add("[" + k + "] " + Devices[k].FriendlyName);
                StartFailed();
                MessageBox.Show(
                    "第 " + (i + 1) + " 张卡片（" + CardRole(models[i]) + "）指向了捕获设备 [" + defIdx + "] " +
                    Devices[defIdx].FriendlyName + "，会造成声音回环！\n\n" +
                    "请把「捕获(虚拟)」改为虚拟设备(如 CABLE Input)，或给这张卡片换别的输出。\n其它可选:\n" +
                    string.Join("\n", others),
                    "设备冲突");
                return;
            }

            try
            {
                // 恢复被 Bind 回填覆盖的 UI 值, 以 UI 为唯一权威源写入端点
                RestoreUIValues(snapM, snapMm, snapMc);
                WriteUIValuesToDevice();

                // 预置运行期设备ID: 引擎在后台完成前 runCaptureId 还是空, Starting 期拔出会漏警
                // (AutoConfigure 已把系统默认设为 cmbDefault 项 → 捕获源即该设备; 引擎完成回调里再以 eng.CaptureId 校正)
                runCaptureId = DeviceIdAt(cmbDefault.SelectedIndex);
                FillRunSinkIds();

                autoRestartCount = 0;   // 手动启动 → 重新给自动重建机会
                StartParams params0 = SnapshotStartParams();
                if (!ValidateRouting(params0.Model))
                {
                    StartFailed();
                    MessageBox.Show("路由校验未通过（详见日志里的 [路由] 行），已中止启动。", "启动中止");
                    return;
                }
                LaunchEngine(runCaptureId, params0, false);
            }
            catch (Exception ex)
            {
                // 参数快照/入队阶段失败 (同步路径)
                Log("[错误] 启动失败: " + ex.Message);
                StartFailed();
                MessageBox.Show("启动失败: " + ex.Message, "错误");
            }
        }

        // 启动参数快照: 引擎 Start 在后台线程跑, UI 值必须先在本线程拷出来
        class StartParams
        {
            public int PreMs, OutMs, GainPct, Mode, FixedPpm;
            public float Gain, Master;
            /// <summary>界面 → 内核的翻译结果 (每路设备/延迟/绑定增益都在里面, 后台线程只读)。</summary>
            public RoutingModel Model;
        }

        StartParams SnapshotStartParams()
        {
            int prof = cmbLatency.SelectedIndex < 0 ? 0 : cmbLatency.SelectedIndex;
            return new StartParams
            {
                PreMs = LatencyProfiles[prof][0],
                OutMs = LatencyProfiles[prof][1],
                Gain = pendingGain,
                Master = CurrentMaster(),
                GainPct = (int)Math.Round(pendingGain * 100),
                Mode = driftMode,
                FixedPpm = fixedPpm,
                Model = BuildModelFromLanes(0, true)
            };
        }

        /// <summary>
        /// 界面 → 内核模型 (启动与热更新的唯一入口)。
        /// <para>srcCh: 捕获源声道数; 0 = 尚未启动, 按立体声假设建抽头。</para>
        /// </summary>
        RoutingModel BuildModelFromLanes(int srcCh, bool logDelayConflict)
        {
            SyncLanesFromCards();       // 控件是权威源
            bool conflict;
            var model = LaneRouting.Build(lanes, cmbSpec.SelectedIndex, srcCh,
                delegate (string id) { return catalog == null ? "" : catalog.NameOf(id); },
                out conflict);
            if (conflict && logDelayConflict)
                Log("[提示] 同一设备上的多路延迟不一致，已按最小值生效（内核每台设备只有一条延迟线）");
            return model;
        }

        /// <summary>
        /// 启动前的路由校验: Error 挡住启动, Warn/Info 只记日志。
        /// 校验两遍: 设备层 (空设备/指向捕获设备/端点声道数不足) + 声道层 (绑定越界/未分配/削波)。
        /// </summary>
        bool ValidateRouting(RoutingModel model)
        {
            var issues = model.ValidateSinks(DeviceIdAt(cmbDefault.SelectedIndex), EndpointChannels);
            // srcCh 传 0: 启动前拿不到捕获格式, 此时判抽头越界会把合法的 6ch 配置全误报
            issues.AddRange(model.Validate(0, model.EnabledSinks()));

            bool hasError = false;
            for (int i = 0; i < issues.Count; i++)
            {
                switch (issues[i].Severity)
                {
                    case IssueSev.Error: hasError = true; Log("[错误] [路由] " + issues[i].Message); break;
                    case IssueSev.Warn: Log("[警告] [路由] " + issues[i].Message); break;
                    default: Log("[提示] [路由] " + issues[i].Message); break;
                }
            }
            int en = model.EnabledSinks().Count;
            Log("[配置] 路由: " + model.DescribeConfig() + " / " + en + " 台设备");
            for (int i = 0; i < model.SinkSpecs.Count; i++)
            {
                var s = model.SinkSpecs[i];
                if (!s.Enabled) continue;
                Log("[配置] " + s.Label + " → " + FindNameById(s.DeviceId) + " (" + s.OutChannels +
                    "ch, 延迟 " + s.DelayMs + "ms)");
            }
            return !hasError;
        }

        /// <summary>端点实际输出声道数 (0 = 拿不到, 交由内核与端点协商时报错)。</summary>
        int EndpointChannels(string id)
        {
            if (catalog == null || string.IsNullOrEmpty(id)) return 0;
            MMDevice d = null;
            try
            {
                d = catalog.Open(id);
                return d == null ? 0 : d.AudioClient.MixFormat.Channels;
            }
            catch { return 0; }
            finally { if (d != null) { try { d.Dispose(); } catch { } } }
        }

        /// <summary>本次运行打开的全部输出端点 (= 启用卡片的设备)。</summary>
        void FillRunSinkIds()
        {
            runSinkIds.Clear();
            SyncLanesFromCards();
            for (int i = 0; i < models.Count; i++)
            {
                string id = models[i].DeviceId;
                if (!string.IsNullOrEmpty(id) && !runSinkIds.Contains(id)) runSinkIds.Add(id);
            }
        }

        /// <summary>
        /// 运行中热替换路由矩阵 (声道抽头/静音/音量增益): 重新 Compile 并逐路原子换引用,
        /// 不停链路、不断音。
        /// <para>设备与延迟是**链路级**参数 (每台设备一条 WasapiOut + 一条延迟线), 改它们只能
        /// 重新开始 —— 这里比对链路形状后给出明确提示, 而不是悄悄换一半。</para>
        /// </summary>
        void ApplyRoutingHot(string reason, bool quiet = false)
        {
            var eng = engine;
            if (!running || eng == null) return;
            int srcCh = 0;
            try { srcCh = eng.CaptureChannels; } catch { }

            var model = BuildModelFromLanes(srcCh, false);
            if (runModel != null && !SameLinkShape(runModel, model))
            {
                Log("[提示] 设备或延迟已改动，需「停止 → 开始」才会生效（当前链路保持不变）");
                return;
            }

            bool ok = false;
            try { ok = eng.SetRouting(model); }
            catch (Exception ex) { Log("[错误] 路由热更新失败: " + ex.Message); }

            if (ok)
            {
                runModel = model;
                if (!quiet)
                    Log("[设置] 路由已热更新 (" + reason + "): " + model.DescribeConfig() +
                        (srcCh > 0 ? " / 源 " + RoutingModel.DescribeSource(srcCh) : ""));
            }
            else if (!quiet)
            {
                Log("[提示] 路由热更新未生效（与当前链路的声道数不匹配），需停止后重新开始");
            }
        }

        /// <summary>链路形状是否一致: 路数、设备、每路声道数、延迟。</summary>
        static bool SameLinkShape(RoutingModel a, RoutingModel b)
        {
            var sa = a.EnabledSinks();
            var sb = b.EnabledSinks();
            if (sa.Count != sb.Count) return false;
            for (int i = 0; i < sa.Count; i++)
            {
                if (sa[i].DeviceId != sb[i].DeviceId) return false;
                if (sa[i].OutChannels != sb[i].OutChannels) return false;
                if (sa[i].DelayMs != sb[i].DelayMs) return false;
            }
            return true;
        }

        // 手动启动与自动重建共用同一条启动链 (差别只在失败处理: 自动重建不弹框、不自动回滚)
        void LaunchEngine(string expectCap, StartParams p, bool autoRestart)
        {
            startAbort = false;
            startFinished.Reset();
            ThreadPool.QueueUserWorkItem(delegate
            {
                AudioEngine eng = null;
                try
                {
                    if (startAbort) throw new OperationCanceledException("启动已取消");
                    // 设备全部写在 p.Model 里 (每台设备一路 sink), 引擎不再单独接收左右 ID
                    eng = new AudioEngine(Log, p.Mode, p.FixedPpm);
                    AudioEngine.StartOutcome outcome = eng.Start(p.Model, p.Gain, p.Master, p.PreMs, p.OutMs,
                        delegate { return startAbort; }, expectCap);

                    if (exiting || IsDisposed || !IsHandleCreated)
                    {   // 关窗等待期间完成 → 只释放引擎, 不碰UI
                        try { eng.Dispose(); } catch { }
                        return;
                    }
                    BeginInvoke(new Action(delegate
                    {
                        if (exiting) { try { eng.Dispose(); } catch { } return; }
                        engine = eng;
                        // 订阅放在赋值之后: 处理函数会按引用校验"还是不是当前引擎", 防止旧引擎的迟到事件触发二次重建
                        eng.Stalled += OnEngineStalledFromAudio;
                        // CaptureId getter 在 COM 对象失效时会 catch 后返回 null —— 不能让 null 覆盖
                        // 启动时已知的正确值 (103 行), 否则断流自动重建会因 runCaptureId 为空而放弃
                        string startedCap = eng.CaptureId;
                        if (!string.IsNullOrEmpty(startedCap)) runCaptureId = startedCap;
                        runModel = p.Model;
                        ResetWatchdog();
                        runSinceTick = Environment.TickCount;
                        SetAppState(AppState.Running);
                        ApplyVolumes();
                        Log("[启动] 端点绑定: 主=" + DevInfo(bindings.Master) + SinkSummary());
                        Log("[启动] 分流已启动 " + eng.SinkCount + " 路 (增益 " + p.GainPct + "%)" +
                            (outcome == AudioEngine.StartOutcome.NoAudioYet ? "，等待音频数据" : ""));
                        // 捕获格式只有启动后才知道: 拿到真实声道数就重建一次模型并热替换矩阵,
                        // 否则"按立体声建抽头"的假设会让真 5.1 源的中置/低音/环绕全部取错
                        ApplyRoutingHot("启动校源");
                        if (autoRestart)
                        {
                            restarting = false;
                            Log("[恢复] 自动重建完成，分流继续");
                        }
                    }));
                }
                catch (Exception ex)
                {
                    try { if (eng != null) eng.Dispose(); } catch { }
                    if (exiting || IsDisposed || !IsHandleCreated) return;
                    string reason = AudioErrors.Describe(ex);
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (exiting) return;
                            Log("[错误] 启动失败: " + reason);
                            if (autoRestart)
                            {
                                restarting = false;
                                GiveUp();   // 重建失败 → 别再假装运行, 明确停下来
                                Log("[错误] 自动重建失败，分流已停止；请检查设备后手动开始");
                                return;
                            }
                            StartFailed();
                            if (!(ex is OperationCanceledException))
                                MessageBox.Show("启动失败: " + reason, "错误");
                        }));
                    }
                    catch { }
                }
                finally { startFinished.Set(); }
            });
        }

        void StopEngine()
        {
            TearDownEngine();
            if (state == AppState.Running || state == AppState.Starting)
            {
                SetAppState(AppState.Stopped);
                Log("[停止] 分流已停止。");
            }
            UnbindAllVolumeSync();
            ClearRunState();
            expectCaptureId = null;
            warnedLostIds.Clear();
            autoRestartCount = 0;
            restarting = false;
            // 停止后还原系统默认输出: 不还原的话默认还挂在虚拟声卡上, 全系统无声
            // (退出路径有自己的 restoreDefaultId 还原, 这里跳过避免二次切换)
            if (!exiting) RestoreDefaultOutput("停止分流");
        }

        // 只拆引擎, 不动状态/绑定/设备ID (自动重建要沿用这些)
        void TearDownEngine()
        {
            var eng = engine;
            engine = null;
            if (eng == null) return;
            try { eng.Stop(); } catch { }
            try { eng.Dispose(); } catch { }
        }

        // 彻底放弃: 拆引擎 + 转失败态 + 解绑, 与 StopEngine 的区别是不打"用户主动停止"的日志
        void GiveUp()
        {
            TearDownEngine();
            SetAppState(AppState.Failed);
            UnbindAllVolumeSync();
            ClearRunState();
            expectCaptureId = null;
            warnedLostIds.Clear();
            autoRestartCount = 0;
            restarting = false;
            // 与 StopEngine 同理: 链路已死, 系统默认必须还给真实设备, 否则全系统无声
            if (!exiting) RestoreDefaultOutput("分流已中断");
        }

        // ---------------- 长期运行看门狗 ----------------

        void ResetWatchdog()
        {
            watchRendered = new long[0];
            watchRenderedTick = new int[0];
            watchImbalanceTick = new int[0];
            captureIdleLogged = false;
            captureIdleSince = 0;
            lastCaptureCheckTick = 0;
        }

        /// <summary>看门狗数组按当前路数扩容 (-1 = "还没取到第一个读数")。</summary>
        void EnsureWatchArrays(int n)
        {
            if (watchRendered.Length == n) return;
            watchRendered = new long[n];
            watchRenderedTick = new int[n];
            watchImbalanceTick = new int[n];
            for (int i = 0; i < n; i++) watchRendered[i] = -1;
        }

        static string SinkLabel(AudioEngine eng, int i)
        {
            var rt = eng == null ? null : eng.SinkAt(i);
            if (rt != null && rt.Spec != null && !string.IsNullOrEmpty(rt.Spec.Label)) return rt.Spec.Label;
            return "第" + (i + 1) + "路";
        }

        static string PpmText(double v)
        {
            return v.ToString("+0;-0;0") + "ppm";
        }

        /// <summary>
        /// 每 2 秒判定一次链路活性。核心判据是"输出线程有没有被调用"(RenderedSamples 是否增长),
        /// 而不是水位高低 —— 源静音时水位本来就该是 0, 用水位判会天天误报。
        /// 逐路判定: 总和会把某一路停摆掩盖掉 (其它路照常增长)。
        /// </summary>
        void WatchdogTick()
        {
            // 设备变更监听回调 (WASAPI 通知线程) 曾抛异常: 只在那边计数, 这里读走并落日志
            if (defaultWatcher != null && defaultWatcher.CallbackErrors > 0)
            {
                Log("[警告] 设备变更监听回调异常 " + defaultWatcher.CallbackErrors + " 次（已恢复监听）");
                defaultWatcher.ResetCallbackErrors();
            }

            var eng = engine;
            if (!running || eng == null) return;
            int now = Environment.TickCount;

            // 连续健康 5 分钟 → 清空重建计数, 避免一天里偶发两三次后就被永久锁死在"不再重试"
            if (autoRestartCount > 0 && runSinceTick != 0 && unchecked(now - runSinceTick) >= 300000)
                autoRestartCount = 0;

            // 10 秒不被调用 → 该路输出端已停摆 (设备休眠/被独占/驱动挂起且没抛事件)
            int sinks = eng.SinkCount;
            EnsureWatchArrays(sinks);
            for (int i = 0; i < sinks; i++)
            {
                var rt = eng.SinkAt(i);
                if (rt == null || rt.Provider == null) continue;
                long r = rt.Provider.RenderedSamples;
                if (r != watchRendered[i]) { watchRendered[i] = r; watchRenderedTick[i] = now; }
                else if (watchRenderedTick[i] != 0 && unchecked(now - watchRenderedTick[i]) >= WatchdogFreezeMs)
                {
                    OnEngineStalled("输出「" + SinkLabel(eng, i) + "」线程已 " + (WatchdogFreezeMs / 1000) +
                        " 秒未取数据（水位 " + rt.Provider.BufferedMs + "ms）—— 该设备休眠/被独占的典型表现");
                    return;
                }
            }

            // 水位失衡检测: 设备实际采样率与标称偏差 10%+ 时, 远超校正环 ±0.5% 修正范围 →
            // 该路水位持续堆积到满。此时 RenderedSamples 仍在涨 (Read 还被调用, 只是消费速率
            // 不匹配), 停摆检测抓不到。判据: 调节水位 >300ms (正常 ≤142ms) 且该路校正触顶
            // (|ppm|>4900), 持续 10 秒。逐路判定: 总和会互相掩盖。
            for (int i = 0; i < sinks; i++)
            {
                var rt = eng.SinkAt(i);
                if (rt == null || rt.Provider == null) continue;
                double ppm = rt.Provider.RatioPpm;
                int reg = rt.Provider.RegulatedMs;
                bool clamped = ppm > 4900 || ppm < -4900;
                if (!clamped || reg <= ImbalanceThresholdMs) { watchImbalanceTick[i] = 0; continue; }
                if (watchImbalanceTick[i] == 0) { watchImbalanceTick[i] = now; continue; }
                if (unchecked(now - watchImbalanceTick[i]) < WatchdogImbalanceMs) continue;
                OnEngineStalled("「" + SinkLabel(eng, i) + "」水位持续堆积至 " + reg + "ms 且校正已触顶 " +
                    PpmText(ppm) + " 超过 10 秒——该设备消费速率严重低于捕获速率，校正环无法补偿");
                return;
            }

            int capTick = eng.LastCaptureTick;
            // 1.5s 阈值: 播放时回调约每 10ms 一次, 1.5s 无回调必为停流; 原 3s 会漏掉 2~3 秒的暂停
            bool captureActive = capTick != 0 && unchecked(now - capTick) < 1500;
            if (captureActive)
            {
                // 恢复补记: 此前若静默超过 2 秒, 记一条恢复事件 (检测粒度 2s, 时长为近似值)
                // —— 没有这条, "应用暂停→恢复"在日志里完全隐形, 会被误判成无故欠载
                if (captureIdleSince != 0 && unchecked(now - captureIdleSince) >= 2000)
                    Log("[状态] 捕获源恢复数据（此前静默约 " + (unchecked(now - captureIdleSince) / 1000) + " 秒）");
                captureIdleLogged = false;
                captureIdleSince = 0;
                lastCaptureCheckTick = 0;
                return;
            }

            // 捕获端没数据: 多数情况只是应用暂停/静音 (此时欠载计数会增长, 属预期而非故障), 只记一次
            // capTick==0 (从未收到数据) 只置位不打提示: 启动路径有自己的"等待音频数据"日志
            if (captureIdleSince == 0) captureIdleSince = now;
            if (!captureIdleLogged && unchecked(now - captureIdleSince) >= 2000)
            {
                captureIdleLogged = true;
                if (capTick != 0) Log("[状态] 捕获源暂无数据（应用暂停或静音），输出待命");
            }
            // 空闲期顺带确认捕获设备是否还在 (1 分钟一次): 设备被移除时引擎不会自杀, 只有这里能发现
            if (captureIdleLogged && (lastCaptureCheckTick == 0 || unchecked(now - lastCaptureCheckTick) >= 60000))
            {
                lastCaptureCheckTick = now;
                if (runCaptureId != null && FindMissingEndpoint(runCaptureId) != null)
                    OnEngineStalled("捕获设备已不可用");
            }
        }

        // 引擎在音频线程上报 → 回 UI 线程统一处理
        void OnEngineStalledFromAudio(AudioEngine src, string why)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    // 旧引擎的迟到事件不能触发二次重建
                    if (!ReferenceEquals(src, engine)) return;
                    OnEngineStalled(why);
                }));
            }
            catch { }
        }

        void OnEngineStalled(string why)
        {
            if (exiting || IsDisposed || restarting) return;
            if (state != AppState.Running && state != AppState.Starting) return;

            Log("[错误] " + why + " → 分流链路已中断，进入自动恢复流程");

            if (autoRestartCount >= MaxAutoRestart)
            {
                Log("[错误] 已连续自动重建 " + autoRestartCount + " 次仍未恢复，停止重试；请检查设备后手动开始");
                GiveUp();
                return;
            }
            autoRestartCount++;
            Log("[恢复] 自动重建输出流 (" + autoRestartCount + "/" + MaxAutoRestart + ")");
            RestartEngine();
        }

        /// <summary>按当前运行参数重建整条链路 (不断开音量绑定, 不重跑 AutoConfigure)。</summary>
        void RestartEngine()
        {
            if (exiting || IsDisposed) return;
            string capId = runCaptureId;

            if (string.IsNullOrEmpty(capId) || runSinkIds.Count == 0)
            {
                // 运行期设备信息意外缺失 (2026-11-17 实测发生过): UI 仍持有权威状态, 先尝试就地恢复,
                // 恢复不了才放弃。恢复逻辑与 BtnStart_Click 相同: 捕获=当前默认选择, 输出=各卡片设备。
                Log("[恢复] 运行期设备信息缺失 (捕获ID=" + (capId ?? "空") +
                    ", 输出端点=" + runSinkIds.Count + " 个) → 尝试按当前界面状态恢复");
                try
                {
                    capId = DeviceIdAt(cmbDefault.SelectedIndex);
                    FillRunSinkIds();
                }
                catch (Exception ex)
                {
                    Log("[错误] 按界面恢复设备信息失败: " + ex.Message);
                }
                if (string.IsNullOrEmpty(capId) || runSinkIds.Count == 0)
                {
                    Log("[错误] 无法自动重建（捕获/输出设备信息仍缺失）；请手动重新开始");
                    GiveUp();
                    return;
                }
                runCaptureId = capId;
                Log("[恢复] 已恢复运行期设备信息: 捕获 1 台 + 输出 " + runSinkIds.Count + " 台");
            }
            string missing = FindMissingEndpoint(runSinkIds.ToArray());
            if (missing != null)
            {
                Log("[错误] 输出设备已不可用 (" + FindNameById(missing) + ")，放弃自动重建；请刷新设备后重新开始");
                GiveUp();
                return;
            }

            // 按当前 UI 重新翻译 (运行期卡片的结构控件是锁定的, 内容与上次启动一致;
            // 音量/静音可能被调过, 重建时一并带过去)
            StartParams p;
            try { p = SnapshotStartParams(); }
            catch (Exception ex)
            {
                Log("[错误] 重建参数生成失败: " + ex.Message);
                GiveUp();
                return;
            }

            restarting = true;
            TearDownEngine();                 // 起播水位/缓冲全部重来, 与首次启动一致
            SetAppState(AppState.Starting);   // 期间 running=false → 看门狗自然停手
            LaunchEngine(capId, p, true);
        }

        // 一次枚举校验多个端点是否仍活动; 返回第一个缺失的 ID, 全部在位返回 null。
        // 原实现每次只查一个 ID, 重建路径要枚举两遍; 枚举失败一律按"都在"处理, 避免误判触发重建。
        static string FindMissingEndpoint(params string[] ids)
        {
            if (ids == null || ids.Length == 0) return null;
            for (int i = 0; i < ids.Length; i++)
                if (string.IsNullOrEmpty(ids[i])) return ids[i] ?? "";   // 空 ID 与旧 IsEndpointActive 语义一致: 视为缺失

            var missing = new List<string>(ids);
            try
            {
                using (var enumerator = new MMDeviceEnumerator())
                {
                    var coll = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                    // MMDeviceCollection 本身无 Dispose; 索引器取出的 MMDevice 是新 RCW, 须逐个释放
                    for (int i = 0; i < coll.Count && missing.Count > 0; i++)
                    {
                        var d = coll[i];
                        try
                        {
                            string id = d.ID;
                            if (id != null) missing.Remove(id);
                        }
                        finally { try { d.Dispose(); } catch { } }
                    }
                }
            }
            catch { return null; }
            return missing.Count > 0 ? missing[0] : null;
        }

        void OnPowerModeChanged(object sender, Microsoft.Win32.PowerModeChangedEventArgs e)
        {
            if (e.Mode != Microsoft.Win32.PowerModes.Resume) return;
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (!running || exiting) return;
                    Log("[恢复] 系统已从休眠唤醒，等待设备就绪后重建音频流");
                    powerTimer.Stop();
                    powerTimer.Start();
                }));
            }
            catch { }
        }

        float CurrentMaster()
        {
            return chkMuteMaster.Checked ? 0f : volMaster.Value / 100f;
        }
    }
}