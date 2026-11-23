namespace StereoSplitter.Model
{
    /// <summary>
    /// 分流链路的应用级状态。
    /// <para>放在 Model 层而不是 <c>MainForm</c> 的嵌套类型里，是为了让<b>状态机本身</b>能进自检：
    /// 迁移表写漏一条边，表现是日志里刷一条"迁移越界"，而不是功能坏掉 —— 这种错只有穷举
    /// 测试才拦得住，而自检跑在 Infrastructure 层，够不到 UI 的嵌套类型。</para>
    /// </summary>
    enum AppState { Ready, Starting, Running, Stopped, Failed }

    /// <summary>
    /// 状态迁移的唯一权威定义（R-07）。
    ///
    /// <para><b>背景</b>：同一个故障会同时流过四条路径 —— 整链路重建（<c>RestartEngine</c>）、
    /// 单路重建（<c>BeginSinkRestart</c>）、端点回归轮询（<c>DeviceReturnTick</c>）、
    /// 看门狗（<c>WatchdogTick</c>）。它们各自调 <c>SetAppState</c>，以前没有任何一处集中
    /// 表达"哪些迁移是允许的"，只能靠 <c>engineGeneration</c> 与 <c>ReferenceEquals</c>
    /// 两道校验在事后兜。这张表把"允许"写成一个可穷举、可自检的集合。</para>
    ///
    /// <para><b>只判不拦</b>：<c>SetAppState</c> 遇到越界迁移只记一条警告，不改行为。
    /// 理由 —— 拦错的代价（恢复路径被冻住、界面卡在"启动中"）远大于记错的代价（日志多一行），
    /// 而这个项目的验证手段只有自检与冒烟测试，不宜在这里引入新的阻断点。</para>
    ///
    /// <para><b>自迁移（X→X）一律允许</b>：<c>SetAppState</c> 是幂等重设 ——
    /// <c>MainForm_Load</c> 会把初始的 Ready 再设一遍，<c>RestartEngine</c> 在"启动中就又判
    /// 停摆"时也会重设 Starting，<c>GiveUp</c> 可能对已经 Failed 的状态再来一次。</para>
    /// </summary>
    static class AppStateMachine
    {
        public static readonly AppState[] All =
        {
            AppState.Ready, AppState.Starting, AppState.Running, AppState.Stopped, AppState.Failed
        };

        /// <summary>日志用的短名。故意用英文枚举名：日志要能 grep，中文文案留给界面。</summary>
        public static string Text(AppState s)
        {
            switch (s)
            {
                case AppState.Starting: return "Starting";
                case AppState.Running: return "Running";
                case AppState.Stopped: return "Stopped";
                case AppState.Failed: return "Failed";
                default: return "Ready";
            }
        }

        /// <summary>
        /// 允许的迁移集合。每条边的语义：
        /// <list type="bullet">
        /// <item>Ready→Starting：点开始；Ready→Failed：Load 期间初始化失败</item>
        /// <item>Starting→Running：链路建成；→Stopped：启动中被停止；→Failed：启动失败或放弃</item>
        /// <item>Running→Starting：整链路重建；→Stopped：用户停止；→Failed：看门狗放弃</item>
        /// <item>Stopped→Starting / Failed→Starting：再次点开始</item>
        /// </list>
        /// 刻意不包含的：任何 →Ready（Ready 只表示"进程起来了、还没开始过"）、
        /// Stopped→Failed / Failed→Stopped（放弃与停止各有专属入口，互相穿越说明两条路径同时动手）。
        /// </summary>
        public static bool CanTransition(AppState from, AppState to)
        {
            if (from == to) return true;                 // 幂等重设，见类注释

            switch (from)
            {
                case AppState.Ready:
                    return to == AppState.Starting || to == AppState.Failed;
                case AppState.Starting:
                    return to == AppState.Running || to == AppState.Stopped || to == AppState.Failed;
                case AppState.Running:
                    return to == AppState.Starting || to == AppState.Stopped || to == AppState.Failed;
                case AppState.Stopped:
                    return to == AppState.Starting;
                case AppState.Failed:
                    return to == AppState.Starting;
                default:
                    return false;
            }
        }
    }
}
