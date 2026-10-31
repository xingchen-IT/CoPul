using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using CoPul.App.Interop;
using CoPul.App.Sdk;
using CoPul.Collector.Providers;

namespace CoPul.App;

/// <summary>
/// 设置页需要的外界信息与动作。App 构造一份传进来，设置页不认识 App 的局部变量。
///
/// <para>
/// 一律用 <c>Func</c> 而不是直接传窗口实例：悬浮窗与任务栏条都是**按需创建**的
/// （用户不切过去就不存在），构造设置页时它们多半还是 <c>null</c>。
/// 传实例会把"此刻有没有"固化成设置页的状态，而用户完全可能在设置页开着的时候
/// 先切到任务栏模式、再回来调字段。
/// </para>
/// </summary>
internal sealed class SettingsContext
{
    /// <summary>共享的配置实例。<b>不要</b>设置页自己 Load 一份（会互相覆盖，见 ConfigStore 注释）。</summary>
    public AppConfig Config { get; init; } = ConfigStore.Current;

    /// <summary>当前视图。切模式时用来避免"点当前模式"这种无意义切换。</summary>
    public Func<FullScreenAutoSwitch.View> CurrentView { get; init; }
        = () => FullScreenAutoSwitch.View.Main;

    /// <summary>切视图。必须走 App 的 ShowView —— 那里同步状态机，直接 Show/Hide 会让全屏回落打架。</summary>
    public Action<FullScreenAutoSwitch.View> ShowView { get; init; } = _ => { };

    public Func<FloatWindow?> GetFloat { get; init; } = () => null;
    public Func<TaskbarWindow?> GetTaskbar { get; init; } = () => null;

    /// <summary>诊断页的数据源。返回采集宿主的六个 Provider。</summary>
    public Func<IReadOnlyList<IMetricProvider>> GetProviders { get; init; }
        = () => Array.Empty<IMetricProvider>();

    /// <summary>当前探测目标（真实生效的那个，可能与配置里的不同）。</summary>
    public Func<string> GetPingTarget { get; init; } = () => OsProvider.DefaultPingTarget;

    /// <summary>改探测目标。落到 OsProvider 上，探针线程下一轮生效。</summary>
    public Action<string> SetPingTarget { get; init; } = _ => { };

    /// <summary>
    /// 当前**真正生效**的数据刷新间隔（ms）。刻意不读配置：实际间隔还要过一遍各指标的 floor，
    /// 这里给的是调度器手里那个数。
    /// </summary>
    public Func<int> GetSampleInterval { get; init; } = () => 500;

    /// <summary>改数据刷新间隔（ms，M4.24）。下一个采样 tick 生效。</summary>
    public Action<int> SetSampleInterval { get; init; } = _ => { };

    /// <summary>全屏自动切换的总开关动作（F-11）。设置页不给 true/false 的猜法，直接调它。</summary>
    public Action<bool> SetFullScreenAutoSwitch { get; init; } = _ => { };

    /// <summary>改防抖档位（ms）。状态机下一轮判定即生效。</summary>
    public Action<int> SetFullScreenHold { get; init; } = _ => { };

    /// <summary>
    /// 当前**真正生效**的热键写法；<c>null</c> = 没注册上（被占用或写法不认识）。
    /// 刻意不读配置：配置里存的是"用户想要的"，冲突时两者并不相同。
    /// </summary>
    public Func<string?> GetHotkey { get; init; } = () => null;

    /// <summary>
    /// 换热键。返回 <c>null</c> 表示成功，否则是给用户看的原因。
    /// 失败时实现方负责把旧热键装回去，设置页不必管回滚。
    /// </summary>
    public Func<string, string?> SetHotkey { get; init; } = _ => "热键不可用";

    /// <summary>
    /// 录键期间暂停全局热键（<c>true</c> = 暂停）。
    /// 不暂停的话，用户在录制框里按下 <c>Ctrl+Alt+C</c> 会被自己先吃掉。
    /// </summary>
    public Action<bool> SuspendHotkey { get; init; } = _ => { };

    public string Version { get; init; } = "";
    public string ConfigPath { get; init; } = ConfigStore.Path;
}

/// <summary>诊断列表里的一行。</summary>
public sealed class DiagRow
{
    public string Name { get; init; } = "";
    public string StatusText { get; init; } = "";
    public Brush BadgeBg { get; init; } = Brushes.Transparent;
    public Brush BadgeFg { get; init; } = Brushes.Gray;
    public string CostText { get; init; } = "";
    public string ReasonText { get; init; } = "";
    public Visibility ReasonVisibility { get; init; } = Visibility.Collapsed;
    public string RemedyText { get; init; } = "";
    public Visibility RemedyVisibility { get; init; } = Visibility.Collapsed;
}

/// <summary>
/// 设置页（G-08）与传感器诊断（G-09 / Q-18）。
///
/// <para>
/// 为什么单独一个窗口而不是在大窗口里加一块：设置是一组低频、互斥、需要静下心看的控件，
/// 混进 0.5s 刷新一次的监控面板里，两个都要让路 —— 面板会为设置项留白，
/// 设置项会被刷新打断。分成独立窗口后，面板保持"只读"这一条性质。
/// </para>
///
/// <para>
/// <b>改动立即生效、立即落盘。</b>没有"确定 / 取消"：所有控件改完就写
/// <see cref="ConfigStore.Current"/> 并通知对应窗口刷新。理由与悬浮窗的
/// <c>PersistAppearance</c> 相同 —— 设置页被任务管理器结束、或程序崩掉时，
/// 用户刚调好的东西不该丢。取消按钮反而要额外维护"改前的快照"，得不偿失。
/// </para>
/// </summary>
public partial class SettingsWindow : Window
{
    private readonly SettingsContext _ctx;
    private readonly AppConfig _config;

    /// <summary>控件初始化期间置 false：XAML 里设 IsChecked 会触发 Checked，那不能当成用户操作。</summary>
    private bool _loaded;

    /// <summary>代码内部改控件状态时的重入锁（比如拒绝关闭最后一个字段后把勾恢复）。</summary>
    private bool _suppress;

    /// <summary>探针改文字色之前的原值（整张表序列化成 <c>fps:#FFF,cpu:#000</c>）。非空 = 改过。</summary>
    private string? _probeTextColorBackup;

    /// <summary>探针改**任务栏**配色之前的原值。与上面那份分开存：两组表互不相干。</summary>
    private string? _probeTrayColorBackup;

    /// <summary>探针改阈值开关之前的原值。</summary>
    private bool? _probeThrBackup;

    internal SettingsWindow(SettingsContext ctx)
    {
        _ctx = ctx;
        _config = ctx.Config;

        InitializeComponent();

        InitFieldGrid();
        InitFloatFieldGrid();
        InitTextSwatches();
        InitTextFieldBar();
        LoadFromConfig();
        LoadDiag();

        VersionText.Text = string.IsNullOrWhiteSpace(ctx.Version) ? "版本未知" : ctx.Version;

        // 底栏常驻"刚刚做了什么"的反馈，配置文件路径挪到按钮的 ToolTip ——
        // 一行里塞不下两样，而路径绝大多数时候只是"我想知道它在哪"，悬停一次就够。
        OpenConfigButton.ToolTip = ctx.ConfigPath;

        // 自启状态要问一次计划任务（COM），放在 Loaded 之后 —— 构造函数里同步等
        // 会让窗口晚几十毫秒才出现，用户看到的是"点了设置没反应"。
        Loaded += (_, _) =>
        {
            bool auto = AutoStart.IsEnabled();
            _suppress = true;
            AutoStartSwitch.IsChecked = auto;
            _suppress = false;
            AutoStartSwitch.IsEnabled = true;
        };
        AutoStartSwitch.IsEnabled = false;

        // 录到一半直接关窗（Alt+F4 / 点 ✕）时 LostKeyboardFocus 不一定会来，
        // 热键就会一直停在"被摘掉"的状态 —— 表现为"关掉设置页以后热键彻底失灵"。
        Closed += (_, _) =>
        {
            if (_recordingHotkey) EndHotkeyRecording();
        };

        _loaded = true;
    }

    // =================================================================
    // 初始化
    // =================================================================

    /// <summary>
    /// 九个字段的勾选框按 <see cref="TrayFields.Keys"/> 的顺序生成。
    /// 不写死在 XAML 里：字段表以后加一项（比如显存占用）时，这里一行都不用改。
    /// </summary>
    private void InitFieldGrid()
    {
        Style style = (Style)FindResource("FieldCheck");

        for (int i = 0; i < TrayFields.Keys.Length; i++)
        {
            var box = new CheckBox
            {
                Style = style,
                Content = TrayFields.Labels[i],
                Tag = TrayFields.Keys[i],
            };
            box.Checked += Field_Changed;
            box.Unchecked += Field_Changed;
            FieldGrid.Children.Add(box);
        }
    }

