using System.Runtime.InteropServices;

namespace CoPul.App.Interop;

/// <summary>
/// 一块显示器的工作区，**单位是物理像素**（Win32 的 RECT 原生单位）。
///
/// <para>
/// 为什么另起一个类型而不是直接用 <see cref="Win32Window.MONITORINFO"/>：那个结构是给
/// WM_GETMINMAXINFO 用的，不带设备名。位置持久化（6.7）必须能认出"是哪块屏"，
/// 否则拔掉副屏后窗口会按旧坐标飞到不存在的地方。
/// </para>
/// </summary>
internal readonly record struct MonitorArea(string Device, int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;
    public int Height => Bottom - Top;

    public bool IsValid => !string.IsNullOrEmpty(Device) && Width > 0 && Height > 0;
}

/// <summary>
/// 多显示器与 DPI 的几件底层工具（需求 6.7）。
///
/// <para>
/// <b>全程用物理像素，不碰 DIP。</b>这是刻意的：项目是 <c>PerMonitorV2</c>，WPF 的
/// <c>Window.Left/Top</c> 单位是"窗口当前所在显示器的 DIP"，跨不同缩放的显示器时
/// 同一组数字对应两个物理位置。既然是存"相对比例"，就没必要在两套单位之间来回换 —— 
/// 比例只用 Win32 的物理矩形算，还原也直接用 <see cref="MoveWindowTo"/> 落到物理坐标，
/// 少一次换算就少一处能算错的地方（A-27 已经因为单位混用让窗口每次启动往右下角挪一截）。
/// </para>
/// </summary>
internal static class Monitors
{
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint MONITOR_DEFAULTTOPRIMARY = 1;
    private const int CCHDEVICENAME = 32;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_FRAMECHANGED = 0x0020;

