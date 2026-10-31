using System.IO;
using System.Text;
using CoPul.App.Interop;
using CoPul.App.Sdk;

namespace CoPul.App.Probes;

/// <summary>
/// <c>--fullscreen-probe</c>：全屏检测与自动切换的验收探针（需求第 8 章）。
///
/// <para>
/// 两条路径：
/// <list type="bullet">
/// <item>不加 <c>--fs-fake</c>：连跑 N 轮<b>真实探测</b>（<c>SHQueryUserNotificationState</c>
/// + 前台矩形 + 无标题栏），每轮打出完整证据。用来确认「桌面上不会误判成全屏」。</item>
/// <item>加 <c>--fs-fake</c>：喂伪造输入，把 8.2 / 8.3 / 8.6 的四条关键链路各跑一遍。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么伪造路径要用虚拟时钟。</b>防抖门限是 1s 级（M4.23 起 0.5s~3s 可调），
/// 轮询 500ms。真跑一遍墙钟要好几秒，四条链路就是十几秒，而且探针里一旦写
/// <c>Thread.Sleep</c> 就卡住 UI 线程。给状态机注入一个可推进的 <c>Clock</c> 之后，
/// 整条链路能在一帧内推完 —— 而且门限判定走的还是同一个 <c>Poll()</code> 实现，
/// 验的是生产逻辑，不是"另写一份近似判定"自欺欺人。
/// </para>
///
/// <para>
/// 伪造路径<b>不真的 Show 窗口</b>，只记账（<c>EnterAutoFloat</c> / <c>ExitToTaskbar</c>
/// 换成往 list 里加一行）。这样它验的是"状态机在什么条件下发出什么动作、来源标记怎么变"；
/// 真的 Hide/Show 编排另有 <c>--view-probe</c> 单独验。两件事必须分开：
/// 混在一起跑，某条失败时无法判断是状态机错了还是视图编排错了。
/// </para>
/// </summary>
internal static class FullScreenProbe
{
    /// <summary>伪造的"全屏应用"证据：QUNS=3（D3D 独占全屏）+ 矩形铺满 + 无标题栏。</summary>
    private static readonly FullScreenInfo FakeFull = new(
        true, "fake:quns=3 +rect -caption", 3, new IntPtr(0x1234),
        "fake-fullscreen-app", "Fake Game", 4242, true, true, @"\\.\DISPLAY1");

    /// <summary>伪造的"桌面"证据：QUNS=5（可接受通知）+ 不铺满 + 有标题栏。</summary>
    private static readonly FullScreenInfo FakeDesktop = new(
        false, "fake:desktop", 5, new IntPtr(0x5678),
        "Progman", "Program Manager", 1111, false, false, @"\\.\DISPLAY1");

    /// <summary>
    /// 跑探针并把报告写进 <paramref name="path"/>。调用方（App）负责在选择此路径时不再建窗口。
    /// </summary>
    /// <param name="polls">真实探测的轮数（每轮间隔 500ms，由 DispatcherTimer 驱动）。</param>
    /// <param name="onDone">报告写完后的回调（App 用来 Shutdown）。</param>
    /// <param name="onStartTimer">把 DispatcherTimer 交给调用方启动 —— 探针不引用 Application，
    /// 方便将来在没有 WPF 应用的场景（单元测试）里也能跑状态机部分。</param>
    public static void Run(string path, bool fake, int polls,
                           Action<System.Windows.Threading.DispatcherTimer> onStartTimer,
                           Action onDone)
    {
        StringBuilder head = BuildHeader(fake, polls);

        if (!fake)
        {
            RunRealProbe(path, polls, head, onStartTimer, onDone);
            return;
        }

        // 伪造路径是纯计算 + 字符串拼接，不需要定时器，直接一次跑完
        head.Append(RunFakeChain());
        File.WriteAllText(path, head.ToString());
        onDone();
    }

    private static StringBuilder BuildHeader(bool fake, int polls)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"time     : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"mode     : {(fake ? "fake（伪造输入，验状态机与来源标记）" : "real（真实探测，验不会误判）")}");
        sb.AppendLine($"polls    : {polls}");
        sb.AppendLine($"trace    : {Trace.Path}");
        sb.AppendLine();

