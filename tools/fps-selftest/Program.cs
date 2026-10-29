// FPS 采集链路的离线自测夹具（不属于产品代码）。
//
// 为什么要它：真实 PresentMon 需要管理员权限（ETW 会话），在没有 UAC 的环境里
// 没法验证「读 CSV 流 → 解析 → 统计 → 六格数字 → 折线图」这条链路。
// 这个程序按 PresentMon 2.x 的 --v1_metrics 表头逐帧输出 CSV，可以顶替它跑通全链路。
//
// 用法：编译后改名 PresentMon.exe 放进 <运行目录>\presentmon\，同时把
// FpsProvider.Initialize 的提权检查临时去掉，然后 CoPul --shot 截图核对。
//
// **编码**（2026-10-29 补）：真实 PresentMon 2.6 往 stdout 写的是 UTF-16LE + BOM，
// 而 --output_file 模式写 UTF-8。默认按真实行为用 UTF-16LE，另外支持一个
// fake-utf8.flag 切换成 UTF-8 无 BOM，用来验证 CoPul 那侧两条路径都能读。
//
// 帧模型：基础 60 FPS，每 300 帧插一次 50ms 的长帧 —— 这样 1% Low 应该明显低于
// 平均帧率（约 1000/50 = 20 FPS），能一眼看出 1% Low 有没有被算成平均值。
//
// **发送方式（2026-10-29 补）：按批次突发写出，不是逐帧。** 真实 PresentMon 是攒够一批
// 才 fflush 的，CoPul 那侧一旦被饿一会儿就会一次性收到几百行。若按"收到时刻"当时间轴，
// 这几百帧会被压进几毫秒，帧率虚高到几万（实机见过 71000 FPS / 帧时间 0.0ms）。
// 夹具故意保留这个突发特性：60 FPS 的模型无论怎么攒批，CoPul 都必须显示 ~60。

using System.Diagnostics;
using System.Text;

const double BaseMs = 1000.0 / 60.0;
const double HitchMs = 50.0;
const int HitchEvery = 300;

// 每批帧数：12 帧≈200ms 的量，一笔写出，模拟 PresentMon 的批量 fflush。
const int BatchSize = 12;

string exeDir = AppContext.BaseDirectory;

// 把收到的命令行落盘：用来确认 CoPul 真的把 --no_track_input 传下来了。
try { File.WriteAllText(Path.Combine(exeDir, "args.txt"), string.Join(' ', args)); } catch { }

bool asUtf8 = File.Exists(Path.Combine(exeDir, "fake-utf8.flag"));

var stdout = Console.OpenStandardOutput();
TextWriter w = asUtf8
    ? new StreamWriter(stdout, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
    : new StreamWriter(stdout, new UnicodeEncoding(bigEndian: false, byteOrderMark: true));

// 表头必须与 PresentMon CsvOutput.cpp 的 WriteCsvHeader<FrameMetrics1> 完全一致，
// 包括 msBetweenPresents 的小写 m。
w.WriteLine(
    "Application,ProcessID,SwapChainAddress,Runtime,SyncInterval,PresentFlags,Dropped," +
    "TimeInSeconds,msInPresentAPI,msBetweenPresents,AllowsTearing,PresentMode," +
    "msUntilRenderComplete,msUntilDisplayed,msBetweenDisplayChange,msFlipDelay");
w.Flush();

int pid = Environment.ProcessId;
var sw = Stopwatch.StartNew();
long frame = 0;
double nextMs = 0;
var batch = new StringBuilder();

// 参数被忽略：CoPul 会传 --output_stdout 等，这里照单全收。
while (true)
{
    double now = sw.Elapsed.TotalMilliseconds;
    if (now < nextMs)
    {
        // 攒够一批再一次性写出（不 Flush，等凑满 BatchSize）
        Thread.Sleep(1);
        continue;
    }

    // 一拍生成 BatchSize 帧，时间戳按真实间隔递进 —— 但它们几乎同时被写出。
    for (int k = 0; k < BatchSize; k++)
    {
        double between = frame == 0 ? BaseMs : (frame % HitchEvery == 0 ? HitchMs : BaseMs);
        double tsMs = nextMs;
        nextMs += between;
        frame++;

        // 列顺序：0 Application, 1 ProcessID, 9 msBetweenPresents, 14 msBetweenDisplayChange
        batch.Append("fakegame.exe,").Append(pid).Append(",0x0,DXGI,1,0,0,")
             .Append((tsMs / 1000.0).ToString("F6")).Append(",0.12,")
             .Append(between.ToString("F6")).Append(",1,0,1.20,4.50,")
             .Append(between.ToString("F6")).Append(",0.80\r\n");
    }

    w.Write(batch);
    w.Flush();
    batch.Clear();
}
