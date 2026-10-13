"""Sonda de velocidad: genera SPEED.COM (mismo binario para el oraculo DOSBox-X y PakuPort).

Por que existe: hay juegos sin NINGUNA fuente de tiempo (verificado sobre los bytes de uno
de prueba: cero int 1Ah, cero lecturas de 40:6C residentes, no engancha INT 8 ni INT 1Ch,
no lee el canal 0 del PIT ni el 3DA). Su velocidad es exactamente proporcional a las
instrucciones por segundo que reciban. Por tanto calibrar su `cyclesPerMs` exige saber
cuantas instrucciones por segundo entrega el oraculo con la config de referencia
(cycles=3000, core=normal) frente a las que entregamos nosotros. Nuestro `cyclesPerMs`
cuenta INSTRUCCIONES; la unidad `cycles` de DOSBox no tiene por que serlo: hay que MEDIRLO.

Que mide: iteraciones completadas en 18 ticks del BIOS (~0,989 s) por dos bucles de coste
muy distinto, ejecutados uno tras otro:

  bucle A (barato)  4 instrucciones/iteracion, solo registros + un cmp a memoria
  bucle B (caro)    8 instrucciones/iteracion, tres accesos a memoria + un MUL de palabra

Los dos juntos responden la pregunta de fondo: si el cociente oraculo/PakuPort es el mismo
en A que en B, el emulador cobra ~1 ciclo por instruccion y las unidades son comparables;
si difiere, cobra por coste real de instruccion y no hay factor unico.

Salida: SPEED.BIN (8 bytes) en el directorio montado
  +0  dword  iteraciones del bucle A
  +4  dword  iteraciones del bucle B

Ensamblado a mano (como el resto de arneses de la casa) y verificado con capstone.
"""
import os, struct, sys

D = os.path.dirname(os.path.abspath(__file__))
TICKS = 18  # 18 ticks del BIOS = 0,989 s

code = bytearray()
fix = []  # (offset_en_code, nombre_dato) -> parchear con la direccion final del dato


def emit(*bs):
    code.extend(bs)


def emit_data_ref(prefix, name, suffix=()):
    """Emite una instruccion cuyo operando es la direccion absoluta de un dato."""
    emit(*prefix)
    fix.append((len(code), name))
    emit(0x00, 0x00)
    if suffix:
        emit(*suffix)


# --- ES = 0040h (segmento de la BDA, donde vive el tick del BIOS en 0:046C) ---
emit(0xB8, 0x40, 0x00)              # mov ax, 0040h
emit(0x8E, 0xC0)                    # mov es, ax

# --- sincronizar con el flanco del tick y fijar BX = tick objetivo -------------
# Se ejecuta dos veces (una por bucle); se emite como subrutina.
sync = len(code) + 3                # la llamada va justo despues; se parchea abajo
emit(0xE8, 0x00, 0x00)              # call sync_tick  (rel16, se parchea)
call1 = len(code) - 2

# --- bucle A: 4 instrucciones por iteracion -----------------------------------
emit(0x33, 0xF6)                    # xor si, si
emit(0x33, 0xFF)                    # xor di, di
a_loop = len(code)
emit(0x46)                          # inc si
emit(0x75, 0x01)                    # jnz +1
emit(0x47)                          # inc di
emit(0x26, 0x3B, 0x1E, 0x6C, 0x00)  # cmp bx, es:[006Ch]
emit(0x75, (256 - (len(code) + 2 - a_loop)) & 0xFF)   # jne a_loop
emit_data_ref((0x89, 0x36), 'res_a')                  # mov [res_a], si
emit_data_ref((0x89, 0x3E), 'res_a2')                 # mov [res_a+2], di

