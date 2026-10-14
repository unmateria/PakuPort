"""Prueba UNA imagen y devuelve el antes y el despues, sin tocar el remake de verdad.

Por que existe. Para saber si un PNG queda bien habia que pasar `capture` (un minuto de partida),
`study` (varios minutos), `check` y luego lanzar el juego y mirar. Cuatro pasos y un cuarto de
hora para juzgar una imagen. Dibujar arte son muchas vueltas cortas, no pocas largas: con ese
bucle nadie itera, y lo que no se itera se entrega mal.

Aqui se corre el juego dos veces sobre la MISMA cinta —una con la imagen puesta y otra sin ella—
y se pega el resultado lado a lado. El remake real (`remake.json`) no se toca: la prueba usa un
manifiesto aparte, asi que se puede probar cualquier cosa sin ensuciar lo que ya estaba admitido.

Uso:
    python tools/remake_kit/try_asset.py <id> <clave> <imagen.png> [--ms 26000] [--export DIR]
"""

import argparse
import json
import os
import shutil
import subprocess
import sys
import tempfile

RAIZ = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)
import pngio  # noqa: E402


def corre(exp, ms, manifiesto, cinta=None, ciclos=0):
    """One run of the export, with or without the test manifest; returns its screenshot.

    With the session tape and a fixed speed, both runs replay the same input and stop at the
    same INSTRUCTION (ms * cyclesPerMs) with the guest frozen there, and the host saves the
    composed screen (PAKU_CORTE_FOTO): the two halves show the same instant of the game. Without
    them it falls back to a cut by host time, and the halves may show different moments."""
    env = dict(os.environ, PAKU_INTERP_ONLY='1', PAKU_NOAUDIO='1', PAKU_NOSPLASH='1',
               PAKU_WINDOWED='1')
    for k in ('PAKU_TAPE_REPLAY', 'PAKU_AUTOEXIT_INSN', 'PAKU_AUTOEXIT_MS', 'PAKU_CORTE_FOTO'):
        env.pop(k, None)
    if cinta and ciclos > 0:
        env.update(PAKU_TAPE_REPLAY=os.path.abspath(cinta), PAKU_AUTOEXIT_INSN=str(ms * ciclos),
                   PAKU_CORTE_FOTO='1')
    else:
        env['PAKU_AUTOEXIT_MS'] = str(ms)
    if manifiesto:
        env['PAKU_REMAKE'] = manifiesto
    else:
        env.pop('PAKU_REMAKE', None)
    tiro = os.path.join(exp, 'screenshot.png')
    if os.path.exists(tiro):
        os.remove(tiro)
    exe = os.path.join(exp, 'bin', 'Release', 'net8.0', os.path.basename(exp) + '-host.exe')
    if not os.path.exists(exe):
        raise SystemExit(f'ROJO: no existe {exe}. Compila el export primero.')
    subprocess.run([exe], cwd=exp, env=env, capture_output=True, text=True,
                   timeout=ms / 1000 + 180)
    if not os.path.exists(tiro):
        raise SystemExit('ROJO: la corrida no dejo captura de pantalla')
    return tiro


