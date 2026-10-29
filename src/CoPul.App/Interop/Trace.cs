using System.IO;

namespace CoPul.App.Interop;

/// <summary>
/// 统一的 trace 日志：写 <c>%TEMP%\copul-trace.log</c>，一行一条，失败静默。
///
/// <para>
/// 为什么统一而不是每个类自己写：全屏自动切换、任务栏重挂、模式切换这三件事的
/// 故障表现全都长成同一副样子（"条不见了""窗口突然换了"），现场排查时必须能把
/// 时间线按顺序拼起来。各自写各自的日志文件，事后还得对齐时间戳，等于没有日志。
/// </para>
/// </summary>
internal static class Trace
{
    private const string FileName = "copul-trace.log";

    /// <summary>日志文件路径（探针与报告里直接引用，方便用户回传）。</summary>
    public static string Path => System.IO.Path.Combine(System.IO.Path.GetTempPath(), FileName);

    /// <summary>追加一行。area 是短前缀（如 <c>fullscreen</c> / <c>tray</c>），便于 grep。</summary>
    public static void Log(string area, string message)
    {
        try
        {
            File.AppendAllText(Path,
                $"{DateTime.Now:HH:mm:ss.fff} [{area}] {message}{Environment.NewLine}");
        }
        catch
        {
            // 日志失败绝不能影响主流程（磁盘满 / 权限被改都可能发生）
        }
    }
}
