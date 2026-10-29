using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CoPul.App.Interop;
using CoPul.App.Sdk;

namespace CoPul.App;

/// <summary>
/// 大窗口（需求第 5 章）。M1.2 起支持：窗口几何持久化（5.4 + A-07）、区块折叠（5.4）、
/// D04 分区 10s 刷新（4.4）、D03 每盘速率（R21）。
/// 仍不做：毛玻璃（5.5，M2）、托盘（5.4 点击 — 的完整行为，M2）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly DashboardState _state = new();
    private readonly CollectorHost _host;
    private readonly AppConfig _config;

    /// <summary>自测用：从快照到界面赋值的累计耗时，用来区分「采集贵」还是「渲染贵」。</summary>
    public double ApplyTotalMs { get; private set; }
    public long ApplyCount { get; private set; }

    /// <summary>自测用：强制同步布局后量到的累计耗时，用来判断成本是不是在 measure/arrange。</summary>
    public double LayoutTotalMs { get; private set; }

    /// <summary>自测用：只在 --stats 下开启，正常运行时布局仍由 WPF 自己延后处理。</summary>
    public bool MeasureLayout { get; set; }

    /// <summary>
    /// 暂停把快照写进界面。自测（<c>--freeze</c>）用它分离「渲染静态窗口」与「每帧更新」的成本；
    /// M2 起悬浮窗拖动时也会置位（6.5）。实际标志存在共享的 <see cref="DashboardState"/> 上，
    /// 这里是转发 —— 两个窗口看到的必须是同一个开关。
    /// </summary>
    public bool Paused
    {
        get => _state.Paused;
        set => _state.Paused = value;
    }

    /// <summary>圆角半径（DIP）。与 XAML 里外层 Border 的 CornerRadius 必须一致，否则描边会露角。</summary>
    private const double CornerRadiusDip = 8;

    /// <summary>true = 已交给 Win11 的 DWM 处理圆角，不用再自己维护窗口 Region。</summary>
    private bool _dwmRoundCorners;

    private IntPtr _hwnd;

    public CollectorHost Host => _host;

    /// <summary>自测用：允许外部改展示层的自测开关。</summary>
    public DashboardState State => _state;

    /// <summary>
    /// 「切到悬浮窗」（M2）。由 App 接线：MainWindow 不认识 FloatWindow——
    /// 两个窗口共享同一份 <see cref="DashboardState"/>，但谁显示谁由 App 决定，
    /// 窗口之间直接 new 会把"数据只有一份、界面可以有两个"这条约束弄丢。
    /// </summary>
    public Action? FloatRequested { get; set; }

    /// <summary>
    /// 「切到任务栏条」（M3）。理由同 <see cref="FloatRequested"/>：
    /// 三个视图共用一份 <see cref="DashboardState"/>，谁显示谁由 App 编排，
    /// 窗口之间互相 new 会把"数据只有一份、界面可以有三个"这条约束弄丢。
    /// </summary>
    public Action? TrayRequested { get; set; }

    public MainWindow()
    {
        InitializeComponent();

        DataContext = _state;

        // 用共享实例而不是自己 Load 一份：悬浮窗也要写同一份配置（外观 / 位置），
        // 各持一份会互相覆盖（见 ConfigStore.Current 的说明）。
        _config = ConfigStore.Current;
        RestoreLayout(_config);

        _host = new CollectorHost(Dispatcher);
        TrendView.Series = _state.Trend;
        TempView.Series = _state.TrendTemp;   // 量程与单位在 XAML 里给了：0–100°C
        FpsView.Series = _state.TrendFps;     // 量程 240 FPS，同样在 XAML 里

        // 进程采集是全部采集里最贵的一组（一轮约 20ms），启动时这块若已折叠就别采
        _host.ProcessSamplingEnabled = !_state.ProcBlock.Collapsed;
        // PresentMon 是独立进程，自己就要吃 0.3–0.5% 单核，折叠了就别让它烧着
        _host.FpsSamplingEnabled = !_state.FpsBlock.Collapsed;

        // FPS 的可用状态是异步的（PresentMon 起得来、会话却可能 1 秒后才被拒），
        // 所以除了启动时那一次，还得听运行中的状态变化
        _host.ProvidersChanged += providers => _state.ApplyProviders(providers);

        _host.InfoReady += info =>
        {
            _state.ApplyInfo(info);
            _state.ApplyProviders(_host.Providers);
        };
        _host.PartitionsRefreshed += info => _state.ApplyPartitions(info.Partitions);
        _host.FrameReady += (snap, _) =>
        {
            if (Paused) return;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _state.ApplySnapshot(snap);
            // N05 网卡名 / N03 探测目标不在快照里（字符串），走旁路；无变化时内部会跳过通知
            _state.ApplyNetworkContext(_host.ActiveNicName, _host.PingTarget);

            // 进程表与三张趋势图：折叠时整段跳过（趋势图连采样都不记，省一半成本）
            if (!_state.ProcBlock.Collapsed)
                _state.ApplyProcesses(_host.TopProcesses, _host.ProcessGpuAvailable);

            // FPS 六格：目标进程名与"会话是否接上"都在快照外面，走旁路
            _state.ApplyFps(snap, _host.FpsTargetApp, _host.FpsCollecting, _host.FpsUnavailableReason);

            if (!_state.TrendBlock.Collapsed)
            {
                _state.PushTrend(snap);
                TrendView.Refresh();
            }

            if (!_state.TempBlock.Collapsed)
            {
                _state.PushTrendTemp(snap);
                TempView.Refresh();
            }

            if (!_state.FpsBlock.Collapsed)
            {
                _state.PushTrendFps(snap);
                FpsView.Refresh();
            }

            ApplyTotalMs += (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                            / System.Diagnostics.Stopwatch.Frequency;
            ApplyCount++;

            if (MeasureLayout)
            {
                long l0 = System.Diagnostics.Stopwatch.GetTimestamp();
                UpdateLayout();
                LayoutTotalMs += (System.Diagnostics.Stopwatch.GetTimestamp() - l0) * 1000.0
                                 / System.Diagnostics.Stopwatch.Frequency;
            }
        };

        Closed += (_, _) =>
        {
            SaveLayout();      // 必须在 Dispose 前：还要读 Window 的几何属性
            _host.Dispose();
        };

        // 采集在后台线程跑，不等窗口 Loaded——这样 --hidden 自测与"窗口还没画出来"的场景
        // 都能拿到数据，UI 侧只要容忍 SystemInfo 为 null 即可（A-12）。
        _host.Start();
    }

    /// <summary>
    /// 恢复上次的窗口几何与折叠状态（5.4 "关闭后重开恢复上次位置与尺寸，且位置必须落在
    /// 当前可见显示器内"）。
    ///
    /// 必须在构造函数里做：<see cref="Window.WindowStartupLocation"/> 只在显示前有效，
    /// 放到 Loaded 里改已经晚了，窗口会先按 CenterScreen 闪一下再跳走。
    /// </summary>
    private void RestoreLayout(AppConfig config)
    {
        // 折叠状态：先恢复，这样第一帧就不会白画一遍被折叠的区块
        foreach (string id in config.CollapsedBlocks)
            _state.Block(id)?.Restore(true);

        bool hasPos = !double.IsNaN(config.WindowLeft) && !double.IsNaN(config.WindowTop);

        // WPF 的 Left/Top/Width 是 DIP，Win32 的 MONITORINFO 是物理像素。
        // 构造时窗口还没显示，GetDpi 返回主屏 DPI，而窗口首次显示就在主屏，够用。
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        // 工作区（DIP）：有保存位置时按"上次那块显示器"算；取不到就退到主屏。
        // 拔掉副屏 / 改分辨率后，保存的坐标可能已经不在任何显示器上（A-07）。
        // 注意 `workPx` 必须先 default 再作为 out 传入：短路表达式里的 out var
        // 在 hasPos 为 false 时编译器视为"可能未赋值"（CS0170）。
        Win32Window.RECT workPx = default;
        bool hasWork = hasPos && Win32Window.TryGetWorkArea(config.WindowLeft * sx, config.WindowTop * sy,
                                                          config.WindowWidth * sx,
                                                          Math.Max(MinHeight, config.WindowHeight) * sy,
                                                          out workPx);
        double workL, workT, workR, workB;
        if (hasWork)
        {
            workL = workPx.Left / sx;
            workT = workPx.Top / sy;
            workR = workPx.Right / sx;
            workB = workPx.Bottom / sy;
        }
        else
        {
            Rect work = SystemParameters.WorkArea;      // DIP，主屏
            workL = work.Left;
            workT = work.Top;
            workR = work.Right;
            workB = work.Bottom;
        }

        double maxW = Math.Max(MinWidth, workR - workL);
        double maxH = Math.Max(MinHeight, workB - workT);

        // A-32：默认几何不写死，按工作区算。
        // 宽度用保存值（默认 820），高度用保存值，没有保存值就用满工作区——
        // 原来写死的 1460 只在这台屏上"看起来对"，换台机器要么超屏、要么多留一截空白。
        double w = Math.Clamp(config.WindowWidth, MinWidth, maxW);
        double h = config.WindowHeight > 0 ? Math.Clamp(config.WindowHeight, MinHeight, maxH) : maxH;

        Width = w;
        Height = h;

        if (hasWork)
        {
            Left = Math.Clamp(config.WindowLeft, workL, Math.Max(workL, workR - w));
            Top = Math.Clamp(config.WindowTop, workT, Math.Max(workT, workB - h));

            // 覆盖 XAML 的 CenterScreen，否则上面的 Left/Top 会被丢掉
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else
        {
            // 首次运行 / 位置失效：居中，尺寸已按工作区钳过
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }

        if (config.WindowMaximized) WindowState = WindowState.Maximized;
    }

    /// <summary>
    /// 保存窗口几何与折叠状态（A-07 原子写入）。只在退出与用户折叠区块时调用，
    /// 不进采集循环——11 章要求"配置写入频率仅退出/变更时"。
    /// </summary>
    private void SaveLayout()
    {
        // 最大化/最小化时 Left/Top/Width/Height 是"当前"的尺寸，必须存 RestoreBounds：
        // 否则"最大化退出 → 重开 → 还原"会还原成一个占满屏幕的窗口。
        Rect bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;

        if (!bounds.IsEmpty && bounds.Width >= MinWidth && bounds.Height >= MinHeight)
        {
            _config.WindowLeft = bounds.X;
            _config.WindowTop = bounds.Y;
            _config.WindowWidth = bounds.Width;
            _config.WindowHeight = bounds.Height;
        }

        _config.WindowMaximized = WindowState == WindowState.Maximized;
        _config.CollapsedBlocks = _state.Blocks.Where(b => b.Collapsed).Select(b => b.Id).ToArray();

        ConfigStore.Save(_config);
    }

    private void ToggleBlock_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { Tag: string id }) return;

        var block = _state.Block(id);
        if (block is null) return;

        block.Toggle();

        // 折叠进程区块时顺手把最贵的两组采集关掉（见 CollectorHost.ProcessSamplingEnabled）
        if (block == _state.ProcBlock) _host.ProcessSamplingEnabled = !block.Collapsed;
        if (block == _state.FpsBlock) _host.FpsSamplingEnabled = !block.Collapsed;

        SaveLayout();   // 折叠状态按 5.4 持久化
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 无边框窗口最大化会盖住任务栏，必须自己把尺寸钳到工作区
        _hwnd = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(_hwnd)?.AddHook(WndProc);

        // 圆角优先交给系统：DWM 会按当前 DPI 裁切并抗锯齿，且最大化时自动恢复直角。
        // Win10 没有这个属性，退到 Region 兜底（锯齿但可用），由尺寸变化维持。
        _dwmRoundCorners = Win32Window.TryEnableDwmRoundCorners(_hwnd);
        if (!_dwmRoundCorners)
        {
            ApplyRoundRegion();
            SizeChanged += (_, _) => ApplyRoundRegion();
        }

        StateChanged += (_, _) =>
        {
            // Region 路径下最大化必须是直角，否则任务栏边缘会缺一小块
            if (!_dwmRoundCorners) ApplyRoundRegion();
        };
    }

    /// <summary>
    /// Win10 兜底路径：按当前 DPI 换算成物理像素再裁 Region。
    /// 不换算会在 150% 缩放下裁出一个明显偏小的圆角（Region 只认物理像素）。
    /// </summary>
    private void ApplyRoundRegion()
    {
        if (_hwnd == IntPtr.Zero) return;

        double scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Win32Window.ApplyRoundRegion(_hwnd,
            (int)Math.Round(ActualWidth * scale),
            (int)Math.Round(ActualHeight * scale),
            (int)Math.Round(CornerRadiusDip * scale),
            round: WindowState != WindowState.Maximized);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32Window.WM_GETMINMAXINFO && Win32Window.TryClampToWorkArea(hwnd, lParam))
            handled = true;

        return IntPtr.Zero;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            e.Handled = true;
            return;
        }

        if (e.LeftButton == MouseButtonState.Pressed)
        {
            try { DragMove(); }
            catch (InvalidOperationException) { /* 拖拽过程中窗口被关闭，忽略 */ }
        }
    }

    private void ToggleMaximize()
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Settings_Click(object sender, RoutedEventArgs e)
        => _state.ShowHint("设置页（挡位 / 托盘 / 毛玻璃开关）在 M2 实现；v1 固定 0.5s 挡位。");

    /// <summary>
    /// 切到悬浮窗（M2）。这里只发请求，真正谁 Hide 谁 Show 由 App 决定——
    /// 悬浮窗需要同一份 DashboardState，自己 new 会变成两套采集。
    /// </summary>
    private void Float_Click(object sender, RoutedEventArgs e)
    {
        if (FloatRequested is null)
        {
            _state.ShowHint("悬浮窗未接线（以 --float 或正常启动时可用）");
            return;
        }

        FloatRequested();
    }

    /// <summary>切到任务栏条（M3）。同样只发请求，Show/Hide 由 App 编排。</summary>
    private void Tray_Click(object sender, RoutedEventArgs e)
    {
        if (TrayRequested is null)
        {
            _state.ShowHint("任务栏条未接线");
            return;
        }

        TrayRequested();
    }
}
