"""
CoPul 图标：「字母 C · 脉冲环」（M4.40 定稿，取代 M4.39 的推力喷管）

=============================================================================
为什么是它
=============================================================================

前四轮一共 16 个概念，几乎全部困在"数据可视化"的语汇里（折线、柱、环、刻度、
准星）。问题是那套语汇**谁都能用**，换个配色就能套在任何监控软件上；16px 下
更是必然糊成线团。

字母 C 不一样：它同时是**名字**（CoPul）和**一个够粗的几何体**——环厚是外径的
45%，缩到 16px 还剩 3 个像素，是这套候选里唯一在最小尺寸上仍然"有形"的构成。
缺口里的琥珀块给了它一个非对称的落点，否则纯 C 会显得像一个裁切出来的甜甜圈。

=============================================================================
几何（归一化到画布，比例量自用户给的截图）
=============================================================================

    环外径 0.300 / 内径 0.165      厚度 = 外径的 45%
    缺口   内缘 12°~-64°、外缘 2°~-56°（两端各向外张 10°，形成喇叭口）
    琥珀块 径向 0.205~0.315、角度 -19°~+3°（落在缺口外半侧，略微探出环外）
    白点   0.058

缺口两端不是径向切：外缘比内缘多转 10°。截图里就是这个形状，端面朝缺口方向
外张，C 才不像"被切掉一段的甜甜圈"。

=============================================================================
三档
=============================================================================

    big   ≥32   内缘暗边 + 环背冷光 + 外缘左上受光 + 琥珀高光
    small ≤24   环加厚、白点/琥珀放大，去掉所有渐变与阴影
                （这些细节在小尺寸只会变成噪点，把图形糊成一团）
    dark  托盘   **不带底盘**：深色任务栏 + 深底盘 = 整块溶解（M4.39 踩过），
                裸环反而最清楚

输出：copul.ico（7 帧）/ copul-1024.png / copul-256.png /
      copul-tray-{light,dark}.ico + 两张验收图。
"""

import math
import os
import struct
import zlib

SS = 4                     # 超采样倍数
SS_HUGE = 3                # ≥512 降到 3×（4× 在 1024 上要跑好几分钟）

# ---------------------------------------------------------------------------
# 配色（沿用项目既有色：青 = AccentGpu 族，琥珀 = AccentNet 族）
# ---------------------------------------------------------------------------

INK      = (0x07, 0x0B, 0x11, 255)      # 底盘
INK_HI   = (0x14, 0x1D, 0x28, 255)      # 底盘左上冷光

CYAN     = (0x3F, 0xC8, 0xDC, 255)
CYAN_HI  = (0x7C, 0xDE, 0xEC, 255)
CYAN_DIM = (0x20, 0x74, 0x84, 255)

AMBER    = (0xE2, 0xA6, 0x4C, 255)
AMBER_HI = (0xF7, 0xCB, 0x88, 255)

WHITE    = (0xE8, 0xF3, 0xFA, 255)

# 深色任务栏的托盘版：环提亮一档
T_CYAN   = (0x4F, 0xD2, 0xE4, 255)
T_DIM    = (0x24, 0x72, 0x80, 255)


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


def add_cov(layers, size, cov_of_size, col, bbox_of_size, ss=None):
    """把"归一化几何 → 覆盖率"的函数直接铺成一层。"""
    layers.append((rasterize(size, cov_of_size(size), bbox_of_size(size), ss), col))


def add_radial(layers, size, cx, cy, r_in, r_out, alpha, col, ss=None):
    """连续径向渐变（smoothstep）。同心圆叠加会出现肉眼可辨的环带。"""
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
# 几何
# ---------------------------------------------------------------------------

def geom(small, dark):
    """一套尺寸的几何参数，全部相对画布（0..1）。"""
    if dark:
        # 托盘深色版没有底盘 → 环放大到画布，裸环直接当图形
        return dict(r_out=0.430, r_in=0.238, dot=0.072,
                    lo_in=13.0, lo_out=7.0, up_in=296.0, up_out=303.0,
                    amb=(0.292, 0.438, -24.0, -4.0, -29.0, -10.0))
    if small:
        return dict(r_out=0.342, r_in=0.176, dot=0.080,
                    lo_in=15.0, lo_out=8.0, up_in=294.0, up_out=301.0,
                    amb=(0.248, 0.404, -28.0, -8.0, -33.0, -14.0))
    return dict(r_out=0.300, r_in=0.165, dot=0.058,
                lo_in=12.0, lo_out=6.0, up_in=296.0, up_out=303.0,
                amb=(0.202, 0.300, -20.0, -2.0, -25.0, -7.0))


