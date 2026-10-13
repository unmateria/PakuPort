#!/usr/bin/env python3
# Genera vectores de verdad de 32 BITS con UNICORN (oraculo INDEPENDIENTE) para el interprete
# de PakuPort. Mismo metodo que gen_opcode_vectors.py (que cubre 8/16 bits contra el LIFTER):
# el "esperado" NUNCA lo escribe un humano, lo dicta Unicorn ejecutando los BYTES REALES.
#
# Diferencias con el de 16 bits, y por que:
#   - El codigo se ENSAMBLA con keystone desde texto, no se escribe a mano en hex. Un opcode de
#     32 bits en modo 16 lleva prefijos 66/67 que son faciles de equivocar a mano.
#   - Se ejecuta en UC_MODE_16 (no MODE_32) a proposito: es EXACTAMENTE el mundo en el que corre
#     la mayoria de los juegos DOS (real mode + prefijo 0x66), asi que el vector valida tambien nuestro camino de
#     decodificacion con Iced en 16 bits. La semantica de la ALU no depende del modo.
#   - Se comparan los OCHO registros de 32 bits y dos ventanas de memoria (scratch y pila), no
#     solo AX/DX: un fallo de ancho se manifiesta en la mitad alta de un registro que el de 16
#     bits ni mira.
#
# Salida: tests/PakuPort.Tests/vectors/opcodes32.json
import json, os, sys

try:
    from unicorn import *
    from unicorn.x86_const import *
    from keystone import Ks, KS_ARCH_X86, KS_MODE_16
    from capstone import Cs, CS_ARCH_X86, CS_MODE_16
except ImportError as e:
    sys.exit("falta una dependencia (pip install unicorn keystone-engine): %s" % e)

OUT = os.path.join(os.path.dirname(__file__), "..", "tests", "PakuPort.Tests", "vectors", "opcodes32.json")

# --- mapa de memoria del banco de pruebas (identico en los dos mundos) ---
CODE = 0x2000          # el snippet
SCRATCH = 0x0100       # datos: 64 bytes con patron conocido
SCRATCH_LEN = 0x40
STACK_TOP = 0x1000     # SP inicial
STACK_WIN = 0x0F80     # ventana de pila comparada
STACK_WIN_LEN = 0x80

# Patron del scratch: bytes crecientes con el bit alto puesto en la 2a mitad, para que un
# acceso de 32 bits mal alineado o de ancho equivocado cambie el resultado de forma visible.
SCRATCH_DATA = bytes(((i * 37 + 11) ^ (0x80 if i >= 32 else 0)) & 0xFF for i in range(SCRATCH_LEN))

# Bits de EFLAGS que PakuPort modela (Cpu.FlagsWord/SetFlagsWord): CF PF AF ZF SF IF DF OF,
# el bit 1 reservado a 1, e IOPL/NT que se conservan. TF (0x100) NO se modela.
FLAGS_MASK = 0x7ED7

REGS = ["eax", "ebx", "ecx", "edx", "esi", "edi", "ebp", "esp"]
UC_REG = {
    "eax": UC_X86_REG_EAX, "ebx": UC_X86_REG_EBX, "ecx": UC_X86_REG_ECX, "edx": UC_X86_REG_EDX,
    "esi": UC_X86_REG_ESI, "edi": UC_X86_REG_EDI, "ebp": UC_X86_REG_EBP, "esp": UC_X86_REG_ESP,
}

KS = Ks(KS_ARCH_X86, KS_MODE_16)
MD = Cs(CS_ARCH_X86, CS_MODE_16)
_asm_cache = {}

