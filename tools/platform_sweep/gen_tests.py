#!/usr/bin/env python3
"""Barrido de plataforma: genera TEST.COM (oráculo DOSBox) y tests.json (PakuPort).

Cada caso: fija TODOS los registros GP (deterministas, default 0), DS=ES=CS salvo
override, flags=0, ejecuta INT nn y vuelca AX,BX,CX,DX,SI,DI,ES,DS,FLAGS + marcador
(20 bytes) al buffer de resultados con prefijo CS (inmune a cambios de DS del handler).
Al final escribe el buffer a RESULT.BIN vía INT 21h 3C/40/3E y termina con 4C.

El estado de la máquina se ARRASTRA entre casos (mismo orden en ambos mundos): un caso
puede preparar estado para el siguiente (INT16 05 push -> INT16 00 get; INT10 set mode).

Salida: TEST.COM, tests.json (misma tabla para `pakuport platform-test`).
"""
import json
import os
import struct
import sys

OUT = os.path.dirname(os.path.abspath(__file__))

# ---------------------------------------------------------------- tabla de casos
# reg values: int. check: lista de regs a comparar ("ax","bx","cx","dx","si","di","es","ds").
# fmask: máscara de FLAGS a comparar (CF=0x0001, ZF=0x0040). Default: CF.
# Los casos con salida volátil (hora real, punteros internos de DOS) llevan check=[] y
# fmask=0: se GRABAN igualmente para inspección manual, pero no computan al veredicto.
CF, ZF = 0x0001, 0x0040

T = []
def t(tid, intno, check=("ax",), fmask=CF, save=None, mbuf=None, **regs):
    d = {"id": tid, "int": intno, "regs": regs, "check": list(check), "fmask": fmask}
    if save:
        d["save"] = list(save)           # (reg-de-salida, nombre-de-slot)
    if mbuf:
        d["mbuf"] = list(mbuf)           # (reg-de-buffer, nbytes) -> buffer comparable
    T.append(d)


def tio(tid, port, kind, val=0, check=("ax",), fmask=0):
    # Caso de PUERTO IO: in8/in16 leen del puerto (comparar AL/AX); out8 escribe AL=val.
    regs = {"dx": port}
    if kind == "out8":
        regs["ax"] = val
    T.append({"id": tid, "int": 0, "io": kind, "regs": regs, "check": list(check), "fmask": fmask})

# --- INT 11h / 12h: equipo y memoria (BDA) ---
t("11_equip", 0x11, check=("ax",), fmask=0)
t("12_memkb", 0x12, check=("ax",), fmask=0)

# --- INT 16h: teclado (ya en paridad; verificación) ---
t("16_05_push", 0x16, ax=0x0500, cx=0x1E61, check=("ax",), fmask=0)      # push 'a' (scan 1E)
t("16_01_peek", 0x16, ax=0x0100, check=("ax",), fmask=ZF)                # hay tecla -> ZF=0, AX=1E61
t("16_00_get", 0x16, ax=0x0000, check=("ax",), fmask=0)                  # saca 1E61
t("16_01_empty", 0x16, ax=0x0100, check=(), fmask=ZF)                    # vacío -> ZF=1
t("16_02_shift", 0x16, ax=0x0200, check=("ax",), fmask=0)
t("16_12_extshift", 0x16, ax=0x1200, check=("ax",), fmask=0)
t("16_05_push2", 0x16, ax=0x0500, cx=0x011B, check=("ax",), fmask=0)     # push ESC
t("16_11_peek", 0x16, ax=0x1100, check=("ax",), fmask=ZF)
t("16_10_get", 0x16, ax=0x1000, check=("ax",), fmask=0)
t("16_03_typematic", 0x16, ax=0x0305, bx=0x0000, check=(), fmask=0)

# --- INT 1Ah: hora (volátil: solo CF y forma) ---
t("1a_00_ticks", 0x1A, ax=0x0000, check=(), fmask=0)
t("1a_02_rtctime", 0x1A, ax=0x0200, check=(), fmask=CF)
t("1a_04_rtcdate", 0x1A, ax=0x0400, check=(), fmask=CF)

