using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 自绘音量滑条 (替代原生 TrackBar)。
    /// 原生 TrackBar 是 comctl32 控件: 轨道固定灰蓝配色、不能自绘, 且轨道画在控件顶部
    /// +10px 处, 控件底部十几像素纯属占位 —— 卡片里就成了"下面一大块空白"。
    /// 这里自己画: 已滑过的左段填蓝色, 右侧浅灰, 滑块白底蓝边, 轨道垂直居中。
    ///
    /// 行为对齐原 TrackBar:
    ///   - 点滑块拖 = 连续跟随; 点轨道 = ±LargeChange 一次 (与"点击每次 5%"一致)
    ///   - 方向键 ±SmallChange, PageUp/PageDown ±LargeChange, Home/End 到两端
    ///   - ValueChanged 任何变化都发; Scroll 只在用户操作时发 (程序赋值不发, 免得回写循环)
    /// </summary>
    class VolumeSlider : Control
    {
        int minimum = 0;
        int maximum = 100;
        int value = 100;
        int smallChange = 1;
        int largeChange = 5;
        bool dragging;
        bool hoverThumb;

        static readonly Color TrackBack = Color.FromArgb(216, 219, 224);
        static readonly Color Accent = Color.FromArgb(0, 120, 215);
        static readonly Color AccentHot = Color.FromArgb(0, 95, 184);
        static readonly Color DisabledFill = Color.FromArgb(198, 202, 208);
        static readonly Color DisabledTrack = Color.FromArgb(226, 228, 232);

        public event EventHandler ValueChanged;
        /// <summary>用户拖动/点击/键盘改变值时触发 (程序改 Value 不触发)。</summary>
        public event EventHandler Scroll;

        /// <summary>
        /// 当前活着的滑条 (按屏幕矩形做命中判定)。
        /// <para>不用 WindowFromPoint: 光标下可能压着别的进程的小窗 (桌面小工具、输入法候选、
        /// 屏幕录制浮条……), 命中会落到那个窗口上, 结果滑条既收不到滚轮、也不会被识别成滑条,
        /// 表现为"鼠标明明在音量条上滚, 却什么都不发生"。按自己的矩形判定不受任何遮挡干扰。</para>
        /// </summary>
        static readonly System.Collections.Generic.List<VolumeSlider> live =
            new System.Collections.Generic.List<VolumeSlider>();

        /// <summary>屏幕坐标命中的滑条 (取最后创建的那个, 即 Z 序最上)。找不到返回 null。</summary>
        public static VolumeSlider HitTestScreen(Point screenPoint)
        {
            for (int i = live.Count - 1; i >= 0; i--)
            {
                VolumeSlider s = live[i];
                if (s == null || s.IsDisposed || !s.Visible || !s.Enabled) continue;
                if (s.RectangleToScreen(s.ClientRectangle).Contains(screenPoint)) return s;
            }
            return null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (!live.Contains(this)) live.Add(this);
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            live.Remove(this);
            base.OnHandleDestroyed(e);
        }

        public VolumeSlider()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = 20;
            TabStop = true;
        }

        public int Minimum
        {
            get { return minimum; }
            set
            {
                if (minimum == value) return;
                minimum = value;
                if (maximum < minimum) maximum = minimum;
                Reclamp();
            }
        }

        public int Maximum
        {
            get { return maximum; }
            set
            {
                if (maximum == value) return;
                maximum = value;
                if (maximum < minimum) minimum = maximum;
                Reclamp();
            }
        }

        /// <summary>上下限变动后把当前值夹回区间。</summary>
        void Reclamp()
        {
            int v = value;
            if (v < minimum) v = minimum;
            if (v > maximum) v = maximum;
            if (v != value) SetValue(v, false);
            else Invalidate();
        }

        public int SmallChange
        {
            get { return smallChange; }
            set { smallChange = Math.Max(1, value); }
        }

        public int LargeChange
        {
            get { return largeChange; }
            set { largeChange = Math.Max(1, value); }
        }

        /// <summary>当前值。程序赋值只发 ValueChanged (不发 Scroll), 与 TrackBar 一致。</summary>
        public int Value
        {
            get { return value; }
            set { SetValue(value, false); }
        }

        int ThumbRadius
        {
            get { int r = Math.Min(Height, 20) / 2 - 3; return r < 5 ? 5 : r; }
        }

        /// <summary>是否位于开启了 AutoScroll 的容器内 (卡片区就是)。</summary>
        bool InAutoScrollHost()
        {
            for (Control p = Parent; p != null; p = p.Parent)
            {
                var sc = p as ScrollableControl;
                if (sc != null && sc.AutoScroll) return true;
            }
            return false;
        }

        /// <summary>
        /// 滚轮一格 = LargeChange (主音量/卡片/增益都是 5), 与"点击轨道每次 5%"一致。
        /// 返回是否吃掉这次滚轮 (false = 没动值, 让卡片区自己滚)。
        /// </summary>
        public bool ApplyWheelDelta(int delta)
        {
            if (!Enabled || delta == 0) return false;
            int notches = delta / 120;
            if (notches == 0) notches = delta > 0 ? 1 : -1;
            SetValue(value + largeChange * notches, true);
            return true;
        }

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            // 不调 base: 值已到顶/到底时也不让消息继续上冒去滚卡片列表
            ApplyWheelDelta(e.Delta);
        }

        int TrackLeft { get { return ThumbRadius; } }
        int TrackRight { get { return Width - ThumbRadius - 1; } }

        int ValueToX(int v)
        {
            int span = maximum - minimum;
            if (span <= 0) return TrackLeft;
            double ratio = (double)(v - minimum) / span;
            return TrackLeft + (int)Math.Round(ratio * (TrackRight - TrackLeft));
        }

        int XToValue(int x)
        {
            int span = TrackRight - TrackLeft;
            if (span <= 0) return minimum;
            double ratio = (double)(x - TrackLeft) / span;
            if (ratio < 0) ratio = 0;
            if (ratio > 1) ratio = 1;
            return minimum + (int)Math.Round(ratio * (maximum - minimum));
        }

        void SetValue(int v, bool fromUser)
        {
            if (v < minimum) v = minimum;
            if (v > maximum) v = maximum;
            if (v == value) return;
            value = v;
            Invalidate();
            var h = ValueChanged;
            if (h != null) h(this, EventArgs.Empty);
            if (fromUser)
            {
                var s = Scroll;
                if (s != null) s(this, EventArgs.Empty);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;

            using (var bg = new SolidBrush(BackColor))
                g.FillRectangle(bg, ClientRectangle);

            int cy = Height / 2;
            const int trackH = 6;
            int r = ThumbRadius;

            var trackRect = new Rectangle(TrackLeft, cy - trackH / 2,
                Math.Max(1, TrackRight - TrackLeft + 1), trackH);
            Color backColor = Enabled ? TrackBack : DisabledTrack;
            Color fillColor = Enabled ? (hoverThumb || dragging ? AccentHot : Accent) : DisabledFill;

            using (var path = UiDraw.RoundRect(trackRect, trackH / 2))
            using (var b = new SolidBrush(backColor))
                g.FillPath(b, path);

            int cx = ValueToX(value);
            if (cx > TrackLeft)
            {
                // 已滑过的左段填蓝: 用整段圆角矩形再裁掉右端 (右端本来也被滑块盖住)
                var fillRect = new Rectangle(TrackLeft, trackRect.Y, cx - TrackLeft, trackH);
                using (var path = UiDraw.RoundRect(fillRect, trackH / 2))
                using (var b = new SolidBrush(fillColor))
                    g.FillPath(b, path);
            }

            // 滑块: 白底 + 蓝边 (蓝边 2px, 视觉上和左段蓝色连成一体)
            var thumb = new Rectangle(cx - r, cy - r, r * 2, r * 2);
            using (var b = new SolidBrush(Enabled ? Color.White : Color.FromArgb(244, 245, 247)))
                g.FillEllipse(b, thumb);
            using (var pen = new Pen(fillColor, 2f))
                g.DrawEllipse(pen, thumb);

            // 键盘焦点提示 (鼠标用户看不到, Tab 进来才显示)
            if (Focused && Enabled)
            {
                using (var pen = new Pen(Color.FromArgb(140, Accent), 1f))
                using (var path = UiDraw.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), 6))
                    g.DrawPath(pen, path);
            }
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled) return;
            // 处在 AutoScroll 容器里时不抢焦点: 子控件一拿焦点, 容器就会把"整个控件可见"
            // 滚一下, 卡片列表会跳动几像素。键盘仍可用 Tab 进入本控件。
            // 主音量/增益不在滚动容器里, 按标准行为点击即获得焦点。
            if (!InAutoScrollHost()) Focus();
            int cx = ValueToX(value);
            var thumb = new Rectangle(cx - ThumbRadius, Height / 2 - ThumbRadius, ThumbRadius * 2, ThumbRadius * 2);
            if (thumb.Contains(e.Location))
            {
                dragging = true;
            }
            else
            {
                // 点轨道: 与 TrackBar 一样按 LargeChange 走一格, 不是直接跳过去
                int step = e.X > cx ? largeChange : -largeChange;
                SetValue(value + step, true);
            }
            Capture = true;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!Enabled) return;

            if (dragging)
            {
                if ((e.Button & MouseButtons.Left) == 0) { dragging = false; Capture = false; }
                else { SetValue(XToValue(e.X), true); return; }
            }

            int cx = ValueToX(value);
            bool hot = Math.Abs(e.X - cx) <= ThumbRadius + 1;
            if (hot != hoverThumb)
            {
                hoverThumb = hot;
                Cursor = hot ? Cursors.Hand : Cursors.Default;
                Invalidate();
            }
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            dragging = false;
            Capture = false;
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (!hoverThumb) return;
            hoverThumb = false;
            Cursor = Cursors.Default;
            Invalidate();
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Left:
                case Keys.Right:
                case Keys.Up:
                case Keys.Down:
                case Keys.PageUp:
                case Keys.PageDown:
                case Keys.Home:
                case Keys.End:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (!Enabled) return;
            switch (e.KeyCode)
            {
                case Keys.Left:
                case Keys.Down: SetValue(value - smallChange, true); e.Handled = true; break;
                case Keys.Right:
                case Keys.Up: SetValue(value + smallChange, true); e.Handled = true; break;
                case Keys.PageDown: SetValue(value - largeChange, true); e.Handled = true; break;
                case Keys.PageUp: SetValue(value + largeChange, true); e.Handled = true; break;
                case Keys.Home: SetValue(minimum, true); e.Handled = true; break;
                case Keys.End: SetValue(maximum, true); e.Handled = true; break;
            }
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            Invalidate();
        }
    }
}
