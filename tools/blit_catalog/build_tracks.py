# -*- coding: utf-8 -*-
"""PASO 2 of the remake pipeline: entity tracks + animation grouping, offline.

Input:  <catalog_dir>/assets.json + instances.jsonl (from build_catalog.py)
Output: <catalog_dir>/tracks.json — trajectories with birth/death, velocity, the set
        of assets that alternate on the track (= animation frames) and a continuity
        metric (fraction of consecutive steps <= JUMP_PX).

Erase heuristic: assets whose mask is entirely '.' (pure background/zero writes) are
draw/erase pairs' eraser side — they are dropped before tracking.
"""
import argparse, json, os, sys

TRACK_GAP_TICKS = 480_000   # ~0.4 s at 0.838 us/tick: max silence inside one track
JUMP_PX = 24                # max movement between consecutive frames of one entity
MIN_POINTS = 4              # tracks shorter than this are noise


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('catalog_dir')
    args = ap.parse_args()

    cat = json.load(open(os.path.join(args.catalog_dir, 'assets.json'), encoding='utf-8'))
    solid = {a['id'] for a in cat['assets']
             if any(ch != '.' for row in a['pixels'] for ch in row)}

    instances = []
    for line in open(os.path.join(args.catalog_dir, 'instances.jsonl'), encoding='utf-8'):
        ins = json.loads(line)
        if ins['asset'] in solid:
            instances.append(ins)
    instances.sort(key=lambda i: i['t'])

    tracks = []          # each: dict(points=[(t,x,y)], assets=set)
    open_tracks = []
    for ins in instances:
        t, x, y = ins['t'], ins['x'], ins['y']
        open_tracks = [tr for tr in open_tracks if t - tr['points'][-1][0] <= TRACK_GAP_TICKS]
        best, best_d = None, None
        for tr in open_tracks:
            lt, lx, ly = tr['points'][-1]
            if t == lt and (lx, ly) != (x, y):
                continue  # same instant, different spot: another simultaneous entity
            d = max(abs(x - lx), abs(y - ly))
            if d <= JUMP_PX and (best is None or d < best_d):
                best, best_d = tr, d
        if best is None:
            best = dict(points=[], assets=set())
            tracks.append(best)
            open_tracks.append(best)
        best['points'].append((t, x, y))
        best['assets'].add(ins['asset'])

    out = []
    for i, tr in enumerate(tracks):
        pts = tr['points']
        if len(pts) < MIN_POINTS:
            continue
        t0, x0, y0 = pts[0]
        t1, x1, y1 = pts[-1]
        steps = list(zip(pts, pts[1:]))
        cont = sum(1 for a, b in steps
                   if max(abs(b[1] - a[1]), abs(b[2] - a[2])) <= JUMP_PX) / len(steps)
        dur_s = (t1 - t0) * 0.838e-6
        out.append(dict(
            id=f'track_{i:04d}',
            assets=sorted(tr['assets']),
            n=len(pts),
            birth=t0, death=t1,
            x0=x0, y0=y0, x1=x1, y1=y1,
            dx=x1 - x0, dy=y1 - y0,
            span_x=max(p[1] for p in pts) - min(p[1] for p in pts),
            span_y=max(p[2] for p in pts) - min(p[2] for p in pts),
            vx=(x1 - x0) / dur_s if dur_s > 0 else 0.0,
            vy=(y1 - y0) / dur_s if dur_s > 0 else 0.0,
            continuity=round(cont, 3),
            points=[[p[0], p[1], p[2]] for p in pts],
        ))

    out.sort(key=lambda tr: -tr['n'])
    path = os.path.join(args.catalog_dir, 'tracks.json')
    json.dump(dict(tracks=out), open(path, 'w', encoding='utf-8'), indent=1)
    anims = [tr for tr in out if len(tr['assets']) > 1]
    print(f'{len(out)} tracks (>{MIN_POINTS - 1} puntos), '
          f'{len(anims)} con varios assets (animaciones) -> {path}')


if __name__ == '__main__':
    main()
