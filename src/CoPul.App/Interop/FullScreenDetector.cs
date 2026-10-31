using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace CoPul.App.Interop;

/// <summary>
/// 一次全屏判定的**证据快照**。不只是一个 bool —— 判定由主判据 + 辅判据 + 否决项拼起来，
/// 只记结果的话，现场出问题（"游戏全屏了条没退"或"桌面下莫名切悬浮窗"）就完全无从下手。
/// </summary>
/// <param name="IsFullScreen">最终结论。</param>
/// <param name="Reason">人类可读的判定路径，例如 <c>quns=3 +rect -caption</c>。</param>
/// <param name="Quns"><c>SHQueryUserNotificationState</c> 的原始值（-1 = 调用失败）。</param>
/// <param name="Hwnd">前台窗口句柄。</param>
/// <param name="ClassName">前台窗口类名。</param>
/// <param name="Title">前台窗口标题。</param>
/// <param name="Pid">前台窗口所属进程。</param>
/// <param name="RectMatchesMonitor">前台矩形是否铺满所在显示器的整屏。</param>
/// <param name="Captionless">前台窗口是否无 <c>WS_CAPTION</c>。</param>
/// <param name="MonitorDevice">前台窗口所在显示器设备名。</param>
public readonly record struct FullScreenInfo(
    bool IsFullScreen,
    string Reason,
    int Quns,
    IntPtr Hwnd,
    string ClassName,
    string Title,
    uint Pid,
    bool RectMatchesMonitor,
    bool Captionless,
    string MonitorDevice)
{
    /// <summary>诊断用一行文本。<c>--fullscreen-probe</c> 与 trace 日志共用。</summary>
    public string Describe() =>
        $"full={IsFullScreen} reason={Reason} quns={Quns} hwnd=0x{Hwnd:X} " +
        $"class={ClassName} pid={Pid} \"{Title}\" " +
        $"rectMatch={RectMatchesMonitor} captionless={Captionless} monitor={MonitorDevice}";

    /// <summary>没有前台窗口 / 取不到任何证据时的空值。</summary>
    public static FullScreenInfo None(string reason) =>
        new(false, reason, -1, IntPtr.Zero, string.Empty, string.Empty, 0, false, false, string.Empty);
}

/// <summary>
/// 全屏检测（需求 8.4）。
///
/// <para>
/// <b>为什么这件事必须做，而且必须在 A 方案下做。</b>任务栏条是 <c>topmost</c> 顶层窗口
/// （7.1 的 A 方案，B 方案已因退出必留残影被弃用），它会盖在全屏游戏画面上 ——
/// 这不是观感问题，是"玩游戏时屏幕上凭空多一条仪表盘"。A 方案下没有任何别的办法绕开，
/// 只能自己检测全屏再自己让位。B 方案时代这条是白送的（子窗口被任务栏连带盖住），
/// 换 A 方案等于把这块债务接了回来。
/// </para>
///
/// <para>
/// <b>三套判据，缺一不可</b>（8.4 原文）：
/// <list type="number">
/// <item>主判据 <c>SHQueryUserNotificationState</c> ∈ {QUNS_BUSY, QUNS_RUNNING_D3D_FULL_SCREEN}</item>
/// <item>辅判据：前台矩形 == 所在显示器整屏 && 无 <c>WS_CAPTION</c> && 不含本进程窗口</item>
/// <item>否决项：前台是桌面 / 任务栏 / 开始菜单时一律不算全屏</item>
/// </list>
/// 主判据覆盖"独占全屏 + 无边框全屏"，辅判据兜住主判据偶发不报的情况（实测有些
/// 无边框 D3D 窗口在 QUNS 上只报 <c>QUNS_ACCEPTS_NOTIFICATIONS</c>）。
/// </para>
///
/// <para>
/// <b>为什么否决项不能省</b>：<c>QUNS_BUSY</c> 不是"全屏应用"专属 ——
/// Windows 在开始菜单全屏、锁屏前、专注时段等场景也会返回它。少了否决项，
/// 用户点一下开始菜单、或者系统进静默时段，任务栏条就会莫名其妙消失又回来。
/// </para>
///
/// <para>
/// <b>本类只做检测，不做任何策略。</b>防抖（进入 ≥800ms / 退出 ≥500ms）与时序都在
/// <see cref="CoPul.App.Sdk.FullScreenAutoSwitch"/>；"该不该真的切"由 App 判定
/// （还要看当前处在哪个模式、<c>floatOrigin</c> 是 manual 还是 auto）。
/// </para>
/// </summary>
internal static class FullScreenDetector
{
    // SHQueryUserNotificationState 的取值（shobjidl.h）
    private const int QUNS_NOT_PRESENT = 0;
    private const int QUNS_BUSY = 2;
    private const int QUNS_RUNNING_D3D_FULL_SCREEN = 3;
    private const int QUNS_PRESENTATION_MODE = 4;
    private const int QUNS_ACCEPTS_NOTIFICATIONS = 5;
    private const int QUNS_QUIET_TIME = 6;
    private const int QUNS_APP = 7;

