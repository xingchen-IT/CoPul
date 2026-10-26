using System.Runtime.InteropServices;
using System.Text;

namespace CoPul.Collector.Interop;

/// <summary>
/// PDH（Performance Data Helper）封装。
/// 为什么不用 PerformanceCounter 类：后者每次 NextValue() 都走一次完整解析，
/// 且中文系统下英文计数器名会解析失败。这里直接调 pdh.dll，
/// 用 PdhAddEnglishCounterW 绕开本地化差异，一次 Collect 刷新整个 query。
/// </summary>
public sealed class PdhQuery : IDisposable
{
    public const uint ERROR_SUCCESS = 0;
    public const uint PDH_MORE_DATA = 0x800007D2;
    public const uint PDH_INVALID_DATA = 0xC0000BC6;
    public const uint PDH_CSTATUS_VALID_DATA = 0x00000000;
    public const uint PDH_CSTATUS_NEW_DATA = 0x00000001;

    private const uint PDH_FMT_DOUBLE = 0x00000200;

    private IntPtr _query = IntPtr.Zero;
    private readonly List<PdhCounter> _counters = new();
    private bool _disposed;

    public bool IsOpen => _query != IntPtr.Zero;
    public IReadOnlyList<PdhCounter> Counters => _counters;

    public bool Open()
    {
        if (IsOpen) return true;
        uint rc = PdhOpenQueryW(null, IntPtr.Zero, out _query);
        if (rc != ERROR_SUCCESS)
        {
            _query = IntPtr.Zero;
            throw new InvalidOperationException($"PdhOpenQuery 失败 rc=0x{rc:X8}");
        }
        return true;
    }

    /// <summary>
    /// 用英文计数器名添加。返回 null 表示添加失败（该计数器在本机不存在）。
    /// 注意：中文 Windows 上必须走 EnglishCounter，直接 PdhAddCounter 会解析不到
    /// `\Processor Information(_Total)\% Processor Utility`。
    /// </summary>
    public PdhCounter? TryAdd(string path)
    {
        if (!IsOpen) throw new InvalidOperationException("query 未打开");

        uint rc = PdhAddEnglishCounterW(_query, path, IntPtr.Zero, out IntPtr handle);
        if (rc != ERROR_SUCCESS || handle == IntPtr.Zero) return null;

        var c = new PdhCounter(path, handle);
        _counters.Add(c);
        return c;
    }

    /// <summary>刷新整个 query 的原始数据。rate 型计数器需要至少两次调用才有值。</summary>
    public uint Collect() => IsOpen ? PdhCollectQueryData(_query) : PDH_INVALID_DATA;

    /// <summary>
    /// 取格式化值。CStatus 非 VALID/NEW_DATA 时返回 null —— 首帧、计数器暂停、
    /// 或分母为 0 都会落到这里，不能当成 0 用。
    /// </summary>
    public static double? TryGetDouble(PdhCounter counter, uint collectStatus)
    {
        if (collectStatus != ERROR_SUCCESS) return null;

        uint rc = PdhGetFormattedCounterValue(counter.Handle, PDH_FMT_DOUBLE, out _, out var v);
        if (rc != ERROR_SUCCESS) return null;
        if (v.CStatus is not (PDH_CSTATUS_VALID_DATA or PDH_CSTATUS_NEW_DATA)) return null;
        if (double.IsNaN(v.DoubleValue) || double.IsInfinity(v.DoubleValue)) return null;

        counter.HasValue = true;
        counter.LastValue = v.DoubleValue;
        return v.DoubleValue;
    }

    /// <summary>
    /// 展开通配符，例如 `\\Network Interface(*)\\Bytes Received/sec` 得到全部实例。
    ///
    /// 这里踩过两个坑，都会表现为「静默少列实例」而不是报错：
    ///
    /// 1. 第一次传 null 只探测时，PDH 返回的长度往往只够放**一个**路径；
    ///    若直接按该长度分配再调用，PDH 写入第一项就返回 SUCCESS。
    ///    正确做法是每次拿到 PDH_MORE_DATA 就按新长度重分配，循环到成功。
    ///
    /// 2. 返回的是 MULTI_SZ（多个以 \0 分隔的字符串，末尾双 \0）。
    ///    .NET 对 StringBuilder 参数的封送按「单串」处理，ToString() 只能取到
    ///    第一个 \0 之前的内容 —— 所以这里必须用 IntPtr + PtrToStringUni 手工逐项读。
    ///
    /// 本机实测后果：三个网卡只返回 1 个（主用的 WiFi 直接消失），
    /// 网络速率因此永远显示 0.0 kB/s。
    /// </summary>
    public static string[] ExpandWildcard(string wildcardPath)
    {
        uint len = 0;
        uint rc = PdhExpandWildCardPathW(null, wildcardPath, IntPtr.Zero, ref len, 0);
        if (rc != PDH_MORE_DATA || len == 0) return Array.Empty<string>();

        for (int attempt = 0; attempt < 5; attempt++)
        {
            IntPtr buf = Marshal.AllocHGlobal((int)len * sizeof(char));
            try
            {
                uint actual = len;
                rc = PdhExpandWildCardPathW(null, wildcardPath, buf, ref actual, 0);

                if (rc == ERROR_SUCCESS)
                {
                    var list = new List<string>();
                    int charOffset = 0;
                    while (charOffset < (int)len)
                    {
                        string? item = Marshal.PtrToStringUni(IntPtr.Add(buf, charOffset * sizeof(char)));
                        if (string.IsNullOrEmpty(item)) break;
                        list.Add(item);
                        charOffset += item.Length + 1;
                    }
                    return list.ToArray();
                }

                if (rc != PDH_MORE_DATA) return Array.Empty<string>();
                len = actual + 2;   // 留出 MULTI_SZ 尾部双 \0
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
            }
        }
        return Array.Empty<string>();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_query != IntPtr.Zero)
        {
            PdhCloseQuery(_query);
            _query = IntPtr.Zero;
        }
        _counters.Clear();
    }

    [StructLayout(LayoutKind.Explicit, Size = 16)]
    private struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public double DoubleValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhOpenQueryW(string? dataSource, IntPtr userData, out IntPtr query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhAddEnglishCounterW(IntPtr query, string fullCounterPath,
        IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll")]
    private static extern uint PdhCollectQueryData(IntPtr query);

    [DllImport("pdh.dll")]
    private static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format,
        out uint counterType, out PdhFmtCounterValue value);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
    private static extern uint PdhExpandWildCardPathW(string? dataSource, string wildCardPath,
        IntPtr expandedPathList, ref uint pathListLength, uint flags);

    [DllImport("pdh.dll")]
    private static extern uint PdhCloseQuery(IntPtr query);
}

/// <summary>query 内的一个计数器句柄。</summary>
public sealed class PdhCounter
{
    internal PdhCounter(string path, IntPtr handle)
    {
        Path = path;
        Handle = handle;
    }

    public string Path { get; }
    internal IntPtr Handle { get; }
    public bool HasValue { get; internal set; }
    public double LastValue { get; internal set; }
}
