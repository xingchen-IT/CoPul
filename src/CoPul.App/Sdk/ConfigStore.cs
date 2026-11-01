using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace CoPul.App.Sdk;

/// <summary>
/// 配置层（需求 3.1 / A-07）。JSON 落盘 + **临时文件 + 原子替换**，
/// 保证崩溃或断电时不会留下半截 JSON 导致下次启动直接读不出来。
///
/// 位置固定为 <c>%APPDATA%\CoPul\config.json</c>：不写注册表，也不放 exe 目录——
/// 后者在 Program Files 下没有写权限，绿色版放 U 盘又会把配置带走去别的机器。
///
/// v1 只有窗口几何与折叠状态；挡位（Q-23）、毛玻璃开关（5.5）、自启（M4）后续加。
/// **未知字段会被忽略、缺字段取默认值**，所以旧版本配置永远能读，不需要 migration。
/// </summary>
public sealed class AppConfig
{
    // ---- 窗口几何 ----
    // 存的是 Window.RestoreBounds，不是 Window.Left/Top：最大化时 Left/Top 是最大化后的值，
    // 直接存会让"最大化状态下退出 → 重开 → 再还原"跳到一个奇怪的尺寸。
    public double WindowLeft { get; set; } = double.NaN;
    public double WindowTop { get; set; } = double.NaN;
    public double WindowWidth { get; set; } = 820;

    /// <summary>
    /// 窗口高（DIP）。**0 = 未设置**，表示"首次启动用满工作区高度"（A-32）。
    ///
    /// 为什么不再写死一个数字：内容自然高度是 1502 DIP（M1.5 用 ScrollViewer.ExtentHeight 实测），
    /// 而 150% 缩放的 2560×1600 屏工作区只有 1019——9 个区块在任何常见屏幕上都不可能一屏放下。
    /// 写死 1460 只有"开发机刚好被系统钳到 1019"这一种巧合下看着对，换台机器不是超屏就是留白。
    /// 现在默认行为是"占满屏幕高度、内部滚动"，加区块也不必再回来改这个值。
    /// </summary>
    public double WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>折叠的区块 id：<c>"cpu"</c> / <c>"gpu"</c> / <c>"mem"</c> / <c>"disk"</c>（5.4 要求持久化）。</summary>
    public string[] CollapsedBlocks { get; set; } = Array.Empty<string>();

    // ---- 悬浮窗外观（M2）----
    // 原来主题与浓度只活在 FloatWindow 的字段里，重启就回到「浅色 + 适中」，
    // 用户每次开机都要重新点一遍，等于没有这个功能。

    /// <summary>
    /// App 级主题（大窗口 + 设置页，2026-11-01）：<c>"White"</c> / <c>"Dark"</c>。
    ///
    /// <para>
    /// <b>与 <see cref="FloatTheme"/> 是两回事</b>：这个管大窗口与设置页的纸面颜色，
    /// 那个管悬浮窗玻璃的深浅。故意分开 —— 用户可能想让大窗口白底、但悬浮窗深色玻璃，
    /// 合并成一个字段就做不到了。默认白色。
    /// </para>
    /// <para>
    /// 同样存字符串而不是枚举：枚举序号插入新值会整体错位，字符串认不出来时能安全回退默认。
    /// </para>
    /// </summary>
    public string AppTheme { get; set; } = "White";

    // ---- 大窗口毛玻璃（2026-11-01）----
    // 与悬浮窗的 FloatGlassLevel 是两套：那个管 34 DIP 长条，这个管 820×1019 的大窗口。
    // 合并成一个会让"悬浮窗调透一点"顺手把大窗口也调透，而两者可用的浓度范围并不相同。

    /// <summary>大窗口毛玻璃开关。默认**关** —— 第一次见面保持原来的纸面观感，
    /// 让用户自己决定要不要玻璃（与悬浮窗默认开不同：那个生来就是玻璃条）。</summary>
    public bool GlassEnabled { get; set; }

    /// <summary>大窗口毛玻璃浓度 0–100（0 = 最透 17% 遮盖，100 = 最实 98%）。</summary>
    public int GlassLevel { get; set; } = 60;

