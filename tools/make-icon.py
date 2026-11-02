"""
CoPul 芯脉 —— 应用图标生成器（R-01）。

纯标准库，不依赖 PIL（本机 pip 无源、Add-Type 被安全策略禁，见 MEMORY.md）。
手写 PNG（zlib）+ ICO 容器（Vista 以后每帧允许直接存 PNG）。

=============================================================================
设计说明（2026-11-02 重绘，取代旧的 make-tray-icon.py）
=============================================================================

**意象**：需求 14.1 对 R-01 的要求是"『脉』意象：波形/电路交叠，单色几何，
避免渐变堆叠"。旧图标只画了一条心电图，问题有两个：
  1. 只有"心电"，没有"脉"（脉络/血管）的延伸感，也没有"芯"（芯片）的暗示 ——
     放在一堆健康类 App 里认不出来。
  2. 深色方底 + 亮绿波，在**白色主题**下很跳（本项目现在默认白主题）。
  3. （2026-11-02 第二轮追加）心电本身就是医疗语汇 —— 用户要"科技感"，
     换的不是颜色是字形：直角方波 + PCB 过孔 + 面板倒角。详见下面「第二轮」一节。

新图标的做法：
  · **一条脉冲波形 + 一条电路走线，两者交叠**，正是"波形/电路交叠"的字面实现。
  · 交叠感用**同一套白色、两种不透明度**表达（走线 55%、波形 100%）——
    是平面的透明度差，不是渐变，符合"单色几何"约束。
  · 底色用品牌**深青绿**而非近黑：既有辨识度，又在白/深两套主题下都立得住
    （近黑方块在白底上太重，亮色方块在深底上又不显眼）。
  · 圆角方块（Windows 11 的应用图标惯例）+ 极淡内描边勾出边界。

**为什么必须超采样再降采样**：
旧脚本在每个目标尺寸上**直接**画抗锯齿（按距离做 1px 淡出）。16px 时描边只有
1.4px、整条曲线才 3-4 像素，那种"每个像素算一点透明度"的做法会把波形糊成一团，
所以旧脚本才被迫为小尺寸单独再写一套点（PULSE_PTS_SMALL）。
新做法是**先在一张 4× 超采样的母版上算覆盖率，再盒式降采样到目标尺寸** ——
小尺寸拿到的是"真实的高分辨率几何"的平均，边和线自然收敛，只需一套几何定义。

用法：
  python tools/make-icon.py            # 生成全部（ico + png + 1024 源图）
  python tools/make-icon.py --preview  # 额外输出一张放大对照图，便于肉眼验收
"""

import math
import struct
import zlib

# ---------------------------------------------------------------------------
# 配色（与 Themes/*.xaml 的品牌色同源，但不直接引用资源 —— 图标是静态资产）
# ---------------------------------------------------------------------------

# R-01 主图标（exe / 快捷方式 / 关于页）：深青绿方底 + 白波形
TILE = (0x17, 0x65, 0x5C, 255)
TILE_RIM = (0x2A, 0x8C, 0x80, 255)
GLYPH = (0xFF, 0xFF, 0xFF, 255)

# R-02 深色任务栏版（2026-11-02 补）：亮青绿方底 + 深波形
#
# 需求 14.1 之所以要求托盘图标分深浅两版，是**对比度**问题不是讲究：
# 实测深色任务栏 #202022 上，深青绿方底几乎融进背景，只剩中间一小段白线可辨，
# 用户在托盘里看到的是"一个白点"，认不出这是什么程序。
# 换成亮青绿（AccentGpu 原色 #4FB3A6）后方底自己发亮，轮廓立得住。
#
# 两版都是"实心方底 + 反色波形"，而不是同色系调透明度 ——
# 半透明在小尺寸下最先糊掉，必须两边都是实色高对比才成立。
TILE_DARK = (0x4F, 0xB3, 0xA6, 255)
TILE_DARK_RIM = (0x6B, 0xC9, 0xBC, 255)
GLYPH_DARK = (0x0B, 0x2E, 0x2A, 255)
# 电路走线：白 78%。第一版给 55% 太淡 —— 叠在同色系的深青绿底上几乎看不出层次，
# "交叠"就退化成了一团糊。78% 仍然明显比纯白波形弱，主次分得开，但又能看清电路的存在。
TRACE = (0xFF, 0xFF, 0xFF, 200)

# 圆角半径（占边长比例）。Windows 11 应用图标约 22%。
TILE_RADIUS = 0.22

