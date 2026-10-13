"""MAPA DE ESCENAS POR FUNCION — parte del expediente de cada funcion.

Convierte "donde esta el codigo del combate?" en un grep. El segmentador de escenas ya existe y
esta juzgado contra verdad terreno independiente (tools/scene_hist/); aqui no
se reinventa: se importa `analyze` y lo unico que se anade es la ATRIBUCION — que funcion del
corpus corria en cada escena, con la misma cuenta lineal->offset que usa `pakuport expediente
capas` (que viaja escrita en capas.json, para que nadie la reinvente y se equivoque).

Salida por funcion: en que escenas vivio y con que peso. Y por escena: sus funciones dominantes.

CONTROL NEGATIVO (--control): se rehace la atribucion BARAJANDO las etiquetas de escena entre las
muestras (misma cantidad de muestras por escena, correspondencia destruida, semilla fija). Si una
funcion vive de verdad en una escena, la real tiene que concentrar mucho mas que la barajada.

La medida es la fraccion de muestras de cada funcion que cae en su escena dominante, promediada
SOLO sobre las funciones con al menos --min-muestras. Sin ese filtro la metrica no mide nada: una
funcion con una sola muestra concentra 1,0 por definicion, tambien en el control, y como la mayoria
de las funciones aparecen una o dos veces, se comen el promedio. Medido en un juego de prueba: con todas las
funciones el real daba 0,914 y el barajado 0,902 (+0,012, indistinguible); con el filtro la
diferencia es la que se ve abajo. Y ROTAR el eje de tiempo tampoco vale como control cuando una
escena ocupa media partida: la rotacion la deja emparejada consigo misma.

Uso:
  python tools\expediente_escenas.py --pcsample export\<id>\logs\pcsample.log ^
                                      --capas games\<id>\capas.json ^
                                      [--out games\<id>\escenas-funciones.json] [--control]
"""
import argparse
import bisect
import collections
import json
import os
import random
import sys

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(RAIZ, 'tools', 'scene_hist'))
import build_scenes


def atribuye(samples, escenas, inicios, por_off, desplaza, barajar=False):
    """(funcion, escena) -> muestras. Con `barajar`, las etiquetas de escena se reparten al azar
    entre las muestras conservando cuantas hay de cada una: es el control negativo."""
    t_ini = [e['t_inicio'] for e in escenas]
    pares = []
    sin_funcion = 0
    for t, pc in samples:
        cs, ip = pc.split(':')
        off = (int(cs, 16) << 4) + int(ip, 16) - desplaza
        i = bisect.bisect_right(inicios, off) - 1
        if i < 0:
            sin_funcion += 1
            continue
        j = max(0, bisect.bisect_right(t_ini, t) - 1)
        pares.append((por_off[inicios[i]], escenas[j]['escena']))
    if barajar:
        etiquetas = [e for _, e in pares]
        random.Random(1982).shuffle(etiquetas)
        pares = [(f, e) for (f, _), e in zip(pares, etiquetas)]
    return collections.Counter(pares), sin_funcion


