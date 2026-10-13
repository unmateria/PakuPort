#!/usr/bin/env python3
"""Synthetic "software-INT threaded VM" class test: hand-assemble VMINT.COM.

Style mirrors tools/coroutine_test/gen_test.py (no external assembler). VMINT.COM
reproduces the "VM threaded through a software INT" class: a runtime installs its OWN software interrupt whose handler reads the
RETURN IP off the stack as a pointer to INLINE OPERANDS (the bytes right after the
`int n` are DATA, not code). The handler consumes the operand, advances the return
IP past it, and IRETs beyond it. Real instances: one tested game's own driver
(INT 91h) and a compiled-BASIC p-code runtime (INT ECh).

The program (COM entry @ seg:0100, always lifted):

  entry:
    install an INT E7 handler with DOS AH=25 (DS:DX -> handler; DS=CS in a COM).
    Then a run of SIX `int E7`, each followed by ONE inline operand byte, and finally
    the code that writes RESULT.BIN = the 16-bit checksum accumulated by the handler.

  handler (reached only through the installed IVT vector):
    read the caller's return IP off the stack ([bp+2] after `push bp`), load the
    operand byte it points at (CS:[retIP]), add it (zero-extended) to `sum`, then
    ADVANCE the on-stack return IP by 1 so the IRET lands PAST the operand on the
    next `int E7` (or, after the last, on the write code). Registers preserved.

Correct machine: the handler consumes each operand as DATA; the six operands
0xF8 0xF9 0xF5 0xFC 0xFD 0x90 sum to 0x056F, written to RESULT.BIN. The operands are
each a harmless 1-byte instruction (clc/stc/cmc/cld/std/nop) ONLY so a static lifter
can decode the entry linearly; a correct run never executes them as code.

Buggy machine (the class): a lifted `int E7` that synthesises a nested-dispatch frame
with return IP = 0 makes the handler read the operand from offset 0 (a fixed byte),
so `sum` diverges from 0x056F; and control resumes in the lifted stream at the operand
byte. Either way RESULT.BIN != 0x056F (or is never written). run_gate.py gates on it.
"""
import json
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100  # COM load offset within the segment

INT_NO = 0xE7                       # the game-installed software interrupt
OPERANDS = [0xF8, 0xF9, 0xF5, 0xFC, 0xFD, 0x90]   # clc stc cmc cld std nop (harmless 1-byte insns)
EXPECTED = sum(OPERANDS) & 0xFFFF   # 0x056F


class Asm:
    """Minimal two-pass 8086 assembler; every encoding is fixed-length so pass 1
    assigns a file offset to each label and pass 2 emits with labels resolved.
    Segment offset of a label = ORG + file_offset."""

    def __init__(self):
        self.items = []
        self.labels = {}

    def label(self, name):
        self.items.append((0, ("label", name)))

    def ins(self, size, enc):
        self.items.append((size, ("ins", enc)))

    def dat(self, size, enc):
        self.items.append((size, ("dat", enc)))

    def segoff(self, name):
        return ORG + self.labels[name]

    def assemble(self):
        off = 0
        for size, spec in self.items:
            if spec[0] == "label":
                self.labels[spec[1]] = off
            else:
                off += size
        total = off
        out = bytearray()
        for size, spec in self.items:
            if spec[0] == "label":
                continue
            b = spec[1](self, len(out))
            assert len(b) == size, f"size mismatch {len(b)}!={size}: {b.hex()}"
            out += b
        assert len(out) == total
        return bytes(out)


def u16(v):
    return struct.pack("<H", v & 0xFFFF)


