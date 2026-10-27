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

    public static string Gb(double mb) => (mb / 1024.0).ToString("F1", CultureInfo.InvariantCulture);

    /// <summary>字节速率 → MB/s，保留 1 位。低于 1 KB/s 时显示 0.0，不显示科学计数法。</summary>
    public static string Rate(double bytesPerSec)
    {
        double mb = bytesPerSec / 1024.0 / 1024.0;
        return mb.ToString("F1", CultureInfo.InvariantCulture);
    }
}
