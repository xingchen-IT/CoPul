using System.Diagnostics;
using CoPul.Collector.Interop;
using CoPul.Collector.Metrics;

namespace CoPul.Collector.Providers;

/// <summary>
/// FPS 采集（需求 4.6）。数据源 = 随包的 PresentMon.exe，它消费 ETW 的 DXGI/D3D9
/// Present 事件流；Windows 本身没有任何 API 能给帧率（Q-07 已批准附带该二进制）。
///
/// 口径（六个数字全部出自同一个 60 秒窗口，避免"图上没有这么低、最低值却很难看"）：
/// - **当前帧率**：最近 1 秒内的呈现帧数换算，<c>(n-1) × 1000 / (t_last - t_first)</c>；
/// - **最低 / 最高 / 平均**：把 60 秒切成 60 个 1 秒桶，每桶算一个帧率再取极值与均值；
/// - **1% Low**：60 秒内最慢 1% 帧的平均帧时间取倒数（业界同 RTSS / CapFrameX 的口径）；
/// - **帧时间**：最近 1 秒的平均帧间隔，等于 1000 / 当前帧率。
///
/// 目标进程怎么选（这块决定了面板显示的是谁的帧率）：
/// 1. 前台窗口进程若在最近 2 秒内有呈现 → 就用它（"前台进程帧率"的字面含义）；
/// 2. 否则退到最近 2 秒内呈现帧最多的进程 —— 切到桌面/聊天窗口时游戏仍在渲染，
///    此时继续显示游戏的帧率比显示一片 `—` 有用；
/// 3. 都没有 → 六个格子全 `—`（A-13：读不到不许写 0）。
///
/// 折叠 FPS 区块时整个会话会被关掉：PresentMon 是独立进程，自己就要吃 0.3–0.5% 单核，
/// 与"看不见就别采"（见 <see cref="ProcessProvider.Enabled"/>）同一条原则。
/// </summary>
public sealed class FpsProvider : IMetricProvider
{
    /// <summary>统计窗口。与趋势图 120 点 × 0.5s 对齐，图上的范围就是统计的范围。</summary>
    private const int WindowMs = 60_000;

    /// <summary>当前帧率的观察窗。</summary>
    private const int InstantWindowMs = 1_000;

    /// <summary>超过这个时间没有呈现帧，就不认为它"正在渲染"。</summary>
    private const int ActiveWindowMs = 2_000;

    /// <summary>这么久没有帧就丢掉该进程的全部历史，防止进程 ID 复用后串数据。</summary>
    private const int ForgetAfterMs = 10_000;

    /// <summary>1% Low 是慢变量，每秒重算一次即可（排序 1 万个数约 0.8ms，0.5s 一次就白费一倍）。</summary>
    private const int Low1IntervalMs = 1_000;

    private readonly List<SampleGroup> _groups;
    private readonly Dictionary<int, Track> _tracks = new();
    private readonly object _gate = new();

    /// <summary>1% Low 的排序暂存。预分配，避免每秒一次的大数组分配。</summary>
    private readonly float[] _scratch = new float[Track.Capacity];

    private PresentMonSession? _session;
    private string _exePath = "";

    private long _low1Ts = long.MinValue;
    private int _low1Pid = -1;
    private double _low1 = double.NaN;

    private volatile string _targetApp = "";
    private volatile int _targetPid;
    private volatile bool _enabled = true;

    /// <summary>采样组自己也用一份 tick 时钟，避免每帧都去读 Stopwatch。</summary>
    private long _nowMs;

    public FpsProvider()
    {
        _groups = new List<SampleGroup>
        {
            // 采样成本 = 遍历 60 秒窗口（本机高帧率下约 1.2 万条）：实测 40–80 µs，
            // 落在 T0 完全没问题。T0 的 floor 是 100ms，实际由用户挡位 500ms 决定。
            new SampleGroup("FPS.Stats", Tier.T0, Sample)
        };
    }

    public string Name => "帧率";

    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;

    public string? UnavailableReason { get; private set; }

    public string? Remedy { get; private set; }

    public double InitializeMs { get; private set; }

    public IReadOnlyList<SampleGroup> Groups => _groups;

    /// <summary>当前正在统计的进程名（PresentMon 的 Application 列）。空串 = 没有目标。</summary>
    public string TargetApp => _targetApp;

    public int TargetPid => _targetPid;

    /// <summary>
    /// 会话是否真的接上了。
    ///
    /// 这里**不能**再要求 <see cref="PresentMonSession.HeaderParsed"/>：PresentMon 的 CSV 表头
    /// 是「第一次要输出数据行时才写」（CsvOutput.cpp: WriteCsvRow 内 <c>if (*fp == nullptr)</c> 分支），
    /// 桌面静止、没有任何进程呈现时它一行都不写。若把"有表头"当成"接上了"，
    /// 用户会看到永久性的"未接入"，而会话其实是好的。
    /// </summary>
    public bool IsCollecting => _session is { IsRunning: true };

