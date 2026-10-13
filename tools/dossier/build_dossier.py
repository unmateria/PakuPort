# -*- coding: utf-8 -*-
"""Dossier del juego: agrega TODA la telemetria offline que PakuPort ya sabe volcar.

Genera games/<id>/dossier.md (legible por humano o IA) y games/<id>/dossier.json
(la misma informacion estructurada). Objetivo: que una IA entienda el juego
-que es, como pinta, como suena, que ficheros pide, que rutinas dibujan- SIN
abrir Ghidra ni un desensamblador.

No toca src/ ni ejecuta nada: solo lee artefactos ya producidos. Si falta una
fuente, la seccion sale como "sin datos" con la receta exacta de captura.


RECETA DE CAPTURA COMPLETA
==========================

Todo se captura corriendo el export del juego (export/<id>) con flags PAKU_*.
Para runs no interactivos usa PAKU_AUTOEXIT_MS (ademas oculta la ventana, no
roba el foco) y PAKU_AUTOKEYS para atravesar menus ('~' = pausa ~1 s).

  1) Blits + assets visuales (secciones "Assets" y "Rutinas de dibujo")

     cd export\\<id>
     set PAKU_LOG_BLIT=2
     set PAKU_AUTOEXIT_MS=90000
     set PAKU_AUTOKEYS=~~ ~ ~ ~ ~
     bin\\Release\\net8.0\\<id>.exe
       -> logs\\blit.log     (rectangulos movs/stos, nivel 1)
       -> logs\\blitraw.bin  (escrituras sueltas a VRAM, nivel 2)

     python tools\\blit_catalog\\build_catalog.py --logs export\\<id>\\logs ^
            --out games\\<id>\\catalog
     python tools\\blit_catalog\\build_tracks.py games\\<id>\\catalog

  2) Sonidos (seccion "Sonidos")

     set PAKU_LOG_AUDIO_EV=1        -> logs\\audio-events.log
     python tools\\audio_catalog\\build_sounds.py ^
            --log export\\<id>\\logs\\audio-events.log --out games\\<id>

  3) Mapa de ficheros + superficie de plataforma (secciones 4 y 5)

     set PAKU_LOG_INT21=1           (llamadas DOS, aperturas de fichero, EXEC)
     set PAKU_LOG_IVT=1             (quien engancha que vector)
     bin\\Release\\net8.0\\<id>.exe 2> ..\\..\\logs\\dossier-<id>-run.err

     Ambos escriben a stderr: UNA sola captura alimenta las dos secciones.
     Pasa el fichero con --int21-log (acepta varios).

  4) Opcionales que enriquecen el dossier

     PAKU_LOG_OPL=1 / PAKU_LOG_SPK=1 / PAKU_LOG_SB=1   detalle de audio
     PAKU_LOG_INT33=1                                  raton
     PAKU_SAMPLE_PC=1                                  anillo de PCs al salir
     PAKU_TAPE_RECORD=<fich>                           partida reproducible


USO
===

  python tools\\dossier\\build_dossier.py <id>
  python tools\\dossier\\build_dossier.py <id> --top 10
  python tools\\dossier\\build_dossier.py <id> --int21-log logs\\dossier-<id>-run.err

Sin argumentos de rutas, busca por convencion:
  games/<id>/game.json, games/<id>/codemap.json, games/<id>/catalog/,
  games/<id>/sounds.json, export/<id>/logs/{blit.log,blitraw.bin},
  logs/dossier-<id>-run.err
"""
import argparse
import collections
import hashlib
import json
import os
import struct
import sys

class Clock:
    """Traduce ticks a tiempo SOLO si hay calibracion; si no, no inventa segundos.

    `Cpu.TickCount` cuenta PASOS del emulador (una instruccion = un paso). El
    equivalente "1 paso = 1 tick del PIT = 0,838 us" solo vale cuando el governor
    corre calibrado (`cyclesPerMs` horneado en game.json o `PAKU_CYCLES`). A
    velocidad libre (`cyc=0` en los latidos [HB]) el eje de ticks es cuenta de
    instrucciones y NO es tiempo: en ese caso este dossier publica ticks a secas.
    """

    def __init__(self, cycles_per_ms=None):
        self.cycles_per_ms = cycles_per_ms
        self.fuente = (f"governor calibrado, cyclesPerMs={cycles_per_ms}"
                       if cycles_per_ms else None)

    @property
    def calibrado(self):
        return bool(self.cycles_per_ms)

    def seconds(self, ticks):
        if not self.calibrado or ticks is None:
            return None
        return round(ticks / (self.cycles_per_ms * 1000.0), 2)

    def label(self):
        return "s" if self.calibrado else "ticks"

    def value(self, ticks):
        """Valor para la columna de tiempo: segundos si hay calibracion, ticks si no."""
        return self.seconds(ticks) if self.calibrado else ticks

VIDEO_MODES = {
    0x00: "texto 40x25 mono", 0x01: "texto 40x25 color", 0x02: "texto 80x25 mono",
    0x03: "texto 80x25 color", 0x04: "CGA 320x200 4 colores", 0x05: "CGA 320x200 4 colores (gris)",
    0x06: "CGA 640x200 mono", 0x07: "texto 80x25 MDA", 0x0D: "EGA 320x200 16 colores",
    0x0E: "EGA 640x200 16 colores", 0x0F: "EGA 640x350 mono", 0x10: "EGA 640x350 16 colores",
    0x11: "VGA 640x480 mono", 0x12: "VGA 640x480 16 colores", 0x13: "MCGA 320x200 256 colores",
}

# INTs que un juego DOS toca de verdad; el resto se lista tal cual.
INT_ROLES = {
    0x10: "BIOS video", 0x11: "BIOS lista de equipo", 0x12: "BIOS memoria",
    0x13: "BIOS disco", 0x14: "BIOS serie", 0x15: "BIOS servicios varios",
    0x16: "BIOS teclado", 0x17: "BIOS impresora", 0x1A: "BIOS reloj",
    0x1C: "tick de usuario", 0x20: "DOS terminar", 0x21: "DOS",
    0x25: "DOS lectura absoluta", 0x26: "DOS escritura absoluta", 0x27: "DOS TSR",
    0x2F: "multiplex", 0x31: "DPMI", 0x33: "raton", 0x67: "EMS",
}

