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
}
