"""量"游戏抢顶之后，悬浮窗多久回到最顶"（M4.30 的 A/B 判据）。

做法：先确认悬浮窗在最顶，然后手工对游戏窗口发一次 `SetWindowPos(HWND_TOPMOST)`
—— GLFW 进全屏时干的就是这件事（实测会把 gameEx 从 0x040010 变成 0x040018）——
然后用 20ms 的粒度数"多久悬浮窗又回到了最顶"。

为什么不用真按 F11 当判据：游戏是否给自己加 topmost 每次都不一样（跑两遍一次
0x040018 一次 0x040010），复现不稳定；手工注入这一步才是可重复的。

用法: python tools/topmost-latency.py <float_pid|负数为排除> [次数]
"""
import ctypes
import statistics
import sys
import time
from ctypes import wintypes as w

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.SetProcessDPIAware()
u32.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetWindow.restype = w.HWND
u32.GetWindow.argtypes = [w.HWND, ctypes.c_uint]
u32.SetWindowPos.argtypes = [w.HWND, w.HWND, ctypes.c_int, ctypes.c_int,
                             ctypes.c_int, ctypes.c_int, ctypes.c_uint]

GW_HWNDPREV = 3
HWND_TOPMOST = -1
SWP_NOMOVE, SWP_NOSIZE, SWP_NOACTIVATE = 0x0002, 0x0001, 0x0010
FLOAT_TITLE = "CoPul 芯脉 · 悬浮窗"


def title(h):
    b = ctypes.create_unicode_buffer(200)
    u32.GetWindowTextW(h, b, 200)
    return b.value


def cls(h):
    b = ctypes.create_unicode_buffer(200)
    u32.GetClassNameW(h, b, 200)
    return b.value


def find_float(pid_want):
    f = []

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def cb(h, _):
        if not u32.IsWindowVisible(h) or title(h) != FLOAT_TITLE:
            return True
        p = w.DWORD(0)
        u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if (pid_want == 0
                or (pid_want > 0 and p.value == pid_want)
                or (pid_want < 0 and p.value != -pid_want)):
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


def blocked(fl, game):
    cur = u32.GetWindow(fl, GW_HWNDPREV)
    for _ in range(80):
        if not cur:
            return False
        if cur == game:
            return True
        cur = u32.GetWindow(cur, GW_HWNDPREV)
    return False


def main():
    pid = int(sys.argv[1]) if len(sys.argv) > 1 else 0
    n = int(sys.argv[2]) if len(sys.argv) > 2 else 5
    fl, game = find_float(pid), find_game()
    if not fl or not game:
        print(f"没找到窗口 float={fl} game={game}")
        return
    print(f"float=0x{fl:X} game=0x{game:X} 轮数={n}")

    # 起手先等悬浮窗回到最顶（上一轮可能刚打完）
    t = time.time()
    while blocked(fl, game) and time.time() - t < 3:
        time.sleep(0.02)

    # 游戏窗口原本是不是 topmost —— 跑完要还原，不能把用户的游戏窗口留在奇怪状态
    was_topmost = bool(u32.GetWindowLongW(game, -20) & 0x8)

    lat = []
    for i in range(n):
        u32.SetWindowPos(w.HWND(game), w.HWND(-1), 0, 0, 0, 0,
                         SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
        t0 = time.perf_counter()
        # 抢顶必然把我压下去；从"发现自己被压"开始计时到"又回到最顶"
        while not blocked(fl, game) and time.perf_counter() - t0 < 1.0:
            time.sleep(0.005)          # 等它真的压下来
        t1 = time.perf_counter()
        while blocked(fl, game) and time.perf_counter() - t1 < 3.0:
            time.sleep(0.02)
        ms = (time.perf_counter() - t1) * 1000
        lat.append(ms)
        print(f"  第 {i + 1} 轮：被压 {ms:7.1f} ms 后回到最顶")
        time.sleep(0.4)

    if lat:
        print(f"样本={len(lat)} 中位={statistics.median(lat):.0f} ms "
              f"最大={max(lat):.0f} ms 最小={min(lat):.0f} ms")

    # 还原游戏窗口的 topmost 状态（-2 = HWND_NOTOPMOST）
    u32.SetWindowPos(w.HWND(game), w.HWND(-2 if not was_topmost else -1), 0, 0, 0, 0,
                     SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE)
    print(f"已还原游戏窗口 topmost={was_topmost}")


main()
