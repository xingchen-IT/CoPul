using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CoPul.App.Interop;

/// <summary>
/// 系统托盘图标（<c>Shell_NotifyIcon</c>）。
///
/// <para>
/// 为什么不引 <c>System.Windows.Forms.NotifyIcon</c>：那要给项目开
/// <c>UseWindowsForms</c>，WPF 与 WinForms 两套消息泵、两套 DPI 感知混在一起，
/// 为了一个图标不值。这里只用 user32 的四个函数，行为完全可控。
/// </para>
///
/// <para>
/// 菜单也走原生 <c>TrackPopupMenu</c> 而不是 WPF <c>ContextMenu</c>：
/// WPF 的 ContextMenu 需要一个 <c>PlacementTarget</c>（真实的 <c>Window</c>）才能定位，
/// 而托盘图标可能在三视图全部隐藏时被点击 —— 那时没有合适的宿主窗口。
/// 原生菜单自己按屏幕坐标弹，与窗口无关，这是它的长处。
/// </para>
/// </summary>
public sealed class TrayIcon : IDisposable
{
    // 回调消息：WM_APP + 1 是托盘图标的惯例起点，避免与系统消息撞号
    private const int WM_APP = 0x8000;
    private const int CallbackMessage = WM_APP + 1;

    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_LBUTTONDBLCLK = 0x0203;
    private const int WM_RBUTTONUP = 0x0205;

    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_STATE = 0x00000008;
    private const uint NIF_SHOWTIP = 0x00000080;
    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIM_SETVERSION = 0x00000004;

    private const uint NOTIFYICON_VERSION_4 = 4;

    private const int SM_CXSMICON = 49;
    private const uint IMAGE_ICON = 1;
    private const uint LR_LOADFROMFILE = 0x00000010;

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint MF_GRAYED = 0x00000001;
    private const uint MF_CHECKED = 0x00000008;
    private const uint MF_POPUP = 0x00000010;

    private const uint TPM_RETURNCMD = 0x0100;
    private const uint TPM_RIGHTBUTTON = 0x0002;

    private HwndSource? _source;
    private IntPtr _hIcon;
    private bool _added;

    /// <summary>左键单击。G-01：显示/隐藏当前模式的窗口。</summary>
    public Action? LeftClick { get; set; }

    /// <summary>右键单击。G-02：弹全局菜单；菜单内容由调用方给。</summary>
    public Action? RightClick { get; set; }

    /// <summary>
    /// 装上托盘图标。
    /// </summary>
    /// <param name="iconPath">.ico 文件路径；取不到时退化为系统默认图标，不抛异常。</param>
    /// <param name="tooltip">悬停提示，最多 127 个字符（系统限制，超了会被截断）。</param>
    public TrayIcon(string iconPath, string tooltip)
    {
        // 承载回调消息的隐藏窗口。不能用 message-only 窗口：托盘图标本身虽然不是窗口，
        // 但 TrackPopupMenu 需要前台窗口才能正常消失，所以这里要一个真实顶层窗口。
        var p = new HwndSourceParameters("CoPulTrayIcon")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,                    // 不带 WS_VISIBLE → 不显示
            ExtendedWindowStyle = 0x00000080,   // WS_EX_TOOLWINDOW → 不进 Alt+Tab
        };

        _source = new HwndSource(p);
        _source.AddHook(Hook);

        _hIcon = LoadIcon(iconPath);

        var data = NewData();
        data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP;
        data.uCallbackMessage = CallbackMessage;
        data.hIcon = _hIcon;
        data.szTip = Truncate(tooltip, 127);

        _added = Shell_NotifyIcon(NIM_ADD, ref data);

