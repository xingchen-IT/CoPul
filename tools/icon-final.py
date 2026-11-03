"""
CoPul 图标：推力喷管。

=============================================================================
为什么是这个
=============================================================================

前四轮一共 16 个概念，全部走"数据可视化"的语汇（折线、柱、环、刻度），
问题是**任何监控软件换个颜色都是同一个图标**；16px 下更是全部糊成一团。

这一版换的是**物**：一枚朝下的火箭发动机。它同时说清三件事 ——

  · 这是"推力"（pulse）：尾焰是整幅唯一的亮区，视线第一秒就落在那里
  · 这是"机器"：钟形喷管、冷却管、喉口、出口唇环，全是真发动机的零件
  · 这是"往下喷"：竖构图。系统托盘里横构图和圆形图标占绝大多数，
    一个上重下轻的竖直亮条在一片方块里最容易被认出来

**竖构图还有工程上的好处**：16px 下一枚图标只有 256 个像素，
竖条的可用像素比环形/折线多得多 —— 亮区宽度占满 1/3 画幅，
无论缩到多小都还剩 5~6 个像素宽，不会像细线那样消失。

=============================================================================
细节分层（这是"加细节"的具体内容）
=============================================================================

  1 安装法兰  顶部一块浅钢色横板，让喷管有"装在某处"的确定感
  2 钟形喷管  上窄下宽，左半压暗做出圆柱体
  3 冷却管    6 根细线沿着母线走，**只在大尺寸出现**（<=32 会变噪点）
  4 喉口      出口上方一条近黑带，喷管因此是"空心的"
  5 出口唇环  琥珀色一道，全图唯一的暖色，也是"高温"的信号
  6 尾焰      三层：青边 → 淡青芯 → 白炽芯
  7 激波钻石  白炽芯里 3 颗菱形，超音速尾焰的标志性结构（真发动机才有）

小尺寸（<=24）另有一套几何：去掉冷却管与钻石，尾焰收敛成一个锐角三角，
唇环加粗 —— 保证 16px 下还剩"暗帽 + 琥珀横杠 + 亮三角"三个可辨元素。

=============================================================================
配色
=============================================================================

守住需求 10.3 的低饱和底线：底 #070B11 / 钢 #2A3644 / 青 #3FC8DC / 琥珀 #E0A44A。
尾焰用的白是 #EAF4F8（不是纯白），纯白在深底上会"发光过度"、边缘发虚。

托盘深色任务栏版（dark=True）走反相：亮青底 + 深色喷管 + 白尾焰。
"""

import math
import os
import struct
import zlib

MASTER = 256
SS = 3            # 超采样倍数（1024 源图降到 2，否则太慢）
SS_BIG = 2
BIG_CUTOFF = 320  # >= 这个尺寸时把 SS 降到 2

# ---------------------------------------------------------------------------
# 配色
# ---------------------------------------------------------------------------

INK        = (0x07, 0x0B, 0x11, 255)
INK_RIM    = (0x1E, 0x2B, 0x3A, 255)

STEEL_TOP  = (0x6A, 0x80, 0x98, 255)
STEEL_L    = (0x3A, 0x4A, 0x5E, 255)
STEEL_R    = (0x1E, 0x29, 0x37, 255)
STEEL_EDGE = (0x8E, 0xA6, 0xBE, 255)
THROAT     = (0x05, 0x08, 0x0C, 255)

CYAN       = (0x3F, 0xC8, 0xDC, 255)
CYAN_PALE  = (0x6E, 0xD8, 0xE8, 255)
FLAME_HOT  = (0xB8, 0xEE, 0xF6, 255)
DIAMOND    = (0xFF, 0xFF, 0xFF, 255)
AMBER      = (0xE0, 0xA4, 0x4A, 255)

RIBS_DARK  = (0x0A, 0x10, 0x17, 170)
RIBS_LIGHT = (0xFF, 0xFF, 0xFF, 48)
MOUTH_C    = (0x4E, 0x61, 0x76, 255)

