using System.Diagnostics;
using System.Net.NetworkInformation;
using CoPul.Collector.Interop;
using CoPul.Collector.Metrics;

namespace CoPul.Collector.Providers;

/// <summary>
/// 操作系统层指标：内存、CPU 占用率、磁盘与网络速率、网络延迟。
///
/// 这一层原本在需求文档 4.0 里是「估算 0.5–0.6 ms/tick」，M0.6 之前从未实测过。
/// 全部 PDH 计数器塞进同一个 query，一次 PdhCollectQueryData 刷新，
/// 符合文档 A-01「定时器合并」——绝不为每个指标各起一个 query。
/// </summary>
public sealed class OsProvider : IMetricProvider
{
    private const string CounterCpuUtility = @"\Processor Information(_Total)\% Processor Utility";
    private const string CounterCpuTime = @"\Processor Information(_Total)\% Processor Time";
    private const string CounterDiskReadTotal = @"\PhysicalDisk(_Total)\Disk Read Bytes/sec";
    private const string CounterDiskWriteTotal = @"\PhysicalDisk(_Total)\Disk Write Bytes/sec";
    private const string CounterDiskBusyTotal = @"\PhysicalDisk(_Total)\% Disk Time";
    private const string CounterDiskReadWildcard = @"\PhysicalDisk(*)\Disk Read Bytes/sec";

    /// <summary>虚拟 / 隧道网卡的实例名关键字。N01/N02 必须排除，否则读数被内部流量污染。</summary>
    private static readonly string[] VirtualNicKeywords =
    {
        "Loopback", "isatap", "Teredo", "vEthernet", "VMware", "Hyper-V",
        "VirtualBox", "TAP-", "Tailscale", "Wintun", "Bluetooth", "WAN Miniport",
        "Pseudo-Interface", "Kernel Debug"
    };

    private PdhQuery? _pdh;
    private PdhCounter? _cpuUtility;
    private PdhCounter? _cpuTime;
    private PdhCounter? _diskReadTotal;
    private PdhCounter? _diskWriteTotal;
    private PdhCounter? _diskBusyTotal;
    private readonly List<NicCounter> _nics = new();
    private readonly List<SampleGroup> _groups;

    /// <summary>C03：每逻辑核一个计数器，按 (处理器组, 逻辑核) 数值序排列。</summary>
    private readonly List<PdhCounter> _coreUtility = new();

    /// <summary>D03：每块物理盘的读/写计数器，下标 == PhysicalDriveN 的 N。</summary>
    private PdhCounter?[] _diskRead = Array.Empty<PdhCounter?>();
    private PdhCounter?[] _diskWrite = Array.Empty<PdhCounter?>();

    private Thread? _pingThread;
    private readonly ManualResetEventSlim _stopPing = new(false);
    private double _cachedLatencyMs = double.NaN;
    private int _pingFailStreak;

    // ---- C10 负载均值状态 ----
    private ulong _lastIdle, _lastKernel, _lastUser;
    private long _lastLoadAvgTs;
    private bool _loadAvgPrimed;
    private double _avg1, _avg5, _avg15;

    public OsProvider()
    {
        _groups = new List<SampleGroup>
        {
            new("Os.Memory", Tier.T0, SampleMemory),
            new("Os.MemoryExtra", Tier.T2, SampleMemoryExtra),
            new("Os.Perf", Tier.T0b, SamplePdh),
            new("Os.LoadAvg", Tier.T2Slow, SampleLoadAvg),
            new("Os.Latency", Tier.T2, SampleLatency)
        };
    }

    public string Name => "OS / PDH";
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;
    public string? UnavailableReason { get; private set; }
    public string? Remedy { get; private set; }
    public double InitializeMs { get; private set; }
    public IReadOnlyList<SampleGroup> Groups => _groups;

    // ---- 诊断字段，供 M0.6 基准输出 ----
    public int PdhCounterCount => _pdh?.Counters.Count ?? 0;

