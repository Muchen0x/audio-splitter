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

    /// <summary>
    /// 焦点虚线框抑制: WinForms 的按钮 / 复选框 / 下拉框拿到焦点就画一圈虚线焦点框,
    /// 鼠标点一下也留一个, 满屏虚线框很难看。
    /// <para>做法是截掉系统"显示焦点框"的 UI 状态请求 (WM_UPDATEUISTATE / UISF_HIDEFOCUS),
    /// 让 ShowFocusCues 永远返回 false。键盘用户看的是控件自己的高亮/边框, 不需要这圈虚线。</para>
    /// </summary>
    static class FocusCue
    {
        public const int WM_UPDATEUISTATE = 0x0129;
        const int UISF_HIDEFOCUS = 0x0001;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>把"要显示焦点框"的请求改写成"继续隐藏", 再交给基类处理。</summary>
        public static void Suppress(ref Message m)
        {
            long wp = m.WParam.ToInt64();
            int action = (int)(wp & 0xFFFF);          // 低 16 位 = 要改哪一位
            int state = (int)((wp >> 16) & 0xFFFF);   // 高 16 位 = 改成的值
            if (action == UISF_HIDEFOCUS && (state & UISF_HIDEFOCUS) == 0)
                m.WParam = new IntPtr((UISF_HIDEFOCUS << 16) | UISF_HIDEFOCUS);
        }

        /// <summary>句柄刚建好时先置成隐藏, 免得第一次点击前就带着焦点框。</summary>
        public static void HideNow(Control c)
        {
            if (c == null || !c.IsHandleCreated) return;
            SendMessage(c.Handle, WM_UPDATEUISTATE, new IntPtr((UISF_HIDEFOCUS << 16) | UISF_HIDEFOCUS), IntPtr.Zero);
        }
    }

    /// <summary>
    /// 双缓冲的 FlowLayoutPanel (卡片区)。
    /// <para>原生 FlowLayoutPanel 的 AutoScroll 在滚动/缩放时是一格一格擦背景再画子控件,
    /// 卡片一多就能看到整片闪白; 开双缓冲后整帧一次性贴上去, 滚动和缩放明显顺。</para>
    /// </summary>
    class BufferedFlowPanel : FlowLayoutPanel
    {
        public BufferedFlowPanel()
        {
            // DoubleBuffered 是 protected, 只能在子类里开
            DoubleBuffered = true;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint, true);
        }
    }

    /// <summary>点完不留虚线焦点框的按钮。</summary>
    class NoCueButton : Button
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FocusCue.HideNow(this);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == FocusCue.WM_UPDATEUISTATE) FocusCue.Suppress(ref m);
            base.WndProc(ref m);
        }
    }

    /// <summary>点完不留虚线焦点框的复选框 (原生 CheckBox 点一下就画一圈虚线)。</summary>
    class NoCueCheckBox : CheckBox
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FocusCue.HideNow(this);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == FocusCue.WM_UPDATEUISTATE) FocusCue.Suppress(ref m);
            base.WndProc(ref m);
        }
    }

    /// <summary>焦点交还窗体 / 输入框回车的公共动作。</summary>
    static class UiFocus
    {
        /// <summary>
        /// 把焦点交还给整个窗体。
        /// <para>数字框/文本框失去焦点时才会把编辑中的文字提交成值, 所以"回车提交并失焦"
        /// 和"点空白处焦点回到窗口"都走这一条路。</para>
        /// </summary>
        public static void ParkOnForm(Control c)
        {
            Form f = (c != null && !c.IsDisposed) ? c.FindForm() : null;
            if (f == null || f.IsDisposed) return;
            try
            {
                f.ActiveControl = null;                  // 当前控件失焦 → 编辑值提交
                if (f.CanFocus && !f.Focused) f.Focus(); // 焦点落到窗体本身
            }
            catch { }
        }
    }

    /// <summary>
    /// 点到空白处 (标签 / 面板 / 卡片底色 / 窗体背景) 时把焦点交回整个窗口。
    /// <para>不这么做的话: 在延迟框里输完数字, 点一下卡片空白, 那个框还带着焦点,
    /// 之后随便敲个方向键就又把值改回去了。点到真控件 (输入框/下拉框/按钮/滑条) 则不动,
    /// 它们自己会抢焦点。</para>
    /// </summary>
    class FocusParkRouter : IMessageFilter
    {
        const int WM_LBUTTONDOWN = 0x0201;

        public bool PreFilterMessage(ref Message m)
        {
            if (m.Msg != WM_LBUTTONDOWN) return false;
            Control hit = Control.FromChildHandle(m.HWnd);
            if (hit == null) return false;              // 下拉列表等非控件窗口: 不介入

            for (Control c = hit; c != null; c = c.Parent)
                if (IsInputLike(c)) return false;

            Form f = hit.FindForm();
            if (f == null || f.IsDisposed || !f.IsHandleCreated) return false;
            // 延后一拍: 先让这次点击按正常规则落地 (该抢焦点的抢完), 再把焦点收回窗体
            try { f.BeginInvoke((Action)(() => UiFocus.ParkOnForm(f))); }
            catch { }
            return false;                               // 消息照常派发, 点击本身不受影响
        }

        /// <summary>真正需要键盘焦点的控件 (点它们不抢焦点, 否则没法输入/选字)。</summary>
        static bool IsInputLike(Control c)
        {
            if (c is TextBoxBase) return true;   // 日志框: 点进去要能选文字/复制
            if (c is ComboBox) return true;      // 下拉框: 展开要用焦点, 收起后自己还焦点
            if (c is UpDownBase) return true;
            if (c is ButtonBase) return true;
            if (c is ListBox) return true;
            return false;
        }
    }

    class NoWheelComboBox : ComboBox
    {
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FocusCue.HideNow(this);
        }

        protected override void OnDropDownClosed(EventArgs e)
        {
            base.OnDropDownClosed(e);
            // 收起下拉后立刻把焦点交回窗体: 下拉框留着焦点, 系统主题会给它描一圈
            // 蓝色聚焦边框 (点两下 = 展开一次再收起, 那一圈就留下了)。
            // 延后一拍: 让"收起的这次点击"先走完, 再改焦点。
            try { BeginInvoke((Action)(() => UiFocus.ParkOnForm(this))); }
            catch { }
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WheelRouting.WM_MOUSEWHEEL)
            {
                // 下拉展开时滚轮归列表自己(滚动选项), 否则转给外层列表
                if (!DroppedDown) WheelRouting.Forward(this, m);
                return;
            }
            if (m.Msg == FocusCue.WM_UPDATEUISTATE) FocusCue.Suppress(ref m);
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            FocusCue.HideNow(this);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            // 回车 = 提交 + 失焦: 数字框是"失去焦点才把编辑中的文字变成值",
            // 所以这里直接把焦点交回窗体, 值顺带落定 (再敲键盘也不会改到它)。
            if (e.KeyCode == Keys.Enter && !e.Alt && !e.Control)
            {
                e.Handled = true;
                e.SuppressKeyPress = true;
                UiFocus.ParkOnForm(this);
                return;
            }
            base.OnKeyDown(e);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WheelRouting.WM_MOUSEWHEEL)
            {
                WheelRouting.Forward(this, m);
                return;
            }
            if (m.Msg == FocusCue.WM_UPDATEUISTATE) FocusCue.Suppress(ref m);
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
