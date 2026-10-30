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
    private readonly ProcessProvider _proc = new();
    private readonly FpsProvider _fps = new();
    private readonly SampleScheduler _scheduler;

    private int _started;
    private int _dirty;
    private volatile MetricSnapshot? _latest;
    private DispatcherTimer? _frameTimer;
    private long _lastFrameTs;
    private Thread? _partThread;
    private readonly ManualResetEventSlim _stopParts = new(false);

    /// <summary>D04 分区刷新次数（诊断用）。与 <see cref="PartitionChangedCount"/> 一起
    /// 证明"没刷新 UI"是"数据没变"而不是"线程没跑"。</summary>
    public long PartitionRefreshCount;
    public long PartitionChangedCount;

    /// <summary>D04 分区刷新间隔。需求 4.4 标 10000ms，实测单次 444 µs / 3 卷。</summary>
    private const int PartitionRefreshMs = 10_000;

    /// <summary>
    /// 界面帧间隔下限（ms）。采集按 0.5s 挡位跑，但**不要把每个 tier 的发布都变成一次重绘**：
    /// 一次整窗布局实测约 2 ms（见 docs/M1.1），2.67 帧/秒就是 0.5% 单核。
    /// 对齐到挡位后帧数降到 2 帧/秒，数值仍然每 0.5s 变一次。
    /// </summary>
    private const int MinFrameIntervalMs = 500;

    public CollectorHost(Dispatcher ui)
    {
        _ui = ui;
        Providers = new IMetricProvider[] { _nvml, _os, _msr, _nvme, _proc, _fps };
        _scheduler = new SampleScheduler(Providers, new SampleSchedulerOptions { UserIntervalMs = 500 });
        _scheduler.SnapshotReady += OnSnapshotReady;
    }

    public IReadOnlyList<IMetricProvider> Providers { get; }

    /// <summary>静态信息。到货前为 null，UI 必须容忍（显示"读取中…"）。</summary>
    public SystemInfo? Info { get; private set; }

    /// <summary>
    /// N05 活动网卡名。字符串装不进 <see cref="MetricSnapshot"/>（只有 double），
    /// 所以单独开一条旁路给 UI，每帧读一次属性即可（无锁，写入方是采集线程，
    /// 引用赋值在 .NET 上是原子的，读到旧值最多晚 0.5 秒）。
    /// </summary>
    public string ActiveNicName => _os.ActiveNicName;

    /// <summary>N03 延迟探测目标，界面用来标注"延迟测的是谁"。</summary>
    public string PingTarget => _os.PingTarget;

    /// <summary>
    /// 改延迟探测目标（G-08 设置页）。取值落在 <see cref="OsProvider.PingTarget"/> 上，
    /// 探针线程下一轮就按新目标发 —— 不需要重启采集，也不需要重开程序。
    ///
    /// <para>
    /// 这里不落盘：CollectorHost 不认识配置层（它被设计成只管采集），
    /// 持久化由设置页写 <see cref="AppConfig.PingTarget"/> 完成。
    /// </para>
    /// </summary>
    public void SetPingTarget(string target) => _os.PingTarget = target;

    /// <summary>
    /// 占用最高的若干进程（M1.4）。与 N05 网卡名同一条旁路：复合结构不进快照。
    /// 5 秒刷新一次，读到旧数组最多晚 5 秒。
    /// </summary>
    public IReadOnlyList<ProcessStat> TopProcesses => _proc.Top;

    /// <summary>每进程 GPU 占用在这台机器上能不能拿到（WDDM 显卡通常拿不到）。</summary>
    public bool ProcessGpuAvailable => _proc.GpuAvailable;

    /// <summary>
    /// F01–F04 的统计对象（PresentMon 的 Application 列）。空串表示当前没有任何进程在渲染，
    /// 与网卡名同理：字符串装不进 <see cref="MetricSnapshot"/>，走旁路。
    /// </summary>
    public string FpsTargetApp => _fps.TargetApp;

    /// <summary>FPS 会话是否真的接上了。用来区分「没提权/没接上」和「接上了但前台没在渲染」。</summary>
    public bool FpsCollecting => _fps.IsCollecting;

    /// <summary>FPS 不可用的具体原因。写在 FPS 区块副标题上——状态栏那一行在窗口底部，
    /// 而 FPS 区块在屏幕中部，让人为了看一句原因去滚到底部是不合理的。</summary>
    public string? FpsUnavailableReason => _fps.UnavailableReason;

    /// <summary>
    /// FPS 区块折叠时关掉 PresentMon。它是独立进程，自己就要吃 0.3–0.5% 单核，
    /// 折叠了就没人看，没必要一直烧着。
    /// </summary>
    public bool FpsSamplingEnabled
    {
        get => _fps.Enabled;
        set => _fps.Enabled = value;
    }

    /// <summary>
    /// 进程区块折叠时关掉这两组采集。进程快照 + GPU Engine 一轮约 20ms，
    /// 是全部采集里最贵的一组，看不见就别采。
    /// </summary>
    public bool ProcessSamplingEnabled
    {
        get => _proc.Enabled;
        set => _proc.Enabled = value;
    }

    public long SnapshotCount => _scheduler.SnapshotCount;

    /// <summary>一帧新数据，<b>已在 UI 线程</b>。第二参数可能为 null（静态信息还没读完）。</summary>
    public event Action<MetricSnapshot, SystemInfo?>? FrameReady;

    /// <summary>静态信息读完，<b>已在 UI 线程</b>。</summary>
    public event Action<SystemInfo>? InfoReady;

    /// <summary>D04 分区数据变了（10s 一次，且只在真的变了时触发），<b>已在 UI 线程</b>。</summary>
    public event Action<SystemInfo>? PartitionsRefreshed;

    /// <summary>
    /// Provider 可用状态发生变化，<b>已在 UI 线程</b>。
    ///
    /// 原来状态只在 <see cref="InfoReady"/> 时摊一次，但 FPS 是异步的：PresentMon 起得来、
    /// 首行 CSV 却可能在 1 秒后失败（ETW 会话名冲突、被杀软拦），那时状态栏还写着"运行中"。
    /// 这里每 2 秒比一次签名，只有真变了才通知。
    /// </summary>
    public event Action<IReadOnlyList<IMetricProvider>>? ProvidersChanged;

    private string _providerSignature = "";
    private long _lastProviderCheckTs;

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

            // D04 的 10s 刷新：不能像静态信息那样只读一次（4.4 标 10000ms），
            // 但也不能进采集循环——它按 0.5s 跑等于每 tick 白读三次卷。
            _partThread = new Thread(PartitionLoop)
            {
                IsBackground = true,
                Name = "CoPul.Partitions",
                Priority = ThreadPriority.BelowNormal
            };
            _partThread.Start();
        }
        catch (Exception ex)
        {
            // 初始化炸了也要让窗口活着并说清原因，不能静默退化成空界面。
            var reason = ex.GetBaseException().Message;
            Post(() => InfoReady?.Invoke(new SystemInfo { OsVersion = "初始化失败：" + reason }));
        }
    }

    /// <summary>
    /// D04 后台刷新循环。用 BelowNormal 优先级 + 10s 间隔，单次 0.4–0.5 ms，
    /// 折算整机 0.002%，可以忽略。
    /// </summary>
    private void PartitionLoop()
    {
        while (!_stopParts.Wait(PartitionRefreshMs))
        {
            var info = Info;
            if (info is null) continue;

            Interlocked.Increment(ref PartitionRefreshCount);
            if (!info.RefreshPartitions()) continue;

            Interlocked.Increment(ref PartitionChangedCount);
            Post(() => PartitionsRefreshed?.Invoke(info));
        }
    }

    private void OnSnapshotReady(MetricSnapshot snap)    {
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
        CheckProviders();
        FrameReady?.Invoke(snap, Info);
    }

    /// <summary>
    /// 每 2 秒比对一次 Provider 状态签名。签名用「状态码 + 原因」拼成：
    /// 只看状态码不够——同样是 Failed，"没提权"和"exe 缺失"的提示文案要跟着换。
    /// </summary>
    private void CheckProviders()
    {
        long now = System.Diagnostics.Stopwatch.GetTimestamp();
        if ((now - _lastProviderCheckTs) * 1000.0 / System.Diagnostics.Stopwatch.Frequency < 2000) return;
        _lastProviderCheckTs = now;

        var sb = new System.Text.StringBuilder(256);
        foreach (var p in Providers) sb.Append((int)p.Status).Append(':').Append(p.UnavailableReason).Append('|');

        string sig = sb.ToString();
        if (sig == _providerSignature) return;

        _providerSignature = sig;
        ProvidersChanged?.Invoke(Providers);
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
        _stopParts.Set();
        _partThread = null;
        _scheduler.Dispose();
    }
}
