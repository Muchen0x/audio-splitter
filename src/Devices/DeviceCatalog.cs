using System;
using System.Collections.Generic;
using NAudio.CoreAudioApi;

namespace StereoSplitter.Devices
{
    /// <summary>
    /// 输出设备目录: 负责枚举、按 ID 解析、虚拟设备识别。
    ///
    /// 关键约束 (RCW 崩溃的根因修复):
    ///   本类持有的 MMDevice 仅用于枚举展示, 绝不参与端点音量/静音操作。
    ///   需要操作端点时调用 Open(id) 新建独立实例, 由调用方 (EndpointVolumeBinding) 负责释放。
    ///   因此刷新设备时可以放心 Dispose 全部旧实例, 不会把正在使用的端点 RCW 一起拆掉。
    /// </summary>
    sealed class DeviceCatalog : IDisposable
    {
        readonly MMDeviceEnumerator enumerator = new MMDeviceEnumerator();
        readonly List<MMDevice> owned = new List<MMDevice>();
        readonly List<EndpointInfo> endpoints = new List<EndpointInfo>();

        public IReadOnlyList<EndpointInfo> Endpoints
        {
            get { return endpoints; }
        }

        public MMDeviceEnumerator Enumerator
        {
            get { return enumerator; }
        }

        /// <summary>重新枚举活动输出设备; 只释放自己拥有的展示用实例。</summary>
        public void Refresh()
        {
            ReleaseOwned();
            endpoints.Clear();

            var coll = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
            for (int i = 0; i < coll.Count; i++)
            {
                var dev = coll[i];
                owned.Add(dev);
                endpoints.Add(new EndpointInfo
                {
                    ID = SafeId(dev),
                    FriendlyName = SafeName(dev),
                    IsVirtual = IsVirtualName(SafeName(dev))
                });
            }
        }

        /// <summary>按 ID 新建独立 MMDevice 实例; 设备不存在/已拔除返回 null。</summary>
        public MMDevice Open(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            try { return enumerator.GetDevice(id); }
            catch { return null; }
        }

        public int IndexOf(string id)
        {
            if (string.IsNullOrEmpty(id)) return -1;
            for (int i = 0; i < endpoints.Count; i++)
                if (endpoints[i].ID == id) return i;
            return -1;
        }

        public EndpointInfo Find(string id)
        {
            int i = IndexOf(id);
            return i >= 0 ? endpoints[i] : null;
        }

        /// <summary>按 ID 取友好名; 找不到或设备已拔除时回退为 ID 本身, 不抛异常。</summary>
        public string NameOf(string id)
        {
            var e = Find(id);
            return e != null ? e.FriendlyName : (id ?? "(未绑定)");
        }

        public string IdAt(int index)
        {
            return (index >= 0 && index < endpoints.Count) ? endpoints[index].ID : "";
        }

        /// <summary>当前系统默认输出 (Multimedia 角色) 的 ID; 失败返回 null。</summary>
        public string DefaultRenderId()
        {
            try
            {
                // F-43: 用 using 保证 ID 读取抛异常时 MMDevice 仍被释放
                using (var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    return def == null ? null : def.ID;
                }
            }
            catch { return null; }
        }

        public string DefaultRenderName()
        {
            try
            {
                // F-43: 同上, SafeName 之外也用 using 兜住释放
                using (var def = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia))
                {
                    return SafeName(def);
                }
            }
            catch { return "(未知)"; }
        }

        // ---------- 静态工具 ----------

        public static string SafeName(MMDevice d)
        {
            if (d == null) return "(未知)";
            try { return d.FriendlyName; } catch { return "(已断开设备)"; }
        }

        static string SafeId(MMDevice d)
        {
            try { return d.ID; } catch { return null; }
        }

        /// <summary>
        /// 虚拟/回环设备识别 (装机器、虚拟声卡、Steam 串流)。命名规则沿用原实现, 不扩大范围以免误判。
        /// </summary>
        public static bool IsVirtualName(string n)
        {
            if (string.IsNullOrEmpty(n)) return false;
            return n.IndexOf("CABLE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("虚拟", StringComparison.Ordinal) >= 0 ||
                   n.IndexOf("MIXLINE", StringComparison.OrdinalIgnoreCase) >= 0 ||
                   n.IndexOf("Steam", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>端点传输类型原始串 (PKEY_Device_EnumeratorName): BTHENUM/USB/HDAUDIO…, 取不到返回空串。</summary>
        public static string TransportName(MMDevice d)
        {
            if (d == null) return "";
            try
            {
                // PKEY_Device_EnumeratorName (a45c254e-...,24) → "BTHENUM" / "USB" / "HDAUDIO"
                // 注意: NAudio 的 PropertyStore 索引器返回 PropertyStoreProperty, 必须取 .Value,
                // 直接 ToString() 只会得到类型名 (曾打出 "NAudio.CoreAudioApi.PropertyStoreProperty")
                var key = new PropertyKey(new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), 24);
                var v = d.Properties[key];
                object o = v == null ? null : v.Value;
                string s = o == null ? null : o.ToString();
                return s == null ? "" : s.Trim();
            }
            catch { return ""; }
        }

        /// <summary>把传输类型翻成人话, 用于启动日志。</summary>
        public static string TransportText(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "未知";
            if (raw.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase)) return "蓝牙";
            if (raw.StartsWith("USB", StringComparison.OrdinalIgnoreCase)) return "USB";
            if (raw.StartsWith("HDAUDIO", StringComparison.OrdinalIgnoreCase)) return "板载";
            if (raw.StartsWith("SWD", StringComparison.OrdinalIgnoreCase)) return "虚拟/软件";
            return raw;
        }

        public static bool IsBluetooth(string raw)
        {
            return !string.IsNullOrEmpty(raw) &&
                   raw.StartsWith("BTHENUM", StringComparison.OrdinalIgnoreCase);
        }

        void ReleaseOwned()
        {
            for (int i = 0; i < owned.Count; i++)
            {
                try { owned[i].Dispose(); } catch { }
            }
            owned.Clear();
        }

        public void Dispose()
        {
            ReleaseOwned();
            endpoints.Clear();
            try { enumerator.Dispose(); } catch { }
        }
    }
}