def concentracion(cuenta, min_muestras):
    """Media, por funcion CON AL MENOS min_muestras, de la fraccion de sus muestras que cae en su
    escena dominante. 1,0 = cada funcion vive en una sola escena."""
    por_fun = collections.defaultdict(collections.Counter)
    for (fun, esc), n in cuenta.items():
        por_fun[fun][esc] += n
    vale = [c for c in por_fun.values() if sum(c.values()) >= min_muestras]
    if not vale:
        return 0.0, 0
    return sum(max(c.values()) / sum(c.values()) for c in vale) / len(vale), len(vale)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--pcsample', required=True)
    ap.add_argument('--capas', required=True, help='el capas.json del mismo juego: trae el '
                                                   'desplazamiento y los offsets de cada funcion')
    ap.add_argument('--out')
    ap.add_argument('--control', action='store_true', help='corre tambien el control negativo')
    ap.add_argument('--window-ms', type=int, default=250)
    ap.add_argument('--top', type=int, default=8)
    ap.add_argument('--min-muestras', type=int, default=20,
                    help='minimo de muestras para que una funcion cuente en la concentracion')
    a = ap.parse_args()

    capas = json.load(open(a.capas, encoding='utf-8'))
    desplaza = int(capas['desplazamiento'], 16)
    funs = capas['funciones']
    inicios = sorted(int(v['offset'], 16) for v in funs.values())
    por_off = {int(v['offset'], 16): k for k, v in funs.items()}

    r = build_scenes.analyze(a.pcsample, window_ms=a.window_ms)
    escenas = r['escenas']
    samples, _meta = build_scenes.load_samples(a.pcsample)
    cuenta, sin_funcion = atribuye(samples, escenas, inicios, por_off, desplaza)

    por_fun = collections.defaultdict(collections.Counter)
    por_esc = collections.defaultdict(collections.Counter)
    for (fun, esc), n in cuenta.items():
        por_fun[fun][esc] += n
        por_esc[esc][fun] += n

    print(f'== ESCENAS x FUNCION: {len(escenas)} escenas, {len(samples):,} muestras, '
          f'{len(por_fun)} funciones vistas ==')
    print(f'   pcsample: {a.pcsample}   capas: {os.path.relpath(a.capas, RAIZ)}')
    if sin_funcion:
        print(f'   {sin_funcion:,} muestras ({100.0*sin_funcion/len(samples):.1f} %) caen en codigo '
              'que el corpus no vio arrancar: no se atribuyen')
    print()
    for e in escenas:
        top = por_esc[e['escena']].most_common(a.top)
        tot = sum(por_esc[e['escena']].values()) or 1
        print(f"  escena {e['escena']}  {e['t_inicio_ms']:.0f}-{e['t_fin_ms']:.0f} ms  "
              f"({e['duracion_ms']:.0f} ms)")
        print('     ' + ' '.join(f"{f.replace('FUN_1000_','')}:{100.0*n/tot:.0f}%" for f, n in top))

    conc, n_conc = concentracion(cuenta, a.min_muestras)
    print(f'\n  concentracion: {conc:.3f} sobre las {n_conc} funciones con >= {a.min_muestras} '
          'muestras (fraccion media en su escena dominante)')
    if a.control:
        c2, _ = atribuye(samples, escenas, inicios, por_off, desplaza, barajar=True)
        conc2, _ = concentracion(c2, a.min_muestras)
        print(f'  control negativo (etiquetas de escena barajadas): {conc2:.3f}')
        if conc > conc2 + 0.10:
            print(f'  VERDE: la atribucion real concentra {conc - conc2:+.3f} mas que la barajada')
        else:
            print(f'  ROJO: la atribucion real ({conc:.3f}) no se separa de una correspondencia '
                  f'falsa ({conc2:.3f}); el mapa no esta diciendo nada')
            return 1

    if a.out:
        salida = {
            'pcsample': a.pcsample,
            'capas': a.capas,
            'ventana_ms': a.window_ms,
            'muestras': len(samples),
            'muestras_sin_funcion': sin_funcion,
            'concentracion': round(conc, 4),
            'concentracion_min_muestras': a.min_muestras,
            'concentracion_funciones': n_conc,
            'escenas': [{'escena': e['escena'], 't_inicio_ms': e['t_inicio_ms'],
                         't_fin_ms': e['t_fin_ms'], 'duracion_ms': e['duracion_ms'],
                         'funciones': [{'func': f, 'muestras': n} for f, n in
                                       por_esc[e['escena']].most_common()]}
                        for e in escenas],
            'funciones': {f: {'escenas': [{'escena': e, 'muestras': n} for e, n in c.most_common()],
                              'capa': funs[f]['capa']}
                          for f, c in sorted(por_fun.items())},
        }
        json.dump(salida, open(a.out, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
        print(f'\n-> {a.out}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