        if (_added)
        {
            // V4 语义下回调的 lParam 是坐标打包，wParam 是事件；不设也基本能用，
            // 但设了之后左键单击才会报 WM_LBUTTONUP 而不是 WM_CONTEXTMENU
            data.uVersion = NOTIFYICON_VERSION_4;
            Shell_NotifyIcon(NIM_SETVERSION, ref data);
        }
    }

    /// <summary>图标是否真的装上了（System32 被锁、explorer 未就绪时会失败）。</summary>
    public bool IsAdded => _added;

    /// <summary>改悬停提示（模式切换后要跟着变）。</summary>
    public void SetTooltip(string tooltip)
    {
        if (!_added) return;

        var data = NewData();
        data.uFlags = NIF_TIP | NIF_SHOWTIP;
        data.szTip = Truncate(tooltip, 127);
        Shell_NotifyIcon(NIM_MODIFY, ref data);
    }

    /// <summary>
    /// 在鼠标位置弹原生菜单，返回被点中项携带的命令；点空处、按 Esc 或点到灰色项返回 <c>null</c>。
    ///
    /// <para>阻塞式：<c>TrackPopupMenu</c> 自己跑消息循环，返回时菜单已关闭。</para>
    /// </summary>
    public string? ShowMenu(IReadOnlyList<TrayMenuEntry> entries)
    {
        if (!_added || _source is null) return null;

        IntPtr hwnd = _source.Handle;
        IntPtr menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return null;

        // id → 命令。不用"下标 +1"：加了子菜单之后先序遍历的序号会整体平移，
        // 调用方那串 case 1/2/3/5 会**静默**指向别的功能（点退出结果切了视图）。
        var commands = new Dictionary<int, string?>();
        int nextId = 1;

        try
        {
            BuildMenu(menu, entries, commands, ref nextId);

            if (!GetCursorPos(out POINT pt)) return null;

            // 不置前台的话，菜单点外面不会消失（TrackPopupMenu 的老毛病）
            bool fg = SetForegroundWindow(hwnd);
            Trace.Log("tray", $"menu popup at=({pt.X},{pt.Y}) fg={fg} entries={entries.Count}");

            uint cmd = TrackPopupMenu(menu, TPM_RETURNCMD | TPM_RIGHTBUTTON,
                                      pt.X, pt.Y, 0, hwnd, IntPtr.Zero);

            // 同上：官方文档要求在 TrackPopupMenu 之后补一条空消息，否则菜单可能残留
            PostMessage(hwnd, 0x0000 /* WM_NULL */, IntPtr.Zero, IntPtr.Zero);

            if (cmd == 0 || !commands.TryGetValue((int)cmd, out string? picked))
            {
                Trace.Log("tray", $"menu picked={cmd} → 无命令");
                return null;
            }

            Trace.Log("tray", $"menu picked={cmd} → {picked ?? "(空)"}");
            return picked;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    /// <summary>递归建菜单。子菜单也归父菜单所有，<c>DestroyMenu</c> 会一并销毁。</summary>
    private static void BuildMenu(IntPtr parent, IReadOnlyList<TrayMenuEntry> entries,
                                  Dictionary<int, string?> commands, ref int nextId)
    {
        foreach (TrayMenuEntry e in entries)
        {
            if (e.IsSeparator)
            {
                AppendMenu(parent, MF_SEPARATOR, IntPtr.Zero, null);
                continue;
            }

            if (e.Children is { Count: > 0 })
            {
                IntPtr sub = CreatePopupMenu();
                if (sub == IntPtr.Zero) continue;

                // MF_POPUP 时 uIDNewItem 要传子菜单句柄，不是 id
                AppendMenu(parent, MF_POPUP | (e.Enabled ? 0u : MF_GRAYED), sub, e.Text);
                BuildMenu(sub, e.Children, commands, ref nextId);
                continue;
            }

            int id = nextId++;
            commands[id] = e.Command;

            uint flags = MF_STRING;
            if (!e.Enabled) flags |= MF_GRAYED;
            if (e.Checked) flags |= MF_CHECKED;

            AppendMenu(parent, flags, new IntPtr(id), e.Text);
        }
    }

    public void Dispose()
    {
        if (_added)
        {
            var data = NewData();
            Shell_NotifyIcon(NIM_DELETE, ref data);
            _added = false;
        }

        if (_hIcon != IntPtr.Zero)
        {
            DestroyIcon(_hIcon);
            _hIcon = IntPtr.Zero;
        }

        if (_source is not null)
        {
            _source.RemoveHook(Hook);
            _source.Dispose();
            _source = null;
        }
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != CallbackMessage) return IntPtr.Zero;

        // NOTIFYICON_VERSION_4：事件在 lParam 的低位，图标 id 在 wParam 的低位。
        // 老版本反过来（事件在 lParam，id 在 uID）。这里只要事件，两种取法都兜住。
        int evt = (int)((long)lParam & 0xFFFF);
        Trace.Log("tray", $"callback evt=0x{evt:X4} wparam=0x{(long)wParam:X}");
        switch (evt)
        {
            case WM_LBUTTONUP:
            case WM_LBUTTONDBLCLK:
                LeftClick?.Invoke();
                handled = true;
                break;

            case WM_RBUTTONUP:
                RightClick?.Invoke();
                handled = true;
                break;
        }

        return IntPtr.Zero;
    }

    private NOTIFYICONDATA NewData()
    {
        return new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _source?.Handle ?? IntPtr.Zero,
            uID = 1,
        };
    }

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..max];

    private static IntPtr LoadIcon(string path)
    {
        // 按系统小图标尺寸取帧：150% 缩放下要 24px，用 0 会让系统自己挑，
        // 有时会拿到 16px 再被拉大，看着发虚
        int cx = GetSystemMetrics(SM_CXSMICON);
        if (cx <= 0) cx = 16;

        if (!string.IsNullOrEmpty(path) && File.Exists(path))
        {
            IntPtr h = LoadImage(IntPtr.Zero, path, IMAGE_ICON, cx, cx, LR_LOADFROMFILE);
            if (h != IntPtr.Zero) return h;
        }

        // 退路：系统默认应用图标。宁可图标丑一点，也不能因为没有图标而整个托盘失灵
        return LoadIconFallback();
    }

    private static IntPtr LoadIconFallback() => LoadIconW(IntPtr.Zero, new IntPtr(32512) /* IDI_APPLICATION */);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersion;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadImage(IntPtr hinst, string name, uint type, int cx, int cy, uint load);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIconW(IntPtr hinst, IntPtr name);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenu(IntPtr menu, uint flags, IntPtr id, string? text);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr menu);

    [DllImport("user32.dll")]
    private static extern uint TrackPopupMenu(IntPtr menu, uint flags, int x, int y,
                                              int reserved, IntPtr hwnd, IntPtr rect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT pt);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hwnd);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}

