using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace CoPul.App.Interop;

/// <summary>
/// 抓窗口所在区域的真实屏幕像素（含 DWM 合成出来的毛玻璃背景）。
///
/// <para>
/// 为什么不能用现成的 <c>--shot</c>：那条路走 <see cref="RenderTargetBitmap"/>，
/// 渲染的是 WPF 自己的视觉树——毛玻璃是 DWM 在合成阶段画的，根本不在视觉树里，
/// 截出来永远是一块纯色，验收"背景确实被模糊了"（6.8 第 1 条）会一直是假通过。
/// </para>
///
/// <para>
/// 为什么不用 System.Drawing：本项目是 WPF，<c>System.Drawing.Common</c> 在 .NET 8 上
/// 不随 WindowsDesktop 框架自动引用，为一个截图功能加一条 NuGet 依赖不划算。
/// GDI 抓位图（约 60 行 P/Invoke）+ WPF 的 PngBitmapEncoder 已经够用。
/// </para>
///
/// <para>
/// 坐标是**物理像素**：调用方把 Window.Left/Top（DIP）乘 DPI 缩放再传进来（A-27）。
/// </para>
/// </summary>
internal static class ScreenCapture
{
    private const int SRCCOPY = 0x00CC0020;
    private const uint BI_RGB = 0;
    private const uint DIB_RGB_COLORS = 0;

    public static void SaveWindow(Window window, string path, int pad = 0)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource src)
            throw new InvalidOperationException("窗口还没有 HWND，无法截屏");

        DpiScale dpi = VisualTreeHelper.GetDpi(window);

        // 客户区在屏幕上的位置：窗口 Left/Top 是 DIP，抓屏坐标必须是物理像素
        int padPx = (int)Math.Round(pad * dpi.DpiScaleX);
        int x = (int)Math.Round(window.Left * dpi.DpiScaleX) - padPx;
        int y = (int)Math.Round(window.Top * dpi.DpiScaleY) - padPx;
        int w = (int)Math.Round(window.ActualWidth * dpi.DpiScaleX) + padPx * 2;
        int h = (int)Math.Round(window.ActualHeight * dpi.DpiScaleY) + padPx * 2;

        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"窗口尺寸为 {window.ActualWidth}×{window.ActualHeight}，无法截屏");

        SaveRect(path, x, y, w, h);
    }

    /// <summary>
    /// 直接向窗口要它的绘制结果（<c>WM_PRINT</c> / <c>PrintWindow</c>），不经过屏幕。
    ///
    /// <para>
    /// 为什么需要：验证"子窗口有没有被任务栏画出来"时，屏幕截图完全不可靠 ——
    /// 实测环境里有全屏游戏压在最上层，任务栏区域拍到的其实是游戏画面。
    /// 让任务栏窗口自己把内容吐到一个 DC 上，就绕过了所有遮挡。
    /// </para>
    ///
    /// <para>
    /// 局限：XAML 岛（Composition 合成）不是 GDI 绘制的，这部分会是空白。
    /// 但本实验关心的正是 GDI 子窗口，所以刚好够用。
    /// </para>
    /// </summary>
    public static bool SaveWindowPrint(IntPtr hwnd, string path, int width, int height)
    {
        if (hwnd == IntPtr.Zero || width <= 0 || height <= 0)
            throw new InvalidOperationException("窗口句柄或尺寸无效");

        // 用 CreateDIBSection 而不是 CreateCompatibleBitmap：前者返回的像素内存由系统保证
        // **零初始化**。CompatibleBitmap 不清零，PrintWindow 失败时留下的是上一次
        // 复用的堆内存 —— 实测真的把上一张图的红色当成"这次拍到了"，白查半小时。
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);

        var header = new BITMAPINFOHEADER
        {
            biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
            biWidth = width,
            biHeight = -height,          // 负数 = 自上而下
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };
        var info = new BITMAPINFO { bmiHeader = header };
        int stride = width * 4;

        IntPtr hBitmap = CreateDIBSection(memDc, ref info, DIB_RGB_COLORS,
                                          out IntPtr bits, IntPtr.Zero, 0);
        if (hBitmap == IntPtr.Zero)
        {
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
            throw new InvalidOperationException("CreateDIBSection 失败");
        }

        IntPtr oldObject = SelectObject(memDc, hBitmap);
        bool ok;
        try
        {
            // PW_RENDERFULLCONTENT = 2：让 DWM 合成的窗口也走重定向表面
            ok = PrintWindow(hwnd, memDc, 2);
            if (!ok) ok = PrintWindow(hwnd, memDc, 0);
        }
        finally
        {
            SelectObject(memDc, oldObject);
        }

        try
        {
            byte[] buffer = new byte[stride * height];
            Marshal.Copy(bits, buffer, 0, Math.Min(buffer.Length, stride * height));

            BitmapSource source = BitmapSource.Create(width, height, 96, 96,
                                                      PixelFormats.Bgra32, null, buffer, stride);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        return ok;
    }

    public static void SaveRect(string path, int x, int y, int width, int height)
    {
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDc, width, height);
        IntPtr oldObject = SelectObject(memDc, hBitmap);

        try
        {
            BitBlt(memDc, 0, 0, width, height, screenDc, x, y, SRCCOPY);
        }
        finally
        {
            // GetDIBits 要求位图不在任何 DC 里，所以先还回去再读像素
            SelectObject(memDc, oldObject);
        }

        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                biHeight = -height,      // 负数 = 自上而下，省一次翻转
                biPlanes = 1,
                biBitCount = 32,
                biCompression = BI_RGB,
            };

            var info = new BITMAPINFO { bmiHeader = header };
            int stride = width * 4;
            byte[] buffer = new byte[stride * height];

            if (GetDIBits(memDc, hBitmap, 0, (uint)height, buffer, ref info, DIB_RGB_COLORS) == 0)
                throw new InvalidOperationException("GetDIBits 失败");

            BitmapSource source = BitmapSource.Create(width, height, 96, 96,
                                                      PixelFormats.Bgra32, null, buffer, stride);

            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(source));

            string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using FileStream stream = File.Create(path);
            encoder.Save(stream);
        }
        finally
        {
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public int biSize;
        public int biWidth;
        public int biHeight;
        public short biPlanes;
        public short biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        // 32 位色不需要调色板，但结构体要留出空间，否则 GetDIBits 会越界写
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
        public uint[] bmiColors;
    }

    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr dest, int x, int y, int width, int height,
                                      IntPtr src, int srcX, int srcY, int rop);
    [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFO info, uint usage,
                                                  out IntPtr bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint lines,
                                        byte[] bits, ref BITMAPINFO info, uint usage);
}
