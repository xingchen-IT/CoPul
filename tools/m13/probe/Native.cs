using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CoPul.M13;

/// <summary>快照里的一个进程。CPU 由两次快照的 CPU 时间差分算出，与任务管理器同一口径。</summary>
public sealed class ProcEntry
{
    public int Pid;
    public string Name = "";
    public long CpuTicks;      // UserTime + KernelTime（100ns）
    public long PrivateWs;     // WorkingSetPrivateSize（任务管理器「内存」列）
    public long WorkingSet;    // WorkingSetSize
}

/// <summary>
/// NtQuerySystemInformation(SystemProcessInformation) 的封装。
///
/// 为什么不用 Process.GetProcesses()：后者对每个进程都要 OpenProcess + GetProcessTimes
/// + GetProcessMemoryInfo，460 个进程就是上千次系统调用。而这里一次调用就把
/// 进程名 / CPU 时间 / 私有工作集全部拿回来 —— 任务管理器走的就是这条路径。
/// </summary>
public static unsafe class Native
{
    private const int SystemProcessInformation = 5;

    // x64 的 SYSTEM_PROCESS_INFORMATION 字段偏移（UNICODE_STRING 在 x64 上 16 字节）
    private const int OffNextEntry = 0;
    private const int OffWorkingSetPrivate = 8;
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffNameLength = 56;
    private const int OffNameBuffer = 64;
    private const int OffUniqueProcessId = 80;
    private const int OffWorkingSetSize = 144;
    private const int OffPrivatePageCount = 200;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer,
        int bufferSize, out int returnLength);

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, byte* buffer,
        int bufferSize, out int returnLength);

    /// <summary>只做系统调用，不碰缓冲区 —— 用来把「内核遍历成本」和「托管侧解析成本」分开。</summary>
    public static double SyscallOnly()
    {
        EnsureBuffer();
        long t0 = Stopwatch.GetTimestamp();
        int rc = NtQuerySystemInformation(SystemProcessInformation, _buf, _bufSize, out _);
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        return rc == 0 ? ms : double.NaN;
    }

    /// <summary>只走 NextEntryOffset 链表，不建对象、不读名字。</summary>
    public static double WalkOnly()
    {
        EnsureBuffer();
        long t0 = Stopwatch.GetTimestamp();
        byte* p = _buf;
        int n = 0;
        while (true)
        {
            n++;
            int next = Marshal.ReadInt32((IntPtr)(p + OffNextEntry));
            if (next == 0) break;
            p += next;
        }
        double ms = (Stopwatch.GetTimestamp() - t0) * 1000.0 / Stopwatch.Frequency;
        return n > 0 ? ms : double.NaN;
    }

    private static byte* _buf;
    private static int _bufSize = 1024 * 1024;

    private static void EnsureBuffer()
    {
        if (_buf == null) _buf = (byte*)Marshal.AllocHGlobal(_bufSize);
    }

    /// <summary>取一次全进程快照。重复调用直到缓冲区够大（进程数会变）。</summary>
    public static List<ProcEntry> Snapshot()
    {
        int size = 256 * 1024;
        byte* buf = null;
        try
        {
            for (int attempt = 0; attempt < 6; attempt++)
            {
                buf = (byte*)Marshal.AllocHGlobal(size);
                int rc = NtQuerySystemInformation(SystemProcessInformation, buf, size, out int need);
                if (rc == 0)
                    return Parse(buf);

                Marshal.FreeHGlobal((IntPtr)buf);
                buf = null;
                if (need <= 0) size *= 2; else size = need + 64 * 1024;
            }
            return new List<ProcEntry>();
        }
        finally
        {
            if (buf != null) Marshal.FreeHGlobal((IntPtr)buf);
        }
    }

    private static List<ProcEntry> Parse(byte* basePtr)
    {
        var list = new List<ProcEntry>();
        byte* p = basePtr;

        while (true)
        {
            int pid = (int)Marshal.ReadInt64((IntPtr)(p + OffUniqueProcessId));
            long user = Marshal.ReadInt64((IntPtr)(p + OffUserTime));
            long kernel = Marshal.ReadInt64((IntPtr)(p + OffKernelTime));
            long privWs = Marshal.ReadInt64((IntPtr)(p + OffWorkingSetPrivate));
            long ws = Marshal.ReadInt64((IntPtr)(p + OffWorkingSetSize));

            ushort nameLen = (ushort)Marshal.ReadInt16((IntPtr)(p + OffNameLength));
            IntPtr namePtr = Marshal.ReadIntPtr((IntPtr)(p + OffNameBuffer));
            string name = nameLen > 0 && namePtr != IntPtr.Zero
                ? Marshal.PtrToStringUni(namePtr, nameLen / 2) ?? ""
                : "";

            list.Add(new ProcEntry
            {
                Pid = pid,
                Name = name,
                CpuTicks = user + kernel,
                PrivateWs = privWs,
                WorkingSet = ws
            });

            int next = Marshal.ReadInt32((IntPtr)(p + OffNextEntry));
            if (next == 0) break;
            p += next;
        }

        return list;
    }
}
