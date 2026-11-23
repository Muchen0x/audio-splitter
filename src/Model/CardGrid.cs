namespace StereoSplitter.Model
{
    /// <summary>
    /// 输出卡片栅格的纯算术（R-07）。
    ///
    /// <para>抽出来的理由不是"少几行"，而是<b>这套规则此前零测试</b>。它管着四件互相牵制的事：
    /// 列数按基准宽的整数倍跳档、多列时每列都要留右侧间距（否则 FlowLayoutPanel 提前折行）、
    /// 窗口窄到排不下时主动减列、竖滚动条出现要让出一整条宽度。任何一条算错的表现都是
    /// "偶尔多一根横向滚动条""滑条长度对不齐"这类只能靠眼睛发现的毛病 —— 而它是纯算术，
    /// 最适合进 <c>--selftest</c> 穷举边界。</para>
    ///
    /// <para>放在 Model 层而不是 <c>MainForm</c>：自检在 Infrastructure 层，够不到 UI 的私有方法。
    /// 与界面相关的部分（控件引用、上次尺寸缓存、SuspendLayout）仍留在 <c>MainForm.Cards</c>。</para>
    /// </summary>
    static class CardGrid
    {
        /// <summary>卡片基准宽度：换列的步长。取 490 = 卡片里固定控件（设备/声道/延迟/百分号/静音）
        /// 占掉约 273px 之后，滑条还剩 200px 左右的最舒适宽度，也是默认窗口下单列卡片的取值区。</summary>
        public const int BaseW = 490;

        /// <summary>并排卡片之间的横向间距（卡片外框到外框）。</summary>
        public const int GapX = 10;

        /// <summary>卡片最窄宽度：再窄第一行的设备下拉就压到「声道」下拉上了。
        /// 第一行的固定控件（× / ms / 延迟框 / 延迟 / 声道）合计约 246px，再加设备下拉 90px
        /// 与左侧 8px，360 是"刚好不重叠"的值；低于它就宁可横向滚动也不挤成畸形。</summary>
        public const int MinW = 360;

        /// <summary>单列时的宽度上限：再宽滑条就拉成一根看不见刻度的长线。</summary>
        public const int MaxW = 1120;

        /// <summary>
        /// 给定可用宽度与卡片数，算列数与统一卡片宽度。
        /// <para>列数按"基准宽度的整数倍"跳档：可视宽到 2×基准才铺第 2 列、到 3×基准才铺第 3 列。
        /// 因为余量摊进卡片宽度，实际卡片宽度始终落在 [基准, 基准+间距] 这个窄区间里，
        /// 所以换列时滑条长度只变化几像素，不会有"窗口宽一点、滑条就长一截"的拉扯感。</para>
        /// </summary>
        /// <param name="avail">可视宽度（已扣掉容器 Padding）。</param>
        /// <param name="count">卡片数量，列数不会超过它。</param>
        /// <param name="fallbackWidth">avail ≤ 0 时的兜底宽度（卡片还没建出来，用控件默认宽）。</param>
        public static void Columns(int avail, int count, int fallbackWidth, out int cols, out int width)
        {
            if (avail <= 0) { cols = 1; width = fallbackWidth; return; }

            cols = (avail + GapX) / (BaseW + GapX);
            if (cols < 1) cols = 1;
            if (cols > count) cols = count < 1 ? 1 : count;

            if (cols <= 1)
            {
                // 单列：卡片铺满可视宽，只有窗口宽到离谱时才封顶
                width = Clamp(avail);
            }
            else
            {
                // 多列：每列都带一个右侧间距（含最后一列），这样 FlowLayoutPanel 的换行判断
                // (x + 卡宽 + Margin.Right > 可视宽) 正好在算好的列数处触发，不会提前折行。
                width = (avail - cols * GapX) / cols;
                if (width < MinW)
                {
                    // 窗口窄到连最小宽度都排不下这么多列 → 主动减列，宁可少一列也不挤成畸形
                    cols = (avail + GapX) / (MinW + GapX);
                    if (cols < 1) cols = 1;
                    width = cols <= 1 ? Clamp(avail) : (avail - cols * GapX) / cols;
                }
            }
        }

        static int Clamp(int avail)
        {
            int w = avail < MinW ? MinW : avail;
            return w > MaxW ? MaxW : w;
        }

        /// <summary>给定列数，卡片占几行（向上取整）。</summary>
        public static int Rows(int count, int cols)
        {
            if (count <= 0) return 0;
            if (cols < 1) cols = 1;
            return (count + cols - 1) / cols;
        }

        /// <summary>
        /// 按这个列数摆完，内容会不会比可视区高（即竖滚动条会不会出现）。
        /// 行高取该行最高的一张卡片，与 FlowLayoutPanel 的排法一致。
        /// </summary>
        /// <param name="heights">每张卡片的实际高度（乱序无关，按索引切行）。</param>
        /// <param name="paddingVertical">容器 Padding 的上下之和。</param>
        /// <param name="rowGap">卡片 Margin.Bottom（行与行之间的空隙）。</param>
        public static bool NeedsVScroll(int count, int cols, int[] heights, int paddingVertical,
                                        int clientHeight, int rowGap)
        {
            if (count <= 0 || heights == null) return false;
            if (cols < 1) cols = 1;

            int n = count < heights.Length ? count : heights.Length;
            int rows = Rows(n, cols);
            int total = paddingVertical;
            for (int r = 0; r < rows; r++)
            {
                int hMax = 0;
                int end = (r + 1) * cols;
                if (end > n) end = n;
                for (int i = r * cols; i < end; i++)
                {
                    int h = heights[i] + rowGap;
                    if (h > hMax) hMax = h;
                }
                total += hMax;
            }
            return total > clientHeight;
        }
    }
}
