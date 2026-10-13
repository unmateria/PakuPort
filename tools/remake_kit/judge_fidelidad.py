"""El JUEZ DE FIDELIDAD: corre la MISMA cinta con y sin remake, y compara.

Es lo que faltaba cuando la intro de un juego de prueba se dio por buena y la prueba manual la tumbo:
no habia forma de decir en numeros que se habia ganado y que se habia roto, asi que se juzgaba a
ojo. Aqui salen cuatro cifras, y ninguna es opinable:

  **cobertura** — que fraccion de los blits de la partida llevan arte, global y POR ESCENA. Una
      cobertura global alta puede esconder una escena entera sin tocar, y eso se ve en pantalla.
  **huecos**   — los assets sin arte, ORDENADOS POR CUANTO SALEN. Da igual que falten 200 si el
      que mas se ve sale 412 veces: se ataca por ahi, no por orden alfabetico.
  **deriva**   — arte que se sale de su sitio: instancias que se van de la pantalla, o cuyo
      tamano declarado no es el que el motor midio. Es como se detecta un sprite mal colocado.
  **regresion**— lo que TENIA arte en el juicio anterior y ya no. Sin esto, "mejorar" puede
      romper y nadie se entera hasta la prueba manual.

Y la comprobacion que sostiene a las otras cuatro: **el invitado tiene que dibujar exactamente lo
mismo** en las dos corridas. Si no, no se estan comparando dos versiones de lo mismo — se estan
comparando dos juegos, y el juicio no vale. Lo que lo hace posible es la CINTA: bajo
`PAKU_TAPE_REPLAY` el reloj se ancla al contador de instrucciones (`InputTape.ClockLocked`), asi
que la ejecucion es repetible.

Se compara la SECUENCIA DE BLITS —que se dibujo, donde, de que tamano y con que identidad— y no
unos cuantos fotogramas volcados, por una razon medida: los fotogramas se muestrean cada 1/70 s y
en un instante de transicion la muestra puede caer un fotograma antes o despues, asi que dos
corridas IDENTICAS ya diferian en el ultimo de seis. La traza de blits no tiene ese problema y
ademas es mucho mas fuerte: son los 1611 dibujos de la partida entera, no seis muestras. Del
`t=`/`frame=` se prescinde a proposito — el tick exacto en que ocurre cada blit tiene jitter de
planificacion, y lo que importa es QUE se dibujo, no en que microsegundo.

Uso:  python tools/remake_kit/judge_fidelidad.py <id> --export <dir> --remake <dir>
"""

import argparse
import hashlib
import json
import os
import re
import shutil
import sys
import subprocess
import tempfile
from collections import defaultdict