# ---------------------------------------------------------------------------
# 2026-11-02（第二轮）—— 从"心电"改成"数字方波 + 面板倒角"
#
# 用户要求"科技感"。回头看得失，问题不在配色，在**字形语汇**：
# 平滑的心电 QRS 曲线是健康/医疗 App 的通用符号，越画得标准越像体检软件。
# 科技感来自三样东西，都是心电不具备的：
#   1. **直角** —— 方波的上升沿/平台/下降沿全是 90°
#   2. **阶跃** —— "信号在高/低两个电平之间跳"，这是数字电路的语言
#   3. **过孔** —— 两端用圆点（via）而不是方块焊盘，才是 PCB 的写法
#
# 另一层是形体：右上角铺一块 16% 提亮的 45° 三角面，读起来是金属件的**倒角高光**。
# 试过两个失败的变体（都出过对照图，见 docs/icon-lab/）：
#   · 把这个三角**挖空** → 轮廓上多出一道不属于砖面的直边，像"折了角的纸"
#   · 四角画取景括号 / 内缩一圈细线 → 一个像裁切标记，一个像"边框画了两遍"
# 亮面版之所以成立，是因为**轮廓仍然是完整的圆角方块**，变化只发生在明度上。
#
# 配色也测过四档（brand 深青 / deepspace 深空蓝 / graphite 石墨 / steel 钢灰）。
# graphite 与 steel 在**深色任务栏**上整体溶解，只剩一圈淡边 —— 和 R-02 分深浅两版
# 是同一个坑。所以主图标继续用品牌深青。
# ---------------------------------------------------------------------------

# 一轮完整方波：低位平台 → 上升沿 → 高位平台 → 下降沿（过冲）→ 深位平台 → 回升
SQUARE = [
    (0.185, 0.585),
    (0.325, 0.585),      # 低位平台
    (0.325, 0.315),      # 上升沿
    (0.450, 0.315),      # 高位平台
    (0.450, 0.740),      # 下降沿（越过基线，形成负脉冲）
    (0.575, 0.740),      # 深位平台
    (0.575, 0.585),      # 回基线
    (0.815, 0.585),      # 收尾低位平台
]

# 小尺寸：平台段相对更长、竖段更短，16px 下才不会糊成一块
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

# 线宽（归一化）。均匀一档 —— 分段的粗细在斜线/竖线交界处必然粘连（前四版的坑）。
STROKE = 0.078
STROKE_SMALL = 0.125

# 两端过孔：半径（归一化）。圆点，不是旧版的方块焊盘。
VIA_R = 0.062
VIA_R_SMALL = 0.080

# 右上角倒角面：三角区大小（占边长）与提亮比例
FACET_K = 0.42
FACET_T = 0.16

# 母版超采样：先按 grid × SS 采样，再盒式降采样到 grid。
MASTER = 256
SS = 4

# 导出尺寸（R-01 要求 16/24/32/48/64/128/256）
ICO_SIZES = (16, 24, 32, 48, 64, 128, 256)


# ---------------------------------------------------------------------------
# 几何工具
# ---------------------------------------------------------------------------

def rounded_rect_cov(px, py, size, radius):
    """点是否在圆角矩形内（精确 SDF，>0 表示内部距离）。"""
    half = size / 2.0
    dx = abs(px - half) - (half - radius)
    dy = abs(py - half) - (half - radius)
    ox, oy = max(dx, 0.0), max(dy, 0.0)
    outside = math.hypot(ox, oy)
    inside = min(max(dx, dy), 0.0)
    return -(outside + inside - radius)   # > 0 = 在内部


def seg_dist(px, py, ax, ay, bx, by):
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    L2 = vx * vx + vy * vy
    t = 0.0 if L2 == 0 else max(0.0, min(1.0, (wx * vx + wy * vy) / L2))
    return math.hypot(px - (ax + t * vx), py - (ay + t * vy))


def polyline_dist(px, py, pts):
    return min(seg_dist(px, py, pts[i][0], pts[i][1], pts[i + 1][0], pts[i + 1][1])
               for i in range(len(pts) - 1))


def scale_pts(pts, size):
    return [(x * size, y * size) for x, y in pts]


# ---------------------------------------------------------------------------
# 渲染：超采样母版 → 盒式降采样
# ---------------------------------------------------------------------------

