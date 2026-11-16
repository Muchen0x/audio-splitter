using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace StereoSplitter.Infrastructure
{
    /// <summary>
    /// ini 设置存取 (exe 同目录 SplitterGUI.ini)。
    ///
    /// 为便于排查与复用, 这里只做"键值 + 原子写", 不知道任何控件:
    ///   - 保持插入顺序写出, 文件内容稳定、git diff 可读
    ///   - 写入走 .tmp + File.Replace, 崩溃时不会留下半截 ini
    /// </summary>
    sealed class SettingsStore
    {
        readonly string path;
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> order = new List<string>();

        public string FilePath
        {
            get { return path; }
        }

        /// <summary>上次 Load() 失败的原因 (null = 正常)。ini 损坏/被占用时静默回默认值, 靠这个字段留痕。</summary>
        public string LastLoadError;

        public SettingsStore(string path)
        {
            this.path = path;
        }

        public void Load()
        {
            values.Clear();
            order.Clear();
            LastLoadError = null;
            try
            {
                if (!File.Exists(path)) return;
                foreach (var line in File.ReadAllLines(path))
                {
                    int i = line.IndexOf('=');
                    if (i <= 0) continue;
                    string key = line.Substring(0, i).Trim();
                    if (key.Length == 0) continue;
                    Put(key, line.Substring(i + 1).Trim());
                }
            }
            catch (Exception ex) { LastLoadError = ex.GetType().Name + ": " + ex.Message; }
        }

        public bool Has(string key)
        {
            return values.ContainsKey(key);
        }

        public void Put(string key, string value)
        {
            if (key == null) return;
            if (!values.ContainsKey(key)) order.Add(key);
            values[key] = value ?? "";
        }

        public void PutInt(string key, int value)
        {
            Put(key, value.ToString());
        }

        /// <summary>删一个键 (值 + 顺序)。返回是否真的存在过。
        /// 路由配置整表重写时用它逐键清理 (ini 是"只覆盖不删除"的表, 不删就留孤儿键)。</summary>
        public bool Remove(string key)
        {
            if (key == null) return false;
            bool had = values.Remove(key);
            if (had) order.Remove(key);
            return had;
        }

        /// <summary>当前全部键的快照 (遍历中允许删除)。</summary>
        public List<string> SnapshotKeys()
        {
            return new List<string>(order);
        }

        /// <summary>删掉指定前缀的全部键 (清理"删了几路输出后留下的 OutN.* 孤儿键")。</summary>
        public void RemovePrefix(string prefix)
        {
            if (string.IsNullOrEmpty(prefix)) return;
            for (int i = order.Count - 1; i >= 0; i--)
            {
                if (order[i].StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    values.Remove(order[i]);
                    order.RemoveAt(i);
                }
            }
        }

        public string GetStr(string key)
        {
            string s;
            return values.TryGetValue(key, out s) ? s : null;
        }

        public int GetInt(string key, int def)
        {
            string s;
            int v;
            if (values.TryGetValue(key, out s) && int.TryParse(s, out v)) return v;
            return def;
        }

        /// <summary>按插入顺序原子写盘。失败时通过 error 返回原因 (由调用方决定如何告知用户)。</summary>
        public bool WriteAll(out string error)
        {
            error = null;
            try
            {
                var sb = new StringBuilder();
                for (int i = 0; i < order.Count; i++)
                {
                    string key = order[i];
                    string val;
                    if (!values.TryGetValue(key, out val)) continue;
                    sb.Append(key).Append('=').Append(val).Append("\r\n");
                }

                string tmp = path + ".tmp";
                // 无 BOM: 与旧版文件保持一致, 否则首个键名会被 BOM 污染导致读取失配
                File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
        }

        public bool WriteAll()
        {
            string ignored;
            return WriteAll(out ignored);
        }
    }
}
