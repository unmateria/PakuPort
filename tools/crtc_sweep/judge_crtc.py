#!/usr/bin/env python3
"""Juez del barrido CRTC: mide la BRECHA entre los registros CRTC que deja el set-mode del
oraculo (DOSBox-X) y los que deja el nuestro, con el MISMO binario (CRTC.COM) en los dos.

Entradas (por defecto en este directorio):
  modes.json         layout (lo emite gen_crtc.py)
  CRTC.ORACULO.BIN   verdad terreno capturada con oracle.conf (NO regenerar a la ligera)
  CRTC.PAKU.BIN      salida del export de PakuPort (copiada de export/crtc/CRTC.BIN)

Salida: por modo, cuantos de los 25 registros (r00..r18) coinciden y la lista de los que
difieren con nombre; resumen global; y el subconjunto de registros que nuestro RENDER ya
lee hoy (VideoState.cs), que es donde la brecha hace dano visible.

Criterio de veredicto (explicito):
  * Falta un fichero de entrada, o su tamano no casa con modes.json  -> ROJO (exit 1).
  * Sin baseline.json                                                -> ROJO (exit 1) con
    la instruccion de crearlo (`--baseline`): sin vara no hay juicio.
  * Alguna coincidencia GLOBAL o de ALGUN modo por DEBAJO del baseline -> ROJO (exit 1).
  * Igual o mejor que el baseline                                    -> VERDE (exit 0).
El veredicto NO exige brecha cero: hoy se ESPERA brecha grande (nuestro set-mode no
programa el CRTC). El juez sirve como anti-regresion desde ya y como vara del port.

Uso:
  python judge_crtc.py --baseline    # congela el estado actual como vara
  python judge_crtc.py [-v]          # juzga contra la vara
"""
import hashlib
import json
import os
import sys

D = os.path.dirname(os.path.abspath(__file__))

# Nombres verbatim de DOSBox-X src/hardware/vga_crtc.cpp (comentarios de los case 0x00..0x18
# en vga_write_p3d5 / vga_read_p3d5x).
REG_NAMES = [
    "horizontal_total", "horizontal_display_end", "start_horizontal_blanking",
    "end_horizontal_blanking", "start_horizontal_retrace", "end_horizontal_retrace",
    "vertical_total", "overflow", "preset_row_scan", "maximum_scan_line",
    "cursor_start", "cursor_end", "start_address_high", "start_address_low",
    "cursor_location_high", "cursor_location_low", "vertical_retrace_start",
    "vertical_retrace_end", "vertical_display_end", "offset", "underline_location",
    "start_vertical_blanking", "end_vertical_blanking", "mode_control", "line_compare",
]

# Registros que el render de PakuPort YA consulta hoy. Verificado uno a uno en
# src/PakuPort.Core/Platform/VideoState.cs:56 (ActiveHeight <- r15/r07/r09) y :492-493
# (start address <- r0C/r0D; line compare <- r18/r07/r09). Si estos divergen, la pantalla
# sale mal en cuanto un juego haga read-modify-write en vez de escribir el valor completo.
# OJO: r13 (offset/stride) NO entra: su unica aparicion en src/ es la linea de traza de
# VideoState.cs:498 (dentro de PAKU_LOG_CRTC==1); el bucle de dibujo usa w/8 fijo. Meterlo
# aqui inflaba la brecha "critica" con un registro que hoy nadie lee.
RENDER_CRITICAL = {0x07: "overflow (bits altos de vbstart/line compare)",
                   0x09: "maximum_scan_line (bits altos de vbstart/line compare)",
                   0x0C: "start_address_high", 0x0D: "start_address_low",
                   0x15: "start_vertical_blanking",
                   0x18: "line_compare"}


def load(meta, path, label):
    if not os.path.exists(path):
        print(f"ROJO: falta {label}: {path}")
        return None
    raw = open(path, "rb").read()
    if len(raw) != meta["total"]:
        print(f"ROJO: {label} mide {len(raw)} B, modes.json dice {meta['total']} B")
        return None
    return raw


def check_oracle_provenance(baseline, ora_bytes):
    """La verdad terreno depende de la MAQUINA con que se capturo (machine=svga_s3): una
    captura con machine=ega tiene otros valores y el juez la tragaria como buena, midiendo
    contra una verdad falsa. El baseline sella el md5 del oraculo con el que se congelo."""
    md5 = hashlib.md5(ora_bytes).hexdigest()
    sealed = baseline.get("oraculo_md5")
    if sealed is None:
        return md5, "baseline antiguo sin sello de oraculo (recongelar con --baseline)"
    if sealed != md5:
        return md5, (f"EL ORACULO HA CAMBIADO: md5 {md5} != {sealed} sellado en baseline.json. "
                     "La vara y la verdad terreno no casan; recaptura con oracle.conf "
                     "(machine=svga_s3) o recongela a conciencia con --baseline.")
    return md5, None


