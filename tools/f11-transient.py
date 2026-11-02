"""F11 切换瞬间的 Z 序取证（M4.30）。

做法：50ms 一拍记录"游戏窗口在不在悬浮窗上面"，中途自动按两次 F11
（全屏 → 窗口 → 全屏），把切换前后的变化抓出来。

判据仍然是 Z 序链（GW_HWNDPREV 往上走），因为"被 DWM 换到独立合成层"
这种遮挡在 Z 序上看不出来 —— 所以同时记录游戏窗口的 style / rect，
用"窗口重建"这一刻对齐时间轴。

用法: python tools/f11-transient.py [输出里是否发按键 1/0]
"""
import ctypes
import sys
import threading
import time
from ctypes import wintypes as w

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.SetProcessDPIAware()
u32.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetWindowRect.argtypes = [w.HWND, ctypes.POINTER(w.RECT)]
u32.GetWindow.restype = w.HWND
u32.GetWindowLongW.restype = ctypes.c_long

VK_F11 = 0x7A
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
    return (r.left, r.top, r.right, r.bottom)


def find_float(pid_want=None):
    """pid_want > 0 只挑该进程；< 0 表示**排除**该进程（用来避开用户自己那份实例）。"""
    f = []

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def cb(h, _):
        if not u32.IsWindowVisible(h) or title(h) != FLOAT_TITLE:
            return True
        p = w.DWORD(0)
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if pid_want == 0:
            f.append(h)
        elif pid_want > 0 and p.value == pid_want:
            f.append(h)
        elif pid_want < 0 and p.value != -pid_want:
            f.append(h)
        return True

    u32.EnumWindows(cb, 0)
    return f[0] if f else None


def find_game():
    f = []

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def cb(h, _):
        if u32.IsWindowVisible(h) and cls(h) == "GLFW30":
            f.append(h)
        return True

    u32.EnumWindows(cb, 0)
    return f[0] if f else None


def above_list(h, max_hops=80):
    out, cur = [], u32.GetWindow(h, GW_HWNDPREV)
    for _ in range(max_hops):
        if not cur:
            break
        out.append(cur)
        cur = u32.GetWindow(cur, GW_HWNDPREV)
    return out


def press_f11():
    u32.keybd_event(VK_F11, 0, 0, 0)
    time.sleep(0.05)
    u32.keybd_event(VK_F11, 0, 2, 0)     # KEYEVENTF_KEYUP


gdi32 = ctypes.WinDLL("gdi32", use_last_error=True)


def sample_pixel(x, y):
    """取屏幕上某点的颜色。悬浮窗的玻璃底是深色（亮度 < 60），游戏村子画面通常是亮色。

    这条判据和 Z 序互补：Z 序说"我在上面"但像素是亮的，就说明**DWM 换了合成层**，
    再多的 SetWindowPos 也没用 —— 那属于渲染通道的问题，不是 Z 序的问题。
    """
    dc = u32.GetDC(None)
    try:
        c = gdi32.GetPixel(dc, x, y)
        if c == 0xFFFFFFFF:
            return None
        return ((c & 0xFF) + ((c >> 8) & 0xFF) + ((c >> 16) & 0xFF)) // 3
    finally:
        u32.ReleaseDC(None, dc)


def main():
    send = (len(sys.argv) > 1 and sys.argv[1] == "1")
    pid = int(sys.argv[2]) if len(sys.argv) > 2 else None
    # 新起的探针实例窗口可能要等一两秒才 Show，等它出现再继续
    fl = None
    for _ in range(40):
        fl = find_float(pid)
        if fl:
            break
        time.sleep(0.2)
    game = find_game()
    if not fl or not game:
        print(f"没找到窗口：float={fl} game={game}")
        return
    p = w.DWORD(0)
    u32.GetWindowThreadProcessId(fl, ctypes.byref(p))
    print(f"float=0x{fl:X} pid={p.value} rect={rect(fl)} game=0x{game:X}")

    # 取浮窗内部三个点（避开文字）：左右各内缩 24px、垂直居中偏下（数值行背景）
    r = rect(fl)
    pts = [(r[0] + 24, (r[1] + r[3]) // 2), ((r[0] + r[2]) // 2, (r[1] + r[3]) // 2),
           (r[2] - 24, (r[1] + r[3]) // 2)]
    print(f"像素采样点: {pts}")

    events = []
    t0 = time.time()
    running = [True]

    def sampler():
        prev = None
        while running[0]:
            chain = above_list(fl)
            blocked = game in chain
            lum = [sample_pixel(*p) for p in pts]
            dark = sum(1 for v in lum if v is not None and v < 80)
            fl_ex = u32.GetWindowLongW(fl, -20) & 0xFFFFFFFF
            g_ex = u32.GetWindowLongW(game, -20) & 0xFFFFFFFF
            # 浮窗上面第一个"有面积"的可见窗口是谁 —— 直接点名遮挡者
            who = "-"
            cur = u32.GetWindow(fl, GW_HWNDPREV)
            for _ in range(80):
                if not cur:
                    break
                rr = rect(cur)
                if u32.IsWindowVisible(cur) and (rr[2] - rr[0]) > 8 and (rr[3] - rr[1]) > 8:
                    who = f"[{cls(cur)[:18]}] {title(cur)[:22]!r}"
                    break
                cur = u32.GetWindow(cur, GW_HWNDPREV)
            key = (blocked, dark, fl_ex, g_ex, who)
            now = time.time() - t0
            if key != prev:
                events.append((round(now, 2), blocked, dark, lum, fl_ex, g_ex, who))
                prev = key
            time.sleep(0.05)

    th = threading.Thread(target=sampler, daemon=True)
    th.start()

    if send:
        time.sleep(1.5)
        print("→ 发送 F11（退出全屏）")
        press_f11()
        time.sleep(3.0)
        print("→ 发送 F11（回到全屏）")
        press_f11()
    time.sleep(4.0)
    running[0] = False
    th.join()

    print("t(s)  被压  暗点  亮度            floatEx   gameEx    上面是谁")
    for t, b, d, lum, fe, ge, who in events:
        note = "正常" if d >= 2 else "★看不见"
        print(f"{t:5.2f}  {'是' if b else '否'}   {d}   {str(lum):14s} 0x{fe:06X} 0x{ge:06X} "
              f"{note} {who}")


main()