# 托盘（深色任务栏）：反相。
# 尾焰不能用白色 —— 亮青底 + 白三角 = 一整块高光，读出来像高脚杯。
# 整个发动机（喷管 + 尾焰 + 唇环）统一做成深色剪影，才是"同一台机器"。
T_PLATE    = (0x4F, 0xC8, 0xDC, 255)
T_RIM      = (0x8A, 0xE2, 0xEC, 255)
T_FLANGE   = (0x0E, 0x22, 0x2A, 255)
T_BELL     = (0x12, 0x2B, 0x36, 255)
T_BELL_R   = (0x09, 0x18, 0x1F, 255)
T_LIP      = (0xC2, 0x74, 0x2A, 255)
T_FLAME    = (0x0B, 0x2C, 0x36, 255)
T_FLAME_IN = (0x14, 0x48, 0x54, 255)
T_RIBS     = (0x0A, 0x1B, 0x23, 110)
T_MOUTH    = (0x0B, 0x1F, 0x27, 255)

BG_RADIUS = 0.22


# ---------------------------------------------------------------------------
# 几何（归一化 0..1）
# ---------------------------------------------------------------------------

# --- 大尺寸 ---------------------------------------------------------------
#
# 喷管型线：燃烧室（等宽）→ 喉部（收缩到最窄）→ 钟形（扩张到出口）。
# 上一版是直边梯形，读出来像灯罩；真正的发动机必须能看出"喉部"这个腰。
BELL_PROFILE = [          # (y, 右半 x)
    (0.098, 0.620),       # 燃烧室顶
    (0.205, 0.620),       # 燃烧室直段
    (0.252, 0.548),       # 喉部（最窄）
    (0.302, 0.586),
    (0.366, 0.652),
    (0.436, 0.716),       # 出口
]
BELL_Y0 = BELL_PROFILE[0][0]
BELL_Y1 = BELL_PROFILE[-1][0]
EXIT_Y = BELL_Y1

FLANGE     = [(0.368, 0.062), (0.632, 0.062), (0.632, 0.106), (0.368, 0.106)]
BELL_MOUTH = [(0.286, EXIT_Y - 0.004), (0.714, EXIT_Y - 0.004),
              (0.710, EXIT_Y + 0.026), (0.290, EXIT_Y + 0.026)]
LIP        = [(0.290, EXIT_Y + 0.030), (0.710, EXIT_Y + 0.030)]

_FY = EXIT_Y + 0.034
# 尾焰轮廓带一点外凸：直边三角形会读成交通锥。中段鼓出来一点才像"喷出来的一团"。
FLAME_OUT = [(0.316, _FY), (0.684, _FY), (0.646, 0.588),
             (0.566, 0.716), (0.500, 0.818), (0.434, 0.716), (0.354, 0.588)]
FLAME_MID = [(0.388, _FY), (0.612, _FY), (0.580, 0.584),
             (0.532, 0.700), (0.500, 0.772), (0.468, 0.700), (0.420, 0.584)]
FLAME_CORE = [(0.436, _FY), (0.564, _FY), (0.540, 0.576),
              (0.516, 0.660), (0.500, 0.712), (0.484, 0.660), (0.460, 0.576)]


def _bell_poly():
    """把型线镜像成闭合多边形。"""
    right = [(x, y) for y, x in BELL_PROFILE]
    left = [(1.0 - x, y) for y, x in reversed(BELL_PROFILE)]
    return right + left


BELL = _bell_poly()
# 左半块压暗：型线左轮廓 + 中轴，构成一个半块（做圆柱体用）
BELL_SH = ([(1.0 - x, y) for y, x in BELL_PROFILE] +
           [(0.5, y) for y, x in reversed(BELL_PROFILE)])


def _bell_half(y):
    """型线上给定高度的右半宽（线性插值）。"""
    for i in range(len(BELL_PROFILE) - 1):
        y0, x0 = BELL_PROFILE[i]
        y1, x1 = BELL_PROFILE[i + 1]
        if y0 <= y <= y1:
            t = (y - y0) / (y1 - y0)
            return x0 + (x1 - x0) * t
    return BELL_PROFILE[-1][1]


# 冷却管：沿型线走，从喉部下方到出口，等分成 6 根
def _rib_x(y, t, margin=0.024):
    h = _bell_half(y)
    left, right = 1.0 - h, h
    return left + margin + t * (right - left - 2 * margin)


RIBS = [(_rib_x(0.300, i / 5.0), _rib_x(0.430, i / 5.0)) for i in range(6)]