    private const int GWL_STYLE = -16;
    private const int WS_CAPTION = 0x00C00000;   // WS_BORDER | WS_DLGFRAME

    /// <summary>
    /// 矩形比较的容差（物理像素）。无边框全屏窗口实测常有 1–2 像素的出入
    /// （显示器整屏矩形与窗口矩形在不同 DPI 感知路径下取整不同），严格相等会漏判。
    /// </summary>
    private const int RectTolerancePx = 2;

    /// <summary>这些类名出现时一律不算全屏（桌面、任务栏、开始菜单 / 搜索）。</summary>
    private static readonly HashSet<string> VetoClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman",                       // 桌面（Win10）
        "WorkerW",                       // 桌面（Win11，壁纸/图标层）
        "Shell_TrayWnd",                 // 主任务栏
        "Shell_SecondaryTrayWnd",        // 副屏任务栏
        "Windows.UI.Core.CoreWindow",    // 开始菜单 / 搜索 / 通知中心的 UWP 宿主
        "ApplicationManager_DesktopShellWindow",
        "StartMenuExperienceHost",
        "Shell_NotifyIcon",              // 托盘弹出层，偶尔会抢前台
    };

    [DllImport("shell32.dll")]
    private static extern int SHQueryUserNotificationState(out int state);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder name, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetWindowTextW")]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32Window.RECT rect);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsIconic(IntPtr hwnd);

    /// <summary>当前前台窗口（诊断 / 探针用，判定本身不用它）。</summary>
    public static IntPtr Foreground => GetForegroundWindow();

    /// <summary>命中否决类名时返回 true，并给出原因。</summary>
    private static bool IsVetoClass(string className, out string reason)
    {
        if (VetoClasses.Contains(className))
        {
            reason = "veto:" + className;
            return true;
        }
        reason = string.Empty;
        return false;
    }

    /// <summary>当前进程 id。用来排除"前台是我们自己的窗口"这种假全屏。</summary>
    private static uint SelfPid
    {
        get
        {
            // Process.Current 每次走内核查询，缓存一份即可（进程 id 不会变）
            if (_selfPid == 0) _selfPid = (uint)Environment.ProcessId;
            return _selfPid;
        }
    }

    private static uint _selfPid;

    /// <summary>
    /// 单次探测（判定主体 = 前台窗口），约 5 次 Win32 调用（<c>SHQueryUserNotificationState</c> 最重）。
    /// 由上层 500ms 轮询驱动（8.4：判定频率 500ms，成本可忽略）。
    /// </summary>
    public static FullScreenInfo Probe() => Probe(IntPtr.Zero);

    /// <summary>
    /// 单次探测，可带一个**替补判定主体**。
    ///
    /// <para>
    /// <b>为什么需要替补（M4.22 的真 bug）。</b>判定的主体一直是 <c>GetForegroundWindow()</c>，
    /// 隐含前提是"前台就是用户正在用的那个应用"。但全屏自动切到悬浮窗之后，用户点一下
    /// 悬浮窗，前台立刻变成 CoPul 自己的窗口 —— 于是走 <c>veto:self</c> 判成"不是全屏"，
    /// 500ms 后状态机就把视图回落任务栏；用户再点回游戏，800ms 后又切回悬浮窗。
    /// 表现就是"点一下回条、再点一下回悬浮窗"的来回抖。
    /// </para>
    ///
    /// <para>
    /// 根因不是 <c>veto:self</c> 这条规则错了（自己的窗口确实不能当全屏证据），
    /// 而是**主体选错了**：这时候该回答的问题是"刚才那个全屏窗口还在不在全屏"，
    /// 而不是"前台是不是全屏"。所以由调用方把上次判成全屏的窗口句柄传进来，
    /// 只有"前台是自己"这一条路径上会改判它。
    /// </para>
    ///
    /// <para>
    /// <b>替补只在那一刻生效，而且必须是活窗口</b>：句柄销毁（游戏关了）、最小化、
    /// 或隐藏时一律退回前台判定，否则会"游戏都退了还赖在悬浮窗"。
    /// </para>
    /// </summary>
    /// <param name="candidate">替补主体；<see cref="IntPtr.Zero"/> = 不用替补（与普通探测完全一致）。</param>
    public static FullScreenInfo Probe(IntPtr candidate)
    {
        IntPtr fg = GetForegroundWindow();
        int quns = QueryNotificationState();

        if (fg == IntPtr.Zero)
            return FullScreenInfo.None($"no-foreground quns={quns}");

        if (IsSelf(fg) && IsUsableSubject(candidate, fg))
            return Evaluate(candidate, quns, "self-focus→prev ");

        return Evaluate(fg, quns, string.Empty);
    }

    /// <summary>句柄是不是本进程的窗口（自己的窗口不能当全屏证据）。</summary>
    private static bool IsSelf(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == SelfPid;
    }

    /// <summary>替补主体能不能用：活着、可见、没最小化，且不是前台自己。</summary>
    private static bool IsUsableSubject(IntPtr candidate, IntPtr foreground)
    {
        if (candidate == IntPtr.Zero || candidate == foreground) return false;
        if (!IsWindow(candidate) || IsIconic(candidate)) return false;
        return IsWindowVisible(candidate);
    }

    /// <summary>对指定窗口做完整判定（主判据 + 辅判据 + 否决项）。</summary>
    private static FullScreenInfo Evaluate(IntPtr hwnd, int quns, string reasonPrefix)
    {
        var cls = new StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);
        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);

        uint pid = 0;
        GetWindowThreadProcessId(hwnd, out pid);

        bool rectMatches = false;
        bool captionless = false;
        string device = string.Empty;

        if (GetWindowRect(hwnd, out Win32Window.RECT rect) &&
            Monitors.TryMonitorBoundsForWindow(hwnd, out Win32Window.RECT monitor, out string monDevice))
        {
            device = monDevice;
            rectMatches = NearlyEqual(rect.Left, monitor.Left) && NearlyEqual(rect.Top, monitor.Top)
                          && NearlyEqual(rect.Right, monitor.Right) && NearlyEqual(rect.Bottom, monitor.Bottom);
        }

        captionless = ((GetWindowLongPtr(hwnd, GWL_STYLE) is var style
                        ? (long)style
                        : 0) & WS_CAPTION) == 0;

        string className = cls.ToString();

        // ---- 否决项最先判：桌面 / 任务栏 / 开始菜单无论 QUNS 说什么都不算全屏 ----
        if (IsVetoClass(className, out string vetoReason))
            return NotFull(reasonPrefix + vetoReason, quns, hwnd, className, title.ToString(),
                           pid, rectMatches, captionless, device);

        // 前台是我们自己的窗口（条 / 悬浮窗 / 大窗口）→ 不是全屏，避免"自己把自己判成全屏"
        if (pid == SelfPid)
            return NotFull(reasonPrefix + "veto:self", quns, hwnd, className, title.ToString(),
                           pid, rectMatches, captionless, device);

        bool auxFull = rectMatches && captionless;

        // 静默时段 / 应用模式 / 演示模式：单靠 QUNS 会误判，必须再要一条"矩形铺满且无标题栏"的旁证。
        // 8.5（Q-15）已定不做进程白名单，所以 PPT 放映 / 浏览器 F11 / 视频全屏一律照切。
        bool accordOnly = quns is QUNS_QUIET_TIME or QUNS_APP or QUNS_PRESENTATION_MODE;

        // 判定走 DecideIsFullScreen，与自检表同一份规则（避免"改一处忘一处"）
        bool isFull = DecideIsFullScreen(quns, rectMatches, captionless);

        string reason =
            (quns == QUNS_RUNNING_D3D_FULL_SCREEN ? $"quns={quns}(d3d)" : string.Empty) +
            (quns == QUNS_BUSY ? "quns=2(需几何旁证)" : string.Empty) +
            (rectMatches ? "+rect" : "-rect") +
            (captionless ? "-caption" : "+caption") +
            (auxFull ? " ⇒ aux-full" : string.Empty) +
            (accordOnly && isFull ? " (quns-weak+aux)" : string.Empty);
        if (reason.Length == 0 || !isFull) reason = "not-full: " + reason.Trim(' ', '+', '-');

        return new FullScreenInfo(isFull, (reasonPrefix + reason).Trim(), quns, hwnd, className,
                                  title.ToString(), pid, rectMatches, captionless, device);
    }

    /// <summary>构造一条"不是全屏"的证据（否决项 / 自己人）。</summary>
    private static FullScreenInfo NotFull(string reason, int quns, IntPtr hwnd, string className,
                                          string title, uint pid, bool rectMatches, bool captionless,
                                          string device) =>
        new(false, reason, quns, hwnd, className, title, pid, rectMatches, captionless, device);

    /// <summary>读 QUNS。失败返回 -1（不抛异常，判定逻辑按"无主判据"继续走辅判据）。</summary>
    public static int QueryNotificationState()
    {
        try
        {
            return SHQueryUserNotificationState(out int state) == 0 ? state : -1;
        }
        catch
        {
            // 极少数系统上 shell32 的这个导出不可用（被策略拦），退化成纯辅判据
            return -1;
        }
    }

    /// <summary>QUNS 数字 → 人类可读名，探针输出用。</summary>
    public static string QunsName(int quns) => quns switch
    {
        QUNS_NOT_PRESENT => "NOT_PRESENT(0)",
        QUNS_BUSY => "BUSY(2)",
        QUNS_RUNNING_D3D_FULL_SCREEN => "D3D_FULL_SCREEN(3)",
        QUNS_PRESENTATION_MODE => "PRESENTATION_MODE(4)",
        QUNS_ACCEPTS_NOTIFICATIONS => "ACCEPTS_NOTIFICATIONS(5)",
        QUNS_QUIET_TIME => "QUIET_TIME(6)",
        QUNS_APP => "APP(7)",
        1 => "PRESENTATION_MODE(1)",
        _ => $"?({quns})",
    };

    private static bool NearlyEqual(int a, int b) => Math.Abs(a - b) <= RectTolerancePx;

    /// <summary>
    /// 判定逻辑的**纯函数版**，供自检表用。与 <see cref="Probe"/> 走同一套规则，
    /// 抽出来是为了能穷举「QUNS × 矩形 × 标题栏」的组合而不必真开全屏应用。
    ///
    /// <para>
    /// <b>为什么这条规则值得单独自检。</b>踩过一次严重误判：前台是个普通浏览器窗口
    /// （矩形不铺满、有标题栏），QUNS 却报 2，按「8.4 原文只认 QUNS」的写法会把它判成全屏，
    /// 任务栏条平白消失。修法是「QUNS_BUSY / 弱判据 必须有几何旁证」，
    /// 但这同时是个**漏判风险点**：改狠了会把真全屏也判掉。
    /// 所以用表把两侧都锁死：误判那行必须为 false，真全屏那几行必须为 true。
    /// </para>
    /// </summary>
    internal static bool DecideIsFullScreen(int quns, bool rectMatches, bool captionless)
    {
        bool auxFull = rectMatches && captionless;

        // 3 = D3D 独占全屏：系统直接告诉我们是全屏，不需要几何旁证
        if (quns == QUNS_RUNNING_D3D_FULL_SCREEN) return true;

        // 几何铺满 + 无标题栏：这是「无边框全屏」的硬证据，不管 QUNS 说什么都认
        return auxFull;
    }
}
