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
using StereoSplitter.Model;

namespace StereoSplitter
{
    partial class MainForm
    {
        // ==================== 规格与输出卡片 ====================

        // R-02: 上标数字的字体。虽然每个声道都要建一个 Label, 但字体本身复用同一个实例
        // (Font 不是控件拥有的资源, Control.Dispose 不会释放它, 必须有人管)。
        // 8pt 而不是更小: 7pt 的「¹」在 96dpi 下只剩 5px 高, 贴在汉字后面基本看不出是数字。
        static readonly Font SpecBadgeFont = new Font("Microsoft YaHei UI", 8F, FontStyle.Bold);

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
            var gridMargin = new Padding(0, 0, gridCols > 1 ? CardGrid.GapX : 0, OutputCardControl.CardGap);

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
                // 卡片自带圆角外框: 竖向靠紧 (间隙 = Margin.Bottom), 横向留 CardGrid.GapX 供换列
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
        /// 日志圆角壳按日志面板的实时尺寸摆放: 左右下三边留白一致 (各 8 设计像素, 用户要求
        /// "左右下留白要一致, 且小一点" —— 原来左/右 15、下 8 不一致), 顶部让出标题行 30。
        /// 拖分隔条改日志区高度时同步跟随 (日志框嵌在壳内, 由壳自己摆)。
        /// </summary>
        void LayoutLogPanel()
        {
            if (logShell == null || splitContainer == null) return;
            Size cs = splitContainer.Panel2.ClientSize;
            // 尺寸没变就别再 SetBounds: 拖分隔条时这个回调每帧都来,
            // 每次都改一次 bounds 会连带整块日志框重排重画。
            if (cs == lastLogPanelSize) return;
            lastLogPanelSize = cs;
            int m = Dpi.S(8);                                          // 左右下统一留白
            int w = Math.Max(Dpi.S(120), cs.Width - m * 2);
            // 高下限设 0: 分隔条现在可以一路把日志区压到没有, 日志框跟着缩到 0 而不是留着
            // 40px 高度溢出到面板外面 (Panel 会裁剪, 但多出来的高度仍会参与滚动条计算)
            int h = Math.Max(0, cs.Height - Dpi.S(30) - m);
            logShell.SetBounds(m, Dpi.S(30), w, h);
        }

        /// <summary>
        /// 增益/主音量两行的自适应布局 (与输出卡片里的音量条同理): 滑条右端跟着面板宽度走,
        /// 右起固定为 [百分比][8][滑条], 行首是 22px 图标 (增益=推子, 主音量=喇叭)。
        /// <para>主音量行行首换成喇叭按钮 (点一下 = 主静音), 原来右侧那个独立的「静音」勾选框
        /// 随之前删掉, 滑条因此比增益行多出 ~66px 的可用长度 —— 但这儿刻意**不给两行不同的长度**:
        /// 滑条左端仍钉在 78 (与「增益:」标签右缘同列), 右端都收到百分比左边 8px,
        /// 两条滑条等长、两个百分比竖向对齐, 改音量时不至于看错行。</para>
        /// </summary>
        void LayoutVolumeRows()
        {
            if (volMaster == null || btnMuteMaster == null) return;
            Control host = volMaster.Parent;
            if (host == null) return;

            int panelW = host.ClientSize.Width;
            // 宽度没变 (改高度/拖分隔条也会走到这里) → 摆出来的结果不会变, 直接跳过
            if (panelW == lastVolPanelW) return;
            lastVolPanelW = panelW;
            int leftEdge = Dpi.S(45);   // 滑条左端: 两行共用一个起点, 图标都在 x=15..37, 右侧留 8px 就贴上
            int rightEdge = Dpi.S(10);  // 右端留白, 与卡片区/日志框右缘一致
            int pctW = lblVolMaster.Width;

            int pctX = panelW - rightEdge - pctW;
            int volW = Math.Max(Dpi.S(120), pctX - Dpi.S(8) - leftEdge);

            volGain.SetBounds(leftEdge, Dpi.S(52), volW, Dpi.S(20));
            lblVolGain.Location = new Point(pctX, Dpi.S(52));
            volMaster.SetBounds(leftEdge, Dpi.S(86), volW, Dpi.S(20));
            lblVolMaster.Location = new Point(pctX, Dpi.S(86));
        }

        /// <summary>AutoSize 控件取宽: 用 PreferredSize (不依赖当前布局), 构造期也能算准。</summary>
        static int PreferredW(Control c)
        {
            return c.AutoSize ? c.PreferredSize.Width : c.Width;
        }

