"""
CoPul 图标概念稿 —— 四个互不相关的方向，出成一张对比版面供选型。

为什么重做：上一版（F 趋势切面）是"折线图"，它的问题是**谁都能用** ——
任何监控/股票/健康 App 换个颜色就是同一个图标。而且 16px 下折线团成一条虫。

这一轮换起点：不从"数据长什么样"出发，从**这个程序是什么**出发。
程序 = 一个悬浮在游戏之上的系统读数层。四个方向分别抓住它的四个侧面：

  1 切面核心  —— 它是"核心"（core vessel）：等轴测的舱体，顶盖打开露出琥珀核
  2 光圈虹膜  —— 它是"观察"：六叶快门，中心留一个透光的孔
  3 字母 C    —— 它是"名字"：开口环 + 缺口里的脉冲块（品牌识别优先）
  4 锁定准星  —— 它是"叠加层"：四角括号压在画面上，中心锁着一个读数

四者共享同一套底盘（深色圆角方块）与配色（青 / 琥珀 / 钢灰），
差异全部落在**几何构成**上，这样比较的是想法而不是配色。

只出 160（看形态）与 32/24/16（看还原度）两种，先在概念层面定方向，
定下来再走完整尺寸集 + 托盘深浅两版。
"""

import math
import os
import struct
import zlib

SS = 3

# ---------------------------------------------------------------------------
# 配色（沿用项目既有色：青 = AccentGpu 族，琥珀 = AccentNet 族）
# ---------------------------------------------------------------------------

INK       = (7, 11, 17, 255)          # 底盘
STEEL_TOP = (66, 86, 108, 255)
STEEL_L   = (40, 54, 70, 255)
STEEL_R   = (23, 32, 44, 255)
SEAM      = (8, 12, 18, 255)
CYAN      = (63, 200, 220, 255)
CYAN_DIM  = (30, 100, 114, 255)
AMBER     = (226, 166, 76, 255)
WHITE     = (232, 243, 250, 255)

SHEET_BG  = (0x0C, 0x0F, 0x14)
STRIP_D   = (0x20, 0x20, 0x22)
STRIP_L   = (0xF0, 0xF0, 0xF2)
LABEL_COL = (0x8F, 0xA3, 0xB8)


# ---------------------------------------------------------------------------
# 覆盖判定
# ---------------------------------------------------------------------------

def rounded_rect_cov(px, py, size, radius):
    half = size / 2.0
    dx = abs(px - half) - (half - radius)
    dy = abs(py - half) - (half - radius)
    ox, oy = max(dx, 0.0), max(dy, 0.0)
    outside = math.hypot(ox, oy)
    inside = min(max(dx, dy), 0.0)
    return -(outside + inside - radius) > 0


def disc_cov(px, py, cx, cy, r):
    return (px - cx) ** 2 + (py - cy) ** 2 <= r * r


def poly_cov(px, py, pts):
    """偶奇规则射线法。pts 已换算成像素坐标。"""
    inside = False
    n = len(pts)
    for i in range(n):
        x1, y1 = pts[i]
        x2, y2 = pts[(i + 1) % n]
        if (y1 > py) != (y2 > py):
            xin = x1 + (py - y1) * (x2 - x1) / (y2 - y1)
            if px < xin:
                inside = not inside
    return inside


def sector_cov(px, py, cx, cy, r0, r1, a0, a1):
    dx, dy = px - cx, py - cy
    d = math.hypot(dx, dy)
    if d < r0 or d > r1:
        return False
    if a1 - a0 >= 360.0:
        return True
    a = math.degrees(math.atan2(dy, dx)) % 360.0
    a0 %= 360.0
    a1 %= 360.0
    if a0 <= a1:
        return a0 <= a <= a1
    return a >= a0 or a <= a1


def stroke_cov(px, py, pts, half):
    """平头方笔：沿线段的局部坐标系判 |v|<=half 且 u∈[0,1]。"""
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

def rasterize(size, cov_fn, bbox=None):
    buf = bytearray(size * size)
    x0, y0, x1, y1 = bbox if bbox else (0, 0, size, size)
    x0 = max(0, int(x0)); y0 = max(0, int(y0))
    x1 = min(size, int(x1) + 1); y1 = min(size, int(y1) + 1)
    if x1 <= x0 or y1 <= y0:
        return buf
    step = 1.0 / SS
    n = SS * SS
    for oy in range(y0, y1):
        base = oy * size
        for ox in range(x0, x1):
            c = 0
            for sy in range(SS):
                yy = oy + (sy + 0.5) * step
                for sx in range(SS):
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


# ---------------------------------------------------------------------------
# 图元（归一化坐标 0..1）
# ---------------------------------------------------------------------------

def add_poly(layers, size, pts, col):
    P = [(x * size, y * size) for x, y in pts]
    xs = [p[0] for p in P]; ys = [p[1] for p in P]
    lay = rasterize(size, lambda x, y: poly_cov(x, y, P),
                    (min(xs) - 2, min(ys) - 2, max(xs) + 2, max(ys) + 2))
    layers.append((lay, col))


