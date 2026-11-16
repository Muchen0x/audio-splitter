using System;
using System.Globalization;
using System.Text;
using StereoSplitter.Infrastructure;

namespace StereoSplitter
{
    /// <summary>
    /// 路由配置的 ini 读写 (P2)。
    ///
    /// **当前分工**: 界面的权威源是 `Out{n}.*` + `Spec`（一卡片一路），路由模型每次由
    /// `LaneRouting.Build()` 现算，所以运行时不读这份 schema；这里保留的是"用文本完整描述
    /// 一套路由"的能力，供自检 (RoutingSelfTest 的 ini 往返) 与将来的外部/批量配置使用。
    /// 两侧不会同时写同一份 ini：本文件只碰 Sink*/Chan*/Bind*，界面只碰 Out*/DeviceLeft 等。
    ///
    /// 存的是**编辑期三层模型** (逻辑声道 / 绑定 / 设备), 不是编译后的矩阵 ——
    /// 矩阵是派生数据, 存派生数据等于把"为什么这么混"丢掉, 以后 UI 就没法反解。
    ///
    /// 与旧版的兼容:
    ///   - 没有 SinkCount 键 = 旧 ini → 由 DeviceLeft/DeviceRight 合成 s0/s1 (split-lr, 同组 g0),
    ///     行为与两路时代逐字节等价; 首次保存即写出新 schema。
    ///   - DeviceLeft/DeviceRight 仍由 UI 层继续写 (旧版 exe 可回退);
    ///     本文件不碰这两个键, 避免两个写入方互相覆盖。
    ///   - 删掉全部新键仍能启动 (回到旧键兜底), 这是 P2 的验收标准之一。
    /// </summary>
    partial class RoutingModel
    {
        public const int MaxSinks = 8;
        public const int MaxChannels = 16;
        public const int MaxBindings = 64;
        public const int MaxTaps = 8;

        const string KMode = "RoutingMode";
        const string KLayout = "RoutingLayout";
        const string KChanCount = "ChanCount";
        const string KBindCount = "BindCount";
        const string KSinkCount = "SinkCount";

        // ---------------------------------------------------------------- 枚举文本

        public static string ModeText(WorkMode m)
        {
            switch (m)
            {
                case WorkMode.SplitLr: return "split-lr";
                case WorkMode.MirrorStereo: return "mirror-stereo";
                case WorkMode.Surround: return "surround";
                case WorkMode.MonoBroadcast: return "mono-broadcast";
                default: return "custom";
            }
        }

        public static WorkMode ParseMode(string s, WorkMode def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            switch (s.Trim().ToLowerInvariant())
            {
                case "split-lr": case "split": return WorkMode.SplitLr;
                case "mirror-stereo": case "mirror": return WorkMode.MirrorStereo;
                case "surround": case "upmix": return WorkMode.Surround;
                case "mono-broadcast": case "mono": return WorkMode.MonoBroadcast;
                case "custom": return WorkMode.Custom;
                default: return def;
            }
        }

        public static string LayoutText(ChannelLayout l)
        {
            switch (l)
            {
                case ChannelLayout.Mono: return "mono";
                case ChannelLayout.Stereo: return "stereo";
                case ChannelLayout.Quad: return "quad";
                case ChannelLayout.Surround51: return "5.1";
                case ChannelLayout.Surround71: return "7.1";
                default: return "custom";
            }
        }

        public static ChannelLayout ParseLayout(string s, ChannelLayout def)
        {
            if (string.IsNullOrEmpty(s)) return def;
            switch (s.Trim().ToLowerInvariant())
            {
                case "mono": case "1.0": return ChannelLayout.Mono;
                case "stereo": case "2.0": return ChannelLayout.Stereo;
                case "quad": return ChannelLayout.Quad;
                case "5.1": case "surround51": return ChannelLayout.Surround51;
                case "7.1": case "surround71": return ChannelLayout.Surround71;
                case "custom": return ChannelLayout.Custom;
                default: return def;
            }
        }