# --- 小尺寸 ---------------------------------------------------------------
# 16px 只有 256 个像素，大尺寸那套"喷管 30% + 尾焰 42%"的比例会把喷管压成一条暗线。
# 这里把喷管放大到占 40% 高度、出口拉宽到 60% 画幅，并且整体提亮一档钢色 ——
# 否则深底上只剩一团亮三角，"发动机"这个信息彻底丢失。
FLANGE_S = [(0.362, 0.098), (0.638, 0.098), (0.638, 0.152), (0.362, 0.152)]
BELL_S   = [(0.424, 0.152), (0.576, 0.152), (0.544, 0.288),
            (0.614, 0.372), (0.800, 0.520), (0.200, 0.520)]
LIP_S    = [(0.204, 0.530), (0.796, 0.530)]
FLAME_OUT_S = [(0.232, 0.542), (0.768, 0.542), (0.500, 0.902)]
FLAME_CORE_S = [(0.390, 0.542), (0.610, 0.542), (0.500, 0.782)]


# ---------------------------------------------------------------------------
# 覆盖判定
# ---------------------------------------------------------------------------

def rounded_rect_cov(px, py, size, radius):
    half = size / 2.0
    dx = abs(px - half) - (half - radius)
    dy = abs(py - half) - (half - radius)
    ox, oy = max(dx, 0.0), max(dy, 0.0)
    return -(math.hypot(ox, oy) + min(max(dx, dy), 0.0) - radius) > 0


def disc_cov(px, py, cx, cy, r):
    return (px - cx) ** 2 + (py - cy) ** 2 <= r * r


def poly_cov(px, py, pts):
    inside = False
    n = len(pts)
    for i in range(n):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 1) % n]
        if (y1 > py) != (y2 > py):
            if px < x1 + (py - y1) * (x2 - x1) / (y2 - y1):
                inside = not inside
    return inside


def stroke_cov(px, py, pts, half):
    for i in range(len(pts) - 1):
        ax, ay = pts[i]
        bx, by = pts[i + 1]
        dx, dy = bx - ax, by - ay
        L = math.hypot(dx, dy)
        if L < 1e-9:
            continue
        ux, uy = dx / L, dy / L
        rx, ry = px - ax, py - ay
        u = (rx * ux + ry * uy) / L
        v = abs(rx * (-uy) + ry * ux)
        if v <= half and 0.0 <= u <= 1.0:
            return True
    return False


# ---------------------------------------------------------------------------
# 光栅化 / 合成
# ---------------------------------------------------------------------------

def rasterize(size, cov_fn, bbox=None, ss=None):
    ss = ss or SS
    buf = bytearray(size * size)
    x0, y0, x1, y1 = bbox if bbox else (0, 0, size, size)
    x0 = max(0, int(x0)); y0 = max(0, int(y0))
    x1 = min(size, int(x1) + 1); y1 = min(size, int(y1) + 1)
    if x1 <= x0 or y1 <= y0:
        return buf
    step = 1.0 / ss
    n = ss * ss
    for oy in range(y0, y1):
        base = oy * size
        for ox in range(x0, x1):
            c = 0
            for sy in range(ss):
                yy = oy + (sy + 0.5) * step
                for sx in range(ss):
                    if cov_fn(ox + (sx + 0.5) * step, yy):
                        c += 1
            if c:
                buf[base + ox] = int(round(255.0 * c / n))
    return buf


def composite(size, layers):
    n = size * size
    out = bytearray(n * 4)
    for i in range(n):
        r = g = b = 0.0
        a = 0.0
        for buf, (cr, cg, cb, ca0) in layers:
            c = buf[i]
            if c == 0:
                continue
            ca = (c / 255.0) * (ca0 / 255.0)
            if ca <= 0:
                continue
            na = ca + a * (1 - ca)
            r = (cr * ca + r * a * (1 - ca)) / na
            g = (cg * ca + g * a * (1 - ca)) / na
            b = (cb * ca + b * a * (1 - ca)) / na
            a = na
        o = i * 4
        out[o] = int(round(min(255, r)))
        out[o + 1] = int(round(min(255, g)))
        out[o + 2] = int(round(min(255, b)))
        out[o + 3] = int(round(min(255, a * 255)))
    for i in range(n):
        o = i * 4
        if out[o + 3] == 0:
            out[o] = out[o + 1] = out[o + 2] = 0
    return out


def add_poly(layers, size, pts, col, ss=None):
    P = [(x * size, y * size) for x, y in pts]
    xs = [p[0] for p in P]; ys = [p[1] for p in P]
    layers.append((rasterize(size, lambda x, y: poly_cov(x, y, P),
                             (min(xs) - 2, min(ys) - 2, max(xs) + 2, max(ys) + 2), ss), col))


