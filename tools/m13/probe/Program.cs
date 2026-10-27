using System.Diagnostics;
using CoPul.Collector.Interop;

namespace CoPul.M13;

/// <summary>
/// M1.4 前置探测：进程列表该走哪条路。
///
/// 三条候选：
///   A. NtQuerySystemInformation(SystemProcessInformation) —— 一次调用拿全量（任务管理器口径）
///   B. PDH `\Process(*)\% Processor Time`            —— 文档化，但本机 459 个实例
///   C. PDH `\GPU Engine(*)\Utilization Percentage`   —— 每进程 GPU，本机 1017 个实例
///
/// 判据是「2s 一次采样时占多少单核」，目标整窗 1.5% 单核，进程表能分到的预算约 0.2%。
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        string mode = args.Length > 0 ? args[0] : "all";
        int iterations = args.Length > 1 && int.TryParse(args[1], out int n) ? n : 15;

        switch (mode)
        {
            case "split": MeasureNtSplit(iterations); return 0;
            case "nvml": MeasureNvml(); return 0;
            case "pdh2": MeasurePdhCombined(iterations); return 0;
            case "gpu": MeasureGpuEngine(iterations); return 0;
        }

        var sw = Stopwatch.StartNew();

        Console.WriteLine($"逻辑核 = {Environment.ProcessorCount}   迭代 = {iterations}");
        Console.WriteLine();

        MeasureNt(iterations);
        Console.WriteLine();
        MeasurePdh(@"\GPU Engine(*)\Utilization Percentage", "GPU Engine 每进程占用率", iterations);
        Console.WriteLine();
        MeasurePdh(@"\Process(*)\% Processor Time", "进程 CPU", iterations);
        Console.WriteLine();
        MeasurePdh(@"\Process(*)\Working Set - Private", "进程私有工作集", iterations);

        Console.WriteLine();
        Console.WriteLine($"总耗时 {sw.Elapsed.TotalSeconds:F1}s");
        return 0;
    }

    // ---------------- A. NtQuerySystemInformation ----------------

    private static void MeasureNt(int iterations)
    {
        Console.WriteLine("=== A. NtQuerySystemInformation(SystemProcessInformation) ===");

        // 预热：第一次调用要把 ntdll 与整条路径走通
        Native.Snapshot();
        Thread.Sleep(50);

        var times = new List<double>(iterations);
        int count = 0;
        for (int i = 0; i < iterations; i++)
        {
            long t0 = Stopwatch.GetTimestamp();
            var snap = Native.Snapshot();
            double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
            times.Add(ms);
            count = snap.Count;
            Thread.Sleep(60);
        }

        Console.WriteLine($"进程数 = {count}");
        Report("单次调用+解析", times);

        // 正确性：与 PDH 交叉验证（PowerShell 实测 top：msmpeng 4.4% / wallpaperui 3.0% /
        // deepseek harness 2.85% / workbuddy 2.39%，均为 PDH 值 ÷ 20 核）
        var a = Native.Snapshot();
        long wall0 = Stopwatch.GetTimestamp();
        Thread.Sleep(1000);
        var b = Native.Snapshot();
        double wallMs = (Stopwatch.GetTimestamp() - wall0) * 1000.0 / Stopwatch.Frequency;

        var prev = a.ToDictionary(x => x.Pid, x => x.CpuTicks);
        var rows = new List<(string Name, int Pid, double Cpu, double MemMb)>();
        double sum = 0;
        foreach (var cur in b)
        {
            if (!prev.TryGetValue(cur.Pid, out long before)) continue;
            double deltaTicks = cur.CpuTicks - before;
            double cpu = deltaTicks / 10_000.0 / wallMs / Environment.ProcessorCount * 100.0;
            sum += cpu;
            rows.Add((cur.Name, cur.Pid, cpu, cur.PrivateWs / 1024.0 / 1024.0));
        }

        Console.WriteLine($"--- CPU 占用 top 8（1 秒窗口，已除以 {Environment.ProcessorCount} 核）---");
        foreach (var r in rows.OrderByDescending(r => r.Cpu).Take(8))
            Console.WriteLine($"  {r.Cpu,6:F2}%  {r.MemMb,7:F0} MB  pid={r.Pid,6}  {r.Name}");

        Console.WriteLine($"--- 私有工作集 top 5 ---");
        foreach (var r in rows.OrderByDescending(r => r.MemMb).Take(5))
            Console.WriteLine($"  {r.MemMb,7:F0} MB  pid={r.Pid,6}  {r.Name}");

        Console.WriteLine($"CPU 合计 = {sum:F1}%（应接近整机占用率，PDH 同刻约 27.5%）");
    }

    /// <summary>
    /// GPU Engine 计数器按引擎类型裁剪：全量 1031 个实例一轮 24ms，
    /// 看只留 3d / compute / video* 能把成本压到多少（copy / overlay 这类对"占用率"没意义）。
    /// </summary>
    private static void MeasureGpuEngine(int iterations)
    {
        Console.WriteLine("=== GPU Engine 按引擎类型裁剪 ===");

        long t0 = Stopwatch.GetTimestamp();
        string[] all = PdhQuery.ExpandWildcard(@"\GPU Engine(*)\Utilization Percentage");
        Console.WriteLine($"  全量展开 {all.Length} 个实例   耗时 {(Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency:F1} ms");

        var byType = new Dictionary<string, int>();
        foreach (string p in all)
        {
            int i = p.LastIndexOf("engtype_", StringComparison.Ordinal);
            string type = i >= 0 ? p[(i + 8)..].TrimEnd(')') : "?";
            byType[type] = byType.GetValueOrDefault(type) + 1;
        }
        Console.WriteLine("  --- 各引擎类型实例数 ---");
        foreach (var kv in byType.OrderByDescending(k => k.Value))
            Console.WriteLine($"    {kv.Key,-14} {kv.Value}");

        // 部分通配符能不能用？能用的话就能只展开感兴趣的引擎类型
        foreach (string filter in new[] { "engtype_3d", "engtype_videodecode", "engtype_videoencode", "engtype_compute" })
        {
            string wc = $@"\GPU Engine(*_{filter})\Utilization Percentage";
            long f0 = Stopwatch.GetTimestamp();
            string[] subset = PdhQuery.ExpandWildcard(wc);
            double fms = (Stopwatch.GetTimestamp() - f0) * 1000.0 / Stopwatch.Frequency;
            Console.WriteLine($"  部分通配符 *_{filter} → {subset.Length} 个   {fms:F1} ms");
        }

        // 引擎类型大小写是 "3D" / "VideoDecode"，按忽略大小写匹配
        string[] keep = all.Where(p =>
            p.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("engtype_VideoDecode", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("engtype_VideoEncode", StringComparison.OrdinalIgnoreCase) ||
            p.Contains("engtype_VideoProcessing", StringComparison.OrdinalIgnoreCase)).ToArray();
        Console.WriteLine($"  裁剪后实例 = {keep.Length}（3D + Video*，跳过 Copy/Overlay/Security/VR）");
        MeasureQueryCost("裁剪", keep, iterations);

        // 只留 3D：最省的一档，代价是纯解码/编码负载看不到
        string[] only3d = all.Where(p => p.Contains("engtype_3D", StringComparison.OrdinalIgnoreCase)).ToArray();
        Console.WriteLine($"  仅 3D 实例 = {only3d.Length}");
        MeasureQueryCost("仅3D", only3d, iterations);
    }

    private static void MeasureQueryCost(string label, string[] paths, int iterations)
    {
        if (paths.Length == 0) { Console.WriteLine($"  [{label}] 无实例，跳过"); return; }

        using var q = new PdhQuery();
        q.Open();
        long a0 = Stopwatch.GetTimestamp();
        var counters = new List<PdhCounter>();
        foreach (string p in paths) { var c = q.TryAdd(p); if (c is not null) counters.Add(c); }
        Console.WriteLine($"  [{label}] 添加 {counters.Count} 个   耗时 {(Stopwatch.GetTimestamp() - a0) * 1000.0 / Stopwatch.Frequency:F1} ms");

        q.Collect();
        Thread.Sleep(60);
        q.Collect();

        var collectMs = new List<double>();
        var readMs = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            long c0 = Stopwatch.GetTimestamp();
            uint status = q.Collect();
            collectMs.Add((Stopwatch.GetTimestamp() - c0) * 1000.0 / Stopwatch.Frequency);
            long r0 = Stopwatch.GetTimestamp();
            foreach (var c in counters) PdhQuery.TryGetDouble(c, status);
            readMs.Add((Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency);
            Thread.Sleep(60);
        }

        Report($"  [{label}] Collect", collectMs);
        Report($"  [{label}] 读取全部", readMs);
        double total = collectMs.Average() + readMs.Average();
        Console.WriteLine($"  [{label}] 一轮合计 {total:F2} ms  →  5s 折算 {total / 5000 * 100:F3}%   10s 折算 {total / 10000 * 100:F3}%");
    }

    // ---------------- 分项测量 ----------------

    /// <summary>把 NtQuery 的 10ms 拆成「系统调用」与「托管解析」两段，看优化parse有没有意义。</summary>
    private static void MeasureNtSplit(int iterations)
    {
        Console.WriteLine("=== NtQuery 成本拆解 ===");
        Native.Snapshot();
        Thread.Sleep(50);

        var sys = new List<double>();
        var walk = new List<double>();
        var full = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            sys.Add(Native.SyscallOnly());
            walk.Add(Native.WalkOnly());
            long t0 = Stopwatch.GetTimestamp();
            Native.Snapshot();
            full.Add((Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency);
            Thread.Sleep(60);
        }

        Report("系统调用（不读缓冲区）", sys);
        Report("走链表（不建对象）    ", walk);
        Report("完整快照（含建对象）  ", full);
    }

    /// <summary>NVML 每进程接口：能拿到利用率吗？能拿到显存吗？</summary>
    private static void MeasureNvml()
    {
        Console.WriteLine("=== NVML 每进程接口 ===");
        int rc = NvmlProc.Init();
        Console.WriteLine($"  nvmlInit = {rc}");
        if (rc != 0) return;

        rc = NvmlProc.GetHandleByIndex(0, out IntPtr dev);
        Console.WriteLine($"  getHandle = {rc}");
        if (rc != 0) { NvmlProc.Shutdown(); return; }

        // 1. 每进程利用率
        var samples = new NvmlProc.UtilSample[64];
        uint n = 64;
        rc = NvmlProc.GetProcessUtilization(dev, samples, ref n, 0);
        Console.WriteLine($"  GetProcessUtilization rc={rc}  count={n}");
        if (rc == 0)
        {
            foreach (var s in samples.Take((int)Math.Min(n, 8u)))
                Console.WriteLine($"    pid={s.Pid,6}  sm={s.SmUtil,3}%  mem={s.MemUtil,3}%  enc={s.EncUtil,3}%  dec={s.DecUtil,3}%");
        }

        // 2. 每进程显存（compute / graphics）
        var infos = new NvmlProc.ProcessInfo[64];
        uint m = 64;
        int rc2 = NvmlProc.GetComputeProcesses(dev, ref m, infos);
        Console.WriteLine($"  GetComputeRunningProcesses rc={rc2}  count={m}");
        if (rc2 == 0)
            foreach (var p in infos.Take((int)Math.Min(m, 5u)))
                Console.WriteLine($"    pid={p.Pid,6}  usedGpuMemory={p.UsedGpuMemory}");

        uint g = 64;
        int rc3 = NvmlProc.GetGraphicsProcesses(dev, ref g, infos);
        Console.WriteLine($"  GetGraphicsRunningProcesses rc={rc3}  count={g}");
        if (rc3 == 0)
            foreach (var p in infos.Take((int)Math.Min(g, 5u)))
                Console.WriteLine($"    pid={p.Pid,6}  usedGpuMemory={p.UsedGpuMemory}");

        NvmlProc.Shutdown();
    }

    /// <summary>CPU 与内存放进同一个 PDH query，看合并后 Collect 是不是比两次单列便宜。</summary>
    private static void MeasurePdhCombined(int iterations)
    {
        Console.WriteLine("=== PDH 合并 query（% Processor Time + Working Set - Private）===");

        string[] cpuPaths = PdhQuery.ExpandWildcard(@"\Process(*)\% Processor Time");
        string[] memPaths = PdhQuery.ExpandWildcard(@"\Process(*)\Working Set - Private");
        Console.WriteLine($"  CPU 实例 {cpuPaths.Length}   内存实例 {memPaths.Length}");

        using var q = new PdhQuery();
        q.Open();
        var counters = new List<PdhCounter>();
        foreach (string p in cpuPaths) { var c = q.TryAdd(p); if (c is not null) counters.Add(c); }
        foreach (string p in memPaths) { var c = q.TryAdd(p); if (c is not null) counters.Add(c); }
        Console.WriteLine($"  计数器总数 {counters.Count}");

        q.Collect();
        Thread.Sleep(60);
        q.Collect();

        var collectMs = new List<double>();
        var readMs = new List<double>();
        for (int i = 0; i < iterations; i++)
        {
            long c0 = Stopwatch.GetTimestamp();
            uint status = q.Collect();
            collectMs.Add((Stopwatch.GetTimestamp() - c0) * 1000.0 / Stopwatch.Frequency);

            long r0 = Stopwatch.GetTimestamp();
            foreach (var c in counters) PdhQuery.TryGetDouble(c, status);
            readMs.Add((Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency);
            Thread.Sleep(60);
        }

        Report("  Collect", collectMs);
        Report("  读取全部", readMs);
        double total = collectMs.Average() + readMs.Average();
        Console.WriteLine($"  一轮合计 {total:F2} ms  →  2s 折算 {total / 2000 * 100:F3}%   5s 折算 {total / 5000 * 100:F3}%");
    }

    // ---------------- B/C. PDH ----------------

    private static void MeasurePdh(string wildcard, string label, int iterations)
    {
        Console.WriteLine($"=== PDH {label} ===");
        Console.WriteLine($"  通配符 {wildcard}");

        long t0 = Stopwatch.GetTimestamp();
        string[] paths = PdhQuery.ExpandWildcard(wildcard);
        double expandMs = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine($"  展开实例 = {paths.Length}   耗时 {expandMs:F2} ms");

        if (paths.Length == 0) { Console.WriteLine("  无实例，跳过"); return; }

        using var q = new PdhQuery();
        q.Open();

        long t1 = Stopwatch.GetTimestamp();
        var counters = new List<PdhCounter>();
        foreach (string p in paths)
        {
            var c = q.TryAdd(p);
            if (c is not null) counters.Add(c);
        }
        double addMs = (Stopwatch.GetTimestamp() - t1) * 1000.0 / Stopwatch.Frequency;
        Console.WriteLine($"  添加计数器 = {counters.Count}   耗时 {addMs:F2} ms");

        // 首帧无效（rate 型需要两次），先跑两次
        q.Collect();
        Thread.Sleep(60);
        q.Collect();

        var collectMs = new List<double>(iterations);
        var readMs = new List<double>(iterations);
        for (int i = 0; i < iterations; i++)
        {
            long c0 = Stopwatch.GetTimestamp();
            uint status = q.Collect();
            double cms = (Stopwatch.GetTimestamp() - c0) * 1000.0 / Stopwatch.Frequency;

            long r0 = Stopwatch.GetTimestamp();
            foreach (var c in counters) PdhQuery.TryGetDouble(c, status);
            double rms = (Stopwatch.GetTimestamp() - r0) * 1000.0 / Stopwatch.Frequency;

            collectMs.Add(cms);
            readMs.Add(rms);
            Thread.Sleep(60);
        }

        Report("  Collect", collectMs);
        Report("  读取全部", readMs);
        double total = collectMs.Average() + readMs.Average();
        Console.WriteLine($"  一轮合计 {total:F2} ms  →  2s 采样折算单核 {total / 2000 * 100:F3}%");
    }

    private static void Report(string label, List<double> times)
    {
        times.Sort();
        double med = times[times.Count / 2];
        Console.WriteLine($"{label}：中位 {med:F3} ms   最坏 {times[^1]:F3} ms   最好 {times[0]:F3} ms");
    }
}
