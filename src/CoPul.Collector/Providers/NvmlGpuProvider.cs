using System.Diagnostics;
using System.Text;
using CoPul.Collector.Interop;
using CoPul.Collector.Metrics;

namespace CoPul.Collector.Providers;

/// <summary>
/// NVIDIA GPU 采集，直连 nvml.dll。
///
/// M0 实测：LHM 的 NvidiaGpu.Update() 76.93 ms，NVML 四项全读 0.004 ms，差约 19000 倍。
/// 原因不是硬件慢，而是 LHM 给每个传感器单独发一次 NVAPI 调用（33 个传感器 = 33 次往返）。
///
/// 边界（文档 A-08 例外）：AMD / Intel 核显没有 NVML。M0 实测本机 Intel UHD 的
/// Temperature / Power / Load 传感器数量均为 0，也没有更好的替代 API，因此 v1
/// 对非 NVIDIA 只保证 G01（型号），其余显示 `—`。
/// </summary>
public sealed class NvmlGpuProvider : IMetricProvider
{
    private IntPtr _device = IntPtr.Zero;
    private bool _initialized;
    private readonly List<SampleGroup> _groups;
    private uint _powerLimitMw;

    public NvmlGpuProvider()
    {
        _groups = new List<SampleGroup>
        {
            new("Nvml.Load", Tier.T0, SampleLoad),
            new("Nvml.Thermal", Tier.T1, SampleThermal),
            new("Nvml.Extra", Tier.T2, SampleExtra)
        };
    }

    public string Name => "NVML";
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;
    public string? UnavailableReason { get; private set; }
    public string? Remedy { get; private set; }
    public double InitializeMs { get; private set; }
    public IReadOnlyList<SampleGroup> Groups => _groups;

    public string DeviceName { get; private set; } = "";

    public bool Initialize()
    {
        if (Status != ProviderStatus.NotInitialized) return Status == ProviderStatus.Ready;

        var sw = Stopwatch.StartNew();
        try
        {
            int rc = Nvml.Init();
            if (rc != Nvml.NVML_SUCCESS)
            {
                Status = ProviderStatus.Failed;
                UnavailableReason = $"nvmlInit 失败，返回码 {rc}。";
                Remedy = "NVIDIA 驱动异常，尝试重装显卡驱动。";
                return false;
            }
            _initialized = true;

            if (Nvml.GetDeviceCount(out uint count) != Nvml.NVML_SUCCESS || count == 0)
            {
                Status = ProviderStatus.Unsupported;
                UnavailableReason = "未发现 NVIDIA GPU。";
                Remedy = "AMD / Intel 显卡的 GPU 指标 v1 不支持，仅显示型号。";
                return false;
            }

            if (Nvml.GetHandleByIndex(0, out _device) != Nvml.NVML_SUCCESS)
            {
                Status = ProviderStatus.Failed;
                UnavailableReason = "取 GPU 句柄失败。";
                return false;
            }

            var sb = new StringBuilder(96);
            if (Nvml.GetName(_device, sb, (uint)sb.Capacity) == Nvml.NVML_SUCCESS)
                DeviceName = sb.ToString();

            if (Nvml.GetPowerLimit(_device, out uint limit) == Nvml.NVML_SUCCESS)
                _powerLimitMw = limit;

            Status = ProviderStatus.Ready;
            return true;
        }
        catch (DllNotFoundException)
        {
            Status = ProviderStatus.Unsupported;
            UnavailableReason = "未找到 nvml.dll（未安装 NVIDIA 驱动）。";
            Remedy = "安装 NVIDIA 官方驱动后 GPU 指标才可用。";
            return false;
        }
        catch (Exception ex)
        {
            Status = ProviderStatus.Failed;
            UnavailableReason = $"NVML 初始化异常：{ex.GetBaseException().Message}";
            return false;
        }
        finally
        {
            sw.Stop();
            InitializeMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>T0：占用率 + 显存。两次 NVML 调用，实测合计 0.002 ms 量级。</summary>
    private void SampleLoad(MetricSnapshot sink)
    {
        if (Nvml.GetUtilization(_device, out var util) == Nvml.NVML_SUCCESS)
        {
            sink.Set(MetricId.GpuLoad, util.Gpu);
        }

        if (Nvml.GetMemoryInfo(_device, out var mem) == Nvml.NVML_SUCCESS && mem.Total > 0)
        {
            double usedMb = mem.Used / 1024.0 / 1024.0;
            double totalMb = mem.Total / 1024.0 / 1024.0;
            sink.Set(MetricId.GpuMemUsedMb, usedMb);
            sink.Set(MetricId.GpuMemTotalMb, totalMb);
            sink.Set(MetricId.GpuMemLoad, usedMb / totalMb * 100.0);
        }
    }

    /// <summary>T1：温度 + 功耗。与 CPU 温度同一视觉节奏，不放进 T0。</summary>
    private void SampleThermal(MetricSnapshot sink)
    {
        if (Nvml.GetTemperature(_device, Nvml.NVML_TEMPERATURE_GPU, out uint temp) == Nvml.NVML_SUCCESS)
            sink.Set(MetricId.GpuTemp, temp);

        if (Nvml.GetPowerUsage(_device, out uint mw) == Nvml.NVML_SUCCESS)
            sink.Set(MetricId.GpuPowerW, mw / 1000.0);
    }

    /// <summary>T2：频率、风扇、编解码引擎。都属于「看到就行」的慢变量。</summary>
    private void SampleExtra(MetricSnapshot sink)
    {
        if (Nvml.GetClockInfo(_device, Nvml.NVML_CLOCK_GRAPHICS, out uint coreMhz) == Nvml.NVML_SUCCESS)
            sink.Set(MetricId.GpuClockCoreMhz, coreMhz);

        if (Nvml.GetClockInfo(_device, Nvml.NVML_CLOCK_MEM, out uint memMhz) == Nvml.NVML_SUCCESS)
            sink.Set(MetricId.GpuClockMemMhz, memMhz);

        if (Nvml.GetFanSpeed(_device, out uint fan) == Nvml.NVML_SUCCESS)
            sink.Set(MetricId.GpuFanPct, fan);

        if (Nvml.GetEncoderUtilization(_device, out uint enc, out _) == Nvml.NVML_SUCCESS)
            sink.Set(MetricId.GpuEncoderLoad, enc);

        if (Nvml.GetDecoderUtilization(_device, out uint dec, out _) == Nvml.NVML_SUCCESS)
            sink.Set(MetricId.GpuDecoderLoad, dec);
    }

    public void Dispose()
    {
        if (_initialized)
        {
            try { Nvml.Shutdown(); } catch { }
            _initialized = false;
        }
        _device = IntPtr.Zero;
        Status = ProviderStatus.NotInitialized;
    }
}
