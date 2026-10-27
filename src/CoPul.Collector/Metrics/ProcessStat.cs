namespace CoPul.Collector.Metrics;

/// <summary>
/// 进程表里的一行。
///
/// 为什么不在 <see cref="MetricSnapshot"/> 里：快照只装 double 标量 / 数组，
/// 而进程行是「pid + 名字 + 三项数值」的复合结构，硬塞进去要拆成 5 组平行数组，
/// 排序时还得保证下标一致。参考 N05 网卡名的做法，走 Provider 属性旁路（A-22 同款约束）。
/// </summary>
public sealed class ProcessStat
{
    public ProcessStat(int pid, string name, double cpuPct, double gpuPct, double memMb)
    {
        Pid = pid;
        Name = name;
        CpuPct = cpuPct;
        GpuPct = gpuPct;
        MemMb = memMb;
    }

    public int Pid { get; }

    /// <summary>进程映像名，已去掉路径。空表示系统空闲进程（PID 0）。</summary>
    public string Name { get; }

    /// <summary>CPU 占用率（%），已按逻辑核数归一化 —— 与任务管理器同一口径。NaN 表示本帧算不出。</summary>
    public double CpuPct { get; }

    /// <summary>GPU 占用率（%）。NaN 表示这台机器拿不到每进程 GPU 数据。</summary>
    public double GpuPct { get; }

    /// <summary>私有工作集（MB）。</summary>
    public double MemMb { get; }
}
