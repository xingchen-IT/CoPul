using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Threading;
using CoPul.App.Sdk;

namespace CoPul.App.Probes;

/// <summary>
/// <c>--fs-live-probe</c>：全屏自动切换的**真机**验收（M4.22）。
///
/// <para>
/// 与 <see cref="FullScreenSwitchProbe"/> 的分工：那条喂伪造的探测结果（验状态机与编排），
/// 这条**什么都不伪造** —— 真的 <c>SHQueryUserNotificationState</c>、真的前台窗口、
/// 真的 Show/Hide 悬浮窗，只按固定节奏把状态采样成文本。
/// </para>
///
/// <para>
/// <b>为什么要这条探针。</b>M4.22 的 bug（点一下悬浮窗就回落任务栏、再点一下又回来）
/// 出在"前台窗口是谁"这条真实链路的交叉上：伪造的 <c>FullScreenInfo</c> 是直接喂给状态机的，
/// 根本不经过 <c>GetForegroundWindow()</c>，所以那条探针结构上验不到它。
/// 必须真跑：另开一个进程造无边框铺满屏的窗口当"游戏"，再让 CoPul 自己切。
/// </para>
///
/// <para>
/// 采样行里最要紧的两列：<c>fg(own?)</c>（前台是不是我们自己）与 <c>full</c>（判定结论）。
/// 修复前：Activate 悬浮窗之后 fg 变 own，full 立刻变 False → 500ms 后 view 回 Taskbar。
/// 修复后：fg 仍是 own，但 full 保持 True（改判了候选窗口），view 稳在 Float。
/// </para>
/// </summary>
internal static class FullScreenLiveProbe
{
    /// <summary>采样间隔。与生产轮询同为 500ms，避免两个定时器互相错开导致读数难对。</summary>
    private const int SampleMs = 500;

    /// <summary>
    /// 采样一段时间内的真实状态迁移。
    /// </summary>
    /// <param name="auto">生产中同一个状态机（真实 2Hz 探测已经在跑）。</param>
    /// <param name="snapshot">三窗口可见性。</param>
    /// <param name="path">输出文本。</param>
    /// <param name="focusAtMs">第几毫秒模拟"用户点了一下悬浮窗"（&lt;0 = 不做）。</param>
    /// <param name="endMs">第几毫秒收尾退出。</param>
    /// <param name="activateFloat">真的把悬浮窗顶到前台（等价于用户点它）。</param>
    /// <param name="onStartTimer">启动 DispatcherTimer。</param>
    /// <param name="onDone">结束回调。</param>
    public static void Run(FullScreenAutoSwitch auto,
                           Func<string> snapshot,
                           string path,
                           int focusAtMs,
                           int endMs,
                           Action activateFloat,
                           Action<DispatcherTimer> onStartTimer,
                           Action onDone)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"time : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine("说明 ：真实探测 + 真实窗口，不伪造任何输入（与 --fs-switch-probe 互补）。");
        sb.AppendLine($"采样 ：每 {SampleMs}ms，共 {endMs}ms");
        sb.AppendLine($"点悬浮：{(focusAtMs >= 0 ? focusAtMs + "ms 时 Activate 悬浮窗（模拟用户点它一下）" : "不做")}");
        sb.AppendLine($"替补  ：{(auto.UseFullHwndCandidate ? "开（前台是自己时改判上次的全屏窗口）" : "关（--no-fs-candidate，旧行为）")}");
        sb.AppendLine($"档位  ：进入确认 {auto.EnterHoldMs}ms / 退出确认 {auto.ExitHoldMs}ms（--fs-hold，M4.23）");
        sb.AppendLine();
        sb.AppendLine("   t/ms view     origin  steady fg                   full  reason");
        sb.AppendLine();

        // 状态机每次探测都会回调，把最近一次证据存下来给采样行用
        Interop.FullScreenInfo last = default;
        auto.Observed = (info, _note) => last = info;

        var sw = Stopwatch.StartNew();
        bool focused = false;
        int transitions = 0;
        string lastTransition = auto.LastTransition;

        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(SampleMs) };
        timer.Tick += (_, _) =>
        {
            long ms = sw.ElapsedMilliseconds;

            IntPtr fg = Interop.FullScreenDetector.Foreground;
            bool own = Interop.Monitors.IsOwnWindow(fg);
            string fgDesc = $"0x{fg.ToInt64():X}{(own ? "(own)" : "(ext)")}";

            sb.AppendLine($"{ms,7} {auto.CurrentView,-8} {auto.FloatOrigin,-7} " +
                          $"{auto.IsFullScreenSteady,-6} {fgDesc,-20} {last.IsFullScreen,-5} {last.Reason}");
            sb.AppendLine($"        可见: {snapshot()}");
            sb.AppendLine($"        候选=0x{auto.FullHwnd.ToInt64():X} 最近迁移={auto.LastTransition}");

            if (auto.LastTransition != lastTransition)
            {
                transitions++;
                lastTransition = auto.LastTransition;
                // 迁移时刻：档位取证主要看这一行 —— 门限越长，这里离"游戏真的全屏/退出"越远。
                sb.AppendLine($"---- {ms}ms 迁移 #{transitions}：{auto.LastTransition} ----");
            }

            if (focusAtMs >= 0 && !focused && ms >= focusAtMs)
            {
                focused = true;
                sb.AppendLine($"---- {ms}ms：Activate 悬浮窗（模拟用户点它一下）----");
                activateFloat();
            }

            if (ms < endMs) return;

            timer.Stop();
            sb.AppendLine();
            sb.AppendLine("== 判定 ==");
            sb.AppendLine($"  迁移次数（含起点）: {transitions}");
            sb.AppendLine($"  终态 view         : {auto.CurrentView}");
            sb.AppendLine($"  终态 origin       : {auto.FloatOrigin}");
            sb.AppendLine();
            sb.AppendLine("== 状态机日志 ==");
            foreach (string line in auto.Journal) sb.AppendLine("  " + line);

            File.WriteAllText(path, sb.ToString());
            onDone();
        };

        onStartTimer(timer);
    }
}
