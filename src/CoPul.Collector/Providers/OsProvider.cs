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

    private Thread? _pingThread;
    private readonly ManualResetEventSlim _stopPing = new(false);
    private double _cachedLatencyMs = double.NaN;
    private int _pingFailStreak;

    public OsProvider()
    {
        _groups = new List<SampleGroup>
        {
            new("Os.Memory", Tier.T0, SampleMemory),
            new("Os.Perf", Tier.T0b, SamplePdh),
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

        // 磁盘速率
        double? diskRead = _diskReadTotal != null ? PdhQuery.TryGetDouble(_diskReadTotal, rc) : null;
        double? diskWrite = _diskWriteTotal != null ? PdhQuery.TryGetDouble(_diskWriteTotal, rc) : null;
        if (diskRead.HasValue) sink.Set(MetricId.DiskReadBytesPerSec, diskRead.Value);
        if (diskWrite.HasValue) sink.Set(MetricId.DiskWriteBytesPerSec, diskWrite.Value);

        double? busy = _diskBusyTotal != null ? PdhQuery.TryGetDouble(_diskBusyTotal, rc) : null;
        if (busy.HasValue) sink.Set(MetricId.DiskBusyPercent, Math.Clamp(busy.Value, 0, 100));

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
