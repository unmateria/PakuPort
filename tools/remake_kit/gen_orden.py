#!/usr/bin/env python3
"""Genera ORDEN.COM — red del ORDEN DE DIBUJO y de la PERSISTENCIA DEL FONDO.

Que se mide y por que importa: tres de los cuatro reparos de la prueba manual de la intro de un
juego de prueba eran el mismo problema — el orden en que se pinta (que tapa a que) y si un sprite se queda
o el juego repinta el fondo debajo son **datos de ejecucion**. Se adivinaron, y se adivinaron mal
de forma plausible. Esto los convierte en una medida con verdad terreno conocida.

ORDEN.COM, en modo 13h, hace exactamente esto y en este orden:
  1. cuadrado A (color 0x0A) en (0,0), 64x64
  2. cuadrado B (color 0x0B) en (32,0), 64x64   <- SOLAPA a A
  3. cuadrado C (color 0x0C) en (64,0), 64x64   <- SOLAPA a B
  4. sprite P (color 0x0D) en (0,100), 32x32    <- PERSISTE: no se toca nunca mas
  5. sprite T (color 0x0E) en (100,100), 32x32  <- TRANSITORIO
  6. espera (varios fotogramas)
  7. borra T repintando el fondo (color 0x00) en el MISMO sitio
  8. espera y se queda quieto

Verdad terreno, por construccion: en pantalla C tapa a B y B tapa a A; P sigue ahi al final y T
no. Un juez que lea la telemetria tiene que llegar a eso solo.

Cada fila de cada cuadrado es un `rep movsw` propio desde un buffer en RAM, o sea un blit propio
con identidad propia (el FNV de sus bytes de origen) — igual que hacen los juegos de verdad. Se
usa `movs` y no `stos` a proposito: el motor solo sabe REEMPLAZAR los blits de `movs` (mira
`RemakeMap.Note`, al que solo se llama por ese camino), asi que una red hecha con `stos` mediria
un camino que el remake no puede usar.

`--invertido` genera la variante que pinta C, B, A: es el CONTROL NEGATIVO. Un juez que se
limite a afirmar "A antes que B" pasaria los dos, y por eso no valdria.

Uso:  python tools/remake_kit/gen_orden.py [--invertido]
"""
import os
import struct
import sys

OUT = os.path.dirname(os.path.abspath(__file__))
INVERTIDO = '--invertido' in sys.argv

CUADRADOS = [(0x0A, 0), (0x0B, 32), (0x0C, 64)]      # (color, x); se pintan en este orden
if INVERTIDO:
    CUADRADOS = list(reversed(CUADRADOS))

# --- ensamblado en dos pasadas: se emiten los bytes con huecos y luego se resuelven ---
trozos = []          # bytes, o ('call', etiqueta) / ('jmp8', etiqueta)
etiquetas = {}


def emite(b):
    trozos.append(b)


def call(nombre):
    trozos.append(('call', nombre))


def etiqueta(nombre):
    trozos.append(('etiqueta', nombre))


# Un buffer de origen por color: 64 bytes, en RAM. Su contenido ES la identidad del blit.
BUFFERS = {c: 0x300 + i * 0x40 for i, c in enumerate([c for c, _ in CUADRADOS] + [0x0D, 0x0E, 0x00])}


def rellena(color):
    """Prepara el buffer de origen de un color (esto va a RAM: no es un blit de pantalla)."""
    emite(b'\xBF' + struct.pack('<H', BUFFERS[color]))    # mov di, buffer
    emite(b'\xB0' + bytes([color]))                       # mov al, color
    emite(b'\xB9\x40\x00')                                # mov cx,64
    emite(b'\xF3\xAA')                                    # rep stosb


def dibuja(color, x, y, ancho_px, filas):
    """Prepara los registros y llama a la rutina que copia fila a fila."""
    emite(b'\xBF' + struct.pack('<H', y * 320 + x))       # mov di, offset en VRAM
    emite(b'\xBE' + struct.pack('<H', BUFFERS[color]))    # mov si, buffer de origen
    emite(b'\xBA' + struct.pack('<H', ancho_px // 2))     # mov dx, palabras por fila
    emite(b'\xBD' + struct.pack('<H', filas))             # mov bp, filas
    call('dibuja')


emite(b'\xB8\x13\x00')          # mov ax,0013h
emite(b'\xCD\x10')              # int 10h
emite(b'\xFC')                  # cld
emite(b'\x0E\x07')              # push cs ; pop es   (ES=DS para preparar los buffers)
for color in BUFFERS:
    rellena(color)
emite(b'\xB8\x00\xA0')          # mov ax,0A000h
emite(b'\x8E\xC0')              # mov es,ax          (ES=VRAM para dibujar)

for color, x in CUADRADOS:
    dibuja(color, x, 0, 64, 64)
dibuja(0x0D, 0, 100, 32, 32)    # P: persiste
dibuja(0x0E, 100, 100, 32, 32)  # T: se borrara
call('espera')
dibuja(0x00, 100, 100, 32, 32)  # repinta el fondo sobre T
call('espera')
emite(b'\xEB\xFE')              # jmp $

# --- rutina de copia: un `rep movsw` POR FILA, o sea un blit reemplazable por fila ---
etiqueta('dibuja')
etiqueta('_fila')
emite(b'\x57')                  # push di
emite(b'\x56')                  # push si
emite(b'\x89\xD1')              # mov cx,dx
emite(b'\xF3\xA5')              # rep movsw     <- EL BLIT
emite(b'\x5E')                  # pop si        (el origen vuelve a empezar en cada fila)
emite(b'\x5F')                  # pop di
emite(b'\x81\xC7\x40\x01')      # add di,320
emite(b'\x4D')                  # dec bp
emite(('jmp8', '_fila'))        # jnz _fila
emite(b'\xC3')                  # ret

# --- espera: suficiente para que pasen varios refrescos de 70 Hz ---
etiqueta('espera')
emite(b'\xBE\x30\x00')          # mov si,48
etiqueta('_e1')
emite(b'\x31\xC9')              # xor cx,cx
etiqueta('_e2')
emite(('loop', '_e2'))          # loop _e2      (65536 iteraciones)
emite(b'\x4E')                  # dec si
emite(('jmp8', '_e1'))          # jnz _e1
emite(b'\xC3')                  # ret

# pasada 1: posiciones
pos, sitios = 0x100, []
for t in trozos:
    if isinstance(t, tuple) and t[0] == 'etiqueta':
        etiquetas[t[1]] = pos
        continue
    sitios.append((pos, t))
    pos += len(t) if isinstance(t, (bytes, bytearray)) else (3 if t[0] == 'call' else 2)

# pasada 2: bytes definitivos
code = bytearray()
for p, t in sitios:
    if isinstance(t, (bytes, bytearray)):
        code += t
    elif t[0] == 'call':
        code += b'\xE8' + struct.pack('<h', etiquetas[t[1]] - (p + 3))
    elif t[0] == 'jmp8':
        code += b'\x75' + struct.pack('<b', etiquetas[t[1]] - (p + 2))   # jnz
    elif t[0] == 'loop':
        code += b'\xE2' + struct.pack('<b', etiquetas[t[1]] - (p + 2))

nombre = 'ORDEN-INV.COM' if INVERTIDO else 'ORDEN.COM'
ruta = os.path.join(OUT, nombre)
open(ruta, 'wb').write(bytes(code))
print(f'{ruta}: {len(code)} bytes · orden de pintado '
      f'{" -> ".join(hex(c) for c, _ in CUADRADOS)}')
