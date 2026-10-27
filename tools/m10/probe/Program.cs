using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CoPul.Collector.Interop;

namespace CoPul.M10;

/// <summary>
/// M1.0 缺失字段探测。目的是在动采集层之前，先用数据回答三个「按文档写会踩坑」的问题：
///
/// 1. C03 各核心占用率：文档 4.1 写「PDH 每逻辑核」。20 个计数器挂进同一个 query 后，
///    单次 Collect 会不会从 2.0ms 涨到 6ms（M0.6 实测约 0.22ms/计数器）？
///    替代路径 NtQuerySystemInformation(SystemProcessorPerformanceInformation) 一次系统调用
///    就能拿全部核的 idle/kernel/user，成本是多少？
/// 2. D01 磁盘型号：不提权时能否拿到型号？SetupAPI 友好名 + IOCTL_DISK_GET_LENGTH_INFO
///    是否都可用，成本多少？
/// 3. M04–M06 已缓存/提交/页面文件：GetPerformanceInfo 一次调用能否同时给出，成本多少？
///
/// 输出纯文本，直接归档为 M1.0 的原始数据（与 tools/m06 的做法一致）。
/// </summary>
internal static class Program
{
    private static void Main()
    {
        Console.OutputEncoding = Encoding.UTF8;
        int cores = Environment.ProcessorCount;

        Console.WriteLine("=== M1.0 Probe ===");
        Console.WriteLine($"time      : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        Console.WriteLine($"admin     : {Native.IsAdministrator()}");
        Console.WriteLine($"cores     : {cores}");
        Console.WriteLine($"os        : {Environment.OSVersion}");
        Console.WriteLine($"runtime   : {Environment.Version}");
        Console.WriteLine();

        ProbeCpuName();
        ProbePerCoreLoad();
        ProbePdhPerCoreCost();
        ProbeRealQuery(false);
        ProbeRealQuery(true);
        ProbePerfInfo();
        ProbeDisks();
        ProbePartitions();
        ProbeSystemTimes();
    }

    // ================================================================
    // A. CPU 型号（C01）
    // ================================================================
    private static void ProbeCpuName()
    {
        Console.WriteLine("--- A. C01 CPU 型号（注册表 ProcessorNameString）---");

        // 预热一次，避免把首次库加载算进耗时
        Native.TryGetCpuName(out _);

        var (ms, n) = Measure(() => Native.TryGetCpuName(out _), 200);
        bool ok = Native.TryGetCpuName(out string name);
        Console.WriteLine($"ok        : {ok}");
        Console.WriteLine($"value     : {name}");
        Console.WriteLine($"cost      : {ms * 1000 / n:F3} µs/call  (n={n}, total={ms:F3} ms)");
        Console.WriteLine();
    }

    // ================================================================
    // B. 每核占用率：NtQuerySystemInformation(8)
    // ================================================================
    private static void ProbePerCoreLoad()
    {
        Console.WriteLine("--- B. C03 每核占用（NtQuerySystemInformation / SystemProcessorPerformanceInformation）---");

        int cores = Environment.ProcessorCount;
        var prev = new Native.ProcessorPerf[cores];
        if (!Native.TryQueryProcessorPerf(prev, out int got))
        {
            Console.WriteLine("FAIL      : NtQuerySystemInformation 不可用");
            Console.WriteLine();
            return;
        }
        Console.WriteLine($"instances : {got}");

        Thread.Sleep(500);

        var cur = new Native.ProcessorPerf[cores];
        Native.TryQueryProcessorPerf(cur, out _);

        var loads = Native.ComputeLoads(prev, cur);
        Console.WriteLine("loads     : " + string.Join(" ", loads.Select(v => v.ToString("F0").PadLeft(3))));
        Console.WriteLine("load avg  : " + loads.Average().ToString("F1"));

        var (ms, n) = Measure(() => Native.TryQueryProcessorPerf(cur, out _), 3000);
        double perCall = ms * 1000 / n;
        Console.WriteLine($"cost      : {perCall:F3} µs/call  (n={n})");
        Console.WriteLine($"@200ms    : {perCall / 1000 * 5:F4} ms/s = {perCall / 1000 * 5 / 10:F4}% 单核 = {perCall / 1000 * 5 / 10 / cores:F5}% 整机");
        Console.WriteLine($"@500ms    : {perCall / 1000 * 2:F4} ms/s = {perCall / 1000 * 2 / 10:F4}% 单核 = {perCall / 1000 * 2 / 10 / cores:F5}% 整机");
        Console.WriteLine();
    }

    // ================================================================
    // B2. 对照：PDH 每核计数器
    // ================================================================
    private static void ProbePdhPerCoreCost()
    {
        Console.WriteLine("--- B2. 对照：PDH 每核计数器（文档 4.1 原方案）---");

        try
        {
            string[] paths = PdhQuery.ExpandWildcard(@"\Processor Information(*)\% Processor Utility");
            var instances = paths
                .Select(p => ExtractInstance(p))
                .Where(s => s.Length > 0 && s != "_Total")
                .ToList();

            Console.WriteLine($"instances : {instances.Count}  -> {string.Join(" | ", instances.Take(24))}");

            using var q = new PdhQuery();
            q.Open();
            q.TryAdd(@"\Processor Information(_Total)\% Processor Utility");
            q.Collect();
            Thread.Sleep(120);
            q.Collect();

            var (ms1, n1) = Measure(() => q.Collect(), 300);
            Console.WriteLine($"1 计数器  : {ms1 / n1:F3} ms/Collect (n={n1})");

            int added = 0;
            foreach (string inst in instances)
            {
                if (q.TryAdd($@"\Processor Information({inst})\% Processor Utility") != null) added++;
            }
            q.Collect();
            Thread.Sleep(120);
            q.Collect();

            var (ms2, n2) = Measure(() => q.Collect(), 300);
            Console.WriteLine($"1+{added} 计数器: {ms2 / n2:F3} ms/Collect (n={n2})  增量 {(ms2 / n2 - ms1 / n1):F3} ms");
            Console.WriteLine($"每计数器  : {(ms2 / n2 - ms1 / n1) / Math.Max(added, 1):F4} ms");
            Console.WriteLine($"@200ms 整机: {(ms2 / n2) * 5 / 10 / Environment.ProcessorCount:F4}%");

            // ---- 关键补充：Collect 只是刷新，逐计数器取值还要再花一次钱 ----
            // M1.0 实测反馈：只测 Collect 会少算一个数量级。真实路径是
            // Collect() 一次 + 每个计数器 PdhGetFormattedCounterValue() 一次。
            var counters = q.Counters.ToList();
            var (ms3, n3) = Measure(() =>
            {
                uint rc = q.Collect();
                foreach (var c in counters) PdhQuery.TryGetDouble(c, rc);
            }, 300);
            Console.WriteLine($"Collect+格式化({counters.Count}): {ms3 / n3:F3} ms  格式化部分 {(ms3 / n3 - ms2 / n2):F3} ms");
            Console.WriteLine($"每计数器格式化: {(ms3 / n3 - ms2 / n2) / Math.Max(counters.Count, 1) * 1000:F1} µs");
            Console.WriteLine($"@500ms 整机  : {(ms3 / n3) * 2 / 10 / Environment.ProcessorCount:F4}%");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL      : {ex.GetBaseException().Message}");
        }
        Console.WriteLine();
    }

    private static string ExtractInstance(string counterPath)    {
        int open = counterPath.IndexOf('(');
        int close = counterPath.LastIndexOf(')');
        return open >= 0 && close > open ? counterPath[(open + 1)..close] : "";
    }

    // ================================================================
    // B3. 复现 OsProvider 的真实计数器组合：加 C03 前 vs 加 C03 后
    // ================================================================
    private static void ProbeRealQuery(bool withCore)
    {
        using var q = new PdhQuery();
        q.Open();

        q.TryAdd(@"\Processor Information(_Total)\% Processor Utility");
        q.TryAdd(@"\Processor Information(_Total)\% Processor Time");
        q.TryAdd(@"\PhysicalDisk(_Total)\Disk Read Bytes/sec");
        q.TryAdd(@"\PhysicalDisk(_Total)\Disk Write Bytes/sec");
        q.TryAdd(@"\PhysicalDisk(_Total)\% Disk Time");

        foreach (string rx in PdhQuery.ExpandWildcard(@"\Network Interface(*)\Bytes Received/sec"))
        {
            string inst = ExtractInstance(rx);
            if (inst.Length == 0 || inst == "_Total") continue;
            if (inst.Contains("Loopback") || inst.Contains("isatap") || inst.Contains("vEthernet")) continue;
            q.TryAdd(rx);
            q.TryAdd(rx.Replace("Bytes Received/sec", "Bytes Sent/sec"));
        }

        if (withCore)
        {
            foreach (string p in PdhQuery.ExpandWildcard(@"\Processor Information(*)\% Processor Utility"))
            {
                string inst = ExtractInstance(p);
                if (inst.Length == 0 || inst.Contains("_Total", StringComparison.OrdinalIgnoreCase)) continue;
                q.TryAdd(p);
            }
        }

        q.Collect();
        Thread.Sleep(120);
        q.Collect();

        var counters = q.Counters.ToList();

        var (ms1, n1) = Measure(() => q.Collect(), 300);
        var (ms2, n2) = Measure(() =>
        {
            uint rc = q.Collect();
            foreach (var c in counters) PdhQuery.TryGetDouble(c, rc);
        }, 300);

        Console.WriteLine($"  计数器 {counters.Count,2} 个：Collect {ms1 / n1:F3} ms   Collect+格式化 {ms2 / n2:F3} ms   " +
                          $"@500ms 整机 {ms2 / n2 * 2 / 10 / Environment.ProcessorCount:F4}%");
    }

    // ================================================================
    // C. 已缓存 / 提交 / 页面文件（M04–M06）
    // ================================================================
    private static void ProbePerfInfo()
    {
        Console.WriteLine("--- C. M04/M05/M06 GetPerformanceInfo ---");

        Native.TryGetPerformanceInfo(out var pi);
        double pageMb = pi.PageSize / 1024.0 / 1024.0;
        Console.WriteLine($"PageSize          : {pi.PageSize} B");
        Console.WriteLine($"M04 已缓存(SystemCache) : {pi.SystemCache * pageMb:F1} MB");
        Console.WriteLine($"M05 提交              : {pi.CommitTotal * pageMb / 1024:F2} / {pi.CommitLimit * pageMb / 1024:F2} GB");
        double pfMb = (pi.CommitTotal - pi.PhysicalTotal) * pageMb;
        Console.WriteLine($"M06 页面文件(推算)     : {pfMb:F0} MB   [= (CommitTotal - PhysicalTotal) * PageSize]");
        Console.WriteLine($"物理总量             : {pi.PhysicalTotal * pageMb / 1024:F2} GB   可用 {pi.PhysicalAvailable * pageMb:F0} MB");
        Console.WriteLine($"进程/线程/句柄        : {pi.ProcessCount} / {pi.ThreadCount} / {pi.HandleCount}");

        Native.TryGetPerformanceInfo(out _);
        var (ms, n) = Measure(() => Native.TryGetPerformanceInfo(out _), 2000);
        double perCall = ms * 1000 / n;
        Console.WriteLine($"cost              : {perCall:F3} µs/call (n={n})");
        Console.WriteLine($"@5000ms 整机      : {perCall / 1000 * 0.2 / 10 / Environment.ProcessorCount:F6}%");
        Console.WriteLine();
    }

    // ================================================================
    // D. 磁盘型号（D01）
    // ================================================================
    private static void ProbeDisks()
    {
        Console.WriteLine("--- D. D01 磁盘型号 ---");

        var (enumMs, _) = Measure(() => Native.EnumerateDiskDrives(out _), 3);
        var disks = Native.EnumerateDiskDrives(out int rawCount);
        Console.WriteLine($"SetupAPI  : rawCount={rawCount}  cost={enumMs / 3:F2} ms");
        foreach (var d in disks)
            Console.WriteLine($"  - [{d.Bus}] {d.Model}  instance={d.InstanceId}");

        Console.WriteLine();
        Console.WriteLine("注册表 disk\\Enum 原始串：");
        foreach (string s in Native.ReadDiskEnumRegistry())
            Console.WriteLine("  " + s);

        Console.WriteLine();
        Console.WriteLine("IOCTL 直读（不提权 0 访问权限）：");
        for (int i = 0; i < 8; i++)
        {
            string path = $@"\\.\PhysicalDrive{i}";
            bool exists = Native.ProbePhysicalDrive(path, out long lengthMb, out string? product, out int err);
            if (!exists) continue;
            Console.WriteLine($"  {path}  size={(lengthMb > 0 ? lengthMb / 1024.0 + " GB" : "?")}  product={product ?? "(null)"}  err={err}");
        }

        Console.WriteLine();
        Console.WriteLine("容量获取路径对照（0 访问 / FILE_READ_ATTRIBUTES / GENERIC_READ）：");
        for (int i = 0; i < 4; i++)
            Console.WriteLine("  " + Native.ProbeCapacity($@"\\.\PhysicalDrive{i}"));
        Console.WriteLine();
    }

    // ================================================================
    // E. 分区（D04）
    // ================================================================
    private static void ProbePartitions()
    {
        Console.WriteLine("--- E. D04 分区 ---");

        Native.TryGetPartitions(out _);
        var parts = Native.TryGetPartitions(out int rawDriveCount);
        Console.WriteLine($"GetDrives : {rawDriveCount} 个卷，通过筛选 {parts.Count} 个");
        foreach (var d in DriveInfo.GetDrives())
            Console.WriteLine($"  raw {d.Name,-4} type={d.DriveType} ready={d.IsReady}");
        foreach (string e in Native.PartitionErrors)
            Console.WriteLine($"  ERR {e}");

        foreach (var p in parts)
        {
            double totalGb = p.TotalMb / 1024.0;
            double freeGb = p.FreeMb / 1024.0;
            double pct = p.TotalMb > 0 ? (p.TotalMb - p.FreeMb) / p.TotalMb * 100 : 0;
            Console.WriteLine($"  {p.Name,-4} {pct,5:F1}%   {totalGb - freeGb:F1}/{totalGb:F1} GB   [{p.DriveType}] {p.Format}");
        }

        var (ms, n) = Measure(() => Native.TryGetPartitions(out _), 300);
        double perCall = ms * 1000 / n;
        Console.WriteLine($"cost      : {perCall:F3} µs/call (n={n})  (@10000ms 整机 {perCall / 1000 * 0.1 / 10 / Environment.ProcessorCount:F7}%)");
        Console.WriteLine();
    }

    // ================================================================
    // F. 负载均值（C10）
    // ================================================================
    private static void ProbeSystemTimes()
    {
        Console.WriteLine("--- F. C10 负载均值（GetSystemTimes + EWMA）---");

        Native.TryGetSystemTimes(out var t0);
        Thread.Sleep(500);
        Native.TryGetSystemTimes(out var t1);
        double load = Native.CpuLoadBetween(t0, t1);
        Console.WriteLine($"当前 500ms 窗口 CPU: {load:F1}%");

        var (ms, n) = Measure(() => Native.TryGetSystemTimes(out _), 2000);
        Console.WriteLine($"cost      : {ms * 1000 / n:F3} µs/call (n={n})");
        Console.WriteLine($"说明      : Windows 无 load average，C10 用 GetSystemTimes 的 CPU 占用做 60/300/900s 指数平滑");
        Console.WriteLine();
    }

    // ================================================================
    // 计时助手
    // ================================================================
    private static (double TotalMs, int N) Measure(Action action, int iterations)
    {
        long t0 = Stopwatch.GetTimestamp();
        for (int i = 0; i < iterations; i++) action();
        long t1 = Stopwatch.GetTimestamp();
        return ((t1 - t0) * 1000.0 / Stopwatch.Frequency, iterations);
    }
}
