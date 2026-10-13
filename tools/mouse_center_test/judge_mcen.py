"""Juez del centrado del reset del raton: compara MCEN.BIN contra la verdad terreno del oraculo.

Que vigila: que un juego que AUTODETECTE en que unidades habla el driver leyendo la posicion
del cursor justo despues del reset (como hace un juego de prueba) llegue a la MISMA
conclusion en PakuPort que en DOSBox. Son dos condiciones:

  1. el driver declara el mismo rango virtual que el oraculo (AX=0026),
  2. tras el reset el cursor esta CENTRADO en ese rango: (max+1)/2, byte-exacto al oraculo
     -- DOSBox-X src/ints/mouse.cpp, Mouse_Reset.

Si el reset deja el cursor en 0,0, ese juego lee 0, deduce "el driver habla virtual-640" y a
partir de ahi divide entre dos todo lo que le llega: el cursor solo alcanza la mitad de la
pantalla ("no llega al lado derecho", segun una prueba manual).

Uso:  python tools\\mouse_center_test\\judge_mcen.py [ruta\\a\\MCEN.BIN]
Sale con codigo 1 si algo esta en ROJO, para encadenarlo en un gate.
"""
import os, struct, sys

D = os.path.dirname(os.path.abspath(__file__))
ruta = sys.argv[1] if len(sys.argv) > 1 else os.path.join(D, 'MCEN.BIN')
oraculo = os.path.join(D, 'MCEN.ORACULO.BIN')

if not os.path.exists(ruta):
    raise SystemExit(f'ROJO: falta {ruta} (ver README.md para regenerarlo)')
if not os.path.exists(oraculo):
    raise SystemExit(f'ROJO: falta la verdad terreno {oraculo} (ver README.md)')


def leer(p):
    d = open(p, 'rb').read()
    if len(d) != 10:
        raise SystemExit(f'ROJO: {p} mide {len(d)} B, se esperaban 10')
    max_x, max_y, x, y, ax = struct.unpack('<HHHHH', d)
    return max_x, max_y, x, y, ax


o_max_x, o_max_y, o_x, o_y, o_ax = leer(oraculo)
max_x, max_y, x, y, ax = leer(ruta)

print(f'oraculo : rango=(0,0)-({o_max_x},{o_max_y})  pos tras reset={o_x},{o_y}  AX={o_ax:04X}')
print(f'PakuPort: rango=(0,0)-({max_x},{max_y})  pos tras reset={x},{y}  AX={ax:04X}')
print()

casos = [
    ('driver instalado (control)', ax == 0xFFFF,          f'AX={ax:04X}'),
    ('rango virtual = oraculo',    (max_x, max_y) == (o_max_x, o_max_y),
                                                          f'({max_x},{max_y}) vs ({o_max_x},{o_max_y})'),
    ('X centrada = (max_x+1)/2',   x == (max_x + 1) // 2, f'x={x} esperado={(max_x + 1) // 2}'),
    ('Y centrada = (max_y+1)/2',   y == (max_y + 1) // 2, f'y={y} esperado={(max_y + 1) // 2}'),
    ('posicion byte-exacta al oraculo', (x, y) == (o_x, o_y), f'({x},{y}) vs ({o_x},{o_y})'),
]

verde = 0
for nombre, ok, detalle in casos:
    print(f'{nombre:34} {detalle:28} -> {"VERDE" if ok else "ROJO"}')
    verde += 1 if ok else 0

print(f'RESUMEN VERDE={verde} ROJO={len(casos) - verde}')
sys.exit(0 if verde == len(casos) else 1)
