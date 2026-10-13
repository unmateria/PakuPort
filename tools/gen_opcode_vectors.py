#!/usr/bin/env python3
# Genera vectores de verdad de opcodes con UNICORN (oraculo INDEPENDIENTE) para los tests del
# lifter de PakuPort. Ejecuta los BYTES REALES de cada instruccion x86-16 en Unicorn y captura
# resultado + flags. El "esperado" NUNCA lo escribe un humano: lo dicta Unicorn (plan/docs04 §1).
#
# Salida: tests/PakuPort.Tests/vectors/opcodes.json
#   [{ "name","code","ax","bx","cx","cf_in","out_ax","cf","pf","af","zf","sf","of","check":[...] }]
# 'check' lista los flags DEFINIDOS por el SDM para ese opcode + opcionalmente "ax" (resultado).
import json, os
from unicorn import *
from unicorn.x86_const import *

OUT = os.path.join(os.path.dirname(__file__), "..", "tests", "PakuPort.Tests", "vectors", "opcodes.json")
CF,PF,AF,ZF,SF,OF = 0,2,4,6,7,11

def run(code: bytes, ax: int, bx: int, cx: int, dx: int, cf_in: int, af_in: int = 0, n_insn: int = 1):
    mu = Uc(UC_ARCH_X86, UC_MODE_16)
    base = 0x1000
    mu.mem_map(base, 0x1000)
    mu.mem_write(base, code)
    mu.reg_write(UC_X86_REG_EAX, ax)
    mu.reg_write(UC_X86_REG_EBX, bx)
    mu.reg_write(UC_X86_REG_ECX, cx)
    mu.reg_write(UC_X86_REG_EDX, dx)
    mu.reg_write(UC_X86_REG_EFLAGS, 0x0002 | (1 if cf_in else 0) | (0x10 if af_in else 0))
    mu.emu_start(base, base + len(code), count=n_insn)
    out_ax = mu.reg_read(UC_X86_REG_EAX) & 0xFFFF
    out_dx = mu.reg_read(UC_X86_REG_EDX) & 0xFFFF
    fl = mu.reg_read(UC_X86_REG_EFLAGS)
    return out_ax, out_dx, fl

def bit(fl, b): return 1 if (fl >> b) & 1 else 0

def vec(name, code_hex, ax, bx, cx, check, cf_in=0, dx=0, af_in=0, n_insn=1):
    out_ax, out_dx, fl = run(bytes.fromhex(code_hex), ax, bx, cx, dx, cf_in, af_in, n_insn)
    return {"name": name, "code": code_hex, "ax": ax, "bx": bx, "cx": cx, "dx": dx,
            "cf_in": cf_in, "af_in": af_in,
            "out_ax": out_ax, "out_dx": out_dx,
            "cf": bit(fl, CF), "pf": bit(fl, PF), "af": bit(fl, AF),
            "zf": bit(fl, ZF), "sf": bit(fl, SF), "of": bit(fl, OF),
            "check": check}

ALL = ["ax", "cf", "pf", "af", "zf", "sf", "of"]
LOG = ["ax", "cf", "pf", "zf", "sf", "of"]          # logicas: AF indefinido
SH  = ["ax", "cf", "pf", "zf", "sf", "of"]          # shifts cuenta==1: AF indefinido
SH1 = ["ax", "cf", "pf", "zf", "sf"]                 # shifts cuenta!=1: AF y OF indefinidos
FLG = ["cf", "pf", "af", "zf", "sf", "of"]          # cmp/test: solo flags

P16 = [(0,0),(0xFFFF,1),(0x7FFF,1),(0x8000,0x8000),(0x1234,0xABCD),(0x00FF,1),
       (0xFFFF,0xFFFF),(0x0FFF,1),(0x7F00,0x0100),(0x8000,0x7FFF),(0xABCD,0x1111),(0x5555,0xAAAA)]
P8  = [(0x00,0x00),(0xFF,0x01),(0x7F,0x01),(0x80,0x80),(0x0F,0x01),(0xAB,0x55),(0x80,0x01),(0xFF,0xFF)]
SHV = [0x0000,0x0001,0x4000,0x8000,0xFFFF,0x1234,0x00FF,0x7FFF,0xC000,0x0080]