def add_disc(layers, size, cx, cy, r, col):
    X, Y, R = cx * size, cy * size, r * size
    lay = rasterize(size, lambda x, y: disc_cov(x, y, X, Y, R),
                    (X - R - 2, Y - R - 2, X + R + 2, Y + R + 2))
    layers.append((lay, col))


def add_sector(layers, size, cx, cy, r0, r1, a0, a1, col):
    X, Y = cx * size, cy * size
    R0, R1 = r0 * size, r1 * size
    lay = rasterize(size, lambda x, y: sector_cov(x, y, X, Y, R0, R1, a0, a1),
                    (X - R1 - 2, Y - R1 - 2, X + R1 + 2, Y + R1 + 2))
    layers.append((lay, col))


def add_stroke(layers, size, pts_norm, w, col):
    P = [(x * size, y * size) for x, y in pts_norm]
    half = w * size / 2.0
    xs = [p[0] for p in P]; ys = [p[1] for p in P]
    pad = half + 3
    lay = rasterize(size, lambda x, y: stroke_cov(x, y, P, half),
                    (min(xs) - pad, min(ys) - pad, max(xs) + pad, max(ys) + pad))
    layers.append((lay, col))


def add_plate(layers, size, radius=0.22, col=INK):
    layers.append((rasterize(size, lambda x, y: rounded_rect_cov(x, y, size, radius * size)), col))


def add_glow(layers, size, cx, cy, r, alpha):
    """软光晕：同心圆叠加。低频，16px 不会变成噪点。"""
    for k, mul in ((1.0, 0.55), (0.72, 0.8), (0.46, 1.0)):
        add_disc(layers, size, cx, cy, r * mul,
                 (CYAN[0], CYAN[1], CYAN[2], int(alpha * mul)))


def scale_about(pts, c, k):
    return [(c[0] + (x - c[0]) * k, c[1] + (y - c[1]) * k) for x, y in pts]


def centroid(pts):
    return (sum(p[0] for p in pts) / len(pts), sum(p[1] for p in pts) / len(pts))


# ---------------------------------------------------------------------------
# 概念 1：切面核心
#   等轴测立方体 = 正六边形切三刀。顶面换成"舱盖"：青色环 + 洞里一颗琥珀核。
#   为什么这样能立住：16px 下整块收敛成一个实心六边形 + 上部一个亮点，
#   轮廓与"折线虫"完全不同。
# ---------------------------------------------------------------------------

def concept_core(size):
    R = 0.345
    hx = 0.8660 * R
    cy = 0.5
    T = (0.5, cy - R)
    UR = (0.5 + hx, cy - R * 0.5)
    LR = (0.5 + hx, cy + R * 0.5)
    B = (0.5, cy + R)
    LL = (0.5 - hx, cy + R * 0.5)
    UL = (0.5 - hx, cy - R * 0.5)
    C = (0.5, cy)

    top = [T, UR, C, UL]
    left = [UL, C, B, LL]
    right = [UR, C, B, LR]
    tc = centroid(top)

    layers = []
    add_plate(layers, size)
    add_glow(layers, size, tc[0], tc[1] - 0.02, 0.44, 26)

    add_poly(layers, size, left, STEEL_L)
    add_poly(layers, size, right, STEEL_R)
    # 中缝：凹进去的那条棱，压一条更暗的线把它"折"出来
    add_stroke(layers, size, [C, B], 0.014, SEAM)

    add_poly(layers, size, top, CYAN)                    # 舱盖环
    hatch = scale_about(top, tc, 0.50)
    add_poly(layers, size, hatch, INK)                   # 舱口
    core = scale_about(top, tc, 0.24)
    add_poly(layers, size, core, AMBER)                  # 核

    # 顶面两条受光的棱（大尺寸才有意义，小尺寸被 AA 吃掉也无所谓）
    add_stroke(layers, size, [T, UR], 0.012, (WHITE[0], WHITE[1], WHITE[2], 110))
    add_stroke(layers, size, [T, UL], 0.012, (WHITE[0], WHITE[1], WHITE[2], 70))
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 概念 2：光圈虹膜
#   六叶快门：环形切成六段，每段带一点角度错位（真虹膜的叶片是叠着的）。
#   中心留一个透光的孔 —— 亮青，孔里一颗小琥珀。
# ---------------------------------------------------------------------------

def concept_iris(size):
    layers = []
    add_plate(layers, size)
    c = (0.5, 0.5)
    add_glow(layers, size, c[0], c[1], 0.42, 22)

    blades = 6
    gap = 7.0
    for i in range(blades):
        a0 = -90.0 + i * (360.0 / blades) + gap * 0.5
        a1 = a0 + 360.0 / blades - gap
        # 叶片外缘亮、内缘暗：一条渐变做不了，就用两段扇形叠出来
        add_sector(layers, size, c[0], c[1], 0.145, 0.405, a0, a1, STEEL_L)
        add_sector(layers, size, c[0], c[1], 0.145, 0.245, a0, a1, STEEL_R)
        # 每片的外缘高光
        add_sector(layers, size, c[0], c[1], 0.385, 0.405, a0 + 1.0, a1 - 1.0, STEEL_TOP)

    add_disc(layers, size, c[0], c[1], 0.145, CYAN)
    add_disc(layers, size, c[0], c[1], 0.062, AMBER)
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 概念 3：字母 C · 脉冲环
#   开口环（缺口朝右上），缺口里塞一块琥珀楔 —— 像仪表盘的红区，也像"漏出来的一拍"。
#   好处是它同时是字母，缩到 16px 也认得出 C。
# ---------------------------------------------------------------------------

