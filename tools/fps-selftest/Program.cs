// FPS 采集链路的离线自测夹具（不属于产品代码）。
//
// 为什么要它：真实 PresentMon 需要管理员权限（ETW 会话），在没有 UAC 的环境里
// 没法验证「读 CSV 流 → 解析 → 统计 → 六格数字 → 折线图」这条链路。
// 这个程序按 PresentMon 2.x 的 --v1_metrics 表头逐帧输出 CSV，可以顶替它跑通全链路。
//
// 用法：编译后改名 PresentMon.exe 放进 <运行目录>\presentmon\，同时把
// FpsProvider.Initialize 的提权检查临时去掉，然后 CoPul --shot 截图核对。
//
// 帧模型：基础 60 FPS，每 300 帧插一次 50ms 的长帧 —— 这样 1% Low 应该明显低于
// 平均帧率（约 1000/50 = 20 FPS），能一眼看出 1% Low 有没有被算成平均值。

using System.Diagnostics;

const double BaseMs = 1000.0 / 60.0;
const double HitchMs = 50.0;
const int HitchEvery = 300;

// 表头必须与 PresentMon CsvOutput.cpp 的 WriteCsvHeader<FrameMetrics1> 完全一致，
// 包括 msBetweenPresents 的小写 m。
Console.Out.WriteLine(
    "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped," +
    "TimeInSeconds,msInPresentAPI,msBetweenPresents,AllowsTearing,PresentMode," +
    "msUntilRenderComplete,msUntilDisplayed,msBetweenDisplayChange,msFlipDelay");
Console.Out.Flush();

int pid = Environment.ProcessId;
var sw = Stopwatch.StartNew();
long frame = 0;
double nextMs = 0;

// 参数被忽略：CoPul 会传 --output_stdout 等，这里照单全收。
while (true)
{
    double now = sw.Elapsed.TotalMilliseconds;
    if (now < nextMs)
    {
        Thread.Sleep(1);
        continue;
    }

    double between = frame == 0 ? BaseMs : (frame % HitchEvery == 0 ? HitchMs : BaseMs);
    nextMs += between;
    frame++;

    // 列顺序：0 Application, 1 ProcessID, 9 msBetweenPresents, 14 msBetweenDisplayChange
    Console.Out.WriteLine(
        $"fakegame.exe,{pid},0x0,DXGI,1,0,0," +
        $"{now / 1000.0:F6},0.12,{between:F6},1,0," +
        $"1.20,4.50,{between:F6},0.80");
    Console.Out.Flush();
}
