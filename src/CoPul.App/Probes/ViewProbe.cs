using System.IO;
using System.Text;
using System.Windows.Threading;
using CoPul.App.Sdk;

namespace CoPul.App.Probes;

/// <summary>
/// <c>--view-probe</c>：三视图互斥编排的验收探针（A-06 / 8.1 / 8.6）。
///
/// <para>
/// 为什么必须另起一个探针，而不是跟 <c>--fullscreen-probe --fs-fake</c> 合并：
/// 那条验的是「状态机在什么条件下发什么动作」，动作是记账的、不碰窗口；
/// 这条验的是「真的 Show/Hide 之后，屏幕上到底还剩几个窗口可见」。
/// 状态机对了但编排写错（比如忘了 Hide 旧视图），表现出来是"两个窗口叠在一起"，
/// 而状态机的日志会全程显示正常 —— 两者必须分开测才分得清。
/// </para>
///
/// <para>
/// <b>时间预算。</b>本机后台拉起的 GUI 约 60 秒会被环境回收（M3 踩过），
/// 探针总时长压在 ~5 秒内：8 步 × 600ms。到点就写盘退出，不追求跑完更多步 ——
/// 半份证据也比被回收后什么都没有强。
/// </para>
/// </summary>
internal static class ViewProbe
{
    /// <summary>单步等待。窗口 Show/Hide 到 IsVisible 反映出来要过一帧，600ms 足够。</summary>
    private const int StepMs = 600;

    /// <summary>
    /// 跑互切链路。每一步记录"哪几个窗口可见"，最后写盘并退出。
    /// </summary>
    /// <param name="show">切到某个视图。必须是生产路径的同一个 ShowView（不要另写一套）。</param>
    /// <param name="snapshot">当前三个窗口的可见性与矩形，供报告逐行记录。</param>
    /// <param name="path">报告输出路径。</param>
    /// <param name="onStartTimer">App 把 DispatcherTimer 启动起来。</param>
    /// <param name="onDone">结束回调（App 用来 Shutdown）。</param>
    public static void Run(Action<FullScreenAutoSwitch.View> show,
                           Func<string> snapshot,
                           string path,
                           FullScreenAutoSwitch.View start,
                           Action<DispatcherTimer> onStartTimer,
                           Action onDone)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"time  : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"start : {start}");
        sb.AppendLine($"step  : {StepMs}ms/步");
        sb.AppendLine($"trace : {Interop.Trace.Path}");
        sb.AppendLine();

        // 链路：每条都从当前视图切走再切回，覆盖 A-06 的"不得同时可见"
        var steps = new List<(string Label, FullScreenAutoSwitch.View Target)>
        {
            ("起点", start),
            ("→ 大窗口", FullScreenAutoSwitch.View.Main),
            ("→ 悬浮窗", FullScreenAutoSwitch.View.Float),
            ("→ 任务栏条", FullScreenAutoSwitch.View.Taskbar),
            ("→ 大窗口", FullScreenAutoSwitch.View.Main),
            ("→ 任务栏条", FullScreenAutoSwitch.View.Taskbar),
            ("→ 悬浮窗", FullScreenAutoSwitch.View.Float),
            ("→ 大窗口", FullScreenAutoSwitch.View.Main),
        };

        int i = 0;
        int violations = 0;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(StepMs) };

        timer.Tick += (_, _) =>
        {
            if (i >= steps.Count)
            {
                timer.Stop();
                sb.AppendLine();
                sb.AppendLine("== 结论 ==");
                sb.AppendLine($"  违规步数 : {violations}");
                sb.AppendLine("  期望：每一行的可见窗口数恰好 1。0 个 = 切换时把新旧都藏了；");
                sb.AppendLine("        ≥2 个 = 忘了 Hide 旧视图（A-06 违反，两份渲染同时吃 GPU）。");
                sb.AppendLine($"  判定 : {(violations == 0 ? "通过" : "失败（见上面 ✗ 的行）")}");
                File.WriteAllText(path, sb.ToString());
                onDone();
                return;
            }

            var (label, target) = steps[i];
            show(target);
            string snap = snapshot();

            // 数 "visible=True" 出现次数 = 当前可见的窗口个数
            int visible = snap.Split("visible=True", StringSplitOptions.None).Length - 1;
            bool ok = visible == 1;
            if (!ok) violations++;

            sb.AppendLine($"#{i + 1} {label,-12} → {snap}");
            sb.AppendLine($"     可见窗口数 = {visible} {(ok ? "✓" : "✗")}");
            i++;
        };

        onStartTimer(timer);
    }
}