V = []
def add16(name, code, check=ALL):
    for a,b in P16: V.append(vec(name, code, a, b, 0, check))
def add8(name, code, check=ALL):
    for a,b in P8: V.append(vec(name, code, a, b, 0, check))

add16("add16", "01d8");  add8("add8", "00d8")
add16("sub16", "29d8");  add8("sub8", "28d8")
add16("and16", "21d8", LOG); add8("and8", "20d8", LOG)
add16("or16",  "09d8", LOG); add8("or8",  "08d8", LOG)
add16("xor16", "31d8", LOG); add8("xor8", "30d8", LOG)
add16("cmp16", "39d8", FLG); add8("cmp8", "38d8", FLG)
add16("test16","85d8", FLG); add8("test8","84d8", FLG)
# adc/sbb con carry-in 0 y 1
for a,b in P16:
    V.append(vec("adc16","11d8",a,b,0,ALL,0)); V.append(vec("adc16","11d8",a,b,0,ALL,1))
    V.append(vec("sbb16","19d8",a,b,0,ALL,0)); V.append(vec("sbb16","19d8",a,b,0,ALL,1))
for a,b in P8:
    V.append(vec("adc8","10d8",a,b,0,ALL,0)); V.append(vec("adc8","10d8",a,b,0,ALL,1))
    V.append(vec("sbb8","18d8",a,b,0,ALL,0)); V.append(vec("sbb8","18d8",a,b,0,ALL,1))
# inc/dec (CF preservado: probar con cf_in 0 y 1)
for a in [0,1,0x7FFF,0x8000,0xFFFF,0x00FF,0x0FFF]:
    V.append(vec("inc16","40",a,0,0,ALL,0)); V.append(vec("inc16","40",a,0,0,ALL,1))
    V.append(vec("dec16","48",a,0,0,ALL,0)); V.append(vec("dec16","48",a,0,0,ALL,1))
for a in [0,1,0x7F,0x80,0xFF,0x0F]:
    V.append(vec("inc8","fec0",a,0,0,ALL,0)); V.append(vec("dec8","fec8",a,0,0,ALL,0))
# neg
for a in [0,1,0x8000,0x7FFF,0xFFFF,0x0001,0x00FF]:
    V.append(vec("neg16","f7d8",a,0,0,ALL))
for a in [0,1,0x80,0x7F,0xFF]:
    V.append(vec("neg8","f6d8",a,0,0,ALL))
# shr / sar por 1
for v in SHV:
    V.append(vec("shl16_1","d1e0",v,0,0,SH))
    V.append(vec("shr16_1","d1e8",v,0,0,SH))
    V.append(vec("sar16_1","d1f8",v,0,0,SH))
# shift por CL (cuentas pequeñas). OF solo definido si cuenta==1.
for v in [0x1234,0x8001,0xFFFF]:
    for cnt in [1,3,7]:
        chk = SH if cnt == 1 else SH1
        V.append(vec("shl16_cl","d3e0",v,0,cnt,chk))
        V.append(vec("shr16_cl","d3e8",v,0,cnt,chk))
        V.append(vec("sar16_cl","d3f8",v,0,cnt,chk))

# --- rotaciones por 1 (rol/ror/rcl/rcr). Solo CF/OF; SF/ZF/PF/AF sin cambio. ---
ROT = ["ax","cf","of"]
for v in SHV:
    V.append(vec("rol16","d1c0",v,0,0,ROT))
    V.append(vec("ror16","d1c8",v,0,0,ROT))
    V.append(vec("rcl16","d1d0",v,0,0,ROT,0)); V.append(vec("rcl16","d1d0",v,0,0,ROT,1))
    V.append(vec("rcr16","d1d8",v,0,0,ROT,0)); V.append(vec("rcr16","d1d8",v,0,0,ROT,1))

