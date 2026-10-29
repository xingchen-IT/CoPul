using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CoPul.App.Interop;

/// <summary>
/// 毛玻璃（需求 6.4）与窗口外观（6.1 / 10.1）。
///
/// 两条路，按能力降级：
/// <list type="number">
/// <item>Win11 22621+ 的 <c>DWMWA_SYSTEMBACKDROP_TYPE</c>：Acrylic / Mica 由系统合成器直接画，
/// 模糊在合成器里做，WPF 侧零成本，拖动也不掉帧，圆角交给 <c>DWMWA_WINDOW_CORNER_PREFERENCE</c>。</item>
/// <item>Win10 1803+ 或方案 1 失败：<c>SetWindowCompositionAttribute</c> +
/// <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c>。</item>
/// </list>
///
/// <para>
/// <b>硬约束：禁止 <c>AllowsTransparency=true</c>。</b>它会把窗口切成 layered window，
/// 系统背景直接失效（第 1 条路白走），而且 WPF 会退掉硬件加速——与 1.5% 预算直接冲突（A-30 同源）。
/// 想让背景透出来，正确做法是把 WPF 自己的底刷成透明（<see cref="Window.Background"/> +
/// <see cref="CompositionTarget.BackgroundColor"/>），把"画背景"这件事交给合成器。
/// </para>
///
/// <para>
/// <b>圆角半径的取舍</b>：DWM 只支持 Round(约 8px) / RoundSmall，<b>不支持自定义半径</b>。
/// 需求 10.1 写悬浮窗 10px，实测无法单独指定——要么吃 8px（硬件裁剪、无锯齿、零成本），
/// 要么退回 <c>SetWindowRgn</c> 拿 10px 但带锯齿。这里选 8px，差 2px 肉眼不可辨，
/// 换成自定义半径要付锯齿代价，不划算。
/// </para>
/// </summary>
internal static class WindowGlass
{
    public enum Backdrop
    {
        /// <summary>不透明（大窗口 M1 就是这种）。</summary>
        None,
        /// <summary>Acrylic：模糊 + 噪点，悬停更"活"，适合悬浮长条。</summary>
        Acrylic,
        /// <summary>Mica：只取壁纸主色做底色，不模糊后方窗口。省电但"透不出后方"。</summary>
        Mica,
    }

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;

    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2;       // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic
    private const int DWMWCP_ROUND = 2;

    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// 把整个客户区标记成"玻璃"。不加这一步时，DWM 只在标题栏区域画背景，
    /// 客户区还是被 WPF 自己的不透明表面盖住——表现为"API 全成功，但看起来是纯色"。
    /// </summary>
    [DllImport("dwmapi.dll")]
    private static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    [StructLayout(LayoutKind.Sequential)]
    private struct MARGINS
    {
        public int cxLeftWidth;
        public int cxRightWidth;
        public int cyTopHeight;
        public int cyBottomHeight;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor;   // AABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    /// <summary>
    /// 施加毛玻璃 + 圆角 + 深色模式。返回实际生效的方式，写进自测/日志便于判断
    /// "看起来不像毛玻璃"到底是环境不支持还是参数写错了。
    /// </summary>
    public static string Apply(Window window, Backdrop kind, bool dark, double tintOpacity = 0.35)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return "未取到 HWND（必须在 SourceInitialized 之后调用）";

        // 关键：让 WPF 别画自己的底，否则不透明的 #0F1216 会把合成器的背景整个盖住，
        // 表现为"设置了毛玻璃但看起来是纯色"。
        window.Background = Brushes.Transparent;
        MakeClientTransparent(window);

        // 注意：这里**不要**调 DwmExtendFrameIntoClientArea。
        // 它和 DWMWA_SYSTEMBACKDROP_TYPE 是互斥的两代方案：一旦把客户区"扩展成玻璃框架"，
        // DWM 就改用老式的纯色 tint 填充，Acrylic 的模糊被覆盖掉。
        // 实测两者并用时窗口内部像素级均匀（#3C3D3F 一个值铺满），看着像不透明灰块。

        int isDark = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDark, sizeof(int));

        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        if (kind == Backdrop.None) return "不透明";

        // 顺序说明：**先 accent，后 DWM systembackdrop**，与"优先用新 API"的直觉相反。
        // 原因是 WPF 的非 layered 窗口走的是不透明 DXGI swapchain（alpha 被 DWM 忽略），
        // SYSTEMBACKDROP_TYPE 即使返回 S_OK 也画不出来——实测窗口内部像素级均匀的纯色。
        // accent 是 DWM 在窗口后方注入的一层，不依赖 swapchain alpha，是 WPF 上真正能出模糊那条路。
        if (TryApplyAccent(hwnd, kind, dark, tintOpacity))
            return kind == Backdrop.Mica ? "Accent Blur（Mica 不可用，降级为模糊）" : "Accent Acrylic";

