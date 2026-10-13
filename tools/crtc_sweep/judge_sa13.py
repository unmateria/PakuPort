#!/usr/bin/env python3
"""Juez del caso START ADDRESS en modo 13h (SA13.COM).

Compara lo que SE VE en los dos mundos: la captura del oraculo (DOSBox-X, captura
de pantalla a 640x400 = el modo 13h doblado) contra el volcado nativo
de PakuPort (PAKU_FB_PNG, 320x200).

Normalizacion de color, y por que hace falta
--------------------------------------------
El DAC del VGA es de 6 bits por componente y hay que estirarlos a 8. Cuando se escribio esta
red los dos mundos lo hacian distinto (PakuPort `v << 2`, max 252; DOSBox-X `(v*255)/63`, max
255), asi que el mismo indice salia (0,0,168) aqui y (0,0,170) alli. **Esa divergencia ya esta
cerrada** (VideoState.DacExpand, red tools/dac_expand: 64/64 bandas identicas al oraculo).
La normalizacion a 6 bits se mantiene igualmente: es gratis y deja a esta red midiendo SOLO el
layout, que es lo suyo, sin depender de como se presente el color.

Aserciones semanticas (para que una captura basura no cuele en verde)
---------------------------------------------------------------------
El COM pinta la fila n del color n y programa el start address a 100, o sea 400 pixeles
= una fila entera + 80. Con eso, en CADA fila de pantalla el color tiene que cambiar
exactamente una vez, en x=240, y de un valor al siguiente. Se exige en los DOS mundos por
separado; si el renderer ignorase el start address, la fila seria de un solo color.
"""
import os
import sys

from PIL import Image

HERE = os.path.dirname(os.path.abspath(__file__))
ORACLE = os.path.join(HERE, "SA13.ORACULO.png")
PAKU = os.path.join(HERE, "SA13.PAKU.png")
W, H = 320, 200
SPLIT_X = 240          # 400 px de start = 1 fila + 80 -> el corte cae en 320-80


def load6(path, name):
    if not os.path.exists(path):
        sys.exit(f"ROJO: falta {name} ({path})")
    im = Image.open(path).convert("RGB")
    if im.size != (W, H):
        im = im.resize((W, H), Image.NEAREST)   # el oraculo captura a 640x400
    px = im.load()
    return [[tuple(c >> 2 for c in px[x, y]) for x in range(W)] for y in range(H)]


def check_layout(img, world):
    """En cada fila: exactamente un cambio de color, y en x=SPLIT_X."""
    bad = []
    for y in range(H):
        cuts = [x for x in range(1, W) if img[y][x] != img[y][x - 1]]
        if cuts != [SPLIT_X]:
            bad.append((y, cuts[:4]))
    if bad:
        print(f"ROJO [{world}]: {len(bad)} filas sin el escalon en x={SPLIT_X} "
              f"(el renderer ignora el start address si la fila sale de un solo color)")
        for y, cuts in bad[:5]:
            print(f"    fila {y}: cortes en {cuts if cuts else '(ninguno)'}")
        return False
    return True


def main():
    o = load6(ORACLE, "captura del oraculo")
    p = load6(PAKU, "volcado de PakuPort")

    ok = True
    ok &= check_layout(o, "oraculo")
    ok &= check_layout(p, "paku")

    diff = [(x, y) for y in range(H) for x in range(W) if o[y][x] != p[y][x]]
    if diff:
        print(f"ROJO: {len(diff)} de {W * H} pixeles difieren entre mundos")
        for x, y in diff[:5]:
            print(f"    ({x},{y}) oraculo={o[y][x]} paku={p[y][x]}")
        ok = False
    else:
        print(f"VERDE: {W * H}/{W * H} pixeles identicos (a 6 bits) y el escalon del start "
              f"address cae en x={SPLIT_X} en los dos mundos")

    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