        static string KindText(ChannelKind k)
        {
            switch (k)
            {
                case ChannelKind.FL: return "FL";
                case ChannelKind.FR: return "FR";
                case ChannelKind.C: return "C";
                case ChannelKind.LFE: return "LFE";
                case ChannelKind.SL: return "SL";
                case ChannelKind.SR: return "SR";
                case ChannelKind.BL: return "BL";
                case ChannelKind.BR: return "BR";
                default: return "G";
            }
        }

        static ChannelKind ParseKind(string s)
        {
            if (string.IsNullOrEmpty(s)) return ChannelKind.Generic;
            switch (s.Trim().ToUpperInvariant())
            {
                case "FL": return ChannelKind.FL;
                case "FR": return ChannelKind.FR;
                case "C": return ChannelKind.C;
                case "LFE": return ChannelKind.LFE;
                case "SL": return ChannelKind.SL;
                case "SR": return ChannelKind.SR;
                case "BL": return ChannelKind.BL;
                case "BR": return ChannelKind.BR;
                default: return ChannelKind.Generic;
            }
        }

        // ---------------------------------------------------------------- 读

        static string F(float v) { return v.ToString("0.###", CultureInfo.InvariantCulture); }

        static float ParseF(string s, float def)
        {
            float v;
            if (!string.IsNullOrEmpty(s) &&
                float.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out v)) return v;
            return def;
        }

        /// <summary>
        /// 从 ini 装载。migrated=true 表示"这份配置还没写过新 schema"(旧 ini / 键被删),
        /// 调用方应立刻保存一次, 让新键落地。
        /// </summary>
        public static RoutingModel LoadFromIni(SettingsStore ini, out bool migrated)
        {
            migrated = false;
            if (ini == null) { migrated = true; return FromLegacy(ini); }

            if (!ini.Has(KSinkCount) || ini.GetInt(KSinkCount, 0) <= 0)
            {
                migrated = true;
                return FromLegacy(ini);
            }

            var m = new RoutingModel();
            m.Mode = ParseMode(ini.GetStr(KMode), WorkMode.SplitLr);
            m.Layout = ParseLayout(ini.GetStr(KLayout), ChannelLayout.Stereo);

            int sc = Math.Max(0, Math.Min(MaxSinks, ini.GetInt(KSinkCount, 0)));
            for (int n = 0; n < sc; n++)
            {
                string p = "Sink" + n + ".";
                var s = new SinkSpec(
                    Str(ini, p + "Key", "s" + n),
                    Str(ini, p + "Label", "设备" + (n + 1)),
                    Str(ini, p + "DeviceId", ""),
                    Math.Max(1, Math.Min(8, ini.GetInt(p + "Channels", 1))),
                    Math.Max(0, Math.Min(2000, ini.GetInt(p + "DelayMs", 0))));
                s.Enabled = ini.GetInt(p + "Enabled", 1) != 0;
                s.Volume = Math.Max(0, Math.Min(200, ini.GetInt(p + "Volume", 100))) / 100f;
                s.ClockGroupId = Str(ini, p + "ClockGroup", "");
                s.CachedName = Str(ini, p + "Name", "");
                m.SinkSpecs.Add(s);
            }

            int cc = Math.Max(0, Math.Min(MaxChannels, ini.GetInt(KChanCount, 0)));
            for (int i = 0; i < cc; i++)
            {
                string p = "Chan" + i + ".";
                var c = new LogicalChannel(Str(ini, p + "Key", "ch" + i),
                                           Str(ini, p + "Name", "声道" + (i + 1)),
                                           ParseKind(ini.GetStr(p + "Kind")));
                c.Order = i;
                c.Gain = ParseF(ini.GetStr(p + "Gain"), 1f);
                c.Mute = ini.GetInt(p + "Mute", 0) != 0;
                int tc = Math.Max(0, Math.Min(MaxTaps, ini.GetInt(p + "TapCount", 0)));
                for (int t = 0; t < tc; t++)
                {
                    int src; float g;
                    if (ParseTap(ini.GetStr(p + "Tap" + t), out src, out g))
                        c.Taps.Add(new SourceTap(src, g));
                }
                m.Channels.Add(c);
            }

            int bc = Math.Max(0, Math.Min(MaxBindings, ini.GetInt(KBindCount, 0)));
            for (int j = 0; j < bc; j++)
            {
                int phys; float g;
                string ck, sk;
                if (ParseBind(ini.GetStr("Bind" + j), out ck, out sk, out phys, out g))
                    m.Bindings.Add(new ChannelBinding(ck, sk, phys, g));
            }

            // 设备在但清单不在 (手改 ini / 未来降级) → 按模式补一份默认清单, 不让 UI 空转
            if (m.Channels.Count == 0 && m.Mode != WorkMode.Custom)
            {
                m.ApplyModeTemplate(m.Mode, m.EnabledSinks());
                migrated = true;
            }
            return m;
        }

