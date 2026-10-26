using System.Diagnostics;
using CoPul.Collector.Interop;
using CoPul.Collector.Metrics;
using LibreHardwareMonitor.PawnIo;

namespace CoPul.Collector.Providers;

/// <summary>
/// CPU 温度 / 功耗 / 频率，全部走 MSR 直读。
///
/// 为什么不走 LHM 的 Cpu.Update()：M0 实测提权后单次 218.10 ms —— 它一次刷新该硬件的
/// 全部 51 个传感器，而我们要的只是其中 3 个值。M0.5 进一步证明这 218 ms 不在驱动层：
/// 同一条 PawnIO 驱动连读 51 条 MSR 只要 0.10 ms，差 2181 倍，瓶颈在 LHM 自己的托管层。
///
/// 驱动来源（方案 A，2026-10-27 定案）：不随包分发 PawnIO，只在启动时检测
/// <see cref="PawnIo.IsInstalled"/>，缺失则引导用户自行安装。
/// </summary>
public sealed class MsrCpuProvider : IMetricProvider
{
    private const uint MSR_IA32_THERM_STATUS = 0x19C;
    private const uint MSR_TEMPERATURE_TARGET = 0x1A2;
    private const uint MSR_PACKAGE_THERM_STATUS = 0x1B1;
    private const uint MSR_RAPL_POWER_UNIT = 0x606;
    private const uint MSR_PKG_ENERGY_STATUS = 0x611;
    private const uint MSR_IA32_PERF_STATUS = 0x198;

    /// <summary>PawnIO 官方发布页。方案 A 的引导目标，填进设置页「传感器诊断」。</summary>
    public const string PawnIoDownloadUrl = "https://pawnio.eu/";

    private IntelMsr? _msr;
    private int _tjMax;
    private double _joulePerCount = 1.0 / (1 << 14);

    private ulong _prevEnergy;
    private long _prevEnergyTs;
    private bool _hasPrevEnergy;

    private readonly List<SampleGroup> _groups;

    public MsrCpuProvider()
    {
        _groups = new List<SampleGroup>
        {
            new("Msr.Package", Tier.T1, SamplePackage),
            new("Msr.AllCores", Tier.T1b, SampleAllCores)
        };
    }

    public string Name => "MSR / PawnIO";
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;
    public string? UnavailableReason { get; private set; }
    public string? Remedy { get; private set; }
    public double InitializeMs { get; private set; }
    public IReadOnlyList<SampleGroup> Groups => _groups;

    /// <summary>驱动与权限都已就绪。UI 可据此决定是否显示温度区块。</summary>
    public bool IsUsable => Status == ProviderStatus.Ready;

    /// <summary>TjMax（°C），从 MSR 0x1A2 读出。0 表示未初始化成功。</summary>
    public int TjMax => _tjMax;

