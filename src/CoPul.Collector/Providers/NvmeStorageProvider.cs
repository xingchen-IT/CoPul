using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using CoPul.Collector.Interop;
using CoPul.Collector.Metrics;
using Microsoft.Win32.SafeHandles;

namespace CoPul.Collector.Providers;

/// <summary>
/// 硬盘温度，自研 NVMe Get Log Page（SMART / Health Information, Log Page 0x02）。
///
/// 为什么不用 LHM：M0 实测两块 NVMe 的 Composite Temperature 都被读成 0.00°C，
/// 但同一份 SMART 记录里 Warning 69 / Critical 74 是通的（说明通路存在，是解析失效）。
/// 显示 0°C 比显示 `—` 更糟 —— 用户会以为硬盘很凉快。
///
/// 实测：NVMe IOCTL 单次 2.6–3.5 ms，所以固定 5000 ms（P-08）。
/// </summary>
public sealed class NvmeStorageProvider : IMetricProvider
{
    private const int NVME_LOG_SMART_HEALTH = 0x02;
    private const int PROTOCOL_DATA_SIZE = 40;   // STORAGE_PROTOCOL_SPECIFIC_DATA
    private const int SMART_DATA_LENGTH = 512;
    private const int QUERY_HEADER_SIZE = 8;     // STORAGE_PROPERTY_QUERY 紧跟在后面

    private readonly List<DiskSlot> _disks = new();
    private readonly List<SampleGroup> _groups;

    public NvmeStorageProvider()
    {
        _groups = new List<SampleGroup>
        {
            new("Nvme.Temp", Tier.T2Slow, SampleTemperatures)
        };
    }

    public string Name => "NVMe SMART";
    public ProviderStatus Status { get; private set; } = ProviderStatus.NotInitialized;
    public string? UnavailableReason { get; private set; }
    public string? Remedy { get; private set; }
    public double InitializeMs { get; private set; }
    public IReadOnlyList<SampleGroup> Groups => _groups;

    public IReadOnlyList<string> DiskNames => _disks.Select(d => d.Name).ToList();

    /// <summary>诊断用：单次 CreateFile 与单次 IOCTL 的实测耗时，决定是否值得缓存句柄。</summary>
    public double LastOpenMs { get; private set; }
    public double LastIoctlMs { get; private set; }

