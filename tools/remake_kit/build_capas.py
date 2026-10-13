"""Mide el ORDEN DE DIBUJO y la PERSISTENCIA DEL FONDO a partir de `blit.log`.

Son las dos preguntas que tumbaron la intro de un juego de prueba, y las dos son datos de
EJECUCION que no se pueden sacar del fichero del juego:

  * **Que tapa a que.** Dos sprites que se solapan se ven de una forma o de la contraria segun
    cual se pinte despues. Aqui se mide con el ordinal DENTRO del fotograma, que el motor
    estampa en cada blit (`frame=` y `orden=`, con PAKU_PRESENT_CAPTURE=1).
  * **Si el sprite se queda o el juego repinta el fondo debajo.** Es literalmente lo que decidio
    que el logo de esa intro se quedara o no. Se mide viendo si los pixeles de un blit los reescribe
    OTRO blit distinto en un fotograma POSTERIOR.

La distincion entre las dos es la que hace que esto valga: que a un sprite lo tape otro en el
MISMO fotograma es composicion (van en capas, y los dos se ven); que lo reescriban en un
fotograma POSTERIOR es borrado (el sprite no persiste). Confundirlas da justo el error de "el
logo no baja" o "los sprites se pisan".

Nada de esto se decide: se cuenta. Y lo que no se puede contar —un modo sin decodificador— sale
declarado como tal.

Uso:  python tools/remake_kit/build_capas.py --blit-log <f> --out <capas.json>
"""

import argparse
import json
import os
import sys
from collections import defaultdict

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, os.path.join(os.path.dirname(AQUI), 'blit_catalog'))
import build_catalog  # noqa: E402

# A partir de esta fraccion de pixeles reescritos en un fotograma posterior, el blit no persiste.
UMBRAL_BORRADO = 0.80


def lee(ruta):
    """Cada linea de blit.log como dict, en el orden en que ocurrieron."""
    filas = []
    with open(ruta, encoding='utf-8', errors='replace') as fh:
        for linea in fh:
            if not linea.startswith('t='):
                continue
            f = dict(p.split('=', 1) for p in linea.split() if '=' in p)
            filas.append(f)
    return filas


def pixeles(f):
    """Conjunto de pixeles de PANTALLA que toca este blit, o None si el modo no se decodifica.

    Para el modo 13h el byte ES el pixel y va lineal, asi que sale de una resta. Para CGA y los
    planares se pasa por `addr_to_xy` del catalogo, que es el decodificador ya medido (el CGA
    va entrelazado y los planares no tienen la fila donde parece).
    """
    try:
        modo = int(f['mode'], 16)
        lin = int(f['lin'], 16)
        n = int(f['bytes'])
    except (KeyError, ValueError):
        return None
    if n <= 0 or n > 1 << 20:
        return None
    ini = lin - n + 1 if f.get('df') == '1' else lin      # DF=1 escribe hacia atras

    if modo == 0x13:
        off = ini - 0xA0000
        if off < 0:
            return None
        return set(range(off, min(off + n, 64000)))

    out = set()
    for i in range(n):
        xy = build_catalog.addr_to_xy(ini + i, modo)
        if xy is None:
            continue
        x, y, ppb = xy
        for k in range(ppb):
            out.add(y * 1024 + x + k)                     # rejilla holgada: solo se usa como clave
    return out or None