def add_disc(layers, size, cx, cy, r, col, ss=None):
    X, Y, R = cx * size, cy * size, r * size
    layers.append((rasterize(size, lambda x, y: disc_cov(x, y, X, Y, R),
                             (X - R - 2, Y - R - 2, X + R + 2, Y + R + 2), ss), col))


def add_stroke(layers, size, pts_norm, w, col, ss=None):
    P = [(x * size, y * size) for x, y in pts_norm]
    half = w * size / 2.0
    xs = [p[0] for p in P]; ys = [p[1] for p in P]
    pad = half + 3
    layers.append((rasterize(size, lambda x, y: stroke_cov(x, y, P, half),
                             (min(xs) - pad, min(ys) - pad, max(xs) + pad, max(ys) + pad), ss), col))


def add_radial(layers, size, cx, cy, r_in, r_out, alpha, col, ss=None):
    """连续径向渐变。

    第一版用"三个同心圆叠加"近似光晕，结果三个圆的边界肉眼可辨 —— 分层看得很清楚。
    这里改成逐像素算覆盖率（smoothstep），一个像素都不量化成 0/1，边缘才是连续的。
    """
    if alpha <= 0:
        return
    X, Y = cx * size, cy * size
    R0, R1 = r_in * size, r_out * size
    x0 = max(0, int(X - R1) - 2); x1 = min(size, int(X + R1) + 3)
    y0 = max(0, int(Y - R1) - 2); y1 = min(size, int(Y + R1) + 3)
    buf = bytearray(size * size)
    for oy in range(y0, y1):
        dy = oy + 0.5 - Y
        base = oy * size
        for ox in range(x0, x1):
            d = math.hypot(ox + 0.5 - X, dy)
            if d >= R1:
                continue
            t = 0.0 if d <= R0 else (d - R0) / (R1 - R0)
            k = 1.0 - t
            k = k * k * (3.0 - 2.0 * k)
            v = int(round(alpha * k))
            if v:
                buf[base + ox] = v
    layers.append((buf, (col[0], col[1], col[2], 255)))


# ---------------------------------------------------------------------------
# 渲染
# ---------------------------------------------------------------------------