    /// <summary>卡片是否也跟着半透。关掉时只有卡片缝与窗口边缘能看到玻璃。</summary>
    public bool GlassCards { get; set; }

    /// <summary>玻璃主题：<c>"Light"</c> / <c>"Dark"</c>。存字符串而不是枚举——
    /// 枚举序号会在插入新值时整体错位，字符串认不出来时还能安全回退默认值。</summary>
    public string FloatTheme { get; set; } = "Light";

    /// <summary>浓度档 1–7（1 最透）。越界由 FloatWindow 夹取，不抛异常。</summary>
    public int FloatGlassLevel { get; set; } = 4;

    /// <summary>毛玻璃开关（右键菜单里那一项）。</summary>
    public bool FloatGlassEnabled { get; set; } = true;

    /// <summary>位置锁定（6.5）：锁定后拖动无效，防止误碰挪走。</summary>
    public bool FloatLocked { get; set; }

    /// <summary>
    /// 鼠标穿透（M4.21）：悬浮窗不接收任何鼠标消息，点它等于点它下面的东西。
    ///
    /// <para>
    /// 默认关 —— 穿透之后右键菜单也收不到了（消息根本不投递过来），
    /// 而菜单是悬浮窗唯一的出口。打开的入口在右键菜单 / 设置页 / 托盘图标三处，
    /// 关掉它的出口也就必须在这三处都在，缺一个都会把用户困在"悬浮窗摸不到"的状态里。
    /// </para>
    /// </summary>
    public bool FloatClickThrough { get; set; }

    /// <summary>
    /// 始终置顶（M4.21）：每秒把悬浮窗提回 topmost 组最顶。默认开。
    ///
    /// <para>
    /// 本来 XAML 已经是 <c>Topmost="True"</c>，但那只保证"进 topmost 组"，
    /// 不保证"排在组里最前"：另一个 topmost 窗口（游戏浮层、录屏工具条）一激活就会压到它上面，
    /// 而没有任何事件通知我们。所以只能巡检。
    /// </para>
    /// </summary>
    public bool FloatAlwaysOnTop { get; set; } = true;

    /// <summary>
    /// 界面缩放档位（Q-05）：100 / 125 / 150。存整数百分比而不是枚举，
    /// 与配置里其它"人能读懂的数值"保持一致；越界由 <see cref="CoPul.App.FloatWindow"/> 夹取。
    /// </summary>
    public int FloatScale { get; set; } = 100;

    /// <summary>
    /// 悬浮窗排列方向（M4.10）：<c>false</c> = 横向单行长条（6.1 默认），<c>true</c> = 竖向。
    ///
    /// <para>
    /// 存 bool 不存字符串：这里只有两个方向、不会再有第三种（横向已经是"一行放不下就换行"
    /// 之外的唯一合理形态），再多一个枚举值只会让配置里多一个没人认得的字符串。
    /// </para>
    /// </summary>
    public bool FloatVertical { get; set; }

    /// <summary>
    /// 悬浮窗常态描边的档位（M4.15）：<c>none</c> / <c>faint</c> / <c>normal</c>。
    ///
    /// <para>
    /// 会用到它是因为"描边要多亮"没有唯一正确答案：深色玻璃上要勾出边界就得叠白，
    /// 而叠白在深色桌面上就是一圈显眼的白边（实测亮度 50 vs 条内 16）。
    /// 与其替用户决定，不如让他自己挑。
    /// </para>
    /// </summary>
    public string FloatEdgeMode { get; set; } = "normal";

    /// <summary>
    /// 悬浮窗**逐字段**的文字颜色（M4.20）：字段名 → <c>#RRGGBB</c>。
    /// 只在表里出现的才是被改过的，缺 = 「自动」（跟随玻璃主题）。key 见 <see cref="FloatFields.Keys"/>。
    ///
    /// <para>
    /// 不存「九项全写一遍」：那样看不出改了哪几项，而且以后加字段要给老配置补种一遍。
    /// 缺项即默认值，这条规则让加字段变成零成本。
    /// </para>
    /// </summary>
    public Dictionary<string, string> FloatFieldColors { get; set; } = FloatInk.NewMap();