INT21_AH = {
    0x00: "terminar", 0x01: "leer tecla con eco", 0x02: "escribir caracter",
    0x06: "E/S de consola directa", 0x07: "leer tecla sin eco", 0x08: "leer tecla sin eco",
    0x09: "escribir cadena $", 0x0B: "estado de teclado", 0x0C: "vaciar y leer",
    0x0E: "seleccionar unidad", 0x19: "unidad actual", 0x1A: "fijar DTA",
    0x25: "fijar vector", 0x2A: "fecha", 0x2C: "hora", 0x2F: "leer DTA",
    0x30: "version de DOS", 0x33: "estado de break", 0x35: "leer vector",
    0x36: "espacio libre en disco", 0x39: "crear directorio", 0x3B: "cambiar directorio",
    0x3C: "crear fichero", 0x3D: "abrir fichero", 0x3E: "cerrar fichero",
    0x3F: "leer fichero", 0x40: "escribir fichero", 0x41: "borrar fichero",
    0x42: "posicionar (seek)", 0x43: "atributos de fichero", 0x44: "IOCTL",
    0x47: "directorio actual", 0x48: "reservar memoria", 0x49: "liberar memoria",
    0x4A: "redimensionar memoria", 0x4B: "ejecutar programa (EXEC)",
    0x4C: "terminar con codigo", 0x4E: "buscar primero", 0x4F: "buscar siguiente",
    0x56: "renombrar", 0x57: "fecha/hora de fichero",
}


# ---------------------------------------------------------------- utilidades

def read_json(path):
    if not path or not os.path.exists(path):
        return None
    try:
        with open(path, "r", encoding="utf-8") as f:
            return json.load(f)
    except Exception as exc:
        print(f"  aviso: {path} ilegible ({exc})", file=sys.stderr)
        return None


def first_existing(*paths):
    for p in paths:
        if p and os.path.exists(p):
            return p
    return None


def human_bytes(n):
    if n is None:
        return "?"
    if n < 1024:
        return f"{n} B"
    if n < 1024 * 1024:
        return f"{n / 1024:.1f} KB"
    return f"{n / (1024 * 1024):.1f} MB"


# ---------------------------------------------------------------- 1. identidad

PACKER_SIGS = [
    (b"LZ91", "LZEXE 0.91"), (b"LZ09", "LZEXE 0.90"),
    (b"PKLITE", "PKLITE"), (b"RB\x00", "EXEPACK"),
    (b"diet", "DIET"), (b"UPX!", "UPX"),
]


def scan_exe(path):
    """Identidad del binario: tamano, hashes, cabecera MZ, packer, censo de INTs."""
    info = {"path": path, "exists": bool(path and os.path.exists(path))}
    if not info["exists"]:
        return info
    with open(path, "rb") as f:
        data = f.read()
    info["size"] = len(data)
    info["sha1"] = hashlib.sha1(data).hexdigest()
    info["md5"] = hashlib.md5(data).hexdigest()
    info["kind"] = "MZ (EXE)" if data[:2] in (b"MZ", b"ZM") else "COM (imagen plana)"
    if data[:2] in (b"MZ", b"ZM") and len(data) >= 0x20:
        (lastp, pages, relocs, hdrparas, minalloc, maxalloc,
         ss, sp, _csum, ip, cs) = struct.unpack_from("<HHHHHHHHHHH", data, 2)
        info["mz"] = {
            "paginas": pages, "bytes_pagina_final": lastp,
            "reubicaciones": relocs, "cabecera_paras": hdrparas,
            "minalloc_paras": minalloc, "maxalloc_paras": maxalloc,
            "entry": f"{cs:04X}:{ip:04X}", "stack": f"{ss:04X}:{sp:04X}",
            "imagen_bytes": (pages - 1) * 512 + (lastp or 512) - hdrparas * 16,
        }
    packers = sorted({name for sig, name in PACKER_SIGS if sig in data[:0x2000] or sig in data})
    info["packer"] = packers or None
    # Censo ESTATICO de INTs: patron de bytes CD xx. Orientativo (puede colar
    # falsos positivos en datos, y no ve nada util en un binario empaquetado).
    census = collections.Counter()
    for i in range(len(data) - 1):
        if data[i] == 0xCD:
            census[data[i + 1]] += 1
    info["int_estatico"] = {f"{k:02X}": v for k, v in census.most_common()}
    return info


def summarize_codemap(cm):
    """codemap.json = direcciones lineales ejecutadas -> cobertura en rangos."""
    if not cm or "code" not in cm:
        return None
    addrs = sorted(set(cm["code"]))
    if not addrs:
        return None
    ranges, start, prev = [], addrs[0], addrs[0]
    for a in addrs[1:]:
        if a != prev + 1:
            ranges.append((start, prev))
            start = a
        prev = a
    ranges.append((start, prev))
    return {
        "bytes_ejecutados": len(addrs),
        "rangos": len(ranges),
        "mayores": [{"desde": f"{a:05X}", "hasta": f"{b:05X}", "bytes": b - a + 1}
                    for a, b in sorted(ranges, key=lambda r: r[0] - r[1])[:8]],
    }


# ---------------------------------------------------------------- 2. assets

def summarize_assets(catalog_dir, top, clock, png_prefix=""):
    if not catalog_dir:
        return None
    cat = read_json(os.path.join(catalog_dir, "assets.json"))
    if not cat:
        return None
    assets = cat.get("assets", [])
    # instances.jsonl da el "cuando" de cada asset (primera y postrera aparicion).
    firstlast = {}
    inst_path = os.path.join(catalog_dir, "instances.jsonl")
    n_inst = 0
    if os.path.exists(inst_path):
        with open(inst_path, "r", encoding="utf-8") as f:
            for line in f:
                try:
                    ins = json.loads(line)
                except ValueError:
                    continue
                n_inst += 1
                a, t = ins.get("asset"), ins.get("t", 0)
                fl = firstlast.get(a)
                if fl is None:
                    firstlast[a] = [t, t]
                else:
                    fl[0] = min(fl[0], t)
                    fl[1] = max(fl[1], t)
    rows = []
    for a in sorted(assets, key=lambda a: -a.get("appearances", 0))[:top]:
        fl = firstlast.get(a["id"])
        blank = all(ch == "." for row in a.get("pixels", []) for ch in row)
        rows.append({
            "id": a["id"], "w": a.get("w"), "h": a.get("h"), "mode": a.get("mode"),
            "appearances": a.get("appearances"), "at": a.get("at"),
            "png": (png_prefix + a["png"]) if a.get("png") else None, "borrador": blank,
            "primera": clock.value(fl[0]) if fl else None,
            "final": clock.value(fl[1]) if fl else None,
        })
    modes = collections.Counter(a.get("mode") for a in assets)
    return {
        "n_assets": len(assets), "n_instancias": n_inst,
        "modos": {str(k): v for k, v in modes.items()},
        "sin_procedencia": sum(1 for a in assets if a.get("at") in (None, "0000:0000")),
        "top": rows,
        "skipped_writes_by_mode": cat.get("skipped_writes_by_mode") or {},
    }


