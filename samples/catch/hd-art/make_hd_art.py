"""Draws high-resolution versions of CATCH's two sprites, for the remake-kit tutorial.

In a real remake this is the artist's (or an image model's) job: the kit tells you WHAT to draw,
at which size and where to save it, and you hand back PNG files. This script stands in for the
artist so the tutorial can be reproduced end to end with nothing but Python's standard library.

Usage:  python samples/catch/hd-art/make_hd_art.py <remake dir> <star key> <paddle key>
        e.g. python samples/catch/hd-art/make_hd_art.py games/catch/remake grupo-AEE58220 grupo-FAF9CA1C
The keys are the `destino` names that `pakuport remake study` writes in pendiente.json.
"""
import math
import os
import sys

sys.path.insert(0, os.path.join(os.path.dirname(__file__), '..', '..', '..', 'tools', 'remake_kit'))
import pngio  # noqa: E402

SCALE = 8


def star(size):
    """A soft four-pointed sparkle on black: white core, yellow rays."""
    rgba = bytearray(size * size * 4)
    c = (size - 1) / 2
    for y in range(size):
        for x in range(size):
            dx, dy = abs(x - c) / c, abs(y - c) / c
            ray = max(0.0, 1 - (dx * 6 + dy) / 1.1, 1 - (dy * 6 + dx) / 1.1)
            core = max(0.0, 1 - math.hypot(dx, dy) * 2.2)
            v = min(1.0, ray + core)
            white = min(1.0, core * 1.6)
            r = int(255 * v)
            g = int((200 + 55 * white) * v)
            b = int(255 * white * v)
            o = (y * size + x) * 4
            rgba[o:o + 4] = bytes((r, g, b, 255))
    return rgba


def paddle(w, h):
    """A rounded, glossy blue bar on black."""
    rgba = bytearray(w * h * 4)
    rad = h / 2
    for y in range(h):
        for x in range(w):
            cx = min(max(x + 0.5, rad), w - rad)
            inside = math.hypot(x + 0.5 - cx, y + 0.5 - rad) <= rad
            o = (y * w + x) * 4
            if not inside:
                rgba[o:o + 4] = bytes((0, 0, 0, 255))
                continue
            t = (y + 0.5) / h                       # 0 at the top, 1 at the bottom
            shine = max(0.0, 1 - abs(t - 0.25) * 6)
            r = int(30 + 60 * (1 - t) + 120 * shine)
            g = int(80 + 110 * (1 - t) + 100 * shine)
            b = int(150 + 105 * (1 - t) * 0.9 + 40 * shine)
            rgba[o:o + 4] = bytes((min(r, 255), min(g, 255), min(b, 255), 255))
    return rgba


def main():
    if len(sys.argv) != 4:
        print(__doc__)
        return 1
    hd = os.path.join(sys.argv[1], 'hd')
    os.makedirs(hd, exist_ok=True)
    s = 7 * SCALE
    pngio.escribir(os.path.join(hd, sys.argv[2] + '.png'), s, s, star(s))
    pw, ph = 24 * SCALE, 5 * SCALE
    pngio.escribir(os.path.join(hd, sys.argv[3] + '.png'), pw, ph, paddle(pw, ph))
    print('wrote', os.path.join(hd, sys.argv[2] + '.png'), 'and', os.path.join(hd, sys.argv[3] + '.png'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
