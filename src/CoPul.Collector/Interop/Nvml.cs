using System.Runtime.InteropServices;
using System.Text;

namespace CoPul.Collector.Interop;

/// <summary>
/// NVML P/Invoke。M0 实测：四项全读 0.004 ms，而 LHM 的 NvidiaGpu.Update() 76.93 ms。
/// 差 19000 倍的原因是 LHM 给每个传感器单独发一次 NVAPI 调用，这里一次拿全。
/// </summary>
internal static class Nvml
{
    private const string Dll = "nvml.dll";

    public const int NVML_SUCCESS = 0;
    public const uint NVML_TEMPERATURE_GPU = 0;

    [StructLayout(LayoutKind.Sequential)]
    public struct Utilization { public uint Gpu; public uint Memory; }

    [StructLayout(LayoutKind.Sequential)]
    public struct MemoryInfo { public ulong Total; public ulong Free; public ulong Used; }

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")]
    public static extern int Init();

    [DllImport(Dll, EntryPoint = "nvmlShutdown")]
    public static extern int Shutdown();

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetCount_v2")]
    public static extern int GetDeviceCount(out uint count);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static extern int GetHandleByIndex(uint index, out IntPtr device);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetName")]
    public static extern int GetName(IntPtr device, StringBuilder name, uint length);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetUtilizationRates")]
    public static extern int GetUtilization(IntPtr device, out Utilization utilization);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetTemperature")]
    public static extern int GetTemperature(IntPtr device, uint sensorType, out uint temp);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetMemoryInfo")]
    public static extern int GetMemoryInfo(IntPtr device, out MemoryInfo memory);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerUsage")]
    public static extern int GetPowerUsage(IntPtr device, out uint milliwatts);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetClockInfo")]
    public static extern int GetClockInfo(IntPtr device, uint clockType, out uint clockMhz);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetFanSpeed")]
    public static extern int GetFanSpeed(IntPtr device, out uint percent);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetEncoderUtilization")]
    public static extern int GetEncoderUtilization(IntPtr device, out uint utilization, out uint samplingPeriodUs);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetDecoderUtilization")]
    public static extern int GetDecoderUtilization(IntPtr device, out uint utilization, out uint samplingPeriodUs);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetPowerManagementLimit")]
    public static extern int GetPowerLimit(IntPtr device, out uint milliwatts);

    public const uint NVML_CLOCK_GRAPHICS = 0;
    public const uint NVML_CLOCK_SM = 1;
    public const uint NVML_CLOCK_MEM = 2;
}
