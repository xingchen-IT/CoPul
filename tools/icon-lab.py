"""
CoPul 芯脉 —— 图标候选稿渲染器（设计探索用，不写回 assets）。

与 make-icon.py 的关系：
  · 复用它的 PNG / ICO 编码与降采样（那部分是标准库手写的，已验证）；
  · 几何与配色完全独立 —— 本文件只负责"看效果"，定稿后再把geometry搬进 make-icon.py。

为什么先出候选再定稿：
  图标是 16px 起要能认出来的东西，肉眼在 256px 上的判断经常不成立。
  这里每张对照图同时给 256 与 64/48/32/24/16 的 1:1 排布，浅底深底各一行，
  一眼能看出"哪个方案在小尺寸下先糊"。

用法：
  python tools/icon-lab.py            # 出三套方案 + 一张配色对照到 docs/icon-lab/
"""

import importlib.util
import math
import os
import struct
import zlib

_HERE = os.path.dirname(os.path.abspath(__file__))
_spec = importlib.util.spec_from_file_location('makeicon', os.path.join(_HERE, 'make-icon.py'))
mk = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(mk)

MASTER = 256
SS = 3          # 3× 超采样 = 768² 采样点/张，够平了，再高纯烧时间

# ---------------------------------------------------------------------------
# 几何基元
# ---------------------------------------------------------------------------

def sdf_round_rect(px, py, cx, cy, hx, hy, r):
    """圆角矩形有符号距离（<0 内部）。"""
    dx = abs(px - cx) - (hx - r)
    dy = abs(py - cy) - (hy - r)
    ox, oy = max(dx, 0.0), max(dy, 0.0)
    return math.hypot(ox, oy) + min(max(dx, dy), 0.0) - r


def seg_dist(px, py, ax, ay, bx, by):
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    L2 = vx * vx + vy * vy
    t = 0.0 if L2 == 0 else max(0.0, min(1.0, (wx * vx + wy * vy) / L2))
    return math.hypot(px - (ax + t * vx), py - (ay + t * vy))


def in_polyline(px, py, pts, half, box):
    """折线笔画：到任一线段距离 <= half。box 是外扩后的包围盒，先做粗筛。"""
    x0, y0, x1, y1 = box
    if px < x0 or px > x1 or py < y0 or py > y1:
        return False
    for i in range(len(pts) - 1):
        if seg_dist(px, py, pts[i][0], pts[i][1], pts[i + 1][0], pts[i + 1][1]) <= half:
            return True
    return False


def polyline_box(pts, half):
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    return (min(xs) - half, min(ys) - half, max(xs) + half, max(ys) + half)


def in_square(px, py, cx, cy, half):
    return abs(px - cx) <= half and abs(py - cy) <= half


def in_rect(px, py, cx, cy, hx, hy):
    return abs(px - cx) <= hx and abs(py - cy) <= hy


def in_disc(px, py, cx, cy, r):
    return (px - cx) ** 2 + (py - cy) ** 2 <= r * r


# ---------------------------------------------------------------------------
# 波形：数字方波（本轮的核心里念）
#
# 旧的 WAVE 是标准心电 QRS：基线 → 陡峰 → 深谷 → 回基线。
# 情绪上它是"心跳"，落在健康/医疗那一族。
# 换成**方波**之后同样是"一条带峰谷的线"，但直角、平台、阶跃三重语汇
# 全部指向"数字信号 / 时钟 / 示波器"，这才是科技感真正的来源。
# ---------------------------------------------------------------------------

# 一轮完整方波：低位平台 → 上升 → 高位平台 → 深位平台（负脉冲）→ 回低位
SQUARE = [
    (0.185, 0.585),
    (0.325, 0.585),   # 低位平台
    (0.325, 0.315),   # 上升沿
    (0.450, 0.315),   # 高位平台
    (0.450, 0.740),   # 下降沿（过冲成负脉冲）
    (0.575, 0.740),   # 深位平台
    (0.575, 0.585),   # 回升
    (0.815, 0.585),   # 收尾低位平台
]

# 小尺寸：平台段相对更长、竖直段更短，16px 下才不会糊成一块
SQUARE_SMALL = [
    (0.195, 0.590),
    (0.350, 0.590),
    (0.350, 0.300),
    (0.480, 0.300),
    (0.480, 0.755),
    (0.610, 0.755),
    (0.610, 0.590),
    (0.805, 0.590),
]

STROKE = 0.078
STROKE_SMALL = 0.125
VIA_R = 0.062          # 两端过孔半径（圆的，不是旧版的方块焊盘）
VIA_R_SMALL = 0.080


# ---------------------------------------------------------------------------
# 配色方案
# ---------------------------------------------------------------------------