def pega(izq, der, destino):
    """Las dos capturas una al lado de la otra, con un separador para que se vea el corte."""
    wa, ha, a, _ = pngio.leer(izq)
    wb, hb, b, _ = pngio.leer(der)
    h = max(ha, hb)
    sep = 8
    w = wa + sep + wb
    out = bytearray(w * h * 4)
    for i in range(0, len(out), 4):
        out[i:i + 4] = b'\x18\x18\x1c\xff'
    for y in range(ha):
        o = (y * w) * 4
        out[o:o + wa * 4] = a[y * wa * 4:(y + 1) * wa * 4]
    for y in range(hb):
        o = (y * w + wa + sep) * 4
        out[o:o + wb * 4] = b[y * wb * 4:(y + 1) * wb * 4]
    pngio.escribir(destino, w, h, bytes(out))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('id')
    ap.add_argument('clave')
    ap.add_argument('imagen')
    ap.add_argument('--ms', type=int, default=26000, help='instante de la partida que se compara')
    ap.add_argument('--export')
    ap.add_argument('--remake')
    a = ap.parse_args()

    exp = a.export or os.path.join(RAIZ, 'export', a.id)
    rem = a.remake or os.path.join(RAIZ, 'games', a.id, 'remake')
    if not os.path.exists(a.imagen):
        print(f'ROJO: no existe {a.imagen}')
        return 2

    # El tamano obligatorio sale del encargo: si no cuadra, el motor lo estiraria a su caja y lo
    # que se veria no seria culpa del dibujo.
    pend = os.path.join(rem, 'pendiente.json')
    enc = {}
    if os.path.exists(pend):
        with open(pend, encoding='utf-8') as fh:
            enc = {e['clave']: e for e in json.load(fh).get('encargos', [])}
    e = enc.get(a.clave)
    if e is None:
        print(f'ROJO: la clave {a.clave} no esta en el encargo. Mira games/{a.id}/remake/hoja.html')
        return 2
    w, h, _, _ = pngio.leer(a.imagen)
    prop_o, prop_n = (e['w'] / e['h']) if e.get('h') else 0, w / h if h else 0
    if prop_o and abs(prop_n - prop_o) / prop_o > 0.10:
        print(f'AVISO: proporcion {w}x{h} frente a {e["w"]}x{e["h"]} del original: se vera '
              f'deformado, y eso no es culpa del dibujo.')

    tmp = tempfile.mkdtemp(prefix='paku-try-')
    shutil.copyfile(a.imagen, os.path.join(tmp, 'prueba.png'))
    manifiesto = os.path.join(tmp, 'manifiesto.json')
    with open(manifiesto, 'w', encoding='utf-8') as fh:
        json.dump({'assets': {a.clave: {'tex': 'prueba.png', 'ax': 0, 'ay': 0,
                                        'w': e.get('w') or w, 'h': e.get('h') or h}}}, fh)

    # Same instant in both runs: the session's tape and the game's fixed speed.
    cinta = os.path.join(rem, 'cinta.tape')
    ciclos = 0
    gj = os.path.join(RAIZ, 'games', a.id, 'game.json')
    if os.path.exists(gj):
        with open(gj, encoding='utf-8') as fh:
            ciclos = int(json.load(fh).get('cyclesPerMs') or 0)
    exacto = os.path.exists(cinta) and ciclos > 0
    print(f'== probando {a.clave} en el instante {a.ms} ms ==')
    if not exacto:
        print('AVISO: sin cinta o sin cyclesPerMs se corta por reloj del anfitrion: el antes y el '
              'despues pueden ser de instantes distintos.')
    salida = os.path.join(RAIZ, 'logs', 'try')
    os.makedirs(salida, exist_ok=True)
    args = (cinta, ciclos) if exacto else (None, 0)
    sin = shutil.copyfile(corre(exp, a.ms, None, *args), os.path.join(salida, 'sin.png'))
    con = shutil.copyfile(corre(exp, a.ms, manifiesto, *args), os.path.join(salida, 'con.png'))

    # ¿Lo llego a poner el motor? Si no, la comparacion no dice nada del dibujo.
    cobertura = os.path.join(exp, 'logs', 'remake-coverage.log')
    puesto = 0
    if os.path.exists(cobertura):
        for linea in open(cobertura, encoding='utf-8', errors='replace'):
            if linea.startswith(('blits=', 'dibujos_logicos=')):
                for campo in linea.split():
                    if campo.startswith('reemplazados='):
                        puesto += int(campo.split('=')[1])
    destino = os.path.join(rem, 'try-' + a.clave.replace(':', '-') + '.png')
    pega(sin, con, destino)
    if puesto == 0:
        print('AVISO: el motor NO llego a poner esta imagen en toda la corrida. O el elemento no '
              f'sale en ese momento (prueba otro --ms) o la clave ya no existe en la partida '
              f'grabada.')
    print(f'antes | despues -> {destino}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