    /// <summary>
    /// 任务栏条**逐字段**的文字颜色（M4.20）。与上面那份各存各的：
    /// 两处字段名不同（悬浮窗有 <c>net</c>、任务栏有 <c>lat</c>），
    /// 而且底色一个是我们的玻璃、一个是系统任务栏 —— 同一个色在两边不见得都可读。
    /// key 见 <see cref="TrayFields.Keys"/>。
    /// </summary>
    public Dictionary<string, string> TrayFieldColors { get; set; } = FloatInk.NewMap();

    /// <summary>
    /// 自定义文字色时，数值到了阈值是否仍然变黄 / 变红（M4.19，M4.20 起两视图共用）。默认**开**。
    ///
    /// <para>
    /// 单色会盖掉"这个数字现在不正常"这层信息 —— 温度 85°C 和 40°C 都是同一个颜色，
    /// 悬浮窗和任务栏本来都是扫一眼看的，颜色是唯一不用读数的提示。所以默认保留阈值色，
    /// 真要"整条一个颜色"由用户自己关。
    /// </para>
    /// </summary>
    public bool TextKeepThreshold { get; set; } = true;

    /// <summary>
    /// <b>旧版</b>（M4.19）的"整条一个色"。M4.20 起读一次就迁移到
    /// <see cref="FloatFieldColors"/>，之后不再写 —— 留着这个属性只是为了不丢老配置。
    /// </summary>
    public string FloatTextColor { get; set; } = FloatInk.Auto;

    /// <summary><b>旧版</b>（M4.19）的阈值开关。同样只读一次就迁移到 <see cref="TextKeepThreshold"/>。</summary>
    public bool FloatTextKeepThreshold { get; set; } = true;

    /// <summary>
    /// 悬浮窗里被**用户关掉**的字段（M4.14）。key 见 <see cref="FloatFields.Keys"/>。
    ///
    /// <para>
    /// 与 <see cref="TaskbarHiddenFields"/> 各存一份：两处显示的内容与宽度规则都不同，
    /// 共用一个列表会出现"关掉任务栏的硬盘温度，悬浮窗的 FPS 也跟着没了"这种莫名其妙。
    /// 同样存"关掉的"：以后加字段默认可见，不需要迁移老配置。
    /// </para>
    /// </summary>
    public string[] FloatHiddenFields { get; set; } = Array.Empty<string>();

    /// <summary>
    /// 任务栏模式的文字取色策略（7.5）：<c>Auto</c> / <c>Light</c> / <c>Dark</c>。
    /// Auto 读 <c>SystemUsesLightTheme</c> 自动决定，另两个是用户手动锁定 ——
    /// Win11 任务栏是半透明的，实际明暗取决于壁纸，注册表读数未必与眼睛看到的一致。
    /// </summary>
    public string TaskbarInk { get; set; } = "Light";

    /// <summary>
    /// 任务栏条是否点击穿透（7.6 / Q-12 采纳方案 2）。默认**穿透**：
    /// 条只负责显示，任何鼠标操作走托盘右键，符合"不抢占"原则。
    ///
    /// <para>
    /// 保留这个开关是因为穿透之后条上收不到右键，文字颜色就调不了了 ——
    /// 用户可以从托盘菜单临时把它关掉，调完再打开。
    /// </para>
    /// </summary>
    public bool TaskbarClickThrough { get; set; } = true;

    /// <summary>
    /// 任务栏条里被**用户关掉**的字段（7.4 / Q-03：九字段可独立开关、默认全开，
    /// 关闭后宽度自适应收缩、不保留空位）。
    ///
    /// <para>
    /// 存"关掉的"而不是"打开的"：以后新增字段默认可见，不需要写迁移逻辑把老用户补全。
    /// key 见 <see cref="TrayFields.Keys"/>。
    /// </para>
    /// </summary>
    public string[] TaskbarHiddenFields { get; set; } = Array.Empty<string>();