def main():
    verbose = "-v" in sys.argv
    make_baseline = "--baseline" in sys.argv
    meta = json.load(open(os.path.join(D, "modes.json")))
    rec, nreg = meta["record_bytes"], meta["nreg"]
    ora = load(meta, os.path.join(D, "CRTC.ORACULO.BIN"), "CRTC.ORACULO.BIN (oraculo)")
    pak = load(meta, os.path.join(D, "CRTC.PAKU.BIN"), "CRTC.PAKU.BIN (PakuPort)")
    if ora is None or pak is None:
        return 1

    per_mode, tot_ok, tot_n = {}, 0, 0
    crit_ok = crit_n = 0
    # Conjunto EXACTO de (modo, registro) que coinciden. El recuento por si solo no ve las
    # permutaciones: arreglar un registro y romper otro deja el marcador igual.
    matched = set()
    misc_bad = []
    print(f"{'modo':>5} {'base':>5} {'coinciden':>10}  divergen (r idx: oraculo -> paku, nombre)")
    for e in meta["modes"]:
        o = ora[e["off"]: e["off"] + rec]
        p = pak[e["off"]: e["off"] + rec]
        diffs = [(i, o[i], p[i]) for i in range(nreg) if o[i] != p[i]]
        ok = nreg - len(diffs)
        per_mode[f"{e['mode']:02X}"] = ok
        tot_ok += ok
        tot_n += nreg
        for i in range(nreg):
            if o[i] == p[i]:
                matched.add(f"{e['mode']:02X}:{i:02X}")
        for i in RENDER_CRITICAL:
            crit_n += 1
            crit_ok += 1 if o[i] == p[i] else 0
        if o[nreg] != p[nreg]:
            misc_bad.append(f"{e['mode']:02X}h:{o[nreg]:02X}->{p[nreg]:02X}")
        misc = "" if o[nreg] == p[nreg] else f"  [miscout 3CCh: {o[nreg]:02X} -> {p[nreg]:02X}]"
        print(f"  {e['mode']:02X}h {e['crtc_base']:03X}h {ok:>6}/{nreg}{misc}")
        if diffs and verbose:
            for i, ov, pv in diffs:
                print(f"          r{i:02X}: {ov:02X} -> {pv:02X}   {REG_NAMES[i]}")
        elif diffs:
            print("          " + ", ".join(f"r{i:02X}:{ov:02X}->{pv:02X}" for i, ov, pv in diffs))

    gap = 100.0 * (tot_n - tot_ok) / tot_n
    print(f"\nGLOBAL: {tot_ok}/{tot_n} registros coinciden  ->  BRECHA {gap:.1f} %")
    print(f"CRITICOS PARA EL RENDER ({len(RENDER_CRITICAL)} regs x {len(meta['modes'])} modos): "
          f"{crit_ok}/{crit_n} coinciden -> brecha {100.0*(crit_n-crit_ok)/crit_n:.1f} %")
    for i, why in sorted(RENDER_CRITICAL.items()):
        n_bad = sum(1 for e in meta["modes"]
                    if ora[e["off"] + i] != pak[e["off"] + i])
        print(f"   r{i:02X} {why:<48} mal en {n_bad}/{len(meta['modes'])} modos")

    # Misc Output (3CCh): no es CRTC, pero el barrido lo captura y desde el fix de los modos
    # mono (07h/0Fh, int10_modes.cpp:1552) coincide en los 15 -> vale como anti-regresion.
    print(f"MISC OUTPUT (3CCh): {len(meta['modes']) - len(misc_bad)}/{len(meta['modes'])} modos coinciden"
          + ("" if not misc_bad else "  DIVERGEN: " + ", ".join(misc_bad)))

    bpath = os.path.join(D, "baseline.json")
    cur = {"global_ok": tot_ok, "total": tot_n, "per_mode": per_mode,
           "oraculo_md5": hashlib.md5(ora).hexdigest(),
           "misc_ok": len(meta["modes"]) - len(misc_bad),
           "matched": sorted(matched)}
    if make_baseline:
        json.dump(cur, open(bpath, "w"), indent=1)
        print(f"\nbaseline.json escrito: {tot_ok}/{tot_n} coincidencias congeladas como vara "
              f"(oraculo md5 {cur['oraculo_md5'][:12]}...).")
        return 0
    if not os.path.exists(bpath):
        print("\nROJO: no hay baseline.json. Crealo con `python judge_crtc.py --baseline`.")
        return 1
    base = json.load(open(bpath))
    _, prov_err = check_oracle_provenance(base, ora)
    if prov_err:
        print(f"\nROJO: {prov_err}")
        return 1
    regress = []
    if tot_ok < base["global_ok"]:
        regress.append(f"global {tot_ok} < {base['global_ok']}")
    for k, v in per_mode.items():
        bv = base["per_mode"].get(k)
        if bv is not None and v < bv:
            regress.append(f"modo {k}h {v} < {bv}")
    # Permutaciones: un (modo,registro) que coincidia y ha dejado de coincidir es regresion
    # aunque el recuento total no baje.
    lost = sorted(set(base.get("matched", [])) - matched)
    if lost:
        regress.append(f"{len(lost)} (modo,reg) que casaban ya no casan: " + ", ".join(lost[:8])
                       + ("..." if len(lost) > 8 else ""))
    if base.get("misc_ok") is not None and (len(meta["modes"]) - len(misc_bad)) < base["misc_ok"]:
        regress.append(f"misc output {len(meta['modes']) - len(misc_bad)} < {base['misc_ok']}")
    if regress:
        print("\nROJO (regresion vs baseline): " + "; ".join(regress))
        return 1
    delta = tot_ok - base["global_ok"]
    print(f"\nVERDE: sin regresion vs baseline ({tot_ok} vs {base['global_ok']}, "
          f"{'+' if delta >= 0 else ''}{delta}). "
          f"NOTA: verde NO significa brecha cero - la brecha medida es {gap:.1f} %.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
