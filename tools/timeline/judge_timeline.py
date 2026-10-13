"""Juez del EJE DE TIEMPO COMUN (todos los logs sellan con Cpu.TickCount).

Verifica de forma objetiva que la traza DOS ([int21]/[4A], PAKU_LOG_INT21=1) se puede
CRUZAR con el resto de la telemetria, que es justo lo que no se podia hacer antes de
sellarla: cada linea lleva t=<Cpu.TickCount> y ese reloj es el mismo que sella blit.log.

Que comprueba, todo medido en UNA corrida headless del export:
  1. TODAS las lineas [int21]/[4A] de stderr llevan t=.
  2. La secuencia de t= de esas lineas es MONOTONA NO DECRECIENTE (Cpu.TickCount es un
     contador global que solo sube; si alguna linea se saliera del orden, el sello no
     valdria como eje).
  3. Los rangos [t_min, t_max] de la traza int21 y de logs/blit.log del MISMO run se
     SOLAPAN, y hay eventos de blit dentro de la ventana de la traza DOS -> el cruce
     entre logs funciona de verdad, no solo "ambos llevan un numero".

Uso:  python tools/timeline/judge_timeline.py <export_dir> [autoexit_ms]
      (por defecto 15000 ms)
Salida: numeros medidos + "GATE OK" / "GATE ROJO"; codigo de salida 0/1.
"""
import os
import re
import subprocess
import sys

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
if len(sys.argv) < 2:
    raise SystemExit('uso: python tools/timeline/judge_timeline.py <export_dir> [autoexit_ms]')
EXPORT = sys.argv[1]
AUTOEXIT_MS = sys.argv[2] if len(sys.argv) > 2 else '15000'

T_RE = re.compile(r'\bt=(\d+)\b')


def run_export():
    """Headless (PAKU_AUTOEXIT_MS oculta la ventana: no roba el foco). Devuelve stderr."""
    env = dict(os.environ,
               PAKU_INTERP_ONLY='1',      # procedencia y ticks por INSTRUCCION (no etiquetas)
               PAKU_LOG_INT21='1',
               PAKU_LOG_BLIT='1',
               PAKU_AUTOEXIT_MS=AUTOEXIT_MS)
    stale = os.path.join(EXPORT, 'logs', 'blit.log')
    if os.path.exists(stale):
        os.remove(stale)
    r = subprocess.run(['dotnet', 'run', '-c', 'Release'], cwd=EXPORT, env=env,
                       capture_output=True, text=True, errors='replace', timeout=900)
    for line in r.stdout.splitlines():
        if 'autoexit' in line:
            print('  ' + line)
    return r.stderr


def int21_ticks(stderr):
    """(#lineas, #sin sello, ticks en orden de aparicion, primer ejemplo sin sello)."""
    total = unstamped = 0
    ticks = []
    first_bad = None
    for line in stderr.splitlines():
        if not (line.startswith('[int21]') or line.startswith('[4A]')):
            continue
        total += 1
        m = T_RE.search(line)
        if m is None:
            unstamped += 1
            if first_bad is None:
                first_bad = line
        else:
            ticks.append(int(m.group(1)))
    return total, unstamped, ticks, first_bad


def blit_ticks(path):
    ticks = []
    if not os.path.exists(path):
        return ticks
    with open(path, 'r', errors='replace') as f:
        for line in f:
            if line.startswith('t='):
                ticks.append(int(line[2:line.index(' ')]))
    return ticks


ok = True


def check(cond, msg):
    global ok
    print(('  OK   ' if cond else '  ROJO ') + msg)
    ok = ok and cond


print(f'export={EXPORT} autoexit={AUTOEXIT_MS} ms')
stderr = run_export()
total, unstamped, ticks, first_bad = int21_ticks(stderr)
blits = blit_ticks(os.path.join(EXPORT, 'logs', 'blit.log'))

print(f'lineas [int21]/[4A]={total} sin_sello={unstamped} eventos blit.log={len(blits)}')
if ticks:
    print(f'int21 t=[{ticks[0]} .. {ticks[-1]}] (min={min(ticks)} max={max(ticks)})')
if blits:
    print(f'blit  t=[{min(blits)} .. {max(blits)}]')

check(total > 0, f'la traza int21 tiene lineas: {total}')
check(unstamped == 0,
      f'todas las lineas llevan t=: sin_sello={unstamped}' +
      (f' ej: {first_bad[:110]}' if first_bad else ''))

desc = [i for i in range(1, len(ticks)) if ticks[i] < ticks[i - 1]]
check(not desc,
      'secuencia de t= monotona no decreciente' +
      ('' if not desc else f' — {len(desc)} retrocesos, 1o en la linea {desc[0]}: '
                           f'{ticks[desc[0] - 1]} -> {ticks[desc[0]]}'))

check(len(blits) > 0, f'blit.log tiene eventos sellados: {len(blits)}')

if ticks and blits:
    lo = max(min(ticks), min(blits))
    hi = min(max(ticks), max(blits))
    inside = sum(1 for t in blits if min(ticks) <= t <= max(ticks))
    span = hi - lo
    print(f'solape int21 n blit = [{lo} .. {hi}] = {span} ticks; '
          f'{inside} blits dentro de la ventana de la traza DOS')
    check(span > 0, f'los rangos de t se SOLAPAN (ancho del solape {span} ticks)')
    check(inside > 0, f'hay blits sellados dentro de la ventana int21: {inside}')
else:
    check(False, 'faltan ticks de int21 o de blit para medir el solape')

print('GATE ' + ('OK' if ok else 'ROJO'))
sys.exit(0 if ok else 1)
