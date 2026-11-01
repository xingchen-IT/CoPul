using System.Diagnostics;
using CoPul.Collector.Metrics;
using CoPul.Collector.Providers;

namespace CoPul.Collector.Scheduling;

public sealed class SampleSchedulerOptions
{
    /// <summary>
    /// 用户挡位（ms）。v1 只开放 500ms（Q-23），M4.24 起开放 0.5/1/2/3/5 秒五档。
    /// 调度器本身不校验，config 层读入后必须用 <see cref="AllowedIntervalsMs"/> 过滤，
    /// 防止旧配置文件里的 50 / 1000 直接生效。
    /// </summary>
    public int UserIntervalMs { get; set; } = 500;

    /// <summary>
    /// 可选挡位白名单（ms）。实际生效间隔 = max(用户挡位, tier floor)，
    /// 所以**调慢一定生效，调快受 floor 约束**（各指标的 floor 见 <c>TierInfo</c>）。
    ///
    /// <para>
    /// 5s 是给"只想看个大概、把占用压到最低"的场景留的（A-26 性能预算那一路），
    /// 再往上趋势图就没法看了。
    /// </para>
    /// </summary>
    public static readonly int[] AllowedIntervalsMs = { 500, 1000, 2000, 3000, 5000 };

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
    /// <summary>
    /// 调度循环单次最长睡眠（ms）。取值需 <b>小于最短 tier floor</b>，否则会错过 tick；
    /// 同时要足够大，避免高频空转。0.5s 挡位下最小 floor 是 500（T0/T0b/T1/T1b 全被钳到 500），
    /// 因此取 500 —— 正好一个 tick，醒来即到点。R17 的前后对比见 docs/M0.6 报告 9.4 节。
    /// </summary>
    private const int MaxWaitMs = 500;

