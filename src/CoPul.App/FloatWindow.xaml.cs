using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CoPul.App.Interop;
using CoPul.App.Sdk;

namespace CoPul.App;

/// <summary>
/// 悬浮窗（需求第 6 章）。M2 当前进度：
/// <list type="bullet">
/// <item>已完成：无边框圆角长条 + 六字段两行式 + 真实数据 + 毛玻璃 + 拖动 + 双击切大窗口 + 右键菜单</item>
/// <item>已完成：浅色 / 深色两套玻璃外观可切换，五档浓度可调</item>
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
    public enum GlassTheme
    {
        Light,
        Dark,
    }

    /// <summary>
    /// 浓度档位 → (accent tint alpha, Shell 遮罩 alpha)。
    ///
    /// <para>
    /// accent 的 alpha **在每一档都固定**，浓度全部交给 Shell：一个旋钮比两个旋钮好调，
    /// 也避免两层互相补偿导致"调了没反应"。accent 那层只负责铺一点底色（白雾 / 暗雾），
    /// Shell 才是真正的浓度旋钮。
    /// </para>
    ///
    /// <para>
    /// 数值来自实测而不是拍脑袋：在 6 DIP 棋盘格背景上，浅色档三档的窗内亮度是
    /// 153 / 178 / 225（棋盘平均 142），对应遮盖约 11% / 35% / 80%。
    /// 五档按这条曲线插值到 15%–92%，跨度均匀，每一档都能看出差别。
    /// </para>
    ///
    /// <para>
    /// 深浅两套仍然分开：浅色是"白雾提亮"，深色是"暗雾压暗"，同名档位的遮盖值不同。
    /// 序号与菜单一一对应：0 = 很透 … 4 = 很实，默认取 2（适中）。
    /// </para>
    /// </summary>
    private static readonly (double Accent, double Shell)[] LightSteps =
    {
        (0.10, 0.00),   // 很透（实测遮盖 ~20%）
        (0.10, 0.25),
        (0.10, 0.47),   // 适中（默认，~60%）
        (0.10, 0.69),
        (0.10, 0.88),   // 很实（~93%）
    };

    /// <summary>
    /// 深色档的 accent alpha 给 0：实测深色 tint 在这条路上几乎不贡献（alpha 0.35 时
    /// 窗口内颜色与背景差不到 1/255），浓度全由 Shell 那层深色遮罩决定，给了也是白给。
    /// 这里靠 <c>AccentState</c> 继续拿到模糊，靠 Shell 拿到压暗。
    /// </summary>
    private static readonly (double Accent, double Shell)[] DarkSteps =
    {
        (0.00, 0.10),   // 很透
        (0.00, 0.32),
        (0.00, 0.52),   // 适中（默认）
        (0.00, 0.72),
        (0.00, 0.90),   // 很实
    };

    private GlassTheme _theme = GlassTheme.Light;
    private int _level = 2;      // 0-based：2 = 适中
    private bool _glass = true;

    /// <summary>
    /// HWND 是否已就绪。没有这个守卫时，构造函数里设 Theme / GlassLevel 会触发一次
    /// 无效的 ApplyGlass（窗口还没 HWND，DWM 调用全部落空），日志里会多出一条"未取到 HWND"。
    /// </summary>
    private bool _hwndReady;

    public FloatWindow(DashboardState state)
    {
        InitializeComponent();
        DataContext = state;

        SourceInitialized += (_, _) =>
        {
            _hwndReady = true;
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
    /// 启动时指定外观，供自测参数用（<c>--glass-dark</c> / <c>--glass-level N</c>）。
    /// 必须在 Show 之前设，否则 UI 已经按默认值画过一遍。
    /// </summary>
    public GlassTheme Theme
    {
        get => _theme;
        set
        {
            if (_theme == value) return;
            _theme = value;
            SyncMenuChecks();
            if (_hwndReady) ApplyGlass(_glass);
        }
    }

    /// <summary>浓度档 1–5（1 最透）。越界会被夹住，不抛异常——菜单 Tag 解析失败也不该炸窗口。</summary>
    public int GlassLevel
    {
        get => _level + 1;
        set
        {
            int index = Math.Clamp(value, 1, 5) - 1;
            if (_level == index) return;
            _level = index;
            SyncMenuChecks();
            if (_hwndReady) ApplyGlass(_glass);
        }
    }

    /// <summary>
    /// 次值文字颜色。浅色玻璃用深灰、深色玻璃用原来的中灰。
    /// 做成依赖属性是为了让 XAML 里六个 TextBlock 都绑到窗口这一处，
    /// 切换主题时不用逐个改 Style（改 Style 会丢掉元素上的本地 Foreground）。
    /// </summary>
    public static readonly DependencyProperty LabelBrushProperty =
        DependencyProperty.Register(nameof(LabelBrush), typeof(Brush), typeof(FloatWindow),
            new PropertyMetadata(null));

    public Brush LabelBrush
    {
        get => (Brush)GetValue(LabelBrushProperty);
        set => SetValue(LabelBrushProperty, value);
    }

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

    // =================================================================
    // 玻璃：主题 × 浓度
    // =================================================================

    private void ApplyGlass(bool on)
    {
        _glass = on;
        bool light = _theme == GlassTheme.Light;

        // 两套画刷先定下来：文字颜色与玻璃底是一体的，不能只换一半
        ApplyTextTheme(light);

        if (!on)
        {
            // 关掉毛玻璃时保留主题：浅色 = 浅色纸面，深色 = 大窗口那套深底。
            // 这样"关玻璃"只是去掉模糊，不是把外观整个换掉。
            WindowGlass.Clear(this);
            GlassMode = "关闭";
            Shell.Background = light ? Palette.Brush("BrushGlassSolid") : Palette.Brush("BrushBg");
            return;
        }

        (double accent, double shell) = (light ? LightSteps : DarkSteps)[_level];

        // 深浅只差 dark 标志与 tint 的 RGB，accent 那条路是同一段代码（A-36）
        GlassMode = WindowGlass.Apply(this, WindowGlass.Backdrop.Acrylic, dark: !light, tintOpacity: accent);

        // Shell 遮罩：浅色铺白、深色铺 Palette 背景色，alpha 由档位给
        Shell.Background = Tint(
            light ? Color.FromRgb(0xF6, 0xF8, 0xFB) : Color.FromRgb(0x0F, 0x12, 0x16),
            shell);
    }

    private void ApplyTextTheme(bool light)
    {
        if (DataContext is DashboardState state) state.SetFloatTheme(light);

        LabelBrush = Palette.Brush(light ? "BrushTextSecondaryLight" : "BrushTextSecondary");

        _edgeNormal = Palette.Brush(light ? "BrushGlassEdge" : "BrushGlassEdgeDark");
        _edgeHover = Palette.Brush(light ? "BrushGlassEdgeHover" : "BrushGlassEdgeDarkHover");
        Shell.BorderBrush = _edgeNormal;
    }

    private Brush _edgeNormal = Palette.Brush("BrushGlassEdge");
    private Brush _edgeHover = Palette.Brush("BrushGlassEdgeHover");

    private static SolidColorBrush Tint(Color rgb, double alpha)
    {
        var brush = new SolidColorBrush(Color.FromArgb(
            (byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), rgb.R, rgb.G, rgb.B));
        brush.Freeze();
        return brush;
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

    /// <summary>
    /// 悬停时把描边加深一档。浅色玻璃上"提亮"等于看不见，所以是加深（黑 12% → 24%）；
    /// 深色玻璃则反过来提亮（白 12% → 22%）。
    /// </summary>
    private void Shell_MouseEnter(object sender, MouseEventArgs e) => Shell.BorderBrush = _edgeHover;

    private void Shell_MouseLeave(object sender, MouseEventArgs e) => Shell.BorderBrush = _edgeNormal;

    private void ShowMain_Click(object sender, RoutedEventArgs e)
        => MainWindowRequested?.Invoke(this, EventArgs.Empty);

    private void ThemeLight_Click(object sender, RoutedEventArgs e) => Theme = GlassTheme.Light;

    private void ThemeDark_Click(object sender, RoutedEventArgs e) => Theme = GlassTheme.Dark;

    private void Level_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int level))
            GlassLevel = level;
    }

    private void ToggleGlass_Click(object sender, RoutedEventArgs e)
        => ApplyGlass(GlassMenu.IsChecked);

    /// <summary>
    /// 菜单勾选状态跟实际状态对齐。用 IsChecked 直接互斥即可——菜单项是 IsCheckable 的，
    /// 点哪项 WPF 会自己先翻掉 IsChecked，所以这里只负责把"另一侧"复位。
    /// </summary>
    private void SyncMenuChecks()
    {
        if (ThemeLightMenu is null) return;   // 构造函数里 SourceInitialized 之前可能还没建好

        ThemeLightMenu.IsChecked = _theme == GlassTheme.Light;
        ThemeDarkMenu.IsChecked = _theme == GlassTheme.Dark;

        Level1Menu.IsChecked = _level == 0;
        Level2Menu.IsChecked = _level == 1;
        Level3Menu.IsChecked = _level == 2;
        Level4Menu.IsChecked = _level == 3;
        Level5Menu.IsChecked = _level == 4;
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
