using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace CoPul.M10;

/// <summary>
/// M1.0 候选路径的原生调用集合。全部集中在探测程序里，确认可用后只把选中的那几条
/// 搬进 src/CoPul.Collector/Interop/ —— 不提前污染正式采集层。
/// </summary>
internal static class Native
{
    // ================================================================
    // 注册表：CPU 型号（C01）
    // ================================================================
    private const int HKEY_LOCAL_MACHINE = unchecked((int)0x80000002);
    private const uint RRF_RT_REG_SZ = 0x00000002;

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
    private static extern int RegGetValueW(IntPtr hkey, string? subKey, string? value,
        uint flags, out uint type, byte[]? data, ref uint dataSize);

    public static bool TryGetCpuName(out string name)
    {
        name = "";
        const string sub = @"HARDWARE\DESCRIPTION\System\CentralProcessor\0";
        uint size = 0;
        int rc = RegGetValueW(HKEY_LOCAL_MACHINE, sub, "ProcessorNameString", RRF_RT_REG_SZ, out _, null, ref size);
        if (rc != 0 || size == 0 || size > 1024) return false;

        var buf = new byte[size];
        rc = RegGetValueW(HKEY_LOCAL_MACHINE, sub, "ProcessorNameString", RRF_RT_REG_SZ, out _, buf, ref size);
        if (rc != 0) return false;

        name = Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0').Trim();
        return name.Length > 0;
    }

    // ================================================================
    // 每核占用率：NtQuerySystemInformation(SystemProcessorPerformanceInformation)
    // ================================================================
#pragma warning disable CS0649   // 字段由 PtrToStructure 填充，编译器看不到赋值
    public struct ProcessorPerf
    {
        public long IdleTime;
        public long KernelTime;
        public long UserTime;
        public long DpcTime;
        public long InterruptTime;
        public long InterruptCount;
    }
#pragma warning restore CS0649

