"""EL CORPUS GUIADO POR COBERTURA — dos pasadas con la misma cinta.

La cuota del grabador guarda las OCHO PRIMERAS invocaciones de cada unidad, y un juego hace casi
siempre lo mismo al principio: en un juego de prueba los ocho primeros turnos son el arranque, y el disparo
que acierta, el fin de partida o el record no entraban nunca. Traducir esos caminos sin caso es
traducir sin red.

  1. PRIMERA PASADA: `PAKU_COBERTURA` apunta, en `<csv>.novedades`, la instruccion en que cada
     direccion de codigo se ejecuto POR PRIMERA VEZ, y en la que cada salto condicional cayo por
     primera vez por cada uno de sus dos lados (sin eso, las SALIDAS de los bucles se quedaban sin
     casos: salir casi nunca ejecuta una direccion nueva).
  2. SEGUNDA PASADA, con la misma cinta y el mismo binario: `PAKU_CORPUS` graba la cuota normal y
     ADEMAS, con `PAKU_CORPUS_NOVEDADES`, las invocaciones que contienen uno de esos instantes. En
     la misma corrida van las otras capturas del expediente (capas, cobertura y muestreo del PC):
     cruzar capturas de partidas distintas da falsos positivos, medido.

Funciona porque el arnes es determinista (dos corridas con la misma cinta son la misma partida,
instruccion a instruccion). Si la segunda pasada no reprodujera la primera, los instantes caerian
en otro sitio y lo diria el indice del corpus (`casos_por_novedad` a cero).

Todo sin ventana, sin sonido y sin las traducciones (`PAKU_SIN_PORTADAS=1`): lo que se graba es el
binario original.

Uso:
  python tools/corpus_guiado.py --export export/<id> ^
         --cinta games/<id>/remake/cobertura.tape --insn 400000000 ^
         --corpus games/<id>/corpus --capturas logs/expediente/<id>
"""
import argparse
import os
import shutil
import subprocess
import sys


def corre(export, cinta, insn, extra):
    env = dict(os.environ, PAKU_INTERP_ONLY='1', PAKU_NOSPLASH='1', PAKU_HEADLESS='1',
               PAKU_NOAUDIO='1', PAKU_SIN_FRENO='1', PAKU_SIN_PORTADAS='1',
               PAKU_AUTOEXIT_INSN=str(insn), PAKU_TAPE_REPLAY=os.path.abspath(cinta))
    env.update(extra)
    r = subprocess.run(['dotnet', 'run', '-c', 'Release', '--no-build'], cwd=export, env=env,
                       capture_output=True, text=True, timeout=7200)
    for linea in (r.stdout + r.stderr).splitlines():
        if linea.startswith(('[CORPUS]', '[COBERTURA]', '[CAPAS]', '[CORTE]')):
            print('   ' + linea)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--export', required=True)
    ap.add_argument('--cinta', required=True)
    ap.add_argument('--insn', type=int, required=True, help='instruccion en que se corta')
    ap.add_argument('--corpus', required=True, help='directorio del corpus (se sustituye)')
    ap.add_argument('--capturas', required=True, help='prefijo de los csv de la segunda pasada')
    ap.add_argument('--extra', action='append', default=[], help='VAR=valor para las dos pasadas')
    a = ap.parse_args()
    extra = dict(e.split('=', 1) for e in a.extra)

    pref = os.path.abspath(a.capturas)
    os.makedirs(os.path.dirname(pref), exist_ok=True)
    novedades = pref + '-pasada1.csv'

    print('1. primera pasada: cuando se ejecuta por primera vez cada direccion')
    corre(a.export, a.cinta, a.insn, dict(extra, PAKU_COBERTURA=novedades))
    if not os.path.exists(novedades + '.novedades'):
        print('   ROJO: la primera pasada no dejo el fichero de novedades')
        return 1

    print('2. segunda pasada: el corpus, guiado por esos instantes, y las capturas del expediente')
    corpus = os.path.abspath(a.corpus)
    if os.path.exists(corpus):
        shutil.rmtree(corpus)
    corre(a.export, a.cinta, a.insn, dict(
        extra,
        PAKU_CORPUS=corpus,
        PAKU_CORPUS_NOVEDADES=novedades + '.novedades',
        PAKU_CAPAS=pref + '-capas.csv',
        PAKU_COBERTURA=pref + '-cobertura.csv',
        PAKU_SAMPLE_PC='1', PAKU_SAMPLE_PC_MS='1'))
    print('   corpus -> %s' % corpus)
    print('   capas -> %s-capas.csv   cobertura -> %s-cobertura.csv' % (pref, pref))
    print('   muestreo del PC -> %s' % os.path.join(a.export, 'logs', 'pcsample.log'))
    return 0


if __name__ == '__main__':
    sys.exit(main())
