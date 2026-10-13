"""Juez de la sonda de modificadores de teclado: lee MODS.BIN y dictamina.

Que vigila: que Shift, Ctrl y Alt lleguen al juego COMO TECLAS y muevan el estado del BIOS.
El arnes teclea `%a&` `*b_` `=c|` (Shift+A, Ctrl+B, Alt+C manteniendo el modificador), asi que
la verdad terreno son los scancodes set 1 de toda la vida y la BDA del PC/AT:

  1. llegan los MAKE de los tres modificadores: 2Ah (Shift izq), 1Dh (Ctrl), 38h (Alt);
  2. llegan sus BREAK: AAh, 9Dh, B8h — sin el break, la tecla se queda pulsada para siempre
     en cualquier juego que lleve tabla de teclas (la mitad del arreglo esta aqui);
  3. los flags de teclado de la BDA (0040:0017) tienen el bit del modificador PULSADO en el
     instante en que llega la letra: bit1 shift, bit2 ctrl, bit3 alt;
  4. el BIOS aplica el modificador al meter la tecla en su buffer, con la tabla de cuatro
     columnas de bios_keyboard.cpp: Shift+A -> 1E41h ('A'), Ctrl+B -> 3002h (caracter de
     control) y Alt+C -> 2E00h (el "alt scancode", sin ASCII).

Uso:  python tools\\kbd_mods\\judge_mods.py [ruta\\a\\MODS.BIN]
Sale con codigo 1 si algo esta en ROJO, para encadenarlo en un gate.
"""
import os, struct, sys

D = os.path.dirname(os.path.abspath(__file__))
ruta = sys.argv[1] if len(sys.argv) > 1 else os.path.join(D, 'MODS.BIN')
if not os.path.exists(ruta):
    raise SystemExit(f'ROJO: falta {ruta} (ver README.md para regenerarlo)')

datos = open(ruta, 'rb').read()
if len(datos) != 34:
    raise SystemExit(f'ROJO: {ruta} mide {len(datos)} B, se esperaban 34')

n = datos[0]
scans = list(datos[1:1 + min(n, 24)])
flags = list(datos[25:28])
bda = list(struct.unpack_from('<HHH', datos, 28))

print('scancodes:', ' '.join(f'{s:02X}' for s in scans) or '(ninguno)')
print('flags BDA:', ' '.join(f'{f:02X}' for f in flags))
print('buffer   :', ' '.join(f'{b:04X}' for b in bda))
print()

casos = [
    ('make Shift (2A)',   0x2A in scans,                 'presente' if 0x2A in scans else 'AUSENTE'),
    ('break Shift (AA)',  0xAA in scans,                 'presente' if 0xAA in scans else 'AUSENTE'),
    ('make Ctrl (1D)',    0x1D in scans,                 'presente' if 0x1D in scans else 'AUSENTE'),
    ('break Ctrl (9D)',   0x9D in scans,                 'presente' if 0x9D in scans else 'AUSENTE'),
    ('make Alt (38)',     0x38 in scans,                 'presente' if 0x38 in scans else 'AUSENTE'),
    ('break Alt (B8)',    0xB8 in scans,                 'presente' if 0xB8 in scans else 'AUSENTE'),
    ('flag shift en la A', len(flags) > 0 and (flags[0] & 0x03) != 0, f'40:17={flags[0]:02X}'),
    ('flag ctrl en la B',  len(flags) > 1 and (flags[1] & 0x04) != 0, f'40:17={flags[1]:02X}'),
    ('flag alt en la C',   len(flags) > 2 and (flags[2] & 0x08) != 0, f'40:17={flags[2]:02X}'),
    ('BIOS: Shift+A = A',  bda[0] == 0x1E41,             f'{bda[0]:04X}'),
    ('BIOS: Ctrl+B = ^B',  bda[1] == 0x3002,             f'{bda[1]:04X}'),
    ('BIOS: Alt+C = 2E00',  bda[2] == 0x2E00,            f'{bda[2]:04X}'),
]

verde = 0
for nombre, ok, detalle in casos:
    print(f'{nombre:22} {detalle:14} -> {"VERDE" if ok else "ROJO"}')
    verde += 1 if ok else 0

print(f'RESUMEN VERDE={verde} ROJO={len(casos) - verde}')
sys.exit(0 if verde == len(casos) else 1)