        static string Str(SettingsStore ini, string key, string def)
        {
            string s = ini.GetStr(key);
            return string.IsNullOrEmpty(s) ? def : s;
        }

        /// <summary>"0*1.0" / "0*0.5" → (src, gain)。空串与 "mute" 返回 false (该声道静音)。</summary>
        static bool ParseTap(string s, out int src, out float gain)
        {
            src = 0; gain = 1f;
            if (string.IsNullOrEmpty(s)) return false;
            string t = s.Trim();
            if (t.Equals("mute", StringComparison.OrdinalIgnoreCase)) return false;
            int star = t.IndexOf('*');
            if (star <= 0) return false;
            int si;
            if (!int.TryParse(t.Substring(0, star).Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out si)) return false;
            src = si < 0 ? 0 : si;
            gain = ParseF(t.Substring(star + 1), 1f);
            return true;
        }

        /// <summary>"ch0,s0,0,1.0" → 绑定。字段数不足视为脏数据丢弃。</summary>
        static bool ParseBind(string s, out string chKey, out string sinkKey, out int phys, out float gain)
        {
            chKey = null; sinkKey = null; phys = 0; gain = 1f;
            if (string.IsNullOrEmpty(s)) return false;
            var parts = s.Split(',');
            if (parts.Length < 3) return false;
            chKey = parts[0].Trim();
            sinkKey = parts[1].Trim();
            if (chKey.Length == 0 || sinkKey.Length == 0) return false;
            if (!int.TryParse(parts[2].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out phys)) return false;
            if (phys < 0) phys = 0;
            if (parts.Length > 3) gain = ParseF(parts[3], 1f);
            return true;
        }

        /// <summary>旧 ini → 新模型: 两路分放左右, 共用时钟组 (与拆分时代的 ratio 共享一致)。</summary>
        static RoutingModel FromLegacy(SettingsStore ini)
        {
            string l = ini == null ? "" : (ini.GetStr("DeviceLeft") ?? "");
            string r = ini == null ? "" : (ini.GetStr("DeviceRight") ?? "");
            int dl = ini == null ? 0 : Math.Max(0, Math.Min(2000, ini.GetInt("DelayLeft", 0)));
            int dr = ini == null ? 0 : Math.Max(0, Math.Min(2000, ini.GetInt("DelayRight", 0)));
            return SplitLr("s0", l, "左", dl, "s1", r, "右", dr);
        }

        // ---------------------------------------------------------------- 写

