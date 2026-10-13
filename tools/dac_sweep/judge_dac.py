"""Juez del barrido del DAC: PakuPort vs oraculo DOSBox-X, byte a byte.

Compara DAC.PAKU.BIN contra DAC.ORACULO.BIN (verdad terreno capturada del oraculo, ver
README.md) por modo y por entrada. Reporta la brecha como porcentaje de bytes distintos
y, en modo -v, las primeras entradas que difieren con sus dos ternas RGB.

Las fuentes de DOSBox-X NO estan en disco en esta maquina, asi que la referencia es el
COMPORTAMIENTO MEDIDO del oraculo, no un listado citado de memoria.

Uso: python tools/dac_sweep/judge_dac.py [-v] [--baseline]
"""
import json, os, sys

D = os.path.dirname(os.path.abspath(__file__))
verbose = '-v' in sys.argv
set_baseline = '--baseline' in sys.argv

meta = json.load(open(os.path.join(D, 'modes.json')))
oracle = open(os.path.join(D, 'DAC.ORACULO.BIN'), 'rb').read()
paku = open(os.path.join(D, 'DAC.PAKU.BIN'), 'rb').read()

total = meta['total']
for name, data in (('DAC.ORACULO.BIN', oracle), ('DAC.PAKU.BIN', paku)):
    if len(data) != total:
        raise SystemExit(f'ROJO: {name} mide {len(data)} B, se esperaban {total}')

REC = meta['record_bytes']
rows, bad_total = [], 0
for m in meta['modes']:
    off, mode = m['off'], m['mode']
    o, p = oracle[off:off + REC], paku[off:off + REC]
    bad = [i for i in range(REC) if o[i] != p[i]]
    bad_entries = sorted({i // 3 for i in bad})
    bad_total += len(bad)
    rows.append((mode, len(bad), bad_entries))
    print(f'modo {mode:02X}h: {REC - len(bad)}/{REC} bytes iguales '
          f'({100.0 * len(bad) / REC:5.1f}% de brecha), '
          f'{len(bad_entries)} entradas de {meta["entries"]} distintas')
    if verbose and bad_entries:
        for e in bad_entries[:8]:
            print(f'    entrada {e:3d}: oraculo {tuple(o[e*3:e*3+3])}  '
                  f'paku {tuple(p[e*3:e*3+3])}')
        if len(bad_entries) > 8:
            print(f'    ... y {len(bad_entries) - 8} entradas mas')

gap = 100.0 * bad_total / total
print(f'\nTOTAL: {total - bad_total}/{total} bytes idapenticos, brecha {gap:.1f}%'
      .replace('idapenticos', 'identicos'))

base_path = os.path.join(D, 'baseline.json')
if set_baseline:
    json.dump({'bad_bytes': bad_total, 'total': total,
               'per_mode': {f'{m:02X}': n for m, n, _ in rows}},
              open(base_path, 'w'), indent=1)
    print(f'baseline congelado en {base_path}')
    sys.exit(0)

# Modos CERRADOS: deben estar a cero, y quedarse a cero. 13h es la ruta de 256 colores
# que se porto desde la tabla medida del oraculo; 0Dh ya coincidia de serie.
# Los demas (03h, 12h) exponen la OTRA brecha, la de la paleta EGA de 64 entradas, que
# sigue ABIERTA a proposito: nuestra tabla de 16 y los AttrRegs 8-15 -> 0x10-0x17 de
# Int10hHandler se eligieron JUNTOS, asi que corregir la tabla sin revisar el mapeo
# atributo->DAC (P54S / color select) cambiaria el texto de todos los juegos probados. Se vigila
# contra baseline para que no empeore.
STRICT_MODES = {0x0D, 0x13}
per_mode = {m: n for m, n, _ in rows}

ok = True
for m in sorted(STRICT_MODES):
    bad = per_mode.get(m, 0)
    print(('  OK  ' if bad == 0 else '  ROJO') + f' modo {m:02X}h byte-exacto: {bad} bytes malos')
    ok = ok and bad == 0

if os.path.exists(base_path):
    base = json.load(open(base_path))
    for m, n, _ in rows:
        if m in STRICT_MODES:
            continue
        prev = base['per_mode'].get(f'{m:02X}', 0)
        worse = n > prev
        print(('  ROJO' if worse else '  OK  ') +
              f' modo {m:02X}h (brecha EGA-64 conocida, ABIERTA) no empeora: '
              f'{prev} -> {n}')
        ok = ok and not worse
else:
    print('  AVISO: sin baseline; congelar con --baseline')

print('GATE ' + ('OK' if ok else 'ROJO'))
sys.exit(0 if ok else 1)