def concept_monogram(size):
    layers = []
    add_plate(layers, size)
    c = (0.5, 0.5)
    add_glow(layers, size, c[0], c[1], 0.40, 16)

    # 环：留 74° 缺口，缺口中心朝 -22°（右上偏右）
    start = -22.0 + 37.0
    end = -22.0 - 37.0
    add_sector(layers, size, c[0], c[1], 0.175, 0.345, start, end, CYAN)
    # 内侧压一道暗边，让环有厚度
    add_sector(layers, size, c[0], c[1], 0.300, 0.345, start, end, CYAN_DIM)

    # 缺口里的楔块（比缺口矮一点，别把开口堵死）
    a0, a1 = -12.0, 6.0
    add_sector(layers, size, c[0], c[1], 0.215, 0.325, a0, a1, AMBER)

    # 中心读数点
    add_disc(layers, size, c[0] + 0.02, c[1] + 0.03, 0.058, WHITE)
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 概念 4：锁定准星
#   四角括号压在画面上 + 中心一个琥珀点。最直接地表达"这是一层叠上去的东西"。
#   左上角那根换成青色：不对称，视线有落点。
# ---------------------------------------------------------------------------

def concept_reticle(size):
    layers = []
    add_plate(layers, size)
    c = (0.5, 0.5)
    add_glow(layers, size, c[0], c[1], 0.34, 18)

    lo, hi = 0.185, 0.815
    arm = 0.165
    w = 0.052
    corners = [
        ((lo, hi - arm), (lo, hi), (lo + arm, hi)),   # 左上
        ((hi - arm, hi), (hi, hi), (hi, hi - arm)),   # 右上
        ((hi, lo + arm), (hi, lo), (hi - arm, lo)),   # 右下
        ((lo + arm, lo), (lo, lo), (lo, lo + arm)),   # 左下
    ]
    for i, path in enumerate(corners):
        col = CYAN if i == 0 else STEEL_TOP
        add_stroke(layers, size, list(path), w, col)

    add_disc(layers, size, c[0], c[1], 0.072, AMBER)
    add_disc(layers, size, c[0], c[1], 0.030, INK)
    return composite(size, layers)


def capsule_cov(px, py, x0, y0, x1, y1):
    if px < x0 or px > x1 or py < y0 or py > y1:
        return False
    r = (x1 - x0) / 2.0
    mx = (x0 + x1) / 2.0
    if py < y0 + r:
        return (px - mx) ** 2 + (py - (y0 + r)) ** 2 <= r * r
    if py > y1 - r:
        return (px - mx) ** 2 + (py - (y1 - r)) ** 2 <= r * r
    return True


# ---------------------------------------------------------------------------
# 概念 5：机甲面罩
#   整块一个"护目镜"。上缘斜下收，下缘弧着收 —— 轮廓本身就不对称。
#   玻璃里一条亮缝，缝分成三段，最右一段是琥珀（像仪表红区）。
#   16px 收敛成"暗六边形 + 一道亮横杠"，与任何图表都不像。
# ---------------------------------------------------------------------------

def concept_visor(size):
    layers = []
    add_plate(layers, size)
    add_glow(layers, size, 0.5, 0.44, 0.46, 18)

    outer = [(0.135, 0.335), (0.50, 0.245), (0.865, 0.335),
             (0.835, 0.63), (0.50, 0.785), (0.165, 0.63)]
    add_poly(layers, size, outer, STEEL_L)
    # 上半块压暗一点，做出"玻璃罩"的曲面
    upper = [(0.135, 0.335), (0.50, 0.245), (0.865, 0.335),
             (0.845, 0.44), (0.50, 0.36), (0.155, 0.44)]
    add_poly(layers, size, upper, STEEL_R)

    # 亮缝：左右两端各有一个斜切角，中间两处断口
    slit_y0, slit_y1 = 0.452, 0.556
    segs = [(0.215, 0.36), (0.395, 0.545), (0.58, 0.775)]
    for i, (sx0, sx1) in enumerate(segs):
        col = AMBER if i == len(segs) - 1 else CYAN
        quad = [(sx0, slit_y0 + 0.012), (sx1 - 0.006, slit_y0),
                (sx1 + 0.004, slit_y1 - 0.012), (sx0 + 0.010, slit_y1)]
        add_poly(layers, size, quad, col)

    add_stroke(layers, size, [(0.20, 0.30), (0.50, 0.222), (0.80, 0.30)],
               0.014, (WHITE[0], WHITE[1], WHITE[2], 60))
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 概念 6：能量罐
#   竖着的玻璃罐，里面一罐青色读数，顶上一圈琥珀封盖，右侧三格刻度。
#   语义最直白：这是一个"容器 + 液位"，也就是"系统状态的读数"。
#   16px 收敛成一根竖条（下半亮、上半暗），横竖都立得住。
# ---------------------------------------------------------------------------

