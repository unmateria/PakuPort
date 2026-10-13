"""Anti-regression judge for the first HD-replacement proof of concept under the
frame (liveness) semantics.

BLIT.COM draws ONE 16-byte row and never touches it again. That is the pathological
case for liveness: an instance nothing ever redraws must NOT be reaped (a game's
static title screen behaves the same). Asserts the original numbers still hold:
  coverage 1/1 = 100%, 1 live instance, 0 dead, and red HD pixels on screen.

Usage: python judge_remake.py [export_dir]   (default: export/blit)
"""
import os, subprocess, sys, time
from PIL import Image

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXP = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, 'export', 'blit')

env = dict(os.environ,
           PAKU_REMAKE=os.path.join(REPO, 'games', 'blit', 'remake.json'),
           PAKU_NOSPLASH='1', PAKU_AUTOEXIT_MS='4000')
for stale in ('logs/remake-coverage.log', 'screenshot.png'):
    try:
        os.remove(os.path.join(EXP, stale))
    except OSError:
        pass

r = subprocess.run(['dotnet', 'run', '-c', 'Release'], cwd=EXP, env=env,
                   capture_output=True, text=True, timeout=300)
print([l for l in r.stdout.splitlines() if 'autoexit' in l] or r.stdout[-400:])

cov_path = os.path.join(EXP, 'logs', 'remake-coverage.log')
for _ in range(30):
    if os.path.exists(cov_path):
        break
    time.sleep(1)
else:
    raise SystemExit('ROJO: no se escribio remake-coverage.log')

cov = {}
for line in open(cov_path):
    for tok in line.split():
        if '=' in tok:
            k, v = tok.split('=', 1)
            cov.setdefault(k, v)

img = Image.open(os.path.join(EXP, 'screenshot.png')).convert('RGB')
red = sum(1 for r_, g_, b_ in img.getdata() if r_ > 180 and g_ < 70 and b_ < 70)

rep, vivas, muertas = int(cov.get('reemplazados', 0)), int(cov.get('instancias_vivas', -1)), int(cov.get('muertas', -1))
print(f'reemplazados={rep} cobertura={cov.get("cobertura")} vivas={vivas} muertas={muertas} rojos={red}')

ok = True
def check(cond, msg):
    global ok
    print(('  OK  ' if cond else '  ROJO') + ' ' + msg)
    ok = ok and cond

check(rep == 1 and cov.get('cobertura') in ('100,0%', '100.0%'),
      f'cobertura 1/1 = 100%: rep={rep} cov={cov.get("cobertura")}')
check(vivas == 1, f'la instancia one-shot SIGUE VIVA (no la siega la semantica de frame): {vivas}')
check(muertas == 0, f'ninguna muerta: {muertas}')
check(red > 400, f'pixeles rojos HD medidos en pantalla: {red}')
print('GATE ' + ('OK' if ok else 'ROJO'))
sys.exit(0 if ok else 1)
