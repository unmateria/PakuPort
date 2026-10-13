#!/usr/bin/env python3
"""Synthetic "ISR private-stack switch" test: generate ISTK.COM by hand-assembling bytes.

Captures an AdLib-driver hang class seen in a real-mode game: a hardware timer ISR that, on each tick,
switches to a PRIVATE stack (a higher SP inside its own data) via a Borland stack-switch
helper, does work there through near call/ret, restores SS:SP and returns via IRET. The
PORT delivers IRQ0 as a NESTED Interp.Run (DosPlatform.FireInterrupt -> IsrDispatch) with
WholeProgram=false. In that mode the interpreter ends the nested run on the FIRST ret/retf
whose SP climbs above the entry sp0 -- but the ISR's private stack is at a LOWER absolute SP
whose SIGNED delta from the (high) entry SP wraps POSITIVE, so the very first ret executed
on the private stack is mistaken for "the ISR frame ended". The nested run bails MID-ISR
with SP left on the private stack; the ISR's own IRET, EOI and register/SP restore never
run. Back in the interrupted main flow SP is now garbage -> a later ret pops 0x0000 and the
game planes through the driver's zero header forever (permanent black screen; AdLib/Roland).

Reproduces exactly that, deterministically (no oracle timing luck needed -- the PIT is
programmed fast so IRQ0 fires within the loop):

  entry (COM entry @ seg:0100):
    - install INT 8 (IVT 0000:0020) -> ISR (this COM, seg = CS at runtime)
    - unmask IRQ0 in the PIC (port 0x21) and program PIT ch0 to a fast rate
    - sti, then spin a loop of pure NON-STACK ops (no call/ret/push/pop): each pass checks
      SP is still near the top (>= SP_GUARD) and waits for the ISR to have run once.
      * SP dropped below SP_GUARD  -> the nested ISR bailed on the private stack and left
        SP relocated -> write FAIL.BIN (trap 0xADAD). RED (the bug).
      * ISR ran (isr_ran != 0) with SP still sane -> write RES.BIN (2 bytes LE = counter).
        GREEN (the fix: IsrMode ran the ISR to its terminal IRET).

  ISR (via IRQ0 nested dispatch):
    push ax
    mov [save_sp], sp          ; save the interrupted SP
    mov sp, PRIVATE_SP         ; switch to the private stack (low absolute SP)
    call isr_sub               ; work through a near call on the private stack
    mov sp, [save_sp]          ; restore
    pop ax
    inc word [isr_ran]         ; mark a CLEAN completion (never reached if the run bailed)
    mov al,0x20 ; out 0x20,al  ; PIC EOI
    iret

  isr_sub:
    ret                        ; the near ret ON the private stack -- the buggy nested
                               ; dispatch bails HERE (SP-sp0 wraps positive), so everything
                               ; after `call isr_sub` in the ISR never runs.

On real hardware / the DOSBox-X oracle (istk_oracle.conf) the ISR completes and the program
writes RES.BIN -- proving the RED is the port's nested-ISR-dispatch model, not the program.
"""
import json
import os

from gen_test import Asm, u16, rel8, rel16, _emit_write

OUT = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100

PRIVATE_SP = 0x4000     # ISR private-stack top: (short)(0x4000 - 0xFFxx) wraps POSITIVE
SP_GUARD = 0x8000       # the main loop's SP must stay above this; the bug drops it to ~0x4000
PIT_DIV = 0x0400        # PIT ch0 divisor -> ~1165 Hz: IRQ0 fires within a few thousand steps
TRAP_SP_ID = 0xADAD     # SP was relocated by the bailed ISR (the bug)


