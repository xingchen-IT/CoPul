using System.Windows.Threading;

namespace CoPul.App.Sdk;

/// <summary>
/// 全屏自动切换的**状态机**（需求第 8 章）。只做三件事：防抖、带来源标记、按模式决定动作。
///
/// <para>
/// <b>为什么单独立一个类而不是写在 App 里。</b>这套逻辑有三样东西必须一起对：
/// 防抖门限（M4.23 起是一个可调档位，进入与退出共用）、<c>floatOrigin ∈ {manual, auto}</c> 的来源标记、
/// 以及"当前处在哪个模式"。三样里任何一样写错，表现出来都是同一句话——
/// 「我明明手动选了悬浮窗，打完游戏它自己变回任务栏了」。把它收在一个可独立驱动的
/// 类里，就能用探针把状态迁移一条条跑出来，而不是靠人肉点游戏验证。
/// </para>
///
/// <para>
/// <b>8.2 的 floatOrigin 是整个设计的关键。</b>悬浮窗有两种来源：
/// 用户自己选的（manual）和全屏自动切过来的（auto）。只有 auto 在退出全屏时才回落任务栏。
/// 少了这个标记就会出现那个经典 bug：手动选了悬浮窗 → 开游戏 → 退游戏 → 悬浮窗没了。
/// </para>
///
/// <para>
/// <b>防抖为什么用时间戳而不是数 tick。</b>判定轮询是 500ms（8.4），而门限是 0.5s~3s 可调，
/// 两者不是整数倍关系。数 tick 的话"进入 ≥800ms"要么是 1 tick（500ms，不够）要么是
/// 2 tick（1000ms，远超）；而且轮询间隔将来若调整，门限会跟着悄悄变。用"首次观察到该状态的
/// 时刻"做差就与轮询频率无关，门限永远是档位本身。
/// </para>
/// </summary>
internal sealed class FullScreenAutoSwitch
{
    /// <summary>
    /// 三个视图。8.1 原定"任意时刻只有一个在显示"，M4.26 起按用户要求改成
    /// **可同时显示**（大窗口 + 悬浮窗 + 任务栏条任意组合）。
    /// </summary>
    public enum View
    {
        Main,
        Float,
        Taskbar,
    }

    /// <summary>
    /// 某个视图此刻是否在显示（由 App 注入，读的是窗口真实的可见状态）。
    ///
    /// <para>
    /// M4.26 之前这里靠 <see cref="CurrentView"/> 一个枚举判断"在哪个模式"，
    /// 三视图可以叠加之后那个枚举只够表示"最近操作的是谁"，判断可见性必须问窗口本人。
    /// 没注入时退回旧行为（<c>CurrentView == v</c>），这样只用状态机的探针不必改造。
    /// </para>
    /// </summary>
    public Func<View, bool>? IsShown { get; set; }

    private bool IsShownView(View v) => IsShown?.Invoke(v) ?? CurrentView == v;

    /// <summary>悬浮态来源（8.2）。</summary>
    public enum Origin
    {
        /// <summary>用户主动选悬浮窗：退出全屏<b>不回落</b>。</summary>
        Manual,
        /// <summary>全屏检测自动切过来：退出全屏<b>必须</b>回落任务栏。</summary>
        Auto,
    }

    /// <summary>判定轮询间隔。8.4 定 500ms。</summary>
    public static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(500);

    /// <summary>
    /// 防抖门限的默认值（M4.23 起进入与退出**共用**一个档位，见 <see cref="HoldMs"/>）。
    /// 8.4 原定进入 800 / 退出 500 两个数，合成一档后取 1000：
    /// 退出那一侧比原来稳一倍（挡得住 1s 内的 Alt+Tab），进入那一侧只慢 200ms。
    /// </summary>
    public static readonly TimeSpan DefaultHold = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// 可选档位（ms）。UI 与探针共用同一份数字，避免"设置页有 2s、探针认不出来"这种分叉。
    /// 门限越长越不容易被瞬时状态（切屏、弹窗、点一下悬浮窗）带偏，代价是切换反应变慢 ——
    /// 这个取舍只有用户自己知道，所以只给一个档位让他挑，不拆成两个数去为难他。
    /// </summary>
    public static readonly int[] HoldChoices = { 500, 1000, 2000, 3000 };

    /// <summary>
    /// 实际生效的门限（ms），进入与退出都用这一个值。改档位走 <see cref="SetHold"/>。
    /// </summary>
    public int HoldMs { get; private set; } = (int)DefaultHold.TotalMilliseconds;

