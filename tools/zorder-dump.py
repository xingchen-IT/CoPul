"""Z 序取证：把顶层窗口按"从最顶到最底"的顺序打出来。

用途（M4.30）：用户报"玩 MC 时悬浮窗不在最上层"，需要判断到底是
  ① 悬浮窗根本没带 WS_EX_TOPMOST（我们的问题）
  ② 带了，但游戏窗口排在它上面（同组抢序）
  ③ 游戏窗口自己就是 topmost，且排在它上面（同上，但要逐帧抢）
  ④ 悬浮窗压根不在前台那个显示器上 / 不可见

只读，不改任何窗口状态。纯 ctypes，不需要第三方包。
"""
import ctypes
from ctypes import wintypes as w

u32 = ctypes.WinDLL("user32", use_last_error=True)

GWL_EXSTYLE = -20
WS_EX_TOPMOST = 0x00000008
WS_EX_TOOLWINDOW = 0x00000080
WS_EX_LAYERED = 0x00080000

u32.GetWindowLongW.restype = ctypes.c_long
u32.GetWindowLongW.argtypes = [w.HWND, ctypes.c_int]
u32.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [w.HWND, ctypes.POINTER(w.RECT)]
u32.GetWindow.restype = w.HWND
u32.GetWindow.argtypes = [w.HWND, ctypes.c_uint]

GW_HWNDFIRST = 0


def text(hwnd):
    b = ctypes.create_unicode_buffer(256)
    u32.GetWindowTextW(hwnd, b, 256)
    return b.value


def cls(hwnd):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(hwnd, b, 256)
    return b.value


def pid_of(hwnd):
    p = w.DWORD(0)
    u32.GetWindowThreadProcessId(hwnd, ctypes.byref(p))
    return p.value


def rect(hwnd):
    r = w.RECT()
    u32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def main():
    fg = u32.GetForegroundWindow()
    print(f"foreground: 0x{fg:X} [{cls(fg)}] {text(fg)!r} pid={pid_of(fg)} rect={rect(fg)}")

    hwnd = u32.GetTopWindow(None)
    idx = 0
    while hwnd and idx < 60:
        visible = bool(u32.IsWindowVisible(hwnd))
        ex = u32.GetWindowLongW(hwnd, GWL_EXSTYLE)
        flags = []
        if ex & WS_EX_TOPMOST:
            flags.append("TOPMOST")
        if ex & WS_EX_TOOLWINDOW:
            flags.append("TOOLWIN")
        if ex & WS_EX_LAYERED:
            flags.append("LAYERED")
        if visible:
            print(f"{idx:2d} 0x{hwnd:X} [{cls(hwnd)[:26]:26s}] pid={pid_of(hwnd):6d} "
                  f"{rect(hwnd)} {','.join(flags) or '-'} {text(hwnd)[:38]!r}")
        hwnd = u32.GetWindow(hwnd, 2)  # GW_HWNDNEXT
        idx += 1


main()
