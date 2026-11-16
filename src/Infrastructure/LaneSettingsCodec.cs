using System;
using System.Collections.Generic;
using StereoSplitter.Infrastructure;

namespace StereoSplitter.Infrastructure
{
    /// <summary>
    /// 输出卡片模型 ↔ ini 键值的编解码器 (P1-3② 第一步: 设置持久化的纯数据部分从 UI 类剥离)。
    /// ini 格式的键名与取值范围约束集中在此文件 —— 未来加键/改格式只动这里。
    /// 两个方法都是纯数据变换: 输入只有 settings 与 models, 不触碰任何控件。
    /// </summary>
    static class LaneSettingsCodec
    {
        /// <summary>从 ini 装载输出模型: 优先 Out* 新键, 没有则用旧的左右键合成两路。
        /// 调用方负责 cmbSpec 恢复、ReassignLanesToSpec 与 RebuildCards。</summary>
        public static void Decode(SettingsStore settings, List<OutputCardModel> models)
        {
            models.Clear();

            int count = settings.GetInt("OutCount", 0);
            if (count > 0)
            {
                for (int i = 0; i < count && i < 16; i++)
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

                    int n = Math.Max(0, Math.Min(8, settings.GetInt(pk + "ChCount", 0)));
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

        /// <summary>把输出模型写入 ini 字典 (Out* 键)。先清干净旧键, 删过的卡/行不留孤儿键。</summary>
        public static void Encode(SettingsStore settings, List<OutputCardModel> models)
        {
            settings.RemovePrefix("Out");
            settings.Put("OutCount", models.Count.ToString());
            for (int li = 0; li < models.Count && li < 16; li++)
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
                settings.Put(pk + "ChCount", m.Slots.Count.ToString());
                for (int s = 0; s < m.Slots.Count && s < 8; s++)
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
