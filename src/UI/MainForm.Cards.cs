using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using NAudio.Wave;
using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;
using StereoSplitter.Devices;

namespace StereoSplitter
{
    partial class MainForm
    {
        // ==================== 规格与输出卡片 ====================

        /// <summary>当前规格的声道清单 (键, 显示名)。键与 Windows 扬声器位对应。
        /// 表在 LaneRouting 里 (内核翻译与界面共用同一张, 免得两边各写一套)。</summary>
        List<KeyValuePair<string, string>> CurrentSpecChannels()
        {
            return LaneRouting.SpecChannels(cmbSpec.SelectedIndex);
        }

        string SpecText()
        {
            string[] names = { "单声道", "立体声", "2.1", "四声道", "5.1" };
            int i = cmbSpec.SelectedIndex;
            return (i >= 0 && i < names.Length) ? names[i] : "立体声";
        }

        string SpecChannelNames()
        {
            var chans = CurrentSpecChannels();
            var names = new List<string>();
            for (int i = 0; i < chans.Count; i++) names.Add(chans[i].Value);
            return string.Join("/", names.ToArray());
        }

        string ChannelNameOf(string key)
        {
            var chans = CurrentSpecChannels();
            for (int i = 0; i < chans.Count; i++) if (chans[i].Key == key) return chans[i].Value;
            return key;
        }

        /// <summary>含该声道的卡片 (多声道卡片里任意一行算)</summary>
        OutputCardControl CardWithChannel(string key)
        {
            if (string.IsNullOrEmpty(key)) return null;
            for (int i = 0; i < cards.Count && i < models.Count; i++)
                for (int s = 0; s < models[i].Slots.Count; s++)
                    if (models[i].Slots[s].ChannelKey == key) return cards[i];
            return null;
        }

        /// <summary>左输出卡片: 含 FL 的卡片 (单声道规格时 M)。</summary>
        OutputCardControl LeftCard()
        {
            return CardWithChannel("FL") ?? CardWithChannel("M") ?? (cards.Count > 0 ? cards[0] : null);
        }

        /// <summary>右输出卡片: 含 FR 的另一张; 都不行则取与左不同的第一张。</summary>
        OutputCardControl RightCard()
        {
            OutputCardControl l = LeftCard();
            OutputCardControl r = CardWithChannel("FR");
            if (r != null && r != l) return r;
            for (int i = 0; i < cards.Count; i++) if (cards[i] != l) return cards[i];
            return l;
        }

        string SafeDeviceId(int idx)
        {
            return (idx >= 0 && catalog != null) ? catalog.IdAt(idx) : "";
        }

        string LeftDeviceId()
        {
            OutputCardControl c = LeftCard();
            return c == null ? "" : SafeDeviceId(c.cmbDevice.SelectedIndex);
        }

        string RightDeviceId()
        {
            OutputCardControl c = RightCard();
            return c == null ? "" : SafeDeviceId(c.cmbDevice.SelectedIndex);
        }

        string LaneSummary()
        {
            var parts = new List<string>();
            for (int i = 0; i < cards.Count && i < models.Count; i++)
            {
                string name = "未选";
                int idx = cards[i].cmbDevice.SelectedIndex;
                if (idx >= 0 && idx < Devices.Count) name = Devices[idx].FriendlyName;
                parts.Add(CardRole(models[i]) + "->" + name);
            }
            return string.Join(" ", parts.ToArray());
        }

        /// <summary>
        /// 控件 → 卡片模型 (控件是权威源) → 内核 lane 列表。
        /// <para>lane 是派生值: 一张卡片可能产生多行 (多声道), 也可能一行都不产生 (空声道)。</para>
        /// </summary>
        void SyncLanesFromCards()
        {
            var chans = CurrentSpecChannels();
            for (int i = 0; i < cards.Count && i < models.Count; i++)
            {
                cards[i].WriteBackToModel(chans);
                int idx = cards[i].SelectedDeviceIndex;
                // F-07: 占位项(索引 ≥ 真实设备数)表示"已保存设备当前不可用" → 保留原 ID，不写空、不换设备
                if (idx >= Devices.Count && !string.IsNullOrEmpty(models[i].DeviceId)) continue;
                models[i].DeviceId = SafeDeviceId(idx);
            }
            RebuildLanes();
        }

        /// <summary>卡片模型 → 内核 lane 列表 (音量已折算成矩阵增益)。</summary>
        void RebuildLanes()
        {
            lanes.Clear();
            lanes.AddRange(LaneRouting.Flatten(models, EndpointVolumeAvailable));
        }

        /// <summary>
        /// 给卡片填设备下拉并恢复选择 (F-07)。保存的设备 ID 当前没枚举到时，不自动换成别的设备，
        /// 而是在末尾加一个占位项并选中它 —— 这样模型里的 DeviceId 原样保留，等 USB/蓝牙设备
        /// 完成枚举或重新插入后刷新即可恢复，不会被静默改写、也不会因落到同一设备被误合并。
        /// </summary>
        void SetCardDeviceSelection(OutputCardControl card, OutputCardModel model, IList<string> devTexts)
        {
            card.SetDevices(devTexts);
            int idx = catalog == null ? -1 : catalog.IndexOf(model.DeviceId);
            if (idx < 0 && !string.IsNullOrEmpty(model.DeviceId))
            {
                // 占位项索引 = 真实设备数，超出 catalog 范围 → SyncLanesFromCards 据此保留模型 ID
                card.AppendMissingDeviceItem("[已保存设备当前不可用] " + DevNameFor(model.DeviceId));
                card.SetDeviceIndex(devTexts.Count);
                return;
            }
            card.SetDeviceIndex(idx);
        }