PALETTES = {
    # 延续现有品牌（AccentGpu 深青），只换字形
    'brand': dict(tile=(0x14, 0x61, 0x5A), rim=(0x2A, 0x8C, 0x80), ink=(0xFF, 0xFF, 0xFF)),
    # 石墨底 + 亮青：最"仪器/机箱"的一档，不发光也不土
    'graphite': dict(tile=(0x11, 0x18, 0x1F), rim=(0x2C, 0x39, 0x45), ink=(0x4F, 0xD8, 0xC8)),
    # 深空蓝 + 冷白青：航天/深空取向，项目名"芯脉"也在这一族
    'deepspace': dict(tile=(0x0E, 0x20, 0x36), rim=(0x1F, 0x3D, 0x60), ink=(0x6E, 0xD3, 0xF5)),
    # 金属石墨 + 冷白：最低调，深色任务栏上也立得住
    'steel': dict(tile=(0x1B, 0x21, 0x27), rim=(0x36, 0x40, 0x4A), ink=(0xE9, 0xEF, 0xF4)),
}


# ---------------------------------------------------------------------------
# 三套方案
#
# 每个方案给一个 painter(px, py, S)，返回叠在底色之上的颜色（None = 只有底）。
# 共同点：圆角方底 + 一轮数字方波 + 两端过孔 —— 这三层在 16px 下都还活着。
# 差异只在"额外那一层工程符号"，也就是科技感的来源。
# ---------------------------------------------------------------------------

def design_wave(px, py, S, pal, small):
    """A 纯方波：只留波形本身，靠直角和阶跃说话。"""
    wave = SQUARE_SMALL if small else SQUARE
    pts = [(x * S, y * S) for x, y in wave]
    half = (STROKE_SMALL if small else STROKE) * S / 2.0
    box = polyline_box(pts, half)
    via = (VIA_R_SMALL if small else VIA_R) * S

    if in_polyline(px, py, pts, half, box):
        return pal['ink']
    # 直角补角：方波的转折必须是"方"的，圆头半笔在 90° 处会缺一块
    for cx, cy in pts:
        if in_square(px, py, cx, cy, half):
            return pal['ink']
    for cx, cy in (pts[0], pts[-1]):
        if in_disc(px, py, cx, cy, via):
            return pal['ink']
    return None


def design_panel(px, py, S, pal, small):
    """B 面板倒角：右上角切出一个亮面，而不是把角挖掉。

    挖空版本（0.30 与 0.46 都试过）读起来是"折了角的纸"——因为切面透出背景，
    轮廓上多了一道不属于砖面的直边。改成**在角上铺一块更亮的三角面**之后，
    轮廓仍是完整的圆角方块，那道斜线变成"倒角的高光面"，这才是金属件的样子。
    """
    c = design_wave(px, py, S, pal, small)
    if c is not None:
        return c
    if _chamfered(px, py, S, 0.42):
        return _blend(pal['tile'], pal['ink'], 0.16)
    return None


def _chamfered(px, py, S, k):
    """右上角 45° 三角区（倒角面）。"""
    return (S - px) + py < k * S


def design_scope(px, py, S, pal, small):
    """C 示波器框：四角小括号，像取景/瞄准框，把波形"框"在一个仪器视窗里。"""
    if not small:
        # 小尺寸下四角括号会退化成四个噪点，直接不出，只保波形
        arm = 0.105 * S
        inset = 0.115 * S
        w = 0.024 * S
        for sx in (1, -1):
            for sy in (1, -1):
                cx = S / 2.0 + sx * (S / 2.0 - inset)
                cy = S / 2.0 + sy * (S / 2.0 - inset)
                hit = (in_rect(px, py, cx + sx * arm / 2.0, cy, arm / 2.0, w / 2.0) or
                       in_rect(px, py, cx, cy + sy * arm / 2.0, w / 2.0, arm / 2.0))
                if hit:
                    return _blend(pal['tile'], pal['ink'], 0.45)
    return design_wave(px, py, S, pal, small)


def design_bezel(px, py, S, pal, small):
    """D 内框：沿砖面内缩一圈细线，像仪器面板的框边。

    内缩量从 0.085 加到 0.155 —— 0.085 时这条内线和外墙的 rim 只差 5px，
    读起来像"边框画了两遍"的错误，而不是"面板一圈框"。
    """
    if not small:
        inset = 0.155 * S
        d = sdf_round_rect(px, py, S / 2.0, S / 2.0,
                           S / 2.0 - inset, S / 2.0 - inset,
                           max(1.0, 0.22 * S - inset))
        if abs(d) <= max(0.7, S * 0.010):
            return _blend(pal['tile'], pal['ink'], 0.26)
    return design_wave(px, py, S, pal, small)


