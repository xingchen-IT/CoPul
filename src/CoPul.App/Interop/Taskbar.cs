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

    [DllImport("dwmapi.dll")]
    private static extern int DwmFlush();

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
    /// <summary>
    /// 把窗口变成"盖在任务栏那块区域上的顶层窗口"（需求 7.1 的 A 方案）。
    ///
    /// <para>
    /// <b>为什么最终选了 A 而不是 B。</b>B 方案（`SetParent` 到 `Shell_TrayWnd` + `WS_CHILD`）
    /// 在 Win11 build 26200 上确实能挂上、能画、坐标也对，但有个绕不过去的缺陷：
    /// 窗口消失后 <b>DWM 不会重绘任务栏那一块</b>。任务栏的可见内容由 XAML 岛
    /// （`DesktopWindowContentBridge`）用 Composition 渲染，不响应 GDI 重绘，
    /// 于是退出后画面上留着一张冻结的仪表盘 —— 进程没了、`EnumChildWindows` 找不到、
    /// 鼠标点得穿，但就是擦不掉。实测 `Hide`、`Detach`、`DwmFlush`、把窗口移出父客户区、
    /// 销毁前 `Opacity=0`、关掉透明表面，六种手段全部无效，只有重启 explorer 能清。
    /// </para>
    ///
    /// <para>
    /// A 方案下窗口是独立顶层窗口，有自己完整的 DWM 表面，销毁时 DWM 正常合成，
    /// 不会留任何东西。代价是要自己跟随任务栏的位置/大小/自动隐藏 —— 1 Hz 巡检
    /// 本来就在跑，顺带做掉。
    /// </para>
    /// </summary>
    public static bool AttachAsTopLevel(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        // 万一之前用 B 方案挂过，先把 WS_CHILD 清掉还原成弹出式顶层窗口
        IntPtr style = GetWindowLongPtr(hwnd, GWL_STYLE);
        long next = ((long)style & ~WS_CHILD) | WS_POPUP;
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)next);

        // WS_EX_TOOLWINDOW：不进 Alt+Tab，任务栏上也不留按钮。
        // 这里**不加** WS_EX_NOACTIVATE —— 加了之后右键菜单会弹不出来（A-33 实测过）。
        IntPtr ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE);
        long exNext = (long)ex | WS_EX_TOOLWINDOW;
        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)exNext);

        SetWindowPos(hwnd, IntPtr.Zero, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED);

        return true;
    }

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
    /// 等 DWM 完成一次合成。
    ///
    /// <para>
    /// 退出流程里<b>必须</b>用：`Hide()` 只是把窗口标记为不可见，屏幕上那块画面
    /// 要等 DWM 下一次合成才会被擦掉。如果 Hide 之后立刻销毁窗口，
    /// DWM 会认为"没有内容要更新"，那块就永远停在隐藏前的最后一帧 ——
    /// 进程已经没了、`EnumChildWindows` 找不到、`ChildWindowFromPointEx` 返回 0、
    /// `RedrawWindow` 与改任务栏尺寸全部无效，只有重启 explorer 才能清掉。
    /// </para>
    ///
    /// <para>
    /// 实测：同一条在**进程存活**时 Hide 是干净的（DWM 有充足时间重绘），
    /// 只有"Hide 完马上销毁"才留残影，所以卡的是时序而不是 Hide 本身。
    /// </para>
    /// </summary>
    public static void FlushComposition()
    {
        try
        {
            DwmFlush();

            // DwmFlush 在没有待合成帧时会立即返回，而 Hide 引发的重绘可能还没排队。
            // 给一次短暂的等待再 flush，确保那一帧真的被合成掉了。
            System.Threading.Thread.Sleep(60);
            DwmFlush();
        }
        catch
        {
            // dwmapi 不可用（极老系统）时退化为纯等待
            System.Threading.Thread.Sleep(60);
        }
    }

    /// <summary>
    /// 把子窗口挪到父客户区外面（远到不可能可见的地方）。
    ///
    /// <para>
    /// 退出流程用：光 <c>Hide</c> 不够 —— 实测 DWM 不会为父窗口重绘那一块，
    /// 任务栏上会留下一张冻结的画面。挪走是"位置真的变了"，
    /// 父窗口必须响应 <c>WM_WINDOWPOSCHANGED</c> 并重绘，DWM 才会把那一块擦干净。
    /// 尺寸一起改，比只改位置更容易触发重绘。
    /// </para>
    /// </summary>
    public static void MoveOutOfParent(IntPtr child)
    {
        if (child == IntPtr.Zero) return;

        SetWindowPos(child, IntPtr.Zero, -20000, -20000, 8, 8,
            SWP_NOZORDER | SWP_NOACTIVATE);
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

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern uint RegisterWindowMessageW(string name);

    /// <summary>
    /// 取一个系统广播消息的 id（这里是 <c>"TaskbarCreated"</c>）。
    ///
    /// <para>
    /// explorer 重启后会广播这条消息，任务栏相关的扩展靠它重新挂载（需求 7.7）。
    /// 返回值要缓存 —— 每次调用结果相同，但消息循环里逐条比较时不该反复进 user32。
    /// </para>
    /// </summary>
    public static int RegisterMessage(string name)
    {
        uint id = RegisterWindowMessageW(name);
        return unchecked((int)id);
    }

    /// <summary>窗口句柄是否仍然有效。explorer 重启后子窗口可能已被系统销毁。</summary>
    public static bool IsAlive(IntPtr hwnd) => hwnd != IntPtr.Zero && IsWindow(hwnd);

    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(IntPtr token, int infoClass,
        out int info, int infoLength, out int returnLength);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);

    /// <summary>
    /// 当前进程是否提权运行。诊断用，不是功能。
    ///
    /// <para>
    /// 为什么要记这个：<c>TaskbarCreated</c> 是 explorer（中完整性）用
    /// <c>SendMessageTimeout(HWND_BROADCAST)</c> 发出来的。<b>UIPI 会拦截低完整性进程
    /// 发给高完整性窗口的消息</b> —— 如果 CoPul 是提权跑的，这条广播永远收不到，
    /// 表现为"重启 explorer 后条不回来"，而代码本身完全正确。
    /// 不把这一条写进日志，排查方向会全错。
    /// </para>
    /// </summary>
    public static bool IsElevated()
    {
        const uint TOKEN_QUERY = 0x0008;
        const int TokenElevation = 20;

        if (!OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, out IntPtr token)) return false;
        try
        {
            if (!GetTokenInformation(token, TokenElevation, out int elevated, sizeof(int), out _))
                return false;
            return elevated != 0;
        }
        finally
        {
            CloseHandle(token);
        }
    }

    /// <summary>
    /// 重启 explorer（自测用）。验证 7.7「explorer 重启后任务栏条自动恢复」。
    ///
    /// <para>
    /// 不能用 <c>Process.Start("taskkill")</c> 之后就立刻拉起：explorer 还没退干净时
    /// 新进程会连到旧的 shell 上，任务栏不一定重建。所以杀完等它真的退出（带超时，
    /// 杀不掉也不能把自测卡死）。
    /// </para>
    /// </summary>
    public static string RestartExplorer()
    {
        try
        {
            var kill = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "taskkill",
                Arguments = "/f /im explorer.exe",
                CreateNoWindow = true,
                UseShellExecute = false,
            });
            kill?.WaitForExit(4000);

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = "explorer.exe",
                UseShellExecute = true,
            });
            return "explorer 已重启";
        }
        catch (Exception ex)
        {
            return "重启失败: " + ex.Message;
        }
    }

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
    ///
    /// <para>
    /// <b>返回 -1 表示"一个有效像素都没取到"</b>，调用方必须保持上一次的判断。
    /// 任务栏开了自动隐藏时会整条滑出屏幕，这时任务栏矩形完全在屏幕外，
    /// <c>GetPixel</c> 一律返回 <c>CLR_INVALID</c>；早先的实现把这种"没采到"
    /// 当成 128（灰色）处理，再拿 <c>&gt; 128</c> 一比就翻成浅底 → 深色字，
    /// 任务栏滑回来就是深底深字，一片糊。
    /// </para>
    /// </summary>
    public static double SampleLuma(IntPtr bar)
    {
        if (bar == IntPtr.Zero || !GetWindowRect(bar, out RECT r)) return -1;
        if (!Monitors.TryPrimaryMonitorBounds(out Win32Window.RECT screen)) return -1;

        // 自动隐藏时任务栏大部分在屏幕外，把采样 y 夹回可见范围（屏幕上最后一条像素）
        int y = Math.Clamp((r.Top + r.Bottom) / 2, screen.Top, screen.Bottom - 1);
        if (r.Top >= screen.Bottom || r.Bottom <= screen.Top) return -1;   // 整条都不在屏幕上

        IntPtr dc = GetDC(IntPtr.Zero);
        if (dc == IntPtr.Zero) return -1;

        try
        {
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

            return count == 0 ? -1 : sum / count;
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

    /// <summary>任务栏停靠边，取值与 <c>ABE_*</c> 一致。</summary>
    public enum Edge
    {
        Left = 0,
        Top = 1,
        Right = 2,
        Bottom = 3,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public IntPtr lParam;
    }

    [DllImport("shell32.dll")]
    private static extern IntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

    private const uint ABM_GETSTATE = 0x00000004;
    private const uint ABM_GETTASKBARPOS = 0x00000005;

    private static APPBARDATA NewBarData() => new() { cbSize = Marshal.SizeOf<APPBARDATA>() };

    /// <summary>
    /// 自测用：强制指定停靠边，<c>null</c> = 用真实值。
    ///
    /// <para>
    /// 为什么要这个开关：验证"竖向任务栏降级"必须先把任务栏挪到左边 ——
    /// 那是改用户的系统设置，自测跑一次改一次不可接受（而且中途进程被杀就留在那儿了）。
    /// 只覆盖<b>判定结果</b>，真正的 <c>SetParent</c>、摆位、隐藏全走生产代码路径，
    /// 所以"降级分支到底会不会把条藏起来"是真验证过的。
    /// </para>
    /// </summary>
    public static Edge? EdgeOverride { get; set; }

    /// <summary>
    /// 可用空白宽度的覆盖值（物理像素），只为自测 7.7「任务栏已满」的字段让位逻辑。
    ///
    /// <para>
    /// 真实触发这个分支得把任务栏图标堆满左边空白 —— 那要往任务栏里塞几十个窗口，
    /// 不可接受也不可重复。所以只伪造"空白有多少"这一个输入，字段让位、宽度计算、
    /// 摆位全走生产代码。
    /// </para>
    /// </summary>
    public static int? FreeWidthOverride { get; set; }

    /// <summary>
    /// 伪造光标位置（屏幕物理像素），只为自测 7.6 的悬停提示。
    ///
    /// <para>
    /// 本机 <c>SetCursorPos</c> 不可靠 —— 设 (68,1576) 读回 (1251,895)，
    /// 光标会被真实鼠标挪走（M4.1 已踩过，点菜单就是因此改成探针的）。
    /// 这里只替换"光标在哪"这一个输入，命中判定、显示/隐藏、摆位全走生产代码。
    /// </para>
    /// </summary>
    public static Win32Window.POINT? HoverOverride { get; set; }

    /// <summary>
    /// 任务栏停在哪条边上（上 / 下 / 左 / 右）。
    ///
    /// <para>
    /// 用 <c>ABM_GETTASKBARPOS</c> 而不是拿任务栏矩形跟屏幕矩形比大小：
    /// 自动隐藏时任务栏是"整条滑出屏幕"的，矩形会整个跑到屏幕外，
    /// 靠几何反推会得出"既不在上也不在下"的错误答案。这条消息直接给 <c>uEdge</c>。
    /// </para>
    ///
    /// <para>取不到时按"底部"处理 —— 这是绝大多数机器的情况，也是最不容易造成事故的猜测。</para>
    /// </summary>
    public static Edge EdgeOf(IntPtr bar)
    {
        if (EdgeOverride is { } forced) return forced;
        if (bar == IntPtr.Zero) return Edge.Bottom;

        APPBARDATA data = NewBarData();
        return SHAppBarMessage(ABM_GETTASKBARPOS, ref data) == IntPtr.Zero
            ? Edge.Bottom
            : (Edge)data.uEdge;
    }

    /// <summary>
    /// 任务栏是否开了"自动隐藏"（<c>ABS_AUTOHIDE</c>）。
    ///
    /// <para>
    /// 这个只用来记日志和诊断：自动隐藏时任务栏整条滑出屏幕，
    /// 而我们的条是它的<b>子窗口</b>，会被父窗口的可见区域裁剪 ——
    /// 也就是说"跟着一起滑走"是白送的，不需要额外做什么。
    /// 真正要防的是<b>采样配色</b>：任务栏滑走后采样点落在屏幕外，
    /// <c>GetPixel</c> 一律返回 <c>CLR_INVALID</c>，会被当成"白色底"从而把字翻成深色。
    /// </para>
    /// </summary>
    public static bool IsAutoHide(IntPtr bar)
    {
        if (bar == IntPtr.Zero) return false;
        APPBARDATA data = NewBarData();
        return ((long)SHAppBarMessage(ABM_GETSTATE, ref data) & 0x1L) != 0;   // ABS_AUTOHIDE
    }

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hwnd, int cmd);

    /// <summary>
    /// 显示/隐藏子窗口。用 Win32 而不是 <c>Window.Visibility</c>：
    /// 条是 <c>WS_CHILD</c> 窗口，WPF 的 <c>Visibility</c> 语义是围绕"顶层窗口"设计的，
    /// 在子窗口上会连带改父窗口的可见性判断，直接用 <c>ShowWindow</c> 最没有副作用。
    /// </summary>
    public static void SetChildVisible(IntPtr hwnd, bool visible)
    {
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return;
        ShowWindow(hwnd, visible ? 4 : 0);   // SW_SHOWNOACTIVATE / SW_HIDE
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
        if (FreeWidthOverride is { } forcedWidth) return forcedWidth;
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
