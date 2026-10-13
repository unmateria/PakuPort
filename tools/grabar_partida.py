# -*- coding: utf-8 -*-
"""GRABAR UNA PARTIDA DE VERDAD, para que el A/B tenga algo que juzgar.

POR QUE EXISTE. El arnes ya es determinista: dos corridas de la misma cinta dejan
0 bytes de diferencia. Pero con la cinta que habia, el control de las funciones portadas NO tiene
poder discriminante, y esta medido: la traduccion ROTA a proposito da el mismo volcado que la
buena, porque la funcion corre en las instrucciones 39-53 del arranque y en esa partida sale de su
bucle sin escribir nada. Lo que falta no es mas arnes: es una partida en la que las funciones
hagan su trabajo.

Esto graba una. El juego se lanza con ventana y con sonido — se juega normal — y cada tecla,
cada movimiento del raton y cada click se anota con la instruccion del invitado en que entro
(`PAKU_TAPE_RECORD`). Al salir queda la cinta, y a partir de ahi la partida se puede repetir
tantas veces como haga falta, siempre igual.

DOS COSAS QUE HAY QUE SABER:

1. **La cinta va con SU binario.** Los ticks son instrucciones del invitado, asi que una cinta
   grabada con un export y reproducida con otro distinto no vale: cualquier cambio del nucleo
   mueve cuantas instrucciones cuesta el mismo trabajo. Por eso esto graba con el export que hay
   AHORA y avisa de la fecha del binario.
2. **Grabar ancla el reloj** (`InputTape.ClockLocked`), igual que reproducir. Es lo que hace que
   la partida se pueda repetir; exige `cyclesPerMs > 0` en el game.json.

Uso:
    python tools/grabar_partida.py --game <id>                # juega tu; se graba al salir
    python tools/grabar_partida.py --game <id> --verifica     # comprueba la cinta grabada
"""
import argparse
import os
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))


def exe_del_export(gid):
    d = os.path.join(ROOT, 'export', gid, 'bin', 'Release')
    if not os.path.isdir(d):
        return None
    for tfm in sorted(os.listdir(d)):
        p = os.path.join(d, tfm, gid + '-host.exe')
        if os.path.exists(p):
            return p
    return None


def corre(exe, env_extra, cwd):
    env = dict(os.environ, PAKU_INTERP_ONLY='1', PAKU_NOSPLASH='1')
    env.update(env_extra)
    return subprocess.run([exe], cwd=cwd, env=env)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', required=True, help='id del juego (el de games/<id>/ y export/<id>/)')
    ap.add_argument('--cinta', help='donde dejar la cinta (por defecto games/<id>/remake/partida.tape)')
    ap.add_argument('--ventana', action='store_true',
                    help='en ventana en vez de a pantalla completa (una captura de pantalla de la '
                         'pantalla completa acelerada suele salir NEGRA, y eso despista)')
    ap.add_argument('--verifica', action='store_true',
                    help='no graba: reproduce la cinta dos veces sin ventana y comprueba que sale igual')
    ap.add_argument('--insn', type=int, default=0,
                    help='con --verifica, en que instruccion se corta cada corrida (0 = la cinta entera)')
    a = ap.parse_args()

    gid = a.game
    export = os.path.join(ROOT, 'export', gid)
    exe = exe_del_export(gid)
    if exe is None:
        print('no hay export compilado en export\\%s: corre `pakuport export --game games\\%s\\game.json` '
              'y `dotnet build -c Release` ahi' % (gid, gid))
        return 1
    cinta = a.cinta or os.path.join(ROOT, 'games', gid, 'remake', 'partida.tape')
    os.makedirs(os.path.dirname(cinta), exist_ok=True)

    if not a.verifica:
        if os.path.exists(cinta):
            print('YA HAY una cinta en %s (%d eventos). Se sobreescribe al salir del juego.'
                  % (cinta, sum(1 for _ in open(cinta))))
        print('binario: %s (%s)' % (exe, time.strftime('%Y-%m-%d %H:%M',
                                                       time.localtime(os.path.getmtime(exe)))))
        print('grabando en %s — juega normal y sal con F12 o cerrando la ventana.' % cinta)
        env = {'PAKU_TAPE_RECORD': cinta}
        if a.ventana:
            env['PAKU_WINDOWED'] = '1'
        corre(exe, env, export)
        if not os.path.exists(cinta):
            print('la cinta no se escribio: el juego no llego a grabar ningun evento')
            return 1
        n = sum(1 for _ in open(cinta))
        ultimo = 0
        for linea in open(cinta):
            try:
                ultimo = max(ultimo, int(linea.split()[0]))
            except (ValueError, IndexError):
                pass
        print('cinta grabada: %d eventos, la partida dura %d instrucciones del invitado' % (n, ultimo))
        print('comprueba que se repite igual:  python tools/grabar_partida.py --game %s --verifica' % gid)
        return 0

    # --verifica: la cinta no vale de nada si no se repite. Dos corridas SIN VENTANA cortadas en
    # la misma instruccion tienen que dejar la misma memoria y la misma pantalla.
    if not os.path.exists(cinta):
        print('no hay cinta en %s' % cinta)
        return 1
    insn = a.insn
    if insn <= 0:
        insn = max(int(l.split()[0]) for l in open(cinta) if l.split()) or 1
    print('reproduciendo dos veces hasta la instruccion %d (sin ventana, sin sonido)' % insn)
    volcados = []
    for i in (1, 2):
        dest = os.path.join(export, 'logs', 'verifica%d.bin' % i)
        corre(exe, {'PAKU_TAPE_REPLAY': os.path.abspath(cinta),
                    'PAKU_HEADLESS': '1', 'PAKU_NOAUDIO': '1',
                    'PAKU_AUTOEXIT_INSN': str(insn),
                    'PAKU_MEMDUMP': dest}, export)
        volcados.append(dest)
    fallo = False
    for suf, que in (('', 'la memoria del invitado'), ('.vram', 'la pantalla')):
        a_, b_ = [open(v + suf, 'rb').read() if os.path.exists(v + suf) else b'' for v in volcados]
        if not a_ or len(a_) != len(b_):
            print('ROJO   %s: no hay dos volcados comparables' % que); fallo = True; continue
        d = sum(1 for x, y in zip(a_, b_) if x != y)
        print(('VERDE' if not d else 'ROJO ') + '  %s: %d bytes de diferencia' % (que, d))
        fallo = fallo or d > 0
    print('\n== LA CINTA %s ==' % ('NO SE REPITE' if fallo else 'SE REPITE'))
    return 1 if fallo else 0


if __name__ == '__main__':
    sys.exit(main())
