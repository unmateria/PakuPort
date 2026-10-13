# -*- coding: utf-8 -*-
"""EL UMBRAL DE UN BUCLE SE MIDE, NO SE ELIGE A OJO.

`PAKU_CORPUS_BUCLE_MIN` decide que salto hacia atras merece grabarse como cabecera de bucle
(la unidad del corpus puede ser una VUELTA de bucle, no solo una llamada). Es un numero, y un numero sin medida al lado es una
opinion. Esto graba el mismo juego con varios umbrales y publica, para cada uno, cuantas
unidades salen, cuantos casos, cuanto ocupa el corpus, que porcentaje de los eventos del juego
queda atribuido a alguna unidad y cuantos bytes de codigo ejecutado no son de nadie.

LO QUE SALIO EN UN JUEGO DE PRUEBA (40 M de instrucciones, cinta de atraccion):

     umbral  unidades  llamadas  vueltas   casos  salidas     MB  eventos %  B sin dueno
          0        12        12        0      95        0   13.5        4.9         1396
          8        52        12       40     408       58   54.7      100.0           40
         16        40        12       28     312       48   42.1      100.0           40
         32        32        12       20     246       34   33.4      100.0           40
         64        29        12       17     222       26   30.2      100.0           40
        128        17        12        5     126        5   17.6       88.1          195

Y lo que eso dice, que es mas util que un numero:

  * **La cobertura es una MESETA, no un pico.** De 8 a 64 sale exactamente lo mismo: 100 % de los
    eventos atribuidos y 40 bytes de codigo sin dueno. El umbral NO lo decide la cobertura.
  * **Fuera de la meseta se rompe por los dos lados.** Con 0 (sin bucles, el control negativo de
    la palanca) el corpus cubre el 4,9 % y deja 1.396 bytes sin dueno; con 128 se pierden los
    bucles medianos y baja al 88,1 %.
  * **Dentro de la meseta lo que cambia es el precio y el grano**: 54,7 MB y 52 unidades a un
    extremo, 30,2 MB y 29 al otro. El defecto (32) esta en medio a proposito, y es una eleccion
    de equilibrio DECLARADA, no una medida que obligue a ese valor.

Uso:  python tools/control_umbral_bucle.py --game games/<id>/game.json \\
          --export export/<id> --cinta games/<id>/remake/atraccion.tape \\
          [--insn 40000000] [--umbrales 0,8,16,32,64,128]
"""

import argparse
import json
import os
import shutil
import subprocess
import sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
CLI = os.path.join(ROOT, 'src', 'PakuPort.Cli', 'bin', 'Release', 'net10.0', 'PakuPort.Cli.exe')


def tam(d):
    return sum(os.path.getsize(os.path.join(r, f))
               for r, _, fs in os.walk(d) for f in fs)


def una_corrida(umbral, args, tmp):
    """Graba el corpus con ese umbral y devuelve sus numeros. Sin ventana y sin sonido: esto se
    lanza muchas veces seguidas y puede haber alguien usando la misma maquina."""
    corpus = os.path.join(tmp, 'corpus-u%d' % umbral)
    capas = os.path.join(tmp, 'capas-u%d.csv' % umbral)
    cobertura = os.path.join(tmp, 'cob-u%d.csv' % umbral)
    if os.path.exists(corpus):
        shutil.rmtree(corpus)

    env = dict(os.environ,
               PAKU_INTERP_ONLY='1', PAKU_NOSPLASH='1', PAKU_HEADLESS='1', PAKU_NOAUDIO='1',
               PAKU_SIN_PORTADAS='1',
               PAKU_AUTOEXIT_INSN=str(args.insn),
               PAKU_TAPE_REPLAY=os.path.abspath(args.cinta),
               PAKU_CORPUS=corpus,
               PAKU_CORPUS_BUCLE_MIN=str(umbral),
               PAKU_CAPAS=capas.replace(os.sep, '/'),
               PAKU_COBERTURA=cobertura.replace(os.sep, '/'))
    subprocess.run(['dotnet', 'run', '-c', 'Release', '--no-build'],
                   cwd=args.export, env=env, capture_output=True, text=True, timeout=7200)

    ix = json.load(open(os.path.join(corpus, 'indice.json'), encoding='utf-8'))
    capas_json = os.path.join(corpus, 'capas.json')
    subprocess.run([CLI, 'expediente', 'capas', '--game', args.game, '--corpus', corpus,
                    '--capas', capas, '--out', capas_json],
                   capture_output=True, text=True, cwd=ROOT)
    pct = json.load(open(capas_json, encoding='utf-8')).get('cobertura_pct', 0)

    frontera_json = os.path.join(corpus, 'frontera.json')
    subprocess.run([sys.executable, os.path.join(ROOT, 'tools', 'expediente_frontera.py'),
                    '--cobertura', cobertura, '--capas', capas_json, '--out', frontera_json],
                   capture_output=True, text=True, cwd=ROOT)
    sin_dueno = json.load(open(frontera_json, encoding='utf-8')).get('bytes_sin_duenyo', -1)

    fila = (umbral, ix['funciones_con_casos'], ix['llamadas_con_casos'], ix['bucles_con_casos'],
            ix['casos_totales'], ix.get('casos_que_salen_del_bucle', 0), tam(corpus) / 1e6,
            pct, sin_dueno)
    shutil.rmtree(corpus)
    return fila


def main():
    p = argparse.ArgumentParser()
    p.add_argument('--game', required=True)
    p.add_argument('--export', required=True)
    p.add_argument('--cinta', required=True)
    p.add_argument('--insn', type=int, default=40000000)
    p.add_argument('--umbrales', default='0,8,16,32,64,128')
    args = p.parse_args()

    tmp = os.path.join(ROOT, 'logs', 'umbral-bucle')
    os.makedirs(tmp, exist_ok=True)

    cab = ('umbral', 'unidades', 'llamadas', 'vueltas', 'casos', 'salidas', 'MB',
           'eventos %', 'B sin dueno')
    print('%7s %9s %9s %8s %7s %8s %6s %10s %12s' % cab, flush=True)
    filas = []
    for u in [int(x) for x in args.umbrales.split(',')]:
        f = una_corrida(u, args, tmp)
        filas.append(f)
        print('%7d %9d %9d %8d %7d %8d %6.1f %10.1f %12d' % f, flush=True)

    # La lectura, dicha en voz alta: donde esta la meseta y donde se rompe.
    con_bucles = [f for f in filas if f[0] > 0]
    meseta = [f for f in con_bucles if f[7] >= max(x[7] for x in con_bucles) - 0.05]
    if meseta:
        lo, hi = meseta[0][0], meseta[-1][0]
        print()
        print('La cobertura es una MESETA de %d a %d (%.1f %% de los eventos, %d B sin dueno).'
              % (lo, hi, meseta[0][7], meseta[0][8]))
        print('Dentro de ella lo que cambia es el precio y el grano: %.1f MB / %d unidades a '
              '%.1f MB / %d unidades.' % (meseta[0][6], meseta[0][1], meseta[-1][6], meseta[-1][1]))
        print('El umbral NO lo decide la cobertura: el defecto es una eleccion de equilibrio, y '
              'esta tabla es lo que la sostiene.')
    shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    main()
