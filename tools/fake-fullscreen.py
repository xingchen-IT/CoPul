# 造一个"真正全屏"的窗口：无边框铺满整屏 + 无标题栏，用来验全屏检测。
# 本机 Add-Type 被安全策略禁，所以走 python + ctypes（记忆里确认可用）。
#
# 用法：
#   python fake-fullscreen.py            # 全屏，等 25 秒自动关
#   python fake-fullscreen.py --hold 60  # 全屏 60 秒
#   python fake-fullscreen.py --windowed # 不铺满（对照组，不该被判成全屏）
#   python fake-fullscreen.py --shrink 8 # 第 8 秒缩成 800x600（模拟"退出全屏但窗口还在"）
#   python fake-fullscreen.py --shrink 8 --restore 2  # 缩窗 2 秒后重新铺满（Alt+Tab 出去又回来）
#
# 关键点：必须真的铺满整屏、真的无 WS_CAPTION，否则测不出检测逻辑。

import ctypes
import ctypes.wintypes as wt
import sys
import time

user32 = ctypes.WinDLL('user32', use_last_error=True)
gdi32 = ctypes.WinDLL('gdi32', use_last_error=True)
kernel32 = ctypes.WinDLL('kernel32', use_last_error=True)

WS_POPUP = 0x80000000
WS_VISIBLE = 0x10000000
WS_EX_TOPMOST = 0x00000008
WS_EX_TOOLWINDOW = 0x00000080

SW_SHOW = 5
HWND_TOPMOST = -1


class WNDCLASS(ctypes.Structure):
    _fields_ = [
        ('style', wt.UINT),
        ('lpfnWndProc', ctypes.c_void_p),
        ('cbClsExtra', ctypes.c_int),
        ('cbWndExtra', ctypes.c_int),
        ('hInstance', wt.HINSTANCE),
        ('hIcon', wt.HICON),
        ('hCursor', wt.HANDLE),
        ('hbrBackground', wt.HBRUSH),
        ('lpszMenuName', ctypes.c_wchar_p),
        ('lpszClassName', ctypes.c_wchar_p),
    ]


