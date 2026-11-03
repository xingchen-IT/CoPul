"""实验：SetWindowCompositionAttribute 的 ACRYLICBLURBEHIND 能不能作用在
**layered** 的任务栏条上（条是 AllowsTransparency=True）。

对照组 = 同一块区域注入 accent 前后的像素均值/纹理量。
若条内变红且 grad 下降 -> accent 可用，直接给条加玻璃即可（无需宿窗）。
若毫无变化 -> 必须另起一个非 layered 的玻璃宿主窗口垫在条下面。

一次调用内完成"启动 + 注入 + 截图 + 收尾"（agent 拉起的 GUI 会被回收）。
"""
import ctypes
import os
import subprocess
import sys
import time
from ctypes import wintypes

sys.path.insert(0, r"D:\CoPulProbe")
from shot import grab, write_png
from detail import read_png, region_stats

user32 = ctypes.WinDLL("user32", use_last_error=True)
WNDENUMPROC = ctypes.WINFUNCTYPE(wintypes.BOOL, wintypes.HWND, wintypes.LPARAM)


class AccentPolicy(ctypes.Structure):
    _fields_ = [("AccentState", ctypes.c_int),
                ("AccentFlags", ctypes.c_int),
                ("GradientColor", ctypes.c_uint),
                ("AnimationId", ctypes.c_int)]


class WCAD(ctypes.Structure):
    _fields_ = [("Attribute", ctypes.c_int),
                ("Data", ctypes.c_void_p),
                ("SizeOfData", ctypes.c_int)]


def set_accent(hwnd, state, color):
    fn = ctypes.WinDLL("user32").SetWindowCompositionAttribute
    fn.restype = ctypes.c_int
    fn.argtypes = [wintypes.HWND, ctypes.POINTER(WCAD)]
    p = AccentPolicy(state, 0, color, 0)
    d = WCAD(19, ctypes.cast(ctypes.byref(p), ctypes.c_void_p), ctypes.sizeof(AccentPolicy))
    return fn(hwnd, ctypes.byref(d))


def rect(hwnd):
    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return r.left, r.top, r.right, r.bottom


def bar_rect():
    tb = user32.FindWindowW("Shell_TrayWnd", None)
    return rect(tb)


def find_bar(pid, tb):
    """进程里那块贴任务栏的顶层窗口：高 = 任务栏高、贴在任务栏左边。"""
    found = []
    def cb(hwnd, _lp):
        p = wintypes.DWORD()
        user32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
        if p.value != pid:
            return True
        l, t, r, b = rect(hwnd)
        if (b - t) == (tb[3] - tb[1]) and l <= tb[0] + 60 and t == tb[1]:
            found.append(hwnd)
        return True
    user32.EnumWindows(WNDENUMPROC(cb), 0)
    return found[0] if found else None


def main():
    exe = r"D:\CoPulProbe\CoPul.exe"
    p = subprocess.Popen([exe, "--tray"], cwd=r"D:\CoPulProbe")
    try:
        tb = bar_rect()
        print("taskbar", tb, flush=True)
        bar = None
        for _ in range(60):
            time.sleep(0.5)
            bar = find_bar(p.pid, tb)
            if bar:
                break
        if not bar:
            print("FAIL: 没找到条窗口")
            return
        print("bar hwnd", hex(bar), rect(bar), flush=True)

        time.sleep(1.2)
        x0, y0, x1, y1 = rect(bar)
        # 只取条右侧 2/3（避开托盘图标一侧）
        w, h, buf = grab(x0, y0, x1, y1)
        write_png(r"D:\CoPulProbe\accent-before.png", w, h, buf)

        ex = user32.GetWindowLongW(bar, -20)
        print(f"exstyle=0x{ex:X} layered={bool(ex & 0x80000)}", flush=True)

        # 强红：alpha 0x80，R=255。这个颜色只要出现一点点都能看出来。
        print("accent acrylic ->", set_accent(bar, 4, 0x800000FF), flush=True)
        time.sleep(0.8)
        w, h, buf = grab(x0, y0, x1, y1)
        write_png(r"D:\CoPulProbe\accent-after.png", w, h, buf)

        for name in ("accent-before.png", "accent-after.png"):
            ww, hh, bpp, px = read_png(os.path.join(r"D:\CoPulProbe", name))
            pad = 6
            mean, grad = region_stats(ww, hh, bpp, px, pad, pad, ww - pad, hh - pad)
            print(f"{name}: mean=({mean[0]:.1f},{mean[1]:.1f},{mean[2]:.1f}) grad={grad:.2f}", flush=True)

        # 还原（进程随后结束，但别留下副作用）
        set_accent(bar, 0, 0)
    finally:
        time.sleep(0.3)
        p.terminate()
        try:
            p.wait(timeout=5)
        except Exception:
            p.kill()


main()