        /// <summary>
        /// 顶部三行 (捕获+开始分流 / 规格 / 声道标注) 与卡片区宽度的自适应布局。
        /// <para>原本是按 600 宽硬编码的固定坐标 (刷新设备右缘 489、添加输出 574),
        /// 窗口缩到 500 宽 (客户区约 484) 时右侧控件会被挤出客户区截断。
        /// 这里改为「右侧控件贴面板右缘、左侧贴左缘、中间下拉居中」, 任意宽度都不越界。</para>
        /// <para>缓冲档位 / 时钟校正 / 捕获消音 / 关闭行为 / 延迟校准 已迁入设置页,
        /// 不再参与这里的布局。</para>
        /// <para>设置齿轮也已搬去标题栏 (见 MainForm.LayoutCaption) —— 第一行不再有它。</para>
        /// <para>「开始分流」搬进第一行 (用户指定)。第一行现为三个独立控件 (用户指定拆开组合壳):
        /// 刷新按钮贴左、开始按钮贴右 (两者同尺寸 30 设计像素), 设备下拉吃掉两者之间的
        /// 全部空地 —— 不影响旁边按钮的前提下最宽、随窗口自适应 (用户指定), 两侧各留一道等宽的缝。</para>
        /// </summary>
        void LayoutTopRows()
        {
            if (cmbDefault == null || btnRefresh == null || btnAddOutput == null) return;
            if (btnStart == null || cmbSpec == null) return;
            Control host = btnStart.Parent;
            if (host == null) return;

            Size panelSize = host.ClientSize;
            if (panelSize == lastTopPanelSize) return;   // 尺寸没变就不重排 (拖分隔条会频繁触发)
            lastTopPanelSize = panelSize;
            int panelW = panelSize.Width;
            int rightEdge = Dpi.S(10);              // 与卡片区左/右缘一致的留白
            int leftEdge = rightEdge;               // 用户批86: 左右留白一致 —— 规格下拉/刷新按钮
                                                    // 与卡片区壳左缘同列, 右侧按钮与壳右缘同列
            int gap = Dpi.S(8);

            // —— 第 1 行: [刷新] [捕获下拉] [开始分流] 三个独立控件 ——
            // 两个按钮同尺寸 (30 设计像素, 比行高 26 大一圈: 图标是这一行的主操作, 大一号才压得住),
            // 各自贴左右缘并垂直居中于行; 下拉**吃掉两按钮之间的全部空地** (用户指定:
            // 不影响旁边两个按钮的前提下最宽、自适应), 左右各留一道等宽的缝 → 自然居中、间距对称。
            int rowTop = Dpi.S(6);
            int rowH = OutputCardControl.RowControlHeight;
            btnRefresh.Left = leftEdge;
            btnRefresh.Top = rowTop + (rowH - btnRefresh.Height) / 2;
            btnStart.Top = rowTop + (rowH - btnStart.Height) / 2;
            btnStart.Left = Math.Max(btnRefresh.Right + gap, panelW - rightEdge - btnStart.Width);

            int cmbW = Math.Max(Dpi.S(40), btnStart.Left - gap - (btnRefresh.Right + gap));
            if (cmbDefault.Width != cmbW) cmbDefault.Width = cmbW;
            cmbDefault.Left = btnRefresh.Right + gap;   // 两侧缝都是 gap → 居中且对称
            cmbDefault.Top = rowTop + (rowH - cmbDefault.Height) / 2;

            // —— 第 2 行: [规格下拉] 贴左, [添加设备] 贴右, 中间空地归「声道标注」或「运行状态字」 ——
            // 规格标签已删, 下拉自己说明是什么: 它当行首读起来才顺, 空出来的右端留给「添加设备」。
            // 标注与状态字**互相替换** (用户指定: 开始分流后状态字替换掉标注), 谁在场谁居中。
            cmbSpec.Left = leftEdge;
            btnAddOutput.Left = Math.Max(cmbSpec.Right + gap, panelW - rightEdge - btnAddOutput.Width);
            if (lblRunState != null && lblRunState.Visible) PlaceRunStateLabel();

            // —— 第 3 行 (声道名 + 角标): 单独一行水平居中 ——
            CenterSpecBadges();

            // —— 卡片区壳: 宽高都跟面板走, 左右留白对称 (用户批85: 卡片区要居中) ——
            // 原来左 15 / 右 10 不对称, 且原生竖滚动条出现时再从右侧吃掉一条 → 更歪。
            // 现在壳左右各 rightEdge(10), 原生条被壳裁掉、自绘细条落进右侧留白带里;
            // 卡片栅格在壳内由 FitCardWidths 动态居中 (Padding.Left)。
            // 改尺寸会触发壳 Resize → Recalc → FitCardWidths 重排卡片。
            if (cardShell != null)
            {
                int cLeft = Dpi.S(10);
                if (cardShell.Left != cLeft) cardShell.Left = cLeft;
                int cw = Math.Max(Dpi.S(200), panelW - cLeft - rightEdge);
                int chh = Math.Max(0, panelSize.Height - cardShell.Top - Dpi.S(4));
                if (cardShell.Width != cw || cardShell.Height != chh)
                    cardShell.Size = new Size(cw, chh);
            }
        }

