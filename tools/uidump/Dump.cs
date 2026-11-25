using System;
using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Collections.Generic;
using System.Windows.Forms;
using StereoSplitter.Model;

namespace StereoSplitter
{
    /// <summary>临时布局测量工具 (不进版本库): 实例化真实 MainForm, 打印控件实际边界。</summary>
    static class Dump
    {
        [STAThread]
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetProcessDPIAware();

        static void Main(string[] args)
        {
            Console.Out.Flush();   // 探针可能被 timeout 掐死: 关掉块缓冲才看得到挂在哪一步
            Console.SetOut(new System.IO.StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true });
            // CopyFromScreen 按物理像素取屏: 不声明 DPI 感知的话, 高缩放屏上窗口会被系统
            // 虚拟化放大, 按 Location/Size 去截只能截到左上角一块 (live/icons 全军覆没)。
            try { SetProcessDPIAware(); } catch { }
            // 与主程序同一套 DPI 换算: 不初始化的话 Dpi.Factor 恒为 1, 窗体不会缩放,
            // 截出来的图就不是用户屏幕上看到的样子 (125% 下会误判"布局已适配")。
            StereoSplitter.Dpi.Init();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 设置弹层: 量菜单行的边界/重叠/溢出, 并验证"菜单行驱动原控件 + 改动保留"语义
            if (args.Length >= 1 && args[0] == "settings")
            {
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(60, 80);
                mf.TopMost = true;               // 前台被别的窗口占着时, 置顶才能保证弹层真的拍到
                mf.Show();
                mf.Activate();
                for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                Control cmb = Field(mf, "cmbLatency") as Control;
                Control drift = Field(mf, "chkDrift") as Control;
                Control tray = Field(mf, "chkMinimizeToTray") as Control;
                Control muteCap = Field(mf, "chkMuteCapture") as Control;
                int idx0 = SelOf(cmb);
                bool d0 = drift is CheckBox && ((CheckBox)drift).Checked;
                bool t0 = tray is CheckBox && ((CheckBox)tray).Checked;
                bool m0 = muteCap is CheckBox && ((CheckBox)muteCap).Checked;
                Console.Out.WriteLine("BEFORE idx=" + idx0 + " drift=" + d0 + " tray=" + t0 + " muteCap=" + m0);
                int idxExpect = int.MinValue;   // 子菜单选档后应有的档位 (int.MinValue = 没选过)

                MethodInfo scMethod = mf.GetType().GetMethod("SettingsControls", BindingFlags.Instance | BindingFlags.NonPublic);

                // 精确采边: ComboPopup 与 SettingsFlyout 的描边环是否真的画出来了
                Action<ToolStripDropDown, string> sampleEdges = (dd, tag) =>
                {
                    if (dd == null || !dd.Visible) { Console.Out.WriteLine("EDGE " + tag + " not visible"); return; }
                    Rectangle r = dd.Bounds;      // 顶层窗口, Bounds 即屏幕矩形
                    using (var bmp = new Bitmap(r.Width, r.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                        int midY = r.Height / 2;
                        var sbs = new StringBuilder("EDGE " + tag + " " + r.Width + "x" + r.Height);
                        for (int x = 0; x <= 4; x++)
                            sbs.Append(" x").Append(x).Append('=').Append(bmp.GetPixel(x, midY));
                        for (int y = 0; y <= 4; y++)
                            sbs.Append(" y").Append(y).Append('=').Append(bmp.GetPixel(r.Width / 2, y));
                        Console.Out.WriteLine(sbs.ToString());
                    }
                };

                // 对照: 打开输出设备下拉的真实弹层 (同 renderer), 拍下来比边框/圆角。
                // 放在弹层打开之前 —— 开下拉会抢焦点把 AutoClose 的弹层顺手关掉。
                Control cmbDefault = Field(mf, "cmbDefault") as Control;
                if (cmbDefault != null)
                {
                    MethodInfo openDD = cmbDefault.GetType().GetMethod("OpenDropDown",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    MethodInfo closeDD = cmbDefault.GetType().GetMethod("CloseDropDown",
                        BindingFlags.Instance | BindingFlags.NonPublic);
                    if (openDD != null)
                    {
                        openDD.Invoke(cmbDefault, null);
                        for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                        // 悬停实拍: 光标摆到第 2 行上再截 (用户要求下拉行悬停 = 菜单行同款圆角垫)
                        var popupDD = cmbDefault.GetType().GetField("popup", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(cmbDefault) as ToolStripDropDown;
                        Point oldCurDD = Cursor.Position;
                        Control listDD = null;
                        if (popupDD != null)
                        {
                            var fldList = popupDD.GetType().GetField("List",
                                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                            if (fldList != null) listDD = fldList.GetValue(popupDD) as Control;
                        }
                        if (listDD != null)
                        {
                            int itemH = (int)cmbDefault.GetType().GetProperty("ItemHeight").GetValue(cmbDefault, null);
                            Cursor.Position = listDD.PointToScreen(new Point(listDD.Width / 2, itemH + itemH / 2));
                            for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                        }
                        using (var bmp = new Bitmap(400, 260))
                        {
                            using (var g = Graphics.FromImage(bmp))
                                g.CopyFromScreen(cmbDefault.PointToScreen(new Point(-20, -20)), Point.Empty, bmp.Size);
                            bmp.Save("dropdown-live.png", System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.Out.WriteLine("DROPDOWN-SHOT -> dropdown-live.png  hoverList=" + (listDD != null));
                        if (listDD != null) Cursor.Position = oldCurDD;
                        for (int i = 0; i < 3; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                        var popupCtl = popupDD;
                        if (popupCtl != null) sampleEdges(popupCtl, "comboPopup");
                        else Console.Out.WriteLine("EDGE comboPopup: field null");
                        if (closeDD != null) closeDD.Invoke(cmbDefault, null);
                        for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    }
                }

                var arr = (Control[])scMethod.Invoke(mf, null);
                using (var fy = new SettingsFlyout(arr))
                {
                    fy.Show(new Point(1000, 60));   // 主窗 (60,80 起, 900 宽) 右侧外, 别被主窗压住
                    for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }

                    // 弹层实拍 (实屏 CopyFromScreen: DrawToBitmap 拍不到顶层弹层的内容)
                    using (var bmp = new Bitmap(fy.Width, fy.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(fy.PointToScreen(Point.Empty), Point.Empty, fy.Size);
                        bmp.Save("flyout-live.png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Console.Out.WriteLine("FLYOUT-SHOT -> flyout-live.png");

                    Control content = fy.Content;
                    var sb2 = new StringBuilder();
                    sb2.AppendLine("SETTINGS content=" + content.ClientSize + " popup=" + fy.Size
                        + " visible=" + fy.Visible);
                    sampleEdges(fy, "flyout");
                    foreach (Control c in content.Controls)
                    {
                        string rowTag = c is MenuRow ? "ROW '" + c.Text + "' enabled=" + c.Enabled : "host '" + c.GetType().Name + "' hidden=" + !c.Visible;
                        sb2.AppendLine("  " + c.GetType().Name + " " + rowTag + " bounds=" + c.Bounds
                            + (c.Right > content.ClientSize.Width ? "  <<< OVERFLOW " + (c.Right - content.ClientSize.Width) : "")
                            + (c.Bottom > content.ClientSize.Height ? "  <<< OVERFLOW-B " + (c.Bottom - content.ClientSize.Height) : ""));
                    }
                    DumpOverlaps(content, "content", sb2);
                    DumpTextFit(content, "content", sb2);
                    Console.Out.Write(sb2.ToString());

                    MethodInfo onClick = typeof(Control).GetMethod("OnClick",
                        BindingFlags.Instance | BindingFlags.NonPublic);

                    // 档位子菜单: 点"缓冲档位"行 → 子菜单弹出 → 点选另一档 → 档位写回 + 弹层仍开着
                    MenuRow bufRow = null;
                    foreach (Control c in content.Controls)
                        if (c is MenuRow && c.Text == "缓冲档位") bufRow = (MenuRow)c;
                    if (bufRow == null)
                    {
                        Console.Out.WriteLine("PROFILEMENU  <<< NO BUF ROW");
                    }
                    else
                    {
                        int bIdx0 = SelOf(cmb);
                        onClick.Invoke(bufRow, new object[] { EventArgs.Empty });
                        for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                        var pm = fy.GetType().GetField("profileMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                            .GetValue(fy) as ToolStripDropDown;
                        Console.Out.WriteLine("PROFILEMENU open=" + (pm != null && pm.Visible)
                            + " items=" + (pm == null ? 0 : pm.Items.Count)
                            + " flyoutStill=" + fy.Visible);
                        if (pm != null && pm.Visible)
                        {
                            // 悬停实拍: 把光标摆到第 2 项 (低延迟) 上, 记录项边界 + 拍悬停垫的样子
                            Rectangle ib = pm.Items[Math.Min(1, pm.Items.Count - 1)].Bounds;
                            var sbi = new StringBuilder("PROFILEITEMS");
                            foreach (ToolStripItem it2 in pm.Items)
                                sbi.Append(" [").Append(it2.Bounds.X).Append(',').Append(it2.Bounds.Y)
                                   .Append(' ').Append(it2.Width).Append('x').Append(it2.Height).Append(']');
                            Console.Out.WriteLine(sbi.ToString());
                            Point hoverPt = pm.PointToScreen(new Point(ib.X + ib.Width / 2, ib.Y + ib.Height / 2));
                            Point oldCur2 = Cursor.Position;
                            Cursor.Position = hoverPt;
                            for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                            using (var bmp = new Bitmap(fy.Width + pm.Width + 20, fy.Height + pm.Height + 20))
                            {
                                using (var g = Graphics.FromImage(bmp))
                                    g.CopyFromScreen(fy.PointToScreen(Point.Empty), Point.Empty, bmp.Size);
                                bmp.Save("profilemenu-live.png", System.Drawing.Imaging.ImageFormat.Png);
                            }
                            Console.Out.WriteLine("PROFILEMENU-SHOT -> profilemenu-live.png  cursor@" + hoverPt);
                            Cursor.Position = oldCur2;
                            for (int i = 0; i < 3; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                        }
                        int target = (SelOf(cmb) + 1) % Math.Max(1, pm == null ? 1 : pm.Items.Count);
                        if (pm != null && pm.Visible)
                        {
                            pm.Items[target].PerformClick();
                            for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                        }
                        Console.Out.WriteLine("PICK idx " + bIdx0 + " -> " + SelOf(cmb)
                            + " (期望 " + target + ")  flyoutStill=" + fy.Visible);
                        idxExpect = target;

                        // —— 二次点击 = 只收子菜单, 父弹层留着 (用户报的回归点) ——
                        Func<ToolStripDropDown> curMenu = () => fy.GetType()
                            .GetField("profileMenu", BindingFlags.Instance | BindingFlags.NonPublic)
                            .GetValue(fy) as ToolStripDropDown;
                        onClick.Invoke(bufRow, new object[] { EventArgs.Empty });   // 重开子菜单
                        for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                        Point oldCursor = Cursor.Position;
                        Cursor.Position = fy.PointToScreen(new Point(fy.Width / 2, fy.Height / 2));
                        var pm1 = curMenu();
                        if (pm1 != null && pm1.Visible)
                            pm1.Close(ToolStripDropDownCloseReason.AppFocusChange); // 模拟"点在外面收起"
                        for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                        Console.Out.WriteLine("TOGGLE-1 menuClosed=" + (pm1 == null || !pm1.Visible)
                            + " flyoutKept=" + fy.Visible + " (期望 True/True)");
                        onClick.Invoke(bufRow, new object[] { EventArgs.Empty });   // 紧跟的这次点击 = 收起那一下
                        for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                        var pm2 = curMenu();
                        Console.Out.WriteLine("TOGGLE-2 reopened=" + (pm2 != null && pm2.Visible) + " (期望 False)");
                        System.Threading.Thread.Sleep(450); Application.DoEvents(); // 过 400ms 闸
                        onClick.Invoke(bufRow, new object[] { EventArgs.Empty });   // 再点一次 = 正常重开
                        for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                        var pm3 = curMenu();
                        Console.Out.WriteLine("TOGGLE-3 reopened=" + (pm3 != null && pm3.Visible) + " (期望 True)");
                        Cursor.Position = oldCursor;
                    }

                    // 借用期间点"自动时钟校正"菜单行 (走 OnClick 反射触发真实 Click 路径):
                    // 勾选必须翻转, 且归还后保持翻转值 —— 弹层是"改动立即生效"语义, 不还原。
                    MenuRow driftRow = null;
                    foreach (Control c in content.Controls)
                        if (c is MenuRow && c.Text == "自动时钟校正") driftRow = (MenuRow)c;
                    if (driftRow == null)
                    {
                        Console.Out.WriteLine("DISTURB  <<< NO DRIFT ROW");
                    }
                    else
                    {
                        MethodInfo onClickRow = onClick;
                        onClickRow.Invoke(driftRow, new object[] { EventArgs.Empty });
                        Application.DoEvents();
                        bool dNow = ((CheckBox)drift).Checked;
                        Console.Out.WriteLine("DISTURB drift " + d0 + " -> " + dNow
                            + (dNow != d0 ? "  (menu row drove real checkbox, good)" : "  <<< NOT FLIPPED"));
                    }

                    fy.ReturnControls();
                    fy.Close();
                }
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

                int idx1 = SelOf(cmb);
                bool d1 = drift is CheckBox && ((CheckBox)drift).Checked;
                bool t1 = tray is CheckBox && ((CheckBox)tray).Checked;
                bool m1 = muteCap is CheckBox && ((CheckBox)muteCap).Checked;
                Console.Out.WriteLine("AFTER  idx=" + idx1 + " drift=" + d1 + " tray=" + t1 + " muteCap=" + m1
                    + " parent=" + (cmb.Parent == null ? "null" : cmb.Parent.GetType().Name)
                    + ((d1 != d0 && t1 == t0 && m1 == m0 && (idxExpect == int.MinValue || idx1 == idxExpect))
                        ? "  (changes kept, good)" : "  <<< UNEXPECTED"));
                Console.Out.Flush();
                Environment.Exit(0);
            }

            // 锁定语义: 运行中延迟框必须仍可编辑, 设备/移除/校准必须置灰
            if (args.Length >= 1 && args[0] == "locks")
            {
                var mf = new MainForm();
                mf.Show();
                for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                var cardsField = Field(mf, "cards") as System.Collections.IList;
                if (cardsField == null || cardsField.Count == 0)
                {
                    Console.Out.WriteLine("NO-CARDS (本机没有可用的输出卡片, 跳过)");
                    return;
                }
                object card0 = cardsField[0];
                var delay = Field(card0, "nudDelay") as Control;
                var dev = Field(card0, "cmbDevice") as Control;
                var rm = Field(card0, "btnRemove") as Control;
                var calib = Field(mf, "btnCalibrate") as Control;
                var start = Field(mf, "btnStart") as Control;

                var stateField = mf.GetType().GetField("state", BindingFlags.Instance | BindingFlags.NonPublic);
                var lockM = mf.GetType().GetMethod("ApplyUiLock", BindingFlags.Instance | BindingFlags.NonPublic);
                Action<string> dump = delegate(string tag)
                {
                    lockM.Invoke(mf, null);
                    Application.DoEvents();
                    Console.Out.WriteLine(tag + " delay=" + (delay != null && delay.Enabled) +
                        " device=" + (dev != null && dev.Enabled) +
                        " remove=" + (rm != null && rm.Enabled) +
                        " calibrate=" + (calib != null && calib.Enabled) +
                        " start=" + (start != null && start.Enabled));
                };

                object running = Enum.Parse(stateField.FieldType, "Running");
                object starting = Enum.Parse(stateField.FieldType, "Starting");
                object ready = Enum.Parse(stateField.FieldType, "Ready");

                stateField.SetValue(mf, ready); dump("READY   ");
                stateField.SetValue(mf, starting); dump("STARTING");
                stateField.SetValue(mf, running); dump("RUNNING ");
                stateField.SetValue(mf, ready); dump("READY2  ");
                Console.Out.WriteLine("EXPECT  READY: delay=True device=True remove=True calibrate=True");
                Console.Out.WriteLine("EXPECT  STARTING: delay=False  (启动中不可改)");
                Console.Out.WriteLine("EXPECT  RUNNING: delay=True device=False remove=False calibrate=True  (运行时临时静音分流)");
                mf.Close();
                return;
            }

            // 数字框箭头: 点一下要改值, 但不能把焦点/插入符带进文本框
            if (args.Length >= 1 && args[0] == "spin")
            {
                var mf = new MainForm();
                mf.Show();
                for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                var cardsField = Field(mf, "cards") as System.Collections.IList;
                if (cardsField == null || cardsField.Count == 0) { Console.Out.WriteLine("NO-CARDS"); return; }
                var nud = Field(cardsField[0], "nudDelay") as NumericUpDown;
                if (nud == null) { Console.Out.WriteLine("NO-NUD"); return; }

                Control buttons = null, edit = null;
                foreach (Control c in nud.Controls)
                {
                    if (c is TextBox) edit = c; else buttons = c;
                }
                Console.Out.WriteLine("CHILDREN buttons=" + (buttons == null ? "null" : buttons.GetType().Name)
                    + " edit=" + (edit == null ? "null" : edit.GetType().Name)
                    + " focused=" + nud.ContainsFocus
                    + " hook=" + (Field(nud, "spin") != null));

                // 先把焦点明确放到别处, 才能验证"点箭头不抢焦点"
                Field(mf, "btnStart");
                var anchor = mf.Controls.Count > 0 ? mf.Controls[0] : (Control)mf;
                anchor.Focus();
                Application.DoEvents();

                decimal v0 = nud.Value;
                ClickSpin(buttons, true);
                for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                Console.Out.WriteLine("UP    value " + v0 + " -> " + nud.Value
                    + "  containsFocus=" + nud.ContainsFocus + "  editFocused=" + (edit != null && edit.Focused));

                decimal v1 = nud.Value;
                ClickSpin(buttons, false);
                for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                Console.Out.WriteLine("DOWN  value " + v1 + " -> " + nud.Value
                    + "  containsFocus=" + nud.ContainsFocus + "  editFocused=" + (edit != null && edit.Focused));

                // 连点性能: 分段测耗时, 定位 15ms 到底花在哪
                anchor.Focus();
                nud.Value = 0;          // 测量工具自己的 ini 会累积, 先归零免得撞上限
                Application.DoEvents();
                double tHook = 0, tValue = 0;
                for (int k = 0; k < 30; k++)
                {
                    var sw = System.Diagnostics.Stopwatch.StartNew();
                    ClickSpin(buttons, true);
                    sw.Stop(); tHook += sw.Elapsed.TotalMilliseconds;

                    // 对照: 绕过箭头钩子, 直接设 Value (仍会触发完整的 ValueChanged → OnCardChanged)
                    var sw2 = System.Diagnostics.Stopwatch.StartNew();
                    nud.Value = nud.Value + 10;
                    sw2.Stop(); tValue += sw2.Elapsed.TotalMilliseconds;
                }
                Application.DoEvents();
                Console.Out.WriteLine("PERF hook=" + (tHook / 30).ToString("0.00")
                    + "ms/click  directValue=" + (tValue / 30).ToString("0.00") + "ms/set");

                // 连续快速点击复现: 逐次看焦点有没有漏进文本框
                anchor.Focus();
                Application.DoEvents();
                for (int k = 0; k < 6; k++)
                {
                    ClickSpin(buttons, true);
                    Application.DoEvents();
                    Console.Out.WriteLine("BURST#" + k + " value=" + nud.Value
                        + " containsFocus=" + nud.ContainsFocus + " editFocused=" + (edit != null && edit.Focused));
                }

                // 系统对快速第二击会额外投递 WM_LBUTTONDBLCLK (若窗口类带 CS_DBLCLKS)
                anchor.Focus();
                Application.DoEvents();
                IntPtr lpDbl = (IntPtr)((2 << 16) | 4);
                SendMessage(buttons.Handle, 0x0203, (IntPtr)1, lpDbl);   // WM_LBUTTONDBLCLK
                Application.DoEvents();
                Console.Out.WriteLine("DBLCLK value=" + nud.Value
                    + " containsFocus=" + nud.ContainsFocus + " editFocused=" + (edit != null && edit.Focused));

                // 真实快速连点的消息序列: 第1击 DOWN+UP, 第2击 DBLCLK+UP (第二击没有 DOWN)
                anchor.Focus();
                nud.Value = 0;
                Application.DoEvents();
                decimal seqStart = nud.Value;
                IntPtr lp2 = (IntPtr)((2 << 16) | 4);  // 上半=UP箭头
                SendMessage(buttons.Handle, 0x0201, (IntPtr)1, lp2);   // 1击 DOWN
                SendMessage(buttons.Handle, 0x0202, (IntPtr)0, lp2);   // 1击 UP
                SendMessage(buttons.Handle, 0x0203, (IntPtr)1, lp2);   // 2击 DBLCLK
                SendMessage(buttons.Handle, 0x0202, (IntPtr)0, lp2);   // 2击 UP
                SendMessage(buttons.Handle, 0x0201, (IntPtr)1, lp2);   // 3击 DOWN
                SendMessage(buttons.Handle, 0x0202, (IntPtr)0, lp2);   // 3击 UP
                SendMessage(buttons.Handle, 0x0203, (IntPtr)1, lp2);   // 4击 DBLCLK
                SendMessage(buttons.Handle, 0x0202, (IntPtr)0, lp2);   // 4击 UP
                Application.DoEvents();
                Console.Out.WriteLine("REAL-BURST 4 clicks: " + seqStart + " -> " + nud.Value
                    + "  delta=" + (nud.Value - seqStart) + " (expected 40)"
                    + " editFocused=" + (edit != null && edit.Focused));

                // 键盘路径必须仍然可用: 点进文本框后能用方向键
                if (edit != null)
                {
                    edit.Focus();
                    Application.DoEvents();
                    Console.Out.WriteLine("CLICK-TEXT editFocused=" + edit.Focused);

                    // 编辑态下再点箭头: 文字先落成值, 焦点收走, 插入符不该继续闪
                    decimal vEdit = nud.Value;
                    ClickSpin(buttons, true);
                    Application.DoEvents();
                    Console.Out.WriteLine("EDIT->SPIN value " + vEdit + " -> " + nud.Value
                        + " containsFocus=" + nud.ContainsFocus + " editFocused=" + edit.Focused);
                }
                // 签名缓存不能把徽标"冻住": 改规格后必须真的重建
                var specPanel = Field(mf, "flwSpecChannels") as FlowLayoutPanel;
                var cmbSpecTest = Field(mf, "cmbSpec") as Control;
                if (specPanel != null && cmbSpecTest != null && ItemCountOf(cmbSpecTest) > 1)
                {
                    string sigBefore = specPanel.Tag as string;
                    int nBefore = specPanel.Controls.Count;
                    int oldIdx = SelOf(cmbSpecTest);
                    SetSel(cmbSpecTest, (oldIdx + 1) % ItemCountOf(cmbSpecTest));
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                    Console.Out.WriteLine("SPEC-CHANGE badges " + nBefore + " -> " + specPanel.Controls.Count
                        + " sigChanged=" + (sigBefore != (specPanel.Tag as string)));
                    SetSel(cmbSpecTest, oldIdx);
                    Application.DoEvents();
                }

                // 自绘下拉: 展开后的弹层几何。行高/圆角是运行时行为, 这里量尺寸与裁剪是否生效。
                DumpComboPopup(mf, "cmbSpec", "规格");
                DumpComboPopup(mf, "cmbDefault", "捕获");

                mf.Close();
                return;
            }

            // 校准窗口: 单独量它的布局 (不测量、不碰声卡)
            if (args.Length >= 1 && args[0] == "calib")
            {
                var lanes = new System.Collections.Generic.List<CalibLane>
                {
                    new CalibLane { DeviceId = "a", Display = "扬声器 (9- Xiaomi Speaker-3178)", CurrentDelayMs = 0 },
                    new CalibLane { DeviceId = "b", Display = "扬声器 (2- Xiaomi Speaker-7801)", CurrentDelayMs = 120 }
                };
                using (var cf = new LatencyCalibrationForm(lanes, 5, delegate { }))
                {
                    cf.Show();
                    for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                    Console.Out.WriteLine("CALIB client=" + cf.ClientSize);
                    foreach (Control c in cf.Controls)
                    {
                        Console.Out.WriteLine("  " + c.GetType().Name + " '" + Trim(c.Text) + "' bounds=" + c.Bounds
                            + (c.Right > cf.ClientSize.Width ? "  <<< OVERFLOW " + (c.Right - cf.ClientSize.Width) : "")
                            + (c.Bottom > cf.ClientSize.Height ? "  <<< OVERFLOW-B " + (c.Bottom - cf.ClientSize.Height) : ""));
                    }

                    // 开始/停止 同一个按钮: 文字变、位置不变、不变宽、不溢出; 状态栏文字不能超出 556px
                    var btnStart = Field(cf, "btnStart") as Button;
                    var btnClose = Field(cf, "btnClose") as Button;
                    var lblState = Field(cf, "lblState") as Label;
                    var t = cf.GetType();
                    var setM = t.GetMethod("SetMeasuringUi", BindingFlags.Instance | BindingFlags.NonPublic);
                    var stopM = t.GetMethod("StopMeasure", BindingFlags.Instance | BindingFlags.NonPublic);
                    var measF = t.GetField("measuring", BindingFlags.Instance | BindingFlags.NonPublic);

                    Action<string> dumpStart = delegate(string tag)
                    {
                        Console.Out.WriteLine(tag + " start='" + btnStart.Text + "' enabled=" + btnStart.Enabled
                            + " bounds=" + btnStart.Bounds + " textFit=" + FitText(btnStart, 10)
                            + " | closeEnabled=" + btnClose.Enabled
                            + " | state='" + Trim(lblState.Text) + "' stateFit=" + FitLabel(lblState));
                    };

                    dumpStart("CALIB-IDLE ");
                    measF.SetValue(cf, true);
                    setM.Invoke(cf, new object[] { true });
                    Application.DoEvents();
                    dumpStart("CALIB-RUN  ");
                    stopM.Invoke(cf, null);
                    Application.DoEvents();
                    dumpStart("CALIB-STOP ");
                    measF.SetValue(cf, false);
                    setM.Invoke(cf, new object[] { false });
                    Application.DoEvents();
                    dumpStart("CALIB-BACK ");
                }
                return;
            }

            // 图标总览: 喇叭三态 + 禁用 / 增益圆圈加号 / 设置螺母 / 方框加号 / × / 刷新圆环箭头。
            // 这种"22px 下看不看得懂"的问题只能放大看像素, 悬停态要点鼠标截不到。
            if (args.Length >= 1 && args[0] == "icons")
            {
                string path = args.Length >= 2 ? args[1] : "icons.png";
                const int cell = 34, rows = 2;
                using (var hostForm = new Form
                {
                    StartPosition = FormStartPosition.Manual, Location = new Point(40, 40),
                    ClientSize = new Size(cell * 5, cell * rows + 6), BackColor = Color.White
                })
                using (var host = new Panel { Dock = DockStyle.Fill, BackColor = Color.White })
                {
                    var ctl = new Control[9];
                    for (int i = 0; i < 4; i++)
                        ctl[i] = new SpeakerButton { Location = new Point(6 + i * cell, 6) };
                    // 喇叭四态: 正常出声 / 已静音 / 音量 0 / 禁用
                    ((SpeakerButton)ctl[1]).Muted = true;
                    ((SpeakerButton)ctl[2]).VolumeValue = 0;
                    ctl[3].Enabled = false;
                    ctl[4] = new GainGlyph { Location = new Point(6, 6 + cell) };
                    ctl[5] = new GearButton { Location = new Point(6 + cell, 4 + cell) };
                    ctl[6] = new GearButton
                    {
                        Glyph = ButtonGlyph.SquarePlus,
                        Location = new Point(6 + cell * 2, 2 + cell), Size = new Size(30, 30)
                    };
                    ctl[7] = new GearButton
                    {
                        Glyph = ButtonGlyph.Cross, Location = new Point(6 + cell * 3, 4 + cell),
                        Size = new Size(20, 20), Enabled = false
                    };
                    ctl[8] = new GearButton
                    {
                        Glyph = ButtonGlyph.Refresh, Location = new Point(6 + cell * 4, 4 + cell)
                    };
                    host.Controls.AddRange(ctl);
                    hostForm.Controls.Add(host);
                    // 截图走 CopyFromScreen —— GearButton 类在 WM_PRINT (DrawToBitmap) 下拍不到
                    // (旧齿轮时代就有, 见类注释); 置顶防遮挡, 弹完随窗体一起关掉。
                    hostForm.TopMost = true;
                    hostForm.Show();
                    hostForm.Activate();
                    for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }

                    string file = System.IO.Directory.Exists(path)
                        ? System.IO.Path.Combine(path, "icons.png") : path;
                    using (var bmp = new Bitmap(hostForm.Width, hostForm.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(hostForm.Location, Point.Empty, hostForm.Size);
                        bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Console.Out.WriteLine("ICONS speaker x4 / gain / gear / add / remove -> " + file);
                }
                return;
            }

            // 「添加设备」选择弹窗: 假数据渲染一张, 看圆角下拉/描边按钮/灰显设备是否成型。
            if (args.Length >= 1 && args[0] == "adddlg")
            {
                string path = args.Length >= 2 ? args[1] : "adddlg.png";
                var texts = new[] { "[0] 扬声器 (Realtek(R) Audio)", "[1] 网易虚拟音频设备", "[2] OF24Q (NVIDIA High Definition Audio)" };
                var ids = new[] { "id0", "id1", "id2" };
                var used = new HashSet<string> { "id1" };   // 第二台灰显
                var chans = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("FL", "左声道"),
                    new KeyValuePair<string, string>("FR", "右声道")
                };
                var dlg = new AddOutputDialog(texts, ids, used, 0, chans, "FR");
                dlg.StartPosition = FormStartPosition.Manual;
                dlg.Location = new Point(40, 40);
                dlg.Show();
                dlg.TopMost = true;
                for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                string file = System.IO.Directory.Exists(path)
                    ? System.IO.Path.Combine(path, "adddlg.png") : path;
                using (var bmp = new Bitmap(dlg.Width, dlg.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(dlg.Location, Point.Empty, dlg.Size);
                    bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                }
                dlg.Close();
                Console.Out.WriteLine("ADDDLG rendered -> " + file);
                return;
            }

            // RoundNumericShell 内部布局探针: 打印数字框与其子控件的真实边界/对齐,
            // 用来定位「数字与单位之间多出一条空隙」是哪一层布局造成的
            if (args.Length >= 1 && args[0] == "nudprobe")
            {
                using (var hostForm = new Form
                {
                    StartPosition = FormStartPosition.Manual, Location = new Point(40, 40),
                    ClientSize = new Size(300, 60), BackColor = Color.White, Font = new Font("Microsoft YaHei UI", 9F)
                })
                {
                    var nud = new NoWheelNumeric { Minimum = 0, Maximum = 2000, Increment = 10, Value = 0 };
                    var shell = new RoundNumericShell(nud, "延迟", "ms");
                    shell.Location = new Point(10, 15);
                    hostForm.Controls.Add(shell);
                    // 谁在改边界: 打印子控件尺寸变化时的调用堆栈 (只留 Frame 行)
                    foreach (Control c in nud.Controls)
                    {
                        Control cc = c;
                        cc.SizeChanged += (s, e) =>
                        {
                            var st = new System.Diagnostics.StackTrace(true);
                            var frames = st.GetFrames();
                            var lines = new List<string>();
                            for (int i = 1; i < frames.Length && lines.Count < 25; i++)
                            {
                                var mth = frames[i].GetMethod();
                                if (mth.DeclaringType != null && !mth.DeclaringType.Name.StartsWith("StackTrace"))
                                    lines.Add(mth.DeclaringType.Name + "." + mth.Name);
                            }
                            Console.Out.WriteLine("  TRACE " + cc.GetType().Name + " -> " + cc.Bounds
                                + " | " + string.Join(" <- ", lines));
                        };
                    }
                    hostForm.Show();
                    for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                    Console.Out.WriteLine("SHELL bounds=" + shell.Bounds
                        + " arrowX=" + shell.GetType().GetField("arrowX",
                              System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)
                              ?.GetValue(shell));
                    Console.Out.WriteLine("INNER bounds=" + nud.Bounds
                        + " align=" + nud.TextAlign + " client=" + nud.ClientSize);
                    foreach (Control c in nud.Controls)
                        Console.Out.WriteLine("  CHILD " + c.GetType().Name + " bounds=" + c.Bounds
                            + " visible=" + c.Visible);
                    foreach (Control c in shell.Controls)
                        Console.Out.WriteLine("  SHELLCHILD " + c.GetType().Name
                            + " '" + (c is Label lb ? lb.Text : "?") + "' bounds=" + c.Bounds);
                    // 时间序列: 每 100ms 打一次编辑框/按钮边界, 看它们何时被谁改回去
                    for (int i = 0; i < 10; i++)
                    {
                        System.Threading.Thread.Sleep(100);
                        Application.DoEvents();
                        foreach (Control c in nud.Controls)
                            Console.Out.WriteLine("  t+" + (i + 1) + "00ms " + c.GetType().Name
                                + " bounds=" + c.Bounds + " visible=" + c.Visible);
                    }
                }
                return;
            }

            // 静音按钮的接线自检: 「点一下真的静音」这件事光看图证明不了 ——
            // 图只能证明图标换了样子, 证明不了 MutedChanged 有没有接到增益计算/落盘上。
            // 这里把按钮的 OnClick 反射调一遍, 再读引擎真正用的 CurrentMaster()。
            if (args.Length >= 1 && args[0] == "mute")
            {
                using (var sf = new MainForm())
                {
                    sf.Show();
                    for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

                    var btnM = Field(sf, "btnMuteMaster") as SpeakerButton;
                    var volM = Field(sf, "volMaster") as VolumeSlider;
                    var cardList = Field(sf, "cards") as System.Collections.IList;
                    var curMaster = typeof(MainForm).GetMethod("CurrentMaster",
                        BindingFlags.Instance | BindingFlags.NonPublic);

                    // 先记下原值, 全部跑完再放回去 (MainForm 关闭时会落盘, 别把用户设置改了)
                    bool wasMuted = btnM.Muted;
                    int wasVol = volM.Value;
                    var card = cardList != null && cardList.Count > 0 ? cardList[0] as OutputCardControl : null;
                    bool wasCardMuted = card != null && card.btnMute.Muted;
                    int wasCardVol = card != null ? card.volTrack.Value : 100;

                    Action<string, bool> check = (name, ok) =>
                        Console.Out.WriteLine((ok ? "  ok   " : "  BAD  ") + name);

                    Console.Out.WriteLine("MUTE 初始 master.muted=" + btnM.Muted + " vol=" + volM.Value);

                    Click(btnM);
                    check("点喇叭 → 静音 (eng=" + curMaster.Invoke(sf, null) + ")", btnM.Muted && (float)curMaster.Invoke(sf, null) == 0f);
                    Click(btnM);
                    check("再点 → 恢复 (eng=" + curMaster.Invoke(sf, null) + ")", !btnM.Muted && (float)curMaster.Invoke(sf, null) > 0f);

                    // 注意: 自动静音规则挂在 Scroll 上 (用户拖动才发), 程序赋值 Value 不算"用户操作",
                    // 所以这里必须走 SetValue(v, fromUser:true) 才算真的模拟了一次拖动。
                    Drag(volM, 0);
                    check("音量拖到 0 → 自动静音", btnM.Muted);
                    Drag(volM, 60);
                    check("拖离 0 → 自动恢复", !btnM.Muted);

                    if (card != null)
                    {
                        Click(card.btnMute);
                        check("卡片喇叭 → Model.Mute=" + card.Model.Mute, card.Model.Mute);
                        Click(card.btnMute);
                        check("卡片再点 → Model.Mute=" + card.Model.Mute, !card.Model.Mute);
                        Drag(card.volTrack, 0);
                        check("卡片音量 0 → 自动静音", card.btnMute.Muted);
                    }

                    // 还原
                    btnM.Muted = wasMuted; volM.Value = wasVol;
                    if (card != null) { card.btnMute.Muted = wasCardMuted; card.volTrack.Value = wasCardVol; }
                    Pump();
                    sf.Close();
                }
                return;
            }


            // 四种状态的按钮配色: 反射调 SetAppState, 把「开始分流」按钮逐态画出来拼成一张。
            // 配色与文案没法用几何量测证明, 只能看图 (也顺手把文案打进控制台, 便于 diff)。
            if (args.Length >= 1 && args[0] == "states")
            {
                var sf2 = new MainForm();
                sf2.Size = new Size(520, 520);
                sf2.Show();
                for (int i = 0; i < 15; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                MethodInfo setState = typeof(MainForm).GetMethod("SetAppState", BindingFlags.Instance | BindingFlags.NonPublic);
                StartButton btn = Field(sf2, "btnStart") as StartButton;
                Label runLbl = Field(sf2, "lblRunState") as Label;
                if (setState == null || btn == null) { Console.Out.WriteLine("STATES unavailable"); return; }

                // 冷启动断言: 什么都不切 (真机打开就是 Ready), 光环也必须在呼吸 ——
                // 曾经 Mode 赋值因"值没变"早退导致动画钟从未启动, 只有切过状态才呼吸, 这条管住它
                byte[, ,] c1 = ButtonGlyphZone(btn);
                for (int k = 0; k < 24; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                byte[, ,] c2 = ButtonGlyphZone(btn);
                Console.Out.WriteLine("ANIM coldstart diff=" + ZoneDiff(c1, c2) + " (0=钟没跑, >0=打开就在呼吸)");

                string path = args.Length >= 2 ? args[1] : "states.png";
                AppState[] states =
                {
                    AppState.Ready, AppState.Starting, AppState.Running, AppState.Stopping, AppState.Stopped
                };
                int bw = btn.Width, bh = btn.Height, gap = 12, padX = 12;
                using (var bmp = new Bitmap(states.Length * (bw + gap) + padX, bh + padX * 2))
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    // 背景用窗体的常态底色: 按钮四角因为 Region 裁剪会露出"没画到"的区域,
                    // DrawToBitmap 在那里留的是控件背色 —— 白底截图上看就像角上有灰块
                    g.Clear(SystemColors.Control);
                    for (int i = 0; i < states.Length; i++)
                    {
                        setState.Invoke(sf2, new object[] { states[i] });
                        // 换色渐变要 220ms 才落位: 等它走完再抓, 断言的是终态而不是半路色
                        for (int k = 0; k < 20; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                        Console.Out.WriteLine("STATE " + AppStateMachine.Text(states[i])
                            + " text='" + btn.Text + "' enabled=" + btn.Enabled
                            + " mode=" + btn.Mode
                            + " tint=" + btn.Tint.R + "," + btn.Tint.G + "," + btn.Tint.B
                            + " runLabel=" + (runLbl == null ? "(null)" : runLbl.Visible ? "'" + runLbl.Text + "'@" + runLbl.Left + "," + runLbl.Top : "(hidden)"));
                        using (var one = new Bitmap(bw, bh))
                        {
                            // 先铺白: RoundButton 用 Region 裁了圆角, 四个角不在绘制区内,
                            // DrawToBitmap 会把它留成未初始化的黑色 —— 看着像"角上有灰块"
                            using (var og = System.Drawing.Graphics.FromImage(one)) og.Clear(Color.White);
                            btn.DrawToBitmap(one, new Rectangle(0, 0, bw, bh));
                            g.DrawImage(one, padX + i * (bw + gap), padX);
                        }
                    }

                    // 动效在动 (不是静止贴图) 的证据: 同一状态隔 350ms 抓两帧, 图形区必须不同
                    setState.Invoke(sf2, new object[] { AppState.Starting });
                    for (int k = 0; k < 20; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                    byte[, ,] f1 = ButtonGlyphZone(btn);
                    for (int k = 0; k < 24; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                    byte[, ,] f2 = ButtonGlyphZone(btn);
                    Console.Out.WriteLine("ANIM working diff=" + ZoneDiff(f1, f2) + " (0=静止贴图, >0=转环在转)");

                    setState.Invoke(sf2, new object[] { AppState.Running });
                    for (int k = 0; k < 20; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                    byte[, ,] f3 = ButtonGlyphZone(btn);
                    for (int k = 0; k < 24; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                    byte[, ,] f4 = ButtonGlyphZone(btn);
                    Console.Out.WriteLine("ANIM pulse   diff=" + ZoneDiff(f3, f4) + " (0=静止贴图, >0=呼吸点在动)");

                    // Idle 的 ▶ 三角呼吸 (用户指定"统一三角形呼吸"): 常态下也必须不是静止贴图
                    setState.Invoke(sf2, new object[] { AppState.Ready });
                    for (int k = 0; k < 20; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                    byte[, ,] f5 = ButtonGlyphZone(btn);
                    for (int k = 0; k < 24; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                    byte[, ,] f6 = ButtonGlyphZone(btn);
                    Console.Out.WriteLine("ANIM idle    diff=" + ZoneDiff(f5, f6) + " (0=静止贴图, >0=三角光环在呼吸)");

                    // 刷新按钮无动效 (用户明确撤掉): 常态隔 360ms 必须是静止贴图 (点击转圈另有 spin 探针)
                    var refBtn = Field(sf2, "btnRefresh") as GearButton;
                    if (refBtn != null && refBtn.Enabled)
                    {
                        byte[, ,] r1 = ButtonGlyphZone(refBtn);
                        for (int k = 0; k < 24; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                        byte[, ,] r2 = ButtonGlyphZone(refBtn);
                        Console.Out.WriteLine("ANIM refresh diff=" + ZoneDiff(r1, r2) + " (>0=意外在动, 应≈0)");
                    }

                    // 规格行实拍 (运行中): 状态字替换声道标注后的样子
                    // DrawToBitmap 在高 DPI 下会把内容按 DPI 比例画进同样大小的位图 (偏移随之放大),
                    // 所以整窗落盘、由外部按像素找红字裁剪, 不在探针里猜偏移
                    using (var full = new Bitmap(sf2.Width, sf2.Height))
                    {
                        sf2.DrawToBitmap(full, new Rectangle(0, 0, sf2.Width, sf2.Height));
                        full.Save("runstate-full.png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Console.Out.WriteLine("RUNSTATE-FULL -> runstate-full.png");

                    // 插播桥验证: 模拟四条运行期 [状态] 日志走 Log(), 读回状态字
                    MethodInfo logM = typeof(MainForm).GetMethod("Log",
                        BindingFlags.Instance | BindingFlags.NonPublic, null, new[] { typeof(string) }, null);
                    var lblM = (System.Windows.Forms.Control)Field(sf2, "lblRunState");
                    var noteT = (System.Windows.Forms.Timer)Field(sf2, "runNoteTimer");
                    if (logM != null && lblM != null)
                    {
                        string[] fakes =
                        {
                            "[状态] 缓冲水位 调节 L=62ms R=61ms 差=1ms 瞬时 L=64ms R=63ms 校正=+3ppm 最低 L=58ms R=57ms 欠载 L=0 R=0 电平 L=12% R=11%",
                            "[状态] 捕获源暂无数据（应用暂停或静音），输出待命",
                            "[状态] 缓冲水位已恢复: 62ms / 目标 120ms",
                            "[状态] 缓冲水位 [4] 66/29ms +496ppm 电平0.6% | [5] 66/29ms +375ppm 电平0.4% | [7] 65/29ms +251ppm 电平0.5%",
                            "[状态] 捕获源恢复数据（此前静默约 12 秒）",
                            "[状态] 运行中"      // 状态机迁移条目: 不许插播, 状态字应保持上一条
                        };
                        foreach (var fk in fakes)
                        {
                            logM.Invoke(sf2, new object[] { fk });
                            Application.DoEvents();
                            Console.Out.WriteLine("MIRROR '" + fk.Substring(7, Math.Min(16, fk.Length - 7))
                                + "…' -> '" + lblM.Text + "' color=" + lblM.ForeColor.R + ","
                                + lblM.ForeColor.G + "," + lblM.ForeColor.B
                                + " holdTimer=" + (noteT != null && noteT.Enabled)
                                + " rect=" + lblM.Left + "," + lblM.Top + "x" + lblM.Width + "x" + lblM.Height);
                        }
                        // 迁移接管: 状态一变, 插播计时器必须停, 文案回到权威版
                        setState.Invoke(sf2, new object[] { AppState.Stopping });
                        Application.DoEvents();
                        Console.Out.WriteLine("MIRROR takeover -> '" + lblM.Text + "' holdTimer=" + (noteT != null && noteT.Enabled));
                        setState.Invoke(sf2, new object[] { AppState.Running });
                        Application.DoEvents();

                        // 插播态整窗实拍 (先回 Running, 再播一条事件让状态字停在插播文案上)
                        logM.Invoke(sf2, new object[] { fakes[0] });
                        for (int k = 0; k < 8; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                        using (var full2 = new Bitmap(sf2.Width, sf2.Height))
                        {
                            sf2.DrawToBitmap(full2, new Rectangle(0, 0, sf2.Width, sf2.Height));
                            full2.Save("runnote-full.png", System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.Out.WriteLine("RUNNOTE-FULL -> runnote-full.png");

                    // 跑马灯断言: 塞进 80px 窄条 → 控件占满整条 + 滚动钟启动;
                    // 隔 300ms 两帧差分 >0 证明真的在滚 (静止贴图恒为 0)
                    ((RunNoteLabel)lblM).PlaceIn(new Rectangle(200, 151, 80, 20));
                    Application.DoEvents();
                    var mq = (System.Windows.Forms.Timer)Field(lblM, "marquee");
                    using (var nf1 = new Bitmap(80, 20))
                    using (var nf2 = new Bitmap(80, 20))
                    {
                        lblM.DrawToBitmap(nf1, new Rectangle(0, 0, 80, 20));
                        System.Threading.Thread.Sleep(300);
                        for (int k = 0; k < 10; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                        lblM.DrawToBitmap(nf2, new Rectangle(0, 0, 80, 20));
                        int d = 0;
                        for (int yy = 0; yy < 20; yy++) for (int xx = 0; xx < 80; xx++)
                            if (nf1.GetPixel(xx, yy) != nf2.GetPixel(xx, yy)) d++;
                        Console.Out.WriteLine("MARQUEE narrow W=" + lblM.Width + " scrollTimer=" + (mq != null && mq.Enabled)
                            + " diff=" + d + " (0=静止贴图, >0=在滚)");
                    }
                    ((RunNoteLabel)lblM).PlaceIn(new Rectangle(191, 151, 258, 20));   // 还原宽档
                    Application.DoEvents();

                    }

                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.Out.WriteLine("STATES -> " + path);
                return;
            }

            // 实屏截图: DrawToBitmap 走 WM_PRINT, 抓不到由系统/DWM 画在窗口上的东西
            // (焦点矩形、默认按钮框)。要看"刚打开时长什么样", 只能 CopyFromScreen。
            if (args.Length >= 1 && args[0] == "live")
            {
                int lw = 500, lh = 500;
                if (args.Length >= 3) { int.TryParse(args[1], out lw); int.TryParse(args[2], out lh); }
                string path = args.Length >= 4 ? args[3] : "live.png";
                bool focusStart = args.Length >= 5 && args[4] == "focus";

                var sf = new MainForm();
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new Point(40, 40);
                sf.Size = new Size(lw, lh);
                // 截图走 CopyFromScreen, 会被别的窗口盖住 → 抓之前先置顶, 抓完随窗体一起关掉
                sf.TopMost = true;
                sf.Show();
                sf.Activate();
                for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }

                if (focusStart)
                {
                    Control btn = Field(sf, "btnStart") as Control;
                    if (btn != null) { btn.Focus(); sf.ActiveControl = btn; }
                    for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                }

                using (var bmp = new Bitmap(sf.Width, sf.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(sf.Location, Point.Empty, sf.Size);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.Out.WriteLine("LIVE form=" + sf.Size + " active="
                    + (sf.ActiveControl == null ? "null" : sf.ActiveControl.Name + "/" + sf.ActiveControl.GetType().Name)
                    + " -> " + path);
                sf.Close();
                return;
            }

            // 截图: 把真实窗体画到位图, 用来肉眼核对自绘控件 (圆角/配色/对齐) ——
            // 几何量测只能证明"放得下", 证明不了"看着对"。弹层是独立窗口, 单独再抓一张。
            if (args.Length >= 1 && args[0] == "shot")
            {
                int sw = 500, sh = 500;
                if (args.Length >= 3) { int.TryParse(args[1], out sw); int.TryParse(args[2], out sh); }
                string path = args.Length >= 4 ? args[3] : "shot.png";

                var sf = new MainForm();
                sf.Size = new Size(sw, sh);
                sf.Show();
                for (int i = 0; i < 15; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                using (var bmp = new Bitmap(sf.Width, sf.Height))
                {
                    sf.DrawToBitmap(bmp, new Rectangle(0, 0, sf.Width, sf.Height));
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.Out.WriteLine("SHOT form=" + sf.Size + " -> " + path);

                // 弹层: 展开规格下拉后单独抓 (含自绘圆角阴影)
                RoundedCombo combo = Field(sf, "cmbSpec") as RoundedCombo;
                if (combo != null)
                {
                    MethodInfo openM = typeof(RoundedCombo).GetMethod("OpenDropDown", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (openM != null)
                    {
                        openM.Invoke(combo, null);
                        for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                        ToolStrip pts = Field(combo, "popup") as ToolStrip;
                        if (pts != null && pts.Visible)
                        {
                            using (var pb = new Bitmap(pts.Width, pts.Height))
                            {
                                pts.DrawToBitmap(pb, new Rectangle(0, 0, pts.Width, pts.Height));
                                string pp = System.IO.Path.Combine(
                                    System.IO.Path.GetDirectoryName(path), "popup.png");
                                pb.Save(pp, System.Drawing.Imaging.ImageFormat.Png);
                                Console.Out.WriteLine("SHOT popup=" + pts.Size + " -> " + pp);
                            }
                        }
                    }
                }
                return;
            }

            // 实屏抓弹层: DrawToBitmap 走 WM_PRINT, 与真实屏幕合成未必一致 (尤其 SetWindowRgn
            // 裁出来的圆角边缘)。要判断"圆角有没有裂缝/缺口", 只能 CopyFromScreen。
            if (args.Length >= 1 && args[0] == "livepopup")
            {
                int lw = 500, lh = 500;
                if (args.Length >= 3) { int.TryParse(args[1], out lw); int.TryParse(args[2], out lh); }
                string path = args.Length >= 4 ? args[3] : "livepopup.png";

                var sf = new MainForm();
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new Point(40, 40);
                sf.Size = new Size(lw, lh);
                sf.TopMost = true;
                sf.Show();
                sf.Activate();
                Pump();

                RoundedCombo combo = Field(sf, "cmbSpec") as RoundedCombo;
                ToolStrip ts = null;
                if (combo != null)
                {
                    System.Reflection.MethodInfo openM = typeof(RoundedCombo)
                        .GetMethod("OpenDropDown", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
                    if (openM != null) openM.Invoke(combo, null);
                    Pump();
                    ts = Field(combo, "popup") as ToolStrip;
                }
                if (ts == null || !ts.Visible)
                {
                    Console.Out.WriteLine("LIVEPOPUP popup=closed");
                    sf.Close();
                    return;
                }

                Rectangle r = ts.Bounds;                       // 顶层窗口, 已是屏幕坐标
                r.Inflate(8, 8);                               // 四周留 8px 背景, 才看得出角上有没有缝
                Rectangle scr = Screen.FromControl(sf).Bounds;
                r.Intersect(scr);

                Console.Out.WriteLine("LIVEPOPUP size=" + ts.Size + " client=" + ts.ClientSize
                    + " region=" + (ts.Region == null ? "NULL" : "set")
                    + " opacity=" + ((ToolStripDropDown)ts).Opacity
                    + " dropshadow=" + ((ToolStripDropDown)ts).DropShadowEnabled);

                using (var bmp = new Bitmap(r.Width, r.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.Out.WriteLine("LIVEPOPUP at=" + ts.Bounds + " -> " + path);
                sf.Close();
                return;
            }

            // 行内声道下拉的弹层: 验证「不使用」这条的文案, 以及它在 92 设计像素宽的弹层里
            // 放不放得下 (去掉全角括号就是为了这个 —— 括号本身吃掉近 20px)。
            if (args.Length >= 1 && args[0] == "rowpopup")
            {
                string path = args.Length >= 2 ? args[1] : "rowpopup.png";

                var sf = new MainForm();
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new Point(40, 40);
                sf.Size = new Size(700, 800);
                sf.TopMost = true;
                sf.Show();
                sf.Activate();
                Pump();

                OutputCardControl card = null;
                var cardList = Field(sf, "cards") as System.Collections.IList;
                if (cardList != null)
                    foreach (object o in cardList)
                    {
                        var c = o as OutputCardControl;
                        if (c != null && c.Model.Multi) { card = c; break; }
                    }
                if (card == null) { Console.Out.WriteLine("ROWPOPUP no multi-channel card"); sf.Close(); return; }

                RoundedCombo combo = null;
                foreach (Control ch in card.Controls)
                {
                    var rc = ch as RoundedCombo;
                    if (rc != null && rc != card.cmbDevice && rc != card.cmbChannel) { combo = rc; break; }
                }
                if (combo == null) { Console.Out.WriteLine("ROWPOPUP no row combo"); sf.Close(); return; }

                // 逐条量"文字宽度 vs 可用宽度": 宽度收到刚好时, 差 1px 就会变成省略号,
                // 光看"能不能放下当前这条"不够 —— 弹层里每一条都得放得下。
                int tw = combo.Width - RoundedCombo.TextPadX - RoundedCombo.ArrowZone;
                var items = new System.Text.StringBuilder();
                for (int i = 0; i < combo.Items.Count; i++)
                {
                    string it = combo.Items[i] == null ? "" : combo.Items[i].ToString();
                    int ext = TextRenderer.MeasureText(it, combo.Font,
                        new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
                    items.Append(i == 0 ? "" : " | ").Append(it)
                         .Append("(text=").Append(ext)
                         .Append(ext <= tw ? " fits" : " CLIP").Append(")");
                }
                Console.Out.WriteLine("ROWPOPUP combo=" + combo.Width + "x" + combo.Height
                    + " textArea=" + tw + " items=" + items);

                typeof(RoundedCombo)
                    .GetMethod("OpenDropDown", BindingFlags.Instance | BindingFlags.NonPublic)
                    .Invoke(combo, null);
                Pump();
                ToolStrip ts = Field(combo, "popup") as ToolStrip;
                if (ts == null || !ts.Visible) { Console.Out.WriteLine("ROWPOPUP popup=closed"); sf.Close(); return; }

                Rectangle r = ts.Bounds;
                r.Inflate(8, 8);
                r.Intersect(Screen.FromControl(sf).Bounds);
                using (var bmp = new Bitmap(r.Width, r.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
                Console.Out.WriteLine("ROWPOPUP at=" + ts.Bounds + " -> " + path);
                sf.Close();
                return;
            }

            // 顶部捕获行: 下拉 + 刷新合成一组后的几何, 以及刷新动画的两态。
            if (args.Length >= 1 && args[0] == "top")
            {
                var sf = new MainForm();
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new Point(40, 40);
                sf.Size = new Size(700, 800);
                sf.TopMost = true;
                sf.Show();
                sf.Activate();
                Pump();

                var body = Field(sf, "cmbDefault") as RoundedCombo;
                var refresh = Field(sf, "btnRefresh") as GearButton;
                var settings = Field(sf, "btnSettings") as GearButton;
                var startB = Field(sf, "btnStart") as Control;
                if (body == null || refresh == null) { Console.Out.WriteLine("TOP no controls"); sf.Close(); return; }

                Action<string> dump = tag =>
                {
                    int lg = startB != null && body.Bounds.Right <= startB.Left
                        ? body.Left - refresh.Right : -1;
                    int rg = startB != null && body.Bounds.Right <= startB.Left
                        ? startB.Left - body.Right : -1;
                    Console.Out.WriteLine("TOP " + tag
                        + " refresh=" + refresh.Bounds
                        + " body=" + body.Bounds
                        + " start=" + (startB == null ? "-" : startB.Bounds.ToString())
                        + " gaps=" + lg + "/" + rg + (lg >= 0 && rg >= 0 && Math.Abs(lg - rg) <= 1 ? " (sym)" : "")
                        + " gear=" + (settings == null ? "-" : settings.Bounds.ToString())
                        + " enabled=" + body.Enabled + "/" + refresh.Enabled
                        + " spinning=" + refresh.IsSpinning);
                };

                Action<string> shot = path =>
                {
                    for (int i = 0; i < 3; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    using (var bmp = new Bitmap(sf.Width, sf.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(sf.Location, Point.Empty, sf.Size);
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                };

                dump("state0");
                shot("top-shell.png");

                // 窄窗口: 壳要缩、按钮要塞回车体, 不能压到齿轮
                sf.Width = 470;
                Pump();
                dump("narrow");
                shot("top-narrow.png");
                sf.Width = 700;
                Pump();

                // 点击刷新: 立即查一次 (StartSpin 在 Click 里同步执行), 角度此刻必须还是 0 (起点)
                MethodInfo click = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
                click.Invoke(refresh, new object[] { EventArgs.Empty });
                dump("spin-click angle=" + SpinAngle(refresh));

                // 转半路: 采样整圈的角度轨迹 —— 必须从 0 单调涨到 360 再归 0,
                // 不是"设个标志位不动"。kick 在 600ms 才触发, 这段采样不会被枚举打断。
                var swMid = System.Diagnostics.Stopwatch.StartNew();
                long nextSample = 0;
                var seq = new System.Text.StringBuilder();
                bool midShot = false;
                while (swMid.ElapsedMilliseconds < 580)
                {
                    long el = swMid.ElapsedMilliseconds;
                    if (el >= nextSample)
                    {
                        seq.Append(el).Append("ms=").Append(SpinAngle(refresh)).Append(' ');
                        nextSample += 70;
                    }
                    if (!midShot && el >= 120) { midShot = true; shot("top-spin.png"); }
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(15);
                }
                dump("spin-seq " + seq.ToString().Trim());

                // 转满一圈必须自己**回到原位**: 角度归 0、图标不再转, 不会歪着停在半路
                var sw = System.Diagnostics.Stopwatch.StartNew();
                while (sw.ElapsedMilliseconds < 2200) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
                dump("spin-end angle=" + SpinAngle(refresh));
                shot("top-spin-end.png");

                sf.Close();
                return;
            }

            // 真拖鼠标: 边缘能不能缩放、标题栏能不能拖窗。命中码对不等于系统真的会进缩放循环。
            if (args.Length >= 1 && args[0] == "resizeprobe")
            {
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(100, 100);
                mf.Size = new Size(900, 700);
                mf.TopMost = true;
                mf.Show(); mf.Activate(); Pump(); Pump();

                Console.Out.WriteLine("RS start size=" + mf.Size + " client=" + mf.ClientSize
                    + " hitR=" + HitCode(mf, mf.ClientSize.Width - 3, 300)
                    + " hitB=" + HitCode(mf, 400, mf.ClientSize.Height - 3));

                // 消息流水: 缩放到底有没有"进循环" (WM_ENTERSIZEMOVE), 以及点击落在哪
                var log = new StringBuilder();
                var filt = new MsgLog(log);
                Application.AddMessageFilter(filt);
                var spy = new MsgSpy();
                spy.AssignHandle(mf.Handle);
                foreach (int inset in new[] { 3, 6 })
                {
                    // 每轮把窗口摆回原位: 前一轮拖完几何全变了, 下一轮的起点就没意义
                    mf.Bounds = new Rectangle(100, 100, 900, 700);
                    mf.WindowState = FormWindowState.Normal;
                    Pump(); Pump();
                    // 真实用户可能正按着鼠标: 等左键抬起再模拟, 否则按下/抬起序被搅乱, 拖拽挂死
                    for (int i = 0; i < 30 && (GetAsyncKeyState(0x01) & 0x8000) != 0; i++)
                        System.Threading.Thread.Sleep(50);
                    int before = mf.Width;
                    log.Clear();
                    spy.Log.Clear();
                    DragMouse(new Point(mf.Right - inset, mf.Top + 300), 80, 0);
                    Console.Out.WriteLine("RS drag inset=" + inset + " dW=" + (mf.Width - before)
                        + " msgs=" + log.ToString().Trim());
                    Console.Out.WriteLine("RS   spy= " + spy.Log.ToString().Trim());
                }
                spy.ReleaseHandle();
                Application.RemoveMessageFilter(filt);

                var seq = new StringBuilder();
                var tick = new System.Windows.Forms.Timer { Interval = 25 };
                tick.Tick += (s0, e0) => seq.Append(mf.Width).Append(' ');
                tick.Start();
                DragMouse(new Point(mf.Right - 3, mf.Top + 300), 80, 0);
                tick.Stop();
                Console.Out.WriteLine("RS after-right-drag size=" + mf.Size + " seq= " + seq);

                DragMouse(new Point(mf.Left + 300, mf.Top + 15), 40, 30);
                Console.Out.WriteLine("RS after-caption-drag loc=" + mf.Location + " size=" + mf.Size);

                Console.Out.Flush();
                mf.Close();
                Environment.Exit(0);
            }

            // 谁把标题条刷成浅蓝渐变: 分别禁用"裸窗体"和"真实主窗体", 看这条渐变是否只跟
            // EnableWindow(FALSE) 有关 (与设置页本身无关); 再用 PrintWindow 区分"画进窗口表面"
            // 还是"DWM 合成在外面"。
            if (args.Length >= 1 && args[0] == "disprobe")
            {
                Action<Form, string> cap = (fm, tag) =>
                {
                    Pump();
                    var r = new Rectangle(fm.Left, fm.Top, fm.Width, Math.Min(64, fm.Height));
                    using (var bmp = new Bitmap(r.Width, r.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                        bmp.Save("dis-" + tag + ".png", System.Drawing.Imaging.ImageFormat.Png);
                        var sbs = new StringBuilder("DIS " + tag);
                        foreach (int x in new[] { 8, r.Width / 2, r.Width - 80 })
                        {
                            Color c = bmp.GetPixel(x, 10);
                            sbs.Append(" x").Append(x).Append('=').Append(c.R).Append(',').Append(c.G).Append(',').Append(c.B);
                        }
                        Console.Out.WriteLine(sbs.ToString());
                    }
                };

                // 1) 裸窗体: 没有自绘标题栏、没有自定义样式位
                var bare = new Form
                {
                    Text = "bare",
                    FormBorderStyle = FormBorderStyle.None,
                    BackColor = Color.White,
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(500, 120),
                    Size = new Size(420, 260),
                    TopMost = true
                };
                bare.Show(); bare.Activate(); Pump();
                cap(bare, "bare-on");
                bare.Enabled = false; Pump();
                cap(bare, "bare-off");
                bare.Enabled = true; Pump();
                bare.Close();

                // 2) 主窗体: 正常 / 被禁用 / 再恢复
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(60, 80);
                mf.Size = new Size(900, 700);
                mf.TopMost = true;
                mf.Show(); mf.Activate(); Pump(); Pump();
                cap(mf, "main-on");
                mf.Enabled = false; Pump(); Pump();
                cap(mf, "main-off");

                // PrintWindow: 让窗口自己把表面画一遍; 若蓝条在, 说明它已在窗口表面里
                using (var bmp = new Bitmap(mf.Width, mf.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                    {
                        IntPtr hdc = g.GetHdc();
                        PrintWindow(mf.Handle, hdc, 2);   // PW_RENDERFULLCONTENT
                        g.ReleaseHdc(hdc);
                    }
                    bmp.Save("dis-main-off-print.png", System.Drawing.Imaging.ImageFormat.Png);
                    Color c = bmp.GetPixel(mf.Width / 2, 10);
                    Console.Out.WriteLine("DIS printwindow-off x-mid=" + c.R + "," + c.G + "," + c.B);
                }

                mf.Enabled = true; Pump(); Pump();
                cap(mf, "main-on2");

                // 4) 装上"不画 NC 区"的处理后, 再失活一次: 标题条应保持窗底色
                var chrome = new ChromeSpy();
                chrome.AssignHandle(mf.Handle);
                var other2 = new Form
                {
                    Text = "other2",
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(1000, 500),
                    Size = new Size(300, 200),
                    TopMost = true
                };
                other2.Show(); other2.Activate(); Pump(); Pump();
                cap(mf, "main-deact-nonc");
                other2.Close(); Pump();
                mf.Activate(); Pump(); Pump();
                cap(mf, "main-react-nonc");
                chrome.ReleaseHandle();

                // 3) 只是"失去激活"呢: 拿另一个窗口抢焦点, 不做禁用、不开模态
                var other = new Form
                {
                    Text = "other",
                    StartPosition = FormStartPosition.Manual,
                    Location = new Point(1000, 500),
                    Size = new Size(300, 200),
                    TopMost = true
                };
                other.Show(); other.Activate(); Pump(); Pump();
                cap(mf, "main-deact");
                other.Close(); Pump();
                mf.Activate(); Pump(); Pump();
                cap(mf, "main-react");

                mf.Close();
                Environment.Exit(0);
            }

            // 打开设置页**期间**主窗口长什么样: ShowDialog 会独占消息循环, 探针自己停在那里,
            // 只能靠 Timer (在模态循环里照常投递) 抓屏 + 读样式位。用户报的"打开设置后会变"
            // 必须在这一刻才看得见。
            if (args.Length >= 1 && args[0] == "opendlg")
            {
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(60, 80);
                mf.Size = new Size(900, 700);
                mf.TopMost = true;
                mf.Show();
                mf.Activate();
                Pump(); Pump();

                // 标题条整段 (含窗口左右边缘各 4px, 好看清边线) 从屏幕上抓下来
                Action<string> strip = tag =>
                {
                    var r = new Rectangle(mf.Left - 4, mf.Top - 4, mf.Width + 8, Math.Min(72, mf.Height));
                    if (r.Right > Screen.PrimaryScreen.Bounds.Right) r.Width = Screen.PrimaryScreen.Bounds.Right - r.Left;
                    using (var bmp = new Bitmap(r.Width, r.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                        bmp.Save("opendlg-" + tag + ".png", System.Drawing.Imaging.ImageFormat.Png);
                        // 抓完立刻采样: 标题条中段 + 齿轮左侧 + 窗内左上角
                        var sbs = new StringBuilder("STRIP " + tag);
                        int[] xs = { 10, r.Width / 2, r.Width - 120, r.Width - 60, r.Width - 2 };
                        foreach (int x in xs)
                        {
                            if (x < 0 || x >= r.Width) continue;
                            Color c = bmp.GetPixel(x, 12);
                            sbs.Append(" x").Append(x).Append('=').Append(c.R).Append(',').Append(c.G).Append(',').Append(c.B);
                        }
                        Console.Out.WriteLine(sbs.ToString());
                    }
                };

                Action<string> st = tag =>
                {
                    int style = GetWindowLong(mf.Handle, -16);
                    Console.Out.WriteLine("DLG " + tag
                        + " style=0x" + style.ToString("X8")
                        + " thickframe=" + ((style & 0x00040000) != 0 ? "y" : "n")
                        + " caption=" + ((style & 0x00C00000) == 0x00C00000 ? "y" : "n")
                        + " size=" + mf.Size + " client=" + mf.ClientSize
                        + " padTop=" + mf.Padding.Top + " state=" + mf.WindowState
                        + " enabled=" + mf.Enabled + " forms=" + Application.OpenForms.Count
                        + " hitR=" + HitCode(mf, mf.ClientSize.Width - 3, 200)
                        + " hitCap=" + HitCode(mf, 240, 12));
                };

                st("before");
                strip("before");

                // 设置页已改为"更多"弹层 (非模态): OpenSettings 立刻返回, 这里按开 → 抓 → 关推进
                MethodInfo openM = mf.GetType().GetMethod("OpenSettings",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                openM.Invoke(mf, null);
                Pump(); Pump(); Pump();

                var flyout = (ToolStripDropDown)Field(mf, "settingsFlyout");
                Console.Out.WriteLine("DLG flyout visible=" + (flyout != null && flyout.Visible)
                    + " size=" + (flyout == null ? Rectangle.Empty.Size : flyout.Size));

                strip("flyout");
                st("flyout");
                Console.Out.Flush();

                if (flyout != null && flyout.Visible) flyout.Close();
                Pump(); Pump();
                strip("after");
                st("after");

                mf.Close();
                Environment.Exit(0);
            }

            // 自绘标题栏: 几何 / WM_NCHITTEST 命中码 / 最大化-还原 / 窄窗标题省略
            if (args.Length >= 1 && args[0] == "titlebar")
            {
                var sf = new MainForm();
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new Point(40, 40);
                sf.Size = new Size(700, 800);
                sf.TopMost = true;
                sf.Show();
                sf.Activate();
                Pump();

                var min = Field(sf, "btnMin") as Control;
                var gear = Field(sf, "btnSettings") as Control;
                var max = Field(sf, "btnMax") as Control;
                var close = Field(sf, "btnClose") as Control;
                if (min == null || gear == null || max == null || close == null)
                { Console.Out.WriteLine("TB no controls"); sf.Close(); return; }

                Action<string> dump = tag =>
                    Console.Out.WriteLine("TB " + tag
                        + " win=" + sf.Size + " client=" + sf.ClientSize + " captionH=" + sf.Padding.Top
                        + " gear=" + gear.Bounds + " min=" + min.Bounds
                        + " max=" + max.Bounds + " close=" + close.Bounds
                        + " glyph=" + Field(max, "glyph"));

                Action<string> shot = path =>
                {
                    for (int i = 0; i < 3; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    using (var bmp = new Bitmap(sf.Width, sf.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(sf.Location, Point.Empty, sf.Size);
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                };

                dump("geom");

                // 样式位: WS_THICKFRAME (0x00040000) 在 = 系统仍认为这窗口有可缩放边框,
                // Snap / Win+方向键 / 系统阴影才有依据; 它不在就只能自己补一套热区。
                int style = GetWindowLong(sf.Handle, -16);
                Console.Out.WriteLine("TB style=0x" + style.ToString("X8")
                    + " thickframe=" + ((style & 0x00040000) != 0 ? "yes" : "no")
                    + " caption=" + ((style & 0x00C00000) == 0x00C00000 ? "yes" : "no"));

                // 命中码: 拖动窗口 / 边缘缩放 / 双击最大化全靠它, 直接把 WM_NCHITTEST 发给窗口看回什么
                Func<int, int, string> hit = (x, y) =>
                {
                    Point scr = sf.PointToScreen(new Point(x, y));
                    IntPtr lp = (IntPtr)((scr.Y << 16) | (scr.X & 0xFFFF));
                    long r = SendMessage(sf.Handle, 0x0084, IntPtr.Zero, lp).ToInt64();
                    switch (r)
                    {
                        case 1: return "CLIENT";
                        case 2: return "CAPTION";
                        case 10: return "LEFT";
                        case 11: return "RIGHT";
                        case 12: return "TOP";
                        case 13: return "TOPLEFT";
                        case 14: return "TOPRIGHT";
                        case 15: return "BOTTOM";
                        case 16: return "BOTTOMLEFT";
                        case 17: return "BOTTOMRIGHT";
                        default: return r.ToString();
                    }
                };
                Console.Out.WriteLine("TB hit edge-tl=" + hit(3, 3)
                    + " edge-top=" + hit(sf.ClientSize.Width / 2, 2)
                    + " edge-right=" + hit(sf.ClientSize.Width - 3, 200)
                    + " edge-bottom=" + hit(400, sf.ClientSize.Height - 3)
                    + " caption-blank=" + hit(240, 12)
                    + " gear=" + hit(gear.Left + gear.Width / 2, gear.Top + gear.Height / 2)
                    + " close=" + hit(close.Left + close.Width / 2, close.Top + close.Height / 2)
                    + " body=" + hit(400, 400));

                shot("title-normal.png");

                MethodInfo click = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);

                // 双击标题栏空白: 系统应把窗口最大化 (HTCAPTION 上的默认行为)
                Point cap = sf.PointToScreen(new Point(240, 12));
                SendMessage(sf.Handle, 0x00A3, (IntPtr)2,
                    (IntPtr)((cap.Y << 16) | (cap.X & 0xFFFF)));   // WM_NCLBUTTONDBLCLK / HTCAPTION
                Pump(); Pump();
                dump("dblclick state=" + sf.WindowState);
                if (sf.WindowState == FormWindowState.Maximized)
                {
                    click.Invoke(max, new object[] { EventArgs.Empty });
                    Pump(); Pump();
                    dump("restore1");   // 第一次还原: 与初始 875x1000 比, 看多出多少
                }

                // 最大化键: 状态 / 字形 / 最大化边界 (钉工作区, 不能盖住任务栏)
                click.Invoke(max, new object[] { EventArgs.Empty });
                Pump(); Pump();
                // MaximizedBounds 是 Form 的 protected 属性, 探针只能反射读
                Func<Form, Rectangle> maxBounds = fm =>
                {
                    var pi = typeof(Form).GetProperty("MaximizedBounds",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    return pi == null ? Rectangle.Empty : (Rectangle)pi.GetValue(fm, null);
                };

                dump("maximized state=" + sf.WindowState + " maxBounds=" + maxBounds(sf)
                    + " closeGapRight=" + (sf.ClientSize.Width - close.Right));
                shot("title-max.png");

                click.Invoke(max, new object[] { EventArgs.Empty });
                Pump(); Pump();
                dump("restored state=" + sf.WindowState);

                // 窄窗口: 标题要让位给按钮 (EndEllipsis), 不能钻到齿轮底下
                sf.Width = 470;
                Pump();
                dump("narrow");
                shot("title-narrow.png");

                sf.Close();
                return;
            }

            // 跨卡片列对齐: 同一张卡 (宽度不变) 在单声道/多声道两种形态下,
            // 设备列/声道列/延迟壳列必须落在同一 x —— 折叠键挂在「×」下方、不占横向链,
            // 所以"有没有折叠键"不该让这三列移动一个像素。
            if (args.Length >= 1 && args[0] == "cols")
            {
                var sf = new MainForm();
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new Point(40, 40);
                sf.Size = new Size(700, 800);
                sf.TopMost = true;
                sf.Show();
                sf.Activate();
                Pump();

                var host = Field(sf, "cardHost") as Control;
                OutputCardControl card = null;
                if (host != null)
                    foreach (Control c in host.Controls)
                    {
                        var oc = c as OutputCardControl;
                        if (oc != null) { card = oc; break; }
                    }
                if (card == null) { Console.Out.WriteLine("COLS no card"); sf.Close(); return; }

                var chans = new List<KeyValuePair<string, string>>();
                chans.Add(new KeyValuePair<string, string>("FL", "FL"));
                chans.Add(new KeyValuePair<string, string>("FR", "FR"));

                Action<string> m = tag =>
                {
                    Console.Out.WriteLine("COLS " + tag
                        + " w=" + card.Width + " h=" + card.Height
                        + " dev=" + card.cmbDevice.Left + ".." + card.cmbDevice.Right
                        + " ch=" + card.cmbChannel.Left + ".." + card.cmbChannel.Right
                        + " shell=" + card.nudShell.Left + ".." + card.nudShell.Right
                        + " tgl=" + card.btnRowsToggle.Left + ".." + card.btnRowsToggle.Right
                        + "y" + card.btnRowsToggle.Top + ".." + card.btnRowsToggle.Bottom
                        + (card.btnRowsToggle.Visible ? "" : "(hidden)")
                        + " x=" + card.btnRemove.Left + ".." + card.btnRemove.Right
                        + "y" + card.btnRemove.Top + ".." + card.btnRemove.Bottom);
                };

                Action<string> shot = path =>
                {
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }
                    using (var bmp = new Bitmap(sf.Width, sf.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(sf.Location, Point.Empty, sf.Size);
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                };

                card.SetMulti(false, 0, chans);
                Pump();
                m("single");
                shot("cols-single.png");

                // 收起状态的键按卡片模型的 Multi 判定 (RowsCollapsed 只对多声道卡成立),
                // 所以造多声道形态时模型也要一起改, 否则点折叠键是空操作
                card.Model.Multi = true;
                card.SetMulti(true, 2, chans);
                Pump();
                // 默认折叠 (用户要求): 多声道卡一建出来就是收起态, 高度按单声道算
                m("multi-default rowsVisible=" + (card.RowsVisible ? "yes" : "no")
                    + " glyph=" + card.btnRowsToggle.Glyph);
                shot("cols-multi-default.png");

                // 点一下展开: 行摊开 + 卡高按行数算; 三列对齐要在展开态比 (这时折叠键才可见)
                MethodInfo click = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
                click.Invoke(card.btnRowsToggle, new object[] { EventArgs.Empty });
                Pump();
                m("expanded rowsVisible=" + (card.RowsVisible ? "yes" : "no")
                    + " glyph=" + card.btnRowsToggle.Glyph);
                shot("cols-expanded.png");

                // 同宽度下三列左缘逐列相等才算对齐: 直接给结论, 不靠人眼比数字
                m("multi-fit=" + (card.nudShell.Right == card.btnRemove.Left - Dpi.S(5) ? "ok" : "BAD"));

                // 再点: 收回默认态; 再点一次必须又能展开 (二值, 不能卡在中间)
                click.Invoke(card.btnRowsToggle, new object[] { EventArgs.Empty });
                Pump();
                m("collapsed rowsVisible=" + (card.RowsVisible ? "yes" : "no")
                    + " glyph=" + card.btnRowsToggle.Glyph);

                click.Invoke(card.btnRowsToggle, new object[] { EventArgs.Empty });
                Pump();
                m("expanded2 rowsVisible=" + (card.RowsVisible ? "yes" : "no")
                    + " glyph=" + card.btnRowsToggle.Glyph);

                card.SetMulti(false, 0, chans);
                Pump();
                sf.Close();
                return;
            }

            // 多声道行的收起/展开: 点一下折叠键, 打印两态几何 + 各存一张实屏图。
            // 状态是"看的方式", 没有配置可以断言 —— 只能看行是不是真的藏了、卡片高度有没有跟着收。
            if (args.Length >= 1 && args[0] == "toggle")
            {
                var sf = new MainForm();
                sf.StartPosition = FormStartPosition.Manual;
                sf.Location = new Point(40, 40);
                sf.Size = new Size(700, 800);
                sf.TopMost = true;
                sf.Show();
                sf.Activate();
                Pump();

                var host = Field(sf, "cardHost") as Control;
                OutputCardControl card = null;
                if (host != null)
                    foreach (Control c in host.Controls)
                    {
                        var oc = c as OutputCardControl;
                        if (oc != null) { card = oc; break; }
                    }
                if (card == null) { Console.Out.WriteLine("TOGGLE no card"); sf.Close(); return; }

                Action<string> dump = tag =>
                {
                    Console.Out.WriteLine("TOGGLE " + tag
                        + " rowsVisible=" + (card.ChildRowCount == 0 ? "n/a" : (card.RowsVisible ? "yes" : "no"))
                        + " cardH=" + card.Height
                        + " shell=" + card.nudShell.Bounds
                        + " ch=" + card.cmbChannel.Bounds
                        + " dev=" + card.cmbDevice.Bounds
                        + " tgl=" + card.btnRowsToggle.Bounds
                        + " x=" + card.btnRemove.Bounds
                        + " accessible='" + card.btnRowsToggle.AccessibleName + "'/'"
                        + card.btnRowsToggle.AccessibleDescription + "'");
                };

                Action<string> shot = path =>
                {
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }
                    using (var bmp = new Bitmap(sf.Width, sf.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(sf.Location, Point.Empty, sf.Size);
                        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                    }
                };

                dump("expanded");

                // 跨卡片列对齐: 「×」在每张卡自己的右上角, 于是它的左侧一个缝就是横向链的右端 ——
                // 单声道卡没有折叠键, 但延迟壳/声道/设备这三列必须和多声道卡落在同一 x。
                // 卡宽一致时这些左缘应当逐列相等; 打印出来直接比, 不用靠眼睛看截图。
                if (host != null)
                {
                    int ci = 0;
                    foreach (Control c in host.Controls)
                    {
                        var oc = c as OutputCardControl;
                        if (oc == null) continue;
                        Console.Out.WriteLine("COLS #" + ci++ + " kind=" + (oc.ChildRowCount > 0 ? "multi" : "single")
                            + " w=" + oc.Width
                            + " dev=" + oc.cmbDevice.Left + ".." + oc.cmbDevice.Right
                            + " ch=" + oc.cmbChannel.Left + ".." + oc.cmbChannel.Right
                            + " shell=" + oc.nudShell.Left + ".." + oc.nudShell.Right
                            + " tgl=" + oc.btnRowsToggle.Left + ".." + oc.btnRowsToggle.Right
                            + (oc.btnRowsToggle.Visible ? "" : "(hidden)")
                            + " x=" + oc.btnRemove.Left + ".." + oc.btnRemove.Right);
                    }
                }

                shot("toggle-expanded.png");

                MethodInfo click = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
                click.Invoke(card.btnRowsToggle, new object[] { EventArgs.Empty });
                Pump();
                dump("collapsed");
                shot("toggle-collapsed.png");

                // 收起状态在整批重建后必须还在 (state 存在静态集合里, 靠 SetMulti 重新落到控件上)
                MethodInfo rb = typeof(MainForm).GetMethod("RebuildCards", BindingFlags.Instance | BindingFlags.NonPublic);
                if (rb != null)
                {
                    rb.Invoke(sf, null);
                    Pump();
                    OutputCardControl again = null;
                    if (host != null)
                        foreach (Control c in host.Controls)
                        {
                            var oc = c as OutputCardControl;
                            if (oc != null) { again = oc; break; }
                        }
                    if (again != null)
                    {
                        card = again;
                        dump("after-rebuild");
                    }
                }


                // 重建后旧控件已被 Dispose, 下面的点击必须落在新卡片上 (card 已在上面换成 again)

                // 再点一次: 必须回到原样 (状态是二值的, 不能点两次卡在中间)
                click.Invoke(card.btnRowsToggle, new object[] { EventArgs.Empty });
                Pump();
                dump("expanded2");

                // 键盘焦点: Tab 进来要能看到焦点框 (AccessibleRole=PushButton 也要求有可见焦点)
                card.btnRowsToggle.Focus();
                Pump();
                Console.Out.WriteLine("TOGGLE focus focused=" + card.btnRowsToggle.Focused
                    + " role=" + card.btnRowsToggle.AccessibleRole);
                shot("toggle-focus.png");
                sf.Close();
                return;
            }

            // 滚动复现: 卡片列表滚动后延迟框出现"重影"(文字/箭头被画两次)。
            // 用 BufferedFlowPanel + 8 张真卡片模拟, 逐步滚动并逐张截图, 看是哪一步开始糊。
            if (args.Length >= 1 && args[0] == "scroll")
            {
                string path = args.Length >= 2 ? args[1] : "scroll.png";
                var devs = new List<string>
                {
                    "[0] 扬声器 (Realtek(R) Audio)", "[1] 网易虚拟音频设备", "[2] OF24Q (NVIDIA High Definition Audio)"
                };
                var chans = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("FL", "左声道"),
                    new KeyValuePair<string, string>("FR", "右声道")
                };
                using (var sform = new Form
                {
                    StartPosition = FormStartPosition.Manual, Location = new Point(40, 40),
                    ClientSize = new Size(560, 150), BackColor = Color.White,
                    Font = new Font("Microsoft YaHei UI", 9F)
                })
                {
                    var host = new BufferedFlowPanel
                    {
                        Dock = DockStyle.Fill, AutoScroll = true, WrapContents = true,
                        FlowDirection = FlowDirection.LeftToRight,
                        Padding = new Padding(0, 4, 0, 6), BackColor = Color.White
                    };
                    sform.Controls.Add(host);
                    for (int i = 0; i < 8; i++)
                    {
                        var m = new OutputCardModel { DeviceId = "dev" + i, DelayMs = 1950, Volume = 100 };
                        var card = new OutputCardControl(m) { Margin = new Padding(0, 0, 0, OutputCardControl.CardGap) };
                        card.SetDevices(devs);
                        card.SetDeviceIndex(i % 3);
                        card.SetChannels(chans);
                        card.SyncFromModel(chans);
                        host.Controls.Add(card);
                    }
                    sform.TopMost = true;
                    sform.Show();
                    sform.Activate();
                    for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                    Console.Out.WriteLine("SCROLL host=" + host.ClientSize + " content=" + host.DisplayRectangle
                        + " vscroll=" + host.VerticalScroll.Visible);
                    // 把光标停在第一张卡片的延迟框上再滚: 滚轮扫过控件会触发 hover 重绘,
                    // 用户报的重影就是在"鼠标停在延迟框上滚列表"时出现的
                    var firstCard = host.Controls[0] as OutputCardControl;
                    if (firstCard != null)
                    {
                        Point sp = firstCard.nudShell.PointToScreen(new Point(firstCard.nudShell.Width / 2, firstCard.nudShell.Height / 2));
                        Cursor.Position = sp;
                        Console.Out.WriteLine("CURSOR at shell " + sp);
                        for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                    }
                    // 走真实滚轮路径: WM_VSCROLL/SB_LINEDOWN (与 AutoScroll 的滚轮同一条路),
                    // 每滚一格只泵一次消息就截图 —— 等太久的话重影早被重绘盖掉了
                    for (int i = 0; i < 10; i++)
                    {
                        SendMessage(host.Handle, 0x0115 /*WM_VSCROLL*/, (IntPtr)1 /*SB_LINEDOWN*/, IntPtr.Zero);
                        Application.DoEvents();
                        Application.DoEvents();
                        string file = System.IO.Path.GetFileNameWithoutExtension(path) + "-" + i + ".png";
                        using (var bmp = new Bitmap(sform.Width, sform.Height))
                        {
                            using (var g = Graphics.FromImage(bmp))
                                g.CopyFromScreen(sform.Location, Point.Empty, sform.Size);
                            bmp.Save(file, System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.Out.WriteLine("  step=" + i + " pos=" + host.AutoScrollPosition + " -> " + file);
                    }
                }
                return;
            }

            // 真实主窗体的滚动复现: 用用户自己的 ini (多张多声道卡片) 让卡片区出滚动条,
            // 光标停在延迟框上, 逐格 WM_VSCROLL 并截图 —— 复现"滚动后延迟框重影"
            if (args.Length >= 1 && args[0] == "realscroll")
            {
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(40, 40);
                mf.Size = new Size(640, 430);
                mf.TopMost = true;
                mf.Show();
                mf.Activate();
                for (int i = 0; i < 14; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                var host = Field(mf, "cardHost") as Control;
                Console.Out.WriteLine("HOST " + (host == null ? "null" : host.Bounds + " vscroll=" + ((ScrollableControl)host).VerticalScroll.Visible
                    + " cards=" + host.Controls.Count));
                if (host != null)
                {
                    // 光标压在第一张卡片的延迟框上 (hover 重绘 + 滚轮派发都在这条路上)
                    OutputCardControl card0 = null;
                    foreach (Control c in host.Controls) { card0 = c as OutputCardControl; if (card0 != null) break; }
                    if (card0 != null)
                    {
                        Point sp = card0.nudShell.PointToScreen(new Point(card0.nudShell.Width / 2, card0.nudShell.Height / 2));
                        Cursor.Position = sp;
                        Console.Out.WriteLine("SHELL screen=" + sp);
                        for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                    }
                    // 真滚轮: 一次 -120 = 3 行 (~48px), 与用户划一下滚轮一致; 下到底再往回滚
                    for (int i = 0; i < 16; i++)
                    {
                        int delta = i < 8 ? -120 : 120;
                        IntPtr lp = (IntPtr)((Math.Max(0, Cursor.Position.Y) << 16) | Math.Max(0, Cursor.Position.X));
                        SendMessage(host.Handle, 0x020A /*WM_MOUSEWHEEL*/,
                             (IntPtr)((delta & 0xFFFF) << 16), lp);
                        // 滚完立刻抓一帧 (不等重绘完成): 用户在滚动中按截图就是这一帧,
                        // 撕裂/残影如果存在, 这里能抓到; 合成渲染下应该是完整的一帧
                        using (var bmp = new Bitmap(mf.Width, mf.Height))
                        {
                            using (var g = Graphics.FromImage(bmp))
                                g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                            bmp.Save("im-" + i + ".png", System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Application.DoEvents();
                        Application.DoEvents();
                        using (var bmp = new Bitmap(mf.Width, mf.Height))
                        {
                            using (var g = Graphics.FromImage(bmp))
                                g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                            bmp.Save("rs-" + i + ".png", System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.Out.WriteLine("  step=" + i + " pos=" + ((ScrollableControl)host).AutoScrollPosition);
                    }
                }
                mf.Close();
                return;
            }

            // 边框采样: 壳高 24 (FixedHeight) 时数字框会不会把上下边框盖掉 ——
            // 直接读屏幕像素, 不看截图 (截图里鼠标光标会干扰)
            if (args.Length >= 1 && args[0] == "borderprobe")
            {
                using (var sform = new Form
                {
                    StartPosition = FormStartPosition.Manual, Location = new Point(60, 60),
                    ClientSize = new Size(340, 90), BackColor = Color.White,
                    Font = new Font("Microsoft YaHei UI", 9F)
                })
                {
                    var n1 = new NoWheelNumeric { Minimum = 0, Maximum = 2000, Increment = 10, Value = 50 };
                    var s1 = new RoundNumericShell(n1, "延迟", "ms") { FixedHeight = 24, Location = new Point(10, 10) };
                    var n2 = new NoWheelNumeric { Minimum = 0, Maximum = 2000, Increment = 10, Value = 50 };
                    var s2 = new RoundNumericShell(n2, "延迟", "ms") { Location = new Point(10, 50) };
                    sform.Controls.Add(s1);
                    sform.Controls.Add(s2);
                    sform.TopMost = true;
                    Cursor.Position = new Point(5, 5);
                    sform.Show();
                    sform.Activate();
                    for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                    Console.Out.WriteLine("s1=" + s1.Bounds + " fixed=24  s2=" + s2.Bounds + " auto");
                    DumpEdges(sform, s1, "FIXED24");
                    DumpEdges(sform, s2, "AUTO");
                }
                return;
            }

            // 蓝色块定位: 反复滚动卡片, 每滚一次扫一遍屏幕上的"强调蓝"像素;
            // 命中就抓图, 同时打印焦点控件链与数字框的选择范围
                                    if (args.Length >= 1 && args[0] == "blueprobe")
            {
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(40, 40);
                mf.Size = new Size(640, 620);
                mf.TopMost = true;
                mf.Show();
                mf.Activate();
                for (int i = 0; i < 16; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                var chost = Field(mf, "cardHost") as Control;
                foreach (Control c in chost.Controls)
                {
                    var oc0 = c as OutputCardControl;
                    if (oc0 == null) continue;
                    Cursor.Position = oc0.nudShell.PointToScreen(new Point(oc0.nudShell.Width / 2, oc0.nudShell.Height / 2));
                    break;
                }
                Application.DoEvents();

                for (int step = 0; step < 30; step++)
                {
                    int delta = step % 8 < 4 ? -120 : 120;
                    IntPtr lpx = (IntPtr)((Math.Max(0, Cursor.Position.Y) << 16) | Math.Max(0, Cursor.Position.X));
                    SendMessage(chost.Handle, 0x020A, (IntPtr)((delta & 0xFFFF) << 16), lpx);
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }

                    foreach (Control c in chost.Controls)
                    {
                        var oc = c as OutputCardControl;
                        if (oc == null || !oc.Visible) continue;
                        Rectangle r = new Rectangle(oc.nudShell.PointToScreen(Point.Empty), oc.nudShell.Size);
                        if (!new Rectangle(mf.Location, mf.Size).Contains(r)) continue;
                        using (var bmp = new Bitmap(r.Width, r.Height))
                        {
                            using (var g = Graphics.FromImage(bmp))
                                g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                            Point bp = Point.Empty; int bn = 0;
                            for (int by = 0; by < bmp.Height; by++)
                                for (int bx = 0; bx < bmp.Width; bx++)
                                {
                                    Color cc = bmp.GetPixel(bx, by);
                                    if (cc.R < 60 && cc.B > 190) { bn++; if (bp.IsEmpty) bp = new Point(bx, by); }
                                }
                            if (bn > 0)
                            {
                                bmp.Save("hit-" + step + ".png", System.Drawing.Imaging.ImageFormat.Png);
                                Point sp = new Point(r.Left + bp.X, r.Top + bp.Y);
                                Console.Out.WriteLine("step=" + step + " blue=" + bn + " atShell=" + bp
                                    + " screen=" + sp + " win=" + WindowInfo(WindowFromPoint(sp)));
                            }
                        }
                        break;
                    }
                }
                mf.Close();
                return;
            }

            // 抓一张完整可见卡片上的延迟壳 (放大看排版/边框)
            if (args.Length >= 1 && args[0] == "shellshot")
            {
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(40, 40);
                mf.Size = new Size(640, 620);
                mf.TopMost = true;
                mf.Show();
                mf.Activate();
                for (int i = 0; i < 16; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }
                Cursor.Position = new Point(mf.Left + 4, mf.Top + 4);
                for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                var chost = Field(mf, "cardHost") as Control;
                int idx = 0;
                foreach (Control c in chost.Controls)
                {
                    var oc = c as OutputCardControl;
                    if (oc == null) continue;
                    Rectangle r = new Rectangle(oc.nudShell.PointToScreen(Point.Empty), oc.nudShell.Size);
                    if (!new Rectangle(mf.Location, mf.Size).Contains(r)) continue;
                    using (var bmp = new Bitmap(r.Width, r.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                        bmp.Save("shot-" + idx + ".png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Console.Out.WriteLine("shot-" + idx + " " + r + " value=" + oc.nudDelay.Value
                        + " shellW=" + oc.nudShell.Width);
                    if (++idx >= 3) break;
                }
                mf.Close();
                return;
            }

            // 圆角锯齿检查: 按控件真实位置抓左上角, 打印像素灰度矩阵 + 存 16x 放大图
            if (args.Length >= 1 && args[0] == "corners")
            {
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(40, 40);
                mf.Size = new Size(560, 560);
                mf.TopMost = true;
                mf.Show();
                mf.Activate();
                for (int i = 0; i < 16; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }
                Cursor.Position = new Point(mf.Left + 2, mf.Top + 2);
                for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

                var items = new List<KeyValuePair<string, Control>>();
                var addBtn = Field(mf, "btnAddOutput") as Control;
                var clearBtn = Field(mf, "btnClearLog") as Control;
                var spec = Field(mf, "cmbSpec") as Control;
                var startBtn = Field(mf, "btnStart") as Control;
                var host = Field(mf, "cardHost") as Control;
                if (addBtn != null) items.Add(new KeyValuePair<string, Control>("btnAdd", addBtn));
                if (clearBtn != null) items.Add(new KeyValuePair<string, Control>("btnClear", clearBtn));
                if (startBtn != null) items.Add(new KeyValuePair<string, Control>("btnStart", startBtn));
                if (spec != null) items.Add(new KeyValuePair<string, Control>("combo", spec));
                if (host != null)
                {
                    foreach (Control c in host.Controls)
                    {
                        var oc = c as OutputCardControl;
                        if (oc == null || !oc.Visible) continue;
                        items.Add(new KeyValuePair<string, Control>("card", oc));
                        items.Add(new KeyValuePair<string, Control>("shell", oc.nudShell));
                        items.Add(new KeyValuePair<string, Control>("cardcombo", oc.Controls.Find("cmbChannel", true).Length > 0 ? oc.Controls.Find("cmbChannel", true)[0] : oc));
                        break;
                    }
                }

                foreach (var kv in items)
                {
                    Control c = kv.Value;
                    Rectangle r = c.RectangleToScreen(c.ClientRectangle);
                    if (r.Width < 12 || r.Height < 12) continue;
                    using (var bmp = new Bitmap(14, 14))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(r.Location, Point.Empty, new Size(14, 14));
                        bmp.Save("corn-" + kv.Key + ".png", System.Drawing.Imaging.ImageFormat.Png);
                        var csb = new StringBuilder();
                        for (int y = 0; y < 8; y++)
                        {
                            csb.Append("    y" + y + ": ");
                            for (int x = 0; x < 8; x++)
                            {
                                Color cc = bmp.GetPixel(x, y);
                                csb.Append(((cc.R + cc.G + cc.B) / 3).ToString("000") + " ");
                            }
                            csb.AppendLine();
                        }
                        Console.Out.WriteLine("CORNER " + kv.Key + " at " + r.Location + " size " + c.Size);
                        Console.Out.Write(csb.ToString());
                    }
                }
                mf.Close();
                return;
            }

            // 分隔条观感 + 拉到最底自动收起日志面板 + 标题栏日志按钮开合 (批72)
            // 批80: 日志圆角壳 + 自绘无边框滚动条 + 水位壳结构断言
            if (args.Length >= 1 && args[0] == "logshell")
            {
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(40, 40);
                mf.Size = new Size(760, 640);
                mf.TopMost = true;
                mf.Show();
                mf.Activate();
                for (int i = 0; i < 16; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                Action<string> shotL = tag =>
                {
                    using (var bmp = new Bitmap(mf.Width, mf.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                        bmp.Save("log-" + tag + ".png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Console.Out.WriteLine("LOG-SHOT -> log-" + tag + ".png");
                };

                // 1) 水位壳: caption「水位」+ unit「秒」在壳内; Panel2 不再有散标签
                var waterShell = Field(mf, "nudWaterShell") as Control;
                bool hasCap = false, hasUnit = false;
                if (waterShell != null)
                    foreach (Control c in waterShell.Controls)
                    {
                        if (c.Text == "水位") hasCap = true;
                        if (c.Text == "秒") hasUnit = true;
                    }
                var scc2 = Field(mf, "splitContainer") as SplitContainer;
                int looseLabels = 0;
                foreach (Control c in scc2.Panel2.Controls)
                    if (c.Text == "水位监控:" || c.Text == "间隔" || (c.Text == "秒" && c.Parent == scc2.Panel2))
                        looseLabels++;
                Console.Out.WriteLine("WATER shell=" + (waterShell != null) + " caption=" + hasCap
                    + " unit=" + hasUnit + " looseLabels=" + looseLabels + " (期望 False/True/True/0)"
                    + " shellW=" + (waterShell != null ? waterShell.Width.ToString() : "?")
                    + " numericW=" + (waterShell != null ? InnerNumericW(waterShell).ToString() : "?")
                    + " (数字框两位数宽, 期望明显小于旧值60)");

                // 2) 日志壳结构: Region 非空 (原生滚动条被窗口区域裁掉), 日志框挂在壳里, 边框 None
                var ls = Field(mf, "logShell") as Control;
                var lt = Field(mf, "txtLog") as TextBox;
                Console.Out.WriteLine("LOGSHELL shell=" + (ls != null)
                    + " region=" + (lt != null && lt.Region != null) + " (期望 True, 裁在文本框上)"
                    + " innerInShell=" + (ls != null && lt != null && lt.Parent == ls) + " (期望 True)"
                    + " border=" + (lt != null ? lt.BorderStyle.ToString() : "?") + " (期望 None)");

                // 2b) 批83: 标题行等高对齐 + 内容框左右下留白一致 (各 8 设计像素) + 标题字号加大
                var clearBtn2 = Field(mf, "btnClearLog") as Control;
                var title2 = Field(mf, "lblLogTitle") as Label;
                bool eqH = waterShell != null && clearBtn2 != null
                    && clearBtn2.Height == waterShell.Height && clearBtn2.Top == waterShell.Top;
                int mDesign = Dpi.S(8);
                int mgL = ls != null ? ls.Left : -1;
                int mgR = (ls != null && scc2 != null) ? scc2.Panel2.ClientSize.Width - ls.Right : -1;
                int mgB = (ls != null && scc2 != null) ? scc2.Panel2.ClientSize.Height - ls.Bottom : -1;
                bool mgOk = ls != null && Math.Abs(mgL - mDesign) <= 1 && Math.Abs(mgR - mDesign) <= 1
                    && Math.Abs(mgB - mDesign) <= 1;
                Console.Out.WriteLine("HEADER eqHeight=" + eqH + " (期望 True, 按钮=壳高且同顶)"
                    + " titleFont=" + (title2 != null ? title2.Font.Size.ToString("0.#") : "?") + "pt (期望>=11)"
                    + " titleLeftAlignedShell=" + (ls != null && title2 != null && Math.Abs(title2.Left - ls.Left) <= 1)
                    + " (期望 True)");
                Console.Out.WriteLine("MARGIN left=" + mgL + " right=" + mgR + " bottom=" + mgB
                    + " 设计值=" + mDesign + " ok=" + mgOk + " (期望 ok=True, 左右下一致)");
                shotL("empty");

                // 3) 填满日志让滚动条出现: 条带像素应有自绘滑块 (198,203,211), 没有原生滚动条特征块
                MethodInfo logM = mf.GetType().GetMethod("Log",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public, null,
                    new Type[] { typeof(string) }, null);
                for (int i = 0; i < 90; i++)
                    logM.Invoke(mf, new object[] { "[状态] 滚动条实拍填充行 " + i.ToString("000") + " —— 圆角壳自绘滑条断言" });
                for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

                var siF = VertScrollInfo(lt);
                Console.Out.WriteLine("SCROLL-FILLED " + (siF.HasValue
                    ? ("max=" + siF.Value.nMax + " page=" + siF.Value.nPage + " pos=" + siF.Value.nPos)
                    : "no-scroll"));
                {
                    int strip = Dpi.S(10);
                    Point sp = ls.PointToScreen(new Point(ls.Width - strip, 0));
                    int thumbPx = 0, sysPx = 0;
                    using (var bmp = new Bitmap(strip, ls.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(sp, Point.Empty, bmp.Size);
                        // 只扫壳内条带的中段 (避开上下圆角处的面板底色补角)
                        for (int y = Dpi.S(10); y < bmp.Height - Dpi.S(10); y++)
                            for (int x = 0; x < bmp.Width; x++)
                            {
                                var p = bmp.GetPixel(x, y);
                                bool thumb = (p.R == 198 && p.G == 203 && p.B == 211)   // 常态
                                          || (p.R == 172 && p.G == 178 && p.B == 188); // 悬停/拖拽加深
                                if (thumb) thumbPx++;
                                if (p.R == 240 && p.G == 240 && p.B == 240) sysPx++;
                            }
                    }
                    Console.Out.WriteLine("BAR thumbPx=" + thumbPx + " (期望>30) sysBarPx=" + sysPx
                        + " (期望0, 原生滚动条已被窗口区域裁掉)");
                }
                // 批81: 原生滚动条泄漏断言 —— 它画在 Inner 窗口矩形右缘之内 ([W-SB, W) 列),
                // Region 必须切在它左边才算裁掉。扫描裁掉区 (文本右缘 .. 壳右缘-2, 中段避开圆角),
                // 深色像素 (<100 灰, 原生滑块/箭头特征) 应为 0; 壳底色/描边都是浅色不误报。
                {
                    int pad = Dpi.S(10);
                    int textW = Math.Max(pad + 40, ls.Width - 2 - 2 * pad);
                    int textEnd = 1 + pad + Math.Min(lt.ClientSize.Width, textW);
                    Point sp2 = ls.PointToScreen(new Point(textEnd, 0));
                    int darkPx = 0;
                    using (var bmp = new Bitmap(ls.Width - 2 - textEnd, ls.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(sp2, Point.Empty, bmp.Size);
                        for (int y = Dpi.S(10); y < bmp.Height - Dpi.S(10); y++)
                            for (int x = 0; x < bmp.Width; x++)
                            {
                                var p = bmp.GetPixel(x, y);
                                if (p.R < 100 && p.G < 100 && p.B < 100) darkPx++;
                            }
                    }
                    int padL2 = lt.Bounds.X - 1;                                   // 左留白 (边框内缘→正文)
                    int padR2 = (ls.Width - 2) - (lt.Bounds.X + lt.ClientSize.Width); // 右留白
                    Console.Out.WriteLine("LEAK textEnd=" + textEnd + " darkPx=" + darkPx
                        + " (期望0 —— 裁掉区内无原生滑块/箭头残迹)");
                    Console.Out.WriteLine("PAD left=" + padL2 + " right=" + padR2 + " (期望相等, ±1)"
                        + " innerX=" + lt.Bounds.X);
                    Console.Out.WriteLine("GEO shell=" + ls.Bounds.ToString()
                        + " inner=" + lt.Bounds.ToString() + " clientW=" + lt.ClientSize.Width
                        + " sbMetric=" + GetSystemMetrics(2)   // SM_CXVSCROLL
                        + " factor=" + Dpi.Factor.ToString("0.###"));
                }
                shotL("filled");

                // 4) 真实鼠标拖拽自绘滑块: 先滚回顶部 (滑块在顶), 按住往下拖 → nPos 必须跟着走。
                //    偶发一击不中时重试一次, 诊断计数区分"没按上滑块"和"按上了但没滚"。
                int dragPos = -1;
                for (int attempt = 0; attempt < 2 && dragPos <= 0; attempt++)
                {
                    if (attempt > 0) Console.Out.WriteLine("DRAG-RETRY attempt=" + attempt);
                    SendMessage(lt.Handle, 0x115, (IntPtr)0x0006, IntPtr.Zero);   // WM_VSCROLL SB_TOP
                    for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    var si0 = VertScrollInfo(lt);
                    int ty0, th0;
                    ThumbGeom(ls, si0.Value, out ty0, out th0);
                    Point thumbSp = ls.PointToScreen(new Point(ls.Width - Dpi.S(6), ty0 + th0 / 2));
                    Cursor.Position = thumbSp;
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);                   // LEFTDOWN
                    for (int i = 0; i < 3; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    for (int k = 1; k <= 10; k++)
                    {
                        Cursor.Position = new Point(thumbSp.X, thumbSp.Y + k * 6);
                        Application.DoEvents();
                        System.Threading.Thread.Sleep(30);
                        Application.DoEvents();
                    }
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);                   // LEFTUP
                    for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    var si1 = VertScrollInfo(lt);
                    dragPos = si1.HasValue ? si1.Value.nPos : 0;
                    Console.Out.WriteLine("DRAG-BAR pos " + (si0.HasValue ? si0.Value.nPos.ToString() : "?")
                        + " -> " + dragPos + " (期望显著增大) starts=" + RefInt(ls, "DragStarts")
                        + " moves=" + RefInt(ls, "DragMoves") + " (诊断: 按上滑块次数/拖拽发消息次数)");
                    shotL("dragged");
                }

                mf.Close();
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                Console.Out.WriteLine("LOGSHELL-DONE");
                return;
            }

            if (args.Length >= 1 && args[0] == "seam")
            {
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(40, 40);
                mf.Size = new Size(760, 640);
                mf.TopMost = true;
                mf.Show();
                mf.Activate();
                for (int i = 0; i < 16; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                var scc = Field(mf, "splitContainer") as SplitContainer;
                var btnLog = Field(mf, "btnLog") as Control;
                MethodInfo onClick = typeof(Control).GetMethod("OnClick",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Func<string> glyphOf = () =>
                {
                    if (btnLog == null) return "?";
                    object g = btnLog.GetType().GetField("glyph",
                        BindingFlags.Instance | BindingFlags.NonPublic).GetValue(btnLog);
                    return g == null ? "?" : g.ToString();
                };
                Action<string> shot = tag =>
                {
                    using (var bmp = new Bitmap(mf.Width, mf.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                        bmp.Save("seam-" + tag + ".png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Console.Out.WriteLine("SEAM-SHOT -> seam-" + tag + ".png");
                };

                Console.Out.WriteLine("SPLIT factor=" + StereoSplitter.Dpi.Factor.ToString("0.##")
                    + " W=" + scc.SplitterWidth + " back=" + scc.BackColor
                    + " dist=" + scc.SplitterDistance + " collapsed=" + scc.Panel2Collapsed
                    + " glyph=" + glyphOf());
                // 批78 防闪: 容器双缓冲 + 面板双缓冲 (反射读)
                var miGetStyle = typeof(Control).GetMethod("GetStyle",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                bool sccDb = (bool)miGetStyle.Invoke(scc, new object[] { ControlStyles.OptimizedDoubleBuffer });
                var piDb = typeof(Control).GetProperty("DoubleBuffered",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                bool p1Db = (bool)piDb.GetValue(scc.Panel1, null);
                bool p2Db = (bool)piDb.GetValue(scc.Panel2, null);
                Console.Out.WriteLine("ANTIFLICKER container=" + sccDb + " (期望 True) panel1=" + p1Db
                    + " panel2=" + p2Db + " (期望 True/True)");
                shot("normal");

                // 按钮收起 (批84: 窗口跟着缩小到分隔条位置, 图标 = 点击后的动作 → LogOn)
                int winH0 = mf.ClientSize.Height, p2h0 = scc.Panel2.Height, sw0 = scc.SplitterWidth;
                onClick.Invoke(btnLog, new object[] { EventArgs.Empty });
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Console.Out.WriteLine("LOG-BTN-COLLAPSED collapsed=" + scc.Panel2Collapsed + " (期望 True) glyph=" + glyphOf() + " (期望 LogOn)"
                    + " winH " + winH0 + "->" + mf.ClientSize.Height + " (期望收掉 p2h+sw=" + (p2h0 + sw0) + ")");
                shot("btn-collapsed");

                // 按钮恢复 (批84: 窗口长回原大小, 图标 = 点击后的动作 → LogOff)
                onClick.Invoke(btnLog, new object[] { EventArgs.Empty });
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Console.Out.WriteLine("LOG-BTN-RESTORED collapsed=" + scc.Panel2Collapsed + " (期望 False) dist=" + scc.SplitterDistance
                    + " glyph=" + glyphOf() + " (期望 LogOff)"
                    + " winH->" + mf.ClientSize.Height + " (期望还原 " + winH0 + ")");
                shot("btn-restored");

                // 真实鼠标小步拖动 (复现用户"小幅度拖动显示异常"): 快速拖 + 分段截图
                Point dragSp = scc.PointToScreen(new Point(scc.Width / 2, scc.Panel1.Bottom + scc.SplitterWidth / 2));
                Cursor.Position = dragSp;
                for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);              // LEFTDOWN
                for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                // 步进拖动 (每步 30ms: SetCursorPos 连发会被系统合并 WM_MOUSEMOVE,
                // 不等的话只泵到个别步 —— 实时拖动要看的是逐步落位, 必须逐步泵)
                int dist0 = scc.SplitterDistance;
                for (int k = 1; k <= 8; k++)
                {
                    Cursor.Position = new Point(dragSp.X, dragSp.Y + k);
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(30);
                    Application.DoEvents();
                    if (k == 3)
                    {
                        shot("drag-mid");
                        // 批77: 实时拖动 —— 拖动中途距离就该已经落位 (原生 XOR 预览时代是原值不动)
                        Console.Out.WriteLine("DRAG-MID-LIVE dist=" + scc.SplitterDistance
                            + " (期望=" + (dist0 + 3) + ", 实时落位生效) panel1H=" + scc.Panel1.Height
                            + " composited=" + ((NoFocusSplitContainer)scc).SubtreeComposited + " (期望 True, 批78 防闪)");
                    }
                }
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);              // LEFTUP
                Application.DoEvents();
                shot("drag-done0");
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                shot("drag-done");
                Console.Out.WriteLine("DRAG-SMALL dist=" + scc.SplitterDistance + " panel1H=" + scc.Panel1.Height + " (期望=" + (dist0 + 8) + ")");
                // 反向再拖 3px (上): 抓点按**当前**滑带中心重算 (上面的拖动已把滑带挪了 8px)
                Point dragSp2 = scc.PointToScreen(new Point(scc.Width / 2, scc.Panel1.Bottom + scc.SplitterWidth / 2));
                Cursor.Position = dragSp2;
                for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                for (int k = 1; k <= 3; k++)
                {
                    Cursor.Position = new Point(dragSp2.X, dragSp2.Y - k);
                    Application.DoEvents();
                    System.Threading.Thread.Sleep(30);
                    Application.DoEvents();
                }
                mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                Application.DoEvents();
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                shot("drag-back");
                Console.Out.WriteLine("DRAG-BACK dist=" + scc.SplitterDistance + " (期望=" + (dist0 + 5) + ")");
                Console.Out.WriteLine("DRAG-END-COUNT=" + ((NoFocusSplitContainer)scc).DragEndCount
                    + " LIVE-MOVES=" + ((NoFocusSplitContainer)scc).LiveDragMoves + " (期望>0)"
                    + " composited-off=" + !((NoFocusSplitContainer)scc).SubtreeComposited + " (期望 True, 松手即撤)");
                // 整窗强制重画: 若能治愈 230 花纹 → 陈旧像素问题 (增量裁剪漏行), 不是几何问题
                mf.Invalidate(true);
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                shot("healed");
                Console.Out.WriteLine("HEALED after full invalidate");

                // 批79: 拖动中逐帧抓拍 —— 滑带中段 (容器 HWND 画) 与两端喇叭口 (窗体 HWND 画)
                // 分属两个窗口、各自双缓冲上屏; 快速拖动时若两窗的 WM_PAINT 落在不同帧,
                // 圆角就会相对滑带抖动。逐帧对比两者位置, 输出偏差。
                Action<string, int> jitRun = (jtag, sleepMs) =>
                {
                    Point gsp = scc.PointToScreen(new Point(scc.Width / 2, scc.Panel1.Bottom + scc.SplitterWidth / 2));
                    Cursor.Position = gsp;
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    mouse_event(0x0002, 0, 0, 0, UIntPtr.Zero);
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    int jmin = int.MaxValue, jmax = int.MinValue, jminBot = int.MaxValue, jmaxBot = int.MinValue;
                    for (int k = 1; k <= 16; k++)
                    {
                        Cursor.Position = new Point(gsp.X, gsp.Y - k);
                        Application.DoEvents();
                        if (sleepMs > 0) System.Threading.Thread.Sleep(sleepMs);
                        Application.DoEvents();
                        using (var bmp = new Bitmap(mf.Width, mf.Height))
                        {
                            using (var g = Graphics.FromImage(bmp))
                                g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                            int xc = scc.PointToScreen(new Point(Math.Max(10, (int)(scc.Width * 0.15f)), 0)).X - mf.Left;
                            int yc = scc.Top + scc.SplitterDistance;   // 滑带上缘 (帧坐标)
                            Func<int, int, int, int[]> darkRows = (x, yFrom, yTo) =>
                            {
                                var ys = new List<int>();
                                for (int y = yFrom; y <= yTo; y++)
                                {
                                    var p = bmp.GetPixel(x, y);
                                    // 批95: 缝线色改为窗口边框色 (203,208,216), 不再是深色 ——
                                    // 判定换成缝线色 ±16: 弧缘 AA 渐变行要稳定入窗 (±12 会把
                                    // 渐变行时而算入时而排除, 出 ±1 假抖动); 背景 240 距缝色
                                    // 37, 不会误入
                                    if (Math.Abs(p.R - 203) <= 16 && Math.Abs(p.G - 208) <= 16
                                        && Math.Abs(p.B - 216) <= 16) ys.Add(y);
                                }
                                return ys.ToArray();
                            };
                            // 中段: 只扫滑带附近, 避开上方卡片文字与下方日志标题
                            int[] mid = darkRows(xc, yc - 6, yc + 14);
                            // 左喇叭口列 (x=4, 让边内): 弧伸到带外 ±r
                            int[] arc = darkRows(4, yc - 14, yc + 20);
                            int bandTop = mid.Length > 0 ? mid[0] : -1;
                            int bandBot = mid.Length > 0 ? mid[mid.Length - 1] : -1;
                            int arcTop = arc.Length > 0 ? arc[0] : -1;
                            int arcBot = arc.Length > 0 ? arc[arc.Length - 1] : -1;
                            int dTop = (bandTop >= 0 && arcTop >= 0) ? arcTop - bandTop : -99;
                            int dBot = (bandBot >= 0 && arcBot >= 0) ? arcBot - bandBot : -99;
                            if (dTop < jmin) jmin = dTop;
                            if (dTop > jmax) jmax = dTop;
                            if (dBot < jminBot) jminBot = dBot;
                            if (dBot > jmaxBot) jmaxBot = dBot;
                            Console.Out.WriteLine(jtag + " k=" + k.ToString("00") + " band=" + bandTop + ".." + bandBot
                                + " arc=" + arcTop + ".." + arcBot + " dTop=" + dTop + " dBot=" + dBot);
                        }
                    }
                    mouse_event(0x0004, 0, 0, 0, UIntPtr.Zero);
                    Application.DoEvents();
                    Console.Out.WriteLine(jtag + "-RANGE dTop=" + jmin + ".." + jmax + " dBot=" + jminBot + ".." + jmaxBot
                        + " (期望恒定; 批95 缝线换浅色后弧缘 AA 渐变行随亚像素位置翻转, ±1 = 检测噪声放行;\n"
                        + "  超过 ±1 或 -99 才算圆角与滑带不同步)");
                    if (jmax - jmin > 1 || jmaxBot - jminBot > 1 || jmin == -99)
                    {
                        using (var bmp = new Bitmap(mf.Width, mf.Height))
                        {
                            using (var g = Graphics.FromImage(bmp))
                                g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                            bmp.Save("seam-jitter-" + jtag + ".png", System.Drawing.Imaging.ImageFormat.Png);
                        }
                        Console.Out.WriteLine("JIT-SHOT -> seam-jitter-" + jtag + ".png");
                    }
                };
                jitRun("JIT", 25);          // 慢速: 每步逐泵
                jitRun("JIT-FAST", 0);      // 快速压力: 不等待, 消息合并成大步长

                // 拉到最底 = SplitterDistance 拉满 → SplitterMoved 应自动整体收起日志面板
                try { scc.SplitterDistance = scc.Height - scc.SplitterWidth; } catch (Exception ex) { Console.Out.WriteLine("DRAG-BOTTOM set failed: " + ex.Message); }
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Console.Out.WriteLine("DRAG-BOTTOM collapsed=" + scc.Panel2Collapsed + " (期望 True)"
                    + " panel2Visible=" + scc.Panel2.Visible
                    + " glyph=" + glyphOf() + " (期望 LogOn, 图标=动作)"
                    + " winH=" + mf.ClientSize.Height + " (已收掉缝隙 sw)");
                shot("collapsed");

                // 点日志按钮 = 恢复 (窗口按收起前定格的日志高度长回日志区)
                int winHCollapsed = mf.ClientSize.Height;
                Func<string> stOf = () => "collapsed=" + scc.Panel2Collapsed + " dist=" + scc.SplitterDistance
                    + " panel2H=" + scc.Panel2.Height + " p2vis=" + scc.Panel2.Visible
                    + " p2min=" + scc.Panel2MinSize + " sccH=" + scc.Height
                    + " lastLogDist=" + (int)Field(mf, "lastLogDist")
                    + " restoreH=" + (int)Field(mf, "_logRestoreH")
                    + " lastP2H=" + (int)Field(mf, "_lastP2H");
                Console.Out.WriteLine("LOG-CLICK before: " + stOf());
                onClick.Invoke(btnLog, new object[] { EventArgs.Empty });
                Console.Out.WriteLine("LOG-CLICK sync-after-click: " + stOf());
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); if (i < 3) Console.Out.WriteLine("LOG-CLICK pump" + i + ": " + stOf()); }
                Console.Out.WriteLine("LOG-CLICK restored collapsed=" + scc.Panel2Collapsed + " (期望 False)"
                    + " dist=" + scc.SplitterDistance + " panel2H=" + scc.Panel2.Height
                    + " glyph=" + glyphOf() + " (期望 LogOff)"
                    + " winH " + winHCollapsed + "->" + mf.ClientSize.Height + " (期望长回日志区, >收起时)");
                shot("restored");

                // 再点两次 = 收起 / 恢复 (幂等开关)
                onClick.Invoke(btnLog, new object[] { EventArgs.Empty });
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Console.Out.WriteLine("LOG-CLICK2 collapsed=" + scc.Panel2Collapsed + " (期望 True) glyph=" + glyphOf());
                onClick.Invoke(btnLog, new object[] { EventArgs.Empty });
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Console.Out.WriteLine("LOG-CLICK3 collapsed=" + scc.Panel2Collapsed + " (期望 False) glyph=" + glyphOf());

                mf.Close();
                return;
            }

            // 真实鼠标点击日志按钮 (批84 用户反馈"点击隐藏后窗口异常且有残留"):
            // Invoke 模拟不带来激活/悬停/鼠标路径, 残留类问题常只在真点击时出现。
            // 点击后按时刻连续抓帧, 找出异常出现在哪一帧。
            if (args.Length >= 1 && args[0] == "logclick")
            {
                StereoSplitter.Dpi.Init();
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(20, 20);
                mf.TopMost = true;
                mf.Show();
                for (int i = 0; i < 14; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }
                var scc = Field(mf, "splitContainer") as SplitContainer;
                var btnLog2 = Field(mf, "btnLog") as Control;
                Action<string> snap = tag =>
                {
                    using (var bmp = new Bitmap(mf.Width, mf.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                        bmp.Save("logclick-" + tag + ".png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                    Console.Out.WriteLine("SNAP " + tag
                        + " rect=" + mf.Location + mf.Size
                        + " client=" + mf.ClientSize
                        + " collapsed=" + scc.Panel2Collapsed
                        + " winH=" + mf.ClientSize.Height + " dist=" + scc.SplitterDistance
                        + " p2h=" + scc.Panel2.Height);
                };
                // 外扩 30px 抓屏: 窗口缩小时旧内容若残留在屏上会落在这个圈里
                Action<string> wideSnap = tag =>
                {
                    var r = new Rectangle(mf.Location.X - 30, mf.Location.Y - 30,
                        mf.Width + 60, mf.Height + 60);
                    using (var bmp = new Bitmap(r.Width, r.Height))
                    {
                        using (var g = Graphics.FromImage(bmp))
                            g.CopyFromScreen(r.Location, Point.Empty, r.Size);
                        bmp.Save("logclick-" + tag + "-wide.png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                };
                Action realClickBtn = () =>
                {
                    Point p = btnLog2.PointToScreen(new Point(btnLog2.Width / 2, btnLog2.Height / 2));
                    SetCursorPos(p.X, p.Y);
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);          // LEFTDOWN
                    Application.DoEvents(); System.Threading.Thread.Sleep(60);
                    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);          // LEFTUP
                };
                snap("base");
                realClickBtn();                                         // 1) 隐藏
                for (int i = 0; i < 5; i++) Application.DoEvents();
                snap("hide-t0");
                System.Threading.Thread.Sleep(80); Application.DoEvents();
                snap("hide-t80");
                System.Threading.Thread.Sleep(200); Application.DoEvents();
                snap("hide-t280");
                System.Threading.Thread.Sleep(600); Application.DoEvents();
                snap("hide-t880");
                wideSnap("hide-t880");
                realClickBtn();                                         // 2) 显示
                for (int i = 0; i < 5; i++) Application.DoEvents();
                snap("show-t0");
                System.Threading.Thread.Sleep(300); Application.DoEvents();
                snap("show-t300");
                wideSnap("show-t300");

                // 批88: 收起 → 用户缩放窗口 → 展开, 应在**当前**大小下方接上"分隔条+日志区",
                // 而不是恢复隐藏前大小; 批94: 收起态窗口底缘 = 深色细线上缘
                int distBeforeHide = scc.SplitterDistance;
                realClickBtn();                                         // 3) 再隐藏
                for (int i = 0; i < 5; i++) Application.DoEvents();
                System.Threading.Thread.Sleep(200); Application.DoEvents();
                int collapsedH2 = mf.ClientSize.Height;
                int distAfterCollapse = scc.SplitterDistance;
                mf.Size = new Size(mf.Width, collapsedH2 - 120);        // 用户拉小窗口
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                int resizedH = mf.ClientSize.Height;
                int restoreH = (int)Field(mf, "_logRestoreH");
                snap("resize-t0");
                realClickBtn();                                         // 4) 再显示
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                int shownH = mf.ClientSize.Height;
                int padB = mf.Padding.Bottom;
                // 批94: 深线画在缝带 sr.Top+(sw-2)/2, 隐藏后窗口底缘 = 缝带上缘+lineOff = 深线上缘
                int lineOff = (scc.SplitterWidth - 2) / 2;
                int expectH = resizedH + restoreH + scc.SplitterWidth + padB - lineOff;
                // 批94: 收起保留 = 标题栏 + Panel1 + lineOff, 底缘 = 深色细线上缘
                // (lineExpect 对齐断言; 旧 keepExpect=缝带上缘公式已过时删除)
                int lineExpect = mf.Padding.Top + distBeforeHide + lineOff;
                Console.Out.WriteLine("RESIZE-RESTORE collapsedWinH=" + collapsedH2
                    + " lineExpect=" + lineExpect
                    + " scc=" + scc.Bounds + " p1h=" + scc.Panel1.Height + " sw=" + scc.SplitterWidth
                    + " padTop=" + mf.Padding.Top + " padBot=" + mf.Padding.Bottom
                    + " resizedH=" + resizedH + " restoreH=" + restoreH
                    + " shownH=" + shownH + " expectH=" + expectH
                    + " collapsed=" + scc.Panel2Collapsed
                    + " p2h=" + scc.Panel2.Height + " dist=" + scc.SplitterDistance
                    + " verdict=" + (shownH == expectH && !scc.Panel2Collapsed
                                && Math.Abs(collapsedH2 - lineExpect) <= 1 ? "OK" : "FAIL"));
                snap("resize-show");
                wideSnap("resize-show");

                // 批92: 用户报"隐藏前后高度不一致" (截图标尺 470 vs 477, 差 7px)。
                // 开合循环前后对比卡片区壳与 Panel1 几何, 抓漂移来源
                var shell2 = Field(mf, "cardShell") as Control;
                Func<string> geomOf = () => "winH=" + mf.ClientSize.Height
                    + " sccH=" + scc.Height + " dist=" + scc.SplitterDistance
                    + " p1h=" + scc.Panel1.Height + " p2h=" + scc.Panel2.Height
                    + " sw=" + scc.SplitterWidth
                    + " shellTop=" + shell2.Top + " shellH=" + shell2.Height
                    + " shellBot=" + shell2.Bottom + " panelH=" + shell2.Parent.ClientSize.Height;
                Console.Out.WriteLine("PANEL-GEOM A(show): " + geomOf());
                realClickBtn();                                         // 隐藏
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Console.Out.WriteLine("PANEL-GEOM B(hide): " + geomOf());
                realClickBtn();                                         // 显示
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Console.Out.WriteLine("PANEL-GEOM C(show): " + geomOf());
                // 拖动分隔条后再开合 (用户可能拖过)
                try { scc.SplitterDistance = scc.SplitterDistance - 60; } catch { }
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Console.Out.WriteLine("PANEL-GEOM D(dragged): " + geomOf());
                realClickBtn();                                         // 隐藏
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Console.Out.WriteLine("PANEL-GEOM E(hide): " + geomOf());
                realClickBtn();                                         // 显示
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Console.Out.WriteLine("PANEL-GEOM F(show): " + geomOf());
                mf.Close();
                return;
            }

            // 卡片区滚动壳 (批85): 原生竖条裁掉 + 右侧留白带自绘细条 + 栅格居中、左右留白对称
            if (args.Length >= 1 && args[0] == "cardshell")
            {
                StereoSplitter.Dpi.Init();
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(20, 20);
                mf.TopMost = true;
                mf.Show();
                for (int i = 0; i < 14; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }
                var shell = Field(mf, "cardShell") as Control;
                var host = Field(mf, "cardHost") as Control;
                var shellParent = shell.Parent;
                StereoSplitter.CardBarOverlay bar = null;
                foreach (Control c in shell.Controls) if (c is StereoSplitter.CardBarOverlay) { bar = (StereoSplitter.CardBarOverlay)c; break; }
                var hostScroll = (ScrollableControl)host;
                int textEnd = (int)Field(shell, "textEnd");
                int cut = -1;
                if (host.Region != null)
                    using (var g = shell.CreateGraphics()) cut = (int)host.Region.GetBounds(g).Width;

                // GEO: 壳/内衬/可视宽几何
                var vs = hostScroll.VerticalScroll;
                Console.Out.WriteLine("CARDSHELL-GEO shell={X=" + shell.Left + ",Y=" + shell.Top
                    + ",W=" + shell.Width + ",H=" + shell.Height + "} inner={X=" + host.Left
                    + ",Y=" + host.Top + ",W=" + host.Width + ",H=" + host.Height + "}"
                    + " contentW=" + (int)typeof(StereoSplitter.CardScrollShell).InvokeMember("ContentWidth",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty, null, shell, null)
                    + " cut=" + cut + " textEnd=" + textEnd
                    + " vscroll=" + vs.Visible + " vmax=" + vs.Maximum + " vval=" + vs.Value
                    + " dispH=" + hostScroll.DisplayRectangle.Height
                    + " autoScroll=" + hostScroll.AutoScroll
                    + " shellScreen=" + shell.PointToScreen(Point.Empty)
                    + " barScreen=" + (bar != null ? bar.RectangleToScreen(bar.ClientRectangle).ToString() : "null")
                    + " hostScreen=" + host.RectangleToScreen(host.ClientRectangle).ToString());

                // SYM: 壳在面板里左右留白对称; 单列卡片行左右留白对称
                int leftGap = shell.Left;
                int rightGap = shellParent.ClientSize.Width - shell.Right;
                var card0 = host.Controls.Count > 0 ? host.Controls[0] : null;
                int cardLeftGap = -1, cardRightGap = -1;
                if (card0 != null)
                {
                    cardLeftGap = shell.Left + host.Left + card0.Left;
                    cardRightGap = shellParent.ClientSize.Width - cardLeftGap - card0.Width;
                }
                Console.Out.WriteLine("CARDSHELL-SYM shellL=" + leftGap + " shellR=" + rightGap
                    + " (期望相等±1) cardL=" + cardLeftGap + " cardR=" + cardRightGap
                    + " (期望相等±1, 单列铺满时都=" + StereoSplitter.Dpi.S(20) + ")");
                bool symOk = Math.Abs(leftGap - rightGap) <= 1
                    && (card0 == null || Math.Abs(cardLeftGap - cardRightGap) <= 1);

                // BAR: 右侧留白带里扫自绘滑条色 (198,203,211); LEAK: 带内不应有原生条深色箭头 (<70)
                int thumbPx = 0, darkPx = 0, hotPx = 0;
                // dark 只扫留白带列 (band 外是卡片内容, X 钮/微调控件的深色像素不算泄漏)
                int bandL = shell.PointToScreen(Point.Empty).X - mf.Left + textEnd;
                int scanL = bandL, scanR = mf.ClientSize.Width - 1;
                int thumbMaxX = -1, thumbMinX = -1;
                using (var bmp = new Bitmap(mf.Width, mf.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                    for (int x = scanL; x < scanR; x++)
                        for (int y = shell.Top; y < shell.Bottom - 1; y++)
                        {
                            if (y >= bmp.Height || x >= bmp.Width) continue;
                            var p = bmp.GetPixel(x, y);
                            if (p.R == 198 && p.G == 203 && p.B == 211) { thumbPx++; if (thumbMinX < 0) thumbMinX = x; thumbMaxX = x; }
                            else if (p.R == 172 && p.G == 178 && p.B == 188) { hotPx++; if (thumbMinX < 0) thumbMinX = x; thumbMaxX = x; }
                            if (p.R < 70 && p.G < 70 && p.B < 70) darkPx++;
                        }
                    // 批90 撤框: FRAME 断言已删。THUMB-FIT 保留 —— 滑条右缘距壳右缘
                    // (留白带外缘) ≤2 物理px = "贴合"语义在无框状态下仍然成立
                    Point shellOrigin = shell.PointToScreen(Point.Empty);
                    int edgeR = shellOrigin.X - mf.Left + shell.Width - 1;
                    int fitGap = thumbMaxX >= 0 ? (edgeR - thumbMaxX) : -99;
                    Console.Out.WriteLine("CARDSHELL-THUMB-FIT thumbX=" + thumbMinX + ".." + thumbMaxX
                        + " shellRightX=" + edgeR + " gap=" + fitGap
                        + " (期望 1..2 = 圆头贴壳右缘) verdict=" + (fitGap >= 1 && fitGap <= 2 ? "OK" : "FAIL"));
                    bmp.Save("cardshell-frame.png", System.Drawing.Imaging.ImageFormat.Png);
                    Console.Out.WriteLine("FRAME-SHOT -> cardshell-frame.png");
                }
                Console.Out.WriteLine("CARDSHELL-BAR thumbPx=" + thumbPx + " hotPx=" + hotPx + " darkPx=" + darkPx + " (thumb/hot 合计>20 = 自绘滑条在屏; dark=0 = 无原生箭头)");
                Console.Out.WriteLine("CARDSHELL-SYM-OK " + symOk);

                // SYMW: 多宽度扫描 (用户批85补充: 左右留白一样多, 滚动条不记入、算留白的一部分)。
                // 每档宽度断言: 壳在面板左右留白相等 + 卡片行在窗口左右留白相等 (滚动条
                // 已被壳裁出内容宽, 天然"不记入", 自绘细条落右侧留白带内)。
                bool symwOk = true;
                int[] sweepW = new int[] { 0, 900, 1100 };   // 0 = 保持当前宽
                foreach (int sw in sweepW)
                {
                    if (sw > 0)
                    {
                        mf.ClientSize = new Size(sw, mf.ClientSize.Height);
                        for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                    }
                    Control card0w = host.Controls.Count > 0 ? host.Controls[0] : null;
                    int lg = shell.Left, rg = shellParent.ClientSize.Width - shell.Right;
                    int clg = -1, crg = -1;
                    if (card0w != null)
                    {
                        clg = shell.Left + host.Left + card0w.Left;
                        crg = shellParent.ClientSize.Width - clg - card0w.Width;
                    }
                    bool okW = Math.Abs(lg - rg) <= 1 && (card0w == null || Math.Abs(clg - crg) <= 1);
                    if (!okW) symwOk = false;
                    // TOPROW-SYM: 规格下拉贴左 / 添加设备贴右, 左右留白一致 (用户批86)
                    var specC = (Control)Field(mf, "cmbSpec");
                    var addC = (Control)Field(mf, "btnAddOutput");
                    int specL = specC.Left;
                    int addR = shellParent.ClientSize.Width - addC.Right;
                    bool okTop = Math.Abs(specL - addR) <= 1;
                    if (!okTop) symwOk = false;
                    Console.Out.WriteLine("CARDSHELL-SYMW winW=" + mf.ClientSize.Width
                        + " shellL=" + lg + " shellR=" + rg + " cardL=" + clg + " cardR=" + crg
                        + " cols=" + (int)Field(mf, "lastCardCols") + " ok=" + okW
                        + " specL=" + specL + " addR=" + addR + " toprowOk=" + okTop
                        + " shellW=" + shell.Width + " contentW=" + (int)typeof(StereoSplitter.CardScrollShell).InvokeMember("ContentWidth",
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty, null, shell, null)
                        + " cardW=" + (card0w != null ? card0w.Width : -1)
                        + " hostW=" + host.Width + " textEnd=" + (int)Field(shell, "textEnd"));
                }
                Console.Out.WriteLine("CARDSHELL-SYMW-OK " + symwOk);
                mf.ClientSize = new Size(625, 625);
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }

                // HBAR (批94b): 窗口窄到挤压带 [HardMinW, MinW) —— 卡片应贴可视宽,
                // 不再抬回 MinW 顶出横向滚动条 (用户: "横向滚动条怎么出来了")
                mf.ClientSize = new Size(460, 625);
                for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                int contentNarrow = (int)typeof(StereoSplitter.CardScrollShell).InvokeMember("ContentWidth",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty, null, shell, null);
                bool hbar = hostScroll.HorizontalScroll.Visible;
                int cardWn = host.Controls.Count > 0 ? host.Controls[0].Width : -1;
                Console.Out.WriteLine("CARDSHELL-HBAR winW=460 contentW=" + contentNarrow
                    + " cardW=" + cardWn + " hscroll=" + hbar
                    + " (期望 False = 挤压带不出横条且卡宽贴可视宽) verdict="
                    + (!hbar && cardWn == contentNarrow ? "OK" : "FAIL"));
                mf.ClientSize = new Size(625, 625);
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }

                // HBAR2 (批95): 再窄过 HardMinW —— 内容真放不下 → 横向滚动必要, 原生横条
                // 仍被窗口区域裁掉, 底部留白带应出现**自绘**横条 (与竖条同款 4px 圆头灰),
                // 且真实拖拽能落位
                mf.ClientSize = new Size(340, 625);
                for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                var innerCards = Field(shell, "Inner") as ScrollableControl;
                var hbarCtl = Field(shell, "HBar") as Control;
                int contentSq = (int)typeof(StereoSplitter.CardScrollShell).InvokeMember("ContentWidth",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.GetProperty, null, shell, null);
                int cardWs = host.Controls.Count > 0 ? host.Controls[0].Width : -1;
                bool hVis = innerCards != null && innerCards.HorizontalScroll.Visible;
                int hThumbPx = 0, hDarkPx = 0, hTx0 = -1, hTx1 = -1;
                Point hbO = hbarCtl.PointToScreen(Point.Empty);
                int hbL = hbO.X - mf.Left, hbT = hbO.Y - mf.Top;
                using (var bmp = new Bitmap(mf.Width, mf.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                    for (int x = hbL; x < hbL + hbarCtl.Width && x < bmp.Width; x++)
                        for (int y = hbT; y < hbT + hbarCtl.Height && y < bmp.Height; y++)
                        {
                            var p = bmp.GetPixel(x, y);
                            bool th = (p.R == 198 && p.G == 203 && p.B == 211) || (p.R == 172 && p.G == 178 && p.B == 188);
                            if (th)
                            {
                                hThumbPx++;
                                if (hTx0 < 0) hTx0 = x;
                                hTx1 = x;
                            }
                            // 原生横条深色箭头/滑块 (<70) 不该出现在底部条带
                            if (p.R < 70 && p.G < 70 && p.B < 70) hDarkPx++;
                        }
                }
                Console.Out.WriteLine("CARDSHELL-HBAR2 winW=340 contentW=" + contentSq + " cardW=" + cardWs
                    + " hscroll=" + hVis + " hThumbPx=" + hThumbPx + " (期望>20) hDarkPx=" + hDarkPx
                    + " (期望0 = 原生横条仍被裁掉) verdict="
                    + (hVis && hThumbPx > 20 && hDarkPx == 0 ? "OK" : "FAIL"));
                using (var bmp2 = new Bitmap(mf.Width, mf.Height))
                {
                    using (var g = Graphics.FromImage(bmp2))
                        g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                    bmp2.Save("cardshell-hbar2.png", System.Drawing.Imaging.ImageFormat.Png);
                    Console.Out.WriteLine("HBAR2-SHOT -> cardshell-hbar2.png");
                }
                // 拖拽横条: 条带中线找滑块列 → 真实鼠标右拖
                int hDrag0 = innerCards != null && innerCards.HorizontalScroll.Visible
                    ? innerCards.HorizontalScroll.Value : -1;
                int hDrag1 = -1, hStarts = 0, hMoves = 0;
                if (hThumbPx > 20)
                {
                    int grabY = hbT + hbarCtl.Height / 2;
                    Point grabH = new Point(mf.Left + (hTx0 + hTx1) / 2, mf.Top + grabY);
                    SetCursorPos(grabH.X, grabH.Y);
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    for (int i = 1; i <= 6; i++)
                    {
                        SetCursorPos(grabH.X + i * 15, grabH.Y);
                        Application.DoEvents(); System.Threading.Thread.Sleep(30);
                        Application.DoEvents();
                    }
                    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
                    for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    hDrag1 = innerCards.HorizontalScroll.Value;
                    hStarts = (int)Field(hbarCtl, "DragStarts");
                    hMoves = (int)Field(hbarCtl, "DragMoves");
                }
                Console.Out.WriteLine("CARDSHELL-DRAG-H before=" + hDrag0 + " after=" + hDrag1
                    + " (期望 after>before=0) starts=" + hStarts + " moves=" + hMoves
                    + " verdict=" + (hDrag1 > hDrag0 && hStarts >= 1 ? "OK" : "FAIL"));
                mf.ClientSize = new Size(625, 625);
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }

                // NOSCROLL: 内容放得下时不该有滑条 (用户批87: 不用滑动就不显示滑动条)。
                // 窗口加高 + 分隔距离拉大, 让 Panel1 高到所有卡片放得下 → 扫整条留白带,
                // 滑条色应为 0。(多声道卡片总高 ~648, 仅拉分隔距离在 625 窗口里放不下)
                int origDist = ((SplitContainer)Field(mf, "splitContainer")).SplitterDistance;
                mf.ClientSize = new Size(mf.ClientSize.Width, 1000);
                var sccN = (SplitContainer)Field(mf, "splitContainer");
                sccN.SplitterDistance = Math.Max(Dpi.S(300), sccN.Height - sccN.SplitterWidth - Dpi.S(10));
                for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                int nsThumb = 0;
                int nsBandL = shell.PointToScreen(Point.Empty).X - mf.Left
                    + (int)Field(shell, "textEnd");
                using (var bmp = new Bitmap(mf.Width, mf.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                    for (int x = nsBandL; x < mf.ClientSize.Width - 1; x++)
                        for (int y = shell.Top; y < shell.Bottom - 1; y++)
                        {
                            if (y >= bmp.Height || x >= bmp.Width) continue;
                            var p = bmp.GetPixel(x, y);
                            bool th = (p.R == 198 && p.G == 203 && p.B == 211) || (p.R == 172 && p.G == 178 && p.B == 188);
                            if (th) nsThumb++;
                        }
                }
                int lastBottom = 0;
                foreach (Control cc in host.Controls)
                    if (cc.Bottom > lastBottom) lastBottom = cc.Bottom;
                Console.Out.WriteLine("CARDSHELL-NOSCROLL vscroll=" + hostScroll.VerticalScroll.Visible
                    + " nMax=" + hostScroll.VerticalScroll.Maximum
                    + " nPage=" + hostScroll.VerticalScroll.Maximum
                    + " shellH=" + shell.Height + " hostClient=" + hostScroll.ClientSize.Width + "x" + hostScroll.ClientSize.Height
                    + " dispH=" + hostScroll.DisplayRectangle.Height
                    + " lastCardBottom=" + lastBottom
                    + " autoScrollMinSize=" + hostScroll.AutoScrollMinSize
                    + " thumbPx=" + nsThumb + " (期望 0 —— 放得下就不显示滑条)");
                mf.ClientSize = new Size(625, 625);
                ((SplitContainer)Field(mf, "splitContainer")).SplitterDistance = origDist;
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                int before = Math.Abs(hostScroll.AutoScrollPosition.Y);
                // 先扫出滑块当前竖直范围 (在条带中线上找滑条色)
                int bxCol = -1, tyFound = -1, thFound = 0;
                using (var bmp = new Bitmap(mf.Width, mf.Height))
                {
                    using (var g = Graphics.FromImage(bmp))
                        g.CopyFromScreen(mf.Location, Point.Empty, mf.Size);
                    int shellScreenX = shell.PointToScreen(Point.Empty).X - mf.Left;
                    int zoneR = shellScreenX + shell.Width - 2;
                    for (int x = shellScreenX + textEnd; x < zoneR && bxCol < 0; x++)
                    {
                        int first = -1, cnt = 0;
                        for (int y = shell.Top; y < shell.Bottom; y++)
                        {
                            if (y >= bmp.Height || x >= bmp.Width) continue;
                            var p = bmp.GetPixel(x, y);
                            bool th = (p.R == 198 && p.G == 203 && p.B == 211) || (p.R == 172 && p.G == 178 && p.B == 188);
                            if (th) { if (first < 0) first = y; cnt++; }
                        }
                        if (cnt > 10) { bxCol = x; tyFound = first; thFound = cnt; }
                    }
                }
                if (bxCol >= 0)
                {
                    Point grab = new Point(mf.Left + bxCol, mf.Top + tyFound + thFound / 2);
                    SetCursorPos(grab.X, grab.Y);
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    for (int i = 1; i <= 6; i++)
                    {
                        SetCursorPos(grab.X, grab.Y + i * 15);
                        Application.DoEvents(); System.Threading.Thread.Sleep(30);
                        Application.DoEvents();
                    }
                    mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);
                    for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                    int after = Math.Abs(hostScroll.AutoScrollPosition.Y);
                    Console.Out.WriteLine("CARDSHELL-DRAG before=" + before + " after=" + after
                        + " (期望显著增大) starts=" + (int)Field(bar, "DragStarts")
                        + " moves=" + (int)Field(bar, "DragMoves"));
                }
                else Console.Out.WriteLine("CARDSHELL-DRAG skip (没找到滑块 —— 内容不足一屏?)");
                mf.Close();
                return;
            }

            // DPI 适配自检: 打印缩放相关的实际值 (uidump 自己也要 Init, 否则 Dpi.S() 恒为 1)
            if (args.Length >= 1 && args[0] == "dpiprobe")
            {
                StereoSplitter.Dpi.Init();
                Console.Out.WriteLine("Dpi.Factor=" + StereoSplitter.Dpi.Factor + " S(15)=" + StereoSplitter.Dpi.S(15));
                var mf = new MainForm();
                mf.StartPosition = FormStartPosition.Manual;
                mf.Location = new Point(20, 20);
                mf.TopMost = true;
                mf.Show();
                for (int i = 0; i < 14; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }
                Console.Out.WriteLine("DeviceDpi=" + mf.DeviceDpi
                    + " AutoScaleMode=" + mf.AutoScaleMode
                    + " AutoScaleDimensions=" + mf.AutoScaleDimensions
                    + " Current=" + mf.CurrentAutoScaleDimensions
                    + " Size=" + mf.Size + " Client=" + mf.ClientSize);
                var b = Field(mf, "btnStart") as Control;
                var host = Field(mf, "cardHost") as Control;
                if (b != null) Console.Out.WriteLine("btnStart=" + b.Bounds);
                if (host != null) Console.Out.WriteLine("cardHost=" + host.Bounds + " cards=" + host.Controls.Count);
                mf.Close();
                return;
            }

            var f = new MainForm();
            int w, h;
            if (args.Length >= 2 && int.TryParse(args[0], out w) && int.TryParse(args[1], out h))
                f.Size = new Size(w, h);
            f.Show();
            for (int i = 0; i < 15; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

            var sb = new StringBuilder();
            sb.AppendLine("FORM      size=" + f.Size + " client=" + f.ClientSize);

            var split = Field(f, "splitContainer") as SplitContainer;
            Control cardHost = Field(f, "cardHost") as Control;
            Control panel1 = split != null ? (Control)split.Panel1 : null;
            Control panel2 = split != null ? (Control)split.Panel2 : null;
            if (panel1 != null) sb.AppendLine("PANEL1    client=" + panel1.ClientSize);
            if (panel2 != null) sb.AppendLine("PANEL2    client=" + panel2.ClientSize);
            if (split != null)
                sb.AppendLine("SPLIT     min1=" + split.Panel1MinSize + " min2=" + split.Panel2MinSize
                    + " dist=" + split.SplitterDistance + " h=" + split.Height);
            var sc = cardHost as ScrollableControl;
            if (cardHost != null)
                sb.AppendLine("CARDHOST  bounds=" + cardHost.Bounds + " client=" + cardHost.ClientSize
                    + " padding=" + cardHost.Padding
                    + " hscroll=" + (sc != null && sc.HorizontalScroll.Visible)
                    + " vscroll=" + (sc != null && sc.VerticalScroll.Visible));

            var cards = Field(f, "cards") as IEnumerable;
            int n = 0;
            if (cards != null && cardHost != null)
            {
                sb.AppendLine("--- CARDS (relative to cardHost client) ---");
                foreach (Control c in cards)
                {
                    sb.AppendLine("CARD[" + n + "] bounds=" + c.Bounds + " right=" + c.Right
                        + " availW=" + cardHost.ClientSize.Width
                        + (c.Right > cardHost.ClientSize.Width ? "  <<< OVERFLOW " + (c.Right - cardHost.ClientSize.Width) + "px" : ""));
                    foreach (Control k in c.Controls)
                        sb.AppendLine("        " + k.GetType().Name + " '" + Trim(k.Text) + "' bounds=" + k.Bounds
                            + (k.Right > c.Width ? "  <<< CLIPPED " + (k.Right - c.Width) + "px" : ""));
                    n++;
                }
                if (n == 0) sb.AppendLine("(no cards)");
            }

            if (panel1 != null && cardHost != null)
                sb.AppendLine("--- PANEL1 CHILDREN ---");
            if (panel1 != null)
            {
                foreach (Control c in panel1.Controls)
                {
                    if (c == cardHost) continue;
                    sb.AppendLine("  " + c.GetType().Name + " '" + Trim(c.Text) + "' bounds=" + c.Bounds
                        + (c.Right > panel1.ClientSize.Width ? "  <<< OVERFLOW " + (c.Right - panel1.ClientSize.Width) + "px" : ""));
                }
                // 规格角标: 内容总宽超过可视宽就会被裁掉 (不会报错, 只是少显示几个)
                foreach (Control c in panel1.Controls)
                {
                    if (!(c is FlowLayoutPanel)) continue;
                    int total = 0;
                    foreach (Control k in c.Controls) total += k.Width + k.Margin.Horizontal;
                    sb.AppendLine("  SPECBADGES count=" + c.Controls.Count + " totalW=" + total
                        + " availW=" + c.ClientSize.Width
                        + (total > c.ClientSize.Width ? "  <<< CLIPPED " + (total - c.ClientSize.Width) + "px" : "  ok"));
                }
                DumpOverlaps(panel1, "panel1", sb);
            }

            Console.Out.Write(sb.ToString());

            // 滚轮误命中检查: 日志区里的屏幕点不应该命中任何音量滑条
            Control txtLog = Field(f, "txtLog") as Control;
            if (txtLog != null)
            {
                Point[] probes = {
                    new Point(txtLog.Width / 2, Math.Max(2, txtLog.Height / 2)),
                    new Point(txtLog.Width / 2, 2),
                    new Point(12, Math.Max(2, txtLog.Height / 2))
                };
                foreach (Point p0 in probes)
                {
                    Point sp = txtLog.PointToScreen(p0);
                    VolumeSlider hit = VolumeSlider.HitTestScreen(sp);
                    string desc = hit == null ? "NULL (good)"
                        : "HIT <<< " + hit.Parent.GetType().Name + " parentBounds=" + hit.Parent.Bounds
                          + " sliderScreen=" + hit.RectangleToScreen(hit.ClientRectangle);
                    Console.Out.Write("HITTEST log" + p0 + " screen=" + sp + " -> " + desc + "\n");
                }
            }

            // 造多张卡片, 让卡片区出现竖滚动条、卡片被裁剪 (滚轮误命中的必要条件)
            if (args.Length >= 3 && args[2] == "many")
            {
                MethodInfo add = f.GetType().GetMethod("AddLaneCard",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                for (int i = 0; i < 6; i++) { add.Invoke(f, new object[] { null }); Application.DoEvents(); }
                for (int i = 0; i < 8; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Control log = Field(f, "txtLog") as Control;
                if (cardHost != null)
                    Console.Out.Write("MANY cards=" + (cards as ICollection == null ? "?" : ((ICollection)cards).Count.ToString())
                        + " cardHost=" + cardHost.Bounds + " vscroll=" + ((ScrollableControl)cardHost).VerticalScroll.Visible + "\n");
                if (log != null)
                {
                    Point sp = log.PointToScreen(new Point(log.Width / 2, Math.Max(2, log.Height / 2)));
                    VolumeSlider hit = VolumeSlider.HitTestScreen(sp);
                    Console.Out.Write("HITTEST(many) screen=" + sp + " -> "
                        + (hit == null ? "NULL (good)"
                            : "HIT <<< " + hit.Parent.GetType().Name
                              + " sliderScreen=" + hit.RectangleToScreen(hit.ClientRectangle)) + "\n");
                }

                // 反向验证: 被卡片区裁剪掉的滑条, 其中心点不该命中自己
                if (cards != null && cardHost != null)
                {
                    Rectangle hostSr = cardHost.RectangleToScreen(cardHost.ClientRectangle);
                    int clipped = 0, bad = 0;
                    foreach (Control card in cards)
                        foreach (Control k in card.Controls)
                        {
                            VolumeSlider s = k as VolumeSlider;
                            if (s == null) continue;
                            Rectangle sr = s.RectangleToScreen(s.ClientRectangle);
                            if (hostSr.Contains(sr)) continue;      // 还在可视区内
                            clipped++;
                            Point c0 = new Point(sr.X + sr.Width / 2, sr.Y + sr.Height / 2);
                            if (VolumeSlider.HitTestScreen(c0) != null) bad++;
                        }
                    Console.Out.Write("CLIPPED-HITTEST clippedSliders=" + clipped
                        + " falseHits=" + bad + (bad == 0 ? " (good)" : " <<< BAD") + "\n");
                }
            }

            // 托盘隐藏诊断: 走真实的 HideToTray/RestoreFromTray, 看句柄是否被重建 (=闪烁)
            if (args.Length >= 3 && args[2] == "tray")
            {
                IntPtr h1 = f.Handle;
                int childBefore = CountHandles(f);
                MethodInfo hide = f.GetType().GetMethod("HideToTray",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                hide.Invoke(f, null);
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                IntPtr h2 = f.Handle;
                Console.Out.Write("TRAY HideToTray: handle " + h1 + " -> " + h2
                    + (h1 == h2 ? "  SAME (no recreate, good)" : "  RECREATED <<< flash") + "\n");
                Console.Out.Write("TRAY   visible=" + f.Visible + " showintaskbar=" + f.ShowInTaskbar
                    + " state=" + f.WindowState + " childHandles " + childBefore + " -> " + CountHandles(f) + "\n");

                MethodInfo restore = f.GetType().GetMethod("RestoreFromTray",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                restore.Invoke(f, null);
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }
                IntPtr h3 = f.Handle;
                Console.Out.Write("TRAY RestoreFromTray: handle " + h2 + " -> " + h3
                    + (h3 == h2 ? "  SAME (good)" : "  RECREATED <<< flash")
                    + " visible=" + f.Visible + " state=" + f.WindowState + "\n");
            }

            // 自绘下拉: 展开后的弹层几何 (行高/圆角裁剪是运行时行为, 这里量尺寸)
            DumpComboPopup(f, "cmbSpec", "规格");
            DumpComboPopup(f, "cmbDefault", "捕获");

            // 极值测试: 把日志区一路压到 0, 再看控件是否溢出
            if (split != null && args.Length >= 3 && (args[2] == "log0" || args[2] == "top0"))
            {
                split.SplitterDistance = args[2] == "log0"
                    ? split.Height - split.SplitterWidth
                    : 0;
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }
                Control log = Field(f, "txtLog") as Control;
                Console.Out.Write("AFTER " + args[2] + ": dist=" + split.SplitterDistance
                    + " panel1=" + panel1.ClientSize
                    + " cardHost=" + (cardHost == null ? "null" : cardHost.Bounds.ToString())
                    + " panel2=" + panel2.ClientSize
                    + " txtLog=" + (log == null ? "null" : log.Bounds.ToString()) + "\n");
            }
            Console.Out.Flush();
            Environment.Exit(0);
        }

        /// <summary>同一容器内可见控件的两两重叠 + 越界检测 (布局回归用)。</summary>
        static void DumpOverlaps(Control host, string label, StringBuilder sb)
        {
            var vis = new System.Collections.Generic.List<Control>();
            foreach (Control c in host.Controls) if (c.Visible) vis.Add(c);
            foreach (Control c in vis)
                if (c.Right > host.ClientSize.Width || c.Bottom > host.ClientSize.Height)
                    sb.AppendLine("  !! CLIP(" + label + ") " + c.GetType().Name + " '" + Trim(c.Text) + "' bounds=" + c.Bounds
                        + " host=" + host.ClientSize);

            for (int i = 0; i < vis.Count; i++)
                for (int j = i + 1; j < vis.Count; j++)
                {
                    Rectangle a = vis[i].Bounds, b = vis[j].Bounds;
                    if (!a.IntersectsWith(b)) continue;
                    Rectangle x = Rectangle.Intersect(a, b);
                    if (x.Width <= 0 || x.Height <= 0) continue;
                    sb.AppendLine("  !! OVERLAP(" + label + ") '" + Trim(vis[i].Text) + "' " + a
                        + " x '" + Trim(vis[j].Text) + "' " + b + " -> " + x.Width + "x" + x.Height);
                }
        }

        /// <summary>单行控件文字宽度 vs 可用宽度 (pad = 左右内边距合计)。</summary>
        static string FitText(Control c, int pad)
        {
            Size need = TextRenderer.MeasureText(c.Text, c.Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            int avail = c.Width - pad;
            return need.Width + "/" + avail + (need.Width > avail ? " <<< OVER" : "");
        }

        /// <summary>单行 Label: 文字宽度超出控件宽度就会被截断 (Label 不自动换行)。</summary>
        static string FitLabel(Label lb)
        {
            Size need = TextRenderer.MeasureText(lb.Text, lb.Font, new Size(int.MaxValue, int.MaxValue),
                TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            return need.Width + "/" + lb.Width + (need.Width > lb.Width ? " <<< CLIP" : "");
        }

        /// <summary>说明文字的换行高度是否够 (Label 固定宽高时最容易切掉最后一行)。</summary>
        static void DumpTextFit(Control host, string label, StringBuilder sb)
        {
            foreach (Control c in host.Controls)
            {
                Label lb = c as Label;
                if (lb != null && !lb.AutoSize && !string.IsNullOrEmpty(lb.Text))
                {
                    Size need = TextRenderer.MeasureText(lb.Text, lb.Font,
                        new Size(lb.Width, int.MaxValue), TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
                    if (need.Height > lb.Height + 2)
                        sb.AppendLine("  !! TEXT-CLIP(" + label + ") '" + Trim(lb.Text) + "' need=" + need.Height + " has=" + lb.Height);
                }
                if (c.HasChildren) DumpTextFit(c, label + "/" + c.GetType().Name, sb);
            }
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool PrintWindow(IntPtr hWnd, IntPtr hdc, int flags);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool SetCursorPos(int x, int y);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtra);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern void mouse_event(int flags, int dx, int dy, int data, IntPtr extra);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern short GetAsyncKeyState(int key);

        /// <summary>真拖一次鼠标 (左键按下 → 移 → 抬起)。缩放/拖动窗口只有真鼠标才走系统循环。</summary>
        static void DragMouse(Point from, int dx, int dy)
        {
            SetCursorPos(from.X, from.Y);
            for (int i = 0; i < 3; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);          // LEFTDOWN
            for (int i = 1; i <= 6; i++)
            {
                SetCursorPos(from.X + dx * i / 6, from.Y + dy * i / 6);
                for (int k = 0; k < 3; k++) { Application.DoEvents(); System.Threading.Thread.Sleep(20); }
            }
            mouse_event(0x0004, 0, 0, 0, IntPtr.Zero);          // LEFTUP
            for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(25); }
        }

        /// <summary>向数字框的箭头区投一次真实的左键按下/抬起。</summary>
        static void ClickSpin(Control buttons, bool up)
        {
            if (buttons == null || !buttons.IsHandleCreated) return;
            int y = up ? 2 : Math.Max(2, buttons.ClientSize.Height - 3);
            IntPtr lp = (IntPtr)((y << 16) | 4);
            SendMessage(buttons.Handle, 0x0201, (IntPtr)1, lp);   // WM_LBUTTONDOWN
            SendMessage(buttons.Handle, 0x0202, (IntPtr)0, lp);   // WM_LBUTTONUP
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool GetScrollInfo(IntPtr hWnd, int nBar, ref ScrollInfo si);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        struct ScrollInfo { public int cbSize, fMask, nMin, nMax, nPage, nPos, nTrackPos; }

        /// <summary>读控件竖向滚动信息; 不需要滚动 (全文可见) 返回 null。</summary>
        static ScrollInfo? VertScrollInfo(Control c)
        {
            if (c == null || !c.IsHandleCreated) return null;
            var si = new ScrollInfo();
            si.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(ScrollInfo));
            si.fMask = 0x1 | 0x2 | 0x4;   // SIF_RANGE | SIF_PAGE | SIF_POS
            if (!GetScrollInfo(c.Handle, 1, ref si) || si.nMax <= 0 || si.nPage >= si.nMax + 1) return null;
            return si;
        }

        /// <summary>按 RoundLogShell 的几何算滑块顶边 y (壳局部) 与滑块高。</summary>
        static void ThumbGeom(Control ls, ScrollInfo si, out int ty, out int th)
        {
            int trackTop = Dpi.S(6), trackLen = ls.Height - 1 - trackTop * 2;
            th = Math.Max(Dpi.S(18), (int)((long)trackLen * si.nPage / (si.nMax + 1)));
            int maxTop = si.nMax + 1 - si.nPage;
            ty = trackTop + (maxTop > 0 ? (int)((long)(trackLen - th) * si.nPos / maxTop) : 0);
        }

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern int GetSystemMetrics(int nIndex);

        /// <summary>反射读 int 字段 (internal 诊断计数)。</summary>
        static int RefInt(object o, string name)
        {
            var f = o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            return f == null ? -1 : (int)f.GetValue(o);
        }

        /// <summary>反射找壳内第一个 NumericUpDown 的宽 (探针不用 Linq)。</summary>
        static int InnerNumericW(Control shell)
        {
            foreach (Control c in shell.Controls)
                if (c is NumericUpDown) return c.Width;
            return -1;
        }

        static int CountHandles(Control root)
        {
            int n = root.IsHandleCreated ? 1 : 0;
            foreach (Control c in root.Controls) n += CountHandles(c);
            return n;
        }

        static string Trim(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            s = s.Replace("\r", " ").Replace("\n", " ");
            return s.Length > 22 ? s.Substring(0, 22) : s;
        }

        /// <summary>下拉框的选中索引 / 条目数 / 设置选中项 —— 自绘的 RoundedCombo 与原生 ComboBox 都覆盖。</summary>
        static int SelOf(Control c)
        {
            if (c is RoundedCombo) return ((RoundedCombo)c).SelectedIndex;
            if (c is ComboBox) return ((ComboBox)c).SelectedIndex;
            return -1;
        }

        static void SetSel(Control c, int idx)
        {
            if (c is RoundedCombo) { ((RoundedCombo)c).SelectedIndex = idx; return; }
            if (c is ComboBox) { ((ComboBox)c).SelectedIndex = idx; }
        }

        static int ItemCountOf(Control c)
        {
            if (c is RoundedCombo) return ((RoundedCombo)c).Items.Count;
            if (c is ComboBox) return ((ComboBox)c).Items.Count;
            return 0;
        }

        /// <summary>
        /// 展开一个自绘下拉, 打印弹层与其中列表的实际尺寸, 再收起。
        /// <para>弹层的尺寸是 ToolStrip 的布局引擎摆的, 算错只会表现为"列表被裁掉一截",
        /// 光读代码看不出来 —— 这里量一遍: 列表高度必须 ≥ 行数×行高, 且窗口要带圆角 Region。</para>
        /// </summary>
        static void DumpComboPopup(Control host, string field, string label)
        {
            RoundedCombo combo = Field(host, field) as RoundedCombo;
            if (combo == null) { Console.Out.WriteLine("COMBO[" + label + "] not found"); return; }

            MeasureComboPopup(combo, label);

            // 再把可见行压到 3 行量一次 —— 这是"必须有滚动条"的分支。
            // 滚动条现在画在右侧留白里, 弹层宽度不能因此变宽 (以前这里加的是 VerticalScrollBarWidth,
            // 于是需要滚动时弹层比下拉框本体宽出 17px, 行高亮也铺不满)。
            int old = combo.MaxDropDownItems;
            if (combo.Items.Count > 3 && old > 3)
            {
                combo.MaxDropDownItems = 3;
                MeasureComboPopup(combo, label + "-scroll");
                combo.MaxDropDownItems = old;
            }
        }

        static void MeasureComboPopup(RoundedCombo combo, string label)
        {
            MethodInfo openM = typeof(RoundedCombo).GetMethod("OpenDropDown", BindingFlags.Instance | BindingFlags.NonPublic);
            if (openM == null) { Console.Out.WriteLine("COMBO[" + label + "] OpenDropDown missing"); return; }
            openM.Invoke(combo, null);
            for (int i = 0; i < 5; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

            ToolStrip ts = Field(combo, "popup") as ToolStrip;
            if (ts == null || !ts.Visible)
            {
                Console.Out.WriteLine("COMBO[" + label + "] popup=closed items=" + combo.Items.Count);
                return;
            }

            Control list = null;
            if (ts.Items.Count > 0)
            {
                var h = ts.Items[0] as ToolStripControlHost;
                if (h != null) list = h.Control;
            }

            int rows = Math.Min(combo.Items.Count, combo.MaxDropDownItems);
            bool fit = list != null && list.Height >= rows * combo.ItemHeight;
            // 自绘列表不另存条目副本 → 行数直接来自数据源
            int listItems = list == null ? -1 : combo.Items.Count;
            // 弹层必须与本体等宽。窗口尺寸即内容尺寸 (无阴影外扩), 可以直接比
            bool sameW = ts.Width == Math.Max(combo.Width, combo.DropDownWidth);
            // 本体文字区够不够放下当前选中项 —— 60px 宽的下拉最容易在这里翻车 (直接看界面只是
            // 觉得"怎么是省略号", 量出来才知道差几像素)
            int textRoom = combo.Width - RoundedCombo.TextPadX - RoundedCombo.ArrowZone;
            int selW = TextRenderer.MeasureText(combo.SelectedText ?? "", combo.Font).Width;
            Console.Out.WriteLine("COMBO[" + label + "] items=" + combo.Items.Count
                + " rows=" + rows + " rowH=" + combo.ItemHeight
                + " scroll=" + (combo.Items.Count > rows ? "yes" : "no")
                + " popup=" + ts.Size
                + " comboW=" + combo.Width
                + " textRoom=" + textRoom + "/selW=" + selW
                + (selW > textRoom ? " <<< ELLIPSIS" : "")
                + " pad=" + ts.Padding.Horizontal + "/" + ts.Padding.Vertical
                + " list=" + (list == null ? "null" : list.Size.ToString())
                + " listItems=" + listItems
                + " region=" + (ts.Region == null ? "NONE" : "rounded")
                + " sameWidth=" + (sameW ? "ok" : "BAD")
                + " fit=" + (fit && listItems == combo.Items.Count ? "ok" : "BAD"));

            MethodInfo closeM = typeof(RoundedCombo).GetMethod("CloseDropDown", BindingFlags.Instance | BindingFlags.NonPublic);
            if (closeM != null) closeM.Invoke(combo, null);
            for (int i = 0; i < 3; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
        }

        /// <summary>空转消息泵: 让布局/事件跑完 (无窗口应用里 DoEvents 是唯一的"再刷新一次")。</summary>
        static void Pump()        {
            for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
        }

        /// <summary>试装"自绘边框"的标准处理: 不让系统画 NC 区 (标题栏/边框都是我们自己的)。</summary>
        sealed class ChromeSpy : NativeWindow
        {
            public bool Swallow = true;
            protected override void WndProc(ref Message m)
            {
                if (Swallow && (m.Msg == 0x0086 || m.Msg == 0x0085))   // WM_NCACTIVATE / WM_NCPAINT
                {
                    m.Result = (IntPtr)1;
                    return;
                }
                base.WndProc(ref m);
            }
        }

        /// <summary>挂在窗口 proc 前面偷看消息 (AssignHandle 会链到原来的 proc, 不影响窗口行为)。</summary>
        sealed class MsgSpy : NativeWindow
        {
            public readonly StringBuilder Log = new StringBuilder();
            protected override void WndProc(ref Message m)
            {
                switch (m.Msg)
                {
                    case 0x0084:
                        base.WndProc(ref m);
                        Log.Append("NCHITTEST→").Append(m.Result.ToInt64()).Append(' ');
                        return;
                    case 0x00A1:
                        Log.Append("NCLDOWN(").Append((int)m.WParam).Append(") ");
                        break;
                    case 0x0112:
                        Log.Append("SYS(0x").Append(((int)m.WParam & 0xFFF0).ToString("X")).Append(") ");
                        break;
                    case 0x0231: Log.Append("ENTERSIZE "); break;
                    case 0x0232: Log.Append("EXITSIZE "); break;
                    case 0x00A3: Log.Append("NCLDBLCLK(").Append((int)m.WParam).Append(") "); break;
                }
                base.WndProc(ref m);
            }
        }

        /// <summary>只记鼠标/系统命令: 判断拖拽的按下有没有真的落到窗口上。</summary>
        sealed class MsgLog : IMessageFilter
        {
            readonly StringBuilder sb;
            public MsgLog(StringBuilder sb) { this.sb = sb; }
            public bool PreFilterMessage(ref Message m)
            {
                switch (m.Msg)
                {
                    case 0x0201: sb.Append("LDOWN(").Append(m.LParam.ToInt64() & 0xFFFF).Append(',').Append((m.LParam.ToInt64() >> 16) & 0xFFFF).Append(") "); break;
                    case 0x0202: sb.Append("LUP "); break;
                    case 0x0200: sb.Append("."); break;
                    case 0x00A1: sb.Append("NCLDOWN(").Append((int)m.WParam).Append(") "); break;
                    case 0x0112: sb.Append("SYS(0x").Append(((int)m.WParam & 0xFFF0).ToString("X")).Append(") "); break;
                    case 0x0231: sb.Append("ENTERSIZE "); break;
                    case 0x0232: sb.Append("EXITSIZE "); break;
                }
                return false;
            }
        }

        /// <summary>把 WM_NCHITTEST 直接发给窗口, 看它回什么命中码 (客户区坐标)。</summary>
        static string HitCode(Form fm, int x, int y)
        {
            Point scr = fm.PointToScreen(new Point(x, y));
            IntPtr lp = (IntPtr)((scr.Y << 16) | (scr.X & 0xFFFF));
            long r = SendMessage(fm.Handle, 0x0084, IntPtr.Zero, lp).ToInt64();
            switch (r)
            {
                case 1: return "CLIENT";
                case 2: return "CAPTION";
                case 10: return "LEFT";
                case 11: return "RIGHT";
                case 12: return "TOP";
                case 13: return "TOPLEFT";
                case 14: return "TOPRIGHT";
                case 15: return "BOTTOM";
                case 16: return "BOTTOMLEFT";
                case 17: return "BOTTOMRIGHT";
                default: return r.ToString();
            }
        }

        /// <summary>模拟一次用户拖动 (Value 赋值不会触发 Scroll, 自动静音那类规则就测不到)。</summary>
        static void Drag(VolumeSlider s, int v)
        {
            if (s == null) return;
            var m = typeof(VolumeSlider).GetMethod("SetValue", BindingFlags.Instance | BindingFlags.NonPublic);
            if (m != null) m.Invoke(s, new object[] { v, true });
            Pump();
        }

        /// <summary>模拟一次点击: Control.OnClick 是 protected, 而它正是按钮切状态的入口。</summary>
        static void Click(Control c)
        {
            if (c == null) return;
            var m = typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic);
            if (m != null) m.Invoke(c, new object[] { EventArgs.Empty });
            Pump();
        }



        /// <summary>最近邻放大保存, 便于数像素。</summary>
        static void SaveZoom(System.Drawing.Image bmp, string path, int z = 6)
        {
            using (var big = new Bitmap(bmp.Width * z, bmp.Height * z))
            {
                using (var g = Graphics.FromImage(big))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                    g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                    g.DrawImage(bmp, 0, 0, big.Width, big.Height);
                }
                big.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
        }


        /// <summary>采样外壳上/下边框那一行的像素: 数字段与文字段各取一点, 看是否还是边框灰。</summary>
        static void DumpEdges(Form host, Control shell, string tag)
        {
            Point at = shell.PointToScreen(Point.Empty);
            int w = shell.Width, h = shell.Height;
            // 数字区在壳内偏右, 取 60% 处; 文字区取 12%
            int[] xs = { (int)(w * 0.12), (int)(w * 0.33) };   // 33% 处正落在数字框窗口内
            using (var bmp = new Bitmap(w, h))
            {
                using (var g = Graphics.FromImage(bmp))
                    g.CopyFromScreen(at, Point.Empty, new Size(w, h));
                bmp.Save("bp-" + tag + ".png", System.Drawing.Imaging.ImageFormat.Png);
                foreach (int x in xs)
                {
                    var sb = new StringBuilder();
                    foreach (int y in new[] { 0, 1, h - 2, h - 1 })
                    {
                        Color c = bmp.GetPixel(x, y);
                        sb.Append(" y" + y + "=" + c.R + "," + c.G + "," + c.B);
                    }
                    Console.Out.WriteLine(tag + " x=" + x + sb);
                }
            }
        }


        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr GetFocus();

        /// <summary>当前键盘焦点所在的控件链 (从最内层控件往上, 打印类型名)。</summary>
        static string FocusChain()
        {
            IntPtr h = GetFocus();
            if (h == IntPtr.Zero) return "(none)";
            Control c = Control.FromChildHandle(h);
            var sb = new StringBuilder();
            for (Control p = c; p != null; p = p.Parent)
            {
                sb.Append(p.GetType().Name);
                if (p is UpDownBase)
                {
                    foreach (Control k in p.Controls)
                    {
                        var tb = k as TextBox;
                        if (tb != null)
                            sb.Append("[sel=" + tb.SelectionStart + "+" + tb.SelectionLength
                                + " foc=" + tb.Focused + "]");
                    }
                }
                sb.Append(" < ");
            }
            return sb.ToString();
        }


        /// <summary>把点集按 8 邻域聚成连通块, 返回外接矩形 (哈希网格泛洪, O(n))。</summary>
        static List<Rectangle> Clusters(List<Point> pts, int tol)
        {
            var res = new List<Rectangle>();
            var set = new HashSet<Point>(pts);
            var q = new Queue<Point>();
            while (set.Count > 0)
            {
                Point seed = default(Point);
                foreach (Point k in set) { seed = k; break; }
                int minX = seed.X, minY = seed.Y, maxX = minX, maxY = minY;
                set.Remove(seed); q.Enqueue(seed);
                while (q.Count > 0)
                {
                    Point cur = q.Dequeue();
                    if (cur.X < minX) minX = cur.X;
                    if (cur.Y < minY) minY = cur.Y;
                    if (cur.X > maxX) maxX = cur.X;
                    if (cur.Y > maxY) maxY = cur.Y;
                    for (int dx = -tol; dx <= tol; dx++)
                        for (int dy = -tol; dy <= tol; dy++)
                        {
                            var nb = new Point(cur.X + dx, cur.Y + dy);
                            if (set.Remove(nb)) q.Enqueue(nb);
                        }
                }
                res.Add(new Rectangle(minX, minY, maxX - minX + 1, maxY - minY + 1));
            }
            return res;
        }


        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern IntPtr WindowFromPoint(Point p);

        [System.Runtime.InteropServices.DllImport("user32.dll", CharSet = System.Runtime.InteropServices.CharSet.Auto)]
        static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

        /// <summary>打印窗口句柄的类名 + 对应控件类型 (定位"是谁画上去的")。</summary>
        static string WindowInfo(IntPtr h)
        {
            if (h == IntPtr.Zero) return "(null)";
            var sb = new StringBuilder(256);
            GetClassName(h, sb, sb.Capacity);
            Control c = Control.FromChildHandle(h);
            return sb + " / " + (c == null ? "(no control)" : c.GetType().Name);
        }

        static object Field(object o, string name)
        {
            if (o == null) return null;
            var t = o.GetType();
            while (t != null)
            {
                var fi = t.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                if (fi != null) return fi.GetValue(o);
                t = t.BaseType;
            }
            return null;
        }

        /// <summary>抓按钮左侧图形区 (动效画的那一带) 的灰度快照, 供两帧差分。</summary>
        static byte[, ,] ButtonGlyphZone(Control btn)
        {
            int w = btn.Width, h = btn.Height;
            var zone = new byte[w, h, 3];
            using (var bmp = new Bitmap(w, h))
            {
                btn.DrawToBitmap(bmp, new Rectangle(0, 0, w, h));
                for (int y = 0; y < h; y++)
                    for (int x = 0; x < Math.Min(w, 46); x++)
                    {
                        Color c = bmp.GetPixel(x, y);
                        zone[x, y, 0] = c.R; zone[x, y, 1] = c.G; zone[x, y, 2] = c.B;
                    }
            }
            return zone;
        }

        /// <summary>两帧灰度快照的平均差。静止贴图恒 0; 有动画的帧间必然 > 0。</summary>
        static double ZoneDiff(byte[, ,] a, byte[, ,] b)
        {
            long sum = 0; int n = 0;
            for (int x = 0; x < a.GetLength(0); x++)
                for (int y = 0; y < a.GetLength(1); y++)
                    for (int c = 0; c < 3; c++) { sum += Math.Abs(a[x, y, c] - b[x, y, c]); n++; }
            return n == 0 ? 0 : (double)sum / n;
        }
        /// <summary>读 GearButton 的私有 spinAngle (度), 取整 —— 判"在转 / 已归位"用。</summary>
        static int SpinAngle(object btn)
        {
            object v = Field(btn, "spinAngle");
            return v == null ? -1 : (int)Math.Round(Convert.ToSingle(v));
        }
    }
}
