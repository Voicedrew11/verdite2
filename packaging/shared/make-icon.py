#!/usr/bin/env python3
"""Generate the placeholder icons.

Deliberately dependency-free (zlib and struct only) so it runs anywhere the rest
of the packaging does. The palette is the one the game's own map is drawn in --
see MapRender.DrawNative -- so the placeholder at least belongs to this project
rather than being a stock shape. It is still a placeholder; see README.md.
"""
import struct
import zlib

FIELD = (0x3A, 0x52, 0x3A)   # the map's field
INK = (0x0E, 0x20, 0x0E)     # its ink
EDGE = (0x7B, 0x8C, 0x7F)    # its bevel highlight


def render(n):
    px = [[FIELD for _ in range(n)] for _ in range(n)]
    s = n / 256.0

    def rect(x0, y0, x1, y1, c):
        for y in range(max(0, int(y0)), min(n, int(y1))):
            for x in range(max(0, int(x0)), min(n, int(x1))):
                px[y][x] = c

    b = max(1, round(10 * s))
    i = max(1, round(8 * s))
    rect(0, 0, n, b, EDGE); rect(0, n - b, n, n, EDGE)
    rect(0, 0, b, n, EDGE); rect(n - b, 0, n, n, EDGE)
    rect(b, b, n - b, b + i, INK); rect(b, n - b - i, n - b, n - b, INK)
    rect(b, b, b + i, n - b, INK); rect(n - b - i, b, n - b, n - b, INK)

    # The V, as two strokes, so it stays crisp when scaled down.
    w = max(1, round(13 * s))
    for t in range(round(150 * s)):
        y = 56 * s + t
        rect(74 * s + t * 0.36, y, 74 * s + t * 0.36 + w, y + 1, INK)
        rect(n - 74 * s - t * 0.36 - w, y, n - 74 * s - t * 0.36, y + 1, INK)

    return px


def png(px):
    n = len(px)
    raw = b"".join(b"\x00" + bytes(v for p in row for v in p) for row in px)

    def chunk(tag, data):
        c = tag + data
        return struct.pack(">I", len(data)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n"
            + chunk(b"IHDR", struct.pack(">IIBBBBB", n, n, 8, 2, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9))
            + chunk(b"IEND", b""))


def ico(sizes):
    """A PNG-compressed ICO, which every Windows since Vista reads."""
    images = [png(render(s)) for s in sizes]
    out = struct.pack("<HHH", 0, 1, len(images))
    offset = 6 + 16 * len(images)
    for s, data in zip(sizes, images):
        out += struct.pack("<BBBBHHII", s % 256, s % 256, 0, 0, 1, 32, len(data), offset)
        offset += len(data)
    return out + b"".join(images)


if __name__ == "__main__":
    import os
    here = os.path.dirname(os.path.abspath(__file__))
    open(os.path.join(here, "verdite2.png"), "wb").write(png(render(256)))
    open(os.path.join(here, "verdite2.ico"), "wb").write(ico([16, 32, 48, 256]))
    print("wrote verdite2.png and verdite2.ico")
