using System.Runtime.InteropServices;

namespace CoPul.App.Interop;

/// <summary>
/// 无边框窗口需要的两件套：<c>WM_GETMINMAXINFO</c> 与显示器工作区。
///
/// 为什么必须有：<c>WindowStyle=None</c> 的窗口最大化时，系统按**整个屏幕**而不是工作区算尺寸，
/// 结果就是盖住任务栏——这是自绘标题栏最常见的一个缺陷。需求 5.4 要求"拖边缘改尺寸、双击最大化"，
/// 所以这里不是可选项。
///
/// 另外附带圆角的两条路（A-31）：Win11 走 DWM 属性（系统裁剪 + 抗锯齿，零成本），
/// Win10 没有这个属性，只能退到 <c>SetWindowRgn</c>（锯齿但能看）。
/// </summary>
internal static class Win32Window
{
    public const int WM_GETMINMAXINFO = 0x0024;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    /// <summary>DWMWA_WINDOW_CORNER_PREFERENCE，Win11 22000 起支持。</summary>
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;

    /// <summary>DWM_WINDOW_CORNER_PREFERENCE.DWMWCP_ROUND。</summary>
    private const int DWMWCP_ROUND = 2;

    /// <summary>DWMWA_BORDER_COLOR，Win11 22000 起支持（Win10 返回失败，无害）。</summary>
    private const int DWMWA_BORDER_COLOR = 34;

