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
    ///   - 写入走唯一 .tmp + File.Replace, 崩溃时不会留下半截 ini
    ///   - R-02: WriteAll 在跨进程锁内重新读取磁盘内容, 只合并本进程 Put/Remove 产生的变化
    /// </summary>
    sealed class SettingsStore
    {
        readonly string path;
        readonly Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly List<string> order = new List<string>();
        readonly HashSet<string> dirty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> removed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> removedPrefixes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
            dirty.Clear();
            removed.Clear();
            removedPrefixes.Clear();
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
                    PutCore(key, line.Substring(i + 1).Trim());   // Load 是建立基线, 不算本进程改动
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
            PutCore(key, value);
            dirty.Add(key);
            removed.Remove(key);
        }

        public void PutInt(string key, int value)
        {
            Put(key, value.ToString());
        }

        void PutCore(string key, string value)
        {
            if (key == null) return;
            if (!values.ContainsKey(key)) order.Add(key);
            values[key] = value ?? "";
        }

        /// <summary>删一个键 (值 + 顺序)。返回是否真的存在过。
        /// 路由配置整表重写时用它逐键清理 (ini 是"只覆盖不删除"的表, 不删就留孤儿键)。</summary>
        public bool Remove(string key)
        {
            if (key == null) return false;
            bool had = values.Remove(key);
            // F-43: values 用 OrdinalIgnoreCase, order 也按同一规则删;
            // 否则大小写不同的键会在 order 留下重复顺序项, 下次 Put 又追加一遍。
            for (int i = order.Count - 1; i >= 0; i--)
                if (string.Equals(order[i], key, StringComparison.OrdinalIgnoreCase))
                    order.RemoveAt(i);
            dirty.Remove(key);
            removed.Add(key);   // 即使本地没读到, 也阻止写盘时把外部新加的同名键带回来
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
            removedPrefixes.Add(prefix);   // 写盘时也清掉外部进程新加的同前缀键
            for (int i = order.Count - 1; i >= 0; i--)
            {
                string key = order[i];
                if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    values.Remove(key);
                    order.RemoveAt(i);
                    dirty.Remove(key);
                    removed.Add(key);
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

        /// <summary>按插入顺序原子写盘。失败时通过 error 返回原因 (由调用方决定如何告知用户)。
        /// R-02: 跨进程锁包住"重新读取现有 ini → 合并本进程脏键/删除键 → 写临时 → 刷新 → 原子替换";
        /// 每个写者用唯一临时名; 关闭临时文件前 Flush(true); 替换时保留 .bak 可恢复备份。</summary>
        public bool WriteAll(out string error)
        {
            error = null;
            System.Threading.Mutex gate = null;
            bool held = false;
            string tmp = null;
            try
            {
                gate = new System.Threading.Mutex(false, LockName(path));
                try { held = gate.WaitOne(System.TimeSpan.FromSeconds(5)); }
                catch (System.Threading.AbandonedMutexException) { held = true; } // 上个写者崩溃, 锁已被释放
                if (!held) { error = "等待配置文件跨进程锁超时 (5s), 未写入"; return false; }

                Dictionary<string, string> diskValues;
                List<string> diskOrder;
                ReadDiskSnapshot(path, out diskValues, out diskOrder);

                // 先应用 RemovePrefix 意图: 外部进程新加的同前缀键也不能留成孤儿键
                foreach (string prefix in removedPrefixes)
                {
                    for (int i = diskOrder.Count - 1; i >= 0; i--)
                    {
                        string key = diskOrder[i];
                        if (key.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                        {
                            diskValues.Remove(key);
                            diskOrder.RemoveAt(i);
                        }
                    }
                }
                // 再应用本进程明确删除过的键; 外部进程新加或未改动的键保留
                foreach (string key in removed)
                {
                    diskValues.Remove(key);
                    RemoveFromOrder(diskOrder, key);
                }
                // 只应用本进程明确 Put 过的键; 外部对其它键的并发修改不会被旧内存镜像覆盖
                foreach (KeyValuePair<string, string> kv in values)
                {
                    if (!dirty.Contains(kv.Key)) continue;
                    if (!diskValues.ContainsKey(kv.Key)) diskOrder.Add(kv.Key);
                    diskValues[kv.Key] = kv.Value;
                }

                var finalOrder = new List<string>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (int i = 0; i < diskOrder.Count; i++)
                {
                    string key = diskOrder[i];
                    if (diskValues.ContainsKey(key) && seen.Add(key)) finalOrder.Add(key);
                }

                var sb = new StringBuilder();
                for (int i = 0; i < finalOrder.Count; i++)
                {
                    string key = finalOrder[i];
                    string val;
                    if (!diskValues.TryGetValue(key, out val)) continue;
                    sb.Append(key).Append('=').Append(val).Append("\r\n");
                }

                tmp = path + ".tmp." + Guid.NewGuid().ToString("N");
                // 无 BOM: 与旧版文件保持一致, 否则首个键名会被 BOM 污染导致读取失配
                byte[] data = new UTF8Encoding(false).GetBytes(sb.ToString());
                using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    fs.Write(data, 0, data.Length);
                    fs.Flush(true);   // 刷到物理介质后再关闭, 降低断电后替换出空内容的风险
                }

                if (File.Exists(path))
                {
                    string backup = path + ".bak";
                    try
                    {
                        // 第三参数保留替换前内容为可恢复备份
                        File.Replace(tmp, path, backup);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        // exFAT/FAT32/网络盘不支持 File.Replace: 退化为"备份 + 覆盖"
                        File.Copy(path, backup, true);
                        File.Copy(tmp, path, true);
                    }
                }
                else File.Move(tmp, path);

                // 写盘成功后把外部并发改动并回内存, 避免下一次保存又用旧镜像覆盖
                values.Clear();
                foreach (KeyValuePair<string, string> kv in diskValues) values[kv.Key] = kv.Value;
                order.Clear();
                order.AddRange(finalOrder);
                dirty.Clear();
                removed.Clear();
                removedPrefixes.Clear();
                return true;
            }
            catch (Exception ex)
            {
                error = ex.GetType().Name + ": " + ex.Message;
                return false;
            }
            finally
            {
                if (held) { try { gate.ReleaseMutex(); } catch { } }
                if (gate != null) { try { gate.Dispose(); } catch { } }
                // 主路径已把 tmp 移走; 失败/回退残留的临时文件在此清理 (.bak 保留以便恢复)
                if (tmp != null) { try { if (File.Exists(tmp)) File.Delete(tmp); } catch { } }
            }
        }

        /// <summary>在锁内重新读取磁盘镜像; 读失败时由 WriteAll 返回错误, 不继续覆盖。</summary>
        static void ReadDiskSnapshot(string path, out Dictionary<string, string> diskValues, out List<string> diskOrder)
        {
            diskValues = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            diskOrder = new List<string>();
            if (!File.Exists(path)) return;
            foreach (string line in File.ReadAllLines(path))
            {
                int i = line.IndexOf('=');
                if (i <= 0) continue;
                string key = line.Substring(0, i).Trim();
                if (key.Length == 0) continue;
                if (!diskValues.ContainsKey(key)) diskOrder.Add(key);
                diskValues[key] = line.Substring(i + 1).Trim();
            }
        }

        static void RemoveFromOrder(List<string> order, string key)
        {
            for (int i = order.Count - 1; i >= 0; i--)
                if (string.Equals(order[i], key, StringComparison.OrdinalIgnoreCase))
                    order.RemoveAt(i);
        }

        /// <summary>由文件全路径派生稳定的命名锁。用 Local\ 前缀保证当前会话互斥;
        /// 若改 Global\ 跨会话, 需显式配置 MutexSecurity ACL, 否则低权限进程打开会抛异常。</summary>
        static string LockName(string path)
        {
            string full;
            try { full = Path.GetFullPath(path).ToUpperInvariant(); }
            catch { full = (path ?? "").ToUpperInvariant(); }
            ulong h = 14695981039346656037UL;   // FNV-1a, 避免引入额外依赖
            for (int i = 0; i < full.Length; i++) { h ^= full[i]; h *= 1099511628211UL; }
            return "Local\\StereoSplitter_ini_" + h.ToString("X16");
        }

        public bool WriteAll()
        {
            string ignored;
            return WriteAll(out ignored);
        }
    }
}
