using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using CoPul.App.Interop;
using CoPul.App.Sdk;

namespace CoPul.App;

/// <summary>
/// 悬浮窗（需求第 6 章）。M2 第一阶段：
/// <list type="bullet">
/// <item>已完成：无边框圆角长条 + 六字段两行式 + 真实数据 + 毛玻璃 + 拖动 + 双击切大窗口 + 右键菜单</item>
/// <item>待做：边缘吸附（6.5）、位置按比例持久化（6.7）、锁定位置 / 三档手动缩放（Q-05）</item>
/// </list>
///
/// <para>
/// 数据不自己采：直接绑 <see cref="MainWindow"/> 那一份 <see cref="DashboardState"/>。
/// 悬浮窗与大窗口是同一份快照的两个视图，各自 new 一个 CollectorHost 会起两份 PresentMon
/// 抢同一个 ETW 会话，FPS 直接互相踩。
/// </para>
/// </summary>
public partial class FloatWindow : Window
{
    /// <summary>
    /// 着色层。定义在 XAML 里是 #590F1216（35%），这里改成 20%：
    /// accent 自己已经带了 45% 的 tint，两层叠起来的总着色才是 6.4 要求的那 55–70%。
    /// 两层都吃满会让背景只剩两成、模糊看不出差别（实测差 4/255）。
    /// </summary>
    private static readonly SolidColorBrush GlassOverlay = Frozen(Color.FromArgb(0x33, 0x0F, 0x12, 0x16));

    /// <summary>关掉毛玻璃时的不透明底，等同大窗口背景。</summary>
    private static readonly SolidColorBrush SolidBackdrop = Frozen(Color.FromRgb(0x0F, 0x12, 0x16));

    private static readonly SolidColorBrush EdgeNormal = Frozen(Color.FromArgb(0x1F, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush EdgeHover = Frozen(Color.FromArgb(0x38, 0xFF, 0xFF, 0xFF));

    private bool _glass = true;

    public FloatWindow(DashboardState state)
    {
        InitializeComponent();
        DataContext = state;

        SourceInitialized += (_, _) =>
        {
            WindowGlass.HideFromAltTab(this);
            ApplyGlass(_glass);
        };

        // 尺寸要等 SizeToContent 算完才能居中，所以位置放到 Loaded
        Loaded += (_, _) =>
        {
            // Loaded 之后 HwndTarget 才稳定，这里补一次透明底（在 SourceInitialized 里设会被覆盖）。
            // 少了这一句，窗口底会变成纯黑，Acrylic 看起来完全没生效。
            WindowGlass.MakeClientTransparent(this);

            if (CenterOnScreen) PlaceAtCenter();
            else PlaceAtBottomLeft();
        };
    }

    /// <summary>验收毛玻璃时把窗口挪到屏幕中间，方便连同背景一起截（--float-center）。</summary>
    public bool CenterOnScreen { get; set; }

    /// <summary>
    /// 实际生效的毛玻璃方式，由 <see cref="WindowGlass.Apply"/> 返回。
    /// 自测/截图验收看这个字段就知道是系统背景生效了还是退到了 accent。
    /// </summary>
    public string GlassMode { get; private set; } = "未应用";

    /// <summary>双击或被菜单要求"回大窗口"。窗口之间不互相引用，由 App 接线。</summary>
    public event EventHandler? MainWindowRequested;

    /// <summary>
    /// 默认位置：当前显示器工作区左下角，距左 12、距底 12（6.7 的兜底规则；
    /// 按比例持久化在下一阶段接上，这里先保证"第一次出来就在看得见的地方"）。
    /// </summary>
    private void PlaceAtBottomLeft()
    {
        Rect wa = SystemParameters.WorkArea;   // 已经是 DIP，与 Window.Left/Top 同一单位（A-27）
        Left = wa.Left + 12;
        Top = wa.Bottom - Height - 12;
    }

    private void PlaceAtCenter()
    {
        Rect wa = SystemParameters.WorkArea;
        Left = wa.Left + (wa.Width - ActualWidth) / 2;
        Top = wa.Top + (wa.Height - ActualHeight) / 2;
    }

    private void ApplyGlass(bool on)
    {
        _glass = on;

        if (on)
        {
            GlassMode = WindowGlass.Apply(this, WindowGlass.Backdrop.Acrylic, dark: true);
            Shell.Background = GlassOverlay;
            return;
        }

        WindowGlass.Clear(this);
        GlassMode = "关闭";
        Shell.Background = SolidBackdrop;
    }

    // =================================================================
    // 交互（6.5）
    // =================================================================

    private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            // 双击切大窗口（Q-05）
            MainWindowRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标在 DragMove 之前就松开了（双击的第二下、或者远程会话里丢事件）——
            // 这不是错误，吞掉即可，否则会冒到 Dispatcher 上写一份无意义的崩溃日志。
        }
    }

    private void Shell_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 右键菜单挂在 Border 上，Topmost 窗口里也照常弹（ContextMenu 自身是独立 popup）
        if (Shell.ContextMenu is not { } menu) return;

        menu.PlacementTarget = Shell;
        menu.IsOpen = true;
        e.Handled = true;
    }

    /// <summary>6.5 没有要求悬停反馈，这里只把 1px 描边提亮一档——玻璃边缘的观感，不做展开。</summary>
    private void Shell_MouseEnter(object sender, MouseEventArgs e) => Shell.BorderBrush = EdgeHover;

    private void Shell_MouseLeave(object sender, MouseEventArgs e) => Shell.BorderBrush = EdgeNormal;

    private void ShowMain_Click(object sender, RoutedEventArgs e)
        => MainWindowRequested?.Invoke(this, EventArgs.Empty);

    private void ToggleGlass_Click(object sender, RoutedEventArgs e)
        => ApplyGlass(GlassMenu.IsChecked);

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
