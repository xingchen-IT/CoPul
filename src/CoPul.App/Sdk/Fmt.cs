using System.Globalization;

namespace CoPul.App.Sdk;

/// <summary>
/// 数值格式化。全部用 <see cref="CultureInfo.InvariantCulture"/>——UI 里的小数点必须永远是 `.`，
/// 否则在中文/欧洲区域设置下会变成 `,`，与"工程仪表"的观感冲突。
/// </summary>
internal static class Fmt
{
    public const string Dash = "—";

    public static string Pct(double v) => v.ToString("F0", CultureInfo.InvariantCulture);

    public static string Temp(double v) => v.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>频率保留 2 位：4.21 GHz 比 4.2 更能体现睿频变化。</summary>
    public static string Ghz(double v) => v.ToString("F2", CultureInfo.InvariantCulture);

    public static string Watts(double v) => v.ToString("F0", CultureInfo.InvariantCulture);

    public static string Mhz(double v) => v.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>
    /// 帧率。整数：0.4 FPS 的差别没人读得出来，而小数会让"最低/最高/平均"那格挤成 6 个字符。
    /// 单位已由格子的 Unit 承担。
    /// </summary>
    public static string Fps(double v) => v.ToString("F0", CultureInfo.InvariantCulture);

    /// <summary>帧时间（ms）。保留 1 位：60 FPS 是 16.7，写 17 就看不出和 16 的差别了。</summary>
    public static string Ms(double v) => v.ToString("F1", CultureInfo.InvariantCulture);

    public static string Gb(double mb) => (mb / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>字节速率 → MB/s，保留 1 位。低于 1 KB/s 时显示 0.0，不显示科学计数法。</summary>
    public static string Rate(double bytesPerSec)
    {
        double mb = bytesPerSec / 1024.0 / 1024.0;
        return mb.ToString("F1", CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// 网络速率 → 自适应单位（N01/N02，需求 4.5「KB/s 或 MB/s，1 位小数，自适应单位」）。
    ///
    /// 分界取 1 MB/s：低于它用 KB/s 才读得出 0.3 这种低流量细节；高于它若还用 KB/s
    /// 会变成 4 位数（10240.0 KB/s），一眼看不出量级。单位由调用方写回单元格，
    /// 因为 <see cref="MetricCell"/> 的单位是展示属性，不在采集层决定。
    /// </summary>
    public static string RateAuto(double bytesPerSec, out string unit)
    {
        double kb = bytesPerSec / 1024.0;
        if (kb < 1024)
        {
            unit = "KB/s";
            return kb.ToString("F1", CultureInfo.InvariantCulture);
        }

        unit = "MB/s";
        return (kb / 1024.0).ToString("F1", CultureInfo.InvariantCulture);
    }
}
