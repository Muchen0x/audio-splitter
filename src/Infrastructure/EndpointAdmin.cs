using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;
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

                    EndpointInfo ep = FindByName(catalog, needle);
                    if (ep == null) { Console.WriteLine("[失败] 找不到设备: " + needle); bad++; continue; }

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
                EndpointInfo ep = FindByName(catalog, needle);
                if (ep == null) { Console.WriteLine("[失败] 找不到设备: " + needle); return 5; }
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

        static EndpointInfo FindByName(DeviceCatalog catalog, string needle)
        {
            if (string.IsNullOrEmpty(needle)) return null;
            for (int i = 0; i < catalog.Endpoints.Count; i++)
                if (catalog.Endpoints[i].FriendlyName.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0)
                    return catalog.Endpoints[i];
            return null;
        }
    }
}
