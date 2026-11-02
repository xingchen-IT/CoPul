"""Z 序采样：持续观察"悬浮窗有没有被游戏压住"。

用法: python tools/zorder-watch.py [秒数] [毫秒间隔]

判据只有一条 —— 从悬浮窗往上走（GW_HWNDPREV）直到链顶，游戏窗口在不在这一段里。
在 = 被压住（用户看不见）；不在 = 在最上层。

只读，不发任何 SetWindowPos。
"""
import ctypes
import sys
import time
from ctypes import wintypes as w

u32 = ctypes.WinDLL("user32", use_last_error=True)
u32.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetWindow.restype = w.HWND
u32.GetWindow.argtypes = [w.HWND, ctypes.c_uint]
u32.GetWindowLongW.restype = ctypes.c_long

u32.SetProcessDPIAware()   # 物理像素，和 shot-full.py 对齐

GW_HWNDPREV = 3
WS_EX_TOPMOST = 0x00000008

FLOAT_TITLE = "CoPul 芯脉 · 悬浮窗"
GAME_CLASS = "GLFW30"


def title(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetWindowTextW(h, b, 256)
    return b.value


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def find_float():
    found = []

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def cb(h, _):
        if u32.IsWindowVisible(h) and title(h) == FLOAT_TITLE:
            found.append(h)
        return True

    u32.EnumWindows(cb, 0)
    return found[0] if found else None


def find_game():
    fg = u32.GetForegroundWindow()
    if fg and cls(fg) == GAME_CLASS:
        return fg
    found = []

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def cb(h, _):
        if u32.IsWindowVisible(h) and cls(h) == GAME_CLASS:
            found.append(h)
        return True

    u32.EnumWindows(cb, 0)
    return found[0] if found else None


def above_chain(h, max_hops=80):
    out, cur = [], u32.GetWindow(h, GW_HWNDPREV)
    for _ in range(max_hops):
        if not cur:
            break
        out.append(cur)
        cur = u32.GetWindow(cur, GW_HWNDPREV)
    return out


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 12.0
    step = float(sys.argv[2]) if len(sys.argv) > 2 else 0.1
    fl, game = find_float(), find_game()
    print(f"float=0x{fl:X} game=0x{game:X}" if fl and game else f"float={fl} game={game}")
    if not fl or not game:
        return
    print(f"float exstyle TOPMOST={bool(u32.GetWindowLongW(fl, -20) & WS_EX_TOPMOST)}")

    end = time.time() + secs
    prev = None
    blocked_n = total = 0
    runs = []          # (起点时刻, 是否被压, 长度)
    while time.time() < end:
        chain = above_chain(fl)
        blocked = game in chain
        total += 1
        blocked_n += 1 if blocked else 0
        if blocked != prev:
            runs.append([round(time.time() % 1000, 1), blocked, 1])
            prev = blocked
        else:
            runs[-1][2] += 1
        time.sleep(step)

    print(f"samples={total} blocked={blocked_n} ({blocked_n * 100 // max(total, 1)}%)")
    for t0, b, n in runs:
        print(f"  {'游戏压住' if b else '悬浮窗在最顶'} 连续 {n} 拍（约 {n * step:.2f}s）")


main()