def asm(src):
    """Ensambla en modo 16 bits y devuelve (bytes, desensamblado).

    TODO snippet se vuelve a DESENSAMBLAR con capstone y el texto viaja en el vector. No es
    adorno: keystone en modo 16 acepta `mov eax,[ebx*4+0x100]` y emite en silencio la forma de
    16 bits `mov eax,[0x100]` — sin prefijo 0x67 y sin el indice. Un vector asi habria dado
    VERDE probando otra instruccion. Los casos que keystone no sabe (`movsd` de cadena, que
    confunde con la SSE, y el direccionamiento de 32 bits) se escriben como `raw:<hex>`.
    """
    if src not in _asm_cache:
        if src.startswith("raw:"):
            code = bytes.fromhex(src[4:].split("#")[0].strip())
        else:
            out, _ = KS.asm(src, CODE)
            if not out:
                raise ValueError("keystone no ensambla: %r" % src)
            code = bytes(out)
        dis = " ; ".join(("%s %s" % (i.mnemonic, i.op_str)).strip() for i in MD.disasm(code, CODE))
        if not dis:
            raise ValueError("capstone no desensambla lo ensamblado: %r -> %s" % (src, code.hex()))
        _asm_cache[src] = (code, dis)
    return _asm_cache[src]

DEFAULT_IN = {"eax": 0, "ebx": 0, "ecx": 0, "edx": 0,
              "esi": 0, "edi": 0, "ebp": 0, "esp": STACK_TOP}

def run(code, regs_in, flags_in, n):
    mu = Uc(UC_ARCH_X86, UC_MODE_16)
    mu.mem_map(0x0000, 0x10000)
    mu.mem_write(CODE, code)
    mu.mem_write(SCRATCH, SCRATCH_DATA)
    for s in (UC_X86_REG_CS, UC_X86_REG_DS, UC_X86_REG_ES, UC_X86_REG_SS):
        mu.reg_write(s, 0)
    for r in REGS:
        mu.reg_write(UC_REG[r], regs_in[r])
    mu.reg_write(UC_X86_REG_EFLAGS, flags_in)
    mu.emu_start(CODE, CODE + len(code), count=n)
    out = {r: mu.reg_read(UC_REG[r]) & 0xFFFFFFFF for r in REGS}
    fl = mu.reg_read(UC_X86_REG_EFLAGS)
    mem = {
        "%X" % SCRATCH: mu.mem_read(SCRATCH, SCRATCH_LEN).hex(),
        "%X" % STACK_WIN: mu.mem_read(STACK_WIN, STACK_WIN_LEN).hex(),
    }
    return out, fl, mem

def bit(fl, b):
    return 1 if (fl >> b) & 1 else 0

V = []

def vec(name, src, check, n=1, masks=None, **regs):
    """Un vector: ensambla `src`, lo corre en Unicorn y guarda entrada+salida.

    check = flags DEFINIDOS por el SDM para ese opcode (los indefinidos no se comparan).
    Los 8 registros y las dos ventanas de memoria se comparan SIEMPRE (aplicando `masks`,
    que solo hace falta para pushfd, cuyos bits no modelados no son un fallo)."""
    regs_in = dict(DEFAULT_IN)
    flags_in = 0x0002
    for k, v in regs.items():
        if k == "flags":
            flags_in = v
        elif k in regs_in:
            regs_in[k] = v & 0xFFFFFFFF
        else:
            raise KeyError("registro desconocido en el vector: %s" % k)
    code, dis = asm(src)
    out, fl, mem = run(code, regs_in, flags_in, n)
    V.append({
        "name": name, "src": src, "dis": dis, "code": code.hex(), "n": n,
        "in": regs_in, "flags_in": flags_in,
        "out": out, "mem_out": mem,
        "masks": masks or {},
        "cf": bit(fl, 0), "pf": bit(fl, 2), "af": bit(fl, 4),
        "zf": bit(fl, 6), "sf": bit(fl, 7), "of": bit(fl, 11),
        "check": check,
    })

ALL = ["cf", "pf", "af", "zf", "sf", "of"]
LOG = ["cf", "pf", "zf", "sf", "of"]      # logicas: AF indefinido
SH1 = ["cf", "pf", "zf", "sf", "of"]      # shift/rot cuenta==1: AF indefinido
SHN = ["cf", "pf", "zf", "sf"]            # shift cuenta!=1: AF y OF indefinidos
ROTN = ["cf"]                             # rot cuenta!=1: solo CF (y no toca SZP)
ROT1 = ["cf", "of"]
NOFLAGS = []                              # mul/div: casi todo indefinido
CFOF = ["cf", "of"]
INCDEC = ["pf", "af", "zf", "sf", "of"]   # inc/dec no tocan CF

