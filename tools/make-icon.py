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
# 几何定义：全部用 0..1 归一化坐标，按目标尺寸缩放
#
# 最终版（2026-11-02）—— 干净脉冲 + 两端焊点
# ---------------------------------------------------------------------------
# 前四版的经验（全部实测截图对比过）：
#   1. 把"波形"与"电路走线"当两套图形叠加 → 杂乱，像"心电图旁边又画了几根线"
#   2. 改成一条连续路径（走线→脉冲→走线）→ 结构对了，但分段线宽在斜线
#      段上必然粘连：R 峰与 S 谷的 x 间距仅 ~0.05，脉冲段一加粗就糊成一团
#   3. 用矩形距离场求尖角 → 斜线段被沿坐标轴平切，脉冲方波化
#   4. 修成沿法向的平头方笔 → 尖角对了，但"暧昧的粗线段"问题依旧
#
# 结论：**分段线宽这个维度本身要砍掉**。线宽一均匀，粘连问题从根上消失，
# 尖峰完全靠几何角度表达（角度够尖，细线也像脉冲；角度不尖，粗线只会更糊）。
#
# 那"芯"从哪来？不是靠走线，而是靠两个更省的元素：
#   · **圆角方底** —— 现代应用的容器形态，本身就是"芯片/面板"的语义
#   · **两端焊点** —— PCB 最小的视觉语素（一颗焊盘），放两个在波形两端，
#     "这是一条走在线路板上的信号"立刻成立，且不会造成任何杂乱
#
# 这样在 16px 下：方底是纯色块、波形是 3-4 段折线、焊点退化成 2 个像素点 ——
# 三层全部保得住，这是图标能认出来的最低要求。
# ---------------------------------------------------------------------------

# 脉冲波形主体。均匀线宽。角度按"标准心电 QRS"取：
# 基线 → R 峰（陡）→ S 谷（更深）→ 回基线。
# 峰的 x 半宽 0.085、y 跨度 0.42 → 斜率约 2.8:1，够陡；
# 谷的 x 半宽 0.085、y 跨度 0.30 → 略缓，与峰形成不对称（真实 QRS 就不对称）。
WAVE = [
    (0.255, 0.545),      # 基线左端（焊点位置）
    (0.395, 0.545),
    (0.480, 0.155),      # R：主峰
    (0.565, 0.775),      # S：主谷
    (0.650, 0.545),      # 回基线
    (0.745, 0.545),      # 基线右端（焊点位置）
]

# 小尺寸简化：去掉基线的两端压平段，让峰谷更陡、更占面积。
# 16px 下 6 个点已经能保住"一峰一谷"，不需要再简化。
WAVE_SMALL = [
    (0.20, 0.545),
    (0.375, 0.545),
    (0.485, 0.135),      # 峰更高
    (0.585, 0.815),      # 谷更深
    (0.690, 0.545),
    (0.820, 0.545),
]

# 线宽（归一化）。均匀一档，不再按段区分 —— 这是本次设计的核心简化。
STROKE = 0.072
STROKE_SMALL = 0.118          # 小尺寸相对更粗，否则 16px 下的线不足 1px

# 两端焊点：正方形边长（归一化）+ 位置（取 WAVE 首末点）
PAD_SIZE = 0.105
PAD_SIZE_SMALL = 0.140

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

def render_master(small=False, dark=False):
    """渲染一张 MASTER×MASTER 的 RGBA 浮点缓冲（按 SS 超采样后平均）。

    small=True  用 WAVE_SMALL（更陡、更占面积），否则用 WAVE。两者都是均匀线宽。
    dark=True   用 R-02 那套亮方底 + 深波形（深色任务栏专用）。
    """
    tile = TILE_DARK if dark else TILE
    tile_rim = TILE_DARK_RIM if dark else TILE_RIM
    glyph = GLYPH_DARK if dark else GLYPH
    size = MASTER
    n = size * SS
    samples = [0.0] * (size * size * 4)

    wave = scale_pts(WAVE_SMALL if small else WAVE, size)
    stroke = (STROKE_SMALL if small else STROKE) * size
    half = stroke / 2.0
    pad = (PAD_SIZE_SMALL if small else PAD_SIZE) * size

    # 焊点：取波形首末点为中心的正方形
    pads = [(wave[0][0], wave[0][1]), (wave[-1][0], wave[-1][1])]

    rim_w = max(1.0, size * 0.012)
    radius = TILE_RADIUS * size

    step = 1.0 / SS
    inv = 1.0 / (SS * SS)

    for sy in range(n):
        py = (sy + 0.5) * step
        for sx in range(n):
            px = (sx + 0.5) * step

            if rounded_rect_cov(px, py, size, radius) <= 0:
                continue

            r, g, b, a = tile
            if rounded_rect_cov(px, py, size, radius) <= rim_w:
                r, g, b, _ = tile_rim

            # ---- 脉冲波形：均匀线宽，平头方笔（沿线段法向切）----
            hit = False
            for si in range(len(wave) - 1):
                ax, ay = wave[si]
                bx, by = wave[si + 1]
                dx, dy = bx - ax, by - ay
                seg_len = math.hypot(dx, dy)
                if seg_len < 1e-9:
                    continue
                ux, uy = dx / seg_len, dy / seg_len
                rx, ry = px - ax, py - ay
                u = (rx * ux + ry * uy) / seg_len
                v = abs(rx * (-uy) + ry * ux)
                # u 两端各外扩 10%：相邻段在尖角处轻微重叠，不留缺口
                if v <= half and -0.10 <= u <= 1.10:
                    hit = True
                    break

            # ---- 两端焊点：小方块（轴对齐，不随线段角度旋转）----
            if not hit:
                for cx, cy in pads:
                    if abs(px - cx) <= pad / 2.0 and abs(py - cy) <= pad / 2.0:
                        hit = True
                        break

            if hit:
                gr, gg, gb, _ = glyph
                r, g, b = gr, gg, gb

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

    print(f'渲染小尺寸母版 {MASTER}x{MASTER}（简化几何 PATH_SMALL）...')
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
