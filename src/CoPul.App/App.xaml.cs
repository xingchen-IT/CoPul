using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CoPul.App.Interop;
using CoPul.App.Sdk;

namespace CoPul.App;

public partial class App : Application
{
    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>UI 线程的 OS 线程 ID。用于在线程分解里认出"渲染/布局"这一路。</summary>
    private uint _uiThreadId;

    /// <summary>
    /// 自测用的纯色背景板（<c>--backdrop #RRGGBB</c>）。只在验收毛玻璃时存在，
    /// 让"窗口内 vs 窗口外"的像素对比有可比性；正常启动恒为 null。
    /// </summary>
    private Window? _backdrop;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallCrashLog();
        _uiThreadId = GetCurrentThreadId();

        // 自测：--taskbar-probe <txt 路径>。只读探测任务栏，不建窗口，写完即退。
        // M3 的地基全是"真实环境长什么样"：任务栏多高、图标区从哪开始、内部结构叫什么。
        // 这几项在 Win10 / Win11 22H2 / 24H2 上都不一样，只能实测，查文档会写出想当然的代码。
        if (ArgValue(e.Args, "--taskbar-probe") is { } probePath)
        {
            File.WriteAllText(probePath, DescribeTaskbar());
            Shutdown();
            return;
        }

        // 自测：--tray-probe <png> [--tray-child]。把一块纯色板挂到任务栏上再截屏，
        // 用来确认「SetParent 到 Shell_TrayWnd 到底能不能显示」这个 M3 的唯一硬前提。
        if (ArgValue(e.Args, "--tray-probe") is { } trayPng)
        {
            RunTrayProbe(trayPng,
                         e.Args.Contains("--tray-child"),
                         e.Args.Contains("--tray-native"),
                         e.Args.Contains("--tray-overlay"),
                         e.Args.Contains("--tray-print"));
            return;
        }

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

