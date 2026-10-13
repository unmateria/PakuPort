"""El juez de la PANTALLA PRESENTADA: comprueba que poner el remake no CONGELA el juego.

Por que existe, con el caso que lo motiva. En la primera prueba manual del kit, el
veredicto fue *"no veo nada en hd, y el raton y el teclado no funcionan"*. Y el juez que ya
habia daba VERDE, con razon: `judge_no_perturba.py` compara lo que el INVITADO escribe en VRAM
con y sin remake, y eso era identico byte a byte. El invitado estaba perfecto. Lo que fallaba
era la capa de encima: una textura a pantalla completa se pintaba sobre todo y no moria nunca
(un dibujo one-shot que nadie sobrescribe vive para siempre, por diseno), asi que el jugador
miraba una FOTOGRAFIA mientras el juego corria debajo respondiendo a todo.

La leccion, que es de metodo: **un juez del lado del invitado no puede certificar lo que se ve.**
Este mira la VENTANA, que es lo unico que el jugador juzga.

Como se mide, y por que asi. No sirve "con remake se parece a sin remake": si el arte HD es
bueno TIENE que verse distinto, asi que la diferencia no dice nada. Lo que se mide es la
ACTIVIDAD: cuantos pixeles de la ventana cambian entre dos instantes. Un juego vivo mueve
pixeles —cursor, resaltados, animacion—; una foto pegada encima no mueve ninguno. Si la
actividad con el remake puesto se desploma respecto a la de sin remake, la pantalla esta
congelada, y da igual lo bonito que sea el arte.

Cuatro corridas deterministas (mismo export, mismo tope de tiempo, sin sonido y con la ventana
oculta): dos sin remake en los instantes T1 y T2, y dos con remake en los mismos instantes.

Control negativo, obligatorio: se repite la medida con `PAKU_REMAKE_NO_AGUJEREAR=1`, que
reproduce a voluntad el fallo original. Si el juez no sale ROJO ahi, no esta midiendo nada y lo
dice en vez de dar un verde vacio.

ESTADO: el juez SABE NEGARSE —detecta los casos en que su medida no valdria y lo
dice en vez de dar un verde vacio— pero NO HA CONSEGUIDO PRONUNCIARSE sobre el juego de prueba. Cuatro intentos,
cada uno arreglando el problema del anterior y descubriendo el siguiente:

  1. intro (12-16 s): cambia el 97 % de la pantalla, pero el asset reemplazado no esta puesto
     ahi, asi que el control negativo tampoco congela nada;
  2. menu (20-26 s): el asset SI esta, pero sin nadie tocando nada no se mueve un solo pixel;
  3. con raton sintetico hay movimiento, pero va a reloj de PARED y las corridas dejan de ser
     comparables (un "conserva el 194 %" es ruido). Se arreglo grabando el movimiento en la CINTA
     y reproduciendola: eso SI es determinista;
  4. midiendo dentro de la caja del asset en vez de en toda la ventana, para que el movimiento de
     fuera no ahogue la senal. Y aun asi el control negativo no se desploma: da 75 %, 135 %,
     182 % en corridas equivalentes.

La razon de fondo, y es un dato util: **una pantalla "congelada" NO esta quieta**. La instancia
entra y sale (nace, se ensucia, muere, vuelve), asi que la imagen alterna entre la foto y el juego
y eso mueve MAS pixeles que el juego normal. La premisa "una foto no mueve pixeles" es falsa, y
con ella se cae la metrica de actividad. Un juez de esto probablemente tiene que mirar otra cosa:
si lo presentado en la caja del asset se corresponde con lo que el invitado tiene en VRAM ahi
(comparando el fotograma del invitado, que `PAKU_PRESENT_DUMP` ya vuelca, con la ventana), en vez
de cuanto cambia.

Mientras tanto la garantia de que el congelado no vuelve NO la da este juez, sino las medidas
directas del arreglo: 51,27 % -> 0,58 % de divergencia respecto al juego real, y el control
positivo con textura magenta (el arte se pinta Y el cursor y el resaltado se ven encima).

Uso:
    python tools/remake_kit/judge_presentado.py <id> [--export DIR] [--remake FICHERO]
                                                     [--t1 12000] [--t2 16000] [--caida 0.5]
"""

import argparse
import json
import os
import shutil
import subprocess
import sys

RAIZ = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)
import pngio  # noqa: E402


