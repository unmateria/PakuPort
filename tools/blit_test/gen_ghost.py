# -*- coding: utf-8 -*-
"""Generates GHOST.COM — synthetic net for the HD-replacement liveness gap.

Reproduces, in 60 bytes of hand-assembled code, what a real-mode game's mouse cursor does and what the
25%-dirty frame semantics cannot handle: a sprite that MOVES while the game repaints
the old position only PARTIALLY (delta blitting from a composition buffer). On that game
~8% of the old rect was measured as touched — well under the
25% erase threshold — so every position the sprite ever occupied stays alive as a
ghost instance.

Mode 13h (linear, 320 bytes per line) to keep the geometry out of the question: the
gap under test is liveness, not addressing. Per step the program

  1. blits an 8-row 16x8 sprite row by row (`rep movsw`, one blit per row: row 1 is
     the manifest key, rows 2..8 are the same logical draw, i.e. the burst window),
  2. burns ~65k instructions so the next writes fall OUTSIDE the 2 ms burst window,
  3. zeroes 8 bytes of the rect it just drew = 8/128 px = 6.25% dirty (the partial
     repaint; deliberately under the 25% threshold, like the measured ~8%),
  4. advances 16 px to the right — NO overlap with the previous position, so the new
     blit cannot be what erases the old one.

After N steps the truth on screen is ONE sprite (the last one drawn is the only one
not overdrawn... in fact all N are on screen here, but only the last is where the
"cursor" is). What a correct liveness rule must yield is 1 live instance; what the
dirty-fraction rule yields today is N. The judge reports the difference as the gap.

Usage: python tools/blit_test/gen_ghost.py   -> writes GHOST.COM next to this file
"""
import os

STEPS = 12
DST0 = 100 * 320 + 32     # y=100, x=32
SPRITE_ROWS, SPRITE_BYTES = 8, 16

CODE_LEN = 0x3B           # SPRITE label = 0x100 + 0x3B (asserted below)
SPRITE = 0x100 + CODE_LEN

code = bytes([
    0xFC,                                # 0100 cld
    0xB8, 0x13, 0x00,                    # 0101 mov ax,0013h
    0xCD, 0x10,                          # 0104 int 10h        ; 320x200x256 linear
    0xB8, 0x00, 0xA0,                    # 0106 mov ax,0A000h
    0x8E, 0xC0,                          # 0109 mov es,ax
    0xBF, DST0 & 0xFF, DST0 >> 8,        # 010B mov di,DST0
    0xBD, STEPS & 0xFF, STEPS >> 8,      # 010E mov bp,STEPS
    # STEP (0111):
    0x57,                                # 0111 push di
    0xBE, SPRITE & 0xFF, SPRITE >> 8,    # 0112 mov si,SPRITE
    0xBA, SPRITE_ROWS, 0x00,             # 0115 mov dx,rows
    # ROW (0118):
    0xB9, SPRITE_BYTES // 2, 0x00,       # 0118 mov cx,words per row
    0xF3, 0xA5,                          # 011B rep movsw      ; DS:SI -> ES:DI
    0x81, 0xC7, (320 - SPRITE_BYTES) & 0xFF, (320 - SPRITE_BYTES) >> 8,   # 011D add di,304
    0x4A,                                # 0121 dec dx
    0x75, 0xF4,                          # 0122 jnz ROW        ; -12
    0x5F,                                # 0124 pop di
    0xB9, 0x00, 0x00,                    # 0125 mov cx,0       ; 65536 iterations
    # DELAY (0128):
    0xE2, 0xFE,                          # 0128 loop DELAY     ; leave the burst window
    0x57,                                # 012A push di
    0x31, 0xC0,                          # 012B xor ax,ax
    0xB9, 0x04, 0x00,                    # 012D mov cx,4       ; 4 words = 8 bytes = 6.25%
    0xF3, 0xAB,                          # 0130 rep stosw      ; partial erase of THIS rect
    0x5F,                                # 0132 pop di
    0x83, 0xC7, SPRITE_BYTES,            # 0133 add di,16      ; move right, no overlap
    0x4D,                                # 0136 dec bp
    0x75, 0xD8,                          # 0137 jnz STEP       ; -40
    # FIN (0139):
    0xEB, 0xFE,                          # 0139 jmp FIN        ; alive for the screenshot
])
assert len(code) == CODE_LEN, f'code is {len(code)} bytes, SPRITE label assumes {CODE_LEN}'

# One distinct byte pattern per row so each row hashes differently: the manifest keys
# row 1 alone and rows 2..8 must be swallowed by the burst window, not counted as
# separate instances.
# Colour indices are kept in 1..15 ON PURPOSE: PakuPort's mode 13h set-mode loads only
# the 16 EGA entries into the DAC and zeroes the other 240 (VideoState.
# LoadDefaultVgaPalette256), so indices >=16 render pure black and the sprite would be
# invisible on screen even though the blits happen. That divergence from a real VGA
# BIOS (which loads 16 EGA + 16 greys + 216 HSV) is reported separately; this net does
# not depend on it either way.
sprite = bytearray()
for r in range(SPRITE_ROWS):
    for j in range(SPRITE_BYTES):
        sprite.append(1 + ((r * 7 + j) % 15))

out = os.path.join(os.path.dirname(os.path.abspath(__file__)), 'GHOST.COM')
with open(out, 'wb') as f:
    f.write(code + bytes(sprite))
print(f'{out}: {len(code)} bytes de codigo + {len(sprite)} de sprite = '
      f'{len(code) + len(sprite)} total')
print(f'  SPRITE en {SPRITE:#06x} (offset 0x{CODE_LEN:02X} del fichero), '
      f'{STEPS} pasos desde DI={DST0} (x=32,y=100), avance 16 px')

try:
    from capstone import Cs, CS_ARCH_X86, CS_MODE_16
    md = Cs(CS_ARCH_X86, CS_MODE_16)
    print('  desensamblado:')
    for i in md.disasm(code, 0x100):
        print(f'    {i.address:04X}  {i.bytes.hex():<12} {i.mnemonic} {i.op_str}')
except ImportError:
    print('  (capstone no instalado: sin verificacion del desensamblado)')
