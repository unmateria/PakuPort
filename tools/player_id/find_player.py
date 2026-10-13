# -*- coding: utf-8 -*-
"""Requisito del remake: "el asset cuyo track responde a las
teclas ES el jugador". Correlacion offline entre la cinta de input y los tracks de
entidades. No toca el nucleo: solo lee ficheros ya emitidos.

Entradas
--------
<catalog_dir>/tracks.json      (tools/blit_catalog/build_tracks.py)
<catalog_dir>/instances.jsonl  (para recuperar QUE asset dibujo cada punto del track)
<tape>                         cinta de input (PAKU_TAPE_RECORD),
                               lineas "<tick> E <scancode> <ascii>"; mismo reloj
                               Cpu.TickCount que los ticks del catalogo.

EL ESTADISTICO (y por que este)
-------------------------------
Un track es una entidad seguida en el tiempo. Su "respuesta" NO es que se redibuje
(casi todo se redibuja cada frame), sino que CAMBIE: un punto cuenta como evento de
respuesta si su (asset, x, y) difiere del punto anterior del mismo track. El sprite del jugador
parado se redibuja identico 60 veces seguidas y no genera ni un evento; en cuanto
gira, cada frame nuevo es un evento.

Para cada track se mide una sola cosa, con su hipotesis nula explicita:

  p_hit  = fraccion de eventos del track que caen dentro de [tecla, tecla+W]
  p_base = fraccion del TIEMPO DE VIDA DEL TRACK cubierta por esas mismas ventanas
           (union, recortada a [nacimiento, muerte] del track)
  lift   = p_hit / p_base
  z      = (aciertos - n*p_base) / sqrt(n*p_base*(1-p_base))

p_base es la probabilidad de acertar POR AZAR si los eventos del track cayeran
uniformemente en su vida; z es el desvio binomial respecto de esa hipotesis. Se
calcula por track (y no global) porque una entidad que nace y muere dentro de una
rafaga tendria p_base~1 y no debe puntuar por vivir en el momento oportuno.

Se ordena por z y no por lift porque lift es enorme y sin sentido con 3 eventos;
z penaliza las muestras pequeñas por construccion. Se imprimen los dos.

La ventana W debe ser al menos lo que dura la reaccion que la tecla dispara. En
el juego de prueba una tecla de giro deja al jugador girando hasta la tecla de parada
(auto-repeticion en ip 0x37C8), asi que W tiene que cubrir la rafaga entera:
por defecto 900 ms, mas que el tiempo maximo que se mantiene pulsada una tecla en la cinta.

Etiquetas propuestas (baratas, como pide el requisito)
-------------------------------------------------------
jugador    el track con z maxima, si supera --min-z
proyectil  track corto (< --short-points puntos) cuyo NACIMIENTO cae en una ventana de
           tecla. Un nacimiento es UNA muestra: por track no significa nada, asi que
           la etiqueta solo se reparte si la POBLACION de tracks cortos tiene señal
           (z agregada >= --min-z): aciertos de nacimiento frente a la cobertura media
           esperada. Si no la tiene, nadie es proyectil y se dice por que.
otro       el resto  (enemigos/decorado: no responden al input)

Uso: python tools/player_id/find_player.py <catalog_dir> --tape <f> [--cycles 270]
"""
import argparse, bisect, json, math, os
from collections import Counter, defaultdict, deque


def load_keys(path):
    """Tape -> sorted list of key-DOWN ticks. 'E' = INT 16h sink (scancode+ascii),
    'H' = raw 8042 scancode; autokeys feed both, so E is preferred and H is used
    only when the tape has no E lines. Break codes (sc>=0x80) are not presses."""
    e, h = [], []
    for line in open(path, encoding='utf-8'):
        f = line.split()
        if len(f) < 2:
            continue
        if f[1] == 'E':
            e.append(int(f[0]))
        elif f[1] == 'H' and int(f[2]) < 0x80:
            h.append(int(f[0]))
    keys = sorted(set(e or h))
    return keys


