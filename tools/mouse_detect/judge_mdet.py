"""Juez de la sonda de deteccion de raton: lee MDET.BIN y dictamina.

Que vigila: que un juego que se detecte el raton **por el vector** (la comprobacion canonica
de Ralf Brown, y la que hace literalmente un juego de prueba) encuentre un driver. Son tres condiciones
y las tres tienen que darse; la del medio es la que fallaba:

  1. el vector de INT 33h no es nulo,
  2. el primer byte al que apunta NO es 0xCF  <-- un IRET pelado = "no hay driver detras",
  3. el reset del driver (AX=0000) responde AX=FFFF con 2 o 3 botones.

Uso:  python tools\\mouse_detect\\judge_mdet.py [ruta\\a\\MDET.BIN]
Sale con codigo 1 si algo esta en ROJO, para encadenarlo en un gate.
"""
import os, struct, sys

D = os.path.dirname(os.path.abspath(__file__))
ruta = sys.argv[1] if len(sys.argv) > 1 else os.path.join(D, 'MDET.BIN')
if not os.path.exists(ruta):
    raise SystemExit(f'ROJO: falta {ruta} (ver README.md para regenerarlo)')

datos = open(ruta, 'rb').read()
if len(datos) != 9:
    raise SystemExit(f'ROJO: {ruta} mide {len(datos)} B, se esperaban 9')

seg, off = struct.unpack_from('<HH', datos, 0)
primer = datos[4]
ax, bx = struct.unpack_from('<HH', datos, 5)

casos = [
    ('vector no nulo',            (seg, off) != (0, 0),   f'vector={seg:04X}:{off:04X}'),
    ('primer byte != CF (IRET)',  primer != 0xCF,         f'byte={primer:02X}'),
    ('reset AX=FFFF, 2-3 botones', ax == 0xFFFF and bx in (2, 3), f'AX={ax:04X} BX={bx:04X}'),
]

verde = 0
for nombre, ok, detalle in casos:
    print(f'{nombre:28} {detalle:24} -> {"VERDE" if ok else "ROJO"}')
    verde += 1 if ok else 0

print(f'RESUMEN VERDE={verde} ROJO={len(casos) - verde}')
sys.exit(0 if verde == len(casos) else 1)
