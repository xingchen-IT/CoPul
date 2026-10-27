namespace CoPul.Collector.Providers;

/// <summary>
/// 采样层级。下限（floor）来自需求文档 4.0，实际间隔 = max(用户挡位, floor)。
/// M0.5 实测把原来的 T1 拆成 T1 与 T1b：全核温度 98% 的成本在 SetThreadAffinityMask 切核。
/// </summary>
public enum Tier
{
    /// <summary>GPU 占用/显存、内存。NVML + 内存计数，单次约 0.06ms，floor 100ms。</summary>
    T0 = 0,

    /// <summary>封装温度、CPU 功耗、频率、GPU 温度/功耗。MSR / NVML 寄存器级，floor 200ms。</summary>
    T1 = 1,

    /// <summary>全核温度。需要逐核切亲和性，成本是纯读的 40 倍，floor 500ms。</summary>
    T1b = 2,

    /// <summary>延迟、丢包、电压、风扇、GPU 低频项。floor 1000ms。</summary>
    T2 = 3,

    /// <summary>硬盘温度。NVMe IOCTL 单次 2.6–3.5ms，固定 5000ms（P-08）。</summary>
    T2Slow = 4,

    /// <summary>型号、磁盘列表、SPD、分区容量。仅启动一次或 10s。</summary>
    T3 = 5,

    /// <summary>
    /// T0 的重活部分：PDH 计数器刷新。M0.6 实测 9 个计数器一次 Collect 约 2.0 ms，
    /// 与计数器数量近似线性（每个约 0.2 ms）。
    /// 而内存 API（0.016 ms）与 NVML（0.044 ms）便宜两个数量级，
    /// 所以把 T0 拆成「便宜的 T0」与「贵的 T0b」，避免为了 CPU 占用率把
    /// 内存/显存也一起拖到 200ms。
    /// </summary>
    T0b = 6
}

public static class TierInfo
{
    public static int FloorMs(Tier tier) => tier switch
    {
        // 100ms 而非 50ms：50ms 挡位已于 v0.6 砍掉（只有 T0 两项真能跑 20Hz，
        // 而 50ms 档 2.185% 已贴 2.6% 上限、无余量）。floor 与最小可选挡位对齐。
        Tier.T0 => 100,
        Tier.T1 => 200,
        // M1.2 曾按 A-26 试抬到 1000ms 验证（整窗 6 轮里 2 轮越 1.5% 线），
        // 四轮结果 1.380 / 1.197 / 2.062 / 1.445（中位 1.41）**反而高于**抬之前的
        // 1.276 —— 当时机器上有 java.exe 78% 的持续负载，参数 0.1pp 级的收益
        // 完全被 0.7pp 级的噪声淹没，实验无效。**已回滚到 500ms**，
        // 等干净环境重测后若仍超标再启用。不要凭一次噪声结论改指标频率。
        Tier.T1b => 500,
        Tier.T2 => 1000,
        Tier.T2Slow => 5000,
        Tier.T3 => 10000,
        Tier.T0b => 200,
        _ => 1000
    };

    public static string Label(Tier tier) => tier switch
    {
        Tier.T0 => "T0 高速",
        Tier.T1 => "T1 寄存器",
        Tier.T1b => "T1b 全核",
        Tier.T2 => "T2 慢变",
        Tier.T2Slow => "T2s 硬盘温度",
        Tier.T3 => "T3 极低频",
        Tier.T0b => "T0b PDH 重活",
        _ => tier.ToString()
    };
}

public enum ProviderStatus
{
    /// <summary>尚未 Initialize。</summary>
    NotInitialized = 0,

    /// <summary>Initialize 成功，可采样。</summary>
    Ready = 1,

    /// <summary>本机没有该硬件（例如无 NVIDIA 显卡）。不算错误。</summary>
    Unsupported = 2,

    /// <summary>硬件存在但初始化失败（权限、驱动、被拦截）。</summary>
    Failed = 3
}

/// <summary>
/// 一个可独立调度的采样组。Provider 内部按「成本相近」拆组，
/// 例如 MsrCpuProvider 拆成 Msr.Package（T1，200ms）与 Msr.AllCores（T1b，500ms）。
/// </summary>
public sealed class SampleGroup
{
    private readonly Action<Metrics.MetricSnapshot> _sample;

    public SampleGroup(string name, Tier tier, Action<Metrics.MetricSnapshot> sample)
    {
        Name = name;
        Tier = tier;
        _sample = sample;
    }

    public string Name { get; }
    public Tier Tier { get; }

    // ---- 运行统计（供 M0.6 基准与后续性能看板使用）----
    public long SampleCount { get; private set; }
    public double TotalMs { get; private set; }
    public double MaxMs { get; private set; }
    public double LastMs { get; private set; }
    public long ErrorCount { get; private set; }

    /// <summary>立即采样一次。调度器用；基准脚本也用它做单组隔离测量。</summary>
    public void Run(Metrics.MetricSnapshot sink)
    {
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            _sample(sink);
        }
        catch (Exception)
        {
            // A-03：单指标失败不影响其他指标。Provider 内部应自行降级，这里只兜底防崩。
            ErrorCount++;
        }
        finally
        {
            double ms = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1000.0
                        / System.Diagnostics.Stopwatch.Frequency;
            LastMs = ms;
            TotalMs += ms;
            SampleCount++;
            if (ms > MaxMs) MaxMs = ms;
        }
    }

    public void ResetStats()
    {
        SampleCount = 0; TotalMs = 0; MaxMs = 0; LastMs = 0; ErrorCount = 0;
    }

    public double AvgMs => SampleCount == 0 ? 0 : TotalMs / SampleCount;
}

/// <summary>
/// 采集提供者。一个 Provider 代表一条独立的硬件访问路径（NVML / MSR / NVMe / OS），
/// 各自独立初始化、独立降级 —— 对应需求文档 3.1 的四 Provider 分层。
/// </summary>
public interface IMetricProvider : IDisposable
{
    string Name { get; }

    ProviderStatus Status { get; }

    /// <summary>不可用的具体原因，直接进设置页「传感器诊断」（Q-18）。</summary>
    string? UnavailableReason { get; }

    /// <summary>给用户看的修复建议（例如「安装 PawnIO 驱动」+ 下载地址）。</summary>
    string? Remedy { get; }

    /// <summary>初始化耗时（ms）。M0 实测 Computer.Open() 提权后 3532ms，所以这项必须可观测。</summary>
    double InitializeMs { get; }

    /// <summary>幂等。失败不抛异常，改写 Status / UnavailableReason 并返回 false。</summary>
    bool Initialize();

    IReadOnlyList<SampleGroup> Groups { get; }
}