    /// <summary>
    /// 长档位下的唤醒上限（ms）。挡位调到 3s/5s 后如果还按 500ms 醒一次，
    /// 一个采样周期要空转 6~10 次——虽然每次只是遍历几个 tier 做比较，
    /// 但既然 R17 的目的就是"别为没事可做醒来"，上限就跟着挡位放宽。
    /// 上限取 2s 而不是挡位本身：挡位变更与停止都由 <c>_stop.Set()</c> 立即打断，
    /// 不需要靠短睡眠来保响应，2s 足够覆盖 tier floor 比用户挡位更细的情况。
    /// </summary>
    private const int MaxWaitCapMs = 2000;

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
            if (_running) _stop.Set();   // 立即唤醒，否则最坏要等 MaxWaitMs 才生效
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
            if (_running) _stop.Set();
        }
    }

    /// <summary>
    /// 实际间隔 = max(用户挡位, tier floor)，并<b>向上对齐到用户挡位的整数倍</b>。省电模式下两者都不低于 1s。
    ///
    /// <para>
    /// 为什么还要对齐（M4.24）：不对齐时慢层会在快层的两个 tick 之间单独到期，
    /// 触发一次"只有它自己"的发布 —— 整窗多一次重绘，用户看到的是
    /// "档位调成 2s，数值却 1.67s 就跳一次"（实测 30s 内发布 18 次而非 15 次：
    /// floor=5s 的层按 5s 走，与 2s 栅格不同步）。
    /// 对齐后所有层都落在挡位的整数倍上，同一批到期、只发布一次。
    /// </para>
    ///
    /// <para>
    /// 500ms 档下各 floor（1000/5000/10000）本来就都是 500 的整数倍，
    /// 所以对齐对默认档零影响 —— 这条是"档位开放后才暴露"的问题，不是回归。
    /// </para>
    /// </summary>
    public int EffectiveIntervalMs(Tier tier)
    {
        int user = _options.UserIntervalMs;
        int floor = TierInfo.FloorMs(tier);

        if (_options.PowerSaving)
        {
            user = Math.Max(user, 1000);
            floor = Math.Max(floor, 1000);
        }

        int eff = Math.Max(user, floor);

        // 抬到挡位的整数倍。代价是慢层比 floor 稍慢一点（2s 档下 5s 的层变 6s），
        // 换来的是"档位 == 实际刷新节奏"，这正是用户调这个档位想拿到的东西。
        if (user > 0 && eff % user != 0) eff = (eff / user + 1) * user;
        return eff;
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
                long dirtyNow = (long)sw.Elapsed.TotalMilliseconds;
                foreach (var t in _tiers)
                {
                    t.IntervalMs = EffectiveIntervalMs(t.Tier);
                    t.Stat.IntervalMs = t.IntervalMs;

                    // 挡位一变相位就散了：慢层原本按 5s 走，改 2s 挡后它的 NextDueMs
                    // 未必落在 2s 栅格上，于是永远卡在快层两个 tick 之间单独到期。
                    // 这里统一重排到"下一个栅格点"，代价是慢层最多多等一个周期，
                    // 只在改挡位时发生一次。
                    t.NextDueMs = (dirtyNow / t.IntervalMs + 1) * t.IntervalMs;
                }
                _tierDirty = false;
            }

            long nowMs = (long)(sw.Elapsed.TotalMilliseconds);
            long minWait = long.MaxValue;

            // 本轮到期的 tier 先都跑完，最后只发布一次。
            // 0.5s 挡位下 T0/T0b/T1/T1b 都会落在同一个 500ms 边界上，如果每层各发一次，
            // 界面就会从 2 帧/秒变成 8 帧/秒 —— 每个视图都要多做 4 倍的布局与渲染。
            List<MetricSnapshot>? due = null;

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
                    // 落点用整除算而不是 nowMs + interval：后者会让慢层偏离挡位栅格，
                    // 从此一直在快层之间单独到期（见 EffectiveIntervalMs 的对齐说明）。
                    t.NextDueMs += t.IntervalMs;
                    if (t.NextDueMs <= nowMs) t.NextDueMs = (nowMs / t.IntervalMs + 1) * t.IntervalMs;

                    (due ??= new List<MetricSnapshot>(4)).Add(sink);
                }

                long wait = t.NextDueMs - nowMs;
                if (wait < minWait) minWait = wait;
            }

            if (due is not null)
            {
                long m0 = Stopwatch.GetTimestamp();
                _latest = Merge(_latest, due);
                MergeTotalMs += (Stopwatch.GetTimestamp() - m0) * 1000.0 / Stopwatch.Frequency;
                _snapshotCount++;
                SnapshotReady?.Invoke(_latest);
            }

            if (minWait == long.MaxValue) minWait = MaxWaitMs;

            // R17：原实现把等待上限钳到 20ms，于是不论挡位多少，每秒都要唤醒约 60 次
            // （0.5s 挡位下 60 秒约 3600 次）。每次醒来都要遍历全部 tier 才发现没事可做，
            // 实测 150–190ms / 60s 的 CPU 全耗在这上面 —— 这是与挡位无关的固定开销，
            // 正是 1s 与 2s 两档中位完全相同（0.417%）的原因。
            // 上限放到 MaxWaitMs 后唤醒次数降一个数量级；挡位变更与停止由 _stop.Set() 立即打断。
            int cap = Math.Clamp(_options.UserIntervalMs, MaxWaitMs, MaxWaitCapMs);
            _stop.Wait((int)Math.Clamp(minWait, 1, cap));
            _stop.Reset();   // 清掉本次唤醒标志供下一轮使用（停止时 _running 已为 false，不会漏)
        }
    }

    /// <summary>
    /// 把本轮到期的各 tier 的部分指标并进总快照。各 tier 刷新节奏不同，
    /// 内存里必须保留其他层的上一轮值，否则 UI 会在两层交替刷新时闪 `—`。
    /// </summary>
    private static MetricSnapshot Merge(MetricSnapshot previous, IReadOnlyList<MetricSnapshot> current)
    {
        var merged = previous.Clone();
        foreach (var sink in current)
        {
            foreach (var kv in sink.Scalars) merged.Set(kv.Key, kv.Value.Value, kv.Value.State, kv.Value.TimestampMs, kv.Value.Note);
            foreach (var kv in sink.Series) merged.SetSeries(kv.Key, kv.Value.Values, kv.Value.State, kv.Value.TimestampMs, kv.Value.Note);
        }
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

        // 每个 Provider **单独兜底**（2026-11-01 修一个真实崩溃）。
        //
        // 实测：跑一份缺 LibreHardwareMonitorLib.dll 的精简部署时，关窗口崩在
        // MsrCpuProvider.Dispose()，异常一路穿到 Window.WmDestroy，
        // 用户看到的是"关程序时弹错误框"。
        //
        // 为什么 MsrCpuProvider.Dispose() 内部那个 try/catch 没兜住：
        // 异常不是发生在 try 块**执行时**，而是发生在**这个方法被 JIT 编译时** ——
        // 编译它需要解析 IntelMsr 类型，而那个类型所在的程序集根本不存在，
        // 于是方法还没开始执行就先抛 TypeLoad/FileNotFound，catch 来不及生效。
        // 这类"方法体里的 try 救不了自己"的坑，只能在上层用 try 包住**调用**。
        //
        // 顺带收益：任何一个 Provider 释放失败都不再阻断其它 Provider 的释放。
        foreach (var p in _providers)
        {
            try { p.Dispose(); }
            catch (Exception ex)
            {
                // 只记 Debug 输出、不抛：这是进程退出路径，抛出只会让用户看到崩溃框。
                // （Collector 是独立项目、不引用 App 的 Trace，这里用 Trace.WriteLine
                //   —— 调试器下可见，生产静默，正是这条路径需要的强度。）
                Trace.WriteLine($"[dispose] provider={p.Name} 释放失败（已忽略）：{ex.GetType().Name}: {ex.Message}");
            }
        }
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
