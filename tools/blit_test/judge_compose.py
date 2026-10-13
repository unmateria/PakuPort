"""Juez de la IDENTIDAD POR PROCEDENCIA (fase 5): el sprite COMPUESTO sigue identificado.

COMPOSE.COM lee DATA.BIN, lo pasa por un buffer intermedio, le hace XOR 0FFh a los 4
primeros bytes (la "composición con el fondo") y lo vuelca a VRAM. Por tanto:

  - el contenido que llega a VRAM YA NO es el del fichero  -> su FNV es OTRO
  - la procedencia sigue siendo DATA.BIN@0x0

El juez corre DOS veces con manifiestos distintos y comprueba las dos caras:

  A) manifiesto por PROCEDENCIA ("file:DATA.BIN@0x0")  -> DEBE reemplazar (1 instancia)
  B) manifiesto por HASH del contenido original        -> NO debe reemplazar (0)

Sin (B) el test no probaría nada: podría estar casando por hash sin que la procedencia
haga falta. (B) es el control que demuestra que el hash de píxeles ya no sirve para un
sprite compuesto, que es justo el problema medido en el cursor de un juego de modo real.

Uso: python tools/blit_test/judge_compose.py [export_dir]  (default export/compose)
"""
import json, os, shutil, subprocess, sys, time

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXP = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, 'export', 'compose')
GAME = os.path.join(REPO, 'games', 'compose')
DATA = open(os.path.join(REPO, 'tools', 'blit_test', 'DATA.BIN'), 'rb').read()
COM = open(os.path.join(REPO, 'tools', 'blit_test', 'COMPOSE.COM'), 'rb').read()

# NCOMPOSE se lee del propio .COM (el `mov cx,N` que precede al bucle de XOR en 0x129),
# con aserción de opcode, para que el juez no se desincronice del binario.
OFF_NCOMPOSE = 0x29
if COM[OFF_NCOMPOSE] != 0xB9:
    raise SystemExit(f'ROJO: COMPOSE.COM cambio - esperaba mov cx,N (0xB9) en 0x{OFF_NCOMPOSE:02X}')
ncompose = int.from_bytes(COM[OFF_NCOMPOSE + 1:OFF_NCOMPOSE + 3], 'little')


def fnv1a(data):
    h = 2166136261
    for b in data:
        h = ((h ^ b) * 16777619) & 0xFFFFFFFF
    return h


composed = bytes(b ^ 0xFF if i < ncompose else b for i, b in enumerate(DATA))
fnv_orig, fnv_comp = fnv1a(DATA), fnv1a(composed)
print(f'DATA.BIN            fnv={fnv_orig:08X}')
print(f'tras componer ({ncompose} B) fnv={fnv_comp:08X}   <- lo que llega a VRAM')
if fnv_orig == fnv_comp:
    raise SystemExit('ROJO: la composicion no cambia el hash; el test no probaria nada')

os.makedirs(os.path.join(GAME, 'hd'), exist_ok=True)
shutil.copyfile(os.path.join(REPO, 'games', 'blit', 'hd', 'test.png'),
                os.path.join(GAME, 'hd', 'test.png'))


def run(tag, assets, expect_replaced):
    manifest = os.path.join(GAME, f'remake-{tag}.json')
    with open(manifest, 'w', encoding='utf-8') as f:
        json.dump({'assets': assets}, f, indent=1)
    cov_path = os.path.join(EXP, 'logs', 'remake-coverage.log')
    try:
        os.remove(cov_path)
    except OSError:
        pass
    env = dict(os.environ, PAKU_REMAKE=manifest, PAKU_NOSPLASH='1', PAKU_AUTOEXIT_MS='4000')
    subprocess.run(['dotnet', 'run', '-c', 'Release'], cwd=EXP, env=env,
                   capture_output=True, text=True, timeout=300)
    for _ in range(30):
        if os.path.exists(cov_path):
            break
        time.sleep(1)
    else:
        raise SystemExit(f'ROJO [{tag}]: no se escribio remake-coverage.log')
    cov = open(cov_path, encoding='utf-8').read()
    head = {}
    for line in cov.splitlines():
        for tok in line.split():
            if '=' in tok:
                k, v = tok.split('=', 1)
                head.setdefault(k, v)
    rep = int(head.get('reemplazados', -1))
    vivas = sum(1 for l in cov.splitlines() if l.startswith('viva '))
    ok = rep == expect_replaced
    print(('  OK  ' if ok else '  ROJO') +
          f' [{tag}] reemplazados={rep} (esperado {expect_replaced}), vivas={vivas}')
    return ok


print('--- A) identidad por PROCEDENCIA: debe reemplazar el sprite compuesto ---')
a = run('prov', {'file:DATA.BIN@0x0': {'tex': 'hd/test.png', 'w': 16, 'h': 1}}, 1)
print('--- B) CONTROL: por hash del contenido ORIGINAL ya no casa (el sprite cambio) ---')
b = run('hash', {f'{fnv_orig:08X}': {'tex': 'hd/test.png', 'w': 16, 'h': 1}}, 0)

print('GATE ' + ('OK' if (a and b) else 'ROJO'))
sys.exit(0 if (a and b) else 1)
