using System.Diagnostics;
using CoPul.Collector.Interop;
using CoPul.Collector.Metrics;

namespace CoPul.Collector.Providers;

/// <summary>
/// 进程级占用采集：每个进程的 CPU / GPU / 内存。
///
/// 三条数据源都是实测选出来的（详见 tools/m13 探测记录）：
///
/// - **CPU 与内存**：NtQuerySystemInformation。一次调用拿全量，本机 460 进程中位 10.5ms。
///   CPU 占用率要自己算差分：ΔCPU时间 / Δ墙钟 / 逻辑核数 —— **必须除以核数**，
///   否则单线程跑满会显示 100%，而任务管理器显示 5%（20 核）。本机 _total 实测 1993.93
///   ≈ 20 核 × 100，正好印证 PDH 给的也是「每核 100」口径。
///
/// - **GPU**：只能用 Windows 的 <c>\GPU Engine(*)\Utilization Percentage</c>。
///   NVML 在这台机器上两条路都不通：<c>nvmlDeviceGetProcessUtilization</c> 返回
///   rc=7（NOT_SUPPORTED），<c>nvmlDeviceGetComputeRunningProcesses</c> 虽然成功
///   但 usedGpuMemory 全是 0xFFFFFFFFFFFFFFFF（值不可用，nvidia-smi 同样显示 [N/A]）。
///   这是 WDDM 显卡的固有限制，不是权限问题。
///
/// 两个降本措施：
/// - 实例按引擎类型裁剪（3D / Video* / Compute），跳过 Copy / Overlay / Security / VR：
///   1045 个实例一轮 25ms → 456 个实例 9.6ms；
/// - 用部分通配符 <c>*_engtype_3D</c> 展开，只花 9ms，而全量展开要 0.5–1s。
/// </summary>
public sealed class ProcessProvider : IMetricProvider
{
    /// <summary>进程表显示几行。多了没意义：下面的进程占比都在 1% 以下。</summary>
    private const int TopCount = 8;

    private static readonly string[] GpuEngineTypes =
    {
        "engtype_3D", "engtype_VideoDecode", "engtype_VideoEncode",
        "engtype_VideoProcessing", "engtype_Compute"
    };

    private readonly List<ProcessEntry> _scratch = new(512);
    private Dictionary<int, long> _prevTicks = new();
    private long _prevTs;

    private PdhQuery? _gpuQuery;
    private List<KeyValuePair<int, PdhCounter>> _gpuCounters = new();
    private Dictionary<int, double> _gpuMap = new();
    private ProcessStat[] _top = Array.Empty<ProcessStat>();

    private volatile bool _enabled = true;

    public string Name => "进程";

    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;

    public string? UnavailableReason { get; private set; }

    public string? Remedy { get; }

    public double InitializeMs { get; private set; }

    /// <summary>
    /// 折叠开关。进程区块被折叠时整组跳过（与折叠区块跳过 <c>ApplySnapshot</c> 同一思路）：
    /// 这两组一轮合计约 20ms，是全部采集里最贵的一组，看不见就别采。
    /// </summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    /// <summary>
    /// 当前占用最高的若干个进程。引用赋值，读到的可能是上一轮的数组 —— 最多晚 5 秒，
    /// 比加锁便宜。UI 每帧读一次即可。
    /// </summary>
    public IReadOnlyList<ProcessStat> Top => _top;

    /// <summary>每进程 GPU 是否可用（初始化时才能确定）。不可用时整列显示 `—`。</summary>
    public bool GpuAvailable => _gpuCounters.Count > 0;

    public IReadOnlyList<SampleGroup> Groups { get; }

    public ProcessProvider()
    {
        Groups = new[]
        {
            new SampleGroup("Proc.List", Tier.T2b, SampleProcesses),
            new SampleGroup("Proc.Gpu", Tier.T2b, SampleGpu)
        };
    }

    public bool Initialize()
    {
        var sw = Stopwatch.StartNew();

        if (!SystemProcessInfo.IsSupported)
        {
            Status = ProviderStatus.Unsupported;
            UnavailableReason = "进程快照接口仅支持 64 位进程";
            InitializeMs = sw.Elapsed.TotalMilliseconds;
            return false;
        }

        if (!SystemProcessInfo.TrySnapshot(_scratch))
        {
            Status = ProviderStatus.Failed;
            UnavailableReason = "取进程快照失败（NtQuerySystemInformation 返回错误）";
            InitializeMs = sw.Elapsed.TotalMilliseconds;
            return false;
        }

        BuildGpuQuery();
        Status = ProviderStatus.Ready;
        InitializeMs = sw.Elapsed.TotalMilliseconds;
        return true;
    }

