# -*- coding: utf-8 -*-
"""JUEZ del decodificador EGA planar (programa de prueba sintetico EGA.COM).

Cadena entera y reproducible:
  captura headless de export/ega (PAKU_INTERP_ONLY=1 PAKU_LOG_BLIT=2)
    -> build_catalog.py
    -> assets.json + PNG
    -> comparacion PIXEL A PIXEL contra el patron esperado

El patron esperado NO esta escrito a mano: se calcula de las tablas de datos del propio
EGA.COM (offsets 0x56 y 0x7E, ver tools/blit_test/EGA.README.md) aplicando la semantica
documentada del modo 0Dh: write mode 0 + map mask de un solo plano => el byte de la CPU
cae tal cual en ese plano; indice de color = p0 | p1<<1 | p2<<2 | p3<<3 con el bit 7 como
pixel mas a la izquierda.

Uso: python tools/blit_catalog/judge_ega.py [export_dir]
Exit 0 = GATE OK, 1 = GATE ROJO.
"""
import json, os, shutil, subprocess, sys

from PIL import Image

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
EXPORT = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, 'export', 'ega')
COM = os.path.join(REPO, 'tools', 'blit_test', 'EGA.COM')
CATALOG = os.path.join(REPO, 'games', 'ega', 'catalog')

BYTES_PER_LINE = 40                      # mode 0Dh: 320 px / 8
SHAPES = [                               # (offset de la tabla en el .COM, filas, bytes/fila, di)
    ('asset de mov es:[di],al', 0x56, 5, 2, 0x00F3),
    ('asset de rep movsb',      0x7E, 1, 4, 0x032A),
]

# Paleta EGA de 16 colores en unidades DAC de 6 bits, tal como la carga
# VideoState.LoadDefaultVgaPalette; se escala a 8 bits con v*255/63. Duplicada a proposito
# (no se importa de build_catalog.py) para que el juez no valide el decodificador consigo mismo.
EGA6 = [(0x00, 0x00, 0x00), (0x00, 0x00, 0x2A), (0x00, 0x2A, 0x00), (0x00, 0x2A, 0x2A),
        (0x2A, 0x00, 0x00), (0x2A, 0x00, 0x2A), (0x2A, 0x15, 0x00), (0x2A, 0x2A, 0x2A),
        (0x15, 0x15, 0x15), (0x15, 0x15, 0x3F), (0x15, 0x3F, 0x15), (0x15, 0x3F, 0x3F),
        (0x3F, 0x15, 0x15), (0x3F, 0x15, 0x3F), (0x3F, 0x3F, 0x15), (0x3F, 0x3F, 0x3F)]
EGA_RGB = [tuple(c * 255 // 63 for c in e) for e in EGA6]

ok = True


def check(cond, msg):
    global ok
    print(('  OK   ' if cond else '  ROJO ') + msg)
    ok = ok and bool(cond)


def expected_grid(com, off, rows, cols):
    """Tablas plano-mayor del .COM -> rejilla de indices de color."""
    grid = []
    for r in range(rows):
        row = []
        for c in range(cols):
            planes = [com[off + p * rows * cols + r * cols + c] for p in range(4)]
            for b in range(7, -1, -1):
                row.append(sum(((planes[k] >> b) & 1) << k for k in range(4)))
        grid.append(row)
    return grid


def capture():
    logs = os.path.join(EXPORT, 'logs')
    shutil.rmtree(logs, ignore_errors=True)
    env = dict(os.environ, PAKU_INTERP_ONLY='1', PAKU_LOG_BLIT='2',
               PAKU_NOSPLASH='1', PAKU_AUTOEXIT_MS='3000')
    r = subprocess.run(['dotnet', 'run', '-c', 'Release'], cwd=EXPORT, env=env,
                       capture_output=True, text=True, timeout=600)
    line = [l for l in r.stdout.splitlines() if 'autoexit' in l]
    print('  ' + (line[0] if line else r.stdout[-300:]))
    return logs


def main():
    if not os.path.isdir(EXPORT):
        print(f'ROJO: no existe {EXPORT} (crealo con: pakuport port tools/blit_test/EGA.COM)')
        return 1
    com = open(COM, 'rb').read()
    print(f'EGA.COM: {len(com)} bytes')

    print('captura headless...')
    logs = capture()

    shutil.rmtree(CATALOG, ignore_errors=True)
    print('catalogo...')
    r = subprocess.run([sys.executable, os.path.join(REPO, 'tools', 'blit_catalog', 'build_catalog.py'),
                        '--logs', logs, '--out', CATALOG], capture_output=True, text=True)
    print('  ' + r.stdout.strip().replace('\n', '\n  '))
    if r.returncode != 0:
        print(r.stderr)
        return 1

    cat = json.load(open(os.path.join(CATALOG, 'assets.json'), encoding='utf-8'))
    assets = cat['assets']
    check(not cat['skipped_writes_by_mode'],
          f'ninguna escritura saltada: {cat["skipped_writes_by_mode"]}')
    check(len(assets) == 2, f'2 assets en el catalogo: {len(assets)}')
    instances = [json.loads(l) for l in open(os.path.join(CATALOG, 'instances.jsonl'))]

    for name, off, rows, cols, di in SHAPES:
        grid = expected_grid(com, off, rows, cols)
        w, h = cols * 8, rows
        x, y = (di % BYTES_PER_LINE) * 8, di // BYTES_PER_LINE
        want = [''.join('.' if v == 0 else format(v, 'x') for v in row) for row in grid]
        print(f'{name}: esperado {w}x{h} en ({x},{y})')
        for row in want:
            print('    ' + row)

        cands = [a for a in assets if a['w'] == w and a['h'] == h]
        if len(cands) != 1:
            check(False, f'{name}: 1 asset {w}x{h} en el catalogo (hay {len(cands)})')
            continue
        a = cands[0]
        check(a['mode'] == 0x0D, f'{name}: mode=0Dh en el asset ({a["mode"]:#04x})')
        check(a['pixels'] == want, f'{name}: mascara de pixeles byte-exacta')

        ins = [i for i in instances if i['asset'] == a['id']]
        check(len(ins) == 1 and ins[0]['x'] == x and ins[0]['y'] == y,
              f'{name}: instancia en ({x},{y}) -> {[(i["x"], i["y"]) for i in ins]}')

        img = Image.open(os.path.join(CATALOG, a['png'])).convert('RGB')
        bad = None
        if img.size != (w, h):
            bad = f'tamano PNG {img.size} != ({w},{h})'
        else:
            for py in range(h):
                for px in range(w):
                    got, exp = img.getpixel((px, py)), EGA_RGB[grid[py][px]]
                    if got != exp and bad is None:
                        bad = f'({px},{py}) {got} != {exp} (indice {grid[py][px]})'
        check(bad is None, f'{name}: PNG {w}x{h} pixel a pixel en paleta EGA'
                           + ('' if bad is None else f' — {bad}'))

    print('GATE ' + ('OK' if ok else 'ROJO'))
    return 0 if ok else 1


if __name__ == '__main__':
    sys.exit(main())
