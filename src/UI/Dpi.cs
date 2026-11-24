using System;
using System.Drawing;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 设计像素 ↔ 实际像素换算 (DPI 适配的唯一入口)。
    ///
    /// <para><b>为什么需要</b>: 界面尺寸全是按 96 DPI 的设计值写死的 (15px 留白、6px 圆角、
    /// 1px 边框、22px 图标)。进程声明 DPI 感知后, 125% 缩放下 GDI 按 120 DPI 绘制 ——
    /// 若几何还按 96 的像素值摆, 控件就比字体小一圈, 文字被裁。所以凡是"写死的像素"
    /// 都要经 <see cref="S"/> / <see cref="F"/> 换算。</para>
    ///
    /// <para><b>谁需要过换算</b>: 构造期设置的 Bounds 由 WinForms 的 AutoScale 自动缩放,
    /// 不必再乘; 需要乘的是 ① 运行时重排 (Load/Resize 里的布局方法) ② 动态创建的控件
    /// (输出卡片、弹层里的行) ③ 自绘里的线宽 / 圆角 / 内边距 / 图标尺寸。</para>
    /// </summary>
    internal static class Dpi
    {
        static float factor = 1f;

        /// <summary>当前缩放系数 (100% = 1, 125% = 1.25)。</summary>
        internal static float Factor { get { return factor; } }

        /// <summary>
        /// 按系统 DPI 定一次系数。必须在进程声明 DPI 感知 (SetProcessDPIAware) 之后调用,
        /// 否则拿到的总是 96 (系统会把 unaware 进程骗成 100%)。
        /// </summary>
        internal static void Init()
        {
            try
            {
                using (var g = Graphics.FromHwnd(IntPtr.Zero))
                    factor = g.DpiX / 96f;
            }
            catch { factor = 1f; }
            if (factor < 1f) factor = 1f;

            // 卡片与栅格的尺寸阈值也按同一系数缩放: 它们参与"排几列 / 卡片多宽"的算术,
            // 只在绘制时乘是来不及的 —— 布局阶段就要用放大后的值。
            ScaleForDpi(factor);
        }

        static void ScaleForDpi(float f)
        {
            StereoSplitter.Model.CardGrid.ScaleForDpi(f);
            OutputCardControl.ScaleForDpi(f);
        }

        static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Form, object> scaledForms =
            new System.Runtime.CompilerServices.ConditionalWeakTable<Form, object>();

        /// <summary>
        /// 把一个"按 96 DPI 摆好的"窗体整体放大。放在 Load 里调用 (构造期控件已全部就位)。
        /// <para>只缩一次; 100% 时不动作。顶层窗口的外框尺寸要自己补 —— <c>Scale</c> 只缩放
        /// 客户区里的控件, 不给窗口本身加倍, 不补的话内容会溢出右缘/下缘。</para>
        /// </summary>
        internal static void ScaleForm(Form f)
        {
            if (f == null || factor == 1f) return;
            if (scaledForms.TryGetValue(f, out _)) return;
            scaledForms.Add(f, null);

            Size design = f.Size;
            f.Scale(new SizeF(factor, factor));
            f.Size = new Size((int)Math.Round(design.Width * factor),
                              (int)Math.Round(design.Height * factor));
        }

        /// <summary>设计像素 → 实际像素 (取整)。</summary>
        internal static int S(int v) { return (int)Math.Round(v * factor); }

        /// <summary>设计像素 → 实际像素 (浮点, 给线宽 / 圆弧 / 字形笔画用)。</summary>
        internal static float F(float v) { return v * factor; }
    }
}
