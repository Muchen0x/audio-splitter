using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using StereoSplitter.Devices;

namespace StereoSplitter.Infrastructure
{
    /// <summary>
    /// 端点维护工具（`--setvol "设备名片段:百分比"` / `--setdefault "设备名片段"`）。
    ///
    /// <para>为什么需要它：<see cref="VolumeProbe"/> 只能看不能改。遇到"实例被强杀、
    /// 端点音量卡在分流值"或"默认输出还挂在虚拟声卡上（系统无声）"时，光靠界面
    /// 修不回来 —— 程序记录的是**当前**值当基线，重启一次反而把错的值固化下来。
    /// 这里直接按设备名改端点，用于善后。</para>
    ///
    /// <para>只按显式命令行参数执行，不做任何隐式写入；每次写入都回读并打印。</para>
    /// </summary>
    static class EndpointAdmin
    {
        /// <summary>--sec 允许的最大播放秒数; Program 解析器与 PlayTest 共用, 防 Thread.Sleep(-1)。</summary>
        public const double MaxPlayTestSeconds = 30;

        /// <summary>维护审计日志写入健康状态: null = 正常, 非 null = 最近一次失败原因。</summary>
        public static string LastWriteError;
        public static bool Healthy { get { return LastWriteError == null; } }
        static int auditWarned = 0;

        sealed class VolTarget
        {
            public string Spec;
            public string Needle;
            public int Pct;
            public EndpointInfo Ep;
            public MMDevice Dev;
            public float BeforeScalar;
            public bool BeforeMute;
            public bool Written;
        }

        static string MuteText(bool m) { return m ? "静音" : "未静音"; }

        /// <summary>F-44: 落实维护审计日志 (logDir 不再只是摆设); 写失败保存健康状态并至少报告一次。</summary>
        static void Audit(string logDir, string line)
        {
            if (string.IsNullOrEmpty(logDir)) return;
            try
            {
                Directory.CreateDirectory(logDir);
                File.AppendAllText(Path.Combine(logDir, "maintenance.log"),
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + line + "\r\n", Encoding.UTF8);
                LastWriteError = null;
                Interlocked.Exchange(ref auditWarned, 0);   // 后续再次失败仍允许重新提示一次
            }
            catch (Exception ex)
            {
                LastWriteError = ex.GetType().Name + ": " + ex.Message;
                if (Interlocked.Exchange(ref auditWarned, 1) == 0)
                {
                    try { Console.WriteLine("[警告] 维护审计日志写入失败 (仅提示一次): " + LastWriteError); } catch { }
                }
            }
        }

        /// <summary>设置一组端点的音量（"名字片段:百分比"）。返回非 0 = 有失败。
        /// 兼容旧调用: 默认不解除静音。</summary>
        public static int SetVolumes(IList<string> specs, string logDir)
        {
            return SetVolumes(specs, logDir, false);
        }