def concept_vessel(size):
    layers = []
    add_plate(layers, size)
    add_glow(layers, size, 0.5, 0.62, 0.36, 20)

    x0, x1, y0, y1 = 0.285, 0.715, 0.135, 0.865
    S = lambda a: a * size
    level = 0.475

    def shell(x, y):
        return capsule_cov(x, y, S(x0), S(y0), S(x1), S(y1))

    def liquid(x, y):
        return capsule_cov(x, y, S(x0 + 0.028), S(y0), S(x1 - 0.028), S(y1)) and y >= S(level)

    def cap(x, y):
        return capsule_cov(x, y, S(x0), S(y0), S(x1), S(y1)) and y <= S(y0 + 0.135)

    box = (S(x0) - 2, S(y0) - 2, S(x1) + 2, S(y1) + 2)
    layers.append((rasterize(size, shell, box), STEEL_R))
    layers.append((rasterize(size, liquid, box), CYAN))
    layers.append((rasterize(size, cap, box), AMBER))

    # 刻度：右侧三格
    for k in range(3):
        yy = 0.335 + k * 0.145
        add_stroke(layers, size, [(0.565, yy), (0.675, yy)], 0.020,
                   (WHITE[0], WHITE[1], WHITE[2], 150))

    # 左侧高光
    add_stroke(layers, size, [(0.345, 0.24), (0.345, 0.78)], 0.016,
               (WHITE[0], WHITE[1], WHITE[2], 90))
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 概念 7：切口方块
#   一整块实心方块，右上角被切掉一个三角，切口里露出内部的核心。
#   极简到只剩"块 + 一个缺口"，缺口是唯一的信息点 —— 这是四个里最大胆的构图。
# ---------------------------------------------------------------------------

def concept_cut(size):
    layers = []
    add_plate(layers, size)
    add_glow(layers, size, 0.66, 0.30, 0.34, 24)

    body = [(0.155, 0.155), (0.845, 0.155), (0.845, 0.845), (0.155, 0.845)]
    add_poly(layers, size, body, STEEL_L)

    # 顶面与左侧受光（两条窄带，做出"块"的厚度感）
    add_poly(layers, size, [(0.155, 0.155), (0.845, 0.155), (0.845, 0.225), (0.155, 0.225)], STEEL_TOP)
    add_poly(layers, size, [(0.155, 0.225), (0.225, 0.225), (0.225, 0.845), (0.155, 0.845)], STEEL_R)

    # 缺口：直角三角，从右上角咬进去
    bite = [(0.845, 0.235), (0.845, 0.545), (0.535, 0.235)]
    add_poly(layers, size, bite, INK)
    # 切口里的核心：比缺口小一圈的青三角
    inner = [(0.800, 0.300), (0.800, 0.500), (0.600, 0.300)]
    add_poly(layers, size, inner, CYAN)

    # 对角那颗琥珀：把视线从缺口牵到另一头
    add_disc(layers, size, 0.305, 0.700, 0.075, AMBER)
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 概念 8：叠层斜条
#   三片平行的斜板，最前面一片是亮的 —— "信息一层层叠在画面上"。
#   不画任何数据，靠"层"和"斜"说话。16px 是三道斜带，辨识度极高。
# ---------------------------------------------------------------------------

def concept_beams(size):
    layers = []
    add_plate(layers, size)

    specs = [(0.300, STEEL_R), (0.455, STEEL_L), (0.610, CYAN)]
    for y, col in specs:
        quad = [(0.235, y), (0.845, y), (0.765, y + 0.115), (0.155, y + 0.115)]
        add_poly(layers, size, quad, col)
        # 板的上棱：一亮一暗，给厚度
        add_stroke(layers, size, [(0.235, y), (0.845, y)], 0.012,
                   (WHITE[0], WHITE[1], WHITE[2], 70 if col is not CYAN else 130))

    # 亮板上的读数刻度
    for k in range(3):
        xx = 0.30 + k * 0.145
        add_stroke(layers, size, [(xx, 0.638), (xx, 0.700)], 0.022, SEAM)
    return composite(size, layers)


CONCEPTS = [
    ("1 切面核心", concept_core),
    ("2 光圈虹膜", concept_iris),
    ("3 字母C",    concept_monogram),
    ("4 锁定准星", concept_reticle),
]

CONCEPTS2 = [
    ("5 机甲面罩", concept_visor),
    ("6 能量罐",   concept_vessel),
    ("7 切口方块", concept_cut),
    ("8 叠层斜条", concept_beams),
]


# ---------------------------------------------------------------------------
# 概念 9：仪表指针
#   一个开了口的速度表：外圈暗钢、已走完的那段是青、一根指针甩向右上、中心琥珀轴。
#   它和"折线"是两套完全不同的语汇：折线说的是"历史"，指针说的是"此刻"。
# ---------------------------------------------------------------------------

