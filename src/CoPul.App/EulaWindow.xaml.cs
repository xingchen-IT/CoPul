using System;
using System.Windows;
using System.Windows.Input;
using CoPul.App.Interop;
using CoPul.App.Sdk;

namespace CoPul.App;

/// <summary>
/// 首次启动的许可协议同意窗（2026-11-04）。见 <see cref="EulaWindow.xaml"/> 顶部的说明。
///
/// <para>
/// 两种用法：<see cref="RunModal"/> 走 <c>ShowDialog</c>（生产路径），
/// <see cref="Show"/> 走非模态（截图取证用 —— 模态窗口会阻塞 <c>OnStartup</c>，
/// 后面注册的截图定时器永远等不到）。
/// </para>
/// </summary>
public partial class EulaWindow : Window
{
    /// <summary>是否以模态方式打开。决定收尾时用 <see cref="Window.DialogResult"/> 还是 <see cref="Window.Close"/>。</summary>
    private bool _modal;

    /// <summary>已经给出结论（同意 / 退出）。防止「点同意 → Closing 又记一次不同意」。</summary>
    private bool _finished;

    private IntPtr _hwnd;

    public EulaWindow()
    {
        InitializeComponent();

        BodyText.Text = Eula.Text;
        VersionText.Text = $"版本 {Eula.Version} · {Eula.EffectiveDate} 生效";
        AcceptButton.IsEnabled = false;

        // 打开时回到顶部：正文 5000+ 字，停在中间会让人误以为"只有这么点条款"。
        Loaded += (_, _) => BodyScroll.ScrollToHome();
    }

    /// <summary>同意结果。仅在窗口关闭后有意义。</summary>
    public bool Accepted { get; private set; }

    /// <summary>
    /// 取证模式（非模态 + 由截图定时器收尾）。只影响日志措辞：探针关窗不是"用户拒绝"，
    /// 两者混在同一条日志里，回头看 trace 会误判有人在测拒绝路径。
    /// </summary>
    public bool ProbeMode { get; set; }

    /// <summary>模态打开，返回用户是否同意。</summary>
    public bool RunModal()
    {
        _modal = true;
        bool? result = ShowDialog();
        return result == true;
    }

    /// <summary>诊断串（探针与截图 sidecar 用）：条款到底有没有载入、多长。</summary>
    public string Diag =>
        $"eula: version={Eula.Version} chars={BodyText.Text.Length} " +
        $"consent={ConsentCheck.IsChecked == true} accepted={Accepted} modal={_modal}";

    /// <summary>「同意」按钮当前可不可点。验收要能证明"没勾选时点不了"。</summary>
    public bool AcceptEnabled => AcceptButton.IsEnabled;

    /// <summary>
    /// 取证用：把勾选框摆成"已勾选"，好拍出主按钮可用时的样子。
    /// 走的是与用户点击**同一条** <c>Consent_Changed</c>，不是另写一段使能逻辑。
    /// </summary>
    public void SetConsentForProbe(bool value)
    {
        ConsentCheck.IsChecked = value;
        Consent_Changed(ConsentCheck, new RoutedEventArgs());
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        _hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;

        // Win11 的圆角由 DWM 决定；WindowStyle=None 之后不显式要求就一定是直角。
        bool dwmRound = Win32Window.TryEnableDwmRoundCorners(_hwnd);
        Interop.Trace.Log("eula", $"round: dwm={dwmRound} hwnd=0x{_hwnd:X}");
    }

    /// <summary>
    /// 没勾选就按回车 = 什么都不发生（按钮是禁用的），不会误同意。
    /// Esc 等同「退出」：给一个不用找按钮的拒绝路径，和 Windows 上其它协议窗一致。
    /// </summary>
    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            Finish(false);
        }
    }

    /// <summary>右上角 ✕ 与「退出」按钮同一个语义：不同意。</summary>
    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        // 标题栏 ✕ 走的是关闭而不是按钮，这里兜住"直接关窗"的路径 ——
        // 不然 Accept 会保持 false 但日志里没有任何记录，事后分不清是拒绝还是崩溃。
        if (!_finished)
        {
            _finished = true;
            Accepted = false;
            Interop.Trace.Log("eula", ProbeMode
                ? "probe window closed (未做选择，仅取证)"
                : "window closed without choice → declined");
        }

        base.OnClosing(e);
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState != MouseButtonState.Pressed) return;
        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标在 DragMove 之前就松开了：WPF 会抛，什么也不做即可。
        }
    }

    private void Consent_Changed(object sender, RoutedEventArgs e)
    {
        AcceptButton.IsEnabled = ConsentCheck.IsChecked == true;
    }

    private void Accept_Click(object sender, RoutedEventArgs e) => Finish(true);

    private void Decline_Click(object sender, RoutedEventArgs e) => Finish(false);

    private void Finish(bool accepted)
    {
        if (_finished) return;
        _finished = true;
        Accepted = accepted;

        Interop.Trace.Log("eula", $"window {(accepted ? "accepted" : "declined")} modal={_modal}");

        if (_modal)
        {
            DialogResult = accepted;
            return;
        }

        Close();
    }
}
