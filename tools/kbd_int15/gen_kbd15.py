"""Sonda del GANCHO DE TECLADO INT 15h AH=4Fh: genera KBD15.COM (mismo binario para el
oraculo y PakuPort).

Por que existe: una prueba manual de un juego 3D informo de que funcionaba perfectamente salvo
las teclas. Medido en PakuPort con el juego en marcha: no lee el puerto 0x60 (cero lecturas propias), no
llama a INT 16h ni una vez, y nadie consume del buffer de teclado de la BDA (PAKU_MEMWATCH
sobre 0040:001A: cero escrituras). El volcado de su memoria viva lo explica:

    INT 15h -> (segmento del juego)  <-- driver de teclado propio del juego
    INT 09h -> F000:1212             <-- sigue en el stub del BIOS: NO roba el IRQ1

y alli esta exactamente la firma del gancho documentado:

    test byte cs:[flag],0FFh
    je   ...
    cmp  ah,4Fh          ; <-- keyboard intercept
    je   ...
    jmp  far cs:[prev]   ; encadena al INT 15h anterior

En el PC/AT el BIOS ofrece CADA scancode a INT 15h con AH=4Fh y CF=1 antes de traducirlo;
el driver puede cambiarlo (devuelve otro AL) o descartarlo (devuelve CF=0). Es el punto de
enganche estandar para quien no quiere robar el INT 9. PakuPort no lo llamaba nunca, asi que
un juego que instale ahi su teclado se queda SORDO sin que se vea en ninguna traza.

DOSBox-X lo implementa en src/cpu/callback.cpp, case CB_IRQ1 (el cuerpo real del INT 9):

    push ax / in al,60h / mov ah,4Fh / stc / int 15h / jnc skip / <handler BIOS> / skip: ...

(el opcode del salto es 73h = JNC: si el driver devuelve CF=0, el BIOS SE SALTA su propio
manejador y la tecla desaparece).

Que mide KBD15.COM, en tres fases, con una pulsacion de tecla en cada una:

  fase 1 (mode=0, "pasar"):     el driver cuenta la llamada y devuelve CF=1 sin tocar AL.
                                Esperado: el BIOS lo llama, y la tecla llega a la BDA.
  fase 2 (mode=1, "descartar"): el driver devuelve CF=0.
                                Esperado: el BIOS lo llama, y la tecla NO llega a la BDA.
  fase 3 (mode=2, "traducir"):  el driver devuelve AL=1Eh ('a') con CF=1.
                                Esperado: la BDA recibe la tecla TRADUCIDA, no la pulsada.

Las tres se miden sobre el buffer de teclado de la BDA (0040:001A head / 001C tail) leido a
pelo, no por INT 16h: asi el juez mide lo que hace el BIOS del emulador y no la ruta paralela
por la que el host del port inyecta ademas cada tecla en el buffer privado de su INT 16h.

Salida: KBD15.BIN (15 bytes) en el directorio de trabajo. Por fase, 5 bytes:
  +0 word  numero de llamadas AH=4Fh con scancode de PULSACION (los break code no cuentan)
  +2 byte  ultimo AL recibido por el driver (el scancode que el BIOS le ofrecio)
  +3 word  entrada que quedo en el buffer de la BDA (0 = ninguna)

Ensamblado a mano; se verifica con capstone si esta instalado.
"""
import os, sys

D = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100

# Tecla que el arnes inyecta en las tres fases: 'z' = scancode 2Ch, ASCII 7Ah.
SCAN_Z = 0x2C
# Scancode al que traduce la fase 3: 'a' = 1Eh (el BIOS debe meter 1E61h en la BDA).
SCAN_A = 0x1E
TIMEOUT_TICKS = 400          # ~22 s por fase; el arnes pulsa mucho antes


class Asm:
    """Mini ensamblador de dos pasadas: emite bytes y resuelve etiquetas abs16 y rel8."""

    def __init__(self):
        self.code = bytearray()
        self.labels = {}
        self.fix16 = []      # (pos, label) -> direccion absoluta (ORG + offset)
        self.fix8 = []       # (pos, label) -> desplazamiento relativo al siguiente byte

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

# ─────────────────────────────────────────────────────────────────────────────────────────
# Cuerpo principal
# ─────────────────────────────────────────────────────────────────────────────────────────
a.db(0xEB); a.r8('main')          # jmp main  (el handler va primero, direccion baja)

