using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;   // ToggleButton（字段开关，M4.25）
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

    /// <summary>
    /// 大窗口毛玻璃的施加入口（2026-11-01）。
    ///
    /// <para>
    /// 设置页**不该**自己调 WindowGlassLarge —— 那要拿 HWND，而设置页是另一个窗口。
    /// 玻璃是主窗口的属性，所以由 App 把主窗口的三个方法传进来。
    /// </para>
    /// </summary>
    public Action<bool, int, int, bool>? ApplyMainGlass { get; init; }

    /// <summary>主窗口当前的毛玻璃状态，打开设置页时用来同步开关与滑块。</summary>
    public Func<(bool Enabled, int Level, int CardOpacity)>? GetMainGlass { get; init; }

    /// <summary>当前视图。切模式时用来避免"点当前模式"这种无意义切换。</summary>
    public Func<FullScreenAutoSwitch.View> CurrentView { get; init; }
        = () => FullScreenAutoSwitch.View.Main;

    /// <summary>切视图。必须走 App 的 ShowView —— 那里同步状态机，直接 Show/Hide 会让全屏回落打架。</summary>
    public Action<FullScreenAutoSwitch.View> ShowView { get; init; } = _ => { };

    /// <summary>
    /// 某个视图此刻是不是真的在显示（M4.29）。三视图可叠加之后，界面上三个开关的亮灭
    /// 必须能读出**事实**，不能靠"用户最后点了谁"去猜 —— 全屏自动切换会自己收放视图，
    /// 那时用户一次都没点过。
    /// </summary>
    public Func<FullScreenAutoSwitch.View, bool> IsViewShown { get; init; } = _ => false;

    /// <summary>
    /// 点一下就翻一次那个视图（M4.29）。**以真实状态为准**，不看控件当前的 IsChecked：
    /// 界面和事实万一不同步（托盘/标题栏/全屏自动切换都改得了），以控件为准会把状态翻反。
    /// </summary>
    public Action<FullScreenAutoSwitch.View> ToggleView { get; init; } = _ => { };

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
    /// 字段开关变了之后重算"还要不要采 FPS"（M4.27）。
    ///
    /// <para>
    /// 折叠大窗口的 FPS 区块会停掉 PresentMon；但悬浮窗 / 任务栏条上的 FPS 字段是另一回事 ——
    /// 两处都关掉才真的不用采，任何一处还亮着就得继续。字段开关在设置页与托盘两处，
    /// 所以这条必须由两边都能调用。
    /// </para>
    /// </summary>
    public Action RefreshFpsSampling { get; init; } = () => { };

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

        InitFieldBar();
        InitTextSwatches();
        InitBgSwatches();
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

        // 视图不止这里能改：托盘菜单、大窗口标题栏、全屏自动切换都会动它。
        // 重新激活时把三个开关拉回真实状态，否则用户"去托盘开一下条，回来一看开关没亮"。
        Activated += (_, _) => SyncViewChips();

        _loaded = true;
    }

    // =================================================================
    // 初始化
    // =================================================================

    /// <summary>
    /// 字段条（M4.25 合并）。原来任务栏 / 悬浮窗各一片勾选框，现在与「文字颜色」同构：
    /// 先选视图，再一条字段条，点亮 = 显示、划掉 = 隐藏。
    ///
    /// <para>
    /// 按 <see cref="TrayFields.Keys"/> / <see cref="FloatFields.Keys"/> 生成，不写死在 XAML 里 ——
    /// 字段表以后加一项时这里一行都不用改。两张表的**顺序不同**（第 6 位 lat vs net），
    /// 所以字段条必须按当前视图重建，不能共用一条。
    /// </para>
    ///
    /// <para>
    /// <c>IsChecked</c> 在挂事件**之前**设：否则每次重建都会把九个字段各触发一次
    /// "用户改了字段"的落盘路径（首帧就会把 hidden 数组按控件状态重写一遍）。
    /// </para>
    /// </summary>
    private void InitFieldBar()
    {
        FieldBar.Children.Clear();

        string[] keys = FieldKeys();
        string[] labels = FieldLabels();

        for (int i = 0; i < keys.Length; i++)
        {
            string key = keys[i];
            var chip = new ToggleButton
            {
                Style = (Style)FindResource("FieldToggle"),
                Content = labels[i],
                Tag = key,
                IsChecked = FieldVisible(key),
                ToolTip = $"{labels[i]}（{key}）",
            };
            chip.Checked += FieldToggle_Changed;
            chip.Unchecked += FieldToggle_Changed;
            FieldBar.Children.Add(chip);
        }

        ApplyFieldHint();
        UpdateFieldToggleEnabled();
    }

    /// <summary>当前正在编辑的视图（"float" / "tray"）。与文字颜色那套同一套写法。</summary>
    private string _fieldView = "float";

    private string[] FieldKeys() => _fieldView == "tray" ? TrayFields.Keys : FloatFields.Keys;

    private string[] FieldLabels() => _fieldView == "tray" ? TrayFields.Labels : FloatFields.Labels;

    private bool FieldVisible(string key) => _fieldView == "tray"
        ? TrayFields.IsVisible(_config, key)
        : FloatFields.IsVisible(_config, key);

    /// <summary>两边的说明文字不一样：任务栏有"空白不够自动让位"这套规则，悬浮窗没有。</summary>
    private void ApplyFieldHint()
    {
        FieldHint.Text = _fieldView == "tray"
            ? "点亮 = 显示，划掉 = 隐藏。关闭的字段不占位，条会跟着收缩。任务栏空白不够时按「功耗 → 延迟 → 网络速率」的顺序临时让位（FPS 留到最后），让位不写入配置，空白恢复后自动长回来。只剩一个字段时该项不可关闭。"
            : "点亮 = 显示，划掉 = 隐藏。关闭的字段不占位：横向条会变窄，竖向条会变矮。只剩一个字段时该项不可关闭。与任务栏条各自独立保存。";
    }

    /// <summary>
    /// 外观（App 级主题）切换（2026-11-01）。白色 / 深色两档，点一下立即生效。
    ///
    /// <para>
    /// <b>为什么不需要重建窗口。</summary>颜色全部是 <c>DynamicResource</c>，替换主题字典后
    /// WPF 会自己重新解析；非颜色的那部分（卡片投影）由 <see cref="ThemeManager.Changed"/>
    /// 通知 App 去刷新（见 Sdk/CardShadow.cs）。
    /// </para>
    ///
    /// <para>
    /// 写盘交给 <see cref="ThemeManager.Apply"/>（它的 persist 参数），这里不重复存 —— 两处都写
    /// 会出现"先存了一次、又被覆盖"，而且以后加主题档位时容易漏改一边。
    /// </para>
    /// </summary>
    // =================================================================
    // 毛玻璃（2026-11-01）
    // =================================================================

    /// <summary>拖滑块时挡住回环：我们自己设 Slider.Value 会再触发一次 ValueChanged。</summary>
    private bool _glassSyncing;

    /// <summary>毛玻璃总开关。</summary>
    private void Glass_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        PushGlass(persist: true);
    }

    /// <summary>
    /// 浓度滑块。**拖动过程中就生效**（不是松手才生效）：
    /// 玻璃浓度是"边调边看"的东西，松手才变会让用户来回拖十几次。
    /// </summary>
    private void GlassLevel_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || _suppress || _glassSyncing) return;

        UpdateGlassLevelText();
        PushGlass(persist: false);      // 拖动中不写盘，松手才写（见下）
    }

    /// <summary>滑块松开时才落盘 —— 拖动中间会产生几十次写盘。</summary>
    protected override void OnMouseLeftButtonUp(System.Windows.Input.MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_loaded) return;

        if (MainGlassSwitch.IsChecked == true) PersistGlass();

        // 任务栏条背景（M4.36）：拖动期间只改窗口（persist:false），松手补一次落盘。
        // 判断点放在"确实是自定义档"上：透明档拖动无意义（控件已禁用），不写盘也不会丢东西。
        if (_bgMode == "custom") PushBackdrop(persist: true);
    }

    /// <summary>预设档位：一键跳到熟悉的浓度。</summary>
    private void GlassPreset_Click(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not Button { Tag: string tag } || !double.TryParse(tag, out double v)) return;

        GlassLevelSlider.Value = v;
        PushGlass(persist: true);
        Hint($"毛玻璃浓度已设为 {v:F0}（实际遮盖约 {WindowGlassLarge.CoverageFor((int)v):P0}）");
    }

    /// <summary>
    /// 卡片透明度滑块（2026-11-01，从开关改成可调）。
    /// 与玻璃浓度同一条规则：拖动中即时生效、不写盘，松手才落盘。
    /// </summary>
    private void GlassCardOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || _suppress || _glassSyncing) return;

        UpdateGlassCardText();
        PushGlass(persist: false);
    }

    /// <summary>把三个旋钮的当前值推给主窗口。</summary>
    private void PushGlass(bool persist)
    {
        if (_ctx.ApplyMainGlass is null) return;

        bool on = MainGlassSwitch.IsChecked == true;
        int level = (int)Math.Round(GlassLevelSlider.Value);
        int cardOpacity = (int)Math.Round(GlassCardSlider.Value);

        _ctx.ApplyMainGlass(on, level, cardOpacity, persist);
        UpdateGlassUiState();

        if (persist && !on) Hint("毛玻璃已关闭，回到不透明纸面");
        else if (persist) Hint($"毛玻璃已开启（浓度 {level}，遮盖约 {WindowGlassLarge.CoverageFor(level):P0}；卡片 {cardOpacity}%）");
    }

    /// <summary>只在"松手"时写盘。拖动的中间值不存。</summary>
    private void PersistGlass()
    {
        if (_ctx.ApplyMainGlass is null) return;
        _ctx.ApplyMainGlass(MainGlassSwitch.IsChecked == true,
                            (int)Math.Round(GlassLevelSlider.Value),
                            (int)Math.Round(GlassCardSlider.Value),
                            true);
    }

    /// <summary>UI 从属状态：玻璃关掉时滑块与预设置灰，但仍显示当前值（下次开直接复用）。</summary>
    private void UpdateGlassUiState()
    {
        bool on = MainGlassSwitch.IsChecked == true;
        GlassControls.IsEnabled = on;

        int level = (int)Math.Round(GlassLevelSlider.Value);
        int cardOpacity = (int)Math.Round(GlassCardSlider.Value);
        UpdateGlassLevelText();
        UpdateGlassCardText();

        GlassStateText.Text = on ? "已开启" : "已关闭";

        // 卡片透明度的说明随数值变 —— 两端都有明确的物理后果，要提前讲清楚
        GlassCardHint.Text = !on
            ? string.Empty
            : cardOpacity >= 100
                ? "卡片完全不透明：只有卡片缝与边缘能看到玻璃"
                : cardOpacity <= 20
                    ? "卡片几乎全透：内容等于直接飘在桌面上，可能难读"
                    : level < 40 && cardOpacity <= 60
                        ? "两者都偏低，文字可能发虚（浅色主题 + 暗壁纸时最明显）"
                        : "卡片透出背景图案，数值越低越透";

        GlassHint.Text = "毛玻璃是 DWM 在窗口后方注入的一层，不依赖窗口自身的半透明 —— " +
                         "所以它能真的模糊背后的窗口与壁纸（不是单纯的半透明）。" +
                         "「玻璃浓度」管背景糊得多实，「卡片透明度」管内容承载面多实，两个旋钮互相独立：" +
                         "可以窗口很透而卡片很实，也可以反过来。亚克力层自带约 17% 的固有底色，" +
                         "所以浓度拉到 0 也到不了全透明。";
    }

    /// <summary>浓度显示：数值 + 实际遮盖率（用户看到的那个百分比）。</summary>
    private void UpdateGlassLevelText()
        => GlassLevelText.Text = $"{(int)Math.Round(GlassLevelSlider.Value)}  " +
                                 $"({WindowGlassLarge.CoverageFor((int)Math.Round(GlassLevelSlider.Value)):P0})";

    /// <summary>卡片透明度显示：直接给百分比（它没有"固有下限"这种换算）。</summary>
    private void UpdateGlassCardText()
        => GlassCardText.Text = $"{(int)Math.Round(GlassCardSlider.Value)}%";

    /// <summary>打开设置页时把主窗口的真实状态同步过来（可能被 --main-glass 改过）。</summary>
    private void SyncGlass()
    {
        (bool Enabled, int Level, int CardOpacity) g = _ctx.GetMainGlass?.Invoke() ?? (false, 60, 75);

        _glassSyncing = true;
        try
        {
            MainGlassSwitch.IsChecked = g.Enabled;
            GlassLevelSlider.Value = g.Level;
            GlassCardSlider.Value = g.CardOpacity;
        }
        finally
        {
            _glassSyncing = false;
        }

        UpdateGlassUiState();
    }

    private void AppTheme_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        AppTheme target = tag == "dark" ? AppTheme.Dark : AppTheme.White;

        // 已经是这个主题就不做事：RadioButton 被 LoadFromConfig 设选中态时也会触发 Checked，
        // 不复位闸门的话每次打开设置页都会白白写一次盘。
        if (ThemeManager.Current == target) return;

        ThemeManager.Apply(target);
        Hint($"外观已切换为{ThemeManager.DisplayName(target)}（大窗口与设置页立即生效）");
    }

    private void FieldView_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        _fieldView = tag;
        InitFieldBar();
        Hint(_fieldView == "tray" ? "正在编辑：任务栏条的显示字段" : "正在编辑：悬浮窗的显示字段");
    }

    /// <summary>
    /// 探针用：直接切视图并重建字段条。
    /// 不走 <c>RadioButton.IsChecked</c> 是因为"重复点同一档"时 WPF 不触发 Checked，
    /// 而探针可能连发两次 <c>fieldview:tray</c> —— 那时 <see cref="_fieldView"/>
    /// 与界面必须仍然一致。
    /// </summary>
    private void SetFieldView(string view)
    {
        _fieldView = view;
        _suppress = true;
        (view == "tray" ? FieldViewTray : FieldViewFloat).IsChecked = true;
        _suppress = false;
        InitFieldBar();
    }

    /// <summary>
    /// 点亮 / 划掉一个字段。
    ///
    /// <para>
    /// 不是"翻一下 key"而是"按控件当前状态整份写一遍"：连点时 WPF 已经把 IsChecked 定好了，
    /// 以它为准比再算一次反状态可靠（与 M4.14 那版一致）。
    /// </para>
    /// </summary>
    private void FieldToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not ToggleButton { Tag: string key } chip) return;

        bool tray = _fieldView == "tray";
        string[] old = tray
            ? (_config.TaskbarHiddenFields ?? Array.Empty<string>())
            : (_config.FloatHiddenFields ?? Array.Empty<string>());

        var hidden = new List<string>(old);
        hidden.RemoveAll(h => string.Equals(h, key, StringComparison.OrdinalIgnoreCase));
        if (chip.IsChecked != true) hidden.Add(key);

        if (tray) _config.TaskbarHiddenFields = hidden.ToArray();
        else _config.FloatHiddenFields = hidden.ToArray();

        int visible = tray ? TrayFields.VisibleCount(_config) : FloatFields.VisibleCount(_config);

        if (visible <= 0)
        {
            // 全关之后条会被夹到 1 px —— 与其画一根发丝，不如拒绝。
            // 回滚必须"整份按配置重同步"，不能只把手里这个框勾回去：
            // 一次操作可能同时涉及多项（连点、探针连发），代码无法知道"哪一个才是刚关的"。
            if (tray) _config.TaskbarHiddenFields = old;
            else _config.FloatHiddenFields = old;

            SyncFieldToggles();
            Hint("至少要保留一个字段");
            return;
        }

        ConfigStore.SaveCurrent();

        // 目标窗口可能还没建（用户没进过那个模式）—— 那就什么都不用刷，
        // 它创建时会从配置里读到这份 hidden。
        if (tray) _ctx.GetTaskbar()?.ReloadFields();
        else _ctx.GetFloat()?.ReloadFields();

        // FPS 字段刚被点亮 / 关掉：PresentMon 要不要继续烧，看两边还有没有人在显示它（M4.27）
        _ctx.RefreshFpsSampling();

        UpdateFieldToggleEnabled();
        Hint($"{visible}/{FieldKeys().Length} 显示（{(tray ? "任务栏条" : "悬浮窗")}）");
    }

    /// <summary>把整条按配置重同步。回滚与探针都走这里。</summary>
    private void SyncFieldToggles()
    {
        _suppress = true;
        foreach (object child in FieldBar.Children)
        {
            if (child is ToggleButton { Tag: string key } chip) chip.IsChecked = FieldVisible(key);
        }
        _suppress = false;
        UpdateFieldToggleEnabled();
    }

    /// <summary>只剩一个可见字段时把它置灰 —— 让"不能再关"是看得见的，而不是点了没反应。</summary>
    private void UpdateFieldToggleEnabled()
    {
        int visible = 0;
        ToggleButton? only = null;

        foreach (object child in FieldBar.Children)
        {
            if (child is ToggleButton { IsChecked: true } chip)
            {
                visible++;
                only = chip;
            }
        }

        _suppress = true;
        foreach (object child in FieldBar.Children)
        {
            if (child is ToggleButton chip)
                chip.IsEnabled = !(visible <= 1 && ReferenceEquals(chip, only));
        }
        _suppress = false;
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

    /// <summary>
    /// 任务栏条背景的色板（M4.36）。
    ///
    /// <para>
    /// <b>不与 <see cref="FloatInk.Presets"/> 共用。</b>文字色要的是"贴在底上最高对比"，
    /// 所以那张表里有纯白、有深墨；而底要的是"有存在感但不抢戏"，全是中低明度的实色。
    /// 两张表混用会在各自场景里全错：拿纯白当条底 = 一整条发光，拿"墨黑"当文字色叠深底 = 看不见。
    /// </para>
    /// </summary>
    private static readonly (string Label, string Hex)[] BgPresets =
    {
        ("深灰", "#101418"),
        ("石墨", "#1B2127"),
        ("蓝灰", "#243040"),
        ("墨青", "#173034"),
        ("暖褐", "#2B2118"),
        ("银灰", "#9AA4B2"),
        ("浅灰", "#D8DEE7"),
        ("白",   "#F4F6F9"),
    };

    /// <summary>按 <see cref="BgPresets"/> 生成色板。写法与 <see cref="InitTextSwatches"/> 同构，
    /// 只是没有「自动」那一格 —— 背景只有 透明 / 自定义 两档，没有"自动取色"这一档。</summary>
    private void InitBgSwatches()
    {
        Style style = (Style)FindResource("Swatch");

        foreach ((string label, string hex) in BgPresets)
        {
            var chip = new RadioButton
            {
                Style = style,
                GroupName = "taskbarbgcolor",
                Tag = hex,
                Background = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)),
                ToolTip = $"{label} {hex}",
            };
            chip.Checked += BgSwatch_Checked;
            BgSwatches.Children.Add(chip);
        }
    }

    private void LoadFromConfig()
    {
        _suppress = true;

        // 毛玻璃（2026-11-01）：按主窗口的**真实状态**同步，不按配置 ——
        // 主窗口可能被 --main-glass 覆盖过，或用户在多开的情况下改过。
        SyncGlass();

        // 外观（2026-11-01）：按**当前生效的主题**同步分段，不按配置 ——
        // 配置可能与实际不符（比如用 --app-theme 临时覆盖启动），用户看到的是实际样子。
        (ThemeManager.Current == AppTheme.Dark ? ThemeDark : ThemeWhite).IsChecked = true;

        // 三个视图各自独立开关（M4.29）：这里不再"选一个"，而是把真实可见性一次到位。
        // 走 SyncViewChips 而不是给某个 IsChecked 赋值 —— 否则另外两个会停在
        // "上次的样子"，用户点开设置页看到的是一张过期的状态图。
        SyncViewChips();

        // 显示字段（M4.25 合并）：与文字颜色同构，默认编辑「悬浮窗」那套。
        // 九格的勾选态由 InitFieldBar 按配置读（构造时已经建过一次），这里只把视图分段对上。
        (_fieldView == "tray" ? FieldViewTray : FieldViewFloat).IsChecked = true;

        MainGlassSwitch.IsChecked = _config.FloatGlassEnabled;
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

        // 任务栏条背景（M4.36）。与「文字取色」并列的一档，回填以窗口为准。
        SyncBackdrop();

        // 防抖档位（M4.23）。配置里的值可能不在档位表上（手改过配置）→ 先归一到最接近的档，
        // 否则一个 1500 会让四个按钮一个都不亮，用户看到的是"当前档位未知"。
        SelectHoldSegment(FullScreenAutoSwitch.NormalizeHold(_config.FullScreenHoldMs));

        // 刷新间隔（M4.24）。同样先归一；选中态以"调度器真正生效的那个数"为准，
        // 这样手改配置写了非法值时 UI 显示的是实际在跑的档位，而不是用户以为的。
        SelectSampleSegment(CollectorHost.NormalizeInterval(_ctx.GetSampleInterval()));

        PingBox.Text = _ctx.GetPingTarget();

        _suppress = false;
        UpdateFieldToggleEnabled();
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
                // 自测：glass:on|off / glasslevel:0-100 / glasscards:on|off
                // 走真实控件（设 IsChecked / Value 触发事件），验的是"用户点这一下"的整条链路。
                case "main-glass":
                    MainGlassSwitch.IsChecked = value != "off";
                    break;

                case "main-glasslevel":
                    if (double.TryParse(value, out double gl)) GlassLevelSlider.Value = gl;
                    break;

                case "main-glasscards":
                    if (double.TryParse(value, out double gco)) GlassCardSlider.Value = gco;
                    break;

                // 自测：theme:white / theme:dark —— 走**真实的分段选择**（设 IsChecked 触发 Checked 事件），
                // 不是直接调 ThemeManager.Apply。前者验的是"用户点这一下"的整条链路
                // （含 RadioButton 状态、写盘、CardShadow 重建），后者只验了一半。
                case "theme":
                    {
                        RadioButton chip = value == "dark" ? ThemeDark : ThemeWhite;
                        chip.IsChecked = true;
                    }
                    break;

                case "field":
                case "floatfield":
                    // 字段开关（M4.25 合并后只剩一条字段条）：先切到对应视图再翻。
                    // 保留两个旧名字是为了让既有取证脚本不用改 —— field 仍指任务栏、
                    // floatfield 仍指悬浮窗，语义没变，只是底下共用一条条子。
                    {
                        string view = key == "field" ? "tray" : "float";
                        SetFieldView(view);

                        foreach (object child in FieldBar.Children)
                        {
                            if (child is not ToggleButton chip || (string)chip.Tag != value) continue;
                            chip.IsChecked = chip.IsChecked != true;   // 触发 apply
                            break;
                        }
                    }
                    break;

                case "fieldview":
                    SetFieldView(value == "tray" ? "tray" : "float");
                    break;

                case "fields":
                case "floatfields":
                    // 自测：把当前视图的字段条整张打印出来（顺序 / 勾选 / 是否置灰 / 前景色）——
                    // 设置页卡片在截图里常被底部截断，而且"划掉"这种视觉状态在缩略图上看不清，
                    // 靠前景色（Tertiary vs Primary）才能确定模板 trigger 真的生效了。
                    System.Console.WriteLine($"fields: view={_fieldView} count={FieldBar.Children.Count}");
                    foreach (object child in FieldBar.Children)
                    {
                        if (child is not ToggleButton chip) continue;
                        string fg = chip.Foreground is SolidColorBrush b
                            ? b.Color.ToString()
                            : chip.Foreground?.ToString() ?? "(null)";
                        System.Console.WriteLine(
                            $"fields: [{chip.Tag}] {chip.Content} checked={chip.IsChecked} " +
                            $"enabled={chip.IsEnabled} fg={fg}");
                    }
                    break;

                case "ping":
                    PingBox.Text = value;
                    ApplyPing();
                    break;

                case "bgmode":
                    // 任务栏条背景（M4.36）：<c>bgmode:auto|custom|transparent</c>
                    BeginBackdropProbeBackup();
                    if (value is "custom" or "transparent" or "auto") BarSegFor(value).IsChecked = true;
                    break;

                case "bgcolor":
                    // 底色：<c>bgcolor:#1B2127</c>（会自动切到「自定义」档）
                    BeginBackdropProbeBackup();
                    BgHexBox.Text = value;
                    ApplyBgHex();
                    break;

                case "bgopacity":
                    // 不透明度：<c>bgopacity:40</c>
                    BeginBackdropProbeBackup();
                    if (int.TryParse(value, out int bgOp)) BgOpacitySlider.Value = Math.Clamp(bgOp, 0, 100);
                    PushBackdrop(persist: true);
                    break;

                case "trayglass":
                    // 条毛玻璃开关（M4.41）：<c>trayglass:on|off|1|0</c>
                    BeginBackdropProbeBackup();
                    TrayGlassSwitch.IsChecked = value is not ("0" or "off" or "false");
                    break;

                case "glassopacity":
                    // 条毛玻璃浓度：<c>glassopacity:60</c>
                    BeginBackdropProbeBackup();
                    if (int.TryParse(value, out int glassOp)) TrayGlassSlider.Value = Math.Clamp(glassOp, 0, 100);
                    PushTrayGlass(persist: true);
                    break;

                case "bg":
                    // 全量打印：设置页看得到的三处 + 窗口实际画出来的底，两段必须一起读
                    // —— 控件对了但窗口没跟上，正是这类"改了没反应"的典型。
                    System.Console.WriteLine($"taskbarbg: mode={_bgMode} color={_bgColor} " +
                                             $"opacity={_bgOpacity} customEnabled={BgCustomPanel.IsEnabled} " +
                                             $"hex='{BgHexBox.Text}' seg=[custom={BgCustom.IsChecked}," +
                                             $"transparent={BgTransparent.IsChecked}]");
                    System.Console.WriteLine($"taskbarglass: on={_trayGlass} opacity={_trayGlassOpacity} " +
                                             $"switch={TrayGlassSwitch.IsChecked} slider={TrayGlassSlider.Value:F0} " +
                                             $"panelEnabled={TrayGlassPanel.IsEnabled} text='{TrayGlassText.Text}'");
                    foreach (object bgChild in BgSwatches.Children)
                        if (bgChild is RadioButton bgChip)
                            System.Console.WriteLine($"bgswatch: [{bgChip.Tag}] checked={bgChip.IsChecked}");
                    System.Console.WriteLine($"taskbarbg: window={_ctx.GetTaskbar()?.BackdropDiag ?? "(未开)"}");
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
                    MainGlassSwitch.IsChecked = value != "0";
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
                    {
                        // 探针沿用旧语义「切到某视图」（幂等），**不是** toggle：
                        // 老脚本会连发 mode:float，toggle 一次反而把它关掉。
                        FullScreenAutoSwitch.View target = value switch
                        {
                            "float" => FullScreenAutoSwitch.View.Float,
                            "taskbar" => FullScreenAutoSwitch.View.Taskbar,
                            _ => FullScreenAutoSwitch.View.Main,
                        };
                        _ctx.ShowView(target);
                        SyncViewChips();
                        DumpViewChips();
                    }
                    break;

                case "viewtoggle":
                    {
                        // M4.29：验「再点一次收起」。连喊两次 viewtoggle:float 应该
                        // 显示 → 收起，第三次再回来。每次把三个开关的亮灭 + 真实可见性
                        // 一起打出来，两边对不上就是这里报。
                        FullScreenAutoSwitch.View target = value switch
                        {
                            "float" => FullScreenAutoSwitch.View.Float,
                            "taskbar" => FullScreenAutoSwitch.View.Taskbar,
                            "main" => FullScreenAutoSwitch.View.Main,
                            _ => FullScreenAutoSwitch.View.Main,
                        };
                        bool before = _ctx.IsViewShown(target);
                        _ctx.ToggleView(target);
                        SyncViewChips();
                        System.Console.WriteLine($"viewtoggle: {value} {before}→{_ctx.IsViewShown(target)}");
                        DumpViewChips();
                    }
                    break;

                case "chip":
                    {
                        // 真的"点一下"那个开关：WPF 会先把 IsChecked 翻过来再发 Click，
                        // 这里照抄这个顺序再进 Mode_Clicked —— 只有这样才走得到
                        // "点亮大窗口 → 设置页自己收起"那条分支。
                        // viewtoggle 是绕开界面的，验不出这一层。
                        ToggleButton chip = value switch
                        {
                            "float" => ModeFloat,
                            "taskbar" => ModeTaskbar,
                            _ => ModeMain,
                        };
                        chip.IsChecked = !(chip.IsChecked == true);
                        Mode_Clicked(chip, new RoutedEventArgs());
                        System.Console.WriteLine($"chip: {value} → 设置页 visible={IsVisible}");
                        DumpViewChips();
                    }
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

    /// <summary>
    /// 点亮 / 收起一个视图（M4.29）。
    ///
    /// <para>
    /// 用 <c>Click</c> 而不是 <c>Checked/Unchecked</c>：后者在程序自己给 IsChecked 赋值时
    /// 也会触发，<see cref="SyncViewChips"/> 每刷一次就会把三个视图各翻一遍。
    /// Click 只在"人真的点了"时才来，正好是这里想要的语义。
    /// </para>
    ///
    /// <para>
    /// 动作本身交给 <see cref="SettingsContext.ToggleView"/>，它按**真实状态**翻 ——
    /// 控件此刻的 IsChecked 已经被 WPF 翻过了，拿它当依据等于翻两次。
    /// </para>
    /// </summary>
    private void Mode_Clicked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;
        if (sender is not ToggleButton { Tag: string tag }) return;

        FullScreenAutoSwitch.View target = tag switch
        {
            "float" => FullScreenAutoSwitch.View.Float,
            "taskbar" => FullScreenAutoSwitch.View.Taskbar,
            _ => FullScreenAutoSwitch.View.Main,
        };

        bool wasShown = _ctx.IsViewShown(target);
        _ctx.ToggleView(target);
        SyncViewChips();

        if (wasShown)
        {
            Hint(target switch
            {
                FullScreenAutoSwitch.View.Float => "已收起悬浮窗",
                FullScreenAutoSwitch.View.Taskbar => "已收起任务栏条",
                _ => "已收起大窗口",
            });
        }
        else if (target == FullScreenAutoSwitch.View.Main)
        {
            // 大窗口 820 宽、设置页 800 宽，都是 CenterScreen —— 两个一起出现只会看到
            // 一个"点了没反应"的错觉（M3 时期就踩过）。所以显示大窗口时把设置页收起来。
            // 用 Hide 不用 Close：设置页的滚动位置、探针选择都还在，点 ⚙ 立刻回来。
            Hide();
        }
    }

    /// <summary>
    /// 把三个开关拉到真实可见性。凡是"视图可能被别人改动"的地方都要调一次：
    /// 打开设置页、点完开关、窗口重新激活（用户可能先去托盘点了一圈再回来）。
    /// </summary>
    private void SyncViewChips()
    {
        _suppress = true;

        ModeMain.IsChecked = _ctx.IsViewShown(FullScreenAutoSwitch.View.Main);
        ModeFloat.IsChecked = _ctx.IsViewShown(FullScreenAutoSwitch.View.Float);
        ModeTaskbar.IsChecked = _ctx.IsViewShown(FullScreenAutoSwitch.View.Taskbar);

        _suppress = false;
    }

    /// <summary>
    /// 探针：把三个开关的亮灭与真实可见性并排打出来。两边必须一致 ——
    /// 只打控件状态的话，"控件亮着但窗口早被全屏切换收走了"这种不同步根本看不出来。
    /// </summary>
    private void DumpViewChips()
    {
        System.Console.WriteLine(
            $"views: main {ModeMain.IsChecked}/live={_ctx.IsViewShown(FullScreenAutoSwitch.View.Main)} " +
            $"float {ModeFloat.IsChecked}/live={_ctx.IsViewShown(FullScreenAutoSwitch.View.Float)} " +
            $"taskbar {ModeTaskbar.IsChecked}/live={_ctx.IsViewShown(FullScreenAutoSwitch.View.Taskbar)}");
    }

    // ---- 悬浮窗外观 ----

    private void GlassSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        bool on = MainGlassSwitch.IsChecked == true;
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
    private (bool locked, bool through, bool topmost, bool autoFs)? _probeInteractBackup;

    private void BeginInteractProbeBackup()
    {
        if (_probeInteractBackup is not null) return;

        _probeInteractBackup = (_config.FloatLocked, _config.FloatClickThrough,
                                _config.FloatAlwaysOnTop, _config.FloatAutoThroughFullscreen);
        Application.Current.Exit += (_, _) => RestoreInteractProbe();
    }

    private void RestoreInteractProbe()
    {
        if (_probeInteractBackup is not { } backup) return;

        _config.FloatLocked = backup.locked;
        _config.FloatClickThrough = backup.through;
        _config.FloatAlwaysOnTop = backup.topmost;
        _config.FloatAutoThroughFullscreen = backup.autoFs;
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

    // ---- 任务栏条背景（M4.36）----

    /// <summary>正在编辑的背景两档（M4.37 起只有 透明 / 自定义）。初值取配置，<see cref="SyncBackdrop"/> 之后以窗口为准。</summary>
    private string _bgMode = "transparent";
    private string _bgColor = "#1B2127";
    private int _bgOpacity = 40;

    /// <summary>任务栏条毛玻璃（M4.41）。与上面三档正交，单独记住，<see cref="SyncBackdrop"/> 之后以窗口为准。</summary>
    private bool _trayGlass;
    private int _trayGlassOpacity = 45;

    /// <summary>程序自己给三个控件赋值时置位，免得回填被当成用户操作（那会把值又写回窗口）。</summary>
    private bool _bgSyncing;

    /// <summary>探针改背景之前的原值。模式 / 底色 / 不透明度 / 毛玻璃是一个整体，一起备份、一起还原。</summary>
    private (string mode, string color, int opacity, bool glass, int glassOpacity)? _probeBackdropBackup;

    /// <summary>
    /// 把三个控件对齐到**窗口当前的真实状态**，窗口没开就按配置。
    ///
    /// <para>
    /// 回填窗口而不是配置，理由与排列 / 边框相同：<c>--settings-probe</c>、托盘菜单都可能改过，
    /// 而用户看到的是屏幕上那条东西的样子。以配置为准会出现"设置页说自定义、条却是透明"。
    /// </para>
    /// </summary>
    private void SyncBackdrop()
    {
        TaskbarWindow? bar = _ctx.GetTaskbar();

        _bgMode = bar?.Backdrop.Mode ?? NormalizeBgMode(_config.TaskbarBackdrop);
        _bgColor = bar?.Backdrop.Color ?? NormalizeBgColor(_config.TaskbarBackdropColor);
        _bgOpacity = bar?.Backdrop.Opacity ?? Math.Clamp(_config.TaskbarBackdropOpacity, 0, 100);

        (bool on, int opacity) glass = bar?.Glass ?? (_config.TaskbarGlass,
                                                     Math.Clamp(_config.TaskbarGlassOpacity, 0, 100));
        _trayGlass = glass.on;
        _trayGlassOpacity = glass.opacity;

        _bgSyncing = true;
        try
        {
            (BarSegFor(_bgMode)).IsChecked = true;
            BgOpacitySlider.Value = _bgOpacity;
            TrayGlassSwitch.IsChecked = _trayGlass;
            TrayGlassSlider.Value = _trayGlassOpacity;
        }
        finally
        {
            _bgSyncing = false;
        }

        SyncBackdropPanel();
    }

    /// <summary>
    /// 三个控件与内部字段对齐：分段选中态、色板选中态、输入框、不透明度文字、以及"自定义"那一块能不能动。
    ///
    /// <para>
    /// 色板选中态不能靠 <c>Checked</c> 事件自己维持：色板是 RadioButton，
    /// 程序把某个 <c>IsChecked</c> 设 false 时 WPF 不会帮我们取消另一组的兄弟，
    /// 换底色（从输入框改）之后必须整条重扫一遍。
    /// </para>
    /// </summary>
    private void SyncBackdropPanel()
    {
        bool custom = _bgMode == "custom";

        // 毛玻璃开 + 透明档时，**底色仍然有用**：它就是磨砂的染色（M4.42），所以色板不灰。
        // 灰的只有「不透明度」—— 那条描述的是实色层有多实，磨砂档根本没有实色层。
        BgCustomPanel.IsEnabled = custom || _trayGlass;
        BgCustomPanel.Opacity = custom || _trayGlass ? 1.0 : 0.45;
        BgOpacityPanel.IsEnabled = custom;
        BgOpacityPanel.Opacity = custom ? 1.0 : 0.45;
        BgColorLabel.Text = custom ? "底色" : "玻璃色";

        if (BgHexBox.Text != _bgColor) BgHexBox.Text = _bgColor;
        BgOpacityText.Text = _bgOpacity + "%";

        TrayGlassPanel.IsEnabled = _trayGlass;
        TrayGlassPanel.Opacity = _trayGlass ? 1.0 : 0.45;
        TrayGlassText.Text = _trayGlassOpacity + "%";
        TrayGlassStateText.Text = _trayGlass ? "已开启" : "已关闭";

        _bgSyncing = true;
        try
        {
            foreach (object child in BgSwatches.Children)
            {
                if (child is RadioButton { Tag: string hex } chip)
                    chip.IsChecked = string.Equals(hex, _bgColor, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            _bgSyncing = false;
        }

        BgHint.Text = _bgMode switch
        {
            "custom" => _trayGlass
                ? "自定义底色同时当磨砂的染色：浓度管这层色有多浓，不透明度管底下还叠多少实色（越低越透出模糊）。文字颜色按这个底色判深浅。"
                : "自定义底色铺在字下面，不透明度管它有多实；0% 等于只剩一层极淡的玻璃，文字颜色仍按这个底色判深浅。",
            _ => _trayGlass
                ? "透明档 + 毛玻璃：只有一层磨砂，模糊后面的任务栏，再用上面那块「玻璃色」按浓度染上去。改玻璃色不会铺成实色 —— 想加实色请切「自定义」。"
                : "透明档不画任何底、也不做模糊，字直接浮在任务栏上。文字颜色仍按任务栏实际亮度自动判，可在下面「文字颜色」里改。",
        };
    }

    /// <summary>毛玻璃开关（M4.41）。挂 <c>Checked/Unchecked</c> 而不是 <c>Click</c>：
    /// 这个开关没有"程序回填自翻"的风险 —— 回填时 <see cref="_bgSyncing"/> 已经把事件挡掉了。</summary>
    private void TrayGlass_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress || _bgSyncing) return;

        _trayGlass = TrayGlassSwitch.IsChecked == true;
        PushTrayGlass(persist: true);
    }

    private void TrayGlass_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || _suppress || _bgSyncing) return;

        _trayGlassOpacity = (int)Math.Round(e.NewValue);
        TrayGlassText.Text = _trayGlassOpacity + "%";

        // 拖动中不写盘，松手补一次（同不透明度滑块）。
        PushTrayGlass(persist: false);
    }

    /// <summary>把开关 + 浓度一起推给窗口（没开就只改配置）。</summary>
    private void PushTrayGlass(bool persist)
    {
        TaskbarWindow? bar = _ctx.GetTaskbar();
        if (bar is not null)
        {
            bar.SetGlass(_trayGlass, _trayGlassOpacity, persist);
        }
        else
        {
            _config.TaskbarGlass = _trayGlass;
            _config.TaskbarGlassOpacity = _trayGlassOpacity;
            if (persist) ConfigStore.SaveCurrent();
        }

        SyncBackdropPanel();

        if (!persist) return;

        string where = bar is not null ? "任务栏条" : "配置";
        Hint(_trayGlass
            ? $"{where}毛玻璃已开启（浓度 {_trayGlassOpacity}%）"
            : $"{where}毛玻璃已关闭");
    }

    private RadioButton BarSegFor(string mode) => mode == "custom" ? BgCustom : BgTransparent;

    /// <summary>
    /// 归一化背景档位。旧的 <c>"auto"</c>（M4.36 的「跟随任务栏 = 垫对比底」）落到透明 ——
    /// 用户已明确两者是一个意思，垫底那套被砍掉了；认不出来的值同样走透明。
    /// </summary>
    private static string NormalizeBgMode(string? mode)
        => mode?.Trim().ToLowerInvariant() == "custom" ? "custom" : "transparent";

    private static string NormalizeBgColor(string? hex)
        => FloatInk.TryParse(hex, out Color c) ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : "#1B2127";

    private void Backdrop_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress || _bgSyncing) return;
        if (sender is not RadioButton { Tag: string tag }) return;

        _bgMode = NormalizeBgMode(tag);
        PushBackdrop(persist: true);
    }

    private void BgSwatch_Checked(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress || _bgSyncing) return;
        if (sender is not RadioButton { Tag: string hex }) return;

        _bgColor = NormalizeBgColor(hex);

        // 点色板本身就是"我要用自定义底"，不用再让人回去点一下「自定义」。
        // 例外：透明档 + 毛玻璃 —— 这块颜色这时是磨砂的染色（M4.42），
        // 挑个颜色不该顺手把"透明"变成"自定义"（那会平白多出一层实色）。
        if (!IsColorOnlyForGlass()) _bgMode = "custom";
        PushBackdrop(persist: true);
    }

    /// <summary>当前是不是「透明 + 毛玻璃」：此时底色只用来染磨砂，不铺实色。</summary>
    private bool IsColorOnlyForGlass() => _bgMode == "transparent" && _trayGlass;

    private void BgHexBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) ApplyBgHex();
    }

    private void ApplyBgHex_Click(object sender, RoutedEventArgs e) => ApplyBgHex();

    /// <summary>输入框里的自定义底色。写法不对要明说，不能静默退回上一个色 ——
    /// 用户会以为"我填的颜色生效了"，然后去调别的东西。</summary>
    private void ApplyBgHex()
    {
        string text = BgHexBox.Text.Trim();
        if (text.Length > 0 && !FloatInk.TryParse(text, out _))
        {
            Hint($"颜色写法不对：{text}（要 #RRGGBB，例如 #1B2127）");
            return;
        }

        _bgColor = NormalizeBgColor(text);

        // 同上：透明 + 毛玻璃时只换染色，不改成自定义档。
        if (!IsColorOnlyForGlass()) _bgMode = "custom";
        PushBackdrop(persist: true);
    }

    private void BgOpacity_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!_loaded || _suppress || _bgSyncing) return;

        _bgOpacity = (int)Math.Round(e.NewValue);
        BgOpacityText.Text = _bgOpacity + "%";

        // 拖动中不写盘：一次拖动会产生几十个值。松手由 OnMouseLeftButtonUp 补一次。
        PushBackdrop(persist: false);
    }

    /// <summary>
    /// 把模式 / 底色 / 不透明度一起推给窗口（没开就只改配置）。
    ///
    /// <para>
    /// 窗口那一支必须<b>三个值一起</b>传：底色与不透明度在画刷那里是一次合成，
    /// 分两次调用会让条闪两下。所以这里不提供单独的 setter。
    /// </para>
    /// </summary>
    private void PushBackdrop(bool persist)
    {
        TaskbarWindow? bar = _ctx.GetTaskbar();
        if (bar is not null)
        {
            bar.SetBackdrop(_bgMode, _bgColor, _bgOpacity, persist);
        }
        else
        {
            _config.TaskbarBackdrop = _bgMode;
            _config.TaskbarBackdropColor = _bgColor;
            _config.TaskbarBackdropOpacity = _bgOpacity;
            if (persist) ConfigStore.SaveCurrent();
        }

        SyncBackdropPanel();

        if (!persist) return;

        string where = bar is not null ? "任务栏条" : "配置";
        Hint(_bgMode switch
        {
            "custom" => $"{where}背景改为自定义 {_bgColor}（不透明度 {_bgOpacity}%）",
            _ => $"{where}背景改为透明（不留底，字直接浮在任务栏上）",
        });
    }

    private void BeginBackdropProbeBackup()
    {
        if (_probeBackdropBackup is not null) return;

        _probeBackdropBackup = (_config.TaskbarBackdrop, _config.TaskbarBackdropColor,
                                _config.TaskbarBackdropOpacity, _config.TaskbarGlass,
                                _config.TaskbarGlassOpacity);
        Application.Current.Exit += (_, _) => RestoreBackdropProbe();
    }

    private void RestoreBackdropProbe()
    {
        if (_probeBackdropBackup is not { } backup) return;

        _config.TaskbarBackdrop = backup.mode;
        _config.TaskbarBackdropColor = backup.color;
        _config.TaskbarBackdropOpacity = backup.opacity;
        _config.TaskbarGlass = backup.glass;
        _config.TaskbarGlassOpacity = backup.glassOpacity;
        _probeBackdropBackup = null;
        ConfigStore.SaveCurrent();
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
        FloatAutoThroughSwitch.IsChecked = f?.AutoThroughFullscreen ?? _config.FloatAutoThroughFullscreen;

        // 穿透状态下「锁定位置」是被动结果，置灰免得用户点了没反应
        FloatLockSwitch.IsEnabled = !through;
        _suppress = false;
    }

    /// <summary>
    /// 全屏自动穿透（M4.33）。默认开，因为它是修缺陷而不是加特性：
    /// 独占全屏的游戏里悬浮窗像素上不了屏、点击却还能被它吃掉，用户一点就掉出全屏。
    /// 留这个开关是为了"我就想在游戏里点它"的少数场景（比如配合录屏）。
    /// </summary>
    private void FloatAutoThroughSwitch_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_loaded || _suppress) return;

        bool on = FloatAutoThroughSwitch.IsChecked == true;
        FloatWindow? f = _ctx.GetFloat();

        if (f is not null) f.AutoThroughFullscreen = on;
        else
        {
            _config.FloatAutoThroughFullscreen = on;
            ConfigStore.SaveCurrent();
        }

        Hint(on
            ? "前台是铺满屏幕的游戏时，悬浮窗自动不接收鼠标（避免点一下就掉出全屏）"
            : "全屏游戏里悬浮窗仍然可点 —— 如果你看不见它，点击会直接把游戏踢出全屏");
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
