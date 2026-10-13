"""Judge for the HD-replacement liveness gap: ghost instances of a MOVING sprite.

GHOST.COM (see gen_ghost.py) walks a 16x8 sprite across the screen, repainting only
6.25% of each abandoned position — the delta-blitting pattern measured on a
real-mode game's cursor (~8%), which never reaches the 25% dirty threshold. Every position the
sprite passes through therefore stays registered as a live HD instance.

The judge measures the gap: live instances vs the 1 the cursor actually is. It is
EXPECTED RED until liveness stops being decided by erased area alone. Its value today
is the number, and the anti-regression it gives whoever fixes it -- exactly how
crtc_sweep measured 102/375 before the CRTC table port closed it.

Everything expected is decoded from GHOST.COM itself (step count, first position,
stride, sprite geometry), so the judge cannot drift from the binary.

Usage: python tools/blit_test/judge_ghost.py [export_dir]  (default export/ghost)
"""
import json, os, shutil, subprocess, sys, time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
COM = os.path.join(REPO, 'tools', 'blit_test', 'GHOST.COM')
EXP = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, 'export', 'ghost')
GAME = os.path.join(REPO, 'games', 'ghost')

# File offsets of the operands read below (org 100h; listing in gen_ghost.py), each
# asserted against its opcode so a regenerated .COM fails loudly instead of silently
# judging the wrong bytes.
OFF_DI = 0x0B      # BF xx xx  mov di,DST0
OFF_BP = 0x0E      # BD xx xx  mov bp,STEPS
OFF_SI = 0x12      # BE xx xx  mov si,SPRITE
OFF_ROWS = 0x15    # BA xx xx  mov dx,rows
OFF_WORDS = 0x18   # B9 xx xx  mov cx,words per row
OFF_ADV = 0x33     # 83 C7 xx  add di,advance


def fnv1a(data):
    h = 2166136261
    for b in data:
        h = ((h ^ b) * 16777619) & 0xFFFFFFFF
    return h


def word(com, off):
    return int.from_bytes(com[off + 1:off + 3], 'little')


com = open(COM, 'rb').read()
for off, opcode, what in ((OFF_DI, 0xBF, 'mov di,DST0'), (OFF_BP, 0xBD, 'mov bp,STEPS'),
                          (OFF_SI, 0xBE, 'mov si,SPRITE'), (OFF_ROWS, 0xBA, 'mov dx,rows'),
                          (OFF_WORDS, 0xB9, 'mov cx,words'), (OFF_ADV, 0x83, 'add di,adv')):
    if com[off] != opcode:
        raise SystemExit(f'ROJO: GHOST.COM cambio - esperaba {what} (0x{opcode:02X}) en '
                         f'0x{off:02X}, hay 0x{com[off]:02X}')

dst0, steps = word(com, OFF_DI), word(com, OFF_BP)
sprite_lin, rows, row_bytes = word(com, OFF_SI), word(com, OFF_ROWS), word(com, OFF_WORDS) * 2
advance = com[OFF_ADV + 2]
row1 = com[sprite_lin - 0x100:sprite_lin - 0x100 + row_bytes]
fnv = fnv1a(row1)

x0, y0 = dst0 % 320, dst0 // 320            # mode 13h: linear 320 bytes per line
last = (x0 + (steps - 1) * advance, y0)
print(f'GHOST.COM: {steps} pasos, sprite {row_bytes}x{rows} desde ({x0},{y0}), '
      f'avance {advance} px, fila 1 fnv={fnv:08X}')
print(f'  el "cursor" acaba en {last}; instancias VIVAS correctas: 1')

os.makedirs(os.path.join(GAME, 'hd'), exist_ok=True)
shutil.copyfile(os.path.join(REPO, 'games', 'blit', 'hd', 'test.png'),
                os.path.join(GAME, 'hd', 'test.png'))
manifest = os.path.join(GAME, 'remake.json')
with open(manifest, 'w', encoding='utf-8') as f:
    json.dump({'assets': {f'{fnv:08X}': {'tex': 'hd/test.png', 'w': row_bytes, 'h': rows}}},
              f, indent=1)

cov_path = os.path.join(EXP, 'logs', 'remake-coverage.log')
try:
    os.remove(cov_path)
except OSError:
    pass

env = dict(os.environ, PAKU_REMAKE=manifest, PAKU_NOSPLASH='1', PAKU_AUTOEXIT_MS='6000')
r = subprocess.run(['dotnet', 'run', '-c', 'Release'], cwd=EXP, env=env,
                   capture_output=True, text=True, timeout=300)
print([l for l in r.stdout.splitlines() if 'autoexit' in l] or r.stdout[-400:])

for _ in range(30):
    if os.path.exists(cov_path):
        break
    time.sleep(1)
else:
    raise SystemExit('ROJO: no se escribio remake-coverage.log')

cov = open(cov_path, encoding='utf-8').read()
head = {}
for line in cov.splitlines():
    for tok in line.split():
        if '=' in tok:
            k, v = tok.split('=', 1)
            head.setdefault(k, v)
vivas = [dict(t.split('=', 1) for t in line.split() if '=' in t)
         for line in cov.splitlines() if line.startswith('viva ')]
pos = sorted((int(v['x']), int(v['y'])) for v in vivas)
print(f'blits={head.get("blits")} reemplazados={head.get("reemplazados")} '
      f'vivas={len(vivas)} muertas={head.get("muertas")}')
print(f'  posiciones vivas: {pos}')

ok = True


def check(cond, msg):
    global ok
    print(('  OK  ' if cond else '  ROJO') + ' ' + msg)
    ok = ok and cond


check(int(head.get('reemplazados', 0)) == steps,
      f'un blit reemplazado por paso ({steps}): {head.get("reemplazados")} '
      '(si no, la ventana de burst se esta comiendo pasos)')
check(len(vivas) == 1,
      f'1 sola instancia viva (el cursor): {len(vivas)}'
      + (f'  <-- {len(vivas) - 1} FANTASMAS, brecha de liveness' if len(vivas) > 1 else ''))
if vivas:
    check(last in pos, f'la posicion final {last} esta viva: {last in pos}')
print('GATE ' + ('OK' if ok else 'ROJO'))
sys.exit(0 if ok else 1)
