using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using LibreHardwareMonitor.PawnIo;
using Microsoft.Win32.SafeHandles;

namespace CoPul.M05;

internal static class Program
{
    private static readonly StringBuilder Report = new();
    private static IntelMsr? _msr;
    private static bool _msrReady;

    private const uint MSR_IA32_THERM_STATUS = 0x19C;
    private const uint MSR_TEMPERATURE_TARGET = 0x1A2;
    private const uint MSR_PACKAGE_THERM_STATUS = 0x1B1;
    private const uint MSR_RAPL_POWER_UNIT = 0x606;
    private const uint MSR_PKG_ENERGY_STATUS = 0x611;
    private const uint MSR_IA32_MPERF = 0xE7;
    private const uint MSR_IA32_APERF = 0xE8;
    private const uint MSR_IA32_PERF_STATUS = 0x198;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        string outFile = args.Length > 0 ? args[0] : "m05-probe.txt";

        W("================ CoPul M0.5 采集路径验证 ================");
        W($"时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"提权        : {(IsAdmin() ? "是" : "否  ← MSR / NVMe 大概率全部失败")}");
        W($"进程架构    : {RuntimeInformation.ProcessArchitecture}");
        W($"逻辑处理器  : {Environment.ProcessorCount}");
        W();

        int ok = 0, total = 0;
        total++; if (SectionA_Driver()) ok++;
        total++; if (SectionB_MsrTemperature()) ok++;
        total++; if (SectionC_MsrPower()) ok++;
        total++; if (SectionD_MsrClock()) ok++;
        total++; if (SectionE_NvmeTemperature()) ok++;
        total++; if (SectionF_Timing()) ok++;

        W();
        W("================ 结论 ================");
        W($"  通过 {ok} / {total} 项");
        W();
        W("  判读口径：");
        W("    A 通过 = PawnIO 驱动与 IntelMSR 模块可用（不必自己写驱动）");
        W("    B/C/D 通过 = MSR 可直读，CPU 温度/功耗/频率已脱离 Cpu.Update()");
        W("    E 通过 = 硬盘温度可自研读 NVMe Log Page");
        W("    F 只输出耗时与预算推算，不做判定");

        try { _msr?.Close(); } catch { }

        try
        {
            File.WriteAllText(outFile, Report.ToString(), new UTF8Encoding(true));
            Console.WriteLine($"\n报告已写入: {Path.GetFullPath(outFile)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"\n写报告失败: {ex.Message}");
        }
        return 0;
    }

    // ============================================================
    // A. 驱动入口
    // ============================================================
    private static bool SectionA_Driver()
    {
        W("[A] PawnIO 驱动 + IntelMSR 模块");
        W("    背景：LHM 0.9.6 已从 WinRing0 迁移到 PawnIO，模块以 .bin 内嵌在程序集里");
        W("          入口 = LibreHardwareMonitor.PawnIo.IntelMsr（public，无参构造）");

        var sw = Stopwatch.StartNew();
        try
        {
            _msr = new IntelMsr();
            sw.Stop();
            W($"    new IntelMsr() 成功，耗时 {sw.Elapsed.TotalMilliseconds:F2} ms");
            _msrReady = true;
        }
        catch (Exception ex)
        {
            sw.Stop();
            W($"    !! new IntelMsr() 失败（{sw.Elapsed.TotalMilliseconds:F2} ms）");
            W($"       {ex.GetBaseException().GetType().Name}: {ex.GetBaseException().Message}");
            W("       若为权限问题，请用管理员身份重跑");
            W();
            return false;
        }

        // 读取 PawnIo 状态（IsInstalled / Version 是静态属性）
        try
        {
            W($"    PawnIo.IsInstalled = {PawnIo.IsInstalled}");
            W($"    PawnIo.Version     = {PawnIo.Version}");
            var f = typeof(IntelMsr).GetField("_pawnIO", BindingFlags.NonPublic | BindingFlags.Instance);
            if (f?.GetValue(_msr) is PawnIo pio)
                W($"    实例 IsLoaded      = {pio.IsLoaded}");
        }
        catch (Exception ex)
        {
            W($"    读取 PawnIo 状态失败: {ex.Message}");
        }

        if (!TryReadMsr(MSR_TEMPERATURE_TARGET, out uint eax, out _))
        {
            W("    !! 读 MSR 0x1A2 返回 false，驱动未生效");
            W();
            return false;
        }
        if (eax == 0)
        {
            W("    !! 读 MSR 0x1A2 返回 true，但值为 0");
            W("       ← 陷阱：IsLoaded=False 时 ReadMsr 不报错，只返回全 0");
            W("         必须按「值是否合理」判定，不能只信返回值");
            W("         （与 M0 的硬盘温度 0.00°C 属同一类假值问题）");
            W();
            return false;
        }
        W($"    试读 MSR 0x1A2 = 0x{eax:X8}  -> 可用");
        W();
        return true;
    }

    // ============================================================
    // B. MSR 温度
    // ============================================================
    private static bool SectionB_MsrTemperature()
    {
        W("[B] MSR 温度");
        if (!_msrReady) { W("    跳过：驱动未就绪"); W(); return false; }

        if (!TryReadMsr(MSR_TEMPERATURE_TARGET, out uint eax, out _))
        {
            W("    !! 读不到 MSR_TEMPERATURE_TARGET(0x1A2)"); W(); return false;
        }
        int tjMax = (int)((eax >> 16) & 0xFF);
        W($"    0x1A2 = 0x{eax:X8}  ->  TjMax = {tjMax} °C");
        bool tjSane = tjMax is >= 80 and <= 120;
        if (!tjSane) W($"    !! TjMax={tjMax} 不在合理区间，该 MSR 可能被虚拟化层拦截");

        bool pkgOk = false;
        if (TryReadMsr(MSR_PACKAGE_THERM_STATUS, out eax, out _))
        {
            bool valid = (eax & 0x8000_0000u) != 0;
            int readout = (int)((eax >> 16) & 0x7F);
            W($"    封装 0x1B1 = 0x{eax:X8}  valid={valid}  readout={readout}  ->  {tjMax - readout} °C");
            pkgOk = valid && readout != 0;
        }
        else W("    !! 读不到 IA32_PACKAGE_THERM_STATUS(0x1B1)");

        W("    每核（SetThreadAffinityMask 逐核）:");
        int cores = Math.Min(Environment.ProcessorCount, 64);
        var temps = new List<string>();
        int validCores = 0;
        for (int i = 0; i < cores; i++)
        {
            IntPtr prev = SetThreadAffinityMask(GetCurrentThread(), (IntPtr)(1L << i));
            if (prev == IntPtr.Zero) { W($"      core{i} 亲和性失败 err={Marshal.GetLastWin32Error()}"); break; }
            if (TryReadMsr(MSR_IA32_THERM_STATUS, out eax, out _))
            {
                bool valid = (eax & 0x8000_0000u) != 0;
                int readout = (int)((eax >> 16) & 0x7F);
                if (valid && readout != 0) { temps.Add($"{tjMax - readout}"); validCores++; }
                else temps.Add("--");
            }
            else temps.Add("ERR");
            SetThreadAffinityMask(GetCurrentThread(), prev);
        }
        W($"      [{string.Join(" ", temps)}]");
        W($"      有效 {validCores}/{cores}");

        W();
        return pkgOk && validCores > 0;
    }

    // ============================================================
    // C. RAPL 功耗
    // ============================================================
    private static bool SectionC_MsrPower()
    {
        W("[C] MSR RAPL 功耗");
        if (!_msrReady) { W("    跳过：驱动未就绪"); W(); return false; }

        if (!TryReadMsr(MSR_RAPL_POWER_UNIT, out uint eax, out _))
        {
            W("    !! 读不到 MSR_RAPL_POWER_UNIT(0x606)"); W(); return false;
        }
        int esu = (int)((eax >> 8) & 0x1F);
        double joulePerCount = 1.0 / (1 << esu);
        W($"    0x606 = 0x{eax:X8}  ESU={esu}  1 count = {joulePerCount:E4} J");

        if (!ReadEnergy(out ulong e1)) { W("    !! 读不到 PKG_ENERGY_STATUS"); W(); return false; }
        Thread.Sleep(1000);
        if (!ReadEnergy(out ulong e2)) { W("    !! 第二次读失败"); W(); return false; }

        ulong delta = e2 >= e1 ? e2 - e1 : (0x1_0000_0000UL - e1 + e2);
        double watts = delta * joulePerCount;
        W($"    1 秒计数差 {delta}  ->  封装功耗 ≈ {watts:F1} W");

        bool sane = watts is > 2 and < 300;
        W(sane ? "    判定 : RAPL 可用" : "    判定 : 数值不合理（RAPL 未启用或被锁）");
        W();
        return sane;

        bool ReadEnergy(out ulong v)
        {
            v = 0;
            if (!TryReadMsr(MSR_PKG_ENERGY_STATUS, out uint a, out uint d)) return false;
            v = ((ulong)d << 32) | a;
            return true;
        }
    }

    // ============================================================
    // D. APERF / MPERF 频率
    // ============================================================
    private static bool SectionD_MsrClock()
    {
        W("[D] MSR 频率");
        if (!_msrReady) { W("    跳过：驱动未就绪"); W(); return false; }

        // D1 MPERF 诊断
        W("    MPERF(0xE7) 连续 4 次（间隔 100ms）：");
        bool mperfMonotonic = true;
        ulong prev = 0;
        for (int i = 0; i < 4; i++)
        {
            TryReadMsr64(MSR_IA32_MPERF, out ulong v);
            if (i > 0 && v <= prev) mperfMonotonic = false;
            W($"      {v,20}  {(i > 0 ? (v > prev ? "+" + (v - prev) : "↓ 倒退") : "")}");
            prev = v;
            Thread.Sleep(100);
        }
        W(mperfMonotonic
            ? "      -> 单调递增，APERF/MPERF 差分可用"
            : "      -> 非单调（乱数），本平台不可用 APERF/MPERF 差分");

        // D2 IA32_PERF_STATUS 采样（期间制造负载，看哪个位段在动）
        bool spin = true;
        var t = new Thread(() =>
        {
            double x = 1;
            while (Volatile.Read(ref spin)) x += Math.Sqrt(x);
        }) { IsBackground = true };
        t.Start();

        var raws = new List<uint>();
        for (int i = 0; i < 6; i++)
        {
            if (TryReadMsr(MSR_IA32_PERF_STATUS, out uint ps, out _)) raws.Add(ps);
            Thread.Sleep(250);
        }
        Volatile.Write(ref spin, false);
        t.Join(500);

        if (raws.Count == 0) { W("    !! 读不到 0x198"); W(); return false; }

        W("    IA32_PERF_STATUS(0x198) 采样（负载中）：");
        foreach (var ps in raws)
            W($"      0x{ps:X8}   [15:0]={ps & 0xFFFF,6}   [15:8]={(ps >> 8) & 0xFF,4}");

        var r0 = raws.Select(p => (int)(p & 0xFFFF)).ToList();
        var r8 = raws.Select(p => (int)((p >> 8) & 0xFF)).ToList();
        bool ok0 = r0.All(v => v is >= 6 and <= 90);
        bool ok8 = r8.All(v => v is >= 6 and <= 90);

        int? ratio = null;
        string which = "";
        if (ok8 && !ok0) { ratio = r8.Max(); which = "[15:8]"; }
        else if (ok0 && !ok8) { ratio = r0.Max(); which = "[15:0]"; }
        else if (ok0 && ok8)
        {
            // 两个都在合理区间：取变化更大的那个（频率随负载变化）
            int span0 = r0.Max() - r0.Min(), span8 = r8.Max() - r8.Min();
            if (span8 >= span0) { ratio = r8.Max(); which = "[15:8]"; }
            else { ratio = r0.Max(); which = "[15:0]"; }
        }

        if (ratio is null)
        {
            W("    判定 : 两个位段都不合理，0x198 不可用 -> 频率应改用 PDH % Processor Performance × 基准频率");
            W();
            return false;
        }

        W($"    判定 : 倍频在 {which}，峰值 ratio={ratio}  ->  {ratio * 0.1:F2} GHz");
        W($"           与规格对照：i7-13650HX P 核基频 2.6 GHz / 睿频 4.9 GHz");
        W();
        return true;
    }

    // ============================================================
    // E. NVMe 温度
    // ============================================================
    private static bool SectionE_NvmeTemperature()
    {
        W("[E] NVMe 温度（IOCTL_STORAGE_QUERY_PROPERTY + Log Page 0x02）");
        bool anyOk = false;
        for (int drv = 0; drv < 8; drv++)
        {
            if (!DriveExists(drv)) continue;
            W($"  -- PhysicalDrive{drv}");

            bool drvOk = false;
            foreach (int headerOffset in new[] { 8, 12 })
            {
                string tag = $"header@{headerOffset}";
                if (TryNvmeSmartLog(drv, headerOffset, out byte[] data, out string err))
                {
                    int kelvin = data[1] | (data[2] << 8);
                    W($"     {tag}: 成功，{data.Length} 字节");
                    W($"       前 16 字节: {BitConverter.ToString(data, 0, Math.Min(16, data.Length))}");
                    W($"       温度 K={kelvin} -> {kelvin - 273.15:F1} °C");
                    W($"       严重告警位 0x{data[0]:X2}  剩余备用 {data[3]}%  备用阈值 {data[4]}%  已使用寿命 {data[5]}%");
                    anyOk = drvOk = true;
                    break;
                }
                W($"     {tag}: 失败 err={err}");
            }
            if (!drvOk) W("     该盘未能读取");
        }
        if (!anyOk) W("    !! 全部失败");
        W();
        return anyOk;
    }

    // ============================================================
    // F. 耗时与预算
    // ============================================================
    private static bool SectionF_Timing()
    {
        W("[F] 单次采集耗时与 T1 预算推算");
        if (!_msrReady) { W("    跳过：驱动未就绪"); W(); return false; }

        for (int i = 0; i < 200; i++) TryReadMsr(MSR_IA32_THERM_STATUS, out _, out _);

        double perMsrUs = TimeAvg(5000, () => TryReadMsr(MSR_IA32_THERM_STATUS, out _, out _));
        double perPkgUs = TimeAvg(2000, () => TryReadMsr(MSR_PACKAGE_THERM_STATUS, out _, out _));
        double perRaplUs = TimeAvg(2000, () => TryReadMsr(MSR_PKG_ENERGY_STATUS, out _, out _));

        W($"    单条 MSR 读         : {perMsrUs,9:F2} µs");
        W($"    封装温度读          : {perPkgUs,9:F2} µs");
        W($"    RAPL 能量读         : {perRaplUs,9:F2} µs");

        // 模拟 Cpu.Update()：51 条 MSR 连续读
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < 51; i++) TryReadMsr(MSR_IA32_THERM_STATUS, out _, out _);
        sw.Stop();
        double batch51 = sw.Elapsed.TotalMilliseconds;
        W($"    51 条连续读（模拟 Cpu.Update）: {batch51:F2} ms    ← M0 实测 LHM 为 218.10 ms");

        // 全核温度（含切核）
        int cores = Math.Min(Environment.ProcessorCount, 64);
        sw.Restart();
        for (int c = 0; c < cores; c++)
        {
            IntPtr prev = SetThreadAffinityMask(GetCurrentThread(), (IntPtr)(1L << c));
            TryReadMsr(MSR_IA32_THERM_STATUS, out _, out _);
            SetThreadAffinityMask(GetCurrentThread(), prev);
        }
        double allCoresMs = sw.Elapsed.TotalMilliseconds;
        double perCoreUs = allCoresMs * 1000.0 / cores;
        W($"    全核温度一轮（{cores} 核含切核）: {allCoresMs:F2} ms  = 每核 {perCoreUs:F0} µs");

        // 隔离：固定亲和后纯读，分离「切核」与「读寄存器」的成本
        IntPtr orig = SetThreadAffinityMask(GetCurrentThread(), (IntPtr)1);
        double pureCoreUs = TimeAvg(2000, () => TryReadMsr(MSR_IA32_THERM_STATUS, out _, out _));
        SetThreadAffinityMask(GetCurrentThread(), orig);
        W($"    固定亲和后的纯读         : {pureCoreUs,6:F2} µs  -> 单次切核开销 ≈ {perCoreUs - pureCoreUs:F0} µs");

        double pkgRaplMs = (perPkgUs + perRaplUs) / 1000.0;
        W($"    封装温度 + RAPL 一轮     : {pkgRaplMs:F3} ms");

        W();
        W("    预算推算（按 4.0 分级，1000 ms = 100% 单核）：");
        double c1 = 1000.0 / 200 * pkgRaplMs;
        double c2 = 1000.0 / 500 * allCoresMs;
        W($"      封装温度+RAPL  @ 200ms : 每秒 {c1,6:F2} ms -> {c1 / 10,5:F3}% 单核");
        W($"      全核温度       @ 500ms : 每秒 {c2,6:F2} ms -> {c2 / 10,5:F3}% 单核");
        W($"      小计                   : {(c1 + c2) / 10,5:F3}% 单核");
        double bad = 1000.0 / 200 * (allCoresMs + pkgRaplMs);
        W($"      若全核温度也跟 200ms   : {bad / 10,5:F2}% 单核   ← 不推荐");

        double nvmeUs = TimeAvg(10, () => TryNvmeSmartLog(0, 8, out _, out _));
        double nvmeMs = nvmeUs / 1000.0;
        W();
        W($"    NVMe 温度读（0 号盘）    : {nvmeMs:F2} ms / 次");
        W($"      @ 2000ms（2 块盘）    : 每秒 {1000.0 / 2000 * nvmeMs * 2,5:F2} ms -> {1000.0 / 2000 * nvmeMs * 2 / 10:F3}% 单核");
        W($"      @ 5000ms（2 块盘）    : 每秒 {1000.0 / 5000 * nvmeMs * 2,5:F2} ms -> {1000.0 / 5000 * nvmeMs * 2 / 10:F3}% 单核");

        W();
        return true;
    }