    /// <summary>
    /// 建 GPU Engine 计数器。失败不影响 CPU / 内存两列 —— 只是 GPU 列显示 `—`。
    /// 展开用部分通配符：全量展开 1045 个实例要 0.5–1s，按类型展开只要 9ms。
    /// </summary>
    private void BuildGpuQuery()
    {
        try
        {
            var query = new PdhQuery();
            query.Open();

            var pairs = new List<KeyValuePair<int, PdhCounter>>();
            foreach (string type in GpuEngineTypes)
            {
                string wildcard = $@"\GPU Engine(*_{type})\Utilization Percentage";
                foreach (string path in PdhQuery.ExpandWildcard(wildcard))
                {
                    int pid = PidFromInstance(path);
                    if (pid < 0) continue;

                    var counter = query.TryAdd(path);
                    if (counter is not null) pairs.Add(new KeyValuePair<int, PdhCounter>(pid, counter));
                }
            }

            if (pairs.Count == 0)
            {
                query.Dispose();
                return;
            }

            _gpuQuery = query;
            _gpuCounters = pairs;
        }
        catch (Exception ex)
        {
            // PDH 建不起来不连累整表：GPU 列显示 `—`
            UnavailableReason = $"每进程 GPU 占用不可用（{ex.GetBaseException().Message}）";
            _gpuQuery = null;
            _gpuCounters = new List<KeyValuePair<int, PdhCounter>>();
        }
    }

    /// <summary>从 <c>\GPU Engine(pid_7764_luid_..._engtype_3D)\Utilization Percentage</c> 里取 pid。</summary>
    private static int PidFromInstance(string path)
    {
        int open = path.LastIndexOf('(');
        int close = path.LastIndexOf(')');
        if (open < 0 || close <= open) return -1;

        string inst = path[(open + 1)..close];
        if (!inst.StartsWith("pid_", StringComparison.Ordinal)) return -1;

        int end = inst.IndexOf('_', 4);
        if (end <= 4) return -1;

        return int.TryParse(inst.AsSpan(4, end - 4), out int pid) ? pid : -1;
    }

    private void SampleProcesses(MetricSnapshot sink)
    {
        // sink 用不到：进程行是复合结构，走 Top 属性旁路（见 ProcessStat 的说明）
        if (!_enabled) return;
        if (!SystemProcessInfo.TrySnapshot(_scratch)) return;

        long now = Stopwatch.GetTimestamp();
        double wallMs = _prevTs == 0
            ? 0
            : (now - _prevTs) * 1000.0 / Stopwatch.Frequency;

        double cores = Environment.ProcessorCount;
        var cur = new Dictionary<int, long>(_scratch.Count);
        var rows = new List<ProcessStat>(_scratch.Count);

        foreach (var e in _scratch)
        {
            cur[e.Pid] = e.CpuTicks;

            // PID 0 是 System Idle Process，它的"占用"是空闲量，显示出来只会误导
            if (e.Pid == 0) continue;

            double cpu = double.NaN;
            if (wallMs > 0 && _prevTicks.TryGetValue(e.Pid, out long prev))
            {
                double delta = e.CpuTicks - prev;
                // 100ns → ms 是 /10000；再除以核数才是任务管理器口径
                if (delta >= 0) cpu = delta / 10_000.0 / wallMs / cores * 100.0;
            }

            double gpu = _gpuMap.TryGetValue(e.Pid, out double g) ? g : double.NaN;
            rows.Add(new ProcessStat(e.Pid, ShortName(e.Name), cpu, gpu,
                                     e.PrivateWorkingSet / 1_048_576.0));
        }

        _prevTicks = cur;
        _prevTs = now;

        // 按 CPU 与 GPU 中的较大者排序：光按 CPU 排的话，
        // 正在硬解码 / 跑游戏但主逻辑很闲的进程（GPU 高、CPU 低）永远排不上来。
        rows.Sort(static (a, b) => Score(b).CompareTo(Score(a)));
        _top = rows.Count <= TopCount ? rows.ToArray() : rows.Take(TopCount).ToArray();
    }

    private static double Score(ProcessStat s)
    {
        double cpu = double.IsNaN(s.CpuPct) ? 0 : s.CpuPct;
        double gpu = double.IsNaN(s.GpuPct) ? 0 : s.GpuPct;
        return Math.Max(cpu, gpu);
    }

    private void SampleGpu(MetricSnapshot sink)
    {
        var query = _gpuQuery;
        if (!_enabled || query is null || _gpuCounters.Count == 0) return;

        uint status = query.Collect();
        var acc = new Dictionary<int, double>();

        foreach (var kv in _gpuCounters)
        {
            double? v = PdhQuery.TryGetDouble(kv.Value, status);
            if (v is null || v.Value <= 0) continue;

            // 同一进程可能同时占着 3D 与解码引擎，按引擎求和（与任务管理器一致）
            acc[kv.Key] = acc.GetValueOrDefault(kv.Key) + v.Value;
        }

        foreach (int pid in acc.Keys.ToList())
        {
            // 多引擎并行时求和会超过 100%，而"占用率"不可能超过 100%
            if (acc[pid] > 100) acc[pid] = 100;
        }

        _gpuMap = acc;
    }

    private static string ShortName(string name)
    {
        if (name.Length == 0) return "";

        int i = name.LastIndexOf('\\');
        return i >= 0 ? name[(i + 1)..] : name;
    }

    public void Dispose()
    {
        _gpuQuery?.Dispose();
        _gpuQuery = null;
        _gpuCounters = new List<KeyValuePair<int, PdhCounter>>();
    }
}