    /// <summary>
    /// 悬浮窗六个字段的勾选框（M4.14）。同样按 <see cref="FloatFields.Keys"/> 生成 ——
    /// 菜单、设置页、落盘用的 hidden 列表三处必须认同一个顺序，写死两份迟早勾错。
    /// </summary>
    private void InitFloatFieldGrid()
    {
        Style style = (Style)FindResource("FieldCheck");

        for (int i = 0; i < FloatFields.Keys.Length; i++)
        {
            var box = new CheckBox
            {
                Style = style,
                Content = FloatFields.Labels[i],
                Tag = FloatFields.Keys[i],
            };
            box.Checked += FloatField_Changed;
            box.Unchecked += FloatField_Changed;
            FloatFieldGrid.Children.Add(box);
        }
    }

    /// <summary>
    /// 文字颜色的色板（M4.19）：「自动」+ <see cref="FloatInk.Presets"/>。
    ///
    /// <para>
    /// 按表生成而不是在 XAML 里写九个方块：加一个预设色只改 <c>FloatInk</c> 一处，
    /// 设置页与悬浮窗认的是同一张表（同 <see cref="FloatFields"/> 那套取舍）。
    /// </para>
    /// </summary>
    private void InitTextSwatches()
    {
        Style style = (Style)FindResource("Swatch");

        var auto = new RadioButton
        {
            Style = style,
            GroupName = "textcolor",
            Content = "自动",
            Tag = FloatInk.Auto,
            Background = (Brush)FindResource("BrushPanel"),
            // 这一格要写两个字，32 的固定宽会把第二个字挤到框外（实测只露出半个"自"）
            BorderBrush = (Brush)FindResource("BrushTextTertiary"),   // 比卡片底色亮一档，否则它就是一个看不出的深块
            Width = double.NaN,
            Padding = new Thickness(9, 3, 9, 3),   // 固定宽去掉后，左右留点空，"自动"才不贴着边框
            ToolTip = "跟随玻璃主题：浅色玻璃用深字、深色玻璃用浅字",
        };
        auto.Checked += TextSwatch_Checked;
        TextSwatches.Children.Add(auto);

        foreach ((string label, string hex) in FloatInk.Presets)
        {
            var chip = new RadioButton
            {
                Style = style,
                GroupName = "textcolor",
                Tag = hex,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                ToolTip = hex,
            };
            chip.Checked += TextSwatch_Checked;
            TextSwatches.Children.Add(chip);
        }
    }

    private void LoadFromConfig()
    {
        _suppress = true;

        switch (_ctx.CurrentView())
        {
            case FullScreenAutoSwitch.View.Float: ModeFloat.IsChecked = true; break;
            case FullScreenAutoSwitch.View.Taskbar: ModeTaskbar.IsChecked = true; break;
            default: ModeMain.IsChecked = true; break;
        }

        foreach (object child in FieldGrid.Children)
        {
            if (child is CheckBox { Tag: string key } box)
                box.IsChecked = TrayFields.IsVisible(_config, key);
        }

        foreach (object child in FloatFieldGrid.Children)
        {
            if (child is CheckBox { Tag: string key } box)
                box.IsChecked = FloatFields.IsVisible(_config, key);
        }

        GlassSwitch.IsChecked = _config.FloatGlassEnabled;
        UpdateLevelText();

        switch (NormalizeScale(_config.FloatScale))
        {
            case 125: Scale125.IsChecked = true; break;
            case 150: Scale150.IsChecked = true; break;
            default: Scale100.IsChecked = true; break;
        }

        // 排列（M4.10）：以**当前窗口**为准而不是以配置为准 —— 配置可能被手改过，
        // 而用户看到的是窗口真实的样子，设置页得跟它一致。
        (IsFloatVertical() ? LayoutColumn : LayoutRow).IsChecked = true;

        // 边框（M4.15）：与排列同一套取舍，以当前窗口为准
        switch (CurrentEdgeMode())
        {
            case "none": EdgeNone.IsChecked = true; break;
            case "faint": EdgeFaint.IsChecked = true; break;
            default: EdgeNormal.IsChecked = true; break;
        }

        // 文字颜色（M4.20）：默认编辑悬浮窗第一格，选中态由字段条自己带
        (_inkView == "tray" ? InkViewTray : InkViewFloat).IsChecked = true;
        SyncTextColor(CurrentFieldColor());
        TextThresholdSwitch.IsChecked = CurrentTextKeepThreshold();

        // 悬浮窗交互（M4.21）
        SyncFloatInteract();

        switch (_config.TaskbarInk)
        {
            case "Light": InkLight.IsChecked = true; break;
            case "Dark": InkDark.IsChecked = true; break;
            default: InkAuto.IsChecked = true; break;
        }

        ThroughSwitch.IsChecked = _config.TaskbarClickThrough;
        FullScreenSwitch.IsChecked = _config.FullScreenAutoSwitch;

        // 防抖档位（M4.23）。配置里的值可能不在档位表上（手改过配置）→ 先归一到最接近的档，
        // 否则一个 1500 会让四个按钮一个都不亮，用户看到的是"当前档位未知"。
        SelectHoldSegment(FullScreenAutoSwitch.NormalizeHold(_config.FullScreenHoldMs));

        // 刷新间隔（M4.24）。同样先归一；选中态以"调度器真正生效的那个数"为准，
        // 这样手改配置写了非法值时 UI 显示的是实际在跑的档位，而不是用户以为的。
        SelectSampleSegment(CollectorHost.NormalizeInterval(_ctx.GetSampleInterval()));

        PingBox.Text = _ctx.GetPingTarget();

        _suppress = false;
        UpdateFieldEnabled();
        UpdateFloatFieldEnabled();
        ShowHotkey();       // 问一次"真正注册上的"热键，冲突时这里会显示"未生效"
    }

    // =================================================================
    // 常规页
    // =================================================================

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        // 双击标题栏最大化 / 还原，与系统窗口一致
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        try { DragMove(); }
        catch (InvalidOperationException) { /* 拖拽中窗口被关掉，忽略 */ }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>自测用：直接切到诊断页（<c>--settings-page diag</c>），省掉一次模拟点击。</summary>
    internal void ShowDiagTab()
    {
        if (NavDiag is not null) NavDiag.IsChecked = true;
    }

    // =================================================================
    // 窗口外观
    // =================================================================

    /// <summary>圆角半径（DIP）。与 XAML 里外层 Border 的 CornerRadius 必须一致，否则描边会露角。</summary>
    private const double CornerRadiusDip = 8;

    /// <summary>true = 已交给 Win11 的 DWM 处理圆角，不用再自己维护窗口 Region。</summary>
    private bool _dwmRoundCorners;

    private IntPtr _hwnd;

    /// <summary>
    /// 让窗口四角真的变圆（与主窗口同一套做法）。
    ///
    /// <para>
    /// <b>为什么 XAML 里那个 <c>CornerRadius="8"</c> 不够</b>：<c>Window.Background</c> 是
    /// 不透明的 <c>BrushBg</c>，它铺满**整个客户区矩形**，四角那两块底色会把 Border
    /// 画出来的圆角盖掉 —— 屏幕上看到的仍然是一个方窗口。
    /// </para>
    ///
    /// <para>
    /// 两条路：Win11 走 <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>，系统按当前 DPI 裁切并
    /// 抗锯齿，最大化时还会自动恢复直角；Win10 没有这个属性，退到
    /// <see cref="Win32Window.ApplyRoundRegion"/>（1 位掩码、无抗锯齿，但可用），
    /// 由尺寸变化与状态切换维持。
    /// </para>
    /// </summary>
    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new WindowInteropHelper(this).Handle;

        _dwmRoundCorners = Win32Window.TryEnableDwmRoundCorners(_hwnd);
        Interop.Trace.Log("settings", $"round: dwm={_dwmRoundCorners} hwnd=0x{_hwnd:X}");

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

