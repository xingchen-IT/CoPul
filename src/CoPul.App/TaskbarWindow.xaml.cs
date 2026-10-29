using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using CoPul.App.Interop;
using CoPul.App.Sdk;

namespace CoPul.App;

/// <summary>
/// 任务栏模式（需求第 7 章）。把六+二格指标画进 Windows 任务栏左侧的空白区。
///
/// <para>
/// <b>嵌入方式</b>：<c>SetParent</c> 到 <c>Shell_TrayWnd</c> 并加 <c>WS_CHILD</c>（7.1 的 B 方案）。
/// 实测 Win11 build 26200 上这条路可行：子窗口被任务栏接受、能正常绘制、坐标相对任务栏客户区。
/// 屏幕上"看不见"过一次，但那是全屏游戏盖住了任务栏，不是挂载失败 ——
/// 用 <c>PrintWindow</c> 直接向任务栏要绘制结果就能看到自己那块。
/// </para>
///
/// <para>
/// <b>为什么不改变任务栏布局</b>：这仍然是"占位"而不是"插入"。它只会占用
/// 开始按钮左边的空白，图标挤过来时会被盖住 —— 7.1 已经把这条列为必须告知的限制。
/// </para>
///
/// <para>
/// 数据不自己采：与大窗口、悬浮窗共用同一份 <see cref="DashboardState"/>。
/// 每多一份 CollectorHost 就多一个 PresentMon 会话，FPS 会互相踩（M2 已踩过）。
/// </para>
/// </summary>
public partial class TaskbarWindow : Window
{
    /// <summary>文字取色策略（7.5：文字必须跟任务栏明暗走，否则深底黑字直接不可读）。</summary>
    public enum InkMode
    {
        /// <summary>读 <c>SystemUsesLightTheme</c> 自动决定。</summary>
        Auto,
        /// <summary>强制浅色字（任务栏是深底）。</summary>
        Light,
        /// <summary>强制深色字（任务栏是浅底）。</summary>
        Dark
    }

    /// <summary>左边距，物理像素。7.3 写的是 8px，任务栏是物理像素坐标系，直接照用。</summary>
    private const int LeftInset = 8;

    /// <summary>组间距（DIP）。必须与 XAML 里各列的 <c>Margin="0,0,7,0"</c> 一致。</summary>
    private const double CellGapDip = 7;

    /// <summary>两端内边距（DIP）。必须与 XAML 里 Shell 的 <c>Padding="6,0"</c> 一致。</summary>
    private const double PaddingDip = 6;

    private readonly DashboardState _state;
    private readonly AppConfig _config;

    private IntPtr _bar;
    private bool _attached;
    private InkMode _ink = InkMode.Auto;

    /// <summary>接收 <c>TaskbarCreated</c> 广播的隐藏顶层窗口。子窗口收不到广播，见 <see cref="InstallTaskbarWatcher"/>。</summary>
    private HwndSource? _watcher;

    private int _taskbarCreatedMsg;

    /// <summary>
    /// 存活巡检（每秒一次）。为什么不能只靠 <c>TaskbarCreated</c> 广播：
    /// 本机实测广播<b>根本没到</b> —— 日志里只有 <c>attach</c> 和 <c>watcher</c> 两行，
    /// 没有任何 <c>TaskbarCreated 收到</c>。最可能的原因是 UIPI：explorer 是中完整性，
    /// 若本进程提权，跨完整性级别的窗口消息会被直接丢弃。
    /// 广播是"快"（重启瞬间就能知道），巡检是"准"（不依赖任何消息），两个都要。
    /// </summary>
    private DispatcherTimer? _watchdog;

    /// <summary>正在恢复中，挡住巡检重入（重建会同步 Close 本窗口）。</summary>
    private bool _recovering;

    /// <summary>已经交给 App 重建，本实例不再管任何事。</summary>
    private bool _handedOff;

