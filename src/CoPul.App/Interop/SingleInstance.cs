using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace CoPul.App.Interop;

/// <summary>
/// 单实例（G-05）：重复启动时把已有实例叫到前面，新实例自己退出。
///
/// <para>
/// 实现分两半，缺一不可：
/// <list type="number">
/// <item><b>命名互斥体</b>判重 —— 可靠，跨进程唯一。</item>
/// <item><b>注册窗口消息</b>做激活 —— 互斥体只能告诉你"已经有一个了"，
/// 没法让那个实例动起来。用 <c>RegisterWindowMessage</c> + <c>HWND_BROADCAST</c>
/// 通知它"有人想见你"，它收到后显示大窗口。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>已知边界</b>：两个实例完整性级别不同（一个提权、一个不提权）时，
/// 广播会被 UIPI 丢掉（与 TaskbarCreated 同一根因，见 TaskbarWindow.InstallTaskbarWatcher）。
/// 那时第二个实例仍然会安静退出（互斥体照常生效），只是没能把第一个叫到前面。
/// 这个取舍可以接受：真正会撞上的场景是同一个人连点两次图标，两次的权限一定相同。
/// </para>
/// </summary>
public static class SingleInstance
{
    /// <summary>
    /// <c>Local\</c> 而不是 <c>Global\</c>：多名用户同时登录时各跑一份是合理的，
    /// 用 Global 会让"另一个用户已登录"变成"我打不开程序"。
    /// </summary>
    private const string MutexName = @"Local\CoPul.SingleInstance";

    private const string ActivateMessageName = "CoPul.Activate";

    private static Mutex? _mutex;
    private static HwndSource? _listener;
    private static int _activateMessage;

    /// <summary>收到"有人又启动了一次"的广播。由 App 决定要做什么（显示大窗口）。</summary>
    public static event Action? ActivateRequested;

    /// <summary>
    /// 尝试成为唯一实例。返回 <c>false</c> 表示已经有一个在跑，调用方应立刻退出。
    /// </summary>
    public static bool TryAcquire()
    {
        try
        {
            // 名字带 Local\ 前缀时 CreateMutex 的 initializedOwner 语义照旧：
            // createdNew 为 false 说明别人先建了（哪怕它已经释放所有权）
            _mutex = new Mutex(initiallyOwned: true, MutexName, out bool createdNew);

            if (!createdNew)
            {
                _mutex.Dispose();
                _mutex = null;
                return false;
            }

            return true;
        }
        catch
        {
            // 建不出互斥体（极端受限环境）时宁可不做限制，也不能让程序起不来
            return true;
        }
    }

    /// <summary>
    /// 开始监听激活广播。必须有一个**顶层**隐藏窗口：
    /// 广播（<c>HWND_BROADCAST</c>）只投递给顶层窗口，message-only 窗口收不到 ——
    /// 这个坑在 TaskbarCreated 上已经踩过一次了。
    /// </summary>
    public static void Listen()
    {
        if (_listener is not null) return;

        _activateMessage = RegisterWindowMessage(ActivateMessageName);
        if (_activateMessage == 0) return;

        var p = new HwndSourceParameters("CoPulActivateListener")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,                    // 不带 WS_VISIBLE → 不显示
            ExtendedWindowStyle = 0x00000080,   // WS_EX_TOOLWINDOW → 不进 Alt+Tab
        };

        try
        {
            _listener = new HwndSource(p);
            _listener.AddHook(Hook);
        }
        catch
        {
            _listener = null;
        }
    }

    /// <summary>请求已有实例现身。发完就可以退出了。</summary>
    public static void SignalExisting()
    {
        int msg = RegisterWindowMessage(ActivateMessageName);
        if (msg == 0) return;

        PostMessage(HWND_BROADCAST, msg, IntPtr.Zero, IntPtr.Zero);
    }

    public static void Release()
    {
        if (_listener is not null)
        {
            _listener.RemoveHook(Hook);
            _listener.Dispose();
            _listener = null;
        }

        if (_mutex is not null)
        {
            try { _mutex.ReleaseMutex(); } catch { /* 没持有就当已经没了 */ }
            _mutex.Dispose();
            _mutex = null;
        }
    }

    private static IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != _activateMessage || _activateMessage == 0) return IntPtr.Zero;

        Trace.Log("single", "收到激活广播");
        ActivateRequested?.Invoke();
        handled = true;
        return IntPtr.Zero;
    }

    private static readonly IntPtr HWND_BROADCAST = new(0xFFFF);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int RegisterWindowMessage(string message);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam);
}
