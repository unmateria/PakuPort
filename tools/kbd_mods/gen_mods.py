"""Sonda de MODIFICADORES DE TECLADO: genera MODS.COM (mismo binario para el oraculo y PakuPort).

Por que existe: al cazar el gancho INT 15h AH=4Fh de un juego de prueba salio a la luz un
agujero del HOST del port. Su tabla `MapKey` no tenia entrada para Shift, Ctrl, Alt, CapsLock,
NumLock ni ScrollLock: devolvia (0,0) y el `if (sc != 0)` del bucle de entrada las tiraba. O sea:

  * un juego que lee scancodes crudos del 0x60 (los que instalan su propia ISR de INT 9)
    NO PODIA VER esas teclas — no llegaba ni el make ni el break;
  * el INT 9 del BIOS jamas recibia 2Ah/1Dh/38h, asi que los flags de teclado de la BDA
    (0040:0017) se quedaban a cero PARA SIEMPRE: INT 16h AH=02 mentia, y el propio BIOS no
    aplicaba Shift ni Ctrl al meter la tecla en su buffer.

Lo unico que disimulaba el agujero es que el host calculaba el ASCII de las mayusculas por su
cuenta antes de encolarlo en el buffer privado de su INT 16h. Todo lo demas estaba muerto.

Que mide MODS.COM, sin robar el INT 9 (lo observa desde el gancho INT 15h AH=4Fh, que ya es
verde por tools/kbd_int15, y devuelve CF=1 para que el BIOS siga su curso normal):

  1. la secuencia CRUDA de scancodes que el BIOS ve llegar (hasta 24), con sus make y break;
  2. el byte de flags 0040:0017 en el instante en que llega cada techa NO modificadora
     (hasta 3) — es decir, con el modificador aun pulsado;
  3. las entradas que el BIOS deja en su buffer de teclado, leidas de la BDA a pelo (hasta 3).

El arnes teclea `%a&` `*b_` `=c|` con PAKU_AUTOKEYS: Shift+A, Ctrl+B, Alt+C manteniendo el
modificador. Esos pares pasan por la MISMA MapKey que una pulsacion real, asi que la sonda mide
el camino del usuario y no un atajo del arnes.

Salida: MODS.BIN (34 bytes) en el directorio de trabajo.
  +0       byte  numero de scancodes registrados
  +1..24   bytes scancodes en orden de llegada
  +25..27  bytes flags 0040:0017 en la 1a, 2a y 3a tecla no modificadora
  +28..33  words las 3 primeras entradas del buffer de la BDA (0 = ninguna)

Ensamblado a mano; se verifica con capstone si esta instalado.
"""
import os

D = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100
MAX_SCAN = 24
TIMEOUT_TICKS = 600          # ~33 s de margen para las tres secuencias del arnes


class Asm:
    """Mini ensamblador de dos pasadas: emite bytes y resuelve etiquetas abs16 y rel8."""

    def __init__(self):
        self.code = bytearray()
        self.labels = {}
        self.fix16 = []
        self.fix8 = []

    def label(self, name):
        self.labels[name] = ORG + len(self.code)

    def db(self, *bs):
        self.code.extend(bs)

    def a16(self, label):
        self.fix16.append((len(self.code), label))
        self.code.extend((0, 0))

    def r8(self, label):
        self.fix8.append((len(self.code), label))
        self.code.append(0)

    def resolve(self):
        for pos, lab in self.fix16:
            v = self.labels[lab]
            self.code[pos] = v & 0xFF
            self.code[pos + 1] = v >> 8
        for pos, lab in self.fix8:
            d = self.labels[lab] - (ORG + pos + 1)
            assert -128 <= d <= 127, f'salto fuera de rango a {lab}: {d}'
            self.code[pos] = d & 0xFF
        return bytes(self.code)


a = Asm()
a.db(0xEB); a.r8('main')          # jmp main

