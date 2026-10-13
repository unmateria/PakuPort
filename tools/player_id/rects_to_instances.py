# -*- coding: utf-8 -*-
"""Bridge: turn the movs/stos rectangles of logs/blit.log into catalog instances.

WHY THIS EXISTS (finding of session 38, reported, not patched into shared tooling):
`tools/blit_catalog/build_catalog.py` builds assets and instances ONLY from
logs/blitraw.bin (the loose VRAM writes of PAKU_LOG_BLIT=2). The movs/stos blits go
to logs/blit.log as rectangles and the catalog keeps just an fnv->count summary of
them: no position, no instance, so they never reach `build_tracks.py`.
Under PAKU_INTERP_ONLY=1 that hides whole entities. In one test game the
player sprite is drawn with `movsw` by routine 03D4:05D6 at a fixed (144,145), so it is
absent from instances.jsonl and no track exists for it. (An earlier catalog did
have a player track only because it was captured in the LIFTED world, where the lifter
emits movs as inline loops and everything lands in blitraw.bin.)

This tool clusters the blit.log rectangles the same way build_catalog clusters loose
writes (time-gap bursts -> 8-connected components over byte cells) and appends the
resulting assets/instances to an existing catalog directory.

Identity of a rect asset is BY PROVENANCE, not by pixels: the sha1 of the sorted
(dx, dy, source-fnv | fill-value) triples of the component. blit.log does not carry
the written bytes, so `pixels` is a placeholder ('#') — enough for build_tracks.py,
which only asks whether the mask is non-empty, and deliberately unable to collide
with the real pixel masks produced by tools/blit_catalog/build_catalog.py.

Usage: python tools/player_id/rects_to_instances.py --logs <dir> --catalog <dir>
"""
import argparse, hashlib, json, os
from collections import Counter, deque

GAP_TICKS = 4000     # same burst split as build_catalog.py
MIN_CELLS = 4        # ignore specks


def addr_to_xy(lin, mode):
    """Linear VRAM address -> (byte-cell x index, y, pixels per byte). Same mapping as
    build_catalog.addr_to_xy, kept local so this tool stays standalone."""
    if mode in (4, 5, 6):
        off = (lin - 0xB8000)
        if not 0 <= off < 0x8000:
            return None
        off &= 0x3FFF
        if off >= 0x2000:
            y, xb = 2 * ((off - 0x2000) // 80) + 1, (off - 0x2000) % 80
        else:
            y, xb = 2 * (off // 80), off % 80
        return xb, y, (8 if mode == 6 else 4)
    if mode == 0x13:
        off = lin - 0xA0000
        if not 0 <= off < 64000:
            return None
        return off % 320, off // 320, 1
    return None


def parse(path):
    """blit.log -> list of (t, mode, lin, nbytes, identity, at), oldest first."""
    out = []
    for line in open(path, encoding='utf-8', errors='replace'):
        if not line.startswith('t='):
            continue
        f = dict(p.split('=', 1) for p in line.split() if '=' in p)
        ident = f.get('fnv') or ('fill:' + f.get('fill', '?'))
        nb = int(f.get('bytes', 0))
        lin = int(f['lin'], 16)
        if f.get('df') == '1':
            lin -= nb - 1
        out.append((int(f['t']), int(f['mode'], 16), lin, nb, ident, f.get('at', '?')))
    return out


def components(cells):
    seen, comps = set(), []
    for start in cells:
        if start in seen:
            continue
        comp, q = {}, deque([start])
        seen.add(start)
        while q:
            c = q.popleft()
            comp[c] = cells[c]
            for dx in (-1, 0, 1):
                for dy in (-1, 0, 1):
                    nb = (c[0] + dx, c[1] + dy)
                    if nb in cells and nb not in seen:
                        seen.add(nb)
                        q.append(nb)
        comps.append(comp)
    return comps


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--logs', default='logs')
    ap.add_argument('--catalog', required=True)
    args = ap.parse_args()

    recs = parse(os.path.join(args.logs, 'blit.log'))
    bursts, cur, last_t, last_mode = [], [], None, None
    for t, mode, lin, nb, ident, at in recs:
        if cur and (t - last_t > GAP_TICKS or mode != last_mode):
            bursts.append((last_mode, cur))
            cur = []
        cur.append((t, lin, nb, ident, at))
        last_t, last_mode = t, mode
    if cur:
        bursts.append((last_mode, cur))

    assets, instances = {}, []
    for mode, burst in bursts:
        cells, meta = {}, {}
        for t, lin, nb, ident, at in burst:
            for b in range(nb):
                p = addr_to_xy(lin + b, mode)
                if p is None:
                    continue
                key = (p[0], p[1])
                cells[key] = ident            # last write wins, as in build_catalog
                meta[key] = (t, at)
        for comp in components(cells):
            if len(comp) < MIN_CELLS:
                continue
            xs = [c[0] for c in comp]
            ys = [c[1] for c in comp]
            x0, y0 = min(xs), min(ys)
            ppb = 8 if mode == 6 else (4 if mode in (4, 5) else 1)
            sig = sorted((c[0] - x0, c[1] - y0, v) for c, v in comp.items())
            h = hashlib.sha1(repr(sig).encode()).hexdigest()
            a = assets.get(h)
            if a is None:
                # Procedencia del asset = la rutina DOMINANTE del componente, con desempate
                # por el write mas antiguo (la que empezo a dibujarlo). Antes se tomaba la
                # de `next(iter(comp))`, un elemento ARBITRARIO: un mismo sprite podia salir
                # con `at` distintos entre corridas, que es justo lo contrario de la
                # doctrina de identidad por procedencia (fleco anotado en su momento).
                cnt = Counter(meta[c][1] for c in comp)
                best = max(cnt.values())
                cands = {v for v, n in cnt.items() if n == best}
                first_at = meta[min(comp, key=lambda c: meta[c][0])][1]
                at_dom = first_at if first_at in cands else sorted(cands)[0]
                a = assets[h] = dict(id='rect_%04d' % len(assets), hash=h, mode=mode,
                                     w=(max(xs) - x0 + 1) * ppb, h=max(ys) - y0 + 1,
                                     appearances=0, at=at_dom,
                                     source='blit.log', pixels=['#'])
            a['appearances'] += 1
            instances.append(dict(asset=a['id'], t=min(meta[c][0] for c in comp),
                                  x=x0 * ppb, y=y0))

    cat_path = os.path.join(args.catalog, 'assets.json')
    cat = json.load(open(cat_path, encoding='utf-8'))
    cat['assets'] = [a for a in cat['assets'] if not a['id'].startswith('rect_')]
    cat['assets'] += sorted(assets.values(), key=lambda a: -a['appearances'])
    json.dump(cat, open(cat_path, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)

    ins_path = os.path.join(args.catalog, 'instances.jsonl')
    keep = [l for l in open(ins_path, encoding='utf-8') if '"rect_' not in l]
    with open(ins_path, 'w', encoding='utf-8') as f:
        f.writelines(keep)
        for i in instances:
            f.write(json.dumps(i) + '\n')
    print(f'{len(recs)} rects de blit.log -> {len(assets)} assets, '
          f'{len(instances)} instancias anadidas a {args.catalog}')


if __name__ == '__main__':
    main()
