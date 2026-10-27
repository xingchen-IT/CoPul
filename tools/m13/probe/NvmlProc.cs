using System.Runtime.InteropServices;

namespace CoPul.M13;

/// <summary>NVML 每进程接口的探测封装。只问两件事：能不能拿到利用率，能不能拿到显存。</summary>
public static class NvmlProc
{
    private const string Dll = "nvml.dll";

    [StructLayout(LayoutKind.Sequential)]
    public struct UtilSample
    {
        public uint Pid;
        public ulong TimeStamp;
        public uint SmUtil;
        public uint MemUtil;
        public uint EncUtil;
        public uint DecUtil;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct ProcessInfo
    {
        public uint Pid;
        public ulong UsedGpuMemory;
        public uint GpuInstanceId;
        public uint ComputeInstanceId;
    }

    [DllImport(Dll, EntryPoint = "nvmlInit_v2")]
    public static extern int Init();

    [DllImport(Dll, EntryPoint = "nvmlShutdown")]
    public static extern int Shutdown();

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    public static extern int GetHandleByIndex(uint index, out IntPtr device);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetProcessUtilization")]
    public static extern int GetProcessUtilization(IntPtr device, [Out] UtilSample[] samples,
        ref uint count, ulong lastSeenTimeStamp);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetComputeRunningProcesses")]
    public static extern int GetComputeProcesses(IntPtr device, ref uint count, [Out] ProcessInfo[] infos);

    [DllImport(Dll, EntryPoint = "nvmlDeviceGetGraphicsRunningProcesses")]
    public static extern int GetGraphicsProcesses(IntPtr device, ref uint count, [Out] ProcessInfo[] infos);
}
