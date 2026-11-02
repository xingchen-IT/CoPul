"""读 PNG 指定坐标的 RGB（纯标准库，支持 8bit RGB/RGBA、非隔行）。

用法：python tools/png-pixel.py <png> x,y [x,y ...]
"""
import struct
import sys
import zlib


def read_png(path):
    d = open(path, "rb").read()
    i = 8
    w = h = ct = 0
    idat = b""
    while i < len(d):
        ln = struct.unpack(">I", d[i:i + 4])[0]
        typ = d[i + 4:i + 8]
        data = d[i + 8:i + 8 + ln]
        if typ == b"IHDR":
            w, h, _bd, ct = struct.unpack(">IIBB", data[:10])
        elif typ == b"IDAT":
            idat += data
        i += 12 + ln
    raw = zlib.decompress(idat)
    bpp = 4 if ct == 6 else 3
    stride = w * bpp
    out = bytearray()
    prev = bytearray(stride)
    pos = 0
    for _y in range(h):
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
                a = line[x - bpp] if x >= bpp else 0
                line[x] = (line[x] + ((a + prev[x]) >> 1)) & 255
        elif f == 4:
            for x in range(stride):
                a = line[x - bpp] if x >= bpp else 0
                c = prev[x - bpp] if x >= bpp else 0
                p = a + prev[x] - c
                pa, pb, pc = abs(p - a), abs(p - prev[x]), abs(p - c)
                pr = a if (pa <= pb and pa <= pc) else (prev[x] if pb <= pc else c)
                line[x] = (line[x] + pr) & 255
        out += line
        prev = line
    return w, h, bpp, bytes(out)


def main():
    path = sys.argv[1]
    w, h, bpp, px = read_png(path)
    print(f"{path} {w}x{h} bpp={bpp}")
    for arg in sys.argv[2:]:
        x, y = (int(v) for v in arg.split(","))
        if not (0 <= x < w and 0 <= y < h):
            print(f"  ({x},{y}) 越界")
            continue
        o = (y * w + x) * bpp
        r, g, b = px[o], px[o + 1], px[o + 2]
        print(f"  ({x},{y}) = {r},{g},{b}  luma={(r * 299 + g * 587 + b * 114) // 1000}")


main()