# Parejas de valores: cubren cero, signo, acarreo entre las dos mitades de 16 bits (el sitio
# exacto donde un helper de 16 bits mal ensanchado se rompe) y desbordamiento con signo.
P32 = [
    (0x00000000, 0x00000000), (0xFFFFFFFF, 0x00000001), (0x7FFFFFFF, 0x00000001),
    (0x80000000, 0x80000000), (0x12345678, 0x9ABCDEF0), (0x0000FFFF, 0x00000001),
    (0xFFFFFFFF, 0xFFFFFFFF), (0x7FFFFFFF, 0x7FFFFFFF), (0x0000FFFF, 0x00000001),
    (0x80000000, 0x7FFFFFFF), (0xDEADBEEF, 0x11111111), (0x55555555, 0xAAAAAAAA),
    (0x0000FFFF, 0x00010000), (0xFFFF0000, 0x00010000), (0x00008000, 0x00008000),
]

def add32(name, src, check=ALL):
    for a, b in P32:
        vec(name, src, check, eax=a, ebx=b)

# ---- ALU binaria de 32 bits ----
add32("add32", "add eax, ebx")
add32("sub32", "sub eax, ebx")
add32("and32", "and eax, ebx", LOG)
add32("or32",  "or eax, ebx",  LOG)
add32("xor32", "xor eax, ebx", LOG)
add32("cmp32", "cmp eax, ebx")
add32("test32", "test eax, ebx", LOG)
for a, b in P32:
    for cf in (0, 1):
        vec("adc32", "adc eax, ebx", ALL, eax=a, ebx=b, flags=0x0002 | cf)
        vec("sbb32", "sbb eax, ebx", ALL, eax=a, ebx=b, flags=0x0002 | cf)

# inmediatos: imm32 e imm8 con extension de signo (dos codificaciones distintas)
for a, _ in P32:
    vec("add32_imm32", "add eax, 0x9ABCDEF0", ALL, eax=a)
    vec("add32_imm8",  "add eax, -1", ALL, eax=a)
    vec("cmp32_imm8",  "cmp eax, 0x7F", ALL, eax=a)
    vec("and32_imm8",  "and eax, -16", LOG, eax=a)

# ---- unarias ----
for a in [0, 1, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0x0000FFFF, 0x00010000, 0xFFFF0000]:
    for cf in (0, 1):
        vec("inc32", "inc eax", INCDEC, eax=a, flags=0x0002 | cf)
        vec("dec32", "dec eax", INCDEC, eax=a, flags=0x0002 | cf)
    vec("neg32", "neg eax", ALL, eax=a)
    vec("not32", "not eax", [], eax=a)

# ---- desplazamientos y rotaciones ----
SHV32 = [0x00000000, 0x00000001, 0x40000000, 0x80000000, 0xFFFFFFFF,
         0x12345678, 0x0000FFFF, 0x7FFFFFFF, 0xC0000000, 0x00000080]
COUNTS = [0, 1, 4, 15, 16, 17, 31, 32, 33]

for v in SHV32:
    for n in COUNTS:
        chk = SH1 if n == 1 else ([] if n % 32 == 0 else SHN)
        vec("shl32", "shl eax, %d" % n, chk, eax=v)
        vec("shr32", "shr eax, %d" % n, chk, eax=v)
        vec("sar32", "sar eax, %d" % n, chk, eax=v)
        rchk = ROT1 if n == 1 else ([] if n % 32 == 0 else ROTN)
        vec("rol32", "rol eax, %d" % n, rchk, eax=v)
        vec("ror32", "ror eax, %d" % n, rchk, eax=v)
        for cf in (0, 1):
            vec("rcl32", "rcl eax, %d" % n, rchk, eax=v, flags=0x0002 | cf)
            vec("rcr32", "rcr eax, %d" % n, rchk, eax=v, flags=0x0002 | cf)
    # cuenta en CL
    for n in (1, 7, 31, 32):
        vec("shl32_cl", "shl eax, cl", SH1 if n == 1 else ([] if n % 32 == 0 else SHN), eax=v, ecx=n)
        vec("sar32_cl", "sar eax, cl", SH1 if n == 1 else ([] if n % 32 == 0 else SHN), eax=v, ecx=n)

