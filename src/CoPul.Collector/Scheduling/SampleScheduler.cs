using System.Diagnostics;
using CoPul.Collector.Metrics;
using CoPul.Collector.Providers;

namespace CoPul.Collector.Scheduling;

public sealed class SampleSchedulerOptions
{
    /// <summary>用户挡位（ms）。50 / 100 / 500 / 1000 / 2000，Q-17 指定。</summary>
    public int UserIntervalMs { get; set; } = 1000;

    /// <summary>省电模式：实际挡位不低于 1s，且 T1 放宽到 1s（P-07 / P-09）。</summary>
    public bool PowerSaving { get; set; }
}

/// <summary>某一层级的运行统计。M0.6 用它验证「实测是否落在 4.0 的估算区间内」。</summary>
public sealed class TierStat
{
    public Tier Tier { get; init; }
    public int IntervalMs { get; internal set; }
    public long Ticks { get; internal set; }
    public double TotalMs { get; internal set; }
    public double MaxMs { get; internal set; }
    public long WindowStartTs { get; internal set; }
    public long WindowEndTs { get; internal set; }

    public double AvgTickMs => Ticks == 0 ? 0 : TotalMs / Ticks;

    /// <summary>
    /// 实际平均间隔（ms）。与 <see cref="IntervalMs"/> 的差值就是调度抖动。
    /// 注意 WindowStartTs/WindowEndTs 本身已经是毫秒（由调度循环用 Stopwatch→ms 折算后传入），
    /// 这里不能再除一次 Stopwatch.Frequency。
    /// </summary>
    public double AvgActualIntervalMs
        => Ticks < 2 ? 0 : (double)(WindowEndTs - WindowStartTs) / (Ticks - 1);

    /// <summary>该层每秒消耗的毫秒数。</summary>
    public double MsPerSecond => AvgActualIntervalMs <= 0 ? 0 : AvgTickMs * 1000.0 / AvgActualIntervalMs;

    /// <summary>折算成「占满一个逻辑核」的百分比。整机占用 = 该值 ÷ 逻辑核数。</summary>
    public double CpuPercentSingleCore => MsPerSecond / 10.0;

    public void Reset()
    {
        Ticks = 0; TotalMs = 0; MaxMs = 0; WindowStartTs = 0; WindowEndTs = 0;
    }
}

/// <summary>
/// 单线程采样调度器。
///
/// 三条来自需求文档的硬约束：
/// - A-01 全进程只有一个采样循环，所有视图共享同一份快照；
/// - 4.0 effective_interval(m) = max(用户挡位, floor(m))，没有指标可以绕过下限；
/// - P-05 挡位变更从下一个 tick 生效，不做瞬时补采。
/// </summary>
public sealed class SampleScheduler : IDisposable
{
    private readonly IReadOnlyList<IMetricProvider> _providers;
    private readonly List<TierState> _tiers = new();
    private readonly SampleSchedulerOptions _options;

    private Thread? _thread;
    private readonly ManualResetEventSlim _stop = new(false);
    private volatile bool _running;
    private volatile bool _tierDirty = true;
    private long _t0;
    private long _snapshotCount;
    private volatile MetricSnapshot _latest = new(0);

    public SampleScheduler(IEnumerable<IMetricProvider> providers, SampleSchedulerOptions? options = null)
    {
        _providers = providers.ToList();
        _options = options ?? new SampleSchedulerOptions();

        BuildTiers();
    }

    /// <summary>按 tier 归组。同一 tier 的所有 group 在同一次 tick 里跑完，共用一个时间片。</summary>
    private void BuildTiers()
    {
        _tiers.Clear();

        var byTier = _providers
            .Where(p => p.Status == ProviderStatus.Ready)
            .SelectMany(p => p.Groups)
            .GroupBy(g => g.Tier)
            .OrderBy(g => (int)g.Key);

        foreach (var g in byTier)
        {
            _tiers.Add(new TierState(g.Key, g.ToList(), EffectiveIntervalMs(g.Key)));
        }
    }

    public MetricSnapshot Latest => _latest;
    public IReadOnlyList<TierStat> Stats => _tiers.Select(t => t.Stat).ToList();
    public long SnapshotCount => _snapshotCount;

    /// <summary>
    /// 快照合并 + 发布的累计耗时。这是「采集之外」的开销，高频挡位下不可忽略：
    /// 每次 tick 都要 Clone 整份快照再逐项覆盖，20Hz 时每秒 20 次。
    /// </summary>
    public double MergeTotalMs { get; private set; }

    public event Action<MetricSnapshot>? SnapshotReady;

    public int UserIntervalMs
    {
        get => _options.UserIntervalMs;
        set
        {
            if (value == _options.UserIntervalMs) return;
            _options.UserIntervalMs = value;
            _tierDirty = true;   // P-05：下一个 tick 生效
        }
    }

    public bool PowerSaving
    {
        get => _options.PowerSaving;
        set
        {
            if (value == _options.PowerSaving) return;
            _options.PowerSaving = value;
            _tierDirty = true;
        }
    }

