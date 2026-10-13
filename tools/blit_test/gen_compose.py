#!/usr/bin/env python3
"""Genera COMPOSE.COM — red de la IDENTIDAD POR PROCEDENCIA (fase 5).

CHAIN.COM demostró que la etiqueta de fichero sobrevive a un buffer intermedio. Falta lo
que de verdad rompe la identidad hoy: que el juego COMPONGA el sprite antes de volcarlo.
Es lo que hace el cursor de un juego de modo real (se mezcla con el fondo que hay debajo),
y por eso su hash de píxeles "solo es estable sobre fondo uniforme" — medido en ese juego.

COMPOSE.COM, en modo 13h:
  1. open DATA.BIN + read 16 bytes a DS:0080h
  2. rep movsb DS:0080h -> ES:0200h            (la cadena de CHAIN.COM)
  3. XOR 0FFh sobre los 4 primeros bytes de 0200h   <- LA COMPOSICION
  4. rep movsw DS:0200h -> A000:0000

El contenido que llega a VRAM YA NO ES el del fichero, así que su FNV es otro: una
entrada de manifiesto por hash de píxeles deja de casar. La procedencia, en cambio, sigue
diciendo DATA.BIN@0x0. Esa es exactamente la propiedad que hace falta para identificar un
sprite compuesto, y la que este test fija.

Usa el MISMO DATA.BIN que BLIT2.COM y CHAIN.COM.

Uso: python tools/blit_test/gen_compose.py   -> escribe COMPOSE.COM junto a este fichero
"""
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))
FNAME = b"DATA.BIN"
NBYTES = 16
BUF_A = 0x0080
BUF_B = 0x0200
NCOMPOSE = 4          # bytes alterados por la "composición"

CODE_LEN = 0x43
fname_off = 0x100 + CODE_LEN

code = bytearray()
code += b"\xB8\x13\x00"                              # mov ax,0013h
code += b"\xCD\x10"                                  # int 10h
code += b"\xB8\x00\x3D"                              # mov ax,3D00h (open)
code += b"\xBA" + struct.pack("<H", fname_off)       # mov dx,FNAME
code += b"\xCD\x21"                                  # int 21h
code += b"\x89\xC3"                                  # mov bx,ax
code += b"\xB4\x3F"                                  # mov ah,3Fh (read)
code += b"\xB9" + struct.pack("<H", NBYTES)          # mov cx,16
code += b"\xBA" + struct.pack("<H", BUF_A)           # mov dx,BUF_A
code += b"\xCD\x21"                                  # int 21h
code += b"\x0E\x07"                                  # push cs ; pop es
code += b"\xBE" + struct.pack("<H", BUF_A)           # mov si,BUF_A
code += b"\xBF" + struct.pack("<H", BUF_B)           # mov di,BUF_B
code += b"\xB9" + struct.pack("<H", NBYTES)          # mov cx,16
code += b"\xF3\xA4"                                  # rep movsb   (cadena de procedencia)
code += b"\xBF" + struct.pack("<H", BUF_B)           # mov di,BUF_B
code += b"\xB9" + struct.pack("<H", NCOMPOSE)        # mov cx,4
# L:
code += b"\x80\x35\xFF"                              # xor byte ptr [di],0FFh  <- composicion
code += b"\x47"                                      # inc di
code += b"\xE2\xFA"                                  # loop L  (-6)
code += b"\xB8\x00\xA0"                              # mov ax,0A000h
code += b"\x8E\xC0"                                  # mov es,ax
code += b"\x31\xFF"                                  # xor di,di
code += b"\xBE" + struct.pack("<H", BUF_B)           # mov si,BUF_B
code += b"\xB9" + struct.pack("<H", NBYTES // 2)     # mov cx,8 words
code += b"\xF3\xA5"                                  # rep movsw -> VRAM
code += b"\xEB\xFE"                                  # jmp $
assert len(code) == CODE_LEN, f'codigo {len(code)} bytes, la etiqueta asume {CODE_LEN}'

path = os.path.join(OUT, 'COMPOSE.COM')
with open(path, 'wb') as f:
    f.write(bytes(code) + FNAME + b"\x00")
print(f'{path}: {len(code)} bytes de codigo + nombre en {fname_off:#06x}')
print(f'  {FNAME.decode()} -> DS:{BUF_A:04X} -> DS:{BUF_B:04X} -> XOR FF en {NCOMPOSE} bytes '
      f'-> A000:0000')

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_16
    md = Cs(CS_ARCH_X86, CS_MODE_16)
    for i in md.disasm(bytes(code), 0x100):
        print(f'    {i.address:04X}  {i.bytes.hex():<10} {i.mnemonic} {i.op_str}')
except ImportError:
    print('  (capstone no instalado: sin verificacion del desensamblado)')
