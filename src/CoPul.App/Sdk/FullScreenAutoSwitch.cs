using System.Windows.Threading;

namespace CoPul.App.Sdk;

/// <summary>
/// 全屏自动切换的**状态机**（需求第 8 章）。只做三件事：防抖、带来源标记、按模式决定动作。
///
/// <para>
/// <b>为什么单独立一个类而不是写在 App 里。</b>这套逻辑有三样东西必须一起对：
/// 800/500ms 两个防抖门限、<c>floatOrigin ∈ {manual, auto}</c> 的来源标记、
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
/// <b>防抖为什么用时间戳而不是数 tick。</b>判定轮询是 500ms（8.4），而门限是 800ms/500ms，
/// 两者不是整数倍关系。数 tick 的话"进入 ≥800ms"要么是 1 tick（500ms，不够）要么是
/// 2 tick（1000ms，远超）；而且轮询间隔将来若调整，门限会跟着悄悄变。用"首次观察到该状态的
/// 时刻"做差就与轮询频率无关，门限永远是 800/500ms 本身。
/// </para>
/// </summary>
internal sealed class FullScreenAutoSwitch
{
    /// <summary>三个互斥视图（8.1：任意时刻只有一个在显示）。</summary>
    public enum View
    {
        Main,
        Float,
        Taskbar,
    }

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

    /// <summary>进入全屏须持续满足的时间（8.4 防抖）。</summary>
    public static readonly TimeSpan EnterHold = TimeSpan.FromMilliseconds(800);

    /// <summary>退出全屏须持续满足的时间（8.4 防抖）。</summary>
    public static readonly TimeSpan ExitHold = TimeSpan.FromMilliseconds(500);

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
        UseFullHwndCandidate && CurrentView == View.Float && FloatOrigin == Origin.Auto
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
    /// 属于"省下来是白赚"的量级。两个视图都不需要时直接停掉定时器，回到零成本。
    /// </para>
    ///
    /// <para>
    /// 需要的只有两种情形：<b>Taskbar</b>（要判断是否该切走）、
    /// <b>Float(auto)</b>（要判断是否该回落）。后者尤其不能漏 ——
    /// 一旦进入自动悬浮就停轮询，退出全屏永远检测不到，用户会卡在悬浮窗里。
    /// </para>
    /// </summary>
    public bool NeedsPolling =>
        CurrentView == View.Taskbar || (CurrentView == View.Float && FloatOrigin == Origin.Auto);

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
    /// 抽成可替换的委托是**为了探针能跑虚拟时间**：8.4 的两个门限是 800ms / 500ms，
    /// 而轮询间隔 500ms。真跑一遍要墙钟等 1.3 秒以上，探针里既难受又慢，一旦想连跑
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

            if (!_isFull && now - _fullSince >= EnterHold.TotalMilliseconds)
            {
                _isFull = true;
                ApplyFullScreen(info);
            }
        }
        else
        {
            _fullSince = 0;
            if (_nonFullSince == 0) _nonFullSince = now;

            if (_isFull && now - _nonFullSince >= ExitHold.TotalMilliseconds)
            {
                _isFull = false;
                ApplyExitFullScreen(info);
            }
        }

        Observed?.Invoke(info, $"steady={_isFull} view={CurrentView} origin={FloatOrigin}");
    }

    /// <summary>防抖后的稳态结论（探针与诊断用）。</summary>
    public bool IsFullScreenSteady => _isFull;

    /// <summary>进入全屏的动作。8.6：只有任务栏模式才动，大窗口与悬浮窗都不打断用户。</summary>
    private void ApplyFullScreen(Interop.FullScreenInfo info)
    {
        if (!Enabled)
        {
            Log($"enter-full: disabled, skip");
            return;
        }

        if (CurrentView == View.Main)
        {
            // 8.6「大窗口模式 + 进入全屏 → 不动作（用户主动在看明细，不应被打断）」
            Log("enter-full: view=Main, 不动作（8.6）");
            return;
        }

        if (CurrentView == View.Float)
        {
            // 已经在悬浮窗上：manual 保持 manual，auto 保持 auto，不重复迁移
            Log($"enter-full: view=Float origin={FloatOrigin}, 已在悬浮态");
            return;
        }

        // CurrentView == Taskbar：按 8.3 进入 Float(auto)
        FloatOrigin = Origin.Auto;
        _fullHwnd = info.Hwnd;      // 记下"谁把全屏顶起来的"，供前台变自己时改判（M4.22）
        LastTransition = $"Taskbar→Float(auto) hwnd=0x{info.Hwnd.ToInt64():X} monitor={info.MonitorDevice}";
        Log($"enter-full: {LastTransition} reason={info.Reason}");
        EnterAutoFloat?.Invoke(info.MonitorDevice);
        CurrentView = View.Float;

        // 进入 auto 悬浮后仍然需要轮询（要检测"什么时候退出全屏"），
        // 但开始/结束的条件变了，重算一次让定时器状态跟上。
        SyncTimer();
    }

    /// <summary>退出全屏的动作。8.2：只有 auto 来源才回落任务栏。</summary>
    private void ApplyExitFullScreen(Interop.FullScreenInfo info)
    {
        if (!Enabled)
        {
            Log("exit-full: disabled, skip");
            return;
        }

        if (CurrentView != View.Float)
        {
            Log($"exit-full: view={CurrentView}, 不动（只有 float(auto) 才回落）");
            return;
        }

        if (FloatOrigin != Origin.Auto)
        {
            // 8.2 的核心：manual 悬浮窗退出全屏后**保持悬浮**，绝不回落
            Log("exit-full: origin=Manual, 保持悬浮窗（8.2）");
            return;
        }

        LastTransition = "Float(auto)→Taskbar";
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
}