def _emit_write(a, fname_label, buf_label, nbytes):
    # create (3C) -> write (40) -> close (3E) -> exit (4C00)
    a.ins(2, lambda a, o: b"\xB4\x3C")                                  # mov ah, 3C
    a.ins(2, lambda a, o: b"\x31\xC9")                                  # xor cx, cx (attrs=0)
    a.ins(3, lambda a, o: b"\xBA" + u16(a.segoff(fname_label)))         # mov dx, offset fname
    a.ins(2, lambda a, o: b"\xCD\x21")                                  # int 21h
    a.ins(2, lambda a, o: b"\x89\xC3")                                  # mov bx, ax (handle)
    a.ins(2, lambda a, o: b"\xB4\x40")                                  # mov ah, 40
    a.ins(3, lambda a, o: b"\xB9" + u16(nbytes))                        # mov cx, nbytes
    a.ins(3, lambda a, o: b"\xBA" + u16(a.segoff(buf_label)))           # mov dx, offset buf
    a.ins(2, lambda a, o: b"\xCD\x21")                                  # int 21h
    a.ins(2, lambda a, o: b"\xB4\x3E")                                  # mov ah, 3E
    a.ins(2, lambda a, o: b"\xCD\x21")                                  # int 21h
    a.ins(3, lambda a, o: b"\xB8\x00\x4C")                              # mov ax, 4C00
    a.ins(2, lambda a, o: b"\xCD\x21")                                  # int 21h


def build():
    a = Asm()

    # ---------- entry (COM entry @ ORG) ----------
    a.label("entry")
    a.ins(2, lambda a, o: b"\xB4\x25")                                  # mov ah, 25h (set int vector)
    a.ins(2, lambda a, o: bytes([0xB0, INT_NO]))                        # mov al, INT_NO
    a.ins(3, lambda a, o: b"\xBA" + u16(a.segoff("handler")))           # mov dx, offset handler
    a.ins(2, lambda a, o: b"\xCD\x21")                                  # int 21h  (DS:DX -> handler)

    # SIX `int E7` each followed by one inline operand byte (the "threaded" ops).
    for op in OPERANDS:
        a.ins(2, lambda a, o: bytes([0xCD, INT_NO]))                    # int INT_NO
        a.dat(1, lambda a, o, _op=op: bytes([_op]))                     # inline operand (DATA)

    # write RESULT.BIN = the 16-bit checksum in `sum`
    _emit_write(a, "fname_res", "sum", 2)

    # ---------- handler (via the installed IVT vector) ----------
    a.label("handler")
    a.ins(1, lambda a, o: b"\x55")                                      # push bp
    a.ins(2, lambda a, o: b"\x89\xE5")                                  # mov bp, sp
    a.ins(1, lambda a, o: b"\x50")                                      # push ax
    a.ins(1, lambda a, o: b"\x53")                                      # push bx
    a.ins(3, lambda a, o: b"\x8B\x5E\x02")                              # mov bx, [bp+2]  (return IP)
    a.ins(3, lambda a, o: b"\x2E\x8A\x07")                              # mov al, cs:[bx] (operand byte)
    a.ins(2, lambda a, o: b"\x30\xE4")                                  # xor ah, ah
    a.ins(5, lambda a, o: b"\x2E\x01\x06" + u16(a.segoff("sum")))       # add cs:[sum], ax
    a.ins(3, lambda a, o: b"\xFF\x46\x02")                              # inc word [bp+2] (skip operand)
    a.ins(1, lambda a, o: b"\x5B")                                      # pop bx
    a.ins(1, lambda a, o: b"\x58")                                      # pop ax
    a.ins(1, lambda a, o: b"\x5D")                                      # pop bp
    a.ins(1, lambda a, o: b"\xCF")                                      # iret

    # ---------- data ----------
    a.label("sum");       a.dat(2, lambda a, o: u16(0))
    a.label("fname_res"); a.dat(11, lambda a, o: b"RESULT.BIN\x00")
    return a


def main():
    a = build()
    com = a.assemble()
    with open(os.path.join(OUT, "VMINT.COM"), "wb") as f:
        f.write(com)
    labels = {name: ORG + off for name, off in a.labels.items()}
    with open(os.path.join(OUT, "labels.json"), "w") as f:
        json.dump({"org": ORG, "size": len(com), "labels": labels,
                   "int_no": INT_NO, "operands": OPERANDS, "expected": EXPECTED}, f, indent=1)
    print(f"VMINT.COM: {len(com)} bytes; INT 0x{INT_NO:02X}; expected sum=0x{EXPECTED:04X} ({EXPECTED})")
    for name in ("entry", "handler", "sum", "fname_res"):
        print(f"  {name:12s} file=0x{a.labels[name]:04X} segoff=0x{ORG + a.labels[name]:04X}")


if __name__ == "__main__":
    main()
