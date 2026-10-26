namespace CoPul.Collector.Metrics;

/// <summary>
/// 一次完整采样的快照。三视图共享同一份，对应需求文档 A-01「全进程只允许一个采集器」。
/// 采集线程写，UI 线程读，靠 <see cref="Freeze"/> 做一层不可变交付，避免锁竞争。
/// </summary>
public sealed class MetricSnapshot
{
    private readonly Dictionary<MetricId, MetricSample> _scalars = new(64);
    private readonly Dictionary<MetricId, MetricSeries> _series = new(8);

    public MetricSnapshot(long timestampMs)
    {
        TimestampMs = timestampMs;
        WallClock = DateTime.Now;
    }

    /// <summary>单调时钟（Stopwatch ticks 折算的毫秒），不受系统改时间影响。</summary>
    public long TimestampMs { get; }

    public DateTime WallClock { get; }

    public IReadOnlyDictionary<MetricId, MetricSample> Scalars => _scalars;
    public IReadOnlyDictionary<MetricId, MetricSeries> Series => _series;

    public void Set(MetricId id, double value, MetricState state = MetricState.Ok,
                    long timestampMs = 0, string? note = null)
        => _scalars[id] = new MetricSample(id, value, state, timestampMs == 0 ? TimestampMs : timestampMs, note);

    public void SetUnavailable(MetricId id, string? note = null)
        => Set(id, double.NaN, MetricState.Unavailable, TimestampMs, note);

    public void SetSeries(MetricId id, double[] values, MetricState state = MetricState.Ok,
                          long timestampMs = 0, string? note = null)
        => _series[id] = new MetricSeries(id, values, state, timestampMs == 0 ? TimestampMs : timestampMs, note);

    public bool TryGet(MetricId id, out MetricSample sample) => _scalars.TryGetValue(id, out sample);

    public bool TryGetSeries(MetricId id, out MetricSeries series)
    {
        if (_series.TryGetValue(id, out var s) && s is not null)
        {
            series = s;
            return true;
        }
        series = null!;
        return false;
    }

    public double? Value(MetricId id)
        => _scalars.TryGetValue(id, out var s) && s.IsUsable ? s.Value : null;

    /// <summary>复制一份用于跨线程交付。字典本身不再被采集线程触碰。</summary>
    public MetricSnapshot Clone()
    {
        var c = new MetricSnapshot(TimestampMs);
        foreach (var kv in _scalars) c._scalars[kv.Key] = kv.Value;
        foreach (var kv in _series) c._series[kv.Key] = kv.Value;
        return c;
    }
}
