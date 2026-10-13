#!/usr/bin/env python3
"""Juez del barrido de plataforma: diffea RESULT.BIN (oráculo DOSBox-X, TEST.COM)
contra result_paku.json (pakuport platform-test) según tests.json.

Veredicto por caso: VERDE si todos los regs de `check` coinciden y los bits de
`fmask` de FLAGS coinciden. Los casos con check=[] y fmask=0 son GRIS (grabados,
sin veredicto). Uso: python judge.py [-v]
"""
import json
import os
import struct
import sys

D = os.path.dirname(os.path.abspath(__file__))
REGS = ("ax", "bx", "cx", "dx", "si", "di", "es", "ds")


def main():
    verbose = "-v" in sys.argv
    meta = json.load(open(os.path.join(D, "tests.json")))
    tests = meta["tests"]
    rec = 20
    raw = open(os.path.join(D, "RESULT.BIN"), "rb").read()
    paku = {r["id"]: r for r in json.load(open(os.path.join(D, "result_paku.json")))}
    total = meta.get("total_result", len(tests) * rec)
    assert len(raw) == total, f"RESULT.BIN {len(raw)} != total_result {total}"

    verde, rojo, gris = [], [], []
    for i, tc in enumerate(tests):
        vals = struct.unpack_from("<10H", raw, i * rec)
        ora = dict(zip(REGS + ("flags", "marker"), vals))
        if ora["marker"] != i + 1:
            print(f"  !! {tc['id']}: marcador {ora['marker']} != {i+1} (COM desalineado)")
            rojo.append(tc["id"])
            continue
        pk = paku[tc["id"]]
        diffs = []
        for r in tc["check"]:
            if ora[r] != pk[r]:
                diffs.append(f"{r}: ora={ora[r]:04X} paku={pk[r]:04X}")
        fm = tc.get("fmask", 1)
        if fm and (ora["flags"] & fm) != (pk["flags"] & fm):
            diffs.append(f"flags&{fm:04X}: ora={ora['flags'] & fm:04X} paku={pk['flags'] & fm:04X}")
        # buffer comparable (round-trip de ficheros, state info...): oráculo en RESULT.BIN
        # [mbuf_rel : +nbytes], PakuPort en result_paku[id]["mbuf"] (hex).
        has_mbuf = "mbuf_rel" in tc
        if has_mbuf:
            nb = tc["mbuf_nbytes"]
            ora_buf = raw[tc["mbuf_rel"]: tc["mbuf_rel"] + nb]
            pk_buf = bytes.fromhex(pk.get("mbuf", ""))
            if ora_buf != pk_buf:
                diffs.append(f"mbuf: ora={ora_buf.hex()} paku={pk_buf.hex()}")
        if not tc["check"] and not fm and not has_mbuf:
            gris.append(tc["id"])
            if verbose:
                print(f"  GRIS  {tc['id']:24} ora={fmt(ora)}")
                print(f"        {'':24} pak={fmt(pk)}")
        elif diffs:
            rojo.append(tc["id"])
            print(f"  ROJO  {tc['id']:24} {'; '.join(diffs)}")
        else:
            verde.append(tc["id"])
            if verbose:
                print(f"  VERDE {tc['id']}")
    print(f"\n== barrido: VERDE={len(verde)} ROJO={len(rojo)} GRIS(grabado)={len(gris)} de {len(tests)} ==")
    return 1 if rojo else 0


def fmt(r):
    return " ".join(f"{k}={r[k]:04X}" for k in REGS + ("flags",))


if __name__ == "__main__":
    sys.exit(main())
