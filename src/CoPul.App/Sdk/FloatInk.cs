using System.Globalization;
using System.Linq;
using System.Windows.Media;

namespace CoPul.App.Sdk;

/// <summary>
/// 悬浮窗文字颜色的写法与换算（M4.19）。
///
/// <para>
/// 配置里存 <c>#RRGGBB</c> 字符串，<b>空串 = 「自动」</b>（跟随玻璃主题：浅色玻璃用深字、
/// 深色玻璃用浅字）。存字符串而不是 <c>Color</c>：人能读懂、能手改，而且解析失败时
/// 只要退回「自动」即可，不会因为一条坏数据就让整个悬浮窗没字。
/// </para>
///
/// <para>
/// 为什么"自动"是默认而不是某个固定色：悬浮窗的底色有七档浓度 × 深浅两档主题，
/// 换一档对比度就变一次。原来那套是配色表里配好的（浅底 <c>BrushTextOnGlass</c> /
/// 深底 <c>BrushTextPrimary</c>），任何单一固定色都只在其中一部分组合上可读。
/// 用户想改的是"我想让它是某个颜色"，那就得自己挑、自己看 —— 设置页给预览就是这个用途。
/// </para>
/// </summary>
public static class FloatInk
{
    /// <summary>「跟随玻璃主题」的写法。空串、null、<c>auto</c> 都认。</summary>
    public const string Auto = "";

    /// <summary>
    /// 次值（小字）相对主值的浓度。
    ///
    /// <para>
    /// 自定义色是"一个色管两段字"，但两段字不能一样重：主值 13px 粗、次值 10px 常规，
    /// 原来两套色也是一深一浅（<c>#1B2027</c> / <c>#5C6672</c>）。同色不同浓度既跟着改，
    /// 又保住"先看大数字"这个层次 —— 全同色会让条看起来像糊成一片。
    /// </para>
    /// </summary>
    private const byte SubAlpha = 0xC8;   // 78%

    /// <summary>
    /// 预设色板。低饱和、与 10.3「禁止霓虹色」一致；前三个是中性色（用得最多），
    /// 后面五个取自配色表里已有的语义色，改过去不会和界面打架。
    /// </summary>
    public static readonly (string Label, string Hex)[] Presets =
    {
        ("白",     "#FFFFFF"),
        ("浅灰",   "#D8DEE7"),
        ("银灰",   "#9AA4B2"),
        ("墨黑",   "#1B2027"),
        ("青",     "#4FB3A6"),
        ("蓝",     "#5B8FF9"),
        ("琥珀",   "#E0A44A"),
        ("藕粉",   "#E8849B"),
    };

    /// <summary>解析 <c>#RRGGBB</c>（# 可省）。认不出返回 false，不让坏值进画刷。</summary>
    public static bool TryParse(string? text, out Color color)
    {
        color = Colors.Transparent;

        string hex = (text ?? string.Empty).Trim().TrimStart('#');
        if (hex.Length != 6) return false;
        if (!int.TryParse(hex, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb)) return false;

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>
    /// 规范化成 <c>#RRGGBB</c>；认不出来（含 <c>auto</c> / 空）一律退回 <see cref="Auto"/>。
    ///
    /// <para>
    /// 大小写与 <c>#</c> 都在这里抹平：用户在输入框里写 <c>ff7fb8</c> 和 <c>#FF7FB8</c>
    /// 应当是同一个颜色，否则配置里会留下两种写法，排查时还要再猜一次。
    /// </para>
    /// </summary>
    public static string Normalize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return Auto;
        if (text.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase)) return Auto;
        if (!TryParse(text, out Color c)) return Auto;