def main():
    hold = 25
    windowed = False
    shrink_at = -1
    restore_after = -1   # 缩窗后多久重新铺满（<0 = 不恢复，模拟真的退出全屏）
    args = sys.argv[1:]
    for i, a in enumerate(args):
        if a == '--hold' and i + 1 < len(args):
            hold = int(args[i + 1])
        if a == '--shrink' and i + 1 < len(args):
            shrink_at = int(args[i + 1])
        if a == '--restore' and i + 1 < len(args):
            restore_after = float(args[i + 1])
        if a == '--windowed':
            windowed = True

    # ---- 必须先声明 DPI 感知，否则 GetSystemMetrics 返回的是 DIP 而不是物理像素 ----
    # 本机是 2560x1600 @150%，DPI-unaware 进程会读到 1707x1067，
    # 造出来的"全屏"窗口其实只铺满 2/3 屏幕 —— 检测器判它非全屏是**正确**的，
    # 但那样就验不到全屏路径了（第一版就是这么白测一轮的）。
    try:
        # Win10 1703+ 的 per-monitor v2
        ctypes.windll.shcore.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4))
    except Exception:
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(2)   # PROCESS_PER_MONITOR_DPI_AWARE
        except Exception:
            try:
                user32.SetProcessDPIAware()                   # Vista+ 老接口兜底
            except Exception:
                pass

    # 主屏宽高（现在是物理像素）
    sw = user32.GetSystemMetrics(0)
    sh = user32.GetSystemMetrics(1)

    # 显式声明返回类型：不声明时 ctypes 按 int（32 位）返回，64 位句柄会溢出
    kernel32.GetModuleHandleW.restype = ctypes.c_void_p
    kernel32.GetModuleHandleW.argtypes = [ctypes.c_wchar_p]
    user32.CreateWindowExW.restype = ctypes.c_void_p
    user32.CreateWindowExW.argtypes = [
        wt.DWORD, ctypes.c_wchar_p, ctypes.c_wchar_p, wt.DWORD,
        ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
        ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p, ctypes.c_void_p,
    ]
    user32.DefWindowProcW.restype = ctypes.c_void_p
    user32.DefWindowProcW.argtypes = [ctypes.c_void_p, wt.UINT, wt.WPARAM, wt.LPARAM]

    user32.ShowWindow.restype = wt.BOOL
    user32.ShowWindow.argtypes = [wt.HWND, ctypes.c_int]
    # 必须显式声明 argtypes：不声明时 ctypes 把 hwnd 当 32 位 int 传，
    # 缩窗那一次调用会静默失败（返回 0 但脚本没查），表现为"明明缩了，检测器还判全屏"。
    user32.SetWindowPos.restype = wt.BOOL
    user32.SetWindowPos.argtypes = [wt.HWND, wt.HWND, ctypes.c_int, ctypes.c_int,
                                    ctypes.c_int, ctypes.c_int, wt.UINT]
    user32.SetForegroundWindow.restype = wt.BOOL
    user32.SetForegroundWindow.argtypes = [wt.HWND]
    user32.GetForegroundWindow.restype = wt.HWND
    user32.DestroyWindow.restype = wt.BOOL
    user32.DestroyWindow.argtypes = [wt.HWND]

    wc = WNDCLASS()
    wc.lpszClassName = 'CoPulFakeFullscreen'
    wc.hInstance = kernel32.GetModuleHandleW(None)
    wc.style = 0
    wc.hbrBackground = gdi32.CreateSolidBrush(0x00202020)  # 深灰（BGR）
    wc.lpfnWndProc = ctypes.cast(
        ctypes.WINFUNCTYPE(ctypes.c_void_p, wt.HWND, wt.UINT, wt.WPARAM, wt.LPARAM)(
            lambda h, m, w, l: user32.DefWindowProcW(h, m, w, l)),
        ctypes.c_void_p)

    if not user32.RegisterClassW(ctypes.byref(wc)):
        err = ctypes.get_last_error()
        if err != 0x582:  # ERROR_CLASS_ALREADY_EXISTS
            print('RegisterClass failed: %d' % err)
            return 1

    style = WS_POPUP | WS_VISIBLE
    exstyle = WS_EX_TOPMOST | WS_EX_TOOLWINDOW

    if windowed:
        # 对照组：800x600 居中，铺不满，不该被判成全屏
        x, y, w, h = (sw - 800) // 2, (sh - 600) // 2, 800, 600
    else:
        x, y, w, h = 0, 0, sw, sh

    hwnd = user32.CreateWindowExW(
        exstyle, wc.lpszClassName, 'FakeFullscreen', style,
        x, y, w, h, None, None, wc.hInstance, None)

    if not hwnd:
        print('CreateWindowEx failed: %d' % ctypes.get_last_error())
        return 1

    user32.ShowWindow(hwnd, SW_SHOW)
    user32.SetWindowPos(hwnd, wt.HWND(HWND_TOPMOST), x, y, w, h, 0)

    # ---- 抢前台的三种手段，按可靠性排序 ----
    #
    # 单靠 SetForegroundWindow 不行：Windows 只允许"当前已在前台的进程"或
    # "刚收到用户输入的进程"设前台，后台 python 调它常常只是任务栏闪一下。
    # 实测就踩到过：伪造窗口明明铺满了，前台却是记事本，检测器于是正确判非全屏，
    # 白测一轮。
    #
    # 1) AttachThreadInput：把自己的输入队列挂到当前前台线程上，绕过前台锁
    # 2) SetForegroundWindow
    # 3) 最后用 SetWindowPos(HWND_TOP) + 短暂 BringWindowToTop
    cur_fg = user32.GetForegroundWindow()
    fg_tid = user32.GetWindowThreadProcessId(cur_fg, None) if cur_fg else 0
    my_tid = kernel32.GetCurrentThreadId()

    attached = False
    if fg_tid and fg_tid != my_tid:
        attached = bool(user32.AttachThreadInput(my_tid, fg_tid, True))

    user32.BringWindowToTop(hwnd)
    user32.SetForegroundWindow(hwnd)
    user32.SetFocus(hwnd)

    if attached:
        user32.AttachThreadInput(my_tid, fg_tid, False)

    user32.UpdateWindow(hwnd)

    # 前台到底抢到没有必须说出来：抢不到的话检测器看到的是"别人"，
    # 会正确判非全屏，而你会以为是全屏检测坏了（M4.22 第一轮就白跑了一轮）。
    got_fg = user32.GetForegroundWindow()
    rect = wt.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(rect))
    print('fake fullscreen window: hwnd=0x%X rect=(%d,%d)-(%d,%d) screen=%dx%d mode=%s'
          % (hwnd, rect.left, rect.top, rect.right, rect.bottom, sw, sh,
             'windowed' if windowed else 'FULLSCREEN'))
    print('foreground: 0x%X  (own=%s, 抢到=%s)'
          % (got_fg, got_fg == hwnd, 'YES' if got_fg == hwnd else 'NO'))
    print('holding for %d seconds...' % hold)
    sys.stdout.flush()

    # 抽消息，让窗口响应系统（否则可能被判为无响应/不渲染）
    msg = wt.MSG()
    start = time.time()
    shrunk = False
    restored = False
    shrunk_at = 0.0
    while time.time() - start < hold:
        # "退出全屏但窗口还在"：缩成 800x600。这一路比"窗口直接销毁"更常见
        # （Alt+Enter 切窗口化、游戏自己退出全屏），替补主体必须跟着判成非全屏。
        if shrink_at >= 0 and not shrunk and time.time() - start >= shrink_at:
            shrunk = True
            shrunk_at = time.time()
            sx, sy = (sw - 800) // 2, (sh - 600) // 2
            ok = user32.SetWindowPos(hwnd, wt.HWND(HWND_TOPMOST), sx, sy, 800, 600, 0)
            user32.GetWindowRect(hwnd, ctypes.byref(rect))
            print('shrunk at %.1fs: ret=%d rect=(%d,%d)-(%d,%d) restore_after=%s'
                  % (time.time() - start, ok, rect.left, rect.top, rect.right, rect.bottom,
                     ('%.1fs' % restore_after) if restore_after >= 0 else 'none'))
            sys.stdout.flush()

        # 又切回全屏（Alt+Tab 看一眼就回来）。退出档位够长的话不该回落任务栏 ——
        # 这正是"退出确认"档位存在的意义（M4.23）。
        if shrunk and restored is False and restore_after >= 0 \
                and time.time() - shrunk_at >= restore_after:
            restored = True
            ok = user32.SetWindowPos(hwnd, wt.HWND(HWND_TOPMOST), 0, 0, sw, sh, 0)
            user32.SetForegroundWindow(hwnd)
            user32.GetWindowRect(hwnd, ctypes.byref(rect))
            print('restored at %.1fs: ret=%d rect=(%d,%d)-(%d,%d)'
                  % (time.time() - start, ok, rect.left, rect.top, rect.right, rect.bottom))
            sys.stdout.flush()

        while user32.PeekMessageW(ctypes.byref(msg), hwnd, 0, 0, 1):
            user32.TranslateMessage(ctypes.byref(msg))
            user32.DispatchMessageW(ctypes.byref(msg))
        time.sleep(0.01)

    user32.DestroyWindow(hwnd)
    print('closed')
    return 0


if __name__ == '__main__':
    sys.exit(main())
