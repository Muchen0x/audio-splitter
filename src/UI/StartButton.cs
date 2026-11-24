using System;
using System.Diagnostics;
using System.Drawing;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>「开始分流」按钮的动效形态（与 <see cref="AppState"/> 一一对应，见 MainForm.UpdateStartButton）。</summary>
    enum StartButtonMode
    {
        /// <summary>静态：播放三角 (可以开始)。</summary>
        Idle,
        /// <summary>过渡态（启动中 / 正在停止）：圆环持续旋转。</summary>
        Working,
        /// <summary>运行中：停止方块 + 呼吸光环。</summary>
        Pulse
    }

    /// <summary>
    /// 「开始分流」图标按钮：与刷新齿轮同一套图标交互 (无边框、悬停浮圆角垫、键盘空格可点)，
    /// 图形随链路状态切换、全程带动效。
    ///
    /// <para><b>形态与图形</b>：<see cref="StartButtonMode.Idle"/> = 播放三角 ▶ (蓝)；
    /// <see cref="StartButtonMode.Working"/> = 240° 圆弧匀速转圈 (启动中 / 正在停止 ——
    /// 与刷新齿轮同一套"时间驱动角度"的画法, 界面卡顿不丢角度, 转满一圈的角度连续所以
    /// 停下来不跳)；<see cref="StartButtonMode.Pulse"/> = 停止方块 ■ + 1.4s 周期的呼吸光环
    /// (运行中 —— 方块与光环一起起伏, 读作"链路活着", 不响不动不抢眼)。</para>
    ///
    /// <para><b>换色渐变</b>：<see cref="BeginTint"/> 把图标色 (悬停垫色的来源) 从当前值在
    /// 220ms 内 ease-out 渐到目标色 —— 状态切换不再"啪"一下跳色。渐变由流逝时间驱动
    /// (不按帧累加), 掉帧也照样准时落位；落点就是目标色本身, 不会停在中途的颜色上。</para>
    ///
    /// <para><b>为什么自成一个类而不是给 GearButton 加图形</b>：齿轮的图形集是"设置 / 刷新 /
    /// 关闭"这类中性图标, 颜色永远跟随悬停深浅; 这里的图形是**状态本身** (可开始 / 进行中 /
    /// 运行中), 颜色跟状态走、还带渐变与呼吸 —— 状态机塞进通用图标按钮会把两条正交的
    /// 颜色语义拧在一起。交互模型 (悬停垫 / 键盘 / 按下位移) 与 GearButton 保持同一套。</para>
    /// </summary>
    class StartButton : Control
    {
        StartButtonMode mode = StartButtonMode.Idle;
        /// <summary>进入当前形态的时刻 —— 呼吸/旋转的相位从 0 起算，换形态即换相位。</summary>
        long modeStartMs;
        Color tint = Color.FromArgb(0x00, 0x78, 0xD7);   // 默认蓝, 与原描边按钮同一支
        bool hot, down;

        // —— 换色渐变 ——
        System.Windows.Forms.Timer anim;
        bool tinting;
        long tintStartMs;
        Color tintFrom, tintTo;
        /// <summary>换色用多久 (ms)。再快就成了跳变，再慢追不上状态本身的切换节奏。</summary>
        internal const int TintMs = 220;
        const int SpinPeriodMs = 900;
        const int PulsePeriodMs = 1400;

        /// <summary>当前动效形态。换形态时重置相位并保证动画钟在跑。</summary>
        public StartButtonMode Mode
        {
            get { return mode; }
            set
            {
                if (mode == value) return;
                mode = value;
                modeStartMs = NowMs();
                if (mode != StartButtonMode.Idle) EnsureTimer().Start();
                Invalidate();
            }
        }

        /// <summary>图标此刻的颜色 (渐变中的中间值也算)。悬停垫色由它派生。</summary>
        public Color Tint { get { return tint; } }

        /// <summary>
        /// 把图标色渐变到目标色。同色调用是安全的 no-op；连发也安全 —— 从**当前**插值位置
        /// 出发，不会闪回上一状态的起点色。
        /// </summary>
        public void BeginTint(Color target)
        {
            if (!IsHandleCreated)
            {
                // 句柄没建 (构造期) 没有消息泵可以驱动 Timer, 直接落位
                tint = target;
                Invalidate();
                return;
            }
            if (tinting && tintTo == target) return;
            if (!tinting && tint == target) return;

            tintFrom = tint;                 // 起点 = 此刻正显示的颜色 (渐变到一半改目标时, 从半路继续)
            tintTo = target;
            tinting = true;
            tintStartMs = NowMs();
            EnsureTimer().Start();
        }

        System.Windows.Forms.Timer EnsureTimer()
        {
            if (anim == null)
            {
                anim = new System.Windows.Forms.Timer { Interval = 16 };
                anim.Tick += OnAnimTick;
            }
            return anim;
        }

        void OnAnimTick(object s, EventArgs e)
        {
            bool busy = tinting;
            if (tinting)
            {
                // 与 GearButton.StartSpin 同一套路: 进度由流逝时间算, 丢帧不欠账
                float t = (NowMs() - tintStartMs) / (float)TintMs;
                if (t >= 1f)
                {
                    tinting = false;
                    tint = tintTo;
                }
                else
                {
                    float k = 1f - (1f - t) * (1f - t);   // ease-out quad: 立刻在动, 末尾平滑落位
                    tint = Color.FromArgb(
                        (int)Math.Round(tintFrom.R + (tintTo.R - tintFrom.R) * k),
                        (int)Math.Round(tintFrom.G + (tintTo.G - tintFrom.G) * k),
                        (int)Math.Round(tintFrom.B + (tintTo.B - tintFrom.B) * k));
                }
            }
            // 渐变完但呼吸/旋转还在跑: 钟不能停
            if (mode != StartButtonMode.Idle) busy = true;

            if (busy) Invalidate();
            else if (anim != null) anim.Stop();
        }

        public StartButton()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint
                | ControlStyles.OptimizedDoubleBuffer | ControlStyles.SupportsTransparentBackColor
                | ControlStyles.ResizeRedraw, true);
            BackColor = Color.Transparent;
            Size = new Size(30, 30);
            Cursor = Cursors.Hand;
        }

        // —— 交互模型与 GearButton 同一套: 悬停垫 / 按下位移 1px / 空格回车可点 ——

        protected override bool IsInputKey(Keys keyData)
        {
            Keys k = keyData & Keys.KeyCode;
            if (k == Keys.Space || k == Keys.Enter) return true;
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if ((e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) && Enabled)
            {
                down = true;
                Invalidate();
                e.Handled = true;
            }
            base.OnKeyDown(e);
        }

        protected override void OnKeyUp(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter)
            {
                if (down)
                {
                    down = false;
                    Invalidate();
                    OnClick(EventArgs.Empty);
                }
                e.Handled = true;
            }
            base.OnKeyUp(e);
        }

        protected override void OnMouseEnter(EventArgs e) { hot = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hot = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left) { down = true; Invalidate(); }
            base.OnMouseDown(e);
        }
        protected override void OnMouseUp(MouseEventArgs e) { if (down) { down = false; Invalidate(); } base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { if (!Enabled) { hot = false; down = false; } Invalidate(); base.OnEnabledChanged(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;

            Color back = Parent != null ? Parent.BackColor : SystemColors.Control;
            using (var b = new SolidBrush(back)) g.FillRectangle(b, ClientRectangle);

            // 悬停/按下垫: 往状态色里混 (蓝色悬停泛浅蓝、运行中泛浅红), 比中性灰多一层语义
            if (Enabled && (hot || down))
                using (var path = UiDraw.RoundRect(new Rectangle(0, 0, Width - 1, Height - 1), Dpi.S(6)))
                using (var fill = new SolidBrush(BlendTint(back, down ? 0.16f : 0.09f)))
                    g.FillPath(fill, path);

            float cx = (Width - 1) / 2f;
            float cy = (Height - 1) / 2f + (down ? 1f : 0f);   // 按下整体下沉 1px, 与齿轮同一反馈
            Color ink = Enabled ? tint : BlendToWhite(tint, 0.30f);

            switch (mode)
            {
                case StartButtonMode.Working:
                    DrawSpinner(g, cx, cy, ink);
                    break;
                case StartButtonMode.Pulse:
                    DrawBreathHalo(g, cx, cy, ink);
                    DrawStop(g, cx, cy, ink);
                    break;
                default:
                    DrawPlay(g, cx, cy, ink);
                    break;
            }
        }

        /// <summary>悬停垫色: 父底色往状态色里混 t。</summary>
        Color BlendTint(Color back, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(back.R + (tint.R - back.R) * t),
                (int)Math.Round(back.G + (tint.G - back.G) * t),
                (int)Math.Round(back.B + (tint.B - back.B) * t));
        }

        static Color BlendToWhite(Color c, float t)
        {
            return Color.FromArgb(
                (int)Math.Round(c.R + (255 - c.R) * t),
                (int)Math.Round(c.G + (255 - c.G) * t),
                (int)Math.Round(c.B + (255 - c.B) * t));
        }

        /// <summary>
        /// 过渡态: 240° 圆弧匀速旋转。缺口弧才转得出来 (整圆旋转在视觉上是静止的);
        /// 匀速不用 ease —— 它表达的是"进行中", 不是"这一下点击生效了"。
        /// </summary>
        void DrawSpinner(Graphics g, float cx, float cy, Color ink)
        {
            float r = Dpi.F(8.8f);
            float angle = 360f * ((NowMs() - modeStartMs) % SpinPeriodMs) / (float)SpinPeriodMs;
            using (var p = new Pen(ink, Dpi.F(2.6f)))
            {
                p.StartCap = System.Drawing.Drawing2D.LineCap.Round;
                p.EndCap = System.Drawing.Drawing2D.LineCap.Round;
                g.DrawArc(p, cx - r, cy - r, r * 2f, r * 2f, angle, 240f);
            }
        }

        /// <summary>
        /// 运行中的呼吸光环: 圆环半径与不透明度同相起伏 (1.4s 周期, 首尾衔接无跳变)。
        /// alpha 封顶 90 —— 它是方块的背景音, 抢过方块本身就成了第二主体。
        /// </summary>
        void DrawBreathHalo(Graphics g, float cx, float cy, Color ink)
        {
            float phase = ((NowMs() - modeStartMs) % PulsePeriodMs) / (float)PulsePeriodMs;
            float w = 0.5f - 0.5f * (float)Math.Cos(phase * 2.0 * Math.PI);   // 0..1..0
            float r = Dpi.F(10.2f + 2.0f * w);
            int alpha = (int)(40 + 50 * w);
            using (var p = new Pen(Color.FromArgb(alpha, ink), Dpi.F(1.6f)))
                g.DrawEllipse(p, cx - r, cy - r, r * 2f, r * 2f);
        }

        /// <summary>播放三角: 圆角三边形 (圆角用加宽线描一遍路径得到, 与卡片图标同一手法)。</summary>
        void DrawPlay(Graphics g, float cx, float cy, Color ink)
        {
            float s = Dpi.F(8.2f);                          // 半边长: 30 按钮里 ≈20px 高的三角
            // 视觉居中: 三角形的几何重心比外接框中心偏右, 左移半像素才不歪
            float x0 = cx - s * 0.72f, y0 = cy - s, x1 = cx - s * 0.72f, y1 = cy + s, x2 = cx + s;
            using (var path = new System.Drawing.Drawing2D.GraphicsPath())
            {
                path.AddPolygon(new PointF[] { new PointF(x0, y0), new PointF(x2, cy), new PointF(x1, y1) });
                using (var b = new SolidBrush(ink))
                {
                    // 1px 加宽描边同色: 多边形尖角填充有锯齿缝, 描一遍路径圆滑 + 补缝
                    using (var p = new Pen(ink, Dpi.F(1.2f)))
                    {
                        p.LineJoin = System.Drawing.Drawing2D.LineJoin.Round;
                        g.DrawPath(p, path);
                    }
                    g.FillPath(b, path);
                }
            }
        }

        /// <summary>停止方块: 圆角 2 的实心小方, 与播放三角同一视觉重量。</summary>
        void DrawStop(Graphics g, float cx, float cy, Color ink)
        {
            float s = Dpi.F(6.4f);
            using (var path = UiDraw.RoundRect(new RectangleF(cx - s, cy - s, s * 2f, s * 2f), Dpi.F(2.4f)))
            using (var b = new SolidBrush(ink))
                g.FillPath(b, path);
        }

        static long NowMs()
        {
            // 与 GearButton/RoundedCombo 同一套: Stopwatch 基准不受系统校时影响
            return (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && anim != null)
            {
                anim.Stop();
                anim.Dispose();
                anim = null;
            }
            base.Dispose(disposing);
        }
    }
}
