#!/usr/bin/env python3
"""Synthetic PC-speaker DIRECT-MODE test: generate SPK2.COM by hand-assembling bytes.

The class: games that play DIGITIZED/pulse audio through the speaker do it in DIRECT mode —
PIT gate (bit 0) OFF, toggling the data bit (bit 1) of port 0x61 by hand with CPU-timed
delays. One tested game does exactly this (at file offset 0x1ce48: in 61/and FE/or 02/out 61/loop/
and FD/out 61 reading pulse widths from a table) and came out MUTE: Speaker.cs only models
the gated PIT-channel-2 square wave.

SPK2.COM toggles bit 1 every 1500 executed instructions with the gate off. Under a 3000
instructions/ms governor (PAKU_CYCLES=3000 + PAKU_INTERP_ONLY=1 so ticks count real
instructions, not lifted labels) that is a 1000.0 Hz square wave, judged from the WAV dump
by run_gate.py exactly like the tone case.
"""
import os

OUT = os.path.dirname(os.path.abspath(__file__))

HALF_LOOPS = 1495        # + xor/out/mov/dec/jnz = 1500 instr = 0.5 ms @ 3000/ms -> 1000 Hz
HALF_PERIODS = 2000      # 1 s of signal

code = bytes([
    # --- gate OFF, data low (direct mode baseline) ---
    0xE4, 0x61,                             # in  al,61h
    0x24, 0xFC,                             # and al,0FCh
    0xE6, 0x61,                             # out 61h,al
    0xBA, HALF_PERIODS & 0xFF, HALF_PERIODS >> 8,  # mov dx,HALF_PERIODS
    # next: (12 bytes to jnz inclusive)
    0x34, 0x02,                             # xor al,2        (toggle data bit)
    0xE6, 0x61,                             # out 61h,al
    0xB9, HALF_LOOPS & 0xFF, HALF_LOOPS >> 8,      # mov cx,HALF_LOOPS
    0xE2, 0xFE,                             # loop $          (CPU-timed delay)
    0x4A,                                   # dec dx
    0x75, 0xF4,                             # jnz next
    # --- data low again ---
    0x24, 0xFC,                             # and al,0FCh
    0xE6, 0x61,                             # out 61h,al
    # --- exit ---
    0xB8, 0x00, 0x4C,                       # mov ax,4C00h
    0xCD, 0x21,                             # int 21h
])

path = os.path.join(OUT, "SPK2.COM")
with open(path, "wb") as f:
    f.write(code)
print(f"SPK2.COM: {len(code)} bytes, {HALF_LOOPS+5} instr/half-period -> 1000.0 Hz @ 3000/ms")
