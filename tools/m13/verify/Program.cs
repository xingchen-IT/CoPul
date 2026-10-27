using System.Diagnostics;
using CoPul.Collector.Metrics;
using CoPul.Collector.Providers;

namespace CoPul.M13Verify;

/// <summary>
/// 进程区块验收：直接驱动 ProcessProvider 跑两个采样周期，把结果打成文本。
/// 口径要与任务管理器一致（已除以逻辑核数），对照 PDH 的
/// <c>\Process(*)\% Processor Time</c> ÷ 核数 验证。
/// </summary>
internal static class Program
{
    private static int Main()
    {
        Console.WriteLine($"逻辑核 = {Environment.ProcessorCount}");
        using var provider = new ProcessProvider();

        if (!provider.Initialize())
        {
            Console.WriteLine($"初始化失败：{provider.UnavailableReason}");
            return 1;
        }

        Console.WriteLine($"初始化 {provider.InitializeMs:F1} ms   每进程 GPU = {(provider.GpuAvailable ? "可用" : "不可用")}");

        // 第 1 轮只建基线：CPU 要两次快照才能算差分，GPU Engine 是 rate 型计数器要两次 Collect
        provider.Groups[0].Run(new MetricSnapshot(0));
        provider.Groups[1].Run(new MetricSnapshot(0));

        // 再跑两轮：GPU 列要到第三轮才有值（第二轮结束时才拿到第一次有效采样）
        for (int round = 2; round <= 3; round++)
        {
            Thread.Sleep(5000);
            provider.Groups[0].Run(new MetricSnapshot(0));
            provider.Groups[1].Run(new MetricSnapshot(0));
            Console.WriteLine($"[第 {round} 轮] GPU 已采到的进程数 = {provider.Top.Count(p => !double.IsNaN(p.GpuPct))}");
        }

        Console.WriteLine();
        Console.WriteLine("=== CoPul 进程表（5 秒窗口）===");
        Console.WriteLine($"{"进程",-34} {"PID",7} {"CPU",8} {"GPU",5} {"内存",10}");
        foreach (var p in provider.Top)
        {
            string cpu = double.IsNaN(p.CpuPct) ? "—" : $"{p.CpuPct:F1}%";
            string gpu = double.IsNaN(p.GpuPct) ? "—" : $"{p.GpuPct:F0}%";
            string mem = p.MemMb >= 1024 ? $"{p.MemMb / 1024:F1} GB" : $"{p.MemMb:F0} MB";
            Console.WriteLine($"{p.Name,-34} {p.Pid,7} {cpu,8} {gpu,5} {mem,10}");
        }

        // 与 PDH 交叉验证：由外部脚本 tools/m13/verify.ps1 在同一时刻采样
        // \Process(*)\% Processor Time ÷ 核数，对比 top 列表与合计值
        return 0;
    }
}
