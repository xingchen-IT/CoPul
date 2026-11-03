using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace CoPul.App.Interop;

/// <summary>
/// 毛玻璃（需求 6.4）与窗口外观（6.1 / 10.1）。
///
/// <para>
/// <b>实际生效的是 <c>SetWindowCompositionAttribute</c> + <c>ACCENT_ENABLE_ACRYLICBLURBEHIND</c></b>，
/// 深浅两档只差 <c>GradientColor</c> 的 RGB（深 #0F1216 / 浅 #F6F8FB）与 alpha。
/// 之所以不是"优先用 Win11 新 API"，见 <see cref="Apply"/> 里的顺序说明——
/// <c>DWMWA_SYSTEMBACKDROP_TYPE</c> 在 WPF 上会返回 S_OK 却什么都不画（A-35）。
/// </para>
///
/// <para>
/// <b>硬约束：禁止 <c>AllowsTransparency=true</c>。</b>它会把窗口切成 layered window，
/// 系统背景直接失效，而且 WPF 会退掉硬件加速——与 1.5% 预算直接冲突（A-30 同源）。
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
    private const int DWMWA_BORDER_COLOR = 34;

    /// <summary>DWM 的"跟着系统走"（默认边框色）。</summary>
    private const int DWMWA_COLOR_DEFAULT = unchecked((int)0xFFFFFFFF);
    /// <summary>DWM 的"不画边框"。</summary>
    private const int DWMWA_COLOR_NONE = unchecked((int)0xFFFFFFFE);

    private const int DWMSBT_NONE = 1;
    private const int DWMSBT_MAINWINDOW = 2;       // Mica
    private const int DWMSBT_TRANSIENTWINDOW = 3;  // Acrylic
    private const int DWMWCP_ROUND = 2;

    private const int WCA_ACCENT_POLICY = 19;

    // 仅作记录，**不要用**：ACCENT_ENABLE_BLURBEHIND(3) 在 Win11 上已被掏空，
    // 既不做模糊也不吃 GradientColor，画面表现为一块不透明的灰板。
    private const int ACCENT_ENABLE_BLURBEHIND = 3;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// 画 / 不画 DWM 给窗口描的那一圈 1 DIP 边框（M4.36）。
    ///
    /// <para>
    /// 为什么需要：任务栏条的「透明」档把底去掉之后，整条上唯一还看得见的就是这圈边框 ——
    /// 实测 150% 缩放下是 2 个设备像素、亮度 90 左右（条内只有 57），
    /// 在壁纸偏暗时像一个空心描边盒子，和"只有字浮在任务栏上"完全不是一回事。
    /// 其余两档必须显式还原 <see cref="DWMWA_COLOR_DEFAULT"/>，
    /// 否则从透明档切回去时那圈边不会自己回来（DWM 会一直记着 NONE）。
    /// </para>
    /// </summary>
    public static void SetWindowBorder(Window window, bool visible)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int color = visible ? DWMWA_COLOR_DEFAULT : DWMWA_COLOR_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_BORDER_COLOR, ref color, sizeof(int));
    }

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
    /// 施加毛玻璃 + 圆角 + 深浅模式。返回实际生效的方式，写进自测/日志便于判断
    /// "看起来不像毛玻璃"到底是环境不支持还是参数写错了。
    /// </summary>
    /// <param name="dark">深色档（tint 用 #0F1216）还是浅色档（tint 用 #F6F8FB）。</param>
    /// <param name="tintOpacity">
    /// accent tint 的 alpha，0–1。这是"模糊层之上的那层颜色有多浓"，
    /// 不直接等于最终遮盖率——窗口自身的 Shell 遮罩还会再叠一层。
    /// </param>
    public static string Apply(Window window, Backdrop kind, bool dark, double tintOpacity, bool blurOnly = false)
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
        if (TryApplyAccent(hwnd, kind, dark, tintOpacity, blurOnly))
            return blurOnly ? "Accent Blur（极透：不模糊、不着色）"
                 : kind == Backdrop.Mica ? "Accent Blur（Mica 不可用，降级为模糊）" : "Accent Acrylic";

        int type = kind == Backdrop.Mica ? DWMSBT_MAINWINDOW : DWMSBT_TRANSIENTWINDOW;
        int hr = DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        if (hr == 0) return kind == Backdrop.Mica ? "DWM Mica" : "DWM Acrylic";

        // 两条都失败：至少提示调用方把底色画回去，否则窗口会变成全透明看不到内容
        return $"失败：Accent 与 DWM 均不可用（DWM hr=0x{hr:X8}）";
    }

    /// <summary>
    /// <b>必须是 0，不能是 2。</b>这是踩了几轮才定位到的关键：社区里流传的 <c>AccentFlags = 2</c>
    /// 会让 DWM 走"不透明边框"那条路，**GradientColor 的 alpha 被整个忽略**，底色锁死在
    /// 系统值（浅色约 88% 白、深色约 85% 深灰），于是"调浓度"像调了个寂寞——
    /// 实测 alpha 从 0.00 拉到 0.70，窗内亮度只动 7/255。
    /// 改成 0（或 0x20）后 alpha 立刻生效：同样三档，窗内亮度 153 → 178 → 225，模糊不受影响。
    /// 保留这个字段只是为了让 <c>--accent-flags</c> 自测参数能复现那次对照实验。
    /// </summary>
    internal static int AccentFlagsOverride = 0;

    /// <summary>
    /// 自测钩子：覆盖 <c>AccentState</c>（0 = 不覆盖，用 <c>ACRYLICBLURBEHIND</c>）。
    /// 用来验证「极透档能不能压到 17% 以下」——亚克力自带一层约 17% 的 basal tint，
    /// 那是它和 <c>BLURBEHIND</c> 的差别所在。
    /// </summary>
    internal static int AccentStateOverride = 0;

    private static bool TryApplyAccent(IntPtr hwnd, Backdrop kind, bool dark, double tintOpacity, bool blurOnly)
    {
        // GradientColor 是 AABBGGRR。alpha 不是随便取的：accent 路径的 tint 会**再叠一次**
        // 窗口自己的半透明底（FloatWindow 的 Shell），两层相乘才是最终"遮住背景"的比例。
        //
        // 深浅两档的可调范围不一样，别用同一个公式：
        // 深色档 accent 自己占主导（45% + Shell 20% ≈ 56%），alpha 一改观感立刻跟着变；
        // 浅色档系统自带约 85% 的白底，alpha 从 0.05 调到 0.50 总遮盖只在 87%–90% 之间浮动，
        // 所以浅色的"浓度"主要靠 Shell 遮罩来体现，accent 只做微调。
        int alpha = (int)Math.Round(Math.Clamp(tintOpacity, 0.0, 1.0) * 255);

        // RGB 必须跟着深浅走。浅色毛玻璃的"白雾"就是这一层给的：
        // 只把 alpha 调薄、RGB 却仍给 #0F1216，画面上会变成一层灰罩而不是奶白玻璃。
        int r = dark ? 0x0F : 0xF6;
        int g = dark ? 0x12 : 0xF8;
        int b = dark ? 0x16 : 0xFB;
        int color = (alpha << 24) | (b << 16) | (g << 8) | r;

        // BLURBEHIND(3) 在 Win11 上**不模糊**（A-37 实测：棋盘格在它下面纹丝不动），
        // 但它的着色基底是 0，而 ACRYLICBLURBEHIND 自带约 17% 的固有 tint（accent alpha
        // 已经给到 0 也压不掉）。所以它只用在"极透"那一档：既要透到几乎看不见玻璃，
        // 就注定不能再要模糊——两者的下限互斥，这里是明确的取舍，不是降级。
        int accentState = AccentStateOverride != 0
            ? AccentStateOverride
            : blurOnly ? ACCENT_ENABLE_BLURBEHIND : ACCENT_ENABLE_ACRYLICBLURBEHIND;

        var policy = new AccentPolicy
        {
            // 深浅两档都用 Acrylic，只换 GradientColor 的 RGB。
            // **不要**给浅色单独用 ACCENT_ENABLE_BLURBEHIND(3)：它在 Win11 上已被掏空，
            // 既不做模糊也不吃 tint，实测窗口内是纯黑 + 窗口自身遮罩（#2C2D2F 正好等于
            // "16% 白叠纯黑"），看起来像一块不透明的灰板。
            AccentState = accentState,
            AccentFlags = AccentFlagsOverride,
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
    /// 只注入 accent（M4.41，任务栏条用）。
    ///
    /// <para>
    /// 与 <see cref="Apply"/> 的区别是**不碰窗口本身**：不刷 <c>Window.Background</c>、
    /// 不改 DWM 圆角偏好与边框色。条的形状由它自己的 <c>Shell</c> 圆角 + layered 逐像素 alpha 决定，
    /// 让 DWM 再插一层 8px 圆角会把四个角啃掉一圈。
    /// </para>
    ///
    /// <para>
    /// <b>layered 窗口也吃 accent</b>——这一条实测过，别再照着"layered 不能用系统背景"的老结论去搭宿窗：
    /// 条的 <c>AllowsTransparency=True</c>（exstyle 0x80088），注入 <c>ACRYLICBLURBEHIND</c> + 半透明红 tint 后，
    /// 条内均值 (128,109,108) → (195,140,97)、纹理量 7.3 → 4.2。若不成立，这组数字会一动不动。
    /// </para>
    /// </summary>
    /// <param name="dark">深色缇还是浅色缇（RGB 见 <see cref="TryApplyAccent"/>）。</param>
    /// <param name="opacity">tint 的 alpha，0–1。0 也仍有亚克力自带的约 17% 基底，压不掉。</param>
    public static bool ApplyAccent(Window window, bool dark, double opacity)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return false;
        return TryApplyAccent(hwnd, Backdrop.Acrylic, dark, opacity, blurOnly: false);
    }

    /// <summary>
    /// 撤掉 accent。<b>没有"自动恢复"</b>：不下发 <c>ACCENT_DISABLED(0)</c>，那层玻璃会一直挂在窗口上。
    /// </summary>
    public static void ClearAccent(Window window)
    {
        IntPtr hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd != IntPtr.Zero) DisableAccent(hwnd);
    }

    private static void DisableAccent(IntPtr hwnd)
    {
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

        DisableAccent(hwnd);
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
