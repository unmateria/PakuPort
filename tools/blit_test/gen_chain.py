#!/usr/bin/env python3
"""Genera CHAIN.COM — red de la CADENA de procedencia (doctrina fase 2 -> fase 5).

BLIT2.COM ya cubre el caso directo: fichero -> RAM -> VRAM, y la telemetria etiqueta el
blit con `file=DATA.BIN@0x0`. Lo que NO cubre nadie es el caso real de los juegos que
componen: fichero -> buffer A -> buffer B -> VRAM. Ahi la etiqueta se pierde en el salto
intermedio, porque BlitProvenance solo la pone `Int21hHandler.ReadFile` y solo se
consulta para el origen del blit final. Es exactamente la "letra pequena" de la
doctrina de procedencia: "la procedencia debe ENCADENARSE, no basta el ultimo
salto", y el prerrequisito de que un cursor compuesto con el fondo conserve su identidad.

CHAIN.COM hace, en modo 13h:
  1. open DATA.BIN + read 16 bytes a DS:0080h      (el unico Record de hoy)
  2. rep movsb DS:0080h -> ES:0200h                (SALTO INTERMEDIO, RAM->RAM)
  3. rep movsw DS:0200h -> A000:0000               (volcado a VRAM)

Sin encadenado, el blit (3) sale sin `file=`. Con encadenado, sale con
`file=DATA.BIN@0x0` igual que el directo de BLIT2.COM.

Usa el MISMO DATA.BIN que BLIT2.COM (no duplica datos de prueba).

Uso: python tools/blit_test/gen_chain.py   -> escribe CHAIN.COM junto a este fichero
"""
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))
FNAME = b"DATA.BIN"
NBYTES = 16          # los que mide DATA.BIN
BUF_A = 0x0080       # destino del read (igual que BLIT2.COM)
BUF_B = 0x0200       # buffer de composicion: el eslabon que hoy rompe la cadena

CODE_LEN = 0x37      # longitud del codigo; el nombre va justo detras (asertado abajo)
fname_off = 0x100 + CODE_LEN

code = bytearray()
code += b"\xB8\x13\x00"                              # mov ax,0013h
code += b"\xCD\x10"                                  # int 10h
code += b"\xB8\x00\x3D"                              # mov ax,3D00h   (open, read-only)
code += b"\xBA" + struct.pack("<H", fname_off)       # mov dx,FNAME
code += b"\xCD\x21"                                  # int 21h
code += b"\x89\xC3"                                  # mov bx,ax      (handle)
code += b"\xB4\x3F"                                  # mov ah,3Fh     (read)
code += b"\xB9" + struct.pack("<H", NBYTES)          # mov cx,16
code += b"\xBA" + struct.pack("<H", BUF_A)           # mov dx,BUF_A
code += b"\xCD\x21"                                  # int 21h        -> Record(BUF_A,16)
code += b"\x0E\x07"                                  # push cs ; pop es
code += b"\xBE" + struct.pack("<H", BUF_A)           # mov si,BUF_A
code += b"\xBF" + struct.pack("<H", BUF_B)           # mov di,BUF_B
code += b"\xB9" + struct.pack("<H", NBYTES)          # mov cx,16
code += b"\xF3\xA4"                                  # rep movsb      <- SALTO INTERMEDIO
code += b"\xB8\x00\xA0"                              # mov ax,0A000h
code += b"\x8E\xC0"                                  # mov es,ax
code += b"\x31\xFF"                                  # xor di,di
code += b"\xBE" + struct.pack("<H", BUF_B)           # mov si,BUF_B   (origen ya "anonimo")
code += b"\xB9" + struct.pack("<H", NBYTES // 2)     # mov cx,8 words
code += b"\xF3\xA5"                                  # rep movsw      -> VRAM
code += b"\xEB\xFE"                                  # jmp $
assert len(code) == CODE_LEN, f'codigo {len(code)} bytes, la etiqueta asume {CODE_LEN}'

path = os.path.join(OUT, 'CHAIN.COM')
with open(path, 'wb') as f:
    f.write(bytes(code) + FNAME + b"\x00")
print(f'{path}: {len(code)} bytes de codigo + nombre en {fname_off:#06x}')
print(f'  cadena: {FNAME.decode()} -> DS:{BUF_A:04X} -> DS:{BUF_B:04X} -> A000:0000 '
      f'({NBYTES} bytes)')

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_16
    md = Cs(CS_ARCH_X86, CS_MODE_16)
    for i in md.disasm(bytes(code), 0x100):
        print(f'    {i.address:04X}  {i.bytes.hex():<10} {i.mnemonic} {i.op_str}')
except ImportError:
    print('  (capstone no instalado: sin verificacion del desensamblado)')
