namespace CoPul.Collector.Metrics;

/// <summary>
/// 指标 ID。命名对齐需求文档第 4 章「指标字典」的编号：
/// C = CPU，G = GPU，M = 内存，D = 磁盘，N = 网络，P = 功耗。
/// 文档编号写在注释里，代码里用语义名，避免后续按 C04 这种编号去猜含义。
/// </summary>
public enum MetricId
{
    // ---------- CPU ----------
    CpuName,               // C01  启动一次（SystemInfo，见该类型）
    CpuTotalLoad,          // C02  % Processor Utility
    CpuCoreLoad,           // C03  数组，每逻辑核占用率（M1.0 补）
    CpuPackageTemp,        // C04
    CpuCoreTemp,           // C05  数组
    CpuClockGhz,           // C06
    CpuPackagePowerW,      // C07
    CpuCoreVoltage,        // C08  v1 默认关闭
    CpuFanRpm,             // C09  v1 默认关闭
    CpuLoadAvg1,           // C10  负载均值 1 分钟（M1.0 补）
    CpuLoadAvg5,           // C10  5 分钟
    CpuLoadAvg15,          // C10  15 分钟

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
    RamCachedMb,           // M04  已缓存（GetPerformanceInfo.SystemCache）
    RamCommitUsedMb,       // M05  提交量（Commit）
    RamCommitLimitMb,      // M05  提交上限（物理 + 页面文件）
    RamPageFileMb,         // M06  页面文件使用 = 提交量 − 物理总量（推算，见 OsProvider）
    // 注：M07（内存频率/通道）走 SPD，需 LuMi 管理员通路，v1 不采（4.3 标"隐藏"）

    // ---------- 磁盘 ----------
    DiskTemperature,       // D02  数组，每盘一个
    DiskReadBytesPerSec,   // D03  合计（PhysicalDisk(_Total)）
    DiskWriteBytesPerSec,  // D03  合计
    DiskReadPerDisk,       // D03  数组，下标 == PhysicalDriveN 的 N（与 DiskTemperature 对齐）
    DiskWritePerDisk,      // D03  数组，同上
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