    /// <summary>DWMWA_COLOR_NONE：让 DWM 不画那条 1px 窗口边框。</summary>
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    /// <summary>DWMWA_COLOR_DEFAULT：恢复系统默认（跟随主题）。</summary>
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRoundRectRgn(int left, int top, int right, int bottom,
                                                    int widthEllipse, int heightEllipse);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hwnd, IntPtr region, bool redraw);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    /// <summary>
    /// 取光标位置（屏幕物理像素）。
    ///
    /// <para>
    /// 7.6 的 hover tooltip 要用：任务栏条默认穿透（Q-12 方案 2），<c>WM_MOUSEMOVE</c>
    /// 根本不会投给条，只能自己问系统光标在哪。**不要**用 WPF 的
    /// <c>Mouse.GetPosition</c> —— 那要有一个能收到鼠标事件的窗口，穿透时正是没有。
    /// </para>
    /// </summary>
    public static bool TryGetCursor(out POINT point) => GetCursorPos(out point);

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    private const uint MONITOR_DEFAULTTONULL = 0;

    /// <summary>
    /// 取窗口所在显示器的工作区，单位是**物理像素**。调用方负责用 DIP 比例换算——
    /// WPF 的 <see cref="System.Windows.Window.Left"/> 是 DIP，Win32 的 RECT 是物理像素，
    /// 两者在 150% 缩放下差 1.5 倍，混用会让窗口每启动一次就往右下角挪一截。
    /// </summary>
    /// <returns>false = 矩形完全不在任何显示器上（或取工作区失败），调用方应回退到居中。</returns>
    public static bool TryGetWorkArea(double leftPx, double topPx, double widthPx, double heightPx,
                                      out RECT work)
    {
        work = default;

        var rect = new RECT
        {
            Left = (int)Math.Round(leftPx),
            Top = (int)Math.Round(topPx),
            Right = (int)Math.Round(leftPx + widthPx),
            Bottom = (int)Math.Round(topPx + heightPx)
        };

        // DEFAULTTONULL：不在任何显示器上时返回 IntPtr.Zero（而不是像 NEAREST 那样硬给一个）
        IntPtr monitor = MonitorFromRect(ref rect, MONITOR_DEFAULTTONULL);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return false;

        work = info.rcWork;
        return true;
    }

    /// <summary>
    /// 把最大化尺寸限制到当前显示器的工作区（扣掉任务栏）。
    /// </summary>
    /// <returns>true = 已改写 lParam，调用方应把 handled 置 true。</returns>
    public static bool TryClampToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return false;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // ptMaxPosition 是相对显示器左上角的偏移，不是屏幕坐标
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;

        // 不限制 ptMaxTrackSize 时，拖到屏幕边缘仍可能超出工作区
        mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
        mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;

        Marshal.StructureToPtr(mmi, lParam, true);
        return true;
    }

    /// <summary>
    /// 让 Win11 的 DWM 给窗口裁圆角。成功时系统自己按当前 DPI 处理半径并做抗锯齿，
    /// 我们不需要再碰 Region；最大化时 DWM 也会自动恢复直角，不用额外判断。
    /// </summary>
    /// <returns>false = 这张系统不认识该属性（Win10 及更早），调用方应退到 <see cref="ApplyRoundRegion"/>。</returns>
    public static bool TryEnableDwmRoundCorners(IntPtr hwnd)
    {
        int preference = DWMWCP_ROUND;
        return DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref preference,
                                     sizeof(int)) == 0;
    }

    /// <summary>
    /// 关掉 DWM 给窗口画的那条 1px 边框。
    ///
    /// <para>
    /// <b>为什么需要</b>：悬浮窗为了拿系统阴影给窗口加了 <c>WS_THICKFRAME</c>（见
    /// <c>FloatWindow.EnableSystemShadow</c>），DWM 从此把它当成"有框架的窗口"，
    /// 除了阴影之外还会在外沿画一条边框。这条边框的颜色默认跟随主题 ——
    /// 浅色主题下是近白色，静止时被圆角与阴影挡住不明显，<b>一旦拖动（进 move loop）就整圈亮起来</b>，
    /// 在深色壁纸上像贴了一圈白胶带。
    /// </para>
    ///
    /// <para>
    /// <b>为什么不动 <c>WS_THICKFRAME</c></b>：那是阴影的唯一开关（M2 报告 2.11 已证伪别的路），
    /// 去掉边框比去掉阴影更划算 —— 阴影是"这扇窗浮在桌面上"的主要线索。
    /// </para>
    ///
    /// <para>
    /// Win10 没有这个属性，调用返回非 0，忽略即可（那边本来也没有 Win11 的圆角边框）。
    /// </para>
    /// </summary>
    /// <returns>true = 系统接受了设置。</returns>
    public static bool TryDisableDwmBorder(IntPtr hwnd) => TrySetDwmBorderColor(hwnd, DWMWA_COLOR_NONE);

    /// <summary>
    /// 直接指定 DWM 边框色。<paramref name="colorRef"/> 用 Win32 的 <c>COLORREF</c>
    /// （<c>0x00BBGGRR</c>，注意红蓝是反的），或用上面两个语义常量。
    ///
    /// <para>
    /// 之所以留"随便给个颜色"这条路：白边到底是不是这条属性画的，光看"关掉之后没了"不够
    /// —— 桌面背景随时在变。把边框涂成纯红，拖一下就知道管辖范围。
    /// </para>
    /// </summary>
    public static bool TrySetDwmBorderColor(IntPtr hwnd, int colorRef)
    {
        if (hwnd == IntPtr.Zero) return false;
        return DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref colorRef, sizeof(int)) == 0;
    }

    /// <summary>把 DWM 边框色恢复成系统默认（自测开关 <c>--float-border default</c> 用）。</summary>
    public static bool TryResetDwmBorder(IntPtr hwnd) => TrySetDwmBorderColor(hwnd, DWMWA_COLOR_DEFAULT);

    /// <summary>
    /// 旧系统的圆角兜底：直接把窗口区域裁成圆角矩形。
    ///
    /// 代价说明：Region 是 1 位掩码，**没有抗锯齿**，8px 半径下边缘会有轻微锯齿；
    /// 换来的是不需要 <c>AllowsTransparency</c>（那个会关掉硬件加速，与 1.5% CPU 预算直接冲突）。
    /// 只在 Win10 上走到这里，Win11 走 DWM 不受影响。
    /// </summary>
    /// <param name="round">false = 恢复成完整矩形（最大化时用）。</param>
    public static void ApplyRoundRegion(IntPtr hwnd, int widthPx, int heightPx, int radiusPx, bool round)
    {
        if (hwnd == IntPtr.Zero || widthPx <= 0 || heightPx <= 0) return;

        if (!round)
        {
            // 传 IntPtr.Zero 表示取消区域限制，窗口回到完整矩形
            SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }

        // CreateRoundRectRgn 的右/下是开区间，+1 才不会切掉最后一列像素；
        // 椭圆参数是**直径**，所以要 2×半径。
        IntPtr region = CreateRoundRectRgn(0, 0, widthPx + 1, heightPx + 1, radiusPx * 2, radiusPx * 2);
        if (region == IntPtr.Zero) return;

        // 成功时系统接管 region 的所有权；失败时得自己删，否则每次改尺寸漏一个 GDI 对象
        if (SetWindowRgn(hwnd, region, true) == 0) DeleteObject(region);
    }
}
