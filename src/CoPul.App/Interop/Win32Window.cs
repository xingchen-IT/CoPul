using System.Runtime.InteropServices;

namespace CoPul.App.Interop;

/// <summary>
/// 无边框窗口需要的两件套：<c>WM_GETMINMAXINFO</c> 与显示器工作区。
///
/// 为什么必须有：<c>WindowStyle=None</c> 的窗口最大化时，系统按**整个屏幕**而不是工作区算尺寸，
/// 结果就是盖住任务栏——这是自绘标题栏最常见的一个缺陷。需求 5.4 要求"拖边缘改尺寸、双击最大化"，
/// 所以这里不是可选项。
/// </summary>
internal static class Win32Window
{
    public const int WM_GETMINMAXINFO = 0x0024;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr monitor, ref MONITORINFO info);

    /// <summary>
    /// 把最大化尺寸限制到当前显示器的工作区（扣掉任务栏）。
    /// </summary>
    /// <returns>true = 已改写 lParam，调用方应把 handled 置 true。</returns>
    public static bool TryClampToWorkArea(IntPtr hwnd, IntPtr lParam)
    {
        IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        if (monitor == IntPtr.Zero) return false;

        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfoW(monitor, ref info)) return false;

        var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);

        // ptMaxPosition 是相对显示器左上角的偏移，不是屏幕坐标
        mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
        mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
        mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
        mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;

        // 不限制 ptMaxTrackSize 时，拖到屏幕边缘仍可能超出工作区
        mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
        mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;

        Marshal.StructureToPtr(mmi, lParam, true);
        return true;
    }
}
