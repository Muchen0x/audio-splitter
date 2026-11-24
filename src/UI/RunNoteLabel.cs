using System;
using System.Drawing;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 规格行的运行状态字。用户指定: 「在不影响旁边按键的情况下弄最宽, 若还是不够就滚动显示」。
    /// <para>自绘的原因: Label 超宽只有截断 (AutoEllipsis), 没有"滚动" —— 而状态字两侧
    /// 是规格下拉与「添加设备」, 宽度硬上限就是它们之间的空地, 超宽只能滚动。</para>
    /// <para>落位规则 (<see cref="PlaceIn"/>): 文字放得下 → 按文字宽居中于空地 (与声道标注
    /// 同一条居中语言); 放不下 → 占满整块空地, 平滑滚动, 循环里带一段空档, 头尾接缝不顶字。
    /// 滚动钟自管: 放得下/隐藏/释放时自停, 只有真在滚时才醒着。</para>
    /// </summary>
    class RunNoteLabel : Control
    {
        float scroll;           // 滚动偏移 (像素)
        int textW = -1;         // 当前文本实测宽 (-1 = 还没量过)
        int textH = -1;
        readonly Timer marquee;

        public RunNoteLabel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Visible = false;
            marquee = new Timer { Interval = 40 };
            marquee.Tick += (s, e) =>
            {
                if (!Visible || textW <= 0 || textW <= Width) { marquee.Stop(); return; }
                scroll += Dpi.F(2.6f);
                if (scroll >= textW + Gap()) scroll = 0f;
                Invalidate();
            };
        }

        static int Gap() { return Dpi.S(28); }   // 循环空档: 一口气读完行首, 再从头滚

        /// <summary>按可用空间落位: 放得下按文字宽居中, 放不下占满整块空地并滚动。
        /// 由 <see cref="MainForm.PlaceRunStateLabel"/> 在换文案和窗口缩放时调用。</summary>
        public void PlaceIn(Rectangle avail)
        {
            if (avail.Width <= 0) { marquee.Stop(); return; }
            var sz = TextRenderer.MeasureText(Text, Font);
            textW = sz.Width; textH = sz.Height;
            if (textW <= avail.Width)
            {
                marquee.Stop();
                scroll = 0f;
                Bounds = new Rectangle(avail.X + (avail.Width - textW) / 2,
                    avail.Y + Math.Max(0, (avail.Height - textH) / 2), textW, textH);
            }
            else
            {
                Bounds = avail;
                if (!marquee.Enabled) marquee.Start();
            }
            Invalidate();
        }

        protected override void OnTextChanged(EventArgs e)
        {
            base.OnTextChanged(e);
            Invalidate();
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            if (marquee == null) return;   // 构造期 Visible=false 就会进到这里, 滚动钟还没建
            if (!Visible) { marquee.Stop(); scroll = 0f; }
            else if (textW > Width && textW > 0) { scroll = 0f; marquee.Start(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            if (string.IsNullOrEmpty(Text)) return;
            if (Parent != null)
                using (var b = new SolidBrush(Parent.BackColor))
                    e.Graphics.FillRectangle(b, ClientRectangle);
            var f = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter;
            if (textW > Width && textW > 0)
            {
                // 跑马灯: 两份文案接力, 间距 Gap, 偏移循环 → 接缝处是空档不是半个字
                int x1 = -(int)scroll, x2 = x1 + textW + Gap();
                TextRenderer.DrawText(e.Graphics, Text, Font,
                    new Rectangle(x1, 0, textW, Height), ForeColor, f);
                TextRenderer.DrawText(e.Graphics, Text, Font,
                    new Rectangle(x2, 0, textW, Height), ForeColor, f);
            }
            else
                TextRenderer.DrawText(e.Graphics, Text, Font, ClientRectangle, ForeColor, f);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) marquee.Dispose();
            base.Dispose(disposing);
        }
    }
}