# ─────────────────────────────────────────────────────────────────────────────────────────
# Driver de INT 15h: observa cada scancode y deja que el BIOS siga (CF=1)
# ─────────────────────────────────────────────────────────────────────────────────────────
a.label('handler')
a.db(0x80, 0xFC, 0x4F)                     # cmp ah,4Fh
a.db(0x75); a.r8('chain')                  # jne chain
a.db(0x53)                                 # push bx
a.db(0x2E, 0x8B, 0x1E); a.a16('n')         # mov bx,cs:[n]
a.db(0x83, 0xFB, MAX_SCAN)                 # cmp bx,24
a.db(0x73); a.r8('no_reg')                 # jae no_reg          (buffer lleno)
a.db(0x2E, 0x88, 0x87); a.a16('scans')     # mov cs:[bx+scans],al
a.db(0x2E, 0xFF, 0x06); a.a16('n')         # inc word cs:[n]
a.label('no_reg')
# ¿es una tecla normal (make, no modificador)? entonces fotografiar los flags de la BDA
a.db(0xA8, 0x80)                           # test al,80h
a.db(0x75); a.r8('fin')                    # jnz fin             (break code)
for sc in (0x2A, 0x36, 0x1D, 0x38, 0x3A, 0x45, 0x46):
    a.db(0x3C, sc)                         # cmp al,sc
    a.db(0x74); a.r8('fin')                # je fin              (modificador: no fotografiar)
a.db(0x2E, 0x8B, 0x1E); a.a16('nf')        # mov bx,cs:[nf]
a.db(0x83, 0xFB, 0x03)                     # cmp bx,3
a.db(0x73); a.r8('fin')                    # jae fin
a.db(0x50)                                 # push ax
a.db(0x06)                                 # push es
a.db(0xB8, 0x40, 0x00)                     # mov ax,40h
a.db(0x8E, 0xC0)                           # mov es,ax
a.db(0x26, 0xA0, 0x17, 0x00)               # mov al,es:[17h]     (flags de teclado del BIOS)
a.db(0x2E, 0x88, 0x87); a.a16('flags')     # mov cs:[bx+flags],al
a.db(0x2E, 0xFF, 0x06); a.a16('nf')        # inc word cs:[nf]
a.db(0x07)                                 # pop es
a.db(0x58)                                 # pop ax
a.label('fin')
a.db(0x5B)                                 # pop bx
a.db(0x55)                                 # push bp
a.db(0x89, 0xE5)                           # mov bp,sp
a.db(0x80, 0x4E, 0x06, 0x01)               # or byte [bp+6],1    (CF=1: que el BIOS la procese)
a.db(0x5D)                                 # pop bp
a.db(0xCF)                                 # iret
a.label('chain')
a.db(0x2E, 0xFF, 0x2E); a.a16('oldvec')    # jmp far cs:[oldvec]

# ─────────────────────────────────────────────────────────────────────────────────────────
# Programa
# ─────────────────────────────────────────────────────────────────────────────────────────
a.label('main')
a.db(0xFB)                        # sti                (sin IF no hay ni IRQ1 ni tick)
a.db(0xB8, 0x15, 0x35)            # mov ax,3515h
a.db(0xCD, 0x21)                  # int 21h            -> ES:BX
a.db(0x89, 0x1E); a.a16('oldvec')
a.db(0x8C, 0x06); a.a16('oldvec_seg')
a.db(0xBA); a.a16('handler')      # mov dx,handler
a.db(0xB8, 0x15, 0x25)            # mov ax,2515h
a.db(0xCD, 0x21)                  # int 21h

# vaciar el buffer del BIOS (head=tail) para que las entradas que se lean sean las del arnes
a.db(0xB8, 0x40, 0x00)            # mov ax,40h
a.db(0x8E, 0xC0)                  # mov es,ax
a.db(0x26, 0xA1, 0x1C, 0x00)      # mov ax,es:[1Ch]
a.db(0x26, 0xA3, 0x1A, 0x00)      # mov es:[1Ah],ax

# esperar a que lleguen los tres tríos o a que se agote el tiempo
a.db(0x26, 0xA1, 0x6C, 0x00)      # mov ax,es:[6Ch]
a.db(0x2E, 0xA3); a.a16('t0')     # mov cs:[t0],ax
a.label('wait')
a.db(0x2E, 0x83, 0x3E); a.a16('nf'); a.db(0x03)      # cmp word cs:[nf],3
a.db(0x74); a.r8('done')                             # je done
a.db(0x26, 0xA1, 0x6C, 0x00)      # mov ax,es:[6Ch]
a.db(0x2E, 0x2B, 0x06); a.a16('t0')                  # sub ax,cs:[t0]
a.db(0x3D, TIMEOUT_TICKS & 0xFF, TIMEOUT_TICKS >> 8) # cmp ax,TIMEOUT
a.db(0x72); a.r8('wait')                             # jb wait
a.label('done')
# margen para que entren los break codes que cierran la secuencia final (~2 ticks)
a.db(0x26, 0xA1, 0x6C, 0x00)      # mov ax,es:[6Ch]
a.db(0x2E, 0xA3); a.a16('t0')     # mov cs:[t0],ax
a.label('wait2')
a.db(0x26, 0xA1, 0x6C, 0x00)      # mov ax,es:[6Ch]
a.db(0x2E, 0x2B, 0x06); a.a16('t0')
a.db(0x3D, 0x08, 0x00)            # cmp ax,8
a.db(0x72); a.r8('wait2')         # jb wait2