        /// <summary>该设备的端点音量是否可用 (绑不上时音量改走混音增益)。</summary>
        bool EndpointVolumeAvailable(string deviceId)
        {
            return !string.IsNullOrEmpty(deviceId) && !noEndpointVolumeDevices.Contains(deviceId);
        }

        /// <summary>卡片的显示角色 (日志/兼容键用): 单声道 → 声道名; 多声道 → "N 声道(…)";</summary>
        string CardRole(OutputCardModel m)
        {
            if (m == null) return "?";
            if (!m.Multi)
                return m.Slots.Count > 0 ? ChannelNameOf(m.Slots[0].ChannelKey) : "未选声道";
            var names = new List<string>();
            for (int i = 0; i < m.Slots.Count; i++)
                if (!string.IsNullOrEmpty(m.Slots[i].ChannelKey)) names.Add(ChannelNameOf(m.Slots[i].ChannelKey));
            return m.Slots.Count + " 声道" + (names.Count > 0 ? "(" + string.Join("/", names.ToArray()) + ")" : "");
        }

        string DevNameFor(string id)
        {
            // 构造期 catalog 尚未建立 (LoadSettings→LoadLanes→RebuildCards 是启动路径)：
            // 保存的设备 ID 此时解析不出友好名，退回 ID 本身，避免 F-07 占位项触发空引用。
            if (catalog == null)
                return string.IsNullOrEmpty(id) ? "(未选设备)" : id;
            string n = FindNameById(id);
            return string.IsNullOrEmpty(n) ? "(未选设备)" : n;
        }

        string DescribeCards()
        {
            var parts = new List<string>();
            for (int i = 0; i < models.Count; i++)
                parts.Add(CardRole(models[i]) + "→" + DevNameFor(models[i].DeviceId));
            return string.Join(" ", parts.ToArray());
        }

        // ==================== 合并与行数 ====================

        /// <summary>
        /// 模型规范化: 同设备的多张卡片合并成一张多声道卡片 + 校正多声道卡片的行数。
        /// <returns>true = 卡片集合/形态变了, 调用方需要重建界面。</returns>
        /// </summary>
        bool NormalizeModels()
        {
            if (normalizing) return false;
            normalizing = true;
            try
            {
                bool changed = false;

                // 1. 行数: 多声道卡片 = 该设备物理声道数, 单声道卡片 = 1 行
                for (int i = 0; i < models.Count; i++)
                    if (EnsureSlots(models[i])) changed = true;

                // 2. 合并同设备。合并前先记下哪几台设备是重复的 —— 只有它们的主音量需要重新取初值
                var dup = new List<string>();
                var seen = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < models.Count; i++)
                {
                    string d = models[i].DeviceId ?? "";
                    if (d.Length > 0 && !seen.Add(d) && !dup.Contains(d)) dup.Add(d);
                }

                int merged = LaneRouting.MergeSameDevice(models, EndpointVolumeAvailable);
                if (merged > 0)
                {
                    changed = true;
                    for (int i = 0; i < models.Count; i++)
                    {
                        if (!models[i].Multi || !dup.Contains(models[i].DeviceId ?? "")) continue;
                        string devId = models[i].DeviceId ?? "";
                        EnsureSlots(models[i]);
                        if (EndpointVolumeAvailable(devId))
                        {
                            // 端点可用 → 主音量接管设备端点，取端点当前值保证"合并不改变响度"
                            int before = models[i].Volume;
                            models[i].Volume = ReadEndpointPercent(devId, before);
                            models[i].Mute = false;
                            Log("[输出] " + DevNameFor(devId) + " 的主音量取端点当前值 " +
                                models[i].Volume + "%（合并前 " + before + "%，设备端点音量从此由这张卡片控制）");
                        }
                        else
                        {
                            // F-08: 端点不可用 → 各卡主音量(含静音)已在 MergeInto 折进对应行增益,
                            // 主音量已是中性的 100/Mute=false, 不能再读端点也不再覆盖, 否则静音路会突然发声。
                            Log("[输出] " + DevNameFor(devId) +
                                " 端点音量不可用，各卡音量（含静音）已折进各自声道行增益，合并响度无损");
                        }
                    }
                    Log("[输出] 同设备的多张卡片已合并为多声道卡片: " + DescribeCards());
                }

                if (changed) RebuildLanes();
                return changed;
            }
            finally { normalizing = false; }
        }

        /// <summary>
        /// 行数对齐: 多声道卡片 = 该设备物理声道数 (读不到按规格声道数), 单声道卡片 = 1 行。
        /// <returns>true = 行数变了。</returns>
        /// </summary>
        bool EnsureSlots(OutputCardModel m)
        {
            int want = m.Multi ? DeviceChannelCount(m.DeviceId) : 1;
            bool changed = false;
            if (m.Slots.Count > want)
            {
                int dropped = 0;
                for (int i = want; i < m.Slots.Count; i++)
                    if (!string.IsNullOrEmpty(m.Slots[i].ChannelKey)) dropped++;
                m.Slots.RemoveRange(want, m.Slots.Count - want);
                if (dropped > 0)
                    Log("[输出] " + DevNameFor(m.DeviceId) + " 的可用声道减少，丢弃了 " + dropped + " 个多声道行");
                changed = true;
            }
            while (m.Slots.Count < want)
            {
                m.Slots.Add(new ChannelSlot { ChannelKey = NextFreeChannelKey(m), Volume = 100 });
                changed = true;
            }
            return changed;
        }

        /// <summary>该设备能用的物理声道数 (读不到就按当前规格的声道数)。</summary>
        int DeviceChannelCount(string deviceId)
        {
            int n = EndpointChannels(deviceId);
            if (n <= 0) n = CurrentSpecChannels().Count;
            return Math.Max(1, Math.Min(8, n));
        }

