"""CONTROL NEGATIVO DE LAS CAPAS — el que el clasificador necesitaba y faltaba.

El clasificador (`pakuport expediente capas`) dice que funcion es render, cual audio, cual E/S y
cual LOGICA a partir de lo que la funcion HIZO durante una partida, medido con PAKU_CAPAS. La
pregunta que este juez contesta es si eso coincide con una VERDAD TERRENO INDEPENDIENTE.

La verdad terreno son las rutinas de dibujo del juego, que estan en su dossier
(`games/<id>/dossier.json`; en el juego de prueba con el que se construyo, generado casi un mes
antes de que PAKU_CAPAS existiera) y salen de OTRO subsistema: la procedencia de blit (`blitraw.bin` y
`blit.log` del kit de remakes), no del perfil de capas. En ese juego de prueba eran 21 direcciones
cs:ip con su cuenta de escrituras.

QUE SE COMPRUEBA
  1. Toda rutina de dibujo que caiga dentro de una funcion del corpus tiene que estar en una
     funcion clasificada RENDER. Un solo fallo tumba el clasificador.
  2. Y al reves: toda funcion clasificada render POR ESCRIBIR EN LA VRAM tiene que contener alguna
     rutina de la verdad terreno. Sin esto, un clasificador que marcara render todo pasaria el
     punto 1.
  3. Lo que cae fuera del corpus se cuenta y se DICE. No es un fallo del clasificador — es su
     universo: no puede juzgar codigo que el corpus no vio arrancar. Pero callarlo convertiria una
     cobertura del 4 % en un aprobado con nota.

CONTROL NEGATIVO DEL PROPIO JUEZ (--mutar): reetiqueta las funciones render como logica y exige
que el juez salga ROJO. Un juez que no se ha visto rojo no mide nada.

Uso:  python tools\control_capas.py --capas games\<id>\capas.json
                                     (--dossier games\<id>\dossier.json | --blitlog <f> [--blitraw <f>])
                                     [--mutar]
"""
import argparse
import bisect
import io
import json
import os
import sys

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, os.path.join(RAIZ, 'tools', 'dossier'))


def rutinas_del_run(blitraw, blitlog, top=64):
    """La verdad terreno tomada de un run RECIEN grabado, con las dos fuentes de la procedencia
    de blit: blitraw.bin (escrituras sueltas, byte a byte) y blit.log (rectangulos de movs/stos).

    Reutiliza el lector del dossier (`tools/dossier/build_dossier.py`) en vez de reimplementarlo:
    si el formato cambia, cambia en un sitio. Hace falta las DOS fuentes para poder exigir el
    reciproco — una funcion que pinta byte a byte no aparece en blit.log, y darla por falso
    positivo seria culpar al clasificador de un hueco de la verdad terreno."""
    import build_dossier

    class _Reloj:
        def value(self, t): return t
        def label(self): return 'ticks'

    r = build_dossier.summarize_routines(blitraw, blitlog, top, _Reloj())
    if not r:
        return []
    salida = []
    for rt in (r.get('blitraw') or {}).get('rutinas', []):
        salida.append((rt['at'], rt['escrituras'], 'blitraw'))
    for rt in (r.get('blitlog') or {}).get('rutinas', []):
        salida.append((rt['at'], rt['bytes'], 'blitlog'))
    return salida


def rutinas_de_blitlog(ruta):
    """Las direcciones `at=` de un blit.log, con sus bytes. Misma verdad terreno que el dossier
    (la procedencia de blit del kit de remakes) pero cruda, para un juego que no tiene dossier.
    Sirve ademas para cruzar PARTIDAS DISTINTAS: en un juego de prueba el blit.log y el perfil de
    capas se grabaron con dos semanas de diferencia, asi que ninguna de las dos medidas ha visto a la otra."""
    cuenta = {}
    for linea in io.open(ruta, encoding='utf-8', errors='replace'):
        at = by = None
        for tok in linea.split():
            if tok.startswith('at='):
                at = tok[3:]
            elif tok.startswith('bytes='):
                by = int(tok[6:])
        if at:
            cuenta[at] = cuenta.get(at, 0) + (by or 1)
    return [(at, n, 'blitlog') for at, n in sorted(cuenta.items(), key=lambda kv: -kv[1])]


def rutinas_de_dibujo(dossier):
    """Las direcciones cs:ip que escribieron en la VRAM, con su cuenta, sacadas del dossier.

    Dos fuentes distintas dentro de el: blitraw (escrituras byte a byte con procedencia) y blitlog
    (rectangulos, el camino de `movs`). Las dos son del kit de remakes, no del perfil de capas.
    """
    salida = []
    r = dossier.get('rutinas', {})
    for rt in r.get('blitraw', {}).get('rutinas', []):
        salida.append((rt['at'], rt['escrituras'], 'blitraw'))
    for rt in r.get('blitlog', {}).get('rutinas', []):
        salida.append((rt['at'], rt['bytes'], 'blitlog'))
    return salida


