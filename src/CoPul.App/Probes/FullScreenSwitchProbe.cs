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
/// <item><c>--view-probe</c>：验 ShowView 的**互斥编排**，视图由人手动切。</item>
/// <item><b>本探针</b>：把两者接在一起 —— 用<b>真实</b>的 <c>EnterAutoFloat</c> / <c>ExitToTaskbar</c>
/// （也就是真的会 Show 悬浮窗、Hide 任务栏条的那两个回调），只是把**探测结果**换成伪造的。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>为什么必须单独验这一条。</b><c>--fullscreen-probe --fs-fake</c> 里那两个动作是
/// <c>calls.Add(...)</code>，它证明了"状态机在该切的时候发了切指令"；但这条指令接到
/// <c>ShowView</c> 之后到底有没有真的把条藏起来、把悬浮窗摆到正确的屏，那片代码
/// 完全没被覆盖。而 M3 恰恰在"Show/Hide 顺序"上踩过坑（B 方案残影、巡检空转）。
/// </para>
///
/// <para>
/// <b>为什么不真开全屏。</b>真全屏要独占显示器，跑一次占一次整屏，中途被杀还留在全屏里
/// （《剑星》就是现场例子）。所以只伪造<b>输入</b>，动作、窗口、编排全部走生产代码 ——
/// 与 <c>--force-edge</c> / <c>--float-snap-probe</c> 同一套取舍。
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

    /// <summary>
    /// 跑端到端链路：任务栏 → (全屏) → 真切悬浮窗 → (退全屏) → 真回任务栏。
    /// </summary>
    /// <param name="auto">**生产中同一个**状态机实例（已有真实接线）。</param>
    /// <param name="snapshot">当前三窗口可见性，供逐行记录。</param>
    /// <param name="startView">起始视图，必须能真的切过去（通常用 taskbar）。</param>
    /// <param name="onStartTimer">启动 DispatcherTimer。</param>
    /// <param name="onDone">结束回调。</param>
    public static void Run(FullScreenAutoSwitch auto,
                           Func<string> snapshot,
                           string path,
                           Action<DispatcherTimer> onStartTimer,
                           Action onDone)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"time  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"step  : {StepMs}ms/步");
        sb.AppendLine($"trace : {Interop.Trace.Path}");
        sb.AppendLine();
        sb.AppendLine("说明：EnterAutoFloat / ExitToTaskbar 是**生产接线**（真的 Show/Hide 窗口），");
        sb.AppendLine("     只有探测结果与时钟是伪造的。");
        sb.AppendLine();

        // 每轮喂一个伪造结果，并把真实的窗口可见性记下来。
        // 序列特意按生产的 500ms 轮询展开（虚拟时钟每步 +500ms），
        // 让 800ms / 500ms 两个防抖门限真的被跨过去。
        var steps = new List<(string Label, Interop.FullScreenInfo Info)>
        {
            ("起点（任务栏）", FakeDesktop),
            ("全屏#1（开始计时）", FakeFull),
            ("全屏#2（500ms，未到 800）", FakeFull),
            ("全屏#3（1000ms → 应真切悬浮窗）", FakeFull),
            ("退全屏#1（开始计时）", FakeDesktop),
            ("退全屏#2（500ms → 应真回任务栏）", FakeDesktop),
        };

        long clock = 1000;
        auto.Clock = () => clock;

        int i = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StepMs) };

        timer.Tick += (_, _) =>
        {
            if (i >= steps.Count)
            {
                timer.Stop();
                sb.AppendLine();
                sb.AppendLine("== 判定 ==");
                sb.AppendLine("  通过条件：seq 里出现 view=Float，且最后一行是 view=Taskbar；");
                sb.AppendLine("            每行可见窗口数恰好 1（A-06）。");
                sb.AppendLine($"  终态视图 : {auto.CurrentView}（期望 Taskbar）");
                sb.AppendLine($"  终态来源 : {auto.FloatOrigin}（期望 Manual，回落时已归位）");
                sb.AppendLine();
                sb.AppendLine("== 状态机迁移日志 ==");
                foreach (string line in auto.Journal) sb.AppendLine("  " + line);

                File.WriteAllText(path, sb.ToString());
                onDone();
                return;
            }

            var (label, info) = steps[i];

            // 喂给生产状态机。这里不调 auto.Poll()（那会走真实探测），
            // 而是把伪造结果直接送进去 —— 判定、防抖、动作、来源标记全走生产代码。
            auto.Poll(info);
            clock += 500;

            string snap = snapshot();
            int visible = snap.Split("visible=True", StringSplitOptions.None).Length - 1;
            bool ok = visible == 1;

            sb.AppendLine($"#{i + 1} {label,-32} view={auto.CurrentView,-8} origin={auto.FloatOrigin,-7} " +
                          $"full={auto.IsFullScreenSteady,-5} 可见={visible} {(ok ? "✓" : "✗")}");
            sb.AppendLine($"     {snap}");
            i++;
        };

        onStartTimer(timer);
    }
}