# --- INT 21h: informativas + dummies del barrido ---
t("21_30_version", 0x21, ax=0x3000, check=("ax", "bx", "cx"), fmask=0)
t("21_19_drive", 0x21, ax=0x1900, check=("ax",), fmask=0)
t("21_33_00_break", 0x21, ax=0x3300, check=("ax", "dx"), fmask=0)
# 21/04 (AUX out) FUERA: bloquea en el BIOS serie de DOSBox sin puerto COM real
# dummies con AL≠0 de entrada: verifica que el handler pone AL=0 (default_fallthrough)
t("21_18_dummy", 0x21, ax=0x185A, check=("ax",), fmask=0)                # CP/M dummy AL=0
t("21_1d_dummy", 0x21, ax=0x1D5A, check=("ax",), fmask=0)
t("21_1e_dummy", 0x21, ax=0x1E5A, check=("ax",), fmask=0)
t("21_20_dummy", 0x21, ax=0x205A, check=("ax",), fmask=0)
t("21_61_dummy", 0x21, ax=0x615A, check=("ax",), fmask=0)
t("21_6b_dummy", 0x21, ax=0x6B5A, check=("ax",), fmask=0)
t("21_1f_dpb", 0x21, ax=0x1F00, check=("ax",), fmask=CF)                 # AL=0 ok (ES:BX volátil)
t("21_32_dpb", 0x21, ax=0x3200, dx=0x0000, check=("ax",), fmask=CF)
t("21_52_sysvars", 0x21, ax=0x5200, check=(), fmask=0)                   # ES:BX interno (grabar)
t("21_62_psp", 0x21, ax=0x6200, check=(), fmask=0)                       # BX=PSP (difiere)
t("21_44_00_ioctl_stdout", 0x21, ax=0x4400, bx=0x0001, check=(), fmask=CF)  # DX=device word (grabar)
t("21_68_flush", 0x21, ax=0x6800, bx=0x0001, check=(), fmask=CF)
t("21_6a_flush", 0x21, ax=0x6A00, bx=0x0001, check=(), fmask=CF)         # alias de 68
t("21_37_switchar", 0x21, ax=0x3700, check=("ax", "dx"), fmask=0)
t("21_64_internal", 0x21, ax=0x6400, check=(), fmask=0)
# 59: "último error extendido" arrastra el historial del boot del oráculo (ambiental)
t("21_59_exterr", 0x21, ax=0x5900, bx=0x0000, check=(), fmask=0)
t("21_58_strategy", 0x21, ax=0x5800, check=("ax",), fmask=CF)

# --- INT 15h: servicios AT (BiosInterrupts) ---
t("15_88_extmem", 0x15, ax=0x8800, check=("ax",), fmask=CF)
# C0: la DIRECCIÓN de la tabla de config es un segmento ROM dinámico en DOSBox-X
# (p.ej. FC94:0000, varía por build); lo comparable es el CONTENIDO (pendiente de
# mem-dumps). Nosotros usamos la clásica F000:E6F5. → GRIS.
t("15_c0_config", 0x15, ax=0xC000, check=(), fmask=CF)
t("15_86_wait0", 0x15, ax=0x8600, cx=0, dx=0, check=(), fmask=CF)
t("15_24_03_a20", 0x15, ax=0x2403, check=("ax", "bx"), fmask=CF)
t("15_77_invalid", 0x15, ax=0x7700, check=("ax",), fmask=CF)   # CF=1, AH=86 (no soportada)

# --- INT 2Fh: multiplex ---
t("2f_1600_winchk", 0x2F, ax=0x1600, check=("ax",), fmask=0)
# 4300: el oráculo TIENE XMS (AL=80). Nosotros respondemos AL=0 (ausente-honesto)
# hasta portar el driver XMS (himem de DOSBox-X) — anotado en BARRIDO_PLATAFORMA.md.
# Afirmar 80 sin entry point (4310) rompería a los llamantes. → GRIS documentado.
t("2f_4300_xmschk", 0x2F, ax=0x4300, check=(), fmask=0)
t("2f_1a00_ansichk", 0x2F, ax=0x1A00, check=(), fmask=0)       # ANSI.SYS check (grabar)