    public bool Initialize()
    {
        if (Status != ProviderStatus.NotInitialized) return Status == ProviderStatus.Ready;

        var sw = Stopwatch.StartNew();
        try
        {
            for (int i = 0; i < 16; i++)
            {
                string path = $@"\\.\PhysicalDrive{i}";
                if (!DriveExists(path)) continue;

                var openSw = Stopwatch.StartNew();
                SafeFileHandle h = Win32.CreateFileW(path,
                    Win32.GENERIC_READ | Win32.GENERIC_WRITE,
                    Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE,
                    IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
                openSw.Stop();

                if (h.IsInvalid)
                {
                    h.Dispose();
                    // 非提权时这里就是错误 5 —— 整个 Provider 不可用，但要给出明确原因
                    continue;
                }

                var ioSw = Stopwatch.StartNew();
                bool ok = TryReadSmartLog(h, out byte[] smart, out _);
                ioSw.Stop();

                if (!ok) { h.Dispose(); continue; }

                string name = ReadProductName(h) ?? $"PhysicalDrive{i}";
                _disks.Add(new DiskSlot(i, name, h, smart.Length > 10));
            }

            if (_disks.Count == 0)
            {
                Status = Win32.IsAdministrator() ? ProviderStatus.Unsupported : ProviderStatus.Failed;
                UnavailableReason = Win32.IsAdministrator()
                    ? "没有可读 SMART 的 NVMe 硬盘。"
                    : "读取硬盘温度需要管理员权限（物理磁盘句柄被拒）。";
                Remedy = Win32.IsAdministrator()
                    ? "SATA 盘走 ATA PASS THROUGH，v1 不做实测承诺。"
                    : "以管理员身份重新启动 CoPul。";
                return false;
            }

            Status = ProviderStatus.Ready;
            return true;
        }
        catch (Exception ex)
        {
            Status = ProviderStatus.Failed;
            UnavailableReason = $"硬盘温度初始化异常：{ex.GetBaseException().Message}";
            return false;
        }
        finally
        {
            sw.Stop();
            InitializeMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    private void SampleTemperatures(MetricSnapshot sink)
    {
        if (_disks.Count == 0)
        {
            sink.SetSeries(MetricId.DiskTemperature, Array.Empty<double>(), MetricState.Unavailable);
            return;
        }

        var temps = new double[_disks.Count];
        int valid = 0;

        for (int i = 0; i < _disks.Count; i++)
        {
            var sw = Stopwatch.StartNew();
            bool ok = TryReadSmartLog(_disks[i].Handle, out byte[] data, out _);
            sw.Stop();
            LastIoctlMs = sw.Elapsed.TotalMilliseconds;

            if (!ok || data.Length < 3) { temps[i] = double.NaN; continue; }

            int kelvin = data[1] | (data[2] << 8);
            double celsius = kelvin - 273.15;

            // A-13 同源规则：温度落在 -20..120 之外一律视为无效读数，不给 UI 假值。
            if (celsius is > -20 and < 120) { temps[i] = celsius; valid++; }
            else temps[i] = double.NaN;
        }

        sink.SetSeries(MetricId.DiskTemperature, temps,
            valid > 0 ? MetricState.Ok : MetricState.Invalid);
    }

    private static bool TryReadSmartLog(SafeFileHandle h, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        error = "";

        // 先按 QUERY_HEADER_SIZE(=8) 试；M0.5 实测这是正确偏移，
        // 12 只作兜底（不同 Windows 版本对协议缓冲区对齐要求可能不同）。
        foreach (int headerOffset in new[] { QUERY_HEADER_SIZE, 12 })
        {
            if (TryIoctl(h, headerOffset, out data, out error)) return true;
        }
        return false;
    }

    private static bool TryIoctl(SafeFileHandle h, int headerOffset, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        error = "";

        int total = headerOffset + PROTOCOL_DATA_SIZE + SMART_DATA_LENGTH;
        IntPtr inBuf = Marshal.AllocHGlobal(total);
        IntPtr outBuf = Marshal.AllocHGlobal(total + 128);
        try
        {
            for (int i = 0; i < total; i++) Marshal.WriteByte(inBuf, i, 0);
            for (int i = 0; i < total + 128; i++) Marshal.WriteByte(outBuf, i, 0);

            Marshal.WriteInt32(inBuf, 0, Win32.PROP_STORAGE_DEVICE_PROTOCOL_SPECIFIC);
            Marshal.WriteInt32(inBuf, 4, Win32.QUERY_STANDARD);

            int p = headerOffset;
            Marshal.WriteInt32(inBuf, p + 0, Win32.PROTOCOL_TYPE_NVME);
            Marshal.WriteInt32(inBuf, p + 4, Win32.NVME_DATA_TYPE_LOG_PAGE);
            Marshal.WriteInt32(inBuf, p + 8, NVME_LOG_SMART_HEALTH);
            Marshal.WriteInt32(inBuf, p + 16, PROTOCOL_DATA_SIZE);
            Marshal.WriteInt32(inBuf, p + 20, SMART_DATA_LENGTH);

            if (!Win32.DeviceIoControl(h, Win32.IOCTL_STORAGE_QUERY_PROPERTY, inBuf, (uint)total,
                    outBuf, (uint)(total + 128), out _, IntPtr.Zero))
            {
                error = Marshal.GetLastWin32Error().ToString();
                return false;
            }

            int retOff = Marshal.ReadInt32(outBuf, headerOffset + 16);
            int retLen = Marshal.ReadInt32(outBuf, headerOffset + 20);
            int start = headerOffset + (retOff > 0 ? retOff : PROTOCOL_DATA_SIZE);
            int len = retLen is > 0 and <= 4096 ? retLen : SMART_DATA_LENGTH;

            data = new byte[len];
            Marshal.Copy(IntPtr.Add(outBuf, start), data, 0, len);
            if (data.Length < 5) { error = $"返回过短({data.Length})"; return false; }
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    /// <summary>读 STORAGE_DEVICE_DESCRIPTOR 的 ProductId，用于多盘时区分。失败返回 null。</summary>
    private static string? ReadProductName(SafeFileHandle h)
    {
        const int PROP_STORAGE_DEVICE = 0;
        const int bufferSize = 1024;

        IntPtr inBuf = Marshal.AllocHGlobal(12);
        IntPtr outBuf = Marshal.AllocHGlobal(bufferSize);
        try
        {
            Marshal.WriteInt32(inBuf, 0, PROP_STORAGE_DEVICE);
            Marshal.WriteInt32(inBuf, 4, Win32.QUERY_STANDARD);

            if (!Win32.DeviceIoControl(h, Win32.IOCTL_STORAGE_QUERY_PROPERTY, inBuf, 12,
                    outBuf, bufferSize, out _, IntPtr.Zero))
                return null;

            int productOffset = Marshal.ReadInt32(outBuf, 16);
            int vendorOffset = Marshal.ReadInt32(outBuf, 12);
            if (productOffset <= 0 || productOffset >= bufferSize) return null;

            string product = Marshal.PtrToStringAnsi(IntPtr.Add(outBuf, productOffset))?.Trim() ?? "";
            string vendor = vendorOffset > 0 && vendorOffset < bufferSize
                ? Marshal.PtrToStringAnsi(IntPtr.Add(outBuf, vendorOffset))?.Trim() ?? ""
                : "";

            string full = $"{vendor} {product}".Trim();
            return string.IsNullOrWhiteSpace(full) ? null : full;
        }
        catch
        {
            return null;
        }
        finally
        {
            Marshal.FreeHGlobal(inBuf);
            Marshal.FreeHGlobal(outBuf);
        }
    }

    private static bool DriveExists(string path)
    {
        using SafeFileHandle h = Win32.CreateFileW(path, 0,
            Win32.FILE_SHARE_READ | Win32.FILE_SHARE_WRITE, IntPtr.Zero, Win32.OPEN_EXISTING, 0, IntPtr.Zero);
        return !h.IsInvalid;
    }

    public void Dispose()
    {
        foreach (var d in _disks) d.Handle.Dispose();
        _disks.Clear();
        Status = ProviderStatus.NotInitialized;
    }

    private sealed record DiskSlot(int Index, string Name, SafeFileHandle Handle, bool IsNvme);
}
