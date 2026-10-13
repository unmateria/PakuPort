"""Sonda de la BDA DE VIDEO tras un set-mode: genera BDAV.COM, el mismo binario para el oraculo
y PakuPort.

Por que existe: buscando la brecha del CGA sobre EGA se midio en un juego EGA, con el juego
ya pintando su logo en modo 0Dh, que la BDA de video de PakuPort no tenia NADA que ver con la
del oraculo — 9 de 18 campos distintos, empezando por el mas basico:

    40:49 modo de video     PakuPort 03   oraculo 0D
    40:4A columnas          PakuPort 50   oraculo 28
    40:84 filas-1           PakuPort 00   oraculo 18
    40:85 altura de caracter PakuPort 00  oraculo 08
    40:87 flags EGA         PakuPort 00   oraculo 60

Nadie escribia esos campos: `INT 10h AH=00` cambiaba el estado interno del emulador (VideoState)
y dejaba la BDA con los valores del arranque. Cualquier juego que le pregunte al BIOS en que
modo esta, cuantas filas hay o cuanto mide la fuente — por `AH=0F`, por `AH=11 AL=30` o leyendo
0040:0049 a pelo, que es de lo mas comun — recibia una respuesta falsa.

Que hace BDAV.COM: para cada modo de la lista (03, 04, 06, 0D, 0E, 10, 12, 13) hace el set-mode
y copia los campos de video de la BDA a su buffer; al terminar vuelve al modo 3 y escribe
BDAV.BIN. Por modo van 34 bytes: 0040:0049..0066 (30) y 0040:0084..0087 (4).

La lista de modos cubre texto (03), CGA de 4 y 2 colores (04, 06), EGA de 16 en sus tres
resoluciones (0D, 0E, 10), VGA 640x480 (12) y el 13h de 256 colores.

Ensamblado a mano; se verifica con capstone si esta instalado.
"""
import os

D = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100
MODOS = (0x03, 0x04, 0x06, 0x0D, 0x0E, 0x10, 0x12, 0x13, 0x03)
POR_MODO = 34


class Asm:
    def __init__(self):
        self.code = bytearray()
        self.labels = {}
        self.fix16 = []
        self.fix8 = []

    def label(self, n):
        self.labels[n] = ORG + len(self.code)

    def db(self, *bs):
        self.code.extend(bs)

    def a16(self, n):
        self.fix16.append((len(self.code), n))
        self.code.extend((0, 0))

    def r8(self, n):
        self.fix8.append((len(self.code), n))
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
a.db(0xFB)                              # sti
a.db(0xBF); a.a16('out')                # mov di,out          (destino en el propio COM)

for i, modo in enumerate(MODOS):
    a.db(0xB8, modo, 0x00)              # mov ax,00<modo>     (AH=00 set video mode)
    a.db(0xCD, 0x10)                    # int 10h
    # copiar 0040:0049..0066 (30 bytes)
    a.db(0xB8, 0x40, 0x00)              # mov ax,40h
    a.db(0x8E, 0xD8)                    # mov ds,ax           (DS = BDA)
    a.db(0xBE, 0x49, 0x00)              # mov si,0049h
    a.db(0xB9, 0x1E, 0x00)              # mov cx,30
    a.db(0xF3, 0xA4)                    # rep movsb           (DS:SI -> ES:DI, ES=PSP)
    # copiar 0040:0084..0087 (4 bytes)
    a.db(0xBE, 0x84, 0x00)              # mov si,0084h
    a.db(0xB9, 0x04, 0x00)              # mov cx,4
    a.db(0xF3, 0xA4)                    # rep movsb
    a.db(0x0E)                          # push cs
    a.db(0x1F)                          # pop ds              (DS de vuelta al COM)

# volver a modo texto y volcar
a.db(0xB8, 0x03, 0x00)                  # mov ax,0003h
a.db(0xCD, 0x10)                        # int 10h
a.db(0xB8, 0x00, 0x3C)                  # mov ax,3C00h
a.db(0x31, 0xC9)                        # xor cx,cx
a.db(0xBA); a.a16('fname')              # mov dx,fname
a.db(0xCD, 0x21)                        # int 21h
a.db(0x89, 0xC3)                        # mov bx,ax
a.db(0xB4, 0x40)                        # mov ah,40h
n = len(MODOS) * POR_MODO
a.db(0xB9, n & 0xFF, n >> 8)            # mov cx,<total>
a.db(0xBA); a.a16('out')                # mov dx,out
a.db(0xCD, 0x21)                        # int 21h
a.db(0xB4, 0x3E)                        # mov ah,3Eh
a.db(0xCD, 0x21)                        # int 21h
a.db(0xB8, 0x00, 0x4C)                  # mov ax,4C00h
a.db(0xCD, 0x21)                        # int 21h

a.label('fname'); a.db(*b'BDAV.BIN\x00')
a.label('out');   a.db(*([0] * n))

blob = a.resolve()
ruta = os.path.join(D, 'BDAV.COM')
open(ruta, 'wb').write(blob)
print(f'BDAV.COM: {len(blob)} bytes ({len(MODOS)} modos x {POR_MODO} B = {n}) -> {ruta}')

try:
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
    fin = a.labels['fname'] - ORG
    print('--- desensamblado (verificacion, primeras instrucciones) ---')
    for i, ins in enumerate(md.disasm(blob[:fin], ORG)):
        if i > 22:
            print('   ... (el resto son los otros modos, identicos)')
            break
        print('%04X  %-12s %s %s' % (ins.address, ins.bytes.hex(), ins.mnemonic, ins.op_str))
except ImportError:
    print('(capstone no instalado: sin verificacion del desensamblado)')
