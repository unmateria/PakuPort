"""Reconstruye los DIBUJOS LOGICOS: varios blits de una misma rutina que componen una figura.

El problema, medido en un juego de prueba: de 1165 claves distintas, 1162 son FILAS de un pixel de
alto. El juego compone cada sprite una fila por blit y cada fila tiene identidad propia, asi que
no existe ninguna clave de la que colgar el sprite entero: reemplazar "por clave" deja fuera el
99,7 % de lo que el juego dibuja. El kit lo venia diciendo en voz alta, y decirlo estaba bien,
pero no arreglaba nada.

Aqui se reconstruye la figura desde la traza con la MISMA regla que aplica el motor en ejecucion
(RemakeMap.AgrupaFila): blits consecutivos de la misma rutina, con la misma x y la misma anchura,
en filas que avanzan de una en una y dentro de la misma rafaga, son UN dibujo. Su identidad es el
encadenado FNV-1a de las identidades de sus filas EN ORDEN — estable aunque el juego lo componga
sobre fondos distintos, porque no depende de lo que hubiera debajo.

Que las dos implementaciones coincidan no se deja a la buena fe: `judge_grupos.py` compara las
claves que salen de aqui con las que el motor dice haber visto.

Salida: grupos.json con `rects` (para el encargo, mismo formato que el catalogo) y `refs` (la
imagen de cada figura, recortada de la pantalla real).

Uso:
    python tools/remake_kit/build_grupos.py --blit-log <blit.log> --out <grupos.json>
        [--present <dir>] [--ref-dir <ref/>] [--cycles 20000] [--burst-ms 2]
"""

import argparse
import json
import os
import sys

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)
sys.path.insert(0, os.path.join(os.path.dirname(AQUI), 'blit_catalog'))
import build_catalog  # noqa: E402
import build_ref      # noqa: E402
import pngio          # noqa: E402


def lee_blits(ruta):
    """Cada linea de blit.log como diccionario, en el orden en que se dibujaron."""
    salida = []
    for linea in open(ruta, encoding='utf-8', errors='replace'):
        campos = {}
        for campo in linea.split():
            if '=' in campo:
                k, _, v = campo.partition('=')
                campos[k] = v
        if 'fnv' not in campos or 'lin' not in campos:
            continue
        try:
            salida.append({
                't': int(campos.get('t', 0)),
                'at': campos.get('at', ''),
                'modo': int(campos.get('mode', '13'), 16),
                'lin': int(campos['lin'], 16),
                'bytes': int(campos.get('bytes', 0)),
                'frame': int(campos.get('frame', 0)),
                'fnv': campos['fnv'],
            })
        except ValueError:
            continue
    return salida


def _xy(lin, modo):
    off = lin - 0xA0000
    if modo in (4, 5, 6):
        o = off & 0x3FFF
        impar = (off & 0x2000) != 0
        fila = (o & 0x1FFF) // 80
        return ((o & 0x1FFF) % 80 * (8 if modo == 6 else 4), fila * 2 + (1 if impar else 0))
    if modo in build_catalog.EGA_MODES:
        bpl = build_catalog.EGA_MODES[modo][0]
        return (off % bpl) * 8, off // bpl
    return off % 320, off // 320


def fnv_de_claves(claves):
    """FNV-1a sobre las claves en hexadecimal ASCII, en orden. Copia exacta del calculo del
    motor (RemakeMap.AgrupaFila): si las dos se separan, el manifiesto deja de casar y nadie se
    entera hasta que el remake no se ve."""
    h = 2166136261
    for clave in claves:
        for ch in f'{int(clave, 16):08X}':
            h = ((h ^ ord(ch)) * 16777619) & 0xFFFFFFFF
    return h