    /// <summary>
    /// 取最接近的合法档位（与 <c>FloatWindow.NormalizeScale</c> 同一套取舍：不拒绝非法输入，
    /// 只校正）。手改配置写了 1500 也能用，不至于退回默认把用户的意图抹掉。
    /// </summary>
    public static int NormalizeHold(int ms)
    {
        int best = HoldChoices[0];
        foreach (int c in HoldChoices)
            if (Math.Abs(c - ms) < Math.Abs(best - ms)) best = c;
        return best;
    }

    /// <summary>改档位。**立即生效**（下一轮判定就按新门限走），不需要重启。</summary>
    public void SetHold(int ms)
    {
        HoldMs = NormalizeHold(ms);
        Log($"hold: {HoldMs}ms（进入与退出同一档）");
    }

    private readonly DispatcherTimer _timer;

    /// <summary>当前视图。<b>由 App 在每次切换后写回</b> —— 状态机不自己改它，
    /// 否则会出现"状态机以为切了、实际 App 没切"的分叉。</summary>
    public View CurrentView { get; set; } = View.Main;

    /// <summary>悬浮态来源（8.2）。切到任务栏 / 大窗口时归位 <see cref="Origin.Manual"/>。</summary>
    public Origin FloatOrigin { get; private set; } = Origin.Manual;

    /// <summary>
    /// 用户这次是不是**主动**选了悬浮窗。用户切到悬浮窗时调一次，
    /// 之后该悬浮窗就算 manual，退出全屏不回落。
    /// </summary>
    public void NoteUserChoseFloat() => FloatOrigin = Origin.Manual;

    /// <summary>用户切到任务栏或大窗口时调：来源标记归位（8.6 最后一行）。</summary>
    public void NoteUserLeftFloat()
    {
        FloatOrigin = Origin.Manual;
        _fullHwnd = IntPtr.Zero;    // 用户自己离开了悬浮态，全屏会话一并作废
    }

    /// <summary>当前记着的"全屏窗口"句柄（探针 / 诊断用）。</summary>
    public IntPtr FullHwnd => _fullHwnd;

    /// <summary>总开关（F-11 / 配置项）。关掉后计时器照跑但动作一律不发，便于现场复现问题。</summary>
    public bool Enabled { get; set; } = true;

    // ---- 由 App 注入的三个动作 ----
    // 刻意不用事件：调用方只有一个（App），事件会让人以为可能有多订阅者，
    // 而"谁 Hide 谁 Show"必须唯一（A-06 的编排原则）。

    /// <summary>进入全屏：任务栏 → 悬浮窗（origin=auto）。参数是全屏窗口所在显示器设备名（8.6 副屏规则）。</summary>
    public Action<string>? EnterAutoFloat { get; set; }

    /// <summary>退出全屏：悬浮窗(auto) → 任务栏（8.2 回落）。</summary>
    public Action? ExitToTaskbar { get; set; }

    /// <summary>读到一次探测结果时回调（探针/日志用，可为 null）。</summary>
    public Action<Interop.FullScreenInfo, string>? Observed { get; set; }

    // ---- 防抖状态 ----
    private long _fullSince;      // 首次观察到"全屏"的时刻（0 = 当前不是全屏）
    private long _nonFullSince;   // 首次观察到"非全屏"的时刻（0 = 当前是全屏）
    private bool _isFull;         // 防抖后的稳态结论

    /// <summary>
    /// 触发这次全屏的那个窗口句柄（M4.22）。
    ///
    /// <para>
    /// 为什么要记它：判定主体是前台窗口，而用户点一下悬浮窗就会把前台变成 CoPul 自己
    /// —— 那一刻"前台是谁"不再携带任何关于游戏的信息。记住句柄，才能在"前台是自己"
    /// 时改问一句"那个窗口还在不在全屏"。见 <see cref="Interop.FullScreenDetector.Probe(IntPtr)"/>。
    /// </para>
    /// </summary>
    private IntPtr _fullHwnd;

    /// <summary>
    /// 替补主体只在 <c>Float(auto)</c> 下给。别的视图里"前台是自己"完全正常
    /// （用户正在用 CoPul 自己的窗口），拿一个旧句柄去判会把这种情形误判成全屏。
    /// </summary>
    private IntPtr CandidateSubject =>
        UseFullHwndCandidate && IsShownView(View.Float) && FloatOrigin == Origin.Auto
            ? _fullHwnd
            : IntPtr.Zero;

    /// <summary>
    /// 是否启用替补主体。默认开；关掉就是 M4.22 之前的旧行为，A/B 对照用（<c>--no-fs-candidate</c>）。
    /// </summary>
    public bool UseFullHwndCandidate { get; set; } = true;

    /// <summary>最近一次迁移（诊断串），探针把它打进 sidecar。</summary>
    public string LastTransition { get; private set; } = "(none)";

    /// <summary>连续探测结果与迁移记录，供 <c>--fullscreen-probe</c> 输出。</summary>
    private readonly List<string> _journal = new(64);

