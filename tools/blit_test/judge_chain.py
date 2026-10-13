"""Juez de la CADENA de procedencia: fichero -> buffer A -> buffer B -> VRAM.

CHAIN.COM (ver gen_chain.py) mete un salto RAM->RAM entre la lectura del fichero y el
volcado a VRAM. La telemetria debe seguir sabiendo de que fichero salio el blit final:
es lo que pide la "letra pequena" de la doctrina de procedencia ("la procedencia
debe ENCADENARSE, no basta el ultimo salto") y el prerrequisito para que un sprite
compuesto con el fondo conserve su identidad.

Compara ademas contra BLIT2.COM, el caso DIRECTO que ya funciona, para que el juez
distinga "la cadena no encadena" de "la procedencia esta rota del todo".

Uso: python tools/blit_test/judge_chain.py [export_dir]   (default export/chain)
"""
import os, subprocess, sys, time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXP = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, 'export', 'chain')
DATA = open(os.path.join(REPO, 'tools', 'blit_test', 'DATA.BIN'), 'rb').read()


def fnv1a(data):
    h = 2166136261
    for b in data:
        h = ((h ^ b) * 16777619) & 0xFFFFFFFF
    return h


expected_fnv = f'{fnv1a(DATA):08X}'
log_path = os.path.join(EXP, 'logs', 'blit.log')
try:
    os.remove(log_path)
except OSError:
    pass

env = dict(os.environ, PAKU_LOG_BLIT='1', PAKU_NOSPLASH='1', PAKU_AUTOEXIT_MS='4000')
r = subprocess.run(['dotnet', 'run', '-c', 'Release'], cwd=EXP, env=env,
                   capture_output=True, text=True, timeout=300)
print([l for l in r.stdout.splitlines() if 'autoexit' in l] or r.stdout[-300:])

for _ in range(30):
    if os.path.exists(log_path):
        break
    time.sleep(1)
else:
    raise SystemExit('ROJO: no se escribio blit.log')

lines = [l.strip() for l in open(log_path, encoding='utf-8') if l.startswith('t=')]
print(f'{len(lines)} lineas de blit.log:')
for l in lines:
    print('   ' + l)

# El blit que llega a VRAM es el que importa (los RAM->RAM no se loguean como rects).
vram = [dict(p.split('=', 1) for p in l.split() if '=' in p)
        for l in lines if 'lin=A0000' in l]

ok = True


def check(cond, msg):
    global ok
    print(('  OK  ' if cond else '  ROJO') + ' ' + msg)
    ok = ok and cond


check(len(vram) == 1, f'un solo blit a VRAM: {len(vram)}')
if vram:
    b = vram[0]
    check(b.get('fnv') == expected_fnv,
          f'el contenido que llega a VRAM es el del fichero (fnv={expected_fnv}): '
          f'{b.get("fnv")}')
    got = b.get('file')
    check(got == f'DATA.BIN@0x0',
          f'la procedencia SOBREVIVE al buffer intermedio (file=DATA.BIN@0x0): '
          f'{got if got else "SIN ETIQUETA <-- la cadena se rompe en el salto RAM->RAM"}')
print('GATE ' + ('OK' if ok else 'ROJO'))
sys.exit(0 if ok else 1)
