using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CoPul.Collector.Interop;

internal static class Win32
{
    [StructLayout(LayoutKind.Sequential)]
    public struct MEMORYSTATUSEX
    {
        public uint dwLength;
        public uint dwMemoryLoad;
        public ulong ullTotalPhys;
        public ulong ullAvailPhys;
        public ulong ullTotalPageFile;
        public ulong ullAvailPageFile;
        public ulong ullTotalVirtual;
        public ulong ullAvailVirtual;
        public ulong ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);

    /// <summary>M0 实测 0.161 ms，可放心进 T0。</summary>
    public static bool TryGetMemory(out MEMORYSTATUSEX status)
    {
        status = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        return GlobalMemoryStatusEx(ref status);
    }

    [DllImport("kernel32.dll")]
    public static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);

    public const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    public const int PROP_STORAGE_DEVICE_PROTOCOL_SPECIFIC = 50;
    public const int QUERY_STANDARD = 0;
    public const int PROTOCOL_TYPE_NVME = 3;
    public const int NVME_DATA_TYPE_LOG_PAGE = 2;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    public static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    public const uint GENERIC_READ = 0x80000000;
    public const uint GENERIC_WRITE = 0x40000000;
    public const uint FILE_SHARE_READ = 1;
    public const uint FILE_SHARE_WRITE = 2;
    public const uint OPEN_EXISTING = 3;

    /// <summary>当前进程是否提权。M0 已确认 CPU 温度与 NVMe 温度都依赖它。</summary>
    public static bool IsAdministrator()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    // =================================================================
    // M1.0 补齐 UI 必需字段时新增的调用。全部实测过成本，见 docs/M1.0 报告。
    // =================================================================

    // ---------------- C01 CPU 型号 ----------------
    private const int HKEY_LOCAL_MACHINE = unchecked((int)0x80000002);
    private const uint RRF_RT_REG_SZ = 0x00000002;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
    private static extern int RegGetValueW(IntPtr hkey, string? subKey, string? value,
        uint flags, out uint type, byte[]? data, ref uint dataSize);

    /// <summary>
    /// CPU 型号。走注册表而不是 WMI：WMI 的 Win32_Processor 首次查询 20–200ms（A-02），
    /// 注册表实测 29 µs。虽然只在启动时调一次，但启动快 200ms 也是白拿的。
    /// </summary>
    public static bool TryGetCpuName(out string name)
    {
        name = "";
        const string sub = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";

        uint size = 0;
        if (RegGetValueW(HKEY_LOCAL_MACHINE, sub, "ProcessorNameString", RRF_RT_REG_SZ, out _, null, ref size) != 0)
            return false;
        if (size == 0 || size > 1024) return false;

        var buf = new byte[size];
        if (RegGetValueW(HKEY_LOCAL_MACHINE, sub, "ProcessorNameString", RRF_RT_REG_SZ, out _, buf, ref size) != 0)
            return false;

        name = Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0').Trim();
        return name.Length > 0;
    }

    // ---------------- M04 / M05 / M06 内存明细 ----------------
    [StructLayout(LayoutKind.Sequential)]
    public struct PERFORMANCE_INFORMATION
    {
        public uint cb;
        public IntPtr CommitTotal;
        public IntPtr CommitLimit;
        public IntPtr CommitPeak;
        public IntPtr PhysicalTotal;
        public IntPtr PhysicalAvailable;
        public IntPtr SystemCache;
        public IntPtr KernelTotal;
        public IntPtr KernelPaged;
        public IntPtr KernelNonpaged;
        public IntPtr PageSize;
        public uint HandleCount;
        public uint ProcessCount;
        public uint ThreadCount;
    }

    [DllImport("psapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPerformanceInfo(out PERFORMANCE_INFORMATION info, uint cb);

    /// <summary>一次调用同时给出 M04（已缓存）/ M05（提交）/ M06（页面文件）。实测 36 µs。</summary>
    public static bool TryGetPerformanceInfo(out PERFORMANCE_INFORMATION info)
    {
        info = default;
        return GetPerformanceInfo(out info, (uint)Marshal.SizeOf<PERFORMANCE_INFORMATION>());
    }

    // ---------------- C10 负载均值 ----------------
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    public static bool TryGetSystemTimes(out ulong idle, out ulong kernel, out ulong user)
        => GetSystemTimes(out idle, out kernel, out user);

    /// <summary>两次 GetSystemTimes 之间全机 CPU 占用。Kernel 已含 Idle，故总量为 Kernel+User。</summary>
    public static double CpuLoadBetween((ulong Idle, ulong Kernel, ulong User) a,
                                        (ulong Idle, ulong Kernel, ulong User) b)
    {
        ulong dIdle = b.Idle - a.Idle;
        ulong dTotal = (b.Kernel - a.Kernel) + (b.User - a.User);
        return dTotal == 0 ? 0 : Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
    }

    // ---------------- D04 分区容量 ----------------
    /// <summary>
    /// 分区使用量。CharSet 必须显式写 Unicode —— 默认是 Ansi，而这里调的是 ...W 版本，
    /// 漏写不会报封送错误，而是把 "C:\" 按 ANSI 塞进宽字符参数，系统看到 "C\0"，
    /// 返回 ERROR_PATH_NOT_FOUND(3)，表现为「所有卷都拿不到容量」。
    /// 这个坑在 M1.0 实测里踩了一次（tools/m10/probe）。
    /// </summary>
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string dir, out ulong freeAvail,
        out ulong totalBytes, out ulong totalFree);

    public static bool TryGetVolumeSpace(string root, out double totalMb, out double freeMb)
    {
        totalMb = 0;
        freeMb = 0;
        if (!GetDiskFreeSpaceExW(root, out _, out ulong total, out ulong free)) return false;
        if (total == 0) return false;

        totalMb = total / 1024.0 / 1024.0;
        freeMb = free / 1024.0 / 1024.0;
        return true;
    }

    // ---------------- D01 物理盘型号 / 类型 / 容量 ----------------
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    private const int PROP_STORAGE_DEVICE = 0;

    public readonly record struct PhysicalDisk(int Index, string Model, string Bus, double CapacityGb);

    /// <summary>
    /// 枚举物理盘。**零访问权限**打开 `\\.\PhysicalDriveN`，不提权也能拿到型号与容量。
    ///
    /// 为什么不走 WMI（4.4 原文写「LHM Storage / WMI」）：A-05 明确排除 WMI 轮询，
    /// 且 WMI 首次查询 20–200ms。也试过 SetupAPI（tools/m10/probe 实测 4.5ms、需要
    /// SP_DEVINFO_DATA 往返），问题是它返回的设备顺序**与 PhysicalDrive 索引不对应**，
    /// 而盘温是按 `PhysicalDriveN` 顺序排成的数组 —— 两边的盘会错位。
    /// 这条路径直接按索引输出，天然对齐。
    /// </summary>
    public static List<PhysicalDisk> EnumeratePhysicalDisks(int maxDevices = 16)
    {
        var list = new List<PhysicalDisk>();

        for (int i = 0; i < maxDevices; i++)
        {
            using SafeFileHandle h = CreateFileW($@"\\.\PhysicalDrive{i}", 0,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
            if (h.IsInvalid) continue;

            double gb = ReadCapacityGb(h);
            var (model, bus) = ReadDeviceDescriptor(h);
            list.Add(new PhysicalDisk(i, model, bus, gb));
        }
        return list;
    }

    private static double ReadCapacityGb(SafeFileHandle h)
    {
        IntPtr buf = Marshal.AllocHGlobal(256);
        try
        {
            // IOCTL_DISK_GET_LENGTH_INFO 在零访问权限下返回 ERROR_ACCESS_DENIED(5)，
            // 必须用 GET_DRIVE_GEOMETRY_EX（M1.0 实测：0 权限可用）。
            if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero, 0,
                    buf, 256, out _, IntPtr.Zero))
                return 0;

            // DISK_GEOMETRY 占 24 字节，DISK_GEOMETRY_EX 的 DiskSize 紧跟其后
            long bytes = Marshal.ReadInt64(buf, 24);
            return bytes > 0 ? bytes / 1024.0 / 1024 / 1024 : 0;
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static (string Model, string Bus) ReadDeviceDescriptor(SafeFileHandle h)
    {
        IntPtr inBuf = Marshal.AllocHGlobal(12);
        IntPtr outBuf = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt32(inBuf, 0, PROP_STORAGE_DEVICE);
            Marshal.WriteInt32(inBuf, 4, QUERY_STANDARD);

            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, 12, outBuf, 1024, out _, IntPtr.Zero))
                return ("", "?");

            int vendorOff = Marshal.ReadInt32(outBuf, 12);
            int productOff = Marshal.ReadInt32(outBuf, 16);
            int busType = Marshal.ReadInt32(outBuf, 28);

            string vendor = vendorOff is > 0 and < 1024
                ? Marshal.PtrToStringAnsi(IntPtr.Add(outBuf, vendorOff))?.Trim() ?? "" : "";
            string product = productOff is > 0 and < 1024
                ? Marshal.PtrToStringAnsi(IntPtr.Add(outBuf, productOff))?.Trim() ?? "" : "";

            return ($"{vendor} {product}".Trim(), BusTypeName(busType));
        }
        catch
        {
            return ("", "?");
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    /// <summary>STORAGE_BUS_TYPE 枚举（winioctl.h）。</summary>
    private static string BusTypeName(int t) => t switch
    {
        1 => "SCSI", 2 => "ATAPI", 3 => "ATA", 4 => "1394", 7 => "USB",
        8 => "RAID", 9 => "iSCSI", 10 => "SAS", 11 => "SATA",
        12 => "SD", 13 => "MMC", 14 => "Virtual", 15 => "FileVirtual",
        16 => "Spaces", 17 => "NVMe", 18 => "SCM", 19 => "UFS",
        _ => "?"
    };
}
