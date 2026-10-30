using System.Runtime.InteropServices;
using System.Windows.Interop;
using CoPul.App.Sdk;

namespace CoPul.App.Interop;

/// <summary>
/// 全局热键（G-03）。<c>RegisterHotKey</c> + 一个隐藏窗口收 <c>WM_HOTKEY</c>。
///
/// <para>
/// <b>为什么必须有一个窗口。</b>热键消息是**投递**给某个窗口的，不是全局钩子：
/// <c>RegisterHotKey(hwnd, ...)</c> 里的 <c>hwnd</c> 就是收信人。传 <c>NULL</c> 也能注册，
/// 但消息会跑到线程消息队列里、没有窗口过程能接住它 —— 除非自己跑 <c>GetMessage</c> 循环，
/// 而那会和 WPF 的消息泵打架。所以这里建一个 0×0 的隐藏顶层窗口（与托盘图标同一套路）。
/// </para>
///
/// <para>
/// <b>注册失败不能让程序启动失败。</b>默认的 <c>Ctrl+Alt+C</c> 很容易被别的软件先占
/// （截图工具、录屏、显卡面板都爱用它）。占用了就如实报出来，其余功能照常 ——
/// 为了一个快捷键让整个程序起不来是本末倒置。
/// </para>
/// </summary>
public sealed class Hotkey : IDisposable
{
    private const int WM_HOTKEY = 0x0312;

    /// <summary>热键 id。同一个窗口可以注册多个，靠它区分；这里只有一个，取个不容易撞的值。</summary>
    private const int HotkeyId = 0x0C09;

    /// <summary><c>ERROR_HOTKEY_ALREADY_REGISTERED</c>。区分"被占用"与"参数不对"全靠它。</summary>
    private const int ErrorAlreadyRegistered = 1409;

    private static readonly IntPtr HwndMessage = new(-3);   // HWND_MESSAGE

    private HwndSource? _source;

    /// <summary>当前**真正注册成功**的写法；<c>null</c> = 没注册上。</summary>
    private string? _applied;

    /// <summary>用户想要的热键（可能因为冲突而暂时没注册上，等会儿还能重试）。</summary>
    private string? _desired;

    private bool _registered;
    private bool _suspended;

    /// <summary>
    /// 热键被按下。已回到 UI 线程（隐藏窗口就建在 UI 线程上）。
    ///
    /// <para>用可赋值的委托属性而不是 event：订阅方只有一个（App），
    /// 与 <see cref="TrayIcon.LeftClick"/> 保持一致，避免"以为可能有多个订阅者"的错觉。</para>
    /// </summary>
    public Action? Pressed { get; set; }

    /// <summary>最近一次失败的原因（给设置页显示）。成功时为 <c>null</c>。</summary>
    public string? LastError { get; private set; }

    /// <summary>当前生效的写法。冲突时是 <c>null</c>，不是"配置里那个"。</summary>
    public string? Applied => _applied;

    /// <summary>注册成功没有。</summary>
    public bool IsRegistered => _registered;

    public Hotkey()
    {
        var p = new HwndSourceParameters("CoPulHotkey")
        {
            Width = 0,
            Height = 0,
            WindowStyle = 0,                    // 不带 WS_VISIBLE
            ExtendedWindowStyle = 0x00000080,   // WS_EX_TOOLWINDOW → 不进 Alt+Tab
            ParentWindow = HwndMessage,         // 消息窗口：连任务栏/Alt+Tab 都不可能出现
        };

        _source = new HwndSource(p);
        _source.AddHook(Hook);
    }