        /// <summary>
        /// 日志面板标题行: 右侧 [水位 N 秒 圆角壳] [清除日志] 整组贴右缘 (与内容框右缘
        /// 同一列), 窗口窄时不会把「清除日志」挤出面板。左侧「日志」标题固定不动。
        /// 垂直方向 (等高/居中) 由 <see cref="AlignLogHeaderRow"/> 负责。
        /// </summary>
        void LayoutLogHeader()
        {
            if (btnClearLog == null || nudWaterShell == null) return;
            Control host = btnClearLog.Parent;
            if (host == null) return;
            AlignLogHeaderRow();

            int panelW = host.ClientSize.Width;
            if (panelW == lastLogHeaderW) return;
            lastLogHeaderW = panelW;
            int rightEdge = Dpi.S(8);   // 与日志圆角壳右缘同一列 (壳左右下留白一致各 8)
            int gap = Dpi.S(6);

            btnClearLog.Left = panelW - rightEdge - btnClearLog.Width;
            nudWaterShell.Left = btnClearLog.Left - gap - nudWaterShell.Width;
        }

        /// <summary>
        /// 标题行垂直对齐 (用户要求水位壳与清除日志按钮视觉等高): 按钮高度取壳高,
        /// 壳/按钮/「日志」标题三者在标题行 (高 30) 内垂直居中, 标题左缘与内容框对齐。
        /// 壳高在字体定型后的 Recalc 才到位 → 挂在壳的 SizeChanged 上反复校正。
        /// </summary>
        void AlignLogHeaderRow()
        {
            if (nudWaterShell == null || btnClearLog == null || nudWaterShell.Parent == null) return;
            int h = nudWaterShell.Height;
            if (btnClearLog.Height != h) btnClearLog.Height = h;
            int y = (Dpi.S(30) - h) / 2;
            if (nudWaterShell.Top != y) nudWaterShell.Top = y;
            if (btnClearLog.Top != y) btnClearLog.Top = y;
            if (lblLogTitle != null)
            {
                if (lblLogTitle.Left != Dpi.S(8)) lblLogTitle.Left = Dpi.S(8);
                int ty = (Dpi.S(30) - lblLogTitle.Height) / 2;
                if (lblLogTitle.Top != ty) lblLogTitle.Top = ty;
            }
        }