def render(size, small=False, dark=False):
    """渲染一幅。small=小尺寸专用几何，dark=深色任务栏（反相）版。"""
    ss = SS_BIG if size >= BIG_CUTOFF else SS
    S = lambda a: a * size

    plate = T_PLATE if dark else INK
    rim = T_RIM if dark else INK_RIM
    flange_c = T_FLANGE if dark else (STEEL_L if small else STEEL_TOP)
    # 小尺寸把喷管提亮一档：深底上 #3A4A5E 缩到 16px 会退成背景
    bell_c = (T_BELL if dark else (STEEL_TOP if small else STEEL_L))
    bell_sh = T_BELL_R if dark else STEEL_R
    lip_c = T_LIP if dark else AMBER
    f_out = T_FLAME if dark else CYAN
    f_mid = T_FLAME_IN if dark else CYAN_PALE
    f_core = T_FLAME if dark else FLAME_HOT

    layers = []

    # 底盘
    layers.append((rasterize(size, lambda x, y: rounded_rect_cov(x, y, size, BG_RADIUS * size),
                             None, ss), plate))

    # 内描边：16px 下差不多就是一个像素，只在 48 以上画
    if size >= 48:
        rw = size * 0.010

        def rim_cov(x, y):
            if not rounded_rect_cov(x, y, size, BG_RADIUS * size):
                return False
            return (not rounded_rect_cov(x + rw, y, size, BG_RADIUS * size) or
                    not rounded_rect_cov(x - rw, y, size, BG_RADIUS * size) or
                    not rounded_rect_cov(x, y + rw, size, BG_RADIUS * size) or
                    not rounded_rect_cov(x, y - rw, size, BG_RADIUS * size))

        layers.append((rasterize(size, rim_cov, None, ss), rim))

    # 尾焰光晕（先画，压在喷管后面）
    # 小尺寸不画：16px 下一层柔光会把"喷管 / 唇环 / 尾焰"三段全糊成一个亮斑，
    # 轮廓信息比"发光感"值钱得多。
    add_radial(layers, size, 0.5, 0.60, 0.02, 0.45, 0 if (dark or small) else 54, CYAN, ss)

    flange = FLANGE_S if small else FLANGE
    bell = BELL_S if small else BELL
    lip = LIP_S if small else LIP
    fobj = FLAME_OUT_S if small else FLAME_OUT
    fcore = FLAME_CORE_S if small else FLAME_CORE

    add_poly(layers, size, bell, bell_c, ss)
    if not small:
        add_poly(layers, size, BELL_SH, bell_sh, ss)

    # 冷却管：只在 96 以上画。小尺寸会变成噪点，把喷管糊成一团灰。
    if size >= 96:
        for i, (xa, xb) in enumerate(RIBS):
            col = T_RIBS if dark else (RIBS_LIGHT if i % 2 else RIBS_DARK)
            add_stroke(layers, size, [(xa, 0.312), (xb, 0.428)], 0.007, col, ss)

    add_poly(layers, size, flange, flange_c, ss)

    # 右侧受光棱：沿型线走一条亮边，喷管因此有圆柱感，而不是一块梯形色板
    if not small:
        edge = [(x - 0.010, y) for y, x in BELL_PROFILE]
        add_stroke(layers, size, edge, 0.012,
                   (STEEL_EDGE[0], STEEL_EDGE[1], STEEL_EDGE[2], 110 if not dark else 60), ss)

    # 出口环：给"喷口"一点金属厚度，否则那一圈只剩一根线
    if not small:
        add_poly(layers, size, BELL_MOUTH, MOUTH_C if not dark else T_MOUTH, ss)

    # 尾焰：外 → 中 → 芯
    add_poly(layers, size, fobj, f_out, ss)
    if not small:
        add_poly(layers, size, FLAME_MID, f_mid, ss)
    add_poly(layers, size, fcore, f_core, ss)

    # 激波钻石：超音速尾焰里周期性出现的亮结。
    # 芯是淡青、钻是纯白 —— 反过来（白芯+白钻）根本看不出来，第一版就是这么废掉的。
    if size >= 48 and not dark:
        for yy, r in ((0.512, 0.028), (0.596, 0.022), (0.672, 0.016)):
            add_poly(layers, size, [(0.500, yy - r), (0.500 + r * 0.85, yy),
                                    (0.500, yy + r), (0.500 - r * 0.85, yy)], DIAMOND, ss)

    # 出口唇环：压在尾焰根部，把"喷口"和"火焰"分开
    add_stroke(layers, size, lip, 0.030 if small else 0.024, lip_c, ss)

    return composite(size, layers)


# ---------------------------------------------------------------------------
# 输出
# ---------------------------------------------------------------------------

def encode_png(size, rgba):
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        raw += rgba[y * size * 4:(y + 1) * size * 4]

    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + \
            struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) + \
        chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b'')


def write_ico(path, frames):
    header = struct.pack('<HHH', 0, 1, len(frames))
    entries = b''
    offset = 6 + 16 * len(frames)
    for size, data in frames:
        entries += struct.pack('<BBBBHHII',
                               0 if size >= 256 else size, 0 if size >= 256 else size,
                               0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, 'wb') as f:
        f.write(header + entries + b''.join(d for _, d in frames))


def write_png_file(path, size, rgba):
    with open(path, 'wb') as f:
        f.write(encode_png(size, rgba))


def write_png_rgb(path, W, H, rgb):
    raw = bytearray()
    for y in range(H):
        raw.append(0)
        raw += rgb[y * W * 3:(y + 1) * W * 3]

    def chunk(tag, data):
        return struct.pack('>I', len(data)) + tag + data + \
            struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)

    ihdr = struct.pack('>IIBBBBB', W, H, 8, 2, 0, 0, 0)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
                chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
TRAY_SIZES = (16, 20, 24, 32, 40, 48, 64)
SMALL_CUTOFF = 24          # <= 这个尺寸用简化几何


def build_frames(sizes, dark=False):
    return [(sz, encode_png(sz, render(sz, small=(sz <= SMALL_CUTOFF), dark=dark)))
            for sz in sizes]


