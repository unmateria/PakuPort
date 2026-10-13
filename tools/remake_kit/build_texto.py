"""Encuentra el TEXTO dibujado y las rutinas PROCEDURALES, y los declara como lo que son.

Dos agujeros del encargo, los dos con su caso real:

  **Texto.** Cuando un asset es un glifo, pedirlo suelto es pedir una letra sin contexto, y el
  modelo la borra o la reinventa. En el remake de un juego de prueba eso se vio en sprites que
  perdieron sus rotulos. Aqui los glifos de una misma linea se agrupan y se entrega **la linea
  entera pegada como una sola imagen**, para que se vea lo que pone. No se hace OCR: no hace
  falta adivinar que letra es cada una si se puede ENSENAR la palabra.

  **Procedural.** Lo que el juego pinta con codigo en vez de con datos —lineas, rellenos,
  particulas— no tiene asset que reemplazar. Hasta ahora eso salia como un hueco mudo, que es lo
  peor: parece que falta arte cuando lo que pasa es que no hay nada que sustituir. Se declara
  como no reemplazable **con la direccion de su rutina**, para poder decidir que hacer con ella
  sabiendo lo que es.

Uso:  python tools/remake_kit/build_texto.py --blit-log <f> --raw <blitraw.bin>
                                             --refs <ref.json> --ref-dir <ref/> --out <texto.json>
"""

import argparse
import json
import os
import struct
import sys
from collections import defaultdict

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)
sys.path.insert(0, os.path.join(os.path.dirname(AQUI), 'blit_catalog'))
import pngio          # noqa: E402
import build_catalog  # noqa: E402

MAX_ANCHO_GLIFO = 24      # por encima de esto no es una letra, es un dibujo
MIN_GLIFOS = 3            # menos de tres seguidos no es una palabra, es casualidad
MAX_SEPARACION = 4        # px de hueco entre glifos de la misma linea


def clave_de(f):
    if 'fnv' in f:
        return ('file:' + f['file']) if 'file' in f else f['fnv']
    return f.get('fill')


def xy(f):
    """Posicion en pantalla del destino del blit, o None si el modo no se decodifica."""
    try:
        modo, lin = int(f['mode'], 16), int(f['lin'], 16)
    except (KeyError, ValueError):
        return None
    r = build_catalog.addr_to_xy(lin, modo)
    return (r[0], r[1]) if r else None


def rutinas_procedurales(raw):
    """Las escrituras sueltas que NO caen en un grupo A..B: no tienen asset, tienen rutina.

    Se cuentan por `at` (el CS:IP que las hace) porque eso es lo unico accionable: no hay imagen
    que sustituir, pero si se puede decir "esta rutina pinta esto".
    """
    if not os.path.exists(raw):
        return {}
    datos = open(raw, 'rb').read()
    if not datos.startswith(b'PBR1'):
        return {}
    p, dentro, cuenta = 4, False, defaultdict(int)
    n = len(datos)
    while p < n:
        tag = datos[p]; p += 1
        if tag == ord('M'):
            p += 1
        elif tag == ord('W'):
            at = struct.unpack_from('<I', datos, p + 5)[0]
            p += 13
            if not dentro:
                cuenta[at] += 1
        elif tag == ord('P'):
            at = struct.unpack_from('<I', datos, p + 8)[0]
            p += 16
            if not dentro:
                cuenta[at] += 1
        elif tag == ord('A'):
            dentro = True
        elif tag == ord('B'):
            p += 4
            lc = datos[p]; p += 1 + lc
            lp = datos[p]; p += 1 + lp
            dentro = False
        else:
            break
    return {f'{at >> 16:04X}:{at & 0xFFFF:04X}': c
            for at, c in sorted(cuenta.items(), key=lambda kv: -kv[1])}


def pega(rutas, destino):
    """Pega las imagenes de una linea en su orden real, para poder LEERLA."""
    imgs = []
    for r in rutas:
        try:
            imgs.append(pngio.leer(r)[:3])
        except Exception:
            return None
    if not imgs:
        return None
    W = sum(i[0] for i in imgs)
    H = max(i[1] for i in imgs)
    lienzo = bytearray(W * H * 4)
    ox = 0
    for w, h, rgba in imgs:
        for y in range(h):
            d = ((y) * W + ox) * 4
            lienzo[d:d + w * 4] = rgba[y * w * 4:(y + 1) * w * 4]
        ox += w
    pngio.escribir(destino, W, H, lienzo)
    return os.path.basename(destino)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--blit-log', required=True)
    ap.add_argument('--raw', required=True)
    ap.add_argument('--refs', required=True)
    ap.add_argument('--ref-dir', required=True)
    ap.add_argument('--out', required=True)
    a = ap.parse_args()

    refs = (json.load(open(a.refs, encoding='utf-8')) if os.path.exists(a.refs) else {}).get('refs', {})

    # --- candidatos a glifo: pequenos y con imagen ---
    instancias = defaultdict(list)          # (frame, y) -> [(x, clave)]
    if os.path.exists(a.blit_log):
        for linea in open(a.blit_log, encoding='utf-8', errors='replace'):
            if not linea.startswith('t='):
                continue
            f = dict(p.split('=', 1) for p in linea.split() if '=' in p)
            cl = clave_de(f)
            info = refs.get(cl if cl in refs else (cl or '').replace('file:', ''))
            if not cl or not info or info['w'] > MAX_ANCHO_GLIFO:
                continue
            pos = xy(f)
            if pos:
                instancias[(int(f.get('frame', 0)), pos[1])].append((pos[0], cl))

    lineas = []
    for (frame, y), puestos in sorted(instancias.items()):
        puestos.sort()
        actual = []
        ultimo_fin = None
        for x, cl in puestos:
            w = refs[cl if cl in refs else cl.replace('file:', '')]['w']
            if ultimo_fin is not None and x - ultimo_fin > MAX_SEPARACION:
                if len(actual) >= MIN_GLIFOS:
                    lineas.append((frame, y, list(actual)))
                actual = []
            actual.append((x, cl))
            ultimo_fin = x + w
        if len(actual) >= MIN_GLIFOS:
            lineas.append((frame, y, list(actual)))

    salida = []
    glifos = set()
    for n, (frame, y, miembros) in enumerate(lineas):
        rutas = [os.path.join(a.ref_dir, refs[c if c in refs else c.replace('file:', '')]['png'])
                 for _, c in miembros]
        nombre = pega(rutas, os.path.join(a.ref_dir, f'texto-{n:03d}.png'))
        for _, c in miembros:
            glifos.add(c)
        salida.append({'id': f'texto-{n:03d}', 'frame': frame, 'y': y,
                       'x': miembros[0][0], 'glifos': [c for _, c in miembros],
                       'imagen': f'ref/{nombre}' if nombre else None})

    proc = rutinas_procedurales(a.raw)
    with open(a.out, 'w', encoding='utf-8') as fh:
        json.dump({'version': 1, 'lineas': salida, 'glifos': sorted(glifos),
                   'procedurales': proc}, fh, indent=1, ensure_ascii=False)

    print(f'{len(salida)} lineas de texto ({len(glifos)} glifos distintos) · '
          f'{len(proc)} rutinas procedurales'
          + (f', la mayor pinta {max(proc.values())} pixeles sin asset' if proc else ''))
    return 0


if __name__ == '__main__':
    sys.exit(main())