def concept_gauge(size):
    layers = []
    add_plate(layers, size)
    c = (0.5, 0.545)
    add_glow(layers, size, c[0], c[1], 0.40, 16)

    A0, A1 = 140.0, 40.0          # 从 140° 顺时针走到 40°（开口朝下）
    add_sector(layers, size, c[0], c[1], 0.255, 0.335, A0, A1, STEEL_R)
    add_sector(layers, size, c[0], c[1], 0.255, 0.300, A0, A1, (18, 26, 36, 255))
    # 值弧：走到 -30°（约 76%）
    add_sector(layers, size, c[0], c[1], 0.262, 0.328, A0, -30.0, CYAN)

    # 刻度
    for k in range(7):
        a = math.radians(140.0 - k * (100.0 / 6))
        add_stroke(layers, size,
                   [(c[0] + math.cos(a) * 0.205, c[1] - math.sin(a) * 0.205),
                    (c[0] + math.cos(a) * 0.245, c[1] - math.sin(a) * 0.245)],
                   0.017, (WHITE[0], WHITE[1], WHITE[2], 110))

    # 指针：从轴心甩到 -28°，末端略收
    na = math.radians(-28.0)
    tip = (c[0] + math.cos(na) * 0.245, c[1] - math.sin(na) * 0.245)
    back = (c[0] - math.cos(na) * 0.070, c[1] + math.sin(na) * 0.070)
    add_stroke(layers, size, [back, tip], 0.052, WHITE)

    add_disc(layers, size, c[0], c[1], 0.062, AMBER)
    add_disc(layers, size, c[0], c[1], 0.026, INK)
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 概念 10：芯片
#   一颗封装的芯片，四角切边、两侧引脚，正中一块斜置的青色核心，左上角一颗琥珀定位点。
#   最"工业"的一个：它不比喻任何东西，它就是这个程序跑在什么上面。
# ---------------------------------------------------------------------------

def concept_chip(size):
    layers = []
    add_plate(layers, size)
    add_glow(layers, size, 0.5, 0.5, 0.36, 16)

    # 引脚（先画，藏在芯片下面）
    for k in range(3):
        yy = 0.315 + k * 0.185
        add_poly(layers, size, [(0.145, yy), (0.245, yy), (0.245, yy + 0.062), (0.145, yy + 0.062)],
                 STEEL_TOP if k != 0 else (96, 104, 116, 255))
        add_poly(layers, size, [(0.755, yy), (0.855, yy), (0.855, yy + 0.062), (0.755, yy + 0.062)],
                 AMBER if k == 0 else STEEL_TOP)

    # 封装体
    body = [(0.245, 0.235), (0.755, 0.235), (0.755, 0.765), (0.245, 0.765)]
    add_poly(layers, size, body, STEEL_L)
    add_poly(layers, size, [(0.245, 0.235), (0.755, 0.235), (0.755, 0.300), (0.245, 0.300)], STEEL_TOP)
    add_poly(layers, size, [(0.245, 0.700), (0.755, 0.700), (0.755, 0.765), (0.245, 0.765)],
             (16, 23, 32, 255))

    # 芯片核心：斜置方
    add_poly(layers, size, [(0.500, 0.372), (0.628, 0.500), (0.500, 0.628), (0.372, 0.500)], CYAN)
    add_poly(layers, size, [(0.500, 0.430), (0.570, 0.500), (0.500, 0.570), (0.430, 0.500)], WHITE)

    add_disc(layers, size, 0.295, 0.290, 0.026, AMBER)
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 概念 11：压力罐
#   立式罐体：上下两道钢箍、中间一条竖窗，窗里青色液位停在六成、液面上一条亮边。
#   "corevessel" 的直接画像 —— 一个装着核心的容器，而不是一个装着数据的图表。
# ---------------------------------------------------------------------------

def concept_canister(size):
    layers = []
    add_plate(layers, size)
    add_glow(layers, size, 0.5, 0.60, 0.34, 18)

    add_poly(layers, size, [(0.315, 0.135), (0.685, 0.135), (0.685, 0.865), (0.315, 0.865)], STEEL_L)
    add_poly(layers, size, [(0.315, 0.135), (0.685, 0.135), (0.685, 0.215), (0.315, 0.215)], STEEL_TOP)
    add_poly(layers, size, [(0.315, 0.785), (0.685, 0.785), (0.685, 0.865), (0.315, 0.865)],
             (16, 23, 32, 255))

    # 观察窗（内凹）
    add_poly(layers, size, [(0.360, 0.255), (0.640, 0.255), (0.640, 0.745), (0.360, 0.745)],
             (9, 15, 21, 255))
    # 液位
    add_poly(layers, size, [(0.360, 0.452), (0.640, 0.452), (0.640, 0.745), (0.360, 0.745)], CYAN)
    # 液面亮边
    add_stroke(layers, size, [(0.362, 0.452), (0.638, 0.452)], 0.020, WHITE)

    # 顶部琥珀指示灯
    add_disc(layers, size, 0.500, 0.175, 0.040, AMBER)
    # 罐体左侧受光
    add_stroke(layers, size, [(0.345, 0.235), (0.345, 0.775)], 0.018,
               (WHITE[0], WHITE[1], WHITE[2], 80))
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 概念 12：棱晶
#   一枚斜置的四棱晶：四个面四个明度，正中一个小洞透出青，洞里一颗琥珀。
#   纯几何、无叙事，但轮廓（菱形）在所有系统托盘里都很扎眼 —— 它不像任何常见图标。
# ---------------------------------------------------------------------------