# ── handler de INT 15h ───────────────────────────────────────────────────────────────────
a.label('handler')
a.db(0x80, 0xFC, 0x4F)            # cmp ah,4Fh
a.db(0x75); a.r8('chain')         # jne chain
a.db(0xA8, 0x80)                  # test al,80h        (break code: pasar sin contar)
a.db(0x75); a.r8('pass')          # jnz pass
a.db(0x2E, 0xFF, 0x06); a.a16('cnt')      # inc word cs:[cnt]
a.db(0x2E, 0xA2); a.a16('lastal')         # mov cs:[lastal],al
a.db(0x2E, 0x80, 0x3E); a.a16('mode'); a.db(0x01)   # cmp byte cs:[mode],1
a.db(0x74); a.r8('discard')       # je discard
a.db(0x2E, 0x80, 0x3E); a.a16('mode'); a.db(0x02)   # cmp byte cs:[mode],2
a.db(0x74); a.r8('translate')     # je translate
# El CF de vuelta se pone MODIFICANDO LOS FLAGS APILADOS y saliendo con IRET, que es el
# idioma que usan los drivers reales (el del juego que lo destapo sale con IRET), y no con `stc; retf 2`.
a.label('pass')
a.db(0x55)                        # push bp
a.db(0x89, 0xE5)                  # mov bp,sp          ([bp+2]=IP [bp+4]=CS [bp+6]=FLAGS)
a.db(0x80, 0x4E, 0x06, 0x01)      # or byte [bp+6],1   (CF=1: el BIOS procesa la tecla)
a.db(0x5D)                        # pop bp
a.db(0xCF)                        # iret
a.label('discard')
a.db(0x55)                        # push bp
a.db(0x89, 0xE5)                  # mov bp,sp
a.db(0x80, 0x66, 0x06, 0xFE)      # and byte [bp+6],0FEh  (CF=0: la tecla desaparece)
a.db(0x5D)                        # pop bp
a.db(0xCF)                        # iret
a.label('translate')
a.db(0xB0, SCAN_A)                # mov al,1Eh
a.db(0xEB); a.r8('pass')          # jmp pass
a.label('chain')
a.db(0x2E, 0xFF, 0x2E); a.a16('oldvec')   # jmp far cs:[oldvec]

# ── programa ─────────────────────────────────────────────────────────────────────────────
a.label('main')
a.db(0xFB)                        # sti                (la sonda VIVE de la IRQ1: sin IF no hay
                                  #                     teclado ni tick de la BDA para el timeout)
a.db(0xB8, 0x15, 0x35)            # mov ax,3515h       (leer vector INT 15h)
a.db(0xCD, 0x21)                  # int 21h            -> ES:BX
a.db(0x89, 0x1E); a.a16('oldvec')          # mov [oldvec],bx
a.db(0x8C, 0x06); a.a16('oldvec_seg')      # mov [oldvec_seg],es
a.db(0xBA); a.a16('handler')      # mov dx,handler
a.db(0xB8, 0x15, 0x25)            # mov ax,2515h       (instalar el nuestro; DS=CS en un COM)
a.db(0xCD, 0x21)                  # int 21h