    /// <summary>累计解析帧数，诊断用。</summary>
    public long FrameCount => _session?.FrameCount ?? 0;

    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value) return;
            _enabled = value;

            if (value) StartSession();
            else StopSession();
        }
    }

    public bool Initialize()
    {
        long t0 = Stopwatch.GetTimestamp();

        _exePath = ResolveExePath();
        if (_exePath.Length == 0)
        {
            Status = ProviderStatus.Failed;
            UnavailableReason = "随包的 PresentMon.exe 不存在（应位于程序目录的 presentmon 子目录）";
            InitializeMs = Elapsed(t0);
            return false;
        }

        if (!Win32.IsAdministrator())
        {
            // 不用起进程试一次：ETW 会话必然被拒。直接给出人话，界面据此显示"需管理员"。
            Status = ProviderStatus.Failed;
            UnavailableReason = "FPS 需管理员权限（PresentMon 的 ETW 会话被拒绝访问）";
            Remedy = "以管理员身份运行 CoPul（或把当前用户加入「性能日志用户」组）";
            InitializeMs = Elapsed(t0);
            return false;
        }

        StartSession();

        if (_session is null || !_session.IsRunning)
        {
            Status = ProviderStatus.Failed;
            // 不能无条件覆盖：StartSession 已经写好了具体原因（未提权 / exe 缺失 / 启动异常），
            // 这里再用"未能启动"盖掉，等于把唯一能定位问题的信息扔了。
            if (string.IsNullOrEmpty(UnavailableReason))
                UnavailableReason = _session?.LastError ?? "PresentMon 未能启动";
            InitializeMs = Elapsed(t0);
            return false;
        }

        Status = ProviderStatus.Ready;
        InitializeMs = Elapsed(t0);
        return true;
    }

    /// <summary>
    /// 找随包的 presentmon。优先 &lt;程序目录&gt;\presentmon\PresentMon.exe，
    /// 其次程序目录本身（单文件发布时可能被拍平）。
    /// </summary>
    private static string ResolveExePath()
    {
        string baseDir = AppContext.BaseDirectory;
        string nested = Path.Combine(baseDir, "presentmon", "PresentMon.exe");
        if (File.Exists(nested)) return nested;

        string flat = Path.Combine(baseDir, "PresentMon.exe");
        return File.Exists(flat) ? flat : "";
    }

    private void StartSession(bool allowUnelevated = false)
    {
        if (_session is not null) return;
        if (_exePath.Length == 0) return;

        var s = new PresentMonSession { Frame = OnFrame };
        s.Faulted += reason =>
        {
            // 看门狗还在重连，这里只翻状态；真正"连续失败"由 LastError 的措辞体现。
            UnavailableReason = reason;
            Status = ProviderStatus.Failed;
        };

        // allowUnelevated 只给离线自测夹具用（fake PresentMon 不碰 ETW，不需要提权）。
        if (!s.Start(_exePath, requireElevation: !allowUnelevated))
        {
            UnavailableReason = s.LastError;
            Status = ProviderStatus.Failed;
            s.Dispose();
            return;
        }

        _session = s;
        Status = ProviderStatus.Ready;
        UnavailableReason = null;
    }

    private void StopSession()
    {
        var s = _session;
        _session = null;
        if (s is null) return;

        s.Frame = null;
        s.Dispose();

        lock (_gate)
        {
            _tracks.Clear();
        }
        _targetApp = "";
        _targetPid = 0;
        _low1 = double.NaN;
        _low1Pid = -1;
    }

    /// <summary>读数线程回调。每帧一次，只做环写入，不做任何计算。</summary>
    private void OnFrame(PresentFrame f)
    {
        if (!_enabled) return;

        lock (_gate)
        {
            // 时基重对齐（PresentMon 换了会话）：旧帧的时间戳全部作废。
            if (f.NewEpoch)
            {
                _tracks.Clear();
                _low1Pid = -1;
            }

            if (!_tracks.TryGetValue(f.Pid, out var track))
            {
                track = new Track { App = f.App };
                _tracks[f.Pid] = track;
            }
            // 用呈现时刻而不是收到这一行的时刻（见 PresentFrame.TsMs 注释）。
            track.Add(f.TsMs, f.BetweenMs);
        }
    }

    // =================================================================
    // 采样：把 60 秒窗口算成六个数字写进快照
    // =================================================================

    private void Sample(MetricSnapshot sink)
    {
        long now = NowMs();
        _nowMs = now;

        Track? target;
        int pid;
        string app;

        lock (_gate)
        {
            Forget(now);
            pid = PickTarget(now, out app);
            target = pid != 0 && _tracks.TryGetValue(pid, out var t) ? t : null;
        }

        _targetPid = pid;
        _targetApp = app;

        if (target is null)
        {
            // 没有正在渲染的进程（或者会话还没接上）。六格全 `—`，副标题会说明原因。
            sink.SetUnavailable(MetricId.FpsPresent);
            sink.SetUnavailable(MetricId.FpsFrameTimeMs);
            sink.SetUnavailable(MetricId.FpsMin60);
            sink.SetUnavailable(MetricId.FpsMax60);
            sink.SetUnavailable(MetricId.FpsAvg60);
            sink.SetUnavailable(MetricId.FpsLow1Pct);
            return;
        }

        double current = BucketFps(target, now, 0);

        // 只有"瞬间值"没有统计值的场景（刚切到目标、不到 2 帧）也算不可用
        if (double.IsNaN(current))
        {
            sink.SetUnavailable(MetricId.FpsPresent);
            sink.SetUnavailable(MetricId.FpsFrameTimeMs);
            sink.SetUnavailable(MetricId.FpsMin60);
            sink.SetUnavailable(MetricId.FpsMax60);
            sink.SetUnavailable(MetricId.FpsAvg60);
            sink.SetUnavailable(MetricId.FpsLow1Pct);
            return;
        }

        ComputeWindow(target, now, out double min, out double max, out double avg);

        // 1% Low 每秒重算一次；换目标进程立刻重算（历史不同，缓存会串）。
        if (pid != _low1Pid || now - _low1Ts >= Low1IntervalMs || _low1Ts == long.MinValue)
        {
            _low1 = ComputeLow1(target, now);
            _low1Ts = now;
            _low1Pid = pid;
        }

        sink.Set(MetricId.FpsPresent, current);
        sink.Set(MetricId.FpsFrameTimeMs, 1000.0 / current);

        // min/max/avg/1%Low 在窗口还没攒够时是 NaN。IsUsable 只看 State、不看值是否有限，
        // 直接 Set 会让 UI 打出 "NaN"（刚启动那几秒就是这个样子），所以统一退成 `—`。
        SetFinite(sink, MetricId.FpsMin60, min);
        SetFinite(sink, MetricId.FpsMax60, max);
        SetFinite(sink, MetricId.FpsAvg60, avg);
        SetFinite(sink, MetricId.FpsLow1Pct, _low1);
    }

    /// <summary>有限值才写 Ok；NaN / ±Inf 一律标记为不可用。</summary>
    private static void SetFinite(MetricSnapshot sink, MetricId id, double v)
    {
        if (double.IsFinite(v)) sink.Set(id, v);
        else sink.SetUnavailable(id);
    }

    /// <summary>
    /// 选目标进程。返回 0 表示"没有任何进程在渲染"。
    /// 规则见类注释：前台优先，退到最活跃的呈现进程。
    /// </summary>
    private int PickTarget(long now, out string app)
    {
        app = "";

        // 排除自己：用户盯着监控面板看的时候，前台就是 CoPul，它是 WPF 界面、
        // 只在数值变动时 present（2–5 FPS），当成"游戏帧率"显示只会让人以为软件坏了。
        // 注意不能用 --exclude CoPul.exe 代替：用 dotnet CoPul.dll 启动时进程名是 dotnet.exe。
        int self = Environment.ProcessId;

        int fg = Win32.GetForegroundProcessId();
        if (fg != 0 && fg != self &&
            _tracks.TryGetValue(fg, out var fgTrack) && now - fgTrack.LastTs <= ActiveWindowMs)
        {
            app = fgTrack.App;
            return fg;
        }

        int bestPid = 0;
        int bestFrames = 0;
        foreach (var kv in _tracks)
        {
            if (kv.Key == self) continue;
            if (now - kv.Value.LastTs > ActiveWindowMs) continue;

            int frames = kv.Value.CountSince(now - InstantWindowMs);
            if (frames > bestFrames)
            {
                bestFrames = frames;
                bestPid = kv.Key;
            }
        }

        if (bestPid != 0) app = _tracks[bestPid].App;
        return bestPid;
    }

    /// <summary>丢掉长时间没有帧的进程。ETW 里进程退出不会有回调，不清理会慢慢攒满。</summary>
    private void Forget(long now)
    {
        if (_tracks.Count == 0) return;

        List<int>? dead = null;
        foreach (var kv in _tracks)
        {
            if (now - kv.Value.LastTs <= ForgetAfterMs) continue;
            (dead ??= new List<int>()).Add(kv.Key);
        }

        if (dead is null) return;
        foreach (int pid in dead) _tracks.Remove(pid);
    }

    /// <summary>
    /// 第 <paramref name="bucket"/> 个 1 秒桶的帧率（0 = 最近一秒）。
    /// 桶内帧数不足 2 时返回 NaN —— 1 帧算不出间隔。
    /// </summary>
    private static double BucketFps(Track t, long now, int bucket)
    {
        long hi = now - bucket * 1000L;          // 桶的上界（含）
        long lo = hi - 1000;                      // 桶的下界（不含）

        int n = 0;
        long firstTs = 0, lastTs = 0;
        for (int i = 0; i < t.Count; i++)
        {
            t.FromNewest(i, out long ts, out _);
            if (ts > hi) continue;                // 比桶新（理论上 bucket>0 时才出现）
            if (ts <= lo) break;                  // 比桶旧，后面的更旧

            if (n == 0) lastTs = ts;              // 从新往旧走：第一个是桶内最新的
            firstTs = ts;
            n++;
        }

        if (n < 2) return double.NaN;
        long span = lastTs - firstTs;
        return span <= 0 ? double.NaN : (n - 1) * 1000.0 / span;
    }

    /// <summary>60 秒逐秒帧率的最低 / 最高 / 平均。有效桶少于 3 个就认为窗口还不够长。</summary>
    private static void ComputeWindow(Track t, long now, out double min, out double max, out double avg)
    {
        min = double.NaN; max = double.NaN; avg = double.NaN;

        double sum = 0;
        int valid = 0;

        for (int b = 0; b < WindowMs / 1000; b++)
        {
            double fps = BucketFps(t, now, b);
            if (double.IsNaN(fps)) continue;

            if (valid == 0) { min = fps; max = fps; }
            else
            {
                if (fps < min) min = fps;
                if (fps > max) max = fps;
            }
            sum += fps;
            valid++;
        }

        if (valid >= 3) avg = sum / valid;
        else { min = double.NaN; max = double.NaN; }
    }

    /// <summary>
    /// 1% Low：把窗口内所有帧时间降序排列，取最慢 1% 求平均，再取倒数。
    /// 样本不足 100 帧（统计上凑不出 1 帧的 1%）时返回 NaN。
    /// </summary>
    private double ComputeLow1(Track t, long now)
    {
        int n = 0;
        for (int i = 0; i < t.Count && n < _scratch.Length; i++)
        {
            t.FromNewest(i, out long ts, out float ms);
            if (now - ts > WindowMs) break;
            if (ms <= 0) continue;
            _scratch[n++] = ms;
        }

        if (n < 100) return double.NaN;

        int take = Math.Max(1, n / 100);
        Array.Sort(_scratch, 0, n);            // 升序；最慢的 take 个在末尾

        double sum = 0;
        for (int i = n - take; i < n; i++) sum += _scratch[i];

        double avgMs = sum / take;
        return avgMs <= 0 ? double.NaN : 1000.0 / avgMs;
    }

    private static long NowMs() => (long)(Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency);

    private static double Elapsed(long t0)
        => (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;

    public void Dispose() => StopSession();

    /// <summary>
    /// 单个进程的帧时间环形缓冲。容量 16384：本机 240 FPS 满 68 秒，比统计窗口还长，
    /// 正常不会回卷；回卷时覆盖最旧的，正是我们要的效果。
    /// </summary>
    private sealed class Track
    {
        public const int Capacity = 16_384;

        private readonly long[] _ts = new long[Capacity];
        private readonly float[] _ms = new float[Capacity];
        private int _head;

        public string App = "";

        /// <summary>最近一帧的时间戳（ms）。目标选择与过期清理都靠它。</summary>
        public long LastTs;

        public int Count { get; private set; }

        public void Add(long ts, double ms)
        {
            _ts[_head] = ts;
            _ms[_head] = (float)ms;
            _head = _head + 1 == Capacity ? 0 : _head + 1;
            if (Count < Capacity) Count++;
            LastTs = ts;
        }

        /// <summary>0 = 最新一帧。</summary>
        public bool FromNewest(int i, out long ts, out float ms)
        {
            if (i < 0 || i >= Count)
            {
                ts = 0;
                ms = 0;
                return false;
            }

            int idx = _head - 1 - i;
            if (idx < 0) idx += Capacity;
            ts = _ts[idx];
            ms = _ms[idx];
            return true;
        }

        /// <summary>比 <paramref name="sinceTs"/> 新的帧有多少个。活跃度比较用，一次最多扫几百条。</summary>
        public int CountSince(long sinceTs)
        {
            int n = 0;
            for (int i = 0; i < Count; i++)
            {
                FromNewest(i, out long ts, out _);
                if (ts <= sinceTs) break;
                n++;
            }
            return n;
        }
    }
}
