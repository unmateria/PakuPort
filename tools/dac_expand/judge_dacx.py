"""Juez de la sonda de expansion del DAC: muestrea las 64 bandas de DACX.COM en el PNG de cada
mundo y compara el color que sale por pantalla.

Verdad terreno DOBLE, y las dos tienen que darse:

  1. la formula de dosbox-x src/hardware/vga_dac.cpp `dacexpand()` para el DAC de 6 bits:
         (v << 2) | (v >> 4)      // abcdef -> abcdefab, equivalente a (v*255)/63
  2. el PNG del ORACULO, si se le pasa: por si la formula del codigo no fuera lo que la
     maquina hace de verdad.

`v << 2` a secas (lo que hacia PakuPort) coincide con la formula solo en los valores donde los
dos bits altos son cero, asi que el error crece con el brillo: en 63 da 252 en vez de 255.

Uso:  python tools\\dac_expand\\judge_dacx.py <paku.png> [oraculo.png]
Sale con codigo 1 si algo esta en ROJO, para encadenarlo en un gate.
"""
import sys
from PIL import Image


def muestrear(ruta):
    """Devuelve el color del centro de cada una de las 64 bandas verticales."""
    im = Image.open(ruta).convert('RGB')
    w, h = im.size
    ancho = w / 64.0
    return [im.getpixel((int(i * ancho + ancho / 2), h // 2)) for i in range(64)]


if len(sys.argv) < 2:
    raise SystemExit('uso: judge_dacx.py <paku.png> [oraculo.png]')

paku = muestrear(sys.argv[1])
oraculo = muestrear(sys.argv[2]) if len(sys.argv) > 2 else None

esperado = [((v << 2) | (v >> 4)) for v in range(64)]

malos_formula, malos_oraculo, malos_gris = [], [], []
for i in range(64):
    r, g, b = paku[i]
    if r != esperado[i]:
        malos_formula.append((i, r, esperado[i]))
    if not (r == g == b):                       # la rampa es gris: los tres iguales
        malos_gris.append((i, paku[i]))
    if oraculo and paku[i] != oraculo[i]:
        malos_oraculo.append((i, paku[i], oraculo[i]))

print('banda  v   PakuPort      esperado' + ('      oraculo' if oraculo else ''))
for i in (0, 1, 16, 31, 42, 62, 63):
    linea = f'  {i:3}  {i:2}  {str(paku[i]):14} {esperado[i]:3}'
    if oraculo:
        linea += f'      {str(oraculo[i])}'
    print(linea)
print()

casos = [
    ('rampa gris (r=g=b)',      not malos_gris,     f'{len(malos_gris)} bandas mal'),
    ('formula (v<<2)|(v>>4)',   not malos_formula,  f'{len(malos_formula)}/64 bandas distintas'),
    ('blanco llega a 255',      paku[63][0] == 255, f'banda 63 = {paku[63][0]}'),
]
if oraculo:
    casos.append(('identico al oraculo', not malos_oraculo, f'{len(malos_oraculo)}/64 bandas distintas'))

verde = 0
for nombre, ok, detalle in casos:
    print(f'{nombre:26} {detalle:28} -> {"VERDE" if ok else "ROJO"}')
    verde += 1 if ok else 0

if malos_formula[:5]:
    print('\nprimeras diferencias contra la formula (banda, PakuPort, esperado):')
    for i, got, exp in malos_formula[:5]:
        print(f'   banda {i:2}: {got} vs {exp}')

print(f'RESUMEN VERDE={verde} ROJO={len(casos) - verde}')
sys.exit(0 if verde == len(casos) else 1)
