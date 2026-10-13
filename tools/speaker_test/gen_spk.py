#!/usr/bin/env python3
"""Synthetic PC-speaker tone test: generate SPK.COM by hand-assembling bytes.

The class: the PC speaker was pure port state in PakuPort (Speaker.cs only stored 0x61 and
generated no audio at all), so every game whose ONLY sound is the internal speaker came out
MUTE — several of the games it was tested with, plus the "IBM" (speaker) sound option of
another one. The fix generates the PIT channel-2 square wave into the host ring, like the OPL.

SPK.COM programs channel 2 in mode 3 (square wave) with reload 0x04A9, opens the 0x61 gate,
waits ~1 second off the BIOS tick, closes the gate and exits. Nothing about the CPU is under
test here: the verdict is the AUDIO, judged by run_gate.py measuring the dominant frequency
of the WAV the host dumps (PAKU_WAV_DUMP).

  1193181.666 / 0x04A9 (1193) = 1000.15 Hz  -> expected tone, judged to +-2%.
"""
import os

OUT = os.path.dirname(os.path.abspath(__file__))

RELOAD = 0x04A9          # 1193 -> 1000.15 Hz
TICKS_TO_WAIT = 18       # ~1 s at the 18.2 Hz BIOS tick

code = bytes([
    # --- PIT channel 2, mode 3 (square wave), LSB+MSB ---
    0xB0, 0xB6,                       # mov al,0B6h
    0xE6, 0x43,                       # out 43h,al
    0xB8, RELOAD & 0xFF, RELOAD >> 8, # mov ax,RELOAD
    0xE6, 0x42,                       # out 42h,al      (LSB)
    0x88, 0xE0,                       # mov al,ah
    0xE6, 0x42,                       # out 42h,al      (MSB)
    # --- speaker on: gate (bit0) + data enable (bit1) ---
    0xE4, 0x61,                       # in  al,61h
    0x0C, 0x03,                       # or  al,3
    0xE6, 0x61,                       # out 61h,al
    # --- baseline BIOS tick into BX ---
    0xB4, 0x00,                       # mov ah,0
    0xCD, 0x1A,                       # int 1Ah         (CX:DX = tick count)
    0x89, 0xD3,                       # mov bx,dx
    # --- wait loop: until DX-BX >= TICKS_TO_WAIT (11 bytes, jb rel8 = -11) ---
    0xB4, 0x00,                       # mov ah,0
    0xCD, 0x1A,                       # int 1Ah
    0x29, 0xDA,                       # sub dx,bx
    0x83, 0xFA, TICKS_TO_WAIT,        # cmp dx,TICKS_TO_WAIT
    0x72, 0xF5,                       # jb  wait
    # --- speaker off ---
    0xE4, 0x61,                       # in  al,61h
    0x24, 0xFC,                       # and al,0FCh
    0xE6, 0x61,                       # out 61h,al
    # --- exit ---
    0xB8, 0x00, 0x4C,                 # mov ax,4C00h
    0xCD, 0x21,                       # int 21h
])

path = os.path.join(OUT, "SPK.COM")
with open(path, "wb") as f:
    f.write(code)
print(f"SPK.COM: {len(code)} bytes, reload={RELOAD} -> {1193181.666/RELOAD:.2f} Hz")
