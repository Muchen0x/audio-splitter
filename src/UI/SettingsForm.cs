using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 设置页: 收纳原本挤在主界面顶部的选项 —— 缓冲档位、自动时钟校正、捕获端静音锁定、
    /// 关闭行为、延迟校准入口。
    ///
    /// <para><b>控件是"借"来的, 不是新建的。</b>主界面仍持有那几个控件实例, 它们的事件绑定、
    /// <c>ApplyUiLock</c> 的启停规则、<c>LoadSettings</c>/<c>SaveSettings</c> 的读写路径全部原样
    /// 保留; 这里只改父容器, 关闭时再搬回去。所以"在设置页改一下"和"以前在主界面点一下"
    /// 走的是同一条代码路径, 不存在两套状态需要互相同步。</para>
    ///
    /// <para>搬出前必须快照控件自身状态、搬回后校正: 换父容器会销毁并重建子窗口句柄,
    /// ComboBox 的选中项在个别路径上会被重置成 -1 (选 -1 就等于悄悄改了延迟档位)。</para>
    /// </summary>
    sealed class SettingsForm : Form
    {
        /// <summary>借来的一个控件 + 它搬出前的状态快照。</summary>
        sealed class Borrowed
        {
            public Control C;
            public bool IsCheck;
            public int ComboIndex = int.MinValue;
            public bool Checked;
        }

        readonly List<Borrowed> borrowed = new List<Borrowed>();
        bool returned;

        readonly Font titleFont = new Font("Microsoft YaHei UI", 10F);
        readonly Font hintFont = new Font("Microsoft YaHei UI", 8.25F);
        // R-02: 窗体基准字体也收成字段, 才能在 Dispose 里释放 (原先直接 Font = new Font(...))
        readonly Font formFont = new Font("Microsoft YaHei UI", 9F);
        static readonly Color HintColor = Color.FromArgb(120, 126, 136);
        static readonly Color TitleColor = Color.FromArgb(44, 44, 42);

        const int PadX = 16;
        const int CardW = 448;

        /// <param name="controls">顺序与 <see cref="MainForm.SettingsControls"/> 严格对应。</param>
        public SettingsForm(Control[] controls)
        {
            Text = "设置 - Audio Splitter";
            Font = formFont;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = SystemColors.Control;
            ClientSize = new Size(480, 542);
            KeyPreview = true;

            // Esc 关闭: 设置页没有"取消"语义 (改动即时生效), 所以 Esc 与"关闭"等价
            KeyDown += (s, e) => { if (e.KeyCode == Keys.Escape) Close(); };

            BuildUi(controls);
        }

        void BuildUi(Control[] c)
        {
            // c 的顺序与 MainForm.SettingsControls() 严格对应
            Control lblBuf = c != null && c.Length > 0 ? c[0] : null;   // "缓冲档位:" 标签
            Control cmbBuf = c != null && c.Length > 1 ? c[1] : null;   // 缓冲档位下拉
            Control chkDrift = c != null && c.Length > 2 ? c[2] : null; // 自动时钟校正
            Control chkMuteCap = c != null && c.Length > 3 ? c[3] : null;
            Control chkTray = c != null && c.Length > 4 ? c[4] : null;
            Control btnCalib = c != null && c.Length > 5 ? c[5] : null;

            // —— 1) 音频链路 ——
            SoftCard card1 = NewCard(14, 158);
            AddTitle(card1, "音频链路");
            if (lblBuf != null)
            {
                // 主界面那个"延迟档位:"标签一并借过来, 文字换成设置页的说法
                lblBuf.Text = "缓冲档位:";
                Borrow(lblBuf, card1, 14, 40, 0, 0);
            }
            else AddLabel(card1, "缓冲档位:", 14, 40);
            if (cmbBuf != null) Borrow(cmbBuf, card1, 88, 36, 252, 0);
            AddHint(card1, "越低延迟越小, 但对设备抖动越敏感; 运行中不可改, 需先停止分流。", 14, 66, 34);

            if (chkDrift != null)
            {
                chkDrift.Text = "自动时钟校正";
                Borrow(chkDrift, card1, 14, 100, 0, 0);
            }
            AddHint(card1, "微调每路的重采样比例抵消晶振差异: 防止长时间播放后延迟漂移、或水位耗尽断音。",
                14, 124, 30);

            // —— 2) 捕获监听 ——
            SoftCard card2 = NewCard(184, 88);
            AddTitle(card2, "捕获监听");
            if (chkMuteCap != null)
            {
                chkMuteCap.Text = "锁定捕获端静音";
                Borrow(chkMuteCap, card2, 14, 36, 0, 0);
            }
            AddHint(card2, "开启后被外部取消静音会自动恢复, 避免声音从原设备漏出。", 14, 58, 20);

            // —— 3) 窗口行为 ——
            SoftCard card3 = NewCard(284, 88);
            AddTitle(card3, "窗口行为");
            if (chkTray != null)
            {
                chkTray.Text = "关闭窗口时收进系统托盘";
                Borrow(chkTray, card3, 14, 36, 0, 0);
            }
            AddHint(card3, "开启后点关闭只隐藏窗口、分流继续; 双击托盘图标恢复。", 14, 58, 20);

            // —— 4) 校准工具 ——
            SoftCard card4 = NewCard(384, 104);
            AddTitle(card4, "校准工具");
            if (btnCalib != null)
            {
                btnCalib.Text = "延迟校准…";
                Borrow(btnCalib, card4, 14, 34, 152, 30);
            }
            AddHint(card4, "用麦克风实测各路声音到达耳朵的时间差并自动补齐, 适合有线 + 无线混用对不齐。\r\n" +
                "需要一支可用麦克风; 校准会独占输出端点, 请先停止分流。", 14, 72, 30);

            // 借来的标签 (原主界面上的"延迟档位:") 已在组 1 开头处理, 这里不再自建

            // —— 底部 ——
            var btnClose = new NoCueButton
            {
                Text = "关闭",
                Location = new Point(ClientSize.Width - PadX - 96, 500),
                Width = 96,
                Height = 30
            };
            btnClose.Click += (s, e) => Close();
            Controls.Add(btnClose);

            var lblFoot = new Label
            {
                Text = "改动立即生效并自动保存",
                Location = new Point(PadX, 508),
                AutoSize = true,
                ForeColor = HintColor,
                Font = hintFont,
                BackColor = SystemColors.Control
            };
            Controls.Add(lblFoot);
        }

        SoftCard NewCard(int y, int h)
        {
            var card = new SoftCard { Location = new Point(PadX, y), Size = new Size(CardW, h) };
            Controls.Add(card);
            return card;
        }

        void AddTitle(Control card, string text)
        {
            card.Controls.Add(new Label
            {
                Text = text,
                Location = new Point(14, 10),
                AutoSize = true,
                Font = titleFont,
                ForeColor = TitleColor,
                BackColor = Color.White
            });
        }

        void AddLabel(Control card, string text, int x, int y)
        {
            card.Controls.Add(new Label
            {
                Text = text,
                Location = new Point(x, y + 3),
                AutoSize = true,
                BackColor = Color.White
            });
        }

        void AddHint(Control card, string text, int x, int y, int h)
        {
            card.Controls.Add(new Label
            {
                Text = text,
                Location = new Point(x, y),
                Size = new Size(CardW - x - 14, h),
                Font = hintFont,
                ForeColor = HintColor,
                BackColor = Color.White
            });
        }

        /// <summary>把主界面持有的控件搬进本页, 并记下搬出前的状态。</summary>
        void Borrow(Control control, Control host, int x, int y, int w, int h)
        {
            var b = new Borrowed { C = control, IsCheck = control is CheckBox };
            ComboBox combo = control as ComboBox;
            if (combo != null) b.ComboIndex = combo.SelectedIndex;
            CheckBox chk = control as CheckBox;
            if (chk != null) b.Checked = chk.Checked;

            if (control.Parent != null) control.Parent.Controls.Remove(control);
            control.Location = new Point(x, y);
            if (w > 0) control.Width = w;
            if (h > 0) control.Height = h;
            host.Controls.Add(control);

            borrowed.Add(b);
        }

        /// <summary>
        /// 把借来的控件原样还给主界面 (Parent 置空) 并校正状态。
        /// <para>幂等: FormClosing 与调用方各调一次都安全。**必须在本窗体 Dispose 之前完成**,
        /// 否则这些控件会随父窗体一起被销毁, 主界面再引用就是访问已释放对象。</para>
        /// </summary>
        public void ReturnControls()
        {
            if (returned) return;
            returned = true;

            foreach (Borrowed b in borrowed)
            {
                Control c = b.C;
                if (c == null || c.IsDisposed) continue;
                if (c.Parent != null) c.Parent.Controls.Remove(c);

                ComboBox combo = c as ComboBox;
                if (combo != null && b.ComboIndex != int.MinValue && combo.SelectedIndex != b.ComboIndex)
                    combo.SelectedIndex = b.ComboIndex;
                CheckBox chk = c as CheckBox;
                if (chk != null && b.IsCheck && chk.Checked != b.Checked) chk.Checked = b.Checked;
            }
            borrowed.Clear();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            ReturnControls();
            base.OnFormClosing(e);
        }

        /// <summary>
        /// 两件事放进同一个 Dispose, 顺序不能调:
        /// <list type="number">
        /// <item>控件必须**先**return 回主界面再 base.Dispose —— 这些控件是本窗体临时收养的,
        /// 跟着一起销毁就是把主界面拆了。即使调用方没来得及搬回也要兜底。</item>
        /// <item>字体必须**后**放 —— base.Dispose 让子控件走完销毁流程, 它们还要用 Font 量文字。
        /// 设置页每打开一次就是一个新实例 (见 MainForm.OpenSettings), 而 <c>Control.Dispose</c>
        /// **不**释放 Font (只释放自己缓存的 HFONT), 不管就是每开一次漏三个 GDI 句柄。</item>
        /// </list>
        /// </summary>
        protected override void Dispose(bool disposing)
        {
            // 兜底: 即使调用方没来得及搬回, 也不能让主界面的控件跟着一起被销毁
            if (disposing) ReturnControls();
            base.Dispose(disposing);
            if (!disposing) return;
            try { formFont.Dispose(); } catch { }
            try { titleFont.Dispose(); } catch { }
            try { hintFont.Dispose(); } catch { }
        }
    }
}
