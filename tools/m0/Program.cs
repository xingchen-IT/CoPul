using System.Diagnostics;
using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using LibreHardwareMonitor.Hardware;

namespace CoPul.M0;

/// <summary>
/// M0 技术验证：确认本机传感器可读性 + 各硬件 Update() 的实际耗时。
/// 目的不是做一个好用的工具，而是回答需求文档第 4.0 节的两个问题：
///   1) 哪些指标在这台机器上真的读得到；
///   2) 每次采集到底花多少毫秒，用来给 T0/T1/T2 分级标定 floor。
/// </summary>
internal static class Program
{
    private const int TimingIterations = 20;
    private static readonly StringBuilder Report = new();

    private static void W(string line = "")
    {
        try { Console.WriteLine(line); } catch { /* 无控制台时忽略 */ }
        Report.AppendLine(line);
    }

    private static int Main(string[] args)
    {
        try { Console.OutputEncoding = Encoding.UTF8; } catch { }

        string reportPath = args.Length > 0 ? args[0] : "m0-report.txt";

        W("================================================================");
        W("  CoPul M0 传感器能力验证");
        W("================================================================");
        W($"时间        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        W($"操作系统    : {Environment.OSVersion.VersionString}");
        W($"运行时      : {RuntimeInformation.FrameworkDescription}  ({(Environment.Is64BitProcess ? "x64" : "x86")})");
        W($"管理员权限  : {(IsAdmin() ? "是" : "否  <-- 温度/功耗类大概率读不到")}");
        W($"CPU 逻辑核  : {Environment.ProcessorCount}");
        W();

        Computer? computer = null;
        try
        {
            computer = new Computer
            {
                IsCpuEnabled = true,
                IsGpuEnabled = true,
                IsMemoryEnabled = true,
                IsMotherboardEnabled = true,
                IsStorageEnabled = true,
                IsControllerEnabled = false,
                IsNetworkEnabled = false
            };

            var sw = Stopwatch.StartNew();
            computer.Open();
            sw.Stop();
            W($"[OK]   Computer.Open() 成功，耗时 {sw.Elapsed.TotalMilliseconds:F1} ms");
        }
        catch (Exception ex)
        {
            W($"[FAIL] Computer.Open() 抛出 {ex.GetType().Name}: {ex.Message}");
        }

        if (computer != null)
        {
            ReportHardwareTree(computer);
            ReportUpdateTimings(computer);
            ReportSensorInventory(computer);
            ReportMetricCoverage(computer);
            ReportMemory();
            ReportNetwork();
            ReportNvmlDirect();
        }

        ReportConclusion(computer);

        try
        {
            File.WriteAllText(reportPath, Report.ToString(), new UTF8Encoding(true));
            W();
            W($"报告已写入: {Path.GetFullPath(reportPath)}");
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"写报告失败: {ex.Message}");
            return 2;
        }

        return 0;
    }

    // ---------------- 硬件树 ----------------

    private static void ReportHardwareTree(Computer computer)
    {
        W();
        W("---------------- 1. 硬件树 ----------------");
        foreach (var hw in computer.Hardware)
            DumpTree(hw, 0);
    }

    private static void DumpTree(IHardware hw, int depth)
    {
        W($"{new string(' ', depth * 2)}- [{depth}] {hw.HardwareType}  {hw.Name}   (id={hw.Identifier})");
        foreach (var sub in hw.SubHardware)
            DumpTree(sub, depth + 1);
    }

    // ---------------- Update 耗时 ----------------

    private static void ReportUpdateTimings(Computer computer)
    {
        W();
        W($"---------------- 2. 各硬件 Update() 耗时（{TimingIterations} 次） ----------------");
        W("这一列是 4.0 分级的关键数据：LHM 的 Update 以硬件为单位，无法只更新单个传感器。");
        W();

        foreach (var hw in Flatten(computer.Hardware))
        {
            double first;
            var samples = new List<double>(TimingIterations);

            var swFirst = Stopwatch.StartNew();
            try { hw.Update(); }
            catch (Exception ex) { W($"{hw.Name} | Update 抛出 {ex.GetType().Name}: {ex.Message}"); continue; }
            swFirst.Stop();
            first = swFirst.Elapsed.TotalMilliseconds;

            for (int i = 0; i < TimingIterations - 1; i++)
            {
                var sw = Stopwatch.StartNew();
                try { hw.Update(); }
                catch { break; }
                sw.Stop();
                samples.Add(sw.Elapsed.TotalMilliseconds);
            }

            if (samples.Count == 0)
            {
                W($"{hw.Name} | 首次 {first:F1} ms | 稳态无样本");
                continue;
            }

            samples.Sort();
            double median = samples[samples.Count / 2];
            double avg = samples.Average();

            string verdict = median < 1 ? "T0 可承受 20Hz"
                           : median < 10 ? "T1 建议 >=500ms"
                           : "T2 建议 >=1000ms";

            W($"{hw.HardwareType,-12} {Truncate(hw.Name, 26),-28} 首次 {first,7:F2} ms | 最小 {samples[0],6:F2} | 中位 {median,6:F2} | 平均 {avg,6:F2} | 最大 {samples[^1],7:F2}   => {verdict}");
        }
    }