# --- INT 33h: ratón ---
t("33_00_reset", 0x33, ax=0x0000, check=("ax", "bx"), fmask=0)
t("33_24_version", 0x33, ax=0x0024, check=("bx", "cx"), fmask=0)
t("33_21_swreset", 0x33, ax=0x0021, check=("ax", "bx"), fmask=0)
t("33_07_xrange", 0x33, ax=0x0007, cx=0, dx=639, check=(), fmask=0)
t("33_08_yrange", 0x33, ax=0x0008, cx=0, dx=199, check=(), fmask=0)
t("33_03_status", 0x33, ax=0x0003, check=("bx",), fmask=0)
t("33_0b_motion", 0x33, ax=0x000B, check=("cx", "dx"), fmask=0)
t("33_15_storage", 0x33, ax=0x0015, check=("bx",), fmask=0)
t("33_1b_sens_get", 0x33, ax=0x001B, check=("bx", "cx", "dx"), fmask=0)
t("33_13_dblspeed", 0x33, ax=0x0013, dx=0x0040, check=(), fmask=0)
t("33_0d_lightpen_on", 0x33, ax=0x000D, check=(), fmask=0)
t("33_0e_lightpen_off", 0x33, ax=0x000E, check=(), fmask=0)
t("33_11_wheelapi", 0x33, ax=0x0011, check=("ax",), fmask=0)
t("33_12_bigcursor", 0x33, ax=0x0012, check=("ax",), fmask=0)
t("33_22_setlang", 0x33, ax=0x0022, bx=0x0000, check=(), fmask=0)
t("33_23_getlang", 0x33, ax=0x0023, check=("bx",), fmask=0)
t("33_25_geninfo", 0x33, ax=0x0025, check=("ax",), fmask=0)
t("33_26_maxcoords", 0x33, ax=0x0026, check=("bx", "cx", "dx"), fmask=0)
t("33_27_masks", 0x33, ax=0x0027, check=("ax", "bx"), fmask=0)
t("33_2a_hotspot", 0x33, ax=0x002A, check=("ax", "bx", "cx"), fmask=0)
t("33_31_virtcoords", 0x33, ax=0x0031, check=("ax", "bx", "cx", "dx"), fmask=0)
t("33_32_advfuncs", 0x33, ax=0x0032, check=("ax", "bx", "cx", "dx"), fmask=0)

# --- INT 10h: vídeo (modo texto 3 primero) ---
t("10_0f_getmode", 0x10, ax=0x0F00, check=("ax", "bx"), fmask=0)
# posición del cursor = estado del prompt de boot en el oráculo (ambiental) → solo forma CX
t("10_03_getcursor", 0x10, ax=0x0300, bx=0x0000, check=("cx",), fmask=0)
t("10_08_readchar", 0x10, ax=0x0800, bx=0x0000, check=("ax",), fmask=0)
t("10_12_10_egainfo", 0x10, ax=0x1210, bx=0x0010, check=("bx", "cx"), fmask=0)
t("10_1a_dispcode", 0x10, ax=0x1A00, check=("ax", "bx"), fmask=0)
t("10_12_30_vertres", 0x10, ax=0x1202, bx=0x30, check=("ax",), fmask=0)   # AL=2: 400 líneas
t("10_12_31_palload", 0x10, ax=0x1200, bx=0x31, check=("ax",), fmask=0)   # AL=0: enable
t("10_12_32_videoaddr", 0x10, ax=0x1200, bx=0x32, check=("ax",), fmask=0)
t("10_12_33_grayscale", 0x10, ax=0x1201, bx=0x33, check=("ax",), fmask=0) # AL=1: disable
t("10_12_34_cursoremu", 0x10, ax=0x1201, bx=0x34, check=("ax",), fmask=0)
t("10_12_36_refresh", 0x10, ax=0x1200, bx=0x36, check=("ax",), fmask=0)
t("10_11_30_fontinfo", 0x10, ax=0x1130, bx=0x0200, check=("cx", "dx"), fmask=0)
t("10_00_mode13", 0x10, ax=0x0013, check=(), fmask=0)
t("10_0f_mode13", 0x10, ax=0x0F00, check=("ax", "bx"), fmask=0)
t("10_10_10_setdac", 0x10, ax=0x1010, bx=0x0005, cx=0x1234 & 0x3F3F, dx=0x2A00, check=(), fmask=0)
t("10_10_15_getdac", 0x10, ax=0x1015, bx=0x0005, check=("cx", "dx"), fmask=0)
t("10_10_18_setpelmask", 0x10, ax=0x1018, bx=0x00FF, check=(), fmask=0)
t("10_10_19_getpelmask", 0x10, ax=0x1019, check=("bx",), fmask=0)
t("10_05_page0", 0x10, ax=0x0500, check=(), fmask=0)
t("10_00_mode3", 0x10, ax=0x0003, check=(), fmask=0)   # restaurar texto