# --- mul/imul/div/idiv (src en BX/BL; AX[/DX] implicitos). SF/ZF/PF/AF indefinidos. ---
MUL16 = ["ax","dx","cf","of"]   # mul/imul 16: resultado DX:AX + CF/OF
MUL8  = ["ax","cf","of"]        # mul/imul 8: resultado AX + CF/OF
DIV16 = ["ax","dx"]             # div/idiv 16: AX=cociente, DX=resto, flags indefinidos
DIV8  = ["ax"]                  # div/idiv 8: AL=cociente AH=resto (ambos en AX)
for a,b in [(0,5),(2,3),(0x1000,0x10),(0xFFFF,0xFFFF),(0x8000,2),(0x1234,0x100),(100,200),(0x7FFF,2)]:
    V.append(vec("mul16","f7e3",a,b,0,MUL16))       # mul bx
    V.append(vec("imul16","f7eb",a,b,0,MUL16))      # imul bx
for a,b in [(0,5),(2,3),(0x80,2),(0xFF,0xFF),(0x7F,2),(0x40,4)]:
    V.append(vec("mul8","f6e3",a,b,0,MUL8))         # mul bl
    V.append(vec("imul8","f6eb",a,b,0,MUL8))        # imul bl
# div16: dividendo DX:AX / BX. dx=0 garantiza que el cociente cabe en 16 bits.
for dx,ax,bx in [(0,100,7),(0,0xFFFF,2),(0,0x1234,0x10),(1,0,3),(2,0x3456,0x100),(0,5,5)]:
    V.append(vec("div16","f7f3",ax,bx,0,DIV16,dx=dx))
# idiv16: dx = extension de signo de ax → dividendo = (short)ax, cociente cabe.
for ax,bx in [(100,7),(0xFFFF,2),(0x8000,0x100),(0x1234,0x10),(0x7FFE,3),(0,5)]:
    dx = 0xFFFF if (ax & 0x8000) else 0
    V.append(vec("idiv16","f7fb",ax,bx,0,DIV16,dx=dx))
# div8: dividendo AX / BL, cociente <=0xFF.
for ax,bl in [(100,7),(0xFF,2),(0x1234&0xFF | 0x0100,3),(200,3),(0,5),(255,255)]:
    V.append(vec("div8","f6f3",ax,bl,0,DIV8))
# idiv8: AX (con signo) / BL.
for ax,bl in [(100,7),(0xFF80,3),(0x007F,2),(0xFFFF,2),(0,5),(0x7F,1)]:
    V.append(vec("idiv8","f6fb",ax,bl,0,DIV8))

# --- BCD/ASCII y lahf/sahf ---
BCD_AL = [0x00,0x05,0x09,0x0A,0x0F,0x10,0x19,0x55,0x99,0x9A,0xAA,0xFF]
DAA = ["ax","cf","af","sf","zf","pf"]   # daa/das: OF indefinido
ASC = ["ax","cf","af"]                   # aaa/aas: SF/ZF/PF/OF indefinidos
AAM = ["ax","sf","zf","pf"]              # aam/aad: CF/OF/AF indefinidos
for al in BCD_AL:
    for cf in (0,1):
        for af in (0,1):
            V.append(vec("daa","27",al,0,0,DAA,cf_in=cf,af_in=af))
            V.append(vec("das","2f",al,0,0,DAA,cf_in=cf,af_in=af))
            V.append(vec("aaa","37",al,0,0,ASC,cf_in=cf,af_in=af))
            V.append(vec("aas","3f",al,0,0,ASC,cf_in=cf,af_in=af))
for a in [0x0000,0x0005,0x0009,0x0010,0x0050,0x0063,0x00FF,0x1234,0x09FF]:
    V.append(vec("aam","d40a",a,0,0,AAM))    # aam 10
    V.append(vec("aad","d50a",a,0,0,AAM))    # aad 10
# lahf/sahf round-trip: cmp fija flags, lahf -> AH; luego se compara AH.
for a,b in P16:
    V.append(vec("lahf_after_cmp","39d89f",a,b,0,["ax"],n_insn=2))   # cmp ax,bx ; lahf -> AH=flags

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w") as f:
    json.dump(V, f, indent=1)
print(f"escritos {len(V)} vectores en {os.path.normpath(OUT)}")
