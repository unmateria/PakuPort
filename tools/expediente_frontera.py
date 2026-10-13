"""LA FRONTERA DE LO DESCONOCIDO — que codigo se ejecuto de verdad, y cual no.

Parte del expediente de cada funcion. Es el antidoto contra la alucinacion: la IA que reescribe
necesita saber DONDE NO HAY DATOS, porque ahi es exactamente donde inventa con aplomo. Un comentario
que explica una rama que nunca se tomo es una conjetura con formato de hecho.

El mapa lo graba el nucleo con PAKU_COBERTURA=<fichero> (src/PakuPort.Core/Cobertura.cs): el rango
completo de cada instruccion que el interprete ejecuta. Aqui se cruza con las funciones del corpus
(que traen su offset y el desplazamiento escritos en capas.json) y sale:

  * por funcion: cuantos de sus bytes se ejecutaron y cuantos no;
  * el codigo ejecutado que NO cae en ninguna funcion conocida — el hueco del corpus, medido;
  * y el total, que es la metrica de cobertura del porteo.

CONTROL (--control): toda direccion que produjo un evento medido por PAKU_CAPAS tiene que estar
dentro de un tramo cubierto. Son dos subsistemas distintos del mismo run: si la cobertura tuviera un
agujero donde otro subsistema vio ejecutarse una instruccion, la cobertura estaria mintiendo.

CONTROL NEGATIVO (--mutar): se desplazan los tramos --mutar-bytes (64 por defecto) y el control
tiene que romperse. Un juez que no se ha visto rojo no mide nada. El desplazamiento tiene que ser
MAYOR QUE UN TRAMO TIPICO o el control se queda corto: medido en un juego de prueba, con 3 bytes solo
2 de 52 direcciones se salian (los tramos son largos y el evento seguia cayendo dentro); con 64 se
sale la mayoria. Un control negativo que apenas se rompe no prueba que el juez mire el dato.

Uso:
  python tools\\expediente_frontera.py --cobertura logs\\expediente\\cobertura-<id>.csv ^
         --capas games\\<id>\\capas.json ^
         [--capas-csv logs\\expediente\\capas-<id>.csv --control] [--out games\\<id>\\frontera.json]
"""
import argparse
import bisect
import io
import json
import os
import sys

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def lee_tramos(ruta, desplaza_mutacion=0):
    """[(inicio, fin)] en direcciones LINEALES, y la cabecera con los totales del run."""
    tramos, cab = [], {}
    for linea in io.open(ruta, encoding='utf-8'):
        linea = linea.strip()
        if linea.startswith('#'):
            for tok in linea.lstrip('#').split():
                if '=' in tok:
                    k, v = tok.split('=', 1)
                    cab[k] = v
            continue
        if not linea or linea.startswith('inicio'):
            continue
        ini, fin, _ = linea.split(',')
        tramos.append((int(ini, 16) + desplaza_mutacion, int(fin, 16) + desplaza_mutacion))
    return tramos, cab


def cubierto(tramos_ini, tramos, lineal):
    i = bisect.bisect_right(tramos_ini, lineal) - 1
    return i >= 0 and tramos[i][0] <= lineal < tramos[i][1]