# --- Round-trips deterministas de más servicios (endurecer cobertura) ---
# INT 21h set/get vector (25/35): fijar IVT[60]=DS:1234 y recuperarlo -> ES:BX
t("21_25_setvec", 0x21, ax=0x2560, dx=0x1234, check=(), fmask=0)
t("21_35_getvec", 0x21, ax=0x3560, check=("bx", "es"), fmask=0)   # ES:BX = pspSeg:1234
# INT 10h cursor (02/03): posicionar y recuperar la posición
t("10_02_setcur", 0x10, ax=0x0200, bx=0, dx=0x0A14, check=(), fmask=0)  # fila 10, col 20
t("10_03_getcur", 0x10, ax=0x0300, bx=0, check=("dx",), fmask=0)        # DX=0A14
# INT 10h write char (09) en el cursor + read char (08): round-trip por el buffer de texto
t("10_02_cur00", 0x10, ax=0x0200, bx=0, dx=0, check=(), fmask=0)        # cursor a 0,0
t("10_09_wrchar", 0x10, ax=0x0942, bx=0x0007, cx=1, check=(), fmask=0)  # 'B' attr 7 en 0,0
t("10_08_rdchar", 0x10, ax=0x0800, bx=0, check=("ax",), fmask=0)        # AX=0742 ('B' attr 7)
# scroll: subir 1 línea toda la pantalla; 0,0 pasa a ser el contenido de la línea 1 (espacio)
t("10_06_scroll1", 0x10, ax=0x0601, bx=0x0700, cx=0, dx=0x184F, check=(), fmask=0)
t("10_02_cur00b", 0x10, ax=0x0200, bx=0, dx=0, check=(), fmask=0)
t("10_08_rd_scrolled", 0x10, ax=0x0800, bx=0, check=("ax",), fmask=0)   # AX=0720 (espacio tras scroll)

# --- INT 21h: round-trip de ficheros (subsistema de ficheros del core de PakuPort) ---
# Secuencia con ESTADO (el handle de 3C se arrastra): crear -> escribir -> seek 0 -> leer ->
# comparar el buffer leído con el payload -> cerrar -> borrar. El oráculo y la Platform hacen
# el round-trip cada uno sobre SU fichero; ambos deben producir AX=bytes y el mismo payload.
t("21_3c_create", 0x21, ax=0x3C00, cx=0, dx="&SWEEP.TMP", save=("ax", "fh"), check=(), fmask=CF)
t("21_40_write", 0x21, ax=0x4000, bx="@fh", cx=16, dx="&hex:00112233445566778899aabbccddeeff", check=("ax",), fmask=CF)
t("21_42_seek0", 0x21, ax=0x4200, bx="@fh", cx=0, dx=0, check=("ax", "dx"), fmask=CF)
t("21_3f_read", 0x21, ax=0x3F00, bx="@fh", cx=16, mbuf=("dx", 16), check=("ax",), fmask=CF)
t("21_3e_close", 0x21, ax=0x3E00, bx="@fh", check=(), fmask=CF)
# reabrir el mismo fichero (open existing, 3D) y releer -> mismo payload
t("21_3d_reopen", 0x21, ax=0x3D00, dx="&SWEEP.TMP", save=("ax", "fh2"), check=(), fmask=CF)
t("21_3f_read2", 0x21, ax=0x3F00, bx="@fh2", cx=16, mbuf=("dx", 16), check=("ax",), fmask=CF)
t("21_42_seekend", 0x21, ax=0x4202, bx="@fh2", cx=0, dx=0, check=("ax", "dx"), fmask=CF)  # SEEK_END -> tamaño=16
t("21_3e_close2", 0x21, ax=0x3E00, bx="@fh2", check=(), fmask=CF)
t("21_41_delete", 0x21, ax=0x4100, dx="&SWEEP.TMP", check=(), fmask=CF)

