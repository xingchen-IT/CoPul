"""Z 序采样（被谁压住版）：每拍看"悬浮窗上面第一个占屏幕的可见窗口"是谁。

用法: python tools/zorder-blocker.py [秒数] [间隔秒]

和 zorder-watch.py 的区别：那个只盯游戏窗口，这个盯**所有**外来窗口 ——
桌面宠物 / 动态壁纸 / 录屏条这类常驻 topmost 全屏窗口，如果它们自己定时提顶，
就会把悬浮窗反复压下去，表现正是"不能一直在最上层"。

只读，不改任何窗口状态。
"""
import ctypes
import sys
import time
from ctypes import wintypes as w

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [w.HWND, ctypes.POINTER(w.RECT)]
u32.GetWindow.restype = w.HWND
u32.GetWindow.argtypes = [w.HWND, ctypes.c_uint]
u32.SetProcessDPIAware()

GW_HWNDPREV = 3
FLOAT_TITLE = "CoPul 芯脉 · 悬浮窗"


def title(h):
    b = ctypes.create_unicode_buffer(200)
    u32.GetWindowTextW(h, b, 200)
    return b.value


def cls(h):
    b = ctypes.create_unicode_buffer(200)
    u32.GetClassNameW(h, b, 200)
    return b.value


def rect(h):
    r = w.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return r


def pid_of(h):
    p = w.DWORD(0)
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value


def find_float():
    found = []

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def cb(h, _):
        if u32.IsWindowVisible(h) and title(h) == FLOAT_TITLE:
            found.append(h)
        return True

    u32.EnumWindows(cb, 0)
    return found[0] if found else None


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 20.0
    step = float(sys.argv[2]) if len(sys.argv) > 2 else 0.1
    fl = find_float()
    if not fl:
        print("没找到悬浮窗（标题: %s）" % FLOAT_TITLE)
        return
    print(f"float=0x{fl:X} 采样 {secs}s / {step}s")

    tally = {}
    samples = 0
    end = time.time() + secs
    while time.time() < end:
        samples += 1
        cur = u32.GetWindow(fl, GW_HWNDPREV)
        who = None
        for _ in range(80):
            if not cur:
                break
            r = rect(cur)
            if u32.IsWindowVisible(cur) and (r.right - r.left) > 8 and (r.bottom - r.top) > 8:
                who = f"[{cls(cur)[:22]}] pid={pid_of(cur)} {title(cur)[:26]!r}"
                break
            cur = u32.GetWindow(cur, GW_HWNDPREV)
        key = who or "(无 · 悬浮窗在最顶)"
        tally[key] = tally.get(key, 0) + 1
        time.sleep(step)

    print(f"samples={samples}")
    for k, n in sorted(tally.items(), key=lambda kv: -kv[1]):
        print(f"  {n:4d} 拍  {k}")


main()
