"""Comprueba que ACTIVAR EL REMAKE no cambia lo que hace el juego.

Es la suposicion sobre la que se apoya el juez de fidelidad entero: si el arte se pintara dentro
de la maquina emulada, comparar "con remake" contra "sin remake" no mediria el arte, mediria dos
juegos distintos. Se da por hecho que el reemplazo vive en el anfitrion y el invitado ni se
entera — y este proyecto ya ha pagado caro dar cosas por hechas, asi que se mide.

Como se mide, y por que asi: la captura de pantalla del host se toma de `RenderFramebuffer()`,
o sea de la VRAM DEL INVITADO, sin la capa HD encima. Si esa imagen sale **byte a byte igual**
con el remake puesto y sin el, el invitado escribio exactamente los mismos pixeles: el remake no
le ha tocado ni la memoria ni el flujo. Eso es determinista y falsable, al reves que comparar
contadores de instrucciones, que dependen del reloj de pared porque la corrida se corta por
tiempo (por eso el contador se reporta, pero solo como sintoma).

  python tools/remake_kit/judge_no_perturba.py [export]     (por defecto: orden)
"""

import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile

RAIZ = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)
import pngio  # noqa: E402

EXPORT = sys.argv[1] if len(sys.argv) > 1 else 'orden'
DIR = os.path.join(RAIZ, 'export', EXPORT)


def corre(remake, destino):
    """Corre el juego y devuelve el hash de los FOTOGRAMAS DEL INVITADO.

    Se usa PAKU_PRESENT_DUMP y no la captura de pantalla del host: aquella es el contenido de la
    VENTANA, que SI lleva la capa HD encima, asi que compararla no probaria nada. El volcado sale
    de `RenderFramebuffer()`, o sea de la VRAM del invitado, sin overlay. De paso ejercita el
    camino de captura sin ventana, que es lo que se acaba de anadir al motor.
    """
    os.makedirs(destino, exist_ok=True)
    env = dict(os.environ, PAKU_INTERP_ONLY='1', PAKU_NOSPLASH='1', PAKU_AUTOEXIT_MS='6000',
               PAKU_PRESENT_DUMP=os.path.join(destino, 'f'),
               PAKU_PRESENT_DUMP_AFTER_MS='4000', PAKU_PRESENT_DUMP_N='4')
    if remake:
        env['PAKU_REMAKE'] = remake
    p = os.path.join(DIR, 'logs', 'exit.log')
    if os.path.exists(p):
        os.remove(p)
    r = subprocess.run(['dotnet', 'run', '-c', 'Release', '--no-build'], cwd=DIR, env=env,
                       capture_output=True, text=True, timeout=600)
    marcos = sorted(f for f in os.listdir(destino) if f.endswith('.rgba'))
    if not marcos:
        raise SystemExit('ROJO: no se volco ningun fotograma. Sin PAKU_PRESENT_CAPTURE efectivo '
                         f'no hay captura sin ventana.\n{r.stdout[-800:]}{r.stderr[-400:]}')
    datos = open(os.path.join(destino, marcos[-1]), 'rb').read()
    tam = marcos[-1].rsplit('.', 2)[-2]
    insns = None
    exit_log = os.path.join(DIR, 'logs', 'exit.log')
    if os.path.exists(exit_log):
        m = re.search(r'interp_insns:\s*(\d+)', open(exit_log, encoding='utf-8').read())
        if m:
            insns = int(m.group(1))
    return hashlib.sha1(datos).hexdigest(), tam, insns


def main():
    if not os.path.isdir(DIR):
        raise SystemExit(f'ROJO: no existe {DIR} (portea el .COM primero)')

    tmp = tempfile.mkdtemp(prefix='no_perturba_')
    # Un manifiesto que SI reemplaza algo, o la prueba no diria nada: se cambia el cuadrado A.
    # Su clave es el FNV de los bytes de origen, calculado igual que en judge_orden.py — no una
    # constante copiada, que se fosilizaria en cuanto el motor cambiara de hash.
    h = 2166136261
    for _ in range(64):
        h = ((h ^ 0x0A) * 16777619) & 0xFFFFFFFF
    clave = f'{h:08X}'
    png = os.path.join(tmp, 'hd.png')
    pngio.escribir(png, 8, 8, bytearray([255, 0, 0, 255] * 64))
    man = os.path.join(tmp, 'remake.json')
    with open(man, 'w', encoding='utf-8') as fh:
        json.dump({'assets': {clave: {'tex': 'hd.png', 'ax': 0, 'ay': 0, 'w': 64, 'h': 1}}}, fh)
    print(f'   manifiesto de prueba: la clave {clave} (el cuadrado A) -> hd.png')

    print('-- sin remake --')
    h_sin, tam_sin, i_sin = corre(None, os.path.join(tmp, 'sin'))
    print(f'   vram sha1={h_sin[:16]}  {tam_sin}  interp_insns={i_sin or "?"}')

    print('-- con remake --')
    h_con, tam_con, i_con = corre(man, os.path.join(tmp, 'con'))
    print(f'   vram sha1={h_con[:16]}  {tam_con}  interp_insns={i_con or "?"}')

    cov = os.path.join(DIR, 'logs', 'remake-coverage.log')
    reemplazados = 0
    if os.path.exists(cov):
        m = re.search(r'reemplazados=(\d+)', open(cov, encoding='utf-8').read())
        reemplazados = int(m.group(1)) if m else 0
    print(f'   el manifiesto reemplazo {reemplazados} blits')

    shutil.rmtree(tmp, ignore_errors=True)

    if reemplazados == 0:
        print('ROJO: el remake no reemplazo NADA, asi que esta prueba no demuestra nada. '
              'Revisa que la clave del manifiesto exista en este juego.')
        return 1
    if h_sin != h_con or tam_sin != tam_con:
        print('ROJO: la VRAM del invitado CAMBIA al activar el remake. El reemplazo no es solo '
              'de presentacion: toca al juego, y entonces el juez de fidelidad compara dos '
              'juegos distintos y no vale.')
        return 1
    if i_sin and i_con:
        d = abs(i_con - i_sin) / max(i_sin, 1)
        print(f'   (las instrucciones difieren un {d * 100:.1f} %, y eso es del reloj de pared: '
              f'la corrida se corta por tiempo, no por instrucciones)')
    print('VERDE: con el remake activo y reemplazando de verdad, la VRAM del invitado sale '
          'IDENTICA byte a byte. El arte se pinta fuera de la maquina emulada.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