    private const int GW_HWNDPREV = 3;

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MONITORINFOEX
    {
        public int cbSize;
        public Win32Window.RECT rcMonitor;
        public Win32Window.RECT rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = CCHDEVICENAME)]
        public string szDevice;
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(Win32Window.POINT point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFOEX info);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out Win32Window.RECT rect);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);

    /// <summary>Win10 1607 起可用。取不到时返回 96，后续按 100% 处理即可。</summary>
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hwnd);

    /// <summary>窗口当前所在的显示器（<c>DEFAULTTONEAREST</c>：窗口在屏幕外也给最近的那块）。</summary>
    public static bool TryForWindow(IntPtr hwnd, out MonitorArea area)
    {
        area = default;
        if (hwnd == IntPtr.Zero) return false;

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        return TryRead(monitor, out area);
    }

    /// <summary>按设备名找显示器。找不到返回 false —— 调用方回退主屏（6.7 的拔插规则）。</summary>
    public static bool TryFind(string? device, out MonitorArea area)
    {
        area = default;
        if (string.IsNullOrEmpty(device)) return false;

        foreach (MonitorArea candidate in All())
        {
            if (string.Equals(candidate.Device, device, StringComparison.OrdinalIgnoreCase))
            {
                area = candidate;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 主显示器。注意不能用 <c>EnumDisplayMonitors</c> 的第一项顶替 ——
    /// 枚举顺序是系统实现细节，多屏时首项不一定是主屏。
    /// </summary>
    public static bool TryPrimary(out MonitorArea area)
    {
        area = default;
        var origin = new Win32Window.POINT { X = 0, Y = 0 };
        IntPtr monitor = MonitorFromPoint(origin, MONITOR_DEFAULTTOPRIMARY);
        return monitor != IntPtr.Zero && TryRead(monitor, out area);
    }

    /// <summary>枚举当前所有显示器。顺序不保证，只用于按名字查找与兜底取第一块。</summary>
    public static IReadOnlyList<MonitorArea> All()
    {
        var list = new List<MonitorArea>(2);

        // 回调委托必须活到 EnumDisplayMonitors 返回之后，否则 GC 可能在回调途中回收它
        bool OnMonitor(IntPtr monitor, IntPtr hdc, IntPtr rect, IntPtr data)
        {
            if (TryRead(monitor, out MonitorArea a)) list.Add(a);
            return true;   // 继续枚举
        }

        MonitorEnumProc proc = OnMonitor;
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, proc, IntPtr.Zero);
        GC.KeepAlive(proc);
        return list;
    }

    private static bool TryRead(IntPtr monitor, out MonitorArea area)
    {
        area = default;
        var info = new MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<MONITORINFOEX>(),
            szDevice = string.Empty
        };
        if (!GetMonitorInfoW(monitor, ref info)) return false;

        area = new MonitorArea(info.szDevice ?? string.Empty,
            info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
        return area.IsValid;
    }

    /// <summary>窗口矩形，物理像素。</summary>
    public static bool TryGetRect(IntPtr hwnd, out Win32Window.RECT rect) => GetWindowRect(hwnd, out rect);

    /// <summary>
    /// 窗口所在显示器的**整屏**矩形（不扣任务栏），物理像素。附设备名。
    ///
    /// <para>
    /// <see cref="TryForWindow"/> 给的是工作区，判"窗口是否铺满整块屏"必须用整屏 ——
    /// 全屏检测（需求 8.4 辅判据）就是拿前台窗口矩形跟这个比。两者差一个任务栏的高度，
    /// 用错了会把"最大化窗口"也判成全屏。
    /// </para>
    /// </summary>
    public static bool TryMonitorBoundsForWindow(IntPtr hwnd, out Win32Window.RECT bounds, out string device)
    {
        bounds = default;
        device = string.Empty;
        if (hwnd == IntPtr.Zero) return false;

        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<MONITORINFOEX>(),
            szDevice = string.Empty
        };
        if (!GetMonitorInfoW(monitor, ref info)) return false;

        bounds = info.rcMonitor;
        device = info.szDevice ?? string.Empty;
        return true;
    }

    /// <summary>整屏矩形 + 工作区一起取，供全屏自动切换摆悬浮窗用（8.6 要落到全屏那块屏）。</summary>
    public static bool TryMonitorAreaForWindow(IntPtr hwnd, out MonitorArea screen)
    {
        screen = default;
        if (!TryMonitorBoundsForWindow(hwnd, out Win32Window.RECT bounds, out string device)) return false;

        screen = new MonitorArea(device, bounds.Left, bounds.Top, bounds.Right, bounds.Bottom);
        return screen.IsValid;
    }

    /// <summary>
    /// 只挪位置不改尺寸。用 <c>SetWindowPos</c> 而不是 <c>Window.Left/Top</c>：
    /// 后者按 DIP 解释，跨 DPI 时会落在错误位置；这里坐标本来就是物理像素，直接落地。
    /// </summary>
    public static void MoveWindowTo(IntPtr hwnd, int leftPx, int topPx)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, leftPx, topPx, 0, 0,
            SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 一次落下位置与尺寸，全部是物理像素。任务栏模式（需求第 7 章）用：
    /// 那个窗口的坐标必须与任务栏矩形精确对齐，走 DIP 会因取整差 1~2 像素，
    /// 表现为条与任务栏之间露一条缝。
    /// </summary>
    /// <param name="topmost">true = 同时提到 Z 序最顶（<c>HWND_TOPMOST</c>）。</param>
    public static void SetBounds(IntPtr hwnd, int x, int y, int width, int height, bool topmost = false)
    {
        if (hwnd == IntPtr.Zero) return;
        IntPtr after = topmost ? new IntPtr(-1) : IntPtr.Zero;
        uint flags = topmost ? SWP_NOACTIVATE : (SWP_NOZORDER | SWP_NOACTIVATE);
        SetWindowPos(hwnd, after, x, y, width, height, flags);
    }

    /// <summary>
    /// 通知系统"窗口框架变了"（<c>SWP_FRAMECHANGED</c>）。
    ///
    /// <para>
    /// 改过 <c>GWL_STYLE</c>（比如为了拿系统阴影加 <c>WS_THICKFRAME</c>）之后必须发一次这个，
    /// 否则 DWM 仍按旧框架缓存渲染，表现为"新样式没生效"。它不发 <c>WM_NCCALCSIZE</c> 之外的
    /// 副作用，位置与尺寸都锁住不动。
    /// </para>
    /// </summary>
    public static void NotifyFrameChanged(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 当前显示器缩放（1.0 = 100%）。吸附阈值 12 DIP 要换成物理像素才能和
    /// <see cref="TryGetRect"/> 的数值比较，否则 150% 屏上"12 像素"实际只有 8 DIP。
    /// </summary>
    public static double ScaleForWindow(IntPtr hwnd)
    {
        uint dpi = hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd);
        return dpi == 0 ? 1.0 : dpi / 96.0;
    }

    /// <summary>原始 DPI（96 = 100%）。取不到返回 0，调用方自行决定兜底值。</summary>
    public static uint DpiForWindow(IntPtr hwnd) => hwnd == IntPtr.Zero ? 0 : GetDpiForWindow(hwnd);

    /// <summary>
    /// 指定显示器的缩放（1.0 = 100%）。按**设备名**查，供"窗口还没 Show、拿不到自己的 HWND"
    /// 时使用 —— <see cref="ScaleForWindow"/> 依赖 HWND，而全屏自动切换要在 Show 之前就把
    /// 悬浮窗摆到目标屏上（否则会先在左上角闪一下再跳过去，实测 600ms 才归位）。
    ///
    /// <para>
    /// 走 <c>GetDpiForMonitor</c>（shcore.dll）。它返回的是"该显示器"的 DPI，与窗口无关；
    /// 混合 DPI 多屏时必须用这个，不能用主屏的 DPI 顶替（A-27）。
    /// 取不到时返回 1.0（按 100% 处理），调用方只用于预摆位，差几像素会被随后的物理像素校正吃掉。
    /// </para>
    /// </summary>
    public static double ScaleForMonitor(string device)
    {
        if (string.IsNullOrEmpty(device)) return 1.0;

        foreach (MonitorArea area in All())
        {
            if (!string.Equals(area.Device, device, StringComparison.OrdinalIgnoreCase)) continue;

            var origin = new Win32Window.POINT { X = area.Left, Y = area.Top };
            IntPtr monitor = MonitorFromPoint(origin, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return 1.0;

            if (TryGetDpiForMonitor(monitor, out uint dpiX, out _) && dpiX > 0) return dpiX / 96.0;
            return 1.0;
        }
        return 1.0;
    }

    private const int MDT_EFFECTIVE_DPI = 0;

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    private static bool TryGetDpiForMonitor(IntPtr monitor, out uint dpiX, out uint dpiY)
    {
        dpiX = dpiY = 96;
        try
        {
            // Win8.1+ 才有；老系统或受限环境下失败就按 100% 处理
            return GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out dpiX, out dpiY) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 把窗口提到 <c>HWND_TOPMOST</c> 组的最顶（M4.21）。
    ///
    /// <para>
    /// 位置、尺寸、激活状态一个都不动 —— 只改 Z 序。这样每秒巡检一次也不会有可见副作用：
    /// 不抢焦点、不重排、不闪。它同时会把 <c>WS_EX_TOPMOST</c> 补回来（style 有可能被别的操作清掉）。
    /// </para>
    /// </summary>
    public static bool RaiseToTopmost(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        return SetWindowPos(hwnd, new IntPtr(-1), 0, 0, 0, 0,          // -1 = HWND_TOPMOST
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    /// <summary>
    /// 打印 <paramref name="hwnd"/> 往上数 <paramref name="count"/> 个窗口（诊断用，M4.21）。
    /// 每条给句柄 / 类名 / 可见性 / 矩形 / 是否自家，用来判断"到底有没有被挡、被谁挡"。
    /// </summary>
    public static string DescribeChain(IntPtr hwnd, int count = 8)
    {
        if (hwnd == IntPtr.Zero) return "(无 HWND)";
        GetWindowRect(hwnd, out Win32Window.RECT self);

        var sb = new System.Text.StringBuilder();
        sb.Append($"self({self.Left},{self.Top})-({self.Right},{self.Bottom}) -> ");

        IntPtr cur = GetWindow(hwnd, GW_HWNDPREV);
        for (int i = 0; i < count && cur != IntPtr.Zero; i++)
        {
            GetWindowRect(cur, out Win32Window.RECT r);
            var cls = new System.Text.StringBuilder(64);
            GetClassNameW(cur, cls, cls.Capacity);

            sb.Append($"[{cls} {(IsWindowVisible(cur) ? "vis" : "hid")} " +
                      $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) {(IsOwnWindow(cur) ? "own" : "ext")}] ");
            cur = GetWindow(cur, GW_HWNDPREV);
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 窗口的类名 + 标题，诊断用（"到底被谁压住了"）。取不到返回 <c>"?"</c>。
    /// </summary>
    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(无)";

        var cls = new System.Text.StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);

        int len = GetWindowTextLengthW(hwnd);
        var text = new System.Text.StringBuilder(len + 1);
        if (len > 0) GetWindowTextW(hwnd, text, text.Capacity);

        GetWindowThreadProcessId(hwnd, out uint pid);
        string owner = IsOwnWindow(hwnd) ? "own" : $"pid={pid}";
        string visible = IsWindowVisible(hwnd) ? "vis" : "hidden";

        return $"0x{hwnd.ToInt64():X} [{cls}] '{text}' {owner} {visible}";
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, System.Text.StringBuilder cls, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, System.Text.StringBuilder text, int max);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hwnd);

    /// <summary>
    /// Z 序上排在 <paramref name="hwnd"/> **上面**、且真的会挡住它的第一个窗口；
    /// <c>IntPtr.Zero</c> = 没被挡。
    ///
    /// <para>
    /// 三个条件缺一不可，都是实测逼出来的：
    /// <list type="bullet">
    /// <item><b>可见</b>：系统给线程挂的 IME 窗口（<c>Default IME</c>）永远占 Z 序第一且不可见，
    /// 后面还跟着托盘消息窗、监听用的 <c>HwndSource</c> 这类没有界面的辅助窗口。</item>
    /// <item><b>有面积</b>：<c>ThumbnailDeviceHelperWnd</c> 之类的窗口 <c>IsWindowVisible</c>
    /// 为真但尺寸是 0 —— 它排在我们上面，可一个像素都不遮。</item>
    /// <item><b>矩形相交</b>：屏幕另一头的置顶窗口排在我们前面，但跟我们各占一块地方，
    /// 为它提顶没有任何意义，还会跟真正需要置顶的程序互相打架。</item>
    /// </list>
    /// 只看 <c>GW_HWNDPREV</c> 会得出"我永远被压着"，于是每秒提一次、一次都提不动（9 秒 9 次无效功）。
    /// </para>
    /// </summary>
    public static IntPtr FirstBlockingAbove(IntPtr hwnd, int maxHops = 64)
    {
        if (hwnd == IntPtr.Zero) return IntPtr.Zero;
        if (!GetWindowRect(hwnd, out Win32Window.RECT self)) return IntPtr.Zero;

        IntPtr cur = GetWindow(hwnd, GW_HWNDPREV);
        for (int i = 0; i < maxHops && cur != IntPtr.Zero; i++)
        {
            if (IsWindowVisible(cur)
                && GetWindowRect(cur, out Win32Window.RECT r)
                && r.Right > r.Left && r.Bottom > r.Top
                && r.Left < self.Right && r.Right > self.Left
                && r.Top < self.Bottom && r.Bottom > self.Top)
                return cur;

            cur = GetWindow(cur, GW_HWNDPREV);
        }
        return IntPtr.Zero;
    }

    /// <summary>窗口是否属于本进程。用来避免"跟自己家的设置页抢顶层"。</summary>
    public static bool IsOwnWindow(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        GetWindowThreadProcessId(hwnd, out uint pid);
        return pid == Environment.ProcessId;
    }

    /// <summary>
    /// 是否本进程**且可见**的窗口。让路只让给这种（M4.21）。
    ///
    /// <para>
    /// 不能只看"是不是自家"：本进程常驻着几个没有界面的辅助窗口（任务栏 <c>TaskbarCreated</c>
    /// 监听用的 <c>HwndSource</c>、托盘图标的消息窗口），它们同样排在 Z 序上方且永远不消失。
    /// 按"自家就不抢"判断，巡检会被它们永久挡住 —— 实测 <c>raiseHits</c> 一直是 0，
    /// 而用户看到的还是被别的置顶窗口压着。真正的诉求只是"别压住用户正在看的自家界面"。
    /// </para>
    /// </summary>
    public static bool IsOwnVisibleWindow(IntPtr hwnd)
        => hwnd != IntPtr.Zero && IsWindowVisible(hwnd) && IsOwnWindow(hwnd);

    /// <summary>
    /// 前台是不是一个**铺满整块显示器**的外部窗口（也就是"在玩游戏"）。
    ///
    /// <para>
    /// 为什么悬浮窗需要知道这件事（M4.32）：全屏游戏是唯一会**反复抢顶**的邻居 ——
    /// GLFW 进全屏会给自己加 <c>WS_EX_TOPMOST</c>，两个窗口同处 topmost 组，
    /// 谁最后 <c>SetWindowPos</c> 谁在上。WinEvent 能覆盖大部分抢顶路径，但不是全部
    /// （实测 <c>SetWindowPos(HWND_TOPMOST)</c> 只发 <c>LOCATIONCHANGE</c>，不发
    /// <c>REORDER</c>；有些游戏走的是别的路径，一次事件都没有），漏掉的那次就只剩
    /// 1Hz 巡检兜底 = 最多 1 秒看不见。全屏在前台时把巡检提到 4Hz，空窗压到 250ms 以内。
    /// </para>
    ///
    /// <para>
    /// 判据用"铺满显示器"而不是那套完整的全屏状态机：这里只要一个**便宜且宽松**的信号
    /// （一次 GetForegroundWindow + GetWindowRect + GetMonitorInfo，约 30µs），
    /// 不需要 QUNS 那套旁证 —— 万一误判成"有全屏应用"，代价只是多跑几次巡检。
    /// </para>
    ///
    /// <para>桌面、任务栏、以及本进程自己的窗口一律不算。</para>
    /// </summary>
    public static bool IsFullscreenForeground(IntPtr self, out IntPtr foreground)
    {
        foreground = Win32Window.ForegroundWindow;
        if (foreground == IntPtr.Zero || foreground == self) return false;
        if (IsOwnWindow(foreground)) return false;

        if (!GetWindowRect(foreground, out Win32Window.RECT r)) return false;
        if (r.Right - r.Left < 320 || r.Bottom - r.Top < 240) return false;

        if (!TryForWindow(foreground, out MonitorArea area)) return false;

        // 2px 容差：全屏窗口的矩形偶尔会因为 DWM 的非客户区差一两个像素。
        const int tol = 2;
        bool covers = r.Left <= area.Left + tol && r.Top <= area.Top + tol
                   && r.Right >= area.Right - tol && r.Bottom >= area.Bottom - tol;
        if (!covers) return false;

        var cls = new System.Text.StringBuilder(64);
        GetClassNameW(foreground, cls, cls.Capacity);
        string name = cls.ToString();

        // 桌面与任务栏本身就是铺满屏的，但它们不是"游戏"：
        // 把它们当成全屏应用会让巡检在正常用桌面时也一直跑 4Hz。
        return name is not ("Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd");
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    /// <summary>
    /// 主显示器的**整屏**矩形（不扣任务栏）。与 <see cref="TryPrimary"/> 的区别：
    /// 那个给的是工作区。任务栏模式要算"任务栏占了哪条边"，必须拿整屏减去工作区，
    /// 而只靠工作区永远只能算出"少了多少"，不知道少在哪一侧。
    /// </summary>
    public static bool TryPrimaryMonitorBounds(out Win32Window.RECT bounds)
    {
        bounds = default;
        var origin = new Win32Window.POINT { X = 0, Y = 0 };
        IntPtr monitor = MonitorFromPoint(origin, MONITOR_DEFAULTTOPRIMARY);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFOEX
        {
            cbSize = Marshal.SizeOf<MONITORINFOEX>(),
            szDevice = string.Empty
        };
        if (!GetMonitorInfoW(monitor, ref info)) return false;

        bounds = info.rcMonitor;
        return true;
    }
}
