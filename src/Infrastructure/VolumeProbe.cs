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
        /// <summary>F-44: 最近一次诊断文本/文件输出失败原因; null = 健康。</summary>
        public static string LastWriteError;
        public static bool Healthy { get { return LastWriteError == null; } }
        static int errorReported = 0;

        public static int Run(string logDir)
        {
            LastWriteError = null;
            System.Threading.Interlocked.Exchange(ref errorReported, 0);

            var sb = new StringBuilder();
            int code = 0;
            bool degraded = false;   // F-25: 配置不可信/被截断/非法整数时置位, 返回单独的部分失败码
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
                    // R-04: 必须读 GUI 真正在用的那个 ini, 否则日志里"对照 UI 值"整段是默认值
                    var st = new SettingsStore(AppPaths.IniPath);
                    st.Load();

                    // F-25: LastLoadError != null 时默认值不可信, 必须明示
                    if (st.LastLoadError != null)
                    {
                        sb.AppendLine("[配置读取异常] UI 配置不可读/不可信: " + st.LastLoadError);
                        sb.AppendLine("  -> 下面显示的可能是默认值, 不能作为停止分流后的还原目标");
                        degraded = true;
                    }

                    int masterVol = ReadInt(st, "VolumeMaster", 100, sb, ref degraded);
                    int masterMute = ReadInt(st, "MuteMaster", 0, sb, ref degraded);
                    int oldLeft = ReadInt(st, "VolumeLeft", 100, sb, ref degraded);
                    int oldRight = ReadInt(st, "VolumeRight", 100, sb, ref degraded);
                    sb.AppendLine("主音量=" + masterVol + "%" + (masterMute != 0 ? " 静音" : ""));
                    sb.AppendLine("旧式左/右=" + oldLeft + "% / " + oldRight + "%");

                    // 复用编解码器的 16/8 上限, 损坏 ini 不再导致数十亿次循环
                    bool outMissing;
                    int n = ReadCount(st, "OutCount", LaneSettingsCodec.MaxOutputCards, sb, ref degraded, out outMissing);
                    if (outMissing) sb.AppendLine("  [缺键] OutCount 不存在 (旧式左右配置或首次运行)");
                    for (int i = 0; i < n; i++)
                    {
                        string p = "Out" + i + ".";
                        string channel = ReadStr(st, p + "Channel", sb);
                        string id = ReadStr(st, p + "DeviceId", sb);
                        int multiFlag = ReadInt(st, p + "Multi", 0, sb, ref degraded);
                        int volume = ReadInt(st, p + "Volume", 100, sb, ref degraded);
                        int mute = ReadInt(st, p + "Mute", 0, sb, ref degraded);
                        int delay = ReadInt(st, p + "DelayMs", 0, sb, ref degraded);
                        bool multi = multiFlag != 0 || channel == LaneRouting.MultiKey;
                        sb.AppendLine("  Out" + i + (multi ? " [多声道]" : "") +
                            " 声道=" + channel +
                            " 主音量=" + volume + "%" +
                            (mute != 0 ? " 静音" : "") +
                            " 延迟=" + delay + "ms" +
                            " 设备=" + (string.IsNullOrEmpty(id) ? "(未绑定)" : catalog.NameOf(id)));
                        bool chMissing;
                        int ch = ReadCount(st, p + "ChCount", LaneSettingsCodec.MaxSlotsPerCard, sb, ref degraded, out chMissing);
                        if (chMissing) sb.AppendLine("      [缺键] " + p + "ChCount 不存在");
                        for (int s = 0; s < ch; s++)
                        {
                            string ck = p + "Ch" + s + ".";
                            string chKey = ReadStr(st, ck + "Channel", sb);
                            int chVol = ReadInt(st, ck + "Volume", 100, sb, ref degraded);
                            sb.AppendLine("      p" + s + " 声道=" + chKey +
                                " 音量=" + chVol + "%");
                        }
                    }
                }
                catch (Exception ex)
                {
                    sb.AppendLine("[读取异常] 读 ini 失败: " + ex.Message);
                    degraded = true;
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
            try { Console.WriteLine(text); }
            catch (Exception ex) { NoteWriteError("Console", ex); degraded = true; }
            try
            {
                if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
                File.WriteAllText(Path.Combine(logDir, "volume-probe.txt"), text, Encoding.UTF8);
            }
            catch (Exception ex) { NoteWriteError("volume-probe.txt", ex); degraded = true; }
            if (code == 0 && degraded) code = 3;   // F-25: 单独的部分失败码 (配置不可信/已截断/非法整数/输出失败)
            return code;
        }

        /// <summary>读取计数键: 区分缺键/非法整数/超限, 并对损坏计数做上限截断。</summary>
        static int ReadCount(SettingsStore st, string key, int max, StringBuilder sb,
                             ref bool degraded, out bool missing)
        {
            missing = false;
            string raw = st.GetStr(key);
            if (raw == null) { missing = true; return 0; }   // 缺键: 旧格式合法, 只标识不算失败
            int v;
            if (!int.TryParse(raw, out v))
            {
                sb.AppendLine("  [非法整数] " + key + "=\"" + raw + "\", 按 0 处理");
                degraded = true;
                return 0;
            }
            if (v < 0 || v > max)
            {
                sb.AppendLine("  [配置计数异常，已截断] " + key + "=" + v + " 超出 0.." + max);
                degraded = true;
                return Math.Max(0, Math.Min(max, v));
            }
            return v;
        }

        /// <summary>F-25: 普通整数键不再靠 GetInt 静默回退; 缺键/非法整数分别留痕。</summary>
        static int ReadInt(SettingsStore st, string key, int def, StringBuilder sb, ref bool degraded)
        {
            string raw = st.GetStr(key);
            if (raw == null)
            {
                sb.AppendLine("  [缺键] " + key + " 不存在, 按默认值 " + def + " 显示");
                return def;
            }
            int v;
            if (!int.TryParse(raw, out v))
            {
                sb.AppendLine("  [非法整数] " + key + "=\"" + raw + "\", 按默认值 " + def + " 显示");
                degraded = true;
                return def;
            }
            return v;
        }

        /// <summary>F-25: 字符串键缺键也不再无声按空值显示。</summary>
        static string ReadStr(SettingsStore st, string key, StringBuilder sb)
        {
            string raw = st.GetStr(key);
            if (raw == null)
            {
                sb.AppendLine("  [缺键] " + key + " 不存在, 按空值显示");
                return "";
            }
            return raw;
        }

        /// <summary>F-44: 诊断输出写失败保存健康状态并至少报告一次。</summary>
        static void NoteWriteError(string where, Exception ex)
        {
            LastWriteError = where + ": " + ex.GetType().Name + ": " + ex.Message;
            if (System.Threading.Interlocked.Exchange(ref errorReported, 1) == 0)
            {
                try { Console.Error.WriteLine("[VolumeProbe] 诊断输出写入失败 (仅提示一次): " + LastWriteError); } catch { }
                try { System.Diagnostics.Trace.WriteLine("[VolumeProbe] " + LastWriteError); } catch { }
            }
        }

        static string Pad(int i)
        {
            return "[" + i + "]";
        }
    }
}
