using System.IO;
using System.Text;
using System.Windows.Threading;
using CoPul.App.Sdk;

namespace CoPul.App.Probes;

/// <summary>
/// <c>--view-probe</c>：三视图**叠加编排**的验收探针（M4.26 起；此前验的是互斥）。
///
/// <para>
/// 为什么必须另起一个探针，而不是跟 <c>--fullscreen-probe --fs-fake</c> 合并：
/// 那条验的是「状态机在什么条件下发什么动作」，动作是记账的、不碰窗口；
/// 这条验的是「真的 Show/Hide 之后，屏幕上到底有几个窗口可见」。
/// 状态机对了但编排写错（比如 Show 时顺手把别的视图藏了），表现出来就是
/// "我要三个一起看，结果只剩一个"，而状态机日志会全程显示正常 —— 两者必须分开测。
/// </para>
///
/// <para>
/// <b>时间预算。</b>本机后台拉起的 GUI 约 60 秒会被环境回收（M3 踩过），
/// 探针总时长压在 ~5 秒内：8 步 × 600ms。到点就写盘退出。
/// </para>
/// </summary>
internal static class ViewProbe
{
    /// <summary>单步等待。窗口 Show/Hide 到 IsVisible 反映出来要过一帧，600ms 足够。</summary>
    private const int StepMs = 600;

    /// <summary>
    /// 跑叠加链路：全隐藏 → 逐个叠加到 3 个 → 逐个收起 → 归零 → 恢复大窗口。
    /// </summary>
    /// <param name="show">显示某个视图。必须是生产路径的同一个 ShowView（不要另写一套）。</param>
    /// <param name="hide">隐藏某个视图（生产路径的 HideView）。</param>
    /// <param name="snapshot">当前三个窗口的可见性与矩形，供报告逐行记录。</param>
    /// <param name="path">报告输出路径。</param>
    /// <param name="start">先显示哪个视图（通常 main）。</param>
    /// <param name="onStartTimer">App 把 DispatcherTimer 启动起来。</param>
    /// <param name="onDone">结束回调（App 用来 Shutdown）。</param>
    public static void Run(Action<FullScreenAutoSwitch.View> show,
                           Action<FullScreenAutoSwitch.View> hide,
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
        sb.AppendLine("说明：M4.26 起三视图可同时显示，「可见窗口数」应当随步数按 0→1→2→3→2→1→0→1 走。");
        sb.AppendLine("      旧行为（互斥）下叠加那两步会停在 1，本探针会直接判失败。");
        sb.AppendLine();

        FullScreenAutoSwitch.View[] rest = start switch
        {
            FullScreenAutoSwitch.View.Main => new[] { FullScreenAutoSwitch.View.Float, FullScreenAutoSwitch.View.Taskbar },
            FullScreenAutoSwitch.View.Float => new[] { FullScreenAutoSwitch.View.Main, FullScreenAutoSwitch.View.Taskbar },
            _ => new[] { FullScreenAutoSwitch.View.Main, FullScreenAutoSwitch.View.Float },
        };
        FullScreenAutoSwitch.View second = rest[0];
        FullScreenAutoSwitch.View third = rest[1];

        // 期望值写死在表里而不是"上一步 +1"：算错了也照样对得上就失去意义。
        var steps = new List<(string Label, Action Act, int Expect)>
        {
            ("全隐藏（起点）", () =>
            {
                hide(FullScreenAutoSwitch.View.Main);
                hide(FullScreenAutoSwitch.View.Float);
                hide(FullScreenAutoSwitch.View.Taskbar);
            }, 0),
            ($"显示 {start}", () => show(start), 1),
            ($"叠加 {second}", () => show(second), 2),
            ($"叠加 {third}（三个同时显示）", () => show(third), 3),
            ($"收起 {second}", () => hide(second), 2),
            ($"收起 {third}", () => hide(third), 1),
            ($"收起 {start}（全空）", () => hide(start), 0),
            ("恢复大窗口", () => show(FullScreenAutoSwitch.View.Main), 1),
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
                sb.AppendLine($"  不符合期望的步数 : {violations}");
                sb.AppendLine("  期望：按 0→1→2→3→2→1→0→1 变化（三个视图互不干扰）。");
                sb.AppendLine("        叠加那步只有 1 个 = Show 时顺手藏了别的视图（回到 M4.26 的互斥行为）；");
                sb.AppendLine("        0 个 = 切换时把新旧都藏了。");
                sb.AppendLine($"  判定 : {(violations == 0 ? "通过" : "失败（见上面 ✗ 的行）")}");
                File.WriteAllText(path, sb.ToString());
                onDone();
                return;
            }

            var (label, act, expect) = steps[i];
            act();
            string snap = snapshot();

            // 数 "visible=True" 出现次数 = 当前可见的窗口个数
            int visible = snap.Split("visible=True", StringSplitOptions.None).Length - 1;
            bool ok = visible == expect;
            if (!ok) violations++;

            sb.AppendLine($"#{i + 1} {label,-22} 可见={visible} 期望={expect} {(ok ? "✓" : "✗")}");
            sb.AppendLine($"     {snap}");
            i++;
        };

        onStartTimer(timer);
    }
}