def bytes_en(tramos, ini, fin):
    """Bytes cubiertos dentro de [ini, fin)."""
    n = 0
    for a, b in tramos:
        if b <= ini:
            continue
        if a >= fin:
            break
        n += min(b, fin) - max(a, ini)
    return n


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--cobertura', required=True)
    ap.add_argument('--capas', required=True, help='trae el desplazamiento y el offset de cada funcion')
    ap.add_argument('--capas-csv', help='el CSV de PAKU_CAPAS del MISMO run, para el control')
    ap.add_argument('--control', action='store_true')
    ap.add_argument('--mutar', action='store_true', help='control negativo: desplaza los tramos')
    ap.add_argument('--mutar-bytes', type=int, default=64)
    ap.add_argument('--out')
    ap.add_argument('--top', type=int, default=20)
    a = ap.parse_args()

    capas = json.load(open(a.capas, encoding='utf-8'))
    desplaza = int(capas['desplazamiento'], 16)
    funs = capas['funciones']

    tramos, cab = lee_tramos(a.cobertura, a.mutar_bytes if a.mutar else 0)
    tramos.sort()
    tramos_ini = [t[0] for t in tramos]
    total_cubierto = sum(b - x for x, b in tramos)

    # El rango de una funcion va de su offset al de la siguiente que el corpus vio. Es una cota
    # SUPERIOR: entre dos funciones puede haber datos o codigo de una tercera que el corpus no
    # aprendio. Cuando ese hueco es enorme el porcentaje deja de significar nada — en un juego
    # de prueba, una funcion tiene 848.096 bytes de "rango" y un 0,1 % que no dice absolutamente nada del
    # codigo de esa funcion. Asi que el porcentaje SOLO se publica si el rango es plausible para
    # una funcion (<= 4 KiB); si no, se marca rango_fiable=false y se publican solo los bytes.
    RANGO_FIABLE = 4096
    orden = sorted((int(v['offset'], 16), k) for k, v in funs.items())
    filas = []
    for i, (off, nombre) in enumerate(orden):
        fin = orden[i + 1][0] if i + 1 < len(orden) else off + 0x400
        lin_i, lin_f = off + desplaza, fin + desplaza
        cub = bytes_en(tramos, lin_i, lin_f)
        fiable = lin_f - lin_i <= RANGO_FIABLE
        filas.append({'func': nombre, 'offset': f'0x{off:X}', 'bytes_rango': lin_f - lin_i,
                      'bytes_ejecutados': cub, 'capa': funs[nombre]['capa'],
                      'rango_fiable': fiable,
                      'pct': round(100.0 * cub / max(1, lin_f - lin_i), 1) if fiable else None})

    primera = orden[0][0] + desplaza if orden else 0
    tope = (orden[-1][0] + desplaza + 0x400) if orden else 0
    sin_duenyo = sum(b - x for x, b in tramos if b <= primera or x >= tope)

    print(f'== FRONTERA de {os.path.basename(a.capas)} ==')
    print(f'   cobertura: {os.path.relpath(a.cobertura, RAIZ)}'
          + ('   [MUTADO: control negativo]' if a.mutar else ''))
    print(f'   {total_cubierto:,} bytes de codigo ejecutados en {len(tramos)} tramos, '
          f'{int(cab.get("instrucciones", 0)):,} instrucciones')
    if cab.get('fuera_del_megabyte', '0') != '0':
        print(f'   {int(cab["fuera_del_megabyte"]):,} instrucciones fuera del megabyte: mundo de 32 '
              'bits, NO medido (limite declarado)')
    print()
    print(f'  {"funcion":<18} {"capa":<12} {"ejecutado":>10} {"del rango":>10}   %')
    fiables = [f for f in filas if f['rango_fiable']]
    for f in sorted(fiables, key=lambda x: x['pct'])[:a.top]:
        print(f'  {f["func"]:<18} {f["capa"]:<12} {f["bytes_ejecutados"]:>10} '
              f'{f["bytes_rango"]:>10}  {f["pct"]:5.1f}')
    if len(fiables) > a.top:
        print(f'  … {len(fiables) - a.top} funciones mas con rango fiable (las de menor cobertura arriba)')
    if len(filas) > len(fiables):
        print(f'  {len(filas) - len(fiables)} funciones con rango NO fiable (> 4 KiB hasta la '
              'siguiente del corpus): se publican sus bytes ejecutados, no un porcentaje')
    print(f'\n  codigo ejecutado FUERA del rango de toda funcion conocida: {sin_duenyo:,} bytes '
          f'({100.0 * sin_duenyo / max(1, total_cubierto):.1f} % de lo ejecutado)')

    rc = 0
    if a.control:
        if not a.capas_csv:
            print('\n  (sin --capas-csv no hay control: se necesita otro subsistema del mismo run)')
        else:
            faltan, mirados = [], 0
            for linea in io.open(a.capas_csv, encoding='utf-8').read().splitlines()[1:]:
                p = linea.split(',')
                if len(p) < 4:
                    continue
                mirados += 1
                lin = int(p[2], 16)
                if not cubierto(tramos_ini, tramos, lin):
                    faltan.append((p[0], p[1], p[2], p[3]))
            print(f'\n  CONTROL: direcciones con eventos de PAKU_CAPAS que la cobertura no cubre: '
                  f'{len(faltan)} de {mirados}')
            for t, d, l, n in faltan[:10]:
                print(f'     {l}  {t} {d}  ({n} veces)')
            if a.mutar:
                print('  VERDE (control negativo): el control detecta la mutacion' if faltan
                      else '  ROJO (control negativo): el control da por buena una cobertura movida')
                rc = 0 if faltan else 1
            else:
                print('  VERDE: toda instruccion que otro subsistema vio ejecutarse esta cubierta'
                      if not faltan else '  ROJO: la cobertura tiene agujeros donde hubo eventos')
                rc = 0 if not faltan else 1

    if a.out and not a.mutar:
        json.dump({'cobertura': a.cobertura, 'capas': a.capas,
                   'bytes_ejecutados': total_cubierto, 'tramos': len(tramos),
                   'instrucciones': int(cab.get('instrucciones', 0)),
                   'bytes_sin_duenyo': sin_duenyo,
                   '_nota_rango': 'el rango de una funcion va de su offset al de la siguiente que el '
                                  'corpus vio: es una COTA SUPERIOR, puede llevar datos o codigo de '
                                  'una funcion que el corpus no aprendio',
                   'funciones': filas},
                  open(a.out, 'w', encoding='utf-8'), indent=1, ensure_ascii=False)
        print(f'\n-> {a.out}')
    return rc


if __name__ == '__main__':
    sys.exit(main())