def corre(export, destino, remake, cinta, ms):
    """Una corrida de la cinta. `remake` None = la version original."""
    os.makedirs(destino, exist_ok=True)
    env = dict(os.environ,
               PAKU_INTERP_ONLY='1', PAKU_NOSPLASH='1', PAKU_AUTOEXIT_MS=str(ms),
               PAKU_LOG_BLIT='1',                       # para saber QUE se dibujo y cuando
               PAKU_TAPE_REPLAY=os.path.abspath(cinta),  # reloj anclado: las dos corridas casan
               PAKU_PRESENT_DUMP=os.path.join(destino, 'f'),
               # Por FOTOGRAMAS, no por milisegundos: bajo la cinta la captura es determinista
               # (tiempo virtual), pero un cronometro de pared haria que las dos corridas
               # volcaran instantes distintos y el juez diria que el remake toca al juego.
               PAKU_PRESENT_DUMP_AFTER_FRAMES='400',
               PAKU_PRESENT_DUMP_N='6')
    if remake:
        env['PAKU_REMAKE'] = os.path.abspath(remake)
    for viejo in ('blit.log', 'remake-coverage.log'):
        p = os.path.join(export, 'logs', viejo)
        if os.path.exists(p):
            os.remove(p)
    subprocess.run(['dotnet', 'run', '-c', 'Release', '--no-build'], cwd=export, env=env,
                   capture_output=True, text=True, timeout=ms // 1000 + 600)
    # Los logs se copian: la siguiente corrida los machaca.
    for nombre in ('blit.log', 'remake-coverage.log'):
        o = os.path.join(export, 'logs', nombre)
        if os.path.exists(o):
            shutil.copyfile(o, os.path.join(destino, nombre))
    return firma_de_lo_dibujado(os.path.join(destino, 'blit.log'))


# Lo que identifica un dibujo: que operacion, en que modo, a donde, de que tamano y con que
# identidad. NO va el `t=`/`frame=`: el tick exacto tiene jitter de planificacion y compararlo
# haria fallar dos corridas identicas.
CAMPOS_FIRMA = ('op', 'mode', 'df', 'dst', 'lin', 'n', 'bytes', 'fnv', 'fill', 'file')


def firma_de_lo_dibujado(ruta):
    """(numero de blits, sha1 de la secuencia) — el retrato de lo que hizo el invitado."""
    if not os.path.exists(ruta):
        return (0, '')
    filas = []
    for linea in open(ruta, encoding='utf-8', errors='replace'):
        if not linea.startswith('t='):
            continue
        f = dict(p.split('=', 1) for p in linea.split() if '=' in p)
        filas.append('|'.join(f.get(c, '') for c in CAMPOS_FIRMA))
    return (len(filas), hashlib.sha1('\n'.join(filas).encode()).hexdigest())


def clave_de(f):
    if 'fnv' in f:
        return ('file:' + f['file']) if 'file' in f else f['fnv']
    return f.get('fill')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('id')
    ap.add_argument('--export', required=True)
    ap.add_argument('--remake', required=True)
    ap.add_argument('--ms', type=int, default=40000)
    a = ap.parse_args()

    cinta = os.path.join(a.remake, 'cinta.tape')
    runtime = os.path.join(a.remake, 'remake.json')
    for f in (cinta, runtime):
        if not os.path.exists(f):
            print(f'ROJO: falta {f}')
            return 2

    admitidos = set(json.load(open(runtime, encoding='utf-8')).get('assets', {}))
    if not admitidos:
        print('ROJO: remake.json no admite ningun asset. Pasa `check` con arte entregado.')
        return 2

    tmp = tempfile.mkdtemp(prefix='fidelidad_')
    try:
        print('-- corrida SIN remake (la referencia) --')
        n_sin, h_sin = corre(a.export, os.path.join(tmp, 'sin'), None, cinta, a.ms)
        print(f'   {n_sin} blits, firma {h_sin[:12]}')

        print('-- corrida CON remake --')
        n_con, h_con = corre(a.export, os.path.join(tmp, 'con'), runtime, cinta, a.ms)
        print(f'   {n_con} blits, firma {h_con[:12]}')

        # --- la invariante que sostiene todo lo demas ---
        if (n_sin, h_sin) != (n_con, h_con):
            print(f'ROJO: el INVITADO no dibuja lo mismo con el remake puesto ({n_sin} blits vs '
                  f'{n_con}). El reemplazo no es solo de presentacion, o la cinta no reproduce '
                  f'igual las dos veces. Mientras eso pase, el resto de las cifras no valen.')
            return 1
        if n_sin == 0:
            print('ROJO: no se registro ningun blit; no hay nada que juzgar.')
            return 1

        # --- cobertura, global y por escena ---
        cov = os.path.join(tmp, 'con', 'remake-coverage.log')
        texto = open(cov, encoding='utf-8').read() if os.path.exists(cov) else ''
        m = re.search(r'blits=(\d+) reemplazados=(\d+) cobertura=([\d.,]+)%', texto)
        blits, reemp = (int(m.group(1)), int(m.group(2))) if m else (0, 0)
        cobertura = 100.0 * reemp / blits if blits else 0.0

        escenas = []
        esc_path = os.path.join(a.remake, 'escenas.json')
        if os.path.exists(esc_path):
            escenas = json.load(open(esc_path, encoding='utf-8')).get('escenas', [])

        por_escena = defaultdict(lambda: [0, 0])     # escena -> [con arte, total]
        huecos = defaultdict(int)
        blit_log = os.path.join(tmp, 'con', 'blit.log')
        if os.path.exists(blit_log):
            for linea in open(blit_log, encoding='utf-8', errors='replace'):
                if not linea.startswith('t='):
                    continue
                f = dict(p.split('=', 1) for p in linea.split() if '=' in p)
                cl = clave_de(f)
                if cl is None:
                    continue
                t = int(f['t'])
                esc = next((e['escena'] for e in escenas
                            if e['t_inicio'] <= t < e['t_fin']), 0)
                tiene = cl in admitidos
                por_escena[esc][1] += 1
                if tiene:
                    por_escena[esc][0] += 1
                else:
                    huecos[cl] += 1

        # --- deriva: arte que no cabe donde dice el motor que va ---
        deriva = []
        for linea in texto.splitlines():
            if not linea.startswith('viva '):
                continue
            g = dict(p.split('=', 1) for p in linea.split() if '=' in p)
            try:
                x, y = int(g['x']), int(g['y'])
            except (KeyError, ValueError):
                continue
            if x < 0 or y < 0 or x >= 320 or y >= 200:
                deriva.append(f'{g.get("tex", "?")} en ({x},{y}), fuera de la pantalla')

        # --- regresion contra el juicio anterior ---
        historico = os.path.join(a.remake, 'informe.json')
        antes = json.load(open(historico, encoding='utf-8')) if os.path.exists(historico) else {}
        perdidos = sorted(set(antes.get('admitidos', [])) - admitidos)

        # --- informe ---
        L = [f'# Juicio de fidelidad — {a.id}', '',
             f'- **cobertura**: {cobertura:.1f} %  ({reemp} de {blits} blits con arte)',
             f'- **huecos**: {len(huecos)} assets sin arte'
             + (f', el mas visto sale {max(huecos.values())} veces' if huecos else ''),
             f'- **deriva**: {len(deriva)} instancias fuera de caja',
             f'- **regresion**: {"ninguna" if not perdidos else str(len(perdidos)) + " assets perdieron su arte"}',
             f'- el invitado dibuja exactamente lo mismo con y sin remake: si '
             f'({n_sin} blits, firma `{h_sin[:12]}`)',
             '']
        if por_escena:
            L += ['## Por escena', '', '| escena | con arte | blits | cobertura |', '|---|---|---|---|']
            for esc in sorted(por_escena):
                con, tot = por_escena[esc]
                L.append(f'| {esc} | {con} | {tot} | {100.0 * con / tot if tot else 0:.1f} % |')
            L.append('')
        if huecos:
            L += ['## Huecos, por cuanto se ven', '', '| clave | veces |', '|---|---|']
            for cl, n in sorted(huecos.items(), key=lambda kv: -kv[1])[:40]:
                L.append(f'| `{cl}` | {n} |')
            L.append('')
        if deriva:
            L += ['## Deriva', ''] + [f'- {d}' for d in deriva[:40]] + ['']
        if perdidos:
            L += ['## Regresion', ''] + [f'- `{c}` tenia arte y ya no' for c in perdidos[:40]] + ['']

        with open(os.path.join(a.remake, 'informe.md'), 'w', encoding='utf-8') as fh:
            fh.write('\n'.join(L))
        with open(historico, 'w', encoding='utf-8') as fh:
            json.dump({'cobertura': cobertura, 'blits': blits, 'reemplazados': reemp,
                       'admitidos': sorted(admitidos)}, fh, indent=1)

        detalle = ' · '.join(f'esc{e} {100.0 * v[0] / v[1] if v[1] else 0:.1f} %'
                             for e, v in sorted(por_escena.items()))
        print(f'cobertura {cobertura:.1f} %' + (f'  ({detalle})' if detalle else ''))
        print(f'huecos: {len(huecos)} assets sin arte'
              + (f', el mas visto sale {max(huecos.values())} veces' if huecos else '')
              + f'  -> {os.path.join(a.remake, "informe.md")}')
        print(f'deriva: {len(deriva)} fuera de caja        '
              f'regresion: {"ninguna" if not perdidos else len(perdidos)}')

        rojo = bool(deriva) or bool(perdidos)
        print('ROJO' if rojo else 'VERDE')
        return 1 if rojo else 0
    finally:
        shutil.rmtree(tmp, ignore_errors=True)


if __name__ == '__main__':
    sys.exit(main())
