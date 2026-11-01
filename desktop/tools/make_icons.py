"""Draws the app and tray icons into src/LiteTunnel.App/Assets. Standard library only.

The shape is the launcher icon of the phone apps: a white arch with a keyhole
on a blue rounded square (viewport 108, cropped around the arch).
"""
import math
import struct
import sys
import zlib
from pathlib import Path

ASSETS = Path(sys.argv[1]) if len(sys.argv) > 1 else Path(__file__).resolve().parent.parent / "src/LiteTunnel.App/Assets"
BLUE = (0x1F, 0x6F, 0xEB)
GRAY = (0x8E, 0x8E, 0x93)
WHITE = (0xFF, 0xFF, 0xFF)

# The crop of the 108 viewport the icon shows, centred on the arch.
X0, Y0, SPAN = 14.0, 12.0, 80.0


def in_rounded_square(x, y):
    r = 0.22
    cx = min(max(x, r), 1 - r)
    cy = min(max(y, r), 1 - r)
    return (x - cx) ** 2 + (y - cy) ** 2 <= r * r


def in_arch(x, y):
    d = math.hypot(x - 54, y - 52)
    if y < 52:
        return 14 <= d <= 24
    if y > 76:
        return False
    if 30 <= x <= 40 or 68 <= x <= 78:
        return True
    return 51 <= x <= 57 and (58 <= y <= 64 or 68 <= y <= 76)


def render(size, background):
    samples = 4
    pixels = []
    for py in range(size):
        row = []
        for px in range(size):
            bg = fg = 0
            for sy in range(samples):
                for sx in range(samples):
                    u = (px + (sx + 0.5) / samples) / size
                    v = (py + (sy + 0.5) / samples) / size
                    if not in_rounded_square(u, v):
                        continue
                    bg += 1
                    if in_arch(X0 + u * SPAN, Y0 + v * SPAN):
                        fg += 1
            n = samples * samples
            alpha = bg / n
            if bg == 0:
                row.append((0, 0, 0, 0))
                continue
            t = fg / bg
            color = tuple(round(background[i] * (1 - t) + WHITE[i] * t) for i in range(3))
            row.append(color + (round(alpha * 255),))
        pixels.append(row)
    return pixels


def png(pixels):
    size = len(pixels)
    raw = b"".join(b"\x00" + bytes(c for p in row for c in p) for row in pixels)

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xFFFFFFFF)

    return (b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", size, size, 8, 6, 0, 0, 0))
            + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


def dib(pixels):
    size = len(pixels)
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0, 0, 0, 0, 0, 0)
    color = b"".join(bytes((p[2], p[1], p[0], p[3])) for row in reversed(pixels) for p in row)
    stride = (size + 31) // 32 * 4
    mask = bytes(stride * size)  # all opaque in the mask: alpha decides
    return header + color + mask


def ico(images):
    entries, blobs = b"", b""
    offset = 6 + 16 * len(images)
    for size, data in images:
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(data), offset)
        blobs += data
        offset += len(data)
    return struct.pack("<HHH", 0, 1, len(images)) + entries + blobs


def main():
    ASSETS.mkdir(parents=True, exist_ok=True)
    big = render(256, BLUE)
    (ASSETS / "LiteTunnel.png").write_bytes(png(big))
    images = [(s, dib(render(s, BLUE))) for s in (16, 32, 48)] + [(256, png(big))]
    (ASSETS / "LiteTunnel.ico").write_bytes(ico(images))
    (ASSETS / "tray-on.png").write_bytes(png(render(64, BLUE)))
    (ASSETS / "tray-off.png").write_bytes(png(render(64, GRAY)))


if __name__ == "__main__":
    main()