# --- INT 21h: allocator (AH=48/4A) protocolo exacto contra DOSBox-X ---
# El programa (TEST.COM, ES=PSP=CS) sondea la memoria como hace un crt0: pide crecer/allocar
# 0xFFFF paras (imposible) y DOS devuelve CF=1, AX=8 (memoria insuficiente) + BX=máximo en
# párrafos. Luego alloca EXACTAMENTE ese máximo (cabe -> CF=0). El segmento devuelto por 48
# (AX en el caso _allocfit) difiere entre mundos (layout distinto): solo se juzga CF.
t("21_4a_growmax", 0x21, ax=0x4A00, bx=0xFFFF, check=("ax", "bx"), fmask=CF)   # SETBLOCK a max
t("21_48_allocmax", 0x21, ax=0x4800, bx=0xFFFF, save=("bx", "freemax"), check=("ax", "bx"), fmask=CF)
t("21_48_allocfit", 0x21, ax=0x4800, bx="@freemax", check=(), fmask=CF)        # cabe -> CF=0

# --- Puertos de hardware (chips): comportamiento observable en reposo vs el oráculo ---
# in8 lee AL (AH=0 por el reset de ax); comparar AX. Los que dependan del entorno -> GRIS.
tio("io_mpu_status", 0x331, "in8", check=("ax",))      # MPU-401 status (DSR/DRR, determinista)
# SB DSP status (0x22C/0x22E): el estado interno del SB en DOSBox evoluciona -> valor no
# determinista entre corridas (0xFF/0x7F) -> GRIS. PakuPort devuelve 0x7F (reposo=listo).
tio("io_sb_dsp_write", 0x22C, "in8", check=(), fmask=0)
tio("io_sb_dsp_read", 0x22E, "in8", check=(), fmask=0)
# gameport 0x201: depende del conf (con joystick el modelo de ejes de PakuPort da 0xF0; sin
# joystick el oráculo da bus flotante 0xFF) -> GRIS, no es un bug de comportamiento
tio("io_gameport", 0x201, "in8", check=(), fmask=0)
tio("io_dma1_status", 0x08, "in8", check=("ax",))      # DMA1 status en reposo (0x00)
tio("io_dma1_mask", 0x0F, "in8", check=("ax",))        # DMA1 mask-all en reposo (0x0F)
tio("io_lpt_status", 0x379, "in8", check=("ax",))      # LPT1 status en reposo (0xDF)

# --- MPU-401 en modo UART (2026-08-24) ---
# La secuencia canonica con la que TODO driver MIDI de DOS detecta la tarjeta: mandar 0x3F al
# puerto de comandos y leer el 0xFE de reconocimiento. Antes 0x330 devolvia 0xFF fijo, no habia
# ACK, y todos los juegos decidian "no hay MPU-401" y se caian al OPL. El reset (0xFF) contesta
# igual. Se juzga byte a byte contra el MPU de DOSBox.
tio("io_mpu_uart_cmd", 0x331, "out8", val=0x3F, check=(), fmask=0)   # entrar en UART
tio("io_mpu_uart_ack", 0x330, "in8", check=("ax",))                 # -> 0xFE
tio("io_mpu_uart_vacio", 0x330, "in8", check=("ax",))               # cola vacia -> 0xFF
tio("io_mpu_reset_cmd", 0x331, "out8", val=0xFF, check=(), fmask=0) # reset
tio("io_mpu_reset_ack", 0x330, "in8", check=("ax",))                # -> 0xFE

