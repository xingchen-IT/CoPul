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

    /// <summary>
    /// 托盘图标。必须活到进程结束 —— 它一旦被 GC 回收，
    /// 隐藏窗口和 <c>Shell_NotifyIcon</c> 的注册就断了，托盘里会留个点不动的死图标。
    /// </summary>
    private TrayIcon? _tray;

    protected override void OnExit(ExitEventArgs e)
    {
        // 显式摘掉托盘图标：不摘的话 Windows 要等鼠标划过托盘区才清理，中间那段时间
        // 是一个"点了没反应"的残影，和 M3 那个 DWM 残影是同一种观感问题
        _tray?.Dispose();
        _tray = null;

        // G-05：释放互斥体，否则下次启动会误判"已经有一个在跑"
        SingleInstance.Release();

        base.OnExit(e);
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        InstallCrashLog();
        _uiThreadId = GetCurrentThreadId();

        // G-04 的提权子进程入口：只做计划任务的注册/删除，做完立刻退。
        // 必须放在单实例检查之前 —— 主程序很可能正开着（用户就是从它的托盘菜单点的），
        // 子进程要是去抢互斥体会被当成"第二个实例"直接退掉，永远注册不上。
        if (ArgValue(e.Args, AutoStart.InternalSwitch) is { } autoInternal)
        {
            bool internalOk = autoInternal.Equals("disable", StringComparison.OrdinalIgnoreCase)
                ? AutoStart.DisableCore()
                : AutoStart.EnableCore();

            Interop.Trace.Log("autostart", $"internal {autoInternal} ok={internalOk}");
            Shutdown(internalOk ? 0 : 1);
            return;
        }

        // 自测：--force-edge left|right|top|bottom，只覆盖"任务栏停在哪条边"的判定结果。
        // 验证 7.7 的竖向任务栏降级必须把任务栏真的挪到左边，那是改用户的系统设置 ——
        // 跑一次改一次不可接受（中途进程被杀还会留在那儿），所以只伪造判定结果，
        // SetParent / 摆位 / 隐藏全走生产路径。
        if (ArgValue(e.Args, "--force-edge") is { } forcedEdge
            && Enum.TryParse(forcedEdge, ignoreCase: true, out Taskbar.Edge parsedEdge))
        {
            Taskbar.EdgeOverride = parsedEdge;
        }

        // 自测：--tray-free <px>，只覆盖"任务栏左侧还有多少空白"这个输入，
        // 用来触发 7.7「任务栏已满」的字段让位。真要触发得往任务栏塞几十个图标，
        // 不可重复也不该做 —— 和 --force-edge 同一个取舍：伪造输入，其余全走生产路径。
        if (int.TryParse(ArgValue(e.Args, "--tray-free"), out int forcedFree))
        {
            Taskbar.FreeWidthOverride = forcedFree;
        }

        // 自测：--hover-probe x,y[，--hover-probe-clear <ms>] 伪造光标位置，验 7.6 的悬停提示。
        // 本机 SetCursorPos 不可靠（设 (68,1576) 读回 (1251,895)）：光标会被真实鼠标挪走，
        // 而"鼠标是否停在条上"正是这条功能的唯一输入，只能伪造。
        if (ArgValue(e.Args, "--hover-probe") is { } hoverAt)
        {
            string[] parts = hoverAt.Split(',');
            if (parts.Length == 2
                && int.TryParse(parts[0], out int hx) && int.TryParse(parts[1], out int hy))
            {
                Taskbar.HoverOverride = new Win32Window.POINT { X = hx, Y = hy };

                // --hover-probe-clear <ms>：过一会儿清掉伪造值 = 模拟"鼠标移开"，
                // 用来验提示框会自己收起来（trace 里能看到 show → hide）
                if (int.TryParse(ArgValue(e.Args, "--hover-probe-clear"), out int clearAfter))
                {
                    var clearTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(clearAfter) };
                    clearTimer.Tick += (_, _) =>
                    {
                        clearTimer.Stop();
                        Taskbar.HoverOverride = null;
                        Interop.Trace.Log("tray", $"hover-probe: 清空伪造光标（{clearAfter} ms）");
                    };
                    clearTimer.Start();
                }
            }
        }

        // 自测：--autostart-probe [status|cmd|enable|disable]，验 G-04。
        // 走的是托盘菜单"开机自启"同一个入口（AutoStart 的几个方法），不是影子逻辑。
        // 结果写 trace —— 这是 GUI 程序，没有控制台可打印。
        if (ArgValue(e.Args, "--autostart-probe") is { } autoProbe)
        {
            string line = autoProbe.ToLowerInvariant() switch
            {
                "cmd" => "cmd: " + AutoStart.CommandLine(),
                "comtest" => "comtest: " + AutoStart.ComTest(),
                "dump" => "dump: " + AutoStart.DumpTask(),
                "enable" => $"enable: ok={AutoStart.Enable()}",
                "disable" => $"disable: ok={AutoStart.Disable()}",
                _ => $"status: enabled={AutoStart.IsEnabled()} cmd={AutoStart.CommandLine()}",
            };

            Interop.Trace.Log("autostart", "probe " + line);
            Shutdown();
            return;
        }

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

        // G-05 单实例：第二个实例只负责"把第一个叫到前面"，然后自己退出。
        //
        // 为什么不放在 OnStartup 最开头：上面那些 --taskbar-probe / --tray-probe 是
        // 写完文件就退的纯探测，不建窗口也不进采集；自测时经常要在用户实例还开着的情况下
        // 跑截图，互斥体一拦就全部失败。所以只对"真正的启动"生效 —— --shot /
        // --stats / --hidden 这些测量模式一律跳过。
        bool measurement = e.Args.Contains("--shot") || e.Args.Contains("--shot-screen")
                           || e.Args.Contains("--stats") || e.Args.Contains("--hidden");

        if (!measurement)
        {
            if (!SingleInstance.TryAcquire())
            {
                // 已有实例：发广播让它现身，自己立刻退。注意此时**不能**建主窗口，
                // 否则会先闪一下再消失（表现像"点了两次图标，弹了两个窗口")
                Interop.Trace.Log("single", "已有实例在运行 → 请求它现身并退出");
                SingleInstance.SignalExisting();
                Shutdown();
                return;
            }

            // 开始监听"第二次启动"的广播。收到后要做什么在下面接线
            // （必须等 ShowView 与几个视图变量都就绪才能订阅，否则编译器会报未赋值）。
            SingleInstance.Listen();
        }

        var window = new MainWindow();
        MainWindow = window;

        // 数据刷新档位（M4.24）：在采集线程 Start 之前设好，
        // 否则会先按默认 500ms 跑几拍再改 —— 用户看到的是"刚开机那两秒跳得快"。
        // --sample-interval <ms> 命令行覆盖（取证用，不落盘）。
        string? ivArg = ArgValue(e.Args, "--sample-interval");
        window.Host.SetSampleInterval(
            ivArg is not null && int.TryParse(ivArg, out int iv)
                ? iv
                : ConfigStore.Current.SampleIntervalMs);

        // --hidden：不显示窗口，用来分离「采集器成本」与「WPF 渲染成本」
        bool hidden = e.Args.Contains("--hidden");

        // =====================================================================
        // 三视图编排（8.1；M4.26 起**可同时显示**，原 A-06「任意时刻只有一个可见」作废）。
        //
        // 为什么把 Show/Hide 收到这几个函数里（M4 重构）：全屏自动切换要自己动视图，
        // 而用户点标题栏 ▭ / ▤、双击条、托盘菜单也在动视图。两边各写一套 Hide/Show，
        // 状态机看到的事实立刻不对 —— 表现就是"退出全屏后没回落"或"回落了两次"。
        // 现在所有显示/隐藏都过 ShowView / HideView，shownMain/Float/Tray 是唯一真相。
        // =====================================================================
        FullScreenAutoSwitch? auto = null;
        FullScreenAutoSwitch.View view = FullScreenAutoSwitch.View.Main;

        // ---- 三视图可见状态（M4.26）----
        // 8.1 原定"任意时刻只有一个在显示"，按用户要求改成**可同时显示**：
        // 大窗口看明细、悬浮窗压桌面、任务栏条贴任务栏，三者互不干涉。
        // 这三个 bool 是唯一真相（不再靠 view 推断），启动分支各自置位。
        bool shownMain = false;
        bool shownFloat = false;
        bool shownTray = false;

        bool IsShown(FullScreenAutoSwitch.View v) => v switch
        {
            FullScreenAutoSwitch.View.Main => shownMain,
            FullScreenAutoSwitch.View.Float => shownFloat,
            _ => shownTray,
        };

        string ComboText() => $"main={shownMain} float={shownFloat} tray={shownTray}";

        // M4.26：三视图可叠加之后，"关大窗口"不再等于退出程序 ——
        // 只要还有悬浮窗/任务栏条在显示，就只把大窗口收起来（退出统一走托盘「退出」）。
        // 一个都没显示时保持老行为：关窗即退出（OnMainWindowClose）。
        // 注：Application.Shutdown() 期间 WPF 会忽略 Cancel，所以托盘退出/探针收尾不受影响。
        window.Closing += (_, ce) =>
        {
            if (!shownFloat && !shownTray) return;

            ce.Cancel = true;
            if (window.IsVisible) window.Hide();
            shownMain = false;
            Interop.Trace.Log("view", $"大窗口被关闭 → 只收起（其它视图还在）组合={ComboText()}");
        };

        // 在这里先声明、稍后再建：热键的按下回调要读 view / 走 ShowView，
        // 而设置页（更早定义）又要拿着它改键。C# 不允许 lambda 引用后面才声明的局部变量，
        // 所以声明放在最前面，实例化仍然等状态机就绪。
        Hotkey? hotkey = null;

        FloatWindow? floating = null;

        FloatWindow EnsureFloat()
        {
            if (floating is not null) return floating;

            floating = new FloatWindow(window.State, ConfigStore.Current);
            floating.MainWindowRequested += (_, _) => ShowView(FullScreenAutoSwitch.View.Main, "float→main");
            return floating;
        }

        TaskbarWindow? taskbar = null;

        TaskbarWindow EnsureTaskbar()
        {
            if (taskbar is not null) return taskbar;

            taskbar = new TaskbarWindow(window.State, ConfigStore.Current);
            taskbar.MainWindowRequested += (_, _) => ShowView(FullScreenAutoSwitch.View.Main, "tray→main");
            taskbar.FloatRequested += (_, _) => ShowView(FullScreenAutoSwitch.View.Float, "tray→float");

            // explorer 重启时原窗口可能已被系统销毁，只能重建（7.7）
            taskbar.RecreateRequested += (_, _) =>
            {
                // 按"条本来是不是开着"恢复，不能用 view 推断：三视图叠加后 view 只表示最近操作过谁。
                bool wasShowing = shownTray;
                try { taskbar?.Close(); } catch { /* 窗口已废，Close 可能抛 */ }
                taskbar = null;
                shownTray = false;

                // 重建后要恢复原状：全屏自动切换场景下条是被动重建的，
                // 这时若无条件 Show 会无视"全屏期间条应该收着"。
                if (wasShowing) EnsureTaskbar().Show();
                shownTray = wasShowing;
            };
            return taskbar;
        }

        /// <summary>
        /// 显示某个视图。**不动其它视图**（M4.26：三视图可同时显示，原 A-06「不得同时可见」作废）。
        /// userInitiated=false 表示这是全屏自动切换自己发起的（8.2 要区分来源）。
        /// </summary>
        /// <param name="activate">
        /// 显不显示后立刻抢前台。全屏自动切换传 false（M4.22）：那时前台是游戏，
        /// 一个覆盖层把自己顶成前台，等于把键盘鼠标从游戏里抢走（有些游戏还会因此暂停/掉帧）。
        /// 窗口本身是 topmost，不激活也照样在最上面。
        /// </param>
        void ShowView(FullScreenAutoSwitch.View target, string reason, bool userInitiated = true,
                      bool activate = true)
        {
            FullScreenAutoSwitch.View previous = view;
            view = target;

            switch (target)
            {
                case FullScreenAutoSwitch.View.Main:
                    // 大窗口的 XAML 是 ShowActivated 默认值（true），Show() 自己就会抢前台。
                    // 要"显示但不抢"时（全屏游戏里恢复视图）必须临时把它关掉 ——
                    // WPF 在 Show 那一刻读这个属性，之后再改回去不影响已经显示出来的窗口。
                    bool wasShowActivated = window.ShowActivated;
                    if (!activate) window.ShowActivated = false;
                    window.Show();
                    window.ShowActivated = wasShowActivated;
                    shownMain = true;
                    if (activate) window.Activate();
                    break;
                case FullScreenAutoSwitch.View.Float:
                    FloatWindow f = EnsureFloat();
                    if (e.Args.Contains("--no-relayout-on-show")) f.RelayoutOnShow = false;

                    // 悬浮窗在 XAML 里就是 ShowActivated="False"：Show() 本身不抢前台，
                    // 要前台时显式 Activate()。这样"全屏自动切"能做到完全不打扰游戏。
                    f.Show();
                    shownFloat = true;
                    if (activate) f.Activate();
                    break;
                case FullScreenAutoSwitch.View.Taskbar:
                    EnsureTaskbar().Show();
                    shownTray = true;
                    break;
            }

            // 状态机同步：CurrentView 现在只表示"最近操作的是谁"，可见性由 IsShown 回调回答。
            if (auto is not null)
            {
                auto.CurrentView = view;

                // 用户主动把悬浮窗显示出来 = manual（退出全屏不回落）。
                // 注意"切到别的视图"**不再**等于离开悬浮态 —— 三视图可以同时开着。
                if (userInitiated && target == FullScreenAutoSwitch.View.Float) auto.NoteUserChoseFloat();

                // 可见组合变了，探测的需求也跟着变（只有条 / auto 浮窗才需要探测）。
                auto.SyncTimer();
            }

            Interop.Trace.Log("view", $"show {target} ← {previous} by={reason} user={userInitiated} 组合={ComboText()}");
        }

        /// <summary>隐藏某个视图。用户亲手藏悬浮窗时顺带把来源归位（8.2）。</summary>
        void HideView(FullScreenAutoSwitch.View target, string reason, bool userInitiated = true)
        {
            switch (target)
            {
                case FullScreenAutoSwitch.View.Main:
                    if (window.IsVisible) window.Hide();
                    shownMain = false;
                    break;
                case FullScreenAutoSwitch.View.Float:
                    floating?.Hide();
                    shownFloat = false;
                    break;
                case FullScreenAutoSwitch.View.Taskbar:
                    taskbar?.Hide();
                    shownTray = false;
                    break;
            }

            if (auto is not null)
            {
                // 用户自己关掉悬浮窗 = 离开悬浮态：来源归位、全屏候选主体作废。
                // 全屏自动切换收回(auto)浮窗时传 userInitiated=false，不碰来源（状态机自己会归位）。
                if (userInitiated && target == FullScreenAutoSwitch.View.Float) auto.NoteUserLeftFloat();
                auto.SyncTimer();
            }

            Interop.Trace.Log("view", $"hide {target} by={reason} user={userInitiated} 组合={ComboText()}");
        }

        void ToggleView(FullScreenAutoSwitch.View target, string reason)
        {
            if (IsShown(target)) HideView(target, reason);
            else ShowView(target, reason);
        }

        // ---- 一键全隐藏 / 恢复（托盘左键与全局热键，M4.26）----
        // 三视图可叠加后，"切换当前模式"没有意义了；这两个动作要的是
        // 「一眼看屏 / 一眼收回」，所以记下藏之前的组合，恢复时原样摆回来。
        bool comboMain = true, comboFloat = false, comboTray = false;

        void HideAllViews(string reason)
        {
            comboMain = shownMain;
            comboFloat = shownFloat;
            comboTray = shownTray;

            // 顺序：先收覆盖层再收大窗口。反过来会有一瞬间只剩覆盖层压着空桌面。
            if (shownFloat) HideView(FullScreenAutoSwitch.View.Float, reason);
            if (shownTray) HideView(FullScreenAutoSwitch.View.Taskbar, reason);
            if (shownMain) HideView(FullScreenAutoSwitch.View.Main, reason);
        }

        void RestoreViews(string reason)
        {
            if (!comboMain && !comboFloat && !comboTray) comboMain = true;   // 从没显示过 → 至少给大窗口

            // 全屏游戏里恢复时**不要**把大窗口顶到前台：热键/托盘多半是"我想看一眼数据"，
            // 把键鼠从游戏里抽走是要命的行为（M4.22 同一条教训，只是这次主角从覆盖层换成了大窗口）。
            bool activateMain = auto is null || !auto.IsFullScreenSteady;

            if (comboMain) ShowView(FullScreenAutoSwitch.View.Main, reason, activate: activateMain);
            if (comboFloat) ShowView(FullScreenAutoSwitch.View.Float, reason);
            if (comboTray) ShowView(FullScreenAutoSwitch.View.Taskbar, reason);
        }

        void ToggleAllViews(string reason)
        {
            if (shownMain || shownFloat || shownTray) HideAllViews(reason);
            else RestoreViews(reason);
        }

        // 大窗口标题栏的「切到悬浮窗」按钮走这条回调：MainWindow 不认识 FloatWindow，
        // 也不该认识——两者的关系由 App 接线，窗口之间不互相 new。
        window.FloatRequested = () => ShowView(FullScreenAutoSwitch.View.Float, "main→float");

        // 同上，「切到任务栏条」（M3）。这条让大窗口能进入任务栏模式——
        // 否则以 start-admin.vbs 启动后就永远到不了任务栏条（只能靠 --tray 启动）。
        window.TrayRequested = () => ShowView(FullScreenAutoSwitch.View.Taskbar, "main→tray");

        // G-08 设置页。窗口不放进三视图的切换序列 —— 它不是"监控视图"，
        // 而是叠加在任一视图上的工具窗口，所以 App 单独持有它，ShowView 不管它。
        SettingsWindow? settings = null;

        void OpenSettings()
        {
            // 已经开着就提到前面，不建第二个：两个设置页各持一份控件状态，
            // 改完这个再改那个，最后写盘的是后关的那个。
            if (settings is { IsLoaded: true })
            {
                settings.Activate();
                return;
            }

            settings = new SettingsWindow(new SettingsContext
            {
                Config = ConfigStore.Current,
                CurrentView = () => view,
                ShowView = target => ShowView(target, "settings"),
                GetFloat = () => floating,
                GetTaskbar = () => taskbar,
                GetProviders = () => window.Host.Providers,
                GetPingTarget = () => window.Host.PingTarget,
                SetPingTarget = window.Host.SetPingTarget,
                GetSampleInterval = () => window.Host.SampleIntervalMs,
                SetSampleInterval = window.Host.SetSampleInterval,
                SetFullScreenAutoSwitch = on =>
                {
                    if (auto is null) return;
                    auto.Enabled = on;
                    auto.SyncTimer();
                },
                SetFullScreenHold = holdMs =>
                {
                    // 防抖档位改了就立即生效：状态机下一轮判定按新门限走。
                    // 已经攒着的"连续全屏时长"不重置 —— 用户是在观察中调档位，
                    // 清掉计时反而会让"刚把门限从 2s 调到 0.5s"这一下不立刻切过去。
                    auto?.SetHold(holdMs);
                },
                GetHotkey = () => hotkey?.Applied,
                SetHotkey = gesture =>
                {
                    if (hotkey is null) return "热键模块未启用（测量模式）";

                    if (!hotkey.Apply(gesture)) return hotkey.LastError ?? "注册失败";

                    // 存规范化之后的写法：用户敲 Ctrl+alt+c 也应该落盘成 Ctrl+Alt+C，
                    // 否则下次启动按字符串解析时又是一遍大小写转换
                    ConfigStore.Current.Hotkey = hotkey.Applied ?? gesture;
                    ConfigStore.SaveCurrent();
                    return null;
                },
                SuspendHotkey = on =>
                {
                    if (on) hotkey?.Suspend();
                    else hotkey?.Resume();
                },
                Version = "v" + (typeof(App).Assembly.GetName().Version?.ToString(3) ?? "0.1.0"),
                ConfigPath = ConfigStore.Path,
            });

            settings.Closed += (_, _) => settings = null;
            settings.Show();

            // 必须 Activate：不激活就是一个后台窗口，DWM 对后台窗口的合成会降级/节流，
            // 实测表现为"窗口只画了一部分，左边还是桌面上别的画面"。用户点设置，
            // 本来也是要它到前面来。
            settings.Activate();

            Interop.Trace.Log("settings", "打开设置页");
        }

        window.SettingsRequested = OpenSettings;

        // G-05：收到"又启动了一次"就显示大窗口。用户连点两次图标的意图就是"我要看到它"，
        // 而它可能正以任务栏条 / 悬浮窗的形态在屏幕上（甚至被全屏检测藏起来了）。
        // 必须走 ShowView 而不是直接 Show，否则状态机与事实不符（全屏回落会打架）。
        if (!measurement)
        {
            SingleInstance.ActivateRequested += () =>
            {
                Interop.Trace.Log("single", "→ 收到激活请求，准备显示大窗口");
                Dispatcher.BeginInvoke(new Action(() =>
                {
                    Interop.Trace.Log("single", "→ UI 线程执行 ShowView(Main)");
                    ShowView(FullScreenAutoSwitch.View.Main, "second-instance");
                }));
            };

            Interop.Trace.Log("single", "activate-handler 已订阅");
        }

        // 托盘图标（G-01 / G-02）。7.6 的 Q-12 采纳「任务栏条默认穿透，全部操作走托盘」，
        // 所以托盘不是可选装饰：穿透之后条上收不到任何鼠标事件，托盘是唯一出口。
        // 截图 / 压测 / --hidden 这类测量模式不挂，否则探针会在托盘里留下图标，
        // 而且托盘窗口会掺进窗口枚举类的结果里，影响判定。
        bool measurementMode = e.Args.Contains("--shot") || e.Args.Contains("--shot-screen")
                               || e.Args.Contains("--stats") || e.Args.Contains("--hidden");
        if (!measurementMode) _tray = CreateTray();

        TrayIcon CreateTray()
        {
            string iconPath = Path.Combine(AppContext.BaseDirectory, "assets", "copul.ico");
            var icon = new TrayIcon(iconPath, "CoPul 芯脉");

            // 装没装上必须打日志：Shell_NotifyIcon 失败时返回值是 false，界面毫无提示，
            // 表现只是"托盘里没有图标"，和"图标被 Win11 收进溢出区"肉眼无法区分
            Interop.Trace.Log("tray", $"added={icon.IsAdded} ico={iconPath} exists={File.Exists(iconPath)}");

            // 左键 = 全部隐藏 / 恢复上一次的组合（G-01，M4.26 改）。
            // 三视图可以叠加之后"切换当前模式"没有意义了；这里要的是"一眼清屏、一眼收回"，
            // 所以记住隐藏之前的组合，恢复时原样摆回来（全部隐藏时提示里写「已隐藏」）。
            icon.LeftClick = () =>
            {
                ToggleAllViews("tray-toggle");
                UpdateTrayTooltip(icon);
            };

            // 右键 = 全局菜单（G-02）。设置页 / 开机自启还没做，先给灰色条目占位，
            // 这样用户知道功能存在、只是没做完，而不是以为程序残缺。
            icon.RightClick = () =>
            {
                var entries = new List<TrayMenuEntry>
                {
                    // M4.26：三项从"三选一"变成**独立开关**（打勾 = 正在显示，再点一次收起）。
                    // 命令串仍是 view:xxx —— 语义变成 toggle，已有探针脚本不用改。
                    TrayMenuEntry.Item("大窗口", "view:main", isChecked: shownMain),
                    TrayMenuEntry.Item("悬浮窗", "view:float", isChecked: shownFloat),
                    TrayMenuEntry.Item("任务栏条", "view:taskbar", isChecked: shownTray),
                    TrayMenuEntry.Item("全部隐藏/恢复", "view:toggle-all"),
                    TrayMenuEntry.Separator(),

                    // 7.4 / Q-03 的字段开关。放在托盘而不是只在条上右键：
                    // 穿透是默认状态（Q-12），条上根本收不到右键，托盘是唯一出口。
                    TrayMenuEntry.Submenu("显示字段", BuildFieldEntries()),

                    // 穿透开着时条上收不到右键，文字颜色就调不了 —— 这里给唯一出口
                    TrayMenuEntry.Item("任务栏条可点击", "toggle:clickthrough",
                                       isChecked: !ConfigStore.Current.TaskbarClickThrough),

                    // 悬浮窗穿透（M4.21）同理：穿透时悬浮窗收不到鼠标，
                    // 这条是关掉它的三个出口之一（另两个是设置页与全局热键切视图）。
                    TrayMenuEntry.Item("悬浮窗可点击", "toggle:floatthrough",
                                       isChecked: !ConfigStore.Current.FloatClickThrough),
                    TrayMenuEntry.Separator(),
                    TrayMenuEntry.Item("设置…", "settings"),
                    TrayMenuEntry.Item("开机自启", "toggle:autostart", isChecked: AutoStart.IsEnabled()),
                    TrayMenuEntry.Separator(),
                    TrayMenuEntry.Item("退出", "exit"),
                };

                RunTrayCommand(icon.ShowMenu(entries));
                UpdateTrayTooltip(icon);
            };

            // 八项字段勾选。只剩一个可见时把它置灰 —— 让"不能再关"看得见，
            // 而不是点了没反应（全关之后条只剩 1 px 宽，用户只会以为程序坏了）。
            List<TrayMenuEntry> BuildFieldEntries()
            {
                AppConfig cfg = ConfigStore.Current;
                int visibleCount = TrayFields.VisibleCount(cfg);

                var items = new List<TrayMenuEntry>(TrayFields.Keys.Length);
                for (int i = 0; i < TrayFields.Keys.Length; i++)
                {
                    string key = TrayFields.Keys[i];
                    bool on = TrayFields.IsVisible(cfg, key);

                    items.Add(TrayMenuEntry.Item(TrayFields.Labels[i], "field:" + key,
                                                 enabled: !(on && visibleCount <= 1),
                                                 isChecked: on));
                }

                return items;
            }

            return icon;
        }

        /// <summary>
        /// 托盘菜单命令的分发。抽出来是为了能被 <c>--tray-menu-probe</c> 直接调用：
        /// 菜单的弹出、勾选、灰色项、子菜单已经用真实截图验过，剩下的
        /// 「命令 → 动作」这一环不该靠模拟鼠标去点（本机 SetCursorPos 不可靠，
        /// 光标位置会被真实鼠标挪走），而是让探针走同一份 switch。
        /// </summary>
        void RunTrayCommand(string? cmd)
        {
            if (string.IsNullOrEmpty(cmd)) return;

            // 字段开关（7.4）：条实例可能还没建（用户没进过任务栏模式），
            // 那种情况只改配置 + 落盘，条建出来时会从配置里读到。
            if (cmd.StartsWith("field:", StringComparison.Ordinal))
            {
                string key = cmd[6..];

                if (!TrayFields.Toggle(ConfigStore.Current, key))
                {
                    Interop.Trace.Log("tray", $"field: 拒绝关闭最后一个可见字段 {key}");
                    return;
                }

                ConfigStore.SaveCurrent();
                taskbar?.ReloadFields();
                Interop.Trace.Log("tray", $"field: {key} → " +
                    $"{(TrayFields.IsVisible(ConfigStore.Current, key) ? "显示" : "隐藏")}");
                return;
            }

            switch (cmd)
            {
                // M4.26：三条改成开关（打勾=显示中，再点收起），另加一条整组切换。
                case "view:main": ToggleView(FullScreenAutoSwitch.View.Main, "tray-menu"); break;
                case "view:float": ToggleView(FullScreenAutoSwitch.View.Float, "tray-menu"); break;
                case "view:taskbar": ToggleView(FullScreenAutoSwitch.View.Taskbar, "tray-menu"); break;
                case "view:toggle-all": ToggleAllViews("tray-menu"); break;

                case "toggle:clickthrough":
                    bool through = !ConfigStore.Current.TaskbarClickThrough;
                    if (taskbar is not null) taskbar.ClickThrough = through;
                    else
                    {
                        ConfigStore.Current.TaskbarClickThrough = through;
                        ConfigStore.SaveCurrent();
                    }
                    break;

                case "toggle:floatthrough":
                    // 悬浮窗穿透（M4.21）。窗口可能还没建（用户没进过悬浮窗模式），
                    // 那种情况只改配置 —— 建出来时会从配置里读到。
                    bool floatThrough = !ConfigStore.Current.FloatClickThrough;
                    if (floating is not null && floating.IsLoaded) floating.ClickThrough = floatThrough;
                    else
                    {
                        ConfigStore.Current.FloatClickThrough = floatThrough;
                        ConfigStore.SaveCurrent();
                    }
                    Interop.Trace.Log("tray", $"悬浮窗穿透 → {floatThrough}");
                    break;

                case "toggle:autostart":
                    // G-04：走计划任务（登录触发 + 最高权限）。未提权时这里会弹一次 UAC ——
                    // 是刻意的：Run 键启动不了需要提权的程序，那样自启只会静默失效。
                    bool wantAuto = !AutoStart.IsEnabled();
                    bool autoOk = wantAuto ? AutoStart.Enable() : AutoStart.Disable();
                    Interop.Trace.Log("tray", $"开机自启 → {(wantAuto ? "开" : "关")} ok={autoOk}");
                    break;

                case "settings": OpenSettings(); break;

                case "exit": Shutdown(); break;
            }
        }

        // 自测：--tray-menu-probe <命令> 启动 1.5 秒后跑一次菜单命令，
        // 结果进 trace（view=... 那行）。配合 --shot 截图就能确认切过去了。
        // 参数用命令串（"view:taskbar" / "field:pwr"）而不是编号：
        // 编号会随菜单结构调整而错位，错位不报错、只会点到别的功能。
        string? probeCmd = ArgValue(e.Args, "--tray-menu-probe");
        if (!string.IsNullOrEmpty(probeCmd))
        {
            var probeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            probeTimer.Tick += (_, _) =>
            {
                probeTimer.Stop();
                Interop.Trace.Log("tray", $"probe: menu-command={probeCmd}");
                RunTrayCommand(probeCmd);
            };
            probeTimer.Start();
        }

        // 自测：--settings [--settings-page diag] [--settings-probe 命令串] 启动后打开设置页，
        // 供 --shot 拍整页（用 RTB，不用 --shot-screen：桌面上有持续置顶的游戏浮层，
        // 抓屏会把设置页切成两半，实测两次都是别人的画面）。
        string? settingsProbe = ArgValue(e.Args, "--settings-probe");
        if (e.Args.Contains("--settings") || settingsProbe is not null)
        {
            string settingsPage = ArgValue(e.Args, "--settings-page") ?? "general";

            var settingsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            settingsTimer.Tick += (_, _) =>
            {
                settingsTimer.Stop();
                OpenSettings();

                if (string.Equals(settingsPage, "diag", StringComparison.OrdinalIgnoreCase))
                    settings?.ShowDiagTab();

                if (!string.IsNullOrEmpty(settingsProbe)) settings?.RunProbe(settingsProbe);
            };
            settingsTimer.Start();
        }

        void UpdateTrayTooltip(TrayIcon icon)
        {
            // M4.26：三视图可同时显示，提示里把当前开着的都列出来（"已隐藏"表示全收起了）。
            var parts = new List<string>(3);
            if (shownMain) parts.Add("大窗口");
            if (shownFloat) parts.Add("悬浮窗");
            if (shownTray) parts.Add("任务栏条");
            string name = parts.Count == 0 ? "已隐藏" : string.Join("+", parts);

            // 悬停提示顺带报一下采集状态：托盘是唯一常驻可见的元素，
            // 采集降级（例如没有管理员权限读不到温度）在这里最容易被发现
            icon.SetTooltip($"CoPul 芯脉 · {name} · {window.State.RunStateText}");
        }

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

        // ---- 纯探测型探针：必须在任何窗口 Show 之前分流 ----
        //
        // 为什么位置这么靠前：--fullscreen-probe 要读**真实的**前台窗口来判断全屏，
        // 而下面 `else if (!hidden) window.Show()` 会把我们自己的大窗口推到前台，
        // 于是探测永远得到 `veto:self`（实测踩到，探针等于什么都没测）。
        // 探针不该建任何窗口，就不该等窗口代码跑完。
        string? fsProbePathEarly = ArgValue(e.Args, "--fullscreen-probe");
        if (fsProbePathEarly is not null)
        {
            Probes.FullScreenProbe.Run(fsProbePathEarly, e.Args.Contains("--fs-fake"),
                int.TryParse(ArgValue(e.Args, "--fs-polls"), out int fp) && fp > 0 ? fp : 6,
                timer => timer.Start(),
                Shutdown);
            return;
        }

        if (wantFloat)
        {
            FloatWindow f = EnsureFloat();
            f.CenterOnScreen = e.Args.Contains("--float-center");   // 验收毛玻璃时用

            // 自测开关必须在 Show 之前设：Show 之后 HWND 就绪，玻璃会按当时的值画一遍。
            // 这些参数是在改外观，如果照常落盘，跑一次验收就会把用户的配置改成"深色第 1 档"。
            bool glassOverride = e.Args.Contains("--glass-dark")
                                 || ArgValue(e.Args, "--glass-level") is not null
                                 || ArgValue(e.Args, "--float-scale") is not null
                                 || ArgValue(e.Args, "--float-edge") is not null
                                 || ArgValue(e.Args, "--float-text") is not null
                                 || ArgValue(e.Args, "--float-through") is not null
                                 || ArgValue(e.Args, "--float-topmost") is not null
                                 || e.Args.Contains("--float-vertical")
                                 || e.Args.Contains("--float-row");
            if (glassOverride) f.PersistToConfig = false;

            // --float-persist-probe：故意要写盘，用来验"改设置 → 存 config.json"这条链路。
            // 必须显式把 PersistToConfig 打开：上面那些外观参数默认一律不落盘，
            // 而这个探针的用途恰恰是"连外观一起存"——不打开它就永远验不到东西。
            if (e.Args.Contains("--float-persist-probe"))
            {
                f.PersistProbe = true;
                f.PersistToConfig = true;
            }

            // --float-menu-probe：首帧后直接弹右键菜单，配合整屏抓屏验"竖向窗口下菜单弹在哪"
            if (e.Args.Contains("--float-menu-probe")) f.MenuProbe = true;

            if (e.Args.Contains("--glass-dark")) f.Theme = FloatWindow.GlassTheme.Dark;
            if (int.TryParse(ArgValue(e.Args, "--accent-flags"), out int flags)) WindowGlass.AccentFlagsOverride = flags;
            if (int.TryParse(ArgValue(e.Args, "--accent-state"), out int astate)) WindowGlass.AccentStateOverride = astate;
            if (int.TryParse(ArgValue(e.Args, "--glass-level"), out int lv)) f.GlassLevel = lv;

            // --float-scale 125：验收三档缩放用（100 / 125 / 150，越界会夹到最近档）
            if (int.TryParse(ArgValue(e.Args, "--float-scale"), out int sc)) f.ScalePercent = sc;

            // --float-vertical：竖向排列（M4.10），验收用；不落盘（见上面的 glassOverride）
            // --float-row：强制横向。配置里可能存着 vertical=true，验收横向时不能靠"删配置"
            if (e.Args.Contains("--float-vertical")) f.IsVertical = true;
            else if (e.Args.Contains("--float-row")) f.IsVertical = false;

            // --float-edge none|faint|normal：静止描边档位（M4.15），配合 --shot-screen 量亮度
            if (ArgValue(e.Args, "--float-edge") is { } edgeMode) f.EdgeMode = edgeMode;

            // --no-shadow：关掉 DWM 系统阴影（6.4），用于排查阴影/边框异常
            if (e.Args.Contains("--no-shadow")) f.SystemShadow = false;

            // --float-text fps:#FF7FB8,cpu:#00E5FF：悬浮窗**逐字段**文字色（M4.20）。
            // 只写一个色（--float-text #FF7FB8）= 九格一起设。配合 --shot 量每格实际前景色。
            // 任务栏那边用 --tray-text 同一套写法。
            if (ArgValue(e.Args, "--float-text") is { } textSpec)
            {
                foreach (KeyValuePair<string, string> kv in FloatInk.ParsePairs(textSpec))
                    f.SetFieldColor(kv.Key, kv.Value);
            }

            // --float-text-flat 1：连阈值色一起盖掉（整条一个颜色），A/B 用
            if (ArgValue(e.Args, "--float-text-flat") is { } flat)
                f.TextKeepThreshold = !(flat == "1" || flat.Equals("true", StringComparison.OrdinalIgnoreCase));

            // --float-force-level warn|danger：强制全部格子进阈值态（M4.19），
            // 用来验"自定义文字色下阈值色是否仍然压过它"。本机平时一项都超不了阈值。
            if (ArgValue(e.Args, "--float-force-level") is { } forceLevel
                && Enum.TryParse<Level>(forceLevel, ignoreCase: true, out var fl))
                DashboardState.ForceLevel = fl;

            // --float-border none|default|#RRGGBB：DWM 那条 1px 窗口边框（M4.13）。
            // 默认 none —— 拖动时浅色主题会整圈亮成白边；default 放回系统色，纯色用于判定归属。
            if (ArgValue(e.Args, "--float-border") is string borderMode)
                f.BorderMode = borderMode;

            // --float-through 1：鼠标穿透（M4.21）。配合下面的探针量 WM_NCHITTEST 计数。
            // --float-topmost 0：关掉"每秒提回顶层"，A/B 用。
            if (ArgValue(e.Args, "--float-through") is { } thrSpec)
                f.ClickThrough = thrSpec is "1" or "true" or "on";

            // --float-hittest-probe：给自己发一条 WM_NCHITTEST，结论写进 sidecar 的 interact 行。
            // 穿透只改命中测试的结论 —— WindowFromPoint 不做命中测试、截图也看不出差别，
            // 只有问窗口本人才有答案（--float-through 1 时应为 -1，默认应为 1）。
            if (e.Args.Contains("--float-hittest-probe")) f.HitTestProbe = true;

            // --float-ztop-probe：每秒把"我"上面的 Z 序链打进 sidecar（M4.21 判据排错用）
            if (e.Args.Contains("--float-ztop-probe")) f.ZOrderProbe = true;

            // --float-ztop-own：自测时把自家可见窗口也算遮挡（验提顶链路，生产不打开）
            if (e.Args.Contains("--float-ztop-own")) f.TopmostIncludeOwn = true;

            // --float-blocker：造一个**本进程**的置顶可见窗口压在悬浮窗上（M4.21 自测）。
            //
            // 为什么不用外部程序来压：CoPul 是提权进程，UIPI 会保证高完整性窗口永远排在
            // 低完整性窗口之上 —— 未提权的 Python 窗口根本压不住它（实测：整屏 blocker
            // 建出来了，但悬浮窗的 Z 序链里一次都没出现）。同进程窗口才压得上去。
            if (ArgValue(e.Args, "--float-blocker") is { } blockerSec)
            {
                var blocker = new Window
                {
                    WindowStyle = WindowStyle.None,
                    ResizeMode = ResizeMode.NoResize,
                    ShowInTaskbar = false,
                    Topmost = true,
                    Background = Brushes.DarkOrange,
                    Left = 0, Top = 300, Width = 900, Height = 400,   // 盖住左下角那片区域
                };
                // 必须**晚于**悬浮窗 Show：同一 topmost 组里后 Show 的排在上面，
                // 在参数区直接 Show 的话它反而落在悬浮窗下面，一次都遮挡不到（实测）。
                var pop = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
                pop.Tick += (_, _) => { pop.Stop(); blocker.Show(); };
                pop.Start();

                if (double.TryParse(blockerSec, out double sec) && sec > 0)
                {
                    var kill = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5 + sec) };
                    kill.Tick += (_, _) => { kill.Stop(); blocker.Close(); };
                    kill.Start();
                }
            }

            if (ArgValue(e.Args, "--float-topmost") is { } topSpec)
                f.AlwaysOnTop = !(topSpec is "0" or "false" or "off");

            // --no-relayout-on-show：关掉"每次显示都强制重排"，A/B 对照用（M4.14）
            if (e.Args.Contains("--no-relayout-on-show")) f.RelayoutOnShow = false;

            // --float-drag-probe keep|release：进入/退出"拖动外观"，验粉色描边与松手还原（M4.14）
            if (ArgValue(e.Args, "--float-drag-probe") is { } dragProbe) f.DragProbe = dragProbe;

            // --float-field-probe ram,vram：首帧后翻字段开关，验条子跟着收窄/收矮（M4.14）
            if (ArgValue(e.Args, "--float-field-probe") is { } fieldProbe) f.FieldProbe = fieldProbe;

            // --no-frame-guard：关掉"发现框架被算回来就纠正"，A/B 用（M4.16）
            if (e.Args.Contains("--no-frame-guard")) f.FrameGuard = false;

            // --no-ncactivate-guard：失焦时允许 DefWindowProc 重画非客户区（A/B，M4.17）
            if (e.Args.Contains("--no-ncactivate-guard")) f.NcActivateGuard = false;

            // --no-nccalc：不吃掉 DWM 那条 8px 框架，自测白框真身用（M4.16）
            if (e.Args.Contains("--no-nccalc")) f.EatFrame = false;

            // --float-move-focus-probe move,defocus,drag：复现"挪过位置后点别处冒白框"（M4.16）
            if (ArgValue(e.Args, "--float-move-focus-probe") is { } mfProbe) f.MoveFocusProbe = mfProbe;

            // 上面这两个探针同样不许落盘：它们会真的改字段开关，而 ReloadFields 那条路
            // 末尾要 SavePlacement（写 config.json）。实测踩到过 —— 跑一次 --float-field-probe
            // 就把用户的字段永久关掉了，之后每次验收量到的都是"上次探针留下的尺寸"。
            if (f.DragProbe is not null || f.FieldProbe is not null) f.PersistToConfig = false;

            // --float-snap-probe x,y：摆到 (x,y) 再跑吸附，结果写进 shots 的 sidecar 文件
            string? snapProbe = ArgValue(e.Args, "--float-snap-probe");
            if (snapProbe is not null)
            {
                string[] parts = snapProbe.Split(',');
                if (parts.Length == 2 && int.TryParse(parts[0], out int px) && int.TryParse(parts[1], out int py))
                    f.SnapProbe = (px, py);
            }

            f.Show();

            // XAML 里 ShowActivated=False（M4.22），Show() 不抢前台；
            // 这条是用户自己选了"以悬浮窗启动"，按手动态补一次 Activate。
            if (!e.Args.Contains("--no-float-activate")) f.Activate();
            view = FullScreenAutoSwitch.View.Float;
            shownFloat = true;
        }
        else if (e.Args.Contains("--tray"))
        {
            // 任务栏模式（第 7 章）。大窗口不 Show，但采集照常跑在它身上。
            TaskbarWindow t = EnsureTaskbar();
            t.Show();
            view = FullScreenAutoSwitch.View.Taskbar;
            shownTray = true;

            // --tray-text fps:#00E5FF,ram:#4FB3A6：任务栏**逐字段**文字色（M4.20），
            // 写法与 --float-text 完全一样，只写个色就是九格一起设。
            // 必须在 Show 之前：ApplyInk 在 ContentRendered 才第一次推色，晚一步就漏。
            if (ArgValue(e.Args, "--tray-text") is { } trayTextSpec)
            {
                t.PersistToConfig = false;      // 同 --float-*：验收不该改用户的配置
                foreach (KeyValuePair<string, string> kv in FloatInk.ParsePairs(trayTextSpec))
                    t.SetFieldColor(kv.Key, kv.Value);
            }

            // --tray-force-level warn|danger：强制全部格子进阈值态（与 --float-force-level 同一件事）
            if (ArgValue(e.Args, "--tray-force-level") is { } trayLevel
                && Enum.TryParse<Level>(trayLevel, ignoreCase: true, out var tl))
                t.ApplyForceLevel(tl);

            // 自测：--tray-restart-probe 在 3 秒后重启 explorer，
            // 配合 --wait 14000 就能验证 7.7「重启后自动恢复」
            if (e.Args.Contains("--tray-restart-probe"))
            {
                var rt = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                rt.Tick += (_, _) =>
                {
                    rt.Stop();
                    File.WriteAllText(Path.Combine(Path.GetTempPath(), "copul-explorer.log"),
                                      Taskbar.RestartExplorer());
                };
                rt.Start();
            }
        }
        else if (!hidden)
        {
            window.Show();
            shownMain = true;
        }

        // 启动分支决定了初始组合，托盘提示要在这一刻就对上（否则第一次悬停会显示"已隐藏"）。
        if (_tray is not null) UpdateTrayTooltip(_tray);

        // ---- 全屏自动切换（第 8 章 / F-11）----
        // 放在窗口 Show 之后：状态机要知道当前视图，而视图是上面这段决定的。
        // 大窗口模式（含 --hidden）同样要装 —— 8.6 规定它"不动作"，但状态机要能装到。
        //
        // 探针模式下**不启动**状态机：--view-probe 要逐步切视图并数可见窗口，
        // 而状态机每 500ms 会按真实全屏状态抢切一次，两边互相打架（实测踩到过：
        // 游戏正在全屏时跑 view-probe，第 3 步就被状态机切走了）。
        // 纯探测的 --fullscreen-probe 已在更前面分流（它不该建任何窗口）。
        bool probeMode = ArgValue(e.Args, "--view-probe") is not null;
        if (!probeMode)
        {
            auto = new FullScreenAutoSwitch { CurrentView = view, IsShown = IsShown };

            // --fs-hold <ms>：命令行覆盖防抖档位（取证用，不落盘）。
            int holdMs = ConfigStore.Current.FullScreenHoldMs;
            if (ArgValue(e.Args, "--fs-hold") is { } holdArg
                && int.TryParse(holdArg.Split(',')[0].Trim(), out int parsedHold))
            {
                holdMs = parsedHold;
            }

            auto.ApplyConfig(ConfigStore.Current.FullScreenAutoSwitch && !e.Args.Contains("--no-fullscreen-switch"),
                             holdMs);

            // --no-fs-candidate：关掉"前台是自己时改判上次的全屏窗口"（M4.22 的 A/B 对照）。
            // 关掉就是修之前的旧行为 —— 点一下悬浮窗就会被判成非全屏并回落任务栏。
            if (e.Args.Contains("--no-fs-candidate")) auto.UseFullHwndCandidate = false;

            // 两个自动动作都必须走 ShowView / HideView，不能自己 Show/Hide：
            // 绕过编排的话 shownXxx 与事实不符，下一次判定就跟着错（M4.26 后这是唯一的真相）。
            auto.EnterAutoFloat = _ =>
            {
                // 全屏第一件事：把任务栏条收起来。它贴着任务栏又是 topmost，
                // 不收就直接压在游戏画面上（M4.26 前是靠"切到悬浮窗"顺带藏掉的）。
                if (shownTray) HideView(FullScreenAutoSwitch.View.Taskbar, "fullscreen-enter",
                                        userInitiated: false);

                // 悬浮窗本来就在显示就不重复 Show（重排会闪一下），否则才弹出来。
                // M4.22：不再 PlaceOnMonitor —— 与手动打开悬浮窗同一个窗口、同一个位置。
                if (!shownFloat)
                    ShowView(FullScreenAutoSwitch.View.Float, "fullscreen-enter",
                             userInitiated: false, activate: false);
            };
            auto.ExitToTaskbar = () =>
            {
                // 只收"这次自动弹出来的"浮窗（origin=Auto）；用户自己开的保持不动（8.2）。
                if (shownFloat && auto.FloatOrigin == FullScreenAutoSwitch.Origin.Auto)
                    HideView(FullScreenAutoSwitch.View.Float, "fullscreen-exit", userInitiated: false);

                // 条是进全屏时收起来的，退出要放回来（状态机只在真收过条时才调这里）。
                ShowView(FullScreenAutoSwitch.View.Taskbar, "fullscreen-exit", userInitiated: false);
            };

            auto.Start();
        }

        // ---- 全局热键（G-03）----
        // 位置必须在状态机之后：回调要读 view、经 ShowView 写 auto，
        // 早于 auto 声明的地方引用它会变成"未赋值变量"（CS0165）。
        //
        // 探针模式不装：--view-probe 自己逐步切视图，热键再插一脚会切乱结果。
        //
        // 测量模式（--shot / --stats / --hidden）**照装**：设置页截图要显示真实的热键状态，
        // 跳过注册的话那一栏永远显示"未生效"，截出来的图骗自己。
        if (!probeMode)
        {
            hotkey = new Hotkey();

            // M4.26：三视图可叠加后，热键不再是"循环切三个模式"（那会跟叠加打架），
            // 改成与托盘左键同一套：**全部隐藏 ↔ 恢复上次的组合**。
            // 走 HideAllViews / RestoreViews 而不是自己 Hide —— 全屏状态机读的是 shownXxx。
            hotkey.Pressed = () =>
            {
                ToggleAllViews("hotkey");
                if (_tray is not null) UpdateTrayTooltip(_tray);
            };

            if (!hotkey.Apply(ConfigStore.Current.Hotkey))
            {
                // 冲突必须说出来。这时用户多半没开着设置页，写日志等于没说。
                // 气泡不抢焦点、关掉就走；程序照常运行，只是这个键不能用。
                _tray?.ShowBalloon("CoPul 芯脉",
                    $"全局热键不可用：{hotkey.LastError}。可在「设置 → 交互」里换一个组合键。",
                    isError: true);
            }
        }

        // 自测：--hotkey-probe <写法> 启动 1.5 秒后换一次热键，把结果打进 trace。
        // 专门用来验三条难验的路：写法错误（Ctrl+Q+Z）、被占用（先开一个占着不放的进程）、正常换键。
        if (ArgValue(e.Args, "--hotkey-probe") is { } hotkeyProbe && hotkey is not null)
        {
            var hp = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            hp.Tick += (_, _) =>
            {
                hp.Stop();
                bool ok = hotkey.Apply(hotkeyProbe);
                Interop.Trace.Log("hotkey",
                    $"probe apply=\"{hotkeyProbe}\" ok={ok} applied={hotkey.Applied ?? "(none)"} " +
                    $"err={hotkey.LastError ?? "-"}");
            };
            hp.Start();
        }

        // 自测：--hotkey-fire <n> 伪造 n 次按下（走 WM_HOTKEY，Hook 与 Pressed 是生产路径）。
        // 与 --hotkey-probe 分工：那条验"能不能注册上"，这条验"按下去之后切到哪"。
        if (int.TryParse(ArgValue(e.Args, "--hotkey-fire"), out int fireCount)
            && fireCount > 0 && hotkey is not null)
        {
            int left = fireCount;
            var fireTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            fireTimer.Tick += (_, _) =>
            {
                if (left <= 0)
                {
                    fireTimer.Stop();
                    Interop.Trace.Log("hotkey", $"probe fire 结束 view={view}");
                    return;
                }

                Interop.Trace.Log("hotkey", $"probe fire 第 {fireCount - left + 1} 次，按前 view={view}");
                left--;
                hotkey.SimulatePress();
            };
            fireTimer.Start();
        }

        // 自测：--view-probe <txt 路径> [--view-probe-start main|float|tray]
        //
        // 验的是**真的** Show/Hide 编排（M4.26：三视图可同时显示）：
        // 逐步叠加/收起，每步数"当前有几个窗口可见"，必须按 0→1→2→3→2→1→0→1 走。
        // 与 --fullscreen-probe --fs-fake 分工：那条验状态机（记账，不碰窗口），这条验编排。
        string? viewProbePath = ArgValue(e.Args, "--view-probe");
        if (viewProbePath is not null)
        {
            FullScreenAutoSwitch.View vStart = FullScreenAutoSwitch.View.Main;
            if (ArgValue(e.Args, "--view-probe-start") is { } vs
                && Enum.TryParse(vs, ignoreCase: true, out FullScreenAutoSwitch.View parsed))
                vStart = parsed;

            Probes.ViewProbe.Run(
                target => ShowView(target, "view-probe", userInitiated: false),
                target => HideView(target, "view-probe", userInitiated: false),
                () => DescribeViewState(window, floating, taskbar),
                viewProbePath, vStart, t => t.Start(), Shutdown);
            return;
        }

        // 自测：--fs-switch-probe <txt 路径>
        //
        // 端到端验"全屏自动切换"：探测结果与时钟伪造，但 EnterAutoFloat / ExitToTaskbar
        // 是**生产接线**（真的 Show 悬浮窗、真的 Hide 任务栏条）。
        // 与另两个探针的分工见 Probes/FullScreenSwitchProbe.cs 的注释。
        string? fsSwitchPath = ArgValue(e.Args, "--fs-switch-probe");
        if (fsSwitchPath is not null && auto is not null)
        {
            // 先停掉生产的 2 Hz 真实探测定时器：它会往同一个状态机里喂真实结果，
            // 与下面喂的伪造结果互相覆盖，跑出来的迁移序列就没法读了。
            // （这正是"探针必须与生产定时器互斥"的又一例。）
            auto.Stop();

            Probes.FullScreenSwitchProbe.Run(
                auto,
                () => DescribeViewState(window, floating, taskbar),
                IsShown,
                target => ShowView(target, "fs-switch-probe", userInitiated: false),
                target => HideView(target, "fs-switch-probe", userInitiated: false),
                fsSwitchPath,
                e.Args.Contains("--fs-multi"),   // M4.26：三视图叠加链路
                t => t.Start(), Shutdown);
            return;
        }

        // 自测：--fs-live-probe <txt 路径> [--fs-live-focus <ms>] [--fs-live-end <ms>]
        //
        // M4.22 的真机验收：不伪造任何输入，靠**真实**的前台窗口与真实探测跑一段，
        // 中途可选 Activate 悬浮窗模拟"用户点它一下"（旧行为会在那之后 500ms 回落任务栏）。
        // 配合外部脚本造的无边框铺满屏窗口当"游戏"使用（tools/fake_fullscreen.py）。
        if (ArgValue(e.Args, "--fs-live-probe") is { } fsLive && auto is not null)
        {
            int focusAt = int.TryParse(ArgValue(e.Args, "--fs-live-focus"), out int fm) && fm >= 0 ? fm : -1;
            int endAt = int.TryParse(ArgValue(e.Args, "--fs-live-end"), out int em) && em > 0 ? em : 20000;

            Probes.FullScreenLiveProbe.Run(
                auto,
                () => DescribeViewState(window, floating, taskbar),
                fsLive, focusAt, endAt,
                () => { if (floating is { IsVisible: true }) floating.Activate(); },
                t => t.Start(), Shutdown);
            return;
        }

        // 自测：--main-close-probe <txt 路径>（M4.26）
        //
        // 三视图可叠加之后，关大窗口不再等于退出程序 —— 但这条判断写错（Cancel 没生效、
        // 条件写反）就是"点一下 X 整个程序没了"，属于最不该错的地方。这条探针把"点 X"
        // 真的走一遍：开齐三视图 → Close() → 再等 2 秒，看进程还在不在
        // （"after" 那行写不出来 = 程序已经退了）。
        if (ArgValue(e.Args, "--main-close-probe") is { } closePath)
        {
            var closeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1.5) };
            closeTimer.Tick += (_, _) =>
            {
                closeTimer.Stop();
                ShowView(FullScreenAutoSwitch.View.Float, "close-probe", userInitiated: false);
                ShowView(FullScreenAutoSwitch.View.Taskbar, "close-probe", userInitiated: false);
                ShowView(FullScreenAutoSwitch.View.Main, "close-probe", userInitiated: false);

                File.WriteAllText(closePath,
                    $"before: main={window.IsVisible} float={floating?.IsVisible} " +
                    $"tray={taskbar?.IsVisible}{Environment.NewLine}");
                window.Close();     // 等价于用户点标题栏的 X
            };
            closeTimer.Start();

            var aliveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            aliveTimer.Tick += (_, _) =>
            {
                aliveTimer.Stop();
                File.AppendAllText(closePath,
                    $"after : alive=True main={window.IsVisible} float={floating?.IsVisible} " +
                    $"tray={taskbar?.IsVisible}{Environment.NewLine}");
                Shutdown();
            };
            aliveTimer.Start();
        }

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

            // --sample-flip <秒>:<档位ms>：测量窗口的中途改一次数据刷新档位（M4.24）。
            // 静态取证只能证明"启动时按档位跑"，证明不了"运行中改档位即时生效"——
            // 而后者才是设置项真正的承诺（P-05：下一个 tick 生效，不重启）。
            int flipAt = -1, flipMs = 0;
            string? flip = ArgValue(e.Args, "--sample-flip");
            if (flip is not null)
            {
                string[] fp = flip.Split(':', StringSplitOptions.TrimEntries);
                if (fp.Length == 2 && int.TryParse(fp[0], out int fs) && int.TryParse(fp[1], out int fm))
                {
                    flipAt = fs;
                    flipMs = fm;
                }
            }

            RunSelfCpuProbe(window, stats,
                int.TryParse(ArgValue(e.Args, "--seconds"), out int sec) && sec > 0 ? sec : 60,
                int.TryParse(ArgValue(e.Args, "--warmup"), out int warm) && warm >= 0 ? warm : 10,
                flipAt, flipMs);
            return;
        }

        // 自测：--switch-view float|main|tray --switch-after <毫秒>（M4.14）
        //
        // 为什么要"延迟切视图"这个开关：用户报的是「从大窗模式第一次直接进竖向悬浮窗，
        // 宽度不对，改一下设置又好了」。直接 --float 启动走的是另一条代码路
        // （窗口在 Show 之前就按配置排好了），**根本不经过"先主窗后悬浮窗"这个顺序**，
        // 只拍前者等于什么都没验。这里让程序照常从主窗起来，若干毫秒后再切，
        // 配合 --shot --wait 更长的等待，拍到的就是用户那条路径。
        if (ArgValue(e.Args, "--switch-view") is { } switchView)
        {
            // 每一项：`<view>@<毫秒>` 或 `layout:row|column@<毫秒>`。
            // 分号分隔成一条时间线 —— "首进竖向宽度不对" 这种只在**特定顺序**下犯的毛病，
            // 一次启动只能排一条序列，没有这个就只能在真机上碰运气复现。
            foreach (string raw in switchView.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] at = raw.Split('@');
                if (at.Length != 2 || !int.TryParse(at[1], out int fireMs) || fireMs < 0) continue;

                string what = at[0].Trim();
                var step = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(fireMs) };
                step.Tick += (_, _) =>
                {
                    step.Stop();

                    if (what.StartsWith("layout:", StringComparison.OrdinalIgnoreCase))
                    {
                        // 走窗口自己的 setter，与设置页那个单选按钮同一条路
                        FloatWindow lf = EnsureFloat();
                        if (e.Args.Contains("--no-relayout-on-show")) lf.RelayoutOnShow = false;
                        lf.IsVertical = what.EndsWith("column", StringComparison.OrdinalIgnoreCase);
                    }
                    else if (what.StartsWith("field:", StringComparison.OrdinalIgnoreCase))
                    {
                        // 字段开关（M4.14）：走菜单那条路，用来验"隐藏期间改了布局，
                        // 再 Show 时宽度跟不跟得上"
                        FloatWindow ff = EnsureFloat();
                        if (e.Args.Contains("--no-relayout-on-show")) ff.RelayoutOnShow = false;
                        ff.PersistToConfig = false;   // 同理：别把探针改的字段写进用户配置
                        ff.ToggleFieldForProbe(what.Substring("field:".Length));
                    }
                    else if (string.Equals(what, "toggle-all", StringComparison.OrdinalIgnoreCase))
                    {
                        // M4.26：验证"全部隐藏 ↔ 恢复上次组合"。时间线里排两次就能走一个来回。
                        ToggleAllViews("switch-view-probe");
                    }
                    else if (what.StartsWith("hide:", StringComparison.OrdinalIgnoreCase)
                             && Enum.TryParse(what.Substring("hide:".Length), ignoreCase: true,
                                              out FullScreenAutoSwitch.View hv))
                    {
                        HideView(hv, "switch-view-probe");
                    }
                    else if (Enum.TryParse(what, ignoreCase: true, out FullScreenAutoSwitch.View v))
                    {
                        ShowView(v, "switch-view-probe", userInitiated: true);
                    }
                };
                step.Start();
            }
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

        // 截哪个窗口**必须在定时器里判**，不能在这里判：设置页 / 任务栏条都是
        // "启动之后才出现的"，启动那一刻它们还不存在，判出来永远是主窗口
        // （第一版就踩了：--settings --shot-screen 拍到的是一张主窗口）。
        int waitMs = int.TryParse(ArgValue(e.Args, "--wait"), out int w) && w > 0 ? w : 4000;

        // 外扩像素：验收毛玻璃必须看到"窗口内 vs 窗口外"的差别，只截窗口本身永远看不出来
        int pad = int.TryParse(ArgValue(e.Args, "--shot-pad"), out int p) && p > 0 ? p : 0;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(waitMs) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();

            // --float-zreport：把悬浮窗的交互诊断打到标准输出（M4.21）。
            // 为什么要单独给一条：同时开设置页时截图目标会变成设置页，sidecar 就不写了，
            // 而"设置页压住悬浮窗"恰恰是最需要这组数字的场景。
            if (e.Args.Contains("--float-zreport") && floating is { IsVisible: true } zf)
                System.Console.WriteLine("zreport: " + zf.InteractDiag);

            // 截哪个窗口必须在这一刻判：设置页 / 任务栏条都是"启动之后才出现的"，
            // 在定时器外面判出来永远是主窗口（第一版 --settings --shot-screen 就拍成了主窗口）。
            Window target = settings is { IsVisible: true } ? settings
                          : taskbar is { IsVisible: true } ? taskbar
                          : floating is { IsVisible: true } ? floating
                          : window;

            // 截谁必须留痕：设置页和主窗口都居中、尺寸接近，选错了肉眼看不出来
            Interop.Trace.Log("shot", $"target={target.GetType().Name} visible={target.IsVisible} " +
                                      $"left={target.Left:F0} top={target.Top:F0} " +
                                      $"size={target.ActualWidth:F0}x{target.ActualHeight:F0} " +
                                      $"settings={(settings is null ? "null" : settings.IsVisible.ToString())}");

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
                                // 必须读**当前**的 taskbar 实例：explorer 重启后条会被重建，
                                // 上面那个 `target` 是等待开始时的快照，它的句柄和 PlacementDiag
                                // 都是老窗口的 —— 用它截图验收会得到"已恢复"的假象或相反。
                                TaskbarWindow live = taskbar is { IsVisible: true } ? taskbar : tb;

                                ScreenCapture.SaveRect(shotScreen, br.Left, br.Top,
                                                       br.Right - br.Left, br.Bottom - br.Top);
                                Taskbar.TryGetRect(live.Handle, out Win32Window.RECT tr);
                                File.WriteAllText(shotScreen + ".info.txt",
                                    $"bar  : {br.Right - br.Left}×{br.Bottom - br.Top} px at ({br.Left},{br.Top}){Environment.NewLine}" +
                                    $"bar  : rect of strip = ({tr.Left},{tr.Top})-({tr.Right},{tr.Bottom}){Environment.NewLine}" +
                                    $"place: {live.PlacementDiag}");
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
                    // 抓屏抓的是"屏幕上合成后的样子"，桌面压着谁就拍到谁。
                    // 开发机上常开着游戏和浏览器，实测设置页被切成只剩右下角一块 ——
                    // 那不是程序画错了，是别人的窗口盖在上面。先把自己抬到 topmost，
                    // 等 DWM 重新合成一帧再拍；进程随后就退出，不需要还原。
                    target.Topmost = true;

                    var cap = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
                    cap.Tick += (_, _) =>
                    {
                        cap.Stop();
                        try
                        {
                            ScreenCapture.SaveWindow(target, shotScreen, pad);

                            if (target is FloatWindow fw)
                                File.WriteAllText(shotScreen + ".info.txt",
                                    $"glass: {fw.GlassMode}{Environment.NewLine}" +
                                    $"border: {fw.DwmBorderDiag}{Environment.NewLine}" +
                                    $"size : {target.ActualWidth:F0}×{target.ActualHeight:F0} DIP{Environment.NewLine}" +
                                    $"scale: {fw.ScaleDiag}{Environment.NewLine}" +
                                    $"{fw.TextDiag}{Environment.NewLine}" +
                                    $"{fw.InteractDiag}" +
                                    (fw.PlacementDiag.Length == 0
                                        ? string.Empty
                                        : Environment.NewLine + "place: " + fw.PlacementDiag));
                            else if (target is TaskbarWindow tb)
                                // 任务栏条：抓屏只用来看"字在任务栏上读不读得出来"，
                                // 逐字段配色靠 sidecar 的运行值判 —— 那边是系统底色，数像素不可靠。
                                File.WriteAllText(shotScreen + ".info.txt",
                                    $"size : {target.ActualWidth:F0}×{target.ActualHeight:F0} DIP{Environment.NewLine}" +
                                    $"{tb.TextDiag}");
                        }
                        catch (Exception ex)
                        {
                            File.WriteAllText(shotScreen + ".error.txt", ex.ToString());
                        }
                        finally
                        {
                            Shutdown();
                        }
                    };
                    cap.Start();
                    deferred = true;
                    return;      // 延后关窗，交给 cap 定时器
                }
                else
                {
                    // --shot-scroll：截屏前先滚动内容区，用来拍一屏放不下的下半部分区块。
                    // 必须在渲染之前滚并强制布局，否则 RTB 画到的还是旧偏移。
                    if (target is MainWindow mw
                        && double.TryParse(ArgValue(e.Args, "--shot-scroll"), out double shotScroll)
                        && shotScroll > 0)
                    {
                        mw.ScrollContent(shotScroll);
                        File.WriteAllText(shot + ".scroll.txt", $"scroll={shotScroll}");
                    }

                    // --run-state-probe：强制顶部状态灯三态。本机没提权，正常跑起来永远是
                    // 「降级运行」，另外两种形状（实心圆 / 空心圆环）平时拍不到。
                    if (target is MainWindow mwState
                        && ArgValue(e.Args, "--run-state-probe") is { } rsProbe
                        && Enum.TryParse<RunState>(rsProbe, true, out var forcedRunState))
                    {
                        mwState.State.ForceRunState(forcedRunState);
                        File.WriteAllText(shot + ".state.txt", forcedRunState.ToString());
                    }

                    SavePng(target, shot!);

                    // RTB 这条以前不留 sidecar，导致"宽度对不对"只能靠数像素。
                    // 尺寸 DIP / 物理像素 / 缩放档 / 探针结论一起写出去 ——
                    // DIP 对不上才是布局问题，DIP 一致而像素不同那是缩放，两回事。
                    if (target is FloatWindow rfw)
                        File.WriteAllText(shot + ".info.txt",
                            $"size : {target.ActualWidth:F2}×{target.ActualHeight:F2} DIP{Environment.NewLine}" +
                            $"px   : {pixelWidthOf(rfw)}×{pixelHeightOf(rfw)} px{Environment.NewLine}" +
                            $"hwnd : {rfw.HwndDiag}{Environment.NewLine}" +
                            $"scale: {rfw.ScaleDiag}{Environment.NewLine}" +
                            $"cells: {rfw.FieldsDiag} cellGap={rfw.CellGap.Right:F1}{Environment.NewLine}" +
                            $"{rfw.TextDiag}{Environment.NewLine}" +
                            $"{rfw.InteractDiag}" +
                            (rfw.PlacementDiag.Length == 0
                                ? string.Empty
                                : Environment.NewLine + "place: " + rfw.PlacementDiag));
                    else if (target is TaskbarWindow rtb)
                        File.WriteAllText(shot + ".info.txt", rtb.TextDiag);
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

            // 7.7 / Q-13：停靠边决定条能不能画（竖向只放得下几列），自动隐藏决定配色能不能采样
            sb.AppendLine($"停靠边 (ABM)         : {Taskbar.EdgeOf(bar)}");
            sb.AppendLine($"自动隐藏 (ABM)       : {(Taskbar.IsAutoHide(bar) ? "开" : "关")}");

            double luma = Taskbar.SampleLuma(bar);
            sb.AppendLine($"取样亮度             : {(luma < 0 ? "-1（整条在屏幕外，取不到）" : luma.ToString("F1"))}");

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
    /// 三个视图的可见性与几何，一行文本。<c>--view-probe</c> 每步打一次，
    /// 用来数"当前有几个窗口可见"（M4.26 起三视图可叠加，期望值由探针按步给）。
    ///
    /// <para>
    /// 用 <see cref="Window.IsVisible"/> 而不是 <c>Visibility</c>：后者只反映 WPF 的属性，
    /// 窗口被 <c>Hide()</c> 之后两者都会变，但被系统连带销毁（explorer 重启）时
    /// <c>IsVisible</c> 会变 false 而 <c>Visibility</c> 可能还留着 —— 判"屏幕上真的有"用前者。
    /// </para>
    /// </summary>
    private static string DescribeViewState(MainWindow main, FloatWindow? floating, TaskbarWindow? taskbar)
    {
        string One(string name, Window? w)
        {
            if (w is null) return $"{name}=(未创建) ";
            IntPtr h = new System.Windows.Interop.WindowInteropHelper(w).Handle;
            if (h == IntPtr.Zero) return $"{name}=hwnd=0 ";
            if (!Monitors.TryGetRect(h, out Win32Window.RECT r))
                return $"{name}=visible={w.IsVisible} rect=? ";
            return $"{name}=visible={w.IsVisible} ({r.Left},{r.Top})-({r.Right},{r.Bottom}) ";
        }

        return One("main", main) + One("float", floating) + One("tray", taskbar);
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
    private void RunSelfCpuProbe(MainWindow window, string path, int seconds, int warmupSec,
                                 int flipAtSec = -1, int flipMs = 0)
    {
        var process = System.Diagnostics.Process.GetCurrentProcess();
        TimeSpan cpu0 = default;
        long wall0 = 0;
        long snap0 = 0;

        // 中途翻档（M4.24）：记录翻档那一刻的墙钟与快照数，结尾据此算前后两段的实测间隔。
        // 只翻一次 —— 翻多次会让"哪一段对应哪个档位"变成要对齐时间戳的麻烦事。
        long flipWallTs = 0;
        long flipSnap = 0;
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
                snap0 = window.Host.SnapshotCount;

                foreach (System.Diagnostics.ProcessThread t in process.Threads)
                {
                    try { thread0[t.Id] = t.TotalProcessorTime; } catch { }
                }

                // 预热期间的 CPU 一并记下来，用于说明"启动成本"到底多大
                warmupNote = $"{cpu0.TotalMilliseconds:F0} ms（含 JIT / PDH 初始化 / 冷盘枚举）";
            }

            // 翻档：位置按"测量窗口内的第 N 秒"算（warmupSec 之后才开始计时）。
            // 记下翻档时刻的墙钟与快照数，结尾用它们切出前后两段。
            if (flipAtSec >= 0 && tick == warmupSec + flipAtSec)
            {
                window.Host.SetSampleInterval(flipMs);
                flipWallTs = System.Diagnostics.Stopwatch.GetTimestamp();
                flipSnap = window.Host.SnapshotCount;
                System.Console.WriteLine($"sample-flip: 第 {flipAtSec}s 改为 {flipMs}ms");
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
                // 数据刷新（M4.24）：档位 + 实测间隔。档位改了但实测没跟着变，
                // 只可能是被 tier floor 钳住 —— 这一行就是唯一的判据。
                // 用窗口内的 delta，不能用累计 SnapshotCount（它把 warmup 期也算进去了）。
                $"sample      : 档位 {window.Host.SampleIntervalMs}ms" +
                $"  实测 {SafeAvg(wallMs, window.Host.SnapshotCount - snap0):F0}ms/次" +
                $"  ({window.Host.SnapshotCount - snap0} 次 / {wallMs / 1000.0:F1}s)",
                $"d04 refresh : {window.Host.PartitionRefreshCount} 次  其中变化 {window.Host.PartitionChangedCount} 次"
            };

            // 翻档对照（M4.24）：前后两段各自的实测间隔。若"下一个 tick 生效"成立，
            // 前段应≈启动档位、后段应≈翻后的档位；两段同一个数就说明没生效。
            if (flipWallTs != 0)
            {
                double beforeMs = (flipWallTs - wall0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                double afterMs = (System.Diagnostics.Stopwatch.GetTimestamp() - flipWallTs)
                                 * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                lines.Add($"sample flip : 翻档前 {SafeAvg(beforeMs, flipSnap - snap0):F0}ms/次" +
                          $"（{flipSnap - snap0} 次 / {beforeMs / 1000.0:F1}s） → 翻档后 " +
                          $"{SafeAvg(afterMs, window.Host.SnapshotCount - flipSnap):F0}ms/次" +
                          $"（{window.Host.SnapshotCount - flipSnap} 次 / {afterMs / 1000.0:F1}s，目标 {flipMs}ms）");
            }

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
    private static int pixelWidthOf(Window w)
        => (int)Math.Ceiling(w.ActualWidth * VisualTreeHelper.GetDpi(w).DpiScaleX);

    private static int pixelHeightOf(Window w)
        => (int)Math.Ceiling(w.ActualHeight * VisualTreeHelper.GetDpi(w).DpiScaleY);

    private static void SavePng(Window window, string path)
    {
        // 先强制走一次布局与渲染失效：刚改过绑定（比如 --run-state-probe 换灯的形状）
        // 只标了脏，没重新排版的话 RTB 画出来的还是改之前的样子。
        window.UpdateLayout();

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
