using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>标题栏上的窗口键。Maximize / Restore 是同一个键的两个状态。</summary>
    enum CaptionGlyph { Minimize, Maximize, Restore, Close }

    /// <summary>
    /// 自绘窗口键 (最小化 / 最大化·还原 / 关闭)。
    ///
    /// <para><b>为什么不复用 GearButton</b>: 齿轮的悬停底是 6px 圆角的小方块, 那是"功能按钮"的观感;
    /// 窗口键彼此紧邻、贴在窗口右缘, 悬停底必须是**铺满整块的矩形** —— 否则键与键之间会露出一条条
    /// 窗底色, 三个键看起来是三个孤立的按钮, 而不是一组系统键。</para>
    ///
    /// <para><b>关闭键悬停变红</b>: Windows 的既有约定, 用户不用读字就知道哪个能关掉窗口。
    /// 其余两键只加深灰底 —— 红底留给"会丢掉当前界面"的那一个。</para>
    /// </summary>
    class CaptionButton : Control
    {
        static readonly Color Ink = Color.FromArgb(60, 60, 60);
        static readonly Color HoverBack = Color.FromArgb(229, 229, 229);
        static readonly Color PressBack = Color.FromArgb(211, 211, 211);
        static readonly Color CloseHover = Color.FromArgb(232, 17, 35);
        static readonly Color ClosePress = Color.FromArgb(243, 106, 116);

        bool hot, down;
        CaptionGlyph glyph;

        public CaptionButton(CaptionGlyph glyph)
        {
            this.glyph = glyph;
            SetStyle(ControlStyles.UserPaint | ControlStyles.OptimizedDoubleBuffer
                   | ControlStyles.AllPaintingInWmPaint, true);
            // 窗口键不进 Tab 序列: 系统标题栏上的键本来也走不到, 混进来只会打断表单的 Tab 顺序
            TabStop = false;
            AccessibleRole = AccessibleRole.PushButton;
        }

        /// <summary>字形。最大化键在窗口最大化后换成「还原」的两层方框。</summary>
        public CaptionGlyph Glyph
        {
            get { return glyph; }
            set { if (glyph == value) return; glyph = value; Invalidate(); }
        }

        bool IsClose { get { return glyph == CaptionGlyph.Close; } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;

            if (hot || down)
            {
                Color back = down ? (IsClose ? ClosePress : PressBack)
                                  : (IsClose ? CloseHover : HoverBack);
                using (var b = new SolidBrush(back)) g.FillRectangle(b, ClientRectangle);
            }

            Color ink = IsClose && (hot || down) ? Color.White : Ink;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // 图标边长 10 设计像素: 与系统标题栏的键同一观感 (16px 图标看着太胖)
            float s = Dpi.F(10f);
            float cx = (Width - 1) / 2f;
            float cy = (Height - 1) / 2f;
            float half = s / 2f;

            using (var pen = new Pen(ink, Dpi.F(1f)))
            {
                switch (glyph)
                {
                    case CaptionGlyph.Minimize:
                        // 半像素对齐: 1px 横线落在像素中心才是实心黑, 否则被拆成两条 50% 灰
                        g.DrawLine(pen, cx - half, (float)Math.Floor(cy) + 0.5f,
                                        cx + half, (float)Math.Floor(cy) + 0.5f);
                        break;

                    case CaptionGlyph.Maximize:
                        g.DrawRectangle(pen, cx - half, cy - half, s, s);
                        break;

                    case CaptionGlyph.Restore:
                        {
                            // 前面一块方框 + 右上层露出的两条边 (Windows 的「向下还原」图形)
                            float o = Math.Max(2f, Dpi.F(2f));
                            g.DrawRectangle(pen, cx - half + o, cy - half + o, s - o, s - o);
                            g.DrawLine(pen, cx - half + o * 2, cy - half, cx + half, cy - half);
                            g.DrawLine(pen, cx + half, cy - half, cx + half, cy + half - o);
                        }
                        break;

                    case CaptionGlyph.Close:
                        g.DrawLine(pen, cx - half, cy - half, cx + half, cy + half);
                        g.DrawLine(pen, cx - half, cy + half, cx + half, cy - half);
                        break;
                }
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { down = true; Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e)
        {
            if (down) { down = false; Invalidate(); }
            base.OnMouseUp(e);
        }
        protected override void OnEnabledChanged(EventArgs e)
        {
            if (!Enabled) { hot = false; down = false; }
            Invalidate();
            base.OnEnabledChanged(e);
        }
    }
}