    // ---------------- 传感器清单 ----------------

    private static void ReportSensorInventory(Computer computer)
    {
        W();
        W("---------------- 3. 传感器清单（现状快照） ----------------");

        foreach (var hw in Flatten(computer.Hardware))
        {
            W();
            W($"[{hw.HardwareType}] {hw.Name}  传感器数 {hw.Sensors.Length}");
            if (hw.Sensors.Length == 0)
            {
                W("    (无)");
                continue;
            }

            foreach (var s in hw.Sensors.OrderBy(s => s.SensorType).ThenBy(s => s.Index))
            {
                string val = s.Value.HasValue ? $"{s.Value.Value,9:F2}" : "     null";
                W($"    {s.SensorType,-12} {Truncate(s.Name, 32),-34} {val} {UnitOf(s.SensorType)}");
            }
        }
    }

    // ---------------- 指标覆盖核对（对照文档 4.1-4.7） ----------------

    private static void ReportMetricCoverage(Computer computer)
    {
        W();
        W("---------------- 4. 指标覆盖核对（对照需求文档 ID） ----------------");
        W("格式: ID  指标名  ->  值  (来源传感器 / 缺失原因)");
        W();

        var cpu = computer.Hardware.FirstOrDefault(h => h.HardwareType == HardwareType.Cpu);
        var gpus = computer.Hardware.Where(h => h.HardwareType
            is HardwareType.GpuNvidia or HardwareType.GpuAmd or HardwareType.GpuIntel).ToList();
        var mem = computer.Hardware
            .Where(h => h.HardwareType == HardwareType.Memory)
            .FirstOrDefault(h => !h.Name.Contains("Virtual", StringComparison.OrdinalIgnoreCase));
        var storages = computer.Hardware.Where(h => h.HardwareType == HardwareType.Storage).ToList();
        var boards = computer.Hardware.Where(h => h.HardwareType == HardwareType.Motherboard).ToList();

        W($"  C01 CPU 型号         ->  {cpu?.Name ?? "(无 CPU 硬件)"}   (来源 Hardware.Name)");
        Check("C02", "CPU 总占用率", cpu, SensorType.Load, "CPU Total", "Total");
        Check("C04", "CPU 封装温度", cpu, SensorType.Temperature, "Package", "Core (Tctl", "Core Average");
        Check("C05", "CPU 核心温度", cpu, SensorType.Temperature, "Core #");
        Check("C06", "CPU 当前频率", cpu, SensorType.Clock, "Core");
        Check("C07", "CPU 封装功耗", cpu, SensorType.Power, "Package");
        Check("C09", "CPU 风扇转速", cpu, SensorType.Fan, "Fan");

        foreach (var g in gpus)
        {
            W($"  -- GPU: {g.Name}");
            CheckOn(g, "G02", "GPU 占用率", SensorType.Load, "GPU Core");

            // G03/G04 显存：必须精确匹配，否则会把 GPU Memory Controller / Memory Free 误当显存
            var mUsed = g.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name == "GPU Memory Used");
            var mTotal = g.Sensors.FirstOrDefault(s => s.SensorType == SensorType.SmallData && s.Name == "GPU Memory Total");
            if (mUsed?.Value is float used && mTotal?.Value is float total && total > 0)
                W($"  {"G03",-4} {"显存占用率",-16} ->  {used / total * 100,-16:F2} %          (由 G04 计算)");
            else
                W($"  {"G03",-4} {"显存占用率",-16} ->  —              (显存传感器缺失)");

            if (mUsed?.Value is float u2 && mTotal?.Value is float t2)
                W($"  {"G04",-4} {"显存使用/总量",-16} ->  {u2:F0} / {t2:F0} MB      (GPU Memory Used / Total)");
            else
                W($"  {"G04",-4} {"显存使用/总量",-16} ->  —              (显存传感器缺失)");

            CheckOn(g, "G05", "GPU 温度", SensorType.Temperature, "GPU Core");
            CheckOn(g, "G06", "GPU 核心频率", SensorType.Clock, "GPU Core");
            CheckOn(g, "G07", "GPU 显存频率", SensorType.Clock, "GPU Memory");
            CheckOn(g, "G08", "GPU 功耗", SensorType.Power, "GPU Package");
            CheckOn(g, "G10", "GPU 风扇", SensorType.Control, "GPU Fan");
        }