        /// <summary>卡片里下一个还没用到的规格声道; 用完了返回 "" (空行)。</summary>
        string NextFreeChannelKey(OutputCardModel m)
        {
            var chans = CurrentSpecChannels();
            for (int i = 0; i < chans.Count; i++)
            {
                bool used = false;
                for (int k = 0; k < m.Slots.Count; k++)
                    if (m.Slots[k].ChannelKey == chans[i].Key) { used = true; break; }
                if (!used) return chans[i].Key;
            }
            return "";
        }

        /// <summary>读某设备端点当前音量 (合并卡片时给主音量取初值)。失败返回 fallback。</summary>
        int ReadEndpointPercent(string deviceId, int fallback)
        {
            if (catalog == null || string.IsNullOrEmpty(deviceId)) return fallback;
            MMDevice d = null;
            EndpointVolumeBinding b = null;
            try
            {
                d = catalog.Open(deviceId);
                if (d == null) return fallback;
                b = new EndpointVolumeBinding(d, "读取", Log);
                return b.IsValid ? Math.Max(0, Math.Min(100, (int)Math.Round(b.ReadVolume() * 100))) : fallback;
            }
            catch { return fallback; }
            finally
            {
                // EndpointVolumeBinding 自己释放它持有的 MMDevice; 绑定没建起来才由这里释放
                if (b != null) { try { b.Dispose(); } catch { } }
                else if (d != null) { try { d.Dispose(); } catch { } }
            }
        }

        /// <summary>
        /// 声道键必须落在当前规格里。force=true 时按顺序重排 (换规格用), 规格声道用完之后的行置空。
        /// </summary>
        void ReassignLanesToSpec(bool force)
        {
            var chans = CurrentSpecChannels();
            if (chans.Count == 0) return;
            var used = new HashSet<string>(StringComparer.Ordinal);

            for (int i = 0; i < models.Count; i++)
            {
                OutputCardModel m = models[i];
                int n = Math.Max(1, m.Slots.Count);
                for (int s = 0; s < n; s++)
                {
                    if (s >= m.Slots.Count) m.Slots.Add(new ChannelSlot());
                    ChannelSlot slot = m.Slots[s];
                    bool ok = false;
                    for (int k = 0; k < chans.Count; k++)
                        if (chans[k].Key == slot.ChannelKey) { ok = true; break; }
                    if (!force && ok) { used.Add(slot.ChannelKey); continue; }

                    string next = "";
                    for (int k = 0; k < chans.Count; k++)
                        if (!used.Contains(chans[k].Key)) { next = chans[k].Key; break; }
                    // 单声道卡片不能没有声道 (否则整张卡不出声), 规格排完了就复用第一个
                    if (next.Length == 0 && !m.Multi) next = chans[0].Key;
                    slot.ChannelKey = next;
                    if (next.Length > 0) used.Add(next);
                }
            }
        }

        void RebuildCards()
        {
            for (int i = 0; i < cards.Count; i++)
            {
                cards[i].Changed -= OnCardChanged;
                cards[i].VolumeScrolled -= OnCardVolumeScrolled;
                cards[i].RemoveRequested -= OnCardRemoveRequested;
                cards[i].ShapeBlocked -= OnCardShapeBlocked;
                cardHost.Controls.Remove(cards[i]);
                cards[i].Dispose();
            }
            cards.Clear();

            var chans = CurrentSpecChannels();
            var devTexts = new List<string>();
            for (int i = 0; i < Devices.Count; i++) devTexts.Add("[" + i + "] " + Devices[i].FriendlyName);

            // 新建卡片先按"当前卡片数"该有的栅格摆好, 免得先按满宽建再被 FitCardWidths 挤一次
            int gridCols, gridW;
            ComputeCardGrid(models.Count, out gridCols, out gridW);
            var gridMargin = new Padding(0, 0, gridCols > 1 ? CardGapX : 0, OutputCardControl.CardGap);

            for (int i = 0; i < models.Count; i++)
            {
                OutputCardModel m = models[i];
                var card = new OutputCardControl(m);
                SetCardDeviceSelection(card, m, devTexts);
                card.SetChannels(chans);
                // 行控件要先建出来, SyncFromModel 才填得进值
                card.SetMulti(m.Multi, m.Multi ? m.Slots.Count : 0, chans);
                card.SyncFromModel(chans);
                card.Changed += OnCardChanged;
                card.VolumeScrolled += OnCardVolumeScrolled;
                card.RemoveRequested += OnCardRemoveRequested;
                card.ShapeBlocked += OnCardShapeBlocked;
                // 卡片自带圆角外框: 竖向靠紧 (间隙 = Margin.Bottom), 横向留 CardGapX 供换列
                card.Margin = gridMargin;
                card.Width = gridW;
                cardHost.Controls.Add(card);
                cards.Add(card);
            }
            // 逐个加入过程中滚动条可能刚好切换, 统一再拉一次;
            // 滚动条的最终显隐要等布局跑完, 故再用 BeginInvoke 兜一次。
            lastCardCols = -1;                      // 卡片是新控件: 强制重排一次
            FitCardWidths();
            if (IsHandleCreated && !IsDisposed)
                BeginInvoke((Action)FitCardWidths);
            ApplyUiLock();          // 新建的卡片要继承当前锁定状态(运行中/启动中)
            RefreshSpecBadges();
        }

        /// <summary>
        /// 分隔条拖动不设上限 (`Panel1MinSize = Panel2MinSize = 0`), 两个面板都能被拖到没有;
        /// 但**窗口整体变矮**时不能把内容区缩没: 一旦内容区被压到 284 以下 (界面上只剩日志),
        /// 就把它拽回 284。只在"高度变小"时触发, 用户主动往上拖分隔条不受这条规则干扰。
        /// </summary>
        void KeepContentOnShrink()
        {
            if (splitContainer == null) return;
            int h = splitContainer.Height;
            bool shrinking = lastSplitH > 0 && h < lastSplitH;
            lastSplitH = h;
            if (!shrinking) return;

            const int minContent = 284;
            int dist = splitContainer.SplitterDistance;
            if (dist >= minContent) return;
            int maxDist = h - splitContainer.Panel2MinSize - splitContainer.SplitterWidth;
            if (maxDist <= dist) return;                 // 窗口矮到连内容区都放不下: 只能这样
            int want = Math.Min(minContent, maxDist);
            if (want <= dist) return;
            try { splitContainer.SplitterDistance = want; } catch { }
        }