# sacar hasta 3 entradas del buffer de la BDA (a pelo, sin INT 16h)
for i in range(3):
    a.db(0x26, 0x8B, 0x1E, 0x1A, 0x00)     # mov bx,es:[1Ah]     (head)
    a.db(0x26, 0x3B, 0x1E, 0x1C, 0x00)     # cmp bx,es:[1Ch]     (tail)
    a.db(0x74); a.r8(f'vacio{i}')          # je vacio
    a.db(0x26, 0x8B, 0x07)                 # mov ax,es:[bx]
    a.db(0x83, 0xC3, 0x02)                 # add bx,2
    a.db(0x83, 0xFB, 0x3E)                 # cmp bx,3Eh          (fin del buffer circular)
    a.db(0x72); a.r8(f'nowrap{i}')         # jb nowrap
    a.db(0xBB, 0x1E, 0x00)                 # mov bx,1Eh
    a.label(f'nowrap{i}')
    a.db(0x26, 0x89, 0x1E, 0x1A, 0x00)     # mov es:[1Ah],bx     (avanzar head)
    a.db(0xEB); a.r8(f'store{i}')          # jmp store
    a.label(f'vacio{i}')
    a.db(0x31, 0xC0)                       # xor ax,ax
    a.label(f'store{i}')
    a.db(0xA3); a.a16(f'bda{i}')           # mov [bda_i],ax

# restaurar el vector y volcar
a.db(0x2E, 0xC5, 0x16); a.a16('oldvec')   # lds dx,cs:[oldvec]
a.db(0xB8, 0x15, 0x25)            # mov ax,2515h
a.db(0xCD, 0x21)                  # int 21h
a.db(0x0E)                        # push cs
a.db(0x1F)                        # pop ds
a.db(0xA1); a.a16('n')            # mov ax,[n]
a.db(0xA2); a.a16('nout')         # mov [nout],al
a.db(0xB8, 0x00, 0x3C)            # mov ax,3C00h
a.db(0x31, 0xC9)                  # xor cx,cx
a.db(0xBA); a.a16('fname')        # mov dx,fname
a.db(0xCD, 0x21)                  # int 21h
a.db(0x89, 0xC3)                  # mov bx,ax
a.db(0xB4, 0x40)                  # mov ah,40h
a.db(0xB9, 0x22, 0x00)            # mov cx,34
a.db(0xBA); a.a16('nout')         # mov dx,nout
a.db(0xCD, 0x21)                  # int 21h
a.db(0xB4, 0x3E)                  # mov ah,3Eh
a.db(0xCD, 0x21)                  # int 21h
a.db(0xB8, 0x00, 0x4C)            # mov ax,4C00h
a.db(0xCD, 0x21)                  # int 21h

# ── datos: el bloque de salida va contiguo (nout, scans, flags, bda0..2) ─────────────────
a.label('nout');  a.db(0)
a.label('scans'); a.db(*([0] * MAX_SCAN))
a.label('flags'); a.db(0, 0, 0)
a.label('bda0');  a.db(0, 0)
a.label('bda1');  a.db(0, 0)
a.label('bda2');  a.db(0, 0)
a.label('n');     a.db(0, 0)
a.label('nf');    a.db(0, 0)
a.label('t0');    a.db(0, 0)
a.label('oldvec'); a.db(0, 0)
a.label('oldvec_seg'); a.db(0, 0)
a.label('fname'); a.db(*b'MODS.BIN\x00')

blob = a.resolve()
ruta = os.path.join(D, 'MODS.COM')
open(ruta, 'wb').write(blob)
print(f'MODS.COM: {len(blob)} bytes -> {ruta}')
assert a.labels['scans'] == a.labels['nout'] + 1, 'el bloque de salida debe ser contiguo'
assert a.labels['bda2'] + 2 - a.labels['nout'] == 34, 'el bloque de salida debe medir 34 bytes'

try:
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
    fin = a.labels['nout'] - ORG
    print('--- desensamblado (verificacion) ---')
    for i in md.disasm(blob[:fin], ORG):
        print('%04X  %-18s %s %s' % (i.address, i.bytes.hex(), i.mnemonic, i.op_str))
except ImportError:
    print('(capstone no instalado: sin verificacion del desensamblado)')