    /// <summary>实际间隔 = max(用户挡位, tier floor)。省电模式下两者都不低于 1s。</summary>
    public int EffectiveIntervalMs(Tier tier)
    {
        int user = _options.UserIntervalMs;
        int floor = TierInfo.FloorMs(tier);

        if (_options.PowerSaving)
        {
            user = Math.Max(user, 1000);
            floor = Math.Max(floor, 1000);
        }
        return Math.Max(user, floor);
    }

    public void Start()
    {
        if (_running) return;

        foreach (var p in _providers)
        {
            // 初始化失败不阻塞启动（A-12：窗口先出）。UI 读 Status 决定显示什么。
            p.Initialize();
        }

        // 初始化之后才知道谁可用。不可用的 provider 其 group 不进调度 —— 否则
        // 失败的 MSR 组仍会跑一遍 SetThreadAffinityMask（实测 20 核一轮 2–13ms，纯浪费）。
        BuildTiers();

        _t0 = Stopwatch.GetTimestamp();
        foreach (var t in _tiers) t.Prime(_t0, EffectiveIntervalMs(t.Tier));

        _running = true;
        _thread = new Thread(Loop)
        {
            IsBackground = true,
            Name = "CoPul.Sampler",
            Priority = ThreadPriority.AboveNormal
        };
        _thread.Start();
    }

    private void Loop()
    {
        var sw = Stopwatch.StartNew();
        while (_running)
        {
            if (_tierDirty)
            {
                foreach (var t in _tiers)
                {
                    t.IntervalMs = EffectiveIntervalMs(t.Tier);
                    t.Stat.IntervalMs = t.IntervalMs;
                }
                _tierDirty = false;
            }

            long nowMs = (long)(sw.Elapsed.TotalMilliseconds);
            long minWait = long.MaxValue;

            foreach (var t in _tiers)
            {
                if (nowMs >= t.NextDueMs)
                {
                    var sink = new MetricSnapshot(nowMs);
                    foreach (var g in t.Groups) g.Run(sink);

                    double tickMs = t.Groups.Sum(g => g.LastMs);
                    t.RecordTick(nowMs, tickMs);

                    // 追帧保护：落后超过一个周期就直接对齐到下一个周期边界，
                    // 否则系统休眠/卡顿后会连发几十个 tick 补采，把 CPU 打满。
                    t.NextDueMs += t.IntervalMs;
                    if (t.NextDueMs <= nowMs) t.NextDueMs = nowMs + t.IntervalMs;

                    long m0 = Stopwatch.GetTimestamp();
                    _latest = Merge(_latest, sink);
                    MergeTotalMs += (Stopwatch.GetTimestamp() - m0) * 1000.0 / Stopwatch.Frequency;
                    _snapshotCount++;
                    SnapshotReady?.Invoke(_latest);
                }

                long wait = t.NextDueMs - nowMs;
                if (wait < minWait) minWait = wait;
            }

            if (minWait == long.MaxValue) minWait = 10;
            _stop.Wait((int)Math.Clamp(minWait, 1, 20));
        }
    }

    /// <summary>
    /// 把本轮 tier 的部分指标并进总快照。各 tier 刷新节奏不同，
    /// 内存里必须保留其他层的上一轮值，否则 UI 会在两层交替刷新时闪 `—`。
    /// </summary>
    private static MetricSnapshot Merge(MetricSnapshot previous, MetricSnapshot current)
    {
        var merged = previous.Clone();
        foreach (var kv in current.Scalars) merged.Set(kv.Key, kv.Value.Value, kv.Value.State, kv.Value.TimestampMs, kv.Value.Note);
        foreach (var kv in current.Series) merged.SetSeries(kv.Key, kv.Value.Values, kv.Value.State, kv.Value.TimestampMs, kv.Value.Note);
        return merged;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        _stop.Set();
        _thread?.Join(2000);
        _thread = null;
    }

    public void ResetStats()
    {
        foreach (var t in _tiers)
        {
            t.Stat.Reset();
            t.Prime(Stopwatch.GetTimestamp(), t.IntervalMs);
        }
        foreach (var p in _providers)
            foreach (var g in p.Groups)
                g.ResetStats();
        _snapshotCount = 0;
        MergeTotalMs = 0;
    }

    public void Dispose()
    {
        Stop();
        _stop.Dispose();
        foreach (var p in _providers) p.Dispose();
    }

    private sealed class TierState
    {
        public TierState(Tier tier, List<SampleGroup> groups, int intervalMs)
        {
            Tier = tier;
            Groups = groups;
            IntervalMs = intervalMs;
            Stat = new TierStat { Tier = tier, IntervalMs = intervalMs };
        }

        public Tier Tier { get; }
        public List<SampleGroup> Groups { get; }
        public int IntervalMs { get; set; }
        public long NextDueMs { get; set; }
        public TierStat Stat { get; }

        public void Prime(long startTs, int intervalMs)
        {
            IntervalMs = intervalMs;
            Stat.IntervalMs = intervalMs;
            NextDueMs = 0;
        }

        public void RecordTick(long nowMs, double tickMs)
        {
            if (Stat.Ticks == 0) Stat.WindowStartTs = nowMs;
            Stat.WindowEndTs = nowMs;
            Stat.Ticks++;
            Stat.TotalMs += tickMs;
            if (tickMs > Stat.MaxMs) Stat.MaxMs = tickMs;
        }
    }
}
