using System;
using System.Collections.Generic;
using StereoSplitter.Infrastructure;
using StereoSplitter.Model;

namespace StereoSplitter.Infrastructure
{
    /// <summary>
    /// 输出卡片模型 ↔ ini 键值的编解码器 (P1-3② 第一步: 设置持久化的纯数据部分从 UI 类剥离)。
    /// ini 格式的键名与取值范围约束集中在此文件 —— 未来加键/改格式只动这里。
    /// 两个方法都是纯数据变换: 输入只有 settings 与 models, 不触碰任何控件。
    /// </summary>
    static class LaneSettingsCodec
    {
        /// <summary>可持久化的最大输出卡数量。UI 的"添加输出"应据此上限禁用按钮。</summary>
        public const int MaxOutputCards = 16;

        /// <summary>单张卡可持久化的最大声道行数。UI 补行时应据此上限约束。</summary>
        public const int MaxSlotsPerCard = 8;

        /// <summary>
        /// 编码前校验: 返回错误描述, null 表示可安全持久化。
        /// UI 可在保存/添加卡片前调用, 避免超限配置被静默截断 (F-24)。
        /// </summary>
        public static string Validate(List<OutputCardModel> models)
        {
            if (models == null) return "输出模型为空, 无法保存输出配置";
            if (models.Count > MaxOutputCards)
                return "输出卡数量 " + models.Count + " 超过上限 " + MaxOutputCards +
                       ", 拒绝保存 (否则重启后会静默丢失第 " + (MaxOutputCards + 1) + " 张及之后的卡片)";
            for (int i = 0; i < models.Count; i++)
            {
                OutputCardModel m = models[i];
                if (m == null) return "第 " + (i + 1) + " 张输出卡为空对象, 拒绝保存";
                if (m.Slots == null) return "第 " + (i + 1) + " 张卡的声道列表为空, 拒绝保存";
                if (m.Slots.Count > MaxSlotsPerCard)
                    return "第 " + (i + 1) + " 张卡的声道行数 " + m.Slots.Count +
                           " 超过上限 " + MaxSlotsPerCard + ", 拒绝保存 (否则重启后会静默丢失超出的声道行)";
            }
            return null;
        }

        /// <summary>从 ini 装载输出模型: 优先 Out* 新键, 没有则用旧的左右键合成两路。
        /// 调用方负责 cmbSpec 恢复、ReassignLanesToSpec 与 RebuildCards。</summary>
        public static void Decode(SettingsStore settings, List<OutputCardModel> models)
        {
            models.Clear();

            int count = settings.GetInt("OutCount", 0);
            if (count > 0)
            {
                // 读取端仍按共享上限钳制 (损坏/旧 ini 不扩大写盘数量), 与 Encode 上限一致
                for (int i = 0; i < count && i < MaxOutputCards; i++)
                {
                    string pk = "Out" + i + ".";
                    var m = new OutputCardModel
                    {
                        DeviceId = settings.GetStr(pk + "DeviceId") ?? "",
                        DelayMs = Math.Max(0, Math.Min(2000, settings.GetInt(pk + "DelayMs", 0))),
                        Volume = Math.Max(0, Math.Min(100, settings.GetInt(pk + "Volume", 100))),
                        Mute = settings.GetInt(pk + "Mute", 0) != 0
                    };
                    string channel = StrOrDefault(settings.GetStr(pk + "Channel"), "FL");
                    bool multi = settings.GetInt(pk + "Multi", 0) != 0 || channel == LaneRouting.MultiKey;

                    int n = Math.Max(0, Math.Min(MaxSlotsPerCard, settings.GetInt(pk + "ChCount", 0)));
                    for (int s = 0; s < n; s++)
                    {
                        string ck = pk + "Ch" + s + ".";
                        m.Slots.Add(new ChannelSlot
                        {
                            ChannelKey = settings.GetStr(ck + "Channel") ?? "",
                            Volume = Math.Max(0, Math.Min(100, settings.GetInt(ck + "Volume", 100)))
                        });
                    }

                    if (multi)
                    {
                        m.Multi = true;                 // 行数由 NormalizeModels 按设备声道数补齐
                        if (m.Slots.Count == 0) m.Slots.Add(new ChannelSlot { ChannelKey = "FL", Volume = 100 });
                    }
                    else
                    {
                        // 旧 ini (没有 Multi/Ch*): 一路 = 一张单声道卡片, 同设备的会在 Normalize 里合并
                        m.Slots.Add(new ChannelSlot
                        {
                            ChannelKey = channel,
                            Volume = Math.Max(0, Math.Min(100, settings.GetInt(pk + "Volume", 100)))
                        });
                    }
                    models.Add(m);
                }
            }
            else
            {
                // 老配置 (DeviceLeft/DeviceRight 那套) → 左右两张卡片
                var l = new OutputCardModel
                {
                    DeviceId = settings.GetStr("DeviceLeft") ?? "",
                    DelayMs = Math.Max(0, Math.Min(2000, settings.GetInt("DelayLeft", 0))),
                    Volume = Math.Max(0, Math.Min(100, settings.GetInt("VolumeLeft", 100))),
                    Mute = settings.GetInt("MuteLeft", 0) != 0
                };
                l.Slots.Add(new ChannelSlot { ChannelKey = "FL", Volume = l.Volume });
                models.Add(l);

                var r = new OutputCardModel
                {
                    DeviceId = settings.GetStr("DeviceRight") ?? "",
                    DelayMs = Math.Max(0, Math.Min(2000, settings.GetInt("DelayRight", 0))),
                    Volume = Math.Max(0, Math.Min(100, settings.GetInt("VolumeRight", 100))),
                    Mute = settings.GetInt("MuteRight", 0) != 0
                };
                r.Slots.Add(new ChannelSlot { ChannelKey = "FR", Volume = r.Volume });
                models.Add(r);
            }
        }