    /// <summary>
    /// N03 延迟探测目标（G-08 设置页可改）。
    ///
    /// <para>
    /// 默认 223.5.5.5 是 Q-09 定的（阿里 DNS）。用户可以改成网关或自定义地址 ——
    /// 内网环境 ping 公网 DNS 往往超时，延迟一格恒为 <c>—</c>，改成本机网关才有意义。
    /// </para>
    ///
    /// <para>
    /// 空值由采集侧的 <c>OsProvider</c> 回退默认，不在这里校验 ——
    /// 配置被手改坏也不该让延迟整格变成 <c>—</c>。
    /// </para>
    /// </summary>
    public string PingTarget { get; set; } = "223.5.5.5";

    // ---- 全局热键（G-03）----

    /// <summary>
    /// 循环切换三视图的全局热键，写法如 <c>Ctrl+Alt+C</c>。
    ///
    /// <para>
    /// 存字符串而不是"修饰键位 + 虚拟键码"两个整数：人能读懂、能手改，
    /// 而且解析失败时能原样打印出来告诉用户"你写的这个我不认识"。
    /// 认不出来的写法由 <c>HotkeyGesture.TryParse</c> 兜住，不影响启动。
    /// </para>
    /// </summary>
    public string Hotkey { get; set; } = HotkeyGesture.Default;

    // ---- 数据刷新（4.0 用户挡位 / M4.24）----

    /// <summary>
    /// CPU/GPU/内存等数据的刷新间隔（ms）。默认 500，可选 1000/2000/3000/5000。
    ///
    /// <para>
    /// 实际生效间隔 = max(本档位, 各指标自身的 floor)，所以**调慢一定生效，调快受 floor 限制**——
    /// 写 50 也不会变成 20Hz，被 floor 钳住。这一层不拒绝非法值，
    /// 由 <see cref="App.Sdk.CollectorHost.NormalizeInterval"/> 归一到最接近的合法档。
    /// </para>
    ///
    /// <para>
    /// 调慢的意义在 A-26 那一路：占用与刷新频率近似成正比，5s 档能把采集开销压到 0.5s 档的
    /// 十分之一量级。代价是数值"跳"得慢，趋势图（定长 120 点）覆盖的时间也按比例变长。
    /// </para>
    /// </summary>
    public int SampleIntervalMs { get; set; } = 500;

    // ---- 全屏自动切换（第 8 章 / F-11）----

    /// <summary>
    /// 全屏自动切换开关（F-11）。默认**开**：A 方案下任务栏条是 topmost 顶层窗口，
    /// 关掉它就等于让条盖在全屏游戏画面上（见 7.1 的 A 方案说明）。
    ///
    /// <para>
    /// 留这个开关是因为它有个用户可能真的想要的边界情况：<c>QUNS_BUSY</c> 的判定范围比
    /// "独占全屏游戏"宽（PPT 放映、浏览器 F11 也算，8.5 已定不做白名单），
    /// 有人就是希望看视频全屏时条还留着。
    /// </para>
    /// </summary>
    public bool FullScreenAutoSwitch { get; set; } = true;

    /// <summary>
    /// 全屏切换的防抖档位（ms），进入与退出**共用**这一个值。默认 1000，可选 500/2000/3000。
    ///
    /// <para>
    /// 调长的唯一理由：判定主体是前台窗口，切屏、弹窗、Alt+Tab 那一瞬间都可能短暂"像全屏"。
    /// 档位越长越稳（实测 2s/3s 挡得住 1.5s 的"切出去又回来"），代价是切换反应也跟着变慢。
    /// 只给一个数：这两个数怎么配合只有"快 vs 稳"一个取舍，拆成两个反而要用户替我们做设计。
    /// </para>
    /// </summary>
    public int FullScreenHoldMs { get; set; } = 1000;

    // ---- 悬浮窗位置（6.7）----
    // 存「显示器 + 相对比例」而不是绝对像素：换分辨率、改缩放、拔插显示器后，
    // 绝对坐标会把窗口扔到屏幕外。三个字段是一个整体，Monitor 为空即视为"从未摆放过"。

    /// <summary>显示器设备名（如 <c>\\.\DISPLAY1</c>）。空 = 未记录过位置。</summary>
    public string FloatMonitor { get; set; } = string.Empty;

    /// <summary>窗口左上角相对该显示器**工作区**（已扣任务栏）的横向比例，0–1。</summary>
    public double FloatRatioX { get; set; } = double.NaN;