        /// <summary>
        /// 算卡片栅格: 列数 + 每张卡片的宽度。
        /// <para>列数按"基准宽度的整数倍"跳档: 可视宽到 2×基准才铺第 2 列、到 3×基准才铺第 3 列。
        /// 因为余量摊进卡片宽度, 实际卡片宽度始终落在 [基准, 基准+间距] 这个窄区间里,
        /// 所以换列时滑条长度只变化几像素, 不会有"窗口宽一点、滑条就长一截"的拉扯感。</para>
        /// <para>可视宽取壳的裁剪值 (<see cref="CardScrollShell.ContentWidth"/>): 原生竖条
        /// 已被壳裁掉、自绘细条落在留白带里, 可视宽不再随原生条显隐跳动 —— 旧版"预测竖条
        /// 出现就扣宽重算"那一轮不再需要。</para>
        /// </summary>
        void ComputeCardGrid(int count, out int cols, out int width)
        {
            if (cardShell == null)
            {
                cols = 1;
                width = OutputCardControl.CardWidth;
                return;
            }

            // 可视宽用壳的稳定值: 原生竖滚动条已被壳裁掉 (自绘细条在留白带里),
            // ClientSize 不再随条的显隐跳动 —— 也不再需要"预测竖条出现、扣宽重算"那一轮。
            // Padding.Left 是栅格居中量 (ApplyCardWidths 设), 不从可视宽里扣 —— 扣了会
            // 与居中量互相喂, 每轮缩一点收敛不到头。
            int full = Math.Max(0, cardShell.ContentWidth);
            if (full <= 0) { cols = 1; width = OutputCardControl.CardWidth; return; }

            CardGrid.Columns(full, count, OutputCardControl.CardWidth, out cols, out width);
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
                return OutputCardControl.HeightFor(models[i].Multi ? Math.Max(1, models[i].Slots.Count) : 0);
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
            // 卡片高度只有 UI 知道 (已建出来的量 Height, 没建的按模型推算), 这里收集成数组
            // 交给 Model 层的纯函数去算 —— 栅格规则因此能被 --selftest 覆盖。
            var heights = new int[n];
            for (int i = 0; i < n; i++) heights[i] = CardHeightAt(i);
            return CardGrid.NeedsVScroll(n, cols, heights, cardHost.Padding.Vertical,
                                         cardHost.ClientSize.Height, OutputCardControl.CardGap);
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

            // 可视宽 = 壳裁剪后的稳定值; 卡片行总宽 (不含每张卡片右侧的换行间距)
            int full = cardShell != null ? cardShell.ContentWidth : cardHost.ClientSize.Width;
            // 兜底: 卡片绝不比可视区宽 (宽到连 HardMinW 都排不下时才听任横向滚动, 批94:
            // 挤压带 [HardMinW, MinW) 内贴可视宽 —— 用户窗口比 MinW 窄十几像素就冒横条)
            if (full >= CardGrid.HardMinW && w > full) w = full;

            // 单列不留右侧间距: 那段 10px 会被算进内容宽度, 宽度已 = 可视宽时
            // 正好顶出一根横向滚动条 (实测 600/1000/1200 宽都出现过)。
            // 多列必须留, 否则 FlowLayoutPanel 的换行判断会提前一张就折行。
            int rightGap = cols > 1 ? CardGrid.GapX : 0;

            // 栅格居中 (用户批85 "输出卡片要居中"): 栅格占不满可视宽时 (单列封顶/
            // 多列除不尽), 余量对半分给左右 —— 卡片行两侧留白对称。居中量走 Padding.Left。
            // 稳妥条件: padding 后行总宽 (含换行间距) 仍不超出可视宽, 否则顶出横向滚动条
            // —— 余量不足 2 个间距时干脆不居中 (不对称量 < 一个间距, 看不出来)。
            int gridW = cols * w + (cols - 1) * CardGrid.GapX;
            int slack = Math.Max(0, full - gridW);
            int padLeft = slack >= 2 * CardGrid.GapX ? slack / 2 : 0;

            // 缩放窗口时 Resize 与 ClientSizeChanged 会连着来好几次, 下面这轮
            // SuspendLayout/ResumeLayout 是**每个子控件全量重排**, 一帧里重复跑就是缩放卡顿的主因。
            // 列数/宽度/间距/居中量都没变(改高度、滚动条开关都会走到这儿) → 直接跳过重排。
            if (cols == lastCardCols && w == lastCardW && rightGap == lastRightGap
                && cards.Count == lastCardCount && padLeft == lastCardPadLeft)
            {
                DropStaleHScroll();
                if (cardShell != null) cardShell.RefreshBar();
                return;
            }
            lastCardCols = cols;
            lastCardW = w;
            lastRightGap = rightGap;
            lastCardCount = cards.Count;
            lastCardPadLeft = padLeft;

            var margin = new Padding(0, 0, rightGap, OutputCardControl.CardGap);

            // 我们在 panel 自己的一次布局里改子控件尺寸 (ClientSizeChanged 就是在布局中发的),
            // 外面再套一层挂起/恢复, 让它在尺寸全部落定后统一重算换行与滚动条;
            // 不这么做的话它会把"内容宽"记成改之前的旧值。
            cardHost.SuspendLayout();
            try
            {
                var hostPad = cardHost.Padding;
                var wantPad = new Padding(padLeft, hostPad.Top, hostPad.Right, hostPad.Bottom);
                if (cardHost.Padding != wantPad) cardHost.Padding = wantPad;
                for (int i = 0; i < cards.Count; i++)
                {
                    // 先摆间距再改宽: 换列发生在容器重新布局时, 间距必须已经就位
                    if (cards[i].Margin != margin) cards[i].Margin = margin;
                    if (cards[i].Width != w) cards[i].Width = w;
                }
            }
            finally { cardHost.ResumeLayout(true); }

            DropStaleHScroll();
            if (cardShell != null) cardShell.RefreshBar();

            if (cols != gridLogCols)
            {
                gridLogCols = cols;
                Log("[界面] 卡片区 " + cols + " 列 × " + w + "px（可视 " + full + "px，" +
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

        /// <summary>
        /// 「添加设备」的第一步: 弹出选择窗 (输出到哪台设备 + 负责哪个声道),
        /// 确认后按所选建卡片。不再"直接加一张默认卡片再让用户改" —— 加出来的卡片
        /// 常常带着不是本意的设备/声道, 还得删掉重来。
        /// </summary>
        void AddOutputViaDialog()
        {
            // F-24: 达上限就别弹窗了, 弹了也加不了
            if (models.Count >= Infrastructure.LaneSettingsCodec.MaxOutputCards)
            {
                Log("[输出] 已达输出卡上限 " + Infrastructure.LaneSettingsCodec.MaxOutputCards + " 张，无法继续添加");
                return;
            }
            if (Devices.Count == 0)
            {
                Log("[输出] 没有可用输出设备, 请先点「刷新设备」或接入设备");
                return;
            }

            // 设备清单与卡片设备下拉同一格式; 已被占用的设备灰显
            var texts = new string[Devices.Count];
            var ids = new string[Devices.Count];
            var used = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < models.Count; i++) used.Add(models[i].DeviceId ?? "");
            int freeIdx = -1;
            for (int i = 0; i < Devices.Count; i++)
            {
                texts[i] = "[" + i + "] " + Devices[i].FriendlyName;
                ids[i] = DeviceIdAt(i);
                if (freeIdx < 0 && !used.Contains(ids[i])) freeIdx = i;
            }

            // 声道清单与默认值: 默认接第一路还没人接的声道 (与原来的直加逻辑一致)
            var chans = CurrentSpecChannels();
            var usedKeys = new HashSet<string>(StringComparer.Ordinal);
            for (int i = 0; i < models.Count; i++)
                for (int s = 0; s < models[i].Slots.Count; s++)
                    usedKeys.Add(models[i].Slots[s].ChannelKey);
            string defaultKey = null;
            for (int i = 0; i < chans.Count; i++)
                if (!usedKeys.Contains(chans[i].Key)) { defaultKey = chans[i].Key; break; }
            if (defaultKey == null && chans.Count > 0) defaultKey = chans[0].Key;

            using (var dlg = new AddOutputDialog(texts, ids, used, freeIdx, chans, defaultKey))
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return;

                var m = new OutputCardModel { DeviceId = dlg.DeviceId };
                if (dlg.IsMulti)
                {
                    m.Multi = true;   // 行数由 NormalizeModels 按设备物理声道数补齐
                }
                else if (!string.IsNullOrEmpty(dlg.ChannelKey))
                {
                    m.Slots.Add(new ChannelSlot { ChannelKey = dlg.ChannelKey, Volume = 100 });
                }
                AddLaneCard(m);
            }
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

        // ==================== 设置页 ====================

        /// <summary>
        /// 交给设置弹层摆放的控件, 顺序与 <see cref="SettingsFlyout"/> 的取用顺序严格一致:
        /// 0 缓冲档位标签 / 1 缓冲档位下拉 / 2 自动时钟校正 / 3 捕获消音 / 4 关闭进托盘 / 5 延迟校准。
        /// </summary>
        internal Control[] SettingsControls()
        {
            return new Control[] { lblLatency, cmbLatency, chkDrift, chkMuteCapture, chkMinimizeToTray, btnCalibrate };
        }

        /// <summary>缓冲档位的短名 ("极低"、"极限"), 用于设置按钮的提示文案。</summary>
        string LatencyProfileShort()
        {
            string[] names = { "标准", "低延迟", "极低", "超低", "极限" };
            int i = cmbLatency.SelectedIndex;
            if (i < 0 || i >= names.Length) i = 0;
            return names[i];
        }

        /// <summary>
        /// 「更多」图标的提示: 把已迁走的两项状态摘要出来, 主界面不必再为它们各占一行。
        /// 状态变化 (改档位 / 开关校正) 与弹层收起时都会刷新。
        /// </summary>
        void RefreshSettingsTip()
        {
            if (btnSettings == null || tipTop == null) return;
            tipTop.SetToolTip(btnSettings,
                "更多\n" +
                "缓冲档位: " + LatencyProfileShort() + "\n" +
                "自动时钟校正: " + (driftMode == 0 ? "关" : "开") + "\n" +
                "关闭时收进托盘: " + (chkMinimizeToTray.Checked ? "开" : "关"));
        }

        /// <summary>
        /// 打开/收起"更多"弹层。控件是"借出去再收回来"的 (见 <see cref="SettingsFlyout"/>):
        /// 搬移期间把 loadingSettings 临时置真, 避免控件换父容器 (句柄重建) 时触发
        /// SelectedIndexChanged/CheckedChanged 把中间态写进 ini, 也避免 chkDrift 的事件
        /// 在搬移途中去调引擎。
        /// <para>弹层是 AutoClose 的: 点弹层外/Esc 收起, Closed 事件里归还控件并释放。
        /// "弹层开着再点图标想收起"有个竞态 —— AutoClose 会先收掉弹层再触发本体的 Click,
        /// 没有那道时间闸就会"关掉又立刻打开" (与 RoundedCombo 同一套对策)。</para>
        /// </summary>
        void OpenSettings()
        {
            if (settingsFlyout != null)
            {
                if (settingsFlyout.Visible) settingsFlyout.Close();
                return;
            }
            // 刚被 AutoClose 收起的 250ms 内不再打开: 这次点击是"想收起", 不是"想打开"
            if (lastFlyoutAutoCloseMs > 0 && NowMs() - lastFlyoutAutoCloseMs < 250) return;

            bool prevLoading = loadingSettings;
            SettingsFlyout f = null;
            loadingSettings = true;          // 搬入阶段: 不落盘、不产生副作用
            try
            {
                f = new SettingsFlyout(SettingsControls());
                settingsFlyout = f;
            }
            finally
            {
                loadingSettings = prevLoading;
            }
            if (f == null) return;

            f.Closed += (s, e) =>
            {
                // 归还控件期间同样不许落盘 (控件换父容器, 句柄重建会重放状态事件)
                loadingSettings = true;
                try { f.ReturnControls(); }
                finally
                {
                    settingsFlyout = null;
                    loadingSettings = false;
                    RefreshSettingsTip();
                    lastFlyoutAutoCloseMs = f.LastAutoCloseMs;
                    // 释放延后一拍: 此刻还在 Closed 事件里, 立刻 Dispose 有可能打乱消息循环
                    try { BeginInvoke((Action)(() => { try { if (!f.IsDisposed) f.Dispose(); } catch { } })); }
                    catch { try { f.Dispose(); } catch { } }
                }
            };

            // 锚在图标正下方, 右缘与图标右缘对齐; 顶出屏幕时往回收
            Point anchor = PointToScreen(new Point(btnSettings.Right - f.Width, btnSettings.Bottom + Dpi.S(4)));
            Rectangle wa = Screen.FromControl(this).WorkingArea;
            if (anchor.X < wa.Left) anchor.X = wa.Left;
            if (anchor.Y + f.Height > wa.Bottom) anchor.Y = Math.Max(wa.Top, btnSettings.Top - f.Height - Dpi.S(4));
            try { f.Show(anchor); }
            catch
            {
                // 弹出失败 (极少见): 归还控件并复位, 别留下一个借走一半的状态
                loadingSettings = true;
                try { f.ReturnControls(); } finally { settingsFlyout = null; loadingSettings = prevLoading; }
                try { f.Dispose(); } catch { }
            }
        }

        /// <summary>弹层最近一次自动收起的时刻 (NowMs 基准); 0 = 没有。</summary>
        long lastFlyoutAutoCloseMs;

        /// <summary>与 RoundedCombo/GearButton 同一套: Stopwatch 基准, 不受系统校时影响。</summary>
        static long NowMs()
        {
            return (long)(System.Diagnostics.Stopwatch.GetTimestamp() * 1000.0
                / System.Diagnostics.Stopwatch.Frequency);
        }

        // ==================== 延迟校准 (麦克风互相关) ====================

        /// <summary>
        /// 打开延迟校准窗口。分流在跑或已停止都可以: 扫频直接向每台设备播放, 分流正在放的音乐
        /// 会叠在扫频上 —— 定位算法按"能在音乐里找扫频"设计 (全段匹配滤波 + 峰显著度),
        /// 音乐盖过扫频时会报错而不是给一个错的值。链路保持原样, 不静音、不拆流。
        /// </summary>
        void OpenLatencyCalibration()
        {
            // 校准是模态窗口: 先收起弹层 (它还借着几个控件), 否则弹层悬在校准窗后面
            if (settingsFlyout != null && settingsFlyout.Visible) settingsFlyout.Close();

            if (state == AppState.Starting)
            {
                MessageBox.Show(this,
                    "分流正在启动，请等启动完成后再校准（或先停止）。\n\n" +
                    "启动过程中链路还在建立、端点尚未稳定，这时放扫频测不出可信结果。",
                    "延迟校准", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            var list = new List<CalibLane>();
            var seen = new HashSet<string>();
            for (int i = 0; i < cards.Count; i++)
            {
                string id = SafeDeviceId(cards[i].SelectedDeviceIndex);
                if (string.IsNullOrEmpty(id)) continue;   // 未选设备 / 「已保存设备当前不可用」占位项
                if (!seen.Add(id)) continue;              // 同一台设备只测一次 (延迟本来就是设备级的)
                list.Add(new CalibLane
                {
                    DeviceId = id,
                    Display = DevNameFor(id),
                    CurrentDelayMs = (int)cards[i].nudDelay.Value
                });
            }

            if (list.Count < 2)
            {
                MessageBox.Show(this,
                    "校准需要至少两张输出卡片、且各自绑定了不同的设备。\n\n" +
                    "当前可用设备只有 " + list.Count + " 路。",
                    "延迟校准", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            // 校准必须用**实际分流时的输出缓冲档位**: 不同端点对同一缓冲请求的实际分配不同
            // (USB 声卡基本照给, 蓝牙有自己的下限), 请求值不一致 → 测出的两路差值就偏。
            int prof = cmbLatency.SelectedIndex < 0 ? 0 : cmbLatency.SelectedIndex;
            prof = Math.Max(0, Math.Min(LatencyProfiles.Length - 1, prof));
            int outMs = LatencyProfiles[prof][1];

            // 校准不需要停音乐、也不需要停分流: 扫频叠在正在播放的音频上, 定位靠全段匹配滤波。
            // 分流在跑时链路保持原样 (端点不动、缓冲不断流), 这里只是多开一个播放流。
            bool hostRunning = (state == AppState.Running) && engine != null;

            Log("[校准] 打开延迟校准: " + list.Count + " 路 (麦克风互相关, 输出缓冲 " + outMs + "ms" +
                (hostRunning ? ", 分流继续播放" : "") + ")");
            using (var f = new LatencyCalibrationForm(list, outMs, ApplyCalibratedDelay, hostRunning))
                f.ShowDialog(this);
        }

        /// <summary>
        /// 把校准结果写回卡片。走 nudDelay.Value → ValueChanged → OnCardChanged → SaveSettings,
        /// 与手动改延迟完全同一条路径 (含存盘与规格角标刷新), 不另开写入通道。
        /// </summary>
        void ApplyCalibratedDelay(string deviceId, int delayMs)
        {
            for (int i = 0; i < cards.Count; i++)
            {
                OutputCardControl c = cards[i];
                if (SafeDeviceId(c.SelectedDeviceIndex) != deviceId) continue;
                if ((int)c.nudDelay.Value != delayMs) c.nudDelay.Value = delayMs;
            }
            Log("[校准] " + DevNameFor(deviceId) + " 延迟 → " + delayMs + " ms");
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
                if (b != null && b.IsValid && b.ReadMuted() != card.btnMute.Muted)
                    b.WriteMute(card.btnMute.Muted);
            }
            if (running)
            {
                if (DelayDiffersFromRunning()) ApplyDelayHot();
                else ApplyRoutingHot("卡片改动", false);
            }
        }

        /// <summary>
        /// 运行中改延迟: **第一下立刻生效** (点箭头就该马上听到), 之后合并窗口内的连续改动
        /// 攒成一次。
        /// <para>为什么要合并: 数字框每敲一位都会触发 ValueChanged, 敲 "180" 会依次产生
        /// 1 / 18 / 180, 逐次跳变就是三声"咔"。但也不能一律延后 —— 点一下箭头却要等
        /// 180ms 才听到, 手感像没响应。</para>
        /// <para>所以用"前沿 + 后沿": 窗口没开就立即应用并开窗; 窗口已开就只更新数值,
        /// 由窗口到点用最终值再应用一次 (那次若已一致则跳过, 见 delayHotTimer.Tick)。</para>
        /// </summary>
        void ApplyDelayHot()
        {
            if (delayHotTimer == null) { ApplyRoutingHot("延迟", false); return; }
            if (delayHotTimer.Enabled) return;      // 已在本轮窗口内, 交给 Tick 收口
            ApplyRoutingHot("延迟", false);
            delayHotTimer.Start();
        }

        /// <summary>
        /// 卡片上的延迟是否已经和运行中的链路不同 —— 决定这次变动能不能走热改路径。
        /// <para>按**设备**比: 同一个设备挂多张卡片时引擎取其中的最小延迟
        /// (见 LaneRouting 的"设备级延迟取最小"), 所以这里也取最小, 否则两张卡的
        /// 延迟不一致会被误判成"改了延迟", 每次动别的设置都多跳一下。</para>
        /// </summary>
        bool DelayDiffersFromRunning()
        {
            if (runModel == null) return false;
            var run = runModel.EnabledSinks();
            for (int i = 0; i < run.Count; i++)
            {
                int want = int.MaxValue;
                for (int c = 0; c < cards.Count; c++)
                    if (string.Equals(cards[c].Model.DeviceId, run[i].DeviceId, StringComparison.Ordinal))
                        want = Math.Min(want, Math.Max(0, cards[c].Model.DelayMs));
                if (want != int.MaxValue && want != run[i].DelayMs) return true;
            }
            return false;
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
            var chans = CurrentSpecChannels();

            // 先算"徽标应该长什么样"的签名。改延迟 / 音量 / 静音时它完全不变,
            // 就不该销毁重建控件 —— 重建十几个 Label + new Font 约 15ms, 连点
            // 延迟箭头时每一击都卡一帧, 而那些改动跟声道徽标毫无关系。
            var counts = new int[chans.Count];
            var sig = new System.Text.StringBuilder();
            for (int i = 0; i < chans.Count; i++)
            {
                // 角标 = 有几个物理声道接到该声道 (多声道卡片的每一行各算一个)
                for (int k = 0; k < models.Count; k++)
                    for (int s = 0; s < models[k].Slots.Count; s++)
                        if (models[k].Slots[s].ChannelKey == chans[i].Key) counts[i]++;
                sig.Append(chans[i].Value).Append('/').Append(counts[i]).Append(';');
            }
            string want = sig.ToString();
            if (string.Equals(flwSpecChannels.Tag as string, want, StringComparison.Ordinal)) return;
            flwSpecChannels.Tag = want;

            flwSpecChannels.SuspendLayout();
            try
            {
                // R-02: Controls.Clear() 的语义是"摘下"而不是"销毁" —— 摘下的 Label 不再有
                // 父容器, 也就不会随窗体一起释放, 只能等 GC。逐个摘 + Dispose 才是真的回收。
                while (flwSpecChannels.Controls.Count > 0)
                {
                    Control old = flwSpecChannels.Controls[0];
                    flwSpecChannels.Controls.RemoveAt(0);
                    old.Dispose();
                }
                for (int i = 0; i < chans.Count; i++)
                {
                    int n = counts[i];
                    // 上标不用 Unicode 字符 (⁸ 在 9pt 下只有 5px 高, 而且字形顶部会被 Label
                    // 的字框裁掉): 单独一个无底色的小号 Label, 靠 Margin.Top 比名字小 2px
                    // 形成上标位置, 字号与字重都能单独调。
                    flwSpecChannels.Controls.Add(new Label
                    {
                        Text = chans[i].Value,
                        AutoSize = true,
                        Margin = new Padding(1, 5, n > 0 ? 0 : 3, 0),
                        ForeColor = n > 0 ? Color.SteelBlue : Color.Gray
                    });
                    if (n > 0)
                    {
                        flwSpecChannels.Controls.Add(new Label
                        {
                            Text = n.ToString(),
                            AutoSize = true,
                            Font = SpecBadgeFont,
                            // 上标感靠"顶对齐"而不是"缩小字号": 数字顶部要明显高过汉字顶部。
                            // 名字的 Margin.Top 是 5, 这里取 1 —— 数字(8pt)本身就比汉字矮,
                            // 再只差 2px 的话看着只是"一行小字", 不像上标。
                            // 左 0 是下限: 负 Margin 会被 FlowLayout 夹成 0 (试过 -2, 纹丝不动),
                            // 「左」与上标之间那 4~5px 的间隙来自 AutoSize 的字体尾部留白, 消不掉。
                            Margin = new Padding(0, 1, 4, 0),
                            ForeColor = Color.SteelBlue
                        });
                    }
                }
            }
            finally { flwSpecChannels.ResumeLayout(); }

            // 内容变了 → 总宽变了 → 重新居中。LayoutTopRows 有"尺寸没变就早退"的优化,
            // 这里必须自己调一次, 否则换规格后角标从 2 个变 6 个仍然按旧宽度居中 (偏右)。
            CenterSpecBadges();
        }

        /// <summary>
        /// 把「声道名 + 输出路数角标」在「＋」与「规格下拉」之间水平居中。
        /// <para>FlowLayoutPanel 没有 TextAlign, 铺满宽度只会把内容钉在左边, 所以先按子控件的
        /// Width+Margin 求和得到内容实际宽度, 再把**面板本身**居中 —— 面板宽度 = 内容宽度,
        /// 内容自然就居中了。</para>
        /// <para><b>居中范围是中间那块空地, 不是整个面板</b>: 同一行左边是「规格」、右边是「＋」,
        /// 按整面板居中会让标注压在它们身上 (8 路声道名能有 3500px, 光靠裁是不够的)。</para>
        /// </summary>
        void CenterSpecBadges()
        {
            if (flwSpecChannels == null || flwSpecChannels.IsDisposed || flwSpecChannels.Parent == null) return;

            int content = 0;
            for (int i = 0; i < flwSpecChannels.Controls.Count; i++)
            {
                Control c = flwSpecChannels.Controls[i];
                content += c.Width + c.Margin.Left + c.Margin.Right;
            }

            int panelW = flwSpecChannels.Parent.ClientSize.Width;
            int sideMargin = Dpi.S(15);  // 与其它行左缘一致; 居中后左右各留这么多
            int leftBound = sideMargin;
            int rightBound = Math.Max(sideMargin, panelW - sideMargin);

            // 两侧控件还没加进父容器 (构造期早于 AddRange) 时按整面板算, 之后 LayoutTopRows 会再调一次
            int gap = Dpi.S(8);
            if (cmbSpec != null && cmbSpec.Parent == flwSpecChannels.Parent)
                leftBound = Math.Max(leftBound, cmbSpec.Right + gap);
            // 运行状态字在场时整条标注都是隐藏的 (互相替换), 两者不会争同一块空地
            if (btnAddOutput != null && btnAddOutput.Parent == flwSpecChannels.Parent)
                rightBound = Math.Min(rightBound, btnAddOutput.Left - gap);

            int usable = Math.Max(0, rightBound - leftBound);
            int w = Math.Min(content, usable);

            if (flwSpecChannels.Width != w) flwSpecChannels.Width = w;
            int left = leftBound + Math.Max(0, (usable - w) / 2);
            if (flwSpecChannels.Left != left) flwSpecChannels.Left = left;
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