def agrupa(blits, burst_ticks):
    """La misma regla que el motor. Devuelve [(clave, at, x, y0, w, filas, frame_fin), ...]."""
    grupos = []
    act = None
    for b in blits:
        ppb = build_catalog.pixels_per_byte(b['modo'])
        w = b['bytes'] * ppb
        x, y = _xy(b['lin'], b['modo'])
        sigue = (act is not None and b['at'] == act['at'] and x == act['x'] and w == act['w']
                 and y == act['y0'] + len(act['claves']) and b['t'] - act['t'] <= burst_ticks)
        if not sigue:
            if act is not None and len(act['claves']) >= 2:
                grupos.append(act)
            act = {'at': b['at'], 'x': x, 'y0': y, 'w': w, 'claves': [], 'modo': b['modo'],
                   't': b['t'], 'frame': b['frame']}
        act['claves'].append(b['fnv'])
        act['t'] = b['t']
        act['frame'] = b['frame']
    if act is not None and len(act['claves']) >= 2:
        grupos.append(act)
    for g in grupos:
        g['clave'] = f"grupo:{fnv_de_claves(g['claves']):08X}"
        g['h'] = len(g['claves'])
    return grupos


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--blit-log', dest='blit_log', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--present', help='muestreo de pantalla para recortar la imagen de cada figura')
    ap.add_argument('--ref-dir', dest='ref_dir', help='donde dejar las imagenes de referencia')
    ap.add_argument('--cycles', type=int, default=20000, help='ciclos por ms del governor')
    ap.add_argument('--burst-ms', dest='burst_ms', type=int, default=2)
    ap.add_argument('--min-alto', dest='min_alto', type=int, default=2)
    a = ap.parse_args()

    if not os.path.exists(a.blit_log):
        print(f'ROJO: no existe {a.blit_log}')
        return 2
    blits = lee_blits(a.blit_log)
    grupos = agrupa(blits, a.burst_ms * a.cycles)
    if not grupos:
        print('sin dibujos logicos: ningun blit encadena con otro de su misma rutina. Este juego '
              'no compone por filas, asi que el reemplazo por clave suelta ya lo cubre.')
        json.dump({'rects': [], 'refs': {}}, open(a.out, 'w', encoding='utf-8'), indent=1)
        return 0

    # Una figura se dibuja muchas veces; nos quedamos con UNA muestra por clave y contamos.
    muestras, veces = {}, {}
    for g in grupos:
        veces[g['clave']] = veces.get(g['clave'], 0) + 1
        previo = muestras.get(g['clave'])
        if previo is None or g['w'] * g['h'] > previo['w'] * previo['h']:
            muestras[g['clave']] = g

    presentados = build_ref.lee_presentados(a.present) if a.present else []
    refs, rects, con_imagen = {}, [], 0
    for clave, g in sorted(muestras.items(), key=lambda kv: -veces[kv[0]]):
        if g['h'] < a.min_alto:
            continue
        rects.append({'fnv': clave, 'count': veces[clave], 'at': g['at'],
                      'bytes': g['w'] * g['h'], 'file': None})
        entrada = {'w': g['w'], 'h': g['h'], 'modo': g['modo'], 'prov': None,
                   'rutina': g['at'], 'veces': veces[clave], 'x': g['x'], 'y': g['y0'],
                   'forma': 'sprite', 'huecos': 0.0}
        # La imagen de la figura, recortada del primer fotograma presentado DESPUES de haberla
        # terminado de dibujar: es la figura tal cual se vio, sin depender de acertar la paleta.
        if presentados and a.ref_dir:
            pantalla = build_ref.tam_pantalla(g['modo'])
            corte = None
            for frame, ruta, fw, fh in presentados:
                if frame < g['frame'] or (fw, fh) != pantalla:
                    continue
                if g['x'] + g['w'] > fw or g['y0'] + g['h'] > fh:
                    break
                datos = open(ruta, 'rb').read()
                if len(datos) != fw * fh * 4:
                    continue
                corte = bytearray()
                for fila in range(g['h']):
                    o = ((g['y0'] + fila) * fw + g['x']) * 4
                    corte += datos[o:o + g['w'] * 4]
                break
            if corte:
                os.makedirs(a.ref_dir, exist_ok=True)
                nombre = 'ref-' + clave.replace(':', '-') + '.png'
                pngio.escribir(os.path.join(a.ref_dir, nombre), g['w'], g['h'], bytes(corte))
                entrada['png'] = nombre
                con_imagen += 1
        refs[clave] = entrada

    json.dump({'rects': rects, 'refs': refs}, open(a.out, 'w', encoding='utf-8'),
              indent=1, ensure_ascii=False)
    altos = sorted((r['h'] for r in refs.values()), reverse=True)
    print(f'{len(grupos)} dibujos logicos ({len(rects)} figuras distintas, {con_imagen} con '
          f'imagen). Alto: mayor {altos[0]} filas, mediana {altos[len(altos) // 2]}.')
    print(f'  -> {a.out}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