        /// <summary>F-20: 第一遍完成全部解析/范围检查/设备唯一匹配, 全部通过后才写入;
        /// 拒绝 0..100 之外的输入 (不再静默钳制); 隐式解除静音改为显式 unmute;
        /// 写入前记录音量/静音快照, 失败时尽力回滚并回读, 无法回滚时明确列出残留状态。</summary>
        public static int SetVolumes(IList<string> specs, string logDir, bool unmute)
        {
            int bad = 0;
            DeviceCatalog catalog = null;
            var targets = new List<VolTarget>();
            try
            {
                catalog = new DeviceCatalog();
                catalog.Refresh();

                // ---- 第一遍: 只解析 / 范围检查 / 唯一匹配, 不写任何设备 ----
                for (int i = 0; i < specs.Count; i++)
                {
                    string spec = specs[i] ?? "";
                    int c = spec.LastIndexOf(':');
                    if (c <= 0) { Console.WriteLine("[拒绝] 参数格式应为 \"设备名片段:百分比\": " + spec); bad++; continue; }
                    string needle = spec.Substring(0, c).Trim();
                    int pct;
                    if (!int.TryParse(spec.Substring(c + 1).Trim(), out pct))
                    { Console.WriteLine("[拒绝] 百分比不是整数: " + spec); bad++; continue; }
                    if (pct < 0 || pct > 100)
                    { Console.WriteLine("[拒绝] 百分比 " + pct + " 超出 0..100 (不再静默钳制): " + spec); bad++; continue; }
                    List<EndpointInfo> hits = FindAllByName(catalog, needle);
                    if (hits.Count == 0) { Console.WriteLine("[失败] 找不到设备: " + needle); bad++; continue; }
                    if (hits.Count > 1) { PrintAmbiguous(needle, hits); bad++; continue; }
                    targets.Add(new VolTarget { Spec = spec, Needle = needle, Pct = pct, Ep = hits[0] });
                }
                if (bad > 0 || targets.Count == 0)
                {
                    string msg = bad > 0
                        ? "[中止] " + bad + " 项校验失败, 未对任何设备写入 (避免部分提交)"
                        : "[中止] 没有待设置的设备 (空操作)";
                    Console.WriteLine(msg);
                    Audit(logDir, "SetVolumes: " + msg);
                    return 4;
                }

                // ---- 第二遍: 先打开全部设备并读快照, 任一失败则整体放弃 ----
                for (int i = 0; i < targets.Count; i++)
                {
                    VolTarget t = targets[i];
                    MMDevice dev = null;
                    try
                    {
                        dev = catalog.Open(t.Ep.ID);
                        if (dev == null) throw new Exception("打不开设备");
                        var vol = dev.AudioEndpointVolume;
                        t.BeforeScalar = vol.MasterVolumeLevelScalar;
                        t.BeforeMute = vol.Mute;
                        t.Dev = dev;
                        dev = null;   // 所有权交给 targets, finally 统一释放
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[失败] 快照 " + t.Ep.FriendlyName + ": " + ex.Message);
                        bad++;
                    }
                    finally { if (dev != null) { try { dev.Dispose(); } catch { } } }
                }
                if (bad > 0)
                {
                    Console.WriteLine("[中止] 快照阶段失败, 未写入任何设备");
                    Audit(logDir, "SetVolumes: 快照阶段失败, 未写入");
                    return 4;
                }

                // ---- 第三遍: 全部就绪后逐个写入 ----
                for (int i = 0; i < targets.Count; i++)
                {
                    VolTarget t = targets[i];
                    try
                    {
                        var vol = t.Dev.AudioEndpointVolume;
                        vol.MasterVolumeLevelScalar = t.Pct / 100f;
                        // 不再隐式解除静音: 只有显式 --unmute 才改静音状态
                        if (unmute && t.Pct > 0 && vol.Mute) vol.Mute = false;
                        t.Written = true;
                        int before = (int)Math.Round(t.BeforeScalar * 100);
                        int after = (int)Math.Round(vol.MasterVolumeLevelScalar * 100);
                        bool afterMute = vol.Mute;
                        Console.WriteLine("[设置] " + t.Ep.FriendlyName +
                            ": 音量 " + before + "% -> " + after + "%" +
                            ", 静音 " + MuteText(t.BeforeMute) + " -> " + MuteText(afterMute) +
                            (after != t.Pct ? " (设备量化, 目标 " + t.Pct + "%)" : "") +
                            (afterMute ? " (仍静音; 需解除请显式加 --unmute)" : ""));
                        Audit(logDir, "SetVolumes: " + t.Spec + " -> " + after + "%, 静音=" + MuteText(afterMute));
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[失败] " + t.Ep.FriendlyName + ": " + ex.Message);
                        bad++;
                        break;   // 停止后续写入, 交给回滚
                    }
                }

                if (bad > 0) Rollback(targets, logDir);
            }
            catch (Exception ex)
            {
                Console.WriteLine("设置端点音量异常: " + ex.Message);
                Audit(logDir, "SetVolumes 异常: " + ex.Message);
                bad++;
                Rollback(targets, logDir);
            }
            finally
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    MMDevice d = targets[i].Dev;
                    if (d != null) { try { d.Dispose(); } catch { } }
                }
                if (catalog != null) { try { catalog.Dispose(); } catch { } }
            }
            return bad == 0 ? 0 : 4;
        }

        /// <summary>失败后尽力把已写入端点恢复为写入前的音量/静音, 并回读确认; 列出无法确认的残留。</summary>
        static void Rollback(List<VolTarget> targets, string logDir)
        {
            Console.WriteLine("[回滚] 尝试把已修改端点恢复为写入前的音量/静音并回读确认……");
            var residual = new List<string>();
            for (int i = 0; i < targets.Count; i++)
            {
                VolTarget t = targets[i];
                if (!t.Written || t.Dev == null) continue;
                try
                {
                    var vol = t.Dev.AudioEndpointVolume;
                    vol.MasterVolumeLevelScalar = t.BeforeScalar;
                    vol.Mute = t.BeforeMute;
                    int now = (int)Math.Round(vol.MasterVolumeLevelScalar * 100);
                    bool nowMute = vol.Mute;
                    int want = (int)Math.Round(t.BeforeScalar * 100);
                    if (now != want || nowMute != t.BeforeMute)
                        residual.Add(t.Ep.FriendlyName + ": 期望 " + want + "%/" + MuteText(t.BeforeMute) +
                                     ", 回读 " + now + "%/" + MuteText(nowMute));
                    else
                        Console.WriteLine("[回滚] " + t.Ep.FriendlyName + " 已恢复并回读确认: " + now + "%/" + MuteText(nowMute));
                }
                catch (Exception ex)
                {
                    residual.Add(t.Ep.FriendlyName + ": 回滚异常 " + ex.Message);
                }
            }
            if (residual.Count > 0)
            {
                Console.WriteLine("[残留状态] 以下端点未能确认恢复, 请人工核对:");
                for (int i = 0; i < residual.Count; i++) Console.WriteLine("    - " + residual[i]);
                Audit(logDir, "SetVolumes 回滚残留 " + residual.Count + " 项");
            }
            else Console.WriteLine("[回滚] 全部已写入端点已恢复并回读确认。");
        }

        /// <summary>把系统默认输出切成名字匹配的设备。返回 0 = 成功。</summary>
        public static int SetDefault(string needle)
        {
            string logDir = AppPaths.LogDir;   // R-04: 与 GUI/自检共用落位规则
            DeviceCatalog catalog = null;
            try
            {
                catalog = new DeviceCatalog();
                catalog.Refresh();
                List<EndpointInfo> hits = FindAllByName(catalog, needle);
                if (hits.Count == 0)
                { Console.WriteLine("[失败] 找不到设备: " + needle); Audit(logDir, "SetDefault: 找不到设备 " + needle); return 5; }
                if (hits.Count > 1)
                { PrintAmbiguous(needle, hits); Audit(logDir, "SetDefault: 多匹配 " + needle); return 8; }
                EndpointInfo ep = hits[0];
                string before = catalog.DefaultRenderName();
                string err;
                if (!SystemDefaultPolicy.TrySetDefault(ep.ID, out err))
                {
                    Console.WriteLine("[失败] 切换默认输出失败: " + err);
                    Audit(logDir, "SetDefault: 失败 " + needle + " -> " + err);
                    return 6;
                }
                string line = "[设置] 默认输出: " + before + " -> " + ep.FriendlyName;
                Console.WriteLine(line);
                Audit(logDir, "SetDefault: " + line);
                return 0;
            }
            catch (Exception ex)
            {
                Console.WriteLine("切换默认输出异常: " + ex.Message);
                Audit(logDir, "SetDefault 异常: " + ex.Message);
                return 7;
            }
            finally { if (catalog != null) { try { catalog.Dispose(); } catch { } } }
        }

        /// <summary>
        /// 列出渲染端点（`--list [名字片段]`，只读干跑）。
        /// 片段为空列全部，否则只列名字包含片段的；用于改之前先确认匹配目标。
        /// 返回 0 = 正常。
        /// </summary>
        public static int ListEndpoints(string needle)
        {
            DeviceCatalog catalog = null;
            try
            {
                catalog = new DeviceCatalog();
                catalog.Refresh();
                string defId = catalog.DefaultRenderId();
                bool filter = !string.IsNullOrEmpty(needle);
                int shown = 0;
                for (int i = 0; i < catalog.Endpoints.Count; i++)
                {
                    EndpointInfo ep = catalog.Endpoints[i];
                    if (filter && ep.FriendlyName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) continue;
                    shown++;
                    MMDevice dev = null;
                    try
                    {
                        dev = catalog.Open(ep.ID);
                        if (dev == null) { Console.WriteLine("[" + i + "] (打不开) " + ep.FriendlyName); continue; }
                        var vol = dev.AudioEndpointVolume;
                        int pct = (int)Math.Round(vol.MasterVolumeLevelScalar * 100);
                        Console.WriteLine("[" + i + "] " + pct.ToString().PadLeft(3) + "%" +
                            (vol.Mute ? " 静音 " : "     ") +
                            (ep.ID == defId ? "[默认] " : "       ") + ep.FriendlyName +
                            "  <" + ep.ID + ">");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine("[" + i + "] (音量不可用: " + ex.Message + ") " + ep.FriendlyName);
                    }
                    finally { if (dev != null) { try { dev.Dispose(); } catch { } } }
                }
                if (shown == 0) Console.WriteLine(filter ? ("没有匹配 \"" + needle + "\" 的设备") : "没有渲染端点");
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("列出端点异常: " + ex.Message); return 9; }
            finally { if (catalog != null) { try { catalog.Dispose(); } catch { } } }
        }

        /// <summary>
        /// 向指定端点放一段测试音（`--playtest "名字片段" [秒]`）。
        /// 用途: 程序链路健康但某一路没声时，绕开程序直接驱动该设备 ——
        /// 听得到 = 端点/硬件/会话层正常，问题在程序侧；听不到 = 设备本身或系统会话层。
        /// 共享模式播放，不动端点音量/静音，不与正在运行的实例冲突。
        /// 返回 0 = 播放完成。
        /// </summary>
        public static int PlayTest(string needle, double seconds)
        {
            // F-21: 打开设备/播放之前完成秒数校验 (Program 已校验, 这里防御直接调用)
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) ||
                seconds <= 0 || seconds > MaxPlayTestSeconds)
            {
                Console.WriteLine("[参数错误] 播放秒数必须是 0 < sec <= " +
                    MaxPlayTestSeconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                    " 的有限值, 收到: " + seconds);
                return 2;
            }
            DeviceCatalog catalog = null;
            try
            {
                catalog = new DeviceCatalog();
                catalog.Refresh();
                List<EndpointInfo> hits = FindAllByName(catalog, needle);
                if (hits.Count == 0) { Console.WriteLine("[失败] 找不到设备: " + needle); return 5; }
                if (hits.Count > 1) { PrintAmbiguous(needle, hits); return 8; }
                EndpointInfo ep = hits[0];
                MMDevice dev = catalog.Open(ep.ID);
                if (dev == null) { Console.WriteLine("[失败] 打不开: " + ep.FriendlyName); return 6; }
                try
                {
                    WaveFormat fmt = dev.AudioClient.MixFormat;
                    var gen = new NAudio.Wave.SampleProviders.SignalGenerator(
                        fmt.SampleRate, fmt.Channels)
                    {
                        Frequency = 440f,
                        Gain = 0.18
                    };
                    using (var player = new WasapiOut(dev, AudioClientShareMode.Shared, true, 50))
                    {
                        player.Init(gen);
                        player.Play();
                        Console.WriteLine("[测试] 正在向 " + ep.FriendlyName + " 播放 " +
                            seconds.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture) +
                            " 秒 440Hz 测试音 (" + fmt.SampleRate + "Hz/" + fmt.Channels + "ch)……");
                        int sleepMs = (int)(seconds * 1000);
                        if (sleepMs < 1) sleepMs = 1;   // 秒数已校验, 这里再兜住绝不出现 Sleep(-1)
                        System.Threading.Thread.Sleep(sleepMs);
                        player.Stop();
                    }
                    Console.WriteLine("[完成] 若该设备没发出声音, 检查: 音箱电源/音量旋钮/Windows 音量合成器里本程序的会话音量。");
                    return 0;
                }
                finally { if (dev != null) { try { dev.Dispose(); } catch { } } }
            }
            catch (Exception ex) { Console.WriteLine("测试音播放异常: " + ex.Message); return 9; }
            finally { if (catalog != null) { try { catalog.Dispose(); } catch { } } }
        }

        /// <summary>多匹配拒绝时打印候选清单，提示用户换成更精确的片段。</summary>
        static void PrintAmbiguous(string needle, List<EndpointInfo> hits)
        {
            Console.WriteLine("[拒绝] \"" + needle + "\" 匹配到 " + hits.Count + " 个设备, 为防误伤已中止:");
            for (int i = 0; i < hits.Count; i++)
                Console.WriteLine("    - " + hits[i].FriendlyName);
            Console.WriteLine("请改用更长的名字片段, 或先用 --list \"片段\" 确认目标。");
        }

        /// <summary>按名字子串找设备（不区分大小写）。**返回全部命中**，调用方需自行处理多匹配。</summary>
        static List<EndpointInfo> FindAllByName(DeviceCatalog catalog, string needle)
        {
            var hits = new List<EndpointInfo>();
            if (string.IsNullOrEmpty(needle)) return hits;
            for (int i = 0; i < catalog.Endpoints.Count; i++)
                if (catalog.Endpoints[i].FriendlyName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    hits.Add(catalog.Endpoints[i]);
            return hits;
        }
    }
}