# shld/shrd de 32 bits
for v in SHV32:
    for n in (1, 8, 16, 31):
        chk = SH1 if n == 1 else SHN
        vec("shld32", "shld eax, ebx, %d" % n, chk, eax=v, ebx=0x9ABCDEF0)
        vec("shrd32", "shrd eax, ebx, %d" % n, chk, eax=v, ebx=0x9ABCDEF0)

# ---- mul / div ----
for a, b in P32:
    vec("mul32", "mul ebx", CFOF, eax=a, ebx=b)
    vec("imul32", "imul ebx", CFOF, eax=a, ebx=b)
    vec("imul32_2", "imul eax, ebx", CFOF, eax=a, ebx=b)
    vec("imul32_3", "imul ecx, ebx, 0x1234", CFOF, ebx=b)

# div/idiv: divisores elegidos para que NUNCA falten (#DE aborta Unicorn y no es lo que se mide)
for edx, eax, ebx in [(0, 0x12345678, 0x1000), (0, 0xFFFFFFFF, 0xFFFF), (0, 1, 1),
                      (0, 0x7FFFFFFF, 3), (1, 0, 0x10000), (0, 0xDEADBEEF, 0x1234567)]:
    vec("div32", "div ebx", NOFLAGS, eax=eax, ebx=ebx, edx=edx)
for edx, eax, ebx in [(0, 0x12345678, 0x1000), (0, 1, 1), (0, 0x7FFFFFFF, 3),
                      (0xFFFFFFFF, 0xFFFFFFFF, 3), (0xFFFFFFFF, 0x80000000, 0x1234),
                      (0, 0x00FFFFFF, 0xFFFFFFFF)]:
    vec("idiv32", "idiv ebx", NOFLAGS, eax=eax, ebx=ebx, edx=edx)

# ---- extension de signo / cero ----
for a in [0, 1, 0x7FFF, 0x8000, 0xFFFF, 0x12345678, 0xFFFF8000, 0x00007FFF]:
    vec("cwde", "cwde", [], eax=a)
    vec("cdq", "cdq", [], eax=a, edx=0xAAAAAAAA)
    vec("cbw", "cbw", [], eax=a)          # anti-regresion: 16 bits, ya funcionaba
    vec("cwd", "cwd", [], eax=a, edx=0xAAAAAAAA)
    vec("movzx32_8", "movzx eax, bl", [], ebx=a)
    vec("movsx32_8", "movsx eax, bl", [], ebx=a)
    vec("movzx32_16", "movzx eax, bx", [], ebx=a)
    vec("movsx32_16", "movsx eax, bx", [], ebx=a)

# ---- bit test / bit scan ----
for v in [0, 1, 0x80000000, 0x12345678, 0xFFFFFFFF, 0x00010000]:
    for b in (0, 1, 15, 16, 31, 33):
        vec("bt32", "bt eax, %d" % b, ["cf"], eax=v)
        vec("bts32", "bts eax, %d" % b, ["cf"], eax=v)
        vec("btr32", "btr eax, %d" % b, ["cf"], eax=v)
        vec("btc32", "btc eax, %d" % b, ["cf"], eax=v)
    vec("bsf32", "bsf ecx, eax", ["zf"], eax=v, ecx=0xAAAAAAAA)
    vec("bsr32", "bsr ecx, eax", ["zf"], eax=v, ecx=0xAAAAAAAA)

# ---- mov / xchg / lea con registros de 32 bits ----
for a, b in P32[:8]:
    vec("mov32_rr", "mov eax, ebx", [], eax=a, ebx=b)
    vec("mov32_ri", "mov eax, 0x9ABCDEF0", [], eax=a)
    vec("xchg32", "xchg eax, ebx", [], eax=a, ebx=b)
    vec("lea32", "lea eax, [bx+si+4]", [], ebx=b & 0xFF, esi=a & 0xFF)

