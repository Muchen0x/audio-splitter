using System;
using System.Collections;
using System.Drawing;
using System.Reflection;
using System.Text;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>临时布局测量工具 (不进版本库): 实例化真实 MainForm, 打印控件实际边界。</summary>
    static class Dump
    {
        [STAThread]
        static void Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            // 设置页: 量分组卡片的内边距/重叠/溢出, 并验证"借出→归还"后状态没变
            if (args.Length >= 1 && args[0] == "settings")
            {
                var mf = new MainForm();
                mf.Show();
                for (int i = 0; i < 12; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(60); }

                Control cmb = Field(mf, "cmbLatency") as Control;
                Control drift = Field(mf, "chkDrift") as Control;
                Control tray = Field(mf, "chkMinimizeToTray") as Control;
                Control muteCap = Field(mf, "chkMuteCapture") as Control;
                int idx0 = cmb is ComboBox ? ((ComboBox)cmb).SelectedIndex : -1;
                bool d0 = drift is CheckBox && ((CheckBox)drift).Checked;
                bool t0 = tray is CheckBox && ((CheckBox)tray).Checked;
                bool m0 = muteCap is CheckBox && ((CheckBox)muteCap).Checked;
                Console.Out.WriteLine("BEFORE idx=" + idx0 + " drift=" + d0 + " tray=" + t0 + " muteCap=" + m0);

                MethodInfo scMethod = mf.GetType().GetMethod("SettingsControls", BindingFlags.Instance | BindingFlags.NonPublic);
                var arr = (Control[])scMethod.Invoke(mf, null);
                using (var sf = new SettingsForm(arr))
                {
                    sf.Show();
                    for (int i = 0; i < 10; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(50); }

                    var sb2 = new StringBuilder();
                    sb2.AppendLine("SETTINGS client=" + sf.ClientSize);
                    foreach (Control c in sf.Controls)
                    {
                        sb2.AppendLine("  " + c.GetType().Name + " '" + Trim(c.Text) + "' bounds=" + c.Bounds
                            + (c.Right > sf.ClientSize.Width ? "  <<< OVERFLOW " + (c.Right - sf.ClientSize.Width) : "")
                            + (c.Bottom > sf.ClientSize.Height ? "  <<< OVERFLOW-B " + (c.Bottom - sf.ClientSize.Height) : ""));
                        foreach (Control k in c.Controls)
                            sb2.AppendLine("      " + k.GetType().Name + " '" + Trim(k.Text) + "' bounds=" + k.Bounds
                                + (k.Right > c.Width ? "  <<< CLIPPED " + (k.Right - c.Width) : "")
                                + (k.Bottom > c.Height ? "  <<< CLIPPED-B " + (k.Bottom - c.Height) : ""));
                        if (c is SoftCard) DumpOverlaps(c, "card", sb2);
                    }
                    DumpOverlaps(sf, "form", sb2);
                    DumpTextFit(sf, "form", sb2);
                    Console.Out.Write(sb2.ToString());

                    sf.ReturnControls();
                }
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

                int idx1 = cmb is ComboBox ? ((ComboBox)cmb).SelectedIndex : -1;
                bool d1 = drift is CheckBox && ((CheckBox)drift).Checked;
                bool t1 = tray is CheckBox && ((CheckBox)tray).Checked;
                bool m1 = muteCap is CheckBox && ((CheckBox)muteCap).Checked;
                Console.Out.WriteLine("AFTER  idx=" + idx1 + " drift=" + d1 + " tray=" + t1 + " muteCap=" + m1
                    + " parent=" + (cmb.Parent == null ? "null" : cmb.Parent.GetType().Name)
                    + ((idx1 == idx0 && d1 == d0 && t1 == t0 && m1 == m0) ? "  (state kept, good)" : "  <<< STATE CHANGED"));
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
                var cmbSpecTest = Field(mf, "cmbSpec") as ComboBox;
                if (specPanel != null && cmbSpecTest != null && cmbSpecTest.Items.Count > 1)
                {
                    string sigBefore = specPanel.Tag as string;
                    int nBefore = specPanel.Controls.Count;
                    int oldIdx = cmbSpecTest.SelectedIndex;
                    cmbSpecTest.SelectedIndex = (oldIdx + 1) % cmbSpecTest.Items.Count;
                    for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
                    Console.Out.WriteLine("SPEC-CHANGE badges " + nBefore + " -> " + specPanel.Controls.Count
                        + " sigChanged=" + (sigBefore != (specPanel.Tag as string)));
                    cmbSpecTest.SelectedIndex = oldIdx;
                    Application.DoEvents();
                }
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

        /// <summary>向数字框的箭头区投一次真实的左键按下/抬起。</summary>
        static void ClickSpin(Control buttons, bool up)
        {
            if (buttons == null || !buttons.IsHandleCreated) return;
            int y = up ? 2 : Math.Max(2, buttons.ClientSize.Height - 3);
            IntPtr lp = (IntPtr)((y << 16) | 4);
            SendMessage(buttons.Handle, 0x0201, (IntPtr)1, lp);   // WM_LBUTTONDOWN
            SendMessage(buttons.Handle, 0x0202, (IntPtr)0, lp);   // WM_LBUTTONUP
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
    }
}
