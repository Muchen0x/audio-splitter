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
            // CopyFromScreen 按物理像素取屏: 不声明 DPI 感知的话, 高缩放屏上窗口会被系统
            // 虚拟化放大, 按 Location/Size 去截只能截到左上角一块 (live/icons 全军覆没)。
            try { SetProcessDPIAware(); } catch { }
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
                int idx0 = SelOf(cmb);
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

                    // 借用期间故意改一下延迟档位: 归还时必须被还原成借用前的值,
                    // 否则"打开设置页 → 关掉 → 档位莫名其妙变了"这类问题看不出来
                    int nItems = Math.Max(1, ItemCountOf(cmb));
                    SetSel(cmb, (SelOf(cmb) + 1) % nItems);
                    Application.DoEvents();
                    Console.Out.WriteLine("DISTURB idx=" + SelOf(cmb) + " (借用期间改值, 归还后应回到 " + idx0 + ")");

                    sf.ReturnControls();
                }
                for (int i = 0; i < 6; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(40); }

                int idx1 = SelOf(cmb);
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
                RoundButton btn = Field(sf2, "btnStart") as RoundButton;
                if (setState == null || btn == null) { Console.Out.WriteLine("STATES unavailable"); return; }

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
                        Application.DoEvents();
                        Console.Out.WriteLine("STATE " + AppStateMachine.Text(states[i])
                            + " text='" + btn.Text + "' enabled=" + btn.Enabled
                            + " fill=" + btn.FillColor.Name + " fillDisabled=" + btn.FillDisabled.Name);
                        using (var one = new Bitmap(bw, bh))
                        {
                            // 先铺白: RoundButton 用 Region 裁了圆角, 四个角不在绘制区内,
                            // DrawToBitmap 会把它留成未初始化的黑色 —— 看着像"角上有灰块"
                            using (var og = System.Drawing.Graphics.FromImage(one)) og.Clear(Color.White);
                            btn.DrawToBitmap(one, new Rectangle(0, 0, bw, bh));
                            g.DrawImage(one, padX + i * (bw + gap), padX);
                        }
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
            // 弹层必须与本体等宽。窗口四周多了一圈自绘阴影 (ShadowPad), 所以要减掉再看内容宽
            int shadow = RoundedCombo.ShadowPad;
            bool sameW = ts.Width - shadow * 2 == Math.Max(combo.Width, combo.DropDownWidth);
            // 本体文字区够不够放下当前选中项 —— 60px 宽的下拉最容易在这里翻车 (直接看界面只是
            // 觉得"怎么是省略号", 量出来才知道差几像素)
            int textRoom = combo.Width - RoundedCombo.TextPadX - RoundedCombo.ArrowZone;
            int selW = TextRenderer.MeasureText(combo.SelectedText ?? "", combo.Font).Width;
            Console.Out.WriteLine("COMBO[" + label + "] items=" + combo.Items.Count
                + " rows=" + rows + " rowH=" + combo.ItemHeight
                + " scroll=" + (combo.Items.Count > rows ? "yes" : "no")
                + " popup=" + ts.Size
                + " shadowPad=" + shadow
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
        static void Pump()
        {
            for (int i = 0; i < 4; i++) { Application.DoEvents(); System.Threading.Thread.Sleep(30); }
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