# ---- memoria: lecturas/escrituras de 32 bits y direccionamiento de 32 bits ----
for a in [0, 0x12345678, 0xFFFFFFFF, 0x80000000]:
    vec("mov32_load", "mov eax, dword ptr [0x100]", [], eax=0xAAAAAAAA)
    vec("mov32_store", "mov dword ptr [0x110], eax", [], eax=a)
    vec("add32_mem", "add dword ptr [0x100], eax", ALL, eax=a)
    vec("add32_rmem", "add eax, dword ptr [0x104]", ALL, eax=a)
    vec("xchg32_mem", "xchg dword ptr [0x108], eax", [], eax=a)
    vec("inc32_mem", "inc dword ptr [0x10C]", INCDEC, eax=a)
    # direccionamiento de 32 bits con escala (prefijo 0x67): el sitio donde MemEa se rompe
    vec("mov32_ea_scale", "raw:66678b049d00010000  # mov eax,[ebx*4+0x100]", [], ebx=3, eax=0xAAAAAAAA)
    vec("mov32_ea_base", "raw:66678b844b00010000  # mov eax,[ebx+ecx*2+0x100]", [], ebx=4, ecx=6, eax=0xAAAAAAAA)

# ---- pila de 32 bits ----
for a, b in P32[:6]:
    vec("push32_pop32", "push eax\npop ebx", [], n=2, eax=a, ebx=b)
    vec("push32_imm", "push 0x9ABCDEF0", [], eax=a)
    vec("push32_mem", "push dword ptr [0x100]", [], eax=a)
    # `push esp` apila el ESP de ANTES de la instruccion (386). Va con vector porque el camino
    # de 16 bits hace lo contrario a proposito (semantica 8086, ver el comentario en Interp).
    vec("push32_esp", "push esp\npop ebx", [], n=2, eax=a, ebx=b)
    vec("pushad_popad", "pushad\npopad", [], n=2, eax=a, ebx=b, ecx=0x11112222, edx=0x33334444,
        esi=0x55556666, edi=0x77778888, ebp=0x9999AAAA)
    vec("pushad", "pushad", [], eax=a, ebx=b, ecx=0x11112222, edx=0x33334444,
        esi=0x55556666, edi=0x77778888, ebp=0x9999AAAA)
    # push/pop de 16 bits: anti-regresion, no debe cambiar nada
    vec("push16_pop16", "push ax\npop bx", [], n=2, eax=a, ebx=b)

# ---- push/pop de REGISTRO DE SEGMENTO con tamano de operando de 32 bits (prefijo 0x66) ----
# El 386 mueve CUATRO bytes de pila aunque el registro tenga dos: al apilar rellena la parte
# alta con ceros y al desapilar lee solo la palabra baja y suma 4 (dosbox-x prefix_66.h
# `Push_32(SegValue(cs))`, cpu.cpp `CPU_PopSeg(seg,true)`). Tratarlo como el de 16 descuadra la
# pila en 2 bytes por instruccion, y eso puede no verse hasta mucho despues: si el retorno lo
# hace un `retfd` (que si desapila 8), la direccion de retorno del llamador se pierde.
# Los segmentos valen 0 en los dos mundos, asi que la senal esta en ESP y en la ventana de pila:
# cada snippet SIEMBRA antes 8 bytes con un patron, y el ancho equivocado deja distinto lo que
# queda sin machacar.
SIEMBRA = "66a3f80f 66a3fc0f"   # mov [0x0FF8],eax ; mov [0x0FFC],eax
for seg, pushd, popd, pushw, popw in (
        ("es", "6606", "6607", "06", "07"),
        ("cs", "660e", None,   "0e", None),   # push cs existe; pop cs no (386)
        ("ss", "6616", "6617", "16", "17"),
        ("ds", "661e", "661f", "1e", "1f"),
        ("fs", "660fa0", "660fa1", "0fa0", "0fa1"),
        ("gs", "660fa8", "660fa9", "0fa8", "0fa9")):
    vec("pushd_" + seg, "raw:%s %s  # push %s (32)" % (SIEMBRA, pushd, seg), [], n=3, eax=0xAABBCCDD)
    vec("pushw_" + seg, "raw:%s %s  # push %s (16)" % (SIEMBRA, pushw, seg), [], n=3, eax=0xAABBCCDD)
    if popd:
        # esp arranca 4 bytes por debajo del tope: el pop tiene que dejarlo EXACTAMENTE en 0x1000
        vec("popd_" + seg, "raw:%s %s  # pop %s (32)" % (SIEMBRA, popd, seg), [], n=3,
            eax=0x00001234, esp=STACK_TOP - 4)
        vec("popw_" + seg, "raw:%s %s  # pop %s (16)" % (SIEMBRA, popw, seg), [], n=3,
            eax=0x00001234, esp=STACK_TOP - 2)
    # La pareja completa: con el ancho equivocado el ciclo no cierra y ESP se queda desplazado.
    if popd:
        vec("pushd_popd_" + seg, "raw:%s %s  # push/pop %s (32)" % (pushd, popd, seg), [], n=2)

