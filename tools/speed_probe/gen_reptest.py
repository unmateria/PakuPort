"""Sonda de REP: genera REPTEST.COM (mismo binario para el oraculo DOSBox-X y PakuPort).

Por que existe, y por que es un COM aparte de SPEED.COM: `tools\\speed_probe` demostro que
`cyclesPerMs=N` equivale a `cycles=N` al 1,0 %... con bucles SIN prefijo REP. Un diagnostico
posterior encontro que esa equivalencia se rompe justo ahi:

  - DOSBox-X `core=normal` descuenta un ciclo POR ELEMENTO dentro de todo bucle REP
    (`if ((--CPU_Cycles) <= 0) break;` en src\\cpu\\core_normal\\string.h, verificado en fuente).
  - Nuestro `Interp.StringOp` ejecuta el `while (c.CX != 0) { c.CX--; Body(); }` ENTERO dentro
    de un solo `Cpu.Step()`: **un `rep movsw` de 1000 palabras cuesta 1 tick, no 1000**.

Consecuencia medida en un juego de prueba, que pasa el 58 % de sus instrucciones en un blit
de pantalla completa hecho con cuatro `rep movsw`: redibuja 159 veces/s donde el original hace
~45, unas 22-25x de divergencia. Con REP gratis NO existe un `cyclesPerMs` bueno — si se baja
para que el blit vaya al ritmo del original, el computo puro va 20x lento, y al reves.

SPEED.COM no se toca: su `SPEED.ORACULO.BIN` es verdad terreno congelada y cambiar su codigo
invalidaria la comparacion. Este COM mide lo que a aquel se le escapaba.

Que mide: iteraciones completadas en 18 ticks del BIOS (~0,989 s) de un bucle cuyo cuerpo es un
`rep movsw` de 1000 palabras mas ~11 instrucciones de control. Coste esperado por iteracion:

  oraculo   ~11 + 1000 = ~1011 ciclos      ->  ~311 iteraciones/s a cycles=315
  nosotros  ~12 ticks                      ->  ~26000 iteraciones/s a cyclesPerMs=315  (~84x)

Salida: REPTEST.BIN (4 bytes, dword con las iteraciones) en el directorio de trabajo.
Ensamblado a mano y verificado con capstone.
"""
import os, struct, sys

D = os.path.dirname(os.path.abspath(__file__))
TICKS = 18
PALABRAS = 1000          # elementos por rep movsw
ORIGEN, DESTINO = 0x4000, 0x2000   # dentro del propio segmento del COM (64 KB nuestros)

code = bytearray()
fix = []


def emit(*bs):
    code.extend(bs)


def emit_ref(prefix, name, suffix=()):
    emit(*prefix)
    fix.append((len(code), name))
    emit(0x00, 0x00)
    if suffix:
        emit(*suffix)


# ES = DS = CS: el movsw copia dentro de nuestro propio segmento.
emit(0x0E)                          # push cs
emit(0x07)                          # pop es
emit(0xFC)                          # cld

emit(0xE8, 0x00, 0x00)              # call sync_tick
call1 = len(code) - 2

emit(0xBD, 0x00, 0x00)              # mov bp, 0        (contador bajo)
emit(0x33, 0xD2)                    # xor dx, dx       (contador alto)

loop = len(code)
emit(0xBE, ORIGEN & 0xFF, ORIGEN >> 8)      # mov si, ORIGEN
emit(0xBF, DESTINO & 0xFF, DESTINO >> 8)    # mov di, DESTINO
emit(0xB9, PALABRAS & 0xFF, PALABRAS >> 8)  # mov cx, PALABRAS
emit(0xF3, 0xA5)                    # rep movsw   <-- 1 tick para nosotros, PALABRAS en DOSBox
emit(0x45)                          # inc bp
emit(0x75, 0x01)                    # jnz +1
emit(0x42)                          # inc dx
# leer el tick del BIOS sin perder ES (que aqui vale CS, no 0x40)
emit(0x06)                          # push es
emit(0xB8, 0x40, 0x00)              # mov ax, 0040h
emit(0x8E, 0xC0)                    # mov es, ax
emit(0x26, 0xA1, 0x6C, 0x00)        # mov ax, es:[006Ch]
emit(0x07)                          # pop es
emit(0x3B, 0xC3)                    # cmp ax, bx
d = loop - (len(code) + 2)
emit(0x75, d & 0xFF)                # jne loop

emit_ref((0x89, 0x2E), 'res')       # mov [res], bp
emit_ref((0x89, 0x16), 'res2')      # mov [res+2], dx

# escribir REPTEST.BIN
emit_ref((0xBA,), 'fname')          # mov dx, fname
emit(0x33, 0xC9)                    # xor cx, cx
emit(0xB4, 0x3C)                    # mov ah, 3Ch
emit(0xCD, 0x21)                    # int 21h
emit(0x8B, 0xD8)                    # mov bx, ax
emit_ref((0xBA,), 'res')            # mov dx, res
emit(0xB9, 0x04, 0x00)              # mov cx, 4
emit(0xB4, 0x40)                    # mov ah, 40h
emit(0xCD, 0x21)                    # int 21h
emit(0xB4, 0x3E)                    # mov ah, 3Eh
emit(0xCD, 0x21)                    # int 21h
emit(0xB8, 0x00, 0x4C)              # mov ax, 4C00h
emit(0xCD, 0x21)                    # int 21h

# sync_tick: espera el flanco y deja BX = tick + TICKS (preserva ES)
sync_tick = len(code)
emit(0x06)                          # push es
emit(0xB8, 0x40, 0x00)              # mov ax, 0040h
emit(0x8E, 0xC0)                    # mov es, ax
emit(0x26, 0x8B, 0x1E, 0x6C, 0x00)  # mov bx, es:[006Ch]
w = len(code)
emit(0x26, 0x3B, 0x1E, 0x6C, 0x00)  # cmp bx, es:[006Ch]
emit(0x74, (256 - (len(code) + 2 - w)) & 0xFF)   # je w
emit(0x26, 0x8B, 0x1E, 0x6C, 0x00)  # mov bx, es:[006Ch]
emit(0x83, 0xC3, TICKS)             # add bx, TICKS
emit(0x07)                          # pop es
emit(0xC3)                          # ret

data_off = len(code)
blob = bytearray()
layout = {}


def put(name, raw):
    layout[name] = data_off + len(blob)
    blob.extend(raw)


put('res', b'\x00\x00')
put('res2', b'\x00\x00')
put('fname', b'REPTEST.BIN\x00')
code.extend(blob)

for off, name in fix:
    struct.pack_into('<H', code, off, 0x100 + layout[name])
struct.pack_into('<h', code, call1, sync_tick - (call1 + 2))

open(os.path.join(D, 'REPTEST.COM'), 'wb').write(bytes(code))
print(f'REPTEST.COM: {len(code)} bytes (codigo {data_off}, datos {len(blob)})')
print(f'  rep movsw de {PALABRAS} palabras por iteracion, ventana {TICKS} ticks')

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_16
    md = Cs(CS_ARCH_X86, CS_MODE_16)
    print('--- desensamblado (capstone) ---')
    for i in md.disasm(bytes(code[:data_off]), 0x100):
        print(f'  {i.address:04X}  {i.bytes.hex():<12} {i.mnemonic} {i.op_str}')
except ImportError:
    print('capstone no disponible: desensamblado no verificado', file=sys.stderr)