def _gap_angles(g, pad=0.0):
    """缺口两端角度（上端归到 -180..180，方便比较）。"""
    up_i = g['up_in'] + pad
    up_o = g['up_out'] + pad
    if up_i > 180.0:
        up_i -= 360.0
        up_o -= 360.0
    return g['lo_in'], g['lo_out'], up_i, up_o


def c_cov(size, g):
    """开口环。缺口两端外张：外缘比内缘多转 10°，端面才有"喇叭口"。"""
    CX = CY = size / 2.0
    R0 = g['r_in'] * size
    R1 = g['r_out'] * size
    lo_i, lo_o, up_i, up_o = _gap_angles(g)

    def cov(px, py):
        dx = px - CX
        dy = py - CY
        d = math.hypot(dx, dy)
        if d < R0 or d > R1:
            return False
        a = math.degrees(math.atan2(dy, dx))
        u = (d - R0) / (R1 - R0)
        lo = lo_i + (lo_o - lo_i) * u
        up = up_i + (up_o - up_i) * u
        return not (up < a < lo)
    return cov


def c_cov_inner(size, g):
    """环内缘的暗边：内径起、占环厚 18%。"""
    CX = CY = size / 2.0
    Rout = g['r_out'] * size
    R0 = g['r_in'] * size
    R1 = (g['r_in'] + (g['r_out'] - g['r_in']) * 0.13) * size
    lo_i, lo_o, up_i, up_o = _gap_angles(g)

    def cov(px, py):
        dx = px - CX
        dy = py - CY
        d = math.hypot(dx, dy)
        if d < R0 or d > R1:
            return False
        a = math.degrees(math.atan2(dy, dx))
        u = (d - R0) / (Rout - R0)
        lo = lo_i + (lo_o - lo_i) * u
        up = up_i + (up_o - up_i) * u
        return not (up < a < lo)
    return cov


def c_cov_rim(size, g):
    """左上 150°~300° 外缘的受光，沿角度两端柔化。"""
    CX = CY = size / 2.0
    band = (g['r_out'] - g['r_in']) * 0.16
    R1 = g['r_out'] * size
    R0 = (g['r_out'] - band) * size
    span = max(1e-6, R1 - R0)

    def cov(px, py):
        dx = px - CX
        dy = py - CY
        d = math.hypot(dx, dy)
        if d < R0 or d > R1:
            return False
        a = math.degrees(math.atan2(dy, dx))
        if not (150.0 <= a <= 300.0):
            return False
        w = min((a - 150.0) / 45.0, (300.0 - a) / 45.0, 1.0)
        return (d - R0) / span < w
    return cov


def _pol(r, a_deg, k):
    a = math.radians(a_deg)
    return (k + r * math.cos(a), k + r * math.sin(a))


def amb_points(g):
    """琥珀脉冲块 = 直边四边形（内缘更宽）。

    早先用的是扇形（角度插值），渲出来像一只"倒料的嘴"，会和环的下端连成
    一个 G。直边块才读得出"一块独立的读数条"。
    """
    r0, r1, ai0, ai1, ao0, ao1 = g['amb']
    return [_pol(r1, ao0, 0.5), _pol(r1, ao1, 0.5), _pol(r0, ai1, 0.5), _pol(r0, ai0, 0.5)]


def _bbox(frac):
    return lambda size: (size * (0.5 - frac) - 2, size * (0.5 - frac) - 2,
                         size * (0.5 + frac) + 2, size * (0.5 + frac) + 2)


