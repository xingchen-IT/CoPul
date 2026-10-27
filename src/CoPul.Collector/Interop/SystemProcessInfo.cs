using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CoPul.Collector.Interop;

/// <summary>快照里的一个进程。</summary>
public sealed class ProcessEntry
{
    public int Pid;
    public string Name = "";

    /// <summary>UserTime + KernelTime，单位 100ns。两次快照相减才是这段时间的 CPU 占用。</summary>
    public long CpuTicks;

    /// <summary>私有工作集字节数。任务管理器「内存」列用的就是它，不是提交大小。</summary>
    public long PrivateWorkingSet;
}

/// <summary>
/// NtQuerySystemInformation(SystemProcessInformation) 封装。
///
/// 选型依据（M1.4 实测，见 tools/m13）：
/// - <c>Process.GetProcesses()</c> + 逐进程读 TotalProcessorTime：本机 460 个进程要上百次
///   OpenProcess / GetProcessTimes，明显更贵；
/// - PDH <c>\Process(*)\% Processor Time</c>：469 个实例一轮 9.1ms，且实例名带 <c>chrome#1</c>
///   这种重名后缀、进程退出后实例会残留；
/// - 本接口一次调用拿回全部进程的 CPU 时间与私有工作集，本机实测中位 10.5ms，
///   其中 <b>几乎全部是系统调用本身</b>（走链表只要 0.05ms），所以解析侧没必要再优化。
///
/// 代价：结构体是未文档化的，偏移按 x64 写死。故只支持 64 位，且解析后做健全性检查，
/// 一旦 PID 或进程名明显不合理就整帧丢弃（宁可显示 `—`，也不能把错值当读数）。
/// </summary>
public static class SystemProcessInfo
{
    private const int SystemProcessInformation = 5;

    // x64 的 SYSTEM_PROCESS_INFORMATION 字段偏移
    private const int OffNextEntry = 0;
    private const int OffWorkingSetPrivate = 8;
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffNameLength = 56;
    private const int OffNameBuffer = 64;
    private const int OffUniqueProcessId = 80;

    public static bool IsSupported => IntPtr.Size == 8;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer,
        int bufferSize, out int returnLength);

    /// <summary>
    /// 取一次全进程快照。缓冲区按需要长度重试放大（进程数随时在变）。
    /// 返回 false 表示这一帧拿不到数据，调用方应保持上一次的结果。
    /// </summary>
    public static bool TrySnapshot(List<ProcessEntry> into)
    {
        if (!IsSupported) return false;

        into.Clear();
        int size = 512 * 1024;
        IntPtr buf = IntPtr.Zero;

        try
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                buf = Marshal.AllocHGlobal(size);
                int rc = NtQuerySystemInformation(SystemProcessInformation, buf, size, out int need);

                if (rc == 0)
                    return Parse(buf, into);

                Marshal.FreeHGlobal(buf);
                buf = IntPtr.Zero;
                size = need > 0 ? need + 128 * 1024 : size * 2;
            }

            return false;
        }
        finally
        {
            if (buf != IntPtr.Zero) Marshal.FreeHGlobal(buf);
        }
    }

    private static bool Parse(IntPtr basePtr, List<ProcessEntry> into)
    {
        IntPtr p = basePtr;

        // 上限保护：结构体偏移写错时会读出天文数字的 NextEntryOffset，
        // 没有这道保护就会读越界直接崩进程。
        for (int guard = 0; guard < 4096; guard++)
        {
            int pid = unchecked((int)Marshal.ReadInt64(p, OffUniqueProcessId));
            long user = Marshal.ReadInt64(p, OffUserTime);
            long kernel = Marshal.ReadInt64(p, OffKernelTime);
            long privWs = Marshal.ReadInt64(p, OffWorkingSetPrivate);

            int nameLen = Marshal.ReadInt16(p, OffNameLength);
            IntPtr namePtr = Marshal.ReadIntPtr(p, OffNameBuffer);
            string name = nameLen > 0 && namePtr != IntPtr.Zero && nameLen <= 32768
                ? Marshal.PtrToStringUni(namePtr, nameLen / 2) ?? ""
                : "";

            // 健全性检查：PID 必须非负且合理，CPU 时间不能为负，私有工作集不能是天文数字。
            // 任何一条不满足都说明偏移对不上，整帧丢弃。
            if (pid < 0 || pid > 4_000_000 || user < 0 || kernel < 0 || privWs < 0 || privWs > 1L << 45)
                return false;

            into.Add(new ProcessEntry
            {
                Pid = pid,
                Name = name,
                CpuTicks = user + kernel,
                PrivateWorkingSet = privWs
            });

            int next = Marshal.ReadInt32(p, OffNextEntry);
            if (next <= 0 || next > 1 << 20) break;
            p = IntPtr.Add(p, next);
        }

        return into.Count > 0;
    }
}
