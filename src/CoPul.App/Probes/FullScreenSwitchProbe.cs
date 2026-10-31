using System.IO;
using System.Text;
using System.Windows.Threading;
using CoPul.App.Sdk;

namespace CoPul.App.Probes;

/// <summary>
/// <c>--fs-switch-probe</c>：全屏自动切换的**生产接线端到端**验收（第 8 章）。
///
/// <para>
/// 与另外两个探针的分工，三者缺一不可：
/// <list type="bullet">
/// <item><c>--fullscreen-probe --fs-fake</c>：验状态机的**判定逻辑**，动作只记账、不碰窗口。</item>
/// <item><c>--view-probe</c>：验 ShowView / HideView 的**叠加编排**，视图由人手动动。</item>
/// <item><b>本探针</b>：把两者接在一起 —— 用<b>真实</b>的 <c>EnterAutoFloat</c> / <c>ExitToTaskbar</c>
/// （也就是真的会 Show 悬浮窗、Hide 任务栏条的那两个回调），只是把**探测结果**换成伪造的。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>两条链路。</b>默认跑"只开任务栏条"的经典链路（进全屏=收条+弹浮窗，退全屏=收浮窗+放回条）。
/// 加 <c>--fs-multi</c> 跑 M4.26 的新链路：三个视图同时显示时进全屏，
/// 只应收起任务栏条 —— 用户自己开着的大窗口与悬浮窗都不许被藏，退出全屏时条要放回来、
/// 手动开的浮窗也不许被收走（8.2）。
/// </para>
///
/// <para>
/// <b>为什么不真开全屏。</b>真全屏要独占显示器，跑一次占一次整屏，中途被杀还留在全屏里。
/// 所以只伪造<b>输入</b>，动作、窗口、编排全部走生产代码。
/// </para>
/// </summary>
internal static class FullScreenSwitchProbe
{
    /// <summary>每步等待。真 Show/Hide + 布局要几帧，600ms 足够（--view-probe 同值）。</summary>
    private const int StepMs = 600;

    /// <summary>伪造的全屏证据。</summary>
    private static readonly Interop.FullScreenInfo FakeFull = new(
        true, "fake:quns=3 +rect -caption", 3, new IntPtr(0x1234),
        "fake-fullscreen-app", "Fake Game", 4242, true, true, @"\\.\DISPLAY1");

    /// <summary>伪造的桌面证据。</summary>
    private static readonly Interop.FullScreenInfo FakeDesktop = new(
        false, "fake:desktop", 2, new IntPtr(0x5678),
        "Progman", "Program Manager", 1111, false, false, @"\\.\DISPLAY1");

    /// <summary>一步：可选的准备动作 + 喂给状态机的探测结果 + 期望的三视图可见性。</summary>
    private readonly record struct Step(string Label,
                                       Action? Setup,
                                       Interop.FullScreenInfo? Info,
                                       bool ExpectMain,
                                       bool ExpectFloat,
                                       bool ExpectTray);

