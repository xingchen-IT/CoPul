namespace CoPul.Collector.Metrics;

/// <summary>
/// 指标 ID。命名对齐需求文档第 4 章「指标字典」的编号：
/// C = CPU，G = GPU，M = 内存，D = 磁盘，N = 网络，P = 功耗。
/// 文档编号写在注释里，代码里用语义名，避免后续按 C04 这种编号去猜含义。
/// </summary>
public enum MetricId
{
    // ---------- CPU ----------
    CpuName,               // C01
    CpuTotalLoad,          // C02  % Processor Utility
    CpuPackageTemp,        // C04
    CpuCoreTemp,           // C05  数组
    CpuClockGhz,           // C06
    CpuPackagePowerW,      // C07
    CpuCoreVoltage,        // C08  v1 默认关闭
    CpuFanRpm,             // C09  v1 默认关闭

    // ---------- GPU ----------
    GpuName,               // G01
    GpuLoad,               // G02
    GpuMemLoad,            // G03
    GpuMemUsedMb,          // G04
    GpuMemTotalMb,         // G04
    GpuTemp,               // G05
    GpuClockCoreMhz,       // G06
    GpuClockMemMhz,        // G07
    GpuPowerW,             // G08
    GpuFanPct,             // G10
    GpuEncoderLoad,        // G11
    GpuDecoderLoad,        // G11

    // ---------- 内存 ----------
    RamLoad,               // M01
    RamUsedMb,             // M02
    RamTotalMb,            // M02
    RamAvailMb,            // M03
    RamSwapUsedMb,         // M04

    // ---------- 磁盘 ----------
    DiskTemperature,       // D02  数组，每盘一个
    DiskReadBytesPerSec,   // D03
    DiskWriteBytesPerSec,  // D03
    DiskBusyPercent,       // D05

    // ---------- 网络 ----------
    NetUpBytesPerSec,      // N01
    NetDownBytesPerSec,    // N02
    NetLatencyMs,          // N03
    NetPacketLossPercent,  // N04
    NetAdapterName,        // N05  非数值，采集层保留字符串

    // ---------- 功耗 ----------
    PowerTotalEstimatedW   // P03
}
