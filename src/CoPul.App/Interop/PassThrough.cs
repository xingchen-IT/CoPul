using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace CoPul.App.Interop;

/// <summary>
/// 跨进程点击穿透（M4.54）。
///
/// <para>
/// <b>为什么不能只用 <c>HTTRANSPARENT</c></b>：那个返回值只在<b>同一线程</b>的窗口之间
/// 传递（微软对 HTTRANSPARENT 的定义就是 "in the same thread"）。游戏在另一个进程，
/// 系统走到进程边界就停 —— 点击既没落到游戏，也没被自己处理，直接消失。
/// </para>
///
/// <para>
/// <b>跨进程只有一条路</b>：<c>WS_EX_LAYERED | WS_EX_TRANSPARENT</c> 一起加。
/// 单独加 <c>WS_EX_TRANSPARENT</c> 无效（实测点击仍被本窗口吃掉），
/// 单独加 <c>WS_EX_LAYERED</c> 只是变成分层窗口，照样挡路。
/// </para>
///
/// <para>
/// <b>实测依据</b>（三组覆盖层各跑一次，跨进程，目标窗口独立进程）：
/// <list type="table">
/// <item><term>HTTRANSPARENT</term><description>WindowFromPoint 命中覆盖层，目标收不到点击</description></item>
/// <item><term>WS_EX_TRANSPARENT</term><description>同样命中覆盖层，覆盖层自己收到点击</description></item>
/// <item><term>LAYERED + TRANSPARENT</term><description>WindowFromPoint 命中目标窗口，左键右键都到达</description></item>
/// </list>
/// </para>
///
/// <para>
/// <b>千万不要调 <c>SetLayeredWindowAttributes</c></b>：WPF 的 <c>AllowsTransparency</c>
/// 用 <c>UpdateLayeredWindow</c> 逐像素提供位图，而 <c>LWA_ALPHA</c> 会把窗口切到
/// "常量 alpha + 色键"那条路，WPF 的位图随即被弃用 —— 实测表现为悬浮窗变成一块
/// 纯灰空条，数字全没。纯 Win32 窗口上那句是必需的，对 WPF 的 per-pixel 窗口却是破坏性的。
/// </para>
///
/// <para>
/// <b><c>WS_EX_LAYERED</c> 必须由 WPF 自己给</b>：窗口创建后再 <c>SetWindowLongPtr</c>
/// 加这一位无效 —— 实测写入 <c>0x801A8</c>，<c>SetWindowLongPtr</c> 返回时立刻回读就是
/// <c>0x1A8</c>，WPF 的窗口过程在 <c>WM_STYLECHANGING</c> 阶段把它改了回去。
/// 所以这里只负责 <c>WS_EX_TRANSPARENT</c>，layered 的前提是
/// <c>FloatWindow.xaml</c> 里的 <c>AllowsTransparency="True"</c>。
/// </para>
///
/// <para>
/// <b>只改这两位</b>：进入时记下原始 exstyle，退出时按掩码还原，不碰 topmost 之类的其他位
/// —— 否则穿透开关一拨，置顶就悄悄丢了。
/// </para>
/// </summary>
internal static class PassThrough
{
    private const int GWL_EXSTYLE = -20;
    private const long WS_EX_LAYERED = 0x00080000L;
    private const long WS_EX_TRANSPARENT = 0x00000020L;

    private const long Mask = WS_EX_LAYERED | WS_EX_TRANSPARENT;

    /// <summary>进入穿透前的扩展样式。窗口销毁前一直留着，用于精确还原。</summary>
    private static readonly Dictionary<IntPtr, long> Baseline = new();

    /// <summary>当前是否处于"真正能跨进程穿透"的状态（两个位都在）。诊断用。</summary>
    public static bool IsOn(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;
        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        return (ex & Mask) == Mask;
    }

    /// <summary>原始扩展样式，写进诊断串（<c>exStyle=0x...</c>）。</summary>
    public static long ExStyle(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return 0;
        return GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
    }

    /// <summary>
    /// 开或关穿透。重复调用同向值是空操作（<c>GetWindowLongPtr</c> 之后直接返回），
    /// 所以可以挂在每次状态变化的路径上，不必自己判重。
    /// </summary>
    /// <returns>这次调用是否真的改了窗口样式。</returns>
    public static bool Set(IntPtr hwnd, bool on)
    {
        if (hwnd == IntPtr.Zero) return false;

        long ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        long target;

        if (on)
        {
            if (!Baseline.ContainsKey(hwnd)) Baseline[hwnd] = ex;
            target = ex | Mask;
        }
        else
        {
            // 只还原这两位：期间可能有人改过 topmost / toolwindow，整串覆盖会把那些改动抹掉
            long original = Baseline.TryGetValue(hwnd, out long saved) ? saved : ex;
            Baseline.Remove(hwnd);
            target = (ex & ~Mask) | (original & Mask);
        }

        if (target == ex) return false;

        SetWindowLongPtr(hwnd, GWL_EXSTYLE, (IntPtr)target);

        // 改过 exstyle 要通知 DWM 重读，否则仍按旧样式缓存渲染
        Monitors.NotifyFrameChanged(hwnd);

        // 自检：WPF 的 HwndTarget 按自己的 per-pixel 需求改写 LAYERED 位，
        // 分三段回读才能看出是哪一步抹掉的。
        long after = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
        Trace.Log("pass", $"on={on} 写入=0x{target:X} 回读=0x{after:X}");
        return true;
    }

    /// <summary>窗口关闭时清掉记录，避免字典长期挂着已失效的句柄。</summary>
    public static void Forget(IntPtr hwnd) => Baseline.Remove(hwnd);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int index, IntPtr value);
}