    public static void Run(FullScreenAutoSwitch auto,
                           Func<string> snapshot,
                           Func<FullScreenAutoSwitch.View, bool> isShown,
                           Action<FullScreenAutoSwitch.View> show,
                           Action<FullScreenAutoSwitch.View> hide,
                           string path,
                           bool multi,
                           Action<DispatcherTimer> onStartTimer,
                           Action onDone)
    {
        const FullScreenAutoSwitch.View VMain = FullScreenAutoSwitch.View.Main;
        const FullScreenAutoSwitch.View VFloat = FullScreenAutoSwitch.View.Float;
        const FullScreenAutoSwitch.View VTray = FullScreenAutoSwitch.View.Taskbar;

        // 步数**按当前档位算**（M4.23）：门限是 0.5s~3s 可调，写死三步四步
        // 会在 2s/3s 档下直接失败（走到最后一步门限还没跨过去）。
        // 触发发生在第 m 个"全屏"步：500*(m-1) ≥ HoldMs ⇒ m = ceil(HoldMs/500) + 1。
        int hold = auto.HoldMs;
        int need = hold / 500 + 1;

        var sb = new StringBuilder();
        sb.AppendLine($"time  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"模式  : {(multi ? "多视图叠加（--fs-multi，M4.26）" : "只开任务栏条（经典链路）")}");
        sb.AppendLine($"档位  : {hold}ms（进入与退出同一档）");
        sb.AppendLine($"step  : {StepMs}ms/步（判定用虚拟时钟 +500ms/步，与生产轮询同频）");
        sb.AppendLine($"trace : {Interop.Trace.Path}");
        sb.AppendLine();
        sb.AppendLine("说明：EnterAutoFloat / ExitToTaskbar 是**生产接线**（真的 Show/Hide 窗口），");
        sb.AppendLine("     只有探测结果与时钟是伪造的。");
        sb.AppendLine();

        var steps = new List<Step>();

        if (!multi)
        {
            steps.Add(new Step("起点（任务栏）", null, FakeDesktop, false, false, true));
            for (int k = 1; k <= need; k++)
            {
                bool fired = 500 * k >= hold + 500;   // 该步结束时门限是否已跨过
                steps.Add(new Step(
                    $"全屏#{k}（{500 * k}ms{(k == need ? " → 应真切悬浮窗" : "，未到门限")}）",
                    null, FakeFull,
                    ExpectMain: false, ExpectFloat: fired, ExpectTray: !fired));
            }

            for (int k = 1; k <= need; k++)
            {
                bool fired = 500 * k >= hold + 500;
                steps.Add(new Step(
                    $"退全屏#{k}（{500 * k}ms{(k == need ? " → 应真回任务栏" : "，未到门限")}）",
                    null, FakeDesktop,
                    ExpectMain: false, ExpectFloat: !fired, ExpectTray: fired));
            }
        }
        else
        {
            // 阶段 A：大窗口 + 任务栏条（浮窗没开）→ 进全屏应当自动弹出浮窗并收起条。
            steps.Add(new Step("起点：大窗口+任务栏条",
                               () => { show(VMain); show(VTray); hide(VFloat); },
                               null, true, false, true));
            for (int k = 1; k <= need; k++)
            {
                bool fired = 500 * k >= hold + 500;
                steps.Add(new Step($"全屏#{k}", null, FakeFull, true, fired, !fired));
            }

            for (int k = 1; k <= need; k++)
            {
                bool fired = 500 * k >= hold + 500;
                steps.Add(new Step($"退全屏#{k}", null, FakeDesktop, true, !fired, fired));
            }

            // 阶段 B：用户自己把浮窗打开（manual），三视图同时在 → 进全屏只收条，
            // 退出全屏后浮窗必须**保留**（8.2 的核心），条放回来，仍然是三视图。
            steps.Add(new Step("用户打开浮窗（manual，三视图齐）", () => show(VFloat), null, true, true, true));
            for (int k = 1; k <= need; k++)
            {
                bool fired = 500 * k >= hold + 500;
                steps.Add(new Step($"全屏#{k}", null, FakeFull, true, true, !fired));
            }

            for (int k = 1; k <= need; k++)
            {
                bool fired = 500 * k >= hold + 500;
                steps.Add(new Step($"退全屏#{k}", null, FakeDesktop, true, true, fired));
            }
        }

        long clock = 1000;
        auto.Clock = () => clock;

        int i = 0;
        int violations = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StepMs) };

        timer.Tick += (_, _) =>
        {
            if (i >= steps.Count)
            {
                timer.Stop();
                sb.AppendLine();
                sb.AppendLine("== 判定 ==");
                sb.AppendLine("  通过条件：status 与 step 两个视图位逐行等于期望；");
                sb.AppendLine("            最终 view=Taskbar、origin=Manual（回落时已归位）。");
                sb.AppendLine($"  不符合期望的步数 : {violations}");
                sb.AppendLine($"  终态视图 : {auto.CurrentView}（期望 Taskbar）");
                sb.AppendLine($"  终态来源 : {auto.FloatOrigin}（期望 Manual）");
                sb.AppendLine($"  判定 : {(violations == 0 && auto.CurrentView == FullScreenAutoSwitch.View.Taskbar &&
                                            auto.FloatOrigin == FullScreenAutoSwitch.Origin.Manual
                                            ? "通过" : "失败（见上面 ✗ 的行）")}");
                sb.AppendLine();
                sb.AppendLine("== 状态机迁移日志 ==");
                foreach (string line in auto.Journal) sb.AppendLine("  " + line);

                File.WriteAllText(path, sb.ToString());
                onDone();
                return;
            }

            Step s = steps[i];

            if (s.Setup is not null)
            {
                s.Setup();
            }
            else if (s.Info is { } info)
            {
                // 喂给生产状态机。这里不调 auto.Poll()（那会走真实探测），
                // 而是把伪造结果直接送进去 —— 判定、防抖、动作、来源标记全走生产代码。
                auto.Poll(info);
                clock += 500;
            }

            bool m = isShown(VMain), f = isShown(VFloat), t = isShown(VTray);
            bool ok = m == s.ExpectMain && f == s.ExpectFloat && t == s.ExpectTray;
            if (!ok) violations++;

            sb.AppendLine($"#{i + 1} {s.Label,-34} view={auto.CurrentView,-8} origin={auto.FloatOrigin,-7}");
            sb.AppendLine($"     main={m} float={f} tray={t}  期望 main={s.ExpectMain} " +
                          $"float={s.ExpectFloat} tray={s.ExpectTray} {(ok ? "✓" : "✗")}");
            sb.AppendLine($"     {snapshot()}");
            i++;
        };

        onStartTimer(timer);
    }
}