        /// <summary>
        /// 日志框按日志面板的实时尺寸摆放: 左边距 15, 右边距 10 (文本框自己的竖滚动条
        /// 就贴在面板右缘), 下面距 8。拖分隔条改日志区高度时同步跟随。
        /// </summary>
        void LayoutLogPanel()
        {
            if (txtLog == null || splitContainer == null) return;
            Size cs = splitContainer.Panel2.ClientSize;
            // 尺寸没变就别再 SetBounds: 拖分隔条时这个回调每帧都来,
            // 每次都改一次 bounds 会连带整块日志框重排重画。
            if (cs == lastLogPanelSize) return;
            lastLogPanelSize = cs;
            int w = Math.Max(120, cs.Width - 15 - 10);
            // 高下限设 0: 分隔条现在可以一路把日志区压到没有, 日志框跟着缩到 0 而不是留着
            // 40px 高度溢出到面板外面 (Panel 会裁剪, 但多出来的高度仍会参与滚动条计算)
            int h = Math.Max(0, cs.Height - 30 - 8);
            txtLog.SetBounds(15, 30, w, h);
        }

        /// <summary>
        /// 增益/主音量两行的自适应布局 (与输出卡片里的音量条同理): 滑条右端跟着面板宽度走,
        /// 右起固定为 [静音][10][百分比][8][滑条]。
        /// 增益行没有静音框, 但预留同样的宽度 —— 两条滑条等长、百分比数字竖向对齐。
        /// </summary>
        void LayoutVolumeRows()
        {
            if (volMaster == null || chkMuteMaster == null) return;
            Control host = volMaster.Parent;
            if (host == null) return;

            int panelW = host.ClientSize.Width;
            // 宽度没变 (改高度/拖分隔条也会走到这里) → 摆出来的结果不会变, 直接跳过
            if (panelW == lastVolPanelW) return;
            lastVolPanelW = panelW;
            const int leftEdge = 78;    // 滑条左端 = "增益:"/"主音量:" 标签右缘
            const int rightEdge = 10;   // 右端留白, 与卡片区/日志框右缘一致
            int muteW = chkMuteMaster.Width;
            int pctW = lblVolMaster.Width;

            chkMuteMaster.Location = new Point(panelW - rightEdge - muteW, 106);
            int pctX = chkMuteMaster.Left - 10 - pctW;
            int volW = Math.Max(120, pctX - 8 - leftEdge);

            volGain.SetBounds(leftEdge, 72, volW, 20);
            lblVolGain.Location = new Point(pctX, 72);
            volMaster.SetBounds(leftEdge, 106, volW, 20);
            lblVolMaster.Location = new Point(pctX, 106);
        }

        /// <summary>AutoSize 控件取宽: 用 PreferredSize (不依赖当前布局), 构造期也能算准。</summary>
        static int PreferredW(Control c)
        {
            return c.AutoSize ? c.PreferredSize.Width : c.Width;
        }

        /// <summary>
        /// 顶部三行 (捕获 / 延迟档位 / 规格) 的自适应布局。
        /// <para>原本是按 600 宽硬编码的固定坐标 (刷新设备右缘 489、添加输出 574),
        /// 窗口缩到 500 宽 (客户区约 484) 时右侧控件会被挤出客户区截断。
        /// 这里改为「右侧控件贴面板右缘、中间下拉吃掉剩余宽度」, 任意宽度都不越界。</para>
        /// </summary>
        void LayoutTopRows()
        {
            if (lblD == null || btnRefresh == null || btnAddOutput == null) return;
            Control host = btnRefresh.Parent;
            if (host == null) return;

            Size panelSize = host.ClientSize;
            if (panelSize == lastTopPanelSize) return;   // 尺寸没变就不重排 (拖分隔条会频繁触发)
            lastTopPanelSize = panelSize;
            int panelW = panelSize.Width;
            const int rightEdge = 10;               // 与卡片区/日志框右缘一致的留白
            const int gap = 8;

            // —— 第 1 行: [捕获(虚拟):] [下拉] [消音] [刷新设备(贴右)] ——
            btnRefresh.Left = panelW - rightEdge - btnRefresh.Width;
            chkMuteCapture.Left = btnRefresh.Left - gap - PreferredW(chkMuteCapture);
            cmbDefault.Width = Math.Max(60, chkMuteCapture.Left - gap - cmbDefault.Left);

            // —— 第 2 行: [延迟档位:] [下拉] [关闭最小化] [校正(贴右)] ——
            chkDrift.Left = panelW - rightEdge - PreferredW(chkDrift);
            chkMinimizeToTray.Left = chkDrift.Left - gap - PreferredW(chkMinimizeToTray);
            cmbLatency.Width = Math.Max(60, chkMinimizeToTray.Left - gap - cmbLatency.Left);

            // —— 第 3 行 (规格): [规格:] [下拉] [声道名…] [添加输出(贴右)] ——
            btnAddOutput.Left = panelW - rightEdge - btnAddOutput.Width;
            flwSpecChannels.Width = Math.Max(60, btnAddOutput.Left - gap - flwSpecChannels.Left);

            // —— 卡片区: 宽高都跟面板走 ——
            // 以前靠 Anchor.Right/Bottom, 但初始 559x166 是按 600x600 窗口写的, 构造期父面板
            // 只有约 484x345 → 右/底边距算成负数, 之后卡片区一直保持 559x166,
            // 卡片右缘被切 75px、底部被切 37px。这里显式给尺寸 (左 15 / 右 10 / 下 4)。
            // 改尺寸会触发 cardHost.Resize → FitCardWidths 重排卡片。
            if (cardHost != null)
            {
                int cw = Math.Max(200, panelW - cardHost.Left - rightEdge);
                int chh = Math.Max(0, panelSize.Height - cardHost.Top - 4);
                if (cardHost.Width != cw || cardHost.Height != chh)
                    cardHost.Size = new Size(cw, chh);
            }
        }