            floating = new FloatWindow(window.State, ConfigStore.Current);
            floating.MainWindowRequested += (_, _) =>
            {
                floating?.Hide();
                window.Show();
                window.Activate();
            };
            return floating;
        }

        TaskbarWindow? taskbar = null;

        TaskbarWindow EnsureTaskbar()
        {
            if (taskbar is not null) return taskbar;

            taskbar = new TaskbarWindow(window.State, ConfigStore.Current);
            taskbar.MainWindowRequested += (_, _) =>
            {
                taskbar?.Hide();
                window.Show();
                window.Activate();
            };
            taskbar.FloatRequested += (_, _) =>
            {
                taskbar?.Hide();
                FloatWindow f = EnsureFloat();
                f.Show();
                f.Activate();
            };
            return taskbar;
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
        if (e.Args.Contains("--backdrop"))
        {
            // --backdrop #RRGGBB：先铺一块满屏纯色，再让悬浮窗压在上面。
            // 验收毛玻璃必须这么做——桌面内容一直在变（游戏 / 网页 / 壁纸轮播），
            // 没有固定背景时同一段采样会得到完全不同的底，像素结论根本不可比。
            // --backdrop checker：改用 6 DIP 黑白棋盘格。纯色底看不出"有没有模糊"
            // （糊了也是同一个颜色），只有高频纹理才能区分「真模糊」与「只是半透明」。
            string? backdrop = ArgValue(e.Args, "--backdrop");
            Brush? boardBrush = backdrop switch
            {
                "checker" => CheckerBrush(),
                null => null,
                _ => TryParseColor(backdrop, out Color bgColor) ? new SolidColorBrush(bgColor) : null,
            };

            if (boardBrush is not null)
            {
                // 尺寸取 1200×500 居中，而不是最大化：够铺满"窗口 + pad"的采样范围，
                // 又不至于整屏盖住用户正在看的画面（自测时会闪几秒）。
                // 必须 Topmost——不然游戏 / 浏览器是前台，色块会被压在下面，采样全错。
                double vw = SystemParameters.PrimaryScreenWidth;
                double vh = SystemParameters.PrimaryScreenHeight;
                _backdrop = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Width = Math.Min(1200, vw),
                    Height = Math.Min(500, vh),
                    Left = (vw - Math.Min(1200, vw)) / 2,
                    Top = (vh - Math.Min(500, vh)) / 2,
                    Background = boardBrush,
                    Topmost = true,
                };
                _backdrop.Show();
            }
        }

        if (wantFloat)
        {
            FloatWindow f = EnsureFloat();
            f.CenterOnScreen = e.Args.Contains("--float-center");   // 验收毛玻璃时用

            // 自测开关必须在 Show 之前设：Show 之后 HWND 就绪，玻璃会按当时的值画一遍。
            // 这些参数是在改外观，如果照常落盘，跑一次验收就会把用户的配置改成"深色第 1 档"。
            bool glassOverride = e.Args.Contains("--glass-dark")
                                 || ArgValue(e.Args, "--glass-level") is not null
                                 || ArgValue(e.Args, "--float-scale") is not null;
            if (glassOverride) f.PersistToConfig = false;

            // --float-persist-probe：故意要写盘，用来验"改设置 → 存 config.json"这条链路
            if (e.Args.Contains("--float-persist-probe")) f.PersistProbe = true;

            if (e.Args.Contains("--glass-dark")) f.Theme = FloatWindow.GlassTheme.Dark;
            if (int.TryParse(ArgValue(e.Args, "--accent-flags"), out int flags)) WindowGlass.AccentFlagsOverride = flags;
            if (int.TryParse(ArgValue(e.Args, "--accent-state"), out int astate)) WindowGlass.AccentStateOverride = astate;
            if (int.TryParse(ArgValue(e.Args, "--glass-level"), out int lv)) f.GlassLevel = lv;

            // --float-scale 125：验收三档缩放用（100 / 125 / 150，越界会夹到最近档）
            if (int.TryParse(ArgValue(e.Args, "--float-scale"), out int sc)) f.ScalePercent = sc;

            // --no-shadow：关掉 DWM 系统阴影（6.4），用于排查阴影/边框异常
            if (e.Args.Contains("--no-shadow")) f.SystemShadow = false;

            // --float-snap-probe x,y：摆到 (x,y) 再跑吸附，结果写进 shots 的 sidecar 文件
            string? snapProbe = ArgValue(e.Args, "--float-snap-probe");
            if (snapProbe is not null)
            {
                string[] parts = snapProbe.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int px) && int.TryParse(parts[1], out int py))
                    f.SnapProbe = (px, py);
            }

            f.Show();
        }
        else if (e.Args.Contains("--tray"))
        {
            // 任务栏模式（第 7 章）。大窗口不 Show，但采集照常跑在它身上。
            TaskbarWindow t = EnsureTaskbar();
            t.Show();
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
        Window target = taskbar is { IsVisible: true } ? taskbar
                      : floating is { IsVisible: true } ? floating
                      : window;

        int waitMs = int.TryParse(ArgValue(e.Args, "--wait"), out int w) && w > 0 ? w : 4000;

        // 外扩像素：验收毛玻璃必须看到"窗口内 vs 窗口外"的差别，只截窗口本身永远看不出来
        int pad = int.TryParse(ArgValue(e.Args, "--shot-pad"), out int p) && p > 0 ? p : 0;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(waitMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // 任务栏分支要延后关窗（先置顶、等一帧再拍），用这个标志跳过下面的 Shutdown
            bool deferred = false;
            try
            {
                if (shotScreen is not null && target is TaskbarWindow tb)
                {
                    // 任务栏条的截图只能走**屏幕抓取**，两条捷径都已实测证伪：
                    // 1) 拍子窗口自己 —— 子窗口没有独立的重定向表面，全黑；
                    // 2) 拍任务栏（PrintWindow）—— 返回 true 但给的是旧表面，
                    //    拍到的是上一轮实验留下的红块，而那时任务栏上什么都没有。
                    // 所以：需要任务栏在屏幕上真的可见。--tray-raise 会在截图前把它临时置顶，
                    // 拍完立刻恢复，不改变用户的任务栏设置。
                    IntPtr bar = Taskbar.Find();
                    bool raise = e.Args.Contains("--tray-raise");
                    if (raise) Taskbar.RaiseForCapture(bar, true);

                    var cap = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(raise ? 500 : 0) };
                    cap.Tick += (_, _) =>
                    {
                        cap.Stop();
                        try
                        {
                            if (bar != IntPtr.Zero && Taskbar.TryGetRect(bar, out Win32Window.RECT br))
                            {
                                ScreenCapture.SaveRect(shotScreen, br.Left, br.Top,
                                                       br.Right - br.Left, br.Bottom - br.Top);
                                Taskbar.TryGetRect(tb.Handle, out Win32Window.RECT tr);
                                File.WriteAllText(shotScreen + ".info.txt",
                                    $"bar  : {br.Right - br.Left}×{br.Bottom - br.Top} px at ({br.Left},{br.Top}){Environment.NewLine}" +
                                    $"bar  : rect of strip = ({tr.Left},{tr.Top})-({tr.Right},{tr.Bottom}){Environment.NewLine}" +
                                    $"place: {tb.PlacementDiag}");
                            }
                        }
                        catch (Exception ex)
                        {
                            File.WriteAllText(shotScreen + ".error.txt", ex.ToString());
                        }
                        finally
                        {
                            if (raise) Taskbar.RaiseForCapture(bar, false);
                            Shutdown();
                        }
                    };
                    cap.Start();
                    deferred = true;
                    return;      // 延后关窗，交给 cap 定时器
                }
                else if (shotScreen is not null)
                {
                    ScreenCapture.SaveWindow(target, shotScreen, pad);
                    if (target is FloatWindow fw)
                        File.WriteAllText(shotScreen + ".info.txt",
                            $"glass: {fw.GlassMode}{Environment.NewLine}" +
                            $"size : {target.ActualWidth:F0}×{target.ActualHeight:F0} DIP{Environment.NewLine}" +
                            $"scale: {fw.ScaleDiag}" +
                            (fw.PlacementDiag.Length == 0
                                ? string.Empty
                                : Environment.NewLine + "place: " + fw.PlacementDiag));
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
                if (!deferred) Shutdown();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// <c>--tray-probe</c>：M3 的可行性实验。
    ///
    /// <para>
    /// 只验一件事：把一块纯红板 <c>SetParent</c> 到 <c>Shell_TrayWnd</c> 之后，
    /// 它到底能不能出现在任务栏上。这块如果不行，需求 7.1 的 B 方案整个作废，
    /// 只能退回 A（顶层窗口盖在任务栏上方），后面的字段布局、字号全都得跟着改。
    /// </para>
    ///
    /// <para>
    /// 用纯红而不是真实内容：任务栏本身是深色带模糊的，灰底灰字根本分不清
    /// "画出来了" 与 "被裁掉了"。红色是任务栏上不可能出现的颜色，出现即证明成功。
    /// </para>
    /// </summary>
    private void RunTrayProbe(string pngPath, bool asChildWindow, bool native, bool overlay, bool print)
    {
        IntPtr bar = Taskbar.Find();
        var log = new System.Text.StringBuilder();
        log.AppendLine("bar : " + Taskbar.Describe(bar));

        if (bar == IntPtr.Zero)
        {
            File.WriteAllText(pngPath + ".info.txt", log.AppendLine("任务栏句柄为空，实验终止").ToString());
            Shutdown();
            return;
        }

        Taskbar.TryGetRect(bar, out Win32Window.RECT barRect);
        uint dpi = Monitors.DpiForWindow(bar);
        double scale = dpi == 0 ? 1.0 : dpi / 96.0;

        // 物理尺寸：高取任务栏全高，宽取 600 物理像素（左侧空白是 0~647，放得下）
        int barH = barRect.Bottom - barRect.Top;
        int probeW = 600;

        // 截图方式二选一：屏幕抓取会被最上层的全屏游戏完全遮住（实测现场就有），
        // PrintWindow 直接向任务栏窗口要绘制结果，绕开所有遮挡。
        void Capture()
        {
            if (print)
                ScreenCapture.SaveWindowPrint(bar, pngPath, barRect.Right - barRect.Left, barH);
            else
                ScreenCapture.SaveRect(pngPath, barRect.Left, barRect.Top, barRect.Right - barRect.Left, barH);
            log.AppendLine($"captured    : {pngPath} ({(print ? "PrintWindow" : "screen")})");
        }

        if (native)
        {
            // 原生 STATIC 子窗口：不经过 WPF，用来判断"任务栏是否根本不吃跨进程子窗口"
            IntPtr child = Taskbar.CreateNativeChild(bar, 8, 0, probeW, barH);
            log.AppendLine($"native STATIC = 0x{child:X}");
            log.AppendLine($"native rect   : {Taskbar.Describe(child)}");
            Taskbar.BringToTop(child);
            log.AppendLine("native brought to top");

            var nativeTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            nativeTimer.Tick += (_, _) =>
            {
                nativeTimer.Stop();
                try
                {
                    Capture();
                }
                catch (Exception ex) { log.AppendLine("capture 失败: " + ex.Message); }
                finally
                {
                    File.WriteAllText(pngPath + ".info.txt", log.ToString());
                    Shutdown();
                }
            };
            nativeTimer.Start();
            return;
        }

        if (overlay)
        {
            // A 方案：顶层窗口盖在任务栏上。任务栏自己是 topmost，所以必须同样 topmost
            // 且排在它之后（SetWindowPos 的 HWND_TOPMOST 会让它进 topmost 组的最高位）。
            var ov = new Window
            {
                WindowStyle = WindowStyle.None,
                ResizeMode = ResizeMode.NoResize,
                ShowInTaskbar = false,
                Background = Brushes.Red,
                WindowStartupLocation = WindowStartupLocation.Manual,
                Width = 100, Height = 100,   // 占位，真正尺寸由 SetBounds 下物理像素
                Topmost = true,
            };
            ov.Show();
            IntPtr ovh = new System.Windows.Interop.WindowInteropHelper(ov).Handle;
            Monitors.SetBounds(ovh, barRect.Left + 8, barRect.Top, probeW, barH, topmost: true);
            log.AppendLine($"overlay      : {Taskbar.Describe(ovh)}");

            var ovTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
            ovTimer.Tick += (_, _) =>
            {
                ovTimer.Stop();
                try
                {
                    Capture();
                }
                catch (Exception ex) { log.AppendLine("capture 失败: " + ex.Message); }
                finally
                {
                    File.WriteAllText(pngPath + ".info.txt", log.ToString());
                    Shutdown();
                }
            };
            ovTimer.Start();
            return;
        }

        var w = new Window
        {
            WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize,
            ShowInTaskbar = false,
            AllowsTransparency = false,
            Background = Brushes.Red,
            Width = probeW / scale,
            Height = barH / scale,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = 0,
            Top = 0,
            Topmost = false,
        };
        w.Show();

        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(w).Handle;
        log.AppendLine($"probe before: {Taskbar.Describe(hwnd)}");

        bool ok = Taskbar.Attach(hwnd, bar, asChildWindow);
        Taskbar.PlaceInTaskbar(hwnd, 8, 0, probeW, barH);
        Taskbar.BringToTop(hwnd);

        log.AppendLine($"attach({(asChildWindow ? "WS_CHILD" : "popup")}) -> {ok}");
        log.AppendLine($"parent now  : 0x{Taskbar.ParentOf(hwnd):X} (bar=0x{bar:X})");
        log.AppendLine($"probe after : {Taskbar.Describe(hwnd)}");

        // 挂载与合成都要几帧，截早了会拍到还没画上的任务栏
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1200) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            try
            {
                Capture();
            }
            catch (Exception ex)
            {
                log.AppendLine("capture 失败: " + ex.Message);
            }
            finally
            {
                File.WriteAllText(pngPath + ".info.txt", log.ToString());
                Shutdown();
            }
        };
        timer.Start();
    }

    /// <summary>
    /// <c>--taskbar-probe</c> 的实现：把任务栏的真实现状打成文本。
    ///
    /// <para>
    /// 为什么值得单独一个探针：M3 的布局全部建立在"任务栏矩形 / 图标区左边界 / DPI"
    /// 这三个数上，而它们都是系统实现细节 —— Win11 22H2 之后任务栏被改写成 XAML 岛，
    /// 内部子窗口的类名与前几版完全不同。先量一遍再写代码，比照着文档猜要省事得多。
    /// </para>
    /// </summary>
    private string DescribeTaskbar()
    {
        var sb = new System.Text.StringBuilder();

        IntPtr bar = Taskbar.Find();
        IntPtr secondary = Taskbar.FindSecondary();

        sb.AppendLine("== 任务栏窗口 ==");
        sb.AppendLine("主屏 Shell_TrayWnd     : " + Taskbar.Describe(bar));
        sb.AppendLine("副屏 Shell_SecondaryTrayWnd: " + Taskbar.Describe(secondary));

        if (bar != IntPtr.Zero)
        {
            uint dpi = GetDpiForWindowSafe(bar);
            sb.AppendLine($"任务栏 DPI           : {dpi} (缩放 {dpi / 96.0:F2})");

            sb.AppendLine();
            sb.AppendLine("== 几何关系（物理像素）==");
            if (Monitors.TryGetRect(bar, out Win32Window.RECT br))
            {
                sb.AppendLine($"任务栏矩形           : L={br.Left} T={br.Top} R={br.Right} B={br.Bottom}  " +
                               $"尺寸 {br.Right - br.Left}x{br.Bottom - br.Top}");
            }

            if (Monitors.TryPrimary(out MonitorArea work))
            {
                sb.AppendLine($"主屏工作区           : L={work.Left} T={work.Top} R={work.Right} B={work.Bottom}  " +
                               $"尺寸 {work.Width}x{work.Height}  设备={work.Device}");
            }

            if (Monitors.TryPrimaryMonitorBounds(out Win32Window.RECT mon))
            {
                sb.AppendLine($"主屏整屏             : L={mon.Left} T={mon.Top} R={mon.Right} B={mon.Bottom}  " +
                               $"尺寸 {mon.Right - mon.Left}x{mon.Bottom - mon.Top}");
                sb.AppendLine($"整屏 - 工作区 差值   : 上 {work.Top - mon.Top}  下 {mon.Bottom - work.Bottom}  " +
                               $"左 {work.Left - mon.Left}  右 {mon.Right - work.Right}");
            }

            sb.AppendLine();
            sb.AppendLine("== 子窗口（深度 1）==");
            foreach ((IntPtr hwnd, string cls, string text) in Taskbar.Children(bar))
            {
                bool hasRect = Taskbar.TryGetRect(hwnd, out Win32Window.RECT r);
                string rectText = hasRect
                    ? $"({r.Left},{r.Top})-({r.Right},{r.Bottom}) {r.Right - r.Left}x{r.Bottom - r.Top}"
                    : "?";
                sb.AppendLine($"  0x{hwnd:X}  class={cls}  title=\"{text}\"  rect={rectText}  visible={Taskbar.IsVisible(hwnd)}");

                // 再深一层：ReBarWindow32 下面才有真正的图标工具条
                foreach ((IntPtr child, string c2, string t2) in Taskbar.Children(hwnd))
                {
                    bool ok2 = Taskbar.TryGetRect(child, out Win32Window.RECT r2);
                    string rt2 = ok2 ? $"({r2.Left},{r2.Top})-({r2.Right},{r2.Bottom}) {r2.Right - r2.Left}x{r2.Bottom - r2.Top}" : "?";
                    sb.AppendLine($"      └ 0x{child:X}  class={c2}  title=\"{t2}\"  rect={rt2}  visible={Taskbar.IsVisible(child)}");
                }
            }
        }

        sb.AppendLine();
        sb.AppendLine($"SystemUsesLightTheme : {ReadLightTheme()}");
        sb.AppendLine($"OS 版本              : {Environment.OSVersion.VersionString} (build {Environment.OSVersion.Version.Build})");

        // 谁盖住了任务栏区域
        if (Taskbar.TryGetRect(bar, out Win32Window.RECT area))
        {
            sb.AppendLine();
            sb.AppendLine("== 与任务栏区域相交的顶层窗口（自下而上不易判序，仅列出）==");
            foreach (string line in Taskbar.WhoCovers(area.Left, area.Top, area.Right, area.Bottom))
                sb.AppendLine("  " + line);
        }

        return sb.ToString();
    }

    /// <summary>任务栏属于 explorer，进程 DPI 感知与自身不同，取不到时按 96 计。</summary>
    private static uint GetDpiForWindowSafe(IntPtr hwnd)
    {
        try { return Monitors.DpiForWindow(hwnd); }
        catch { return 96; }
    }

    private static string ReadLightTheme()
    {
        try
        {
            object? v = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                "SystemUsesLightTheme", null);
            return v?.ToString() ?? "(未设置)";
        }
        catch { return "(读取失败)"; }
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

    /// <summary>解析 <c>#RRGGBB</c>。只接受这一种写法——自测参数，不需要容错各种花样。</summary>
    private static bool TryParseColor(string text, out Color color)
    {
        color = Colors.Black;
        string hex = text.TrimStart('#');
        if (hex.Length != 6 || !int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                System.Globalization.CultureInfo.InvariantCulture, out int rgb))
            return false;

        color = Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        return true;
    }

    /// <summary>
    /// 6 DIP 黑白棋盘格。用来判断"模糊到底有没有发生"：真模糊会把高频格子糊成均匀灰，
    /// 只有半透明的话格子仍然清清楚楚。**纯色背景永远测不出这一条。**
    /// </summary>
    private static Brush CheckerBrush()
    {
        var group = new DrawingGroup();
        group.Children.Add(new GeometryDrawing(
            new SolidColorBrush(Color.FromRgb(0x1E, 0x22, 0x28)), null, new RectangleGeometry(new Rect(0, 0, 12, 12))));
        group.Children.Add(new GeometryDrawing(
            Brushes.White, null, new RectangleGeometry(new Rect(0, 0, 6, 6))));
        group.Children.Add(new GeometryDrawing(
            Brushes.White, null, new RectangleGeometry(new Rect(6, 6, 6, 6))));

        var brush = new DrawingBrush(group)
        {
            TileMode = TileMode.Tile,
            Viewport = new Rect(0, 0, 12, 12),
            ViewportUnits = BrushMappingMode.Absolute,
            Viewbox = new Rect(0, 0, 12, 12),
            ViewboxUnits = BrushMappingMode.Absolute,
            Stretch = Stretch.None,
        };
        brush.Freeze();
        return brush;
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
