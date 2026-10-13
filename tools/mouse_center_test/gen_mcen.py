"""Sonda del CENTRADO DEL RESET del raton: genera MCEN.COM (mismo binario para el oraculo
y PakuPort).

Por que existe: una prueba manual de un juego de modo 13h informo de que el raton "no circula
por toda la pantalla, no llega al lado derecho". La causa esta escrita en el propio ejecutable
del juego (bytes verificados): el juego AUTODETECTA en que unidades habla el driver leyendo la
posicion del cursor JUSTO DESPUES del reset.

    mov word [flag],0     ; asume driver en pixeles
    mov ax,3
    int 33h               ; -> CX = posicion X tras el reset
    cmp cx,00A0h          ; 160 = centro de una pantalla de 320
    je  es_1x
    mov word [flag],1     ; no es 160 -> el driver habla virtual-640

A partir de ahi, con el flag a 1 el juego hace `shl cx,1` al FIJAR rango y posicion, y `shr`
al leerlas. Si el driver no centra el cursor en el reset, el juego lee 0, deduce "virtual-640"
y luego divide entre dos todo lo que le llega: **el cursor solo alcanza la mitad de la
pantalla**. Ese es el sintoma exacto del parte.

DOSBox-X src/ints/mouse.cpp, Mouse_Reset:

    mouse.x = static_cast<float>((mouse.max_x + 1)/ 2);
    mouse.y = static_cast<float>((mouse.max_y + 1)/ 2);

Que mide MCEN.COM, en modo 13h (el del juego que lo destapo), en este orden:
  1. reset del driver (INT 33h AX=0000) -> AX, que sirve de control,
  2. el rango virtual que declara el driver (AX=0026) -> max_x, max_y,
  3. la posicion del cursor (AX=0003) -> CX, DX  <-- el dato que decide.

Salida: MCEN.BIN (10 bytes) en el directorio de trabajo.
  +0 word  max_x declarado por el driver (AX=0026 -> CX)
  +2 word  max_y declarado por el driver (AX=0026 -> DX)
  +4 word  posicion X tras el reset (AX=0003 -> CX)
  +6 word  posicion Y tras el reset (AX=0003 -> DX)
  +8 word  AX del reset (control: FFFF = driver instalado)

Ensamblado a mano; se verifica con capstone si esta instalado.
"""
import os, sys

D = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100

code = bytearray()
fix = []          # (posicion del hueco, nombre de la etiqueta de datos)


def emit(*bs):
    code.extend(bs)


def emit_ref(prefix, label):
    """Emite `prefix` y un hueco de 16 bits para la direccion de `label`."""
    emit(*prefix)
    fix.append((len(code), label))
    emit(0x00, 0x00)


emit(0x0E)                                   # push cs
emit(0x1F)                                   # pop  ds        (COM: DS ya = CS, explicito igualmente)

# --- modo 13h: el driver reajusta su rango al cambiar de modo (Mouse_AfterNewVideoMode) ---
emit(0xB8, 0x13, 0x00)                       # mov ax,0013h
emit(0xCD, 0x10)                             # int 10h

# --- 1: reset del driver (control) ---
emit(0x31, 0xC0)                             # xor ax,ax
emit(0xCD, 0x33)                             # int 33h
emit_ref((0xA3,), 'buf+8')                   # mov [buf+8],ax

# --- 2: rango virtual declarado por el driver ---
emit(0xB8, 0x26, 0x00)                       # mov ax,0026h
emit(0xCD, 0x33)                             # int 33h        -> CX=max_x, DX=max_y
emit_ref((0x89, 0x0E), 'buf')                # mov [buf+0],cx
emit_ref((0x89, 0x16), 'buf+2')              # mov [buf+2],dx

# --- 3: posicion del cursor tras el reset  <-- el dato que decide ---
emit(0xB8, 0x03, 0x00)                       # mov ax,3
emit(0xCD, 0x33)                             # int 33h        -> CX=x, DX=y
emit_ref((0x89, 0x0E), 'buf+4')              # mov [buf+4],cx
emit_ref((0x89, 0x16), 'buf+6')              # mov [buf+6],dx

# --- volver a texto para no dejar la pantalla en grafico ---
emit(0xB8, 0x03, 0x00)                       # mov ax,0003h
emit(0xCD, 0x10)                             # int 10h

# --- volcar MCEN.BIN ---
emit(0xB4, 0x3C)                             # mov ah,3Ch     (crear fichero)
emit(0x31, 0xC9)                             # xor cx,cx
emit_ref((0xBA,), 'nombre')                  # mov dx,nombre
emit(0xCD, 0x21)                             # int 21h        -> AX = handle
emit(0x89, 0xC3)                             # mov bx,ax
emit(0xB4, 0x40)                             # mov ah,40h     (escribir)
emit(0xB9, 0x0A, 0x00)                       # mov cx,10
emit_ref((0xBA,), 'buf')                     # mov dx,buf
emit(0xCD, 0x21)                             # int 21h
emit(0xB4, 0x3E)                             # mov ah,3Eh     (cerrar)
emit(0xCD, 0x21)                             # int 21h
emit(0xB8, 0x00, 0x4C)                       # mov ax,4C00h
emit(0xCD, 0x21)                             # int 21h

# --- datos ---
labels = {}
labels['nombre'] = ORG + len(code)
code.extend(b'MCEN.BIN\x00')
labels['buf'] = ORG + len(code)
code.extend(b'\x00' * 10)
for extra in (2, 4, 6, 8):
    labels[f'buf+{extra}'] = labels['buf'] + extra

for pos, label in fix:
    addr = labels[label]
    code[pos] = addr & 0xFF
    code[pos + 1] = (addr >> 8) & 0xFF

ruta = os.path.join(D, 'MCEN.COM')
open(ruta, 'wb').write(bytes(code))
print(f'MCEN.COM: {len(code)} bytes -> {ruta}')

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_16
    md = Cs(CS_ARCH_X86, CS_MODE_16)
    fin_codigo = labels['nombre'] - ORG
    n = 0
    for ins in md.disasm(bytes(code[:fin_codigo]), ORG):
        print(f'  {ins.address:04x}  {ins.mnemonic:6} {ins.op_str}')
        n += ins.size
    if n != fin_codigo:
        sys.exit(f'ROJO: capstone solo decodifico {n} de {fin_codigo} bytes de codigo')
    print('capstone: codigo integro')
except ImportError:
    print('capstone no instalado: COM sin verificar')
