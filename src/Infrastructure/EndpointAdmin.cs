using System;
using System.Collections.Generic;
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
        /// <summary>设置一组端点的音量（"名字片段:百分比"）。返回非 0 = 有失败。</summary>
        public static int SetVolumes(IList<string> specs, string logDir)
        {
            int bad = 0;
            DeviceCatalog catalog = null;
            try
            {
                catalog = new DeviceCatalog();
                catalog.Refresh();
                for (int i = 0; i < specs.Count; i++)
                {
                    string spec = specs[i] ?? "";
                    int c = spec.LastIndexOf(':');
                    if (c <= 0) { Console.WriteLine("[跳过] 参数格式应为 \"设备名片段:百分比\": " + spec); bad++; continue; }
                    string needle = spec.Substring(0, c).Trim();
                    int pct;
                    if (!int.TryParse(spec.Substring(c + 1).Trim(), out pct)) { Console.WriteLine("[跳过] 百分比不是整数: " + spec); bad++; continue; }
                    pct = Math.Max(0, Math.Min(100, pct));

                    EndpointInfo ep = null;
                    List<EndpointInfo> hits = FindAllByName(catalog, needle);
                    if (hits.Count == 0) { Console.WriteLine("[失败] 找不到设备: " + needle); bad++; continue; }
                    if (hits.Count > 1) { PrintAmbiguous(needle, hits); bad++; continue; }
                    ep = hits[0];

                    MMDevice dev = null;
                    try
                    {
                        dev = catalog.Open(ep.ID);
                        if (dev == null) { Console.WriteLine("[失败] 打不开: " + ep.FriendlyName); bad++; continue; }
                        var vol = dev.AudioEndpointVolume;
                        int before = (int)Math.Round(vol.MasterVolumeLevelScalar * 100);
                        vol.MasterVolumeLevelScalar = pct / 100f;
                        if (pct > 0 && vol.Mute) vol.Mute = false;
                        int after = (int)Math.Round(vol.MasterVolumeLevelScalar * 100);
                        Console.WriteLine("[设置] " + ep.FriendlyName + ": 音量 " + before + "% → " + after + "%" +
                            (after != pct ? "（设备量化，目标 " + pct + "%）" : "") +
                            (vol.Mute ? "（仍静音）" : ""));
                    }
                    catch (Exception ex) { Console.WriteLine("[失败] " + ep.FriendlyName + ": " + ex.Message); bad++; }
                    finally { if (dev != null) { try { dev.Dispose(); } catch { } } }
                }
            }
            catch (Exception ex) { Console.WriteLine("设置端点音量异常: " + ex.Message); bad++; }
            finally { if (catalog != null) { try { catalog.Dispose(); } catch { } } }
            return bad == 0 ? 0 : 4;
        }

        /// <summary>把系统默认输出切成名字匹配的设备。返回 0 = 成功。</summary>
        public static int SetDefault(string needle)
        {
            DeviceCatalog catalog = null;
            try
            {
                catalog = new DeviceCatalog();
                catalog.Refresh();
                List<EndpointInfo> hits = FindAllByName(catalog, needle);
                if (hits.Count == 0) { Console.WriteLine("[失败] 找不到设备: " + needle); return 5; }
                if (hits.Count > 1) { PrintAmbiguous(needle, hits); return 8; }
                EndpointInfo ep = hits[0];
                string before = catalog.DefaultRenderName();
                string err;
                if (!SystemDefaultPolicy.TrySetDefault(ep.ID, out err))
                {
                    Console.WriteLine("[失败] 切换默认输出失败: " + err);
                    return 6;
                }
                Console.WriteLine("[设置] 默认输出: " + before + " → " + ep.FriendlyName);
                return 0;
            }
            catch (Exception ex) { Console.WriteLine("切换默认输出异常: " + ex.Message); return 7; }
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
                        System.Threading.Thread.Sleep((int)(seconds * 1000));
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
