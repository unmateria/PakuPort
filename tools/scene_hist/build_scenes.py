"""Scene segmentation from a periodic PC sample log (PAKU_SAMPLE_PC_MS).

Requirement: "Escenas/fases: titulo / menu / partida /
game-over, detectadas por histograma de PC + hash de pantalla". This is the PC-histogram
half: offline, no engine involvement, reads logs/pcsample.log and emits

  (a) a histogram of PCs per time window, and
  (b) a segmentation into SCENES: runs of windows whose dominant PC set is stable, cut
      where it changes abruptly.

Method (deliberately simple; every parameter is one CLI flag):

  * Samples are bucketed into windows of --window-ms of GUEST time. The log's own header
    carries cycles_per_ms, so ticks (Cpu.TickCount, the clock every other PakuPort log
    uses) convert to guest ms without guessing.
  * A window is described by the distribution of CODE PAGES it hit, not exact cs:ip:
    key = cs:(ip >> --page-shift), 256-byte pages by default. This is the one choice that
    makes the method work on real code: a scene is "this set of routines is running", and
    a routine spans dozens of addresses. With exact PCs, a busy menu that walks 60 distinct
    addresses per window has almost no overlap with itself window to window (measured on
    a test game: 53 spurious cuts vs 7 with pages).
  * Distance between a window and the scene's running profile is 1 - histogram
    intersection (sum of per-key minima of the normalized distributions): 0 = identical
    mass, 1 = disjoint code. A cut needs --threshold (default 0.5 = half the sampling mass
    moved elsewhere) sustained over --confirm consecutive windows, so a one-off window
    (a disk load, one frame of a different routine) does not split a scene. The cut is
    placed at the FIRST of those windows.

Usage:
  python build_scenes.py <pcsample.log> [--json out.json] [--windows] [--window-ms 250]
                         [--threshold 0.5] [--confirm 2] [--page-shift 8] [--top 6]
"""
import argparse
import collections
import json
import sys


def load_samples(path):
    """Returns (samples, meta). samples = [(tick, 'CCCC:IIII')] in log order."""
    samples = []
    meta = {}
    with open(path, encoding='utf-8', errors='replace') as fh:
        for line in fh:
            line = line.strip()
            if not line:
                continue
            if line.startswith('#'):
                for tok in line.lstrip('#').split():
                    if '=' in tok:
                        k, v = tok.split('=', 1)
                        meta[k] = v
                continue
            parts = line.split()
            if len(parts) < 2 or not parts[0].startswith('t=') or not parts[1].startswith('pc='):
                continue
            samples.append((int(parts[0][2:]), parts[1][3:]))
    return samples, meta


def page_key(pc, shift):
    cs, ip = pc.split(':')
    return '%s:%03X' % (cs, int(ip, 16) >> shift) if shift else pc


def build_windows(samples, window_ticks, shift):
    """Histogram of code pages (and exact PCs) per fixed window of guest time."""
    pages = collections.defaultdict(collections.Counter)
    exact = collections.defaultdict(collections.Counter)
    for t, pc in samples:
        w = t // window_ticks
        pages[w][page_key(pc, shift)] += 1
        exact[w][pc] += 1
    return [(w, pages[w], exact[w]) for w in sorted(pages)]


def intersection_distance(a, b):
    """1 - histogram intersection of two counters, normalized. 0 = same mix, 1 = disjoint."""
    na, nb = sum(a.values()), sum(b.values())
    if not na or not nb:
        return 1.0
    inter = sum(min(a[k] / na, b[k] / nb) for k in set(a) | set(b))
    return 1.0 - inter


def segment(windows, threshold, confirm):
    """Greedy segmentation. Returns (scenes, dists) where a scene is the list of window
    indices it owns and dists[i] is the distance of window i to its scene profile."""
    scenes, dists = [], []
    profile = collections.Counter()
    current = []
    pending = []
    for idx, (_, pg, _ex) in enumerate(windows):
        d = intersection_distance(pg, profile) if profile else 0.0
        dists.append(d)
        if profile and d > threshold:
            pending.append(idx)
            if len(pending) >= confirm:
                cut = pending[0]
                # Pending windows were provisionally absorbed into the current scene while
                # waiting for confirmation; the cut takes them back.
                scenes.append([i for i in current if i < cut])
                current = list(range(cut, idx + 1))
                profile = collections.Counter()
                for i in current:
                    profile.update(windows[i][1])
                pending = []
                continue
        else:
            pending = []
        current.append(idx)
        profile.update(pg)
    if current:
        scenes.append(current)
    return [s for s in scenes if s], dists