        Check("M01", "内存占用率", mem, SensorType.Load, "Memory");
        Check("M02", "内存使用/总量", mem, SensorType.Data, "Memory");
        Check("M05", "提交量", null, SensorType.Data, "");   // 预期走 P/Invoke，不在此表验证

        if (storages.Count == 0)
            W("  -- 存储: 未枚举到任何 Storage 硬件（LHM 访问 SMART 需要管理员权限 + 内核驱动）");

        for (int i = 0; i < storages.Count; i++)
        {
            var d = storages[i];
            W($"  -- 存储 #{i + 1}: {d.Name}");
            CheckOn(d, "D02", "硬盘温度", SensorType.Temperature, "Temperature", "Drive");
            CheckOn(d, "D03", "读写速率", SensorType.Throughput, "Read", "Write");
            CheckOn(d, "D05", "磁盘活动", SensorType.Load, "Used Space", "Total Activity");
        }

        foreach (var b in boards)
        {
            var subs = Flatten(new[] { b }).Where(h => h.HardwareType
                is HardwareType.SuperIO or HardwareType.EmbeddedController).ToList();
            W($"  -- 主板: {b.Name}  (子硬件 {subs.Count} 个)");
            foreach (var si in subs)
                CheckOn(si, "C09", $"风扇 [{si.Name}]", SensorType.Fan, "Fan");
        }

        void Check(string id, string name, IHardware? hw, SensorType type, params string[] keys)
            => CheckOn(hw, id, name, type, keys);