    private static double TimeAvg(int n, Action act)
    {
        var sw = Stopwatch.StartNew();
        for (int i = 0; i < n; i++) act();
        sw.Stop();
        return sw.Elapsed.TotalMilliseconds * 1000.0 / n;
    }

    // ============================================================
    // 基础设施
    // ============================================================
    private const uint IOCTL_STORAGE_QUERY_PROPERTY = 0x002D1400;
    private const int PROP_STORAGE_DEVICE_PROTOCOL_SPECIFIC = 50;
    private const int QUERY_STANDARD = 0;
    private const int PROTOCOL_TYPE_NVME = 3;
    private const int NVME_DATA_TYPE_LOG_PAGE = 2;
    private const int NVME_LOG_SMART_HEALTH = 0x02;

    private static bool TryReadMsr(uint msr, out uint eax, out uint edx)
    {
        eax = edx = 0;
        if (_msr == null) return false;
        if (!_msr.ReadMsr(msr, out ulong v)) return false;
        edx = (uint)(v >> 32);
        eax = (uint)(v & 0xFFFF_FFFF);
        return true;
    }

    private static bool TryReadMsr64(uint msr, out ulong value)
    {
        value = 0;
        return _msr != null && _msr.ReadMsr(msr, out value);
    }

    private static bool TryNvmeSmartLog(int drv, int headerOffset, out byte[] data, out string error)
    {
        data = Array.Empty<byte>();
        error = "";

        const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
        const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, OPEN_EXISTING = 3;

        using SafeFileHandle h = CreateFileW($@"\\.\PhysicalDrive{drv}",
            GENERIC_READ | GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE,
            IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (h.IsInvalid) { error = $"CreateFile={Marshal.GetLastWin32Error()}"; return false; }

        const int dataLen = 512, protoSize = 40;
        int total = headerOffset + protoSize + dataLen;
        IntPtr inBuf = Marshal.AllocHGlobal(total);
        IntPtr outBuf = Marshal.AllocHGlobal(total + 128);
        try
        {
            for (int i = 0; i < total; i++) Marshal.WriteByte(inBuf, i, 0);
            for (int i = 0; i < total + 128; i++) Marshal.WriteByte(outBuf, i, 0);

            Marshal.WriteInt32(inBuf, 0, PROP_STORAGE_DEVICE_PROTOCOL_SPECIFIC);
            Marshal.WriteInt32(inBuf, 4, QUERY_STANDARD);

            int p = headerOffset;
            Marshal.WriteInt32(inBuf, p + 0, PROTOCOL_TYPE_NVME);
            Marshal.WriteInt32(inBuf, p + 4, NVME_DATA_TYPE_LOG_PAGE);
            Marshal.WriteInt32(inBuf, p + 8, NVME_LOG_SMART_HEALTH);
            Marshal.WriteInt32(inBuf, p + 16, protoSize);
            Marshal.WriteInt32(inBuf, p + 20, dataLen);

            if (!DeviceIoControl(h, IOCTL_STORAGE_QUERY_PROPERTY, inBuf, (uint)total,
                    outBuf, (uint)(total + 128), out _, IntPtr.Zero))
            {
                error = Marshal.GetLastWin32Error().ToString();
                return false;
            }

            int retOff = Marshal.ReadInt32(outBuf, headerOffset + 16);
            int retLen = Marshal.ReadInt32(outBuf, headerOffset + 20);
            int start = headerOffset + (retOff > 0 ? retOff : protoSize);
            int len = retLen is > 0 and <= 4096 ? retLen : dataLen;

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

    private static bool DriveExists(int drv)
    {
        using SafeFileHandle h = CreateFileW($@"\\.\PhysicalDrive{drv}", 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
        return !h.IsInvalid;
    }

    private static Type[] SafeGetTypes(Assembly asm)
    {
        try { return asm.GetTypes(); }
        catch (ReflectionTypeLoadException ex) { return ex.Types.Where(t => t != null).Cast<Type>().ToArray(); }
    }

    private static bool IsAdmin()
    {
        using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(id)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static void W(string s = "")
    {
        Console.WriteLine(s);
        Report.AppendLine(s);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
        IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes, IntPtr hTemplateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(SafeFileHandle hDevice, uint dwIoControlCode,
        IntPtr lpInBuffer, uint nInBufferSize, IntPtr lpOutBuffer, uint nOutBufferSize,
        out uint lpBytesReturned, IntPtr lpOverlapped);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr SetThreadAffinityMask(IntPtr hThread, IntPtr dwThreadAffinityMask);
}