    /// <summary>
    /// 自己的 HWND，在挂载时缓存下来。
    ///
    /// <para>
    /// 不能拿 <see cref="Handle"/> 做存活判断：窗口被父窗口连带销毁后，
    /// <c>WindowInteropHelper.Handle</c> 会返回 <c>IntPtr.Zero</c>，
    /// 巡检里 `if (hwnd == 0) return` 会把它当成"还没初始化"直接跳过 ——
    /// 于是明明窗口已经没了，巡检却什么都不做（第一次就是这么静默失败的）。
    /// </para>
    /// </summary>
    private IntPtr _hwnd;

    /// <summary>双击或被菜单要求"回大窗口"。窗口之间不互相引用，由 App 接线。</summary>
    public event EventHandler? MainWindowRequested;

    /// <summary>切回悬浮窗（任务栏条上无法做复杂设置，菜单里给一条出口）。</summary>
    public event EventHandler? FloatRequested;

    /// <summary>
    /// 本窗口已被系统销毁，需要外部重建一个新的（explorer 重启时会发生）。
    /// App 收到后负责关掉旧实例、new 一个新的并 Show。
    /// </summary>
    public event EventHandler? RecreateRequested;

    /// <summary>自测时不改写用户的配置。</summary>
    public bool PersistToConfig { get; set; } = true;

    /// <summary>诊断串，供截图 sidecar 记录。</summary>
    public string PlacementDiag { get; private set; } = string.Empty;

