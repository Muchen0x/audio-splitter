using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 滚轮路由: 光标落在可滚动列表里时, 把 WM_MOUSEWHEEL 转给那个列表。
    ///
    /// 为什么必须在 Application 层拦: TrackBar / NumericUpDown / ComboBox 自己会吃掉滚轮,
    /// 光标停在下拉框或数字框上滚一下, 就是"顺手把延迟改了 10ms"。原来只给 ComboBox 套了
    /// NoWheelComboBox, 但设备行里数字框/滑条都有, 逐个堵既啰嗦又漏。
    /// 这里一处统一: 只要光标在列表区域内, 滚轮一律归列表, 行内控件不再需要 NoWheel* 变体。
    /// </summary>
    sealed class WheelRouter : IMessageFilter
    {
        const int WM_MOUSEWHEEL = 0x020A;

        [DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(Point pt);

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        readonly List<Control> hosts = new List<Control>();

        public void Add(Control host)
        {
            if (host != null && !hosts.Contains(host)) hosts.Add(host);
        }

        public void Remove(Control host)
        {
            hosts.Remove(host);
        }

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL || hosts.Count == 0) return false;

            Point screen = Cursor.Position;
            for (int i = 0; i < hosts.Count; i++)
            {
                var host = hosts[i];
                if (host == null || host.IsDisposed || !host.Visible || !host.IsHandleCreated) continue;

                Rectangle area;
                try { area = host.RectangleToScreen(host.ClientRectangle); }
                catch { continue; }
                if (!area.Contains(screen)) continue;

                // 命中的最深控件本身就是"能滚的容器" → 交给默认处理, 别抢
                IntPtr hwnd = WindowFromPoint(screen);
                Control under = hwnd == IntPtr.Zero ? null : Control.FromChildHandle(hwnd);
                if (under == null || under == host) return false;

                // lParam 用屏幕坐标 (WM_MOUSEWHEEL 的约定), wParam 原样带过高位 delta
                int lp = ((screen.Y & 0xFFFF) << 16) | (screen.X & 0xFFFF);
                SendMessage(host.Handle, WM_MOUSEWHEEL, m.WParam, (IntPtr)lp);
                return true;
            }
            return false;
        }
    }
}