def corre(exp, ms, remake, destino, sin_agujerear=False, cinta=None):
    """Una corrida hasta `ms` y la captura de la VENTANA que el host deja al salir."""
    env = dict(os.environ,
               PAKU_INTERP_ONLY='1',
               PAKU_AUTOEXIT_MS=str(ms),   # cierra solo Y oculta la ventana: no roba el foco
               PAKU_NOAUDIO='1',           # puede haber alguien jugando en esta misma maquina
               PAKU_WINDOWED='1')
    # Sin nadie tocando nada un menu se queda COMPLETAMENTE quieto, y sin actividad esta prueba
    # no puede distinguir un congelado de una pantalla en reposo. La CINTA da las dos cosas que
    # hacen falta a la vez: movimiento (el que se grabo) y determinismo, porque se reinyecta en
    # los mismos ticks. El raton sintetico daba movimiento pero a reloj de PARED, y entonces las
    # corridas dejan de ser comparables entre si y los numeros bailan.
    if cinta and os.path.exists(cinta):
        env['PAKU_TAPE_REPLAY'] = cinta
    if remake:
        env['PAKU_REMAKE'] = remake
    else:
        env.pop('PAKU_REMAKE', None)
    if sin_agujerear:
        env['PAKU_REMAKE_NO_AGUJEREAR'] = '1'
    tiro = os.path.join(exp, 'screenshot.png')
    if os.path.exists(tiro):
        os.remove(tiro)
    r = subprocess.run([os.path.join(exp, 'bin', 'Release', 'net8.0',
                                     os.path.basename(exp) + '-host.exe')],
                       cwd=exp, env=env, capture_output=True, text=True, timeout=ms / 1000 + 180)
    if not os.path.exists(tiro):
        raise SystemExit(f'ROJO: la corrida de {ms} ms no dejo captura de ventana en {tiro}')
    shutil.copyfile(tiro, destino)
    return destino, geometria(r.stdout + r.stderr)


def geometria(salida):
    """Donde cae la imagen del juego dentro de la ventana, sacado del latido del host
    (`asp=`, `escala=`, `ox=`, `oy=`). Sin esto solo se puede medir la ventana ENTERA, y
    entonces el movimiento que cae FUERA del asset reemplazado ahoga la senal: medido en un juego de prueba, el
    control negativo se quedaba en un 75 % cuando dentro de la caja la actividad era cero."""
    g = {}
    for linea in salida.splitlines():
        if '[HB]' not in linea:
            continue
        for campo in linea.split():
            for k in ('asp=', 'escala=', 'ox=', 'oy='):
                if campo.startswith(k):
                    try:
                        g[k[:-1]] = float(campo[len(k):].replace(',', '.'))
                    except ValueError:
                        pass
    return g or None


def actividad(png_a, png_b, caja=None):
    """Pixeles que cambian entre dos instantes. Es la senal de vida de la pantalla.

    `caja` acota la medida al rectangulo del asset reemplazado (en pixeles de ventana): es lo
    unico que el remake puede tapar, asi que medir fuera de el solo anade ruido."""
    wa, ha, a, _ = pngio.leer(png_a)
    wb, hb, b, _ = pngio.leer(png_b)
    if (wa, ha) != (wb, hb):
        raise SystemExit(f'ROJO: capturas de distinto tamano ({wa}x{ha} vs {wb}x{hb}); '
                         'sin geometria estable no hay comparacion posible')
    x0, y0, x1, y1 = caja if caja else (0, 0, wa, ha)
    x0, y0 = max(0, int(x0)), max(0, int(y0))
    x1, y1 = min(wa, int(x1)), min(ha, int(y1))
    n = 0
    for y in range(y0, y1):
        fila = y * wa
        for x in range(x0, x1):
            i = (fila + x) * 4
            if a[i] != b[i] or a[i + 1] != b[i + 1] or a[i + 2] != b[i + 2]:
                n += 1
    return n, max(1, (x1 - x0) * (y1 - y0))