    /// <summary>
    /// 换成这个热键。失败时**自动把上一个热键装回去**，不会留下"改坏了就再也没有热键"的状态。
    /// </summary>
    /// <returns>true = 已生效；false = 没生效，原因见 <see cref="LastError"/>。</returns>
    public bool Apply(string? gesture)
    {
        string desired = (gesture ?? string.Empty).Trim();
        string? previous = _desired;

        // 先松开旧的。同一个键换写法（Ctrl+Alt+C → Ctrl+Alt+F9）时，
        // 不先注销的话新键能注册上、但旧键还挂着，等于多了一个看不见的热键
        Stop();
        _suspended = false;

        string? error = null;
        HotkeyCombo combo = default;

        if (!HotkeyGesture.TryParse(desired, out combo, out string parseError))
        {
            error = parseError;
        }
        else
        {
            _desired = combo.Text;
            if (!Register(combo)) error = LastError;
        }

        if (error is null)
        {
            LastError = null;
            Trace.Log("hotkey", $"apply \"{combo.Text}\" ok");
            return true;
        }

        // 失败：把上一次的热键装回去。用户连试三次都失败的话，
        // 不然这里就是一个"越改越没"的陷阱 —— 最后一个能用的组合键也没了
        _desired = previous;
        if (!_registered && previous is not null
            && HotkeyGesture.TryParse(previous, out HotkeyCombo old, out _))
        {
            Register(old);
        }

        LastError = error;      // Register 可能把它改成别的，恢复成"换新键失败"的真实原因
        Trace.Log("hotkey", $"apply \"{desired}\" 失败：{error}；已回滚到 {_applied ?? "(无)"}");
        return false;
    }

    /// <summary>
    /// 暂停/恢复（设置页录键时用）。
    ///
    /// <para>
    /// 录键期间**必须**先注销：热键是全局的，用户在自己窗口里按 <c>Ctrl+Alt+C</c>
    /// 想录下这个组合，系统会先把 <c>WM_HOTKEY</c> 发给我们 —— 于是切了视图，
    /// 输入框里什么都没收到。这是所有热键录制框都会踩的坑。
    /// </para>
    /// </summary>
    public void Suspend()
    {
        if (_suspended) return;

        Stop();
        _suspended = true;
        Trace.Log("hotkey", "suspend（录键中）");
    }

    /// <summary>恢复。已经注册着就什么都不做，不会重复注册。</summary>
    public void Resume()
    {
        if (!_suspended) return;

        _suspended = false;
        if (_registered || _desired is null) return;

        if (HotkeyGesture.TryParse(_desired, out HotkeyCombo combo, out _))
        {
            Register(combo);
            Trace.Log("hotkey", $"resume \"{_desired}\" ok={_registered}");
        }
    }

    /// <summary>
    /// 伪造一次按下（探针用）。走的是<b>同一条</b>消息路径：
    /// 往自己的隐藏窗口投一条 <c>WM_HOTKEY</c>，Hook 照常派发。
    ///
    /// <para>
    /// 为什么要它：真发一次按键得用 <c>SendInput</c> 抢占真实键盘，而热键一旦没注册上，
    /// 那串 <c>Ctrl+Alt+C</c> 就会漏给前台窗口。验"注册成功"与"按下之后做什么"
    /// 是两件事，前者用真实注册验，后者用这条消息验。
    /// </para>
    /// </summary>
    public void SimulatePress()
    {
        if (_source is null) return;
        PostMessage(_source.Handle, WM_HOTKEY, new IntPtr(HotkeyId), IntPtr.Zero);
    }

    public void Dispose()
    {
        Stop();

        if (_source is not null)
        {
            _source.RemoveHook(Hook);
            _source.Dispose();
            _source = null;
        }
    }

    private bool Register(HotkeyCombo combo)
    {
        if (_source is null) return false;

        // MOD_NOREPEAT：按住不放不要连发，否则三个视图会被连按般地来回切
        uint mods = combo.Modifiers | HotkeyGesture.ModNoRepeat;

        if (RegisterHotKey(_source.Handle, HotkeyId, mods, combo.VirtualKey))
        {
            _registered = true;
            _applied = combo.Text;
            return true;
        }

        int err = Marshal.GetLastWin32Error();
        _registered = false;
        _applied = null;
        LastError = err == ErrorAlreadyRegistered
            ? $"{combo.Text} 已被其它程序占用"
            : $"注册热键失败（Win32 错误 {err}）";
        return false;
    }

    private void Stop()
    {
        if (!_registered || _source is null) return;

        UnregisterHotKey(_source.Handle, HotkeyId);
        _registered = false;
        _applied = null;
    }

    private IntPtr Hook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY) return IntPtr.Zero;
        if ((int)(long)wParam != HotkeyId) return IntPtr.Zero;

        Trace.Log("hotkey", "WM_HOTKEY");
        handled = true;
        Pressed?.Invoke();
        return IntPtr.Zero;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hwnd, int id);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