def concept_prism(size):
    layers = []
    add_plate(layers, size)
    c = (0.5, 0.5)
    add_glow(layers, size, c[0], c[1], 0.36, 20)

    T = (0.5, 0.135)
    R = (0.865, 0.5)
    B = (0.5, 0.865)
    L = (0.135, 0.5)
    add_poly(layers, size, [T, R, c], STEEL_L)          # 右上
    add_poly(layers, size, [T, c, L], STEEL_TOP)        # 左上
    add_poly(layers, size, [R, B, c], (17, 24, 33, 255))  # 右下
    add_poly(layers, size, [L, c, B], STEEL_R)          # 左下

    # 中洞 + 核心
    add_poly(layers, size, [(0.5, 0.330), (0.670, 0.5), (0.5, 0.670), (0.330, 0.5)], INK)
    add_poly(layers, size, [(0.5, 0.392), (0.608, 0.5), (0.5, 0.608), (0.392, 0.5)], CYAN)
    add_disc(layers, size, 0.5, 0.5, 0.040, AMBER)

    # 上棱受光
    add_stroke(layers, size, [T, R], 0.014, (WHITE[0], WHITE[1], WHITE[2], 120))
    add_stroke(layers, size, [T, L], 0.014, (WHITE[0], WHITE[1], WHITE[2], 90))
    return composite(size, layers)


CONCEPTS3 = [
    ("9 仪表指针",  concept_gauge),
    ("10 芯片",     concept_chip),
    ("11 压力罐",   concept_canister),
    ("12 棱晶",     concept_prism),
]

ALL_SETS = {1: CONCEPTS, 2: CONCEPTS2, 3: CONCEPTS3}


# ---------------------------------------------------------------------------
# 概念 13：推力喷管
#   上半个钟形喷管，下半个纯白芯 + 青边的等离子束，束尖拖到尾。
#   竖构图，在清一色横构图/圆形图标里最扎眼；16px 下是一条亮竖条，最不容易糊。
# ---------------------------------------------------------------------------

def concept_plume(size):
    layers = []
    add_plate(layers, size)
    add_glow(layers, size, 0.5, 0.62, 0.42, 34)

    bell = [(0.418, 0.120), (0.582, 0.120), (0.700, 0.392), (0.300, 0.392)]
    add_poly(layers, size, bell, STEEL_L)
    add_poly(layers, size, [(0.418, 0.120), (0.500, 0.120), (0.500, 0.392), (0.300, 0.392)], STEEL_R)
    add_poly(layers, size, [(0.418, 0.120), (0.582, 0.120), (0.582, 0.172), (0.418, 0.172)], STEEL_TOP)
    add_stroke(layers, size, [(0.300, 0.392), (0.700, 0.392)], 0.028, AMBER)

    outer = [(0.330, 0.410), (0.670, 0.410), (0.560, 0.742), (0.500, 0.862), (0.440, 0.742)]
    add_poly(layers, size, outer, CYAN)
    core = [(0.418, 0.410), (0.582, 0.410), (0.528, 0.700), (0.500, 0.782), (0.472, 0.700)]
    add_poly(layers, size, core, WHITE)
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 14：三角翼
#   俯视的三角翼，机头朝上，尾缘中间开一个凹口，座舱是一小片青，尾喷口一点琥珀。
#   轮廓本身就是一整个锐角三角形 —— 缩放不丢形。
# ---------------------------------------------------------------------------

def concept_delta(size):
    layers = []
    add_plate(layers, size)
    add_glow(layers, size, 0.5, 0.46, 0.40, 20)

    left = [(0.500, 0.135), (0.500, 0.612), (0.352, 0.720), (0.145, 0.720)]
    right = [(0.500, 0.135), (0.500, 0.612), (0.648, 0.720), (0.855, 0.720)]
    add_poly(layers, size, left, STEEL_L)
    add_poly(layers, size, right, STEEL_TOP)

    # 座舱
    add_poly(layers, size, [(0.500, 0.238), (0.552, 0.352), (0.500, 0.430), (0.448, 0.352)], CYAN)
    # 尾喷口
    add_poly(layers, size, [(0.500, 0.620), (0.560, 0.706), (0.500, 0.706), (0.440, 0.706)], AMBER)
    # 前缘高光
    add_stroke(layers, size, [(0.500, 0.150), (0.198, 0.712)], 0.016,
               (WHITE[0], WHITE[1], WHITE[2], 70))
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 15：雷达
#   三层同心环 + 一道扫掠楔 + 两个琥珀回波。环与楔全是"监测"的原型，
#   而且没有任何一个方向（旋转对称被那一道楔打破了）。
# ---------------------------------------------------------------------------

