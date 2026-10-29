using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CoPul.App.Interop;

namespace CoPul.App;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>UI 线程的 OS 线程 ID。用于在线程分解里认出"渲染/布局"这一路。</summary>
    private uint _uiThreadId;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallCrashLog();
        _uiThreadId = GetCurrentThreadId();

        var window = new MainWindow();
        MainWindow = window;

        // --hidden：不显示窗口，用来分离「采集器成本」与「WPF 渲染成本」
        bool hidden = e.Args.Contains("--hidden");

        // --float：以悬浮窗模式启动（M2）。大窗口全程不 Show，但它的 Host 照常采集——
        // 悬浮窗只是同一份 DashboardState 的另一个视图，不自己起第二套 PresentMon。
        FloatWindow? floating = null;

        FloatWindow EnsureFloat()
        {
            if (floating is not null) return floating;

            floating = new FloatWindow(window.State);
            floating.MainWindowRequested += (_, _) =>
            {
                floating?.Hide();
                window.Show();
                window.Activate();
            };
            return floating;
        }

        // 大窗口标题栏的「切到悬浮窗」按钮走这条回调：MainWindow 不认识 FloatWindow，
        // 也不该认识——两者的关系由 App 接线，窗口之间不互相 new。
        window.FloatRequested = () =>
        {
            window.Hide();
            FloatWindow f = EnsureFloat();
            f.Show();
            f.Activate();
        };

        bool wantFloat = e.Args.Contains("--float");
        if (wantFloat)
        {
            FloatWindow f = EnsureFloat();
            f.CenterOnScreen = e.Args.Contains("--float-center");   // 验收毛玻璃时用
            f.Show();
        }
        else if (!hidden) window.Show();

        // 自测：--stats <txt 路径> [--seconds 60] [--warmup 10] [--freeze] [--hidden]
        string? stats = ArgValue(e.Args, "--stats");
        if (stats is not null)
        {
            window.MeasureLayout = true;
            if (e.Args.Contains("--no-cores")) window.State.SuppressCoreLine = true;
            if (e.Args.Contains("--freeze"))
            {
                // 窗口照常显示但不再接收新数据：用来判断成本在「更新」还是在「合成」
                var freezeAt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                freezeAt.Tick += (_, _) => { freezeAt.Stop(); window.Paused = true; };
                freezeAt.Start();
            }

            RunSelfCpuProbe(window, stats,
                int.TryParse(ArgValue(e.Args, "--seconds"), out int sec) && sec > 0 ? sec : 60,
                int.TryParse(ArgValue(e.Args, "--warmup"), out int warm) && warm >= 0 ? warm : 10);
            return;
        }

        // 无头验收用：--shot <png 路径> [--wait 毫秒]
        // M1 没有自动化 UI 测试，用它把真实窗口渲染成图片，肉眼核对布局与数值。
        //
        // --shot-screen 走另一条路（GDI 抓屏）：毛玻璃是 DWM 在合成阶段画的，
        // RenderTargetBitmap 只能画 WPF 自己的视觉树，截出来永远是一块纯色。
        // 验收 6.8 第 1 条（背景确实被模糊）必须用 --shot-screen。
        string? shot = ArgValue(e.Args, "--shot");
        string? shotScreen = ArgValue(e.Args, "--shot-screen");
        if (shot is null && shotScreen is null) return;

        // 截哪个窗口：悬浮窗可见时截悬浮窗，否则截大窗口
        Window target = floating is { IsVisible: true } ? floating : window;

        int waitMs = int.TryParse(ArgValue(e.Args, "--wait"), out int w) && w > 0 ? w : 4000;

        // 外扩像素：验收毛玻璃必须看到"窗口内 vs 窗口外"的差别，只截窗口本身永远看不出来
        int pad = int.TryParse(ArgValue(e.Args, "--shot-pad"), out int p) && p > 0 ? p : 0;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(waitMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                if (shotScreen is not null)
                {
                    ScreenCapture.SaveWindow(target, shotScreen, pad);
                    if (target is FloatWindow fw)
                        File.WriteAllText(shotScreen + ".info.txt",
                            $"glass: {fw.GlassMode}{Environment.NewLine}" +
                            $"size : {target.ActualWidth:F0}×{target.ActualHeight:F0} DIP");
                }
                else
                {
                    SavePng(target, shot!);
                }
            }
            catch (Exception ex)
            {
                File.WriteAllText((shotScreen ?? shot) + ".error.txt", ex.ToString());
            }
            finally
            {
                Shutdown();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// 崩溃日志（A-29）。常驻型程序最怕"窗口一闪就没"——没有堆栈就只能靠猜。
    /// 任何未处理异常都写一份到 <c>%LOCALAPPDATA%\CoPul\crash-*.log</c>，
    /// UI 线程和非 UI 线程各挂一个钩子；写完不拦截，程序该退还是退（坏了的程序留着更危险）。
    /// </summary>
    private void InstallCrashLog()
    {
        // 注意：DispatcherUnhandledException 是实例事件，必须挂在 this 上（写 App. 会编译不过）
        DispatcherUnhandledException += (_, a) => WriteCrash("ui", a.Exception);
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
            WriteCrash("app", a.ExceptionObject as Exception ?? new Exception(a.ExceptionObject?.ToString() ?? "未知异常"));
    }

    private static void WriteCrash(string kind, Exception? ex)
    {
        try
        {
            string dir = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CoPul");
            Directory.CreateDirectory(dir);

            string file = System.IO.Path.Combine(dir, $"crash-{DateTime.Now:yyyyMMdd-HHmmss}-{kind}.log");
            File.WriteAllText(file,
                $"时间: {DateTime.Now:yyyy-MM-dd HH:mm:ss}{Environment.NewLine}" +
                $"版本: {typeof(App).Assembly.GetName().Version}{Environment.NewLine}" +
                $"命令行: {Environment.CommandLine}{Environment.NewLine}" +
                $"工作目录: {Environment.CurrentDirectory}{Environment.NewLine}" +
                $"管理员: {IsElevated()}{Environment.NewLine}{Environment.NewLine}{ex}");
        }
        catch
        {
            // 写日志本身失败也不能再抛，否则会把原始异常盖掉
        }
    }

    private static bool IsElevated()
    {
        try
        {
            var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return new System.Security.Principal.WindowsPrincipal(id)
                .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }

    private static string? ArgValue(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>
    /// 量自身 CPU。用 <c>Process.TotalProcessorTime</c> 的增量除以墙钟时间，
    /// 口径与 M0.6 的基准程序一致（60 秒窗口下量化误差 ±0.026%）。
    ///
    /// 注意两点：
    /// 1. 这个数字**包含 WPF 渲染**，正是需求 11 章要考核的"整机口径"；
    ///    m06 只量采集器，两者不能直接相加比较；
    /// 2. 必须跳过预热再取基线——启动那几十秒里有 JIT、PDH 初始化（约 0.6s）、
    ///    冷盘枚举（R20，约 0.19s），混进去会把稳态读数抬高好几倍（M0.6 踩过同一个坑）。
    /// </summary>
    private void RunSelfCpuProbe(MainWindow window, string path, int seconds, int warmupSec)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        TimeSpan cpu0 = default;
        long wall0 = 0;
        int tick = 0;
        string? warmupNote = null;
        var thread0 = new Dictionary<int, TimeSpan>();

        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        timer.Tick += (_, _) =>
        {
            tick++;

            if (tick == warmupSec)
            {
                process.Refresh();
                cpu0 = process.TotalProcessorTime;
                wall0 = System.Diagnostics.Stopwatch.GetTimestamp();

                foreach (System.Diagnostics.ProcessThread t in process.Threads)
                {
                    try { thread0[t.Id] = t.TotalProcessorTime; } catch { }
                }

                // 预热期间的 CPU 一并记下来，用于说明"启动成本"到底多大
                warmupNote = $"{cpu0.TotalMilliseconds:F0} ms（含 JIT / PDH 初始化 / 冷盘枚举）";
            }

            if (tick < warmupSec + seconds) return;
            timer.Stop();

            process.Refresh();
            double wallMs = (System.Diagnostics.Stopwatch.GetTimestamp() - wall0) * 1000.0
                            / System.Diagnostics.Stopwatch.Frequency;
            double cpuMs = (process.TotalProcessorTime - cpu0).TotalMilliseconds;
            double single = cpuMs / wallMs * 100.0;
            double machine = single / Environment.ProcessorCount;

            var lines = new List<string>
            {
                $"time        : {DateTime.Now:yyyy-MM-dd HH:mm:ss}",
                $"window      : {window.ActualWidth:F0}×{window.ActualHeight:F0}  state={window.WindowState}",
                $"admin       : {IsAdministrator()}",
                $"warmup(s)   : {warmupSec}  期间 CPU {warmupNote}",
                $"window(s)   : {wallMs / 1000.0:F1}",
                $"cpu time    : {cpuMs:F0} ms",
                $"single-core : {single:F3}%",
                $"machine     : {machine:F4}%",
                $"threads     : {process.Threads.Count}",
                // 渲染档位：2 = 硬件加速，0/1 = 软件渲染（远程桌面或驱动异常时 WPF 会退到这里，
                // 那时 CPU 高是环境问题，不是采集器的问题）
                $"render tier : {RenderCapability.Tier >> 16}",
                $"frames      : {window.ApplyCount}  平均 ApplySnapshot {SafeAvg(window.ApplyTotalMs, window.ApplyCount):F3} ms" +
                $"  平均 Layout {SafeAvg(window.LayoutTotalMs, window.ApplyCount):F3} ms",
                // D04 的 10s 刷新：刷新次数 vs 变化次数。60s 窗口下应该刷新 5-6 次，
                // 变化 0 次是正常的（磁盘使用率一秒内不可能跳过一个整数百分点）。
                $"d04 refresh : {window.Host.PartitionRefreshCount} 次  其中变化 {window.Host.PartitionChangedCount} 次"
            };

            // 线程级分解：总 CPU 高但不知道贵在哪时，这是唯一能定位的手段
            var deltas = new List<(int Id, double Ms)>();
            foreach (System.Diagnostics.ProcessThread t in process.Threads)
            {
                try
                {
                    thread0.TryGetValue(t.Id, out var before);
                    deltas.Add((t.Id, (t.TotalProcessorTime - before).TotalMilliseconds));
                }
                catch { }
            }

            lines.Add("thread breakdown (ms):");
            foreach (var d in deltas.OrderByDescending(x => x.Ms).Take(6))
                lines.Add($"  tid {d.Id,-7} {d.Ms,7:F0} ms  {d.Ms / wallMs * 100.0,6:F2}% single-core" +
                          (d.Id == _uiThreadId ? "   <- UI/渲染线程" : ""));

            try
            {
                string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllLines(path, lines);
            }
            finally
            {
                Shutdown();
            }
        };
        timer.Start();
    }

    private static bool IsAdministrator()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return new System.Security.Principal.WindowsPrincipal(identity)
            .IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
    }

    private static double SafeAvg(double total, long count) => count == 0 ? 0 : total / count;

    /// <summary>
    /// 按当前 DPI 把窗口内容渲染成 PNG。用 <c>RenderTargetBitmap</c> 而不是屏幕截图，
    /// 这样不依赖窗口是否在最前，也不会被远程桌面/锁屏影响。
    /// </summary>
    private static void SavePng(Window window, string path)
    {
        var dpi = VisualTreeHelper.GetDpi(window);

        int pixelWidth = (int)Math.Ceiling(window.ActualWidth * dpi.DpiScaleX);
        int pixelHeight = (int)Math.Ceiling(window.ActualHeight * dpi.DpiScaleY);
        if (pixelWidth <= 0 || pixelHeight <= 0)
            throw new InvalidOperationException($"窗口尺寸为 {window.ActualWidth}×{window.ActualHeight}，无法渲染。");

        var bitmap = new RenderTargetBitmap(pixelWidth, pixelHeight,
                                            96 * dpi.DpiScaleX, 96 * dpi.DpiScaleY, PixelFormats.Pbgra32);
        bitmap.Render(window);

        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));

        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
