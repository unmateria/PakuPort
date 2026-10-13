#!/usr/bin/env python3
"""Juez del caso del CANDADO del CRTC (write protect r11 bit 7). Ver gen_lock.py.

Entradas: LOCK.ORACULO.BIN (verdad terreno, capturada con lock.conf) y LOCK.PAKU.BIN
(salida del export de PakuPort). Layout: A[25] + B[8] + C + D = 35 bytes.

Veredicto:
  * falta un fichero o no mide 35 B                          -> ROJO exit 1
  * en CUALQUIERA de los dos mundos falla una asercion:
      - B[i] != A[i] para algun i de 0..7  (el candado NO ignora la escritura)
      - C != A[0] XOR 55h                  (con el candado abierto NO escribe)
      - D != A[11h]                        (r11 no se relee como se escribio)
      - A[11h] sin bit 7                   (el set-mode no dejo el candado puesto)
                                                             -> ROJO exit 1
  * los 35 bytes no son identicos entre mundos               -> ROJO exit 1
  * todo lo demas                                            -> VERDE exit 0

Sin baseline: las aserciones son absolutas (semantica del hardware), y el byte-exacto
entre mundos es la vara. Una captura basura no puede colar: fallaria las aserciones.
"""
import os
import sys

D = os.path.dirname(os.path.abspath(__file__))
TOTAL = 35


def load(name):
    path = os.path.join(D, name)
    if not os.path.exists(path):
        print(f"ROJO: falta {name}")
        return None
    raw = open(path, "rb").read()
    if len(raw) != TOTAL:
        print(f"ROJO: {name} mide {len(raw)} B, esperados {TOTAL}")
        return None
    return raw


def check_world(label, raw):
    a, b, c, d = raw[0:25], raw[25:33], raw[33], raw[34]
    ok = True
    if not (a[0x11] & 0x80):
        print(f"ROJO [{label}]: el set-mode no dejo el candado puesto (r11={a[0x11]:02X})")
        ok = False
    for i in range(8):
        if b[i] != a[i]:
            print(f"ROJO [{label}]: escritura CANDADA a r{i:02X} colo: {a[i]:02X} -> {b[i]:02X}")
            ok = False
    if c != (a[0] ^ 0x55):
        print(f"ROJO [{label}]: con el candado ABIERTO r00 no se escribio: "
              f"esperado {a[0] ^ 0x55:02X}, leido {c:02X}")
        ok = False
    if d != a[0x11]:
        print(f"ROJO [{label}]: r11 no se relee como se escribio: {a[0x11]:02X} -> {d:02X}")
        ok = False
    if ok:
        print(f"  [{label}] r11={a[0x11]:02X} (candado puesto), 8/8 escrituras candadas "
              f"ignoradas, unlock escribe ({a[0]:02X}->{c:02X}), relock OK")
    return ok


def main():
    ora = load("LOCK.ORACULO.BIN")
    pak = load("LOCK.PAKU.BIN")
    if ora is None or pak is None:
        return 1
    ok = check_world("oraculo", ora) & check_world("paku", pak)
    if ora != pak:
        diffs = [i for i in range(TOTAL) if ora[i] != pak[i]]
        print("ROJO: los mundos divergen en los offsets " +
              ", ".join(f"{i} ({ora[i]:02X}->{pak[i]:02X})" for i in diffs))
        ok = False
    if not ok:
        return 1
    print("\nVERDE: candado de escritura del CRTC identico al oraculo (35/35 bytes).")
    return 0


if __name__ == "__main__":
    sys.exit(main())