        /// <summary>把输出模型写入 ini 字典 (Out* 键)。先清干净旧键, 删过的卡/行不留孤儿键。
        /// F-24: 编码前拒绝超限 (不再静默截断), OutCount/ChCount 写实际持久化数量。</summary>
        public static void Encode(SettingsStore settings, List<OutputCardModel> models)
        {
            // 先校验再动 settings: 拒绝时一个 Put 都不做, 避免留下半张路由表。
            // 用异常而非返回值: 保持现有 Encode 签名不变, UI 的 SyncSettingsDict 已有 catch 会留痕。
            string error = Validate(models);
            if (error != null) throw new InvalidOperationException("无法保存输出配置: " + error);

            settings.RemovePrefix("Out");
            int outCount = Math.Min(models.Count, MaxOutputCards);
            settings.Put("OutCount", outCount.ToString());
            for (int li = 0; li < outCount; li++)
            {
                OutputCardModel m = models[li];
                string pk = "Out" + li + ".";
                // Channel 仍是"卡片声道": 单声道写声道键, 多声道写哨兵 (旧版本读到哨兵会退回第一行)
                settings.Put(pk + "Channel",
                    m.Multi ? LaneRouting.MultiKey : (m.Slots.Count > 0 ? m.Slots[0].ChannelKey : "FL"));
                settings.Put(pk + "DeviceId", m.DeviceId ?? "");
                settings.Put(pk + "DelayMs", m.DelayMs.ToString());
                settings.Put(pk + "Volume", m.Volume.ToString());
                settings.Put(pk + "Mute", m.Mute ? "1" : "0");
                settings.Put(pk + "Multi", m.Multi ? "1" : "0");
                int slotCount = Math.Min(m.Slots.Count, MaxSlotsPerCard);
                settings.Put(pk + "ChCount", slotCount.ToString());
                for (int s = 0; s < slotCount; s++)
                {
                    string ck = pk + "Ch" + s + ".";
                    settings.Put(ck + "Channel", m.Slots[s].ChannelKey ?? "");
                    settings.Put(ck + "Volume", m.Slots[s].Volume.ToString());
                }
            }
        }

        static string StrOrDefault(string s, string def)
        {
            return string.IsNullOrEmpty(s) ? def : s;
        }
    }
}
