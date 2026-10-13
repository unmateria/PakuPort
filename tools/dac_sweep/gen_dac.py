#!/usr/bin/env python3
"""Barrido del DAC: genera DAC.COM (mismo binario para el oraculo DOSBox-X y PakuPort).

Para CADA modo de video de la lista:
  1. INT 10h AH=00 AL=<modo>       (set mode: el BIOS debe cargar la paleta por defecto)
  2. OUT 3C7h, 0                   (indice de LECTURA del DAC = entrada 0)
  3. 768 x IN AL,3C9h              (256 entradas x R,G,B, con autoincremento del indice)

y al terminar vuelve a modo 3 y escribe el buffer entero a DAC.BIN por INT 21h
(3C crear / 40 escribir / 3E cerrar), igual que CRTC.COM y platform_sweep/TEST.COM.

Por que estos 4 modos: 13h es el caso que motiva el barrido (256 colores), 0Dh y 12h
cubren la ruta de 16 colores (que carga la paleta EGA de 64 entradas, distinta rutina)
y 03h el arranque en texto. Asi el juez ve las dos rutas, no solo la rota.

Layout de DAC.BIN: 768 bytes por modo (256 entradas x 3 componentes de 6 bits), en el
orden de MODES; offset del modo n = n * 768.

Salida: DAC.COM + modes.json (layout para el juez).
"""
import json
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))

# Orden EXACTO de ejecucion (el estado se arrastra entre modos; los dos mundos corren
# la misma secuencia).
MODES = [0x03, 0x0D, 0x12, 0x13]

NENTRIES = 256
REC = NENTRIES * 3          # 768 bytes por modo
RES_BASE = 0x2000           # buffer de resultados dentro del COM, lejos del codigo
FNAME = b"DAC.BIN"


def build_com():
    code = bytearray()
    code += b"\x0E\x1F"                                    # push cs ; pop ds
    code += b"\x0E\x07"                                    # push cs ; pop es
    for n, mode in enumerate(MODES):
        rec = RES_BASE + n * REC
        code += b"\xB8" + struct.pack("<H", mode)          # mov ax,00<mode> (AH=00 set mode)
        code += b"\xCD\x10"                                # int 10h
        code += b"\xBA" + struct.pack("<H", 0x03C7)        # mov dx,3C7h
        code += b"\x30\xC0"                                # xor al,al
        code += b"\xEE"                                    # out dx,al      (read index = 0)
        code += b"\xBA" + struct.pack("<H", 0x03C9)        # mov dx,3C9h
        code += b"\xBF" + struct.pack("<H", rec)           # mov di,rec
        code += b"\xB9" + struct.pack("<H", REC)           # mov cx,768
        # L:
        code += b"\xEC"                                    # in al,dx       (R/G/B)
        code += b"\x2E\x88\x05"                            # mov cs:[di],al
        code += b"\x47"                                    # inc di
        code += b"\xE2\xF9"                                # loop L         (-7)
    code += b"\xB8\x03\x00\xCD\x10"                        # mov ax,0003h ; int 10h (texto)
    code += b"\x0E\x1F"                                    # push cs ; pop ds (DS:DX del 3C/40)

    total = len(MODES) * REC
    # Epilogo de tamano fijo: el offset del nombre de fichero se calcula con esa longitud
    # (mismo truco que gen_crtc.py).
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
        f"el codigo pisa el buffer (0x{0x100 + len(body):X} > 0x{RES_BASE:X})"
    # La imagen incluye el buffer a CEROS: la memoria alta no esta garantizada a cero en
    # DOS real y el juez exige len(DAC.BIN) == total.
    img = bytearray(RES_BASE - 0x100 + total)
    img[0:len(body)] = body
    return bytes(img), total


def main():
    com, total = build_com()
    with open(os.path.join(OUT, "DAC.COM"), "wb") as f:
        f.write(com)
    meta = {"record_bytes": REC, "entries": NENTRIES, "total": total, "res_base": RES_BASE,
            "modes": [{"mode": m, "index": n, "off": n * REC} for n, m in enumerate(MODES)]}
    with open(os.path.join(OUT, "modes.json"), "w") as f:
        json.dump(meta, f, indent=1)
    print(f"DAC.COM: {len(com)} bytes, {len(MODES)} modos x {REC} B = {total} B "
          f"de resultado en CS:{RES_BASE:04X}")

    try:
        from capstone import Cs, CS_ARCH_X86, CS_MODE_16
        md = Cs(CS_ARCH_X86, CS_MODE_16)
        n = 0
        for i in md.disasm(com[:60], 0x100):
            print(f'    {i.address:04X}  {i.bytes.hex():<10} {i.mnemonic} {i.op_str}')
            n += 1
            if n > 14:
                break
    except ImportError:
        print('  (capstone no instalado: sin verificacion del desensamblado)')


if __name__ == "__main__":
    main()