# --- SoundBlaster DSP handshake (oracle.conf sbtype=sb2 base=220 irq=7 dma=1) ---
# Reset -> 0xAA, luego Get DSP Version (0xE1) -> 0x02,0x01 (SB 2.01). Byte-exacto vs el SB
# real de DOSBox. El estado del SB se ARRASTRA entre casos (mismo TEST.COM secuencial / mismo
# objeto platform), igual que el resto del arnés. El ack 0xAA del reset lo programa DOSBox a
# 20 us (evento PIC): un INT 15h 86 de ~500 us entremedias deja que venza en el oráculo.
tio("io_sb_reset_hi", 0x226, "out8", val=1, check=(), fmask=0)         # assert reset
tio("io_sb_reset_lo", 0x226, "out8", val=0, check=(), fmask=0)         # deassert -> encola 0xAA
t("io_sb_reset_wait", 0x15, ax=0x8600, cx=0x0000, dx=0x0200, check=(), fmask=0)  # ~500us de espera
tio("io_sb_reset_stat", 0x22E, "in8", check=("ax",))                  # read status -> 0xAA (dato disponible)
tio("io_sb_reset_ack", 0x22A, "in8", check=("ax",))                   # read data -> 0xAA
tio("io_sb_ver_cmd", 0x22C, "out8", val=0xE1, check=(), fmask=0)      # Get DSP Version
tio("io_sb_ver_stat", 0x22E, "in8", check=("ax",))                   # status -> 0xAA (2 bytes en cola)
tio("io_sb_ver_major", 0x22A, "in8", check=("ax",))                  # major -> 0x02
tio("io_sb_ver_minor", 0x22A, "in8", check=("ax",))                  # minor -> 0x01

# ---------------------------------------------------------------- estado entre casos
# Algunos servicios encadenan (INT 21h ficheros: el handle de 3C se usa en 40/3F/3E). El
# arnés resetea registros por caso, así que un caso puede GUARDAR un registro de salida en un
# slot con nombre (save=("ax","fh")) y otro CARGARLO como valor de entrada (bx="@fh"). Los
# slots viven en una zona fija del COM (0x7F00+, lejos del código y del buffer de resultados).
VARS_BASE = 0x7F00
_VAR_OFF = {}
def var_off(name):
    if name not in _VAR_OFF:
        _VAR_OFF[name] = VARS_BASE + len(_VAR_OFF) * 2
    return _VAR_OFF[name]

# Cadenas/buffers embebidos (nombre de fichero, payload de escritura). Zona fija 0x7D00.
# Un caso los referencia con dx="&SWEEP.TMP" (DX = offset de la cadena "SWEEP.TMP\0" en el COM).
STR_BASE = 0x7D00
_STR = {}          # texto -> (offset, bytes)
def str_off(text):
    if text not in _STR:
        raw = _decode_embed(text)
        off = STR_BASE + sum(len(b) + 1 for _, b in _STR.values())
        _STR[text] = (off, raw)
    return _STR[text][0]
def _decode_embed(text):
    # "SWEEP.TMP" literal; "\xNN..." para payload binario determinista
    if text.startswith("hex:"):
        return bytes.fromhex(text[4:])
    return text.encode("ascii")

# pre-registrar slots y cadenas referidos (para que existan antes de ensamblar)
for _tc in T:
    if "save" in _tc:
        var_off(_tc["save"][1])
    for _v in _tc["regs"].values():
        if isinstance(_v, str) and _v.startswith("@"):
            var_off(_v[1:])
        elif isinstance(_v, str) and _v.startswith("&"):
            str_off(_v[1:])

# ---------------------------------------------------------------- ensamblador COM
REGSET = {"ax": 0xB8, "cx": 0xB9, "dx": 0xBA, "bx": 0xBB, "si": 0xBE, "di": 0xBF}
# mov reg, [disp16] (cs override): 2E 8B /r  — carga un registro desde un slot de var
LOAD = {
    "ax": b"\x2E\xA1", "bx": b"\x2E\x8B\x1E", "cx": b"\x2E\x8B\x0E",
    "dx": b"\x2E\x8B\x16", "si": b"\x2E\x8B\x36", "di": b"\x2E\x8B\x3E",
}
# mov cs:[disp16], reg
STORE = {
    "ax": b"\x2E\xA3", "bx": b"\x2E\x89\x1E", "cx": b"\x2E\x89\x0E",
    "dx": b"\x2E\x89\x16", "si": b"\x2E\x89\x36", "di": b"\x2E\x89\x3E",
    "es": b"\x2E\x8C\x06", "ds": b"\x2E\x8C\x1E",
}
REC = 20  # bytes por caso: ax bx cx dx si di es ds flags marker


