using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace ThemeProbe;

/// <summary>
/// 方案 A（双调色板 + DynamicResource 全量替换）的技术验证。
/// 把结论写进 %TEMP%\theme-probe.txt，跑完即退 —— 没有窗口交互，纯自证。
/// </public>
public static class Program
{
    [STAThread]
    public static void Main()
    {
        var log = new List<string>();
        try
        {
            Run(log);
        }
        catch (Exception ex)
        {
            log.Add("FATAL: " + ex);
        }

        string path = Path.Combine(Path.GetTempPath(), "theme-probe.txt");
        File.WriteAllText(path, string.Join(Environment.NewLine, log));
        Console.WriteLine(string.Join(Environment.NewLine, log));
    }

    private static void Run(List<string> log)
    {
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };

        // ---- 两本调色板，键名完全相同、只有颜色不同 ----
        var dark = MakePalette("Dark", "#0B0E13", "#E8ECF2", "#5B8FF9");
        var light = MakePalette("Light", "#F5F7FA", "#1A1D22", "#2F62C4");

        // 先装深色
        app.Resources.MergedDictionaries.Add(dark);

        // ---- 建一个用 DynamicResource 的窗口 + N 个元素，模拟真实绑定 ----
        int elementCount = 390;
        var panel = new StackPanel();
        for (int i = 0; i < elementCount; i++)
        {
            var tb = new TextBlock { Text = "x" };
            // 代码侧用 SetResourceReference（等价于 XAML 的 DynamicResource）
            tb.SetResourceReference(TextBlock.ForegroundProperty, "ProbeText");
            tb.SetResourceReference(TextBlock.BackgroundProperty, "ProbeBg");
            panel.Children.Add(tb);
        }

        var win = new Window
        {
            Content = panel,
            Width = 400,
            Height = 300,
            ShowInTaskbar = false,
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = -3000,   // 屏幕外：不干扰用户桌面，但窗口仍需真实创建才有资源查找上下文
            Top = 0,
        };
        win.Show();

        // ---- 测 1：初始颜色 ----
        var first = (TextBlock)panel.Children[0];
        log.Add("== 1. 初始（深色）==");
        log.Add($"   背景 = {Hex((SolidColorBrush)first.Background)}");
        log.Add($"   文字 = {Hex((SolidColorBrush)first.FocusedForegroundOr(first))}");

        // ---- 测 2：整本替换字典，看是否即时生效 ----
        log.Add("");
        log.Add("== 2. 替换调色板（深 → 浅），DynamicResource 是否即时生效 ==");

        var sw = Stopwatch.StartNew();
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(light);

        // 强制布局一遍，让资源重新解析（真实场景里用户能看到结果，这里只是确保测量公平）
        win.UpdateLayout();
        sw.Stop();

        var afterBg = Hex((SolidColorBrush)first.Background);
        log.Add($"   替换耗时 = {sw.Elapsed.TotalMilliseconds:F2} ms（{elementCount} 个元素 × 2 个 DynamicResource）");
        log.Add($"   第一个元素背景 = {afterBg}  {(afterBg == "#FFF5F7FA" ? "✓ 已变浅" : "✗ 没变")}");
        log.Add($"   判定 = {(afterBg == "#FFF5F7FA" ? "通过：替换字典即生效" : "失败：DynamicResource 未跟随")}");

        // ---- 测 3：Freeze 过的画刷能不能放进字典 ----
        log.Add("");
        log.Add("== 3. Freeze 后放进字典是否可用 ==");
        var frozen = new SolidColorBrush(Color.FromRgb(0x11, 0x22, 0x33));
        frozen.Freeze();
        var frozenPalette = new ResourceDictionary { ["ProbeFrozen"] = frozen };
        app.Resources.MergedDictionaries.Add(frozenPalette);

        var probe = new Border();
        probe.SetResourceReference(Border.BackgroundProperty, "ProbeFrozen");
        ((StackPanel)win.Content).Children.Add(probe);
        win.UpdateLayout();

        Brush? resolved = probe.Background;
        log.Add($"   解析结果 = {Hex((SolidColorBrush?)resolved)}");
        log.Add($"   判定 = {(resolved is SolidColorBrush b && Hex(b) == "#FF112233" ? "通过：Freeze 的画刷可用" : "失败")}");

        // ---- 测 4：反向切回深色 ----
        log.Add("");
        log.Add("== 4. 再切回深色（验证可来回切）==");
        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(dark);
        win.UpdateLayout();
        var back = Hex((SolidColorBrush)first.Background);
        log.Add($"   第一个元素背景 = {back}  {(back == "#FF0B0E13" ? "✓ 回到深色" : "✗ 没回")}");
        log.Add($"   判定 = {(back == "#FF0B0E13" ? "通过：可来回切换" : "失败")}");

        // ---- 测 5：StaticResource 对照（确认它**不会**变，为"必须全量替换"提供反证）----
        log.Add("");
        log.Add("== 5. 对照：StaticResource 会不会跟着变（预期：不会）==");
        var staticTb = new TextBlock { Text = "static" };
        staticTb.SetResourceReference(TextBlock.ForegroundProperty, "ProbeText");
        // SetResourceReference 是 DynamicResource 的代码等价，先撤掉，改用真正的静态查找
        staticTb.ClearValue(TextBlock.ForegroundProperty);
        staticTb.Foreground = (Brush)app.Resources["ProbeText"];   // 静态取值：拿到的是当时的实例
        ((StackPanel)win.Content).Children.Add(staticTb);

        app.Resources.MergedDictionaries.Clear();
        app.Resources.MergedDictionaries.Add(light);
        win.UpdateLayout();
        log.Add($"   静态取值后切到浅色，该元素文字 = {Hex((SolidColorBrush)staticTb.Foreground)}");
        log.Add("   说明：静态取到的画刷实例不会跟随字典替换 —— 所以产品代码必须全量改 DynamicResource");

        win.Close();
        app.Shutdown();
    }

    private static ResourceDictionary MakePalette(string name, string bg, string text, string accent)
    {
        var d = new ResourceDictionary();
        d["ProbeBg"] = Freeze(bg);
        d["ProbeText"] = Freeze(text);
        d["ProbeAccent"] = Freeze(accent);
        d["__name"] = name;
        return d;
    }

    private static SolidColorBrush Freeze(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }

    private static string Hex(SolidColorBrush? b)
    {
        if (b is null) return "(null)";
        return $"#{b.Color.A:X2}{b.Color.R:X2}{b.Color.G:X2}{b.Color.B:X2}";
    }
}

internal static class TextBlockExt
{
    /// <summary>取 TextBlock 的实际前景（未显式设置时 WPF 默认黑，这里只做探针用）。</summary>
    public static Brush FocusedForegroundOr(this TextBlock tb, TextBlock _) =>
        tb.Foreground ?? Brushes.Black;
}