def coverage(keys, w, lo, hi):
    """Total ticks of [lo,hi] covered by the union of [k, k+w]."""
    tot, last = 0, lo
    for k in keys:
        a, b = max(k, lo, last), min(k + w, hi)
        if b > a:
            tot += b - a
            last = b
    return tot


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('catalog_dir')
    ap.add_argument('--tape', required=True)
    ap.add_argument('--cycles', type=int, default=270, help='guest ticks per ms')
    # Ventana ESTRECHA a proposito: es el retardo real entre la tecla y el primer frame que
    # el juego dibuja en respuesta. Con 900 ms la tasa base se satura (p_base 0,44) y el
    # resultado deja de ser informativo; con 100 ms baja a 0,066 y el contraste es enorme.
    ap.add_argument('--window-ms', type=int, default=100)
    ap.add_argument('--burst-ms', type=int, default=150,
                    help='eventos mas juntos que esto = una sola rafaga (unidad independiente)')
    ap.add_argument('--min-events', type=int, default=8)
    # 3.0 dejaba pasar ~10 % de falsos positivos bajo la hipotesis nula (medido: 2 de 20
    # corridas con los tiempos de la cinta re-sorteados etiquetaban 'jugador' a un track
    # ajeno). 4.0 deja el mejor z nulo observado (2,38) con margen.
    ap.add_argument('--min-z', type=float, default=4.0)
    ap.add_argument('--short-points', type=int, default=20,
                    help='tracks con menos puntos = candidatos a proyectil')
    ap.add_argument('--out', default=None)
    args = ap.parse_args()

    w = args.window_ms * args.cycles
    burst_ticks = args.burst_ms * args.cycles
    keys = load_keys(args.tape)
    tracks = json.load(open(os.path.join(args.catalog_dir, 'tracks.json'),
                            encoding='utf-8'))['tracks']

    # point -> asset: build_tracks.py keeps only the SET of assets per track, so the
    # per-point asset is recovered by joining back on (t, x, y).
    by_pos = defaultdict(deque)
    for line in open(os.path.join(args.catalog_dir, 'instances.jsonl'), encoding='utf-8'):
        i = json.loads(line)
        by_pos[(i['t'], i['x'], i['y'])].append(i['asset'])

    def covered(t):
        j = bisect.bisect_right(keys, t) - 1
        return j >= 0 and t - keys[j] <= w

    rows = []
    for tr in tracks:
        pts = tr['points']
        prev, events = None, []
        for t, x, y in pts:
            q = by_pos.get((t, x, y))
            a = q[0] if q else None
            cur = (a, x, y)
            if prev is not None and cur != prev:
                events.append(t)
            prev = cur
        # Los cambios de un track NO son independientes: una sola pulsacion dispara una
        # RAFAGA de cambios (el jugador gira varios frames seguidos). Contar cada cambio como
        # un Bernoulli infla el z (medido: 65 eventos = 9 rafagas; z 8,37 por evento contra
        # 2,72 por rafaga a la misma ventana). La unidad independiente es la rafaga: se
        # colapsan los eventos separados por menos de --burst-ms y se puntua su ARRANQUE.
        bursts = []
        for t in events:
            if not bursts or t - bursts[-1] > burst_ticks:
                bursts.append(t)
        events = bursts
        n = len(events)
        lo, hi = tr['birth'], tr['death']
        span = max(hi - lo, 1)
        p = coverage(keys, w, lo, hi) / span
        hits = sum(1 for t in events if covered(t))
        if n and 0 < p < 1:
            z = (hits - n * p) / math.sqrt(n * p * (1 - p))
        else:
            z = 0.0
        # Posicion DOMINANTE del track, no su primer punto: x0/y0 puede ser un punto
        # espurio (el jugador del juego de prueba nacia en (144,166) cuando su posicion real es
        # (144,145) en 658 de 659 puntos, y era lo que se publicaba). x0/y0 se conservan en
        # el JSON para quien los use; lo que se MUESTRA es la moda, con su fraccion.
        (mx, my), mode_n = Counter((x, y) for _, x, y in pts).most_common(1)[0]
        rows.append(dict(id=tr['id'], n_points=tr['n'], n_events=n, hits=hits,
                         p_base=round(p, 4), p_hit=round(hits / n, 4) if n else 0.0,
                         lift=round((hits / n) / p, 2) if n and p > 0 else 0.0,
                         z=round(z, 2), x0=tr['x0'], y0=tr['y0'],
                         x_mode=mx, y_mode=my,
                         mode_frac=round(mode_n / len(pts), 3) if pts else 0.0,
                         span_x=tr['span_x'], span_y=tr['span_y'],
                         assets=len(tr['assets']), birth=lo, death=hi,
                         birth_covered=covered(lo)))

    ranked = sorted([r for r in rows if r['n_events'] >= args.min_events],
                    key=lambda r: -r['z'])
    weak = [r for r in rows if r['n_events'] < args.min_events]

    # Population test for "born when a key is pressed" (projectiles).
    short = [r for r in rows if r['n_points'] < args.short_points]
    b_hits = sum(1 for r in short if r['birth_covered'])
    b_exp = sum(r['p_base'] for r in short)
    b_z = ((b_hits - b_exp) / math.sqrt(b_exp * (1 - b_exp / len(short)))
           if short and 0 < b_exp < len(short) else 0.0)
    proj_ok = b_z >= args.min_z

    for r in rows:
        r['label'] = 'otro'
    if ranked and ranked[0]['z'] >= args.min_z:
        ranked[0]['label'] = 'jugador'
    if proj_ok:
        for r in short:
            if r['birth_covered'] and r['label'] != 'jugador':
                r['label'] = 'proyectil'

    print(f'{len(keys)} pulsaciones en la cinta, ventana W={args.window_ms} ms '
          f'({w} ticks), {len(tracks)} tracks ({len(ranked)} con >= '
          f'{args.min_events} eventos)')
    print('%-11s %6s %7s %5s %7s %6s %6s %8s  %-9s %s' %
          ('track', 'puntos', 'eventos', 'hits', 'p_base', 'p_hit', 'lift', 'z',
           'etiqueta', 'pos/span'))
    for r in ranked:
        print('%-11s %6d %7d %5d %7.3f %6.3f %6.2f %8.2f  %-9s (%d,%d) %dx%d' %
              (r['id'], r['n_points'], r['n_events'], r['hits'], r['p_base'],
               r['p_hit'], r['lift'], r['z'], r['label'], r['x_mode'], r['y_mode'],
               r['span_x'], r['span_y']))
    if weak:
        print(f'({len(weak)} tracks con menos de {args.min_events} eventos, fuera del '
              f'ranking: ' + ' '.join(r['id'] for r in weak) + ')')
    print(f'nacimientos de tracks cortos (<{args.short_points} puntos) dentro de '
          f'ventana: {b_hits}/{len(short)}, esperados por azar {b_exp:.1f}, '
          f'z={b_z:.2f} -> ' + ('etiqueta proyectil repartida'
                                if proj_ok else 'SIN senal: nadie es proyectil'))

    out = args.out or os.path.join(args.catalog_dir, 'player.json')
    json.dump(dict(window_ms=args.window_ms, cycles=args.cycles, keys=len(keys),
                   birth_hits=b_hits, birth_expected=round(b_exp, 2),
                   birth_z=round(b_z, 2), ranking=ranked, sin_eventos=weak),
              open(out, 'w', encoding='utf-8'), indent=1)
    print('->', out)


if __name__ == '__main__':
    main()
