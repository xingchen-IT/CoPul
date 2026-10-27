using System.Diagnostics;
using System.Runtime.InteropServices;

namespace CoPul.Collector.Interop;

/// <summary>
/// GPU 型号的兜底来源（R19）：DXGI 适配器枚举。
///
/// 为什么需要它：<see cref="Providers.NvmlGpuProvider"/> 在非 NVIDIA 机器上直接
/// <c>Unsupported</c>，G01 就没有任何来源；而需求文档 4.2 承诺"非 NVIDIA 也保证 G01"。
/// 即使装了 NVIDIA 驱动，若 nvmlInit 失败（老驱动 / 容器内 / 服务被禁用），G01 同样会是空。
///
/// 为什么是 DXGI 而不是别的：
/// - WMI <c>Win32_VideoController</c> 首次查询 20–200 ms，且违反 A-05；
/// - LHM 需要提权，且它本身也是通过 DXGI / NVAPI 拿的名字；
/// - <c>CreateDXGIFactory1</c> 是 dxgi.dll 的纯用户态调用，**不提权、不建 D3D 设备**，
///   首次调用含 DLL 加载约 1–3 ms，之后同进程内再调是微秒级。
///
/// 不做的事：不取显存用量、不取占用率——那些在非 NVIDIA 平台上仍然显示 `—`（A-08 例外），
/// 这里只负责"这一格不要是空的"。
/// </summary>
internal static class Dxgi
{
    /// <summary>一块 GPU 适配器。字段名与 <see cref="DXGI_ADAPTER_DESC1"/> 对齐，便于查资料。</summary>
    public sealed record Adapter(string Description, uint VendorId, uint DeviceId, uint SubSysId,
                                 ulong DedicatedVramBytes, uint Flags, long Luid)
    {
        /// <summary><c>DXGI_ADAPTER_FLAG_SOFTWARE</c>：WARP 软件渲染器 / 远程桌面基础显示驱动。</summary>
        public bool IsSoftware => (Flags & 2) != 0;

        public double DedicatedVramGb => DedicatedVramBytes / 1024.0 / 1024.0 / 1024.0;

        public string VendorName => VendorId switch
        {
            0x10DE => "NVIDIA",
            0x1002 or 0x1022 => "AMD",
            0x8086 => "Intel",
            0x1414 => "Microsoft",
            0x5143 => "Qualcomm",
            _ => $"0x{VendorId:X4}"
        };
    }

    private const int DXGI_ERROR_NOT_FOUND = unchecked((int)0x887A0002);

    [DllImport("dxgi.dll", ExactSpelling = true)]
    private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