def _blend(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


DESIGNS = {
    'a-wave': design_wave,
    'b-panel': design_panel,
    'c-scope': design_scope,
    'd-bezel': design_bezel,
}


# ---------------------------------------------------------------------------
# 渲染
# ---------------------------------------------------------------------------

def render(master, ss, painter, pal):
    size = master
    n = size * ss
    samples = [0.0] * (size * size * 4)
    step = 1.0 / ss
    inv = 1.0 / (ss * ss)
    radius = 0.22 * size
    rim_w = max(1.0, size * 0.012)

    for sy in range(n):
        py = (sy + 0.5) * step
        for sx in range(n):
            px = (sx + 0.5) * step

            d = sdf_round_rect(px, py, size / 2.0, size / 2.0, size / 2.0, size / 2.0, radius)
            if d > 0:
                continue

            col = pal['rim'] if d > -rim_w else pal['tile']
            over = painter(px, py, size, pal, small=False)
            if over is not None:
                col = over

            i = ((sy // ss) * size + (sx // ss)) * 4
            samples[i] += col[0] * inv
            samples[i + 1] += col[1] * inv
            samples[i + 2] += col[2] * inv
            samples[i + 3] += 255.0 * inv

    return samples


def render_small(master, ss, painter, pal):
    """小尺寸用简化几何重渲一张母版，等价于 make-icon.py 的 SMALL 分支。"""
    size = master
    n = size * ss
    samples = [0.0] * (size * size * 4)
    step = 1.0 / ss
    inv = 1.0 / (ss * ss)
    radius = 0.22 * size
    rim_w = max(1.0, size * 0.012)

    for sy in range(n):
        py = (sy + 0.5) * step
        for sx in range(n):
            px = (sx + 0.5) * step
            d = sdf_round_rect(px, py, size / 2.0, size / 2.0, size / 2.0, size / 2.0, radius)
            if d > 0:
                continue
            col = pal['rim'] if d > -rim_w else pal['tile']
            over = painter(px, py, size, pal, small=True)
            if over is not None:
                col = over
            i = ((sy // ss) * size + (sx // ss)) * 4
            samples[i] += col[0] * inv
            samples[i + 1] += col[1] * inv
            samples[i + 2] += col[2] * inv
            samples[i + 3] += 255.0 * inv
    return samples


def sheet(master, small_master, name, out_dir):
    """对照图：上行浅底、下行深底；每行 = 256 大图 + 64/48/32/24/16 原尺寸。"""
    smalls = [64, 48, 32, 24, 16]
    big = 256
    pad = 16
    gap = 14
    W = pad + big + gap * 2 + sum(smalls) + gap * (len(smalls) - 1) + pad
    row_h = big + pad * 2
    H = row_h * 2

    canvas = bytearray(W * H * 3)
    for row, bg in enumerate(((0xF2, 0xF4, 0xF7), (0x0B, 0x0E, 0x13))):
        for y in range(row * row_h, (row + 1) * row_h):
            o = y * W * 3
            canvas[o:o + W * 3] = bytes(bg) * W

    for row in range(2):
        y0 = row * row_h + (row_h - big) // 2
        # 大图
        compose(canvas, W, mk.resample(master, MASTER, big), big, pad, y0)
        # 小图：同一基线居中
        x = pad + big + gap * 2
        for s in smalls:
            src = small_master if s <= 24 else master
            rgba = mk.resample(src, MASTER, s)
            compose(canvas, W, rgba, s, x, y0 + (big - s) // 2)
            x += s + gap

    raw = bytearray()
    for y in range(H):
        raw.append(0)
        raw += canvas[y * W * 3:(y + 1) * W * 3]

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack('>IIBBBBB', W, H, 8, 2, 0, 0, 0)
    path = os.path.join(out_dir, f'{name}.png')
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
                chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))
    print(f'  {path}  ({W}x{H})')


def compose(canvas, W, rgba, size, ox, oy):
    """把 RGBA 叠到 RGB 画布上（按 alpha 混合）。"""
    for y in range(size):
        base = ((oy + y) * W + ox) * 3
        for x in range(size):
            si = (y * size + x) * 4
            a = rgba[si + 3]
            if a == 0:
                continue
            o = base + x * 3
            if a == 255:
                canvas[o] = rgba[si]
                canvas[o + 1] = rgba[si + 1]
                canvas[o + 2] = rgba[si + 2]
            else:
                for c in range(3):
                    canvas[o + c] = (rgba[si + c] * a + canvas[o + c] * (255 - a)) // 255


def main():
    out_dir = os.path.join(_HERE, '..', 'docs', 'icon-lab')
    os.makedirs(out_dir, exist_ok=True)

    for name, painter in DESIGNS.items():
        for pal_name, pal in PALETTES.items():
            print(f'渲染 {name} / {pal_name} ...')
            m = render(MASTER, SS, painter, pal)
            ms = render_small(MASTER, SS, painter, pal)
            sheet(m, ms, f'{name}-{pal_name}', out_dir)


if __name__ == '__main__':
    main()