def concept_radar(size):
    layers = []
    add_plate(layers, size)
    c = (0.5, 0.520)
    add_glow(layers, size, c[0], c[1], 0.40, 18)

    for r in (0.175, 0.255, 0.335):
        add_sector(layers, size, c[0], c[1], r - 0.010, r, 0.0, 360.0, CYAN_DIM)

    add_sector(layers, size, c[0], c[1], 0.0, 0.335, -64.0, -26.0, (30, 100, 114, 190))
    add_sector(layers, size, c[0], c[1], 0.0, 0.335, -64.0, -60.0, CYAN)

    add_disc(layers, size, 0.640, 0.395, 0.032, AMBER)
    add_disc(layers, size, 0.395, 0.660, 0.023, AMBER)
    add_disc(layers, size, c[0], c[1], 0.030, WHITE)
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 16：机械镜头
#   镜头筒（外圈带八道缺口）+ 青色光圈 + 暗瞳 + 一道镜面反光。
#   最"看"的一个：它把"监测"直接画成一只眼睛，但用的是光学器件的语言。
# ---------------------------------------------------------------------------

def concept_lens(size):
    layers = []
    add_plate(layers, size)
    c = (0.5, 0.5)
    add_glow(layers, size, c[0], c[1], 0.38, 20)

    add_sector(layers, size, c[0], c[1], 0.205, 0.350, 0.0, 360.0, STEEL_L)
    add_sector(layers, size, c[0], c[1], 0.320, 0.350, 0.0, 360.0, STEEL_TOP)
    # 筒身八道缺口
    for k in range(8):
        a = k * 45.0
        add_sector(layers, size, c[0], c[1], 0.268, 0.352, a - 3.5, a + 3.5, (12, 18, 26, 255))

    add_sector(layers, size, c[0], c[1], 0.150, 0.205, 0.0, 360.0, (14, 21, 30, 255))
    add_disc(layers, size, c[0], c[1], 0.150, CYAN)
    add_disc(layers, size, c[0], c[1], 0.078, (10, 16, 23, 255))
    add_disc(layers, size, c[0], c[1], 0.030, WHITE)
    # 镜面反光
    add_sector(layers, size, c[0], c[1], 0.160, 0.196, 196.0, 246.0,
               (WHITE[0], WHITE[1], WHITE[2], 150))
    return composite(size, layers)


CONCEPTS4 = [
    ("13 推力喷管", concept_plume),
    ("14 三角翼",   concept_delta),
    ("15 雷达",     concept_radar),
    ("16 机械镜头", concept_lens),
]

ALL_SETS[4] = CONCEPTS4


# ---------------------------------------------------------------------------
# 输出
# ---------------------------------------------------------------------------

def write_png_rgb(path, W, H, rgb):
    raw = bytearray()
    for y in range(H):
        raw.append(0)
        raw += rgb[y * W * 3:(y + 1) * W * 3]

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack('>IIBBBBB', W, H, 8, 2, 0, 0, 0)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
                chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


def write_png_rgba(path, size, rgba):
    raw = bytearray()
    for y in range(size):
        raw.append(0)
        raw += rgba[y * size * 4:(y + 1) * size * 4]

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
                chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


# 3x5 点阵数字，用来在版面上标号
DIGITS = {
    '1': ['010', '110', '010', '010', '111'],
    '2': ['111', '001', '111', '100', '111'],
    '3': ['111', '001', '111', '001', '111'],
    '4': ['101', '101', '111', '001', '001'],
    '5': ['111', '100', '111', '001', '111'],
    '6': ['111', '100', '111', '101', '111'],
    '7': ['111', '001', '010', '010', '010'],
    '8': ['111', '101', '111', '101', '111'],
    '9': ['111', '101', '111', '001', '111'],
    '0': ['111', '101', '101', '101', '111'],
}


def blit_rgba(canvas, W, H, rgba, size, ox, oy, bg=None):
    """把 RGBA 贴到 RGB 画布上；bg 给定则先铺满一块 size×size 的纯色底。"""
    if bg is not None:
        for y in range(size):
            row = (oy + y) * W + ox
            if oy + y < 0 or oy + y >= H:
                continue
            for x in range(size):
                if ox + x < 0 or ox + x >= W:
                    continue
                o = (row + ox + x) * 3
                canvas[o] = bg[0]; canvas[o + 1] = bg[1]; canvas[o + 2] = bg[2]
    for y in range(size):
        Y = oy + y
        if Y < 0 or Y >= H:
            continue
        for x in range(size):
            X = ox + x
            if X < 0 or X >= W:
                continue
            si = (y * size + x) * 4
            a = rgba[si + 3]
            if a == 0:
                continue
            o = (Y * W + X) * 3
            for k in range(3):
                canvas[o + k] = int(rgba[si + k] * a / 255 + canvas[o + k] * (255 - a) / 255)


