#!/usr/bin/env python3
"""Juez del caso de la PALETA en los modos CGA sobre EGA/VGA (CGAPAL.COM).

Compara byte a byte, entre el oraculo y PakuPort, lo que queda programado en el Attribute
Controller y en el DAC (64 entradas: la paleta EGA completa, porque los modos CGA apuntan a
indices 13h/15h/17h, por encima de 16) en tres momentos:

  fase 0  tras `int 10h AX=0004` (modo 4)
  fase 1  tras `int 10h AH=0B BH=01 BL=00`  (seleccionar la paleta CGA 0: verde/rojo/marron)
  fase 2  tras `int 10h AX=1000 BX=011Ch`   (recolorear el registro de paleta 1)

Aserciones semanticas, para que una captura basura no cuele en verde (se exigen en los DOS
mundos por separado, como en judge_lock.py):

  - fase 0: los registros 1..3 del AC valen 13h/15h/17h — la paleta CGA por defecto que
    programa el BIOS (dosbox-x int10_modes.cpp, case M_CGA4).
  - fase 1: pasan a 12h/14h/16h — INT10_SetColorSelect con BL=0 hace
    `val = (CURRENT_PAL & 0x10) | 2 | BL` y escribe val, val+2, val+4 (int10_pal.cpp:465-474).
  - las tres fases: los indices del AC apuntan a entradas del DAC que NO son negras, o el
    modo CGA se veria en negro.
"""
import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
ORACLE = os.path.join(HERE, "CGAPAL.ORACULO.BIN")
PAKU = os.path.join(HERE, "CGAPAL.PAKU.BIN")

NDAC = 64
PHASE = 16 + NDAC * 3
TOTAL = PHASE * 3
FASES = ["modo 4", "tras AH=0B (paleta CGA 0)", "tras AH=10 (recolorear el registro 1)"]
ESPERADO_AC = {0: (0x13, 0x15, 0x17), 1: (0x12, 0x14, 0x16), 2: (0x12, 0x14, 0x16)}


def load(path, name):
    if not os.path.exists(path):
        sys.exit(f"ROJO: falta {name} ({path})")
    d = open(path, "rb").read()
    if len(d) != TOTAL:
        sys.exit(f"ROJO: {name} mide {len(d)} B, se esperaban {TOTAL}")
    return d


def split(d, f):
    b = d[f * PHASE:(f + 1) * PHASE]
    ac = list(b[:16])
    dac = [tuple(b[16 + i * 3:19 + i * 3]) for i in range(NDAC)]
    return ac, dac


def main():
    o = load(ORACLE, "captura del oraculo")
    p = load(PAKU, "captura de PakuPort")

    ok = True
    ac_ok = dac_ok = 0
    for f in range(3):
        ao, do = split(o, f)
        ap, dp = split(p, f)
        iguales_ac = sum(1 for i in range(16) if ao[i] == ap[i])
        iguales_dac = sum(1 for i in range(NDAC) if do[i] == dp[i])
        ac_ok += iguales_ac
        dac_ok += iguales_dac
        print(f"fase {f} ({FASES[f]}): AC {iguales_ac}/16, DAC {iguales_dac}/{NDAC}")
        if iguales_ac != 16:
            print("    oraculo:", " ".join(f"{b:02X}" for b in ao))
            print("    paku   :", " ".join(f"{b:02X}" for b in ap))
            ok = False
        if iguales_dac != NDAC:
            ok = False

        # aserciones semanticas por mundo
        for mundo, ac, dac in (("oraculo", ao, do), ("paku", ap, dp)):
            esp = ESPERADO_AC[f]
            if tuple(ac[1:4]) != esp:
                print(f"    ROJO [{mundo}] fase {f}: AC[1..3] = "
                      f"{tuple(f'{b:02X}' for b in ac[1:4])}, se esperaba "
                      f"{tuple(f'{b:02X}' for b in esp)}")
                ok = False
            negros = [i for i in ac[1:4] if dac[i & 0x3F] == (0, 0, 0)]
            if negros:
                print(f"    ROJO [{mundo}] fase {f}: el AC apunta a entradas NEGRAS del DAC "
                      f"({[f'{i:02X}' for i in negros]}): el modo CGA se veria en negro")
                ok = False

    print(f"total: AC {ac_ok}/48, DAC {dac_ok}/{NDAC * 3} — {'VERDE' if ok else 'ROJO'}")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