def render(size, small=False, dark=False):
    """渲染一幅。small=小尺寸简化几何，dark=深色任务栏的托盘版（无底盘）。"""
    ss = SS_HUGE if size >= 512 else SS
    g = geom(small, dark)
    layers = []

    if not dark:
        layers.append((rasterize(size, lambda x, y: rounded_rect_cov(x, y, size, 0.225 * size), ss=ss),
                       INK))
        if not small:
            # 底盘左上一点冷光：整块纯色在大面积深色壁纸上像"没渲染完"
            add_radial(layers, size, 0.36, 0.26, 0.02, 0.72, 24, INK_HI, ss)

    # 环背冷光：只有大盘才有意义，小尺寸会变成一圈脏边
    if not small and not dark:
        add_radial(layers, size, 0.5, 0.5, g['r_in'] * 0.6, g['r_out'] * 2.5, 30, CYAN, ss)

    ring = T_CYAN if dark else CYAN
    add_cov(layers, size, lambda s: c_cov(s, g), ring, _bbox(g['r_out'] + 0.02), ss)
    if not small:
        add_cov(layers, size, lambda s: c_cov_inner(s, g), T_DIM if dark else CYAN_DIM,
                _bbox(g['r_out'] + 0.02), ss)
        add_cov(layers, size, lambda s: c_cov_rim(s, g), CYAN_HI, _bbox(g['r_out'] + 0.02), ss)

    ap = amb_points(g)
    add_poly(layers, size, ap, AMBER, ss)
    if not small:
        # 外缘一窄条高光：只有大盘画，小尺寸会把它吃掉
        add_poly(layers, size, [ap[0], ap[1],
                                ((ap[2][0] - ap[1][0]) * 0.18 + ap[1][0],
                                 (ap[2][1] - ap[1][1]) * 0.18 + ap[1][1]),
                                ((ap[3][0] - ap[0][0]) * 0.18 + ap[0][0],
                                 (ap[3][1] - ap[0][1]) * 0.18 + ap[0][1])], AMBER_HI, ss)

    add_disc(layers, size, 0.5, 0.5, g['dot'], WHITE, ss)
    return composite(size, layers)


# ---------------------------------------------------------------------------
# 编码 / 输出
# ---------------------------------------------------------------------------

def encode_png(size, rgba):
    raw = bytearray()
    stride = size * 4
    for y in range(size):
        raw.append(0)
        raw += rgba[y * stride:(y + 1) * stride]

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    return (b'\x89PNG\r\n\x1a\n' +
            chunk(b'IHDR', struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)) +
            chunk(b'IDAT', zlib.compress(bytes(raw), 9)) +
            chunk(b'IEND', b''))


def write_ico(path, frames):
    n = len(frames)
    with open(path, 'wb') as f:
        f.write(struct.pack('<HHH', 0, 1, n))
        offset = 6 + 16 * n
        for size, data in frames:
            f.write(struct.pack('<BBBBHHII',
                                size if size < 256 else 0, size if size < 256 else 0,
                                0, 0, 1, 32, len(data), offset))
            offset += len(data)
        for _, data in frames:
            f.write(data)


def write_png_file(path, size, rgba):
    with open(path, 'wb') as f:
        f.write(encode_png(size, rgba))


def write_png_rgb(path, W, H, rgb):
    raw = bytearray()
    for y in range(H):
        raw.append(0)
        raw += rgb[y * W * 3:(y + 1) * W * 3]

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' +
                chunk(b'IHDR', struct.pack('>IIBBBBB', W, H, 8, 2, 0, 0, 0)) +
                chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)
TRAY_SIZES = (16, 20, 24, 32, 40, 48, 64)
SMALL_CUTOFF = 24


def build_frames(sizes, dark=False):
    return [(sz, encode_png(sz, render(sz, small=(sz <= SMALL_CUTOFF), dark=dark)))
            for sz in sizes]


def _blit_zoom(canvas, W, H, rgba, sz, zoom, ox, oy, bg):
    for dy in range(sz * zoom):
        for dx in range(sz * zoom):
            si = ((dy // zoom) * sz + (dx // zoom)) * 4
            a = rgba[si + 3]
            if a == 0:
                continue
            X, Y = ox + dx, oy + dy
            if X >= W or Y >= H:
                continue
            o = (Y * W + X) * 3
            canvas[o:o + 3] = bytes(
                [int(rgba[si + c] * a / 255 + bg[c] * (255 - a) / 255) for c in range(3)])


def make_verify(docs_dir):
    sizes = [16, 24, 32, 48, 64]
    zoom, pad = 4, 16
    cw = ch = max(sizes) * zoom
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
            _blit_zoom(canvas, W, H, rgba, sz, zoom, ox, row * (ch + pad) + pad, bg)
    write_png_rgb(os.path.join(docs_dir, 'preview-icon-sizes.png'), W, H, canvas)
    print('  preview-icon-sizes.png')

    tsizes = [16, 20, 24, 32]
    tzoom, tpad = 4, 14
    tcw = tch = max(tsizes) * tzoom
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
            _blit_zoom(tc, TW, TH, rgba, sz, tzoom, ox, row * (tch + tpad) + tpad, tbgs[row])
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
