# -*- coding: utf-8 -*-
"""PASO 1b of the remake pipeline: offline asset catalog from blit telemetry.

Reads logs/blit.log (movs/stos rectangles, PAKU_LOG_BLIT=1) and logs/blitraw.bin
(loose VRAM writes, PAKU_LOG_BLIT=2) and emits:
  <out>/assets.json      catalog: id, mode, WxH, mask hash, appearances, routine (at)
  <out>/instances.jsonl  one line per on-screen instance (asset, t, x, y, at) -> paso 2
  <out>/assets/*.png     one decoded PNG per asset

Clustering: bursts of raw writes split by time gap, then by connected components in
screen space. Decoders: CGA modes 4/5 (2bpp interleaved B800), mode 6 (1bpp), mode
13h (linear A000) and EGA/VGA planar modes 0Dh-12h (4 planes, 1bpp each -> 16 colors).

Usage: python tools/blit_catalog/build_catalog.py --logs logs --out games/<id>/catalog
"""
import argparse, hashlib, json, os, struct, sys, zlib
from collections import defaultdict, deque

GAP_TICKS = 4000          # ~3.3 ms at 0.838 us/tick: splits bursts between frames/sprites
MIN_PIXELS = 4            # ignore specks smaller than this many written bytes

CGA_PAL = [(0, 0, 0), (85, 255, 255), (255, 85, 255), (255, 255, 255)]

# EGA/VGA planar graphics modes -> (bytes per line, active lines). Derived from the mode
# table of src/PakuPort.Core/Platform/Int10hHandler.cs (bytes per line = Width / 8).
EGA_MODES = {0x0D: (40, 200), 0x0E: (80, 200), 0x0F: (80, 350),
             0x10: (80, 350), 0x11: (80, 480), 0x12: (80, 480)}

# 16-color EGA palette: the `ega` table of VideoState.LoadDefaultVgaPalette (6-bit DAC
# units) scaled to 8 bits (v * 255 / 63). After a BIOS mode set the attribute registers
# map index i onto that same entry i, so the pixel index indexes this table directly.
EGA_PAL = [(0, 0, 0), (0, 0, 170), (0, 170, 0), (0, 170, 170),
           (170, 0, 0), (170, 0, 170), (170, 85, 0), (170, 170, 170),
           (85, 85, 85), (85, 85, 255), (85, 255, 85), (85, 255, 255),
           (255, 85, 85), (255, 85, 255), (255, 255, 85), (255, 255, 255)]


def read_blitraw(path):
    """Yield (addr, val, at, t_abs, mode) from blitraw.bin.

    'W' records: addr is a CPU linear address and val one byte of pixel data.
    'P' records (planar modes): addr is the resolved plane address and val packs the four
    plane bytes after the write (p0 | p1<<8 | p2<<16 | p3<<24).
    """
    with open(path, 'rb') as f:
        data = f.read()
    if data[:4] != b'PBR1':
        sys.exit(f'{path}: bad header {data[:4]!r}')
    i, t, mode = 4, 0, -1
    out = []
    n = len(data)
    while i < n:
        k = data[i]
        if k == 0x4D:  # 'M' mode
            mode = data[i + 1]
            i += 2
        elif k == 0x57:  # 'W' write
            lin, = struct.unpack_from('<I', data, i + 1)
            val = data[i + 5]
            at, dt = struct.unpack_from('<II', data, i + 6)
            t += dt
            out.append((lin, val, at, t, mode))
            i += 14
        elif k == 0x50:  # 'P' planar write: plane address + the 4 planes after the write
            addr, = struct.unpack_from('<I', data, i + 1)
            p0, p1, p2, p3 = data[i + 5:i + 9]
            at, dt = struct.unpack_from('<II', data, i + 9)
            t += dt
            out.append((addr, p0 | (p1 << 8) | (p2 << 16) | (p3 << 24), at, t, mode))
            i += 17
        elif k == 0x41:  # 'A' abre grupo (PAKU_LOG_BLIT=3): aqui no aporta, se salta
            i += 1
        elif k == 0x42:  # 'B' cierra grupo y le pone identidad; lo consume tools/remake_kit
            at_, = struct.unpack_from('<I', data, i + 1)
            lc = data[i + 5]
            lp = data[i + 6 + lc]
            i += 7 + lc + lp
        else:
            sys.exit(f'{path}: unknown record 0x{k:02X} at {i}')
    return out


def parse_blit_log(path):
    """Parse the text rectangles of blit.log into dicts (movs/stos, identity = source FNV)."""
    rects = []
    if not os.path.exists(path):
        return rects
    for line in open(path, encoding='utf-8', errors='replace'):
        if not line.startswith('t='):
            continue
        f = dict(p.split('=', 1) for p in line.split() if '=' in p)
        rects.append(f)
    return rects


def pixels_per_byte(mode):
    if mode in EGA_MODES or mode == 6:
        return 8
    return 4 if mode in (4, 5) else 1