for fl in (0x0002, 0x0003, 0x08D5 | 0x0002, 0x0002 | 0x0400):
    # pushfd deja en la pila bits que no modelamos (TF); se compara enmascarado
    vec("pushfd_pop", "pushfd\npop eax", [], n=2, flags=fl, masks={"eax": FLAGS_MASK})
    vec("popfd", "push 0x00000CD5\npopfd", ALL, n=2, flags=fl)
    vec("pushf16_pop", "pushf\npop ax", [], n=2, flags=fl, masks={"eax": FLAGS_MASK})

# ---- instrucciones de cadena de 32 bits ----
for df in (0, 1):
    base_flags = 0x0002 | (0x400 if df else 0)
    si = 0x100 if not df else 0x100 + 4 * 3
    di = 0x120 if not df else 0x120 + 4 * 3
    # REP: `count` de Unicorn cuenta ITERACIONES, no instrucciones, asi que un rep con count=1
    # para a media copia mientras el interprete lo termina. Se le da margen de sobra y quien
    # corta de verdad es la direccion final del snippet (`until` de emu_start).
    vec("movsd_rep", "raw:f366a5  # rep movsd", [], n=64, flags=base_flags, esi=si, edi=di, ecx=4)
    vec("stosd_rep", "rep stosd", [], n=64, flags=base_flags, eax=0xDEADBEEF, edi=di, ecx=4)
    vec("lodsd", "lodsd", [], flags=base_flags, esi=si, eax=0xAAAAAAAA)
    vec("scasd", "scasd", ALL, flags=base_flags, eax=0xDEADBEEF, edi=di)
    vec("cmpsd", "cmpsd", ALL, flags=base_flags, esi=si, edi=di)
    vec("movsd", "raw:66a5  # movsd", [], flags=base_flags, esi=si, edi=di)
    # repne scasb / rep movsb: aparecen en el juego objetivo y son de 8 bits (anti-regresion)
    vec("repne_scasb", "repne scasb", ALL, n=64, flags=base_flags, eax=0x00000021, edi=0x100, ecx=32)
    vec("rep_movsb", "rep movsb", [], n=64, flags=base_flags, esi=0x100, edi=0x120, ecx=16)

# ---- jecxz / loop (ECX de 32 bits) ----
for ecx in (0, 1, 0x10000, 0xFFFFFFFF):
    # jecxz salta al hlt final; si no salta, ejecuta el inc
    # Los dos destinos caen en el `hlt` que cierra el snippet (o en un nop justo antes): un
    # salto hacia atras se colgaria en el interprete, que corre hasta el hlt y no por cuenta.
    vec("jecxz", "jecxz 0x2005\ninc eax\nnop", [], n=2, ecx=ecx, eax=0)
    vec("loop16", "loop 0x2002", [], n=1, ecx=ecx)

os.makedirs(os.path.dirname(OUT), exist_ok=True)
with open(OUT, "w") as f:
    json.dump(V, f, indent=1)
print("%d vectores de 32 bits -> %s" % (len(V), os.path.normpath(OUT)))
names = {}
for v in V:
    names[v["name"]] = names.get(v["name"], 0) + 1
print("familias: %d" % len(names))
