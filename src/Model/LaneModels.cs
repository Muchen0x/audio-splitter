using System.Collections.Generic;

namespace StereoSplitter.Model
{
    // ==================================================================================
    // 输出侧的纯数据模型: 卡片 (设备) → 声道行 → 内核输入 "路"。
    //
    // 这些类型原先定义在 UI/OutputCard.cs 里, 但 Translation (LaneRouting)、持久化
    // (LaneSettingsCodec) 与自检 (RoutingSelfTest) 都要用它们 —— 放在 UI 里就成了
    // "Infrastructure → UI" 的反向依赖。现在集中到 Model, 谁都能依赖, 且不含任何
    // 控件 / WinForms 引用。
    // ==================================================================================

    /// <summary>
    /// 内核输入: 一路 = 一个物理声道接一个逻辑声道。
    /// 由 <see cref="LaneRouting.Flatten"/> 从卡片模型生成 (音量已折算成矩阵增益)。
    /// </summary>
    class OutputLane
    {
        public string ChannelKey = "FL";   // 规格里的声道键: M/FL/FR/C/LFE/SL/SR/BL/BR
        public string DeviceId = "";
        public int DelayMs;
        public int Volume = 100;           // 进混音矩阵的有效音量 (端点承载时为 100)
        public bool Mute;
        public int PhysIndex = -1;         // 该设备上的物理声道号; -1 = 由内核按顺序分配
    }

    /// <summary>卡片里的一行 = 该设备的一个物理声道: 接哪个逻辑声道 + 该声道自己的音量。</summary>
    class ChannelSlot
    {
        public string ChannelKey = "";     // "" = 这个物理声道空着 (不出声)
        public int Volume = 100;
    }

    /// <summary>
    /// 一张输出卡片 = **一台设备** (同设备的多路会自动合并成一张)。
    /// <para>Multi=false: 只有一个声道, Slots[0].ChannelKey 就是卡片声道下拉的值。</para>
    /// <para>Multi=true: 声道下拉选"多声道", Slots 按物理声道 p0..pN-1 排列,
    /// 每行各自选逻辑声道并有独立音量条; 卡片主音量 = 设备端点音量。</para>
    /// </summary>
    class OutputCardModel
    {
        public string DeviceId = "";
        public int DelayMs;
        public int Volume = 100;           // 主音量: 写设备端点音量 (端点不可用时回落为增益)
        public bool Mute;
        public bool Multi;
        public readonly List<ChannelSlot> Slots = new List<ChannelSlot>();
    }
}