        /// <summary>把当前模型整体写回 ini (含未启用的路, 停用不等于删除)。</summary>
        public void SaveToIni(SettingsStore ini)
        {
            if (ini == null) return;
            // ini 是"插入顺序 + 只覆盖不删除"的表: 删掉一条声道/设备后, 旧键 (Chan7.*) 会留在
            // 文件里, 和 ChanCount 对不上 —— 人工打开 ini 会以为它还在生效 (实际加载按 count 截断)。
            // 每次存盘先把本模型写过的键全部清掉再重写, 文件即等于内存模型的镜像。
            var stale = ini.SnapshotKeys();
            for (int i = 0; i < stale.Count; i++)
                if (IsRoutingKey(stale[i])) ini.Remove(stale[i]);

            ini.Put(KMode, ModeText(Mode));
            ini.Put(KLayout, LayoutText(Layout));

            ini.Put(KChanCount, Channels.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < Channels.Count; i++)
            {
                var c = Channels[i];
                string p = "Chan" + i + ".";
                ini.Put(p + "Key", c.Key);
                ini.Put(p + "Name", c.Name);
                ini.Put(p + "Kind", KindText(c.Kind));
                ini.Put(p + "Gain", F(c.Gain));
                ini.Put(p + "Mute", c.Mute ? "1" : "0");
                ini.Put(p + "TapCount", c.Taps.Count.ToString(CultureInfo.InvariantCulture));
                for (int t = 0; t < c.Taps.Count; t++)
                    ini.Put(p + "Tap" + t, c.Taps[t].Src.ToString(CultureInfo.InvariantCulture) + "*" + F(c.Taps[t].Gain));
            }

            ini.Put(KBindCount, Bindings.Count.ToString(CultureInfo.InvariantCulture));
            for (int j = 0; j < Bindings.Count; j++)
            {
                var b = Bindings[j];
                ini.Put("Bind" + j, b.ChannelKey + "," + b.SinkKey + "," +
                                    b.Phys.ToString(CultureInfo.InvariantCulture) + "," + F(b.Gain));
            }

            ini.Put(KSinkCount, SinkSpecs.Count.ToString(CultureInfo.InvariantCulture));
            for (int n = 0; n < SinkSpecs.Count; n++)
            {
                var s = SinkSpecs[n];
                string p = "Sink" + n + ".";
                ini.Put(p + "Key", s.Key);
                ini.Put(p + "Label", s.Label);
                ini.Put(p + "DeviceId", s.DeviceId ?? "");
                ini.Put(p + "Name", s.CachedName ?? "");
                ini.Put(p + "Enabled", s.Enabled ? "1" : "0");
                ini.Put(p + "DelayMs", s.DelayMs.ToString(CultureInfo.InvariantCulture));
                ini.Put(p + "ClockGroup", s.ClockGroupId ?? "");
                ini.Put(p + "Channels", s.OutChannels.ToString(CultureInfo.InvariantCulture));
                ini.Put(p + "Volume", ((int)Math.Round(s.EffectiveVolume * 100)).ToString(CultureInfo.InvariantCulture));
            }
        }

        /// <summary>
        /// 清掉本模型写过的全部键 (仅测试/重置用)。ini 是插入顺序表, 删键只能重写整个文件,
        /// 因此这里只做"键名前缀判定"的辅助, 供将来"恢复默认"调用。
        /// </summary>
        public static bool IsRoutingKey(string key)
        {
            if (string.IsNullOrEmpty(key)) return false;
            return key == KMode || key == KLayout || key == KChanCount || key == KBindCount || key == KSinkCount ||
                   key.StartsWith("Chan", StringComparison.Ordinal) ||
                   key.StartsWith("Bind", StringComparison.Ordinal) ||
                   key.StartsWith("Sink", StringComparison.Ordinal);
        }

        /// <summary>一句话摘要 (日志用): "split-lr 2 路 / 2 声道 / 2 绑定"。</summary>
        public string DescribeConfig()
        {
            var sb = new StringBuilder();
            sb.Append(ModeText(Mode)).Append(' ');
            int en = 0;
            for (int i = 0; i < SinkSpecs.Count; i++) if (SinkSpecs[i].Enabled) en++;
            sb.Append(en).Append('/').Append(SinkSpecs.Count).Append(" 路 / ")
              .Append(Channels.Count).Append(" 声道 / ")
              .Append(Bindings.Count).Append(" 绑定");
            return sb.ToString();
        }
    }
}