def summarize_tracks(catalog_dir, top, clock):
    if not catalog_dir:
        return None
    doc = read_json(os.path.join(catalog_dir, "tracks.json"))
    if not doc:
        return None
    tracks = doc.get("tracks", [])
    rows, animations = [], []
    for tr in sorted(tracks, key=lambda t: -t.get("n", 0))[:top]:
        vx, vy = tr.get("vx", 0.0), tr.get("vy", 0.0)
        span_x, span_y = tr.get("span_x", 0), tr.get("span_y", 0)
        if span_x <= 4 and span_y <= 4:
            clase = "estatico"
        elif abs(vx) >= 2 * abs(vy):
            clase = "horizontal " + ("izq" if vx < 0 else "dcha")
        elif abs(vy) >= 2 * abs(vx):
            clase = "vertical " + ("arriba" if vy < 0 else "abajo")
        else:
            clase = "diagonal"
        rows.append({
            "id": tr.get("id"), "clase": clase, "n": tr.get("n"),
            "frames": len(tr.get("assets", [])),
            "desde": [tr.get("x0"), tr.get("y0")], "hasta": [tr.get("x1"), tr.get("y1")],
            "vx_px_por_1000_ticks": round(vx, 2), "vy_px_por_1000_ticks": round(vy, 2),
            "vida": clock.value(tr.get("death", 0) - tr.get("birth", 0)),
            "nace": clock.value(tr.get("birth", 0)),
            "continuidad": round(tr.get("continuity", 0.0), 3),
        })
    for tr in tracks:
        assets = tr.get("assets", [])
        if len(assets) > 1:
            animations.append({"track": tr.get("id"), "frames": len(assets),
                               "assets": assets[:12]})
    animations.sort(key=lambda a: -a["frames"])
    clases = collections.Counter(r["clase"].split()[0] for r in rows)
    return {"n_tracks": len(tracks), "top": rows,
            "animaciones": animations[:top], "clases_top": dict(clases)}


# ---------------------------------------------------------------- 3. sonidos

def summarize_sounds(path, top, clock):
    doc = read_json(path)
    if not doc:
        return None
    sounds = doc.get("sounds", [])
    rows = []
    for s in sounds[:top]:
        rows.append({
            "shape": s.get("shape"), "src": s.get("src"), "count": s.get("count"),
            "avg_ms": s.get("avg_ms"), "min_ms": s.get("min_ms"), "max_ms": s.get("max_ms"),
            "items": s.get("items"), "first_pc": s.get("first_pc"),
            "pcs": s.get("pcs", []), "detail": (s.get("detail") or "")[:80],
            "primera": clock.value(s.get("first_t", 0)),
        })
    return {"n_sonidos": len(sounds), "eventos": doc.get("events"),
            "spk": doc.get("spk"), "opl": doc.get("opl"), "top": rows}


# ---------------------------------------------- 4-5. log INT21 / IVT / heartbeat

