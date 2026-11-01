"""图标尺寸验收：把 16/24/32/48/64 各尺寸在真实像素下并排放出对照图。

为什么必须单独做这件事：图标画在 512×512 放大图里好看，不等于在 16px 下能认出来。
真实场景（任务栏 / 资源管理器小图标 / exe 文件列表）里用户看到的就是 16 和 32。
放大看会自我欺骗 —— 抗锯齿的过渡像素在放大后变成"柔和边缘"，实际在屏幕上就是糊。
所以这里严格按**原始像素**渲染，再最近邻放大（放大只是为了让人眼看清像素块，
不引入任何插值模糊），并且同时给白底和深底两种背景 —— 图标要在这两种底上都立得住。
"""

import struct
import sys
import zlib

sys.path.insert(0, r'D:\CoPul开发\tools')

from importlib import import_module
icon = import_module('make-icon')


def read_ico_frames(path):
    d = open(path, 'rb').read()
    n = struct.unpack('<H', d[4:6])[0]
    out = []
    for i in range(n):
        off = 6 + i * 16
        w, h, cc, r, planes, bpp, sz, dataoff = struct.unpack('<BBBBHHII', d[off:off + 16])
        out.append((w or 256, d[dataoff:dataoff + sz]))
    return out


def decode_png(png):
    i = 8
    w = h = 0
    idat = b''
    while i < len(png):
        ln = struct.unpack('>I', png[i:i + 4])[0]
        typ = png[i + 4:i + 8]
        data = png[i + 8:i + 8 + ln]
        if typ == b'IHDR':
            w, h, bd, ct = struct.unpack('>IIBB', data[:10])
        elif typ == b'IDAT':
            idat += data
        elif typ == b'IEND':
            break
        i += 12 + ln
    raw = zlib.decompress(idat)
    bpp = 4 if ct == 6 else 3
    stride = w * bpp
    out = bytearray()
    prev = bytearray(stride)
    pos = 0
    for y in range(h):
        f = raw[pos]
        pos += 1
        line = bytearray(raw[pos:pos + stride])
        pos += stride
        if f == 1:
            for x in range(bpp, stride):
                line[x] = (line[x] + line[x - bpp]) & 255
        elif f == 2:
            for x in range(stride):
                line[x] = (line[x] + prev[x]) & 255
        elif f == 3:
            for x in range(stride):
                line[x] = (line[x] + ((line[x - bpp] if x >= bpp else 0) + prev[x]) // 2) & 255
        elif f == 4:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                b = prev[x]
                c = prev[x - bpp] if x >= bpp else 0
                pp = a + b - c
                pa, pb, pc = abs(pp - a), abs(pp - b), abs(pp - c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                line[x] = (line[x] + pr) & 255
        out += line
        prev = line
    return w, h, bpp, bytes(out)


def main():
    frames = read_ico_frames(r'D:\CoPul开发\src\CoPul.App\assets\copul.ico')

    # 只取真实出现的小尺寸
    want = [16, 24, 32, 48, 64]
    zoom = 6
    cell = max(want) * zoom
    pad = 14
    cols = len(want)
    W = cols * (cell + pad) + pad
    H = 2 * (cell + pad) + pad

    bg_light = (0xEE, 0xF1, 0xF5)
    bg_dark = (0x20, 0x24, 0x2A)

    canvas = bytearray(W * H * 3)
    for row, bg in enumerate((bg_light, bg_dark)):
        y0 = row * (cell + pad) + pad
        for y in range(y0, min(y0 + cell, H)):
            for x in range(W):
                o = (y * W + x) * 3
                canvas[o:o + 3] = bytes(bg)

    for col, size in enumerate(want):
        png = dict(frames)[size]
        w, h, bpp, px = decode_png(png)
        scale = zoom
        for dy in range(cell):
            sy = dy // scale
            if sy >= h:
                continue
            for dx in range(cell):
                sx = dx // scale
                if sx >= w:
                    continue
                si = (sy * w + sx) * bpp
                a = px[si + 3] if bpp == 4 else 255
                if a == 0:
                    continue
                for row, bg in enumerate((bg_light, bg_dark)):
                    oy = row * (cell + pad) + pad + dy
                    ox = pad + col * (cell + pad) + dx
                    o = (oy * W + ox) * 3
                    blend = [int(px[si + c] * a / 255 + bg[c] * (255 - a) / 255) for c in range(3)]
                    canvas[o:o + 3] = bytes(blend)

    raw = bytearray()
    for y in range(H):
        raw.append(0)
        raw += canvas[y * W * 3:(y + 1) * W * 3]

    def chunk(tag, data):
        return (struct.pack('>I', len(data)) + tag + data +
                struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF))

    ihdr = struct.pack('>IIBBBBB', W, H, 8, 2, 0, 0, 0)
    path = r'C:\Users\user\AppData\Local\Temp\icon-sizes.png'
    with open(path, 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n' + chunk(b'IHDR', ihdr) +
                chunk(b'IDAT', zlib.compress(bytes(raw), 9)) + chunk(b'IEND', b''))
    print('written', path, f'({W}x{H})')


if __name__ == '__main__':
    main()