    public IReadOnlyList<string> Journal => _journal;

    public FullScreenAutoSwitch()
    {
        _timer = new DispatcherTimer { Interval = PollInterval };
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>
    /// 是否真的需要这一次探测。
    ///
    /// <para>
    /// 8.6 已规定两种视图下全屏检测**不会产生任何动作**：大窗口（"用户主动在看明细，不应被打断"）
    /// 与 manual 悬浮窗（"遵循用户意愿，不自动回切"）。既然不会有动作，探测就是纯开销。
    /// 而实测单次探测 564 µs、2 Hz 轮询 = <b>0.11% 单核</b> —— 在 1.5% 总预算里占 7%，
    /// 属于"省下来是白赚"的量级。
    /// </para>
    ///
    /// <para>
    /// 需要的只有两种情形：<b>任务栏条在显示</b>（要判断是否该切走）、
    /// <b>auto 悬浮窗在显示</b>（要判断是否该回落）。后者尤其不能漏 ——
    /// 一旦进入自动悬浮就停轮询，退出全屏永远检测不到，用户会卡在悬浮窗里。
    /// </para>
    ///
    /// <para>
    /// M4.26 三视图可叠加后又多一条 <c>_fullSession</c>：全屏时把条收起来之后，
    /// 如果用户顺手把悬浮窗也关了，屏幕上就没有任何"需要检测的视图"了 ——
    /// 但只要这次会话欠着"把条放回来"，就必须继续轮询，否则退出全屏后条永远回不来。
    /// </para>
    /// </summary>
    public bool NeedsPolling =>
        _fullSession
        || IsShownView(View.Taskbar)
        || (IsShownView(View.Float) && FloatOrigin == Origin.Auto);

    /// <summary>
    /// 本次全屏会话是否还欠着"把任务栏条放回来"。
    /// 只有进全屏时**真的收过条**才置位；置位期间定时器不许停。
    /// </summary>
    private bool _fullSession;

    /// <summary>诊断用：本次全屏会话是否在等回落（探针 / sidecar）。</summary>
    public bool FullSessionActive => _fullSession;

    /// <summary>定时器一轮。不需要探测时直接返回，一次 Win32 调用都不发。</summary>
    private void Tick()
    {
        if (!Enabled || !NeedsPolling) return;
        Poll();
    }

    /// <summary>按需启停定时器。视图/来源变化后调一次（App 在 ShowView 末尾会调）。</summary>
    public void SyncTimer()
    {
        if (!_started) return;

        bool need = Enabled && NeedsPolling;
        if (need && _timer.IsEnabled) return;
        if (!need && !_timer.IsEnabled) return;

        if (need)
        {
            _timer.Start();
            Log("timer: start（当前视图需要检测）");
        }
        else
        {
            _timer.Stop();
            Log("timer: stop（当前视图不会产生动作，省掉探测）");
        }
    }

    private bool _started;

    public void Start()
    {
        _started = true;
        Log($"start enabled={Enabled} view={CurrentView} origin={FloatOrigin} polling={NeedsPolling}");
        SyncTimer();
    }

    public void Stop()
    {
        _started = false;
        _timer.Stop();
    }

    private static void TraceLog(string message) => Interop.Trace.Log("fullscreen", message);

    private void Log(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss.fff} {message}";
        _journal.Add(line);
        if (_journal.Count > 40) _journal.RemoveAt(0);
        TraceLog(message);
    }

    /// <summary>跑一轮判定。定时器每 500ms 调一次；探针也可以手动连调若干次。</summary>
    public void Poll()
    {
        // 替补主体只在"自动悬浮态"下给出去：那正是"用户点了悬浮窗 → 前台变自己"的场景，
        // 而别的视图下给了只会把"用户正在用 CoPul"误判成全屏。
        Poll(Interop.FullScreenDetector.Probe(CandidateSubject));
    }

    /// <summary>
    /// 时间源。默认 <c>Environment.TickCount64</c>（单调、不受改系统时间影响）。
    ///
    /// <para>
    /// 抽成可替换的委托是**为了探针能跑虚拟时间**：门限是 1s（8.4 原定进入 800 / 退出 500，
    /// M4.23 起合成一个可调档位），而轮询间隔 500ms。真跑一遍要墙钟等 2 秒以上，探针里既难受又慢，一旦想连跑
    /// 十几条迁移就是十几秒。用虚拟时钟可以在一帧内把整条链路推完，且门限判定走的是
    /// 同一个 <see cref="Poll"/> 代码 —— 验的是生产逻辑，不是另一套近似实现。
    /// </para>
    /// </summary>
    public Func<long> Clock { get; set; } = () => Environment.TickCount64;

