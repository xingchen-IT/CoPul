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

    /// <summary>默认高 1460：M1.5 的 FPS 区块（一排六格 + 折线）与进程表同时展开所需。高 DPI 屏上会被系统钳到工作区高度，靠内容区滚动。</summary>
    public double WindowHeight { get; set; } = 1460;
    public bool WindowMaximized { get; set; }

    /// <summary>折叠的区块 id：<c>"cpu"</c> / <c>"gpu"</c> / <c>"mem"</c> / <c>"disk"</c>（5.4 要求持久化）。</summary>
    public string[] CollapsedBlocks { get; set; } = Array.Empty<string>();

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

    public static AppConfig Load()
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
    /// 留下的就是一个语法不完整的 JSON，下次启动 <see cref="Load"/> 抛异常 —— 用户表现为
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

            // LoadError 不该写回磁盘，否则下次读出来还以为又出错了
            var snapshot = new AppConfig
            {
                WindowLeft = config.WindowLeft,
                WindowTop = config.WindowTop,
                WindowWidth = config.WindowWidth,
                WindowHeight = config.WindowHeight,
                WindowMaximized = config.WindowMaximized,
                CollapsedBlocks = config.CollapsedBlocks,
                ConfigVersion = config.ConfigVersion
            };

            string json = JsonSerializer.Serialize(snapshot, JsonOptions);

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
}