        /// <summary>
        /// 日志面板标题行: 右侧 [水位监控: 间隔 N 秒] [清除日志] 整组贴右缘,
        /// 窗口窄时不会把「清除日志」挤出面板。左侧「日志」标题固定不动。
        /// </summary>
        void LayoutLogHeader()
        {
            if (btnClearLog == null || lblWater == null) return;
            Control host = btnClearLog.Parent;
            if (host == null) return;

            int panelW = host.ClientSize.Width;
            if (panelW == lastLogHeaderW) return;
            lastLogHeaderW = panelW;
            const int rightEdge = 10;
            const int gap = 6;

            btnClearLog.Left = panelW - rightEdge - btnClearLog.Width;
            lblWaterSec.Left = btnClearLog.Left - 8 - PreferredW(lblWaterSec);
            nudWaterInterval.Left = lblWaterSec.Left - gap - nudWaterInterval.Width;
            lblWaterIv.Left = nudWaterInterval.Left - gap - PreferredW(lblWaterIv);
            lblWater.Left = lblWaterIv.Left - gap - PreferredW(lblWater);
        }

        /// <summary>卡片基准宽度: 换列的步长。取 490 = 卡片里固定控件(设备/声道/延迟/百分号/静音)
        /// 占掉约 273px 之后, 滑条还剩 200px 左右的最舒适宽度, 也是默认窗口下单列卡片的取值区。</summary>
        const int CardBaseW = 490;
        /// <summary>并排卡片之间的横向间距 (卡片外框到外框)。</summary>
        const int CardGapX = 10;
        /// <summary>卡片最窄宽度: 再窄第一行的设备下拉就压到「声道」下拉上了。
        /// 第一行的固定控件 (× / ms / 延迟框 / 延迟 / 声道) 合计约 246px, 再加设备下拉 90px
        /// 与左侧 8px, 360 是"刚好不重叠"的值; 低于它就宁可横向滚动也不挤成畸形。</summary>
        const int CardMinW = 360;
        /// <summary>单列时的宽度上限: 再宽滑条就拉成一根看不见刻度的长线。</summary>
        const int CardMaxW = 1120;

        /// <summary>
        /// 算卡片栅格: 列数 + 每张卡片的宽度。
        /// <para>列数按"基准宽度的整数倍"跳档: 可视宽到 2×基准才铺第 2 列、到 3×基准才铺第 3 列。
        /// 因为余量摊进卡片宽度, 实际卡片宽度始终落在 [基准, 基准+间距] 这个窄区间里,
        /// 所以换列时滑条长度只变化几像素, 不会有"窗口宽一点、滑条就长一截"的拉扯感。</para>
        /// <para><b>竖滚动条要占宽度</b>: 窗口变矮 → 卡片区出现竖条 → 可视宽少了 17px。
        /// 如果卡片宽度还按"没竖条"的宽度算, 内容就比可视宽多出一条竖条的量,
        /// 横向滚动条立刻跟着冒出来。所以这里先算一遍列数, 用算出来的行数预测竖条会不会出现,
        /// 会出现就把它的宽度扣掉再算一遍 —— 卡片自己变窄, 永远不需要横向滚动。</para>
        /// </summary>
        void ComputeCardGrid(int count, out int cols, out int width)
        {
            if (cardHost == null)
            {
                cols = 1;
                width = OutputCardControl.CardWidth;
                return;
            }

            // cardHost.Width 是"外框宽", 竖条显隐不影响它 (ClientSize 会跟着少一条滚动条),
            // 用它当基准算出来的宽度才是稳定的; ClientSize 则用来看"此刻真实可用"多少。
            int full = Math.Max(0, cardHost.Width - cardHost.Padding.Horizontal);
            int avail = Math.Min(full, Math.Max(0, cardHost.ClientSize.Width - cardHost.Padding.Horizontal));
            if (avail <= 0) { cols = 1; width = OutputCardControl.CardWidth; return; }

            GridCols(avail, count, out cols, out width);

            // 这一版摆完会比可视区高 → 竖条会出现 → 扣掉竖条宽度重算, 卡片顺势缩短
            if (NeedVScroll(cols))
            {
                int narrower = Math.Max(0, full - SystemInformation.VerticalScrollBarWidth);
                if (narrower >= CardMinW && narrower < avail)
                    GridCols(narrower, count, out cols, out width);
            }
        }

        /// <summary>纯算术: 给定可用宽度与卡片数, 算出列数与统一卡片宽度。</summary>
        static void GridCols(int avail, int count, out int cols, out int width)
        {
            if (avail <= 0) { cols = 1; width = OutputCardControl.CardWidth; return; }

            cols = Math.Max(1, (avail + CardGapX) / (CardBaseW + CardGapX));
            cols = Math.Min(cols, Math.Max(1, count));

            if (cols <= 1)
            {
                // 单列: 卡片铺满可视宽 (与改造前完全一致), 只有窗口宽到离谱时才封顶
                width = Math.Min(Math.Max(avail, CardMinW), CardMaxW);
            }
            else
            {
                // 多列: 每列都带一个右侧间距(含最后一列), 这样 FlowLayoutPanel 的换行判断
                // (x + 卡宽 + Margin.Right > 可视宽) 正好在算好的列数处触发, 不会提前折行。
                width = (avail - cols * CardGapX) / cols;
                if (width < CardMinW)
                {
                    // 窗口窄到连最小宽度都排不下这么多列 → 主动减列, 宁可少一列也不挤成畸形
                    cols = Math.Max(1, (avail + CardGapX) / (CardMinW + CardGapX));
                    width = cols <= 1 ? Math.Min(Math.Max(avail, CardMinW), CardMaxW)
                                      : (avail - cols * CardGapX) / cols;
                }
            }
        }

