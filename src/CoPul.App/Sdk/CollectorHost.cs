using System.Windows.Threading;
using CoPul.Collector.Metrics;
using CoPul.Collector.Providers;
using CoPul.Collector.Scheduling;

namespace CoPul.App.Sdk;

/// <summary>
/// 采集宿主：把四个 Provider、调度器、静态信息收在一处，向 UI 只暴露「一帧一帧的结果」。
///
/// 三条约束都落在这里：
/// - **A-12 窗口先出**：Provider 初始化（PDH 约 0.6s）与静态信息（冷盘可到 190ms，R20）
///   全部在后台线程跑，UI 线程一行阻塞调用都没有；
/// - **A-01 单采集循环**：全进程只有一个 <see cref="SampleScheduler"/>，三视图共享快照；
/// - **Dispatcher 限流**：采集线程每 0.5s 发一次快照，若 UI 还没画完就只置脏标记，
///   绝不排队——否则窗口被拖动/最小化时消息队列会堆几十帧，越拖越卡。
/// </summary>
public sealed class CollectorHost : IDisposable
{
    private readonly Dispatcher _ui;
    private readonly NvmlGpuProvider _nvml = new();
    private readonly OsProvider _os = new();
    private readonly MsrCpuProvider _msr = new();
    private readonly NvmeStorageProvider _nvme = new();
    private readonly SampleScheduler _scheduler;

    private int _started;
    private int _dirty;
    private volatile MetricSnapshot? _latest;
    private DispatcherTimer? _frameTimer;
    private long _lastFrameTs;

    /// <summary>
    /// 界面帧间隔下限（ms）。采集按 0.5s 挡位跑，但**不要把每个 tier 的发布都变成一次重绘**：
    /// 一次整窗布局实测约 2 ms（见 docs/M1.1），2.67 帧/秒就是 0.5% 单核。
    /// 对齐到挡位后帧数降到 2 帧/秒，数值仍然每 0.5s 变一次。
    /// </summary>
    private const int MinFrameIntervalMs = 500;

    public CollectorHost(Dispatcher ui)
    {
        _ui = ui;
        Providers = new IMetricProvider[] { _nvml, _os, _msr, _nvme };
        _scheduler = new SampleScheduler(Providers, new SampleSchedulerOptions { UserIntervalMs = 500 });
        _scheduler.SnapshotReady += OnSnapshotReady;
    }

    public IReadOnlyList<IMetricProvider> Providers { get; }

    /// <summary>静态信息。到货前为 null，UI 必须容忍（显示"读取中…"）。</summary>
    public SystemInfo? Info { get; private set; }

    public long SnapshotCount => _scheduler.SnapshotCount;

    /// <summary>一帧新数据，<b>已在 UI 线程</b>。第二参数可能为 null（静态信息还没读完）。</summary>
    public event Action<MetricSnapshot, SystemInfo?>? FrameReady;

    /// <summary>静态信息读完，<b>已在 UI 线程</b>。</summary>
    public event Action<SystemInfo>? InfoReady;

    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;

        // 帧定时器跑在 UI 线程上，比"每来一帧就 BeginInvoke"便宜：
        // 空闲时每次 tick 只是一次时间比较，没有消息排队。
        _frameTimer = new DispatcherTimer(DispatcherPriority.Render, _ui)
        {
            Interval = TimeSpan.FromMilliseconds(MinFrameIntervalMs / 2.0)
        };
        _frameTimer.Tick += (_, _) => TryRender();
        _frameTimer.Start();

        var thread = new Thread(InitThenRun)
        {
            IsBackground = true,
            Name = "CoPul.Init"
        };
        thread.Start();
    }

    /// <summary>
    /// 后台初始化。顺序不能反：<see cref="SampleScheduler.Start"/> 里会调各 Provider 的
    /// <c>Initialize()</c>，NVML 的 <c>DeviceName</c> 只有那之后才有值。
    /// </summary>
    private void InitThenRun()
    {
        try
        {
            _scheduler.Start();

            var info = SystemInfo.Collect();
            info.ApplyNvmlGpuName(_nvml.DeviceName);
            Info = info;

            Post(() => InfoReady?.Invoke(info));
        }
        catch (Exception ex)
        {
            // 初始化炸了也要让窗口活着并说清原因，不能静默退化成空界面。
            var reason = ex.GetBaseException().Message;
            Post(() => InfoReady?.Invoke(new SystemInfo { OsVersion = "初始化失败：" + reason }));
        }
    }

    private void OnSnapshotReady(MetricSnapshot snap)
    {
        _latest = snap;
        Interlocked.Exchange(ref _dirty, 1);   // 只置脏，真正绘制交给 UI 线程的帧定时器
    }

    private void TryRender()
    {
        if (Volatile.Read(ref _dirty) == 0) return;

        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        double sinceMs = _lastFrameTs == 0
            ? double.MaxValue
            : (now - _lastFrameTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        if (sinceMs < MinFrameIntervalMs) return;

        var snap = _latest;
        if (snap is null) return;

        Interlocked.Exchange(ref _dirty, 0);
        _lastFrameTs = now;
        FrameReady?.Invoke(snap, Info);
    }

    private void Post(Action action)
    {
        if (_ui.HasShutdownStarted || _ui.HasShutdownFinished) return;

        try
        {
            // Render 优先级：比 DataBind 低，让输入/布局先走，避免拖动窗口时数值把队列占满。
            _ui.BeginInvoke(DispatcherPriority.Render, action);
        }
        catch (InvalidOperationException)
        {
            // Dispatcher 正在关闭，丢掉这一帧即可
        }
    }

    public void Dispose()
    {
        _scheduler.SnapshotReady -= OnSnapshotReady;
        _frameTimer?.Stop();
        _frameTimer = null;
        _scheduler.Dispose();
    }
}
