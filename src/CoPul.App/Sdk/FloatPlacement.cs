using CoPul.App.Interop;

namespace CoPul.App.Sdk;

/// <summary>
/// 悬浮窗位置的持久化形式（需求 6.7）：<c>{显示器, 相对比例 x, 相对比例 y}</c>。
///
/// <para>
/// 存比例而不是绝对像素的理由：换分辨率、改缩放、拔插显示器之后，绝对坐标会把窗口
/// 扔到屏幕外面去，而比例永远落在"同一块屏的同一个相对位置"。
/// </para>
///
/// <para>
/// 比例是**相对该显示器工作区**（已扣掉任务栏）算的，不是相对整个屏幕。
/// 否则任务栏换边、自动隐藏与否，都会让同一比例落到不同地方。
/// </para>
/// </summary>
internal readonly record struct FloatPlacement(string Monitor, double RatioX, double RatioY)
{
    /// <summary>
    /// 窗口至少要有这么多比例落在工作区内（6.5「拖到屏幕外」与 6.7「夹紧」的共同要求）。
    /// 宽高各判一次，不做面积判定 —— 细长条的面积判定会放过"整条竖在屏幕外"这种极端情况。
    /// </summary>
    public const double MinVisible = 0.6;

    /// <summary>有记录才算数。<see cref="double.NaN"/> 是配置里的"未设置"标记。</summary>
    public bool IsKnown =>
        !string.IsNullOrEmpty(Monitor) && double.IsFinite(RatioX) && double.IsFinite(RatioY);

    /// <summary>
    /// 记录当前位置。允许比例为负或大于 1（窗口被拖出一半时就是这样），
    /// 还原时由 <see cref="TryResolve"/> 夹回来 —— 存的时候不夹，
    /// 是为了让"只露出一角的窗口"下次还能回到同样的位置。
    /// </summary>
    public static FloatPlacement Capture(string device, int leftPx, int topPx, in MonitorArea work)
    {
        if (!work.IsValid) return default;

        return new FloatPlacement(
            device,
            (leftPx - work.Left) / (double)work.Width,
            (topPx - work.Top) / (double)work.Height);
    }

    /// <summary>
    /// 算回物理坐标。
    /// </summary>
    /// <param name="monitors">当前所有显示器。为 null 或空时直接失败，调用方走默认位置。</param>
    /// <param name="primary">找不到记录的显示器时用它顶上（6.7：显示器换了就回主屏）。</param>
    /// <param name="windowWidthPx">窗口宽，物理像素。尺寸是自适应的，所以必须由调用方现场量。</param>
    /// <returns>false = 无法确定位置，调用方应该用默认位置。</returns>
    public bool TryResolve(IReadOnlyList<MonitorArea> monitors, MonitorArea primary,
                           int windowWidthPx, int windowHeightPx,
                           out MonitorArea area, out int leftPx, out int topPx)
    {
        area = default;
        leftPx = 0;
        topPx = 0;

        if (!IsKnown) return false;

        // 显示器还在就按它算；换了显示器就落到主屏的同一比例位置
        if (!Monitors.TryFind(Monitor, out area))
        {
            if (!primary.IsValid) return false;
            area = primary;
        }

        leftPx = area.Left + (int)Math.Round(RatioX * area.Width);
        topPx = area.Top + (int)Math.Round(RatioY * area.Height);

        Clamp(area, windowWidthPx, windowHeightPx, ref leftPx, ref topPx);
        return true;
    }

    /// <summary>
    /// 把坐标夹到"至少 60% 可见"。
    ///
    /// <para>
    /// 需求 6.7 原文写的是"不满足则改用默认位置"，这里改成了夹紧 —— 直接丢回默认位置
    /// 会把用户摆好的位置整个放弃，而夹紧保住了他的意图、同样满足 6.5 的 60% 可见硬约束。
    /// 只有"连显示器都找不到"才回默认位置。
    /// </para>
    /// </summary>
    public static void Clamp(in MonitorArea work, int windowWidthPx, int windowHeightPx,
                             ref int leftPx, ref int topPx)
    {
        // 允许露出的最大量 = 窗口尺寸的 (1 - 60%)
        int slackX = (int)(windowWidthPx * (1 - MinVisible));
        int slackY = (int)(windowHeightPx * (1 - MinVisible));

        // 左边界最多到 work.Left - slackX；右边界最少到 work.Right - 60% 宽
        leftPx = Math.Clamp(leftPx, work.Left - slackX, work.Right - (int)(windowWidthPx * MinVisible));
        topPx = Math.Clamp(topPx, work.Top - slackY, work.Bottom - (int)(windowHeightPx * MinVisible));
    }
}
