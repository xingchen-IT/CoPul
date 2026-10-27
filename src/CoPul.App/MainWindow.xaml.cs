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

    /// <summary>自测用：暂停把快照写进界面，用于分离「WPF 渲染静态窗口」与「每次更新的布局成本」。</summary>
    public bool Paused { get; set; }

    public CollectorHost Host => _host;

    /// <summary>自测用：允许外部改展示层的自测开关。</summary>
    public DashboardState State => _state;

    public MainWindow()
    {
        InitializeComponent();

        DataContext = _state;

        _config = ConfigStore.Load();
        RestoreLayout(_config);

        _host = new CollectorHost(Dispatcher);
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

        double w = Math.Max(MinWidth, config.WindowWidth);
        double h = Math.Max(MinHeight, config.WindowHeight);

        bool hasPos = !double.IsNaN(config.WindowLeft) && !double.IsNaN(config.WindowTop);

        // WPF 的 Left/Top/Width 是 DIP，Win32 的 MONITORINFO 是物理像素。
        // 构造时窗口还没显示，GetDpi 返回主屏 DPI，而窗口首次显示就在主屏，够用。
        var dpi = VisualTreeHelper.GetDpi(this);
        double sx = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
        double sy = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

        if (hasPos && Win32Window.TryGetWorkArea(config.WindowLeft * sx, config.WindowTop * sy,
                                                w * sx, h * sy, out var workPx))
        {
            // 换回 DIP 再钳制，避免把物理像素直接和 DIP 比较
            double workL = workPx.Left / sx;
            double workT = workPx.Top / sy;
            double workR = workPx.Right / sx;
            double workB = workPx.Bottom / sy;

            // 尺寸也要钳：4K 下存的 3840×2000 换到 1080p 屏会超出工作区
            w = Math.Min(w, workR - workL);
            h = Math.Min(h, workB - workT);

            Width = w;
            Height = h;
            Left = Math.Clamp(config.WindowLeft, workL, Math.Max(workL, workR - w));
            Top = Math.Clamp(config.WindowTop, workT, Math.Max(workT, workB - h));

            // 覆盖 XAML 的 CenterScreen，否则上面的 Left/Top 会被丢掉
            WindowStartupLocation = WindowStartupLocation.Manual;
        }
        else
        {
            // 没有配置，或上次的位置已经不在任何显示器上（拔了副屏 / 改了分辨率）
            Width = w;
            Height = h;
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
        SaveLayout();   // 折叠状态按 5.4 持久化
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 无边框窗口最大化会盖住任务栏，必须自己把尺寸钳到工作区
        var handle = new WindowInteropHelper(this).Handle;
        HwndSource.FromHwnd(handle)?.AddHook(WndProc);
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
}
