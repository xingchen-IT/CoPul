using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CoPul.App.Interop;
using CoPul.App.Sdk;

namespace CoPul.App;

/// <summary>
/// 悬浮窗（需求第 6 章）。M2 当前进度：
/// <list type="bullet">
/// <item>已完成：无边框圆角长条 + 六字段两行式 + 真实数据 + 毛玻璃 + 拖动 + 双击切大窗口 + 右键菜单</item>
/// <item>已完成：浅色 / 深色两套玻璃外观可切换，七档浓度可调</item>
/// <item>已完成：边缘吸附、位置按比例持久化、锁定位置、外观偏好持久化（6.5 / 6.6 / 6.7）</item>
/// <item>待做：三档手动缩放（Q-05）、投影（6.4）</item>
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
    /// 七档按这条曲线插值，跨度均匀，每一档都能看出差别。
    /// </para>
    ///
    /// <para>
    /// 深浅两套仍然分开：浅色是"白雾提亮"，深色是"暗雾压暗"，同名档位的遮盖值不同。
    /// 序号与菜单一一对应：0 = 极透 … 6 = 极实，默认取 3（适中）。
    /// </para>
    ///
    /// <para>
    /// <b>前两档是"透到几乎没有着色"，只能调 accent 的 tint。</b>Shell 遮罩已经到 0 了，
    /// 再往下压就会变负；此时唯一的着色来源是 accent 层，所以这两档把 accent 从 0.10
    /// 压到 0.04 / 0.00。0.00 那档等于"只模糊、不叠色"——背景是什么颜色，窗口就是它的模糊版，
    /// <b>浅色主题下遇到暗壁纸时深色文字会读不清</b>，这是这条路的物理下限，不是参数没调够。
    /// </para>
    /// </summary>
    private static readonly (double Accent, double Shell, bool BlurOnly)[] LightSteps =
    {
        (0.00, 0.00, true),    // 极透：BLURBEHIND 基底为 0，实测遮盖 2%（无模糊）
        (0.00, 0.00, false),   // 很透：亚克力自带的 17% 基底就是下限
        (0.10, 0.14, false),   // 较透 ~36%
        (0.10, 0.47, false),   // 适中（默认）~60%
        (0.10, 0.69, false),   // 较实 ~77%
        (0.10, 0.88, false),   // 很实 ~94%
        (0.10, 0.97, false),   // 极实 ~98%
    };

    /// <summary>
    /// 深色档的 accent alpha 给 0：实测深色 tint 在这条路上几乎不贡献（alpha 0.35 时
    /// 窗口内颜色与背景差不到 1/255），浓度全由 Shell 那层深色遮罩决定，给了也是白给。
    /// 这里靠 <c>AccentState</c> 继续拿到模糊，靠 Shell 拿到压暗。
    /// </summary>
    private static readonly (double Accent, double Shell, bool BlurOnly)[] DarkSteps =
    {
        (0.00, 0.00, true),    // 极透（无模糊）
        (0.00, 0.00, false),   // 很透 ~17%
        (0.00, 0.22, false),   // 较透 ~36%
        (0.00, 0.52, false),   // 适中（默认）~52%
        (0.00, 0.72, false),   // 较实 ~72%
        (0.00, 0.90, false),   // 很实 ~90%
        (0.00, 0.97, false),   // 极实 ~97%
    };

    private GlassTheme _theme = GlassTheme.Light;
    private int _level = 3;      // 0-based：3 = 适中
    private bool _glass = true;

    /// <summary>位置锁（6.5）。锁定后拖动无效，但双击、右键菜单照常。</summary>
    private bool _locked;

    /// <summary>共享数据状态。用来在拖动期间置 <see cref="DashboardState.Paused"/>。</summary>
    private readonly DashboardState _state;

    /// <summary>共享配置。<see cref="ConfigStore.Current"/> 的同一份，改完直接 <see cref="ConfigStore.SaveCurrent"/>。</summary>
    private readonly AppConfig _config;

    /// <summary>
    /// HWND 是否已就绪。没有这个守卫时，构造函数里设 Theme / GlassLevel 会触发一次
    /// 无效的 ApplyGlass（窗口还没 HWND，DWM 调用全部落空），日志里会多出一条"未取到 HWND"。
    /// </summary>
    private bool _hwndReady;

    public FloatWindow(DashboardState state, AppConfig config)
    {
        InitializeComponent();

        _state = state;
        _config = config;
        DataContext = state;

        // 外观从配置读回。必须先于 InitializeComponent 之后、Show 之前落到字段上，
        // 否则窗口会先按默认值画一遍再被改掉（闪一下浅色再变深色）。
        if (Enum.TryParse(config.FloatTheme, ignoreCase: true, out GlassTheme theme))
            _theme = theme;
        _level = Math.Clamp(config.FloatGlassLevel, 1, LevelCount) - 1;
        _glass = config.FloatGlassEnabled;
        _locked = config.FloatLocked;
        _scalePercent = NormalizeScale(config.FloatScale);

        // 缩放必须在 Show 之前落定：窗口尺寸会决定 SizeToContent 算出的宽度，
        // 而定位（ContentRendered）依赖最终尺寸。晚一步改就会出现"先按 100% 摆好，
        // 再放大成 150%"，结果窗口跑出屏幕。
        ApplyScale();

        SourceInitialized += (_, _) =>
        {
            _hwndReady = true;
            WindowGlass.HideFromAltTab(this);

            // 顺序不能反：先改 style 拿系统阴影，再上毛玻璃。反过来的话 accent 会被
            // 随后那次 SetWindowLong 弄失效（背景退回清晰壁纸）。
            EnableSystemShadow();
            ApplyGlass(_glass);
            SyncMenuChecks();
        };

        // Loaded 时 HwndTarget 才稳定，这里补一次透明底（在 SourceInitialized 里设会被覆盖）。
        // 少了这一句，窗口底会变成纯黑，Acrylic 看起来完全没生效。
        Loaded += (_, _) => WindowGlass.MakeClientTransparent(this);

        // 定位必须等首帧渲染完：SizeToContent 的窗口在 Loaded 阶段宽度还没定型，
        // 实测此时 GetWindowRect 量到的是 94px（最终 381 DIP × 1.5 = 571px）。
        // 拿这个宽度去算「60% 可见」的夹紧范围会算出完全错误的结果。
        ContentRendered += (_, _) =>
        {
            // 首帧渲染完再补一次玻璃。改过窗口 style 之后 accent 会丢掉（实测背景
            // 退回清晰壁纸），SourceInitialized 里那次设置不够稳，这里兜底。
            // 只跑一次、且只是重设同一组画刷，不会有可见闪烁。
            if (_glass) ApplyGlass(_glass);

            _contentRenderedOnce = true;

            if (PersistProbe)
            {
                // 走 setter，也就是用户点菜单走的同一条路
                Theme = GlassTheme.Dark;
                GlassLevel = 6;
                ScalePercent = 125;
                PlacementDiag = $"persist-probe: theme={Theme} level={GlassLevel} scale={ScalePercent}";
                Application.Current.Shutdown();
                return;
            }

            if (SnapProbe is { } probe)
            {
                // 自测优先：直接摆到指定位置再跑吸附 + 夹紧，不读配置也不写配置
                Monitors.MoveWindowTo(Handle, probe.X, probe.Y);
                SettlePlacement();
                PlacementDiag = DescribePlacement("snap-probe");
            }
            else if (_pendingAutoPlace is not null)
            {
                // 全屏自动切换（8.6）已经把窗口摆到全屏那块屏上了，这里**不能**再按配置还原：
                // 那会把窗口挪回"上次手动摆的位置"，而用户此刻正看着另一块屏上的游戏。
                // 实测症状：自动切过来的悬浮窗先出现在 (24,1453)，紧接着自己被挪到
                // (1989,0) —— 位置对了半秒然后跳走，比一直错更难排查。
                ApplyAutoPlace(_pendingAutoPlace);
            }
            else if (CenterOnScreen) PlaceAtCenter();
            else if (TryRestorePlacement())
            {
                // 还原后收紧一次：比例记的是左边界，而窗口换过缩放档 / 换过分辨率后
                // 右边界可能已经出界。这里的语义是"用户没要求出界"，所以用整窗可见而不是 60%。
                EnsureFullyVisible();
                SavePlacement();
                PlacementDiag = DescribePlacement("restored");
            }
            else
            {
                PlaceAtBottomLeft();
                PlacementDiag = DescribePlacement("default");
            }
        };
    }

    /// <summary>
    /// 全屏自动切换请求的目标显示器（8.6）。在 <see cref="ContentRendered"/> 之前落地，
    /// 那里再真正摆位 —— 因为 SizeToContent 的窗口在 Show 之后宽度还没定型，
    /// 此时量到的宽度是错的（见 ContentRendered 的注释），直接摆会算错底边。
    /// </summary>
    private string? _pendingAutoPlace;

    /// <summary>
    /// 请求把悬浮窗摆到指定显示器。由全屏自动切换调用（8.6）。
    ///
    /// <para>
    /// <b>会在 Show 之前就把窗口预摆到目标屏</b>（用 DIP 算），这样首帧就出现在正确位置。
    /// 少了这一步，实测会出现"窗口先闪现在屏幕左上角 (38,38)，600ms 后才跳到左下角" ——
    /// 全屏游戏上那一闪非常显眼，用户会以为程序抽风。
    /// </para>
    ///
    /// <para>
    /// 预摆位之后 <see cref="ContentRendered"/> 还会用物理像素再校一次
    /// （<see cref="ApplyAutoPlace"/>）：预摆位用的是 DIP，而 SizeToContent 的宽度那时还没定型，
    /// 底边对齐一定差几个像素。两步各管一半。
    /// </para>
    /// </summary>
    public void PlaceOnMonitor(string device)
    {
        if (string.IsNullOrEmpty(device)) return;

        // 第一步：Show 之前先按 DIP 预摆（此时没有 HWND，只能操作 Window.Left/Top）
        if (!_contentRenderedOnce)
        {
            PrePlaceBeforeShow(device);
            _pendingAutoPlace = device;
            return;
        }

        ApplyAutoPlace(device);
    }

    /// <summary>
    /// Show 之前的预摆位。用目标屏的 DPI 把"左下角 + 12 DIP 内缩"换算成 DIP 坐标。
    ///
    /// <para>
    /// 这段必须能容忍"还没有句柄/还没布局"：拿不到实际尺寸时用 <see cref="SizeToContent"/>
    /// 之前已知的 100% 基准高度估，误差由随后的物理像素校正吸收。
    /// </para>
    /// </summary>
    private void PrePlaceBeforeShow(string device)
    {
        if (!Monitors.TryFind(device, out MonitorArea work) || !work.IsValid) return;

        double scale = Monitors.ScaleForMonitor(device);
        if (scale <= 0) scale = 1.0;

        // 目标屏工作区的 DIP 坐标（工作区是物理像素，除缩放换 DIP）
        double workLeftDip = work.Left / scale;
        double workBottomDip = work.Bottom / scale;

        // 高度是确定的（尺寸由 ScalePercent 决定，不依赖内容测量）；宽度交给 SizeToContent，
        // 所以横向按"左边界 = 工作区左 + 12 DIP"锚定，不参与右边界对齐。
        double heightDip = BaseHeight * (_scalePercent / 100.0);
        Left = workLeftDip + 12;
        Top = workBottomDip - heightDip - 12;
    }

    /// <summary>是否已经过首帧渲染（决定 PlaceOnMonitor 是立刻摆还是延后）。</summary>
    private bool _contentRenderedOnce;

    /// <summary>把窗口摆到指定显示器的左下角（8.6 的副屏规则）。</summary>
    private void ApplyAutoPlace(string device)
    {
        _pendingAutoPlace = null;

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Monitors.TryFind(device, out MonitorArea work) || !work.IsValid) return;

        UpdateLayout();
        if (!Monitors.TryGetRect(hwnd, out Win32Window.RECT rect)) return;

        int widthPx = rect.Right - rect.Left;
        int heightPx = rect.Bottom - rect.Top;
        if (widthPx <= 0 || heightPx <= 0) return;

        int scale = (int)Math.Round(Monitors.ScaleForWindow(hwnd));
        if (scale <= 0) scale = 1;
        int inset = 12 * scale;

        // 左边界贴工作区左侧、底边贴工作区底部（全屏时那块屏没有任务栏，工作区≈整屏）
        Monitors.MoveWindowTo(hwnd, work.Left + inset, work.Bottom - heightPx - inset);
        PlacementDiag = DescribePlacement("auto-fullscreen:" + device);
    }

    /// <summary>验收毛玻璃时把窗口挪到屏幕中间，方便连同背景一起截（--float-center）。</summary>
    public bool CenterOnScreen { get; set; }

    /// <summary>
    /// 自测：<c>--float-snap-probe x,y</c>。启动后先把窗口硬摆到 (x,y)（物理像素），
    /// 再跑一次 <see cref="SnapToWorkAreaEdge"/>，把结果写进 <see cref="PlacementDiag"/>。
    ///
    /// <para>
    /// 为什么要这个探针：吸附逻辑一行 if 看着简单，但它是"物理像素坐标 + DPI 换算阈值 +
    /// 四个方向的正负号"，靠肉眼看截图根本分不清"没吸附"和"吸附了但我算错了"。探针把
    /// 窗口矩形与工作区矩形一起打出来，对不对一眼可读。
    /// </para>
    /// </summary>
    public (int X, int Y)? SnapProbe { get; set; }

    /// <summary>最近一次位置运算的诊断串，供截图工具写进 sidecar 文件。</summary>
    public string PlacementDiag { get; private set; } = string.Empty;

    /// <summary>
    /// 自测：<c>--float-persist-probe</c>。启动后把外观改成「深色 + 第 6 档」再退出，
    /// 用来验证"改设置 → 写 config.json"这条链路真的通。
    ///
    /// <para>
    /// 这条链路历史上出过事：<see cref="ConfigStore.Save"/> 原本手抄了一份字段白名单，
    /// 加新字段时漏同步就是"设置存不上"且没有任何报错。菜单点不了，就用探针走路。
    /// </para>
    /// </summary>
    public bool PersistProbe { get; set; }

    /// <summary>
    /// 外观变化是否写回配置。默认 true；用 <c>--glass-dark</c> / <c>--glass-level</c>
    /// 跑验收时由 App 关掉 —— 那些参数是临时改外观，不该把用户的配置改成"深色第 1 档"。
    /// </summary>
    public bool PersistToConfig { get; set; } = true;

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
            PersistAppearance();
        }
    }

    /// <summary>浓度档总数。档位表、菜单项、越界夹取都从这一处取，加档时改这里就够。</summary>
    private const int LevelCount = 7;

    /// <summary>浓度档 1–7（1 最透）。越界会被夹住，不抛异常——菜单 Tag 解析失败也不该炸窗口。</summary>
    public int GlassLevel
    {
        get => _level + 1;
        set
        {
            int index = Math.Clamp(value, 1, LevelCount) - 1;
            if (_level == index) return;
            _level = index;
            SyncMenuChecks();
            if (_hwndReady) ApplyGlass(_glass);
            PersistAppearance();
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

    // =================================================================
    // 缩放（Q-05）：100 / 125 / 150 三档
    // =================================================================

    /// <summary>100% 档的基准尺寸（需求 6.2 / 6.3 的数值）。所有档位都是它乘乘数。</summary>
    private const double BaseHeight = 34;
    private const double BaseValueFont = 13;
    private const double BaseValueLine = 16;
    private const double BaseLabelFont = 10;
    private const double BaseLabelLine = 13;
    private const double BaseGap = 8;

    private int _scalePercent = 100;

    /// <summary>
    /// 当前缩放档位（100 / 125 / 150）。越界会夹到最近的有效档，不抛异常 ——
    /// 配置被手改坏、或命令行参数乱写时，窗口照常打开比崩掉好。
    /// </summary>
    public int ScalePercent
    {
        get => _scalePercent;
        set
        {
            int v = NormalizeScale(value);
            if (_scalePercent == v) return;
            _scalePercent = v;
            ApplyScale();
            SyncMenuChecks();
            PersistAppearance();

            // 放大后窗口可能有一部分跑出工作区（原来贴右下角的条会顶到屏幕外）。
            // 位置按比例保存，比例本身没变，但当前这一帧得先夹回来；
            // 布局是延迟的，不 UpdateLayout 的话 GetWindowRect 拿到的还是旧尺寸。
            if (_hwndReady)
            {
                UpdateLayout();
                EnsureFullyVisible();
                SavePlacement();
            }
        }
    }

    /// <summary>把任意输入夹到三档之一。取"最接近的档"而不是直接拒绝：手改配置写成 120 时，
    /// 用户期望的是"差不多大"，不是"回到 100"。</summary>
    private static int NormalizeScale(int value)
    {
        int[] steps = { 100, 125, 150 };
        int best = steps[0];
        int bestDiff = int.MaxValue;
        foreach (int s in steps)
        {
            int diff = Math.Abs(s - value);
            if (diff < bestDiff) { bestDiff = diff; best = s; }
        }
        return best;
    }

    /// <summary>
    /// 把档位乘数刷进所有尺寸。这是"改 DIP 后重新布局"，不是缩放渲染结果（理由见 XAML 顶部注释）。
    /// </summary>
    private void ApplyScale()
    {
        double s = _scalePercent / 100.0;

        // 窗口高度与 Shell 高度一起改：只改一个的话 Border 会被 Window 拉伸，
        // TotalHeight 与 ShellHeight 白填，圆角上下沿会错位。
        Height = BaseHeight * s;
        ShellHeight = BaseHeight * s;

        ValueFontSize = BaseValueFont * s;
        ValueLineHeight = BaseValueLine * s;
        LabelFontSize = BaseLabelFont * s;
        LabelLineHeight = BaseLabelLine * s;
        CellGap = new Thickness(0, 0, BaseGap * s, 0);

        // 圆角、内边距、描边这几个必须走代码：CornerRadius / Thickness 类型绑起来要先写转换器，
        // 而它们只在切档时变一次，代码直接给更省事。
        Shell.CornerRadius = new CornerRadius(10 * s);
        Shell.Padding = new Thickness(10 * s, 0, 10 * s, 0);

        // 描边取整：0.8 DIP 的边框在布局取整后有时变 0、有时变 1，看上去像"描边时有时无"。
        Shell.BorderThickness = new Thickness(Math.Max(1, Math.Round(s)));

        foreach (FloatField f in AllFields) f.ApplyScale(s);
    }

    /// <summary>六个字段，按 6.3 的显示顺序。缓存一份，避免每次缩放都分配数组。</summary>
    private FloatField[] AllFields => new[]
    {
        _state.FloatFps, _state.FloatCpu, _state.FloatGpu,
        _state.FloatRam, _state.FloatVram, _state.FloatNet,
    };

    /// <summary>
    /// 缩放诊断串，供截图 sidecar 文件输出。缩放是"每列宽 + 字号 + 条高"一起变，
    /// 光看截图分不清"确实放大了"和"我只改了字号没改列宽"，把数字打出来最省事。
    /// </summary>
    public string ScaleDiag
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(_scalePercent).Append("% ");
            foreach (FloatField f in AllFields)
                sb.Append(f.Key).Append('=').Append(f.Width.ToString("F1")).Append(' ');
            sb.Append($"font={ValueFontSize:F1}/{LabelFontSize:F1} h={ShellHeight:F1}");

            // 布局实测：Style Setter 里的绑定是否真的落到元素上，只能看元素的运行值。
            // 光看 XAML 会误以为"写了就一定生效"——这里把第一列两个 TextBlock 的
            // 实际高度 / 行高 / 字号打出来，一眼能分清"没绑定"和"绑了但算错"。
            if (Shell.Child is StackPanel row && row.Children.Count > 0
                && row.Children[0] is StackPanel col)
            {
                sb.Append(" tb=");
                foreach (UIElement child in col.Children)
                {
                    if (child is TextBlock t)
                        sb.Append($"{t.ActualHeight:F1}/{t.LineHeight:F1}/{t.FontSize:F1} ");
                }
            }

            return sb.ToString();
        }
    }

    // 下面这组依赖属性只为给 XAML 绑定用。默认值全部取 100% 档，
    // 这样即使 ApplyScale 没跑到，窗口也是正常大小而不是 0。
    public static readonly DependencyProperty ShellHeightProperty =
        DependencyProperty.Register(nameof(ShellHeight), typeof(double), typeof(FloatWindow),
            new PropertyMetadata(BaseHeight));

    public static readonly DependencyProperty ValueFontSizeProperty =
        DependencyProperty.Register(nameof(ValueFontSize), typeof(double), typeof(FloatWindow),
            new PropertyMetadata(BaseValueFont));

    public static readonly DependencyProperty ValueLineHeightProperty =
        DependencyProperty.Register(nameof(ValueLineHeight), typeof(double), typeof(FloatWindow),
            new PropertyMetadata(BaseValueLine));

    public static readonly DependencyProperty LabelFontSizeProperty =
        DependencyProperty.Register(nameof(LabelFontSize), typeof(double), typeof(FloatWindow),
            new PropertyMetadata(BaseLabelFont));

    public static readonly DependencyProperty LabelLineHeightProperty =
        DependencyProperty.Register(nameof(LabelLineHeight), typeof(double), typeof(FloatWindow),
            new PropertyMetadata(BaseLabelLine));

    public static readonly DependencyProperty CellGapProperty =
        DependencyProperty.Register(nameof(CellGap), typeof(Thickness), typeof(FloatWindow),
            new PropertyMetadata(new Thickness(0, 0, BaseGap, 0)));

    public double ShellHeight
    {
        get => (double)GetValue(ShellHeightProperty);
        set => SetValue(ShellHeightProperty, value);
    }

    public double ValueFontSize
    {
        get => (double)GetValue(ValueFontSizeProperty);
        set => SetValue(ValueFontSizeProperty, value);
    }

    public double ValueLineHeight
    {
        get => (double)GetValue(ValueLineHeightProperty);
        set => SetValue(ValueLineHeightProperty, value);
    }

    public double LabelFontSize
    {
        get => (double)GetValue(LabelFontSizeProperty);
        set => SetValue(LabelFontSizeProperty, value);
    }

    public double LabelLineHeight
    {
        get => (double)GetValue(LabelLineHeightProperty);
        set => SetValue(LabelLineHeightProperty, value);
    }

    public Thickness CellGap
    {
        get => (Thickness)GetValue(CellGapProperty);
        set => SetValue(CellGapProperty, value);
    }

    /// <summary>
    /// 实际生效的毛玻璃方式，由 <see cref="WindowGlass.Apply"/> 返回。
    /// 自测/截图验收看这个字段就知道是系统背景生效了还是退到了 accent。
    /// </summary>
    public string GlassMode { get; private set; } = "未应用";

    // =================================================================
    // 投影（6.4）
    // =================================================================

    /// <summary>是否启用系统阴影。<c>--no-shadow</c> 可关掉，用于排查阴影/边框异常。</summary>
    public bool SystemShadow { get; set; } = true;

    private const int GWL_STYLE = -16;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WM_NCCALCSIZE = 0x0083;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);

    /// <summary>
    /// 让 DWM 给这条无边框窗口画系统阴影（6.4：blur 16 / offsetY 4 / alpha 20%）。
    ///
    /// <para>
    /// <b>为什么不用 WPF 的 DropShadowEffect</b>：本窗口 <c>AllowsTransparency=False</c>
    /// （毛玻璃与硬件加速的前提，A-30），WPF 内容裁在窗口矩形内。唯一的机会是"窗口比内容
    /// 大一圈，让阴影落在这圈空隙里"，但 accent 是<b>整窗</b>生效的 —— 实测那圈空隙被涂成
    /// 不透明灰板（窗口变成一个 429×82 的直角灰板嵌着 381×34 的条），比没有阴影更糟。
    /// 该路线已证伪，见 M2 报告 2.11。
    /// </para>
    ///
    /// <para>
    /// <b>DWM 只给"有非客户区框架"的窗口画阴影。</b>所以先加 <c>WS_THICKFRAME</c> 让 DWM
    /// 认为它是有边框窗口，再用 <c>WM_NCCALCSIZE</c> 返回 0 把客户区重新铺满 ——
    /// 8px 边框在视觉上被吃掉，阴影留下。实测条下方从 112 luma 渐升到 125（远处背景 159），
    /// 是自然渐隐、没有硬边。
    /// </para>
    ///
    /// <para>
    /// <b>顺序必须是"先改 style、再 ApplyGlass"</b>：改窗口 style 会让先前设好的 accent 失效
    /// （实测背景从模糊退回清晰锐利的壁纸）。所以调用点在 SourceInitialized 里排在 ApplyGlass
    /// 之前，ContentRendered 再补一次兜底。
    /// </para>
    /// </summary>
    private void EnableSystemShadow()
    {
        if (!SystemShadow) return;

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;

        HwndSource.FromHwnd(hwnd)?.AddHook(NcCalcSizeHook);

        IntPtr style = GetWindowLongPtr(hwnd, GWL_STYLE);
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)((long)style | WS_THICKFRAME));

        // 改完 style 必须通知框架已变，否则 DWM 仍按旧框架缓存渲染。
        Monitors.NotifyFrameChanged(hwnd);
    }

    private IntPtr NcCalcSizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // wParam=TRUE 表示"客户区要重新算"。返回 0 = 整个窗口都是客户区，
        // 于是 WS_THICKFRAME 带来的 8px 边框不可见，但 DWM 仍按"有边框窗口"给它画阴影。
        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero)
        {
            handled = true;
            return IntPtr.Zero;
        }
        return IntPtr.Zero;
    }

    /// <summary>双击或被菜单要求"回大窗口"。窗口之间不互相引用，由 App 接线。</summary>
    public event EventHandler? MainWindowRequested;

    /// <summary>
    /// 默认位置：主显示器工作区左下角，距左 12、距底 12（6.7 的兜底规则）。
    /// 只在"从未摆放"或"配置里的显示器已经不在了"时走到这里。
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
    // 位置持久化与吸附（6.5 / 6.7）
    // =================================================================

    private IntPtr Handle => new WindowInteropHelper(this).Handle;

    /// <summary>
    /// 按配置还原位置。全程物理像素（见 <see cref="Monitors"/> 的说明）。
    /// </summary>
    /// <returns>false = 没有记录 / 取不到显示器 / 尺寸还没算好，调用方走默认位置。</returns>
    private bool TryRestorePlacement()
    {
        var saved = new FloatPlacement(_config.FloatMonitor, _config.FloatRatioX, _config.FloatRatioY);
        if (!saved.IsKnown) return false;

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero) return false;
        if (!Monitors.TryGetRect(hwnd, out Win32Window.RECT rect)) return false;

        int widthPx = rect.Right - rect.Left;
        int heightPx = rect.Bottom - rect.Top;
        if (widthPx <= 0 || heightPx <= 0) return false;   // SizeToContent 还没算完

        Monitors.TryPrimary(out MonitorArea primary);
        if (!saved.TryResolve(Monitors.All(), primary, widthPx, heightPx,
                              out _, out int leftPx, out int topPx))
            return false;

        Monitors.MoveWindowTo(hwnd, leftPx, topPx);
        return true;
    }

    /// <summary>记录当前位置。拖动结束、锁定切换时调用；失败静默（下次启动回默认位置而已）。</summary>
    private void SavePlacement()
    {
        // 自测参数（--float-scale / --glass-dark 等）只是临时改外观，不该动用户的配置。
        // 位置和外观共用同一个开关：一次验收把配置里的位置也改了，下次启动会莫名跳一下。
        if (!PersistToConfig) return;

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Monitors.TryForWindow(hwnd, out MonitorArea work)) return;
        if (!Monitors.TryGetRect(hwnd, out Win32Window.RECT rect)) return;

        FloatPlacement placement = FloatPlacement.Capture(work.Device, rect.Left, rect.Top, work);
        if (!placement.IsKnown) return;

        _config.FloatMonitor = placement.Monitor;
        _config.FloatRatioX = placement.RatioX;
        _config.FloatRatioY = placement.RatioY;
        ConfigStore.SaveCurrent();
    }

    /// <summary>
    /// 松手时的贴边吸附距离（DIP）。需求 6.5 原文写 12，实测手感偏紧 ——
    /// 34 DIP 高的条要精确停到距边 12 以内才有反应，正常拖动很难命中，
    /// 用户反馈"基本吸不上"。放宽到 20（约窗口高度的 3/5），拖到边上松手就能挂住。
    ///
    /// <para>
    /// 必须按当前显示器缩放换算成物理像素：20 DIP 在 150% 屏上是 30 像素，
    /// 直接拿 20 去比会让高 DPI 用户觉得"怎么拖都吸不上"。
    /// </para>
    /// </summary>
    private const double SnapDip = 20;

    /// <summary>松手时贴边（6.5）：距工作区边缘在阈值内就吸上去，另一轴保持不动。</summary>
    private void SnapToWorkAreaEdge()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Monitors.TryForWindow(hwnd, out MonitorArea work)) return;
        if (!Monitors.TryGetRect(hwnd, out Win32Window.RECT rect)) return;

        int snap = (int)Math.Round(SnapDip * Monitors.ScaleForWindow(hwnd));

        int widthPx = rect.Right - rect.Left;
        int heightPx = rect.Bottom - rect.Top;

        int left = rect.Left;
        int top = rect.Top;

        if (Math.Abs(rect.Left - work.Left) <= snap) left = work.Left;                    // 贴左
        else if (Math.Abs(work.Right - rect.Right) <= snap) left = work.Right - widthPx;  // 贴右

        if (Math.Abs(rect.Top - work.Top) <= snap) top = work.Top;                        // 贴上
        else if (Math.Abs(work.Bottom - rect.Bottom) <= snap) top = work.Bottom - heightPx; // 贴下

        if (left == rect.Left && top == rect.Top) return;

        Monitors.MoveWindowTo(hwnd, left, top);
    }

    /// <summary>
    /// 松手后的位置整理：先贴边（6.5），再夹进"至少 60% 可见"的范围（6.5「拖到屏幕外」）。
    ///
    /// <para>
    /// 顺序不能反。先夹紧再吸附的话，被夹到工作区边缘的窗口会被吸附逻辑再挪一次，
    /// 贴边的手感反而变成"弹一下"。吸附只在阈值（<see cref="SnapDip"/>）以内生效，
    /// 夹紧只处理越界，先吸后夹是唯一不会互相打架的顺序。
    /// </para>
    /// </summary>
    private void SettlePlacement()
    {
        SnapToWorkAreaEdge();
        ClampInsideWorkArea();
    }

    /// <summary>把窗口夹回"至少 60% 落在工作区内"。拖出屏幕时松手会被拉回来。</summary>
    private void ClampInsideWorkArea()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Monitors.TryForWindow(hwnd, out MonitorArea work)) return;
        if (!Monitors.TryGetRect(hwnd, out Win32Window.RECT rect)) return;

        int left = rect.Left;
        int top = rect.Top;
        FloatPlacement.Clamp(work, rect.Right - rect.Left, rect.Bottom - rect.Top, ref left, ref top);

        if (left == rect.Left && top == rect.Top) return;
        Monitors.MoveWindowTo(hwnd, left, top);
    }

    /// <summary>
    /// 把窗口整个收进工作区（不是 60%，是全部可见）。
    ///
    /// <para>
    /// 和 <see cref="ClampInsideWorkArea"/> 的分工：那个是"用户把窗口拖出屏幕"时的宽松限制
    /// （6.5 只要求 60% 可见，尊重用户意图）；这个是"用户没有要求出界"时用的严格版 ——
    /// 启动还原位置、切缩放档位都属于这一类。两者混用会出现图片里那种情况：
    /// 位置记住了左边界（比例语义），窗口从 571 放大到 857 后右边界溢出 74px，
    /// 而 60% 规则认为"露了 91%"完全合格，于是留半截在屏幕外。
    /// </para>
    ///
    /// <para>
    /// 窗口比工作区还大时（极少见：150% 档 + 很小的屏），不能硬夹成负坐标，
    /// 那种情况交回 <see cref="ClampInsideWorkArea"/> 的 60% 规则处理。
    /// </para>
    /// </summary>
    private void EnsureFullyVisible()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Monitors.TryForWindow(hwnd, out MonitorArea work)) return;
        if (!Monitors.TryGetRect(hwnd, out Win32Window.RECT rect)) return;

        int widthPx = rect.Right - rect.Left;
        int heightPx = rect.Bottom - rect.Top;
        if (widthPx <= 0 || heightPx <= 0) return;

        if (widthPx > work.Width || heightPx > work.Height)
        {
            ClampInsideWorkArea();
            return;
        }

        int left = Math.Clamp(rect.Left, work.Left, work.Right - widthPx);
        int top = Math.Clamp(rect.Top, work.Top, work.Bottom - heightPx);

        if (left == rect.Left && top == rect.Top) return;
        Monitors.MoveWindowTo(hwnd, left, top);
    }

    /// <summary>把窗口矩形、工作区、缩放打成一串，供自测核对。</summary>
    private string DescribePlacement(string tag)
    {
        if (!Monitors.TryGetRect(Handle, out Win32Window.RECT r)) return $"{tag}: 取不到窗口矩形";

        Monitors.TryForWindow(Handle, out MonitorArea work);
        return $"{tag}: window=({r.Left},{r.Top})-({r.Right},{r.Bottom}) " +
               $"work=({work.Left},{work.Top})-({work.Right},{work.Bottom}) " +
               $"device={work.Device} scale={Monitors.ScaleForWindow(Handle):F2}";
    }

    // =================================================================
    // 玻璃：主题 × 浓度
    // =================================================================

    /// <summary>
    /// 毛玻璃开关（G-08 设置页的入口）。与右键菜单的「毛玻璃」同一条路径 ——
    /// 复用 <see cref="ApplyGlass"/>，不另写一份，否则两处对"关掉玻璃长什么样"迟早会不一致。
    /// </summary>
    public bool GlassEnabled
    {
        get => _glass;
        set
        {
            if (_glass == value) return;
            ApplyGlass(value);
            PersistAppearance();
            SyncMenuChecks();
        }
    }

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
            _edgeIdle = _edgeNormal;   // 有实底了，描边回到常态那一档
            return;
        }

        (double accent, double shell, bool blurOnly) = (light ? LightSteps : DarkSteps)[_level];

        // 深浅只差 dark 标志与 tint 的 RGB，accent 那条路是同一段代码（A-36）。
        // blurOnly 只对「极透」档为真：那一档放弃模糊换 2% 的遮盖（A-37）。
        GlassMode = WindowGlass.Apply(this, WindowGlass.Backdrop.Acrylic, dark: !light,
            tintOpacity: accent, blurOnly: blurOnly);

        // Shell 遮罩：浅色铺白、深色铺 Palette 背景色，alpha 由档位给
        Shell.Background = Tint(
            light ? Color.FromRgb(0xF6, 0xF8, 0xFB) : Color.FromRgb(0x0F, 0x12, 0x16),
            shell);

        // 极透档几乎没有底色，淡描边会完全看不见边界，窗口像"文字直接浮在桌面上"。
        // 这一档借用悬停档的描边作为常驻值，用户至少能找到窗口的四条边。
        _edgeIdle = blurOnly ? _edgeHover : _edgeNormal;
        Shell.BorderBrush = _edgeIdle;
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

    /// <summary>
    /// 鼠标不在窗口上时实际用的描边。平时等于 <see cref="_edgeNormal"/>；
    /// 「极透」档没有底色打底，1px 淡描边根本看不见边界，这时换成 <see cref="_edgeHover"/> 那一档。
    /// </summary>
    private Brush _edgeIdle = Palette.Brush("BrushGlassEdge");

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
            // 双击切大窗口（Q-05）。放在锁定判断之前：锁定只锁位置，不锁"切回大窗口"。
            MainWindowRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        if (_locked) return;   // 6.5：锁定后拖动无效

        // 拖动期间停掉数据更新（6.5）。DragMove 会把 UI 线程交给模态循环，
        // 不闸住数据入口的话数字仍每 0.5s 重排一次，拖起来有顿感。
        // Paused 在共享 State 上，两个视图同时静默。
        _state.Paused = true;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标在 DragMove 之前就松开了（双击的第二下、或者远程会话里丢事件）——
            // 这不是错误，吞掉即可，否则会冒到 Dispatcher 上写一份无意义的崩溃日志。
        }
        finally
        {
            _state.Paused = false;
        }

        // 吸附、夹紧与落盘放在松手之后：拖动途中改位置会和 DragMove 抢，窗口会抖
        SettlePlacement();
        SavePlacement();
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

    private void Shell_MouseLeave(object sender, MouseEventArgs e) => Shell.BorderBrush = _edgeIdle;

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
    {
        ApplyGlass(GlassMenu.IsChecked);
        PersistAppearance();
    }

    private void Scale_Click(object sender, RoutedEventArgs e)
    {
        // Tag 里是百分比整数；解析不出来就当作没点（菜单结构被改坏也不该炸窗口）
        if (sender is MenuItem { Tag: string tag } && int.TryParse(tag, out int percent))
            ScalePercent = percent;
    }

    private void ToggleLock_Click(object sender, RoutedEventArgs e)
    {
        // IsCheckable 的菜单项点下去时 WPF 已经先把 IsChecked 翻过了，这里读到的就是新状态
        _locked = LockMenu.IsChecked;
        _config.FloatLocked = _locked;
        ConfigStore.SaveCurrent();
    }

    /// <summary>
    /// 把当前外观写回配置。改一次存一次，而不是等退出 ——
    /// 悬浮窗被任务管理器结束、或程序崩掉时，用户刚调好的外观不该丢。
    /// </summary>
    private void PersistAppearance()
    {
        if (!PersistToConfig) return;

        _config.FloatTheme = _theme.ToString();
        _config.FloatGlassLevel = _level + 1;
        _config.FloatGlassEnabled = _glass;
        _config.FloatScale = _scalePercent;
        ConfigStore.SaveCurrent();
    }

    /// <summary>
    /// 菜单勾选状态跟实际状态对齐。用 IsChecked 直接互斥即可——菜单项是 IsCheckable 的，
    /// 点哪项 WPF 会自己先翻掉 IsChecked，所以这里只负责把"另一侧"复位。
    /// </summary>
    private void SyncMenuChecks()
    {
        if (ThemeLightMenu is null) return;   // 构造函数里 SourceInitialized 之前可能还没建好

        ThemeLightMenu.IsChecked = _theme == GlassTheme.Light;
        ThemeDarkMenu.IsChecked = _theme == GlassTheme.Dark;

        // 这两项也要跟配置对齐：从 config.json 读回"深色 + 锁定"时，
        // 菜单若还显示默认的勾选状态，用户会以为设置没生效。
        if (LockMenu is not null) LockMenu.IsChecked = _locked;
        if (GlassMenu is not null) GlassMenu.IsChecked = _glass;

        // 档位用数组走循环：加档时只要 XAML 补一项、LevelCount 加一，这里不用动
        MenuItem?[] levels =
        {
            Level1Menu, Level2Menu, Level3Menu, Level4Menu,
            Level5Menu, Level6Menu, Level7Menu,
        };
        for (int i = 0; i < levels.Length; i++)
            if (levels[i] is { } item) item.IsChecked = _level == i;

        // 缩放三档同理：值写在 Tag 里，勾选状态由当前档位反推，不另存一份真值
        if (Scale100Menu is not null)
        {
            Scale100Menu.IsChecked = _scalePercent == 100;
            Scale125Menu.IsChecked = _scalePercent == 125;
            Scale150Menu.IsChecked = _scalePercent == 150;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
