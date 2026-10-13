#!/usr/bin/env python3
"""Synthetic "IRET idiom inside a hardware ISR" test: generate IIDM.COM.

Captures a sound-driver crash class seen in a real-mode game (driver thunk 1FF6:063D): inside a hardware
timer ISR the game uses IRET as a RETURN IDIOM — `pushf / push cs / call sub` where `sub`
ends in IRET, i.e. "far return + restore the FLAGS saved on entry". That intermediate IRET
is NOT the ISR's terminal one. The port delivers IRQ0 as a NESTED Interp.Run (IsrMode,
WholeProgram=false, softIntDepth=0); `case Iret` ends the nested run on the FIRST IRET it
meets, so the ISR tail (register pops, EOI, terminal IRET) never runs and the interrupted
flow resumes with the ISR's unpopped words still on its stack. In that game the interrupted
blitter's next `retf` popped garbage (IP=driver segment, CS=6 = the pushed sound function
number) and walked the IVT as code (0006:010E..013C, prefix 0x67 killed the interp).

Deterministic repro (same harness as ISTK: fast PIT, IRQ0 fires inside the loop):

  entry: install INT 8 -> ISR, unmask IRQ0, fast PIT, record [init_sp], sti, then spin a
  loop of pure NON-STACK ops. Each pass compares SP to [init_sp]:
    * SP != init_sp -> the nested run bailed at the idiom IRET and leaked ISR words ->
      FAIL.BIN trap 0xB1B1. RED (the bug).
    * ISR completed cleanly (isr_ran != 0) with SP intact -> RES.BIN = counter. GREEN.

  ISR:
    push ax
    pushf                  ; idiom part 1: save FLAGS
    push cs                ; idiom part 2: fake far-call frame segment
    call inner             ; near call -> stack holds [FLAGS][CS][IP]
    inc word [isr_ran]     ; ISR tail — never runs if the nested run bailed at inner's IRET
    pop ax
    mov al,0x20 ; out 0x20,al   ; PIC EOI
    iret                   ; TERMINAL iret (stack back at nested-run entry level)

  inner:
    inc word [subcount]
    iret                   ; the IDIOM iret: pops IP,CS,FLAGS = far return + flags restore

Fix under test (Interp.cs case Iret): in IsrMode an IRET is terminal ONLY at the nested
run's entry stack level (SS:SP == ss0:sp0); any deeper IRET is the game's idiom and must
execute in-interp (pop IP:CS:FLAGS and continue). The DOSBox-X oracle (iidm_oracle.conf)
proves the program itself is correct on real hardware semantics.
"""
import json
import os

from gen_test import Asm, u16, rel8, rel16, _emit_write

OUT = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100

PIT_DIV = 0x0400        # ~1165 Hz: IRQ0 fires within a few thousand steps
TRAP_SP_ID = 0xB1B1     # SP leaked ISR words after the bailed idiom IRET (the bug)


