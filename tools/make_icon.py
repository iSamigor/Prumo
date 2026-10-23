"""Generates src/Prumo/Assets/prumo.ico from vector-ish shapes.

Design: dark rounded square (#0F172A) + a plumb bob teal->cyan (#2DD4BF -> #22D3EE)
hanging from a thin vertical thread. Small sizes drop the thread and enlarge the bob.

Run: python tools/make_icon.py
"""
from __future__ import annotations

import io
import struct
import sys
from pathlib import Path

from PIL import Image, ImageDraw

ROOT = Path(__file__).resolve().parent.parent
OUT_ICO = ROOT / "src" / "Prumo" / "Assets" / "prumo.ico"

SIZES = [16, 20, 24, 32, 40, 48, 64, 256]
SS = 4  # supersampling factor

BG = (0x0F, 0x17, 0x2A)
TEAL = (0x2D, 0xD4, 0xBF)
CYAN = (0x22, 0xD3, 0xEE)


def bez(p0, p1, p2, p3, n=64):
    pts = []
    for i in range(n + 1):
        t = i / n
        mt = 1.0 - t
        x = mt**3 * p0[0] + 3 * mt * mt * t * p1[0] + 3 * mt * t * t * p2[0] + t**3 * p3[0]
        y = mt**3 * p0[1] + 3 * mt * mt * t * p1[1] + 3 * mt * t * t * p2[1] + t**3 * p3[1]
        pts.append((x, y))
    return pts


def lerp(c0, c1, t):
    return tuple(round(a + (b - a) * t) for a, b in zip(c0, c1))


def render(size: int) -> Image.Image:
    s = size * SS
    img = Image.new("RGBA", (s, s), (0, 0, 0, 0))
    draw = ImageDraw.Draw(img)
    draw.rounded_rectangle([0, 0, s - 1, s - 1], radius=s * 0.22, fill=BG + (255,))

    small = size <= 24  # thread would turn to mush here
    cx = s / 2

    if small:
        y0, tip = 0.19 * s, 0.875 * s
        hw, sw = 0.20 * s, 0.10 * s
    else:
        y0, tip = 0.33 * s, 0.875 * s
        hw, sw = 0.165 * s, 0.08 * s

    body = tip - y0
    widest = y0 + body * 0.24  # square shoulders, then a straight conical taper

    silhouette = []
    silhouette += bez((cx - sw, y0), (cx - sw - 0.02 * s, y0 + 0.015 * s),
                      (cx - hw, widest - 0.07 * s), (cx - hw, widest))
    silhouette.append((cx, tip))  # straight sides -> plumb bob cone
    silhouette.append((cx + hw, widest))
    silhouette += list(reversed(bez((cx + sw, y0), (cx + sw + 0.02 * s, y0 + 0.015 * s),
                                    (cx + hw, widest - 0.07 * s), (cx + hw, widest))))[1:]
    silhouette += bez((cx + sw, y0), (cx + 0.03 * s, y0 - 0.016 * s),
                      (cx - 0.03 * s, y0 - 0.016 * s), (cx - sw, y0))

    mask = Image.new("L", (s, s), 0)
    mdraw = ImageDraw.Draw(mask)
    mdraw.polygon(silhouette, fill=255)

    top = y0
    if not small:
        thread_w = max(s * 0.016, 1.2 * SS)
        thread_top = 0.085 * s
        mdraw.rectangle([cx - thread_w / 2, thread_top, cx + thread_w / 2, y0 + 1], fill=255)
        top = thread_top

    gradient = Image.new("RGB", (1, s))
    for y in range(s):
        t = min(max((y - top) / (tip - top), 0.0), 1.0)
        gradient.putpixel((0, y), lerp(TEAL, CYAN, t))
    gradient = gradient.resize((s, s))

    img.paste(gradient, (0, 0), mask)
    return img.resize((size, size), Image.LANCZOS)


def dib_entry(im: Image.Image, size: int) -> bytes:
    px = im.load()
    rows = []
    for y in range(size - 1, -1, -1):
        row = bytearray()
        for x in range(size):
            r, g, b, a = px[x, y]
            row += bytes((b, g, r, a))
        rows.append(bytes(row))
    xor = b"".join(rows)
    stride = ((size + 31) // 32) * 4
    and_mask = b"\x00" * (stride * size)  # alpha channel decides transparency
    header = struct.pack("<IiiHHIIiiII", 40, size, size * 2, 1, 32, 0,
                         len(xor) + len(and_mask), 0, 0, 0, 0)
    return header + xor + and_mask


def build_ico(images: dict[int, Image.Image]) -> bytes:
    blobs = []
    for size in SIZES:
        if size >= 256:
            buf = io.BytesIO()
            images[size].save(buf, format="PNG", optimize=True)
            blobs.append(buf.getvalue())
        else:
            blobs.append(dib_entry(images[size], size))

    header = struct.pack("<HHH", 0, 1, len(SIZES))
    offset = 6 + 16 * len(SIZES)
    entries = b""
    for size, blob in zip(SIZES, blobs):
        dim = 0 if size >= 256 else size
        entries += struct.pack("<BBBBHHII", dim, dim, 0, 0, 1, 32, len(blob), offset)
        offset += len(blob)
    return header + entries + b"".join(blobs)


def main() -> int:
    OUT_ICO.parent.mkdir(parents=True, exist_ok=True)
    images = {size: render(size) for size in SIZES}
    OUT_ICO.write_bytes(build_ico(images))
    print(f"wrote {OUT_ICO} ({OUT_ICO.stat().st_size} bytes, sizes {SIZES})")

    if len(sys.argv) > 1:
        preview_dir = Path(sys.argv[1])
        preview_dir.mkdir(parents=True, exist_ok=True)
        images[256].save(preview_dir / "prumo-icon-256.png")

        pad, gap = 16, 12
        strip_w = pad * 2 + sum(SIZES) + gap * (len(SIZES) - 1)
        strip_h = pad * 2 + 256 + 40
        strip = Image.new("RGBA", (strip_w, strip_h), (0xF1, 0xF5, 0xF9, 255))
        x = pad
        for size in SIZES:
            strip.alpha_composite(images[size], (x, pad + 256 - size))  # baseline aligned
            x += size + gap
        strip.save(preview_dir / "prumo-icon-sizes.png")
        print(f"previews in {preview_dir}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
