using System.Windows;
using CoPul.App.Interop;

namespace CoPul.App.Sdk;

/// <summary>App 级主题（大窗口 + 设置页）。悬浮窗有自己的玻璃主题，不在此列。</summary>
public enum AppTheme
{
    /// <summary>白色（默认）。</summary>
    White,
    /// <summary>深色。</summary>
    Dark,
}

/// <summary>
/// App 级主题的加载与**运行时切换**（大窗口 + 设置页）。
///
/// <para>
/// <b>为什么切换能做到"即时生效、不重建窗口"。</b>靠的是把两套颜色做成两本**键名完全相同**的
/// 资源字典（PaletteDark / PaletteLight），切换时只替换 <see cref="Application.Resources"/> 里
/// 那**一本**，XAML 里所有颜色引用都是 <c>DynamicResource</c>（会跟随资源查找重新解析）。
/// </para>
///
/// <para>
/// <b>为什么必须全量 DynamicResource。</b>方案定下来之前单独做过验证（<c>tools/theme-probe/</c>）：
/// 390 个元素 × 2 个动态绑定，整本替换后颜色立刻变，耗时 <b>7.31 ms</b>；
/// 而同一场景下 <c>StaticResource</c> 静态取到的画刷实例**完全不跟随**字典替换。
/// 所以漏改一处就是"切换后那一处颜色不变"，这是本方案唯一的机械风险点。
/// </para>
///
/// <para>
/// <b>为什么只换主题那一本、不整本清空。</b><see cref="Application.Resources"/> 里除了主题字典，
/// 还挂着 Styles（里面合并了 PaletteShared 的玻璃色）与 Icons。整本清空会把它们一起丢掉，
/// 切换瞬间所有窗口都会找不到样式而大面积回退灰色。所以给主题字典打一个标记键
/// <c>__theme</c>，按标记定位、只替换它。
/// </para>
/// </summary>
public static class ThemeManager
{
    /// <summary>主题字典里的标记键。值是 "dark" / "light"。不供 XAML 使用。</summary>
    private const string MarkerKey = "__theme";

    private const string DarkThemeMarker = "dark";
    private const string LightThemeMarker = "light";

    /// <summary>当前生效的主题。启动前默认白色（与 <see cref="AppConfig.AppTheme"/> 默认值一致）。</summary>
    public static AppTheme Current { get; private set; } = AppTheme.White;

    /// <summary>
    /// 主题切换完成后的通知。视图层若持有"主题派生状态"（如投影效果对象、间距）应在这里刷新。
    /// <para>
    /// 为什么需要它：颜色走 DynamicResource 自动跟随，但**非颜色**的东西（<c>DropShadowEffect</c>
    /// 的效果对象、<c>Thickness</c> 间距）不是资源查找能解决的，必须由代码重建。
    /// </para>
    /// </summary>
    public static event Action? Changed;

    /// <summary>
    /// 装上一本主题字典。**必须在任何窗口创建之前**调用（App.OnStartup 最前面），
    /// 否则窗口首次资源查找找不到主题键，会一路回退到 <see cref="Palette"/> 的灰色兜底。
    /// </summary>
    public static void Apply(AppTheme theme, bool persist = true)
    {
        Current = theme;

        if (Application.Current is { } app)
        {
            // 先移除已有的主题字典（按标记找，不碰 Styles / Icons）
            RemoveThemeDictionary(app);

            // 再装新的。位置放在最前面：主题键要能被 Styles 内部（已合并 Shared）之后的查找命中。
            var dict = new ResourceDictionary
            {
                Source = new Uri(
                    theme == AppTheme.Dark
                        ? "pack://application:,,,/CoPul;component/Themes/PaletteDark.xaml"
                        : "pack://application:,,,/CoPul;component/Themes/PaletteLight.xaml"),
            };
            app.Resources.MergedDictionaries.Insert(0, dict);
        }

        // 关键：不清缓存的话 Palette.Brush 会一直返回旧主题的画刷，
        // 表现为"切了主题，但数值颜色还是旧的"（颜色缓存按 key 存，不认主题）。
        Palette.InvalidateCache();

        if (persist)
        {
            ConfigStore.Current.AppTheme = theme == AppTheme.Dark ? "Dark" : "White";
            ConfigStore.SaveCurrent();
        }

        Trace.Log("theme", $"apply {theme} persist={persist}");
        Changed?.Invoke();
    }

    /// <summary>从配置读出的主题名 → 枚举。认不出来的值一律回默认白色（不崩、不阻断启动）。</summary>
    public static AppTheme Parse(string? name)
        => string.Equals(name, "Dark", StringComparison.OrdinalIgnoreCase) ? AppTheme.Dark : AppTheme.White;

    /// <summary>启动时按配置应用一次（不重复写盘，避免每次启动都覆盖用户文件）。</summary>
    public static void ApplyFromConfig() => Apply(Parse(ConfigStore.Current.AppTheme), persist: false);

    /// <summary>当前主题的人话名字，设置页与诊断日志共用。</summary>
    public static string DisplayName(AppTheme theme) => theme == AppTheme.Dark ? "深色" : "白色";

    /// <summary>按标记键移除主题字典。没有标记的字典（Styles / Icons）一概不动。</summary>
    private static void RemoveThemeDictionary(Application app)
    {
        var found = new List<ResourceDictionary>();
        foreach (ResourceDictionary d in app.Resources.MergedDictionaries)
        {
            if (d.Contains(MarkerKey)) found.Add(d);
        }

        // 先收集再删：边遍历边 Remove 会跳过元素
        foreach (ResourceDictionary d in found) app.Resources.MergedDictionaries.Remove(d);
    }

    /// <summary>
    /// 当前主题的原始标记（"dark"/"light"），供探针核对"装的是不是我以为的那一本"。
    /// 直接读字典里的标记键而不是读 <see cref="Current"/> —— 后者只是内存状态，
    /// 万一装错了字典，两者会不一致，而这才是有价值的诊断信息。
    /// </summary>
    public static string ActualMarker
    {
        get
        {
            if (Application.Current is not { } app) return "(no-app)";
            foreach (ResourceDictionary d in app.Resources.MergedDictionaries)
            {
                if (d.Contains(MarkerKey)) return d[MarkerKey]?.ToString() ?? "(null)";
            }
            return "(none)";
        }
    }
}