def make_verify(docs_dir):
    """两张验收图：尺寸阶梯（白底/深底各一排）与托盘深浅两版。"""
    sizes = [16, 24, 32, 48, 64]
    zoom = 4
    cw = ch = max(sizes) * zoom
    pad = 16
    W = len(sizes) * (cw + pad) + pad
    H = 2 * (ch + pad) + pad
    canvas = bytearray(W * H * 3)
    bgs = [(0xEE, 0xF1, 0xF5), (0x0A, 0x0E, 0x14)]
    for row, bg in enumerate(bgs):
        y0 = row * (ch + pad) + pad
        for y in range(y0, min(y0 + ch, H)):
            for x in range(W):
                o = (y * W + x) * 3
                canvas[o:o + 3] = bytes(bg)

    for col, sz in enumerate(sizes):
        rgba = render(sz, small=(sz <= SMALL_CUTOFF))
        ox = pad + col * (cw + pad)
        for row, bg in enumerate(bgs):
            oy = row * (ch + pad) + pad
            for dy in range(sz * zoom):
                for dx in range(sz * zoom):
                    si = ((dy // zoom) * sz + (dx // zoom)) * 4
                    a = rgba[si + 3]
                    X, Y = ox + dx, oy + dy
                    if X >= W or Y >= H or a == 0:
                        continue
                    o = (Y * W + X) * 3
                    canvas[o:o + 3] = bytes(
                        [int(rgba[si + c] * a / 255 + bg[c] * (255 - a) / 255) for c in range(3)])
    write_png_rgb(os.path.join(docs_dir, 'preview-icon-sizes.png'), W, H, canvas)
    print('  preview-icon-sizes.png')

    tsizes = [16, 20, 24, 32]
    tzoom = 4
    tcw = tch = max(tsizes) * tzoom
    tpad = 14
    TW = len(tsizes) * (tcw + tpad) + tpad
    TH = 2 * (tch + tpad) + tpad
    tc = bytearray(TW * TH * 3)
    tbgs = [(0x20, 0x20, 0x22), (0xF0, 0xF0, 0xF2)]
    for row, bg in enumerate(tbgs):
        y0 = row * (tch + tpad) + tpad
        for y in range(y0, min(y0 + tch, TH)):
            for x in range(TW):
                o = (y * TW + x) * 3
                tc[o:o + 3] = bytes(bg)

    for col, sz in enumerate(tsizes):
        for row in (0, 1):
            rgba = render(sz, small=(sz <= SMALL_CUTOFF), dark=(row == 0))
            ox = tpad + col * (tcw + tpad)
            oy = row * (tch + tpad) + tpad
            for dy in range(sz * tzoom):
                for dx in range(sz * tzoom):
                    si = ((dy // tzoom) * sz + (dx // tzoom)) * 4
                    a = rgba[si + 3]
                    X, Y = ox + dx, oy + dy
                    if X >= TW or Y >= TH or a == 0:
                        continue
                    o = (Y * TW + X) * 3
                    bg = tbgs[row]
                    tc[o:o + 3] = bytes(
                        [int(rgba[si + c] * a / 255 + bg[c] * (255 - a) / 255) for c in range(3)])
    write_png_rgb(os.path.join(docs_dir, 'preview-icon-tray.png'), TW, TH, tc)
    print('  preview-icon-tray.png')


def main():
    here = os.path.dirname(os.path.abspath(__file__))
    assets = os.path.join(here, '..', 'src', 'CoPul.App', 'assets')
    docs = os.path.join(here, '..', 'docs')
    os.makedirs(assets, exist_ok=True)

    print('主图标（R-01）...')
    ico = os.path.join(assets, 'copul.ico')
    write_ico(ico, build_frames(ICO_SIZES))
    print(f'  copul.ico  {os.path.getsize(ico)} B / {len(ICO_SIZES)} 帧')

    print('源图 ...')
    write_png_file(os.path.join(assets, 'copul-1024.png'), 1024, render(1024))
    write_png_file(os.path.join(assets, 'copul-256.png'), 256, render(256))
    print('  copul-1024.png / copul-256.png')

    print('托盘 ...')
    for dark, name in ((False, 'light'), (True, 'dark')):
        p = os.path.join(assets, f'copul-tray-{name}.ico')
        write_ico(p, build_frames(TRAY_SIZES, dark=dark))
        for sz in (16, 32):
            write_png_file(os.path.join(assets, f'copul-tray-{name}-{sz}.png'), sz,
                           render(sz, small=(sz <= SMALL_CUTOFF), dark=dark))
        print(f'  copul-tray-{name}.ico {os.path.getsize(p)} B')

    print('验收图 ...')
    make_verify(docs)
    print('完成。')


if __name__ == '__main__':
    main()