        void CheckOn(IHardware? hw, string id, string name, SensorType type, params string[] keys)
        {
            if (hw == null)
            {
                W($"  {id,-4} {name,-16} ->  —              (无对应硬件)");
                return;
            }

            var hit = hw.Sensors.FirstOrDefault(s =>
                    s.SensorType == type && keys.Length > 0 &&
                    s.Name.Equals(keys[0], StringComparison.OrdinalIgnoreCase))
                ?? hw.Sensors.FirstOrDefault(s =>
                    s.SensorType == type &&
                    (keys.Length == 0 || keys.Any(k => s.Name.Contains(k, StringComparison.OrdinalIgnoreCase))));

            if (hit == null)
            {
                W($"  {id,-4} {name,-16} ->  —              (缺 {type} 传感器)");
                return;
            }

            if (!hit.Value.HasValue)
            {
                W($"  {id,-4} {name,-16} ->  null           ({hit.Name} 存在但无值)");
                return;
            }

            string v = $"{hit.Value.Value:F2} {UnitOf(hit.SensorType)}";
            W($"  {id,-4} {name,-16} ->  {v,-16} ({hit.Name})");
        }
    }

    // ---------------- 内存 ----------------

    private static void ReportMemory()
    {
        W();
        W("---------------- 5. 内存（P/Invoke GlobalMemoryStatusEx） ----------------");

        var ms = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        var sw = Stopwatch.StartNew();
        bool ok = GlobalMemoryStatusEx(ref ms);
        sw.Stop();

        if (!ok)
        {
            W($"  [FAIL] GlobalMemoryStatusEx 失败，错误码 {Marshal.GetLastWin32Error()}");
            return;
        }

        double totalGB = ms.ullTotalPhys / 1024.0 / 1024 / 1024;
        double availGB = ms.ullAvailPhys / 1024.0 / 1024 / 1024;
        double usedGB = totalGB - availGB;

        W($"  M01 占用率      : {ms.dwMemoryLoad} %");
        W($"  M02 使用/总量   : {usedGB:F1} / {totalGB:F1} GB");
        W($"  M03 可用        : {availGB:F1} GB");
        W($"  M06 页面文件    : {(ms.ullTotalPageFile - ms.ullAvailPageFile) / 1024.0 / 1024 / 1024:F1} / {ms.ullTotalPageFile / 1024.0 / 1024 / 1024:F1} GB");
        W($"  单次耗时        : {sw.Elapsed.TotalMilliseconds:F3} ms  => 可归入 T0");
    }

    // ---------------- 网络 ----------------

    private static void ReportNetwork()
    {
        W();
        W("---------------- 6. 网络（计数器差分 + ICMP） ----------------");

        var nics = NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .Where(n => n.OperationalStatus == OperationalStatus.Up)
            .ToList();

        W($"  活动网卡 {nics.Count} 个:");
        foreach (var n in nics)
        {
            var st = n.GetIPStatistics();
            W($"    {n.Name,-30} type={n.NetworkInterfaceType,-16} 收 {st.BytesReceived / 1024.0 / 1024:F1} MB  发 {st.BytesSent / 1024.0 / 1024:F1} MB");
        }

        if (nics.Count > 0)
        {
            var pick = nics.OrderByDescending(n => n.GetIPStatistics().BytesReceived).First();
            var a = pick.GetIPStatistics();
            Thread.Sleep(1000);
            var b = pick.GetIPStatistics();

            double downKB = (b.BytesReceived - a.BytesReceived) / 1024.0;
            double upKB = (b.BytesSent - a.BytesSent) / 1024.0;

            var sw = Stopwatch.StartNew();
            _ = pick.GetIPStatistics();
            sw.Stop();

            W();
            W($"  N01 上行速率    : {upKB:F1} KB/s   (采样 {pick.Name})");
            W($"  N02 下行速率    : {downKB:F1} KB/s");
            W($"  计数器单次耗时  : {sw.Elapsed.TotalMilliseconds:F3} ms  => 可归入 T0");
        }

        // 延迟：对比 ICMP 与 TCP 两种路径
        W();
        foreach (var target in new[] { "223.5.5.5", "8.8.8.8" })
        {
            try
            {
                using var ping = new Ping();
                var sw = Stopwatch.StartNew();
                var reply = ping.Send(target, 1000);
                sw.Stop();

                string rtt = reply.Status == IPStatus.Success ? $"{reply.RoundtripTime} ms" : reply.Status.ToString();
                W($"  N03 延迟 {target,-12} : {rtt,-16} 单次阻塞 {sw.Elapsed.TotalMilliseconds:F1} ms");
            }
            catch (Exception ex)
            {
                W($"  N03 延迟 {target,-12} : 失败 {ex.GetType().Name}");
            }
        }

        W("  注意：ICMP 单次耗时包含网络往返，属阻塞调用，必须异步化。");
    }

    // ---------------- NVML 直连对照 ----------------
    // 目的：验证 LHM 的 NVIDIA Update() 耗时是「硬件本身慢」还是「LHM 每传感器一次 NVAPI 调用的开销」。
    // 若是后者，正式实现应绕过 LHM 直接用 NVML。

    private static void ReportNvmlDirect()
    {
        W();
        W("---------------- 7. NVML 直连对照（绕过 LHM） ----------------");

        try
        {
            int rc = nvmlInit_v2();
            if (rc != 0) { W($"  nvmlInit 失败, rc={rc}"); return; }

            try
            {
                if (nvmlDeviceGetCount_v2(out uint count) != 0 || count == 0)
                {
                    W("  未发现 NVML 设备");
                    return;
                }

                W($"  NVML 设备数: {count}");
                if (nvmlDeviceGetHandleByIndex_v2(0, out IntPtr dev) != 0) { W("  取句柄失败"); return; }

                var nameBuf = new StringBuilder(96);
                if (nvmlDeviceGetName(dev, nameBuf, (uint)nameBuf.Capacity) == 0)
                    W($"  设备 0: {nameBuf}");

                // 单次「四项全读」耗时
                var samples = new List<double>(60);
                for (int i = 0; i < 60; i++)
                {
                    var sw = Stopwatch.StartNew();
                    nvmlDeviceGetUtilizationRates(dev, out _);
                    nvmlDeviceGetTemperature(dev, 0, out _);
                    nvmlDeviceGetMemoryInfo(dev, out _);
                    nvmlDeviceGetPowerUsage(dev, out _);
                    sw.Stop();
                    if (i >= 5) samples.Add(sw.Elapsed.TotalMilliseconds);   // 丢弃预热
                }

                samples.Sort();
                W($"  单次四项全读: 中位 {samples[samples.Count / 2]:F3} ms / 最小 {samples[0]:F3} / 最大 {samples[^1]:F3}  (n={samples.Count})");
                W($"  折算: 20Hz 需 ≤50ms，实测中位 {samples[samples.Count / 2]:F3} ms => {(samples[samples.Count / 2] < 50 ? "可承受 20Hz" : "不可承受 20Hz")}");

                if (nvmlDeviceGetUtilizationRates(dev, out var util) == 0 &&
                    nvmlDeviceGetMemoryInfo(dev, out var mem) == 0 &&
                    nvmlDeviceGetTemperature(dev, 0, out uint temp) == 0 &&
                    nvmlDeviceGetPowerUsage(dev, out uint mw) == 0)
                {
                    W($"  样本值: 占用 {util.gpu}% / 显存 {mem.used / 1024.0 / 1024:F0} of {mem.total / 1024.0 / 1024:F0} MB / 温度 {temp}°C / 功耗 {mw / 1000.0:F1} W");
                }
            }
            finally
            {
                nvmlShutdown();
            }
        }
        catch (DllNotFoundException)
        {
            W("  未找到 nvml.dll（未安装 NVIDIA 驱动或不在搜索路径），跳过。");
        }
        catch (Exception ex)
        {
            W($"  NVML 调用异常: {ex.GetType().Name}: {ex.Message}");
        }
    }

    // ---------------- 结论 ----------------

    private static void ReportConclusion(Computer? computer)
    {
        W();
        W("---------------- 8. 结论 ----------------");

        if (computer == null)
        {
            W("  Computer 未初始化，无法给出结论。");
            return;
        }

        var (cpuTempAll, cpuTempVal, cpuTempEff) = CountSensors(computer, HardwareType.Cpu, SensorType.Temperature);
        var (cpuPwrAll, cpuPwrVal, cpuPwrEff) = CountSensors(computer, HardwareType.Cpu, SensorType.Power);
        var gpuTypes = new[] { HardwareType.GpuNvidia, HardwareType.GpuAmd };
        var (gpuTempAll, gpuTempVal, gpuTempEff) = CountSensors(computer, gpuTypes, SensorType.Temperature);
        var (gpuPwrAll, gpuPwrVal, gpuPwrEff) = CountSensors(computer, gpuTypes, SensorType.Power);
        var (diskAll, diskVal, diskEff) = CountSensors(computer, HardwareType.Storage, SensorType.Temperature);
        bool storagePresent = computer.Hardware.Any(h => h.HardwareType == HardwareType.Storage);

        W("  判定口径：传感器存在 != 可用。温度/功耗/风扇/频率读数为 0 也视为无效（驱动未生效的典型表现）。");
        W();
        Row("CPU 温度", cpuTempAll, cpuTempVal, cpuTempEff);
        Row("CPU 功耗", cpuPwrAll, cpuPwrVal, cpuPwrEff);
        Row("GPU 温度", gpuTempAll, gpuTempVal, gpuTempEff);
        Row("GPU 功耗", gpuPwrAll, gpuPwrVal, gpuPwrEff);
        W($"  硬盘温度   : 存在 {diskAll,3} / 有值 {diskVal,3} / 有效 {diskEff,3}   {Verdict(diskAll, diskEff)}");
        W($"  存储枚举   : {(storagePresent ? "有 Storage 硬件" : "** 完全为空（需管理员 + 内核驱动）**")}");

        var (fanAll, fanVal, fanEff) = CountSensors(computer, new[] { HardwareType.Motherboard, HardwareType.Cpu }, SensorType.Fan);
        W($"  风扇转速   : 存在 {fanAll,3} / 有值 {fanVal,3} / 有效 {fanEff,3}   {Verdict(fanAll, fanEff)}");
        W();

        if (!IsAdmin())
            W("  提示：本次为非管理员运行。上述 null / 0 值不能代表硬件不支持，必须提权重跑后再下结论。");

        W("  未验证项：FPS（需 PresentMon，本次未引入）、整机功耗（无传感器，只能建模估算）。");
    }

    private static void Row(string label, int total, int withValue, int effective)
        => W($"  {label}   : 存在 {total,3} / 有值 {withValue,3} / 有效 {effective,3}   {Verdict(total, effective)}");

    private static string Verdict(int total, int effective)
    {
        if (total == 0) return "=> 传感器缺失";
        if (effective == 0) return "=> 存在但全为 null/0，驱动未生效";
        if (effective < total) return "=> 部分可用";
        return "=> 可用";
    }

    // ---------------- 工具 ----------------

    private static bool IsAdmin()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            return new WindowsPrincipal(identity).IsInRole(WindowsBuiltInRole.Administrator);
        }
        catch { return false; }
    }

    private static IEnumerable<IHardware> Flatten(IEnumerable<IHardware> roots)
    {
        foreach (var hw in roots)
        {
            yield return hw;
            foreach (var sub in Flatten(hw.SubHardware))
                yield return sub;
        }
    }

    private static (int Total, int WithValue, int Effective) CountSensors(Computer computer, HardwareType type, SensorType sensorType)
        => CountSensors(computer, new[] { type }, sensorType);

    private static (int Total, int WithValue, int Effective) CountSensors(Computer computer, HardwareType[] types, SensorType sensorType)
    {
        int total = 0, withValue = 0, effective = 0;
        foreach (var hw in computer.Hardware.Where(h => types.Contains(h.HardwareType)))
        {
            var (t, v, e) = CountSensorsDeep(hw, sensorType);
            total += t; withValue += v; effective += e;
        }
        return (total, withValue, effective);
    }

    private static (int Total, int WithValue, int Effective) CountSensorsDeep(IHardware hw, SensorType type)
    {
        int total = 0, withValue = 0, effective = 0;
        foreach (var s in hw.Sensors)
        {
            if (s.SensorType != type) continue;
            total++;
            if (!s.Value.HasValue) continue;
            withValue++;
            if (IsMeaningful(type, s.Value.Value)) effective++;
        }
        foreach (var sub in hw.SubHardware)
        {
            var (t, v, e) = CountSensorsDeep(sub, type);
            total += t; withValue += v; effective += e;
        }
        return (total, withValue, effective);
    }

    /// <summary>温度/功耗/风扇/频率/电压 读数为 0 时视为无效读数（驱动未生效的典型表现）。</summary>
    private static bool IsMeaningful(SensorType type, float v) => type switch
    {
        SensorType.Temperature or SensorType.Power or SensorType.Fan
            or SensorType.Clock or SensorType.Voltage or SensorType.Current => v != 0f,
        _ => true
    };

    private static int SumFans(IHardware hw) => hw.Sensors.Count(s => s.SensorType == SensorType.Fan);

    private static string Truncate(string s, int max)
        => s.Length <= max ? s : s[..(max - 1)] + "…";

    private static string UnitOf(SensorType t) => t switch
    {
        SensorType.Temperature => "°C",
        SensorType.Load => "%",
        SensorType.Control => "%",
        SensorType.Level => "%",
        SensorType.Clock => "MHz",
        SensorType.Frequency => "Hz",
        SensorType.Power => "W",
        SensorType.Voltage => "V",
        SensorType.Current => "A",
        SensorType.Fan => "RPM",
        SensorType.SmallData => "MB",
        SensorType.Data => "GB",
        SensorType.Throughput => "MB/s",
        _ => ""
    };

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlUtilization { public uint gpu; public uint memory; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NvmlMemory { public ulong total; public ulong free; public ulong used; }

    [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
    private static extern int nvmlInit_v2();

    [DllImport("nvml.dll", EntryPoint = "nvmlShutdown")]
    private static extern int nvmlShutdown();

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetCount_v2")]
    private static extern int nvmlDeviceGetCount_v2(out uint count);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
    private static extern int nvmlDeviceGetHandleByIndex_v2(uint index, out IntPtr device);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
    private static extern int nvmlDeviceGetName(IntPtr device, StringBuilder name, uint length);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
    private static extern int nvmlDeviceGetUtilizationRates(IntPtr device, out NvmlUtilization utilization);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
    private static extern int nvmlDeviceGetTemperature(IntPtr device, uint sensorType, out uint temp);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
    private static extern int nvmlDeviceGetMemoryInfo(IntPtr device, out NvmlMemory memory);

    [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetPowerUsage")]
    private static extern int nvmlDeviceGetPowerUsage(IntPtr device, out uint milliwatts);

    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
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
}
