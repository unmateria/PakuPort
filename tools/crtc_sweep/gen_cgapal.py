#!/usr/bin/env python3
"""Caso de la PALETA en los modos CGA sobre EGA/VGA: genera CGAPAL.COM (mismo binario para
el oraculo DOSBox-X y para PakuPort).

Lo que mide, y por que va ANTES de tocar el render
-------------------------------------------------
Destapado al portar un juego CGA: su titulo sale con el dibujo exacto del
original pero en cian/magenta/blanco donde el oraculo lo pinta en azul/rojo/gris. En una
EGA/VGA los modos 4/5 NO son un CGA de colores fijos: los 2 bits del pixel entran en el
Attribute Controller y de ahi al DAC, asi que sus 4 colores son REDEFINIBLES — y el juego
los redefine. `VideoState.RenderCga()` no mira ni `AttrRegs` ni `Dac`: pinta con las tablas
fijas `CgaPalettes`/`CgaBackgroundColors`.

Antes de cambiar el render hay que saber si GUARDAMOS esa paleta como el hardware. Si el
BIOS deja el AC/DAC distintos que el oraculo, leerlos al pintar empeoraria las cosas en vez
de arreglarlas. Este COM compara justo eso, y sale por FICHERO (como CRTC.COM y LOCK.COM),
asi que no necesita capturar la pantalla.

  fase 0  int 10h AX=0004 (modo 4)                      -> A: AC[0..15] + DAC[0..15]  (64 B)
  fase 1  int 10h AH=0B BH=01 BL=00 (paleta CGA 0)      -> B: idem                    (64 B)
  fase 2  int 10h AH=10 AL=00 BL=01 BH=1Ch (recolorea
          el registro de paleta 1 a un color arbitrario) -> C: idem                    (64 B)

CGAPAL.BIN = 192 bytes. Juez: judge_cgapal.py.

Como se leen los registros (vga_attr.cpp / vga_dac.cpp):
  Attribute Controller: `in al,3DAh` resetea el flip-flop indice/dato; `out 3C0h,idx`
  selecciona y `in al,3C1h` devuelve el dato. El indice se escribe SIN el bit 5 para poder
  leer; al terminar se escribe 20h para volver a habilitar la salida de video (PAS), que si
  no la pantalla se queda negra.
  DAC: `out 3C7h,idx` (indice de LECTURA) y tres `in al,3C9h` (R,G,B de 6 bits).
"""
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))

RES = 0x2000
NDAC = 64                       # los modos CGA sobre EGA apuntan a indices 13h/15h/17h, o sea
                                # POR ENCIMA de 16: hay que leer las 64 entradas de la paleta
                                # EGA o no se ve si estan cargadas (el barrido del DAC encontro justo eso
                                # en el modo 13h: todo indice >= 16 salia negro).
PHASE = 16 + NDAC * 3
TOTAL = PHASE * 3
FNAME = b"CGAPAL.BIN"


def read_block(dst):
    """AC[0..15] -> dst..dst+15, DAC[0..NDAC-1] (RGB) -> dst+16..."""
    c = bytearray()
    # --- Attribute Controller ---
    for i in range(16):
        c += b"\xBA\xDA\x03\xEC"                        # mov dx,3DAh ; in al,dx (reset flip-flop)
        c += b"\xBA\xC0\x03"                            # mov dx,3C0h
        c += b"\xB0" + bytes([i])                       # mov al, i     (sin bit 5: modo indice)
        c += b"\xEE"                                    # out dx,al
        c += b"\xBA\xC1\x03"                            # mov dx,3C1h
        c += b"\xEC"                                    # in al,dx      (dato)
        c += b"\x2E\xA2" + struct.pack("<H", dst + i)   # mov cs:[dst+i], al
    # rehabilitar la salida de video (PAS = bit 5)
    c += b"\xBA\xDA\x03\xEC"                            # reset flip-flop
    c += b"\xBA\xC0\x03\xB0\x20\xEE"                    # mov dx,3C0h ; mov al,20h ; out dx,al
    # --- DAC 0..NDAC-1 ---
    for i in range(NDAC):
        c += b"\xBA\xC7\x03"                            # mov dx,3C7h (indice de lectura)
        c += b"\xB0" + bytes([i])                       # mov al, i
        c += b"\xEE"                                    # out dx,al
        c += b"\xBA\xC9\x03"                            # mov dx,3C9h
        for comp in range(3):
            c += b"\xEC"                                # in al,dx
            c += b"\x2E\xA2" + struct.pack("<H", dst + 16 + i * 3 + comp)
    return bytes(c)


def build_com():
    c = bytearray()
    c += b"\x0E\x1F"                                    # push cs ; pop ds
    c += b"\xB8\x04\x00\xCD\x10"                        # mov ax,0004h ; int 10h (modo 4)
    c += read_block(RES)
    c += b"\xB4\x0B\xB7\x01\xB3\x00\xCD\x10"            # mov ah,0Bh ; bh=1 ; bl=0 ; int 10h
    c += read_block(RES + PHASE)
    c += b"\xB8\x00\x10\xBB\x1C\x01\xCD\x10"            # mov ax,1000h ; bx=011Ch ; int 10h
    #                                                     (BL=01 registro de paleta, BH=1Ch valor)
    c += read_block(RES + PHASE * 2)

    # epilogo: volver a texto y escribir CGAPAL.BIN — igual que gen_crtc.py / gen_lock.py
    c += b"\xB8\x03\x00\xCD\x10"                        # mov ax,0003h ; int 10h
    c += b"\x0E\x1F"                                    # push cs ; pop ds
    epilog_len = 10 + 11 + 4 + 5
    fname_off = 0x100 + len(c) + epilog_len
    e = bytearray()
    e += b"\xB4\x3C\x31\xC9" + b"\xBA" + struct.pack("<H", fname_off) + b"\xCD\x21"
    e += b"\x89\xC3\xB4\x40" + b"\xB9" + struct.pack("<H", TOTAL)
    e += b"\xBA" + struct.pack("<H", RES) + b"\xCD\x21"
    e += b"\xB4\x3E\xCD\x21"
    e += b"\xB8\x00\x4C\xCD\x21"
    assert len(e) == epilog_len, (len(e), epilog_len)

    body = bytes(c) + bytes(e) + FNAME + b"\x00"
    assert 0x100 + len(body) <= RES, f"el codigo pisa el buffer (0x{0x100 + len(body):X})"
    img = bytearray(RES - 0x100 + TOTAL)
    img[0:len(body)] = body
    return bytes(img)


def main():
    com = build_com()
    with open(os.path.join(OUT, "CGAPAL.COM"), "wb") as f:
        f.write(com)
    print(f"CGAPAL.COM: {len(com)} bytes; CGAPAL.BIN esperado: {TOTAL} B "
          f"(3 fases x {PHASE}: AC 16 + DAC 16x3) en CS:{RES:04X}")


if __name__ == "__main__":
    main()