def emit_case(code: bytearray, idx: int, tc: dict, res_base: int):
    base = res_base + idx * REC
    r = tc["regs"]
    # marcador (índice+1) ANTES de nada
    code += bytes([REGSET["ax"]]) + struct.pack("<H", idx + 1)
    code += STORE["ax"] + struct.pack("<H", base + 18)
    # flags=0 deterministas: xor ax,ax; push ax; popf
    code += b"\x31\xC0\x50\x9D"
    # ds=cs, es=cs
    code += b"\x0E\x1F\x0E\x07"
    # overrides de segmento (vía ax, antes del ax final)
    if "ds" in r:
        code += bytes([REGSET["ax"]]) + struct.pack("<H", r["ds"]) + b"\x8E\xD8"
    if "es" in r:
        code += bytes([REGSET["ax"]]) + struct.pack("<H", r["es"]) + b"\x8E\xC0"
    # GP deterministas (default 0), ax el último. Un valor puede ser: int (inmediato),
    # "@var" (cargar del slot de estado), "&str" (offset de cadena embebida). Si el caso
    # tiene mbuf, el registro de buffer se fuerza a la dirección del buffer comparable.
    mbuf = tc.get("_mbuf")   # (reg, abs_off, nbytes) resuelto en build_com
    for reg in ("bx", "cx", "dx", "si", "di", "ax"):
        if mbuf and reg == mbuf[0]:
            code += bytes([REGSET[reg]]) + struct.pack("<H", mbuf[1])
            continue
        v = r.get(reg, 0)
        if isinstance(v, str) and v.startswith("@"):
            code += LOAD[reg] + struct.pack("<H", var_off(v[1:]))
        elif isinstance(v, str) and v.startswith("&"):
            code += bytes([REGSET[reg]]) + struct.pack("<H", str_off(v[1:]))
        else:
            code += bytes([REGSET[reg]]) + struct.pack("<H", v)
    # INT nn — o una operación de PUERTO IO (chips: MPU-401, SB, DMA, PIC...). Para IO, DX ya
    # lleva el puerto (y AL el dato en out8); in8=EC (in al,dx), in16=ED (in ax,dx), out8=EE.
    if "io" in tc:
        code += {"in8": b"\xEC", "in16": b"\xED", "out8": b"\xEE"}[tc["io"]]
    else:
        code += bytes([0xCD, tc["int"]])
    # volcado: pushf; stores de los regs de salida; pop ax -> store flags; restaurar ds
    code += b"\x9C"
    code += STORE["ax"] + struct.pack("<H", base + 0)
    for i, reg in enumerate(("bx", "cx", "dx", "si", "di"), start=1):
        code += STORE[reg] + struct.pack("<H", base + i * 2)
    code += STORE["es"] + struct.pack("<H", base + 12)
    code += STORE["ds"] + struct.pack("<H", base + 14)
    # save: guardar un registro de salida en un slot de estado (los regs siguen válidos
    # aquí, antes del `pop ax` que los pisa)
    if "save" in tc:
        sreg, sname = tc["save"]
        code += STORE[sreg] + struct.pack("<H", var_off(sname))
    code += b"\x58" + STORE["ax"] + struct.pack("<H", base + 16)
    code += b"\x0E\x1F"  # ds=cs


def _assign_mbufs(tests, res_base):
    # Los buffers comparables (mbuf) viven en el área de resultados, tras los registros.
    # Devuelve el tamaño total del resultado (registros + mbufs).
    acc = len(tests) * REC
    for tc in tests:
        if "mbuf" in tc:
            reg, nbytes = tc["mbuf"]
            tc["_mbuf"] = (reg, res_base + acc, nbytes)  # (reg-de-buffer, dir abs en CS, nbytes)
            tc["_mbuf_rel"] = acc                         # offset en RESULT.BIN
            acc += nbytes
    return acc