def build():
    a = Asm()

    # ---------- entry (COM entry @ ORG) ----------
    a.label("entry")
    # install INT 8 vector (0000:0020) -> ISR, seg = CS
    a.ins(2, lambda a, o: b"\x31\xC0")                                     # xor ax, ax
    a.ins(2, lambda a, o: b"\x8E\xC0")                                     # mov es, ax
    a.ins(7, lambda a, o: b"\x26\xC7\x06\x20\x00" + u16(a.segoff("ISR")))  # mov word es:[0x20], ISR
    a.ins(2, lambda a, o: b"\x8C\xC8")                                     # mov ax, cs
    a.ins(4, lambda a, o: b"\x26\xA3\x22\x00")                             # mov es:[0x22], ax
    # unmask IRQ0 in the master PIC
    a.ins(2, lambda a, o: b"\xE4\x21")                                     # in al, 0x21
    a.ins(2, lambda a, o: b"\x24\xFE")                                     # and al, 0xFE
    a.ins(2, lambda a, o: b"\xE6\x21")                                     # out 0x21, al
    # program PIT ch0 (mode 3, lo/hi) to a fast rate
    a.ins(2, lambda a, o: b"\xB0\x36")                                     # mov al, 0x36
    a.ins(2, lambda a, o: b"\xE6\x43")                                     # out 0x43, al
    a.ins(2, lambda a, o: bytes([0xB0, PIT_DIV & 0xFF]))                   # mov al, div lo
    a.ins(2, lambda a, o: b"\xE6\x40")                                     # out 0x40, al
    a.ins(2, lambda a, o: bytes([0xB0, (PIT_DIV >> 8) & 0xFF]))            # mov al, div hi
    a.ins(2, lambda a, o: b"\xE6\x40")                                     # out 0x40, al
    a.ins(1, lambda a, o: b"\xFB")                                         # sti

    # ---------- main loop: pure non-stack ops (no call/ret/push/pop) ----------
    a.label("loop_head")
    a.ins(2, lambda a, o: b"\x89\xE0")                                     # mov ax, sp
    a.ins(3, lambda a, o: b"\x3D" + u16(SP_GUARD))                         # cmp ax, SP_GUARD
    a.ins(2, lambda a, o: b"\x72" + rel8(o, a.labels["sp_corrupt"]))       # jb sp_corrupt (SP relocated -> BUG)
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("counter")))          # inc word [counter]
    a.ins(5, lambda a, o: b"\x83\x3E" + u16(a.segoff("isr_ran")) + b"\x00")# cmp word [isr_ran], 0
    a.ins(2, lambda a, o: b"\x74" + rel8(o, a.labels["loop_head"]))        # je loop_head (wait for a clean ISR)
    # ISR ran cleanly AND SP is still sane -> success
    _emit_write(a, "fname_res", "counter", 2)                             # write RES.BIN = counter, exit 4C00

    # ---------- sp_corrupt: FAIL.BIN (counter, trapid) ----------
    a.label("sp_corrupt")
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_SP_ID))  # mov [trapid], 0xADAD
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("counter")))              # mov ax, [counter]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf")))               # mov [outbuf], ax
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("trapid")))               # mov ax, [trapid]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf") + 2))           # mov [outbuf+2], ax
    _emit_write(a, "fname_fail", "outbuf", 4)

    # ---------- ISR (via IRQ0 nested dispatch) ----------
    a.label("ISR")
    a.ins(1, lambda a, o: b"\x50")                                         # push ax
    a.ins(4, lambda a, o: b"\x89\x26" + u16(a.segoff("save_sp")))          # mov [save_sp], sp
    a.ins(3, lambda a, o: b"\xBC" + u16(PRIVATE_SP))                       # mov sp, PRIVATE_SP (private stack)
    a.ins(3, lambda a, o: b"\xE8" + rel16(o, a.labels["isr_sub"]))         # call isr_sub (near, on private stack)
    a.ins(4, lambda a, o: b"\x8B\x26" + u16(a.segoff("save_sp")))          # mov sp, [save_sp] (restore)
    a.ins(1, lambda a, o: b"\x58")                                         # pop ax
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("isr_ran")))          # inc word [isr_ran] (CLEAN completion)
    a.ins(2, lambda a, o: b"\xB0\x20")                                     # mov al, 0x20
    a.ins(2, lambda a, o: b"\xE6\x20")                                     # out 0x20, al (PIC EOI)
    a.ins(1, lambda a, o: b"\xCF")                                         # iret

    a.label("isr_sub")
    a.ins(1, lambda a, o: b"\x90")                                         # nop (trivial work)
    a.ins(1, lambda a, o: b"\xC3")                                         # ret (near ret ON the private stack)

    # ---------- data ----------
    a.label("counter");    a.dat(2, lambda a, o: u16(0))
    a.label("isr_ran");    a.dat(2, lambda a, o: u16(0))
    a.label("save_sp");    a.dat(2, lambda a, o: u16(0))
    a.label("trapid");     a.dat(2, lambda a, o: u16(0))
    a.label("outbuf");     a.dat(4, lambda a, o: b"\x00\x00\x00\x00")
    a.label("fname_res");  a.dat(8, lambda a, o: b"RES.BIN\x00")
    a.label("fname_fail"); a.dat(9, lambda a, o: b"FAIL.BIN\x00")
    return a


def main():
    a = build()
    com = a.assemble()
    with open(os.path.join(OUT, "ISTK.COM"), "wb") as f:
        f.write(com)
    labels = {name: ORG + off for name, off in a.labels.items()}
    with open(os.path.join(OUT, "istk_labels.json"), "w") as f:
        json.dump({"org": ORG, "size": len(com), "labels": labels,
                   # n_iters is timing-dependent for ISTK (how many loops before IRQ0 fires);
                   # the judge checks RES.BIN PRESENCE, not a fixed count. Kept for run_gate compat.
                   "n_iters": 0,
                   "private_sp": PRIVATE_SP, "sp_guard": SP_GUARD,
                   "pit_div": PIT_DIV, "trap_sp_id": TRAP_SP_ID}, f, indent=1)
    print(f"ISTK.COM: {len(com)} bytes")
    for name in ("entry", "loop_head", "sp_corrupt", "ISR", "isr_sub"):
        print(f"  {name:12s} file=0x{a.labels[name]:04X} segoff=0x{ORG + a.labels[name]:04X}")


if __name__ == "__main__":
    main()
