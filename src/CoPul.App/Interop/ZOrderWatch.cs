using System.Runtime.InteropServices;
using System.Threading;

namespace CoPul.App.Interop;

/// <summary>
/// Z 序变化的**事件源**（M4.30）：谁被提到谁上面时立刻通知，不再等下一秒的巡检。
///
/// <para>
/// <b>为什么非要有它</b>：M4.21 的置顶是"每秒巡检一次"，前提是"被压住"这件事能被
/// <c>GW_HWNDPREV</c> 链看见。用真机打 MC 的 F11 抓到的现场是：GLFW 进全屏时会给
/// 自己的窗口加 <c>WS_EX_TOPMOST</c>（实测 gameEx 0x040010 → 0x040018），于是
/// <b>两个窗口同处 topmost 组，谁最后 <c>SetWindowPos</c> 谁在上</b>。
/// 巡检是 1Hz，所以每次游戏提顶，悬浮窗都要空窗半秒才被拉回来 ——
/// 用户看到的就是"玩 MC 时它老不在最上层"。实测恢复延迟：旧法中位 551ms。
/// </para>
///
/// <para>
/// <b>哪个事件才是对的（实测，不是猜）</b>：写了个独立探针把游戏的窗口
/// <c>SetWindowPos(HWND_TOPMOST)</c> 一次，收到的是
/// <c>EVENT_OBJECT_LOCATIONCHANGE</c>（19 条），<b>没有</b>
/// <c>EVENT_OBJECT_REORDER</c>；换成 <c>SetForegroundWindow</c> 才同时来 REORDER。
/// 也就是说"自己提顶"这条路径只发 LOCATIONCHANGE —— 只挂 REORDER 等于没挂。
/// </para>
///
/// <para>
/// <b>合并而不是限流</b>：LOCATIONCHANGE 是高频事件（拖窗口、动画、悬停都在刷）。
/// 用"上次执行距现在 < 80ms 就丢掉"的限流会把整串事件连尾一起丢光，
/// 于是改成：第一个事件排一次 16ms 后的执行，期间再来的只合并。
/// 这样既不会刷爆，也保证"变化之后 16~32ms 内一定跑一次"。
/// </para>
///
/// <para>
/// <c>WINEVENT_SKIPOWNPROCESS</c> 必须加：悬浮窗自己提顶也会产生事件，
/// 不加就是"自己通知自己再提顶自己"的自激回路（两个实例互相顶的场景会打到 12Hz）。
/// </para>
/// </summary>
internal sealed class ZOrderWatch : IDisposable
{
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint EVENT_OBJECT_REORDER = 0x8004;
    private const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    /// <summary>OBJID_WINDOW：只关心"窗口对象"级别的事件，子元素的全是有噪声的。</summary>
    private const int OBJID_WINDOW = 0;

    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;
    private const uint WINEVENT_SKIPOWNPROCESS = 0x0002;

    /// <summary>合并窗口：第一个事件之后多久真正执行一次检查。</summary>
    private const int FlushMs = 16;

    private delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr hwnd,
                                       int idObject, int idChild, uint thread, uint time);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr module,
                                                 WinEventProc proc, uint processId, uint threadId,
                                                 uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(IntPtr hook);

    /// <summary>Z 序可能变了。**在 UI 线程上触发**（内部投递回构造时所在的同步上下文）。</summary>
    public event Action? Changed;

    /// <summary>
    /// 委托必须自己保活。<c>SetWinEventHook</c> 只存函数指针，GC 一回收就是
    /// "回调进野指针"——在 .NET 上表现为随机崩溃或干脆静默不再回调。
    /// </summary>
    private readonly WinEventProc _proc;

    /// <summary>构造时所在的同步上下文（WPF 的 UI 线程）。用来把合并后的执行投回 UI 线程。</summary>
    private readonly SynchronizationContext? _ctx;

    private readonly Timer _flush;

    private IntPtr _fgHook, _reorderHook, _moveHook;

    /// <summary>1 = 已经排了一次执行，期间的额外事件直接合并掉。</summary>
    private int _scheduled;

    private volatile bool _disposed;

    public ZOrderWatch()
    {
        _ctx = SynchronizationContext.Current;
        _proc = OnWinEvent;
        _flush = new Timer(Flush, null, Timeout.Infinite, Timeout.Infinite);

        const uint flags = WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS;

        // ① 前台换人（游戏抢回焦点、开始菜单弹出）：低频、极准。
        _fgHook = SetWinEventHook(EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
                                  IntPtr.Zero, _proc, 0, 0, flags);

        // ② Z 序重排：SetForegroundWindow / 切换活动窗口这类走它。
        _reorderHook = SetWinEventHook(EVENT_OBJECT_REORDER, EVENT_OBJECT_REORDER,
                                       IntPtr.Zero, _proc, 0, 0, flags);

        // ③ 位置变化：**别的窗口自己 SetWindowPos 提顶，只发这一个**（实测）。
        //    少了它就是本次要修的那个 bug 修不掉 —— 游戏抢顶时我们收不到任何消息。
        _moveHook = SetWinEventHook(EVENT_OBJECT_LOCATIONCHANGE, EVENT_OBJECT_LOCATIONCHANGE,
                                    IntPtr.Zero, _proc, 0, 0, flags);
    }

    /// <summary>挂了几个钩子（自测/诊断用，<c>3</c> 才算全挂上）。</summary>
    public int HookCount => (_fgHook != IntPtr.Zero ? 1 : 0)
                          + (_reorderHook != IntPtr.Zero ? 1 : 0)
                          + (_moveHook != IntPtr.Zero ? 1 : 0);

    private void OnWinEvent(IntPtr hook, uint eventType, IntPtr hwnd,
                            int idObject, int idChild, uint thread, uint time)
    {
        if (_disposed) return;
        if (idObject != OBJID_WINDOW) return;             // 子元素事件与窗口层级无关

        // 已经排上了就合并掉：不丢尾（不像限流），也不刷爆
        if (Interlocked.CompareExchange(ref _scheduled, 1, 0) != 0) return;

        try { _flush.Change(FlushMs, Timeout.Infinite); }
        catch (ObjectDisposedException) { /* 关窗竞态，忽略 */ }
    }

    private void Flush(object? _)
    {
        Volatile.Write(ref _scheduled, 0);
        if (_disposed) return;

        // 钩子回调本来就在 UI 线程（OUTOFCONTEXT + 同线程消息泵），
        // 但定时器回调在线程池线程上，必须投回去再碰窗口。
        if (_ctx is null)
        {
            Raise();
            return;
        }

        _ctx.Post(_ => Raise(), null);
    }

    private void Raise()
    {
        if (_disposed) return;
        try { Changed?.Invoke(); }
        catch
        {
            // 通知方抛异常绝不能打断事件分发
        }
    }

    public void Dispose()
    {
        _disposed = true;
        if (_fgHook != IntPtr.Zero) UnhookWinEvent(_fgHook);
        if (_reorderHook != IntPtr.Zero) UnhookWinEvent(_reorderHook);
        if (_moveHook != IntPtr.Zero) UnhookWinEvent(_moveHook);
        _fgHook = _reorderHook = _moveHook = IntPtr.Zero;
        _flush.Dispose();
    }
}
