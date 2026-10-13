"""Sonda de EXPANSION DEL DAC (6 bits -> 8): genera DACX.COM, el mismo binario para el oraculo
y PakuPort.

Por que existe: estaba reportado y sin arreglar que el DAC de 6 bits lo estirabamos con
`v << 2` (maximo 252) mientras DOSBox usa `(v * 255) / 63` (maximo 255) — mismo indice de
paleta, (0,0,168) aqui y (0,0,170) alli. La formula exacta esta en dosbox-x
src/hardware/vga_dac.cpp, `dacexpand()`:

    (v << 2) | (v >> 4)        // los dos bits altos rellenan los dos bajos: abcdef -> abcdefab

y el propio comentario del fichero dice que es "equivalente a (v*255)/63 sin multiplicar ni
dividir". Afecta a TODOS los colores de TODOS los juegos, no a un caso raro: cada componente
sale hasta un 1,2 % mas apagada de lo que debe, y el blanco puro nunca llega a 255.

Por que hacia falta una sonda NUEVA: el barrido del DAC (tools/dac_sweep) compara los
6 bits LEIDOS POR PUERTO, que son identicos en los dos mundos, asi que no puede ver esto. Lo
que diverge es el color que sale por pantalla, y eso solo se mide mirando el framebuffer.

Que hace DACX.COM: modo 13h, carga en el DAC una rampa gris de 64 pasos (indice i -> r=g=b=i,
los 64 valores posibles de 6 bits) y pinta 64 bandas verticales de 5 px, una por indice. El
juez muestrea el centro de cada banda en el PNG de cada mundo y compara.

Ensamblado a mano; se verifica con capstone si esta instalado.
"""
import os

D = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100


class Asm:
    def __init__(self):
        self.code = bytearray()
        self.labels = {}
        self.fix8 = []

    def label(self, n):
        self.labels[n] = ORG + len(self.code)

    def db(self, *bs):
        self.code.extend(bs)

    def r8(self, n):
        self.fix8.append((len(self.code), n))
        self.code.append(0)

    def resolve(self):
        for pos, lab in self.fix8:
            d = self.labels[lab] - (ORG + pos + 1)
            assert -128 <= d <= 127, f'salto fuera de rango a {lab}: {d}'
            self.code[pos] = d & 0xFF
        return bytes(self.code)


a = Asm()
a.db(0xFB)                        # sti
a.db(0xB8, 0x13, 0x00)            # mov ax,0013h        (modo 13h, 320x200x256)
a.db(0xCD, 0x10)                  # int 10h

# ── cargar el DAC: indice i -> (i,i,i), i = 0..63 ────────────────────────────────────────
a.db(0xBA, 0xC8, 0x03)            # mov dx,3C8h         (indice de escritura del DAC)
a.db(0x30, 0xC0)                  # xor al,al
a.db(0xEE)                        # out dx,al           (empezar por el indice 0)
a.db(0x42)                        # inc dx              (3C9h = datos)
a.db(0x31, 0xC9)                  # xor cx,cx
a.label('dac')
a.db(0x88, 0xC8)                  # mov al,cl
a.db(0xEE)                        # out dx,al           (rojo)
a.db(0xEE)                        # out dx,al           (verde)
a.db(0xEE)                        # out dx,al           (azul)
a.db(0x41)                        # inc cx
a.db(0x83, 0xF9, 0x40)            # cmp cx,64
a.db(0x72); a.r8('dac')           # jb dac

# ── pintar 64 bandas verticales de 5 px (64*5 = 320) por cada una de las 200 filas ───────
a.db(0xB8, 0x00, 0xA0)            # mov ax,A000h
a.db(0x8E, 0xC0)                  # mov es,ax
a.db(0x31, 0xFF)                  # xor di,di
a.db(0xBE, 0xC8, 0x00)            # mov si,200          (filas)
a.label('fila')
a.db(0x31, 0xDB)                  # xor bx,bx           (banda 0..63)
a.label('banda')
a.db(0x89, 0xD8)                  # mov ax,bx           (al = indice de la banda)
a.db(0xB9, 0x05, 0x00)            # mov cx,5
a.db(0xF3, 0xAA)                  # rep stosb
a.db(0x43)                        # inc bx
a.db(0x83, 0xFB, 0x40)            # cmp bx,64
a.db(0x72); a.r8('banda')         # jb banda
a.db(0x4E)                        # dec si
a.db(0x75); a.r8('fila')          # jnz fila

a.label('fin')
a.db(0xEB); a.r8('fin')           # jmp $   (queda en pantalla para que cada mundo la capture)

blob = a.resolve()
ruta = os.path.join(D, 'DACX.COM')
open(ruta, 'wb').write(blob)
print(f'DACX.COM: {len(blob)} bytes -> {ruta}')

try:
    import capstone
    md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_16)
    print('--- desensamblado (verificacion) ---')
    for i in md.disasm(blob, ORG):
        print('%04X  %-12s %s %s' % (i.address, i.bytes.hex(), i.mnemonic, i.op_str))
except ImportError:
    print('(capstone no instalado: sin verificacion del desensamblado)')