        int CardCountForGrid()
        {
            if (cards != null && cards.Count > 0) return cards.Count;
            return models != null ? models.Count : 0;
        }

        /// <summary>第 i 张卡片的实际高度 (还没建出来时按模型推算, 与 SetMulti 的公式一致)。</summary>
        int CardHeightAt(int i)
        {
            if (cards != null && i < cards.Count) return cards[i].Height;
            if (models != null && i < models.Count)
                return OutputCardControl.CardHeight +
                    (models[i].Multi ? 4 + Math.Max(1, models[i].Slots.Count) * OutputCardControl.RowHeight : 0);
            return OutputCardControl.CardHeight;
        }

        /// <summary>
        /// 按这个列数摆完, 内容会不会比可视区高 (即竖滚动条会不会出现)。
        /// 行高取该行最高的一张卡片, 与 FlowLayoutPanel 的排法一致。
        /// </summary>
        bool NeedVScroll(int cols)
        {
            int n = CardCountForGrid();
            if (n == 0 || cardHost == null) return false;
            if (cols < 1) cols = 1;

            int rows = (n + cols - 1) / cols;
            int total = cardHost.Padding.Vertical;
            for (int r = 0; r < rows; r++)
            {
                int hMax = 0;
                for (int i = r * cols; i < Math.Min(n, (r + 1) * cols); i++)
                {
                    int h = CardHeightAt(i) + OutputCardControl.CardGap;   // + Margin.Bottom
                    if (h > hMax) hMax = h;
                }
                total += hMax;
            }
            return total > cardHost.ClientSize.Height;
        }

        bool fitAgain;           // 本轮摆完又触发布局 → 需要再收敛一次

        /// <summary>
        /// 按当前可视宽度摆卡片: 统一宽度 + 横向间距。
        /// 不逐张算宽度: 卡片是逐个加进 FlowLayoutPanel 的, 滚动条可能在中途出现/消失,
        /// 后建的卡片量到的可视宽度就比前面的大一个滚动条宽 (17px), 于是滑条长度对不齐。
        /// <para>改宽度本身会触发容器重新布局, 布局又会改滚动条 → 再发 ClientSizeChanged。
        /// 以前那一次回调被重入保护直接丢掉, 于是"竖条出现了、卡片还是旧宽度"这一版就留在屏幕上
        /// (横条就是这么来的)。这里改成: 被丢掉的那次记个待办, 本轮回手后再收敛一遍。</para>
        /// </summary>
        void FitCardWidths()
        {
            if (cards == null || cards.Count == 0) return;
            if (fittingWidths) { fitAgain = true; return; }
            fittingWidths = true;
            try
            {
                int guard = 0;
                do
                {
                    fitAgain = false;
                    ApplyCardWidths();
                }
                while (fitAgain && ++guard < 4);
            }
            finally { fittingWidths = false; }
        }

        void ApplyCardWidths()
        {
            int cols, w;
            ComputeCardGrid(cards.Count, out cols, out w);

            int avail = cardHost.ClientSize.Width - cardHost.Padding.Horizontal;
            // 兜底: 卡片绝不比可视区宽 (宽到连最小卡片宽都排不下时才听任横向滚动,
            // 宁可横滚也不能把第一行的设备/声道/延迟挤成重叠)
            if (avail >= CardMinW && w > avail) w = avail;

            // 单列不留右侧间距: 那段 10px 会被算进内容宽度, 宽度已 = 可视宽时
            // 正好顶出一根横向滚动条 (实测 600/1000/1200 宽都出现过)。
            // 多列必须留, 否则 FlowLayoutPanel 的换行判断会提前一张就折行。
            int rightGap = cols > 1 ? CardGapX : 0;

            // 缩放窗口时 Resize 与 ClientSizeChanged 会连着来好几次, 下面这轮
            // SuspendLayout/ResumeLayout 是**每个子控件全量重排**, 一帧里重复跑就是缩放卡顿的主因。
            // 列数/宽度/间距都没变(改高度、滚动条开关都会走到这儿) → 直接跳过重排。
            if (cols == lastCardCols && w == lastCardW && rightGap == lastRightGap
                && cards.Count == lastCardCount)
            {
                DropStaleHScroll();
                return;
            }
            lastCardCols = cols;
            lastCardW = w;
            lastRightGap = rightGap;
            lastCardCount = cards.Count;

            var margin = new Padding(0, 0, rightGap, OutputCardControl.CardGap);

            // 我们在 panel 自己的一次布局里改子控件尺寸 (ClientSizeChanged 就是在布局中发的),
            // 外面再套一层挂起/恢复, 让它在尺寸全部落定后统一重算换行与滚动条;
            // 不这么做的话它会把"内容宽"记成改之前的旧值。
            cardHost.SuspendLayout();
            try
            {
                for (int i = 0; i < cards.Count; i++)
                {
                    // 先摆间距再改宽: 换列发生在容器重新布局时, 间距必须已经就位
                    if (cards[i].Margin != margin) cards[i].Margin = margin;
                    if (cards[i].Width != w) cards[i].Width = w;
                }
            }
            finally { cardHost.ResumeLayout(true); }

            DropStaleHScroll();

            if (cols != gridLogCols)
            {
                gridLogCols = cols;
                Log("[界面] 卡片区 " + cols + " 列 × " + w + "px（可视 " + avail + "px，" +
                    cards.Count + " 张卡片）");
            }
        }

