using System;
using System.Collections.Generic;
using System.Drawing;
using System.Windows.Forms;

namespace StereoSplitter
{
    /// <summary>
    /// 「添加设备」的选择弹窗: 点「添加设备」不再直接加卡片, 而是先在这里选好
    /// **输出到哪台设备** 和 **负责哪个声道**, 确认后按所选建卡片。
    /// <para>设备下拉里已被其它卡片占用的设备显示成灰色 (选了会立刻并进那张卡片,
    /// 用户按"加一路"却看不到卡片变多, 容易以为坏了) —— 默认落在第一台没人用的设备上。</para>
    /// <para>样式与主界面同一套: 圆角下拉 + 描边圆角按钮, 没有系统灰按钮。</para>
    /// </summary>
    class AddOutputDialog : Form
    {
        // 次要操作 (取消) 用与下拉同一套黑色细边; 主操作 (添加) 保留强调蓝,
        // 它是这一屏的唯一主动作, 边与字同色才能一眼分出主次。

        readonly RoundedCombo cmbDevice = new RoundedCombo
        {
            Location = new Point(110, 16),
            Width = 200,
            Height = 26,
            ItemHeight = 24,
            MaxDropDownItems = 12
        };
        readonly RoundedCombo cmbChannel = new RoundedCombo
        {
            Location = new Point(110, 54),
            Width = 200,
            Height = 26,
            ItemHeight = 24,
            MaxDropDownItems = 12
        };
        readonly RoundButton btnOk;
        readonly RoundButton btnCancel;

        readonly string[] deviceIds;
        readonly List<KeyValuePair<string, string>> channels;

        /// <summary>确认后的选择: 设备 ID。取消时为 null。</summary>
        public string DeviceId { get; private set; }
        /// <summary>确认后的选择: 是否「多声道」形态。</summary>
        public bool IsMulti { get; private set; }
        /// <summary>确认后的选择: 单声道形态的逻辑声道键。</summary>
        public string ChannelKey { get; private set; }

        /// <param name="deviceTexts">设备下拉的显示文本 (与卡片设备下拉同一格式, 平行于 deviceIds)。</param>
        /// <param name="deviceIds">与 deviceTexts 平行的设备 ID。</param>
        /// <param name="usedDeviceIds">已被卡片占用的设备 ID (显示成灰色)。</param>
        /// <param name="defaultDeviceIdx">默认选中的设备下标 (第一台没人用的; -1 = 全被占用, 不预选)。</param>
        /// <param name="specChannels">规格声道清单 (Key → 显示名)。</param>
        /// <param name="defaultChannelKey">默认声道 (第一路没人接的)。</param>
        public AddOutputDialog(string[] deviceTexts, string[] deviceIds, HashSet<string> usedDeviceIds,
            int defaultDeviceIdx, List<KeyValuePair<string, string>> specChannels, string defaultChannelKey)
        {
            this.deviceIds = deviceIds;
            channels = specChannels;

            Text = "添加设备";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MinimizeBox = MaximizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            BackColor = Color.White;
            ClientSize = new Size(330, 158);
            Font = new Font("Microsoft YaHei UI", 9F);

            var lblDev = new Label { Text = "输出到设备:", Location = new Point(18, 21), AutoSize = true };
            var lblChan = new Label { Text = "负责声道:", Location = new Point(18, 59), AutoSize = true };

            cmbDevice.Items.AddRange(deviceTexts);
            cmbDevice.ItemGrayedFilter = idx =>
            {
                string id = idx >= 0 && idx < deviceIds.Length ? deviceIds[idx] : "";
                return usedDeviceIds.Contains(id ?? "");
            };
            if (defaultDeviceIdx >= 0 && defaultDeviceIdx < deviceTexts.Length)
                cmbDevice.SelectedIndex = defaultDeviceIdx;

            var chanItems = new List<object>();
            int multiIdx = -1, defaultIdx = -1;
            for (int i = 0; i < specChannels.Count; i++)
            {
                chanItems.Add(specChannels[i].Value);
                if (specChannels[i].Key == defaultChannelKey) defaultIdx = i;
            }
            chanItems.Add(LaneRouting.MultiText);
            multiIdx = chanItems.Count - 1;
            cmbChannel.Items.AddRange(chanItems.ToArray());
            cmbChannel.SelectedIndex = defaultIdx >= 0 ? defaultIdx : 0;

            btnOk = new RoundButton
            {
                Text = "添加",
                Location = new Point(ClientSize.Width - 18 - 76 - 8 - 76, ClientSize.Height - 40),
                Size = new Size(76, 28),
                Outline = true,
                FillColor = Color.White,
                BorderColor = Color.FromArgb(0x00, 0x78, 0xD7),
                TextColor = Color.FromArgb(0x00, 0x78, 0xD7)
            };
            btnOk.Click += (s, e) => Confirm();

            btnCancel = new RoundButton
            {
                Text = "取消",
                Location = new Point(ClientSize.Width - 18 - 76, ClientSize.Height - 40),
                Size = new Size(76, 28),
                Outline = true,
                FillColor = Color.White,
                BorderColor = Color.Black,
                TextColor = Color.FromArgb(64, 64, 64)
            };
            btnCancel.Click += (s, e) => { DialogResult = DialogResult.Cancel; };

            Controls.AddRange(new Control[] { lblDev, cmbDevice, lblChan, cmbChannel, btnOk, btnCancel });
            AcceptButton = btnOk;
            CancelButton = btnCancel;
            // 与主窗口同一套 DPI 放大 (控件都按 96 DPI 摆好后, 在 Load 里整体乘一次)
            Load += (s2, e2) => Dpi.ScaleForm(this);
        }

        void Confirm()
        {
            int di = cmbDevice.SelectedIndex;
            if (di < 0 || di >= deviceIds.Length)
            {
                MessageBox.Show(this, "请选择一台输出设备。", Text, MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            DeviceId = deviceIds[di];
            int ci = cmbChannel.SelectedIndex;
            if (ci >= 0 && ci < cmbChannel.Items.Count &&
                (string)cmbChannel.Items[ci] == LaneRouting.MultiText)
            {
                IsMulti = true;
                ChannelKey = null;
            }
            else
            {
                IsMulti = false;
                ChannelKey = ci >= 0 && ci < channels.Count ? channels[ci].Key : null;
            }
            DialogResult = DialogResult.OK;
        }
    }
}
