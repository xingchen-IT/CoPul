using System.Diagnostics;
using System.Runtime.InteropServices;
using CoPul.Collector.Interop;

namespace CoPul.Collector.Metrics;

/// <summary>一块物理盘。索引与 <see cref="MetricId.DiskTemperature"/> 数组下标一致。</summary>
public sealed record DiskInfo(int Index, string Model, string Bus, double CapacityGb);

/// <summary>一个卷（分区）。</summary>
public sealed record PartitionInfo(string Name, double TotalMb, double FreeMb, string Format)
{
    public double UsedPercent => TotalMb > 0 ? (TotalMb - FreeMb) / TotalMb * 100.0 : double.NaN;
}

/// <summary>
/// 启动时读一次的静态硬件描述（需求文档 4.1 C01 / 4.2 G01 / 4.4 D01 / D04）。
///
/// 为什么不塞进 <see cref="MetricSnapshot"/>：里面全是字符串与结构体，而 snapshot 的通道
/// 只有 double 与 double[]。硬塞就得给 snapshot 加一路 string 字典，代价是每次 tick
/// 的 Clone 都要多复制一遍这些永不变化的内容。分开之后采集循环的开销一分不涨。
///
/// 用法（M1 起）：
/// <code>
/// var info = SystemInfo.Collect();          // 约 5 ms，放在后台线程（A-12）
/// info.GpuName = nvmlProvider.DeviceName;   // GPU 型号来自 NVML 的 nvmlDeviceGetName
/// </code>
/// </summary>
public sealed class SystemInfo
{
    /// <summary>C01 CPU 型号，例如 "13th Gen Intel(R) Core(TM) i7-13650HX"。</summary>
    public string CpuName { get; set; } = "";

    /// <summary>G01 GPU 型号。由 <c>NvmlGpuProvider.DeviceName</c> 回填——采集层不重复问 NVML。</summary>
    public string GpuName { get; set; } = "";

    public int LogicalCores { get; set; }

    /// <summary>D01 物理盘列表，按 PhysicalDrive 索引升序。</summary>
    public IReadOnlyList<DiskInfo> Disks { get; set; } = Array.Empty<DiskInfo>();

    /// <summary>D04 卷列表（固定盘 + 可移动盘，仅已就绪的）。</summary>
    public IReadOnlyList<PartitionInfo> Partitions { get; set; } = Array.Empty<PartitionInfo>();

    public string OsVersion { get; set; } = "";

    /// <summary>读取失败的项及原因，直接进设置页诊断（Q-18）。</summary>
    public List<string> Warnings { get; } = new();

    /// <summary>整轮静态信息读取耗时。M1 实测约 5 ms，只在启动时跑一次。</summary>
    public double CollectMs { get; private set; }

    /// <summary>分段耗时，用于解释「为什么首帧磁盘区块慢半拍」。</summary>
    public double CpuNameMs { get; private set; }
    public double DisksMs { get; private set; }
    public double PartitionsMs { get; private set; }

    /// <summary>
    /// 读一遍静态信息。**必须在后台线程调用**（A-12）：单次约 5 ms，
    /// 而窗口要求"先出窗口、数据后到"，放 UI 线程会白白拖慢首帧。
    /// </summary>
    public static SystemInfo Collect()
    {
        var sw = Stopwatch.StartNew();
        var info = new SystemInfo
        {
            LogicalCores = Environment.ProcessorCount,
            OsVersion = RuntimeInformation.OSDescription
        };

        if (Win32.TryGetCpuName(out string cpu)) info.CpuName = cpu;
        else info.Warnings.Add("C01 CPU 型号读取失败（注册表 ProcessorNameString 不可用）。");
        info.CpuNameMs = sw.Elapsed.TotalMilliseconds;

        var tDisk = Stopwatch.StartNew();
        try
        {
            info.Disks = Win32.EnumeratePhysicalDisks()
                .Select(d => new DiskInfo(d.Index, d.Model, d.Bus, d.CapacityGb))
                .ToList();

            if (info.Disks.Count == 0)
                info.Warnings.Add("D01 未枚举到物理盘（零访问权限打开 \\\\.\\PhysicalDriveN 全部失败）。");
        }
        catch (Exception ex)
        {
            info.Warnings.Add($"D01 物理盘枚举异常：{ex.GetBaseException().Message}");
        }
        info.DisksMs = tDisk.Elapsed.TotalMilliseconds;

        var tPart = Stopwatch.StartNew();
        try
        {
            var parts = new List<PartitionInfo>();
            foreach (var d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (d.DriveType is not (DriveType.Fixed or DriveType.Removable)) continue;
                    if (!Win32.TryGetVolumeSpace(d.Name, out double totalMb, out double freeMb)) continue;

                    parts.Add(new PartitionInfo(d.Name.TrimEnd('\\'), totalMb, freeMb, d.DriveFormat));
                }
                catch
                {
                    // 空光驱 / 离线卷 / 无权限，跳过这一卷即可，不影响其他卷
                }
            }
            info.Partitions = parts;
        }
        catch (Exception ex)
        {
            info.Warnings.Add($"D04 分区枚举异常：{ex.GetBaseException().Message}");
        }
        info.PartitionsMs = tPart.Elapsed.TotalMilliseconds;

        sw.Stop();
        info.CollectMs = sw.Elapsed.TotalMilliseconds;
        return info;
    }
}
