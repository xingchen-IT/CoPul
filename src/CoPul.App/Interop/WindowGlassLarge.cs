using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Media;

namespace CoPul.App.Interop;

/// <summary>
/// 大窗口的**毛玻璃**（2026-11-01 新增。悬浮窗那套见 <see cref="WindowGlass"/>）。
///
/// <para>
/// <b>为什么不能直接复用 <see cref="WindowGlass"/>。</b>那个类是为 34 DIP 高的悬浮长条写的，
/// 它的假设在大窗口上全部不成立：
/// <list type="number">
/// <item>悬浮窗把 <c>Window.Background</c> 刷透明就够了（它只有一个半透明 Shell 盖住整窗）；
/// 大窗口的根 <c>Border</c> 有自己的不透明底 <c>BrushBg</c>，会**把玻璃整个盖住** ——
/// 必须让根 Border 的底也变成半透明/透明。</item>
/// <item>悬浮窗没有内容窗口；大窗口里 9 张卡片各有一层不透明 <c>BrushPanel</c> 底，
/// 面积占了大半。玻璃只在卡片缝与边缘可见 —— 所以「卡片是否也半透」是一个**独立的产品决策**，
/// 不能写死在实现里（用户已确认做成开关）。</item>
/// <item>悬浮窗是 <c>SizeToContent</c> 的小窗；大窗口可缩放、可最大化，
/// 最大化时 DWM 圆角自动变直角，玻璃的边界要对得上。</item>
/// </list>
/// </para>
///
/// <para>
/// <b>浓度模型（0–100 连续）。</b>沿用悬浮窗实测过的那条曲线，而不是自己编一套：
/// 亚克力层自带约 <b>17% 固有 tint</b>（A-40：accent alpha 给 0 也压不掉），
/// 所以 0 不等于"全透"，而是"17% 遮盖 + 真模糊"。滑块 0–100 线性映射到
/// 窗口遮罩 alpha 0→0.97，实际遮盖率按 <c>1-(1-0.17)(1-a)</c> 算，
/// 两端分别约 17% 与 98%，与悬浮窗七档实测曲线（17/36/60/77/91/98）对得上。
/// </para>
///
/// <para>
/// <b>A-39/A-41 的两条铁律在这里同样适用</b>：
/// <c>AccentFlags</c> 必须是 0（否则 GradientColor 的 alpha 被整个忽略，调浓度纹丝不动）；
/// 改过 style 之后必须发 <c>SWP_FRAMECHANGED</c>，否则 DWM 按旧框架缓存渲染、
/// 模糊降级成"tint 在、模糊没了"，而返回值完全正常。
/// </para>
/// </summary>
internal static class WindowGlassLarge
{
    private const int WCA_ACCENT_POLICY = 19;

    private const int ACCENT_ENABLE_BLURBEHIND = 3;         // Win11 空壳，仅作对照
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;  // 真正能出模糊那条路

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    private const int DWMSBT_NONE = 1;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

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

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    /// <summary>
    /// 亚克力层的固有 tint（A-40 实测 17%，压不掉）。浓度 0 时的下限就由它决定。
    /// </summary>
    public const double AcrylicBasalTint = 0.17;

    /// <summary>
    /// 把浓度 0–100 换算成"实际遮住背景的比例"（0.17–0.98）。
    ///
    /// <para>
    /// 为什么不是简单 0→100 对应 0→100%：亚克力自带 17% 是不可压的下限，
    /// 直接线性会让"滑块拉到 0 却仍有 17% 遮罩"看起来像 bug。这里显式把这个下限算进去，
    /// 返回值就是**用户实际看到的遮盖率**，设置页可以直接显示这个百分比。
    /// </para>
    /// </summary>
    public static double CoverageFor(int level)
    {
        double a = Math.Clamp(level, 0, 100) / 100.0;          // 遮罩层的 alpha
        double shell = a * 0.97;                                // 0.97 是实测"极实"档的值
        return 1 - (1 - AcrylicBasalTint) * (1 - shell);
    }

    /// <summary>
    /// 施加毛玻璃。<paramref name="level"/> 0–100。
    /// </summary>
    /// <param name="dark">深色主题（tint 用 #0F1216）还是白色主题（tint 用 #F6F8FB）。</param>
    /// <returns>实际生效的方式描述，写进 trace 便于判断"不像毛玻璃"是环境还是参数问题。</returns>
    public static string Apply(Window window, int level, bool dark)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return "未取到 HWND（必须在 SourceInitialized 之后调用）";

        int isDark = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref isDark, sizeof(int));

        int round = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref round, sizeof(int));

        // accent 层的 tint：浅色给白雾、深色给暗雾。alpha 固定给 0，
        // 浓度完全交给窗口遮罩层（两个旋钮会互相补偿，排查"这档为什么没变化"要拆两层，
        // 这是悬浮窗踩过的教训 —— A-36）。
        // 深色 tint 在这条路上几乎不贡献（alpha 0.35 与背景差 <1/255），给 0 也一样。
        int alpha = 0;
        int r = dark ? 0x0F : 0xF6;
        int g = dark ? 0x12 : 0xF8;
        int b = dark ? 0x16 : 0xFB;
        int color = (alpha << 24) | (b << 16) | (g << 8) | r;

        var policy = new AccentPolicy
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            // **必须是 0**：AccentFlags=2 会让 DWM 走不透明边框路径，
            // GradientColor 的 alpha 被整个忽略，表现为"调浓度纹丝不动"（A-39）。
            AccentFlags = 0,
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
            if (SetWindowCompositionAttribute(hwnd, ref data) != 0) return "Accent Acrylic";
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        // accent 失败才退 DWM（在 WPF 非 layered 窗口上通常返回 S_OK 但画不出来，A-35）
        int type = DWMSBT_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));
        return "失败：Accent 不可用（已显式关掉 systembackdrop，避免残留半套效果）";
    }

    /// <summary>
    /// 撤销毛玻璃，回到不透明纸面。
    ///
    /// <para>
    /// <b>必须显式下发 ACCENT_DISABLED(0)</b>：accent 没有"自动恢复"，
    /// 不下发的话窗口会一直挂着上一次的玻璃层，而调用方已经把底刷回了不透明色 ——
    /// 表现为"关掉毛玻璃后，窗口边缘还留着一圈模糊"。
    /// </para>
    /// </summary>
    public static void Clear(Window window)
    {
        IntPtr hwnd = new System.Windows.Interop.WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;

        int type = DWMSBT_NONE;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref type, sizeof(int));

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
    /// 让 WPF 自己别画底，把"画背景"交给合成器。
    /// <b>必须在 Loaded 之后调用</b>：SourceInitialized 阶段 HwndTarget 还没建好，
    /// 这时设的值会被后续初始化覆盖，窗口底变成纯黑（悬浮窗踩过，A-35 的 #050608 就是这个）。
    /// </summary>
    public static void MakeCompositionTransparent(Window window)
    {
        if (PresentationSource.FromVisual(window) is System.Windows.Interop.HwndSource src
            && src.CompositionTarget is not null)
        {
            src.CompositionTarget.BackgroundColor = Colors.Transparent;
        }
    }
}