    public bool Initialize()
    {
        if (Status != ProviderStatus.NotInitialized) return Status == ProviderStatus.Ready;

        var sw = Stopwatch.StartNew();
        try
        {
            // 第一步：驱动是否安装。方案 A 的关键分支 —— 不静默失败，也不试图自带安装。
            bool installed;
            try
            {
                installed = PawnIo.IsInstalled;
            }
            catch
            {
                installed = false;
            }

            if (!installed)
            {
                Status = ProviderStatus.Unsupported;
                UnavailableReason = "未检测到 PawnIO 驱动，CPU 温度 / 功耗 / 频率不可用。";
                Remedy = $"请安装 PawnIO 后重启 CoPul：{PawnIoDownloadUrl}";
                return false;
            }

            _msr = new IntelMsr();

            // 第二步：读 TjMax 做合理性校验（A-13）。
            // 陷阱：驱动未真正生效时 ReadMsr 返回 true，但值恒为 0，只信返回值会显示 0°C。
            if (!TryRead(MSR_TEMPERATURE_TARGET, out ulong target) || Eax(target) == 0)
            {
                Status = ProviderStatus.Failed;
                UnavailableReason = Win32.IsAdministrator()
                    ? "PawnIO 已安装但 TjMax 读数为空，驱动可能未加载或被安全软件拦截。"
                    : "读取 CPU 温度需要管理员权限（PawnIO 驱动句柄被拒）。";
                Remedy = Win32.IsAdministrator() ? "检查安全软件是否拦截 PawnIO 驱动。" : "以管理员身份重新启动 CoPul。";
                return false;
            }

            _tjMax = (int)((Eax(target) >> 16) & 0xFF);
            if (_tjMax is < 80 or > 120)
            {
                Status = ProviderStatus.Failed;
                UnavailableReason = $"TjMax={_tjMax}°C 不在合理区间 [80,120]，MSR 读数可能被虚拟化层拦截。";
                Remedy = "若运行在虚拟机内，CPU 温度不可用。";
                return false;
            }

            // RAPL 能量单位：0x606 bits[12:8] = ESU，1 count = 1/2^ESU 焦耳
            if (TryRead(MSR_RAPL_POWER_UNIT, out ulong unit))
            {
                int esu = (int)((Eax(unit) >> 8) & 0x1F);
                _joulePerCount = 1.0 / (1 << esu);
            }

            Status = ProviderStatus.Ready;
            return true;
        }
        catch (Exception ex)
        {
            Status = ProviderStatus.Failed;
            UnavailableReason = $"MSR 初始化失败：{ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}";
            Remedy = "确认以管理员身份运行，并检查 PawnIO 服务状态。";
            return false;
        }
        finally
        {
            sw.Stop();
            InitializeMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>T1：封装温度 + RAPL 功耗 + 当前倍频。实测每轮约 0.3 ms。</summary>
    private void SamplePackage(MetricSnapshot sink)
    {
        if (_msr == null) return;
        long now = Stopwatch.GetTimestamp();

        // 封装温度：0x1B1 bits[22:16] 是距 TjMax 的读数，bit31 为有效位
        if (TryRead(MSR_PACKAGE_THERM_STATUS, out ulong pkg))
        {
            uint eax = Eax(pkg);
            bool valid = (eax & 0x8000_0000u) != 0;
            int readout = (int)((eax >> 16) & 0x7F);
            if (valid && readout != 0)
                sink.Set(MetricId.CpuPackageTemp, _tjMax - readout);
            else
                sink.Set(MetricId.CpuPackageTemp, double.NaN, MetricState.Invalid, 0, "封装温度 readout=0");
        }
        else
        {
            sink.Set(MetricId.CpuPackageTemp, double.NaN, MetricState.Unavailable);
        }

        // RAPL 功耗：能量计数差分，需要上一轮的值
        if (TryRead(MSR_PKG_ENERGY_STATUS, out ulong energy))
        {
            uint eax = Eax(energy);
            if (_hasPrevEnergy)
            {
                ulong delta = eax >= (uint)_prevEnergy ? eax - (uint)_prevEnergy
                                                       : (0x1_0000_0000UL - (uint)_prevEnergy + eax);
                double seconds = (now - _prevEnergyTs) / (double)Stopwatch.Frequency;
                if (seconds > 0.01)
                {
                    double watts = delta * _joulePerCount / seconds;
                    if (watts is > 0.5 and < 400)
                        sink.Set(MetricId.CpuPackagePowerW, watts);
                    else
                        sink.Set(MetricId.CpuPackagePowerW, double.NaN, MetricState.Invalid, 0, $"RAPL={watts:F1}W 越界");
                }
            }
            _prevEnergy = eax;
            _prevEnergyTs = now;
            _hasPrevEnergy = true;
        }

        // 频率：0x198 bits[15:8] = 倍频，× 100MHz。
        // 陷阱：高 32 位才是 EAX；且 bits[15:0] 是 ratio<<8，直接用会得到 1126GHz。
        if (TryRead(MSR_IA32_PERF_STATUS, out ulong perf))
        {
            uint eax = Eax(perf);
            int ratio = (int)((eax >> 8) & 0xFF);
            if (ratio is > 4 and < 100)
                sink.Set(MetricId.CpuClockGhz, ratio * 0.1);
            else
                sink.Set(MetricId.CpuClockGhz, double.NaN, MetricState.Invalid, 0, $"倍频 {ratio} 越界");
        }
    }

    /// <summary>
    /// T1b：逐核温度。M0.5 实测全核一轮 1.7–2.1 ms，其中 98% 是 SetThreadAffinityMask
    /// 的切核开销（85–101 µs/核，是读 MSR 的 40 倍），所以固定 500ms。
    /// </summary>
    private void SampleAllCores(MetricSnapshot sink)
    {
        if (_msr == null) return;
        int cores = Math.Min(Environment.ProcessorCount, 64);
        var temps = new double[cores];
        int valid = 0;

        IntPtr thread = Win32.GetCurrentThread();
        for (int i = 0; i < cores; i++)
        {
            IntPtr prev = Win32.SetThreadAffinityMask(thread, (IntPtr)(1L << i));
            if (prev == IntPtr.Zero) { temps[i] = double.NaN; continue; }

            if (TryRead(MSR_IA32_THERM_STATUS, out ulong v))
            {
                uint eax = Eax(v);
                bool ok = (eax & 0x8000_0000u) != 0;
                int readout = (int)((eax >> 16) & 0x7F);
                if (ok && readout != 0) { temps[i] = _tjMax - readout; valid++; }
                else temps[i] = double.NaN;
            }
            else temps[i] = double.NaN;

            Win32.SetThreadAffinityMask(thread, prev);
        }

        sink.SetSeries(MetricId.CpuCoreTemp, temps,
            valid > 0 ? MetricState.Ok : MetricState.Invalid);
    }

    /// <summary>
    /// IntelMsr.ReadMsr 把结果塞进 ulong：[31:0] 是 EAX，[63:32] 是 EDX。
    /// 取错半边是这块最容易犯的错 —— 直接用高 32 位会把 RAPL 能量读成 0、把 TjMax 读成 0。
    /// </summary>
    private static uint Eax(ulong v) => (uint)(v & 0xFFFF_FFFF);

    private bool TryRead(uint msr, out ulong value)
    {
        value = 0;
        return _msr != null && _msr.ReadMsr(msr, out value);
    }

    public void Dispose()
    {
        try { _msr?.Close(); } catch { /* 关闭失败不影响进程退出 */ }
        _msr = null;
        Status = ProviderStatus.NotInitialized;
    }
}
