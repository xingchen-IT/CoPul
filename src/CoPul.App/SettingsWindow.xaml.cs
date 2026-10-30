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

    /// <summary>全屏自动切换的总开关动作（F-11）。设置页不给 true/false 的猜法，直接调它。</summary>
    public Action<bool> SetFullScreenAutoSwitch { get; init; } = _ => { };

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

    internal SettingsWindow(SettingsContext ctx)
    {
        _ctx = ctx;
        _config = ctx.Config;

        InitializeComponent();

        InitFieldGrid();
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

        GlassSwitch.IsChecked = _config.FloatGlassEnabled;
        UpdateLevelText();

        switch (NormalizeScale(_config.FloatScale))
        {
            case 125: Scale125.IsChecked = true; break;
            case 150: Scale150.IsChecked = true; break;
            default: Scale100.IsChecked = true; break;
        }

        switch (_config.TaskbarInk)
        {
            case "Light": InkLight.IsChecked = true; break;
            case "Dark": InkDark.IsChecked = true; break;
            default: InkAuto.IsChecked = true; break;
        }

        ThroughSwitch.IsChecked = _config.TaskbarClickThrough;
        FullScreenSwitch.IsChecked = _config.FullScreenAutoSwitch;
        PingBox.Text = _ctx.GetPingTarget();

        _suppress = false;
        UpdateFieldEnabled();
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