    /// <summary>
    /// 自测：<c>--settings-probe "field:pwr,ping:1.1.1.1,level:+,mode:float"</c>。
    ///
    /// <para>
    /// 走的全是控件自己的事件路径（翻 <c>IsChecked</c> 会触发 Checked/Unchecked → 同一份
    /// 处理函数），不是为了测试另写一套影子逻辑。为什么要探针而不是模拟鼠标：
    /// 本机 <c>SetCursorPos</c> 不可靠（M4.1 起就如此），点歪了会以为功能坏了。
    /// </para>
    ///
    /// <para>
    /// 要验的核心是"改完设置，别的窗口真的跟着变"这一环 —— 配置写没写进去看
    /// <c>config.json</c> 就知道，但"条有没有重新排版""悬浮窗有没有换浓度"只能看这里。
    /// </para>
    /// </summary>
    internal void RunProbe(string commands)
    {
        foreach (string raw in commands.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] kv = raw.Split(':', 2);
            string key = kv[0].Trim();
            string value = kv.Length > 1 ? kv[1].Trim() : string.Empty;

            switch (key)
            {
                case "field":
                    foreach (object child in FieldGrid.Children)
                    {
                        if (child is not CheckBox box || (string)box.Tag != value) continue;
                        box.IsChecked = box.IsChecked != true;   // 触发 apply
                        break;
                    }
                    break;

                case "floatfield":
                    // 悬浮窗字段（M4.14）：<c>floatfield:ram</c> 翻一下「内存」。
                    foreach (object child in FloatFieldGrid.Children)
                    {
                        if (child is not CheckBox box || (string)box.Tag != value) continue;
                        box.IsChecked = box.IsChecked != true;   // 触发 apply
                        break;
                    }
                    break;

                case "floatfields":
                    // 自测：把「悬浮窗字段」整张表打印出来（顺序 / 勾选 / 是否置灰）——
                    // 设置页卡片在截图里常被底部截断，靠肉眼数不准。
                    foreach (object child in FloatFieldGrid.Children)
                    {
                        if (child is not CheckBox box) continue;
                        System.Console.WriteLine(
                            $"floatfield: [{box.Tag}] {box.Content} checked={box.IsChecked} enabled={box.IsEnabled}");
                    }
                    System.Console.WriteLine($"floatfield-count: {FloatFieldGrid.Children.Count}");
                    break;

                case "ping":
                    PingBox.Text = value;
                    ApplyPing();
                    break;

                case "level":
                    StepLevel(value == "-" ? -1 : +1);
                    break;

                case "scale":
                    if (int.TryParse(value, out int pct))
                        (pct >= 150 ? Scale150 : pct >= 125 ? Scale125 : Scale100).IsChecked = true;
                    break;

                case "layout":
                    (value == "v" || value == "column" ? LayoutColumn : LayoutRow).IsChecked = true;
                    break;

                case "ink":
                    (value switch
                    {
                        "light" => InkLight,
                        "dark" => InkDark,
                        _ => InkAuto,
                    }).IsChecked = true;
                    break;

                case "through":
                    ThroughSwitch.IsChecked = value != "0";
                    break;

                case "glass":
                    GlassSwitch.IsChecked = value != "0";
                    break;

                case "fullscreen":
                    FullScreenSwitch.IsChecked = value != "0";
                    break;

                case "fshold":
                    // 防抖档位（M4.23）。两个作用：
                    //   ① 把档位点成指定值（等价于用户点了那个分段按钮，走同一条 Checked 路径）
                    //   ② 把四个按钮的选中状态打到 stdout —— 常规页比窗口高，截图里这一块常被截断。
                    {
                        if (int.TryParse(value, out int hms))
                            SelectHoldSegment(FullScreenAutoSwitch.NormalizeHold(hms));

                        System.Console.WriteLine($"fshold cfg: hold={_config.FullScreenHoldMs}");
                        foreach (RadioButton chip in new[] { FsHold500, FsHold1000, FsHold2000, FsHold3000 })
                            System.Console.WriteLine($"fshold: [{chip.Tag}] '{chip.Content}' checked={chip.IsChecked}");
                    }
                    break;

                case "interval":
                    // 数据刷新间隔（M4.24）。与 fshold 同款：点成指定档 + 把五个按钮打到 stdout。
                    // 走 SelectSampleSegment（只改选中态）而不是直接调 SetSampleInterval，
                    // 是为了和用户点击走完全同一条路径（Checked → 落盘 → 通知采集器）。
                    {
                        if (int.TryParse(value, out int ims))
                            SelectSampleSegment(CollectorHost.NormalizeInterval(ims));

                        System.Console.WriteLine($"interval cfg: {_config.SampleIntervalMs} " +
                                                 $"live={_ctx.GetSampleInterval()}");
                        foreach (RadioButton chip in new[] { Sample500, Sample1000, Sample2000, Sample3000, Sample5000 })
                            System.Console.WriteLine($"interval: [{chip.Tag}] '{chip.Content}' checked={chip.IsChecked}");
                    }
                    break;

                case "mode":
                    (value switch
                    {
                        "float" => ModeFloat,
                        "taskbar" => ModeTaskbar,
                        _ => ModeMain,
                    }).IsChecked = true;
                    break;

                case "capture":
                    // 模拟"正在录制时按下这组键"：走 PreviewKeyDown 之后的同一段逻辑，
                    // 包括摘热键、组合、校验、注册、落盘、重画。
                    if (TryParseProbeKey(value, out Key probeKey, out ModifierKeys probeMods))
                    {
                        BeginHotkeyRecording();
                        HandleHotkeyKey(probeKey, probeMods);
                        EndHotkeyRecording();
                    }
                    break;

                case "hotkey":
                    // 走"录完之后"的同一条路，不模拟按键：本机 SetCursorPos 不可靠，
                    // 而且这里要验的是"改键 → 注册 → 落盘"三件事连起来的效果。
                    // 注意这段跑在打开设置页 1.5s 后，那时全局热键已经注册过一次了。
                    ApplyHotkey(value);
                    break;

                case "diag":
                    ShowDiagTab();
                    break;

                case "textcolor":
                    // 文字颜色（M4.20）：<c>textcolor:fps:#FF7FB8;cpu:#00E5FF</c>（分号分隔 ——
                    // 逗号被命令串自己占了），也可以只写一个色（<c>textcolor:#FF7FB8</c>）
                    // = 当前视图九格一起设，<c>textcolor:auto</c> = 当前格改回自动。
                    BeginTextProbeBackup();
                    ApplyTextColorPairs(value);
                    break;

                case "textview":
                    // 切到哪个视图再改：<c>textview:tray</c>
                    InkViewTray.IsChecked = value == "tray";
                    InkViewFloat.IsChecked = value != "tray";
                    _inkView = value == "tray" ? "tray" : "float";
                    _inkField = InkKeys()[0];
                    InitTextFieldBar();
                    SyncTextColor(CurrentFieldColor());
                    break;

                case "textfield":
                    // 选中某个字段：<c>textfield:ram</c>
                    foreach (object child in TextFieldBar.Children)
                    {
                        if (child is RadioButton { Tag: string k } chip && k == value) chip.IsChecked = true;
                    }
                    break;

                case "textthr":
                    // 阈值色开关：<c>textthr:0</c> = 整条一个颜色
                    BeginTextProbeBackup();
                    TextThresholdSwitch.IsChecked = value != "0";
                    break;

                case "floatlock":
                    // 悬浮窗交互（M4.21）：<c>floatlock:1</c> / <c>floatthrough:1</c> / <c>floattopmost:0</c>
                    BeginInteractProbeBackup();
                    FloatLockSwitch.IsChecked = value is "1" or "true" or "on";
                    break;

                case "floatthrough":
                    BeginInteractProbeBackup();
                    FloatThroughSwitch.IsChecked = value is "1" or "true" or "on";
                    break;

                case "floattopmost":
                    BeginInteractProbeBackup();
                    FloatTopmostSwitch.IsChecked = !(value is "0" or "false" or "off");
                    break;

                case "floatinteract":
                    // 三个开关 + 窗口实际状态（截图里这块常被截断，靠肉眼数不准）
                    FloatWindow? fi = _ctx.GetFloat();
                    System.Console.WriteLine($"floatinteract: lock={FloatLockSwitch.IsChecked} " +
                                             $"through={FloatThroughSwitch.IsChecked} " +
                                             $"topmost={FloatTopmostSwitch.IsChecked} " +
                                             $"lockEnabled={FloatLockSwitch.IsEnabled}");
                    System.Console.WriteLine($"floatinteract: window={(fi is null ? "(未开)" : fi.InteractDiag)}");
                    break;

                case "textcolors":
                    // 全表打印（截图里这块常被截断，靠肉眼数不准）
                    foreach (object child in TextSwatches.Children)
                    {
                        if (child is not RadioButton chip) continue;
                        System.Console.WriteLine($"textswatch: [{chip.Tag}] '{chip.Content}' " +
                                                 $"checked={chip.IsChecked} bg={chip.Background}");
                    }
                    System.Console.WriteLine($"textview: {_inkView} field={_inkField} " +
                                             $"hexBox='{TextHexBox.Text}' thr={TextThresholdSwitch.IsChecked}");
                    System.Console.WriteLine("textdump float: " + DumpColors(false));
                    System.Console.WriteLine("textdump tray : " + DumpColors(true));
                    System.Console.WriteLine($"textpreview: darkMain={PreviewValueDark.Foreground} " +
                                             $"darkSub={PreviewLabelDark.Foreground} " +
                                             $"lightMain={PreviewValueLight.Foreground} " +
                                             $"lightSub={PreviewLabelLight.Foreground}");
                    break;

                case "scroll":
                    // 只给截图用：常规页比窗口高，下半部分（采集与网络 / 启动）不看一眼就没验过
                    if (double.TryParse(value, out double offset))
                        GeneralPage.ScrollToVerticalOffset(offset);
                    break;
            }

            Interop.Trace.Log("settings", $"probe {key}:{value}");
        }
    }

    private void Nav_Checked(object sender, RoutedEventArgs e)
    {
        if (GeneralPage is null || DiagPage is null) return;

        bool diag = sender is RadioButton { Tag: "diag" };
        GeneralPage.Visibility = diag ? Visibility.Collapsed : Visibility.Visible;
        DiagPage.Visibility = diag ? Visibility.Visible : Visibility.Collapsed;

        // 切到诊断页时重读一次：Providers 的状态是采集线程在改的，
        // 打开设置页那一刻的快照不该一直留着（FPS 往往要几秒才接上）。
        if (diag && _loaded) LoadDiag();
    }

    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        FullScreenAutoSwitch.View target = tag switch
        {
            "float" => FullScreenAutoSwitch.View.Float,
            "taskbar" => FullScreenAutoSwitch.View.Taskbar,
            _ => FullScreenAutoSwitch.View.Main,
        };

        if (_ctx.CurrentView() == target) return;
        _ctx.ShowView(target);

        // 切回大窗口时关掉设置页：两个窗口都是 800 px 宽、都居中，会严丝合缝地重叠，
        // 用户只会看到"点了没反应"。切到悬浮窗/任务栏条时不关 —— 那两个是边缘小窗，
        // 用户本来就是想边看边调。
        if (target == FullScreenAutoSwitch.View.Main) Close();
    }

    private void Field_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        ApplyFields();
    }

    /// <summary>
    /// 把八个勾选框的当前状态写回配置。
    ///
    /// <para>
    /// 不用 <see cref="TrayFields.Toggle"/>：那个是"翻转一个 key"，而这里一次可能同时变多项
    /// （用户连点）。按整份勾选状态重建 hidden 数组更直接，也不会和条上的右键菜单打架 ——
    /// 两边最终都落在同一份 <see cref="AppConfig.TaskbarHiddenFields"/> 上。
    /// </para>
    /// </summary>
    private void ApplyFields()
    {
        var hidden = new List<string>(TrayFields.Keys.Length);

        foreach (object child in FieldGrid.Children)
        {
            if (child is CheckBox { IsChecked: false } box) hidden.Add((string)box.Tag);
        }

        int visible = TrayFields.Keys.Length - hidden.Count;
        if (visible <= 0)
        {
            // 全关之后条会被夹到 1 px —— 与其画一根发丝，不如拒绝。
            //
            // 回滚必须"整份按配置重同步"，不能只把某一个框勾回去：一次操作可能同时
            // 涉及多项（用户连点、探针连发），而代码无法知道"哪一个才是刚关的"。
            // 第一版记了个 lastOff，结果是"遍历到的最后一个未勾选框"—— 只剩 FPS 时
            // 关 FPS，被勾回去的是最右边的「整机功耗」（探针实测抓到）。
            _suppress = true;
            foreach (object child in FieldGrid.Children)
            {
                if (child is CheckBox { Tag: string key } box)
                    box.IsChecked = TrayFields.IsVisible(_config, key);
            }
            _suppress = false;

            Hint("至少要保留一个字段");
            UpdateFieldEnabled();
            return;
        }

        _config.TaskbarHiddenFields = hidden.ToArray();
        ConfigStore.SaveCurrent();

        // 条可能还没建（用户没进过任务栏模式）—— 那就什么都不用刷，
        // 它创建时会从配置里读到这份 hidden。
        _ctx.GetTaskbar()?.ReloadFields();
        UpdateFieldEnabled();

        Hint($"字段已更新：{visible}/{TrayFields.Keys.Length} 显示");
    }

    /// <summary>只剩一个可见字段时把它置灰 —— 让"不能再关"是看得见的，而不是点了没反应。</summary>
    private void UpdateFieldEnabled()
    {
        int visible = 0;
        CheckBox? only = null;

        foreach (object child in FieldGrid.Children)
        {
            if (child is CheckBox { IsChecked: true } box)
            {
                visible++;
                only = box;
            }
        }

        _suppress = true;
        foreach (object child in FieldGrid.Children)
        {
            if (child is CheckBox box) box.IsEnabled = !(visible <= 1 && ReferenceEquals(box, only));
        }
        _suppress = false;
    }

    // ---- 悬浮窗字段（M4.14）----

    /// <summary>
    /// 悬浮窗字段勾选。与任务栏那套同一套写法，区别只有两个落点：
    /// 写 <see cref="AppConfig.FloatHiddenFields"/>，以及让窗口（如果开着）立刻重排。
    /// </summary>
    private void FloatField_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not CheckBox { Tag: string key } box) return;

        string[] old = _config.FloatHiddenFields ?? Array.Empty<string>();

        // 不是"翻一下"而是"按控件当前状态写一遍"：连点时 WPF 已经把 IsChecked 定好了，
        // 以它为准比再算一次反状态可靠。
        var hidden = new List<string>(old);
        hidden.RemoveAll(h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase));
        if (box.IsChecked != true) hidden.Add(key);
        _config.FloatHiddenFields = hidden.ToArray();

        if (FloatFields.VisibleCount(_config) <= 0)
        {
            // 全关之后条宽会归零、窗口被夹成一根发丝。回滚必须"整份按配置重同步"，
            // 不能只把手里这个框勾回去 —— 与任务栏那边踩过的是同一个坑。
            _config.FloatHiddenFields = old;

            _suppress = true;
            foreach (object child in FloatFieldGrid.Children)
            {
                if (child is CheckBox { Tag: string k } b)
                    b.IsChecked = FloatFields.IsVisible(_config, k);
            }
            _suppress = false;

            Hint("至少要保留一个字段");
            UpdateFloatFieldEnabled();
            return;
        }

        ConfigStore.SaveCurrent();

        // 窗口没开就什么都不用刷 —— 它创建时会从配置里读到这份 hidden。
        _ctx.GetFloat()?.ReloadFields();
        UpdateFloatFieldEnabled();

        Hint($"悬浮窗字段已更新：{FloatFields.VisibleCount(_config)}/{FloatFields.Keys.Length} 显示");
    }

    /// <summary>只剩一个可见字段时置灰最后那项（同任务栏）。</summary>
    private void UpdateFloatFieldEnabled()
    {
        int visible = 0;
        CheckBox? only = null;

        foreach (object child in FloatFieldGrid.Children)
        {
            if (child is CheckBox { IsChecked: true } box)
            {
                visible++;
                only = box;
            }
        }

        _suppress = true;
        foreach (object child in FloatFieldGrid.Children)
        {
            if (child is CheckBox box) box.IsEnabled = !(visible <= 1 && ReferenceEquals(box, only));
        }
        _suppress = false;
    }

    // ---- 悬浮窗外观 ----

    private void GlassSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        bool on = GlassSwitch.IsChecked == true;
        FloatWindow? f = _ctx.GetFloat();

        if (f is not null)
        {
            // 走窗口的属性而不是直接改配置：窗口自己会重画玻璃并落盘，
            // 两处都写的话会出现"配置说开、窗口还是关的"这种分叉。
            f.GlassEnabled = on;
        }
        else
        {
            _config.FloatGlassEnabled = on;
            ConfigStore.SaveCurrent();
        }

        Hint(on ? "毛玻璃已开启（下次显示悬浮窗时生效）" : "毛玻璃已关闭");
    }

    private void LevelUp_Click(object sender, RoutedEventArgs e) => StepLevel(+1);

    private void LevelDown_Click(object sender, RoutedEventArgs e) => StepLevel(-1);

    private void StepLevel(int delta)
    {
        int next = Math.Clamp(_config.FloatGlassLevel + delta, 1, 7);

        FloatWindow? f = _ctx.GetFloat();
        if (f is not null)
        {
            f.GlassLevel = next;
            _config.FloatGlassLevel = f.GlassLevel;   // 窗口会夹取，以它的值为准
        }
        else
        {
            _config.FloatGlassLevel = next;
            ConfigStore.SaveCurrent();
        }

        UpdateLevelText();
        Hint($"玻璃浓度 {_config.FloatGlassLevel}/7");
    }

    private void UpdateLevelText()
    {
        int level = Math.Clamp(_config.FloatGlassLevel, 1, 7);
        LevelText.Text = level + " / 7";

        LevelDown.IsEnabled = level > 1;
        LevelUp.IsEnabled = level < 7;
    }

    private void Scale_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string tag } || !int.TryParse(tag, out int percent)) return;

        FloatWindow? f = _ctx.GetFloat();
        if (f is not null)
        {
            f.ScalePercent = percent;
            _config.FloatScale = f.ScalePercent;
        }
        else
        {
            _config.FloatScale = percent;
            ConfigStore.SaveCurrent();
        }

        Hint($"悬浮窗缩放 {percent}%（下次显示时生效）");
    }

    /// <summary>
    /// 排列（M4.10）：横向 / 竖向。与缩放同一套写法 —— 悬浮窗开着就直接改，
    /// 没开就只落盘，下次显示时按配置生效。
    /// </summary>
    private void Layout_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        bool vertical = tag == "column";

        FloatWindow? f = _ctx.GetFloat();
        if (f is not null)
        {
            f.IsVertical = vertical;
            _config.FloatVertical = f.IsVertical;
        }
        else
        {
            _config.FloatVertical = vertical;
            ConfigStore.SaveCurrent();
        }

        Hint(vertical ? "悬浮窗改为竖向排列" : "悬浮窗改为横向排列");
    }

    /// <summary>当前悬浮窗是否竖向排列；窗口没开着就看配置。</summary>
    private bool IsFloatVertical() => _ctx.GetFloat()?.IsVertical ?? _config.FloatVertical;

    /// <summary>当前边框档位；窗口没开着就看配置（与 <see cref="IsFloatVertical"/> 同一套取舍）。</summary>
    private string CurrentEdgeMode() => _ctx.GetFloat()?.EdgeMode ?? _config.FloatEdgeMode;

    /// <summary>
    /// 边框（M4.15）：静止时描边的浓度。与排列同一套写法 ——
    /// 悬浮窗开着就直接改，没开就只落盘。
    /// </summary>
    private void Edge_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        FloatWindow? f = _ctx.GetFloat();
        if (f is not null)
        {
            f.EdgeMode = tag;
            _config.FloatEdgeMode = f.EdgeMode;
        }
        else
        {
            _config.FloatEdgeMode = tag;
            ConfigStore.SaveCurrent();
        }

        Hint(tag switch
        {
            "none" => "边框已关闭（悬停与拖动时仍会显示）",
            "faint" => "边框改为「淡」",
            _ => "边框改为「明显」",
        });
    }

    // ---- 文字颜色（M4.19 整条一个色 → M4.20 逐字段，悬浮窗 / 任务栏各一套）----

    /// <summary>当前正在编辑哪个视图（<c>float</c> / <c>tray</c>）。</summary>
    private string _inkView = "float";

    /// <summary>当前视图的字段名表。两视图的字段并不一样（<c>net</c> vs <c>lat</c>）。</summary>
    private string[] InkKeys() => _inkView == "tray" ? TrayFields.Keys : FloatFields.Keys;

    private string[] InkLabels() => _inkView == "tray" ? TrayFields.Labels : FloatFields.Labels;

    /// <summary>当前正在编辑的字段。切视图时落到该视图的第一项。</summary>
    private string _inkField = FloatFields.Keys[0];

    /// <summary>当前视图 + 当前字段的配色写法；窗口开着就以窗口为准（与排列 / 边框同一套取舍）。</summary>
    private string CurrentFieldColor()
    {
        if (_inkView == "tray")
        {
            TaskbarWindow? bar = _ctx.GetTaskbar();
            return bar is not null ? bar.FieldColor(_inkField) : FloatInk.Get(_config.TrayFieldColors, _inkField);
        }

        FloatWindow? f = _ctx.GetFloat();
        return f is not null ? f.FieldColor(_inkField) : FloatInk.Get(_config.FloatFieldColors, _inkField);
    }

    private bool CurrentTextKeepThreshold()
    {
        FloatWindow? f = _ctx.GetFloat();
        return f?.TextKeepThreshold ?? _config.TextKeepThreshold;
    }

    /// <summary>
    /// 字段选择条：九个字段各一格，**字用它自己的颜色**显示。
    ///
    /// <para>
    /// 为什么要染色：九个格子里只改了三格时，靠"我记得改过哪几个"是靠不住的，
    /// 而色板一次只能反映一格的状态。让字段名自己呈现当前色，改了哪几格一眼可见。
    /// 「自动」保持默认字色 —— 那正是"没改过"的意思。
    /// </para>
    /// </summary>
    private void InitTextFieldBar()
    {
        TextFieldBar.Children.Clear();

        string[] keys = InkKeys();
        string[] labels = InkLabels();
        Brush normal = (Brush)FindResource("BrushTextPrimary");

        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            var chip = new RadioButton
            {
                Style = (Style)FindResource("FieldChip"),
                GroupName = "inkfield",
                Content = labels[i],
                Tag = key,
                IsChecked = key == _inkField,
                ToolTip = $"{labels[i]}（{key}）",
            };
            chip.Checked += TextField_Checked;
            PaintFieldChip(chip, key, normal);
            TextFieldBar.Children.Add(chip);
        }
    }

    /// <summary>把一格字段条染成它当前的颜色。</summary>
    private void PaintFieldChip(RadioButton chip, string key, Brush normal)
    {
        string hex = _inkView == "tray"
            ? (_ctx.GetTaskbar()?.FieldColor(key) ?? FloatInk.Get(_config.TrayFieldColors, key))
            : (_ctx.GetFloat()?.FieldColor(key) ?? FloatInk.Get(_config.FloatFieldColors, key));

        chip.Foreground = FloatInk.IsAuto(hex) ? normal : FloatInk.ToBrush(hex) ?? normal;
        chip.ToolTip = FloatInk.IsAuto(hex) ? $"{chip.Content}（{key}）：自动" : $"{chip.Content}（{key}）：{hex}";
    }

    /// <summary>改完一个字段后刷新整条（只重染色，不重建 —— 重建会把选中态抖一下）。</summary>
    private void RefreshFieldBar()
    {
        Brush normal = (Brush)FindResource("BrushTextPrimary");
        foreach (object child in TextFieldBar.Children)
        {
            if (child is RadioButton { Tag: string key } chip) PaintFieldChip(chip, key, normal);
        }
    }

    private void InkView_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        _inkView = tag;
        _inkField = InkKeys()[0];

        InitTextFieldBar();
        SyncTextColor(CurrentFieldColor());
        Hint(_inkView == "tray" ? "正在编辑：任务栏条的文字颜色" : "正在编辑：悬浮窗的文字颜色");
    }

    private void TextField_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string key }) return;

        _inkField = key;
        SyncTextColor(CurrentFieldColor());
    }

    /// <summary>
    /// 把色板 / 输入框 / 预览三处对齐到 <paramref name="hex"/>。
    ///
    /// <para>
    /// 自定义色（不在预设表里）时<b>一个都不选</b>：色板表示"预设"，
    /// 硬把最接近的那格勾上会让用户以为"我选的就是这个色"。
    /// 代码可以把一整组 RadioButton 都置 false —— 只有鼠标点击才不允许取消。
    /// </para>
    /// </summary>
    private void SyncTextColor(string? hex)
    {
        string normalized = FloatInk.Normalize(hex);

        _suppress = true;
        foreach (object child in TextSwatches.Children)
        {
            if (child is RadioButton { Tag: string tag } chip)
                chip.IsChecked = string.Equals(tag, normalized, StringComparison.OrdinalIgnoreCase);
        }
        _suppress = false;

        if (TextHexBox.Text != normalized) TextHexBox.Text = normalized;
        UpdateTextPreview(normalized);
    }

    /// <summary>
    /// 探针改文字色 / 阈值开关之前的备份（与 <c>FloatWindow.ProbeToggle</c> 同一套）。
    ///
    /// <para>
    /// 为什么必须还原：这里改的是共享的那份 <see cref="AppConfig"/>，
    /// 而主窗口关闭时也会整份落盘 —— 跑一次 <c>textcolor:fps=#FF7FB8</c> 就会把用户的
    /// 配色永久改掉，之后每次"默认长什么样"的验收量到的都是上次探针留的色。
    /// 挂 <see cref="Application.Exit"/> 是因为它比任何窗口的 <c>Closing</c> 都晚，
    /// 能盖掉"窗口关闭时顺手写盘"那一次。
    /// </para>
    /// </summary>
    /// <summary>
    /// 探针改「悬浮窗交互」三项之前的备份。与文字色那份同一套理由：
    /// 这里改的是共享的 <see cref="AppConfig"/>，而主窗口关闭时也会整份落盘 ——
    /// 跑一次 <c>floatthrough:1</c> 就把用户的悬浮窗永久设成穿透，那是最难自查的一种污染
    /// （表现为"鼠标点不到悬浮窗"，用户不会想到是上次跑过探针）。
    /// </summary>
    private (bool locked, bool through, bool topmost)? _probeInteractBackup;

    private void BeginInteractProbeBackup()
    {
        if (_probeInteractBackup is not null) return;

        _probeInteractBackup = (_config.FloatLocked, _config.FloatClickThrough, _config.FloatAlwaysOnTop);
        Application.Current.Exit += (_, _) => RestoreInteractProbe();
    }

    private void RestoreInteractProbe()
    {
        if (_probeInteractBackup is not { } backup) return;

        _config.FloatLocked = backup.locked;
        _config.FloatClickThrough = backup.through;
        _config.FloatAlwaysOnTop = backup.topmost;
        _probeInteractBackup = null;
        ConfigStore.SaveCurrent();
    }

    private void BeginTextProbeBackup()
    {
        if (_probeTextColorBackup is not null) return;

        _probeTextColorBackup = Snapshot(_config.FloatFieldColors);
        _probeTrayColorBackup = Snapshot(_config.TrayFieldColors);
        _probeThrBackup = _config.TextKeepThreshold;
        Application.Current.Exit += (_, _) => RestoreTextProbe();
    }

    private static string Snapshot(Dictionary<string, string>? map)
    {
        if (map is null || map.Count == 0) return string.Empty;

        var parts = new List<string>();
        foreach (KeyValuePair<string, string> kv in map) parts.Add($"{kv.Key}:{kv.Value}");
        return string.Join(",", parts);
    }

    private void RestoreTextProbe()
    {
        if (_probeTextColorBackup is null) return;

        _config.FloatFieldColors = FloatInk.ParsePairs(_probeTextColorBackup);
        _config.TrayFieldColors = FloatInk.ParsePairs(_probeTrayColorBackup);
        _config.TextKeepThreshold = _probeThrBackup ?? true;
        _probeTextColorBackup = null;
        ConfigStore.SaveCurrent();
    }

    /// <summary>
    /// 预览：深底 / 浅底各一条，主值 + 次值两段一起看。
    ///
    /// <para>
    /// 「自动」时两块用**各自的**主题色（深底浅字、浅底深字），
    /// 这正是默认行为；自定义后两块都用所选色 —— 于是"这个色在我的条上读不读得出来"
    /// 在设置页就能看出来，不用回到悬浮窗去试。
    /// </para>
    /// </summary>
    private void UpdateTextPreview(string? hex)
    {
        SolidColorBrush? custom = FloatInk.ToBrush(hex);

        PreviewValueDark.Foreground = custom ?? (Brush)FindResource("BrushTextPrimary");
        PreviewLabelDark.Foreground = custom is null
            ? (Brush)FindResource("BrushTextSecondary")
            : FloatInk.ToSubBrush(custom);

        PreviewValueLight.Foreground = custom ?? (Brush)FindResource("BrushTextOnGlass");
        PreviewLabelLight.Foreground = custom is null
            ? (Brush)FindResource("BrushTextSecondaryLight")
            : FloatInk.ToSubBrush(custom);
    }

    private void TextSwatch_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string hex }) return;
        ApplyTextColor(hex);
    }

    private void ApplyTextHex_Click(object sender, RoutedEventArgs e) => ApplyTextHex();

    private void TextHexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyTextHex();
    }

    private void ApplyTextHex()
    {
        string text = TextHexBox.Text.Trim();

        // 认不出来就明说，不要静默退回「自动」—— 用户会以为"我填的颜色生效了"。
        if (text.Length > 0 && !FloatInk.TryParse(text, out _))
        {
            Hint($"颜色写法不对：{text}（要 #RRGGBB，例如 #FF7FB8）");
            return;
        }

        ApplyTextColor(FloatInk.Normalize(text));
    }

    /// <summary>
    /// 改**当前视图当前字段**的文字色。窗口开着就直接改，没开就只落盘
    /// （与缩放 / 边框同一套写法）。
    ///
    /// <para>
    /// 落盘那一支不能少：用户在悬浮窗没开着的时候打开设置页调色，
    /// 改完下次启动必须还是那个色，否则等于改了个寂寞。
    /// </para>
    /// </summary>
    private void ApplyTextColor(string hex)
    {
        string normalized = FloatInk.Normalize(hex);
        string view = _inkView == "tray" ? "任务栏条" : "悬浮窗";
        string field = _inkView == "tray"
            ? TrayFields.LabelOf(_inkField)
            : FloatFields.LabelOf(_inkField);

        if (_inkView == "tray")
        {
            TaskbarWindow? bar = _ctx.GetTaskbar();
            if (bar is not null) bar.SetFieldColor(_inkField, normalized);
            else
            {
                FloatInk.Set(_config.TrayFieldColors, _inkField, normalized);
                ConfigStore.SaveCurrent();
            }
        }
        else
        {
            FloatWindow? f = _ctx.GetFloat();
            if (f is not null) f.SetFieldColor(_inkField, normalized);
            else
            {
                FloatInk.Set(_config.FloatFieldColors, _inkField, normalized);
                ConfigStore.SaveCurrent();
            }
        }

        SyncTextColor(normalized);
        RefreshFieldBar();
        Hint(FloatInk.IsAuto(normalized)
            ? $"{view}「{field}」改回「自动」（跟随底色）"
            : $"{view}「{field}」文字颜色 {normalized}");
    }

    /// <summary>
    /// 一次改好几个字段（<c>fps:#FF7FB8,cpu:#00E5FF</c>）。探针专用 ——
    /// 真人一次只调一格，但自测要能一口气摆出"三格三个色"的画面来验。
    /// 没被提到的一律不动，所以它能叠在现有配色上用。
    /// </summary>
    private void ApplyTextColorPairs(string? spec)
    {
        var map = FloatInk.ParsePairs(spec);
        if (map.Count == 0) return;

        bool changed = false;
        foreach (string key in InkKeys())
        {
            if (!map.TryGetValue(key, out string? hex)) continue;

            if (_inkView == "tray")
            {
                TaskbarWindow? bar = _ctx.GetTaskbar();
                if (bar is not null) bar.SetFieldColor(key, hex);
                else FloatInk.Set(_config.TrayFieldColors, key, hex);
            }
            else
            {
                FloatWindow? f = _ctx.GetFloat();
                if (f is not null) f.SetFieldColor(key, hex);
                else FloatInk.Set(_config.FloatFieldColors, key, hex);
            }
            changed = true;
        }

        if (changed && _ctx.GetFloat() is null && _ctx.GetTaskbar() is null) ConfigStore.SaveCurrent();
        SyncTextColor(CurrentFieldColor());
        RefreshFieldBar();
    }

    /// <summary>诊断用：把一个视图九格的配色打成一行。</summary>
    private string DumpColors(bool tray)
    {
        var parts = new List<string>();
        foreach (string key in tray ? TrayFields.Keys : FloatFields.Keys)
        {
            string hex = tray
                ? (_ctx.GetTaskbar()?.FieldColor(key) ?? FloatInk.Get(_config.TrayFieldColors, key))
                : (_ctx.GetFloat()?.FieldColor(key) ?? FloatInk.Get(_config.FloatFieldColors, key));
            parts.Add($"{key}={(FloatInk.IsAuto(hex) ? "auto" : hex)}");
        }
        return string.Join(" ", parts);
    }

    /// <summary>当前视图九个字段全部改回「自动」。逐点九次太反人类，给个总开关。</summary>
    private void ResetAllTextColors_Click(object sender, RoutedEventArgs e)
    {
        if (_inkView == "tray")
        {
            TaskbarWindow? bar = _ctx.GetTaskbar();
            if (bar is not null) bar.SetAllFieldColors(FloatInk.Auto);
            else
            {
                _config.TrayFieldColors = FloatInk.NewMap();
                ConfigStore.SaveCurrent();
            }
        }
        else
        {
            FloatWindow? f = _ctx.GetFloat();
            if (f is not null) f.SetAllFieldColors(FloatInk.Auto);
            else
            {
                _config.FloatFieldColors = FloatInk.NewMap();
                ConfigStore.SaveCurrent();
            }
        }

        SyncTextColor(FloatInk.Auto);
        RefreshFieldBar();
        Hint(_inkView == "tray" ? "任务栏条九个字段都改回「自动」" : "悬浮窗九个字段都改回「自动」");
    }

    private void TextThreshold_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        bool on = TextThresholdSwitch.IsChecked == true;

        // 开关只有一个，两个视图共用：改完两边都要重算一遍，
        // 否则任务栏那一路会停在"自定义色让不让位"的旧结论上。
        FloatWindow? f = _ctx.GetFloat();
        if (f is not null) f.TextKeepThreshold = on;
        _ctx.GetTaskbar()?.RefreshText();

        _config.TextKeepThreshold = on;
        ConfigStore.SaveCurrent();

        Hint(on ? "超阈值的数值仍会变黄 / 红" : "自定义色不再让位给告警色（整条一个颜色）");
    }

    // ---- 任务栏条 ----

    private void Ink_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        TaskbarWindow.InkMode mode = tag switch
        {
            "light" => TaskbarWindow.InkMode.Light,
            "dark" => TaskbarWindow.InkMode.Dark,
            _ => TaskbarWindow.InkMode.Auto,
        };

        TaskbarWindow? bar = _ctx.GetTaskbar();
        if (bar is not null)
        {
            bar.SetInkMode(mode);
        }
        else
        {
            _config.TaskbarInk = mode.ToString();
            ConfigStore.SaveCurrent();
        }

        Hint(mode switch
        {
            TaskbarWindow.InkMode.Light => "任务栏条按深色底配色",
            TaskbarWindow.InkMode.Dark => "任务栏条按浅色底配色",
            _ => "任务栏条按任务栏实际亮度自动配色",
        });
    }

    private void ThroughSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        bool through = ThroughSwitch.IsChecked == true;
        TaskbarWindow? bar = _ctx.GetTaskbar();

        if (bar is not null)
        {
            bar.ClickThrough = through;
        }
        else
        {
            _config.TaskbarClickThrough = through;
            ConfigStore.SaveCurrent();
        }

        Hint(through ? "任务栏条已设为点击穿透（操作走托盘菜单）" : "任务栏条已可点击");
    }

    // ---- 悬浮窗交互（M4.21）----

    /// <summary>
    /// 三个开关都走同一个形状：窗口开着就改窗口（它自己会落盘），没开就只改配置。
    /// 与缩放 / 排列同一套取舍 —— 悬浮窗是按需创建的，此刻多半不存在。
    /// </summary>
    private void FloatLockSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        bool locked = FloatLockSwitch.IsChecked == true;
        FloatWindow? f = _ctx.GetFloat();

        if (f is not null) f.Locked = locked;
        else
        {
            _config.FloatLocked = locked;
            ConfigStore.SaveCurrent();
        }

        Hint(locked ? "悬浮窗位置已锁定（拖动无效）" : "悬浮窗位置已解锁");
    }

    private void FloatThroughSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        bool through = FloatThroughSwitch.IsChecked == true;
        FloatWindow? f = _ctx.GetFloat();

        if (f is not null) f.ClickThrough = through;
        else
        {
            _config.FloatClickThrough = through;
            ConfigStore.SaveCurrent();
        }

        // 穿透会连带把「锁定位置」顶成强制状态（鼠标消息根本不投递），UI 要跟着变，
        // 否则用户会看到"锁定开着但我没开"却无从下手。
        SyncFloatInteract();

        Hint(through
            ? "悬浮窗已设为鼠标穿透：右键菜单失效，可从本页 / 托盘图标 / 全局热键关掉"
            : "悬浮窗已恢复接收鼠标");
    }

    private void FloatTopmostSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        bool onTop = FloatTopmostSwitch.IsChecked == true;
        FloatWindow? f = _ctx.GetFloat();

        if (f is not null) f.AlwaysOnTop = onTop;
        else
        {
            _config.FloatAlwaysOnTop = onTop;
            ConfigStore.SaveCurrent();
        }

        Hint(onTop ? "悬浮窗每秒提回最顶层" : "悬浮窗不再强制置顶（仍是普通置顶窗口）");
    }

    /// <summary>
    /// 三个开关与事实对齐。穿透会强制锁定，所以每次都要重读一次窗口的实际状态，
    /// 而不是照抄配置 —— 配置里 <c>FloatLocked</c> 此刻可能已经被穿透顶成 true。
    /// </summary>
    private void SyncFloatInteract()
    {
        FloatWindow? f = _ctx.GetFloat();
        bool locked = f?.Locked ?? _config.FloatLocked;
        bool through = f?.ClickThrough ?? _config.FloatClickThrough;

        _suppress = true;
        FloatLockSwitch.IsChecked = locked;
        FloatThroughSwitch.IsChecked = through;
        FloatTopmostSwitch.IsChecked = f?.AlwaysOnTop ?? _config.FloatAlwaysOnTop;

        // 穿透状态下「锁定位置」是被动结果，置灰免得用户点了没反应
        FloatLockSwitch.IsEnabled = !through;
        _suppress = false;
    }

    // ---- 采集与网络 ----

    private void PingBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyPing();
    }

    private void ApplyPing_Click(object sender, RoutedEventArgs e) => ApplyPing();

    private void ApplyPing()
    {
        string target = PingBox.Text?.Trim() ?? string.Empty;

        _ctx.SetPingTarget(target);
        _config.PingTarget = _ctx.GetPingTarget();   // 存兜底后的值，配置里不留空串
        ConfigStore.SaveCurrent();

        PingBox.Text = _config.PingTarget;
        Hint(string.IsNullOrWhiteSpace(target)
            ? $"探测目标为空，已回退默认 {_config.PingTarget}"
            : $"延迟探测目标：{_config.PingTarget}（下一轮生效）");
    }

    /// <summary>
    /// 防抖档位（M4.23）：进入与退出共用同一个数。
    /// 立即生效（状态机下一轮判定就按新门限走），不用重启。
    /// </summary>
    private void FsHold_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string tag }) return;
        if (!int.TryParse(tag, out int ms)) return;

        _config.FullScreenHoldMs = ms;
        ConfigStore.SaveCurrent();
        _ctx.SetFullScreenHold(ms);

        Hint($"全屏切换确认改为 {ms / 1000.0:0.#}s：进游戏连续全屏这么久才切悬浮窗，退出全屏也要连续这么久才回任务栏条");
    }

    /// <summary>
    /// 数据刷新间隔（M4.24）。下一个采样 tick 生效（P-05：不补采、不重启采集线程），
    /// 改完立刻能看出数值跳动变慢。
    /// </summary>
    private void SampleRate_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string tag }) return;
        if (!int.TryParse(tag, out int ms)) return;

        _config.SampleIntervalMs = ms;
        ConfigStore.SaveCurrent();
        _ctx.SetSampleInterval(ms);

        // 回读真实生效值：调快可能被各指标的 floor 钳住，这时提示要说"实际是 x 秒"，
        // 否则用户点了 0.5s 却发现没变化会以为是 bug。
        int real = _ctx.GetSampleInterval();
        Hint(real == ms
            ? $"数据刷新间隔改为 {ms / 1000.0:0.#}s：CPU、显卡等数值每这么久更新一次"
            : $"数据刷新间隔设为 {ms / 1000.0:0.#}s，但部分指标自身下限更长，实际按 {real / 1000.0:0.#}s 跑");
    }

    private void SelectSampleSegment(int ms)
    {
        string want = ms.ToString();
        foreach (RadioButton b in new[] { Sample500, Sample1000, Sample2000, Sample3000, Sample5000 })
            b.IsChecked = (b.Tag as string) == want;
    }

    private void SelectHoldSegment(int ms)
    {
        string want = ms.ToString();
        foreach (RadioButton b in new[] { FsHold500, FsHold1000, FsHold2000, FsHold3000 })
            b.IsChecked = (b.Tag as string) == want;
    }

    private void FullScreenSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        bool on = FullScreenSwitch.IsChecked == true;
        _config.FullScreenAutoSwitch = on;
        ConfigStore.SaveCurrent();
        _ctx.SetFullScreenAutoSwitch(on);

        Hint(on ? "全屏时自动收起悬浮窗 / 任务栏条" : "全屏自动切换已关闭（条会盖在全屏画面上）");
    }

    // ---- 交互 ----

    /// <summary>是否正在录制热键。录制期间全局热键被摘掉，退出录制要装回去。</summary>
    private bool _recordingHotkey;

    private void HotkeyBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        => BeginHotkeyRecording();

    private void BeginHotkeyRecording()
    {
        if (_recordingHotkey) return;
        _recordingHotkey = true;

        // 必须先摘下自己的热键。热键是全局的：不摘的话，用户在这里按下当前组合键，
        // 系统会先把 WM_HOTKEY 发给我们，视图切走了，输入框什么都收不到。
        _ctx.SuspendHotkey(true);

        HotkeyBox.Text = "请按下组合键…";
        HotkeyHint.Text = "按 Esc 取消录制。至少要带一个 Ctrl / Alt / Shift / Win。";
    }

    private void HotkeyBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        if (!_recordingHotkey) return;
        EndHotkeyRecording();
    }

    private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (!_recordingHotkey) return;

        // 全吞：空格会在输入框里产生字符、方向键会移光标，都不该发生
        e.Handled = true;

        // 按着 Alt 时 WPF 把真实的键放在 SystemKey 里，Key 只会是 Key.System
        HandleHotkeyKey(e.Key == Key.System ? e.SystemKey : e.Key, Keyboard.Modifiers);
    }

    /// <summary>
    /// 录制框收到一个"完整的"按键之后的全部处理。抽出来是为了让探针能喂一对
    /// <c>(键, 修饰键)</c> 走同一段逻辑 —— 本机 <c>SendInput</c> 进不了输入队列
    /// （对照实验：一个纯 Python 接收端也收不到），真按键没法自动造出来。
    /// </summary>
    private void HandleHotkeyKey(Key key, ModifierKeys modifiers)
    {
        if (!_recordingHotkey) return;

        if (key == Key.Escape)
        {
            Keyboard.ClearFocus();      // 触发 Lost → 恢复原热键
            Hint("已取消，热键未改动");
            return;
        }

        // 只按了修饰键：还没到主键，把当前按下的部分显示出来让用户知道程序在听
        if (key is Key.LeftCtrl or Key.RightCtrl
                 or Key.LeftAlt or Key.RightAlt
                 or Key.LeftShift or Key.RightShift
                 or Key.LWin or Key.RWin
                 or Key.System or Key.None)
        {
            HotkeyBox.Text = HotkeyGesture.FormatModifiers(ToModifiers(modifiers)) + "…";
            return;
        }

        if (!HotkeyGesture.TryCompose(ToModifiers(modifiers), (uint)KeyInterop.VirtualKeyFromKey(key),
                                      out string gesture, out string error))
        {
            // 不合法的组合（比如只有字母没修饰键）留在录制状态，让用户接着按 ——
            // 退出录制只会让人以为"点了没反应"
            Hint(error);
            HotkeyBox.Text = "请按下组合键…";
            return;
        }

        ApplyHotkey(gesture);
        Keyboard.ClearFocus();
    }

    private void HotkeyDefault_Click(object sender, RoutedEventArgs e) => ApplyHotkey(HotkeyGesture.Default);

    private void EndHotkeyRecording()
    {
        _recordingHotkey = false;
        _ctx.SuspendHotkey(false);      // 已注册过就什么都不做，不会重复注册
        ShowHotkey();
    }

    /// <summary>
    /// 改热键的唯一入口：录制框与探针都走这里，避免"探针验的和用户点的是两条路"。
    /// </summary>
    private void ApplyHotkey(string gesture)
    {
        string? error = _ctx.SetHotkey(gesture);
        if (error is not null)
        {
            Hint($"没换成：{error}");
            ShowHotkey();               // 实现方已回滚到上一个热键，这里只要重画
            return;
        }

        _config.Hotkey = _ctx.GetHotkey() ?? gesture;
        ShowHotkey();
        Hint($"全局热键已改为 {_config.Hotkey}");
    }

    /// <summary>
    /// 重画热键框。显示的是**真正注册上的**那个，不是配置里的 ——
    /// 冲突时两者不同，而用户需要知道"现在按哪个键才有用"。
    /// </summary>
    private void ShowHotkey()
    {
        string? applied = _ctx.GetHotkey();

        if (applied is null)
        {
            HotkeyBox.Text = "未生效";
            HotkeyHint.Text = $"配置里是 {_config.Hotkey}，但没能注册上（多半被别的程序占了）。点一下这里重新录一个。";
            return;
        }

        HotkeyBox.Text = applied;
        HotkeyHint.Text = "点击输入框后直接按下组合键即可录制，Esc 取消。按一次循环切换：大窗口 → 悬浮窗 → 任务栏条。";
    }

    private static uint ToModifiers(ModifierKeys keys)
    {
        uint mods = 0;
        if ((keys & ModifierKeys.Control) != 0) mods |= HotkeyGesture.ModControl;
        if ((keys & ModifierKeys.Alt) != 0) mods |= HotkeyGesture.ModAlt;
        if ((keys & ModifierKeys.Shift) != 0) mods |= HotkeyGesture.ModShift;
        if ((keys & ModifierKeys.Windows) != 0) mods |= HotkeyGesture.ModWin;
        return mods;
    }

    /// <summary>探针用：把 <c>ctrl+alt+F9</c> 这样的写法翻成一对 WPF 的键与修饰键。</summary>
    private static bool TryParseProbeKey(string text, out Key key, out ModifierKeys mods)
    {
        key = Key.None;
        mods = ModifierKeys.None;

        if (!HotkeyGesture.TryParse(text, out HotkeyCombo combo, out _)) return false;

        key = KeyInterop.KeyFromVirtualKey((int)combo.VirtualKey);
        if (key == Key.None) return false;

        if ((combo.Modifiers & HotkeyGesture.ModControl) != 0) mods |= ModifierKeys.Control;
        if ((combo.Modifiers & HotkeyGesture.ModAlt) != 0) mods |= ModifierKeys.Alt;
        if ((combo.Modifiers & HotkeyGesture.ModShift) != 0) mods |= ModifierKeys.Shift;
        if ((combo.Modifiers & HotkeyGesture.ModWin) != 0) mods |= ModifierKeys.Windows;
        return true;
    }

    // ---- 启动 ----

    private void AutoStartSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        bool want = AutoStartSwitch.IsChecked == true;

        // Enable/Disable 未提权时会自提权重跑自己（一次 UAC），阻塞几百毫秒到几秒。
        // 在 UI 线程同步等的话，整个设置页会卡住 —— 连"正在注册"都刷不出来。
        AutoStartSwitch.IsEnabled = false;
        Hint(want ? "正在注册开机自启（可能弹一次 UAC）…" : "正在取消开机自启…");

        Task.Run(() => want ? AutoStart.Enable() : AutoStart.Disable())
            .ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() =>
            {
                bool actual = AutoStart.IsEnabled();

                _suppress = true;
                AutoStartSwitch.IsChecked = actual;
                _suppress = false;
                AutoStartSwitch.IsEnabled = true;

                AutoStartHint.Text = actual
                    ? "已注册计划任务（登录时触发，最高权限）。取消勾选即可移除。"
                    : "通过计划任务注册（程序需要管理员权限，Run 键会被 UAC 拦下）。首次开启会弹一次 UAC 确认。";

                Hint(actual == want
                    ? (want ? "开机自启已开启" : "开机自启已关闭")
                    : "未生效：可能取消了一次 UAC 确认");
            })), TaskScheduler.Default);
    }

    private void OpenConfigDir_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(_ctx.ConfigPath);
            if (string.IsNullOrEmpty(dir)) return;

            System.IO.Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = $"\"{dir}\"",
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Hint("打开目录失败：" + ex.Message);
        }
    }

    // =================================================================
    // 传感器诊断（G-09 / Q-18）
    // =================================================================

    private void RefreshDiag_Click(object sender, RoutedEventArgs e) => LoadDiag();

    /// <summary>
    /// 逐项摊开六个采集器的状态。4.2 的「不得静默降级」在设置页的落点：
    /// 条上/面板上一个 <c>—</c> 到底是"没这个硬件"还是"没提权"，只有这里说得清。
    /// </summary>
    private void LoadDiag()
    {
        IReadOnlyList<IMetricProvider> providers = _ctx.GetProviders();
        var rows = new List<DiagRow>(providers.Count);
        int ready = 0;

        foreach (IMetricProvider p in providers)
        {
            bool ok = p.Status == ProviderStatus.Ready;
            if (ok) ready++;

            string status = p.Status switch
            {
                ProviderStatus.Ready => "就绪",
                ProviderStatus.Unsupported => "本机不支持",
                ProviderStatus.Failed => "初始化失败",
                _ => "未初始化",
            };

            string cost = p.InitializeMs > 0 ? $"init {p.InitializeMs:F0} ms" : "init —";
            string? reason = p.UnavailableReason;
            string? remedy = p.Remedy;

            rows.Add(new DiagRow
            {
                Name = p.Name,
                StatusText = status,
                BadgeBg = Tint(ok ? (byte)0x63 : (byte)0xE0, ok ? (byte)0xA9 : (byte)0xA4, ok ? (byte)0x6D : (byte)0x4A),
                BadgeFg = Palette.Brush(ok ? "BrushOk" : "BrushWarn"),
                CostText = cost,
                ReasonText = reason ?? string.Empty,
                ReasonVisibility = string.IsNullOrWhiteSpace(reason) ? Visibility.Collapsed : Visibility.Visible,
                RemedyText = remedy ?? string.Empty,
                RemedyVisibility = string.IsNullOrWhiteSpace(remedy) ? Visibility.Collapsed : Visibility.Visible,
            });
        }

        DiagList.ItemsSource = rows;

        bool elevated = Taskbar.IsElevated();
        string summary = $"{ready}/{providers.Count} 个采集器就绪 · 管理员权限：{(elevated ? "是" : "否（温度 / 功耗 / 硬盘温度会降级为 —）")}";
        if (!string.IsNullOrWhiteSpace(_config.LoadError)) summary += " · " + _config.LoadError;

        DiagSummary.Text = summary;
    }

    /// <summary>状态徽标的底色：同色系 20% 透明度，不用实心色块抢视线（10.3）。</summary>
    private static Brush Tint(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(0x33, r, g, b));
        brush.Freeze();
        return brush;
    }

    // =================================================================

    private void Hint(string text) => FooterText.Text = text;

    /// <summary>与 FloatWindow.NormalizeScale 同一套规则：取最接近的档，不拒绝。</summary>
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
}