# --- bucle B: 8 instrucciones por iteracion (memoria + MUL) -------------------
emit(0xE8, 0x00, 0x00)              # call sync_tick (rel16, se parchea)
call2 = len(code) - 2
emit(0x33, 0xF6)                    # xor si, si
emit(0x33, 0xFF)                    # xor di, di
b_loop = len(code)
emit_data_ref((0xA1,), 'buf')                         # mov ax, [buf]
emit_data_ref((0x03, 0x06), 'buf2')                   # add ax, [buf+2]
emit_data_ref((0xA3,), 'buf4')                        # mov [buf+4], ax
emit_data_ref((0xF7, 0x26), 'buf6')                   # mul word [buf+6]
emit(0x46)                          # inc si
emit(0x75, 0x01)                    # jnz +1
emit(0x47)                          # inc di
emit(0x26, 0x3B, 0x1E, 0x6C, 0x00)  # cmp bx, es:[006Ch]
emit(0x75, (256 - (len(code) + 2 - b_loop)) & 0xFF)   # jne b_loop
emit_data_ref((0x89, 0x36), 'res_b')                  # mov [res_b], si
emit_data_ref((0x89, 0x3E), 'res_b2')                 # mov [res_b+2], di

# --- escribir SPEED.BIN -------------------------------------------------------
emit_data_ref((0xBA,), 'fname')     # mov dx, fname
emit(0x33, 0xC9)                    # xor cx, cx
emit(0xB4, 0x3C)                    # mov ah, 3Ch (create)
emit(0xCD, 0x21)                    # int 21h
emit(0x8B, 0xD8)                    # mov bx, ax  (handle)
emit_data_ref((0xBA,), 'res_a')     # mov dx, res_a
emit(0xB9, 0x08, 0x00)              # mov cx, 8
emit(0xB4, 0x40)                    # mov ah, 40h (write)
emit(0xCD, 0x21)                    # int 21h
emit(0xB4, 0x3E)                    # mov ah, 3Eh (close)
emit(0xCD, 0x21)                    # int 21h
emit(0xB8, 0x00, 0x4C)              # mov ax, 4C00h
emit(0xCD, 0x21)                    # int 21h

# --- subrutina sync_tick: espera el flanco y deja BX = tick + TICKS -----------
sync_tick = len(code)
emit(0x26, 0x8B, 0x1E, 0x6C, 0x00)  # mov bx, es:[006Ch]
w = len(code)
emit(0x26, 0x3B, 0x1E, 0x6C, 0x00)  # cmp bx, es:[006Ch]
emit(0x74, (256 - (len(code) + 2 - w)) & 0xFF)        # je w   (esperar al flanco)
emit(0x26, 0x8B, 0x1E, 0x6C, 0x00)  # mov bx, es:[006Ch]
emit(0x83, 0xC3, TICKS)             # add bx, TICKS
emit(0xC3)                          # ret

# --- datos --------------------------------------------------------------------
data_off = len(code)
layout = {}


def put(name, raw):
    layout[name] = data_off + len(blob)
    blob.extend(raw)


blob = bytearray()
put('res_a', b'\x00\x00')
put('res_a2', b'\x00\x00')
put('res_b', b'\x00\x00')
put('res_b2', b'\x00\x00')
put('buf', b'\x39\x05')             # valores arbitrarios no nulos para el bucle B
put('buf2', b'\x17\x00')
put('buf4', b'\x00\x00')
put('buf6', b'\x03\x00')
put('fname', b'SPEED.BIN\x00')
code.extend(blob)

# --- parcheos -----------------------------------------------------------------
for off, name in fix:
    struct.pack_into('<H', code, off, 0x100 + layout[name])
struct.pack_into('<h', code, call1, sync_tick - (call1 + 2))
struct.pack_into('<h', code, call2, sync_tick - (call2 + 2))

out = os.path.join(D, 'SPEED.COM')
open(out, 'wb').write(bytes(code))
print(f'SPEED.COM: {len(code)} bytes (codigo {data_off}, datos {len(blob)})')
print(f'  bucle A = 4 instr/iter, bucle B = 8 instr/iter, ventana = {TICKS} ticks')

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_16
    md = Cs(CS_ARCH_X86, CS_MODE_16)
    print('--- desensamblado (capstone) ---')
    for i in md.disasm(bytes(code[:data_off]), 0x100):
        print(f'  {i.address:04X}  {i.bytes.hex():<12} {i.mnemonic} {i.op_str}')
except ImportError:
    print('capstone no disponible: desensamblado no verificado', file=sys.stderr)