def fase(idx, mode):
    """Vacia el buffer de la BDA, arma el driver en `mode`, espera una tecla y anota."""
    out = f'out{idx}'
    a.db(0xB8, 0x40, 0x00)                     # mov ax,40h
    a.db(0x8E, 0xC0)                           # mov es,ax
    a.db(0x26, 0xA1, 0x1C, 0x00)               # mov ax,es:[1Ch]     (tail)
    a.db(0x26, 0xA3, 0x1A, 0x00)               # mov es:[1Ah],ax     (head=tail: buffer vacio)
    a.db(0x2E, 0xC7, 0x06); a.a16('cnt'); a.db(0x00, 0x00)          # mov word cs:[cnt],0
    a.db(0x2E, 0xC6, 0x06); a.a16('mode'); a.db(mode)               # mov byte cs:[mode],mode
    a.db(0x26, 0xA1, 0x6C, 0x00)               # mov ax,es:[6Ch]     (tick actual)
    a.db(0x2E, 0xA3); a.a16('t0')              # mov cs:[t0],ax
    a.label(f'wait{idx}')
    a.db(0x2E, 0x83, 0x3E); a.a16('cnt'); a.db(0x00)                # cmp word cs:[cnt],0
    a.db(0x75); a.r8(f'got{idx}')              # jne got
    a.db(0x26, 0xA1, 0x6C, 0x00)               # mov ax,es:[6Ch]
    a.db(0x2E, 0x2B, 0x06); a.a16('t0')        # sub ax,cs:[t0]
    a.db(0x3D, TIMEOUT_TICKS & 0xFF, TIMEOUT_TICKS >> 8)            # cmp ax,TIMEOUT
    a.db(0x72); a.r8(f'wait{idx}')             # jb wait
    a.label(f'got{idx}')
    a.db(0x2E, 0xA1); a.a16('cnt')             # mov ax,cs:[cnt]
    a.db(0xA3); a.a16(out)                     # mov [out+0],ax
    a.db(0x2E, 0xA0); a.a16('lastal')          # mov al,cs:[lastal]
    a.db(0xA2); a.a16(f'{out}_al')             # mov [out+2],al
    a.db(0x26, 0x8B, 0x1E, 0x1A, 0x00)         # mov bx,es:[1Ah]     (head)
    a.db(0x26, 0x3B, 0x1E, 0x1C, 0x00)         # cmp bx,es:[1Ch]     (tail)
    a.db(0x74); a.r8(f'vacio{idx}')            # je vacio            (nada en el buffer)
    a.db(0x26, 0x8B, 0x07)                     # mov ax,es:[bx]      (entrada scancode+ascii)
    a.db(0xEB); a.r8(f'store{idx}')            # jmp store
    a.label(f'vacio{idx}')
    a.db(0x31, 0xC0)                           # xor ax,ax
    a.label(f'store{idx}')
    a.db(0xA3); a.a16(f'{out}_bda')            # mov [out+3],ax


fase(1, 0)
fase(2, 1)
fase(3, 2)

# ── restaurar el vector y volcar el resultado ────────────────────────────────────────────
a.db(0x2E, 0xC5, 0x16); a.a16('oldvec')   # lds dx,cs:[oldvec]
a.db(0xB8, 0x15, 0x25)            # mov ax,2515h
a.db(0xCD, 0x21)                  # int 21h
a.db(0x0E)                        # push cs
a.db(0x1F)                        # pop ds             (DS de vuelta al PSP)
a.db(0xB8, 0x00, 0x3C)            # mov ax,3C00h       (crear fichero)
a.db(0x31, 0xC9)                  # xor cx,cx
a.db(0xBA); a.a16('fname')        # mov dx,fname
a.db(0xCD, 0x21)                  # int 21h            -> AX=handle
a.db(0x89, 0xC3)                  # mov bx,ax
a.db(0xB4, 0x40)                  # mov ah,40h         (escribir)
a.db(0xB9, 0x0F, 0x00)            # mov cx,15
a.db(0xBA); a.a16('out1')         # mov dx,out1
a.db(0xCD, 0x21)                  # int 21h
a.db(0xB4, 0x3E)                  # mov ah,3Eh         (cerrar)
a.db(0xCD, 0x21)                  # int 21h
a.db(0xB8, 0x00, 0x4C)            # mov ax,4C00h
a.db(0xCD, 0x21)                  # int 21h

# ── datos ────────────────────────────────────────────────────────────────────────────────
a.label('out1');     a.db(0, 0)
a.label('out1_al');  a.db(0)
a.label('out1_bda'); a.db(0, 0)
a.label('out2');     a.db(0, 0)
a.label('out2_al');  a.db(0)
a.label('out2_bda'); a.db(0, 0)
a.label('out3');     a.db(0, 0)
a.label('out3_al');  a.db(0)
a.label('out3_bda'); a.db(0, 0)
a.label('cnt');      a.db(0, 0)
a.label('lastal');   a.db(0)
a.label('mode');     a.db(0)
a.label('t0');       a.db(0, 0)
a.label('oldvec');   a.db(0, 0)
a.label('oldvec_seg'); a.db(0, 0)
a.label('fname');    a.db(*b'KBD15.BIN\x00')

blob = a.resolve()
ruta = os.path.join(D, 'KBD15.COM')
open(ruta, 'wb').write(blob)
print(f'KBD15.COM: {len(blob)} bytes -> {ruta}')

try:
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
    fin = a.labels['out1'] - ORG
    print('--- desensamblado (verificacion) ---')
    for i in md.disasm(blob[:fin], ORG):
        print('%04X  %-18s %s %s' % (i.address, i.bytes.hex(), i.mnemonic, i.op_str))
except ImportError:
    print('(capstone no instalado: sin verificacion del desensamblado)')