def draw_digit(canvas, W, H, ox, oy, text, scale, col):
    """点阵数字（支持多位）。scale 是每一格放大的像素数。"""
    cx = ox
    for ch in text:
        bmp = DIGITS.get(ch)
        if bmp is None:
            cx += 4 * scale
            continue
        for ry, row in enumerate(bmp):
            for rx, bit in enumerate(row):
                if bit != '1':
                    continue
                for yy in range(scale):
                    for xx in range(scale):
                        X, Y = cx + rx * scale + xx, oy + ry * scale + yy
                        if 0 <= X < W and 0 <= Y < H:
                            o = (Y * W + X) * 3
                            canvas[o] = col[0]; canvas[o + 1] = col[1]; canvas[o + 2] = col[2]
        cx += 4 * scale


def mk_contact_sheet():
    """12 个概念一屏看完：4 列 × 3 行，每格 96px 图标 + 编号。"""
    here = os.path.dirname(os.path.abspath(__file__))
    docs = os.path.join(here, '..', 'docs')

    flat = []
    for r in sorted(ALL_SETS):
        flat.extend(ALL_SETS[r])

    COLS = 4
    ROWS = (len(flat) + COLS - 1) // COLS
    CELL, ICON, PAD = 176, 96, 18
    W = PAD * 2 + CELL * COLS
    H = PAD * 2 + CELL * ROWS
    canvas = bytearray(W * H * 3)
    for i in range(W * H):
        canvas[i * 3] = SHEET_BG[0]
        canvas[i * 3 + 1] = SHEET_BG[1]
        canvas[i * 3 + 2] = SHEET_BG[2]

    for n, (title, fn) in enumerate(flat):
        r, c = divmod(n, COLS)
        x0 = PAD + c * CELL
        y0 = PAD + r * CELL
        draw_digit(canvas, W, H, x0 + 8, y0 + 6, str(n + 1), 2, LABEL_COL)
        big = fn(ICON)
        blit_rgba(canvas, W, H, big, ICON, x0 + (CELL - ICON) // 2 - 4,
                  y0 + (CELL - ICON) // 2 + 4)

    path = os.path.join(docs, 'icon-all.png')
    write_png_rgb(path, W, H, canvas)
    print('wrote', path, W, 'x', H)


def main():
    import sys
    arg = sys.argv[1] if len(sys.argv) > 1 else '1'

    if arg == 'all':
        mk_contact_sheet()
        return

    round_no = int(arg) if arg in ('1', '2', '3', '4') else 1
    set_ = ALL_SETS[round_no]
    tag = 'icon-concepts' if round_no == 1 else f'icon-concepts{round_no}'

    here = os.path.dirname(os.path.abspath(__file__))
    docs = os.path.join(here, '..', 'docs')
    out = os.path.join(docs, 'icon-candidates' if round_no == 1 else f'icon-candidates{round_no}')
    os.makedirs(out, exist_ok=True)

    ICON = 160
    CW = 200
    PAD = 20
    LABEL_H = 18
    STRIP_H = 42
    SMALLS = (32, 24, 16)

    W = PAD + CW * len(set_)
    H = PAD + LABEL_H + ICON + 12 + STRIP_H + 6 + STRIP_H + PAD
    canvas = bytearray(W * H * 3)
    for i in range(W * H):
        canvas[i * 3] = SHEET_BG[0]
        canvas[i * 3 + 1] = SHEET_BG[1]
        canvas[i * 3 + 2] = SHEET_BG[2]

    for idx, (title, fn) in enumerate(set_):
        x0 = PAD + idx * CW
        cx = x0 + CW // 2
        draw_digit(canvas, W, H, x0 + 6, PAD - 2, str(idx + 1 + (round_no - 1) * 4), 2, LABEL_COL)

        big = fn(ICON)
        write_png_rgba(os.path.join(out, f'c{idx + 1}-160.png'), ICON, big)
        blit_rgba(canvas, W, H, big, ICON, cx - ICON // 2, PAD + LABEL_H)

        for row, bg in enumerate((STRIP_D, STRIP_L)):
            sy = PAD + LABEL_H + ICON + 12 + row * (STRIP_H + 6)
            strip_w = CW - 24
            for y in range(sy, sy + STRIP_H):
                for x in range(x0 + 12, x0 + 12 + strip_w):
                    o = (y * W + x) * 3
                    canvas[o] = bg[0]; canvas[o + 1] = bg[1]; canvas[o + 2] = bg[2]
            total = sum(SMALLS) + 20 * (len(SMALLS) - 1)
            sx = cx - total // 2
            for sz in SMALLS:
                small = fn(sz)
                write_png_rgba(os.path.join(out, f'c{idx + 1}-{sz}.png'), sz, small)
                blit_rgba(canvas, W, H, small, sz, sx, sy + (STRIP_H - sz) // 2)
                sx += sz + 20

    path = os.path.join(docs, tag + '.png')
    write_png_rgb(path, W, H, canvas)
    print('wrote', path, W, 'x', H)


if __name__ == '__main__':
    main()
