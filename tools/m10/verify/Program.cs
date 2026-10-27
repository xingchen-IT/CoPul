using System.Text;
using CoPul.Collector.Metrics;
using CoPul.Collector.Providers;
using CoPul.Collector.Scheduling;

namespace CoPul.M10;

/// <summary>
/// M1.0 字段验收：把新增的 9 项指标（C01/C03/C10/M04/M05/M06/D01/D04 + G01）
/// 在真实调度器下取一份快照，逐项打印，供人工比对。
///
/// 与 tools/m06 的分工：m06 测**成本**（60 秒窗口），这里测**有没有值、顺序对不对**。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        // 默认等 6 秒：T2s（硬盘温度 / 负载均值）floor 是 5000ms，等短了拿不到。
        int waitMs = 6000;
        int wIdx = Array.IndexOf(args, "--wait");
        if (wIdx >= 0 && wIdx + 1 < args.Length && int.TryParse(args[wIdx + 1], out int w) && w > 0) waitMs = w;

        Console.WriteLine("=== M1.0 字段验收 ===");
        Console.WriteLine($"time  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"admin : {IsAdministrator()}");
        Console.WriteLine();

        // 静态信息：M1 会放进后台线程（A-12），这里同步调用来量耗时
        var info = SystemInfo.Collect();

        bool coreLoad = !args.Any(a => a == "--no-core");
        var os = new OsProvider { EnablePerCoreLoad = coreLoad };
        var nvml = new NvmlGpuProvider();
        var msr = new MsrCpuProvider();
        var nvme = new NvmeStorageProvider();

        using var sched = new SampleScheduler(new IMetricProvider[] { nvml, os, msr, nvme },
            new SampleSchedulerOptions { UserIntervalMs = 500 });

        sched.Start();
        info.GpuName = nvml.DeviceName;

        // 进程 CPU 用 TotalProcessorTime 增量算；60 秒窗口误差 ±0.026%（M0.6 结论），
        // 这里只有几秒，只能看量级，用于「开/关 C03」的相对对比。
        var proc = System.Diagnostics.Process.GetCurrentProcess();
        TimeSpan cpu0 = proc.TotalProcessorTime;
        long wall0 = System.Diagnostics.Stopwatch.GetTimestamp();

        Thread.Sleep(waitMs);
        var snap = sched.Latest;

        proc.Refresh();
        double wallMs = (System.Diagnostics.Stopwatch.GetTimestamp() - wall0) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
        double cpuMs = (proc.TotalProcessorTime - cpu0).TotalMilliseconds;
        Console.WriteLine($"C03 每核占用   : {(coreLoad ? "开" : "关")}");
        Console.WriteLine($"进程 CPU       : {cpuMs / wallMs * 100:F3}% 单核 = {cpuMs / wallMs * 100 / Environment.ProcessorCount:F4}% 整机  (窗口 {wallMs / 1000:F1}s)");
        Console.WriteLine();

        DumpSystemInfo(info);
        DumpScalars(snap);
        DumpSeries(snap);
        DumpDiagnostics(os, nvml, msr, nvme, sched);

        sched.Stop();
        return 0;
    }

    private static void DumpSystemInfo(SystemInfo i)
    {
        Console.WriteLine("--- 静态信息 SystemInfo ---");
        Console.WriteLine($"C01 CPU 型号     : {N(i.CpuName)}");
        Console.WriteLine($"G01 GPU 型号     : {N(i.GpuName)}");
        Console.WriteLine($"逻辑核数         : {i.LogicalCores}");
        Console.WriteLine($"OS               : {i.OsVersion}");
        Console.WriteLine($"读取耗时         : {i.CollectMs:F2} ms  (CPU名 {i.CpuNameMs:F2} / 物理盘 {i.DisksMs:F2} / 分区 {i.PartitionsMs:F2})");
        Console.WriteLine("D01 物理盘       :");
        foreach (var d in i.Disks)
            Console.WriteLine($"    #{d.Index} [{d.Bus,-4}] {d.Model}  {d.CapacityGb:F1} GB");
        if (i.Disks.Count == 0) Console.WriteLine("    （空）");
        Console.WriteLine("D04 分区         :");
        foreach (var p in i.Partitions)
            Console.WriteLine($"    {p.Name,-3} {p.UsedPercent,5:F1}%  {p.TotalMb / 1024 - p.FreeMb / 1024:F1}/{p.TotalMb / 1024:F1} GB  {p.Format}");
        if (i.Partitions.Count == 0) Console.WriteLine("    （空）");
        foreach (string w in i.Warnings) Console.WriteLine($"    WARN {w}");
        Console.WriteLine();
    }

    private static void DumpScalars(MetricSnapshot s)
    {
        Console.WriteLine("--- 标量（M1.0 新增项标 ★）---");

        MetricId[] ids =
        {
            // ★ M1.0 新增
            MetricId.CpuLoadAvg1, MetricId.CpuLoadAvg5, MetricId.CpuLoadAvg15,
            MetricId.RamCachedMb, MetricId.RamCommitUsedMb, MetricId.RamCommitLimitMb, MetricId.RamPageFileMb,
            // 已有项，顺带确认没被改坏
            MetricId.CpuTotalLoad, MetricId.CpuPackageTemp, MetricId.CpuClockGhz, MetricId.CpuPackagePowerW,
            MetricId.GpuLoad, MetricId.GpuMemUsedMb, MetricId.GpuTemp, MetricId.GpuPowerW,
            MetricId.RamLoad, MetricId.RamUsedMb, MetricId.RamTotalMb,
            MetricId.DiskReadBytesPerSec, MetricId.DiskWriteBytesPerSec, MetricId.NetDownBytesPerSec, MetricId.NetLatencyMs
        };

        foreach (MetricId id in ids)
        {
            string mark = id is MetricId.CpuLoadAvg1 or MetricId.CpuLoadAvg5 or MetricId.CpuLoadAvg15
                or MetricId.RamCachedMb or MetricId.RamCommitUsedMb or MetricId.RamCommitLimitMb
                or MetricId.RamPageFileMb ? "★" : " ";
            if (s.TryGet(id, out var v))
                Console.WriteLine($"  {mark} {id,-22} {v.Value,12:F2}  {v.State}{(v.Note is null ? "" : "  // " + v.Note)}");
            else
                Console.WriteLine($"  {mark} {id,-22} {"(缺失)",12}  ——");
        }
        Console.WriteLine();
    }

    private static void DumpSeries(MetricSnapshot s)
    {
        Console.WriteLine("--- 数组（★ = M1.0 新增）---");

        foreach (MetricId id in new[] { MetricId.CpuCoreLoad, MetricId.CpuCoreTemp, MetricId.DiskTemperature })
        {
            if (!s.TryGetSeries(id, out var series))
            {
                Console.WriteLine($"  {id}: （缺失）");
                continue;
            }

            string mark = id == MetricId.CpuCoreLoad ? "★" : " ";
            Console.WriteLine($"  {mark} {id} [{series.Values.Length}] {series.State}");
            Console.WriteLine("      " + string.Join(" ", series.Values.Select(v =>
                double.IsNaN(v) ? "  —" : v.ToString("F0").PadLeft(3))));
        }
        Console.WriteLine();
    }

    private static void DumpDiagnostics(OsProvider os, NvmlGpuProvider nvml, MsrCpuProvider msr,
                                        NvmeStorageProvider nvme, SampleScheduler sched)
    {
        Console.WriteLine("--- 诊断 ---");
        Console.WriteLine($"  Os.PDH 计数器总数   : {os.PdhCounterCount}（其中每核 {os.CoreCounterCount}）");
        Console.WriteLine($"  Os.Perf 单次 Collect: {os.LastCollectMs:F3} ms   rc={os.LastCollectStatus}");
        Console.WriteLine($"  网卡（纳入统计）    : {string.Join(", ", os.NicNames)}");

        foreach (var p in new IMetricProvider[] { nvml, os, msr, nvme })
            Console.WriteLine($"  {p.Name,-10} {p.Status,-14} init={p.InitializeMs,7:F2} ms  {p.UnavailableReason}");

        Console.WriteLine($"  快照数              : {sched.SnapshotCount}");
        Console.WriteLine("  各层实际间隔：");
        foreach (var t in sched.Stats)
            Console.WriteLine($"    {t.Tier,-16} 设定{t.IntervalMs,5} ms  实际{t.AvgActualIntervalMs,8:F1} ms  " +
                              $"平均tick {t.AvgTickMs,7:F3} ms  最大 {t.MaxMs,7:F3} ms  {t.Ticks} 次");
        Console.WriteLine();
    }

    private static string N(string s) => string.IsNullOrEmpty(s) ? "(空)" : s;

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }
}