    /// <summary>纵向比例，0–1。</summary>
    public double FloatRatioY { get; set; } = double.NaN;

    /// <summary>
    /// 配置结构版本。1 = M4.17 及以前；2 = M4.18 起（悬浮窗九字段，新增三项默认关闭）；
    /// 3 = M4.20 起（文字色改成逐字段，旧版的"整条一个色"迁移进 <see cref="FloatFieldColors"/>）。
    /// </summary>
    public int ConfigVersion { get; set; } = 1;

    /// <summary>读配置时遇到的异常（读不出来就用默认值，不弹窗、不阻断启动）。诊断用。</summary>
    [JsonIgnore]
    public string? LoadError { get; set; }
}

/// <summary>配置文件的读写。单文件、单进程，不加锁。</summary>
public static class ConfigStore
{
    private const string FolderName = "CoPul";
    private const string FileName = "config.json";

    /// <summary>
    /// 全进程共享的唯一一份配置。
    ///
    /// <para>
    /// 为什么必须是单例：<see cref="MainWindow"/> 与 <see cref="FloatWindow"/> 都会改配置
    /// （大窗口存几何、悬浮窗存外观与位置），而 <see cref="Save"/> 是整份覆盖写。
    /// 两边各 <c>Load</c> 一份的话，后写的那个会把它读到的旧值盖回去 ——
    /// 表现为"刚调好的浓度，关掉大窗口又变回去了"。
    /// </para>
    /// </summary>
    public static AppConfig Current => _current ??= Normalize(MigrateTextColor(SeedFields(LoadFromDisk())));