/// <summary>托盘菜单里的一项。</summary>
public sealed class TrayMenuEntry
{
    private TrayMenuEntry() { }

    public string Text { get; private init; } = string.Empty;

    /// <summary>
    /// 被点中时回传的命令串（如 <c>"view:taskbar"</c> / <c>"field:pwr"</c>）。
    /// 灰色占位项留 <c>null</c>。
    ///
    /// <para>
    /// 为什么不用数字 id：id 是按先序遍历顺序分配的，加一个子菜单就会让后面所有项平移，
    /// 而调用方的 <c>switch (id)</c> 不会报错 —— 表现为"点退出却切了视图"这种静默错位。
    /// </para>
    /// </summary>
    public string? Command { get; private init; }

    public bool Enabled { get; private init; } = true;
    public bool Checked { get; private init; }
    public bool IsSeparator { get; private init; }

    /// <summary>子菜单项。非空时这一项本身不携带命令，只负责展开。</summary>
    public IReadOnlyList<TrayMenuEntry>? Children { get; private init; }

    public static TrayMenuEntry Item(string text, string? command = null,
                                     bool enabled = true, bool isChecked = false)
        => new() { Text = text, Command = command, Enabled = enabled, Checked = isChecked };

    public static TrayMenuEntry Submenu(string text, IReadOnlyList<TrayMenuEntry> children,
                                        bool enabled = true)
        => new() { Text = text, Children = children, Enabled = enabled };

    public static TrayMenuEntry Separator() => new() { IsSeparator = true };
}