        /// <summary>
        /// 竖滚动条刚出现的那一版, FlowLayoutPanel 会把"内容宽"记成卡片收窄之前的旧宽度
        /// (实测: 卡片已 542, 它仍按 559 算), 横向滚动条就此赖着不走 —— 窗口变矮时冒出横条
        /// 就是这么来的。卡片确实已经放得下时, 开关一次 AutoScroll 让它按当前内容重算滚动条。
        /// </summary>
        void DropStaleHScroll()
        {
            if (cardHost == null || !cardHost.HorizontalScroll.Visible) return;
            if (cards == null || cards.Count == 0) return;

            // 内容真的比可视区宽 (窗口窄到连最小卡片宽都排不下) → 留着横条, 否则看不全
            int need = 0;
            for (int i = 0; i < cards.Count; i++)
            {
                int r = cards[i].Width + cards[i].Margin.Horizontal;
                if (r > need) need = r;
            }
            if (need > cardHost.ClientSize.Width) return;

            Point keep = cardHost.AutoScrollPosition;
            cardHost.AutoScroll = false;
            cardHost.AutoScroll = true;
            cardHost.PerformLayout();
            cardHost.AutoScrollPosition = new Point(Math.Abs(keep.X), Math.Abs(keep.Y));
        }

        void AddLaneCard(OutputCardModel preset)
        {
            // F-24: 达到持久化上限就拒绝新增, 否则第 17 张卡本次可见、重启后静默消失
            if (models.Count >= Infrastructure.LaneSettingsCodec.MaxOutputCards)
            {
                Log("[输出] 已达输出卡上限 " + Infrastructure.LaneSettingsCodec.MaxOutputCards + " 张，无法继续添加");
                return;
            }
            var chans = CurrentSpecChannels();
            var m = preset ?? new OutputCardModel();
            if (preset == null)
            {
                // 新卡片接第一个还没人接的声道 (都有人接则接第一个)
                string key = chans.Count > 0 ? chans[0].Key : "FL";
                var usedKeys = new HashSet<string>(StringComparer.Ordinal);
                for (int i = 0; i < models.Count; i++)
                    for (int s = 0; s < models[i].Slots.Count; s++)
                        usedKeys.Add(models[i].Slots[s].ChannelKey);
                for (int i = 0; i < chans.Count; i++)
                    if (!usedKeys.Contains(chans[i].Key)) { key = chans[i].Key; break; }
                m.Slots.Add(new ChannelSlot { ChannelKey = key, Volume = 100 });

                // 新卡片默认接"还没被占用"的设备: 选到已被占用的设备会立刻并进那张多声道卡片,
                // 用户按"加一路"却看不到卡片变多, 容易以为坏了。设备都用完了就留空让用户自己选。
                m.DeviceId = FirstFreeDeviceId();
            }
            models.Add(m);
            NormalizeModels();
            RebuildCards();
            SaveSettings();
            Log("[输出] 新增卡片: " + (m.Slots.Count > 0 ? ChannelNameOf(m.Slots[0].ChannelKey) : "未选声道") +
                " → " + DevNameFor(m.DeviceId) + " (共 " + models.Count + " 张)");
            // 运行中加卡: 该设备可能刚接入端点音量, 归属要跟着改 (绑端点 ⇄ 混音增益)
            if (running) SyncEndpointBindingsWhileRunning();
        }