def caja_del_asset(manifiesto, geo):
    """El rectangulo del asset MAYOR del manifiesto, en pixeles de ventana. Es la zona que el
    remake puede tapar, y por tanto la unica donde la medida significa algo."""
    if not geo or not all(k in geo for k in ('asp', 'escala', 'ox', 'oy')):
        return None
    try:
        with open(manifiesto, encoding='utf-8') as fh:
            assets = json.load(fh).get('assets', {})
    except (OSError, ValueError):
        return None
    if not assets:
        return None
    mayor = max(assets.values(), key=lambda e: (e.get('w') or 0) * (e.get('h') or 0))
    x, y = mayor.get('ax', 0), mayor.get('ay', 0)
    w, h = mayor.get('w') or 0, mayor.get('h') or 0
    if w <= 0 or h <= 0:
        return None
    esc, asp, ox, oy = geo['escala'], geo['asp'], geo['ox'], geo['oy']
    return (ox + x * esc, oy + y * asp * esc,
            ox + (x + w) * esc, oy + (y + h) * asp * esc)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('id')
    ap.add_argument('--export')
    ap.add_argument('--remake')
    ap.add_argument('--t1', type=int, default=12000)
    ap.add_argument('--t2', type=int, default=16000)
    ap.add_argument('--cinta', help='cinta.tape a reproducir (por defecto, la del remake)')
    ap.add_argument('--caida', type=float, default=0.5,
                    help='fraccion minima de la actividad original que debe conservarse')
    a = ap.parse_args()

    exp = a.export or os.path.join(RAIZ, 'export', a.id)
    remake = a.remake or os.path.join(RAIZ, 'games', a.id, 'remake', 'remake.json')
    if not os.path.isdir(exp):
        print(f'ROJO: no existe el export {exp}')
        return 2
    if not os.path.exists(remake):
        print(f'ROJO: no existe el manifiesto {remake}. Sin arte admitido no hay nada que juzgar.')
        return 2
    cinta = a.cinta or os.path.join(RAIZ, 'games', a.id, 'remake', 'cinta.tape')
    tmp = os.path.join(RAIZ, 'logs', 'judge-presentado')
    os.makedirs(tmp, exist_ok=True)

    print(f'== pantalla presentada: {a.id} ==')
    sin1, geo = corre(exp, a.t1, None, os.path.join(tmp, 'sin-t1.png'), cinta=cinta)
    sin2, _ = corre(exp, a.t2, None, os.path.join(tmp, 'sin-t2.png'), cinta=cinta)
    caja = caja_del_asset(remake, geo)
    if caja:
        print(f'  midiendo dentro del asset reemplazado: {int(caja[2] - caja[0])}x'
              f'{int(caja[3] - caja[1])} px de ventana')
    else:
        print('  (sin geometria del host: se mide la ventana entera, con mas ruido)')
    act_sin, total = actividad(sin1, sin2, caja)
    print(f'  sin remake: {act_sin} px de {total} cambian entre {a.t1} y {a.t2} ms '
          f'({100 * act_sin / total:.2f} %)')
    if act_sin == 0:
        print('ROJO: sin el remake la pantalla YA esta quieta entre esos dos instantes, asi que '
              'esta prueba no puede distinguir un congelado. Elige otros --t1/--t2 (o una escena '
              'con movimiento) y vuelve a pasarla.')
        return 2

    con1, _ = corre(exp, a.t1, remake, os.path.join(tmp, 'con-t1.png'), cinta=cinta)
    con2, _ = corre(exp, a.t2, remake, os.path.join(tmp, 'con-t2.png'), cinta=cinta)
    act_con, _ = actividad(con1, con2, caja)
    frac = act_con / act_sin
    print(f'  con remake: {act_con} px ({100 * act_con / total:.2f} %) — conserva el '
          f'{frac * 100:.1f} % de la actividad')

    # Control negativo: reproducir el fallo a proposito. Un juez que nunca se ha visto rojo no
    # ha demostrado que sepa ponerse rojo.
    neg1, _ = corre(exp, a.t1, remake, os.path.join(tmp, 'neg-t1.png'), sin_agujerear=True, cinta=cinta)
    neg2, _ = corre(exp, a.t2, remake, os.path.join(tmp, 'neg-t2.png'), sin_agujerear=True, cinta=cinta)
    act_neg, _ = actividad(neg1, neg2, caja)
    frac_neg = act_neg / act_sin
    print(f'  control negativo (PAKU_REMAKE_NO_AGUJEREAR=1): {act_neg} px — conserva el '
          f'{frac_neg * 100:.1f} %')

    if frac_neg >= a.caida:
        print('ROJO: el control negativo NO se ha puesto rojo. Con la textura tapandolo todo la '
              'actividad deberia desplomarse y no lo ha hecho, asi que esta medida no vale: o el '
              'asset reemplazado no cubre lo que se mueve, o la palanca no llego al host.')
        return 2

    if frac < a.caida:
        print(f'ROJO: el remake CONGELA la pantalla. Conserva el {frac * 100:.1f} % de la '
              f'actividad (el minimo es {a.caida * 100:.0f} %). El juego sigue corriendo debajo '
              f'—el teclado y el raton llegan— pero el jugador esta mirando una imagen fija.')
        return 1

    print('VERDE: el remake no congela la pantalla (y el control negativo si la congela).')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
