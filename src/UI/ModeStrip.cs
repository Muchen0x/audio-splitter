using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 工作模式带: 5 张等宽模式卡片 + 一行摘要 + 内联确认条。
    ///
    /// 为什么不用下拉框: 下拉框只能看到当前值, 卡片带能让人一眼看到"有哪些可能",
    /// 而且当前模式自己亮着 —— 多设备路由的第一层心智模型必须始终可见。
    ///
    /// 切换是破坏性操作 (会重排清单与绑定), 因此清单非空时进入"待确认"状态:
    /// 卡片高亮跟着手指走, 摘要行变成一句确认 + [切换] [取消]，**不弹模态框**
    /// (模态会打断正在试听的人)。
    /// </summary>
    sealed class ModeStripControl : Control
    {
        // 画法与配色
        static readonly Color Accent = Color.FromArgb(0, 120, 215);
        static readonly Color AccentFill = Color.FromArgb(232, 242, 252);
        static readonly Color CardBorder = Color.FromArgb(208, 208, 208);
        static readonly Color CardFill = Color.FromArgb(250, 250, 250);
        static readonly Color TextMain = Color.FromArgb(32, 32, 32);
        static readonly Color TextSub = Color.FromArgb(110, 110, 110);
        static readonly Color WarnBorder = Color.FromArgb(240, 200, 130);
        static readonly Color WarnFill = Color.FromArgb(255, 248, 232);

        public const int StripHeight = 76;
        const int CardTop = 6;
        const int CardHeight = 46;
        const int Gap = 6;
        const int SideMargin = 12;
        const int LabelWidth = 34;   // 左侧"模式"两个字

        WorkMode mode = WorkMode.SplitLr;
        WorkMode pendingMode;
        bool confirming;
        string summary = "";
        string confirmText = "";
        int hoverIndex = -1;
        Rectangle[] confirmButtons = new Rectangle[2];

        /// <summary>用户点了某张模式卡片 (是否需要确认由宿主决定)。</summary>
        public event Action<WorkMode> ModePicked;
        /// <summary>待确认条上的「切换」。</summary>
        public event Action ConfirmAccepted;
        /// <summary>待确认条上的「取消」。</summary>
        public event Action ConfirmCancelled;

        public ModeStripControl()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = StripHeight;
            BackColor = SystemColors.Control;
            TabStop = false;
        }

        public WorkMode Mode
        {
            get { return mode; }
            set { if (mode != value) { mode = value; Invalidate(); } }
        }

        public string Summary
        {
            get { return summary; }
            set { if (summary != value) { summary = value ?? ""; Invalidate(); } }
        }

        /// <summary>进入待确认: 摘要行改显这句 + 两个按钮。传 null 退出。</summary>
        public void BeginConfirm(WorkMode target, string text)
        {
            pendingMode = target;
            confirmText = text ?? "";
            confirming = true;
            Invalidate();
        }

        public void CancelConfirm()
        {
            if (!confirming) return;
            confirming = false;
            confirmText = "";
            Invalidate();
        }

        public bool Confirming { get { return confirming; } }

        public WorkMode PendingMode { get { return pendingMode; } }

        // ---------------------------------------------------------------- 几何

        int CardWidth
        {
            get
            {
                int avail = Width - SideMargin * 2 - LabelWidth;
                int w = (avail - Gap * 4) / 5;
                return Math.Max(64, w);
            }
        }

        Rectangle CardRect(int i)
        {
            return new Rectangle(SideMargin + LabelWidth + i * (CardWidth + Gap), CardTop, CardWidth, CardHeight);
        }

        int HitCard(int x, int y)
        {
            for (int i = 0; i < 5; i++)
                if (CardRect(i).Contains(x, y)) return i;
            return -1;
        }

        static WorkMode ModeAt(int i)
        {
            switch (i)
            {
                case 0: return WorkMode.SplitLr;
                case 1: return WorkMode.MirrorStereo;
                case 2: return WorkMode.Surround;
                case 3: return WorkMode.MonoBroadcast;
                default: return WorkMode.Custom;
            }
        }

        public static string NameOf(WorkMode m)
        {
            switch (m)
            {
                case WorkMode.SplitLr: return "左右分离";
                case WorkMode.MirrorStereo: return "同源并播";
                case WorkMode.Surround: return "拼装环绕";
                case WorkMode.MonoBroadcast: return "单声广播";
                default: return "自定义";
            }
        }

        static string ModeName(int i)
        {
            switch (i)
            {
                case 0: return "左右分离";
                case 1: return "同源并播";
                case 2: return "拼装环绕";
                case 3: return "单声广播";
                default: return "自定义";
            }
        }

        static int IndexOfMode(WorkMode m)
        {
            switch (m)
            {
                case WorkMode.SplitLr: return 0;
                case WorkMode.MirrorStereo: return 1;
                case WorkMode.Surround: return 2;
                case WorkMode.MonoBroadcast: return 3;
                default: return 4;
            }
        }

        // ---------------------------------------------------------------- 交互

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int hit = HitCard(e.X, e.Y);
            if (hit != hoverIndex) { hoverIndex = hit; Invalidate(); }
            Cursor = hit >= 0 ? Cursors.Hand : Cursors.Default;
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            if (hoverIndex != -1) { hoverIndex = -1; Invalidate(); }
            base.OnMouseLeave(e);
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            if (confirming)
            {
                if (confirmButtons[0].Contains(e.Location))
                {
                    var h = ConfirmAccepted;
                    if (h != null) h();
                    return;
                }
                if (confirmButtons[1].Contains(e.Location))
                {
                    CancelConfirm();
                    var c = ConfirmCancelled;
                    if (c != null) c();
                    return;
                }
            }

            int hit = HitCard(e.X, e.Y);
            if (hit < 0) return;
            var handler = ModePicked;
            if (handler != null) handler(ModeAt(hit));
            base.OnMouseClick(e);
        }

        // ---------------------------------------------------------------- 绘制

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var br = new SolidBrush(TextSub))
            using (var f = new Font("Microsoft YaHei UI", 9F))
                g.DrawString("模式", f, br, SideMargin, CardTop + 14);

            int active = IndexOfMode(confirming ? pendingMode : mode);
            for (int i = 0; i < 5; i++)
            {
                var r = CardRect(i);
                bool sel = (i == active);
                DrawCard(g, r, i, sel, i == hoverIndex);
            }

            if (confirming) DrawConfirmRow(g);
            else DrawSummaryRow(g);
        }

        void DrawCard(Graphics g, Rectangle r, int idx, bool selected, bool hover)
        {
            using (var fill = new SolidBrush(selected ? AccentFill : (hover ? Color.FromArgb(242, 246, 250) : CardFill)))
            using (var pen = new Pen(selected ? Accent : CardBorder, selected ? 2f : 1f))
            {
                g.FillRectangle(fill, r);
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            }

            DrawGlyph(g, idx, new Rectangle(r.X, r.Y + 7, r.Width, 16), selected ? Accent : Color.FromArgb(120, 120, 120));

            using (var br = new SolidBrush(selected ? Accent : TextMain))
            using (var f = new Font("Microsoft YaHei UI", 8.5F))
            {
                var sz = g.MeasureString(ModeName(idx), f);
                g.DrawString(ModeName(idx), f, br,
                    r.X + (r.Width - sz.Width) / 2, r.Bottom - sz.Height - 5);
            }
        }

        /// <summary>8×8 示意图: 用几个小方块画出"声源怎么分到几台设备"。</summary>
        static void DrawGlyph(Graphics g, int idx, Rectangle area, Color color)
        {
            using (var br = new SolidBrush(color))
            {
                int cx = area.X + area.Width / 2;
                int top = area.Y;
                const int w = 7, h = 6;

                switch (idx)
                {
                    case 0:  // 左右分离: 两个方块被拉向两侧
                        g.FillRectangle(br, cx - 17, top + 2, w, h);
                        g.FillRectangle(br, cx + 10, top + 2, w, h);
                        g.FillRectangle(br, cx - 8, top + 9, 3, 3);
                        g.FillRectangle(br, cx + 5, top + 9, 3, 3);
                        break;
                    case 1:  // 同源并播: 两组各两个方块
                        g.FillRectangle(br, cx - 18, top + 3, 4, h);
                        g.FillRectangle(br, cx - 12, top + 3, 4, h);
                        g.FillRectangle(br, cx + 8, top + 3, 4, h);
                        g.FillRectangle(br, cx + 14, top + 3, 4, h);
                        g.FillRectangle(br, cx - 2, top + 3, 4, h);
                        break;
                    case 2:  // 拼装环绕: 一排六个方块
                        for (int i = 0; i < 6; i++)
                            g.FillRectangle(br, cx - 24 + i * 8, top + 4, w, h);
                        break;
                    case 3:  // 单声广播: 中间一个方块
                        g.FillRectangle(br, cx - 4, top + 2, 8, 8);
                        g.FillRectangle(br, cx - 16, top + 4, 4, 4);
                        g.FillRectangle(br, cx + 12, top + 4, 4, 4);
                        break;
                    default: // 自定义: 齿轮近似 (中心方 + 四向短线)
                        g.FillRectangle(br, cx - 4, top + 3, 8, 8);
                        g.FillRectangle(br, cx - 1, top, 2, 3);
                        g.FillRectangle(br, cx - 1, top + 11, 2, 3);
                        g.FillRectangle(br, cx - 7, top + 6, 3, 2);
                        g.FillRectangle(br, cx + 4, top + 6, 3, 2);
                        break;
                }
            }
        }

        void DrawSummaryRow(Graphics g)
        {
            int y = CardTop + CardHeight + 4;
            using (var dot = new SolidBrush(summary.StartsWith("⚠", StringComparison.Ordinal) ? Color.FromArgb(214, 120, 0) : Accent))
                g.FillEllipse(dot, SideMargin + 1, y + 4, 6, 6);
            using (var br = new SolidBrush(TextSub))
            using (var f = new Font("Microsoft YaHei UI", 9F))
                g.DrawString(Clip(g, summary, f, Width - SideMargin * 2 - 16), f, br, SideMargin + 13, y);
        }

        void DrawConfirmRow(Graphics g)
        {
            int y = CardTop + CardHeight + 2;
            var bar = new Rectangle(SideMargin, y, Width - SideMargin * 2, 20);
            using (var fill = new SolidBrush(WarnFill))
            using (var pen = new Pen(WarnBorder))
            {
                g.FillRectangle(fill, bar);
                g.DrawRectangle(pen, bar.X, bar.Y, bar.Width - 1, bar.Height - 1);
            }

            using (var f = new Font("Microsoft YaHei UI", 9F))
            using (var br = new SolidBrush(Color.FromArgb(120, 84, 20)))
            {
                // 两个按钮右对齐
                int bw = 48, bh = 16;
                int bx = bar.Right - 8 - bw * 2 - 6;
                confirmButtons[0] = new Rectangle(bx, y + 2, bw, bh);
                confirmButtons[1] = new Rectangle(bx + bw + 6, y + 2, bw, bh);

                g.DrawString(Clip(g, confirmText, f, confirmButtons[0].X - bar.X - 12), f, br, bar.X + 8, y + 3);

                DrawMiniButton(g, confirmButtons[0], "切换", true);
                DrawMiniButton(g, confirmButtons[1], "取消", false);
            }
        }

        void DrawMiniButton(Graphics g, Rectangle r, string text, bool accent)
        {
            using (var fill = new SolidBrush(accent ? Accent : Color.White))
            using (var pen = new Pen(accent ? Accent : CardBorder))
            {
                g.FillRectangle(fill, r);
                g.DrawRectangle(pen, r.X, r.Y, r.Width - 1, r.Height - 1);
            }
            using (var f = new Font("Microsoft YaHei UI", 8F))
            using (var br = new SolidBrush(accent ? Color.White : TextMain))
            {
                var sz = g.MeasureString(text, f);
                g.DrawString(text, f, br, r.X + (r.Width - sz.Width) / 2, r.Y + (r.Height - sz.Height) / 2);
            }
        }

        /// <summary>按像素宽度截断并加省略号 (MeasureString 每帧调用, 只在超宽时做二分)。</summary>
        static string Clip(Graphics g, string s, Font f, int maxWidth)
        {
            if (string.IsNullOrEmpty(s) || maxWidth <= 8) return s ?? "";
            if (g.MeasureString(s, f).Width <= maxWidth) return s;
            int lo = 0, hi = s.Length;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (g.MeasureString(s.Substring(0, mid) + "…", f).Width <= maxWidth) lo = mid;
                else hi = mid - 1;
            }
            return s.Substring(0, lo) + "…";
        }
    }
}
