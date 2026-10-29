using System.Runtime.InteropServices;
using System.Text;

namespace CoPul.App.Interop;

/// <summary>
/// 任务栏（需求第 7 章）的底层访问。单位一律是**物理像素**，与 <see cref="Monitors"/> 保持一致。
///
/// <para>
/// 为什么不能拿 <see cref="Monitors"/> 的工作区凑合：工作区按定义是"显示器减去任务栏"，
/// 里面根本不包含任务栏，量不出任务栏有多高、图标区从哪开始。任务栏必须走
/// <c>Shell_TrayWnd</c> 这个窗口句柄自己量。
/// </para>
/// </summary>
internal static class Taskbar
{
    private const uint GW_HWNDNEXT = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowW(string? className, string? windowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowExW(IntPtr parent, IntPtr after, string? className, string? windowName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetClientRect(IntPtr hwnd, out RECT rect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsWindowVisible(IntPtr hwnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hwnd, StringBuilder buffer, int size);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hwnd, StringBuilder buffer, int size);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr hwnd, uint cmd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetParent(IntPtr child, IntPtr parent);

    [DllImport("user32.dll")]
    private static extern IntPtr GetParent(IntPtr hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after,
                                            int x, int y, int cx, int cy, uint flags);

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    // 用 long 而不是 int：0x80000000 在 int 下是负数常量，转 uint 时编译器拒绝编译期转换
    private const long WS_CHILD = 0x40000000L;
    private const long WS_POPUP = 0x80000000L;

    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOZORDER = 0x0004;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_FRAMECHANGED = 0x0020;

    /// <summary>
    /// 把窗口挂到任务栏下（需求 7.1 的 B 方案）。
    ///
    /// <para>
    /// <paramref name="asChildWindow"/> 决定要不要真的加 <c>WS_CHILD</c>：
    /// 只是 <c>SetParent</c> 而不改样式，窗口仍然带 <c>WS_POPUP</c>，
    /// 属于"被裁剪的弹出窗口"而不是真正子窗口 —— 它会跟着父窗口一起动，
    /// 但 Z 序、裁剪和命中测试的行为都不可靠。两种都试一遍是因为
    /// 跨进程 <c>SetParent</c> 在 Win11 上还有溢出/闪烁的已知差异，得实测。
    /// </para>
    /// </summary>
    /// <returns>true = SetParent 返回值非零（成功）。</returns>
    public static bool Attach(IntPtr child, IntPtr parent, bool asChildWindow)
    {
        if (child == IntPtr.Zero || parent == IntPtr.Zero) return false;

        if (asChildWindow)
        {
            IntPtr style = GetWindowLongPtr(child, GWL_STYLE);
            long next = ((long)style | WS_CHILD) & ~WS_POPUP;
            SetWindowLongPtr(child, GWL_STYLE, (IntPtr)next);
        }

        // SetParent 失败时返回 IntPtr.Zero，且 LastError 才有意义
        IntPtr previous = SetParent(child, parent);

        // 子窗口必须重发 FRAMECHANGED，否则 DWM 仍按顶层窗口缓存它的位置
        SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        return previous != IntPtr.Zero || GetParent(child) == parent;
    }

    /// <summary>把窗口从任务栏摘下来，恢复成顶层窗口。</summary>
    public static void Detach(IntPtr child, bool wasChildWindow)
    {
        if (child == IntPtr.Zero) return;

        SetParent(child, IntPtr.Zero);

        if (wasChildWindow)
        {
            IntPtr style = GetWindowLongPtr(child, GWL_STYLE);
            long next = ((long)style & ~WS_CHILD) | WS_POPUP;
            SetWindowLongPtr(child, GWL_STYLE, (IntPtr)next);
            SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
                SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
        }
    }

    /// <summary>
    /// 把窗口挪到任务栏客户区里的 (x, y)，尺寸用物理像素。
    /// 挂载之后 <see cref="Win32Window.RECT"/> 与 <c>Window.Left</c> 都不再是屏幕坐标，
    /// 只有 <c>SetWindowPos</c> 直接下物理像素才可控。
    /// </summary>
    public static void PlaceInTaskbar(IntPtr child, int x, int y, int width, int height)
    {
        if (child == IntPtr.Zero) return;
        SetWindowPos(child, IntPtr.Zero, x, y, width, height,
            SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);
    }

    /// <summary>
    /// 放到父窗口 Z 序的最顶（<c>HWND_TOP</c>）。
    ///
    /// <para>
    /// 这一条是 Win11 任务栏实验里补出来的：<c>SetParent</c> 成功、矩形也对，但窗口
    /// 一像素都不显示 —— 任务栏的第一个子窗口是
    /// <c>Windows.UI.Composition.DesktopWindowContentBridge</c>（XAML 岛），
    /// 它整条覆盖任务栏，新挂进来的 GDI 子窗口排在它下面就被完全遮住。
    /// </para>
    /// </summary>
    public static void BringToTop(IntPtr child)
    {
        if (child == IntPtr.Zero) return;
        SetWindowPos(child, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
    }

    public static IntPtr ParentOf(IntPtr hwnd) => GetParent(hwnd);

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern uint GetPixel(IntPtr hdc, int x, int y);

    /// <summary>
    /// 采样任务栏实际显示出来的平均亮度（0–255）。
    ///
    /// <para>
    /// 为什么不用注册表的 <c>SystemUsesLightTheme</c>：那个只说明"系统主题是浅色"，
    /// 而 Win11 的任务栏是<b>跟随壁纸</b>的半透明层 —— 实测这台机器上
    /// <c>SystemUsesLightTheme=1</c>，任务栏底色却是深蓝灰（壁纸是暗色星球）。
    /// 按注册表判断会给深色底配深色字，直接读不清。
    /// </para>
    ///
    /// <para>
    /// 采样点取托盘左侧那段空白：图标区在中间、时钟在最右，那两处一定有内容。
    /// </para>
    /// </summary>
    public static double SampleLuma(IntPtr bar)
    {
        if (bar == IntPtr.Zero || !GetWindowRect(bar, out RECT r)) return 128;

        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return 128;

        try
        {
            int y = (r.Top + r.Bottom) / 2;
            double sum = 0;
            int count = 0;

            // 取任务栏右侧 1/8 宽度里的几个点（托盘左边、时钟右边的空白）
            for (int i = 1; i <= 5; i++)
            {
                int x = r.Right - (r.Right - r.Left) / 8 - i * 12;
                if (x <= r.Left) break;

                uint c = GetPixel(dc, x, y);
                if (c == 0xFFFFFFFF) continue;   // CLR_INVALID

                int rr = (int)(c & 0xFF);
                int gg = (int)((c >> 8) & 0xFF);
                int bb = (int)((c >> 16) & 0xFF);
                sum += 0.299 * rr + 0.587 * gg + 0.114 * bb;
                count++;
            }

            return count == 0 ? 128 : sum / count;
        }
        finally
        {
            ReleaseDC(IntPtr.Zero, dc);
        }
    }

    /// <summary>
    /// 把任务栏临时提到 Z 序最顶（<c>HWND_TOPMOST</c>）。
    ///
    /// <para>
    /// 只给自测用：任务栏被全屏应用盖住时，屏幕抓取拍不到它，也就没法验证
    /// 子窗口到底有没有被合成出来。<c>PrintWindow</c> 走不通 —— 实测返回 <c>true</c>
    /// 却是**旧的重定向表面**（拍到上一轮实验留下的红块，而那时任务栏上什么都没有）。
    /// </para>
    /// </summary>
    public static void RaiseForCapture(IntPtr bar, bool topmost)
    {
        if (bar == IntPtr.Zero) return;
        IntPtr after = topmost ? new IntPtr(-1) : new IntPtr(-2);   // HWND_TOPMOST / HWND_NOTOPMOST
        SetWindowPos(bar, after, 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010);   // NOSIZE|NOMOVE|NOACTIVATE
    }

    /// <summary>
    /// 任务栏左侧的空白宽度（物理像素），从任务栏左边缘算到"不该被盖住的第一个东西"。
    ///
    /// <para>
    /// 优先级：开始按钮 → 应用图标区（<c>ReBarWindow32</c>）→ 托盘区 → 退化成任务栏宽的 1/4。
    /// 为什么要三级：Win11 把开始按钮做成 XAML 岛之后，<c>Start</c> 子窗口仍然存在但
    /// 常常 <c>IsWindowVisible=false</c>（实测就是），这时它依然有个有效矩形可用；
    /// 若连句柄都没有（某些 24H2 构建），退到图标区左边也不会盖住用户的东西。
    /// </para>
    /// </summary>
    /// <returns>0 = 一个都没量到，调用方应放弃在任务栏显示。</returns>
    public static int FreeLeftWidth(IntPtr bar)
    {
        if (bar == IntPtr.Zero) return 0;
        if (!GetWindowRect(bar, out RECT barRect)) return 0;
        int barWidth = barRect.Right - barRect.Left;

        foreach (string cls in new[] { "Start", "ReBarWindow32", "TrayNotifyWnd" })
        {
            IntPtr child = FindWindowExW(bar, IntPtr.Zero, cls, null);
            if (child == IntPtr.Zero) continue;
            if (!GetWindowRect(child, out RECT r)) continue;

            int width = r.Left - barRect.Left;
            // 宽度不合理就跳过：XAML 岛那层会给 0 宽或整屏宽，直接用会算出荒谬的结果
            if (width >= 120 && width < barWidth) return width;
        }

        return barWidth / 4;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowExW(uint exStyle, string className, string windowName,
                                                 uint style, int x, int y, int width, int height,
                                                 IntPtr parent, IntPtr menu, IntPtr instance, IntPtr param);

    /// <summary>
    /// 造一个**原生**子窗口（不是 WPF 的），用来把"挂载失败"拆成两种可能：
    /// 任务栏不接受跨进程子窗口，还是 WPF 的 <c>Window</c> 不能当子窗口用。
    ///
    /// <para>
    /// 用 <c>STATIC</c> + <c>SS_WHITERECT</c>：整个客户区填白，不需要处理 <c>WM_PAINT</c>。
    /// 任务栏是深色的，一块纯白出现即证明挂载这条路本身通。
    /// </para>
    /// </summary>
    public static IntPtr CreateNativeChild(IntPtr parent, int x, int y, int width, int height)
    {
        const uint WS_CHILD = 0x40000000;
        const uint WS_VISIBLE = 0x10000000;
        const uint SS_WHITERECT = 0x00000006;

        return CreateWindowExW(0, "STATIC", "", WS_CHILD | WS_VISIBLE | SS_WHITERECT,
                               x, y, width, height, parent, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>主屏任务栏。explorer 重启后句柄会失效，调用方必须重取。</summary>
    public static IntPtr Find()
    {
        IntPtr hwnd = FindWindowW("Shell_TrayWnd", null);
        return IsWindow(hwnd) ? hwnd : IntPtr.Zero;
    }

    /// <summary>副屏任务栏（Win10/11 每块屏各一个）。v1 不用（Q-14），仅探针记录。</summary>
    public static IntPtr FindSecondary()
    {
        IntPtr hwnd = FindWindowW("Shell_SecondaryTrayWnd", null);
        return IsWindow(hwnd) ? hwnd : IntPtr.Zero;
    }

    public static bool TryGetRect(IntPtr hwnd, out Win32Window.RECT rect)
    {
        rect = default;
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out RECT r)) return false;
        rect = new Win32Window.RECT { Left = r.Left, Top = r.Top, Right = r.Right, Bottom = r.Bottom };
        return true;
    }

    public static bool TryGetClientSize(IntPtr hwnd, out int width, out int height)
    {
        width = height = 0;
        if (hwnd == IntPtr.Zero || !GetClientRect(hwnd, out RECT r)) return false;
        width = r.Right - r.Left;
        height = r.Bottom - r.Top;
        return true;
    }

    public static bool IsVisible(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindowVisible(hwnd);

    public static string Describe(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "(null)";

        var cls = new StringBuilder(256);
        GetClassNameW(hwnd, cls, cls.Capacity);
        var text = new StringBuilder(256);
        GetWindowTextW(hwnd, text, text.Capacity);
        GetWindowThreadProcessId(hwnd, out uint pid);

        bool hasRect = TryGetRect(hwnd, out Win32Window.RECT r);
        TryGetClientSize(hwnd, out int cw, out int ch);

        return $"hwnd=0x{hwnd:X} class={cls} title=\"{text}\" pid={pid} " +
               (hasRect ? $"rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) {r.Right - r.Left}x{r.Bottom - r.Top}" : "rect=?") +
               $" client={cw}x{ch} visible={IsWindowVisible(hwnd)}";
    }

    /// <summary>按文档顺序列出所有子窗口（深度 1），供探针确认 Win11 任务栏的内部结构。</summary>
    public static IReadOnlyList<(IntPtr Hwnd, string Class, string Text)> Children(IntPtr parent)
    {
        var list = new List<(IntPtr, string, string)>(8);
        if (parent == IntPtr.Zero) return list;

        // FindWindowEx 会反复命中同一个，必须用 GetWindow(GW_HWNDNEXT) 逐项走
        IntPtr child = FindWindowExW(parent, IntPtr.Zero, null, null);
        int guard = 0;
        while (child != IntPtr.Zero && guard++ < 64)
        {
            var cls = new StringBuilder(256);
            GetClassNameW(child, cls, cls.Capacity);
            var text = new StringBuilder(256);
            GetWindowTextW(child, text, text.Capacity);
            list.Add((child, cls.ToString(), text.ToString()));
            child = GetWindow(child, GW_HWNDNEXT);
        }
        return list;
    }

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr param);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr param);

    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    /// <summary>
    /// 找出所有**可见且矩形与给定区域相交**的顶层窗口。
    ///
    /// <para>
    /// 为什么需要：实测截图里任务栏区域显示的根本不是任务栏，而是别的东西。
    /// 到底是谁盖住了它，只能靠枚举顶层窗口 + 比较矩形来判断 ——
    /// 光看 <c>Shell_TrayWnd</c> 的 rect 依然是正确的，完全看不出被遮挡。
    /// </para>
    /// </summary>
    public static IReadOnlyList<string> WhoCovers(int left, int top, int right, int bottom)
    {
        var list = new List<string>(8);

        bool OnWindow(IntPtr hwnd, IntPtr param)
        {
            if (!IsWindowVisible(hwnd)) return true;
            if (!GetWindowRect(hwnd, out RECT r)) return true;

            // 只要与目标区域有交叠就算
            if (r.Right <= left || r.Left >= right || r.Bottom <= top || r.Top >= bottom) return true;

            long ex = (long)GetWindowLongPtr(hwnd, GWL_EXSTYLE);
            var cls = new StringBuilder(256);
            GetClassNameW(hwnd, cls, cls.Capacity);
            var text = new StringBuilder(256);
            GetWindowTextW(hwnd, text, text.Capacity);
            GetWindowThreadProcessId(hwnd, out uint pid);

            string proc;
            try { proc = System.Diagnostics.Process.GetProcessById((int)pid).ProcessName; }
            catch { proc = "?"; }

            var flags = new List<string>(4);
            if ((ex & WS_EX_TOPMOST) != 0) flags.Add("TOPMOST");
            if ((ex & WS_EX_LAYERED) != 0) flags.Add("LAYERED");
            if ((ex & WS_EX_TRANSPARENT) != 0) flags.Add("CLICKTHROUGH");
            if ((ex & WS_EX_TOOLWINDOW) != 0) flags.Add("TOOLWINDOW");

            list.Add($"0x{hwnd:X} [{proc}] class={cls} title=\"{text}\" " +
                     $"rect=({r.Left},{r.Top})-({r.Right},{r.Bottom}) " +
                     $"{(flags.Count == 0 ? "-" : string.Join("|", flags))}");
            return true;
        }

        EnumWindowsProc proc2 = OnWindow;
        EnumWindows(proc2, IntPtr.Zero);
        GC.KeepAlive(proc2);
        return list;
    }
}
