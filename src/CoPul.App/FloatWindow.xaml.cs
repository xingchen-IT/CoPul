using System.Text;
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

        // 字段子菜单按 FloatFields.Keys 生成（M4.14）：顺序与表一致，加字段不用改 XAML。
        BuildFieldMenu();

        // 外观从配置读回。必须先于 InitializeComponent 之后、Show 之前落到字段上，
        // 否则窗口会先按默认值画一遍再被改掉（闪一下浅色再变深色）。
        if (Enum.TryParse(config.FloatTheme, ignoreCase: true, out GlassTheme theme))
            _theme = theme;
        _level = Math.Clamp(config.FloatGlassLevel, 1, LevelCount) - 1;
        _glass = config.FloatGlassEnabled;
        _locked = config.FloatLocked;
        _lockedBeforeThrough = config.FloatLocked;
        _clickThrough = config.FloatClickThrough;
        _alwaysOnTop = config.FloatAlwaysOnTop;
        _scalePercent = NormalizeScale(config.FloatScale);
        _vertical = config.FloatVertical;
        _edgeMode = NormalizeEdgeMode(config.FloatEdgeMode);

        // 逐字段文字色（M4.20）。只把配置里出现的键抄进内存表，缺的仍是「自动」。
        foreach (string key in FloatFields.Keys)
            FloatInk.Set(_fieldColors, key, FloatInk.Get(config.FloatFieldColors, key));
        _textKeepThreshold = config.TextKeepThreshold;

        // 缩放必须在 Show 之前落定：窗口尺寸会决定 SizeToContent 算出的宽度，
        // 而定位（ContentRendered）依赖最终尺寸。晚一步改就会出现"先按 100% 摆好，
        // 再放大成 150%"，结果窗口跑出屏幕。
        ApplyScale();
        ApplyFieldVisibility();

        SourceInitialized += (_, _) =>
        {
            _hwndReady = true;
            WindowGlass.HideFromAltTab(this);

            // 顺序不能反：先改 style 拿系统阴影，再上毛玻璃。反过来的话 accent 会被
            // 随后那次 SetWindowLong 弄失效（背景退回清晰壁纸）。
            // 消息钩子只挂一次，且**不挂在 EnableSystemShadow 里**：
            // 那条路在 --no-shadow 时会整个跳过，而穿透靠的 WM_NCHITTEST 也走这个钩子 ——
            // 挂在里面等于"关掉阴影就没有穿透"，两者毫无关系。
            HwndSource.FromHwnd(Handle)?.AddHook(NcCalcSizeHook);

            // XAML 里写死的是 Topmost="True"，而配置可能存着 false —— 不同步的话
            // "设置里关了置顶"会在下次启动悄悄失效（窗口照样在 topmost 组里）。
            Topmost = _alwaysOnTop;

            EnableSystemShadow();
            ApplyGlass(_glass);
            SyncMenuChecks();
        };

        // Loaded 时 HwndTarget 才稳定，这里补一次透明底（在 SourceInitialized 里设会被覆盖）。
        // 少了这一句，窗口底会变成纯黑，Acrylic 看起来完全没生效。
        Loaded += (_, _) => WindowGlass.MakeClientTransparent(this);

        // 框架守门（M4.16）：DWM 偶尔会把 WS_THICKFRAME 那 8px 重新算回客户区几何，
        // 表现为窗口四周多一圈（浅色主题下就是白框）。四条时机都看一眼，
        // 发现不一致就补一次"框架已变"，让它重新问一次 WM_NCCALCSIZE。
        LocationChanged += (_, _) => GuardFrame();
        SizeChanged += (_, _) => GuardFrame();
        // 激活状态变化一律 force：WM_NCACTIVATE 会把非客户区画回来，而这条路径
        // **不改客户区几何**（实测 gap 恒为 0），光靠 gap 判断永远发现不了，必须无条件补一次。
        Activated += (_, _) => GuardFrame(force: true);
        Deactivated += (_, _) => GuardFrame(force: true);
        DpiChanged += (_, _) => GuardFrame();

        // 兜底巡检：上面五个时机之外的漏网的（系统 DPI 变了而窗口尺寸恰好没变之类）。
        // 一秒一次、只比两次矩形，窗口看不见时连这两次都省了。
        //
        // 置顶巡检（M4.21）顺同一趟走：它也是"每秒看一眼、没变化就什么也不做"，
        // 单独再起一个定时器只是多一次唤醒。
        var frameWatch = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        frameWatch.Tick += (_, _) =>
        {
            if (!IsVisible) return;
            GuardFrame();
            GuardTopmost("poll");
        };
        frameWatch.Start();

        // Z 序事件驱动提顶（M4.30）。
        //
        // 一秒一次的巡检对"被别的置顶窗口压住"是不够的：真机打 MC 的 F11 抓到，
        // GLFW 进全屏时会给自己的窗口加 WS_EX_TOPMOST，两个窗口同处 topmost 组，
        // 谁最后 SetWindowPos 谁在上 → 悬浮窗每次都要空窗 0.5~1 秒才被拉回来。
        // 挂两个 WinEvent（前台变化 + Z 序重排）把这段空窗压到几十毫秒内。
        //
        // 判断仍然交给 GuardTopmost：钩子只负责"可能有变化"，不知道策略，
        // 也不该在回调里做 80 次 GetWindow 的链遍历（reorder 一秒能来几百条）。
        _zWatch = new ZOrderWatch();
        _zWatch.Changed += () =>
        {
            if (_alwaysOnTop && IsVisible) GuardTopmost("event");
        };
        Closed += (_, _) =>
        {
            _zWatch?.Dispose();
            _zWatch = null;
        };

        // 定位必须等首帧渲染完：SizeToContent 的窗口在 Loaded 阶段宽度还没定型，
        // 实测此时 GetWindowRect 量到的是 94px（最终 381 DIP × 1.5 = 571px）。
        // 拿这个宽度去算「60% 可见」的夹紧范围会算出完全错误的结果。
        //
        // 竖向还有第二次尺寸变化：首帧时六格的数据都还是「—」（28 DIP 宽），
        // 采集到第一个快照后 `18.5/31.6G` 这类长串一进来，窗口才撑到 61 DIP。
        // 如果它正贴着右边缘，这 33 DIP 就会长到屏幕外 —— 尺寸变一次再夹一次。
        // 没有缩放边框（ResizeMode=NoResize），尺寸只会因布局/数据变，不会和用户抢。
        SizeChanged += (_, _) =>
        {
            if (!_contentRenderedOnce) return;
            EnsureFullyVisible();
        };

        // 每次被切到前台都强制重排一次（M4.14）。
        //
        // 症状：从大窗口切到竖向悬浮窗，第一次进去宽度不对，改一下任意设置又恢复正常。
        // 成因是"尺寸在不可见期间被改过"：排列、缩放、字段都是隐藏时也能改的，
        // SizeToContent 的窗口在不可见时只更新了布局缓存，HWND 尺寸还是旧的；
        // 再次 Show 时 WPF 认为"没变化"就沿用了旧尺寸。改设置会再走一次 ApplyScale，
        // 于是看起来"改一下就好了"。
        //
        // 这里不重读配置、只重排 —— 用户在本次会话里选过的东西不能被配置反向覆盖。
        IsVisibleChanged += (_, e) =>
        {
            if (e.NewValue is not true) return;
            if (RelayoutOnShow) RelayoutNow();
        };

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
                IsVertical = true;
                PlacementDiag = $"persist-probe: theme={Theme} level={GlassLevel} " +
                                $"scale={ScalePercent} vertical={IsVertical}";
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

            // 自测放在最后：走到这里尺寸已定型，命中测试量到的才是真实布局
            if (DragProbe is { } dragMode) RunDragProbe(dragMode);
            if (MoveFocusProbe is { } mfSpec) RunMoveFocusProbe(mfSpec);
            if (FieldProbe is { } fieldKeys) RunFieldProbe(fieldKeys);
            if (MenuProbe) ProbeMenuAndHitTest();
            if (HitTestProbe) ProbeHitTest();
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

        // 横向的高度是定值（34 × 档位），竖向的高度要由六行内容决定 —— Show 之前量不到，
        // 用 EstimateVerticalLength 估。两者都是"底边对齐、左边界锚定"，不需要两套规则。
        double heightDip = _vertical ? EstimateVerticalLength() : BaseHeight * (_scalePercent / 100.0);
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
    /// 自测：<c>--float-menu-probe</c>。首帧渲染后直接弹右键菜单，配合整屏抓屏验收
    /// "菜单弹在哪"。存在的意义是竖向 —— 竖向窗口只有一百来 DIP 宽、一百多高，
    /// 菜单落点由元素位置算，只靠看代码判断不了，必须实拍。
    /// </summary>
    public bool MenuProbe { get; set; }

    /// <summary>
    /// 自测：<c>--float-move-focus-probe move,defocus,drag</c>（M4.16）。
    /// 复现"挪过位置后点别处冒白框"，见 <see cref="RunMoveFocusProbe"/>。
    /// </summary>
    public string? MoveFocusProbe { get; set; }

    /// <summary>
    /// 自测：<c>--float-drag-probe keep|release</c>（M4.14）。
    ///
    /// <para>
    /// 拖动时的粉色描边没法靠"真的按住窗口拖"来截图：<c>DragMove</c> 是模态循环，
    /// 定时器根本不进，抓不到那一帧。这里直接把窗口切进"拖动外观"，
    /// 配合 <c>--shot</c>（RTB 拍的是视觉树，颜色是准的）看颜色；
    /// <c>keep</c> 留在粉色上供拍照，<c>release</c> 立刻撤掉 —— 两条路的描边色都写进 sidecar。
    /// </para>
    /// </summary>
    public string? DragProbe { get; set; }

    /// <summary>
    /// 自测：<c>--float-field-probe ram,vram</c>（M4.14）。首帧后按逗号逐个翻字段开关，
    /// 用来验"关掉一格之后条真的变窄/变矮"，以及 sidecar 里各格的高度。
    /// </summary>
    public string? FieldProbe { get; set; }

    /// <summary>
    /// 自测：<c>--no-relayout-on-show</c> 关掉它（M4.14）。
    ///
    /// <para>
    /// 只用来做 A/B：同一份代码跑两遍，一遍开着一遍关着，
    /// 才能证明「显示时强制重排」这一步确实修了"首进竖向宽度不对"，
    /// 而不是"这个场景下本来就不会犯"。
    /// </para>
    /// </summary>
    public bool RelayoutOnShow { get; set; } = true;

    // =================================================================
    // 鼠标穿透 / 始终置顶（M4.21）
    // =================================================================

    /// <summary>位置锁定（6.5）。锁定后拖动无效，但鼠标消息照收（右键菜单仍可用）。</summary>
    public bool Locked
    {
        get => _locked;
        set
        {
            if (_locked == value) return;
            _locked = value;
            SyncMenuChecks();
            PersistAppearance();
        }
    }

    /// <summary>
    /// 鼠标穿透（M4.21）：命中测试一律返回 <c>HTTRANSPARENT</c>，
    /// 点悬浮窗等于点它下面的东西。
    ///
    /// <para>
    /// 与「锁定位置」是两件事，但穿透<b>必然连带拖不动</b>：鼠标消息根本不投递，
    /// <c>DragMove</c> 没有触发源。所以打开穿透时记下当前锁定状态并强制锁上、
    /// 同时把「锁定位置」项禁掉 —— 否则用户会解锁，然后发现"解锁了却还是拖不动"。
    /// 关掉穿透再把锁定还原成打开之前的样子。
    /// </para>
    /// </summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (_clickThrough == value) return;
            _clickThrough = value;

            if (_clickThrough)
            {
                _lockedBeforeThrough = _locked;
                _locked = true;
            }
            else
            {
                _locked = _lockedBeforeThrough;
            }

            SyncMenuChecks();
            PersistAppearance();
        }
    }

    /// <summary>
    /// 始终置顶（M4.21）：每秒把悬浮窗提回 topmost 组最顶。
    ///
    /// <para>
    /// XAML 里的 <c>Topmost="True"</c> 只保证"进 topmost 组"，不保证"排在组里最前"——
    /// 另一个置顶窗口（游戏浮层、录屏工具条）一激活就压上来，而系统不会通知我们。
    /// 所以只能巡检，见 <see cref="GuardTopmost"/>。
    /// </para>
    /// </summary>
    public bool AlwaysOnTop
    {
        get => _alwaysOnTop;
        set
        {
            if (_alwaysOnTop == value) return;
            _alwaysOnTop = value;
            Topmost = value;                 // 关掉时是真的退出 topmost 组，不是"不再巡检"
            SyncMenuChecks();
            PersistAppearance();
        }
    }

    private bool _clickThrough;
    private bool _alwaysOnTop = true;

    /// <summary>打开穿透之前的锁定状态，关掉穿透时还原（见 <see cref="ClickThrough"/>）。</summary>
    private bool _lockedBeforeThrough;

    /// <summary>
    /// 自测诊断串。穿透是"消息级别的"，配置写了不等于消息真的被拦掉了 ——
    /// 所以计数用**真实命中次数**：鼠标每动一次都会来一条 <c>WM_NCHITTEST</c>。
    /// </summary>
    public string InteractDiag =>
        $"interact: lock={_locked} through={_clickThrough} hitTestHits={_hitTestHits} " +
        $"ontop={_alwaysOnTop}(exStyle={_exTopmost}) raiseHits={_topmostHits} reason={_lastReason} " +
        $"zhooks={(_zWatch?.HookCount ?? 0)} above=0x{_lastAbove.ToInt64():X}" +
        $" aboveOwn={_aboveOwn} fgOwn={_fgOwn} aboveNow=[{Monitors.Describe(_lastAbove)}]" +
        (_topmostHits == 0
            ? string.Empty
            : $" raiseOk={_raiseOk} postRaise=0x{_lastAboveAfter.ToInt64():X} [{_aboveAfterDesc}]") +
        (_zDiag.Length == 0 ? string.Empty : Environment.NewLine + "zorder: " + _zDiag) +
        (_hitTestResult == int.MinValue
            ? string.Empty
            : $" probeHT={DescribeHit(_hitTestResult)}");

    /// <summary>被穿透的命中测试次数（<c>WM_NCHITTEST</c> 拦下多少条）。</summary>
    private int _hitTestHits;

    /// <summary>巡检把窗口提回顶层的次数。</summary>
    private int _topmostHits;

    /// <summary>最近一次提顶是被什么触发的（<c>poll</c> / <c>event</c>，诊断用，M4.30）。</summary>
    private string _lastReason = "(无)";

    /// <summary>提顶 trace 的限流时间戳（M4.30）。reorder 事件很密，不限流会刷满日志。</summary>
    private long _lastTopLogMs;

    /// <summary>Z 序事件源（M4.30）。为 null 表示还没挂上或已释放。</summary>
    private ZOrderWatch? _zWatch;

    /// <summary>最近一次巡检时压在上面的那个窗口（诊断用）。</summary>
    private IntPtr _lastAbove;

    /// <summary>窗口是否真的带 <c>WS_EX_TOPMOST</c>（诊断用）。</summary>
    private bool _exTopmost;

    /// <summary>上面那个窗口 / 前台窗口是不是自家窗口（诊断用，M4.21）。</summary>
    private bool _aboveOwn, _fgOwn;

    /// <summary>提顶**之后**压在上面的窗口。用于证明提顶真的生效（M4.21）。</summary>
    private IntPtr _lastAboveAfter;

    /// <summary>
    /// 自测：把"我"上面那一段 Z 序打印出来（M4.21）。
    ///
    /// <para>
    /// 判据是三层过滤（可见 / 有面积 / 矩形相交），任一层写错表现都一样 ——
    /// "永远没被挡"和"永远被挡"都只体现在 <c>raiseHits</c> 上，看不出是哪一层错了。
    /// 只能把链打出来：窗口在不在链上、坐标对不对、是不是被 UIPI 挡着，一眼就能分辨。
    /// </para>
    /// </summary>
    public bool ZOrderProbe { get; set; }

    /// <summary>
    /// 自测：把自家可见窗口也算"遮挡"，用来验"检测 → 提顶 → 回到最顶"整条链（M4.21）。
    /// 生产永远为 false —— 不然每秒都会把设置页顶到下面去。
    /// </summary>
    public bool TopmostIncludeOwn { get; set; }

    /// <summary>Z 序诊断文本（<see cref="ZOrderProbe"/> 打开时才写）。</summary>
    private string _zDiag = "";

    /// <summary>提顶调用的返回值，以及提完仍压在上面的那个窗口是谁（诊断用）。</summary>
    private bool _raiseOk = true;
    private string _aboveAfterDesc = "";

    /// <summary>自测窗口正中心命中测试的返回值。<c>int.MinValue</c> = 没探测过。</summary>
    private int _hitTestResult = int.MinValue;

    /// <summary>
    /// 自测：给自己发一条 <c>WM_NCHITTEST</c>，结论写进 <see cref="InteractDiag"/>。
    ///
    /// <para>
    /// 穿透改的是**命中测试的结论**，而这一层没有别的观测手段：
    /// <c>WindowFromPoint</c> 不做命中测试（M4.1 踩过，它照样返回我们的 HWND），
    /// 截图更是看不出任何差别（穿透不碰渲染，一个像素都不变）。
    /// 只剩"发消息问窗口本人"这一条路 —— 而它正好就是真实鼠标走的那条路。
    /// </para>
    /// </summary>
    public bool HitTestProbe { get; set; }

    private void ProbeHitTest()
    {
        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Monitors.TryGetRect(hwnd, out Win32Window.RECT r)) return;

        int x = (r.Left + r.Right) / 2, y = (r.Top + r.Bottom) / 2;
        IntPtr lParam = (IntPtr)(((y & 0xFFFF) << 16) | (x & 0xFFFF));
        _hitTestResult = SendMessage(hwnd, WM_NCHITTEST, IntPtr.Zero, lParam).ToInt32();
    }

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "SendMessageW")]
    private static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);

    /// <summary>把命中测试的返回值翻成人能读的词。只列本项目会遇到的几个。</summary>
    private static string DescribeHit(int hit) => hit switch
    {
        HTTRANSPARENT => "HTTRANSPARENT(-1 穿透中)",
        0 => "HTNOWHERE(0)",
        1 => "HTCLIENT(1 正常接收)",
        2 => "HTCAPTION(2 可拖动)",
        _ => hit.ToString(),
    };

    /// <summary>
    /// 把悬浮窗提回 topmost 组最顶（M4.21 起，M4.30 改成事件驱动 + 三条提顶理由）。
    ///
    /// <para>
    /// 为什么必须巡检：没有任何事件能告诉我们"Z 序变了"。别的置顶窗口盖上来时，
    /// 我们收不到消息，等到用户发现"悬浮窗不见了"往往已经过了很久。
    /// 提顶用的 <c>SetWindowPos</c> 带 <c>NOMOVE|NOSIZE|NOACTIVATE</c>，
    /// 不改几何也不抢焦点，所以每秒一次没有可见副作用。
    /// </para>
    ///
    /// <para>
    /// <b>M4.30 的两处修正</b>（真机打 MC F11 抓到的现场，<c>tools/f11-transient.py</c>）：
    /// <list type="number">
    /// <item>提顶不再只由"检测到遮挡"触发 —— 每秒只靠 <c>GW_HWNDPREV</c> 判一次，
    ///   碰上游戏自己提顶（GLFW 全屏会给窗口加 <c>WS_EX_TOPMOST</c>）就要空窗近乎一秒。
    ///   现在改为 Z 序事件一到就进这里，reason 会写成 <c>event</c>。</item>
    /// <item>去掉"本进程有窗口在前台就不抢"的早退。那条策略的原意是别压住设置页，
    ///   但设置页压着悬浮窗时 <paramref name="reason"/> 那条路上的 <c>_aboveOwn</c> 已经挡住了；
    ///   而"前台是自家的、上面压着的却是外部窗口（游戏 / 开始菜单）"这个组合下，
    ///   早退会让悬浮窗**永远**出不来 —— 正是用户报的"不能一直在最上层"。</item>
    /// </list>
    /// </para>
    /// </summary>
    /// <param name="reason">触发来源：<c>poll</c>（1Hz 兜底）/ <c>event</c>（Z 序事件）。</param>
    private void GuardTopmost(string reason)
    {
        if (!_hwndReady) return;

        IntPtr hwnd0 = Handle;
        if (hwnd0 != IntPtr.Zero)
            _exTopmost = (GetWindowLongPtr(hwnd0, GWL_EXSTYLE).ToInt64() & WS_EX_TOPMOST) != 0;
        bool lostTopmost = !_exTopmost;

        if (!_alwaysOnTop || !IsVisible) return;

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;

        // 只看**可见**的上方窗口：IME 窗口、托盘消息窗这类隐藏窗口永远排在 Z 序最前，
        // 但它们不占屏幕 —— 拿它们当"被压住了"会变成每秒一次提不动的无效功。
        if (ZOrderProbe) _zDiag = Monitors.DescribeChain(hwnd, 8);

        IntPtr above = Monitors.FirstBlockingAbove(hwnd);
        _lastAbove = above;
        _aboveOwn = Monitors.IsOwnVisibleWindow(above);

        // 提顶的三条理由：被外部窗口压住 / WS_EX_TOPMOST 掉了 / 什么都没有就不动。
        // 第二条单独列出来是因为它会被第一条漏掉：style 掉了但恰好没有窗口与我重叠时，
        // "没被挡住"是真的，"不在 topmost 组里"也是真的 —— 后者才是根因。
        if (above == IntPtr.Zero && !lostTopmost) return;

        IntPtr foreground = Win32Window.ForegroundWindow;
        _fgOwn = foreground != IntPtr.Zero && foreground != hwnd && Monitors.IsOwnWindow(foreground);
        // 自家可见窗口（设置页 / 大窗口）压着我：让路是策略，不是能力。
        // 自测要验"检测到就顶得动"，本机造不出能压住提权进程的外部窗口（UIPI），
        // 所以 --float-ztop-own 允许拿自家窗口当遮挡物。
        if (_aboveOwn && !TopmostIncludeOwn) return;

        _raiseOk = Monitors.RaiseToTopmost(hwnd);
        _topmostHits++;
        _lastReason = reason;

        // 提完立刻再问一次：这是"提顶到底有没有生效"的唯一证据 ——
        // 事前的 above 只能证明"被压了"，提完的 above 才证明"真的回到了最顶"。
        _lastAboveAfter = Monitors.FirstBlockingAbove(hwnd);
        _aboveAfterDesc = Monitors.Describe(_lastAboveAfter);
        LogTopmost(above, lostTopmost);
    }

    /// <summary>
    /// 提顶落一条 trace（限流 1 秒）。
    ///
    /// <para>
    /// 这条日志是 M4.30 的取证入口：下次再"看不见悬浮窗"，直接看
    /// <c>%TEMP%\copul-trace.log</c> 里的 <c>[topmost]</c> 行就能分辨
    /// —— 有 raise 且 after 为空 = 我们赢了；没有 raise = 根本没被挡住，
    /// 那就是**渲染**问题（DWM 换了合成层），跟 Z 序无关。
    /// </para>
    /// </summary>
    private void LogTopmost(IntPtr above, bool lostTopmost)
    {
        long now = Environment.TickCount64;
        if (now - _lastTopLogMs < 1000) return;
        _lastTopLogMs = now;

        Trace.Log("topmost",
            $"raise reason={_lastReason} hits={_topmostHits} exTopmost={!lostTopmost} " +
            $"blocker=[{Monitors.Describe(above)}] ok={_raiseOk} " +
            $"after=0x{_lastAboveAfter.ToInt64():X} [{_aboveAfterDesc}]");
    }

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
    // 文字颜色（M4.19 整条一个色 → M4.20 逐字段）
    // =================================================================

    /// <summary>
    /// 悬浮窗九个字段各自的文字色。key 见 <see cref="FloatFields.Keys"/>，
    /// 值是 <c>#RRGGBB</c>，空串 = 「自动」（跟随玻璃主题）。
    ///
    /// <para>
    /// 一个字段一个色，而不是整条一个色：用户要的是"温度那一格单独标成橙色"，
    /// 整条一个色做不到。主值用所选色、次值用同色降浓度（78%）——
    /// 原来两段的深浅关系（<c>#1B2027</c> / <c>#5C6672</c>）不能被抹平，
    /// 全同浓度会让 34px 高的条看起来糊成一片。
    /// </para>
    ///
    /// <para>
    /// 只影响悬浮窗：任务栏条读的是 <see cref="Sdk.FloatField.TopBrushAlt"/> 那一套，
    /// 与这里互不相干（M4.20 起任务栏有自己的一张表）。
    /// </para>
    /// </summary>
    public IReadOnlyDictionary<string, string> FieldColors => _fieldColors;

    /// <summary>某个字段当前的文字色写法（没单独设就是「自动」）。</summary>
    public string FieldColor(string key) => FloatInk.Get(_fieldColors, key);

    /// <summary>
    /// 改一个字段的文字色。设置页点色板 / 填十六进制 / 自测探针都走这里 ——
    /// 只有一个入口，"改完要不要重刷、要不要落盘"才不会漏。
    /// </summary>
    public void SetFieldColor(string key, string hex)
    {
        if (!FloatInk.IsFieldKey(key)) return;

        string normalized = FloatInk.Normalize(hex);
        if (FloatInk.Get(_fieldColors, key) == normalized) return;

        FloatInk.Set(_fieldColors, key, normalized);
        ApplyTextTheme(_theme == GlassTheme.Light);
        PersistAppearance();
    }

    /// <summary>九个字段一起设成同一个色（A/B 与"全部重置"用）。</summary>
    public void SetAllFieldColors(string hex)
    {
        string normalized = FloatInk.Normalize(hex);

        bool changed = false;
        foreach (string key in FloatFields.Keys)
        {
            if (FloatInk.Get(_fieldColors, key) == normalized) continue;
            FloatInk.Set(_fieldColors, key, normalized);
            changed = true;
        }
        if (!changed) return;

        ApplyTextTheme(_theme == GlassTheme.Light);
        PersistAppearance();
    }

    /// <summary>
    /// 自定义文字色下，数值超阈值是否仍然变黄 / 变红。默认开 ——
    /// 两个视图共用同一个开关，见 <see cref="AppConfig.TextKeepThreshold"/>。
    /// </summary>
    public bool TextKeepThreshold
    {
        get => _textKeepThreshold;
        set
        {
            if (_textKeepThreshold == value) return;
            _textKeepThreshold = value;
            ApplyTextTheme(_theme == GlassTheme.Light);
            PersistAppearance();
        }
    }

    /// <summary>逐字段的文字色。只存被改过的，「自动」的不留占位。</summary>
    private readonly Dictionary<string, string> _fieldColors = FloatInk.NewMap();

    private bool _textKeepThreshold = true;

    /// <summary>
    /// 自测 sidecar 用的文字色诊断串。
    ///
    /// <para>
    /// 判据必须是**运行值**而不是配置项：配置写了 <c>#FFFFFF</c> 而画刷没换上时，
    /// 两处看起来都对，只有"格子上实际是什么颜色"能证明这条链路通了。
    /// 所以每行都打两份：cfg 是配置、live 是那一格 TextBlock 的 Foreground。
    /// </para>
    /// </summary>
    public string TextDiag
    {
        get
        {
            var sb = new StringBuilder();
            sb.Append($"text: keepThreshold={_textKeepThreshold} ");
            sb.Append($"label={Name(LabelBrush as SolidColorBrush)}{Environment.NewLine}");

            foreach ((string key, StackPanel row, StackPanel col) in FieldCells())
            {
                Brush? live = LiveBrush(row, col);
                (Brush? main, Brush? sub) = _state.FieldInk(key, alt: false);
                sb.Append($"  {key,-4} cfg={(FloatInk.IsAuto(FieldColor(key)) ? "auto" : FieldColor(key)),-8} " +
                          $"main={Name(main as SolidColorBrush)} sub={Name(sub as SolidColorBrush)} " +
                          $"live={Name(live as SolidColorBrush)}{Environment.NewLine}");
            }
            return sb.ToString().TrimEnd('\r', '\n');

            static string Name(SolidColorBrush? b) => b is null ? "null" : b.Color.ToString();
        }
    }

    /// <summary>取一格数值 TextBlock 实际的前景色。"改了到底有没有落到元素上"的判据。</summary>
    private static Brush? LiveBrush(StackPanel row, StackPanel col)
    {
        foreach (StackPanel panel in new[] { row, col })
        {
            if (panel.Visibility != Visibility.Visible) continue;
            foreach (object? child in LogicalTreeHelper.GetChildren(panel))
            {
                if (child is TextBlock { Foreground: { } brush }) return brush;
            }
        }
        return null;
    }

    // =================================================================
    // 缩放（Q-05）：100 / 125 / 150 三档
    // =================================================================

    /// <summary>
    /// 100% 档的基准尺寸（需求 6.2 / 6.3 的数值）。所有档位都是它乘乘数。
    ///
    /// <para>
    /// 高度只服务横向（6.1 的细条）：M4.11 把内存 / 显存的"已用 / 总共"拆成两行，
    /// 但<b>只在竖向拆</b>，横向仍是 `18.5/31.6G` 一行，所以这里保持 34 不动。
    /// 竖向的高度由内容决定（SizeToContent），不读这个值。
    /// </para>
    /// </summary>
    private const double BaseHeight = 34;
    private const double BaseValueFont = 13;
    private const double BaseValueLine = 16;
    private const double BaseLabelFont = 10;
    private const double BaseLabelLine = 13;
    private const double BaseGap = 8;

    /// <summary>三行格子里「已用」与「总共」之间的空隙（100% 档）。只有竖向用得到。</summary>
    private const double BaseSplitGap = 1;

    /// <summary>横向两端的内边距（6.3：左右各 10）。</summary>
    private const double BasePadX = 10;

    // ---- 竖向排列（M4.10）----

    /// <summary>
    /// 竖向时格与格之间的行距（100% 档）。M4.11 从 6 放到 8：内存 / 显存拆成三行之后
    /// 每格的行数不再是同一个数，行距太小会让"哪两行属于同一格"变得难分。
    /// </summary>
    private const double BaseRowGap = 8;

    /// <summary>竖向的上下内边距（100% 档）。</summary>
    private const double BasePadY = 12;

    /// <summary>
    /// 竖向的左右内边距（100% 档）。横向是 10，竖向收到 6：
    /// 竖条宽度完全由"最长的那句次值"撑着（实测 `19.0/31.6G` 要 53 DIP），
    /// 内边距每多 1 DIP 条就胖 2 DIP，这里是最划算的一刀。
    /// </summary>
    private const double BasePadXCompact = 6;

    /// <summary>
    /// 竖向下次值的字号比例（相对 <see cref="BaseLabelFont"/> 的 10 → 9）。
    ///
    /// <para>
    /// 竖条宽度 = 最长次值 + 内边距，而次值字号每降 1px，那句最长的就窄约 5 DIP。
    /// 只降次值、不动主值：竖排时用户的视线落在数字上，10 → 9 只是"详情更次要"，
    /// 主值仍是 13 号不用歪头。
    /// </para>
    /// </summary>
    private const double VerticalLabelRatio = 0.9;

    /// <summary>竖向排列会话（M4.10）。横向是 6.1 的默认形态。</summary>
    private bool _vertical;

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

        // 描边取整：0.8 DIP 的边框在布局取整后有时变 0、有时变 1，看上去像"描边时有时无"。
        Shell.BorderThickness = new Thickness(Math.Max(1, Math.Round(s)));

        ValueFontSize = BaseValueFont * s;
        ValueLineHeight = BaseValueLine * s;

        // 次值在竖向下缩一档：条宽就是被最长的那句次值撑出来的（见 VerticalLabelRatio）。
        LabelFontSize = BaseLabelFont * ( _vertical ? VerticalLabelRatio : 1.0) * s;
        LabelLineHeight = BaseLabelLine * s;

        // 横向的列间距、竖向的行距。竖向那个 Margin 只在竖向面板上用，
        // 横向时留着不参与布局，所以不用清。
        CellGap = new Thickness(0, 0, BaseGap * s, 0);
        RowCellMargin = new Thickness(0, 0, 0, BaseRowGap * s);
        SplitGap = new Thickness(0, BaseSplitGap * s, 0, 0);

        // 圆角、内边距这几个必须走代码：CornerRadius / Thickness 类型绑起来要先写转换器，
        // 而它们只在切档时变一次，代码直接给更省事。
        Shell.CornerRadius = new CornerRadius(10 * s);

        if (!_vertical)
        {
            // 横向（6.1）：高度定死，宽度交给 SizeToContent —— 六格宽度是内容决定的。
            RowPanel.Visibility = Visibility.Visible;
            ColumnPanel.Visibility = Visibility.Collapsed;

            SizeToContent = SizeToContent.Width;
            Width = double.NaN;          // 从竖向切回来时清掉定死的宽度，否则不会跟着内容走
            Height = BaseHeight * s;
            ShellHeight = BaseHeight * s;
            Shell.Padding = new Thickness(BasePadX * s, 0, BasePadX * s, 0);
        }
        else
        {
            // 竖向：宽高都按内容长（一列六格），没有一条边是手算的。
            RowPanel.Visibility = Visibility.Collapsed;
            ColumnPanel.Visibility = Visibility.Visible;

            // 最后一格也带着行距，所以底部内边距要减去这段，视觉留白才和顶部一致 ——
            // 不扣的话底边会明显悬空，扣多了又会贴边。
            double padBottom = Math.Max(0, BasePadY - BaseRowGap) * s;
            Shell.Padding = new Thickness(BasePadXCompact * s, BasePadY * s, BasePadXCompact * s, padBottom);

            ShellHeight = double.NaN;    // 条高改由内容决定
            SizeToContent = SizeToContent.WidthAndHeight;
            Width = double.NaN;
            Height = double.NaN;
        }

        foreach (FloatField f in AllFields) f.ApplyScale(s);

        // 间距是"按档位算出来的绝对值"，缩放变了得重排一次末位间距
        ApplyFieldVisibility();
    }

    /// <summary>
    /// 排列方向（M4.10）。<c>false</c> = 横向单行长条（6.1 默认），<c>true</c> = 竖向。
    ///
    /// <para>
    /// 与 <see cref="ScalePercent"/> 同一套收尾：改完要重排、要防止窗口跑出屏幕、
    /// 要把位置按新尺寸重存并落盘。少任何一步的表现是"切成竖向之后窗口一半在屏幕外"。
    /// </para>
    /// </summary>
    public bool IsVertical
    {
        get => _vertical;
        set
        {
            if (_vertical == value) return;
            _vertical = value;
            ApplyScale();
            SyncMenuChecks();
            PersistAppearance();

            if (_hwndReady)
            {
                UpdateLayout();
                EnsureFullyVisible();
                SavePlacement();
            }
        }
    }

    /// <summary>
    /// 竖排时的高度估计（DIP）。只给 <see cref="PrePlaceBeforeShow"/> 用 —— Show 之前
    /// 量不到窗口尺寸，先按"六格两行 + 内存 / 显存各多一行 + 行距 + 上下内边距"估一个；
    /// 这串数字是定值不是猜的，误差只剩边框那 2 像素，随后的物理像素校正会吸收掉。
    /// </summary>
    private double EstimateVerticalLength()
    {
        double s = _scalePercent / 100.0;
        double twoLine = BaseValueLine + BaseLabelLine;
        double threeLine = BaseValueLine + BaseLabelLine + BaseSplitGap + BaseLabelLine;

        // 内存 / 显存拆成三行（M4.11），其余是两行；关掉的字段不占位（M4.14），
        // 行距个数也跟着可见字段数走 —— 否则预摆位会把条估高，首帧就贴得偏高。
        double cells = 0;
        int visible = 0;
        foreach (string key in FloatFields.Keys)
        {
            if (!FloatFields.IsVisible(_config, key)) continue;
            visible++;
            cells += key is "ram" or "vram" ? threeLine : twoLine;
        }

        double gaps = Math.Max(0, visible - 1) * BaseRowGap;
        return (cells + gaps + BasePadY + Math.Max(0, BasePadY - BaseRowGap)) * s
               + 2 * Shell.BorderThickness.Left;
    }

    /// <summary>九个字段，按显示顺序。缓存一份，避免每次缩放都分配数组。</summary>
    private FloatField[] AllFields => new[]
    {
        _state.FloatFps, _state.FloatCpu, _state.FloatGpu,
        _state.FloatRam, _state.FloatVram, _state.FloatNet,
        _state.FloatDisk, _state.FloatIo, _state.FloatPwr,
    };

    // =================================================================
    // 字段开关（M4.14）
    // =================================================================

    /// <summary>
    /// 字段 key ↔ 两个面板里那一格。横竖两套面板各有一份同名元素，
    /// 关掉一个字段必须两处一起收 —— 只改一套的话，切排列时它会"复活"，
    /// 而这种错位编译完全不报错。
    /// </summary>
    private (string Key, StackPanel Row, StackPanel Col)[] FieldCells() => new[]
    {
        ("fps", RowFps, ColFps),
        ("cpu", RowCpu, ColCpu),
        ("gpu", RowGpu, ColGpu),
        ("ram", RowRam, ColRam),
        ("vram", RowVram, ColVram),
        ("net", RowNet, ColNet),
        ("disk", RowDisk, ColDisk),
        ("io", RowIo, ColIo),
        ("pwr", RowPwr, ColPwr),
    };

    /// <summary>按配置把「显示字段」落到两套面板上。构造函数里调一次，之后每次切换再调。</summary>
    private void ApplyFieldVisibility()
    {
        // 先找出"最后一个可见格"：横条的右间距只加在格子之间，
        // 末位那格不能带 —— 否则关掉后面几项时条子右边多出一截空白，宽度凭空变大。
        // （竖向那套每格都带下间距，含末位，是既有行为，不动。）
        var cells = FieldCells();
        int lastVisible = -1;
        for (int i = 0; i < cells.Length; i++)
        {
            if (FloatFields.IsVisible(_config, cells[i].Key)) lastVisible = i;
        }

        for (int i = 0; i < cells.Length; i++)
        {
            (string key, StackPanel row, StackPanel col) = cells[i];
            bool show = FloatFields.IsVisible(_config, key);
            Visibility v = show ? Visibility.Visible : Visibility.Collapsed;
            if (row.Visibility != v) row.Visibility = v;
            if (col.Visibility != v) col.Visibility = v;

            Thickness gap = i == lastVisible ? new Thickness(0) : CellGap;
            if (!row.Margin.Equals(gap)) row.Margin = gap;
        }
    }

    /// <summary>
    /// 「显示字段」子菜单。按 <see cref="FloatFields.Keys"/> 生成，
    /// 与设置页的勾选框共用同一张表 —— 两处各写一份顺序，迟早会有一个勾错。
    /// </summary>
    private void BuildFieldMenu()
    {
        if (FieldMenu is null) return;
        FieldMenu.Items.Clear();

        for (int i = 0; i < FloatFields.Keys.Length; i++)
        {
            var item = new MenuItem
            {
                Header = FloatFields.Labels[i],
                Tag = FloatFields.Keys[i],
                IsCheckable = true,
                IsChecked = FloatFields.IsVisible(_config, FloatFields.Keys[i]),
            };
            item.Click += Field_Click;
            FieldMenu.Items.Add(item);
        }
    }

    private void Field_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: string key } item) return;

        if (!FloatFields.Toggle(_config, key))
        {
            // 只剩这一个：WPF 已经把勾翻掉了，翻回来并说明原因。
            item.IsChecked = true;
            return;
        }

        if (PersistToConfig) ConfigStore.SaveCurrent();
        ReloadFields();
    }

    /// <summary>
    /// 探针专用：翻一个字段开关，返回 true = 已改变。
    ///
    /// <para>
    /// 比 <see cref="FloatFields.Toggle(AppConfig, string)"/> 多做的唯一一件事是
    /// <b>退出时还原</b>：探针改的是共享的那份 <c>AppConfig</c>，而主窗口关闭时也会整份落盘
    /// —— 实测跑一次字段探针就把用户的字段永久关掉了，之后每次验收量到的都是
    /// "上次探针留下的尺寸"。挂 <c>Application.Exit</c> 在最末尾写回原值，
    /// 位置比任何窗口的 Closing 都晚，谁先存都不会把它带走。
    /// </para>
    /// </summary>
    private bool ProbeToggle(string key)
    {
        if (_probeHiddenBackup is null)
        {
            _probeHiddenBackup = _config.FloatHiddenFields is { } cur ? (string[])cur.Clone() : Array.Empty<string>();

            if (Application.Current is { } app)
                app.Exit += (_, _) =>
                {
                    _config.FloatHiddenFields = _probeHiddenBackup;
                    ConfigStore.SaveCurrent();
                };
        }

        return FloatFields.Toggle(_config, key);
    }

    /// <summary>探针改字段之前的原值；非 null 就说明这次运行动过字段。</summary>
    private string[]? _probeHiddenBackup;

    /// <summary>自测用：按 key 翻一个字段开关，走的是菜单那条路（<c>--switch-view field:ram@3000</c>）。</summary>
    public void ToggleFieldForProbe(string key)
    {
        if (ProbeToggle(key)) ReloadFields();
    }

    /// <summary>
    /// 字段变化后重排：关掉一格，条要立刻跟着变窄/变矮，而不是等下次显示。
    ///
    /// <para>
    /// 供设置页调（<c>GetFloat()?.ReloadFields()</c>）：窗口没开时设置页只改配置，
    /// 窗口开着就直接走这里，两边不会出现"配置说关了、条上还在"。
    /// </para>
    /// </summary>
    public void ReloadFields()
    {
        ApplyFieldVisibility();
        SyncMenuChecks();
        RelayoutNow();
    }

    /// <summary>
    /// 强制重排一次并夹回屏幕内。
    ///
    /// <para>
    /// <c>UpdateLayout</c> 必须在这里：<c>SizeToContent</c> 的窗口改完可见性/内边距后，
    /// 布局是延迟的，不刷一次 <c>GetWindowRect</c> 拿到的还是旧尺寸，
    /// 夹取就会按旧宽高算 —— 表现是"改完字段条跑出屏幕"或"宽度没变"。
    /// </para>
    /// </summary>
    private void RelayoutNow()
    {
        // 不要在这里把 Width/Height 清成 NaN —— 实测（M4.14）：那样做反而更糟。
        // 隐藏状态下清 NaN 之后，WPF 不会给 HWND 重新定尺寸（Show 之后 Width 仍读到 NaN，
        // 尺寸停在旧值）；而 SizeToContent 自己写的那个定值（如 47.33）在
        // InvalidateMeasure + UpdateLayout 之后是会跟着内容走的。
        InvalidateMeasure();
        Shell.InvalidateMeasure();
        UpdateLayout();

        // 首帧之前位置还没摆过（Left/Top 是默认值），这时候夹取与落盘都会算错比例 ——
        // 交给 ContentRendered 那条路。
        if (!_contentRenderedOnce || !_hwndReady) return;

        EnsureFullyVisible();
        SavePlacement();
    }

    /// <summary>
    /// HWND 的真实尺寸（物理像素）与 <c>SizeToContent</c> 当前值（M4.14）。
    ///
    /// <para>
    /// 为什么必须单列一项：<c>--shot</c> 走 <c>RenderTargetBitmap</c>，它按
    /// <c>ActualWidth/Height</c>（WPF 布局值）画，**量不到 HWND 实际多大**。
    /// 两者不一致时 —— 布局已重排、HWND 还是旧尺寸 —— 截图看起来完全正常，
    /// 而用户屏幕上看到的就是"宽度不对"。这个洞不补，宽度类的 bug 会一直"复现不了"。
    /// </para>
    /// </summary>
    public string HwndDiag
    {
        get
        {
            IntPtr h = Handle;
            string rect = h != IntPtr.Zero && Monitors.TryGetRect(h, out Win32Window.RECT r)
                ? $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) {r.Right - r.Left}x{r.Bottom - r.Top}px"
                : "no-hwnd";

            return $"hwnd={rect} dip={ActualWidth:F2}x{ActualHeight:F2} " +
                   $"stc={SizeToContent} vertical={_vertical} width={(double.IsNaN(Width) ? "NaN" : Width.ToString("F2"))}";
        }
    }

    /// <summary>
    /// 缩放诊断串，供截图 sidecar 文件输出。缩放是"每列宽 + 字号 + 条高"一起变，
    /// 光看截图分不清"确实放大了"和"我只改了字号没改列宽"，把数字打出来最省事。
    /// </summary>
    public string ScaleDiag
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            sb.Append(_vertical ? "竖 " : "横 ");
            sb.Append(_scalePercent).Append("% ");
            foreach (FloatField f in AllFields)
                sb.Append(f.Key).Append('=').Append(f.Width.ToString("F1")).Append(' ');
            sb.Append($"font={ValueFontSize:F1}/{LabelFontSize:F1} h={ShellHeight:F1}");

            // 布局实测：Style Setter 里的绑定是否真的落到元素上，只能看元素的运行值。
            // 光看 XAML 会误以为"写了就一定生效"——这里把每个格子（StackPanel）的实际高度
            // 连同首个子元素的 实际高度/行高/字号 一起打出来。三行格（内存 / 显存）
            // 到底有没有把条撑出边界，只有这串数字能说清。
            Panel panel = _vertical ? ColumnPanel : RowPanel;
            sb.Append($" cells={panel.ActualWidth:F0}x{panel.ActualHeight:F0} [");
            foreach (UIElement child in panel.Children)
            {
                if (child is not StackPanel cell) continue;
                sb.Append($"{cell.ActualHeight:F1}");
                if (cell.Children.Count > 0 && cell.Children[0] is TextBlock first)
                    sb.Append($"/{first.ActualHeight:F1}");
                sb.Append(' ');
            }
            sb.Append(']');

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

    /// <summary>竖向面板里每一格的外边距：下侧是行距。</summary>
    public static readonly DependencyProperty RowCellMarginProperty =
        DependencyProperty.Register(nameof(RowCellMargin), typeof(Thickness), typeof(FloatWindow),
            new PropertyMetadata(new Thickness(0, 0, 0, BaseRowGap)));

    /// <summary>三行格子里第三行（"总共"）的上边距，把"已用"和"总共"分成一组两行。</summary>
    public static readonly DependencyProperty SplitGapProperty =
        DependencyProperty.Register(nameof(SplitGap), typeof(Thickness), typeof(FloatWindow),
            new PropertyMetadata(new Thickness(0, BaseSplitGap, 0, 0)));

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

    public Thickness RowCellMargin
    {
        get => (Thickness)GetValue(RowCellMarginProperty);
        set => SetValue(RowCellMarginProperty, value);
    }

    public Thickness SplitGap
    {
        get => (Thickness)GetValue(SplitGapProperty);
        set => SetValue(SplitGapProperty, value);
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

    /// <summary>
    /// DWM 那条 1px 窗口边框怎么处理（M4.13）：<c>none</c>（默认，关掉）/ <c>default</c>
    /// （系统主题色）/ <c>#RRGGBB</c>（指定色，仅用于判定"拖动白边是不是这条属性画的"）。
    /// </summary>
    public string BorderMode { get; set; } = "none";

    /// <summary>边框设置结果，写进截图 sidecar（<c>border: none / default / #FF0000 / unsupported</c>）。</summary>
    public string DwmBorderDiag { get; private set; } = "?";

    private const int GWL_STYLE = -16;
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_TOPMOST = 0x00000008L;
    private const int WS_THICKFRAME = 0x00040000;
    private const int WM_NCCALCSIZE = 0x0083;
    private const int WM_NCACTIVATE = 0x0086;
    private const int WM_NCHITTEST = 0x0084;

    /// <summary>命中测试结论"这里没有我"，系统继续往下层窗口投递。</summary>
    private const int HTTRANSPARENT = -1;

    [System.Runtime.InteropServices.DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

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

        // 钩子已在 SourceInitialized 里挂好（那里不受 SystemShadow 影响），这里只管 style。
        IntPtr style = GetWindowLongPtr(hwnd, GWL_STYLE);
        SetWindowLongPtr(hwnd, GWL_STYLE, (IntPtr)((long)style | WS_THICKFRAME));

        // 改完 style 必须通知框架已变，否则 DWM 仍按旧框架缓存渲染。
        Monitors.NotifyFrameChanged(hwnd);

        // 顺序同样是"加完框架再抹边框"：WS_THICKFRAME 正是 DWM 决定要不要画边框的依据，
        // 先设边框色再改 style，这一次 style 变更会把边框带回来。
        DwmBorderDiag = ApplyDwmBorder(hwnd);
    }

    /// <summary>
    /// 按 <see cref="BorderMode"/> 设置 DWM 边框色，返回给 sidecar 的诊断字符串。
    ///
    /// <para>
    /// 这条边框不是我们画的，是 DWM 对"有框架窗口"（<see cref="WS_THICKFRAME"/>）的附加产物：
    /// 颜色跟随主题，浅色主题下接近白色。静止时它压在圆角与阴影之间不太显眼，
    /// 一旦进入拖动（move loop）就整圈亮起来 —— 用户说的"移动时的白色边框"就是它。
    /// </para>
    /// </summary>
    private string ApplyDwmBorder(IntPtr hwnd)
    {
        switch (BorderMode.ToLowerInvariant())
        {
            case "none":
                return Win32Window.TryDisableDwmBorder(hwnd) ? "none" : "unsupported";

            case "default":
                return Win32Window.TryResetDwmBorder(hwnd) ? "default" : "unsupported";

            default:
                if (BorderMode.Length == 7 && BorderMode[0] == '#' && int.TryParse(
                        BorderMode.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out int rgb))
                {
                    // #RRGGBB → COLORREF 0x00BBGGRR（红蓝对调，Win32 的老约定）
                    int colorRef = (rgb & 0xFF) << 16 | (rgb >> 8 & 0xFF) << 8 | (rgb >> 16 & 0xFF);
                    return Win32Window.TrySetDwmBorderColor(hwnd, colorRef)
                        ? BorderMode.ToUpperInvariant()
                        : "unsupported";
                }
                return "none";
        }
    }

    /// <summary><c>WM_NCCALCSIZE</c> 命中次数。自测用：0 就说明那条框架从来没被吃掉。</summary>
    private int _ncCalcHits;

    /// <summary><c>WM_NCACTIVATE</c> 命中次数。自测用：失焦到底有没有走到那条重绘路径。</summary>
    private int _ncActivateHits;

    /// <summary>
    /// 自测：<c>--no-ncactivate-guard</c> 关掉"失焦时不重画非客户区"，A/B 用（M4.17）。
    /// </summary>
    public bool NcActivateGuard { get; set; } = true;

    /// <summary>框架守门补过的次数。自测 sidecar 会输出，平时应当是 0。</summary>
    private int _frameGuardHits;

    /// <summary>自测：<c>--no-frame-guard</c> 关掉守门，A/B 用。</summary>
    public bool FrameGuard { get; set; } = true;

    /// <summary>
    /// 检查"客户区有没有被 DWM 又切掉一圈"，切了就补一次框架变更通知。
    ///
    /// <para>
    /// <c>WS_THICKFRAME</c> 是拿系统阴影的代价：它同时让 DWM 认为这个窗口有 8px 框架。
    /// 平时靠 <see cref="NcCalcSizeHook"/> 返回 0 把客户区铺满整个窗口吃掉它，
    /// 但这条消息不是每次重绘都来 —— 一旦某次 DWM 用了带框架的几何，窗口四周就多一圈
    /// （浅色主题下是白的），而窗口位置/尺寸/激活状态的变化都会触发重新计算，
    /// 所以在这四个时机各看一眼最省事。
    /// </para>
    ///
    /// <para>
    /// 只在真的不一致时才动：通知用的是 <c>SWP_NOMOVE|NOSIZE|NOZORDER|NOACTIVATE</c>，
    /// 不改几何也不会重排，但没必要每秒发一次。
    /// </para>
    /// </summary>
    private void GuardFrame(bool force = false)
    {
        if (!FrameGuard || !EatFrame || !SystemShadow || !_hwndReady) return;

        IntPtr hwnd = Handle;
        if (hwnd == IntPtr.Zero) return;
        if (!Monitors.TryGetRect(hwnd, out Win32Window.RECT wr)) return;
        if (!Win32Window.TryGetClientRect(hwnd, out int cw, out int ch)) return;

        int ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
        if (cw == ww && ch == wh && !force) return;      // 正常：客户区铺满窗口

        _frameGuardHits++;
        Monitors.NotifyFrameChanged(hwnd);
    }

    /// <summary>
    /// 自测：<c>--no-nccalc</c> 关掉"把客户区铺满整个窗口"。关掉后 <c>WS_THICKFRAME</c>
    /// 带来的 8px 框架会整圈露出来（150% 下 12 物理像素），用于确认白框的真身。
    /// </summary>
    public bool EatFrame { get; set; } = true;

    private IntPtr NcCalcSizeHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        // wParam=TRUE 表示"客户区要重新算"。返回 0 = 整个窗口都是客户区，
        // 于是 WS_THICKFRAME 带来的 8px 边框不可见，但 DWM 仍按"有边框窗口"给它画阴影。
        // EatFrame=false 是给自测用的反例开关：不吃框架，DWM 那条 8px 就会露出来，
        // 用来确认用户看到的白框到底是不是它。
        if (msg == WM_NCCALCSIZE && wParam != IntPtr.Zero && EatFrame)
        {
            _ncCalcHits++;          // 自测用：这条消息到底来过几次
            handled = true;
            return IntPtr.Zero;
        }

        // WM_NCACTIVATE（M4.17）：窗口**失焦**时 DefWindowProc 会顺手重画一遍非客户区，
        // 那条被 WM_NCCALCSIZE 吃掉的框架就在这时被画回来 —— 浅色主题下是白的，
        // 正好对上"移动完点桌面/点别的应用/开着设置页就冒白框"（三者共同点都是失焦）。
        // 把 lParam 传 -1 让 DefWindowProc 跳过非客户区重绘（WPF WindowChrome 同款做法）。
        if (msg == WM_NCACTIVATE && NcActivateGuard && EatFrame)
        {
            _ncActivateHits++;
            handled = true;
            return DefWindowProc(hwnd, msg, wParam, (IntPtr)(-1));
        }
        // WM_NCHITTEST（M4.21 鼠标穿透）：返回 HTTRANSPARENT，系统会把鼠标消息
        // 继续投递给下层的窗口 —— 点悬浮窗等于点它下面的东西。
        //
        // 与任务栏那条用的是同一招，**不用 WS_EX_TRANSPARENT**：那个要配 LAYERED 才生效，
        // 一配 WPF 就退软件合成、毛玻璃全变（M4.1 踩过）。HTTRANSPARENT 只改命中测试的结论，
        // 不碰渲染，画面一个像素都不会变。切换也不需要重建窗口 —— 这条消息每次都读当前值。
        //
        // 代价：右键菜单也一起收不到了（消息根本不投递过来）。出口放在托盘图标、
        // 设置页和全局热键三处，缺一个都会把用户困住，见 ConfigStore.FloatClickThrough。
        if (msg == WM_NCHITTEST && _clickThrough)
        {
            _hitTestHits++;
            handled = true;
            return new IntPtr(HTTRANSPARENT);
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

        // 竖向时 Height 是 NaN（Height 交给 SizeToContent），要退回实际渲染高度。
        // 这一步在 ContentRendered 里，ActualHeight 已经有值；万一还没量出来，按短边兜底，
        // 至少不会算出 NaN 让窗口跑到屏幕外。
        double h = ActualHeight > 0 ? ActualHeight : BaseHeight * (_scalePercent / 100.0);

        Left = wa.Left + 12;
        Top = wa.Bottom - h - 12;
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
            // 深色玻璃的实底用 BrushGlassSolidDark 而**不是** BrushBg（2026-11-01 双主题）：
            // BrushBg 现在属于 App 主题（大窗口/设置页），用户把大窗口切成白色后它会变成浅灰，
            // 深色玻璃的底就跟着变浅，与它自己的浅色文字配色直接打架。
            // 玻璃深浅只由 FloatTheme 决定，必须与 App 主题完全无关（见 PaletteShared 的说明）。
            Shell.Background = light ? Palette.Brush("BrushGlassSolid") : Palette.Brush("BrushGlassSolidDark");
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
        // 这一档借用悬停档的描边作为常驻值，用户至少能找到窗口的四条边 ——
        // 但这只在 normal 档成立，「淡」/「无」是用户主动选的，不能被这里改回去。
        ApplyEdge();
    }

    /// <summary>
    /// 常态描边的档位（M4.15）：<c>none</c>（透明）/ <c>faint</c>（5%）/ <c>normal</c>（12%）。
    ///
    /// <para>
    /// 只管**静止时**那一圈；悬停与拖动仍各自给反馈（否则选了「无」就彻底没有边界，
    /// 鼠标进窗口、拖动窗口都毫无回应）。这是刻意的：三个状态要能分开看，
    /// "静止看不见"不该顺带把"被抓住了"也一起抹掉。
    /// </para>
    /// </summary>
    public string EdgeMode
    {
        get => _edgeMode;
        set
        {
            string v = NormalizeEdgeMode(value);
            if (_edgeMode == v) return;
            _edgeMode = v;
            ApplyEdge();
            SyncMenuChecks();
            PersistAppearance();
        }
    }

    private string _edgeMode = "normal";

    /// <summary>把任意输入夹到三档之一，认不出来按 normal（与缩放档同一套取舍）。</summary>
    private static string NormalizeEdgeMode(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "none" => "none",
        "faint" => "faint",
        _ => "normal",
    };

    /// <summary>按「主题 × 档位」取常态描边的画刷。</summary>
    private static Brush EdgeBrushFor(bool light, string mode) => mode switch
    {
        "none" => Palette.Brush("BrushGlassEdgeNone"),
        "faint" => Palette.Brush(light ? "BrushGlassEdgeFaint" : "BrushGlassEdgeDarkFaint"),
        _ => Palette.Brush(light ? "BrushGlassEdge" : "BrushGlassEdgeDark"),
    };

    /// <summary>
    /// 把当前档位刷到描边上。抽成方法是因为三个入口都要走它：
    /// 换主题、换浓度档（极透档会临时提亮）、用户在菜单/设置页里改档位。
    /// </summary>
    private void ApplyEdge()
    {
        bool light = _theme == GlassTheme.Light;
        _edgeNormal = EdgeBrushFor(light, _edgeMode);

        // 极透档借用悬停档提亮 —— 但只在 normal 档这么做。
        // 「淡」和「无」本来就是为了不要那圈白，再提亮一次等于把用户的选择改回去。
        (_, _, bool blurOnly) = (light ? LightSteps : DarkSteps)[_level];
        _edgeIdle = (_glass && blurOnly && _edgeMode == "normal") ? _edgeHover : _edgeNormal;

        Shell.BorderBrush = _edgeIdle;
    }

    private void ApplyTextTheme(bool light)
    {
        // 用户选的色深浅通用：条的底色有七档 × 深浅两套，替他"自动提亮"等于猜，
        // 猜错了更难解释。设置页给了深底 / 浅底两块预览，让用户自己看。
        Brush themedSub = Palette.Brush(light ? "BrushTextSecondaryLight" : "BrushTextSecondary");

        if (DataContext is DashboardState state)
        {
            state.SetFloatTheme(light);
            state.SetFloatText(_fieldColors, _textKeepThreshold, themedSub);
        }

        // 保留这个 DP：任务栏那种"整条一个次值色"的写法已被逐字段取代，
        // 但它是诊断里"主题色现在是什么"的参照，也让老的自测脚本不至于读不到东西。
        LabelBrush = themedSub;

        _edgeNormal = EdgeBrushFor(light, _edgeMode);
        _edgeHover = Palette.Brush(light ? "BrushGlassEdgeHover" : "BrushGlassEdgeDarkHover");
        _edgeDrag = Palette.Brush(light ? "BrushGlassEdgeDrag" : "BrushGlassEdgeDragDark");
        Shell.BorderBrush = _edgeNormal;
    }

    private Brush _edgeNormal = Palette.Brush("BrushGlassEdge");
    private Brush _edgeHover = Palette.Brush("BrushGlassEdgeHover");

    /// <summary>
    /// 按住拖动时的描边（M4.14）：粉色。
    /// 拖动期间窗口一直在动，靠"提亮一档"表达"被抓住"只会变成一圈白边；
    /// 换成一个只在这种状态下出现的颜色，手一松就消失，语义反而更清楚。
    /// </summary>
    private Brush _edgeDrag = Palette.Brush("BrushGlassEdgeDrag");

    /// <summary>是否正处在拖动循环里（DragMove 是模态循环，期间不能切悬停描边）。</summary>
    private bool _dragging;

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

        // 拖动时整圈换成粉色描边（M4.14）。之前那版是"压回常态"，可拖动必然按住窗口
        // —— 也就必然触发 MouseEnter，常态压不住多久就被悬停档顶回去；用户看到的是
        // 一圈白边跟着窗口跑，松手后还要等鼠标移开才消失。粉色只属于"正在拖"这个状态，
        // 松手立刻撤掉，既不会误认为悬停，也不会拖完还挂着。
        BeginDragVisual();
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
            EndDragVisual();
        }

        // 吸附、夹紧与落盘放在松手之后：拖动途中改位置会和 DragMove 抢，窗口会抖
        SettlePlacement();
        SavePlacement();
    }

    /// <summary>
    /// 进入拖动外观：粉色描边。抽成方法是为了让自测探针能走同一条路 ——
    /// "拖动时是什么颜色"靠截图看，而截图没法真的按住窗口去拖。
    /// </summary>
    private void BeginDragVisual()
    {
        _dragging = true;
        Shell.BorderBrush = _edgeDrag;
    }

    /// <summary>
    /// 退出拖动外观：一律回到常态，**不是** <c>IsMouseOver ? hover : idle</c>。
    ///
    /// <para>
    /// 松手时鼠标一定还在窗口上，按悬停恢复等于"粉色换成白色继续挂着" ——
    /// 用户说的"拖动结束白边不能及时消失"就是这个。悬停反馈等下一次 MouseEnter 再给。
    /// </para>
    /// </summary>
    private void EndDragVisual()
    {
        _dragging = false;
        Shell.BorderBrush = _edgeIdle;
        Shell.InvalidateVisual();
    }

    private void Shell_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        // 右键菜单挂在 Border 上，Topmost 窗口里也照常弹（ContextMenu 自身是独立 popup）
        ShowContextMenu();
        e.Handled = true;
    }

    /// <summary>
    /// 自测：<c>--float-drag-probe keep|release</c>。
    ///
    /// <para>
    /// 两个值各管一半证据：<c>keep</c> 停在拖动外观上给 <c>--shot</c> 拍（RTB 走视觉树，
    /// 颜色是准的），<c>release</c> 立刻撤掉，验的就是"松手后到底有没有回到常态"。
    /// 三个色都写进 sidecar —— 只看截图分不清"回到常态"和"本来就没变"。
    /// </para>
    /// </summary>
    private void RunDragProbe(string mode)
    {
        static string Name(Brush b) => b is SolidColorBrush sc ? sc.Color.ToString() : b?.ToString() ?? "null";

        BeginDragVisual();
        string during = Name(Shell.BorderBrush);
        UpdateLayout();

        if (!string.Equals(mode, "keep", StringComparison.OrdinalIgnoreCase))
            EndDragVisual();

        PlacementDiag = $"drag-probe: mode={mode} during={during} " +
                        $"after={Name(Shell.BorderBrush)} idle={Name(_edgeIdle)} " +
                        $"hover={Name(_edgeHover)}";
    }

    /// <summary>自测：每格的可见性与右间距，用来查"条子为什么宽了 8 DIP"这类问题。</summary>
    public string FieldsDiag
    {
        get
        {
            var sb = new System.Text.StringBuilder();
            foreach ((string key, StackPanel row, StackPanel col) in FieldCells())
            {
                sb.Append(' ').Append(key).Append(row.Visibility == Visibility.Visible ? "+" : "-")
                  .Append("/r").Append(row.Margin.Right.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture))
                  .Append("/w").Append(row.Width.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture));
            }
            return sb.ToString().TrimStart();
        }
    }

    /// <summary>窗口矩形与客户区的单边差值（物理像素）。0 = 框架已被吃掉。</summary>
    private static string FrameGap(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return "n/a";
        if (!Monitors.TryGetRect(hwnd, out Win32Window.RECT wr)) return "n/a";
        if (!Win32Window.TryGetClientRect(hwnd, out int cw, out int ch)) return "n/a";
        int ww = wr.Right - wr.Left, wh = wr.Bottom - wr.Top;
        return $"{(ww - cw) / 2}/{(wh - ch) / 2}";
    }

    /// <summary>
    /// 自测：<c>--float-move-focus-probe move,defocus,drag</c>。
    ///
    /// <para>
    /// 复现"把窗口挪过位置、再去点别的窗口，边上就冒出一圈白"。三个开关随便组合：
    /// <c>move</c> 搬一次位置、<c>drag</c> 走一遍拖动收尾、<c>defocus</c> 把焦点让给桌面。
    /// 抓屏之外还要看描边的**画刷值** —— 白框到底是我们画的还是 DWM 画的，
    /// 光看截图分不出来，得看 <c>Shell.BorderBrush</c> 变没变。
    /// </para>
    /// </summary>
    private void RunMoveFocusProbe(string spec)
    {
        static string Name(Brush b) => b is SolidColorBrush sc ? sc.Color.ToString() : b?.ToString() ?? "null";

        var opts = spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                       .Select(s => s.ToLowerInvariant()).ToHashSet();
        IntPtr hwnd = Handle;
        string before = Name(Shell.BorderBrush);

        if (opts.Contains("drag"))
        {
            BeginDragVisual();
            EndDragVisual();
        }

        if (opts.Contains("move") && hwnd != IntPtr.Zero && Monitors.TryGetRect(hwnd, out Win32Window.RECT r0))
            Monitors.MoveWindowTo(hwnd, r0.Left + 60, r0.Top + 40);

        if (opts.Contains("defocus")) Win32Window.MoveFocusToDesktop();

        // 主动通知"框架变了"：真实拖动/跨屏/失焦重绘都会让 DWM 重算非客户区，
        // 用这一招把那条路径拉进来，看 8px 框架会不会露出来。
        if (opts.Contains("frame") && hwnd != IntPtr.Zero) Monitors.NotifyFrameChanged(hwnd);

        // 等一拍再读：失焦后的重绘不同步，立刻读到的还是旧状态
        var settle = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            IntPtr fg = Win32Window.ForegroundWindow;
            string rect = hwnd != IntPtr.Zero && Monitors.TryGetRect(hwnd, out Win32Window.RECT r1)
                ? $"{r1.Left},{r1.Top} {r1.Right - r1.Left}x{r1.Bottom - r1.Top}"
                : "n/a";

            // 客户区 vs 窗口矩形：差值就是 DWM 那条 8px 框架露出来的宽度（150% 下 12px）
            string frame = "n/a";
            if (hwnd != IntPtr.Zero && Win32Window.TryGetClientRect(hwnd, out int cw, out int ch)
                && Monitors.TryGetRect(hwnd, out Win32Window.RECT r2))
            {
                int ww = r2.Right - r2.Left, wh = r2.Bottom - r2.Top;
                frame = $"window={ww}x{wh} client={cw}x{ch} gapLR={(ww - cw) / 2} gapTB={(wh - ch) / 2}";
            }

            // 自证守门有效：先把框架放出来（gap 应为 8/12px），再让守门收回去（应回 0）
            if (opts.Contains("framebreak") && hwnd != IntPtr.Zero)
            {
                EatFrame = false;
                Monitors.NotifyFrameChanged(hwnd);
                UpdateLayout();
                string broken = FrameGap(hwnd);
                EatFrame = true;
                GuardFrame();
                Monitors.NotifyFrameChanged(hwnd);
                UpdateLayout();
                frame += $" | break={broken} -> guard={FrameGap(hwnd)} hits={_frameGuardHits}";
            }

            PlacementDiag = $"move-focus: spec={spec}{Environment.NewLine}" +
                            $"  border before={before} after={Name(Shell.BorderBrush)}{Environment.NewLine}" +
                            $"  idle={Name(_edgeIdle)} normal={Name(_edgeNormal)} hover={Name(_edgeHover)}{Environment.NewLine}" +
                            $"  active={IsActive} fgIsSelf={fg == hwnd} rect={rect}{Environment.NewLine}" +
                            $"  ncCalcHits={_ncCalcHits} ncActivateHits={_ncActivateHits} guardHits={_frameGuardHits} {frame}{Environment.NewLine}" +
                            $"  dmwBorder={DwmBorderDiag}";
        };
        settle.Start();
    }

    /// <summary>
    /// 自测：<c>--float-field-probe ram,vram</c>。逐个翻字段开关，
    /// 把"翻的结果 + 翻完的窗口尺寸"写进 sidecar —— 关掉两格之后条必须真的变窄/变矮，
    /// 光看菜单上的勾说明不了布局有没有跟着收。
    /// </summary>
    private void RunFieldProbe(string keys)
    {
        var sb = new System.Text.StringBuilder("field-probe:");

        foreach (string raw in keys.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string key = raw.ToLowerInvariant();
            sb.Append(' ').Append(key).Append(ProbeToggle(key) ? "=toggled" : "=blocked");
        }

        ReloadFields();
        Shell.UpdateLayout();

        sb.Append($" win={ActualWidth:F1}x{ActualHeight:F1} visible={FloatFields.VisibleCount(_config)}");
        PlacementDiag = sb.ToString();
    }

    /// <summary>
    /// 弹右键菜单。抽成方法是为了让 <c>--float-menu-probe</c> 走同一条路径 ——
    /// 竖向窗口比横向矮胖得多（约 110 × 140），菜单落点是 Popup 按 PlacementTarget
    /// 的屏幕矩形算的，换形态必须重验一次。
    /// </summary>
    private void ShowContextMenu()
    {
        if (Shell.ContextMenu is not { } menu) return;

        menu.PlacementTarget = Shell;
        menu.IsOpen = true;
    }

    /// <summary>
    /// 自测：<c>--float-menu-probe</c>。验两件事 ——
    /// ① 右键路由事件还能把菜单打开（事件链没被打断）；
    /// ② 命中测试能在 Border 上找到子元素（找得到 = 点得到 = 也能拖）。
    ///
    /// <para>
    /// 不靠真实鼠标：本机光标随时会被使用者挪走（M4.3 踩过），
    /// 而 <c>RaiseEvent</c> 走的是与 WPF 输入系统同一套路由，差别只在"谁第一个收到"。
    /// 取窗口正中一点，横向 / 竖向都落在条内。
    /// </para>
    /// </summary>
    private void ProbeMenuAndHitTest()
    {
        var args = new MouseButtonEventArgs(Mouse.PrimaryDevice, Environment.TickCount, MouseButton.Right)
        {
            RoutedEvent = UIElement.MouseRightButtonUpEvent,
        };
        Shell.RaiseEvent(args);

        bool open = Shell.ContextMenu?.IsOpen == true;

        // 取正中一点：能取到元素 = 命中测试正常，右键与拖动走的是同一套命中。
        // 取 null 就说明"看得见却点不到"。
        // 先 UpdateLayout 再量：ContentRendered 那一刻竖向窗口的 SizeToContent 还没跑完，
        // 不刷会量到 28 宽这种中间值，看着像"条子只占了一半"。
        Shell.UpdateLayout();
        object? hit = Shell.InputHitTest(new Point(Shell.ActualWidth / 2, Shell.ActualHeight / 2));
        Point origin = Shell.PointToScreen(new Point(0, 0));

        PlacementDiag = $"menu-probe: isOpen={open} hit={hit?.GetType().Name ?? "null"} " +
                        $"win={ActualWidth:F0}x{ActualHeight:F0} shell={Shell.ActualWidth:F0}x{Shell.ActualHeight:F0} " +
                        $"origin=({origin.X:F0},{origin.Y:F0})";

        if (FieldMenu is not null)
        {
            foreach (object child in FieldMenu.Items)
            {
                if (child is MenuItem { Tag: string k } mi)
                    System.Console.WriteLine($"menufield: [{k}] {mi.Header} checked={mi.IsChecked} enabled={mi.IsEnabled}");
            }
        }

        if (Shell.ContextMenu is { IsOpen: true } m) m.IsOpen = false;
    }

    /// <summary>
    /// 悬停时把描边加深一档。浅色玻璃上"提亮"等于看不见，所以是加深（黑 12% → 24%）；
    /// 深色玻璃则反过来提亮（白 12% → 14%）。
    ///
    /// <para>
    /// 拖动期间直接返回：<c>DragMove</c> 是模态循环，中途冒出来的 MouseEnter
    /// 会把粉色描边盖成悬停色，看起来像"一拖就变白"。
    /// </para>
    /// </summary>
    private void Shell_MouseEnter(object sender, MouseEventArgs e)
    {
        if (_dragging) return;
        Shell.BorderBrush = _edgeHover;
    }

    private void Shell_MouseLeave(object sender, MouseEventArgs e)
    {
        if (_dragging) return;   // 拖动中偶尔会漏进 Leave（窗口跟着光标走），别把粉色抹掉
        Shell.BorderBrush = _edgeIdle;
    }

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

    /// <summary>排列菜单（M4.10）：Tag 是 <c>row</c> / <c>column</c>，与缩放一样靠代码互斥。</summary>
    private void Layout_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag })
            IsVertical = tag == "column";
    }

    private void Edge_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: string tag }) EdgeMode = tag;
    }

    private void ToggleLock_Click(object sender, RoutedEventArgs e)
    {
        // IsCheckable 的菜单项点下去时 WPF 已经先把 IsChecked 翻过了，这里读到的就是新状态
        Locked = LockMenu.IsChecked;
    }

    private void ToggleThrough_Click(object sender, RoutedEventArgs e)
        => ClickThrough = ThroughMenu.IsChecked;

    private void ToggleTopmost_Click(object sender, RoutedEventArgs e)
        => AlwaysOnTop = TopmostMenu.IsChecked;

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
        _config.FloatVertical = _vertical;
        _config.FloatEdgeMode = _edgeMode;
        _config.TextKeepThreshold = _textKeepThreshold;
        _config.FloatLocked = _locked;
        _config.FloatClickThrough = _clickThrough;
        _config.FloatAlwaysOnTop = _alwaysOnTop;

        // 整张表换掉而不是逐项改：用户在设置页"全部重置"时，旧表里那些项要真的消失，
        // 只改动的几项会留下"上次的色"这种幽灵值。
        _config.FloatFieldColors = FloatInk.NewMap();
        foreach (KeyValuePair<string, string> kv in _fieldColors)
            FloatInk.Set(_config.FloatFieldColors, kv.Key, kv.Value);
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

        // 穿透（M4.21）：穿透时"锁定位置"是**强制**的（鼠标消息根本不投递，拖不动），
        // 所以把它一并勾上并禁用 —— 放用户在穿透状态下解锁，只会得到"解锁了却还是拖不动"。
        if (ThroughMenu is not null) ThroughMenu.IsChecked = _clickThrough;
        if (LockMenu is not null) LockMenu.IsEnabled = !_clickThrough;
        if (TopmostMenu is not null) TopmostMenu.IsChecked = _alwaysOnTop;

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

        // 排列两项：同样是"从当前状态反推勾选"，不另存真值
        if (LayoutRowMenu is not null)
        {
            LayoutRowMenu.IsChecked = !_vertical;
            LayoutColumnMenu.IsChecked = _vertical;
        }

        // 显示字段：真值只在配置里，勾选状态每次从配置反推
        if (FieldMenu is not null)
        {
            foreach (object child in FieldMenu.Items)
            {
                if (child is MenuItem { Tag: string key } item)
                    item.IsChecked = FloatFields.IsVisible(_config, key);
            }
        }

        // 边框三档（M4.15）：同样从当前值反推
        if (EdgeNoneMenu is not null)
        {
            EdgeNoneMenu.IsChecked = _edgeMode == "none";
            EdgeFaintMenu.IsChecked = _edgeMode == "faint";
            EdgeNormalMenu.IsChecked = _edgeMode == "normal";
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Application.Current.Shutdown();
}