    public static bool IsAdministrator()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int returnLength);

    public static bool TryQueryProcessorPerf(ProcessorPerf[] buffer, out int count)
    {
        count = 0;
        int len = buffer.Length * Marshal.SizeOf<ProcessorPerf>();
        IntPtr p = Marshal.AllocHGlobal(len);
        try
        {
            int rc = NtQuerySystemInformation(8, p, len, out _);
            if (rc != 0) return false;

            int n = Math.Min(buffer.Length, len / Marshal.SizeOf<ProcessorPerf>());
            for (int i = 0; i < n; i++)
                buffer[i] = Marshal.PtrToStructure<ProcessorPerf>(IntPtr.Add(p, i * Marshal.SizeOf<ProcessorPerf>()));

            count = n;
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(p);
        }
    }

    /// <summary>
    /// 两次采样之间的每核占用率。KernelTime 已包含 IdleTime（Windows 口径），
    /// 所以总量是 Kernel + User，不含 Dpc/Interrupt（它们已经被算进 Kernel）。
    /// </summary>
    public static double[] ComputeLoads(ProcessorPerf[] prev, ProcessorPerf[] cur)
    {
        var result = new double[cur.Length];
        for (int i = 0; i < cur.Length; i++)
        {
            long dIdle = cur[i].IdleTime - prev[i].IdleTime;
            long dKernel = cur[i].KernelTime - prev[i].KernelTime;
            long dUser = cur[i].UserTime - prev[i].UserTime;
            long total = dKernel + dUser;

            result[i] = total <= 0 ? double.NaN : Math.Clamp((total - dIdle) * 100.0 / total, 0, 100);
        }
        return result;
    }

    // ================================================================
    // 已缓存 / 提交 / 页面文件（M04–M06）
    // ================================================================
    [StructLayout(LayoutKind.Sequential)]
    public struct PerformanceInfo
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
    private static extern bool GetPerformanceInfo(out PerformanceInfo info, uint cb);

    public static bool TryGetPerformanceInfo(out PerformanceInfo info)
    {
        info = default;
        return GetPerformanceInfo(out info, (uint)Marshal.SizeOf<PerformanceInfo>());
    }

    // ================================================================
    // 磁盘列表（D01）：SetupAPI
    // ================================================================
    public sealed record DiskDrive(string Model, string Bus, string InstanceId);

    private static readonly Guid GUID_DEVCLASS_DISKDRIVE = new("4D36E967-E325-11CE-BFC1-08002BE10318");
    private const uint DIGCF_PRESENT = 0x00000002;
    private const uint SPDRP_DEVICEDESC = 0x00000000;
    private const uint SPDRP_FRIENDLYNAME = 0x0000000C;

    [StructLayout(LayoutKind.Sequential)]
    private struct SP_DEVINFO_DATA
    {
        public uint cbSize;
        public Guid ClassGuid;
        public uint DevInst;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevsW(ref Guid classGuid, IntPtr enumerator, IntPtr parent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiEnumDeviceInfo(IntPtr set, uint index, ref SP_DEVINFO_DATA data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceRegistryPropertyW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data,
        uint property, out uint regType, byte[]? buffer, uint bufferSize, out uint requiredSize);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiGetDeviceInstanceIdW(IntPtr set, ref SP_DEVINFO_DATA data,
        StringBuilder instanceId, uint size, IntPtr requiredSize);

    [DllImport("setupapi.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    /// <summary>枚举全部磁盘设备。启动一次，成本已计入探测输出。</summary>
    public static List<DiskDrive> EnumerateDiskDrives(out int rawCount)
    {
        var list = new List<DiskDrive>();
        rawCount = 0;

        Guid guid = GUID_DEVCLASS_DISKDRIVE;
        IntPtr set = SetupDiGetClassDevsW(ref guid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT);
        if (set == IntPtr.Zero || set == new IntPtr(-1)) return list;

        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = (uint)Marshal.SizeOf<SP_DEVINFO_DATA>() };
            for (uint i = 0; SetupDiEnumDeviceInfo(set, i, ref data); i++)
            {
                rawCount++;

                string friendly = ReadProperty(set, ref data, SPDRP_FRIENDLYNAME);
                string desc = ReadProperty(set, ref data, SPDRP_DEVICEDESC);
                string model = string.IsNullOrWhiteSpace(friendly) ? desc : friendly;

                var sb = new StringBuilder(256);
                string instance = SetupDiGetDeviceInstanceIdW(set, ref data, sb, (uint)sb.Capacity, IntPtr.Zero)
                    ? sb.ToString()
                    : "";

                list.Add(new DiskDrive(model, GuessBus(instance), instance));
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
        return list;
    }

    private static string ReadProperty(IntPtr set, ref SP_DEVINFO_DATA data, uint property)
    {
        SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, null, 0, out uint required);
        if (required == 0 || required > 4096) return "";

        var buf = new byte[required];
        if (!SetupDiGetDeviceRegistryPropertyW(set, ref data, property, out _, buf, required, out _)) return "";
        return Encoding.Unicode.GetString(buf).TrimEnd('\0').Trim();
    }

    private static string GuessBus(string instanceId)
    {
        string s = instanceId.ToUpperInvariant();
        if (s.StartsWith("USBSTOR")) return "USB";
        if (s.StartsWith("SCSI")) return "SCSI/NVMe";
        if (s.StartsWith("NVME")) return "NVMe";
        if (s.StartsWith("IDE") || s.StartsWith("ATA")) return "ATA";
        if (s.StartsWith("SD")) return "SD";
        return "?";
    }

    /// <summary>注册表 HKLM\SYSTEM\CurrentControlSet\Services\disk\Enum 的原始项，作对照。</summary>
    public static List<string> ReadDiskEnumRegistry()
    {
        var list = new List<string>();
        const string sub = @"SYSTEM\CurrentControlSet\Services\disk\Enum";

        for (int i = 0; i < 16; i++)
        {
            uint size = 0;
            int rc = RegGetValueW(HKEY_LOCAL_MACHINE, sub, i.ToString(), RRF_RT_REG_SZ, out _, null, ref size);
            if (rc != 0 || size == 0 || size > 2048) continue;

            var buf = new byte[size];
            rc = RegGetValueW(HKEY_LOCAL_MACHINE, sub, i.ToString(), RRF_RT_REG_SZ, out _, buf, ref size);
            if (rc != 0) continue;
            list.Add($"[{i}] " + Encoding.Unicode.GetString(buf, 0, (int)size).TrimEnd('\0'));
        }
        return list;
    }

    // ================================================================
    // 物理盘容量 / 产品名（D01）
    // ================================================================
    private const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string name, uint access, uint share,
        IntPtr sa, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle h, uint code, IntPtr inBuf, uint inSize,
        IntPtr outBuf, uint outSize, out uint returned, IntPtr overlapped);

    /// <summary>
    /// 以「零访问权限」打开物理盘。不提权时能否拿到容量与产品名，是 D01 能否做全的关键 ——
    /// NVMe Provider 里的产品名靠 GENERIC_READ|WRITE，非提权必然失败。
    /// </summary>
    public static bool ProbePhysicalDrive(string path, out long lengthMb, out string? product, out int error)
    {
        lengthMb = 0;
        product = null;
        error = 0;

        using SafeFileHandle h = CreateFileW(path, 0, 3 /* share R/W */, IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
        if (h.IsInvalid) { error = Marshal.GetLastWin32Error(); return false; }

        IntPtr outBuf = Marshal.AllocHGlobal(64);
        try
        {
            if (DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, outBuf, 64, out _, IntPtr.Zero))
                lengthMb = Marshal.ReadInt64(outBuf) / 1024 / 1024;
        }
        finally { Marshal.FreeHGlobal(outBuf); }

        // STORAGE_DEVICE_DESCRIPTOR
        IntPtr inBuf = Marshal.AllocHGlobal(12);
        IntPtr desc = Marshal.AllocHGlobal(1024);
        try
        {
            Marshal.WriteInt32(inBuf, 0, 0);
            Marshal.WriteInt32(inBuf, 4, 0);
            if (DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, 12, desc, 1024, out _, IntPtr.Zero))
            {
                int vendorOff = Marshal.ReadInt32(desc, 12);
                int productOff = Marshal.ReadInt32(desc, 16);
                string vendor = vendorOff is > 0 and < 1024 ? Marshal.PtrToStringAnsi(IntPtr.Add(desc, vendorOff))?.Trim() ?? "" : "";
                string prod = productOff is > 0 and < 1024 ? Marshal.PtrToStringAnsi(IntPtr.Add(desc, productOff))?.Trim() ?? "" : "";
                string full = $"{vendor} {prod}".Trim();
                if (full.Length > 0) product = full;
            }
            else
            {
                error = Marshal.GetLastWin32Error();
            }
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(desc);
        }

        return true;
    }

    // ================================================================
    // 磁盘容量：哪条路径在不提权时可用（D01 的「容量」）
    // ================================================================
    private const uint IOCTL_DISK_GET_DRIVE_GEOMETRY_EX = 0x000700A0;
    private const uint IOCTL_STORAGE_READ_CAPACITY = 0x002D5140;
    private const uint FILE_READ_ATTRIBUTES = 0x00000080;

    /// <summary>
    /// 逐个访问权限 × IOCTL 组合试一遍，返回一行结论。
    /// IOCTL_DISK_GET_LENGTH_INFO 在零访问权限下返回 false（M1.0 实测），
    /// 所以必须找到一条不提权也能拿容量的路，否则 D01 的「容量」对普通用户永远为空。
    /// </summary>
    public static string ProbeCapacity(string path)
    {
        var parts = new List<string>();
        var combos = new (string Label, uint Access)[]
        {
            ("0", 0),
            ("RA", FILE_READ_ATTRIBUTES),
            ("GR", 0x80000000)
        };

        foreach (var (label, access) in combos)
        {
            using SafeFileHandle h = CreateFileW(path, access, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h.IsInvalid)
            {
                parts.Add($"{label}=open:{(uint)Marshal.GetLastWin32Error()}");
                continue;
            }

            string len = TryLengthInfo(h);
            string geom = TryGeometryEx(h);
            parts.Add($"{label}[len:{len} geom:{geom}]");
        }

        return $"{path}  " + string.Join("  ", parts);
    }

    private static string TryLengthInfo(SafeFileHandle h)
    {
        IntPtr buf = Marshal.AllocHGlobal(32);
        try
        {
            if (!DeviceIoControl(h, IOCTL_DISK_GET_LENGTH_INFO, IntPtr.Zero, 0, buf, 32, out _, IntPtr.Zero))
                return "F" + Marshal.GetLastWin32Error();
            long bytes = Marshal.ReadInt64(buf);
            return $"{bytes / 1024.0 / 1024 / 1024:F1}G";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    private static string TryGeometryEx(SafeFileHandle h)
    {
        IntPtr buf = Marshal.AllocHGlobal(256);
        try
        {
            if (!DeviceIoControl(h, IOCTL_DISK_GET_DRIVE_GEOMETRY_EX, IntPtr.Zero, 0, buf, 256, out _, IntPtr.Zero))
                return "F" + Marshal.GetLastWin32Error();
            // DISK_GEOMETRY 占 24 字节，DiskSize 紧跟其后（8 字节对齐）
            long bytes = Marshal.ReadInt64(buf, 24);
            return $"{bytes / 1024.0 / 1024 / 1024:F1}G";
        }
        finally { Marshal.FreeHGlobal(buf); }
    }

    // ================================================================
    // 分区（D04）
    // ================================================================
    public sealed record Partition(string Name, double TotalMb, double FreeMb, string DriveType, string Format);

    // CharSet 必须显式写 Unicode：默认是 Ansi，而这里调的是 ...W 版本。
    // 漏了它不会报封送错误，而是把 "C:\" 按 ANSI 编码塞进宽字符参数，
    // 系统看到的是 "C\0" —— 返回 ERROR_PATH_NOT_FOUND(3)，表现为「所有卷都拿不到容量」。
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetDiskFreeSpaceExW(string dir, out ulong freeAvail, out ulong total, out ulong totalFree);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern uint GetDriveTypeW(string root);

    public static List<string> PartitionErrors { get; } = new();

    public static List<Partition> TryGetPartitions(out int count)
    {
        var list = new List<Partition>();
        PartitionErrors.Clear();
        count = 0;

        foreach (var d in DriveInfo.GetDrives())
        {
            count++;
            try
            {
                if (!d.IsReady) { PartitionErrors.Add($"{d.Name} not ready"); continue; }
                if (d.DriveType is not (DriveType.Fixed or DriveType.Removable))
                {
                    PartitionErrors.Add($"{d.Name} type={d.DriveType}");
                    continue;
                }

                string root = d.Name;
                if (!GetDiskFreeSpaceExW(root, out _, out ulong total, out _))
                {
                    PartitionErrors.Add($"{root} freeSpace:err={Marshal.GetLastWin32Error()}");
                    continue;
                }

                double totalMb = total / 1024.0 / 1024.0;
                double freeMb = d.AvailableFreeSpace / 1024.0 / 1024.0;
                list.Add(new Partition(root.TrimEnd('\\'), totalMb, freeMb, d.DriveType.ToString(), d.DriveFormat));
            }
            catch
            {
                // 空光驱、离线卷等，忽略
            }
        }
        return list;
    }

    // ================================================================
    // 负载均值（C10）
    // ================================================================
    [StructLayout(LayoutKind.Sequential)]
    public struct FileTime
    {
        public uint Low;
        public uint High;
        public ulong Value => ((ulong)High << 32) | Low;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemTimes(out FileTime idle, out FileTime kernel, out FileTime user);

    public static bool TryGetSystemTimes(out (ulong Idle, ulong Kernel, ulong User) t)
    {
        bool ok = GetSystemTimes(out var i, out var k, out var u);
        t = (i.Value, k.Value, u.Value);
        return ok;
    }

    public static double CpuLoadBetween((ulong Idle, ulong Kernel, ulong User) a,
                                        (ulong Idle, ulong Kernel, ulong User) b)
    {
        ulong dIdle = b.Idle - a.Idle;
        ulong dTotal = (b.Kernel - a.Kernel) + (b.User - a.User);
        return dTotal == 0 ? 0 : Math.Clamp((dTotal - dIdle) * 100.0 / dTotal, 0, 100);
    }
}
