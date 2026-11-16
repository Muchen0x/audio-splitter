using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    /// <summary>
    /// 滚轮路由: 下拉框/数字框自己不吃滚轮, 而是把消息转给外层的 AutoScroll 容器。
    /// <para>以前这里是直接 return —— 值确实没被改, 但滚动链也在控件上断了: 鼠标滑过卡片上的
    /// 下拉或延迟框时, 输出卡片列表就滚不动, 得先把鼠标挪到空白处。这里改成转发,
    /// 控件自身依旧不改值, 滚动却照常发生。</para>
    /// </summary>
    static class WheelRouting
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        public const int WM_MOUSEWHEEL = 0x020A;

        /// <summary>
        /// 把滚轮转给最近一个「开了 AutoScroll 且当前真的能滚」的容器。
        /// <para>找到就滚它并返回 true; 找不到返回 false (调用方自行决定怎么处理)。</para>
        /// </summary>
        public static bool ForwardToScroller(Control self, Message m)
        {
            for (Control p = self.Parent; p != null; p = p.Parent)
            {
                if (!p.IsHandleCreated) continue;
                ScrollableControl sc = p as ScrollableControl;
                if (sc != null && sc.AutoScroll && sc.VerticalScroll.Visible)
                {
                    SendMessage(p.Handle, m.Msg, m.WParam, m.LParam);
                    return true;
                }
            }
            return false;
        }

        /// <summary>把滚轮转给最近的「AutoScroll 且能滚」的容器; 找不到就交给直接父窗口继续冒泡。</summary>
        public static void Forward(Control self, Message m)
        {
            if (ForwardToScroller(self, m)) return;

            Control nearest = null;
            for (Control p = self.Parent; p != null; p = p.Parent)
                if (p.IsHandleCreated) { nearest = p; break; }

            if (nearest != null)
                SendMessage(nearest.Handle, m.Msg, m.WParam, m.LParam);   // 交给父窗口按默认规则冒泡
        }
    }

    class NoWheelComboBox : ComboBox
    {
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WheelRouting.WM_MOUSEWHEEL)
            {
                // 下拉展开时滚轮归列表自己(滚动选项), 否则转给外层列表
                if (!DroppedDown) WheelRouting.Forward(this, m);
                return;
            }
            base.WndProc(ref m);
        }
    }

    // 鼠标滚轮不改值的 NumericUpDown (只允许手动输入/上下按钮, 每次 ±Increment)
    class NoWheelNumeric : NumericUpDown
    {
        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 不调 base: 滚轮对数字框一律无效 (误滚一下就把延迟拖偏, 很难发现)
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WheelRouting.WM_MOUSEWHEEL)
            {
                WheelRouting.Forward(this, m);
                return;
            }
            base.WndProc(ref m);
        }
    }

    // 不接受焦点的 SplitContainer: 点击/拖动分隔条不产生焦点虚线框
    class NoFocusSplitContainer : SplitContainer
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetFocus();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SetFocus(IntPtr hWnd);

        IntPtr restoreFocus = IntPtr.Zero;

        protected override void Select(bool directed, bool forward)
        {
        }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.Style &= ~0x00010000; // WS_TABSTOP: 去掉样式, 焦点虚线框无从绘制
                return cp;
            }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == 0x0201)                       // WM_LBUTTONDOWN: child 能收到, 此刻记录旧焦点 (Focus 尚未发生)
            {
                restoreFocus = GetFocus();
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == 0x0007)                       // WM_SETFOCUS: 焦点被抢到分隔条 → 立即还回
            {
                IntPtr target = (restoreFocus != IntPtr.Zero && restoreFocus != Handle) ? restoreFocus : IntPtr.Zero;
                if (target == IntPtr.Zero)
                {
                    Form f = FindForm();
                    if (f != null) target = f.Handle;  // 兜底还给窗体
                }
                if (target != IntPtr.Zero && target != Handle) SetFocus(target);
                return;
            }
            if (m.Msg == 0x0021)                       // WM_MOUSEACTIVATE (顶层才收得到, 保留无害)
            {
                m.Result = (IntPtr)3;                  // MA_NOACTIVATE
                return;
            }
            base.WndProc(ref m);
        }
    }

    /// <summary>
    /// 光标停在音量滑条上时的滚轮去向。
    /// <para>滑条刻意不抢焦点 (一抢焦点 AutoScroll 容器就会滚一下, 卡片列表会抖),
    /// 所以它收不到 WM_MOUSEWHEEL —— 这里按光标位置自己找控件。</para>
    /// <para>规则: **卡片列表正在滚动时, 滚轮只滚列表, 绝不改音量** —— 拖着列表往下划,
    /// 鼠标扫过某张卡片的音量条时音量不会莫名其妙跳 5%; 列表没东西可滚时(不显示竖滚动条)
    /// 才回落到原来的滚轮调音量, 免得白滚一场。</para>
    /// </summary>
    class VolumeWheelRouter : IMessageFilter
    {
        const int WM_MOUSEWHEEL = 0x020A;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_MOUSEWHEEL) return false;

            // 命中判定用消息自带的屏幕坐标 (就是滚轮真正发生的位置), 比读光标更可靠:
            // 光标可能被别的程序挪走, 而这条消息的坐标是投递那一刻定下的。
            long lp = m.LParam.ToInt64();
            Point at = new Point((short)(lp & 0xFFFF), (short)((lp >> 16) & 0xFFFF));
            VolumeSlider slider = VolumeSlider.HitTestScreen(at);
            if (slider == null) slider = VolumeSlider.HitTestScreen(Cursor.Position);
            if (slider == null) return false;

            // 卡片列表能滚 → 这一格滚列表, 滑条值不动 (吞掉消息, 免得又滚一次)
            if (WheelRouting.ForwardToScroller(slider, m)) return true;

            // 列表没得滚 → 保持原行为: 滚轮调这一格音量
            int delta = (short)((m.WParam.ToInt64() >> 16) & 0xFFFF);
            return slider.ApplyWheelDelta(delta);
        }
    }

    /// <summary>自绘用的公共绘制工具。</summary>
    static class UiDraw
    {
        /// <summary>圆角矩形路径 (radius 会被夹到不超过半高/半宽)。</summary>
        public static System.Drawing.Drawing2D.GraphicsPath RoundRect(Rectangle r, int radius)
        {
            var path = new System.Drawing.Drawing2D.GraphicsPath();
            int max = Math.Min(r.Width, r.Height) / 2;
            int d = Math.Min(radius, max) * 2;
            if (d <= 0)
            {
                path.AddRectangle(r);
            }
            else
            {
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
            }
            return path;
        }
    }
}