def addr_to_xy(lin, mode):
    """VRAM address -> (x_pixel_of_first_pixel, y, pixels_per_byte) or None.

    In planar modes `lin` is the plane address of a 'P' record, not a CPU address.
    """
    if mode in EGA_MODES:
        bpl, height = EGA_MODES[mode]
        if not 0 <= lin < bpl * height:
            return None  # off-screen page / scratch buffer: no screen coordinates
        return (lin % bpl) * 8, lin // bpl, 8
    if mode in (4, 5, 6):
        off = lin - 0xB8000
        if not 0 <= off < 0x8000:
            return None
        off &= 0x3FFF  # CGA has 16K; BC000-BFFFF mirrors B8000-BBFFF
        if off >= 0x2000:
            y = 2 * ((off - 0x2000) // 80) + 1
            xb = (off - 0x2000) % 80
        else:
            y = 2 * (off // 80)
            xb = off % 80
        ppb = 8 if mode == 6 else 4
        return xb * ppb, y, ppb
    if mode == 0x13:
        off = lin - 0xA0000
        if not 0 <= off < 64000:
            return None
        return off % 320, off // 320, 1
    return None


def decode_byte(val, mode):
    """One VRAM byte cell -> list of pixel indices, leftmost first."""
    if mode in EGA_MODES:
        p = (val & 0xFF, (val >> 8) & 0xFF, (val >> 16) & 0xFF, (val >> 24) & 0xFF)
        return [sum(((p[k] >> b) & 1) << k for k in range(4)) for b in range(7, -1, -1)]
    if mode in (4, 5):
        return [(val >> s) & 3 for s in (6, 4, 2, 0)]
    if mode == 6:
        return [(val >> s) & 1 for s in range(7, -1, -1)]
    return [val]


def components(cells):
    """cells: {(xb, y): val}. 8-neighbourhood on byte cells -> list of dicts."""
    seen, comps = set(), []
    for start in cells:
        if start in seen:
            continue
        comp, q = {}, deque([start])
        seen.add(start)
        while q:
            c = q.popleft()
            comp[c] = cells[c]
            cx, cy = c
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    nb = (cx + dx, cy + dy)
                    if nb in cells and nb not in seen:
                        seen.add(nb)
                        q.append(nb)
        comps.append(comp)
    return comps


def chars_per_pixel(mode):
    """Characters one pixel takes in a mask row. MUST be fixed-width or the row misaligns.

    256-colour modes (13h and friends: one byte per pixel) index up to 255, which does NOT
    fit in one hex digit. With a single digit, format(p,'x') emitted one OR two characters
    depending on the value, so every row silently grew and shifted: measured on a test game, a
    320-pixel-wide full-screen asset produced a 640-wide reference whose decoder then read
    each nibble as a separate colour. Reference images for every 13h game tested were unusable,
    which is why nobody could draw art from them.
    """
    return 2 if pixels_per_byte(mode) == 1 else 1


def mask_rows(comp, mode):
    """comp: {(xb,y): val} byte cells -> (rows list of strings, x0_px, y0, w, h).

    A row holds chars_per_pixel(mode) characters per pixel; '.' (repeated) means the game
    never wrote that pixel.
    """
    xs = [c[0] for c in comp]
    ys = [c[1] for c in comp]
    xb0, xb1, y0, y1 = min(xs), max(xs), min(ys), max(ys)
    ppb = pixels_per_byte(mode)
    cpp = chars_per_pixel(mode)
    vacio = '.' * cpp
    w = (xb1 - xb0 + 1) * ppb
    rows = []
    for y in range(y0, y1 + 1):
        row = []
        for xb in range(xb0, xb1 + 1):
            v = comp.get((xb, y))
            if v is None:
                row.extend([vacio] * ppb)
            else:
                row.extend(vacio if p == 0 else format(p, '0%dx' % cpp)
                           for p in decode_byte(v, mode))
        rows.append(''.join(row))
    return rows, xb0 * ppb, y0, w, y1 - y0 + 1


def write_png(path, rows, mode, palette=None):
    """Reference image of an asset. `palette` (list of (r,g,b) indexed by colour index, from
    the game's effective DAC) is what makes a 256-colour reference show the REAL colours; a
    grayscale placeholder is used when nobody captured it, and it must not be mistaken for
    the game's actual look."""
    cpp = chars_per_pixel(mode)
    h = len(rows)
    w = (len(rows[0]) // cpp) if rows else 0
    raw = bytearray()
    for r in rows:
        raw.append(0)  # filter none
        for i in range(0, len(r), cpp):
            ch = r[i:i + cpp]
            idx = 0 if ch[0] == '.' else int(ch, 16)
            if mode in EGA_MODES:
                rgb = EGA_PAL[idx & 15]   # BIOS default palette (game reprogramming not captured)
            elif mode in (4, 5, 6):
                rgb = CGA_PAL[idx & 3]
            elif palette and idx < len(palette):
                rgb = palette[idx]
            else:  # mode 13h without a captured DAC -> grayscale placeholder
                rgb = (idx, idx, idx)
            raw.extend(rgb)

    def chunk(tag, body):
        return (struct.pack('>I', len(body)) + tag + body
                + struct.pack('>I', zlib.crc32(tag + body) & 0xFFFFFFFF))

    png = (b'\x89PNG\r\n\x1a\n'
           + chunk(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 2, 0, 0, 0))
           + chunk(b'IDAT', zlib.compress(bytes(raw)))
           + chunk(b'IEND', b''))
    open(path, 'wb').write(png)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--logs', default='logs')
    ap.add_argument('--out', required=True)
    ap.add_argument('--min-count', type=int, default=1)
    args = ap.parse_args()

    raw_path = os.path.join(args.logs, 'blitraw.bin')
    events = read_blitraw(raw_path) if os.path.exists(raw_path) else []
    rect_lines = parse_blit_log(os.path.join(args.logs, 'blit.log'))
    print(f'{len(events)} escrituras sueltas, {len(rect_lines)} rects movs/stos')

    os.makedirs(os.path.join(args.out, 'assets'), exist_ok=True)

    # --- burst split by time gap and mode ---
    bursts, cur, last_t, last_mode = [], [], None, None
    skipped_modes = defaultdict(int)
    for lin, val, at, t, mode in events:
        if addr_to_xy(lin, mode) is None:
            skipped_modes[mode] += 1
            continue
        if cur and (t - last_t > GAP_TICKS or mode != last_mode):
            bursts.append((last_mode, cur))
            cur = []
        cur.append((lin, val, at, t))
        last_t, last_mode = t, mode
    if cur:
        bursts.append((last_mode, cur))

    # --- per burst: byte-cell grid (last write wins), then spatial components ---
    assets = {}     # hash -> asset dict
    instances = []
    for mode, burst in bursts:
        cells, at_of, t_of = {}, {}, {}
        for lin, val, at, t in burst:
            x, y, ppb = addr_to_xy(lin, mode)
            key = (x // ppb, y)
            cells[key] = val
            at_of[key], t_of[key] = at, t
        for comp in components(cells):
            if len(comp) < MIN_PIXELS:
                continue
            rows, x0, y0, w, h = mask_rows(comp, mode)
            hsh = hashlib.sha1(('%dx%d\n' % (w, h) + '\n'.join(rows)).encode()).hexdigest()
            some = next(iter(comp))
            a = assets.get(hsh)
            if a is None:
                a = assets[hsh] = dict(id=f'asset_{len(assets):04d}', hash=hsh, mode=mode,
                                       w=w, h=h, rows=rows, appearances=0,
                                       at=f'{at_of[some] >> 16:04X}:{at_of[some] & 0xFFFF:04X}')
            a['appearances'] += 1
            instances.append(dict(asset=a['id'], t=t_of[some], x=x0, y=y0))

    kept = [a for a in assets.values() if a['appearances'] >= args.min_count]
    kept.sort(key=lambda a: -a['appearances'])
    for a in kept:
        png = os.path.join(args.out, 'assets', a['id'] + '.png')
        write_png(png, a['rows'], a['mode'])
        a['png'] = os.path.relpath(png, args.out).replace('\\', '/')

    # --- movs/stos rectangles: identity by source FNV (pixels unknown at log level) ---
    rect_index = defaultdict(lambda: dict(count=0, at=None, bytes=0, file=None))
    for f in rect_lines:
        fnv = f.get('fnv') or f.get('fill')
        if not fnv:
            continue
        r = rect_index[fnv]
        r['count'] += 1
        r['at'] = r['at'] or f.get('at')
        r['bytes'] = int(f.get('bytes', 0))
        r['file'] = r['file'] or f.get('file')

    out = dict(
        source_logs=args.logs,
        skipped_writes_by_mode={f'{m:02X}': n for m, n in sorted(skipped_modes.items())},
        assets=[{k: v for k, v in a.items() if k != 'rows'} | {'pixels': a['rows']}
                for a in kept],
        rects=[dict(fnv=k, **v) for k, v in
               sorted(rect_index.items(), key=lambda kv: -kv[1]['count'])],
    )
    with open(os.path.join(args.out, 'assets.json'), 'w', encoding='utf-8') as f:
        json.dump(out, f, indent=1, ensure_ascii=False)
    with open(os.path.join(args.out, 'instances.jsonl'), 'w', encoding='utf-8') as f:
        for ins in instances:
            f.write(json.dumps(ins) + '\n')
    print(f'{len(kept)} assets, {len(instances)} instancias, {len(rect_index)} rects '
          f'-> {args.out}')
    if skipped_modes:
        print('AVISO escrituras saltadas por modo (sin decodificador o fuera de pantalla):',
              dict(skipped_modes))


if __name__ == '__main__':
    main()
