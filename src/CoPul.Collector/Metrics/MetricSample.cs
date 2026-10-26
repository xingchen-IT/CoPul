namespace CoPul.Collector.Metrics;

/// <summary>单个指标的可用性状态。UI 靠它决定显示数值还是 `—`。</summary>
public enum MetricState
{
    /// <summary>本轮采到了有效值。</summary>
    Ok = 0,

    /// <summary>本轮采集失败，显示上一轮值并标注过期。</summary>
    Stale = 1,

    /// <summary>该指标在本机不存在或权限不足，显示 `—`。</summary>
    Unavailable = 2,

    /// <summary>采到了但数值不合理（例如温度恒为 0），按 A-13 视为无效。</summary>
    Invalid = 3
}

/// <summary>一个标量指标的一次采样。</summary>
public readonly record struct MetricSample(
    MetricId Id,
    double Value,
    MetricState State,
    long TimestampMs,
    string? Note = null)
{
    public bool IsUsable => State == MetricState.Ok;
}

/// <summary>一个数组型指标的一次采样（每逻辑核温度、每块盘温度）。</summary>
public sealed record MetricSeries(
    MetricId Id,
    double[] Values,
    MetricState State,
    long TimestampMs,
    string? Note = null)
{
    public bool IsUsable => State == MetricState.Ok;

    public double Max => Values.Length == 0 ? double.NaN : Values.Max();
}
