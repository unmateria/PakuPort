"""Juez de la sonda del gancho de teclado INT 15h AH=4Fh: lee KBD15.BIN y dictamina.

Que vigila: que el INT 9 del BIOS respete el contrato del PC/AT antes de meter la tecla en el
buffer — ofrecersela a INT 15h con AH=4Fh y CF=1, y obedecer lo que el driver conteste. Son
tres cosas y las tres se miden con la MISMA tecla ('z', scancode 2Ch) pulsada una vez por fase:

  fase 1 "pasar":     el driver es llamado (cnt>=1) con AL=2Ch, devuelve CF=1
                      -> la BDA recibe 2C7Ah (la tecla, sin tocar).
  fase 2 "descartar": el driver es llamado, devuelve CF=0
                      -> la BDA NO recibe nada (la tecla desaparece, como en el hardware).
  fase 3 "traducir":  el driver es llamado, devuelve AL=1Eh con CF=1
                      -> la BDA recibe 1E61h ('a'), NO la tecla pulsada.

La fase 1 es la que arregla al juego que lo destapo (instala ahi su teclado y no roba el INT 9);
las fases 2 y 3 estan para que el gancho no se implemente a medias: llamar al driver e ignorar
su respuesta pasaria la fase 1 y seguiria siendo falso.

Uso:  python tools\\kbd_int15\\judge_kbd15.py [ruta\\a\\KBD15.BIN]
Sale con codigo 1 si algo esta en ROJO, para encadenarlo en un gate.
"""
import os, struct, sys

D = os.path.dirname(os.path.abspath(__file__))
ruta = sys.argv[1] if len(sys.argv) > 1 else os.path.join(D, 'KBD15.BIN')
if not os.path.exists(ruta):
    raise SystemExit(f'ROJO: falta {ruta} (ver README.md para regenerarlo)')

datos = open(ruta, 'rb').read()
if len(datos) != 15:
    raise SystemExit(f'ROJO: {ruta} mide {len(datos)} B, se esperaban 15')

fases = []
for i in range(3):
    cnt, al, bda = struct.unpack_from('<HBH', datos, i * 5)
    fases.append((cnt, al, bda))

SCAN_Z, ENTRADA_Z = 0x2C, 0x2C7A   # 'z'
ENTRADA_A = 0x1E61                 # 'a', a la que traduce la fase 3

casos = [
    ('f1 driver llamado',      fases[0][0] >= 1,               f'cnt={fases[0][0]}'),
    ('f1 scancode ofrecido',   fases[0][1] == SCAN_Z,          f'AL={fases[0][1]:02X}'),
    ('f1 tecla en la BDA',     fases[0][2] == ENTRADA_Z,       f'BDA={fases[0][2]:04X}'),
    ('f2 driver llamado',      fases[1][0] >= 1,               f'cnt={fases[1][0]}'),
    ('f2 tecla DESCARTADA',    fases[1][2] == 0x0000,          f'BDA={fases[1][2]:04X}'),
    ('f3 driver llamado',      fases[2][0] >= 1,               f'cnt={fases[2][0]}'),
    ('f3 tecla TRADUCIDA',     fases[2][2] == ENTRADA_A,       f'BDA={fases[2][2]:04X}'),
]

verde = 0
for nombre, ok, detalle in casos:
    print(f'{nombre:24} {detalle:16} -> {"VERDE" if ok else "ROJO"}')
    verde += 1 if ok else 0

print(f'RESUMEN VERDE={verde} ROJO={len(casos) - verde}')
sys.exit(0 if verde == len(casos) else 1)