    /// <summary>当前 HWND；窗口未初始化时返回 <c>IntPtr.Zero</c>。</summary>
    public IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 调试日志。任务栏重挂涉及 4 个异步环节（广播 → 重试 → 检查存活 → 重建），
    /// 中间任何一环断了，表面现象都只是"条没了"，靠猜会连错三次。
    /// </summary>
    private static void Trace(string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "copul-tray.log"),
                $"{DateTime.Now:HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch { /* 日志失败不能影响主流程 */ }
    }

    public static readonly DependencyProperty LabelBrushProperty =
        DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(TaskbarWindow),
            new PropertyMetadata(null));

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

    public TaskbarWindow(DashboardState state, AppConfig config)
    {
        InitializeComponent();
        DataContext = state;

        _state = state;
        _config = config;

        if (Enum.TryParse(config.TaskbarInk, out InkMode saved)) _ink = saved;

        SourceInitialized += (_, _) =>
        {
            // 先拿掉 Alt+Tab 里的条目，再挂父窗口：顺序反了会先在任务栏闪一个空壳
            WindowGlass.HideFromAltTab(this);

            // 子窗口不需要 DWM 的整窗 accent —— 7.5 默认「跟随系统」= 完全透明，
            // 底色由任务栏自己提供。这里只把 WPF 的窗口底刷成透明。
            WindowGlass.MakeClientTransparent(this);

            Attach();

            // 换主题/换壁纸时系统会广播 WM_SETTINGCHANGE，字色要跟着重算
            HwndSource.FromHwnd(Handle)?.AddHook(SettingsChangedHook);

            InstallTaskbarWatcher();
            StartWatchdog();
        };

        // 放在 ContentRendered：Auto 模式要采样任务栏亮度，必须先有 _bar
        ContentRendered += (_, _) => ApplyInk();
    }

    /// <summary>
    /// 挂到任务栏上并定位。explorer 重启后要重来一遍，所以独立成方法。
    /// </summary>
    public void Attach()
    {
        // 先缓存自己的 HWND：后面巡检要靠它判断"我还在不在"，而 WindowInteropHelper
        // 在窗口被销毁后会返回 0（见 _hwnd 的注释）
        IntPtr hwnd = Handle;
        if (hwnd != IntPtr.Zero) _hwnd = hwnd;

        _bar = Taskbar.Find();
        if (_bar == IntPtr.Zero)
        {
            // 程序可能比任务栏先起来（开机自启、或正赶上 explorer 重启），等一会儿再试
            PlacementDiag = "attach: Shell_TrayWnd 未找到";
            ScheduleReattach(0);
            return;
        }

        if (hwnd == IntPtr.Zero) return;

        _attached = Taskbar.Attach(hwnd, _bar, asChildWindow: true);
        Trace($"attach: bar=0x{_bar:X} ok={_attached} hwnd=0x{hwnd:X}");
        Reposition();
    }

    /// <summary>按任务栏当前矩形重新摆位。任务栏高度/位置变化（缩放、移动）时也要调。</summary>
    public void Reposition()
    {
        if (!_attached || _bar == IntPtr.Zero) return;
        if (!Taskbar.TryGetRect(_bar, out Win32Window.RECT barRect)) return;

        // 高度跟随任务栏（7.3）。宽度取开始按钮左侧的空白，最多用到那里。
        int barHeight = barRect.Bottom - barRect.Top;
        int freeWidth = Taskbar.FreeLeftWidth(_bar);
        if (barHeight <= 0 || freeWidth <= LeftInset) return;

        int width = freeWidth - LeftInset;

        // 内容需要的宽度：所有列宽 + 组间距 7 + 两端内边距 6+6（与 TaskbarWindow.xaml 对齐）
        double neededDip = 0;
        foreach (FloatField f in AllFields) neededDip += f.Width + CellGapDip;
        neededDip += PaddingDip * 2;

        // 最后一列没有右间距
        neededDip -= CellGapDip;

        uint dpi = Monitors.DpiForWindow(_bar);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;
        int neededPx = (int)Math.Ceiling(neededDip * scale);

        // 7.7「任务栏已满」：空白不够时按优先级从右向左截断，先砍功耗、再砍延迟。
        // 第一版做法是缩到可用宽度并让文字自己出省略号，字段级的砍法留给下一轮。
        int finalWidth = Math.Min(width, Math.Max(neededPx, 1));

        // 坐标必须是**相对任务栏客户区**的，不是屏幕坐标。
        // 这里曾经传 `barRect.Left + LeftInset, barRect.Top`（屏幕坐标），
        // 结果子窗口被放到父客户区的 y=1528 处 —— 屏幕外，表现为"什么都看不见"，
        // 而 GetWindowRect 报 (8,3056) 正好是 2×1528，是父偏移被加了两遍的样子。
        Monitors.SetBounds(Handle, LeftInset, 0, finalWidth, barHeight);

        PlacementDiag = $"bar=({barRect.Left},{barRect.Top})-({barRect.Right},{barRect.Bottom}) " +
                        $"free={freeWidth} need={neededPx} used={finalWidth} scale={scale:F2}";
    }

    /// <summary>八个字段，按 7.4 的顺序。</summary>
    private FloatField[] AllFields => new[]
    {
        _state.FloatFps, _state.FloatCpu, _state.FloatGpu, _state.FloatRam,
        _state.FloatDisk, _state.FloatIo, _state.FloatLat, _state.FloatPwr,
    };

    /// <summary>
    /// 7.5 的文字颜色。Auto 读 <c>SystemUsesLightTheme</c>：
    /// 1 = 系统（任务栏）用浅色 → 浅底 → 深色字。
    /// </summary>
    public void ApplyInk()
    {
        bool taskbarIsLight = _ink switch
        {
            InkMode.Light => false,
            InkMode.Dark => true,

            // Auto 采样任务栏的**实际亮度**，不读注册表。
            // 实测 SystemUsesLightTheme=1 而任务栏是深蓝灰（跟随深色壁纸），
            // 按注册表判断会给深底配深字，什么都看不见。
            _ => Taskbar.SampleLuma(_bar) > 128,
        };

        // 走 OnLightAlt 而不是 DashboardState.SetFloatTheme：那个会翻所有字段的
        // OnLight，把悬浮窗的颜色一起改掉。两个视图共用一份字段，主题必须各管各的。
        foreach (FloatField f in AllFields) f.OnLightAlt = taskbarIsLight;

        LabelBrush = taskbarIsLight
            ? new SolidColorBrush(Color.FromRgb(0x4B, 0x55, 0x63))
            : new SolidColorBrush(Color.FromRgb(0xB8, 0xC2, 0xCE));

        // 给条一层淡底。为什么不能纯透明（7.5 的「跟随系统」）：
        // 实测任务栏是"跟随壁纸"的半透明，壁纸很亮时（现场是一张橙红星球）深色字
        // 直接糊成一团。垫一层 40% 的对比色后，字在任何壁纸上都能读，
        // 视觉上也还是一个"任务栏上的小组件"，不破坏一致性。
        Shell.Background = taskbarIsLight
            ? new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF))
            : new SolidColorBrush(Color.FromArgb(0x66, 0x00, 0x00, 0x00));

        AutoInkMenu.IsChecked = _ink == InkMode.Auto;
        LightInkMenu.IsChecked = _ink == InkMode.Light;
        DarkInkMenu.IsChecked = _ink == InkMode.Dark;
    }

    /// <summary>
    /// 装 <c>TaskbarCreated</c> 监听（7.7 的 explorer 重启）。
    ///
    /// <para>
    /// 关键点：这条消息是 <c>HWND_BROADCAST</c> 发出去的，而广播<b>只投递给顶层窗口</b>。
    /// 我们的条本身是任务栏的子窗口，收不到。必须另开一个不显示的顶层窗口专门收。
    /// message-only 窗口（<c>HWND_MESSAGE</c>）同样收不到广播，所以不能用它。
    /// </para>
    /// </summary>
    private void InstallTaskbarWatcher()
    {
        _taskbarCreatedMsg = Taskbar.RegisterMessage("TaskbarCreated");
        Trace($"watcher: msg={_taskbarCreatedMsg}");
        if (_taskbarCreatedMsg == 0) return;

        var p = new HwndSourceParameters("CoPulTaskbarWatcher")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,                    // 不带 WS_VISIBLE → 不显示
            ExtendedWindowStyle = 0x00000080,   // WS_EX_TOOLWINDOW → 不进 Alt+Tab
        };

        try
        {
            _watcher = new HwndSource(p);
            _watcher.AddHook(BroadcastHook);
            Trace($"watcher: hwnd=0x{_watcher.Handle:X}");
        }
        catch
        {
            // 装不上就退化成"重启 explorer 后手动重开"，不影响其余功能
            _watcher = null;
        }
    }

    private IntPtr BroadcastHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == _taskbarCreatedMsg)
        {
            Trace("TaskbarCreated 收到");
            ScheduleReattach(0);
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// 每秒检查一次"条还在不在、任务栏还是不是原来那个"。
    /// 只做两次 <c>IsWindow</c>，不进 <c>FindWindow</c>，开销可以忽略。
    /// </summary>
    private void StartWatchdog()
    {
        if (_watchdog is not null) return;

        Trace($"watchdog: 启动 elevated={Taskbar.IsElevated()}");

        _watchdog = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _watchdog.Tick += (_, _) => Poll();
        _watchdog.Start();
    }

    private void Poll()
    {
        if (_handedOff || _recovering) return;

        // 还没挂上（启动时任务栏没就绪）——交给 Attach 里的重试链，这里不插手
        if (!_attached) return;

        // 自己的窗口没了：explorer 退出时系统会连带销毁它的所有子窗口。
        // WPF 此时还活在自己的世界里（HWND 已失效），只能整窗重建。
        if (!Taskbar.IsAlive(_hwnd))
        {
            Recover(rebuild: true);
            return;
        }

        // 任务栏换了句柄（同一台机器上任务栏被重建）。自己的窗口还在说明
        // 只是脱离了父子关系，重新 SetParent 即可，不用重建。
        if (!Taskbar.IsAlive(_bar) || Taskbar.Find() != _bar)
        {
            Recover(rebuild: false);
        }
    }

    private void Recover(bool rebuild)
    {
        _recovering = true;
        try
        {
            if (rebuild)
            {
                Trace($"watchdog: 条 hwnd=0x{_hwnd:X} 已被销毁 → 重建");
                _handedOff = true;
                _watchdog?.Stop();
                RecreateRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            Trace($"watchdog: 任务栏换了（旧 0x{_bar:X}）→ 重挂");
            Taskbar.Detach(_hwnd, wasChildWindow: true);
            _attached = false;
            Attach();
            ApplyInk();
        }
        finally
        {
            _recovering = false;
        }
    }

    /// <summary>
    /// explorer 重启后重挂。必须重试 —— 任务栏窗口不是 explorer 一起来就存在的，
    /// 刚广播完那一下 <c>FindWindow("Shell_TrayWnd")</c> 实测常常还是空。
    /// </summary>
    private void ScheduleReattach(int attempt)
    {
        var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        t.Tick += (_, _) =>
        {
            t.Stop();

            IntPtr found = Taskbar.Find();
            Trace($"reattach#{attempt}: bar=0x{found:X}");
            if (found == IntPtr.Zero)
            {
                // 20 次 × 700ms ≈ 14 秒。explorer 重启到任务栏真正就绪实测要 2–5 秒，
                // 早先只留 8 次（5.6 秒）在慢机器上会直接放弃。
                if (attempt < 20) ScheduleReattach(attempt + 1);
                return;
            }

            // 父窗口被销毁时，子窗口有时会被系统一并销毁 —— 那种情况只能重建。
            // 判活必须用缓存的 _hwnd：窗口销毁后 `Handle` 会返回 0，用它判会直接判成
            // "没初始化"（第一次 7.7 验证失败就是这个原因）。
            if (!Taskbar.IsAlive(_hwnd))
            {
                Trace($"reattach#{attempt}: alive=False hwnd=0x{_hwnd:X} → 触发重建");
                _handedOff = true;
                _watchdog?.Stop();
                RecreateRequested?.Invoke(this, EventArgs.Empty);
                return;
            }

            Trace($"reattach#{attempt}: alive=True hwnd=0x{_hwnd:X}");

            // 先摘干净（清掉 WS_CHILD）再挂到新任务栏。
            // 少了 Detach，残留的子窗口样式会让 SetParent 之后的位置计算错位。
            Taskbar.Detach(_hwnd, wasChildWindow: true);
            _attached = false;
            Attach();
            ApplyInk();
        };
        t.Start();
    }

    private IntPtr SettingsChangedHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int WM_SETTINGCHANGE = 0x001A;
        const int WM_DISPLAYCHANGE = 0x007E;

        if (msg == WM_SETTINGCHANGE || msg == WM_DISPLAYCHANGE)
        {
            // 这两个消息来得非常密（主题、DPI、显示器、颜色都会发），
            // 排到 Dispatcher 队尾去做，避免在窗口过程里改窗口尺寸
            Dispatcher.BeginInvoke(new Action(() =>
            {
                ApplyInk();
                Reposition();
            }));
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        Trace("窗口关闭");

        _watchdog?.Stop();
        _watchdog = null;

        // 监听窗口是独立的顶层窗口，不随本窗口关闭自动销毁，必须显式释放，
        // 否则重建实例时会多留一个空窗口在后台
        _watcher?.Dispose();
        _watcher = null;
        base.OnClosed(e);
    }

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount == 2)
        {
            MainWindowRequested?.Invoke(this, EventArgs.Empty);
            e.Handled = true;
        }
    }

    private void Shell_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 菜单由 XAML 的 ContextMenu 自动弹出，这里只需要把事件标记为已处理，
        // 否则点击会穿透到任务栏，触发任务栏自己的右键菜单（与 7.6 的"不抢占"冲突）
        e.Handled = true;
    }

    private void ShowMain_Click(object sender, RoutedEventArgs e)
        => MainWindowRequested?.Invoke(this, EventArgs.Empty);

    private void ToFloat_Click(object sender, RoutedEventArgs e)
        => FloatRequested?.Invoke(this, EventArgs.Empty);

    private void InkAuto_Click(object sender, RoutedEventArgs e) => SetInk(InkMode.Auto);
    private void InkLight_Click(object sender, RoutedEventArgs e) => SetInk(InkMode.Light);
    private void InkDark_Click(object sender, RoutedEventArgs e) => SetInk(InkMode.Dark);

    private void SetInk(InkMode mode)
    {
        _ink = mode;
        ApplyInk();

        if (!PersistToConfig) return;
        _config.TaskbarInk = mode.ToString();
        ConfigStore.SaveCurrent();
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        Application.Current.Shutdown();
    }
}
