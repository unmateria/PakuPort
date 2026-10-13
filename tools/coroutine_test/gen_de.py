#!/usr/bin/env python3
"""Synthetic #DE (divide fault) parity test: generate DE.COM by hand-assembling bytes.

The class (a C runtime long-division helper in a real-mode game): the INTERPRETER vectors a
divide fault through INT 0 faithfully (Interp.cs case Div/Idiv: catch -> Platform.Int(0),
8086 semantics: the handler's IRET resumes at the instruction FOLLOWING the div), but
LIFTED code emits `c.Div16(...)` bare — a zero divisor raises DivideByZeroException and
kills the host thread. Lifted and interpreted worlds diverge on the same bytes.

  entry (always lifted, COM entry @ seg:0100):
    installs a real-mode INT 0 vector (handler below), fills the segment word of the
    far pointer with CS, then `call far [ptr_loop]`. If control ever RETURNS here the
    fault path unwound the co-routine -> poison FAIL.BIN trap 0xEEEE.

  BUCLE island (reachable ONLY via far-indirect [ptr_loop]):
    N=10000 loop of `div di` with DI=0 -> #DE each iteration, then a second N=10000 loop
    of `div di` with DX:AX=0x10000, DI=1 -> divide OVERFLOW, the other #DE flavour (same
    INT 0 vector on the 8086; originally the port only caught the zero flavour and an
    OverflowException killed the host while booting a real-mode game). The INT 0 handler counts in BX and
    IRETs (8086: resume after the div). When both loops end it writes RES.BIN
    (2 bytes LE = BX, expected 20000/0x4E20) and exits 4C00.

  HANDLER island (reachable ONLY via the IVT — never seeded, always interpreted):
    inc bx; iret.

Verdict: RES.BIN == 10000 in BOTH partitions. Partition b (BUCLE interpreted) is the
REFERENCE — the interp's INT 0 trampoline is the model a tested game already depends on
(one of its sound routines divides by zero on a cold buffer). Partition a (BUCLE lifted) is the class:
before the Lifter fix the DivideByZeroException kills the host (no RES.BIN = RED);
with the fix (LiftMulDiv wraps Div/Idiv in the same catch->INT 0 trampoline) it flips
GREEN. No DOSBox oracle for this suite: #DE pushed-IP semantics differ per cputype
(8086 pushes next-IP; 286+ pushes the faulting IP) and DOSBox-X's cputype emulation of
that quirk is not established — the parity reference here is the interp partition, which
implements the documented 8086 behaviour the port targets.
"""
import json
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100

N_ITERS = 10000
TRAP_ENTRY_ID = 0xEEEE


class Asm:
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


def rel8(ins_off, target_off):
    disp = target_off - (ins_off + 2)
    assert -128 <= disp <= 127, f"rel8 out of range: {disp}"
    return struct.pack("<b", disp)


def _emit_write(a, fname_label, buf_label, nbytes):
    a.ins(2, lambda a, o: b"\xB4\x3C")
    a.ins(2, lambda a, o: b"\x31\xC9")
    a.ins(3, lambda a, o: b"\xBA" + u16(a.segoff(fname_label)))
    a.ins(2, lambda a, o: b"\xCD\x21")
    a.ins(2, lambda a, o: b"\x89\xC3")
    a.ins(2, lambda a, o: b"\xB4\x40")
    a.ins(3, lambda a, o: b"\xB9" + u16(nbytes))
    a.ins(3, lambda a, o: b"\xBA" + u16(a.segoff(buf_label)))
    a.ins(2, lambda a, o: b"\xCD\x21")
    a.ins(2, lambda a, o: b"\xB4\x3E")
    a.ins(2, lambda a, o: b"\xCD\x21")
    a.ins(3, lambda a, o: b"\xB8\x00\x4C")
    a.ins(2, lambda a, o: b"\xCD\x21")


