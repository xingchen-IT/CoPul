"""录悬浮窗所在的那一条画面（每秒一张），用来抓"全屏游戏时悬浮窗消失"的现场。

只存 637x111 的竖条（窗口本体 + 下方 60px 参照带），不存整屏 —— 300 帧也只有几 MB。

用法: python tools/float-strip-recorder.py [秒数] [间隔秒] [输出目录]
  秒数默认 180，间隔默认 1.0。

每帧同时记一行 txt：悬浮窗矩形、前台窗口、上方遮挡者、条内/条外亮度。
判"像不像悬浮窗还在"只做粗筛（亮度差 < 6 标 ?），最终以存下来的图为准。
"""
import ctypes
import os
import struct
import sys
import time
import zlib
from ctypes import wintypes as w

u32 = ctypes.WinDLL("user32", use_last_error=True)
gdi = ctypes.WinDLL("gdi32", use_last_error=True)
u32.SetProcessDPIAware()

SW = u32.GetSystemMetrics(0)
SH = u32.GetSystemMetrics(1)


class BIH(ctypes.Structure):
    _fields_ = [("biSize", w.DWORD), ("biWidth", ctypes.c_long), ("biHeight", ctypes.c_long),
                ("biPlanes", w.WORD), ("biBitCount", w.WORD), ("biCompression", w.DWORD),
                ("biSizeImage", w.DWORD), ("biXPelsPerMeter", ctypes.c_long),
                ("biYPelsPerMeter", ctypes.c_long), ("biClrUsed", w.DWORD), ("biClrImportant", w.DWORD)]


class BI(ctypes.Structure):
    _fields_ = [("bmiHeader", BIH), ("bmiColors", w.DWORD * 3)]


u32.GetWindowTextW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetClassNameW.argtypes = [w.HWND, w.LPWSTR, ctypes.c_int]
u32.GetWindowLongW.restype = ctypes.c_long
u32.GetWindow.restype = w.HWND

FLOAT_TITLE = "CoPul 芯脉 · 悬浮窗"
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


def find_float():
    out = []

    @ctypes.WINFUNCTYPE(w.BOOL, w.HWND, w.LPARAM)
    def cb(h, _):
        if u32.IsWindowVisible(h) and title(h) == FLOAT_TITLE:
            out.append(h)
        return True

    u32.EnumWindows(cb, 0)
    return out[0] if out else None


def above_list(h, limit=40):
    out = []
    cur = h
    for _ in range(limit):
        cur = u32.GetWindow(cur, GW_HWNDPREV)
        if not cur:
            break
        if u32.IsWindowVisible(cur):
            out.append(cur)
    return out


def grab(x, y, w, h):
    src = u32.GetWindowDC(u32.GetDesktopWindow())
    mem = gdi.CreateCompatibleDC(src)
    info = BI()
    info.bmiHeader.biSize = ctypes.sizeof(BIH)
    info.bmiHeader.biWidth = w
    info.bmiHeader.biHeight = -h
    info.bmiHeader.biPlanes = 1
    info.bmiHeader.biBitCount = 32
    info.bmiHeader.biCompression = 0
    bits = ctypes.c_void_p()
    bmp = gdi.CreateDIBSection(mem, ctypes.byref(info), 0, ctypes.byref(bits), None, 0)
    old = gdi.SelectObject(mem, bmp)
    gdi.BitBlt(mem, 0, 0, w, h, src, x, y, 0x00CC0020)
    buf = ctypes.string_at(bits, w * h * 4)
    gdi.SelectObject(mem, old)
    gdi.DeleteObject(bmp)
    gdi.DeleteDC(mem)
    u32.ReleaseDC(u32.GetDesktopWindow(), src)
    return buf


def write_png(path, w, h, rgba):
    rows = []
    for y in range(h):
        o = y * w * 4
        row = bytearray([0])
        for x in range(w):
            p = o + x * 4
            row += bytes((rgba[p + 2], rgba[p + 1], rgba[p]))
        rows.append(bytes(row))
    raw = b"".join(rows)

    def chunk(t, d):
        return struct.pack(">I", len(d)) + t + d + struct.pack(">I", zlib.crc32(t + d) & 0xFFFFFFFF)

    png = (b"\x89PNG\r\n\x1a\n"
           + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0))
           + chunk(b"IDAT", zlib.compress(raw, 6))
           + chunk(b"IEND", b""))
    open(path, "wb").write(png)


def mean_lum(px, w, h, y0, y1):
    tot = n = 0
    for y in range(y0, y1):
        o = y * w * 4
        for x in range(0, w, 3):
            p = o + x * 4
            tot += (px[p + 2] * 299 + px[p + 1] * 587 + px[p] * 114) // 1000
            n += 1
    return tot / max(n, 1)


def main():
    secs = float(sys.argv[1]) if len(sys.argv) > 1 else 180.0
    gap = float(sys.argv[2]) if len(sys.argv) > 2 else 1.0
    outdir = sys.argv[3] if len(sys.argv) > 3 else r"D:\CoPulRun\rec"
    os.makedirs(outdir, exist_ok=True)
    log = open(os.path.join(outdir, "frames.txt"), "w", encoding="utf-8")

    fl = find_float()
    if not fl:
        print("没有悬浮窗")
        return
    x0, y0, x1, y1 = rect(fl)
    strip_h = (y1 - y0) + 60
    print(f"float=0x{fl:X} rect=({x0},{y0},{x1},{y1}) 录制 {secs:.0f}s → {outdir}")

    t0 = time.time()
    i = 0
    while time.time() - t0 < secs:
        fl = find_float()
        if not fl:
            log.write(f"{time.time() - t0:7.2f} 悬浮窗没了\n")
            log.flush()
            time.sleep(gap)
            i += 1
            continue
        fx0, fy0, fx1, fy1 = rect(fl)
        w_, h_ = fx1 - fx0, min((fy1 - fy0) + 60, 400)
        px = grab(fx0, fy0, w_, h_)
        inside = mean_lum(px, w_, h_, 4, (fy1 - fy0) - 4)
        below = mean_lum(px, w_, h_, (fy1 - fy0) + 6, h_ - 2)
        diff = abs(inside - below)

        fg = u32.GetForegroundWindow()
        chain = above_list(fl)
        g_ex = 0
        for c in chain:
            if cls(c) == "GLFW30":
                g_ex = u32.GetWindowLongW(c, -20) & 0xFFFFFFFF
                break

        write_png(os.path.join(outdir, f"f{i:04d}.png"), w_, h_, px)
        log.write(f"{time.time() - t0:7.2f} rect=({fx0},{fy0},{fx1},{fy1}) in={inside:5.1f} "
                  f"out={below:5.1f} d={diff:5.1f}{'  ?' if diff < 6 else ''} "
                  f"fg=[{cls(fg)}]{title(fg)[:16]!r} above={len(chain)} gameEx=0x{g_ex:06X}\n")
        log.flush()
        i += 1
        time.sleep(gap)

    log.close()
    print(f"完成：{i} 帧 → {outdir}")


main()
