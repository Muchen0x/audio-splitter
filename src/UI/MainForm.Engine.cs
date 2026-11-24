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
using StereoSplitter.Infrastructure;
using StereoSplitter.Model;

namespace StereoSplitter
{
    partial class MainForm
    {
        void BtnStart_Click(object sender, EventArgs e)
        {
            // 过渡态防重复触发: 按钮本身已置为不可点 (见 ApplyUiLock), 但托盘菜单、快捷键、
            // 程序化调用仍可能打到这条路径 —— 启动中再点一次会重复 AutoConfigure 并第二次建链路。
            if (state == AppState.Starting || state == AppState.Stopping)
            {
                Log("[提示] 正在" + (state == AppState.Starting ? "启动" : "停止") + "，请稍候。");
                return;
            }

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
            // 按钮是独立 HWND, 窗体的 Update() 不会重绘它 —— 这里单点一次, 让"启动中…"
            // 在 AutoConfigure 那段同步耗时之前就落到屏幕上
            btnStart.Update();

            // 捕获主音量 UI 当前值 (AutoConfigure 的 Bind 会用设备真值回填覆盖, 须提前存下)
            snapM = volMaster.Value;
            snapMm = btnMuteMaster.Muted;
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
                // 失败原因直接进弹窗 (用户要求, 不再让用户去翻日志)
                string msg = "自动配置失败，已中止启动。";
                if (!string.IsNullOrEmpty(autoCfgFailReason)) msg += "\n\n原因: " + autoCfgFailReason;
                MessageBox.Show(msg, "启动中止");
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
                List<string> routeErrors;
                if (!ValidateRouting(params0.Model, out routeErrors))
                {
                    StartFailed();
                    // Error 级问题原文直接进弹窗 (用户要求), 日志照旧留全量
                    string msg = "路由校验未通过，已中止启动。";
                    if (routeErrors != null && routeErrors.Count > 0)
                        msg += "\n\n" + string.Join("\n", routeErrors.ToArray());
                    MessageBox.Show(msg, "启动中止");
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
                delegate (string id) { return catalog == null ? -1 : catalog.IndexOf(id); },
                out conflict);
            if (conflict && logDelayConflict)
                Log("[提示] 同一设备上的多路延迟不一致，已按最小值生效（内核每台设备只有一条延迟线）");
            return model;
        }

