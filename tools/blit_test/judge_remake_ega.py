"""Judge for RemakeMap.GameXY in EGA planar modes (HD replacement).

EGA.COM's second figure is a `rep movsb` of CX bytes to ES:DI in mode 0Dh (planar,
40 bytes per line). The HD instance it registers must land on the game pixel that
address means in a PLANAR mode -- (DI % bpl) * 8, DI / bpl -- not on the linear
320-byte stride RemakeMap fell back to before this judge existed.

Everything expected is decoded from EGA.COM itself (mode byte, DI, CX and the source
table), so the judge cannot drift from the .COM the way a hardcoded pair would.

Usage: python tools/blit_test/judge_remake_ega.py [export_dir]  (default export/ega)
"""
import json, os, shutil, subprocess, sys, time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
COM = os.path.join(REPO, 'tools', 'blit_test', 'EGA.COM')
EXP = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, 'export', 'ega')
GAME = os.path.join(REPO, 'games', 'ega')

# Bytes per line of the EGA/VGA planar modes, = Width / 8 (same table as
# tools/blit_catalog/build_catalog.py, itself derived from Int10hHandler's mode table).
EGA_BPL = {0x0D: 40, 0x0E: 80, 0x0F: 80, 0x10: 80, 0x11: 80, 0x12: 80}

# Offsets INTO THE FILE of the operands the judge reads (org 100h; see EGA.README.md
# for the full listing). Asserted against their opcodes below so a rewrite of the
# .COM fails loudly here instead of silently judging the wrong bytes.
OFF_MODE_AX = 0x01   # B8 0D 00   mov ax,000Dh
OFF_FIG2_DI = 0x45   # BF 2A 03   mov di,032Ah
OFF_FIG2_CX = 0x48   # B9 04 00   mov cx,4
OFF_TABLE2 = 0x7E    # TABLA2, plane 0 first


def fnv1a(data):
    h = 2166136261
    for b in data:
        h = ((h ^ b) * 16777619) & 0xFFFFFFFF
    return h


com = open(COM, 'rb').read()
for off, opcode, what in ((OFF_MODE_AX, 0xB8, 'mov ax,mode'),
                          (OFF_FIG2_DI, 0xBF, 'mov di,dst'),
                          (OFF_FIG2_CX, 0xB9, 'mov cx,len')):
    if com[off] != opcode:
        raise SystemExit(f'ROJO: EGA.COM cambio - esperaba {what} (0x{opcode:02X}) '
                         f'en 0x{off:02X}, hay 0x{com[off]:02X}')

mode = com[OFF_MODE_AX + 1]
di = int.from_bytes(com[OFF_FIG2_DI + 1:OFF_FIG2_DI + 3], 'little')
cx = int.from_bytes(com[OFF_FIG2_CX + 1:OFF_FIG2_CX + 3], 'little')
src = com[OFF_TABLE2:OFF_TABLE2 + cx]
fnv = fnv1a(src)

bpl = EGA_BPL[mode]
exp_x, exp_y = (di % bpl) * 8, di // bpl
bug_x, bug_y = di % 320, di // 320   # the linear-stride fallback this judge exists to catch

print(f'EGA.COM: modo {mode:02X}h, dst DI={di} ({di:#06x}), {cx} bytes, '
      f'origen {src.hex()} -> fnv={fnv:08X}')
print(f'  esperado (planar, {bpl} bytes/linea): x={exp_x} y={exp_y}')
print(f'  fallback lineal-320 (el fallo):       x={bug_x} y={bug_y}')

os.makedirs(os.path.join(GAME, 'hd'), exist_ok=True)
shutil.copyfile(os.path.join(REPO, 'games', 'blit', 'hd', 'test.png'),
                os.path.join(GAME, 'hd', 'test.png'))
manifest = os.path.join(GAME, 'remake.json')
with open(manifest, 'w', encoding='utf-8') as f:
    json.dump({'assets': {f'{fnv:08X}': {'tex': 'hd/test.png', 'w': cx * 8, 'h': 1}}},
              f, indent=1)

cov_path = os.path.join(EXP, 'logs', 'remake-coverage.log')
for stale in (cov_path, os.path.join(EXP, 'screenshot.png')):
    try:
        os.remove(stale)
    except OSError:
        pass

env = dict(os.environ, PAKU_REMAKE=manifest, PAKU_NOSPLASH='1', PAKU_AUTOEXIT_MS='4000')
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
print(cov.strip())
vivas = [dict(t.split('=', 1) for t in line.split() if '=' in t)
         for line in cov.splitlines() if line.startswith('viva ')]

ok = True


def check(cond, msg):
    global ok
    print(('  OK  ' if cond else '  ROJO') + ' ' + msg)
    ok = ok and cond


check(len(vivas) == 1, f'exactamente 1 instancia viva: {len(vivas)}')
if vivas:
    gx, gy = int(vivas[0]['x']), int(vivas[0]['y'])
    check((gx, gy) == (exp_x, exp_y),
          f'instancia en el pixel PLANAR ({exp_x},{exp_y}): medido ({gx},{gy})'
          + (f'  <-- es el fallback lineal-320' if (gx, gy) == (bug_x, bug_y) else ''))
print('GATE ' + ('OK' if ok else 'ROJO'))
sys.exit(0 if ok else 1)
