# -*- coding: utf-8 -*-
"""JUEZ del PASO 1: does the blit catalog rediscover a reference set of sprites?

The reference is a sprite catalog made independently (by hand or by an earlier pipeline):
a JSON with {"sprites": [{"id": ..., "pixels": [rows]}]}.

Compares pixel MASKS (not format hashes): a reference sprite counts as found when its
mask appears as a subwindow of some catalog asset (catalog assets are byte-aligned,
so they may carry '.' padding or neighbours around the true sprite).

Usage: python tools/blit_catalog/judge_reference_sprites.py <catalog_dir> --reference <catalog.json>
       [--target 40]
Exit code 0 = GATE OK (found >= target), 1 = GATE FAIL.
"""
import argparse, json, os, sys


def subwindow_match(needle, hay):
    nh, nw = len(needle), len(needle[0])
    hh, hw = len(hay), len(hay[0]) if hay else 0
    if nw > hw or nh > hh:
        return False
    for dy in range(hh - nh + 1):
        for dx in range(hw - nw + 1):
            if all(hay[dy + y][dx:dx + nw] == needle[y] for y in range(nh)):
                return True
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('catalog_dir')
    ap.add_argument('--reference', required=True,
                    help='catalog.json de referencia con {"sprites": [...]}')
    ap.add_argument('--target', type=int, default=40)
    args = ap.parse_args()

    pilot = json.load(open(args.reference, encoding='utf-8'))['sprites']
    cat = json.load(open(os.path.join(args.catalog_dir, 'assets.json'),
                         encoding='utf-8'))['assets']
    hays = [a['pixels'] for a in cat]

    found, missing = [], []
    for s in pilot:
        ok = any(subwindow_match(s['pixels'], h) for h in hays)
        (found if ok else missing).append(s['id'])

    print(f'encontrados {len(found)}/{len(pilot)} sprites de la referencia '
          f'(objetivo >= {args.target})')
    if missing:
        print('faltan:', ' '.join(missing))
    if len(found) >= args.target:
        print('GATE OK')
        return 0
    print('GATE FAIL')
    return 1


if __name__ == '__main__':
    sys.exit(main())
