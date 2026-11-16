using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using NAudio.CoreAudioApi;
using StereoSplitter.Devices;

namespace StereoSplitter.Infrastructure
{
    /// <summary>
    /// 端点音量探针 (`--volprobe`): 只读打印每台输出设备当前的音量/静音, 并与 ini 里
    /// 记录的 UI 值对照。
    ///
    /// <para>为什么需要它: "停止后音量还原了没有" 这个问题上, 程序日志和用户在
    /// Windows 音量合成器里看到的东西可能对不上 (端点被独占/驱动量化/看错设备)。
    /// 这个探针不碰音频链路、不改任何值, 给出的是端点真值, 便于把问题定位到
    /// "程序没还原" 还是 "设备不接受写入"。</para>
    /// </summary>
    static class VolumeProbe
    {
        public static int Run(string logDir)
        {
            var sb = new StringBuilder();
            int code = 0;
            DeviceCatalog catalog = null;
            try
            {
                catalog = new DeviceCatalog();
                catalog.Refresh();

                string defId = catalog.DefaultRenderId();
                sb.AppendLine("默认输出: " + catalog.DefaultRenderName());
                sb.AppendLine();
                sb.AppendLine("== 渲染端点当前状态 ==");

                var lines = new List<string>();
                for (int i = 0; i < catalog.Endpoints.Count; i++)
                {
                    EndpointInfo ep = catalog.Endpoints[i];
                    MMDevice dev = null;
                    try
                    {
                        dev = catalog.Open(ep.ID);
                        if (dev == null)
                        {
                            lines.Add(Pad(i) + " (打不开) " + ep.FriendlyName);
                            continue;
                        }
                        var vol = dev.AudioEndpointVolume;
                        int pct = (int)Math.Round(vol.MasterVolumeLevelScalar * 100);
                        bool mute = vol.Mute;
                        lines.Add(Pad(i) + " " + pct.ToString().PadLeft(3) + "%" + (mute ? " 静音" : "    ") +
                            (ep.ID == defId ? " [默认] " : "        ") + ep.FriendlyName);
                    }
                    catch (Exception ex)
                    {
                        lines.Add(Pad(i) + " (音量不可用: " + ex.Message + ") " + ep.FriendlyName);
                    }
                    finally
                    {
                        if (dev != null) { try { dev.Dispose(); } catch { } }
                    }
                }
                foreach (string l in lines) sb.AppendLine(l);

                // 与 ini 里的 UI 值对照: 分流停止后设备值应当等于 ini 里保存的 UI 值
                sb.AppendLine();
                sb.AppendLine("== ini 里记录的 UI 值 (SettingsStore) ==");
                try
                {
                    var st = new SettingsStore(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "SplitterGUI.ini"));
                    st.Load();
                    sb.AppendLine("主音量=" + st.GetInt("VolumeMaster", 100) + "%" +
                        (st.GetInt("MuteMaster", 0) != 0 ? " 静音" : ""));
                    sb.AppendLine("旧式左/右=" + st.GetInt("VolumeLeft", 100) + "% / " +
                        st.GetInt("VolumeRight", 100) + "%");
                    int n = st.GetInt("OutCount", 0);
                    for (int i = 0; i < n; i++)
                    {
                        string p = "Out" + i + ".";
                        string id = st.GetStr(p + "DeviceId");
                        bool multi = st.GetInt(p + "Multi", 0) != 0 || st.GetStr(p + "Channel") == LaneRouting.MultiKey;
                        sb.AppendLine("  Out" + i + (multi ? " [多声道]" : "") +
                            " 声道=" + st.GetStr(p + "Channel") +
                            " 主音量=" + st.GetInt(p + "Volume", 100) + "%" +
                            (st.GetInt(p + "Mute", 0) != 0 ? " 静音" : "") +
                            " 延迟=" + st.GetInt(p + "DelayMs", 0) + "ms" +
                            " 设备=" + catalog.NameOf(id));
                        int ch = st.GetInt(p + "ChCount", 0);
                        for (int s = 0; s < ch; s++)
                        {
                            string ck = p + "Ch" + s + ".";
                            sb.AppendLine("      p" + s + " 声道=" + st.GetStr(ck + "Channel") +
                                " 音量=" + st.GetInt(ck + "Volume", 100) + "%");
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("(读 ini 失败: " + ex.Message + ")");
                }
            }
            catch (Exception ex)
            {
                sb.AppendLine("探针异常: " + ex);
                code = 2;
            }
            finally
            {
                if (catalog != null) { try { catalog.Dispose(); } catch { } }
            }

            string text = sb.ToString();
            try { Console.WriteLine(text); } catch { }
            try
            {
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "volume-probe.txt"), text, Encoding.UTF8);
            }
            catch { }
            return code;
        }

        static string Pad(int i)
        {
            return "[" + i + "]";
        }
    }
}
