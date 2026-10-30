using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace CoPul.App.Controls;

/// <summary>
/// 任务栏条的悬停提示（需求 7.6：显示各指标完整名称，尤其硬盘温度对应哪块盘）。
///
/// <para>
/// <b>为什么不能挂 WPF 的 <c>ToolTip</c></b>：条默认点击穿透（Q-12 方案 2），
/// <c>WM_MOUSEMOVE</c> 根本不会投给它，ToolTip 的自动弹出永远不会触发。
/// 显示时机由调用方轮询光标决定（见 <see cref="CoPul.App.TaskbarWindow"/> 的 PollHover），
/// 这里只负责"长什么样、放哪儿"。
/// </para>
///
/// <para>
/// 允许 <c>AllowsTransparency</c>（会变 layered 窗口、WPF 走软件合成）：
/// 本窗口是几十毫秒内出现/消失的小方块，不承载毛玻璃也不追求渲染性能，
/// 而圆角 + 投影只有 layered 才画得出来。与 A-30 那条"常驻窗口不许 layered"不冲突。
/// </para>
/// </summary>
internal sealed class FieldTooltip : Window
{
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    private readonly TextBlock _text;
    private readonly Border _card;

    public FieldTooltip()
    {
        // 标题不可见（WindowStyle=None），只为了让自测脚本能用 FindWindow 找到它 ——
        // 无标题窗口只能靠类名 HwndWrapper[...] 猜，那种一一对应关系很脆。
        Title = "CoPulTip";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);

        _text = new TextBlock
        {
            FontSize = 12,
            LineHeight = 19,
            TextWrapping = TextWrapping.NoWrap,
        };

        if (Application.Current?.TryFindResource("FontUi") is FontFamily font) _text.FontFamily = font;

        _card = new Border
        {
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(11, 8, 11, 8),
            Child = _text,
            Effect = new DropShadowEffect
            {
                BlurRadius = 14,
                ShadowDepth = 2,
                Opacity = 0.30,
                Color = Colors.Black,
            },
        };

        Content = _card;
        SetTone(true);

        SourceInitialized += (_, _) =>
        {
            IntPtr hwnd = new WindowInteropHelper(this).Handle;
            if (hwnd == IntPtr.Zero) return;

            // TRANSPARENT：提示框只是"贴上去的一张纸"，鼠标要能穿过去点任务栏
            //   （它已经是 layered 窗口，加这个才真正生效）。
            // NOACTIVATE：不许抢焦点。
            // TOOLWINDOW：不进 Alt+Tab。
            // 注意 A-33：NOACTIVATE 会让 DragMove 与右键菜单失灵 —— 这里两者都没有。
            int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        };
    }

    /// <summary>
    /// 深浅跟随任务栏实际亮度（与 7.5 同一套判断，不另读注册表）。
    /// </summary>
    public void SetTone(bool onLight)
    {
        _card.Background = onLight
            ? new SolidColorBrush(Color.FromArgb(0xF5, 0xFA, 0xFB, 0xFC))
            : new SolidColorBrush(Color.FromArgb(0xEE, 0x20, 0x21, 0x24));

        _text.Foreground = onLight
            ? new SolidColorBrush(Color.FromRgb(0x1F, 0x23, 0x28))
            : new SolidColorBrush(Color.FromRgb(0xE6, 0xEA, 0xEF));
    }

    public void SetText(string text)
    {
        if (_text.Text != text) _text.Text = text;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
