# -*- coding: utf-8 -*-
"""PASO 5 of the remake pipeline: offline sound catalog from the audio-event telemetry.

Reads logs/audio-events.log (produced by the core with PAKU_LOG_AUDIO_EV=1) and emits
games/<id>/sounds.json: one entry per distinct fingerprint with

  shape        grouping key: fnvhz for spk (frequencies only) / fnv for opl. Speaker
               durations wobble a few ms between runs, so grouping by the exact fnv
               would give every appearance of the same effect its own identity.
  fnv          exact fingerprint of the first appearance (quantized sequence)
  fnv_variants every exact fingerprint seen under this shape
  src          "spk" (PIT channel 2 square wave) or "opl" (AdLib register program)
  count        appearances in the run
  first_pc     cs:ip of the routine that first triggered it (the "who"), plus pcs
  avg_ms       average duration in milliseconds
  min_ms/max_ms, first_t/last_t, items (segments or register writes)
  detail       the seq=/regs= detail of the first appearance, for eyeballing

Usage:
  python tools/audio_catalog/build_sounds.py --log logs/audio-events.log --out games/<id>
  python tools/audio_catalog/build_sounds.py --log export/<id>/logs/audio-events.log \
         --out games/<id> --print
"""
import argparse
import json
import os
import sys


def parse(path):
    """Yield one dict per event line; unknown/garbled lines are skipped."""
    with open(path, 'r', encoding='utf-8', errors='replace') as f:
        for line in f:
            line = line.strip()
            if not line.startswith('t='):
                continue
            ev = {}
            for tok in line.split(' '):
                if '=' not in tok:
                    continue
                k, v = tok.split('=', 1)
                ev[k] = v
            if 'src' not in ev or 'fnv' not in ev:
                continue
            try:
                ev['t'] = int(ev['t'])
                ev['n'] = int(ev['n'])
                ev['ms'] = int(ev['ms'])
            except (KeyError, ValueError):
                continue
            ev['detail'] = ev.get('seq') or ev.get('regs') or ''
            yield ev


def build(events):
    cat = {}
    for ev in events:
        shape = ev.get('fnvhz', ev['fnv'])
        key = (ev['src'], shape)
        e = cat.get(key)
        if e is None:
            e = cat[key] = {
                'shape': shape, 'fnv': ev['fnv'], 'fnv_variants': [],
                'src': ev['src'], 'count': 0,
                'first_pc': ev.get('pc', '0000:0000'),
                'items': ev['n'], 'detail': ev['detail'],
                'first_t': ev['t'], 'last_t': ev['t'],
                'min_ms': ev['ms'], 'max_ms': ev['ms'], '_sum_ms': 0,
                'pcs': [],
            }
        e['count'] += 1
        e['_sum_ms'] += ev['ms']
        e['min_ms'] = min(e['min_ms'], ev['ms'])
        e['max_ms'] = max(e['max_ms'], ev['ms'])
        e['last_t'] = ev['t']
        pc = ev.get('pc', '0000:0000')
        if pc not in e['pcs']:
            e['pcs'].append(pc)
        if ev['fnv'] not in e['fnv_variants']:
            e['fnv_variants'].append(ev['fnv'])
    out = []
    for e in cat.values():
        e['avg_ms'] = round(e.pop('_sum_ms') / e['count'], 1)
        out.append(e)
    out.sort(key=lambda e: (-e['count'], e['first_t']))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--log', default=os.path.join('logs', 'audio-events.log'))
    ap.add_argument('--out', help='game directory that receives sounds.json')
    ap.add_argument('--print', dest='show', action='store_true')
    a = ap.parse_args()

    if not os.path.exists(a.log):
        print(f'{a.log}: no existe (¿corriste con PAKU_LOG_AUDIO_EV=1?)')
        return 1
    sounds = build(parse(a.log))
    doc = {
        'source': os.path.basename(a.log),
        'events': sum(s['count'] for s in sounds),
        'spk': sum(1 for s in sounds if s['src'] == 'spk'),
        'opl': sum(1 for s in sounds if s['src'] == 'opl'),
        'sounds': sounds,
    }
    if a.out:
        os.makedirs(a.out, exist_ok=True)
        dst = os.path.join(a.out, 'sounds.json')
        with open(dst, 'w', encoding='utf-8') as f:
            json.dump(doc, f, indent=1)
        print(f'{dst}: {len(sounds)} huellas ({doc["spk"]} spk, {doc["opl"]} opl) '
              f'de {doc["events"]} eventos')
    if a.show or not a.out:
        for s in sounds:
            print(f'{s["src"]} {s["shape"]} x{s["count"]:<4} avg={s["avg_ms"]}ms '
                  f'items={s["items"]} pc={s["first_pc"]} {s["detail"][:80]}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