        /// <summary>还没被任何卡片占用的设备 ID (没有则返回 "")。</summary>
        string FirstFreeDeviceId()
        {
            if (catalog == null) return "";
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < models.Count; i++) used.Add(models[i].DeviceId ?? "");
            for (int i = 0; i < Devices.Count; i++)
            {
                string id = DeviceIdAt(i);
                if (!used.Contains(id)) return id;
            }
            return "";
        }

        void OnCardChanged(OutputCardControl card)
        {
            if (loadingSettings || loadingDevices || normalizing) return;
            // 形态切换前先记下旧形态: 单声道卡片的音量 == 设备端点音量, 多声道行音量 == 矩阵增益。
            // 两者的归属不同, 切换时要把衰减从一个容器搬到另一个, 否则会双份相乘 (12%×12%=1.44%)。
            bool wasMulti = card.Model.Multi;
            int oldRow0 = card.Model.Slots.Count > 0 ? card.Model.Slots[0].Volume : 100;

            SyncLanesFromCards();
            OutputCardModel m = card.Model;
            if (!wasMulti && m.Multi)
            {
                // 单 → 多: 主音量继续承载端点, 各行增益复位 (行是新出现的, 不该继承端点值)
                for (int i = 0; i < m.Slots.Count; i++) m.Slots[i].Volume = 100;
                RebuildLanes();
                Log("[输出] " + DevNameFor(m.DeviceId) + " 切到多声道: 主音量 " + m.Volume +
                    "% 仍由设备端点承载, 各行增益复位 100%");
            }
            else if (wasMulti && !m.Multi)
            {
                // 多 → 单: 把原 p0 行的增益折进主音量, 切回单声道不跳响度
                m.Volume = Math.Max(0, Math.Min(100, (int)Math.Round(m.Volume * oldRow0 / 100.0)));
                if (m.Slots.Count > 0) m.Slots[0].Volume = 100;
                RebuildLanes();
                Log("[输出] " + DevNameFor(m.DeviceId) + " 切回单声道: 原 p0 行增益 " + oldRow0 +
                    "% 已折进主音量 → " + m.Volume + "%");
            }

            // 声道下拉切到/切离「多声道」, 或换到已被占用的设备 → 形态变了, 必须先重建卡片再存盘
            // (models 已被合并/改行数, 旧 cards 再写回模型就会写到错误的卡片上)
            bool structural = NormalizeModels();
            if (structural) RebuildCards();
            SaveSettings();
            RefreshSpecBadges();
            if (structural) return;

            // 换设备: 先把被弃设备的音量还原, 再按新集合绑端点 (否则旧设备永久停在分流音量)
            if (running) SyncEndpointBindingsWhileRunning();
            // 卡片主音量 = 设备端点音量 → 静音也写端点 (与音量同一套双向绑定/还原基线)
            if (running && bindings != null && !volSyncing)
            {
                var b = bindings.SinkOf(card.Model.DeviceId);
                if (b != null && b.IsValid && b.ReadMuted() != card.chkMute.Checked)
                    b.WriteMute(card.chkMute.Checked);
            }
            if (running) ApplyRoutingHot("卡片改动", false);
        }

        /// <summary>
        /// 运行中试图切单/多声道形态 → 已被卡片拦下并回滚。形态改的是设备物理声道数
        /// (OutChannels)，WasapiOut 建好后换不了，只能重新开始。
        /// </summary>
        void OnCardShapeBlocked(OutputCardControl card)
        {
            Log("[提示] " + DevNameFor(card.Model.DeviceId) + " 单/多声道形态是链路级参数，" +
                "运行中不能切换（已还原）。要改请先「停止分流」。");
        }

        void OnCardVolumeScrolled(OutputCardControl card, bool isSlot)
        {
            if (volSyncing) return;
            // 主滑条 = 设备端点音量; 行滑条 = 混音增益 (端点音量是设备级单值, 分不了声道)
            var b = (bindings != null) ? bindings.SinkOf(card.Model.DeviceId) : null;
            bool wroteEndpoint = false;
            if (!isSlot && b != null && b.IsValid)
            {
                bool ok = b.WriteVolume(card.volTrack.Value / 100f);
                if (ok && card.volTrack.Value > 0 && b.ReadMuted()) ok = b.WriteMute(false);
                if (!ok)
                {
                    // F-06: 端点写失败 → 回退混音增益, 让滑条仍生效
                    FallbackEndpointToMix(card.Model.DeviceId, "音量写入");
                }
                else
                {
                    wroteEndpoint = true;
                    // 端点承载音量, 矩阵里的主音量不重复计入 (Flatten 已按 noEndpointVolumeDevices 决定)
                }
            }
            // 拖动中的热更新: 只改增益, 不记日志 (一次拖动几十次事件, 记了就是刷屏)
            if (!wroteEndpoint && running) ApplyRoutingHot("音量", true);
            SaveSettings();
        }

        void OnCardRemoveRequested(OutputCardControl card)
        {
            if (loadingSettings) return;
            int idx = cards.IndexOf(card);
            if (idx < 0 || idx >= models.Count) return;
            if (models.Count <= 1) { Log("[输出] 至少保留一张输出卡片"); return; }
            string who = CardRole(models[idx]) + " → " + DevNameFor(models[idx].DeviceId);
            models.RemoveAt(idx);
            RebuildCards();
            SaveSettings();
            Log("[输出] 已移除: " + who + " (剩 " + models.Count + " 张)");
            // 运行中删卡: 该设备的端点音量必须立刻还原
            if (running) SyncEndpointBindingsWhileRunning();
        }

        /// <summary>
        /// 规格行右侧的声道标注: 有输出的显示蓝色并在名字旁挂数字角标(= 路数), 没输出的灰色。
        /// </summary>
        void RefreshSpecBadges()
        {
            if (flwSpecChannels == null) return;
            flwSpecChannels.SuspendLayout();
            try
            {
                flwSpecChannels.Controls.Clear();
                var chans = CurrentSpecChannels();
                for (int i = 0; i < chans.Count; i++)
                {
                    // 角标 = 有几个物理声道接到该声道 (多声道卡片的每一行各算一个)
                    int n = 0;
                    for (int k = 0; k < models.Count; k++)
                        for (int s = 0; s < models[k].Slots.Count; s++)
                            if (models[k].Slots[s].ChannelKey == chans[i].Key) n++;
                    var name = new Label
                    {
                        Text = chans[i].Value,
                        AutoSize = true,
                        Margin = new Padding(1, 7, 0, 0),
                        ForeColor = n > 0 ? Color.SteelBlue : Color.Gray
                    };
                    flwSpecChannels.Controls.Add(name);
                    if (n > 0)
                    {
                        var badge = new Label
                        {
                            Text = n.ToString(),
                            AutoSize = false,
                            Size = new Size(15, 15),
                            TextAlign = ContentAlignment.MiddleCenter,
                            BackColor = Color.SteelBlue,
                            ForeColor = Color.White,
                            Margin = new Padding(1, 4, 3, 0),
                            Font = new Font("Microsoft YaHei UI", 7F, FontStyle.Bold)
                        };
                        flwSpecChannels.Controls.Add(badge);
                    }
                }
            }
            finally { flwSpecChannels.ResumeLayout(); }
        }

        /// <summary>从 ini 装载输出模型: 编解码在 LaneSettingsCodec (纯数据), 这里只做控件与卡片联动。</summary>
        void LoadLanes()
        {
            models.Clear();
            cmbSpec.SelectedIndex = Math.Max(0, Math.Min(4, GetInt("Spec", 1)));

            Infrastructure.LaneSettingsCodec.Decode(settings, models);

            ReassignLanesToSpec(false);   // 只修正落不进规格的声道键
            // 这里**不合并**: 构造期 catalog 还没建, 读不到端点音量, 合并出来的主音量会是 100%
            // (老 ini 里同设备两张卡各自是"混音增益", 主音量一旦按 100% 写端点就会突然变响)。
            // 合并放在 RefreshDevices 里做 —— 那时能读到端点真值, 合并不改响度。
            RebuildCards();
        }
    }
}