def lineal(at):
    seg, off = at.split(':')
    return (int(seg, 16) << 4) + int(off, 16)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--capas', required=True, help='games/<id>/capas.json')
    ap.add_argument('--dossier', help='verdad terreno: games/<id>/dossier.json')
    ap.add_argument('--blitlog', help='verdad terreno: un blit.log crudo del kit de remakes')
    ap.add_argument('--blitraw', help='y su blitraw.bin. Con los dos, la verdad terreno esta '
                                      'completa y el reciproco (punto 2) es exigible')
    ap.add_argument('--mutar', action='store_true',
                    help='control negativo: reetiqueta Render como Logica; el juez DEBE salir rojo')
    a = ap.parse_args()
    if not (a.dossier or a.blitlog or a.blitraw):
        ap.error('hace falta una verdad terreno: --dossier, o --blitlog [--blitraw]')

    capas = json.load(open(a.capas, encoding='utf-8'))
    if 'desplazamiento' not in capas:
        print('ROJO: el capas.json no trae "desplazamiento"; regeneralo con la version de hoy')
        return 2
    desplaza = int(capas['desplazamiento'], 16)

    funs = capas['funciones']
    if a.mutar:
        funs = {k: dict(v, capa='Logica' if v['capa'] == 'Render' else v['capa'])
                for k, v in funs.items()}

    inicios = sorted(int(v['offset'], 16) for v in funs.values())
    por_off = {int(v['offset'], 16): (k, v) for k, v in funs.items()}

    def contiene(off):
        i = bisect.bisect_right(inicios, off) - 1
        return por_off[inicios[i]] if i >= 0 else None

    if a.blitraw:
        rutinas = rutinas_del_run(a.blitraw, a.blitlog)
        fuente_vt = a.blitraw
        completa = True
    elif a.blitlog:
        rutinas = rutinas_de_blitlog(a.blitlog)
        fuente_vt = a.blitlog
        # Sin blitraw la verdad terreno solo ve los rectangulos: una funcion que escribe la VRAM
        # byte a byte no sale, asi que el reciproco no se puede exigir sin acusar en falso.
        completa = False
    else:
        rutinas = rutinas_de_dibujo(json.load(open(a.dossier, encoding='utf-8')))
        fuente_vt = a.dossier
        completa = True
    print(f'== CONTROL de las capas contra la procedencia de blit ==')
    print(f'   clasificacion: {os.path.relpath(a.capas, RAIZ)}  (cobertura {capas.get("cobertura_pct", "?")} %)')
    print(f'   verdad terreno: {os.path.relpath(fuente_vt, RAIZ)} — {len(rutinas)} rutinas de dibujo'
          + ('   [MUTADO: control negativo]' if a.mutar else ''))
    print()

    ok, mal, fuera = [], [], []
    cubren = set()
    for at, cuenta, fuente in rutinas:
        off = lineal(at) - desplaza
        c = contiene(off)
        if c is None:
            fuera.append((at, cuenta, fuente))
            continue
        nombre, ficha = c
        if ficha['capa'] == 'Render':
            ok.append((at, nombre))
            cubren.add(nombre)
        else:
            mal.append((at, cuenta, fuente, nombre, ficha['capa']))

    for at, cuenta, fuente, nombre, capa in mal:
        print(f'  FALLO  {at} ({cuenta} escrituras, {fuente}) cae en {nombre}, clasificada {capa}')

    # Punto 2: ninguna funcion marcada render-por-VRAM sin respaldo en la verdad terreno.
    # El reciproco se exige solo a las funciones que pintan CON SUS PROPIAS INSTRUCCIONES: si el
    # que escribe es un servicio de INT llamado por ellas, la procedencia de blit no puede verlas
    # (el BIOS no pinta con instrucciones del invitado) y contarlas como falso positivo seria
    # culpar al clasificador de un hueco de la verdad terreno. Se cuentan aparte y se dicen.
    por_servicio = [k for k, v in funs.items()
                    if v['capa'] == 'Render' and 'servicio de INT' in v.get('prueba', '')]
    falsos = [k for k, v in funs.items()
              if completa and v['capa'] == 'Render' and 'escribe en la VRAM' in v.get('prueba', '')
              and 'servicio de INT' not in v.get('prueba', '') and k not in cubren]
    for k in falsos:
        print(f'  FALSO POSITIVO  {k} dice render por escribir en la VRAM y la verdad terreno no lo ve')

    print(f'  rutinas dentro del corpus y clasificadas Render : {len(ok)}')
    print(f'  rutinas dentro del corpus y MAL clasificadas    : {len(mal)}')
    print(f'  rutinas fuera del corpus (no juzgables)         : {len(fuera)}')
    if fuera:
        print('     ' + ' '.join(at for at, _, _ in fuera[:20]) + (' …' if len(fuera) > 20 else ''))
    print(f'  funciones render sin respaldo (falsos positivos): '
          + (f'{len(falsos)}' if completa else 'NO EXIGIBLE (verdad terreno sin blitraw.bin)'))
    if por_servicio:
        print(f'  render POR SERVICIO, no juzgable contra la procedencia de blit: {len(por_servicio)}')
        print('     ' + ' '.join(por_servicio))

    verde = not mal and not falsos and ok
    print()
    if a.mutar:
        # En el control negativo lo que se exige es lo contrario: el juez tiene que romperse.
        print('VERDE (control negativo): el juez detecta la mutacion' if not verde
              else 'ROJO (control negativo): el juez ha dado por bueno un clasificador mutado')
        return 0 if not verde else 1
    print(f'VERDE: {len(ok)} de {len(ok) + len(mal)} rutinas juzgables bien clasificadas' if verde
          else 'ROJO: el clasificador de capas no coincide con la verdad terreno')
    return 0 if verde else 1


if __name__ == '__main__':
    sys.exit(main())