        // 真实探测的"当下这一刻"先打一次，无论哪种模式都有价值：
        // fake 模式下它给出的是"探针自己看到的世界"，与下面的伪造链路可对照阅读。
        FullScreenInfo now = FullScreenDetector.Probe();
        sb.AppendLine("== 真实探测（此刻）==");
        sb.AppendLine("  " + now.Describe());
        sb.AppendLine($"  QUNS 档位        : {FullScreenDetector.QunsName(now.Quns)}");
        sb.AppendLine($"  前台矩形匹配整屏 : {now.RectMatchesMonitor}");
        sb.AppendLine($"  前台无标题栏     : {now.Captionless}");
        sb.AppendLine();

        sb.Append(RunDecideTable());
        return sb;
    }

    /// <summary>
    /// 判定组合自检表：穷举「QUNS × 矩形铺满 × 无标题栏」的全部 8 种组合，逐行对照期望。
    ///
    /// <para>
    /// <b>为什么必须有这张表。</b>踩过一次严重误判：前台是普通浏览器窗口
    /// （矩形不铺满、有标题栏），QUNS 却报 2。按需求 8.4 原文「主判据 QUNS ∈ {2,3}」
    /// 的写法会把它判成全屏，任务栏条平白消失。修成「QUNS_BUSY 必须有几何旁证」之后，
    /// 又引入漏判风险（改狠了会把真全屏判掉）。这张表把两侧都锁死：
    /// 误判那行必须 false，真全屏那几行必须 true。
    /// </para>
    ///
    /// <para>
    /// 纯逻辑、无需真开全屏应用 —— 这类规则就该这样验，而不是开个游戏碰运气。
    /// </para>
    /// </summary>
    private static string RunDecideTable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("== 判定组合自检表（DecideIsFullScreen，与 Probe 同一份规则）==");
        sb.AppendLine("   QUNS | 矩形铺满 | 无标题栏 | 期望 | 实际 | 结果  说明");
        sb.AppendLine("  ------+----------+----------+------+------+------  ----");

        // (quns, rectMatches, captionless, expected, note)
        var cases = new (int Quns, bool Rect, bool NoCaption, bool Expect, string Note)[]
        {
            // ---- 必须判为全屏（真全屏的两种形态）----
            (3, true, true, true, "D3D 独占全屏（QUNS=3）"),
            (3, false, false, true, "D3D 独占全屏 + 几何取整差异（QUNS=3 不看几何）"),
            (2, true, true, true, "无边框全屏 + QUNS 也报忙（《剑星》实测是这条）"),
            (5, true, true, true, "无边框全屏但 QUNS 未置位（辅判据单独成立）"),
            (6, true, true, true, "专注时段 + 几何铺满（弱判据加旁证）"),

            // ---- 必须判为非全屏（误判案例）----
            (2, false, false, false, "普通浏览器窗口 + QUNS=2 ← 实测踩到的误判"),
            (2, false, true, false, "有 QUNS 但未铺满、缺标题栏（半屏应用）"),
            (2, true, false, false, "铺满但有标题栏（最大化窗口，不是全屏）"),
            (5, false, false, false, "普通桌面状态"),
            (6, false, true, false, "专注时段但没全屏"),
            (0, false, true, false, "锁屏 / 无前台"),
        };

        int pass = 0, fail = 0;
        foreach (var c in cases)
        {
            bool actual = FullScreenDetector.DecideIsFullScreen(c.Quns, c.Rect, c.NoCaption);
            bool ok = actual == c.Expect;
            if (ok) pass++; else fail++;

            sb.AppendLine($"  {c.Quns,4} | {(c.Rect ? "是" : "否"),8} | {(c.NoCaption ? "是" : "否"),8} | " +
                          $"{(c.Expect ? "全屏" : "否"),4} | {(actual ? "全屏" : "否"),4} | " +
                          $"{(ok ? "✓" : "✗ 不符")}   {c.Note}");
        }

        sb.AppendLine($"  ------+----------+----------+------+------+------");
        sb.AppendLine($"  通过 {pass} / 失败 {fail}");
        sb.AppendLine($"  判定 : {(fail == 0 ? "通过（两条边界都被锁住）" : "失败 —— 上面 ✗ 的行是误判或漏判")}");
        sb.AppendLine();
        return sb.ToString();
    }

    /// <summary>真实探测：连跑 N 轮看稳定性。桌面静止时每一轮结论都应当一致且为 notfull。</summary>
    private static void RunRealProbe(string path, int polls, StringBuilder sb,
                                     Action<System.Windows.Threading.DispatcherTimer> onStartTimer,
                                     Action onDone)
    {
        sb.AppendLine($"== 真实探测 ×{polls}（每轮 500ms，验证稳定性）==");

        int i = 0;
        var seen = new List<bool>(polls);
        var costs = new List<double>(polls);

        var timer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = FullScreenAutoSwitch.PollInterval
        };

        timer.Tick += (_, _) =>
        {
            i++;
            long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
            FullScreenInfo info = FullScreenDetector.Probe();
            double us = (System.Diagnostics.Stopwatch.GetTimestamp() - t0) * 1_000_000.0
                        / System.Diagnostics.Stopwatch.Frequency;
            costs.Add(us);

            seen.Add(info.IsFullScreen);
            sb.AppendLine($"  #{i} {info.Describe()}");
            sb.AppendLine($"     单次探测耗时 {us:F1} µs");

            if (i < polls) return;

            timer.Stop();
            bool anyFull = seen.Contains(true);
            bool anyNotFull = seen.Contains(false);

            // A-26 要求新增渲染面重跑性能，全屏检测虽不是渲染面，但它是**新增的常驻轮询**，
            // 同样必须报成本。这里给的是单次耗时，2Hz 下的占比可以一眼算出来。
            double avgUs = costs.Average();
            double perSecondUs = avgUs * 1000.0 / FullScreenAutoSwitch.PollInterval.TotalMilliseconds;
            sb.AppendLine();
            sb.AppendLine("== 成本（A-26 口径：新增常驻轮询必须报数）==");
            sb.AppendLine($"  单次探测 : 平均 {avgUs:F1} µs（最小 {costs.Min():F1} / 最大 {costs.Max():F1}）");
            sb.AppendLine($"  轮询频率 : {1000.0 / FullScreenAutoSwitch.PollInterval.TotalMilliseconds:F0} Hz（8.4 定的 500ms）");
            sb.AppendLine($"  折算占用 : {perSecondUs / 1_000_000.0 * 100:F4}% 单核" +
                          $"（= {perSecondUs / 1_000_000.0 * 100 / Environment.ProcessorCount:F6}% 整机，" +
                          $"{Environment.ProcessorCount} 逻辑核）");
            sb.AppendLine();

            if (anyFull && anyNotFull)
                sb.AppendLine("结论 : 抖动 —— 同一状态下出现了两种结果，判定不稳定（查 QUNS / 前台是否在切）");
            else if (anyFull)
                sb.AppendLine("结论 : 稳定判为全屏。若此刻你并没有开全屏应用，说明某条判据过宽（看上面 reason）");
            else
                sb.AppendLine("结论 : 稳定判为「非全屏」（桌面状态下的期望结果）");

            sb.AppendLine();
            sb.AppendLine("判据自检说明：");
            sb.AppendLine("  · 桌面 / 任务栏 / 开始菜单会被否决项直接排除（reason 以 veto: 开头）");
            sb.AppendLine("  · 主判据 QUNS ∈ {2 BUSY, 3 D3D_FULL_SCREEN}");
            sb.AppendLine("  · 辅判据 = 前台矩形铺满整屏 && 无 WS_CAPTION，可独立成立");
            sb.AppendLine("  · 静默时段 / 应用模式 / 演示模式（QUNS 6/7/4）需要辅判据同时成立才算全屏");
            sb.AppendLine();
            sb.AppendLine("真实全屏验收方法：开一个全屏游戏或按 Win+Tab 进全屏，再跑同一条命令，");
            sb.AppendLine("期望全部 full=true 且 reason 里出现 quns=3（或 quns=2）。");

            File.WriteAllText(path, sb.ToString());
            onDone();
        };

        onStartTimer(timer);
    }

    /// <summary>伪链路 A–D 全部在虚拟时钟上跑，无 IO、无等待。</summary>
    private static string RunFakeChain()
    {
        var sb = new StringBuilder();

        sb.AppendLine("== 链路 A：任务栏 → 全屏 → 悬浮(auto) → 退全屏 → 回落任务栏（8.3）==");

        long clock = 1000;   // 虚拟时钟起点（非 0，避免与"未设置"哨兵值混淆）
        var a = new FullScreenAutoSwitch
        {
            CurrentView = FullScreenAutoSwitch.View.Taskbar,
            Clock = () => clock,
            EnterAutoFloat = _ => { },
            ExitToTaskbar = () => { },
        };
        var aCalls = new List<string>();
        a.EnterAutoFloat = mon => aCalls.Add($"EnterAutoFloat({mon})");
        a.ExitToTaskbar = () => aCalls.Add("ExitToTaskbar()");

        var rows = new List<string>();

        // 每轮 500ms（与生产的 PollInterval 一致），时间由虚拟时钟推进
        void Tick(FullScreenInfo info, string label)
        {
            a.Poll(info);
            rows.Add($"  {label,-28} view={a.CurrentView,-8} origin={a.FloatOrigin,-6} " +
                     $"steady_full={a.IsFullScreenSteady,-5} t={clock}ms");
            clock += 500;
        }

        // 步数按当前档位算（M4.23）：门限不再固定 800/500，写死三步的话
        // 2s/3s 档下走到最后一步门限都没跨过去，链路会假失败。
        int need = a.HoldMs / 500 + 1;

        Tick(FakeDesktop, "t+0    起点（任务栏）");
        Tick(FakeFull, "t+500  全屏#1（开始计时）");
        for (int k = 2; k <= need; k++)
            Tick(FakeFull, $"全屏#{k}（已 {500 * (k - 1)}ms{(k == need ? " → 触发" : "，未到门限")}）");

        Tick(FakeDesktop, "退全屏#1（开始计时）");
        for (int k = 2; k <= need; k++)
            Tick(FakeDesktop, $"退全屏#{k}（已 {500 * (k - 1)}ms{(k == need ? " → 触发" : "，未到门限")}）");

        foreach (string r in rows) sb.AppendLine(r);
        sb.AppendLine($"  动作序列 : {(aCalls.Count == 0 ? "(无)" : string.Join(" / ", aCalls))}");

        // 期望：进入时发 EnterAutoFloat，退出时发 ExitToTaskbar，最后停在 Taskbar
        bool aEnter = aCalls.Count >= 1 && aCalls[0].StartsWith("EnterAutoFloat");
        bool aExit = aCalls.Count >= 2 && aCalls[1].StartsWith("ExitToTaskbar");
        bool aEnd = a.CurrentView == FullScreenAutoSwitch.View.Taskbar;
        sb.AppendLine($"  判定 : {(aEnter && aExit && aEnd ? "通过" : "失败")}" +
                      $"（进入={aEnter} 回落={aExit} 终态Taskbar={aEnd}）");
        sb.AppendLine($"  期望 : 门限 {a.HoldMs}ms 跨过后进入悬浮(auto)、再跨过后回落任务栏、动作恰好 2 次");
        sb.AppendLine();

        // ---- 链路 B：manual 悬浮窗退出全屏必须保持悬浮（8.2 的核心）----
        sb.AppendLine("== 链路 B：用户手动悬浮(manual) → 全屏 → 退全屏 → 必须保持悬浮（8.2）==");

        clock = 5000;
        var bCalls = new List<string>();
        var b = new FullScreenAutoSwitch
        {
            CurrentView = FullScreenAutoSwitch.View.Float,
            Clock = () => clock,
            EnterAutoFloat = mon => bCalls.Add($"EnterAutoFloat({mon})"),
            ExitToTaskbar = () => bCalls.Add("ExitToTaskbar()"),
        };
        b.NoteUserChoseFloat();   // 用户主动点了「切到悬浮窗」

        void TickB(FullScreenInfo info, string label)
        {
            b.Poll(info);
            rows.Add($"  {label,-28} view={b.CurrentView,-8} origin={b.FloatOrigin,-6} " +
                     $"steady_full={b.IsFullScreenSteady,-5} t={clock}ms");
            clock += 500;
        }

        TickB(FakeFull, "t+0    全屏#1（开始计时）");
        for (int k = 2; k <= need; k++)
            TickB(FakeFull, $"全屏#{k}（已 {500 * (k - 1)}ms{(k == need ? " → 跨过门限" : "，未到门限")}）");
        TickB(FakeDesktop, "退全屏#1（开始计时）");
        for (int k = 2; k <= need; k++)
            TickB(FakeDesktop, $"退全屏#{k}（已 {500 * (k - 1)}ms{(k == need ? " → 跨过门限" : "，未到门限")}）");

        sb.AppendLine($"  动作序列 : {(bCalls.Count == 0 ? "(无)" : string.Join(" / ", bCalls))}");
        sb.AppendLine($"  判定 : {(bCalls.Count == 0 ? "通过（8.2 manual 不回落）" : "失败（manual 被回落成任务栏）")}");
        sb.AppendLine($"  期望 : 动作 0 次，view 全程 Float，origin 保持 Manual");
        sb.AppendLine();

        // ---- 链路 C：大窗口模式下进入全屏不动作（8.6）----
        sb.AppendLine("== 链路 C：大窗口模式 + 进入全屏 → 不动作（8.6）==");

        clock = 9000;
        var cCalls = new List<string>();
        var c = new FullScreenAutoSwitch
        {
            CurrentView = FullScreenAutoSwitch.View.Main,
            Clock = () => clock,
            EnterAutoFloat = mon => cCalls.Add($"EnterAutoFloat({mon})"),
            ExitToTaskbar = () => cCalls.Add("ExitToTaskbar()"),
        };
        c.Poll(FakeFull);
        clock += 500;
        c.Poll(FakeFull);
        clock += 500;
        c.Poll(FakeFull);

        sb.AppendLine($"  动作序列 : {(cCalls.Count == 0 ? "(无)" : string.Join(" / ", cCalls))}");
        sb.AppendLine($"  判定 : {(cCalls.Count == 0 ? "通过（8.6 大窗口不被打断）" : "失败（大窗口被切走了）")}");
        sb.AppendLine();

        // ---- 链路 D：开关关闭后一律不动作 ----
        sb.AppendLine("== 链路 D：开关关闭（--no-fullscreen-switch）→ 一律不动作 ==");

        clock = 12000;
        var dCalls = new List<string>();
        var d = new FullScreenAutoSwitch
        {
            CurrentView = FullScreenAutoSwitch.View.Taskbar,
            Enabled = false,
            Clock = () => clock,
            EnterAutoFloat = mon => dCalls.Add($"EnterAutoFloat({mon})"),
            ExitToTaskbar = () => dCalls.Add("ExitToTaskbar()"),
        };
        for (int i = 0; i < 4; i++)
        {
            d.Poll(FakeFull);
            clock += 500;
        }

        sb.AppendLine($"  动作序列 : {(dCalls.Count == 0 ? "(无)" : string.Join(" / ", dCalls))}");
        sb.AppendLine($"  判定 : {(dCalls.Count == 0 ? "通过（开关生效）" : "失败（开关没生效）")}");
        sb.AppendLine();

        // ---- 链路 E：用户中途主动切走，之后退出全屏不应再回落（8.6 最后一行）----
        sb.AppendLine("== 链路 E：全屏期间用户手动切任务栏 → origin 重置 → 退出全屏不再回切（8.6）==");

        clock = 15000;
        var eCalls = new List<string>();
        var e = new FullScreenAutoSwitch
        {
            CurrentView = FullScreenAutoSwitch.View.Taskbar,
            Clock = () => clock,
            EnterAutoFloat = mon => eCalls.Add($"EnterAutoFloat({mon})"),
            ExitToTaskbar = () => eCalls.Add("ExitToTaskbar()"),
        };

        // 正常进入全屏：切悬浮(auto)。同样要走满防抖门限（当前档位，默认 1s）。
        e.Poll(FakeFull); clock += 500;
        e.Poll(FakeFull); clock += 500;
        e.Poll(FakeFull); clock += 500;

        // 全屏期间用户手动切回任务栏（8.6：遵循用户意愿，origin 重置）
        e.CurrentView = FullScreenAutoSwitch.View.Taskbar;
        e.NoteUserLeftFloat();

        // 退出全屏：因为 origin 已归位 Manual，不应再触发 ExitToTaskbar
        e.Poll(FakeDesktop); clock += 500;
        e.Poll(FakeDesktop); clock += 500;

        sb.AppendLine($"  动作序列 : {(eCalls.Count == 0 ? "(无)" : string.Join(" / ", eCalls))}");
        bool eOnlyEnter = eCalls.Count == 1 && eCalls[0].StartsWith("EnterAutoFloat");
        sb.AppendLine($"  判定 : {(eOnlyEnter ? "通过（只进入一次，退出时按用户意愿不再自动回切）" : "失败")}");
        sb.AppendLine($"  期望 : 只有一次 EnterAutoFloat，没有 ExitToTaskbar");
        sb.AppendLine();

        sb.AppendLine("== 状态机迁移日志 ==");
        foreach (string line in a.Journal) sb.AppendLine("  " + line);
        sb.AppendLine();
        sb.AppendLine("说明：伪造链路的动作只记账、不真的 Show/Hide 窗口；");
        sb.AppendLine("     真视图编排用 --view-probe 验（会真的切换三个窗口并记每步可见性）。");

        return sb.ToString();
    }
}