    /// <summary>C03 实际挂上的每核计数器数量。少于逻辑核数说明实例名解析失败。</summary>
    public int CoreCounterCount => _coreUtility.Count;
    public double LastCollectMs { get; private set; }
    public uint LastCollectStatus { get; private set; }
    public double LastPingMs { get; private set; }
    public IReadOnlyList<PdhCounter> PdhCounters => _pdh?.Counters ?? (IReadOnlyList<PdhCounter>)Array.Empty<PdhCounter>();
    public IReadOnlyList<string> NicNames => _nics.Select(n => n.Name).ToList();

    /// <summary>过滤前的全部 Network Interface 实例。诊断用：解释「为什么只找到 1 块网卡」。</summary>
    public List<string> AllNicInstances { get; } = new();

    /// <summary>被排除的实例及原因（虚拟网卡 / 无收发计数器）。</summary>
    public List<(string Instance, string Reason)> ExcludedNics { get; } = new();

    /// <summary>延迟探测目标。默认国内 DNS（Q-09），8.8.8.8 实测 186 ms 不适合做基准。</summary>
    public string PingTarget { get; set; } = "223.5.5.5";

    /// <summary>延迟探测间隔。N03 的 floor 是 1000ms（P-04：高频 ICMP 会抬高被测量值）。</summary>
    public int PingIntervalMs { get; set; } = 1000;

    /// <summary>
    /// 是否采集 C03 每核占用率。默认开。
    /// 关掉它是 R18 的退路之一：0.5s 档读数离散 3 倍，若将来贴线，
    /// 先关 C03（大窗口 20 格变灰）比降全核温度频率对用户更无损。
    /// 必须在 <see cref="Initialize"/> 之前设置。
    /// </summary>
    public bool EnablePerCoreLoad { get; set; } = true;