    private static AppConfig? _current;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        // 允许注释与尾逗号：用户手改配置时不至于因为多一个逗号就打不开
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    /// <summary>配置文件的完整路径。<c>%APPDATA%\CoPul\config.json</c>。</summary>
    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), FolderName, FileName);

    /// <summary>
    /// M4.18 新增字段的播种：把 <see cref="FloatFields.DefaultHidden"/> 里那几项写进
    /// <c>FloatHiddenFields</c>，只做一次（<c>ConfigVersion</c> 顶到 2 之后不再动）。
    ///
    /// <para>
    /// 为什么要"默认关"：老用户升级上来如果三项全开，横向条从 381 直接长到 509 DIP、
    /// 竖向从 272 长到 400 多 —— 那是没打招呼就把界面改了。想要哪条自己勾开。
    /// </para>
    /// </summary>
    private static AppConfig SeedFields(AppConfig config)
    {
        if (config.ConfigVersion >= 2) return config;

        var hidden = new List<string>(config.FloatHiddenFields ?? Array.Empty<string>());
        foreach (string key in FloatFields.DefaultHidden)
        {
            if (!hidden.Contains(key, StringComparer.OrdinalIgnoreCase)) hidden.Add(key);
        }

        config.FloatHiddenFields = hidden.ToArray();
        config.ConfigVersion = 2;
        return config;
    }

    /// <summary>
    /// M4.20 的文字色迁移：旧版的"整条一个色"铺到悬浮窗九项上，阈值开关换个名字继续用。
    ///
    /// <para>
    /// 只铺给悬浮窗：M4.19 那个色本来就只作用于悬浮窗，任务栏那时候没有自定义色，
    /// 凭空给它染一遍等于替用户改了一个他没改过的视图。
    /// 迁移完把旧字段清空 —— 配置里留一个"看着像在生效、其实没人读"的值，
    /// 下次排查会先被它骗一次。
    /// </para>
    /// </summary>
    private static AppConfig MigrateTextColor(AppConfig config)
    {
        if (config.ConfigVersion >= 3) return config;

        string legacy = FloatInk.Normalize(config.FloatTextColor);
        if (!FloatInk.IsAuto(legacy) && config.FloatFieldColors.Count == 0)
        {
            foreach (string key in FloatFields.Keys) FloatInk.Set(config.FloatFieldColors, key, legacy);
        }

        // 旧开关只在"用户明确关掉"时才和默认值不同，所以只有 false 需要搬过来
        if (!config.FloatTextKeepThreshold) config.TextKeepThreshold = false;

        config.FloatTextColor = FloatInk.Auto;
        config.ConfigVersion = 3;
        return config;
    }

    /// <summary>
    /// 把两张配色表收拾成"可直接用"的样子：字典换回不区分大小写的比较器、
    /// 写法统一成 <c>#RRGGBB</c>、值为「自动」的占位项丢掉。
    ///
    /// <para>
    /// 为什么必须重造一遍：<c>System.Text.Json</c> 反序列化出来的字典用的是<b>默认</b>比较器，
    /// 属性初始值里那个 <c>OrdinalIgnoreCase</c> 根本传不进去 ——
    /// 而字段名是用户手改配置时最爱写错大小写的地方（<c>FPS</c> vs <c>fps</c>）。
    /// </para>
    /// </summary>
    private static AppConfig Normalize(AppConfig config)
    {
        config.FloatFieldColors = Rekey(config.FloatFieldColors);
        config.TrayFieldColors = Rekey(config.TrayFieldColors);
        return config;
    }

    private static Dictionary<string, string> Rekey(Dictionary<string, string>? src)
    {
        var map = FloatInk.NewMap();
        if (src is null) return map;

        foreach (KeyValuePair<string, string> kv in src) FloatInk.Set(map, kv.Key, kv.Value);
        return map;
    }

    /// <summary>从磁盘读一次。外部请用 <see cref="Current"/>，不要绕过缓存。</summary>
    private static AppConfig LoadFromDisk()
    {
        string path = Path;
        try
        {
            if (!File.Exists(path)) return new AppConfig();

            string json = File.ReadAllText(path);
            if (string.IsNullOrWhiteSpace(json)) return new AppConfig();
            return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        }
        catch (Exception ex)
        {
            // 配置坏了不能影响启动：返回默认值并记下原因，UI 可在诊断页展示。
            return new AppConfig { LoadError = $"配置读取失败，已用默认值：{ex.GetBaseException().Message}" };
        }
    }

    /// <summary>
    /// A-07：先写 <c>config.json.tmp</c> 并落盘，再用 <see cref="File.Replace"/> 原子替换。
    ///
    /// 为什么不能直接 <c>File.WriteAllText</c>：那个调用在写了一半时断电/被杀进程，
    /// 留下的就是一个语法不完整的 JSON，下次启动 <see cref="LoadFromDisk"/> 抛异常 —— 用户表现为
    /// "设置一直保存不上"。Replace 在 NTFS 上是原子的，要么旧文件、要么新文件。
    ///
    /// 只在退出与用户改设置时调用，不进采集循环（11 章：配置写入频率仅退出/变更时）。
    /// </summary>
    /// <returns>true = 已落盘；false = 失败（已吞掉异常，调用方不必处理）。</returns>
    public static bool Save(AppConfig config)
    {
        string path = Path;
        string temp = path + ".tmp";
        try
        {
            string? dir = System.IO.Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            // 直接序列化整份配置。以前这里手抄了一个 snapshot 白名单，结果是每次加新字段
            // 都得记得同步改两处，漏了就是"设置存不上"且没有任何报错（M2 差点踩第二次）。
            // LoadError 标了 [JsonIgnore]，本来就不会落盘 —— 以后新增的运行时字段照此办理。
            string json = JsonSerializer.Serialize(config, JsonOptions);

            // 必须显式 Flush(true)：只写进 OS 缓存就返回的话，"原子替换"换上去的可能还是空内容。
            using (var fs = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None))
            using (var writer = new StreamWriter(fs))
            {
                writer.Write(json);
                writer.Flush();
                fs.Flush(flushToDisk: true);
            }

            if (File.Exists(path))
            {
                // File.Replace 需要一个存在的目标；备份文件传 null 表示不留 .bak
                File.Replace(temp, path, destinationBackupFileName: null, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temp, path);
            }
            return true;
        }
        catch
        {
            try { if (File.Exists(temp)) File.Delete(temp); } catch { /* 清不掉也无所谓 */ }
            return false;
        }
    }

    /// <summary>把共享实例落盘。悬浮窗改一次外观调一次，不需要自己拿配置引用。</summary>
    public static bool SaveCurrent() => Save(Current);
}
