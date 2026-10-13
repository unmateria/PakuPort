#!/usr/bin/env python3
"""Barrido CRTC: genera CRTC.COM (mismo binario para el oraculo DOSBox-X y para PakuPort).

Para CADA modo de vídeo de la lista:
  1. INT 10h AH=00 AL=<modo>            (set mode: el BIOS debe programar el CRTC)
  2. IN AL, 3CCh                        (Miscellaneous Output: bit0 = base color/mono)
  3. Para i = 0x00..0x18:  OUT base,i ; IN AL,base+1  -> guarda el byte leído

y al terminar vuelve a modo 3 y escribe el buffer entero a CRTC.BIN por INT 21h
(3C crear / 40 escribir / 3E cerrar), igual que tools/platform_sweep/TEST.COM.

Layout de CRTC.BIN (ver README.md):
  registro por modo = 26 bytes:  [0..24] = CRTC r00..r18 leídos, [25] = Misc Output (3CCh)
  offset del modo n = n * 26, en el orden de MODES (no en orden numérico de modo).

Base del CRTC por modo: DOSBox-X int10_modes.cpp:1547-1549
  bool mono_mode=(mode == 7) || (mode==0xf);  if (mono_mode) crtc_base=0x3b4; else 0x3d4;
El misc output se escribe ANTES (línea 1580, "Setup for 3b4 or 3d4") que el bloque
"Program CRTC" (línea 1686), así que al volver del INT 10h los puertos ya están donde
toca. Por eso la base va HORNEADA en el COM (constante por modo, mismo binario en los
dos mundos) en vez de decidirse en runtime desde 3CCh: si un mundo pusiera otro bit0
compararíamos puertos distintos sin enterarnos. El byte 25 graba el Misc Output real de
cada mundo para que el juez pueda avisar de esa divergencia.

Salida: CRTC.COM + modes.json (lista de modos, base y layout, para el juez).
"""
import json
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))

# Modos barridos, en el orden EXACTO en el que corren (el estado se arrastra entre modos
# igual que en el barrido de plataforma; los dos mundos ejecutan la misma secuencia).
# Los 12 modos del plan + 07/0F (los mono, para medir también la ruta 3B4/3B5) + 11.
MODES = [0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07,
         0x0D, 0x0E, 0x0F, 0x10, 0x11, 0x12, 0x13]

NREG = 25          # CRTC r00..r18 (0x18 incluido)
REC = NREG + 1     # +1 byte de Miscellaneous Output
RES_BASE = 0x2000  # buffer de resultados dentro del COM (16-alineado, lejos del código)


def crtc_base(mode):
    """DOSBox-X int10_modes.cpp:1547 — modos 7 y 0Fh son mono -> CRTC en 3B4/3B5."""
    return 0x3B4 if mode in (0x07, 0x0F) else 0x3D4


def build_com():
    code = bytearray()
    code += b"\x0E\x1F"            # push cs ; pop ds
    code += b"\x0E\x07"            # push cs ; pop es
    for n, mode in enumerate(MODES):
        rec = RES_BASE + n * REC
        base = crtc_base(mode)
        code += b"\xB8" + struct.pack("<H", mode)          # mov ax, 00<mode>  (AH=00 set mode)
        code += b"\xCD\x10"                                # int 10h
        code += b"\xBA" + struct.pack("<H", 0x03CC)        # mov dx, 3CCh
        code += b"\xEC"                                    # in al, dx     (Misc Output)
        code += b"\x2E\xA2" + struct.pack("<H", rec + NREG)  # mov cs:[rec+25], al
        for i in range(NREG):
            code += b"\xBA" + struct.pack("<H", base)      # mov dx, base
            code += b"\xB0" + bytes([i])                   # mov al, i
            code += b"\xEE"                                # out dx, al    (index)
            code += b"\x42"                                # inc dx
            code += b"\xEC"                                # in al, dx     (data)
            code += b"\x2E\xA2" + struct.pack("<H", rec + i)  # mov cs:[rec+i], al
    code += b"\xB8\x03\x00\xCD\x10"                        # mov ax,0003h ; int 10h (texto)
    code += b"\x0E\x1F"                                    # push cs ; pop ds (DS:DX del 3C/40)

    total = len(MODES) * REC
    # El epílogo tiene tamaño fijo, así que el offset del nombre de fichero se calcula
    # con una plantilla del mismo tamaño (mismo truco que gen_tests.py).
    epilog_len = 10 + 11 + 4 + 5
    fname_off = 0x100 + len(code) + epilog_len
    epilog = bytearray()
    epilog += b"\xB4\x3C\x31\xC9" + b"\xBA" + struct.pack("<H", fname_off) + b"\xCD\x21"  # create
    epilog += b"\x89\xC3\xB4\x40" + b"\xB9" + struct.pack("<H", total)
    epilog += b"\xBA" + struct.pack("<H", RES_BASE) + b"\xCD\x21"                          # write
    epilog += b"\xB4\x3E\xCD\x21"                                                          # close
    epilog += b"\xB8\x00\x4C\xCD\x21"                                                      # exit
    assert len(epilog) == epilog_len, (len(epilog), epilog_len)

    body = bytes(code) + bytes(epilog) + FNAME + b"\x00"
    assert 0x100 + len(body) <= RES_BASE, \
        f"el código pisa el buffer (0x{0x100 + len(body):X} > 0x{RES_BASE:X})"
    # La imagen incluye el buffer a CEROS: la memoria por encima del fichero no está
    # garantizada a cero en DOS real, y el juez exige len(CRTC.BIN) == total.
    img = bytearray(RES_BASE - 0x100 + total)
    img[0:len(body)] = body
    return bytes(img), total


FNAME = b"CRTC.BIN"


def main():
    com, total = build_com()
    with open(os.path.join(OUT, "CRTC.COM"), "wb") as f:
        f.write(com)
    meta = {
        "record_bytes": REC,
        "nreg": NREG,
        "total": total,
        "res_base": RES_BASE,
        "modes": [{"mode": m, "index": n, "off": n * REC, "crtc_base": crtc_base(m)}
                  for n, m in enumerate(MODES)],
    }
    with open(os.path.join(OUT, "modes.json"), "w") as f:
        json.dump(meta, f, indent=1)
    print(f"CRTC.COM: {len(com)} bytes, {len(MODES)} modos x {REC} B = {total} B "
          f"de resultado en CS:{RES_BASE:04X}")


if __name__ == "__main__":
    main()
