"""Sonda de DETECCION DE RATON: genera MDET.COM (mismo binario para el oraculo y PakuPort).

Por que existe: una prueba manual de un juego que se maneja solo con raton informo de que el
raton "no funciona en absoluto", y la traza PAKU_LOG_INT33 no registraba NI UNA llamada al
driver. La razon esta en el propio ejecutable del juego (bytes verificados):

    mov ax,3533h      ; leer el vector de INT 33h
    int 21h           ; -> ES:BX
    mov ax,es
    or  ax,bx
    jz  sin_raton     ; vector nulo
    mov bl,es:[bx]
    cmp bl,0CFh
    jne hay_raton     ; <-- primer byte 0xCF => "no hay driver instalado"

Es la comprobacion canonica (Ralf Brown, INT 33h): vector no nulo Y primer byte distinto de CF,
porque un vector que apunta a un IRET pelado es un vector SIN driver detras. PakuPort ponia
exactamente 0xCF en su stub de la IVT (F000:00nn), asi que cualquier juego que se detecte asi
concluye que la maquina no tiene raton, y ni siquiera llega a llamar al driver.

Que mide MDET.COM, en este orden:
  1. el vector de INT 33h (segmento y offset) tal y como lo devuelve INT 21h AH=35h,
  2. el PRIMER BYTE al que apunta ese vector  <-- el dato que decide,
  3. la respuesta al reset del driver (INT 33h AX=0000 -> AX=FFFF, BX=nº de botones),
     que sirve de control: si esto ya fallara, el problema seria otro y no la deteccion.

Salida: MDET.BIN (9 bytes) en el directorio de trabajo.
  +0 word  segmento del vector
  +2 word  offset del vector
  +4 byte  primer byte apuntado
  +5 word  AX tras el reset
  +7 word  BX tras el reset

Ensamblado a mano; se verifica con capstone si esta instalado.
"""
import os, sys

D = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100

code = bytearray()
fix = []          # (posicion del hueco, nombre de la etiqueta de datos)


def emit(*bs):
    code.extend(bs)


def emit_ref(prefix, label, suffix=()):
    """Emite `prefix`, un hueco de 16 bits para la direccion de `label`, y `suffix`."""
    emit(*prefix)
    fix.append((len(code), label))
    emit(0x00, 0x00)
    if suffix:
        emit(*suffix)


emit(0x0E)                                   # push cs
emit(0x1F)                                   # pop  ds        (COM: DS ya = CS, explicito igualmente)

# --- 1 y 2: el vector de INT 33h y el byte al que apunta ---
emit(0xB8, 0x33, 0x35)                       # mov ax,3533h
emit(0xCD, 0x21)                             # int 21h        -> ES:BX
emit(0x8C, 0xC0)                             # mov ax,es
emit_ref((0xA3,), 'buf')                     # mov [buf+0],ax
emit_ref((0x89, 0x1E), 'buf+2')              # mov [buf+2],bx
emit(0x26, 0x8A, 0x07)                       # mov al,es:[bx]
emit_ref((0xA2,), 'buf+4')                   # mov [buf+4],al

# --- 3: control, el driver responde al reset ---
emit(0x31, 0xC0)                             # xor ax,ax
emit(0xCD, 0x33)                             # int 33h
emit_ref((0xA3,), 'buf+5')                   # mov [buf+5],ax
emit_ref((0x89, 0x1E), 'buf+7')              # mov [buf+7],bx

# --- volcar MDET.BIN ---
emit(0xB4, 0x3C)                             # mov ah,3Ch     (crear fichero)
emit(0x31, 0xC9)                             # xor cx,cx
emit_ref((0xBA,), 'nombre')                  # mov dx,nombre
emit(0xCD, 0x21)                             # int 21h        -> AX = handle
emit(0x89, 0xC3)                             # mov bx,ax
emit(0xB4, 0x40)                             # mov ah,40h     (escribir)
emit(0xB9, 0x09, 0x00)                       # mov cx,9
emit_ref((0xBA,), 'buf')                     # mov dx,buf
emit(0xCD, 0x21)                             # int 21h
emit(0xB4, 0x3E)                             # mov ah,3Eh     (cerrar)
emit(0xCD, 0x21)                             # int 21h
emit(0xB8, 0x00, 0x4C)                       # mov ax,4C00h
emit(0xCD, 0x21)                             # int 21h

# --- datos ---
labels = {}
labels['nombre'] = ORG + len(code)
code.extend(b'MDET.BIN\x00')
labels['buf'] = ORG + len(code)
code.extend(b'\x00' * 9)
for extra in (2, 4, 5, 7):
    labels[f'buf+{extra}'] = labels['buf'] + extra

for pos, label in fix:
    addr = labels[label]
    code[pos] = addr & 0xFF
    code[pos + 1] = (addr >> 8) & 0xFF

ruta = os.path.join(D, 'MDET.COM')
open(ruta, 'wb').write(bytes(code))
print(f'MDET.COM: {len(code)} bytes -> {ruta}')

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
