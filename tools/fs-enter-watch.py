"""按一次 F11 让游戏进/出全屏，同时逐拍记录：
  - 悬浮窗是否被可见窗口压住（Z 序）
  - 悬浮窗矩形内的像素（判断"到底画没画出来"）
  - 游戏窗口的 exstyle / 几何（判断是不是真全屏、有没有自己抢 topmost）

用法：python tools/fs-enter-watch.py [seconds] [--no-key]
  --no-key 只观察不按键（用来取基线）
"""
import ctypes
import sys
import time
from ctypes import wintypes as w

u32 = ctypes.WinDLL("user32", use_last_error=True)
gdi = ctypes.WinDLL("gdi32", use_last_error=True)
u32.SetProcessDPIAware()
u32.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_long
u32.GetWindow.restype = w.HWND
gdi.CreateCompatibleDC.restype = w.HDC
gdi.CreateCompatibleBitmap.restype = w.HBITMAP
gdi.SelectObject.restype = w.HGDIOBJ

FLOAT_TITLE = "CoPul 芯脉 · 悬浮窗"
VK_F11 = 0x7A
GW_HWNDPREV = 3


def title(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetWindowTextW(h, b, 256)
    return b.value


def cls(h):
    b = ctypes.create_unicode_buffer(256)
    u32.GetClassNameW(h, b, 256)
    return b.value


def rect(h):
    r = w.RECT()
    u32.GetWindowRect(h, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def find(pred):
    out = []

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def cb(h, _):
        if u32.IsWindowVisible(h) and pred(h):
            out.append(h)
        return True

    u32.EnumWindows(cb, 0)
    return out[0] if out else None


def pixel(x, y):
    """取屏幕一个点（物理像素）。"""
    hdc = u32.GetDC(None)
    v = gdi.GetPixel(hdc, x, y)
    u32.ReleaseDC(None, hdc)
    if v == -1 or v == 0xFFFFFFFF:
        return None
    return v


def luma(v):
    if v is None:
        return None
    r, g, b = v & 0xFF, (v >> 8) & 0xFF, (v >> 16) & 0xFF
    return (r * 299 + g * 587 + b * 114) // 1000


def above_chain(h, limit=40):
    """从 h 往上走链，返回比它更靠前的可见窗口。"""
    out = []
    cur = h
    for _ in range(limit):
        cur = u32.GetWindow(cur, GW_HWNDPREV)
        if not cur:
            break
        if not u32.IsWindowVisible(cur):
            continue
        ex = u32.GetWindowLongW(cur, -20) & 0xFFFFFFFF
        st = u32.GetWindowLongW(cur, -16) & 0xFFFFFFFF
        if not (ex & 0x80000) and not (st & 0x10000000):
            continue
        out.append(cur)
    return out


def describe(h):
    if not h:
        return "-"
    return f"[{cls(h)}] {title(h)!r}"


def overlaps(a, b):
    return not (a[2] <= b[0] or b[2] <= a[0] or a[3] <= b[1] or b[3] <= a[1])


def main():
    dur = float(sys.argv[1]) if len(sys.argv) > 1 and not sys.argv[1].startswith("-") else 8.0
    send = "--no-key" not in sys.argv

    fl = find(lambda h: title(h) == FLOAT_TITLE)
    game = find(lambda h: cls(h) == "GLFW30")
    if not fl or not game:
        print(f"找不到窗口 float=0x{(fl or 0):X} game=0x{(game or 0):X}")
        return

    fr = rect(fl)
    # 采样点取悬浮窗中部：这里一定落在底色/文字上，被盖住时会变成游戏的像素
    pts = [(fr[0] + 24 + i * ((fr[2] - fr[0]) - 48) // 4, fr[1] + (fr[3] - fr[1]) // 2) for i in range(5)]
    print(f"float=0x{fl:X} rect={fr} game=0x{game:X} rect={rect(game)}")
    print(f"采样点: {pts}")

    t0 = time.time()
    events = []
    prev = None
    pressed = False

    while time.time() - t0 < dur:
        el = time.time() - t0
        if send and not pressed and el > 1.5:
            u32.keybd_event(VK_F11, 0, 0, 0)
            time.sleep(0.05)
            u32.keybd_event(VK_F11, 0, 2, 0)
            pressed = True
            print(f"→ {el:.2f}s 发送 F11")

        chain = [c for c in above_chain(fl) if overlaps(rect(c), fr)]
        lum = [luma(pixel(*p)) for p in pts]
        dark = sum(1 for v in lum if v is not None and v < 90)
        g_ex = u32.GetWindowLongW(game, -20) & 0xFFFFFFFF
        g_st = u32.GetWindowLongW(game, -16) & 0xFFFFFFFF
        g_r = rect(game)
        key = (dark, g_ex, g_r, tuple(chain[:2]))
        if key != prev:
            events.append((round(el, 2), dark, lum, g_ex, g_st, g_r, describe(chain[0]) if chain else "-", len(chain)))
            prev = key
        time.sleep(0.05)

    print("t(s)  暗点  亮度              gameEx     style       gameRect                 上方窗口")
    for el, dark, lum, g_ex, g_st, g_r, top, n in events:
        print(f"{el:5.2f}  {dark}    {str(lum):24s} 0x{g_ex:06X}  0x{g_st:08X} {str(g_r):26s} {top}")


main()