def clave_de(f):
    """La identidad del blit, la MISMA con la que se indexa en el catalogo y el manifiesto."""
    if 'fnv' in f:
        return ('file:' + f['file']) if 'file' in f else f['fnv']
    if 'fill' in f:
        return f['fill']
    return None                                           # VRAM->VRAM: sin identidad, a proposito


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--blit-log', required=True)
    ap.add_argument('--out', required=True)
    a = ap.parse_args()

    if not os.path.exists(a.blit_log):
        print(f'ROJO: no existe {a.blit_log}')
        return 2
    filas = lee(a.blit_log)
    if not filas:
        print(f'ROJO: {a.blit_log} no tiene lineas de blit')
        return 2

    sin_frame = sum(1 for f in filas if 'frame' not in f)
    if sin_frame:
        print(f'AVISO: {sin_frame} lineas sin `frame=` (captura de un motor anterior). El orden '
              f'de dibujo saldra degradado.')

    # Ultimo que escribio cada pixel: (indice del blit). Se recorre en orden real.
    # Cuantos blits lleva cada fotograma: hace falta para poder comparar el `orden` de dos
    # claves, porque el ordinal se reinicia en cada fotograma y en crudo no es comparable.
    blits_por_frame = defaultdict(int)
    for f in filas:
        blits_por_frame[int(f.get('frame', 0))] += 1

    dueno = {}
    tapado_mismo = defaultdict(int)      # pixeles que a un blit le tapan en SU fotograma
    tapado_luego = defaultdict(int)      # ...y en uno POSTERIOR (eso es borrado)
    tapa_a = defaultdict(set)            # QUIEN tapa a quien, observado pixel a pixel
    tapado_por = defaultdict(set)
    total_px = defaultdict(int)
    posiciones = defaultdict(list)       # posicion RELATIVA dentro de su fotograma (0..1)
    frames = defaultdict(list)
    veces = defaultdict(int)
    sin_pixeles = 0

    for i, f in enumerate(filas):
        cl = clave_de(f)
        px = pixeles(f)
        if px is None:
            sin_pixeles += 1
            continue
        fr = int(f.get('frame', 0))
        if cl is not None:
            veces[cl] += 1
            total_px[cl] += len(px)
            posiciones[cl].append(int(f.get('orden', 0)) / max(1, blits_por_frame[fr] - 1))
            frames[cl].append(fr)
        for p in px:
            previo = dueno.get(p)
            if previo is not None:
                cl_prev, fr_prev = previo
                if cl_prev is not None and cl_prev != cl:
                    if fr > fr_prev:
                        tapado_luego[cl_prev] += 1
                    else:
                        tapado_mismo[cl_prev] += 1
                        if cl is not None:
                            tapa_a[cl].add(cl_prev)
                            tapado_por[cl_prev].add(cl)
            dueno[p] = (cl, fr)

    capas = {}
    for cl in veces:
        tot = total_px[cl] or 1
        frac_luego = tapado_luego[cl] / tot
        frac_mismo = tapado_mismo[cl] / tot
        pos = sorted(posiciones[cl])[len(posiciones[cl]) // 2]
        capas[cl] = {
            'veces': veces[cl],
            'posicion_en_frame': round(pos, 4),
            'primer_frame': min(frames[cl]),
            'ultimo_frame': max(frames[cl]),
            'tapado_mismo_frame': round(frac_mismo, 3),
            'reescrito_despues': round(frac_luego, 3),
            'persistencia': 'transitorio' if frac_luego >= UMBRAL_BORRADO else 'persistente',
            # La prueba DIRECTA de quien va delante: se observo pixel a pixel, no se dedujo.
            'tapa_a': sorted(tapa_a[cl]),
            'tapado_por': sorted(tapado_por[cl]),
        }

    # La CAPA: el puesto en el orden de pintado, por la posicion RELATIVA dentro del fotograma
    # (el ordinal crudo se reinicia en cada uno, asi que en crudo no es comparable). OJO: solo
    # significa algo entre claves que coinciden en pantalla; para dos que nunca se solapan, el
    # dato que manda es `tapa_a`/`tapado_por`, que es observacion y no ordenacion.
    for n, cl in enumerate(sorted(capas, key=lambda k: capas[k]['posicion_en_frame'])):
        capas[cl]['capa'] = n

    with open(a.out, 'w', encoding='utf-8') as fh:
        json.dump({'version': 1, 'fuente': a.blit_log, 'umbral_borrado': UMBRAL_BORRADO,
                   'blits': len(filas), 'sin_pixeles': sin_pixeles, 'capas': capas},
                  fh, indent=1, ensure_ascii=False)

    trans = sum(1 for v in capas.values() if v['persistencia'] == 'transitorio')
    print(f'{len(filas)} blits · {len(capas)} claves · {trans} transitorias '
          f'(el juego repinta el fondo debajo) · {len(capas) - trans} persistentes'
          + (f' · {sin_pixeles} sin decodificar' if sin_pixeles else ''))
    return 0


if __name__ == '__main__':
    sys.exit(main())