def build_com(tests):
    # Dos pasadas: la primera con res_base provisional para medir el tamaño del código.
    for res_base in (0x8000, None):
        total_result = _assign_mbufs(tests, res_base or 0x8000)
        code = bytearray()
        for i, tc in enumerate(tests):
            emit_case(code, i, tc, res_base or 0x8000)
        # epílogo: crear RESULT.BIN, escribir buffer, cerrar, exit
        fname_off = 0x100 + len(code) + len(EPILOG_TEMPLATE)
        res_off = (fname_off + len(FNAME) + 1 + 15) & ~15
        if res_base is None:
            res_base = res_off
            total_result = _assign_mbufs(tests, res_base)
            code = bytearray()
            for i, tc in enumerate(tests):
                emit_case(code, i, tc, res_base)
        epilog = bytearray()
        epilog += b"\xB4\x3C\x31\xC9" + bytes([0xBA]) + struct.pack("<H", fname_off) + b"\xCD\x21"  # create
        epilog += b"\x89\xC3\xB4\x40" + bytes([0xB9]) + struct.pack("<H", total_result)
        epilog += bytes([0xBA]) + struct.pack("<H", res_base) + b"\xCD\x21"                        # write
        epilog += b"\xB4\x3E\xCD\x21"                                                              # close
        epilog += b"\xB8\x00\x4C\xCD\x21"                                                          # exit
        assert len(epilog) == len(EPILOG_TEMPLATE)
    # Construir la imagen completa (0x100..top) con ceros, y colocar cada zona en su sitio fijo.
    strings_end = STR_BASE + sum(len(b) + 1 for _, b in _STR.values())
    vars_end = VARS_BASE + len(_VAR_OFF) * 2
    top = max(res_base + total_result, strings_end, vars_end)
    img = bytearray(top - 0x100)
    body = bytes(code) + bytes(epilog) + FNAME + b"\x00"
    img[0:len(body)] = body
    assert 0x100 + len(body) <= STR_BASE, f"el código pisa las cadenas (0x{0x100+len(body):X} > 0x{STR_BASE:X})"
    for _, (off, raw) in _STR.items():
        img[off - 0x100: off - 0x100 + len(raw)] = raw   # cadena + terminador (ya es 0 por el bytearray)
    # vars y resultados quedan a cero (el bytearray los inicializa)
    return bytes(img), res_base, total_result


FNAME = b"RESULT.BIN"
EPILOG_TEMPLATE = b"\x00" * (10 + 11 + 4 + 5)  # mismo tamaño que el epílogo real


def main():
    com, res_base, total_result = build_com(T)
    with open(os.path.join(OUT, "TEST.COM"), "wb") as f:
        f.write(com)
    # Serializar los casos para el lado PakuPort (platform-test) y el juez: sin campos internos,
    # con mbuf_rel/nbytes (offset del buffer comparable en RESULT.BIN).
    ser = []
    for tc in T:
        d = {"id": tc["id"], "int": tc["int"], "regs": tc["regs"], "check": tc["check"], "fmask": tc["fmask"]}
        if "io" in tc:
            d["io"] = tc["io"]
        if "save" in tc:
            d["save"] = tc["save"]
        if "mbuf" in tc:
            d["mbuf_reg"] = tc["mbuf"][0]
            d["mbuf_nbytes"] = tc["mbuf"][1]
            d["mbuf_rel"] = tc["_mbuf_rel"]
        ser.append(d)
    with open(os.path.join(OUT, "tests.json"), "w") as f:
        json.dump({"record_bytes": REC, "total_result": total_result,
                   "vars_base": VARS_BASE, "str_base": STR_BASE,
                   "strings": {t: {"off": off, "hex": raw.hex()} for t, (off, raw) in _STR.items()},
                   "tests": ser}, f, indent=1)
    n_mbuf = sum(1 for tc in T if "mbuf" in tc)
    print(f"TEST.COM: {len(com)} bytes, {len(T)} casos ({n_mbuf} con mbuf), resultados CS:{res_base:04X} len {total_result}")


if __name__ == "__main__":
    main()
