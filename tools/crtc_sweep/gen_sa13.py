#!/usr/bin/env python3
"""Caso del START ADDRESS del CRTC en MODO 13h: genera SA13.COM (mismo binario para el
oraculo DOSBox-X y para PakuPort).

Lo que mide, y por que hace falta una red nueva
----------------------------------------------
`VideoState.RenderMode13h` lee `Planes[0]` LINEALMENTE desde el offset 0: ignora el start
address del CRTC (r0C/r0D), el offset/stride (r13) y el line compare. El camino planar
(`RenderEgaPlanar`) si los respeta desde que se portaron las tablas del CRTC. Es decir: cualquier juego que haga doble
buffer o scroll por hardware en modo 13h veria siempre la pagina 0. Ninguno de los juegos probados lo
dispara hoy, asi que el fallo es invisible en los gates existentes — de ahi este COM.

Lo que hace el COM
------------------
  1. `int 10h` AH=00 AL=13h  (modo 13h; el BIOS deja r13=28h y r14 con el bit 6 = dword mode)
  2. Rellena los 64 KB de A000: con un patron POR FILAS de 320 pixeles: la fila n vale n.
     204 filas completas (65280 B) + los ultimos 256 B a 0FEh como marcador.
  3. Programa el start address a 100 (r0C=00, r0D=64h).
     100 * 4 = 400 pixeles = UNA fila entera + 80 pixeles, elegido a proposito para que
     falle distinto un error de escala que un error de desplazamiento:
       - correcto:            la primera fila de pantalla son 240 px del color 1 y 80 del 2
       - si se ignora:        la primera fila es entera del color 0
       - si se aplica sin *4: la primera fila serian 220 px del 0 y 100 del 1
  4. `jmp $` — la pantalla se queda fija para que los dos mundos se capturen sin carreras.

El start address NO lo tapa el candado de escritura (r11 bit 7 solo protege r00..r07, ver
LOCK.COM), asi que la escritura entra en los dos mundos.

Por que el resultado sale por PANTALLA y no por fichero, como el resto de redes del CRTC:
lo que se compara es el LAYOUT del render, y eso no se puede leer por puertos — un COM no
puede preguntar "que se esta viendo". La captura se hace fuera: `video_screenshot_raw_vga`
en el oraculo y `PAKU_FB_PNG` en el nuestro. Juez: judge_sa13.py.

Modelo del hardware portado (dosbox-x src/hardware/vga_draw.cpp, VGA_SetupDrawing:6302+,
y vga_crtc.cpp VGA_CheckAddrShift:47-55):

    addr_shift = 2 si r14 bit 6 (dword mode)  |  0 si r17 bit 6 (byte mode)  |  1 si no
    case M_VGA:  address = real_start << addr_shift;  linear_mask = 0x3FFFF
    case M_LIN8: address = (address + bytes_skip) * 4 + panning     <- byte_panning_shift=4

Como la memoria encadenada de DOSBox guarda el pixel lineal P en (P & ~3) << 2 | (P & 3),
esa direccion dividida entre 4 da el PIXEL de inicio:  pixel_start = real_start << addr_shift
(x4 en el modo 13h estandar). Y el paso de linea (address_add:6924) es
scan_len * (8 << addr_shift) en memoria = r13 * (2 << addr_shift) pixeles = 320 con r13=28h.
"""
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))

CRTC_BASE = 0x3D4
START_ADDR = 100        # 100 * 4 = 400 px = 1 fila + 80 px
ROWS = 204              # 204 * 320 = 65280 B; los 256 restantes van a 0FEh
ROW_W = 320
TAIL = 0x10000 - ROWS * ROW_W
TAIL_VAL = 0xFE


def build_com():
    c = bytearray()
    c += b"\x0E\x1F"                                    # push cs ; pop ds
    c += b"\xB8\x13\x00\xCD\x10"                        # mov ax,0013h ; int 10h

    # --- patron: fila n = color n, 320 px por fila ---
    c += b"\xB8\x00\xA0\x8E\xC0"                        # mov ax,0A000h ; mov es,ax
    c += b"\x31\xFF"                                    # xor di,di
    c += b"\x30\xC0"                                    # xor al,al        (color = nº de fila)
    c += b"\xBB" + struct.pack("<H", ROWS)              # mov bx, ROWS
    loop = len(c)                                       # etiqueta "fila"
    c += b"\xB9" + struct.pack("<H", ROW_W)             # mov cx, 320
    c += b"\xF3\xAA"                                    # rep stosb
    c += b"\xFE\xC0"                                    # inc al
    c += b"\x4B"                                        # dec bx
    rel = loop - (len(c) + 2)                           # jnz corto hacia atras
    assert -128 <= rel <= 127
    c += b"\x75" + struct.pack("<b", rel)               # jnz fila
    c += b"\xB0" + bytes([TAIL_VAL])                    # mov al, 0FEh
    c += b"\xB9" + struct.pack("<H", TAIL)              # mov cx, TAIL
    c += b"\xF3\xAA"                                    # rep stosb

    # --- start address: r0C = high, r0D = low ---
    def outreg(idx, val):
        return (b"\xBA" + struct.pack("<H", CRTC_BASE) + b"\xB0" + bytes([idx]) + b"\xEE"
                + b"\x42" + b"\xB0" + bytes([val]) + b"\xEE")
    c += outreg(0x0C, (START_ADDR >> 8) & 0xFF)
    c += outreg(0x0D, START_ADDR & 0xFF)

    c += b"\xEB\xFE"                                    # jmp $ (pantalla fija)
    return bytes(c)


def main():
    com = build_com()
    with open(os.path.join(OUT, "SA13.COM"), "wb") as f:
        f.write(com)
    print(f"SA13.COM: {len(com)} bytes; start address = {START_ADDR} "
          f"(= {START_ADDR * 4} px = fila {START_ADDR * 4 // ROW_W} + {START_ADDR * 4 % ROW_W} px)")


if __name__ == "__main__":
    main()