    /// <summary>
    /// 用给定的一次探测结果驱动状态机。抽成可传入参数是为了让探针能喂伪造数据，
    /// 跑"任务栏 → 全屏 → 悬浮 → 退全屏 → 任务栏"整条链路而不必真开一个全屏游戏
    /// （那要独占显示器，跑一次改一次不可接受，与 <c>--force-edge</c> 的取舍一致）。
    /// </summary>
    public void Poll(Interop.FullScreenInfo info)
    {
        long now = Clock();

        if (info.IsFullScreen)
        {
            _nonFullSince = 0;
            if (_fullSince == 0) _fullSince = now;

            if (!_isFull && now - _fullSince >= HoldMs)
            {
                _isFull = true;
                ApplyFullScreen(info);
            }
        }
        else
        {
            _fullSince = 0;
            if (_nonFullSince == 0) _nonFullSince = now;

            if (_isFull && now - _nonFullSince >= HoldMs)
            {
                _isFull = false;
                ApplyExitFullScreen(info);
            }
        }

        Observed?.Invoke(info, $"steady={_isFull} view={CurrentView} origin={FloatOrigin}");
    }

    /// <summary>防抖后的稳态结论（探针与诊断用）。</summary>
    public bool IsFullScreenSteady => _isFull;

    /// <summary>
    /// 进入全屏的动作。M4.26：判据不再是"当前在哪个视图"，而是**任务栏条在不在显示** ——
    /// 条贴着任务栏、又是 topmost，全屏时它会直接压在游戏画面上，这是唯一需要收起来的东西。
    /// 只开大窗口（看明细）或只开悬浮窗时一律不动作（8.6 原意保留）。
    /// </summary>
    private void ApplyFullScreen(Interop.FullScreenInfo info)
    {
        if (!Enabled)
        {
            Log($"enter-full: disabled, skip");
            return;
        }

        if (!IsShownView(View.Taskbar))
        {
            // 8.6「大窗口模式 + 进入全屏 → 不动作」在三视图叠加后自然归并成这一条：
            // 条没显示就没有任何东西会挡住游戏，不需要打断用户。
            Log("enter-full: 任务栏条未显示，不动作（8.6）");
            return;
        }

        // 浮窗本来就在显示 = 用户自己开的，退出全屏别动它；
        // 不在显示 = 这次由全屏自动弹出来，退出时要负责收回去（8.2）。
        if (!IsShownView(View.Float)) FloatOrigin = Origin.Auto;

        _fullHwnd = info.Hwnd;      // 记下"谁把全屏顶起来的"，供前台变自己时改判（M4.22）
        _fullSession = true;
        LastTransition = $"收条→Float({(FloatOrigin == Origin.Auto ? "auto" : "keep-manual")}) " +
                         $"hwnd=0x{info.Hwnd.ToInt64():X} monitor={info.MonitorDevice}";
        Log($"enter-full: {LastTransition} reason={info.Reason}");
        EnterAutoFloat?.Invoke(info.MonitorDevice);
        CurrentView = View.Float;

        // 会话期间即使界面全被关掉也得继续探测（要等"退出全屏"把条放回来），重算定时器状态。
        SyncTimer();
    }

    /// <summary>
    /// 退出全屏的动作。M4.26：只对"进全屏时真的收过条"的会话负责 ——
    /// 放回任务栏条，并把**这次自动弹出来的**悬浮窗收回去；用户自己开的浮窗保持不动（8.2）。
    /// </summary>
    private void ApplyExitFullScreen(Interop.FullScreenInfo info)
    {
        if (!Enabled)
        {
            Log("exit-full: disabled, skip");
            return;
        }

        if (!_fullSession)
        {
            // 进全屏时条没显示过（或只是一直在看大窗口），这次退出不该凭空冒出个条来。
            Log($"exit-full: 本次未收起过任务栏条，不动作（view={CurrentView} origin={FloatOrigin}）");
            return;
        }

        _fullSession = false;
        LastTransition = FloatOrigin == Origin.Auto ? "Float(auto)→放回任务栏条" : "放回任务栏条（浮窗保留）";
        Log($"exit-full: {LastTransition} reason={info.Reason}");
        ExitToTaskbar?.Invoke();
        FloatOrigin = Origin.Manual;
        _fullHwnd = IntPtr.Zero;    // 全屏会话结束，替补主体作废
        CurrentView = View.Taskbar;
        SyncTimer();
    }

    /// <summary>把配置里的开关应用到状态机（启动时调一次）。</summary>
    public void ApplyConfig(bool enabled)
    {
        Enabled = enabled;
        SyncTimer();
    }

    /// <summary>启动时一次性应用：开关 + 防抖档位。</summary>
    public void ApplyConfig(bool enabled, int holdMs)
    {
        SetHold(holdMs);
        ApplyConfig(enabled);
    }
}