        int type = kind == Backdrop.Mica ? DWMSBT_MAINWINDOW : DWMSBT_TRANSIENTWINDOW;
        int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        if (hr == 0) return kind == Backdrop.Mica ? "DWM Mica" : "DWM Acrylic";

        // 两条都失败：至少提示调用方把底色画回去，否则窗口会变成全透明看不到内容
        return $"失败：Accent 与 DWM 均不可用（DWM hr=0x{hr:X8}）";
    }

    private static bool TryApplyAccent(IntPtr hwnd, Backdrop kind, bool dark, double tintOpacity)
    {
        // GradientColor 是 AABBGGRR。alpha 不是随便取的：accent 路径的 tint 会**再叠一次**
        // 窗口自己的半透明底（FloatWindow 的 Shell），两层相乘才是最终"遮住背景"的比例。
        // 实测 65% + 35% 遮罩时背景只剩 ~22%，落在暗部壁纸上只差 4/255，肉眼几乎看不出模糊；
        // 取 45% tint + 20% 遮罩后总着色约 56%，仍在 6.4 要求的 55–70% 内，但透出率翻倍。
        int alpha = (int)Math.Round(Math.Clamp(dark ? 0.45 : 0.55, 0.0, 1.0) * 255);
        int color = (alpha << 24) | (0x16 << 16) | (0x12 << 8) | 0x0F;

        var policy = new AccentPolicy
        {
            // 浅色模式下 Blur 更接近系统观感，深色模式用 Acrylic
            AccentState = kind == Backdrop.Mica || !dark
                ? ACCENT_ENABLE_BLURBEHIND
                : ACCENT_ENABLE_ACRYLICBLURBEHIND,
            AccentFlags = 2,
            GradientColor = color,
            AnimationId = 0,
        };

        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size,
            };
            return SetWindowCompositionAttribute(hwnd, ref data) != 0;
        }
        catch
        {
            return false;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 把 WPF 的合成目标背景刷成透明。**必须在窗口 Loaded 之后调用一次**：
    /// SourceInitialized 阶段 HwndTarget 还没完全建好，这时设的值会被后续初始化覆盖，
    /// 结果是窗口底变成纯黑（实测 #050608 = 35% 的 #0F1216 叠在纯黑上），
    /// 看起来就像"毛玻璃完全没生效"。
    /// </summary>
    public static void MakeClientTransparent(Window window)
    {
        if (PresentationSource.FromVisual(window) is HwndSource src && src.CompositionTarget is not null)
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
    }

    /// <summary>
    /// 关掉毛玻璃（6.6 菜单里取消勾选）。注意必须把 WPF 的底画回来一部分——
    /// <see cref="Apply"/> 把 Window.Background 刷成 Transparent 了，不还原会变成全透明窗。
    /// 这里让 Window 保持透明、由外层 Border 的不透明底色盖满整窗：外层 Border 填满客户区，
    /// 视觉等同不透明，四角仍是圆角（DWM 已把窗口裁成圆角）。
    /// </summary>
    public static void Clear(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int type = DWMSBT_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));

        // accent 路径没有"关"的开关，只能显式下发 ACCENT_DISABLED(0)
        var policy = new AccentPolicy { AccentState = 0 };
        int size = Marshal.SizeOf<AccentPolicy>();
        IntPtr buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(policy, buffer, false);
            var data = new WindowCompositionAttributeData
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = buffer,
                SizeOfData = size,
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    /// <summary>
    /// 设成工具窗口：不进任务栏、也不进 Alt+Tab（需求 6.1）。
    /// 光靠 <c>ShowInTaskbar=false</c> 只解决任务栏，Alt+Tab 里还会出现一条空白项。
    ///
    /// <para>
    /// <b>不要再加 <c>WS_EX_NOACTIVATE</c></b>：它让窗口永远拿不到焦点，
    /// 连带 <c>DragMove()</c>（内部走 WM_NCLBUTTONDOWN）和右键 <c>ContextMenu</c> 都会失灵——
    /// 表现为"窗口点了没反应"，而且不报错，排查起来极费时间。
    /// </para>
    /// </summary>
    public static void HideFromAltTab(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int ex = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_TOOLWINDOW);
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int value);
}