        return "#" + c.R.ToString("X2") + c.G.ToString("X2") + c.B.ToString("X2");
    }

    /// <summary>是否「自动」（跟随主题）。</summary>
    public static bool IsAuto(string? hex) => string.IsNullOrEmpty(hex);

    /// <summary>建一张"九个字段全是自动"的空表。只存被改过的字段，方便一眼看出改了哪些。</summary>
    public static Dictionary<string, string> NewMap()
        => new(StringComparer.OrdinalIgnoreCase);

    /// <summary>取一个字段的配色写法；没有单独设过就是「自动」。</summary>
    public static string Get(IReadOnlyDictionary<string, string>? map, string key)
        => map is not null && map.TryGetValue(key, out string? hex)
            ? Normalize(hex)
            : Auto;

    /// <summary>写回一个字段的配色。「自动」的条目不留占位，配置里只留被改过的。</summary>
    public static void Set(Dictionary<string, string> map, string key, string? hex)
    {
        string normalized = Normalize(hex);
        if (IsAuto(normalized)) map.Remove(key);
        else map[key] = normalized;
    }

    /// <summary>
    /// 两个视图的字段名**并不相同**（悬浮窗有 <c>net</c>、任务栏有 <c>lat</c>），
    /// 但底层是同一批 <see cref="FloatField"/>（<c>Net</c> / <c>Lat</c> 都绑网络延迟）。
    /// 取并集是为了"没写字段名 = 全部"这种写法在两边都对得上。
    /// </summary>
    public static readonly string[] AllKeys = FloatFields.Keys
        .Union(TrayFields.Keys, StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static readonly char[] Separators = { ',', ';' };

    /// <summary>是不是一个存在的字段名。写错字段名不算"改了这一格"。</summary>
    public static bool IsFieldKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        foreach (string k in AllKeys)
        {
            if (string.Equals(k, key, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// 解析「多字段配色串」（M4.20），自测与探针共用。
    ///
    /// <para>
    /// 两种写法：<c>fps:#FF7FB8,cpu:#00E5FF</c>（逐字段）与 <c>#FF7FB8</c>（不写字段名 =
    /// 全部字段一起设，给"整条同色"的 A/B 用）。认不出的字段名直接跳过 ——
    /// 宁可少配一格，也不要把 <c>fpps:#FFF</c> 这种笔误写进配置。
    /// </para>
    ///
    /// <para>
    /// 逗号与分号都认：命令行里用逗号顺手，但设置页探针的命令串本身是按逗号切的
    /// （<c>--settings-probe "textcolor:fps:#FF7FB8;cpu:#00E5FF"</c>），里面再用逗号
    /// 会被外层先切开 —— 那种场合改用分号。
    /// </para>
    /// </summary>
    public static Dictionary<string, string> ParsePairs(string? text)
    {
        var map = NewMap();
        if (string.IsNullOrWhiteSpace(text)) return map;

        foreach (string part in text.Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            string item = part.Trim();
            if (item.Length == 0) continue;

            int colon = item.IndexOf(':');
            if (colon < 0)
            {
                // 没写字段名 = 全部
                string all = Normalize(item);
                foreach (string key in AllKeys) Set(map, key, all);
                continue;
            }

            string field = item[..colon].Trim();
            if (!IsFieldKey(field)) continue;
            Set(map, field, item[(colon + 1)..]);
        }
        return map;
    }

    /// <summary>
    /// 配置项 → 主值画刷。返回 <c>null</c> 表示"跟随主题"，调用方用回原来的语义色。
    /// 画刷一律 Freeze：它会被九个格子共用，可冻结能让 WPF 跳过每次改值的深拷贝检查。
    /// </summary>
    public static SolidColorBrush? ToBrush(string? hex)
    {
        if (!TryParse(hex, out Color c)) return null;
        return Freeze(new SolidColorBrush(c));
    }

    /// <summary>次值用的同色降浓度版（见 <see cref="SubAlpha"/>）。</summary>
    public static SolidColorBrush ToSubBrush(SolidColorBrush main)
    {
        Color c = main.Color;
        return Freeze(new SolidColorBrush(Color.FromArgb(SubAlpha, c.R, c.G, c.B)));
    }

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        if (brush.CanFreeze) brush.Freeze();
        return brush;
    }
}