def build():
    a = Asm()

    # ---- entry (always lifted) ----
    a.ins(2, lambda a, o: b"\x31\xC0")                                   # xor ax,ax
    a.ins(2, lambda a, o: b"\x8E\xC0")                                   # mov es,ax
    # es: mov word [0000], handler_off  (IVT INT 0 offset)
    a.ins(7, lambda a, o: b"\x26\xC7\x06\x00\x00" + u16(a.segoff("HANDLER")))
    # es: mov [0002], cs                (IVT INT 0 segment)
    a.ins(5, lambda a, o: b"\x26\x8C\x0E\x02\x00")
    # mov [ptr_loop+2], cs              (fill far-pointer segment at runtime)
    a.ins(4, lambda a, o: b"\x8C\x0E" + u16(a.segoff("ptr_loop") + 2))
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_loop")))       # call far [ptr_loop]
    # poison: an unwind swallowed the thread and returned here
    a.ins(4, lambda a, o: b"\x89\x1E" + u16(a.segoff("failbuf")))        # mov [failbuf], bx
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("failbuf") + 2) + u16(TRAP_ENTRY_ID))
    _emit_write(a, "fname_fail", "failbuf", 4)

    # ---- BUCLE island (far-indirect only) ----
    a.label("BUCLE")
    a.ins(2, lambda a, o: b"\x31\xDB")                                   # xor bx,bx
    a.ins(3, lambda a, o: b"\xB9" + u16(N_ITERS))                        # mov cx, N
    a.label("loop_top")
    a.ins(2, lambda a, o: b"\x31\xD2")                                   # xor dx,dx
    a.ins(3, lambda a, o: b"\xB8\x01\x00")                               # mov ax,1
    a.ins(2, lambda a, o: b"\x31\xFF")                                   # xor di,di
    a.ins(2, lambda a, o: b"\xF7\xF7")                                   # div di -> #DE
    a.ins(2, lambda a, o: b"\xE2" + rel8(o, a.labels["loop_top"]))       # loop loop_top
    # second loop: divide OVERFLOW (0x1:0000 / 1 -> quotient 0x10000) is the SAME #DE
    # vector on the 8086 as divide-by-zero (a real-mode game's boot: OverflowException killed the host)
    a.ins(3, lambda a, o: b"\xB9" + u16(N_ITERS))                        # mov cx, N
    a.label("loop2_top")
    a.ins(3, lambda a, o: b"\xBA\x01\x00")                               # mov dx,1
    a.ins(2, lambda a, o: b"\x31\xC0")                                   # xor ax,ax
    a.ins(3, lambda a, o: b"\xBF\x01\x00")                               # mov di,1
    a.ins(2, lambda a, o: b"\xF7\xF7")                                   # div di -> overflow #DE
    a.ins(2, lambda a, o: b"\xE2" + rel8(o, a.labels["loop2_top"]))      # loop loop2_top
    a.ins(4, lambda a, o: b"\x89\x1E" + u16(a.segoff("resbuf")))         # mov [resbuf], bx
    _emit_write(a, "fname_res", "resbuf", 2)

    # ---- HANDLER island (IVT only, never seeded) ----
    a.label("HANDLER")
    a.ins(1, lambda a, o: b"\x43")                                       # inc bx
    a.ins(1, lambda a, o: b"\xCF")                                       # iret

    # ---- data ----
    a.label("fname_res")
    a.dat(8, lambda a, o: b"RES.BIN\x00")
    a.label("fname_fail")
    a.dat(9, lambda a, o: b"FAIL.BIN\x00")
    a.label("ptr_loop")
    a.dat(4, lambda a, o: u16(a.segoff("BUCLE")) + u16(0))
    a.label("resbuf")
    a.dat(2, lambda a, o: u16(0))
    a.label("failbuf")
    a.dat(4, lambda a, o: u16(0) + u16(0))

    code = a.assemble()
    with open(os.path.join(OUT, "DE.COM"), "wb") as f:
        f.write(code)
    labels = {
        "size": len(code),
        # two #DE flavours, N faults each: divide-by-zero + divide-overflow
        "n_iters": 2 * N_ITERS,
        "labels": {name: ORG + off for name, off in a.labels.items()},
    }
    with open(os.path.join(OUT, "de_labels.json"), "w") as f:
        json.dump(labels, f, indent=2)
    print(f"DE.COM: {len(code)} bytes; BUCLE @ 0x{ORG + a.labels['BUCLE']:X}; "
          f"HANDLER @ 0x{ORG + a.labels['HANDLER']:X}")


if __name__ == "__main__":
    build()