def blend(a, b, t):
    """按比例线性混两个 RGB，返回 RGB（不带 alpha）。"""
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def render_master(small=False, dark=False):
    """渲染一张 MASTER×MASTER 的 RGBA 浮点缓冲（按 SS 超采样后平均）。

    small=True  用 SQUARE_SMALL（平台更长、竖段更短），否则用 SQUARE。
    dark=True   用 R-02 那套亮方底 + 深波形（深色任务栏专用）。
    """
    tile = TILE_DARK if dark else TILE
    tile_rim = TILE_DARK_RIM if dark else TILE_RIM
    glyph = GLYPH_DARK if dark else GLYPH
    # 倒角面：往字色方向提亮 16%（深色托盘版是亮方底，于是自动变成"压暗"）
    facet = blend(tile, glyph, FACET_T)
    size = MASTER
    n = size * SS
    samples = [0.0] * (size * size * 4)

    wave = scale_pts(SQUARE_SMALL if small else SQUARE, size)
    stroke = (STROKE_SMALL if small else STROKE) * size
    half = stroke / 2.0
    via = (VIA_R_SMALL if small else VIA_R) * size
    facet_k = FACET_K * size

    xs = [p[0] for p in wave]
    ys = [p[1] for p in wave]
    box = (min(xs) - half, min(ys) - half, max(xs) + half, max(ys) + half)

    rim_w = max(1.0, size * 0.012)
    radius = TILE_RADIUS * size

    step = 1.0 / SS
    inv = 1.0 / (SS * SS)

    for sy in range(n):
        py = (sy + 0.5) * step
        for sx in range(n):
            px = (sx + 0.5) * step

            d = rounded_rect_cov(px, py, size, radius)
            if d <= 0:
                continue

            if d <= rim_w:
                r, g, b, _ = tile_rim
            elif (size - px) + py < facet_k:
                r, g, b = facet
            else:
                r, g, b, _ = tile

            # ---- 方波：到折线的距离（均匀线宽）+ 顶点补方（保证转角是 90° 的方角）----
            hit = False
            if box[0] <= px <= box[2] and box[1] <= py <= box[3]:
                for si in range(len(wave) - 1):
                    if seg_dist(px, py, wave[si][0], wave[si][1],
                                wave[si + 1][0], wave[si + 1][1]) <= half:
                        hit = True
                        break
                if not hit:
                    for cx, cy in wave:
                        if abs(px - cx) <= half and abs(py - cy) <= half:
                            hit = True
                            break

            # ---- 两端过孔：圆点（PCB 的 via，不是焊盘方块）----
            if not hit:
                for cx, cy in (wave[0], wave[-1]):
                    if (px - cx) ** 2 + (py - cy) ** 2 <= via * via:
                        hit = True
                        break

            if hit:
                r, g, b, _ = glyph

            i = ((sy // SS) * size + (sx // SS)) * 4
            samples[i]     += r * inv
            samples[i + 1] += g * inv
            samples[i + 2] += b * inv
            samples[i + 3] += 255.0 * inv

    return samples


def resample(src, src_size, dst_size):
    """面积平均降采样（任意比例）。返回 dst_size² 的 RGBA 8bit 列表。"""
    buf = [0] * (dst_size * dst_size * 4)
    ratio = src_size / dst_size

    for dy in range(dst_size):
        y0 = dy * ratio
        y1 = y0 + ratio
        sy0 = int(math.floor(y0))
        sy1 = max(sy0 + 1, int(math.ceil(y1)))
        for dx in range(dst_size):
            x0 = dx * ratio
            x1 = x0 + ratio
            sx0 = int(math.floor(x0))
            sx1 = max(sx0 + 1, int(math.ceil(x1)))

            acc = [0.0, 0.0, 0.0, 0.0]
            wsum = 0.0
            for sy in range(sy0, min(sy1, src_size)):
                wy = min(sy + 1.0, y1) - max(sy, y0)
                if wy <= 0:
                    continue
                for sx in range(sx0, min(sx1, src_size)):
                    wx = min(sx + 1.0, x1) - max(sx, x0)
                    if wx <= 0:
                        continue
                    w = wx * wy
                    si = (sy * src_size + sx) * 4
                    sa = src[si + 3]
                    # 预乘后平均，避免边缘出现暗边（带 alpha 的图必须这么做）
                    acc[0] += src[si]     * sa * w
                    acc[1] += src[si + 1] * sa * w
                    acc[2] += src[si + 2] * sa * w
                    acc[3] += sa * w
                    wsum += w

            if wsum == 0:
                continue
            a = acc[3] / wsum
            di = (dy * dst_size + dx) * 4
            if a < 1e-6:
                buf[di] = buf[di + 1] = buf[di + 2] = buf[di + 3] = 0
            else:
                buf[di]     = int(round(min(255, acc[0] / acc[3])))
                buf[di + 1] = int(round(min(255, acc[1] / acc[3])))
                buf[di + 2] = int(round(min(255, acc[2] / acc[3])))
                buf[di + 3] = int(round(min(255, a)))
    return buf


def encode_png(size, rgba):
    raw = bytearray()
    for y in range(size):
        raw.append(0)                       # filter: none
        row = rgba[y * size * 4:(y + 1) * size * 4]
        raw += bytes(row)

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
            chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


def write_ico(path, frames):
    """frames: [(size, png_bytes)]。ICO 头 + 目录 + 各帧 PNG（Vista+ 允许）。"""
    header = struct.pack('<HHH', 0, 1, len(frames))
    entries = b''
    offset = 6 + 16 * len(frames)
    for size, data in frames:
        entries += struct.pack('<BBBBHHII',
                               0 if size >= 256 else size,
                               0 if size >= 256 else size,
                               0, 0, 1, 32, len(data), offset)
        offset += len(data)
    with open(path, 'wb') as f:
        f.write(header + entries + b''.join(d for _, d in frames))


def upscale_bilinear(src, src_size, dst_size):
    """母版 256 → 1024 源图。几何图形双线性放大足够（不是照片）。"""
    buf = [0] * (dst_size * dst_size * 4)
    ratio = src_size / dst_size
    for dy in range(dst_size):
        fy = (dy + 0.5) * ratio - 0.5
        y0 = max(0, min(src_size - 1, int(math.floor(fy))))
        y1 = min(src_size - 1, y0 + 1)
        ty = max(0.0, min(1.0, fy - y0))
        for dx in range(dst_size):
            fx = (dx + 0.5) * ratio - 0.5
            x0 = max(0, min(src_size - 1, int(math.floor(fx))))
            x1 = min(src_size - 1, x0 + 1)
            tx = max(0.0, min(1.0, fx - x0))

            out = [0, 0, 0, 0]
            for k, (yy, ww) in enumerate(((y0, 1 - ty), (y1, ty))):
                for j, (xx, wxx) in enumerate(((x0, 1 - tx), (x1, tx))):
                    w = ww * wxx
                    si = (yy * src_size + xx) * 4
                    for c in range(4):
                        out[c] += src[si + c] * w
            di = (dy * dst_size + dx) * 4
            for c in range(4):
                buf[di + c] = int(round(min(255, out[c])))
    return buf


# ---------------------------------------------------------------------------
# 主流程
# ---------------------------------------------------------------------------

def main():
    import os
    import sys

    here = os.path.dirname(os.path.abspath(__file__))
    assets = os.path.join(here, '..', 'src', 'CoPul.App', 'assets')
    preview_dir = os.path.join(here, '..', 'docs')

    print(f'渲染大尺寸母版 {MASTER}x{MASTER}（{SS}x 超采样 = {MASTER*SS}x{MASTER*SS} 采样）...')
    master = render_master(small=False)

    print(f'渲染小尺寸母版 {MASTER}x{MASTER}（简化几何 SQUARE_SMALL）...')
    master_small = render_master(small=True)

    # 尺寸分界：<= 24 用简化几何的母版，其余用完整几何。
    # 24 是分界线而不是 32：实测 32px 下 12 段折线还读得出折角，
    # 24 开始 Q/S 两个小折就和基线糊在一起了。
    SMALL_CUTOFF = 24

    frames = []
    for s in ICO_SIZES:
        src = master_small if s <= SMALL_CUTOFF else master
        rgba = resample(src, MASTER, s)
        frames.append((s, encode_png(s, rgba)))
        print(f'  {s}x{s} ok{"（简化几何）" if s <= SMALL_CUTOFF else ""}')

    ico_path = os.path.join(assets, 'copul.ico')
    write_ico(ico_path, frames)
    print(f'写入 {ico_path}（{len(frames)} 帧，{os.path.getsize(ico_path)} 字节）')

    # R-01 要求 1024 源图；另外存一张 256 PNG 供 README / 关于页直接用
    big = upscale_bilinear(master, MASTER, 1024)
    src_path = os.path.join(assets, 'copul-1024.png')
    with open(src_path, 'wb') as f:
        f.write(encode_png(1024, big))
    print(f'写入 {src_path}')

    mid = resample(master, MASTER, 256)
    png256 = os.path.join(assets, 'copul-256.png')
    with open(png256, 'wb') as f:
        f.write(encode_png(256, mid))
    print(f'写入 {png256}')

    # ---- 托盘图标两套（R-02 / R-03）----
    # 只出托盘真正用得到的尺寸：16/20/24/32 与 @2x（32/40/48/64）。
    # 不做 128/256 —— 托盘永远不会用到，徒增文件体积。
    TRAY_SIZES = (16, 20, 24, 32, 40, 48, 64)

    print('渲染托盘浅色版（R-03：深方底 + 白波形，给浅色任务栏）...')
    tray_light_frames = []
    for sz in TRAY_SIZES:
        src = master_small if sz <= SMALL_CUTOFF else master
        tray_light_frames.append((sz, encode_png(sz, resample(src, MASTER, sz))))

    print('渲染托盘深色版（R-02：亮方底 + 深波形，给深色任务栏）...')
    m_light_master = render_master(small=False, dark=True)
    m_light_small = render_master(small=True, dark=True)
    tray_dark_frames = []
    for sz in TRAY_SIZES:
        src = m_light_small if sz <= SMALL_CUTOFF else m_light_master
        tray_dark_frames.append((sz, encode_png(sz, resample(src, MASTER, sz))))

    tray_light_path = os.path.join(assets, 'copul-tray-light.ico')
    write_ico(tray_light_path, tray_light_frames)
    print(f'写入 {tray_light_path}（{os.path.getsize(tray_light_path)} 字节）')

    tray_dark_path = os.path.join(assets, 'copul-tray-dark.ico')
    write_ico(tray_dark_path, tray_dark_frames)
    print(f'写入 {tray_dark_path}（{os.path.getsize(tray_dark_path)} 字节）')

    # 托盘还要 PNG（部分场景直接读 PNG 更省事，R-02 规格里也写了 .png）
    for sz in (16, 32):
        for name, src_master in (('light', master_small if sz <= SMALL_CUTOFF else master),
                                 ('dark', m_light_small if sz <= SMALL_CUTOFF else m_light_master)):
            path = os.path.join(assets, f'copul-tray-{name}-{sz}.png')
            with open(path, 'wb') as f:
                f.write(encode_png(sz, resample(src_master, MASTER, sz)))

    # 验收用：把关键尺寸拼一张放大对照图（白底 + 深底各一半），一次看清两种主题下的观感
    if '--preview' in sys.argv:
        make_preview(master, master_small, SMALL_CUTOFF, preview_dir)


def make_preview(master, master_small, cutoff, out_dir):
    """放大对照图：上排白底、下排深底，16/24/32/48/64 放大到统一高度。"""
    heights = [16, 24, 32, 48, 64]
    zoom = 4
    cell = max(heights) * zoom
    pad = 12
    cols = len(heights)
    W = cols * (cell + pad) + pad
    H = 2 * (cell + pad) + pad

    canvas = bytearray(W * H * 3)
    bg_light = (0xF2, 0xF4, 0xF7)
    bg_dark = (0x0B, 0x0E, 0x13)

    for row, bg in enumerate((bg_light, bg_dark)):
        for y in range(row * (cell + pad) + pad, (row + 1) * (cell + pad)):
            if y >= H:
                break
            for x in range(W):
                o = (y * W + x) * 3
                canvas[o:o + 3] = bytes(bg)

    for col, h in enumerate(heights):
        rgba = resample(master, MASTER, h)
        # 放大：最近邻（放大只是看形状，不引入插值模糊）
        for dy in range(cell):
            sy = dy // zoom
            if sy >= h:
                continue
            for dx in range(cell):
                sx = dx // zoom
                if sx >= h:
                    continue
                si = (sy * h + sx) * 4
                a = rgba[si + 3]
                if a == 0:
                    continue
                for row, bg in enumerate((bg_light, bg_dark)):
                    oy = row * (cell + pad) + pad + dy
                    ox = pad + col * (cell + pad) + dx
                    o = (oy * W + ox) * 3
                    blend = [int(rgba[si + c] * a / 255 + bg[c] * (255 - a) / 255) for c in range(3)]
                    canvas[o:o + 3] = bytes(blend)

    raw = bytearray()
    for y in range(H):
        raw.append(0)
        raw += canvas[y * W * 3:(y + 1) * W * 3]

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack('>IIBBBBB', W, H, 8, 2, 0, 0, 0)
    path = __import__('os').path.join(out_dir, 'preview-icon-sizes.png')
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
                chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))
    print(f'写入 {path}')


if __name__ == '__main__':
    main()