    public bool Initialize()
    {
        if (Status != ProviderStatus.NotInitialized) return Status == ProviderStatus.Ready;

        var sw = Stopwatch.StartNew();
        try
        {
            _pdh = new PdhQuery();
            _pdh.Open();

            _cpuUtility = _pdh.TryAdd(CounterCpuUtility);
            _cpuTime = _pdh.TryAdd(CounterCpuTime);
            _diskReadTotal = _pdh.TryAdd(CounterDiskReadTotal);
            _diskWriteTotal = _pdh.TryAdd(CounterDiskWriteTotal);
            _diskBusyTotal = _pdh.TryAdd(CounterDiskBusyTotal);

            AddNetworkCounters();
            AddCoreCounters();
            AddPerDiskCounters();

            // 速率型计数器必须至少两次 Collect 才有值，第一次只是建基线。
            _pdh.Collect();
            Thread.Sleep(60);
            _pdh.Collect();

            Status = ProviderStatus.Ready;

            // ICMP 单次实测 13–30 ms。放进采样线程会直接推迟其他 tier 的 tick
            // （M0.6 实测 50ms 挡位下 T2 的实际间隔被拖到 752ms），
            // 所以延迟探测走独立线程，采样组只读缓存值。
            _pingThread = new Thread(PingLoop)
            {
                IsBackground = true,
                Name = "CoPul.Ping",
                Priority = ThreadPriority.BelowNormal
            };
            _pingThread.Start();
            return true;
        }
        catch (Exception ex)
        {
            Status = ProviderStatus.Failed;
            UnavailableReason = $"PDH 初始化失败：{ex.GetBaseException().Message}";
            Remedy = "性能计数器库损坏时，可运行 `lodctr /R` 重建。";
            return false;
        }
        finally
        {
            sw.Stop();
            InitializeMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    private void AddNetworkCounters()
    {
        var paths = PdhQuery.ExpandWildcard(@"\Network Interface(*)\Bytes Received/sec");
        foreach (string rxPath in paths)
        {
            string instance = ExtractInstance(rxPath);
            if (string.IsNullOrEmpty(instance)) continue;
            AllNicInstances.Add(instance);

            if (instance == "_Total") { ExcludedNics.Add((instance, "_Total 汇总项")); continue; }

            string? hit = VirtualNicKeywords.FirstOrDefault(k => instance.Contains(k, StringComparison.OrdinalIgnoreCase));
            if (hit != null) { ExcludedNics.Add((instance, $"虚拟网卡关键字 {hit}")); continue; }

            string txPath = rxPath.Replace("Bytes Received/sec", "Bytes Sent/sec");
            var rx = _pdh!.TryAdd(rxPath);
            var tx = _pdh!.TryAdd(txPath);
            if (rx == null || tx == null) { ExcludedNics.Add((instance, "计数器添加失败")); continue; }

            _nics.Add(new NicCounter(instance, rx, tx));
        }
    }

    private static string ExtractInstance(string counterPath)
    {
        int open = counterPath.IndexOf('(');
        int close = counterPath.LastIndexOf(')');
        return open >= 0 && close > open ? counterPath[(open + 1)..close] : "";
    }

    /// <summary>
    /// C03 每逻辑核占用率。M1.0 实测：20 个计数器挂进同一个 query，单次 Collect 从
    /// 0.165 ms 涨到 0.339 ms（**增量 0.174 ms**，即 8.3 µs/计数器），
    /// 折算整机 0.0044% —— 比读一次 GetSystemTimes 还便宜的事不用另开通道。
    ///
    /// 为什么不用 NtQuerySystemInformation(SystemProcessorPerformanceInformation)：
    /// 它只要 48 µs（便宜 4 倍），但口径是 `% Processor Time`，而 C02 用的是
    /// `% Processor Utility`（Q-06，与任务管理器一致）。两者在睿频下能差出 10 个点，
    /// 大窗口里"总占用 35%、20 格加起来 50%"会被当成 bug。**口径一致压过 0.17 ms**。
    /// </summary>
    private void AddCoreCounters()
    {
        if (!EnablePerCoreLoad) return;

        var items = new List<(int Group, int Index, string Path)>();

        foreach (string path in PdhQuery.ExpandWildcard(@"\Processor Information(*)\% Processor Utility"))
        {
            string inst = ExtractInstance(path);
            if (inst.Length == 0 || inst.Contains("_Total", StringComparison.OrdinalIgnoreCase)) continue;

            // 实例名形如 "0,12"（处理器组, 逻辑核）。PDH 返回的是字典序，
            // "0,10" 排在 "0,2" 前面 —— 直接照抄会让 20 格顺序错乱，必须按数值重排。
            string[] seg = inst.Split(',');
            if (seg.Length != 2) continue;
            if (!int.TryParse(seg[0], out int grp) || !int.TryParse(seg[1], out int idx)) continue;

            items.Add((grp, idx, path));
        }

        foreach (var it in items.OrderBy(i => i.Group).ThenBy(i => i.Index))
        {
            var c = _pdh!.TryAdd(it.Path);
            if (c != null) _coreUtility.Add(c);
        }
    }

    /// <summary>
    /// D03 每盘读写速率（R21）。PhysicalDisk 的实例名形如 <c>"0 C:"</c> / <c>"1 D: E:"</c>，
    /// **首段数字就是 PhysicalDriveN 的 N**，所以可以直接对齐到 D02 的盘温数组下标。
    ///
    /// 三个坑：
    /// 1. 实例名里的盘符会变（插拔 U 盘、盘符重排），但索引部分不变——这里只取索引，
    ///    盘符一律忽略，否则"E: 换成 F:"就会让整块盘的读数变成 `—`。
    /// 2. 索引可能不连续（本机就是 0、2，没有 1），数组长度取 <c>maxIndex + 1</c>、
    ///    空位留 null，**不能按枚举顺序塞**——那样第 2 块盘的速率会画到第 1 块盘上。
    /// 3. 热插拔后新盘不会自动出现（实例是初始化时一次性挂上的），要等重启。
    ///    这是 v1 的已知限制，见 docs/M1.2 的 R23。
    /// </summary>
    private void AddPerDiskCounters()
    {
        var found = new List<(int Index, string Instance, string ReadPath)>();

        foreach (string path in PdhQuery.ExpandWildcard(CounterDiskReadWildcard))
        {
            string inst = ExtractInstance(path);
            if (inst.Length == 0) continue;
            if (inst.StartsWith("_Total", StringComparison.OrdinalIgnoreCase)) continue;

            // 首段空格前是数字索引："0 C:" / "1 D: E:"
            int sp = inst.IndexOf(' ');
            string head = sp > 0 ? inst[..sp] : inst;
            if (!int.TryParse(head, out int index) || index < 0) continue;

            found.Add((index, inst, path));
        }

        if (found.Count == 0) return;

        int maxIndex = found.Max(f => f.Index);
        _diskRead = new PdhCounter?[maxIndex + 1];
        _diskWrite = new PdhCounter?[maxIndex + 1];

        foreach (var f in found)
        {
            _diskRead[f.Index] = _pdh!.TryAdd(f.ReadPath);
            _diskWrite[f.Index] = _pdh!.TryAdd(f.ReadPath.Replace("Disk Read Bytes/sec", "Disk Write Bytes/sec"));
        }
    }

    /// <summary>成功挂上的每盘计数器数量（读+写各算一个）。诊断用。</summary>
    public int PerDiskCounterCount => _diskRead.Count(c => c != null) + _diskWrite.Count(c => c != null);

    /// <summary>实际枚举到的物理盘索引，升序。诊断用：对照 D01 的盘列表看有没有漏盘。</summary>
    public IReadOnlyList<int> PerDiskIndices =>
        Enumerable.Range(0, _diskRead.Length).Where(i => _diskRead[i] != null || _diskWrite[i] != null).ToList();

    /// <summary>T0：内存。M0.6 实测 0.016–0.022 ms，20Hz 完全无压力。</summary>
    private void SampleMemory(MetricSnapshot sink)
    {
        if (Win32.TryGetMemory(out var mem))
        {
            double totalMb = mem.ullTotalPhys / 1024.0 / 1024.0;
            double availMb = mem.ullAvailPhys / 1024.0 / 1024.0;
            double usedMb = totalMb - availMb;

            sink.Set(MetricId.RamTotalMb, totalMb);
            sink.Set(MetricId.RamAvailMb, availMb);
            sink.Set(MetricId.RamUsedMb, usedMb);
            sink.Set(MetricId.RamLoad, totalMb > 0 ? usedMb / totalMb * 100.0 : 0);
        }
    }

    /// <summary>
    /// T0b：一次 PDH 刷新读全部计数器。实测 9 个计数器约 2.0 ms —— 这是全项目
    /// 最贵的一层，成本与计数器数量近似线性，所以 floor 提到 200ms 而不是 50ms。
    /// </summary>
    private void SamplePdh(MetricSnapshot sink)
    {
        if (_pdh is not { IsOpen: true }) return;

        // ---- 一次 Collect 刷新整个 query ----
        var sw = Stopwatch.StartNew();
        uint rc = _pdh.Collect();
        sw.Stop();
        LastCollectMs = sw.Elapsed.TotalMilliseconds;
        LastCollectStatus = rc;

        // CPU 占用率：优先 % Processor Utility（与任务管理器口径一致，文档 Q-06）
        double? cpu = _cpuUtility != null ? PdhQuery.TryGetDouble(_cpuUtility, rc) : null;
        cpu ??= _cpuTime != null ? PdhQuery.TryGetDouble(_cpuTime, rc) : null;
        if (cpu.HasValue) sink.Set(MetricId.CpuTotalLoad, Math.Clamp(cpu.Value, 0, 100));

        // C03：每逻辑核占用。同一个 query、同一次 Collect，不需要第二次刷新。
        if (_coreUtility.Count > 0)
        {
            var loads = new double[_coreUtility.Count];
            int valid = 0;
            for (int i = 0; i < loads.Length; i++)
            {
                double? v = PdhQuery.TryGetDouble(_coreUtility[i], rc);
                if (v.HasValue) { loads[i] = Math.Clamp(v.Value, 0, 100); valid++; }
                else loads[i] = double.NaN;
            }

            if (valid > 0)
                sink.SetSeries(MetricId.CpuCoreLoad, loads,
                    valid == loads.Length ? MetricState.Ok : MetricState.Stale);
        }

        // 磁盘速率
        double? diskRead = _diskReadTotal != null ? PdhQuery.TryGetDouble(_diskReadTotal, rc) : null;
        double? diskWrite = _diskWriteTotal != null ? PdhQuery.TryGetDouble(_diskWriteTotal, rc) : null;
        if (diskRead.HasValue) sink.Set(MetricId.DiskReadBytesPerSec, diskRead.Value);
        if (diskWrite.HasValue) sink.Set(MetricId.DiskWriteBytesPerSec, diskWrite.Value);

        double? busy = _diskBusyTotal != null ? PdhQuery.TryGetDouble(_diskBusyTotal, rc) : null;
        if (busy.HasValue) sink.Set(MetricId.DiskBusyPercent, Math.Clamp(busy.Value, 0, 100));

        // D03 每盘：与合计同一次 Collect，只是多几次格式化（实测增量在噪声内）
        if (_diskRead.Length > 0)
        {
            var readPer = new double[_diskRead.Length];
            var writePer = new double[_diskRead.Length];
            int readValid = 0, writeValid = 0;

            for (int i = 0; i < _diskRead.Length; i++)
            {
                double? r = _diskRead[i] != null ? PdhQuery.TryGetDouble(_diskRead[i]!, rc) : null;
                double? w = _diskWrite[i] != null ? PdhQuery.TryGetDouble(_diskWrite[i]!, rc) : null;
                readPer[i] = r ?? double.NaN;
                writePer[i] = w ?? double.NaN;
                if (r.HasValue) readValid++;
                if (w.HasValue) writeValid++;
            }

            if (readValid > 0) sink.SetSeries(MetricId.DiskReadPerDisk, readPer,
                readValid == readPer.Length ? MetricState.Ok : MetricState.Stale);
            if (writeValid > 0) sink.SetSeries(MetricId.DiskWritePerDisk, writePer,
                writeValid == writePer.Length ? MetricState.Ok : MetricState.Stale);
        }

        // 网络：取当前流量最大的物理网卡作为「活动网卡」
        double bestTotal = -1;
        double bestRx = double.NaN, bestTx = double.NaN;
        string bestName = "";

        foreach (var nic in _nics)
        {
            double? rx = PdhQuery.TryGetDouble(nic.Received, rc);
            double? tx = PdhQuery.TryGetDouble(nic.Sent, rc);
            if (!rx.HasValue && !tx.HasValue) continue;

            double sum = (rx ?? 0) + (tx ?? 0);
            if (sum > bestTotal)
            {
                bestTotal = sum;
                bestRx = rx ?? 0;
                bestTx = tx ?? 0;
                bestName = nic.Name;
            }
        }

        if (bestTotal >= 0)
        {
            sink.Set(MetricId.NetDownBytesPerSec, bestRx);
            sink.Set(MetricId.NetUpBytesPerSec, bestTx);
        }
    }

    /// <summary>
    /// T2：内存明细 M04 已缓存 / M05 提交 / M06 页面文件。
    /// 一次 GetPerformanceInfo 同时给出三项，M1.0 实测 **36 µs**，
    /// 所以不必像文档 4.3 原写的那样拆成 2s 与 5s 两个节奏，统一挂 T2（1s）。
    /// </summary>
    private void SampleMemoryExtra(MetricSnapshot sink)
    {
        if (!Win32.TryGetPerformanceInfo(out var pi)) return;

        double pageMb = pi.PageSize / 1024.0 / 1024.0;
        if (pageMb <= 0) return;

        sink.Set(MetricId.RamCachedMb, pi.SystemCache.ToInt64() * pageMb);
        sink.Set(MetricId.RamCommitUsedMb, pi.CommitTotal.ToInt64() * pageMb);
        sink.Set(MetricId.RamCommitLimitMb, pi.CommitLimit.ToInt64() * pageMb);

        // 页面文件使用 = 提交量 − 物理总量。这是推算值，不是"页面文件占用了多少"：
        // 提交量超过物理内存的部分必然落在页面文件（或压缩内存）里。
        // 精确值要走 NtQuerySystemInformation(SystemPageFileInformation)，v1 不做。
        double pageFileMb = (pi.CommitTotal.ToInt64() - pi.PhysicalTotal.ToInt64()) * pageMb;
        sink.Set(MetricId.RamPageFileMb, Math.Max(0, pageFileMb),
            note: "推算值（提交量 − 物理总量），非页面文件独立读数");
    }

    /// <summary>
    /// T2s：C10 负载均值。Windows 没有 load average，这里用全机 CPU 占用做
    /// 60 / 300 / 900 秒时间常数的指数平滑（与 uptime 的语义接近，但不是同一算法）。
    /// 单次 29 µs，5s 一次可忽略。
    /// </summary>
    private void SampleLoadAvg(MetricSnapshot sink)
    {
        if (!Win32.TryGetSystemTimes(out ulong idle, out ulong kernel, out ulong user)) return;

        long now = Stopwatch.GetTimestamp();
        if (!_loadAvgPrimed)
        {
            _lastIdle = idle; _lastKernel = kernel; _lastUser = user;
            _lastLoadAvgTs = now;
            _loadAvgPrimed = true;
            return;   // 首个样本只建基线，没有区间可算
        }

        double dt = (now - _lastLoadAvgTs) / (double)Stopwatch.Frequency;
        if (dt <= 0) return;

        double load = Win32.CpuLoadBetween((_lastIdle, _lastKernel, _lastUser), (idle, kernel, user));
        _lastIdle = idle; _lastKernel = kernel; _lastUser = user; _lastLoadAvgTs = now;

        // alpha = 1 - e^(-dt/τ)，τ = 60 / 300 / 900 s
        double a1 = 1 - Math.Exp(-dt / 60.0);
        double a5 = 1 - Math.Exp(-dt / 300.0);
        double a15 = 1 - Math.Exp(-dt / 900.0);

        // 冷启动时直接取首个读数，避免从 0 慢慢爬上来（前 15 分钟都偏低）
        if (_avg1 <= 0 && _avg5 <= 0 && _avg15 <= 0) { _avg1 = _avg5 = _avg15 = load; }

        _avg1 += (load - _avg1) * a1;
        _avg5 += (load - _avg5) * a5;
        _avg15 += (load - _avg15) * a15;

        sink.Set(MetricId.CpuLoadAvg1, _avg1);
        sink.Set(MetricId.CpuLoadAvg5, _avg5);
        sink.Set(MetricId.CpuLoadAvg15, _avg15);
    }

    /// <summary>
    /// T2：只读缓存值，不阻塞。真正的探测在 <see cref="PingLoop"/> 里。
    /// </summary>
    private void SampleLatency(MetricSnapshot sink)
    {
        double v = _cachedLatencyMs;
        if (double.IsNaN(v))
        {
            sink.Set(MetricId.NetLatencyMs, double.NaN, MetricState.Unavailable, 0, "ICMP 尚未取得有效样本");
            return;
        }

        sink.Set(MetricId.NetLatencyMs, v,
            _pingFailStreak >= 2 ? MetricState.Stale : MetricState.Ok,
            note: _pingFailStreak >= 2 ? $"连续 {_pingFailStreak} 次探测失败" : null);
    }

    private void PingLoop()
    {
        using var ping = new Ping();
        while (!_stopPing.IsSet)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                var reply = ping.Send(PingTarget, 800);
                sw.Stop();
                LastPingMs = sw.Elapsed.TotalMilliseconds;

                if (reply.Status == IPStatus.Success)
                {
                    _cachedLatencyMs = reply.RoundtripTime;
                    _pingFailStreak = 0;
                }
                else
                {
                    _pingFailStreak++;
                }
            }
            catch
            {
                sw.Stop();
                LastPingMs = sw.Elapsed.TotalMilliseconds;
                _pingFailStreak++;
            }

            // 失败时退避到 2s，避免目标不可达时每秒打一个超时包
            int wait = _pingFailStreak > 0 ? 2000 : PingIntervalMs;
            if (_stopPing.Wait(wait)) break;
        }
    }

    public void Dispose()
    {
        _stopPing.Set();
        _pingThread?.Join(1500);
        _pingThread = null;

        _pdh?.Dispose();
        _pdh = null;
        Status = ProviderStatus.NotInitialized;
    }

    private sealed record NicCounter(string Name, PdhCounter Received, PdhCounter Sent);
}
