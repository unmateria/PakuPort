#!/usr/bin/env python3
"""Synthetic co-routine class test: generate CORO.COM by hand-assembling bytes.

Style mirrors tools/platform_sweep/gen_tests.py (no external assembler). CORO.COM
reproduces the "retf-to-a-manually-pushed-continuation" pattern (the blitter
co-routine class):

  entry (always lifted, COM entry @ seg:0100):
    fills the SEGMENT word of two far pointers with CS at runtime, then
    `call far [ptr_loop]`. If control ever RETURNS here, an unwind swallowed a
    retf -> poison exit 0xEE (entry trap).

  BUCLE island (reachable ONLY via far-indirect [ptr_loop]):
    N=10000 loop. Each iteration pushes CS + the offset of `loop_check` (a manual
    far continuation) and does `call far [ptr_row]`. On a correct machine the
    row's retf lands on `loop_check` (NOT on the call-far return `trap_pair`).
    When N hits 0 it writes RES.BIN (2 bytes LE = counter, expected 10000/0x2710)
    and exits 4C00.

  FILA island (reachable ONLY via far-indirect [ptr_row]):
    trivial work + `add sp,4` (discards the call-far's own pushed CS:IP pair) +
    `retf` -> jumps to the manually-pushed continuation. THIS is the co-routine.

The discriminator: `trap_pair` (the instruction right after `call far [ptr_row]`,
i.e. "el par del lcall") is POISON. A correct retf never executes it (it jumps to
loop_check). If the FarRet unwind model discards the popped destination and instead
resumes at the call-site's saved return, control lands on trap_pair -> writes
FAIL.BIN (4 bytes LE = counter, then trap id 0xDDDD) and exits. RES.BIN present with
0x2710 == success; FAIL.BIN present == the co-routine bug fired.

The islands are ONLY reachable by far-indirect call, so a purely static discovery
never sees them; which of {entry, BUCLE, FILA} get LIFTED is then forced per export
via `extraSeeds` in games/<id>/game.json, id=coro (see run_gate.py / README).
"""
import json
import os
import struct

OUT = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100  # COM load offset within the segment

N_ITERS = 10000
TRAP_PAIR_ID = 0xDDDD   # retf returned to the call pair (co-routine bug)
TRAP_ENTRY_ID = 0xEEEE  # unwound all the way back to entry


class Asm:
    """Minimal two-pass 8086 assembler. Every encoding is fixed-length so pass 1
    can assign a file offset to each label; pass 2 emits bytes with labels resolved.
    Segment offset of a label = ORG + file_offset."""

    def __init__(self):
        self.items = []      # (size, spec)
        self.labels = {}     # name -> file offset

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


def rel16(ins_off, target_off):
    return struct.pack("<h", target_off - (ins_off + 3))


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
    a.ins(2, lambda a, o: b"\x8C\xC8")                                  # mov ax, cs
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("ptr_loop") + 2))      # mov [ptr_loop+2], ax
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("ptr_row") + 2))       # mov [ptr_row+2], ax
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_loop")))      # call far [ptr_loop]
    a.label("entry_poison")                                            # reached only on a bad unwind
    a.ins(3, lambda a, o: b"\xB8" + u16(0x4C00 | (TRAP_ENTRY_ID & 0xFF)))  # mov ax, 0x4CEE
    a.ins(2, lambda a, o: b"\xCD\x21")                                  # int 21h (terminate, code EE)
    a.ins(2, lambda a, o: b"\xEB\xFE")                                  # jmp $ (guard)

    # ---------- BUCLE island (via [ptr_loop]) ----------
    a.label("BUCLE")
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("N")) + u16(N_ITERS))      # mov word [N], 10000
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("counter")) + u16(0))      # mov word [counter], 0
    a.label("loop_check")                                                       # manual continuation target
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("counter")))               # inc word [counter]
    a.ins(4, lambda a, o: b"\xFF\x0E" + u16(a.segoff("N")))                     # dec word [N]
    a.ins(2, lambda a, o: b"\x74" + rel8(o, a.labels["L_done"]))               # jz L_done
    a.ins(1, lambda a, o: b"\x0E")                                              # push cs
    a.ins(3, lambda a, o: b"\x68" + u16(a.segoff("loop_check")))               # push offset loop_check
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_row")))              # call far [ptr_row]
    a.label("trap_pair")                                                       # call-far return: POISON
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_PAIR_ID))  # mov word [trapid],DDDD
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))          # jmp fail_write
    a.label("L_done")
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["done"]))                # jmp done

    # ---------- FILA island (via [ptr_row]) : THE co-routine ----------
    a.label("FILA")
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("work")))                 # inc word [work] (trivial work)
    a.ins(3, lambda a, o: b"\x83\xC4\x04")                                     # add sp, 4 (drop call-far pair)
    a.ins(1, lambda a, o: b"\xCB")                                             # retf -> manual continuation

    # ---------- done: write RES.BIN (2 bytes = counter) ----------
    a.label("done")
    _emit_write(a, "fname_res", "counter", 2)

    # ---------- fail_write: write FAIL.BIN (4 bytes = counter, trapid) ----------
    a.label("fail_write")
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("counter")))                  # mov ax, [counter]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf")))                   # mov [outbuf], ax
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("trapid")))                   # mov ax, [trapid]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf") + 2))               # mov [outbuf+2], ax
    _emit_write(a, "fname_fail", "outbuf", 4)

    # ---------- data ----------
    a.label("ptr_loop")
    a.dat(4, lambda a, o: u16(a.segoff("BUCLE")) + u16(0))   # off (asm-time), seg (runtime)
    a.label("ptr_row")
    a.dat(4, lambda a, o: u16(a.segoff("FILA")) + u16(0))
    a.label("counter");   a.dat(2, lambda a, o: u16(0))
    a.label("N");         a.dat(2, lambda a, o: u16(0))
    a.label("trapid");    a.dat(2, lambda a, o: u16(0))
    a.label("work");      a.dat(2, lambda a, o: u16(0))
    a.label("outbuf");    a.dat(4, lambda a, o: b"\x00\x00\x00\x00")
    a.label("fname_res");  a.dat(8, lambda a, o: b"RES.BIN\x00")
    a.label("fname_fail"); a.dat(9, lambda a, o: b"FAIL.BIN\x00")
    return a


def main():
    a = build()
    com = a.assemble()
    with open(os.path.join(OUT, "CORO.COM"), "wb") as f:
        f.write(com)
    labels = {name: ORG + off for name, off in a.labels.items()}
    with open(os.path.join(OUT, "labels.json"), "w") as f:
        json.dump({"org": ORG, "size": len(com), "labels": labels,
                   "n_iters": N_ITERS,
                   "trap_pair_id": TRAP_PAIR_ID, "trap_entry_id": TRAP_ENTRY_ID}, f, indent=1)
    print(f"CORO.COM: {len(com)} bytes")
    for name in ("entry", "BUCLE", "loop_check", "trap_pair", "FILA", "done", "fail_write"):
        print(f"  {name:12s} file=0x{a.labels[name]:04X} segoff=0x{ORG + a.labels[name]:04X}")


if __name__ == "__main__":
    main()
