"""Sonda del RITMO DE PRESENTACION: genera PRES.COM.

Por que existe: una prueba manual de un juego 3D informo de parpadeos en el cielo. El juego no espera
al retrace (contador de lecturas del 0x3DA congelado en vuelo, medido) ni hace page flipping
(escribe siempre r0D=00, medido), asi que lo que el usuario ve depende por completo de a que
ritmo PRESENTAMOS nosotros. Y ese ritmo estaba roto:

    [PRESENT] capturas=62 descartadas=8 intervalo ms: min=8.4 p50=14.3 p90=27.0 max=33.3

63 frames por segundo en vez de 70, con ~10 % DESCARTADOS y un p90 al doble del periodo. Un
frame de cada diez duraba lo que dos. En un menu estatico no se nota; con la imagen entera
moviendose, es un tiron varias veces por segundo.

La causa no tiene que ver con ningun juego: el acelerador que funde capturas simultaneas
comparaba con `Environment.TickCount64`, cuya resolucion en Windows es ~15,6 ms, contra un
periodo VGA de 14,286 ms. Al ser el periodo MENOR que la granularidad del reloj, dos capturas
consecutivas caian a menudo en la misma ranura y la segunda se tiraba.

PRES.COM no necesita ningun juego: pone el modo 13h y se queda en un bucle. El tick de refresco
corre igual, asi que el ritmo se mide en estado puro. El juez lee la traza PAKU_LOG_PRESENT=1.

Ensamblado a mano; se verifica con capstone si esta instalado.
"""
import os, sys

D = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100

code = bytearray()


def emit(*bs):
    code.extend(bs)


emit(0xB8, 0x13, 0x00)                       # mov ax,0013h
emit(0xCD, 0x10)                             # int 10h        (modo 13h)

# Bucle: escribe un byte en VRAM y repite. Mantiene la CPU ocupada como un juego real,
# de modo que el tick de refresco compite con trabajo de verdad y no con un HLT.
emit(0xB8, 0x00, 0xA0)                       # mov ax,0A000h
emit(0x8E, 0xC0)                             # mov es,ax
emit(0x31, 0xFF)                             # xor di,di
# bucle:
inicio = ORG + len(code)
emit(0x26, 0xFE, 0x05)                       # inc byte es:[di]
emit(0x47)                                   # inc di
emit(0x81, 0xE7, 0xFF, 0x7F)                 # and di,7FFFh
rel = inicio - (ORG + len(code) + 2)
emit(0xEB, rel & 0xFF)                       # jmp bucle

ruta = os.path.join(D, 'PRES.COM')
open(ruta, 'wb').write(bytes(code))
print(f'PRES.COM: {len(code)} bytes -> {ruta}')

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_16
    md = Cs(CS_ARCH_X86, CS_MODE_16)
    n = 0
    for ins in md.disasm(bytes(code), ORG):
        print(f'  {ins.address:04x}  {ins.mnemonic:6} {ins.op_str}')
        n += ins.size
    if n != len(code):
        sys.exit(f'ROJO: capstone solo decodifico {n} de {len(code)} bytes')
    print('capstone: codigo integro')
except ImportError:
    print('capstone no instalado: COM sin verificar')
