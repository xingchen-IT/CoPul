using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using CoPul.App.Interop;
using CoPul.App.Sdk;

namespace CoPul.App;

/// <summary>
/// 大窗口（需求第 5 章）。M1 只做「能跑、数值对」：
/// 无毛玻璃（5.5 的开关留到 M2）、无折叠（5.4 的折叠状态留到 M1.2）。
/// </summary>
public partial class MainWindow : Window
{
    private readonly DashboardState _state = new();
    private readonly CollectorHost _host;

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

        _host = new CollectorHost(Dispatcher);
        _host.InfoReady += info =>
        {
            _state.ApplyInfo(info);
            _state.ApplyProviders(_host.Providers);
        };
        _host.FrameReady += (snap, _) =>
        {
            if (Paused) return;

            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            _state.ApplySnapshot(snap);
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

        Loaded += (_, _) => { /* 占位：M1.2 会在这里做尺寸/位置恢复 */ };
        Closed += (_, _) => _host.Dispose();

        // 采集在后台线程跑，不等窗口 Loaded——这样 --hidden 自测与"窗口还没画出来"的场景
        // 都能拿到数据，UI 侧只要容忍 SystemInfo 为 null 即可（A-12）。
        _host.Start();
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
