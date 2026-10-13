#!/usr/bin/env python3
"""Caso del CANDADO de escritura del CRTC (write protect, r11 bit 7): genera LOCK.COM
(mismo binario para el oraculo DOSBox-X y para PakuPort).

Lo que el barrido de set-mode NO ve (judge_crtc.py compara el estado final tras el
INT 10h): con el candado puesto (el BIOS deja r11=8E) el hardware IGNORA las escrituras
del juego a r00..r07 (vga_crtc.cpp:294 crtc(read_only) + los case 0x00..0x07 con
"if (crtc(read_only)) break;"). Este COM lo mide directamente:

  fase 0  int 10h AH=00 AL=03 (set mode 3: deja r11=8E, candado PUESTO)
          lee r00..r18                                   -> A[0..24]   (25 B)
  fase 1  para i=0..7: escribe A[i] XOR 55h, relee       -> B[0..7]    (8 B)
          (candado puesto: se espera B[i] == A[i] en los DOS mundos; el XOR 55h
           lleva el bit 4 puesto, asi que en i=7 tambien se ejercita el matiz del
           line compare: el byte almacenado NO cambia aunque el latch interno si)
  fase 2  escribe r11 = A[11h] AND 7Fh (ABRE el candado)
          escribe r00 = A[0] XOR 55h, relee              -> C          (1 B)
          (candado abierto: se espera C == A[0] XOR 55h)
          restaura r00 = A[0]; escribe r11 = A[11h] (candado puesto otra vez)
          relee r11                                      -> D          (1 B)

LOCK.BIN = 35 bytes: A(25) + B(8) + C(1) + D(1). Juez: judge_lock.py (byte-exacto
oraculo vs paku + las 3 aserciones semanticas en cada mundo por separado).

NO observable por puertos (documentado, no medido): el bit 4 de una escritura a r07 con
el candado puesto SI pasa al latch line_compare (vga_crtc.cpp case 0x07, "Line compare
bit ignores read only") — la lectura devuelve el byte viejo, asi que un COM no lo ve;
en PakuPort esta implementado fiel en VideoState.WriteCrtcRegister (campo LineCompare).
"""
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))

BASE = 0x3D4       # modo 3 -> CRTC en color
NREG = 25
RES = 0x2000       # A en 0x2000, B en 0x2019, C en 0x2021, D en 0x2022
A, B, C, D = RES, RES + 25, RES + 33, RES + 34
TOTAL = 35
FNAME = b"LOCK.BIN"


def build_com():
    c = bytearray()
    c += b"\x0E\x1F"                                   # push cs ; pop ds
    c += b"\x0E\x07"                                   # push cs ; pop es
    c += b"\xB8\x03\x00\xCD\x10"                       # mov ax,0003h ; int 10h (r11 <- 8E)

    def sel(i):
        """DX=BASE, AL=i, OUT (selecciona registro), DX=BASE+1."""
        return b"\xBA" + struct.pack("<H", BASE) + b"\xB0" + bytes([i]) + b"\xEE\x42"

    # fase 0: A[i] = read r_i    (mismo patron que CRTC.COM, stores con prefijo CS)
    for i in range(NREG):
        c += sel(i)
        c += b"\xEC"                                   # in al, dx
        c += b"\x2E\xA2" + struct.pack("<H", A + i)    # mov cs:[A+i], al
    # fase 1: para i=0..7, escribe A[i]^55h y relee -> B[i]
    for i in range(8):
        c += sel(i)
        c += b"\x2E\xA0" + struct.pack("<H", A + i)    # mov al, cs:[A+i]
        c += b"\x34\x55"                               # xor al, 55h
        c += b"\xEE"                                   # out dx, al   (escritura CANDADA)
        c += b"\xEC"                                   # in al, dx    (releer)
        c += b"\x2E\xA2" + struct.pack("<H", B + i)    # mov cs:[B+i], al
    # fase 2a: abrir el candado — r11 = A[11h] & 7Fh
    c += sel(0x11)
    c += b"\x2E\xA0" + struct.pack("<H", A + 0x11)     # mov al, cs:[A+11h]
    c += b"\x24\x7F"                                   # and al, 7Fh
    c += b"\xEE"                                       # out dx, al
    # fase 2b: r00 = A[0]^55h con el candado abierto, releer -> C
    c += sel(0x00)
    c += b"\x2E\xA0" + struct.pack("<H", A + 0)        # mov al, cs:[A+0]
    c += b"\x34\x55\xEE"                               # xor al,55h ; out dx,al
    c += b"\xEC"                                       # in al, dx
    c += b"\x2E\xA2" + struct.pack("<H", C)            # mov cs:[C], al
    # restaurar r00 (el indice sigue seleccionado; leer no lo cambia)
    c += b"\x2E\xA0" + struct.pack("<H", A + 0)        # mov al, cs:[A+0]
    c += b"\xEE"                                       # out dx, al
    # cerrar el candado otra vez y releer r11 -> D
    c += sel(0x11)
    c += b"\x2E\xA0" + struct.pack("<H", A + 0x11)     # mov al, cs:[A+11h]
    c += b"\xEE"                                       # out dx, al
    c += b"\xEC"                                       # in al, dx
    c += b"\x2E\xA2" + struct.pack("<H", D)            # mov cs:[D], al

    # epilogo: escribir LOCK.BIN (35 B desde RES) y salir — igual que gen_crtc.py
    c += b"\x0E\x1F"                                   # push cs ; pop ds
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
    with open(os.path.join(OUT, "LOCK.COM"), "wb") as f:
        f.write(com)
    print(f"LOCK.COM: {len(com)} bytes; LOCK.BIN esperado: {TOTAL} B "
          f"(A=25, B=8, C=1, D=1) en CS:{RES:04X}")


if __name__ == "__main__":
    main()