def build():
    a = Asm()

    # ---------- entry (COM entry @ ORG) ----------
    a.label("entry")
    a.ins(2, lambda a, o: b"\x31\xC0")                                     # xor ax, ax
    a.ins(2, lambda a, o: b"\x8E\xC0")                                     # mov es, ax
    a.ins(7, lambda a, o: b"\x26\xC7\x06\x20\x00" + u16(a.segoff("ISR")))  # mov word es:[0x20], ISR
    a.ins(2, lambda a, o: b"\x8C\xC8")                                     # mov ax, cs
    a.ins(4, lambda a, o: b"\x26\xA3\x22\x00")                             # mov es:[0x22], ax
    a.ins(2, lambda a, o: b"\xE4\x21")                                     # in al, 0x21
    a.ins(2, lambda a, o: b"\x24\xFE")                                     # and al, 0xFE
    a.ins(2, lambda a, o: b"\xE6\x21")                                     # out 0x21, al
    a.ins(2, lambda a, o: b"\xB0\x36")                                     # mov al, 0x36
    a.ins(2, lambda a, o: b"\xE6\x43")                                     # out 0x43, al
    a.ins(2, lambda a, o: bytes([0xB0, PIT_DIV & 0xFF]))                   # mov al, div lo
    a.ins(2, lambda a, o: b"\xE6\x40")                                     # out 0x40, al
    a.ins(2, lambda a, o: bytes([0xB0, (PIT_DIV >> 8) & 0xFF]))            # mov al, div hi
    a.ins(2, lambda a, o: b"\xE6\x40")                                     # out 0x40, al
    a.ins(4, lambda a, o: b"\x89\x26" + u16(a.segoff("init_sp")))          # mov [init_sp], sp
    a.ins(1, lambda a, o: b"\xFB")                                         # sti

    # ---------- main loop: pure non-stack ops ----------
    a.label("loop_head")
    a.ins(2, lambda a, o: b"\x89\xE0")                                     # mov ax, sp
    a.ins(4, lambda a, o: b"\x3B\x06" + u16(a.segoff("init_sp")))          # cmp ax, [init_sp]
    a.ins(2, lambda a, o: b"\x75" + rel8(o, a.labels["sp_corrupt"]))       # jne sp_corrupt (leak -> BUG)
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("counter")))          # inc word [counter]
    a.ins(5, lambda a, o: b"\x83\x3E" + u16(a.segoff("isr_ran")) + b"\x00")# cmp word [isr_ran], 0
    a.ins(2, lambda a, o: b"\x74" + rel8(o, a.labels["loop_head"]))        # je loop_head
    _emit_write(a, "fname_res", "counter", 2)                              # RES.BIN = counter, exit

    # ---------- sp_corrupt: FAIL.BIN (counter, trapid, sp, init_sp) ----------
    a.label("sp_corrupt")
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_SP_ID))
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("counter")))              # mov ax, [counter]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf")))               # mov [outbuf], ax
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("trapid")))               # mov ax, [trapid]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf") + 2))           # mov [outbuf+2], ax
    a.ins(4, lambda a, o: b"\x89\x26" + u16(a.segoff("outbuf") + 4))       # mov [outbuf+4], sp
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("init_sp")))              # mov ax, [init_sp]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf") + 6))           # mov [outbuf+6], ax
    _emit_write(a, "fname_fail", "outbuf", 8)

    # ---------- ISR ----------
    a.label("ISR")
    a.ins(1, lambda a, o: b"\x50")                                         # push ax
    a.ins(1, lambda a, o: b"\x9C")                                         # pushf   (idiom: save FLAGS)
    a.ins(1, lambda a, o: b"\x0E")                                         # push cs (idiom: far frame)
    a.ins(3, lambda a, o: b"\xE8" + rel16(o, a.labels["inner"]))           # call inner (near)
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("isr_ran")))          # inc word [isr_ran] (tail)
    # EOI BEFORE pop ax: the EOI clobbers AL, and a real ISR must hand the interrupted code
    # its registers back intact (the strict SP==init guard reads SP through AX — an IRQ landing
    # between its `mov ax,sp` and `cmp` would otherwise see AL=0x20 and false-positive).
    a.ins(2, lambda a, o: b"\xB0\x20")                                     # mov al, 0x20
    a.ins(2, lambda a, o: b"\xE6\x20")                                     # out 0x20, al (EOI)
    a.ins(1, lambda a, o: b"\x58")                                         # pop ax
    a.ins(1, lambda a, o: b"\xCF")                                         # iret (TERMINAL)

    a.label("inner")
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("subcount")))         # inc word [subcount]
    a.ins(1, lambda a, o: b"\xCF")                                         # iret (IDIOM: far ret + flags)

    # ---------- data ----------
    a.label("counter");    a.dat(2, lambda a, o: u16(0))
    a.label("isr_ran");    a.dat(2, lambda a, o: u16(0))
    a.label("subcount");   a.dat(2, lambda a, o: u16(0))
    a.label("init_sp");    a.dat(2, lambda a, o: u16(0))
    a.label("trapid");     a.dat(2, lambda a, o: u16(0))
    a.label("outbuf");     a.dat(8, lambda a, o: bytes(8))
    a.label("fname_res");  a.dat(8, lambda a, o: b"RES.BIN\x00")
    a.label("fname_fail"); a.dat(9, lambda a, o: b"FAIL.BIN\x00")
    return a


def main():
    a = build()
    com = a.assemble()
    with open(os.path.join(OUT, "IIDM.COM"), "wb") as f:
        f.write(com)
    labels = {name: ORG + off for name, off in a.labels.items()}
    with open(os.path.join(OUT, "iidm_labels.json"), "w") as f:
        json.dump({"org": ORG, "size": len(com), "labels": labels,
                   # timing-dependent loop count: the judge checks RES.BIN presence, not a value
                   "n_iters": 0,
                   "pit_div": PIT_DIV, "trap_sp_id": TRAP_SP_ID}, f, indent=1)
    print(f"IIDM.COM: {len(com)} bytes")
    for name in ("entry", "loop_head", "sp_corrupt", "ISR", "inner"):
        print(f"  {name:12s} file=0x{a.labels[name]:04X} segoff=0x{ORG + a.labels[name]:04X}")


if __name__ == "__main__":
    main()