        /// <summary>
        /// 启动前的路由校验: Error 挡住启动, Warn/Info 只记日志。
        /// 校验两遍: 设备层 (空设备/指向捕获设备/端点声道数不足) + 声道层 (绑定越界/未分配/削波)。
        /// <para>errors 带回全部 Error 级问题 (面向用户的原文) —— 失败弹窗直接展示, 不再让用户翻日志。</para>
        /// </summary>
        bool ValidateRouting(RoutingModel model, out List<string> errors)
        {
            var issues = model.ValidateSinks(DeviceIdAt(cmbDefault.SelectedIndex), EndpointChannels);
            // srcCh 传 0: 启动前拿不到捕获格式, 此时判抽头越界会把合法的 6ch 配置全误报
            issues.AddRange(model.Validate(0, model.EnabledSinks()));

            bool hasError = false;
            errors = new List<string>();
            for (int i = 0; i < issues.Count; i++)
            {
                switch (issues[i].Severity)
                {
                    case IssueSev.Error:
                        hasError = true;
                        Log("[错误] [路由] " + issues[i].Message);
                        errors.Add(issues[i].Message);
                        break;
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

        /// <summary>
        /// 端点实际输出声道数 (0 = 拿不到, 交由内核与端点协商时报错)。
        /// <para>R-05: 结果带 TTL 缓存。调用链是 卡片事件 → NormalizeModels → EnsureSlots →
        /// DeviceChannelCount → 这里, 而 <c>catalog.Open(id)</c> 会真的激活一次音频客户端
        /// (USB/蓝牙端点 10~100ms, 且是 COM 调用)。3 张多声道卡上每松一次音量滑条 = 3 次激活,
        /// 全部压在 UI 线程上。缓存后这条链退化成纯算术。</para>
        /// <para>失效时机: <see cref="RefreshDevices"/> (设备下拉展开前刷新 / 设备列表重建)、
        /// <see cref="HandleDeviceLost"/> (端点消失/停用)、设备属性变化回调
        /// (<c>DefaultChangeWatcher.OnPropertyValueChanged</c>, 覆盖"在 Windows 里改了扬声器配置"),
        /// 另有 <see cref="ChannelCacheMs"/> 的 TTL 兜底。</para>
        /// <para>故意**不**在 <see cref="BindEndpointVolumeSync"/> 里失效: 它是"运行中每松一次
        /// 音量滑条"都会走的方法, 在那儿清缓存等于把缓存关掉; 而声道数是端点自己的属性,
        /// 与我们的音量绑定无关。</para>
        /// </summary>
        int EndpointChannels(string id)
        {
            if (catalog == null || string.IsNullOrEmpty(id)) return 0;

            lock (chanCache)
            {
                int hit, at;
                // 两张表一起查: 只有 chanCache 有值而 tick 缺失时宁可按未命中处理, 不冒 KeyNotFound 的险
                if (chanCache.TryGetValue(id, out hit) && chanCacheTick.TryGetValue(id, out at) &&
                    unchecked(Environment.TickCount - at) < ChannelCacheMs)
                    return hit;
            }

            int n = QueryEndpointChannels(id);

            // 只缓存成功结果: 0 是"这次没读到"(端点正忙/刚插上), 缓存住会把一次瞬时失败
            // 钉死 30 秒 —— 用户看到的正是"多声道卡片少了一行, 怎么点都不回来"。
            if (n > 0)
            {
                lock (chanCache)
                {
                    chanCache[id] = n;
                    chanCacheTick[id] = Environment.TickCount;
                }
            }
            return n;
        }

        /// <summary>真正去打开端点读 MixFormat (唯一会做 COM 激活的地方)。</summary>
        int QueryEndpointChannels(string id)
        {
            MMDevice d = null;
            try
            {
                d = catalog.Open(id);
                return d == null ? 0 : d.AudioClient.MixFormat.Channels;
            }
            catch { return 0; }
            finally { if (d != null) { try { d.Dispose(); } catch { } } }
        }

        /// <summary>丢掉某端点的声道数缓存; id 为空 = 全清。</summary>
        void InvalidateChannelCache(string id)
        {
            lock (chanCache)
            {
                if (string.IsNullOrEmpty(id)) { chanCache.Clear(); chanCacheTick.Clear(); }
                else { chanCache.Remove(id); chanCacheTick.Remove(id); }
            }
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
        /// <para>设备与声道数仍是**链路级**参数 (每台设备一条 WasapiOut, 换设备/换物理声道数
        /// 都要重建), 改它们只能重新开始 —— 这里比对链路形状后给出明确提示, 而不是悄悄换一半。</para>
        /// <para>延迟例外: 它只是延迟线的 FIFO 深度, 不碰端点与矩阵, 于是可以就地热改
        /// (见 <see cref="AudioEngine.ApplyDelays"/>) —— 校准完能立刻听效果。</para>
        /// </summary>
        void ApplyRoutingHot(string reason, bool quiet = false)
        {
            var eng = engine;
            if (!running || eng == null) return;
            int srcCh = 0;
            try { srcCh = eng.CaptureChannels; } catch { }

            var model = BuildModelFromLanes(srcCh, false);
            bool delayHot = false;
            if (runModel != null && !SameLinkShape(runModel, model))
            {
                // 差异只在延迟 → 逐路改延迟线目标, 不必停分流。
                // 设备/声道数变了才是真正的链路变更 (要换端点、换缓冲), 那时才要求停→开始。
                if (OnlyDelayDiffers(runModel, model) && eng.ApplyDelays(model)) delayHot = true;
                else
                {
                    Log("[提示] 设备或声道数已改动，需「停止 → 开始」才会生效（当前链路保持不变）");
                    return;
                }
            }

            bool ok = false;
            try { ok = eng.SetRouting(model); }
            catch (Exception ex) { Log("[错误] 路由热更新失败: " + ex.Message); }

            if (ok)
            {
                runModel = model;
                if (!quiet)
                {
                    // 纯延迟改动只说一句: 矩阵那一句是同一次操作的一半, 分开报反而像发生了两件事
                    if (delayHot) Log("[设置] 延迟已热生效 (" + reason + "): " + DescribeDelays(model));
                    else Log("[设置] 路由已热更新 (" + reason + "): " + model.DescribeConfig() +
                        (srcCh > 0 ? " / 源 " + RoutingModel.DescribeSource(srcCh) : ""));
                }
            }
            else
            {
                // R-14: 延迟这一步确实成功了（ApplyDelays 返回 true），只是矩阵没换成。
                // 不同步 runModel 的延迟字段，下次卡片一改 SameLinkShape 又会判"延迟不同" →
                // 重复走 OnlyDelayDiffers → 重复 ApplyDelays → 重复打同一段提示，
                // 日志里看像"改一次触发两次"，实际第二次什么也没做成。
                if (delayHot && runModel != null) CopyDelaysInto(runModel, model);
                if (!quiet)
                {
                    if (delayHot) Log("[提示] 延迟已生效，但矩阵未刷新（声道数与当前链路不匹配），新配置需停止后重新开始");
                    else Log("[提示] 路由热更新未生效（与当前链路的声道数不匹配），需停止后重新开始");
                }
            }
        }

        /// <summary>
        /// 把 <paramref name="from"/> 的延迟按路键写进 <paramref name="to"/>。
        /// 只用于"延迟已生效、矩阵没换"这一种状态: runModel 表示**链路与矩阵实际生效的样子**,
        /// 延迟线是唯一能脱离矩阵单独生效的参数, 所以这里只同步延迟, 不动声道抽头。
        /// </summary>
        static void CopyDelaysInto(RoutingModel to, RoutingModel from)
        {
            var sa = to.EnabledSinks();
            var sb = from.EnabledSinks();
            if (sa.Count != sb.Count) return;
            for (int i = 0; i < sa.Count; i++)            // 先整体校验键序, 避免改了一半才发现对不上
                if (!string.Equals(sa[i].Key, sb[i].Key, StringComparison.Ordinal)) return;
            for (int i = 0; i < sa.Count; i++) sa[i].DelayMs = sb[i].DelayMs;
        }

        /// <summary>日志用的延迟摘要, 如 "左=0ms / 右=180ms"。</summary>
        static string DescribeDelays(RoutingModel m)
        {
            var sinks = m.EnabledSinks();
            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < sinks.Count; i++)
            {
                if (i > 0) sb.Append(" / ");
                sb.Append(sinks[i].Label).Append('=').Append(sinks[i].DelayMs).Append("ms");
            }
            return sb.ToString();
        }

        /// <summary>两份模型的差异是否**只在延迟**上 (路数、稳定键、设备、声道数完全一致)。</summary>
        static bool OnlyDelayDiffers(RoutingModel a, RoutingModel b)
        {
            var sa = a.EnabledSinks();
            var sb = b.EnabledSinks();
            if (sa.Count != sb.Count || sa.Count == 0) return false;
            bool diff = false;
            for (int i = 0; i < sa.Count; i++)
            {
                if (!string.Equals(sa[i].Key, sb[i].Key, StringComparison.Ordinal)) return false;
                if (!string.Equals(sa[i].DeviceId, sb[i].DeviceId, StringComparison.Ordinal)) return false;
                if (sa[i].OutChannels != sb[i].OutChannels) return false;
                if (sa[i].DelayMs != sb[i].DelayMs) diff = true;
            }
            return diff;
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
            // F-03: 这一代启动的所有回调都带上编号; 期间若又拆了引擎/启动了新的一代,
            // 旧回调在入口就被丢弃, 不会把上一代的结果写进新状态。
            int gen = Interlocked.Increment(ref engineGeneration);
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

                    if (exiting || IsDisposed || !IsHandleCreated || gen != Volatile.Read(ref engineGeneration))
                    {   // 关窗等待期间完成, 或期间又换了一代 → 只释放引擎, 不碰UI
                        try { eng.Dispose(); } catch { }
                        return;
                    }
                    BeginInvoke(new Action(delegate
                    {
                        if (exiting || gen != Volatile.Read(ref engineGeneration))
                        { try { eng.Dispose(); } catch { } return; }
                        engine = eng;
                        // 订阅放在赋值之后: 处理函数会按引用校验"还是不是当前引擎", 防止旧引擎的迟到事件触发二次重建
                        eng.Stalled += OnEngineStalledFromAudio;
                        eng.SinkStalled += OnSinkStalledFromAudio;
                        // CaptureId getter 在 COM 对象失效时会 catch 后返回 null —— 不能让 null 覆盖
                        // 启动时已知的正确值 (103 行), 否则断流自动重建会因 runCaptureId 为空而放弃
                        string startedCap = eng.CaptureId;
                        if (!string.IsNullOrEmpty(startedCap)) runCaptureId = startedCap;
                        runModel = p.Model;
                        ResetWatchdog();
                        sinkRestartCount = new int[0];   // 新链路 → 单路重建预算重新给
                        sinkRestartTick = new int[0];
                        sinkRestarting = new bool[0];
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
                    if (exiting || IsDisposed || !IsHandleCreated || gen != Volatile.Read(ref engineGeneration)) return;
                    string reason = AudioErrors.Describe(ex);
                    try
                    {
                        BeginInvoke(new Action(delegate
                        {
                            if (exiting || gen != Volatile.Read(ref engineGeneration)) return;
                            Log("[错误] 启动失败: " + reason);
                            if (autoRestart)
                            {
                                restarting = false;
                                // 重建失败多半是端点还没就绪 (USB 重新枚举/蓝牙重连): 挂上监听,
                                // 设备回来时自动再试一次 (DeviceReturnTick 触发后会先解除监听, 不会循环)
                                ArmDeviceReturnWatch(runCaptureId, runSinkIds);
                                GiveUp();   // 重建失败 → 别再假装运行, 明确停下来
                                Log("[错误] 自动重建失败，分流已停止；设备回归后会自动续播，也可手动开始");
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
            DisarmDeviceReturnWatch();      // 用户主动停止 → 不做设备回归续播

            // 先亮出"正在停止": TearDownEngine 里的 Stop/Dispose 是同步的 (WASAPI 拆除会等
            // 渲染线程退出), 耗时几十到几百毫秒 —— 期间界面仍显示"停止分流/运行中",
            // 看起来就是"点了没反应", 用户会再点一次。
            bool wasActive = (state == AppState.Running || state == AppState.Starting);
            if (wasActive)
            {
                SetAppState(AppState.Stopping);
                btnStart.Update();          // 同上: 按钮有自己的 HWND, 不点它不会当场重绘
            }

            TearDownEngine();
            if (state == AppState.Stopping)
            {
                SetAppState(AppState.Stopped);
                Log("[停止] 分流已停止。");
            }
            // R-09: 非拆除类"吞异常"的汇总。每次停止打一条 (0 次不打), 平时不出声 ——
            // 它的价值在于: 用户报"偶尔卡一下/偶尔没声"时, 日志里能查到"有几处代码在掩盖异常"。
            string swallowed = Swallow.Summary();
            if (swallowed != null) Log("[诊断] " + swallowed);
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
            // F-03: 先作废旧代的全部后台回调, 再拆引擎。ClearRunState 随后会重置
            // sinkRestarting 数组; 若旧回调先落地就会写进新数组, 递增编号让它们一律被丢弃。
            Interlocked.Increment(ref engineGeneration);
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

        // ---------------- 设备回归自动续播 ----------------

        /// <summary>放弃重建前快照运行参数并开启监听 (5 秒/次)。</summary>
        void ArmDeviceReturnWatch(string capId, System.Collections.Generic.List<string> sinkIds)
        {
            returnCaptureId = capId;
            returnSinkIds = sinkIds != null ? new System.Collections.Generic.List<string>(sinkIds) : new System.Collections.Generic.List<string>();
            if (returnCaptureId == null || returnSinkIds.Count == 0) { DisarmDeviceReturnWatch(); return; }
            if (deviceReturnTimer != null) deviceReturnTimer.Start();
        }

        void DisarmDeviceReturnWatch()
        {
            if (deviceReturnTimer != null) deviceReturnTimer.Stop();
            returnCaptureId = null;
            returnSinkIds = null;
        }

        /// <summary>
        /// 监听 tick: 设备全部回归 → 走与手动点开始完全相同的启动链 (含 AutoConfigure)。
        /// 用户手动停止/手动启动/退出时自动解除。
        /// </summary>
        void DeviceReturnTick()
        {
            if (exiting || IsDisposed || !IsHandleCreated) { DisarmDeviceReturnWatch(); return; }
            if (running || state == AppState.Starting || state == AppState.Running) { DisarmDeviceReturnWatch(); return; }
            if (returnCaptureId == null || returnSinkIds == null || returnSinkIds.Count == 0) { DisarmDeviceReturnWatch(); return; }

            var ids = new string[returnSinkIds.Count + 1];
            ids[0] = returnCaptureId;
            returnSinkIds.CopyTo(ids, 1);
            string stillMissing;
            // R-13: Unknown (枚举失败) 与 Missing 一样按"继续等"处理 —— 绝不能在判断不了的时候自动开流
            if (ProbeEndpoints(out stillMissing, ids) != EndpointProbe.Present) return;

            Log("[恢复] 端点已全部回归 (捕获 + " + returnSinkIds.Count + " 路输出) → 自动续播");
            DisarmDeviceReturnWatch();
            BtnStart_Click(this, EventArgs.Empty);
        }

        // ---------------- 长期运行看门狗 ----------------

        void ResetWatchdog()
        {
            watchRendered = new long[0];
            watchRenderedTick = new int[0];
            watchImbalanceTick = new int[0];
            watchTrims = new long[0];
            watchTrimBurst = new int[0];
            watchTrimLastTick = new int[0];
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
            watchImbalanceReg = new int[n];
            watchImbFloorTick = new int[n];
            watchTrims = new long[n];
            watchTrimBurst = new int[n];
            watchTrimLastTick = new int[n];
            for (int i = 0; i < n; i++) { watchRendered[i] = -1; watchTrims[i] = -1; }
        }

        /// <summary>
        /// 单路重建后清掉该路的看门狗状态: 新 provider 的累计量都从 0 重新记,
        /// 旧值留着会让"削峰计数只增"的判据再也对不上 (watchTrims 停在旧的大数 → 判据③ 永久失效)。
        /// </summary>
        void ResetSinkWatch(int i)
        {
            if (i < 0) return;
            if (i < watchRendered.Length) { watchRendered[i] = -1; watchRenderedTick[i] = 0; }
            if (i < watchImbalanceTick.Length) { watchImbalanceTick[i] = 0; watchImbalanceReg[i] = 0; }
            if (i < watchImbFloorTick.Length) watchImbFloorTick[i] = 0;
            if (i < watchTrims.Length) { watchTrims[i] = -1; watchTrimBurst[i] = 0; watchTrimLastTick[i] = 0; }
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
            if (sinkRestartCount.Length > 0 && runSinceTick != 0 && unchecked(now - runSinceTick) >= 300000)
            {
                Array.Clear(sinkRestartCount, 0, sinkRestartCount.Length);
                Array.Clear(sinkRestartTick, 0, sinkRestartTick.Length);
            }

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
                    OnSinkStalled(i, "输出「" + SinkLabel(eng, i) + "」线程已 " + (WatchdogFreezeMs / 1000) +
                        " 秒未取数据（水位 " + rt.Provider.BufferedMs + "ms）—— 该设备休眠/被独占的典型表现");
                    return;
                }
            }

            // 水位失衡检测 v2: 设备实际消费速率异常 (休眠抖动/驱动重置/端点僵死前兆) 时,
            // RenderedSamples 仍在涨 (Read 还被调用, 只是消费速率不匹配), 停摆检测抓不到。
            // 两条判据, 逐路判定 (总和会互相掩盖):
            // ① 趋势: 校正触顶 (|ppm|>9900) 持续 5 秒且水位比窗口起点净涨 ≥10ms。
            //    起播预缓冲放水阶段校正同样触顶, 但水位在下降 → 不触发。
            // ② 兜底: 调节水位 >200ms 持续 10 秒 (正常 ≤110ms)。
            for (int i = 0; i < sinks; i++)
            {
                var rt = eng.SinkAt(i);
                if (rt == null || rt.Provider == null) continue;
                double ppm = rt.Provider.RatioPpm;
                int reg = rt.Provider.RegulatedMs;
                int targetMs = rt.Provider.TargetMs;
                bool clamped = ppm > ClampDetectPpm || ppm < -ClampDetectPpm;

                // 判据③: provider 靠削峰维持 = 已经在自愈。单次属瞬态 (USB 等时抖动/端点挂起),
                // 一分钟内反复出现才说明设备消费速率真的不够。
                long trims = rt.Provider.TrimCount;
                if (watchTrims[i] < 0) watchTrims[i] = trims;
                else if (trims > watchTrims[i])
                {
                    watchTrims[i] = trims;
                    // F-37: 真正的 60 秒滚动窗口 —— 判定抽到 BurstWindowTick 纯函数(供自检)。
                    // 旧实现用"距上次削峰是否超 60s"清零, 导致 8 次分布在数分钟(每次间隔都<60s)
                    // 也触发, 日志却谎称"60 秒内 8 次"。窗口起点记录真实跨度用于日志。
                    int windowStart = watchTrimLastTick[i];
                    int burst = watchTrimBurst[i];
                    bool tripped = BurstWindowTick(now, ref windowStart, ref burst);
                    watchTrimLastTick[i] = windowStart;
                    watchTrimBurst[i] = burst;
                    if (tripped)
                    {
                        int spanSec = (int)(unchecked(now - windowStart) / 1000);
                        OnSinkStalled(i, "「" + SinkLabel(eng, i) + "」持续跟不上：" + spanSec +
                            " 秒内削峰 " + burst + " 次，水位反复堆到目标 " + targetMs +
                            "ms 的 2 倍——该设备消费速率严重低于捕获速率");
                        return;
                    }
                }

                // 判据②: 水位绝对线, 不要求触顶 (捕捉触顶与越线交替出现的震荡期)。
                // 阈值随目标浮动: 高延迟档位目标本身就有 100ms, 写死 200ms 会把它当故障。
                int floorMs = Math.Max(ImbalanceThresholdMs, targetMs * 5 / 2);
                if (reg > floorMs)
                {
                    if (watchImbFloorTick[i] == 0) watchImbFloorTick[i] = now;
                    else if (unchecked(now - watchImbFloorTick[i]) >= WatchdogImbalanceMs)
                    {
                        OnSinkStalled(i, "「" + SinkLabel(eng, i) + "」水位持续 " + reg + "ms 超过 " +
                            (WatchdogImbalanceMs / 1000) + " 秒（目标 " + targetMs + "ms），远超正常范围——该设备消费速率异常");
                        return;
                    }
                }
                else watchImbFloorTick[i] = 0;

                // 判据①: 刚削过峰说明是"卡了一下又被压回来", 交给判据③ 计数, 这里不判死
                if (rt.Provider.SinceLastTrimMs < TrimGraceMs) { watchImbalanceTick[i] = 0; continue; }

                // 判据①: 触顶 + 水位净涨 (起播放水触顶但水位下降, 滑窗重计不会误触发)
                if (!clamped) { watchImbalanceTick[i] = 0; continue; }
                if (watchImbalanceTick[i] == 0)
                {
                    watchImbalanceTick[i] = now;
                    watchImbalanceReg[i] = reg;
                    continue;
                }
                if (unchecked(now - watchImbalanceTick[i]) < WatchdogTrendMs) continue;
                if (reg - watchImbalanceReg[i] >= ImbalanceGrowMs)
                {
                    OnSinkStalled(i, "「" + SinkLabel(eng, i) + "」校正触顶 " + PpmText(ppm) + " 持续 " +
                        (WatchdogTrendMs / 1000) + " 秒且水位净涨 " + (reg - watchImbalanceReg[i]) + "ms（" +
                        watchImbalanceReg[i] + "→" + reg + "ms）——该设备消费速率严重低于捕获速率");
                    return;
                }
                // 触顶但水位没涨 (放水/瞬态): 窗口滑动重新计时
                watchImbalanceTick[i] = now;
                watchImbalanceReg[i] = reg;
            }

            // 输出端点存活探测 (30 秒一次): 设备被系统禁用/驱动重置后 WasapiOut 不抛事件,
            // 数据写进黑洞但 RenderedSamples 照涨, 水位也正常 —— 只有枚举端点才能发现。
            if (lastSinkProbeTick == 0 || unchecked(now - lastSinkProbeTick) >= 30000)
            {
                lastSinkProbeTick = now;
                var sinkIds = eng.SinkEndpointIds();
                string missingSink;
                EndpointProbe sinkProbe = ProbeEndpoints(out missingSink, sinkIds);
                if (sinkProbe == EndpointProbe.Unknown)
                {
                    // 30 秒一次的探测, 失败时只提示一次, 免得 COM 长时间不可用时刷屏
                    if (!sinkProbeWarned)
                    {
                        sinkProbeWarned = true;
                        Log("[警告] 输出端点存活探测失败（无法枚举活动设备），本轮跳过；设备状态暂按正常处理");
                    }
                }
                else if (sinkProbe == EndpointProbe.Present)
                {
                    sinkProbeWarned = false;
                }
                if (sinkProbe == EndpointProbe.Missing)
                {
                    int mi = -1;
                    for (int i = 0; i < sinkIds.Length; i++)
                        if (sinkIds[i] == missingSink) { mi = i; break; }
                    OnSinkStalled(mi, "输出设备已不可用（端点存活探测: " + FindNameById(missingSink) + "）");
                    return;
                }
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
                string capMissing;
                if (runCaptureId != null &&
                    ProbeEndpoints(out capMissing, runCaptureId) == EndpointProbe.Missing)
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

        // 单路故障 → 优先只重建这一路
        void OnSinkStalledFromAudio(AudioEngine src, int index, string why)
        {
            if (IsDisposed || !IsHandleCreated) return;
            try
            {
                BeginInvoke(new Action(delegate
                {
                    if (!ReferenceEquals(src, engine)) return;
                    OnSinkStalled(index, why);
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
                Log("[错误] 已连续自动重建 " + autoRestartCount + " 次仍未恢复，停止重试（设备回归后会自动续播）");
                // 放弃重建 ≠ 放弃监听: 设备掉线/休眠后回归是常态, 挂上监听用户就不必手动点开始
                ArmDeviceReturnWatch(runCaptureId, runSinkIds);
                GiveUp();
                return;
            }
            autoRestartCount++;
            Log("[恢复] 自动重建输出流 (" + autoRestartCount + "/" + MaxAutoRestart + ")");
            RestartEngine();
        }

        // ---------------- 单路重建 (只重启故障那一路, 其余路不断音) ----------------

        // 单路重建预算: 同一路连着重启 3 次还稳不住, 说明不是瞬态, 升级为整链路重建
        // (整链路那条路有自己的放弃逻辑: 设备回归自动续播)。
        const int MaxSinkRestart = 3;
        const int SinkRestartMinGapMs = 3000;   // 两次单路重建之间的最小间隔, 防止看门狗热循环
        int[] sinkRestartCount = new int[0];
        int[] sinkRestartTick = new int[0];
        // 逐路"正在重建"标记: 两路同时掉线时各自排队重建 (引擎内部串行), 不会互相吞掉对方的故障
        bool[] sinkRestarting = new bool[0];

        void EnsureSinkRestartArrays(int n)
        {
            if (sinkRestartCount.Length == n && sinkRestarting.Length == n) return;
            var c = new int[n];
            var t = new int[n];
            var b = new bool[n];
            Array.Copy(sinkRestartCount, c, Math.Min(n, sinkRestartCount.Length));
            Array.Copy(sinkRestartTick, t, Math.Min(n, sinkRestartTick.Length));
            Array.Copy(sinkRestarting, b, Math.Min(n, sinkRestarting.Length));
            sinkRestartCount = c;
            sinkRestartTick = t;
            sinkRestarting = b;
        }

        /// <summary>
        /// 单路故障: 只重建这一路。健康的其它路不停、不断音 —— 这是"突然没声"感受的根源:
        /// 以前任何一路出问题都拆整条链路, 两路一起静音约 1 秒。
        /// </summary>
        void OnSinkStalled(int index, string why)
        {
            if (exiting || IsDisposed || restarting) return;
            // 启动/重建过程中链路还没稳定, 单路重建会和启动流程抢同一批端点 → 交给整链路流程
            if (state == AppState.Starting) { OnEngineStalled(why); return; }
            if (state != AppState.Running) return;
            var eng = engine;
            if (eng == null) return;
            if (index < 0 || index >= eng.SinkCount) { OnEngineStalled(why); return; }

            int now = Environment.TickCount;
            EnsureSinkRestartArrays(eng.SinkCount);
            // 已经在等这一路回归 (5 秒轮询中): 看门狗的端点探测每 30 秒还会再报一次同样的故障,
            // 不能让它把重试预算耗光、更不该因此拆掉正在正常出声的其它路
            if (sinkRetryPending.Contains(index)) return;
            if (sinkRestarting[index]) return;      // 这一路正在重建, 别再排一次
            if (sinkRestartTick[index] != 0 && unchecked(now - sinkRestartTick[index]) < SinkRestartMinGapMs)
                return;     // 刚重建过, 给它稳定时间; 真的没救会由看门狗再次上报

            if (sinkRestartCount[index] >= MaxSinkRestart)
            {
                Log("[恢复] 「" + SinkLabel(eng, index) + "」单路重建 " + sinkRestartCount[index] +
                    " 次仍不稳定 → 改为重建整条链路");
                Array.Clear(sinkRestartCount, 0, sinkRestartCount.Length);
                Array.Clear(sinkRestartTick, 0, sinkRestartTick.Length);
                OnEngineStalled(why);
                return;
            }

            sinkRestartCount[index]++;
            sinkRestartTick[index] = now;
            Log("[错误] " + why + " → 只重建「" + SinkLabel(eng, index) + "」，其它路继续出声 (" +
                sinkRestartCount[index] + "/" + MaxSinkRestart + ")");
            BeginSinkRestart(eng, index, false);
        }

        /// <summary>
        /// 异步重建一路 (不阻塞 UI)。awaitingDevice=true 表示这是"等端点回归"的定时重试。
        /// 失败分两种: 端点仍不在 → 该路继续静音等待 (其它路照常出声); 其它原因 → 升级为整链路重建。
        /// </summary>
        void BeginSinkRestart(AudioEngine eng, int index, bool awaitingDevice)
        {
            EnsureSinkRestartArrays(eng.SinkCount);
            if (sinkRestarting[index]) return;
            sinkRestarting[index] = true;

            int idx = index;
            int gen = Volatile.Read(ref engineGeneration);
            // F-03: 跟踪在途任务, FormClosing 会等它们退出再放 UI 状态。
            // Reset/Increment 都在 UI 线程, 与后台的 Decrement/Set 不会倒序。
            sinkRestartFinished.Reset();
            Interlocked.Increment(ref sinkRestartInFlight);

            ThreadPool.QueueUserWorkItem(delegate
            {
                string err = null;
                bool gone = false;
                bool ok = false;
                try { ok = eng.RestartSink(idx, out err, out gone); }
                catch (Exception ex) { err = ex.Message; }
                finally
                {
                    if (Interlocked.Decrement(ref sinkRestartInFlight) == 0) sinkRestartFinished.Set();
                }

                try
                {
                    BeginInvoke(new Action(delegate
                    {
                        // F-03: 必须**先**验证代际与引擎引用, 再改任何数组。旧实现把
                        // sinkRestarting[idx]=false 放在校验之前, 上一代的迟到回调会清掉
                        // 新一代同下标的"正在重建"标记, 让新链路的看门狗以为它还在重建。
                        // 回调可能仍排在消息队列里, 但 gen 不符时一律原地丢弃。
                        if (gen != Volatile.Read(ref engineGeneration) || !ReferenceEquals(eng, engine)) return;
                        if (idx < sinkRestarting.Length) sinkRestarting[idx] = false;
                        if (exiting || IsDisposed) return;

                        if (ok)
                        {
                            sinkRetryPending.Remove(idx);
                            ResetSinkWatch(idx);
                            Log(awaitingDevice
                                ? "[恢复] 「" + SinkLabel(eng, idx) + "」端点已回归，该路已重建（其它路未中断）"
                                : "[恢复] 「" + SinkLabel(eng, idx) + "」已单独重建，其它路未中断");
                            if (sinkRetryPending.Count == 0) DisarmSinkRetry();
                            return;
                        }

                        if (gone)
                        {
                            // 设备真的不在了: 只让这一路静音等待, 绝不因此拆掉还在正常出声的其它路
                            if (!awaitingDevice)
                                Log("[状态] 「" + SinkLabel(eng, idx) +
                                    "」端点已不在（可能已拔出/被禁用）→ 该路静音等待设备回归，其它路继续出声");
                            ArmSinkRetry(idx);
                            return;
                        }

                        Log("[错误] 单路重建失败 (" + (err ?? "未知") + ") → 改为重建整条链路");
                        DisarmSinkRetry();
                        OnEngineStalled("单路重建失败: " + (err ?? SinkLabel(eng, idx)));
                    }));
                }
                catch
                {
                    // 窗体已销毁 → 没人接手; 只在仍是同一代时复位, 免得看门狗永久停摆
                    if (gen == Volatile.Read(ref engineGeneration) && idx < sinkRestarting.Length)
                        sinkRestarting[idx] = false;
                }
            });
        }

        // ---------------- 单路端点掉线: 只等这一路, 其它路照常出声 ----------------

        /// <summary>等待回归的单路 (引擎 sink 下标)。每 5 秒试一次, 端点一回来就重建该路。</summary>
        readonly List<int> sinkRetryPending = new List<int>();

        void ArmSinkRetry(int index)
        {
            if (!sinkRetryPending.Contains(index)) sinkRetryPending.Add(index);
            if (sinkRetryTimer != null && !sinkRetryTimer.Enabled) sinkRetryTimer.Start();
        }

        void DisarmSinkRetry()
        {
            sinkRetryPending.Clear();
            if (sinkRetryTimer != null) sinkRetryTimer.Stop();
        }

        void SinkRetryTick()
        {
            var eng = engine;
            if (exiting || IsDisposed || !IsHandleCreated || !running || eng == null)
            {
                DisarmSinkRetry();
                return;
            }
            if (sinkRetryPending.Count == 0) { DisarmSinkRetry(); return; }

            // 倒序遍历: BeginSinkRestart 的回调可能从列表里移除元素
            for (int k = sinkRetryPending.Count - 1; k >= 0; k--)
            {
                int idx = sinkRetryPending[k];
                if (idx < 0 || idx >= eng.SinkCount || idx >= sinkRestarting.Length)
                {
                    sinkRetryPending.RemoveAt(k);
                    continue;
                }
                if (sinkRestarting[idx]) continue;   // 上一轮还在建, 等下一拍
                BeginSinkRestart(eng, idx, true);
            }
            if (sinkRetryPending.Count == 0) DisarmSinkRetry();
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
            string missing;
            EndpointProbe rebuildProbe = ProbeEndpoints(out missing, runSinkIds.ToArray());
            if (rebuildProbe == EndpointProbe.Unknown)
                Log("[警告] 无法枚举输出端点，跳过重建前的在位校验（若端点真的不在，重建会自行失败并上报）");
            if (rebuildProbe == EndpointProbe.Missing)
            {
                Log("[错误] 输出设备已不可用 (" + FindNameById(missing) + ")，放弃自动重建；等待设备回归后自动续播");
                ArmDeviceReturnWatch(capId, runSinkIds);
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

        // 端点存活探测的三种结果。
        // R-13: 旧实现"枚举失败一律按都在处理"会拖着死设备; 反过来当"缺失"又会误拆健康链路。
        // 加 Unknown 让调用方把这一轮判断作废, 而不是被迫在两个错误结论里选一个。
        enum EndpointProbe { Present, Missing, Unknown }

        /// <summary>
        /// 一次枚举校验多个端点是否仍活动 (原实现每次只查一个 ID, 重建路径要枚举两遍)。
        /// Missing → <paramref name="firstMissing"/> 为第一个缺失的 ID; Unknown → 枚举本身失败, 返回值无意义。
        /// </summary>
        static EndpointProbe ProbeEndpoints(out string firstMissing, params string[] ids)
        {
            firstMissing = null;
            if (ids == null || ids.Length == 0) return EndpointProbe.Present;
            for (int i = 0; i < ids.Length; i++)
            {
                if (string.IsNullOrEmpty(ids[i]))   // 空 ID 与旧 IsEndpointActive 语义一致: 视为缺失
                {
                    firstMissing = ids[i] ?? "";
                    return EndpointProbe.Missing;
                }
            }

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
            catch (Exception ex)
            {
                Swallow.Note("ProbeEndpoints 枚举活动端点", ex);
                return EndpointProbe.Unknown;
            }
            if (missing.Count > 0) { firstMissing = missing[0]; return EndpointProbe.Missing; }
            return EndpointProbe.Present;
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
            return btnMuteMaster.Muted ? 0f : volMaster.Value / 100f;
        }
    }
}