def top_items(counter, n):
    total = sum(counter.values()) or 1
    return [{'pc': k, 'muestras': v, 'pct': round(100.0 * v / total, 1)}
            for k, v in counter.most_common(n)]


def analyze(path, window_ms=250, threshold=0.5, confirm=2, page_shift=8, top=6):
    samples, meta = load_samples(path)
    if not samples:
        raise SystemExit('sin muestras en %s (se ejecuto con PAKU_SAMPLE_PC_MS?)' % path)
    cyc = int(meta.get('cycles_per_ms', 0)) or 1
    window_ticks = max(1, window_ms * cyc)
    windows = build_windows(samples, window_ticks, page_shift)
    scene_idx, dists = segment(windows, threshold, confirm)

    scenes = []
    for n, idxs in enumerate(scene_idx, 1):
        pg, ex = collections.Counter(), collections.Counter()
        for i in idxs:
            pg.update(windows[i][1])
            ex.update(windows[i][2])
        t0 = windows[idxs[0]][0] * window_ticks
        t1 = (windows[idxs[-1]][0] + 1) * window_ticks
        scenes.append({
            'escena': n,
            't_inicio': t0,
            't_fin': t1,
            't_inicio_ms': round(t0 / cyc, 1),
            't_fin_ms': round(t1 / cyc, 1),
            'duracion_ms': round((t1 - t0) / cyc, 1),
            'muestras': sum(pg.values()),
            'pcs_dominantes': top_items(ex, top),
            'paginas_dominantes': top_items(pg, top),
        })
    win_out = [{
        't_inicio': w * window_ticks,
        't_inicio_ms': round(w * window_ticks / cyc, 1),
        'muestras': sum(pg.values()),
        'distinct_pcs': len(ex),
        'distancia': round(dists[i], 3),
        'paginas': top_items(pg, top),
    } for i, (w, pg, ex) in enumerate(windows)]
    return {
        'fuente': path,
        'cycles_per_ms': cyc,
        'sample_ms': int(meta.get('sample_ms', 0)),
        'muestras': len(samples),
        'ventana_ms': window_ms,
        'umbral': threshold,
        'confirmar': confirm,
        'page_shift': page_shift,
        'escenas': scenes,
        'ventanas': win_out,
    }


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('log')
    ap.add_argument('--json')
    ap.add_argument('--windows', action='store_true', help='imprime el histograma por ventana')
    ap.add_argument('--window-ms', type=int, default=250)
    ap.add_argument('--threshold', type=float, default=0.5)
    ap.add_argument('--confirm', type=int, default=2)
    ap.add_argument('--page-shift', type=int, default=8)
    ap.add_argument('--top', type=int, default=6)
    a = ap.parse_args()
    r = analyze(a.log, a.window_ms, a.threshold, a.confirm, a.page_shift, a.top)

    print('muestras=%d  ventana=%d ms  umbral=%.2f  confirmar=%d  pagina=2^%d B  cyc/ms=%d'
          % (r['muestras'], r['ventana_ms'], r['umbral'], r['confirmar'], r['page_shift'], r['cycles_per_ms']))
    if a.windows:
        print('\n-- histograma por ventana --')
        print('%9s %6s %5s %5s  %s' % ('t_ms', 'muest', 'uniq', 'dist', 'paginas dominantes'))
        for w in r['ventanas']:
            top = ' '.join('%s:%.0f%%' % (p['pc'], p['pct']) for p in w['paginas'][:4])
            print('%9.0f %6d %5d %5.2f  %s'
                  % (w['t_inicio_ms'], w['muestras'], w['distinct_pcs'], w['distancia'], top))
    print('\n-- escenas (%d) --' % len(r['escenas']))
    print('%3s %10s %10s %9s %7s  %s' % ('#', 't_ini_ms', 't_fin_ms', 'dur_ms', 'muest', 'pcs dominantes'))
    for s in r['escenas']:
        top = ' '.join('%s:%.0f%%' % (p['pc'], p['pct']) for p in s['pcs_dominantes'][:4])
        print('%3d %10.0f %10.0f %9.0f %7d  %s'
              % (s['escena'], s['t_inicio_ms'], s['t_fin_ms'], s['duracion_ms'], s['muestras'], top))
    if a.json:
        with open(a.json, 'w', encoding='utf-8') as fh:
            json.dump(r, fh, indent=1)
        print('\njson -> %s' % a.json)
    return 0


if __name__ == '__main__':
    sys.exit(main())
