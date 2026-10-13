#!/usr/bin/env python3
"""Emite la tabla C# de la paleta VGA de 256 colores A PARTIR DE LA MEDIDA DEL ORACULO.

Las fuentes de DOSBox-X no estan en disco en esta maquina, asi que la referencia es el
DAC real capturado con DAC.COM en DOSBox-X (`DAC.ORACULO.BIN`, bloque del modo 13h).
Reproducir "de memoria" el algoritmo HSV de la BIOS seria inventar; la tabla medida es
verdad terreno y el juez la verifica byte a byte.

Uso: python tools/dac_sweep/gen_palette_cs.py > <fragmento>   (o --check)
"""
import json, os, sys

D = os.path.dirname(os.path.abspath(__file__))
meta = json.load(open(os.path.join(D, 'modes.json')))
oracle = open(os.path.join(D, 'DAC.ORACULO.BIN'), 'rb').read()

off = next(m['off'] for m in meta['modes'] if m['mode'] == 0x13)
pal = oracle[off:off + meta['record_bytes']]

if '--check' in sys.argv:
    # Estructura esperada, para que un cambio del barrido no cuele una tabla absurda.
    assert pal[:3] == b'\x00\x00\x00', 'la entrada 0 deberia ser negra'
    assert tuple(pal[15 * 3:15 * 3 + 3]) == (63, 63, 63), 'la 15 deberia ser blanco'
    assert tuple(pal[31 * 3:31 * 3 + 3]) == (63, 63, 63), 'la 31 cierra la rampa de grises'
    assert all(b == 0 for b in pal[248 * 3:]), 'las 248..255 deberian ser negras'
    assert max(pal) <= 63, 'el DAC es de 6 bits'
    print(f'OK: {len(pal)} bytes, estructura 16 EGA + 16 grises + 216 HSV + 8 negras')
    sys.exit(0)

print('    // Paleta VGA de 256 colores por defecto: 16 EGA + 16 grises (rampa no lineal)')
print('    // + 216 HSV + 8 negras. MEDIDA del oraculo (DOSBox-X svga_s3) con')
print('    // tools/dac_sweep/DAC.COM, no copiada de memoria; regenerable con')
print('    // `python tools/dac_sweep/gen_palette_cs.py` y verificada byte a byte por')
print('    // tools/dac_sweep/judge_dac.py. Valores en unidades de 6 bits del DAC.')
print('    private static readonly byte[] Vga256Palette =')
print('    {')
for i in range(0, 768, 24):
    row = ', '.join(f'0x{b:02X}' for b in pal[i:i + 24])
    print(f'        {row},')
print('    };')
