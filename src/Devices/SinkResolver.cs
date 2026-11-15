using System;
using System.Collections.Generic;

namespace StereoSplitter.Devices
{
    /// <summary>
    /// 设备解析 (P2): 保存的是端点 ID, 但 ID 会因重装驱动、换 USB 口、蓝牙重新配对而变化。
    /// 因此按 ID → 全名 → 归一化名 三级回落, 到不了才认"设备不在"。
    ///
    /// 不做模糊打分/编辑距离: 音响名字彼此太像 ("Speakers (Realtek Audio)" vs
    /// "Speakers (USB Audio)"), 打分会把用户配好的路由悄悄接到错的设备上 ——
    /// 接错比接不上更难排查, 所以只做确定性的三级匹配。
    /// </summary>
    sealed class SinkResolver
    {
        public enum MatchKind { ExactId, ExactName, FuzzyName, NotFound }

        public sealed class Result
        {
            public MatchKind Kind;
            public string DeviceId;      // NotFound 时为 null
            public string FriendlyName;  // NotFound 时为 null

            public bool Found { get { return Kind != MatchKind.NotFound; } }

            public string KindText
            {
                get
                {
                    switch (Kind)
                    {
                        case MatchKind.ExactId: return "ID 命中";
                        case MatchKind.ExactName: return "按名字命中";
                        case MatchKind.FuzzyName: return "按名字(去序号)命中";
                        default: return "未找到";
                    }
                }
            }
        }

        /// <summary>
        /// 解析一路输出。taken 里是已被其它启用路占用的端点 ID: 同名设备有多台时跳过它们,
        /// 避免两路解析到同一台 (那会让其中一路静音)。
        /// </summary>
        public static Result Resolve(SinkSpec cfg, DeviceCatalog catalog, ICollection<string> taken)
        {
            var r = new Result { Kind = MatchKind.NotFound };
            if (catalog == null || cfg == null) return r;

            // 1) ID 精确命中
            if (!string.IsNullOrEmpty(cfg.DeviceId))
            {
                var e = catalog.Find(cfg.DeviceId);
                if (e != null)
                {
                    r.Kind = MatchKind.ExactId;
                    r.DeviceId = e.ID;
                    r.FriendlyName = e.FriendlyName;
                    return r;
                }
            }

            string want = cfg.CachedName;
            if (string.IsNullOrEmpty(want)) return r;

            // 2) 全名精确 (同名多台时取第一台没被占用的)
            var hit = Pick(catalog.Endpoints, want, taken, false);
            if (hit != null)
            {
                r.Kind = MatchKind.ExactName;
                r.DeviceId = hit.ID;
                r.FriendlyName = hit.FriendlyName;
                return r;
            }

            // 3) 归一化名 (去掉 "2- " 序号前缀、"默认设备" 后缀等)
            hit = Pick(catalog.Endpoints, want, taken, true);
            if (hit != null)
            {
                r.Kind = MatchKind.FuzzyName;
                r.DeviceId = hit.ID;
                r.FriendlyName = hit.FriendlyName;
                return r;
            }

            return r;
        }

        static EndpointInfo Pick(IReadOnlyList<EndpointInfo> list, string want, ICollection<string> taken, bool normalized)
        {
            if (list == null) return null;
            string key = normalized ? NormalizeName(want) : want.Trim();
            if (key.Length == 0) return null;
            for (int i = 0; i < list.Count; i++)
            {
                var e = list[i];
                if (e == null || string.IsNullOrEmpty(e.ID)) continue;
                if (taken != null && taken.Contains(e.ID)) continue;
                string name = normalized ? NormalizeName(e.FriendlyName) : (e.FriendlyName ?? "").Trim();
                if (string.Equals(name, key, StringComparison.OrdinalIgnoreCase)) return e;
            }
            return null;
        }

        /// <summary>
        /// 归一化友好名: 去首尾空白、去 "默认设备"/"(Default)" 修饰、去重复计数前缀 ("2- USB")、
        /// 去 "(2- ...)" 这类括号里的枚举序号, 压缩连续空格。
        /// 注意不能去掉整个括号 —— 括号里通常是驱动名 ("Speakers (Realtek Audio)"),
        /// 那是区分同型号多台设备的主要线索。
        /// </summary>
        public static string NormalizeName(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            string t = s.Trim();

            // 前缀 "1- " / "2- " (WASAPI 对同名设备的编号)
            int dash = t.IndexOf("- ", StringComparison.Ordinal);
            if (dash > 0 && dash <= 3 && AllDigits(t, 0, dash)) t = t.Substring(dash + 2).Trim();

            // 后缀 "默认设备" / "(默认设备)" / "Default Device"
            t = StripSuffix(t, "默认设备");
            t = StripSuffix(t, "Default Device");
            t = StripSuffix(t, "(Default)");

            // 括号内的枚举序号 "(2- USB Audio)" → "(USB Audio)"
            t = StripIndexInParens(t);

            // 压缩空格
            var sb = new System.Text.StringBuilder(t.Length);
            bool space = false;
            for (int i = 0; i < t.Length; i++)
            {
                char c = t[i];
                if (c == ' ' || c == '\t') { space = true; continue; }
                if (space && sb.Length > 0) sb.Append(' ');
                space = false;
                sb.Append(c);
            }
            return sb.ToString().Trim();
        }

        static bool AllDigits(string s, int start, int end)
        {
            if (end <= start) return false;
            for (int i = start; i < end; i++)
                if (s[i] < '0' || s[i] > '9') return false;
            return true;
        }

        static string StripSuffix(string s, string suffix)
        {
            if (suffix.Length == 0 || s.Length < suffix.Length) return s;
            if (!s.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) return s;
            return s.Substring(0, s.Length - suffix.Length).Trim();
        }

        static string StripIndexInParens(string s)
        {
            int open = s.IndexOf('(');
            if (open < 0) return s;
            int close = s.IndexOf(')', open + 1);
            if (close < 0) return s;
            string inner = s.Substring(open + 1, close - open - 1);
            string trimmed = inner.TrimStart();
            int dash = trimmed.IndexOf("- ", StringComparison.Ordinal);
            if (dash <= 0 || dash > 3 || !AllDigits(trimmed, 0, dash)) return s;
            string fixedInner = trimmed.Substring(dash + 2).Trim();
            return s.Substring(0, open + 1) + fixedInner + s.Substring(close);
        }
    }
}
