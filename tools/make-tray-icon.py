"""生成 CoPul 托盘图标 assets/copul.ico（纯标准库，无 PIL）。

本机 pip 无源、Add-Type 被禁，所以手写 PNG（zlib）+ ICO 容器。
Vista 以后的 ICO 允许每帧直接存 PNG，Windows 10/11 读取正常。

图标构图（32x32 视觉基准，按比例缩放到各尺寸）：
  - 深色圆角方块底 #0F1216（10.3 的背景色），1px 亮边 #2A3138 让它从白色托盘里浮出来
  - 中间一条脉搏折线 #4FB3A6（10.3 的 GPU 语义色，明度 65%，不刺眼）
  不做渐变/发光 —— 16x16 下会糊成一团，且 10.3 明确禁止霓虹感。

用法：
  python tools/make-tray-icon.py
  -> src/CoPul.App/assets/copul.ico
"""

import math
import struct
import zlib

SIZES = (16, 24, 32, 48, 64)

BG = (0x0F, 0x12, 0x16, 255)          # 背景
EDGE = (0x2A, 0x31, 0x38, 255)        # 边缘描边
PULSE = (0x4F, 0xB3, 0xA6, 255)       # 脉搏线

# 脉搏折线，归一化坐标（0..1），按 32x32 设计的比例。
# 形状取标准心电图的 QRS 波群：基线 → 小 dip（Q）→ 高尖峰（R）→ 深谷（S）→ 回基线。
# 只画成一个对称的"山"会像字母 M，加上 Q 与 S 才读得出是心跳。
PULSE_PTS = [
    (0.13, 0.54), (0.30, 0.54), (0.38, 0.62),
    (0.46, 0.26), (0.56, 0.74), (0.64, 0.49), (0.72, 0.54), (0.87, 0.54),
]

# 小尺寸（16 / 24）单独一套点位：像素太少，Q 波和 S 波会糊成一块色斑。
# 只留「基线 -> 单尖峰 -> 基线」，配上更粗的线，16px 下才读得出是个波。
PULSE_PTS_SMALL = [
    (0.10, 0.56), (0.34, 0.56), (0.50, 0.18), (0.66, 0.82), (0.80, 0.56), (0.90, 0.56),
]


def rounded_rect_alpha(x, y, size, radius):
    """点 (x,y) 在圆角矩形内返回 True。用超级椭圆近似，避免写四段圆弧。"""
    half = size / 2.0
    cx = size - 1 - x          # 以中心为原点，像素中心取整数
    cy = size - 1 - y
    dx = abs(cx - half + 0.5)
    dy = abs(cy - half + 0.5)
    inner = half - radius
    if dx <= inner or dy <= inner:
        return True
    # 圆角区：到角点圆心 (inner, inner) 的距离
    ox = dx - inner
    oy = dy - inner
    return (ox * ox + oy * oy) <= radius * radius


def seg_dist(px, py, ax, ay, bx, by):
    vx, vy = bx - ax, by - ay
    wx, wy = px - ax, py - ay
    L2 = vx * vx + vy * vy
    t = 0.0 if L2 == 0 else max(0.0, min(1.0, (wx * vx + wy * vy) / L2))
    dx, dy = px - (ax + t * vx), py - (ay + t * vy)
    return math.hypot(dx, dy)


def render(size):
    """返回 size x size 的 RGBA 像素行列表。"""
    radius = max(2.0, size * 0.22)
    # 小尺寸要更粗的线才看得见：16px 下 1.4px 的线只有 1 个像素宽，会被抗锯齿冲淡
    stroke = max(1.6, size * (0.13 if size <= 24 else 0.085))
    edge_w = max(0.9, size * 0.03)

    pts_src = PULSE_PTS_SMALL if size <= 24 else PULSE_PTS
    pts = [(x * (size - 1), y * (size - 1)) for x, y in pts_src]

    rows = []
    for y in range(size):
        row = []
        for x in range(size):
            if not rounded_rect_alpha(x + 0.5, y + 0.5, size, radius):
                row.append((0, 0, 0, 0))
                continue

            # 底
            color = BG

            # 边缘描边：靠近圆角矩形边界的一圈
            if not rounded_rect_alpha(x + 0.5 - edge_w, y + 0.5, size, radius) or \
               not rounded_rect_alpha(x + 0.5 + edge_w, y + 0.5, size, radius) or \
               not rounded_rect_alpha(x + 0.5, y + 0.5 - edge_w, size, radius) or \
               not rounded_rect_alpha(x + 0.5, y + 0.5 + edge_w, size, radius):
                color = EDGE

            # 脉搏线：到折线的距离小于半线宽就着色，并按距离做 1px 抗锯齿
            d = min(seg_dist(x + 0.5, y + 0.5, pts[i][0], pts[i][1],
                             pts[i + 1][0], pts[i + 1][1])
                    for i in range(len(pts) - 1))
            half = stroke / 2.0
            if d <= half + 0.5:
                # 覆盖率：<=half 全实心，到 half+0.5 渐隐
                a = 1.0 if d <= half - 0.5 else max(0.0, (half + 0.5 - d))
                a = min(1.0, a)
                r = int(PULSE[0] * a + color[0] * (1 - a))
                g = int(PULSE[1] * a + color[1] * (1 - a))
                b = int(PULSE[2] * a + color[2] * (1 - a))
                color = (r, g, b, 255)

            row.append(color)
        rows.append(row)
    return rows


def encode_png(rows):
    size = len(rows)
    raw = bytearray()
    for row in rows:
        raw.append(0)                      # filter: none
        for (r, g, b, a) in row:
            raw += bytes((r, g, b, a))

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack('>IIBBBBB', size, size, 8, 6, 0, 0, 0)
    return (b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
            chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))


def main():
    frames = [(s, encode_png(render(s))) for s in SIZES]

    header = struct.pack('<HHH', 0, 1, len(frames))
    offset = 6 + 16 * len(frames)
    entries = b''
    for size, data in frames:
        entries += struct.pack('<BBBBHHII',
                               0 if size >= 256 else size,
                               0 if size >= 256 else size,
                               0, 0, 1, 32, len(data), offset)
        offset += len(data)

    out = 'src/CoPul.App/assets/copul.ico'
    with open(out, 'wb') as f:
        f.write(header + entries + b''.join(d for _, d in frames))
    print(f'wrote {out}: {len(frames)} frames {[s for s, _ in frames]}')


if __name__ == '__main__':
    main()
