"""Juez de la sonda de la BDA de video: compara BDAV.BIN de PakuPort contra el del oraculo.

Verdad terreno: BDAV.ORACULO.BIN, capturado ejecutando el MISMO BDAV.COM en DOSBox-X real.
No se compara contra una tabla escrita a mano, porque la mitad de estos campos dependen de
detalles del BIOS que no se pueden deducir "de memoria" sin inventar.

Por campo, ademas del byte, se dice que es: asi un ROJO se lee sin tener que ir a un manual.

Uso:  python tools\\bda_video\\judge_bdav.py [paku.bin] [oraculo.bin]
Sale con codigo 1 si algo esta en ROJO, para encadenarlo en un gate.
"""
import os, sys

D = os.path.dirname(os.path.abspath(__file__))
MODOS = (0x03, 0x04, 0x06, 0x0D, 0x0E, 0x10, 0x12, 0x13, 0x03)
POR_MODO = 34

# offset dentro del bloque de 34 -> (direccion en el segmento 40h, nombre)
CAMPOS = {}
for i in range(30):
    CAMPOS[i] = 0x49 + i
for i in range(4):
    CAMPOS[30 + i] = 0x84 + i

NOM = {0x49: 'modo de video', 0x4A: 'columnas (lo)', 0x4B: 'columnas (hi)',
       0x4C: 'tam pagina (lo)', 0x4D: 'tam pagina (hi)', 0x4E: 'inicio pagina (lo)',
       0x4F: 'inicio pagina (hi)', 0x60: 'cursor: linea fin', 0x61: 'cursor: linea inicio',
       0x62: 'pagina activa', 0x63: 'puerto CRTC (lo)', 0x64: 'puerto CRTC (hi)',
       0x65: 'CRT mode control', 0x66: 'color select (CGA)', 0x84: 'filas - 1',
       0x85: 'altura de caracter (lo)', 0x86: 'altura de caracter (hi)', 0x87: 'flags EGA'}

paku_p = sys.argv[1] if len(sys.argv) > 1 else os.path.join(D, 'BDAV.BIN')
orac_p = sys.argv[2] if len(sys.argv) > 2 else os.path.join(D, 'BDAV.ORACULO.BIN')
for p in (paku_p, orac_p):
    if not os.path.exists(p):
        raise SystemExit(f'ROJO: falta {p} (ver README.md para regenerarlo)')

paku, orac = open(paku_p, 'rb').read(), open(orac_p, 'rb').read()
esperado = len(MODOS) * POR_MODO
for n, p in ((len(paku), paku_p), (len(orac), orac_p)):
    if n != esperado:
        raise SystemExit(f'ROJO: {p} mide {n} B, se esperaban {esperado}')

total_mal = 0
for m, modo in enumerate(MODOS):
    base = m * POR_MODO
    malos = []
    for off in range(POR_MODO):
        addr = CAMPOS[off]
        if paku[base + off] != orac[base + off]:
            malos.append((addr, paku[base + off], orac[base + off]))
    # los cursores de las 8 paginas (40:50..40:5F) son ruido de posicion, no configuracion
    malos = [x for x in malos if not (0x50 <= x[0] <= 0x5F)]
    total_mal += len(malos)
    estado = 'VERDE' if not malos else f'ROJO ({len(malos)} campos)'
    print(f'modo {modo:02X}h  {estado}')
    for addr, p, o in malos:
        print(f'     40:{addr:02X} {NOM.get(addr, "?"):24} PakuPort {p:02X}  oraculo {o:02X}')

print(f'\nRESUMEN campos distintos = {total_mal} (0 = VERDE)')
sys.exit(0 if total_mal == 0 else 1)