    /// <summary>
    /// 枚举全部 GPU 适配器（含软件渲染器，由调用方按 <see cref="Adapter.IsSoftware"/> 过滤）。
    /// 失败时不抛异常，返回 false 并给出原因——启动路径不允许因为一块显卡崩掉整个窗口。
    /// </summary>
    public static bool TryGetAdapters(out List<Adapter> adapters, out double elapsedMs, out string? error)
    {
        var sw = Stopwatch.StartNew();
        adapters = new List<Adapter>();
        error = null;

        IntPtr factoryPtr = IntPtr.Zero;
        IDXGIFactory1? factory = null;
        try
        {
            Guid iid = typeof(IDXGIFactory1).GUID;
            int hr = CreateDXGIFactory1(ref iid, out factoryPtr);
            if (hr < 0 || factoryPtr == IntPtr.Zero)
            {
                error = $"CreateDXGIFactory1 失败，HRESULT 0x{hr:X8}。";
                return false;
            }

            factory = (IDXGIFactory1)Marshal.GetTypedObjectForIUnknown(factoryPtr, typeof(IDXGIFactory1));

            for (uint i = 0; i < 32; i++)
            {
                int rc = factory.EnumAdapters1(i, out IDXGIAdapter1? adapter);
                if (rc == DXGI_ERROR_NOT_FOUND || rc < 0 || adapter is null) break;

                try
                {
                    if (adapter.GetDesc1(out DXGI_ADAPTER_DESC1 desc) >= 0)
                    {
                        adapters.Add(new Adapter(
                            (desc.Description ?? "").TrimEnd('\0'),
                            desc.VendorId,
                            desc.DeviceId,
                            desc.SubSysId,
                            (ulong)desc.DedicatedVideoMemory,
                            desc.Flags,
                            desc.AdapterLuid));
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(adapter);
                }
            }

            // 去重：DXGI 会把同一块物理卡重复列出来。本机实测 RTX 4060 被列了 4 次，
            // 而且 **LUID 各不相同**（不同输出通路各有一个适配器实例），所以 LUID 不能当去重键。
            // 只能用「型号 + 子系统 ID + 独显容量」认同一块卡。代价：真装两块一模一样型号的卡会被合成一条，
            // 这种装机方式在笔记本/一体机上不存在，且 G01 只需要一个名字，可以接受。
            adapters = adapters
                .GroupBy(a => (a.Description, a.VendorId, a.DeviceId, a.SubSysId, a.DedicatedVramBytes))
                .Select(g => g.First())
                .ToList();

            return adapters.Count > 0;
        }
        catch (Exception ex)
        {
            error = $"DXGI 枚举异常：{ex.GetBaseException().Message}";
            return false;
        }
        finally
        {
            if (factory is not null) Marshal.ReleaseComObject(factory);
            if (factoryPtr != IntPtr.Zero) Marshal.Release(factoryPtr);
            sw.Stop();
            elapsedMs = sw.Elapsed.TotalMilliseconds;
        }
    }

    /// <summary>
    /// 挑一块"主 GPU"用于显示型号。
    ///
    /// 规则：先剔掉软件渲染器，再按**独显容量降序**取第一块；容量全为 0（纯核显机器）时取第一块。
    /// 不用"索引 0"是因为笔记本上 DXGI 的枚举顺序不保证独显在前，而显存容量能稳定区分
    /// 独显与核显。多 GPU 场景下完整列表留在 <see cref="Metrics.SystemInfo.GpuAdapters"/> 里，UI 可自行展开。
    /// </summary>
    public static Adapter? PickPrimary(List<Adapter> adapters)
    {
        var real = adapters.Where(a => !a.IsSoftware && !string.IsNullOrWhiteSpace(a.Description)).ToList();
        if (real.Count == 0) return null;

        Adapter best = real[0];
        foreach (var a in real)
            if (a.DedicatedVramBytes > best.DedicatedVramBytes) best = a;
        return best;
    }

    // ================================================================
    // COM 接口定义
    //
    // 用 [ComImport] + 手工声明 vtable 顺序，比 C++/CLI 或 CsWinRT 都轻。
    // 规则：接口继承来的方法必须按原顺序全部列出（IUnknown 的三个不用写，运行时会补），
    // 且方法签名只要**参数个数与栈布局**正确即可——我们不调用的方法写成占位也无妨。
    // 全部用 PreserveSig，避免 COM 异常影响启动路径。
    // ================================================================

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DXGI_ADAPTER_DESC1
    {
        /// <summary><c>WCHAR Description[128]</c>，共 256 字节。</summary>
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;

        /// <summary><c>SIZE_T</c>，x64 下 8 字节。</summary>
        public UIntPtr DedicatedVideoMemory;
        public UIntPtr DedicatedSystemMemory;
        public UIntPtr SharedSystemMemory;

        /// <summary><c>LUID</c>（DWORD + LONG，共 8 字节），本类不用，占位以对齐后续字段。</summary>
        public long AdapterLuid;

        /// <summary><c>DXGI_ADAPTER_FLAG</c>：1 = Remote，2 = Software，4 = ACG 兼容。</summary>
        public uint Flags;
    }

    [ComImport, Guid("29038f61-3839-4626-91fd-086879011a05"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIAdapter1
    {
        // ---- IDXGIObject ----
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);

        // ---- IDXGIAdapter ----
        [PreserveSig] int EnumOutputs(uint output, out IntPtr ppOutput);
        [PreserveSig] int GetDesc(IntPtr pDesc);
        [PreserveSig] int CheckInterfaceSupport(ref Guid interfaceName, out long umdVersion);

        // ---- IDXGIAdapter1（唯一被调用的方法）----
        [PreserveSig] int GetDesc1(out DXGI_ADAPTER_DESC1 pDesc);
    }

    [ComImport, Guid("770aae78-f26f-4dba-a829-253c83d1b387"),
     InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IDXGIFactory1
    {
        // ---- IDXGIObject ----
        [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr pData);
        [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr pUnknown);
        [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr pData);
        [PreserveSig] int GetParent(ref Guid riid, out IntPtr ppParent);

        // ---- IDXGIFactory ----
        [PreserveSig] int EnumAdapters(uint adapter, out IntPtr ppAdapter);
        [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
        [PreserveSig] int GetWindowAssociation(out IntPtr pWindowHandle);
        [PreserveSig] int CreateSwapChain(IntPtr pDevice, IntPtr pDesc, out IntPtr ppSwapChain);
        [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr ppAdapter);

        // ---- IDXGIFactory1（唯一被调用的方法）----
        [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1? ppAdapter);
        [PreserveSig] int IsCurrent();
    }
}