def parse_run_logs(paths):
    """Parsea capturas de stderr con lineas [int21], [IVT], [HB], [SAMPLE_PC].

    El log de INT21 no lleva sello de tiempo: el "cuando" se expresa como el
    ordinal de la llamada DOS (llamada n de N), que es un proxy estable.
    """
    if not paths:
        return None
    out = {
        "fuentes": [], "n_lineas": 0, "int21_llamadas": 0,
        "ah": collections.Counter(), "ficheros": {}, "exec": [],
        "ivt": [], "hb": [], "sample_pc": [], "alloc_fail": [], "gaps": [], "otros": [],
    }
    for path in paths:
        if not os.path.exists(path):
            continue
        out["fuentes"].append(path)
        with open(path, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.rstrip("\n")
                out["n_lineas"] += 1
                if line.startswith("[int21] AH="):
                    fields = dict(tok.split("=", 1) for tok in line[8:].split() if "=" in tok)
                    ah = fields.get("AH", "??")
                    al = fields.get("AL", "??")
                    out["int21_llamadas"] += 1
                    key = f"{ah}/{al}" if ah in ("4B", "44", "3D", "0E") else ah
                    out["ah"][key] += 1
                elif line.startswith("[int21] 3D open"):
                    name = line.split("'", 2)[1] if "'" in line else "?"
                    found = "NOT FOUND" not in line
                    e = out["ficheros"].setdefault(name, {
                        "nombre": name, "aperturas": 0, "fallos": 0,
                        "primera_llamada": out["int21_llamadas"], "cs": set(), "ruta": None,
                    })
                    e["aperturas"] += 1
                    if not found:
                        e["fallos"] += 1
                        if "(" in line:
                            e["ruta"] = line.split("(", 1)[1].split(")")[0]
                    if "CS=" in line:
                        e["cs"].add(line.rsplit("CS=", 1)[1].strip(" )"))
                elif line.startswith("[int21] 4B00 EXEC child="):
                    out["exec"].append(line.split("child=", 1)[1])
                elif "ALLOC FAIL" in line:
                    out["alloc_fail"].append(line)
                elif line.startswith("[IVT] set"):
                    parts = line.split()
                    # [IVT] set 08 = SSSS:OOOO (antes SSSS:OOOO) desde CCCC:IIII
                    out["ivt"].append({
                        "int": parts[2], "nuevo": parts[4],
                        "antes": parts[6].strip("()") if len(parts) > 6 else "?",
                        "desde": parts[-1],
                    })
                elif line.startswith("[HB]"):
                    out["hb"].append(dict(tok.split("=", 1)
                                          for tok in line[5:].split() if "=" in tok))
                elif line.startswith("[SAMPLE_PC]"):
                    out["sample_pc"].append(line)
                elif line.startswith("[GAP]"):
                    out["gaps"].append(line[6:])
                elif line.startswith("[int21]"):
                    out["otros"].append(line)
    for e in out["ficheros"].values():
        e["cs"] = sorted(e["cs"])
    out["ah"] = dict(out["ah"])
    # Vectores enganchados: ultimo dueno de cada INT + quien lo engancho.
    hooks = {}
    for h in out["ivt"]:
        hooks[h["int"]] = h
    out["hooks"] = hooks
    return out


# ---------------------------------------------------------------- 6. rutinas

def read_blitraw(path):
    """Devuelve (contador de at, contador de modo, n_escrituras, bbox_lineal, ticks)."""
    with open(path, "rb") as f:
        data = f.read()
    if data[:4] != b"PBR1":
        return None
    at_count = collections.Counter()
    mode_count = collections.Counter()
    i, t, mode, n = 4, 0, -1, 0
    lo, hi = None, None
    plo, phi = None, None   # planar 'P' records carry PLANE addresses, not CPU linear ones
    at_mode = collections.defaultdict(collections.Counter)
    while i < len(data):
        k = data[i]
        if k == 0x4D:
            mode = data[i + 1]
            mode_count[mode] += 0
            i += 2
        elif k == 0x57:
            lin, = struct.unpack_from("<I", data, i + 1)
            at, dt = struct.unpack_from("<II", data, i + 6)
            t += dt
            at_count[at] += 1
            mode_count[mode] += 1
            at_mode[at][mode] += 1
            lo = lin if lo is None else min(lo, lin)
            hi = lin if hi is None else max(hi, lin)
            n += 1
            i += 14
        elif k == 0x50:
            # 'P' = planar write (EGA/VGA modes 0Dh-12h), 17 B: u32 plane addr, 4x u8 plane
            # bytes, u32 at, u32 dt. Skipping it silently reported "0 escrituras sueltas"
            # AND a false "el juego corrio LIFTEADO" for every planar game.
            # `addr` is a PLANE address, not a CPU linear one: kept apart from lin_min/max.
            addr, = struct.unpack_from("<I", data, i + 1)
            at, dt = struct.unpack_from("<II", data, i + 9)
            t += dt
            at_count[at] += 1
            mode_count[mode] += 1
            at_mode[at][mode] += 1
            plo = addr if plo is None else min(plo, addr)
            phi = addr if phi is None else max(phi, addr)
            n += 1
            i += 17
        else:
            break
    return {"at": at_count, "modo": mode_count, "n": n,
            "lin_min": lo, "lin_max": hi, "ticks": t, "at_mode": at_mode,
            "plane_min": plo, "plane_max": phi}


def parse_blit_log(path):
    """Rectangulos movs/stos: at -> conteo, bytes, operacion."""
    rows = collections.defaultdict(lambda: {"n": 0, "bytes": 0, "ops": collections.Counter(),
                                            "modos": collections.Counter()})
    total = 0
    with open(path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            if not line.startswith("t="):
                continue
            fields = dict(tok.split("=", 1) for tok in line.split() if "=" in tok)
            at = fields.get("at", "?")
            r = rows[at]
            r["n"] += 1
            total += 1
            try:
                r["bytes"] += int(fields.get("bytes", 0))
            except ValueError:
                pass
            r["ops"][fields.get("op", "?")] += 1
            r["modos"][fields.get("mode", "?")] += 1
    return {"total": total, "rows": rows}


def summarize_routines(blitraw_path, blitlog_path, top, clock):
    out = {"blitraw": None, "blitlog": None}
    if blitraw_path and os.path.exists(blitraw_path):
        raw = read_blitraw(blitraw_path)
        if raw:
            rows = []
            for at, cnt in raw["at"].most_common(top):
                modos = ",".join(f"{m:02X}" for m in raw["at_mode"][at])
                rows.append({"at": f"{at >> 16:04X}:{at & 0xFFFF:04X}",
                             "escrituras": cnt,
                             "pct": round(100.0 * cnt / max(1, raw["n"]), 1),
                             "modos": modos})
            out["blitraw"] = {
                "fuente": blitraw_path, "escrituras": raw["n"],
                "duracion": clock.value(raw["ticks"]), "duracion_unidad": clock.label(),
                "modos": {f"0x{m:02X}": c for m, c in raw["modo"].items() if m >= 0},
                "rango_lineal": (f"{raw['lin_min']:05X}-{raw['lin_max']:05X}"
                                 if raw["lin_min"] is not None else None),
                "rango_plano": (f"{raw['plane_min']:05X}-{raw['plane_max']:05X}"
                                if raw.get("plane_min") is not None else None),
                "rutinas": rows,
                # bool(rows) first: all([]) is True, so an empty table used to print the
                # "el juego corrio LIFTEADO" warning as if provenance had been lost.
                "sin_procedencia": bool(rows) and all(r["at"] == "0000:0000" for r in rows),
            }
    if blitlog_path and os.path.exists(blitlog_path):
        log = parse_blit_log(blitlog_path)
        if log["total"]:
            rows = []
            for at, r in sorted(log["rows"].items(), key=lambda kv: -kv[1]["n"])[:top]:
                rows.append({"at": at, "rectangulos": r["n"], "bytes": r["bytes"],
                             "op": r["ops"].most_common(1)[0][0],
                             "modo": r["modos"].most_common(1)[0][0]})
            out["blitlog"] = {"fuente": blitlog_path, "rectangulos": log["total"],
                              "rutinas": rows}
    return out if (out["blitraw"] or out["blitlog"]) else None


# ---------------------------------------------------------------- 7. resumen

def build_summary(d):
    """Resumen ejecutivo de ~10 lineas por REGLAS (nada de IA)."""
    g = d["identidad"]["game_json"] or {}
    exe = d["identidad"]["exe"]
    lines = []

    title = g.get("title") or d["id"]
    kind = exe.get("kind", "?") if exe.get("exists") else (g.get("format", "?").upper())
    size = human_bytes(exe.get("size")) if exe.get("exists") else "binario no localizado"
    packer = ", ".join(exe.get("packer") or []) if exe.get("exists") else ""
    lines.append(f"**Que es**: `{title}` — {kind}, {size}"
                 + (f", empaquetado con {packer}" if packer else "")
                 + f"; formato declarado `{g.get('format', '?')}`.")

    mode = g.get("videoMode")
    mode_txt = ""
    if mode:
        mi = int(str(mode), 16) if str(mode).startswith("0x") else int(mode)
        mode_txt = f"arranca en modo {mode} ({VIDEO_MODES.get(mi, 'desconocido')})"
    rt = d.get("rutinas") or {}
    raw, blog = rt.get("blitraw"), rt.get("blitlog")
    if raw and blog:
        forma = (f"mezcla: {blog['rectangulos']} rectangulos movs/stos y "
                 f"{raw['escrituras']} escrituras sueltas")
    elif raw:
        forma = (f"pinta A MANO byte a byte ({raw['escrituras']} escrituras sueltas a VRAM, "
                 f"0 rectangulos movs/stos)")
    elif blog:
        forma = f"pinta con movs/stos ({blog['rectangulos']} rectangulos)"
    else:
        forma = "sin telemetria de dibujo"
    rutinas = set()
    if raw and not raw.get("sin_procedencia"):
        rutinas |= {x["at"] for x in raw["rutinas"]}
    if blog:
        rutinas |= {x["at"] for x in blog["rutinas"]}
    if rutinas:
        forma += f"; {len(rutinas)} rutinas distintas escriben a VRAM (seccion 6)"
    lines.append(f"**Como pinta**: {mode_txt + '; ' if mode_txt else ''}{forma}.")

    a = d.get("assets")
    if a:
        lines.append(f"**Assets**: {a['n_assets']} sprites distintos reconstruidos en "
                     f"{a['n_instancias']} apariciones en pantalla; los PNG estan en "
                     f"`catalog/assets/`.")
    else:
        lines.append("**Assets**: sin catalogo (correr con `PAKU_LOG_BLIT=2` y pasar "
                     "`build_catalog.py`).")

    tr = d.get("tracks")
    if tr:
        clases = ", ".join(f"{k} x{v}" for k, v in tr["clases_top"].items())
        anim = len(tr["animaciones"])
        lines.append(f"**Entidades**: {tr['n_tracks']} trayectorias seguidas ({clases}); "
                     f"{anim} de ellas alternan varios sprites = animaciones.")
    else:
        lines.append("**Entidades**: sin tracks (`build_tracks.py`).")

    s = d.get("sonidos")
    if s:
        srcs = collections.Counter(r["src"] for r in s["top"])
        ef = "efecto distinto" if s["n_sonidos"] == 1 else "efectos distintos"
        ev = "evento" if s["eventos"] == 1 else "eventos"
        lines.append(f"**Como suena**: {s['n_sonidos']} {ef} "
                     f"({', '.join(f'{k}={v}' for k, v in srcs.items())}) sobre "
                     f"{s['eventos']} {ev} de audio.")
    else:
        lines.append("**Como suena**: sin catalogo de sonidos (`PAKU_LOG_AUDIO_EV=1` + "
                     "`build_sounds.py`).")

    r = d.get("run")
    if r is None:
        lines.append("**Ficheros**: sin datos (correr con `PAKU_LOG_INT21=1` y pasar "
                     "`--int21-log`).")
    elif r["int21_llamadas"] == 0 and not r["ficheros"]:
        lines.append("**Ficheros**: CERO llamadas a INT 21h en la partida capturada — el "
                     "juego es autocontenido y no toca el sistema de ficheros de DOS.")
    else:
        faltan = [f for f in r["ficheros"].values() if f["fallos"]]
        lines.append(f"**Ficheros**: {len(r['ficheros'])} nombres abiertos en "
                     f"{r['int21_llamadas']} llamadas DOS"
                     + (f"; {len(faltan)} no encontrados (opcionales o faltantes)." if faltan
                        else "; ninguno falto."))

    cfg = []
    if g.get("cyclesPerMs"):
        cfg.append(f"velocidad horneada `cyclesPerMs={g['cyclesPerMs']}`")
    if g.get("sb"):
        sb = g["sb"]
        cfg.append(f"Sound Blaster base {sb.get('base')} IRQ {sb.get('irq')} DMA {sb.get('dma')}")
    if g.get("cmdTail"):
        cfg.append(f"linea de comandos `{g['cmdTail']}`")
    if g.get("interpDefault"):
        cfg.append("motor interprete por defecto")
    if g.get("dosMap"):
        cfg.append(f"mapa de memoria `{g['dosMap']}`")
    lines.append("**Que config pide**: " + ("; ".join(cfg) + "." if cfg
                                            else "nada especial (todo por defecto)."))

    cm = d["identidad"].get("codemap")
    if cm:
        plural = "rango contiguo" if cm["rangos"] == 1 else "rangos contiguos"
        lines.append(f"**Codigo vivo**: {cm['bytes_ejecutados']} bytes ejecutados de verdad "
                     f"en {cm['rangos']} {plural} (el resto del binario son datos o "
                     f"codigo no alcanzado en la captura).")

    # Solo INTs con >=3 apariciones: el censo estatico cuenta patrones de byte y una
    # aparicion suelta suele ser un falso positivo dentro de datos.
    ints = {k: v for k, v in (d["identidad"]["exe"].get("int_estatico") or {}).items()
            if v >= 3}
    if ints:
        top_ints = ", ".join(f"INT {k}h ({INT_ROLES.get(int(k, 16), 'otro')})"
                             for k in list(ints)[:5])
        lines.append(f"**Superficie de plataforma**: en el binario aparecen {top_ints}.")

    pend = d.get("pendientes") or []
    if pend:
        lines.append(f"**Telemetria que falta**: {len(pend)} apuntes — ver la seccion final.")
    return lines


def detect_pending(d):
    """Que telemetria haria falta para el nivel 'mejor que Ghidra'."""
    out = []
    a, rt, r = d.get("assets"), d.get("rutinas") or {}, d.get("run")
    raw = rt.get("blitraw")
    if raw and raw.get("sin_procedencia"):
        out.append("**Procedencia de blits perdida en codigo LIFTEADO**: todas las escrituras "
                   "de `blitraw.bin` llegan con `at=0000:0000` porque `BlitRawLog` toma el "
                   "cs:ip del anillo del interprete (`Interp.CurrentPc`), que en el mundo "
                   "lifteado esta vacio. Sin esto no hay mapa de rutinas de dibujo para los "
                   "juegos que corren lifteados. (Pendiente de nucleo, no implementado aqui.)")
    if a and a.get("sin_procedencia"):
        msg = (f"**{a['sin_procedencia']} de {a['n_assets']} assets sin rutina de origen** "
               "(`at=0000:0000`): el catalogo se construyo desde una captura del mundo "
               "LIFTEADO, donde `BlitRawLog` no tiene cs:ip. Recapturar con "
               "`PAKU_INTERP_ONLY=1` y rehacer `build_catalog.py` da a cada sprite la "
               "rutina que lo dibuja.")
        raw2 = rt.get("blitraw")
        if raw2 and not raw2.get("sin_procedencia"):
            msg += (" (Confirmado en este mismo dossier: la seccion 6 SI trae procedencia "
                    "porque su captura corrio en el interprete.)")
        out.append(msg)
    if a and a.get("skipped_writes_by_mode"):
        out.append(f"**Modos de video sin decodificador offline**: "
                   f"{a['skipped_writes_by_mode']} escrituras descartadas por el catalogo "
                   "(EGA planar pendiente en `build_catalog.py`).")
    if r is None:
        out.append("**Sin captura de INT 21h/IVT**: correr con `PAKU_LOG_INT21=1 PAKU_LOG_IVT=1` "
                   "y pasar el stderr con `--int21-log`.")
    else:
        out.append("**El log de INT 21h no lleva sello de tiempo** (`t=`): el 'cuando' de cada "
                   "apertura de fichero solo puede darse como ordinal de llamada. Anadir "
                   "`Cpu.TickCount` a la linea lo alinearia con blit.log y audio-events.log.")
        if not r["hooks"]:
            out.append("**Ningun enganche de IVT capturado**: o el juego no engancha vectores, "
                       "o falto `PAKU_LOG_IVT=1` en la captura.")
    if not d.get("sonidos"):
        out.append("**Sin catalogo de sonidos**: `PAKU_LOG_AUDIO_EV=1` + "
                   "`tools/audio_catalog/build_sounds.py`.")
    out.append("**Histograma de escenas**: `PAKU_SAMPLE_PC` solo vuelca el anillo de los "
               "ultimos 64 PC al salir. Un muestreo PERIODICO (cada N ms, a fichero) daria el "
               "histograma de PC que separa titulo / menu / partida / game-over.")
    out.append("**Correlacion variable-marcador**: no existe telemetria de escrituras a RAM "
               "de datos correlacionadas con el redibujo de un asset; es lo que convertiria "
               "el dossier en un mapa de variables vivas (puntuacion, vidas).")
    out.append("**Correlacion input-track**: la cinta de input (`PAKU_TAPE_RECORD`) y los "
               "tracks existen por separado; cruzarlos identificaria al JUGADOR "
               "automaticamente.")
    if not d["reloj"]["calibrado"]:
        out.append("**Eje de tiempo sin calibrar**: los logs sellan con `Cpu.TickCount` "
                   "(pasos del emulador) y este juego corre a velocidad libre "
                   "(`cyclesPerMs` no horneado), asi que los ticks NO son tiempo real ni "
                   "comparables entre runs. Un sello de reloj de pared en las cabeceras de "
                   "log, u hornear `cyclesPerMs`, daria un eje comun a blit/audio/input.")
    return out


# ---------------------------------------------------------------- render markdown

def md_table(headers, rows):
    out = ["| " + " | ".join(headers) + " |",
           "|" + "|".join("---" for _ in headers) + "|"]
    for r in rows:
        out.append("| " + " | ".join("" if c is None else str(c) for c in r) + " |")
    return out


def render(d):
    L = []
    g = d["identidad"]["game_json"] or {}
    L.append(f"# Dossier — {g.get('title') or d['id']}")
    L.append("")
    L.append(f"Generado por `tools/dossier/build_dossier.py` el {d['generado']}. "
             "Todo lo que sigue sale de telemetria de ejecucion real de PakuPort: "
             "ningun dato es inferido a mano ni por un modelo.")
    L.append("")

    L.append("## 0. Resumen ejecutivo")
    L.append("")
    for line in d["resumen"]:
        L.append(f"- {line}")
    L.append("")
    if d["reloj"]["calibrado"]:
        L.append(f"> Eje de tiempo: segundos, derivados de {d['reloj']['fuente']}.")
    else:
        L.append("> Eje de tiempo: **ticks** = pasos del emulador (`Cpu.TickCount`), NO "
                 "segundos. Este juego corre a velocidad libre (`cyclesPerMs` sin hornear), "
                 "asi que los ticks solo ordenan sucesos dentro de un mismo run.")
    L.append("")

    # -- 1
    L.append("## 1. Identidad")
    L.append("")
    exe = d["identidad"]["exe"]
    rows = [("id", d["id"]), ("titulo", g.get("title")), ("exe", exe.get("path")),
            ("formato declarado", g.get("format")), ("tipo real", exe.get("kind")),
            ("tamano", human_bytes(exe.get("size")) if exe.get("exists") else "NO LOCALIZADO"),
            ("sha1", exe.get("sha1")), ("md5", exe.get("md5")),
            ("packer detectado", ", ".join(exe.get("packer") or []) or "ninguno"),
            ("loadSegment", g.get("loadSegment")), ("dsBase", g.get("dsBase")),
            ("entryOverride", g.get("entryOverride")), ("cmdTail", g.get("cmdTail")),
            ("videoMode", g.get("videoMode")), ("cyclesPerMs", g.get("cyclesPerMs")),
            ("budgetPerFrame", g.get("budgetPerFrame")),
            ("sb", json.dumps(g["sb"]) if g.get("sb") else None),
            ("dosMap", g.get("dosMap")), ("interpDefault", g.get("interpDefault"))]
    L += md_table(["campo", "valor"], [(k, v) for k, v in rows if v not in (None, "")])
    L.append("")
    mz = exe.get("mz")
    if mz:
        L.append("Cabecera MZ:")
        L.append("")
        L += md_table(["campo", "valor"], list(mz.items()))
        L.append("")
    cm = d["identidad"].get("codemap")
    if cm:
        L.append(f"**Codigo ejecutado** (`codemap.json`): {cm['bytes_ejecutados']} bytes en "
                 f"{cm['rangos']} "
                 f"{'rango contiguo' if cm['rangos'] == 1 else 'rangos contiguos'}. "
                 "Mayores bloques:")
        L.append("")
        L += md_table(["desde", "hasta", "bytes"],
                      [(r["desde"], r["hasta"], r["bytes"]) for r in cm["mayores"]])
        L.append("")

    # -- 2
    L.append("## 2. Assets visuales")
    L.append("")
    a, tr = d.get("assets"), d.get("tracks")
    if not a:
        L.append("_Sin datos._ Captura: `PAKU_LOG_BLIT=2` al correr el export, luego "
                 "`python tools/blit_catalog/build_catalog.py --logs export/<id>/logs "
                 "--out games/<id>/catalog`.")
        L.append("")
    else:
        L.append(f"{a['n_assets']} sprites distintos, {a['n_instancias']} apariciones. "
                 f"Modos de video por asset: {a['modos']}. "
                 f"Assets sin rutina de origen: {a['sin_procedencia']}.")
        L.append("")
        if a["sin_procedencia"] == a["n_assets"]:
            L.append("> La columna `rutina` sale a `0000:0000` porque este catalogo se "
                     "construyo desde una captura del mundo LIFTEADO. Recapturando con "
                     "`PAKU_INTERP_ONLY=1` cada sprite queda etiquetado con la instruccion "
                     "que lo dibuja (ver seccion 6, que si trae procedencia).")
            L.append("")
        L.append(f"### 2.1 Los {len(a['top'])} assets mas frecuentes")
        L.append("")
        u = d["reloj"]["unidad"]
        L += md_table(["asset", "PNG", "WxH", "modo", "apariciones", f"1a vez ({u})",
                       f"ult. vez ({u})", "rutina", "nota"],
                      [(r["id"], f"![{r['id']}]({r['png']})" if r["png"] else "",
                        f"{r['w']}x{r['h']}", f"0x{r['mode']:02X}" if r["mode"] is not None else "",
                        r["appearances"], r["primera"], r["final"], r["at"],
                        "borrador (mascara vacia)" if r["borrador"] else "")
                       for r in a["top"]])
        L.append("")
    if tr:
        L.append(f"### 2.2 Entidades seguidas ({tr['n_tracks']} tracks)")
        L.append("")
        L.append("Cada track es una entidad seguida en el tiempo: de donde sale, a donde va, "
                 "a que velocidad y con que sprites se anima. `vx/vy` en pixeles por 1000 "
                 "pasos del emulador. `continuidad` = fraccion de saltos consecutivos "
                 "plausibles (1.0 = trayectoria limpia, sin costuras del tracker).")
        L.append("")
        u = d["reloj"]["unidad"]
        L += md_table(["track", "clase", "puntos", "frames", "desde", "hasta", "vx", "vy",
                       f"nace ({u})", f"vida ({u})", "continuidad"],
                      [(r["id"], r["clase"], r["n"], r["frames"],
                        f"{r['desde'][0]},{r['desde'][1]}", f"{r['hasta'][0]},{r['hasta'][1]}",
                        r["vx_px_por_1000_ticks"], r["vy_px_por_1000_ticks"],
                        r["nace"], r["vida"], r["continuidad"]) for r in tr["top"]])
        L.append("")
        if tr["animaciones"]:
            L.append("### 2.3 Animaciones (assets que alternan en un mismo track)")
            L.append("")
            L += md_table(["track", "frames", "assets (primeros)"],
                          [(x["track"], x["frames"], ", ".join(x["assets"]))
                           for x in tr["animaciones"]])
            L.append("")

    # -- 3
    L.append("## 3. Sonidos")
    L.append("")
    s = d.get("sonidos")
    if not s:
        L.append("_Sin datos._ Captura: `PAKU_LOG_AUDIO_EV=1` al correr el export, luego "
                 "`python tools/audio_catalog/build_sounds.py --log export/<id>/logs/"
                 "audio-events.log --out games/<id>`.")
        L.append("")
    else:
        L.append(f"{s['n_sonidos']} efecto(s) distinto(s) sobre {s['eventos']} evento(s) "
                 f"(speaker: {s['spk']}, OPL: {s['opl']}). `shape` es la huella agrupadora; "
                 "`rutina` es quien lo dispara (el 'quien' que necesita el remake).")
        L.append("")
        u = d["reloj"]["unidad"]
        L += md_table(["huella", "fuente", "veces", "ms medio", "ms min-max", "items",
                       "rutina", f"1a vez ({u})", "detalle"],
                      [(r["shape"], r["src"], r["count"], r["avg_ms"],
                        f"{r['min_ms']}-{r['max_ms']}", r["items"], r["first_pc"],
                        r["primera"], f"`{r['detail']}`") for r in s["top"]])
        L.append("")

    # -- 4
    L.append("## 4. Mapa de ficheros")
    L.append("")
    r = d.get("run")
    if r is None:
        L.append("_Sin datos._ Captura: correr el export con `PAKU_LOG_INT21=1` redirigiendo "
                 "stderr a un fichero y pasarlo con `--int21-log`.")
        L.append("")
    elif not r["ficheros"]:
        L.append(f"Capturado ({', '.join(r['fuentes'])}): **el juego no abre ningun fichero**. "
                 f"Llamadas a INT 21h en la partida: {r['int21_llamadas']}."
                 + (" Cero llamadas DOS = juego autocontenido, todos sus datos van dentro del "
                    "binario." if r["int21_llamadas"] == 0 else ""))
        L.append("")
    else:
        L.append("`orden` = ordinal de la llamada DOS en la que aparece por primera vez "
                 "(el log de INT 21h no lleva sello de tiempo, ver seccion 7).")
        L.append("")
        L += md_table(["fichero", "aperturas", "fallos (NOT FOUND)", "orden", "CS que abre",
                       "ruta resuelta"],
                      [(f["nombre"], f["aperturas"], f["fallos"], f["primera_llamada"],
                        ", ".join(f["cs"]), f["ruta"] or "")
                       for f in sorted(r["ficheros"].values(),
                                       key=lambda f: f["primera_llamada"])])
        L.append("")
        faltan = [f for f in r["ficheros"].values() if f["fallos"]]
        if faltan:
            L.append("Ficheros que faltaron (opcionales del juego, o datos que la entrega no "
                     "incluye — este es el chequeo que caza entregas incompletas): "
                     + ", ".join(f"`{f['nombre']}`" for f in faltan))
            L.append("")
        if r["exec"]:
            L.append("Programas hijo lanzados (INT 21h 4B00): "
                     + ", ".join(f"`{e}`" for e in r["exec"]))
            L.append("")

    # -- 5
    L.append("## 5. Superficie de plataforma")
    L.append("")
    ints = exe.get("int_estatico") or {}
    if ints:
        L.append("### 5.1 INTs presentes en el binario (censo estatico `CD xx`)")
        L.append("")
        L.append("_Orientativo_: cuenta patrones de byte, asi que puede incluir falsos "
                 "positivos dentro de datos, y en un binario empaquetado solo ve el stub.")
        L.append("")
        L += md_table(["INT", "veces", "papel"],
                      [(f"{k}h", v, INT_ROLES.get(int(k, 16), "—"))
                       for k, v in list(ints.items())[:20]])
        L.append("")
    if r:
        L.append("### 5.2 Servicios DOS usados en ejecucion (INT 21h)")
        L.append("")
        if not r["ah"]:
            L.append("Ninguno: el juego no llama a INT 21h.")
        else:
            L += md_table(["AH", "veces", "servicio"],
                          [(k, v, INT21_AH.get(int(k.split("/")[0], 16), "—"))
                           for k, v in sorted(r["ah"].items(), key=lambda kv: -kv[1])])
        L.append("")
        L.append("### 5.3 Vectores enganchados (IVT)")
        L.append("")
        if not r["hooks"]:
            L.append("Ninguno capturado (o falto `PAKU_LOG_IVT=1`).")
        else:
            L += md_table(["INT", "handler nuevo", "handler previo", "enganchado desde"],
                          [(f"{k}h", v["nuevo"], v["antes"], v["desde"])
                           for k, v in sorted(r["hooks"].items())])
        L.append("")
        if r["hb"]:
            L.append("### 5.4 Estado del runtime (latidos `[HB]`)")
            L.append("")
            last = r["hb"][-1]
            L.append("Ultimo latido de la captura: " + ", ".join(
                f"`{k}={v}`" for k, v in last.items()))
            L.append("")
            L.append("`ivt8` = dueno actual de la IRQ0 (`F000:0008` = nadie la engancho, sigue "
                     "el stub de la BIOS). `irq0/irq1` = interrupciones servidas. "
                     "`cyc` = ciclos por ms del governor (0 = velocidad libre).")
            L.append("")
        if r["gaps"]:
            L.append("### 5.5 Cobertura del interprete (`[GAP]`)")
            L.append("")
            L.append("Un hueco/miss es codigo que el mundo lifteado no cubria y tuvo que "
                     "ejecutar el interprete: cero huecos = la ejecucion entera esta "
                     "explicada.")
            L.append("")
            for g in r["gaps"][-3:]:
                L.append(f"    {g}")
            L.append("")
        if r["sample_pc"]:
            L.append("### 5.6 Anillo de PCs al salir (`PAKU_SAMPLE_PC`)")
            L.append("")
            for s_ in r["sample_pc"][-1:]:
                L.append(f"    {s_[:400]}")
            L.append("")
        if r["alloc_fail"]:
            L.append("**Fallos de reserva de memoria detectados:**")
            L.append("")
            for line in r["alloc_fail"][:5]:
                L.append(f"    {line}")
            L.append("")

    # -- 6
    L.append("## 6. Rutinas de dibujo (calientes)")
    L.append("")
    rt = d.get("rutinas")
    if not rt:
        L.append("_Sin datos._ Captura: `PAKU_LOG_BLIT=2` (genera `logs/blit.log` y "
                 "`logs/blitraw.bin`).")
        L.append("")
    else:
        raw, blog = rt.get("blitraw"), rt.get("blitlog")
        L.append("Cada `cs:ip` es la instruccion EXACTA que escribio a VRAM: es un mapa de "
                 "funciones de dibujo medido en ejecucion, no inferido de un desensamblado. "
                 "El de mas volumen es el blitter principal; los de volumen fijo y bajo "
                 "suelen ser el HUD o el borrado de pantalla.")
        L.append("")
        if blog:
            L.append(f"### 6.1 Rectangulos `movs`/`stos` — {blog['rectangulos']} en "
                     f"`{blog['fuente']}`")
            L.append("")
            L += md_table(["rutina (cs:ip)", "rectangulos", "bytes", "op", "modo"],
                          [(x["at"], x["rectangulos"], x["bytes"], x["op"], x["modo"])
                           for x in blog["rutinas"]])
            L.append("")
        if raw:
            L.append(f"### 6.2 Escrituras sueltas a VRAM — {raw['escrituras']} en "
                     f"`{raw['fuente']}` ({raw['duracion']} {raw['duracion_unidad']} "
                     f"de partida)")
            L.append("")
            L.append(f"Modos de video vistos: {raw['modos']}. Rango de VRAM tocado: "
                     f"`{raw['rango_lineal']}`.")
            L.append("")
            L += md_table(["rutina (cs:ip)", "escrituras", "% del total", "modos"],
                          [(x["at"], x["escrituras"], x["pct"], x["modos"])
                           for x in raw["rutinas"]])
            L.append("")
            if raw["sin_procedencia"]:
                L.append("> **Aviso**: todas las escrituras llegan con `at=0000:0000`. Esto "
                         "significa que el juego corrio LIFTEADO: `BlitRawLog` saca el cs:ip "
                         "del anillo del interprete, que en ese mundo esta vacio. El mapa de "
                         "rutinas de esta seccion solo es real cuando el juego corre en el "
                         "interprete (`interpDefault: true` en game.json, o `PAKU_INTERP_ONLY=1`).")
                L.append("")

    # -- 7
    L.append("## 7. Telemetria que falta (para el nivel 'mejor que Ghidra')")
    L.append("")
    L.append("Detectado automaticamente a partir de lo que SI hay en este dossier. Es un "
             "listado de pendientes, no un plan de implementacion.")
    L.append("")
    for p in d["pendientes"]:
        L.append(f"- {p}")
    L.append("")

    L.append("## 8. Como reproducir este dossier")
    L.append("")
    L.append("```")
    L.append(d["comando"])
    L.append("```")
    L.append("")
    L.append("La receta completa de captura (que flag `PAKU_*` alimenta que seccion) esta en "
             "`python tools/dossier/build_dossier.py --help`.")
    L.append("")
    return "\n".join(L)


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(
        description=__doc__.split("RECETA")[0].strip(),
        epilog=__doc__[__doc__.index("RECETA"):],
        formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("game_id", help="id del juego (subdirectorio de games/)")
    ap.add_argument("--games-dir", default="games")
    ap.add_argument("--export-dir", default="export")
    ap.add_argument("--out", help="directorio de salida (por defecto games/<id>)")
    ap.add_argument("--catalog", help="directorio de catalogo de assets")
    ap.add_argument("--sounds", help="ruta de sounds.json")
    ap.add_argument("--blitraw", help="ruta de blitraw.bin")
    ap.add_argument("--blit-log", help="ruta de blit.log")
    ap.add_argument("--int21-log", action="append", default=[],
                    help="captura de stderr con lineas [int21]/[IVT]/[HB] (repetible)")
    ap.add_argument("--exe", help="ruta del binario original (si game.json miente)")
    ap.add_argument("--top", type=int, default=20, help="filas por tabla (def. 20)")
    a = ap.parse_args()

    gid = a.game_id
    gdir = os.path.join(a.games_dir, gid)
    if not os.path.isdir(gdir):
        print(f"error: no existe {gdir}", file=sys.stderr)
        return 2
    out_dir = a.out or gdir
    os.makedirs(out_dir, exist_ok=True)

    game = read_json(os.path.join(gdir, "game.json")) or {}
    exe_path = a.exe or game.get("exePath")
    if exe_path and not os.path.exists(exe_path):
        # game.json guarda rutas absolutas de la maquina donde se porteo: probar el export.
        alt = first_existing(
            os.path.join(a.export_dir, gid, os.path.basename(exe_path)),
            os.path.join(a.export_dir, game.get("id", gid), os.path.basename(exe_path)))
        if alt:
            exe_path = alt

    catalog = a.catalog or (os.path.join(gdir, "catalog")
                            if os.path.isdir(os.path.join(gdir, "catalog")) else None)
    sounds = a.sounds or os.path.join(gdir, "sounds.json")
    exp_logs = os.path.join(a.export_dir, gid, "logs")
    blitraw = a.blitraw or first_existing(os.path.join(exp_logs, "blitraw.bin"),
                                          os.path.join("logs", "blitraw.bin"))
    blitlog = a.blit_log or first_existing(os.path.join(exp_logs, "blit.log"),
                                           os.path.join("logs", "blit.log"))
    int21_logs = list(a.int21_log)
    if not int21_logs:
        auto = first_existing(os.path.join("logs", f"dossier-{gid}-run.err"))
        if auto:
            int21_logs = [auto]

    clock = Clock(game.get("cyclesPerMs"))
    # Las rutas png de assets.json son relativas al catalogo; el dossier vive en out_dir.
    png_prefix = ""
    if catalog:
        rel = os.path.relpath(catalog, out_dir).replace(os.sep, "/")
        png_prefix = "" if rel == "." else rel + "/"

    import datetime
    d = {
        "id": gid,
        "generado": datetime.datetime.now().strftime("%Y-%m-%d %H:%M"),
        "comando": "python tools/dossier/build_dossier.py " + " ".join(sys.argv[1:]),
        "identidad": {
            "game_json": game,
            "exe": scan_exe(exe_path),
            "codemap": summarize_codemap(read_json(os.path.join(gdir, "codemap.json"))),
        },
        "reloj": {"calibrado": clock.calibrado, "fuente": clock.fuente,
                  "unidad": clock.label()},
        "assets": summarize_assets(catalog, a.top, clock, png_prefix),
        "tracks": summarize_tracks(catalog, a.top, clock),
        "sonidos": summarize_sounds(sounds, a.top, clock),
        "run": parse_run_logs(int21_logs),
        "rutinas": summarize_routines(blitraw, blitlog, a.top, clock),
    }
    d["pendientes"] = detect_pending(d)
    d["resumen"] = build_summary(d)

    md_path = os.path.join(out_dir, "dossier.md")
    js_path = os.path.join(out_dir, "dossier.json")
    with open(md_path, "w", encoding="utf-8") as f:
        f.write(render(d))
    # El JSON lleva estructuras auxiliares no serializables (Counter/defaultdict ya
    # convertidos); default=str cubre cualquier resto.
    with open(js_path, "w", encoding="utf-8") as f:
        json.dump(d, f, indent=1, ensure_ascii=False, default=str)

    poblada = [name for name, val in (
        ("identidad", d["identidad"]["exe"].get("exists")),
        ("assets", d["assets"]), ("tracks", d["tracks"]), ("sonidos", d["sonidos"]),
        ("ficheros/plataforma", d["run"]), ("rutinas", d["rutinas"])) if val]
    print(f"{md_path} + {js_path}")
    print(f"  secciones pobladas ({len(poblada)}): {', '.join(poblada)}")
    print(f"  pendientes de telemetria: {len(d['pendientes'])}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
