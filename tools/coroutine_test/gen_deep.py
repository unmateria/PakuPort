#!/usr/bin/env python3
"""Synthetic "unwind-to-the-bottom" test: generate DEEP.COM by hand-assembling bytes.

Captures the unwind-to-the-bottom class that the intra-module co-routine
gate (gen_test.py / CORO.COM) does NOT cover. In a real-mode game's boot the AdLib driver init fires a
retf whose FarRet unwind walks through ALL C# frames by the PROPAGATE branch (SP > _spEntry
at every guard) and KILLS the game thread with a "clean exit" (no INT 21h/4Ch) — discarding
GENUINE driver->driver destinations (3673->7901:01F9). The earlier FarRet redirect fix
only acts in the STOP branch, so it is never even invoked here.

CORO.COM's retf STOPS at a frame (its continuation `loop_check` sits at the caller BUCLE's
own stack level, SP <= _spEntry -> STOP -> redirect). DEEP.COM instead pushes the
continuation onto a RELOCATED, low-addressed "driver stack" and returns to it by discarding
several frames at once, so the popped SP is a *positive signed delta* from EVERY frame's
_spEntry (including the COM entry's 0xFFFE) -> every guard PROPAGATES -> the thread exits
before the continuation runs. The lost work is the whole 10000-iteration loop.

Regions (COM @ seg:0100):

  entry (always lifted, _spEntry = 0xFFFE):
    fills the SEGMENT word of two far pointers with CS, then RELOCATES SP to a low private
    "driver stack" (0x4000, mirroring a driver that switches to its own stack), pushes a
    manual far continuation CONT at the top of it (push cs / push offset CONT), and does
    `call far [ptr_disp]`. If control ever RESUMES here inline, a bad unwind stopped/redirected
    to the wrong place -> poison FAIL.BIN trap 0xEE.

  DISPATCH island (via [ptr_disp]): a middle "driver dispatcher" frame; `call far [ptr_inner]`.
    Resuming here inline -> poison FAIL.BIN trap 0xDD.

  INNER island (via [ptr_inner]): THE driver-init retf. `add sp,8` drops INNER's OWN and
    DISPATCH's return frames, then `retf` pops entry's hand-pushed CONT far pointer. On real
    hardware / pure interp control jumps to CONT. In the lifted port the popped SP (0x4000)
    exceeds every frame's _spEntry -> the unwind propagates out of the top frame -> thread dies.

  CONT (reached ONLY by the retf landing correctly): runs the N=10000 counter loop, writes
    RES.BIN (2 bytes LE = counter, expected 10000/0x2710) and exits 4C00.

Success = RES.BIN present with counter 10000. The unwind-to-the-bottom bug = NEITHER file written (clean
thread exit, matching the AdLib symptom). FAIL.BIN present = a DIFFERENT mechanism fired
(a poison), which would tell the investigator the unwind stopped/redirected rather than propagated.

The islands are reachable ONLY by far-indirect call, so a purely static discovery never sees
them; which of {entry, DISPATCH, INNER} get LIFTED is forced per export via `extraSeeds` in
games/<id>/game.json, id=deep (see run_gate.py). CONT is intentionally never seeded (the bug kills the
thread before it, so it need not be lifted; on hardware it is just COM bytes).
"""
import json
import os

from gen_test import Asm, u16, rel8, rel16, _emit_write

OUT = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100

N_ITERS = 10000
DRIVER_SP = 0x4000      # low private "driver stack" top; (short)(0x4000 - 0xFFFE) > 0 => every
                        # frame's guard PROPAGATES. Must stay <= 0x7FFD to keep the signed delta
                        # positive against 0xFFFE, and well above the image (< ~0x0200) for I/O.
TRAP_ENTRY_ID = 0x00EE  # a poison at entry's inline return fired (unwind stopped/redirected wrong)
TRAP_DISP_ID = 0x00DD   # a poison at dispatch's inline return fired


def build():
    a = Asm()

    # ---------- entry (COM entry @ ORG, _spEntry = 0xFFFE) ----------
    a.label("entry")
    a.ins(2, lambda a, o: b"\x8C\xC8")                                     # mov ax, cs
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("ptr_disp") + 2))         # mov [ptr_disp+2], ax
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("ptr_inner") + 2))        # mov [ptr_inner+2], ax
    a.ins(3, lambda a, o: b"\xBC" + u16(DRIVER_SP))                        # mov sp, DRIVER_SP  (switch to driver stack)
    a.ins(1, lambda a, o: b"\x0E")                                         # push cs           [SP-2]=CS
    a.ins(3, lambda a, o: b"\x68" + u16(a.segoff("CONT")))                 # push offset CONT   [SP-2]=CONT (manual continuation)
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_disp")))         # call far [ptr_disp] -> DISPATCH
    a.label("after_entry")                                                # POISON: entry resumed inline
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_ENTRY_ID))  # mov [trapid], 00EE
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))      # jmp fail_write

    # ---------- DISPATCH island (via [ptr_disp]) : middle driver frame ----------
    a.label("DISPATCH")
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_inner")))        # call far [ptr_inner] -> INNER
    a.label("after_disp")                                                 # POISON: dispatch resumed inline
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_DISP_ID))   # mov [trapid], 00DD
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))      # jmp fail_write

    # ---------- INNER island (via [ptr_inner]) : the driver-init deep retf ----------
    a.label("INNER")
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("work")))             # inc [work] (trivial marker)
    a.ins(3, lambda a, o: b"\x83\xC4\x08")                                 # add sp, 8 (drop INNER + DISPATCH return frames)
    a.ins(1, lambda a, o: b"\xCB")                                         # retf -> pops entry's hand-pushed CONT far ptr

    # ---------- CONT: the continuation (runs ONLY if the retf lands correctly) ----------
    a.label("CONT")
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("counter")) + u16(0))        # mov [counter], 0
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("N")) + u16(N_ITERS))        # mov [N], 10000
    a.label("cont_loop")
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("counter")))          # inc [counter]
    a.ins(4, lambda a, o: b"\xFF\x0E" + u16(a.segoff("N")))                # dec [N]
    a.ins(2, lambda a, o: b"\x75" + rel8(o, a.labels["cont_loop"]))        # jnz cont_loop
    _emit_write(a, "fname_res", "counter", 2)                             # write RES.BIN = counter, exit 4C00

    # ---------- fail_write: write FAIL.BIN (4 bytes = counter, trapid) if a poison fired ----------
    a.label("fail_write")
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("counter")))             # mov ax, [counter]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf")))              # mov [outbuf], ax
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("trapid")))              # mov ax, [trapid]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf") + 2))          # mov [outbuf+2], ax
    _emit_write(a, "fname_fail", "outbuf", 4)

    # ---------- data ----------
    a.label("ptr_disp")
    a.dat(4, lambda a, o: u16(a.segoff("DISPATCH")) + u16(0))   # off (asm-time), seg (runtime)
    a.label("ptr_inner")
    a.dat(4, lambda a, o: u16(a.segoff("INNER")) + u16(0))
    a.label("counter");    a.dat(2, lambda a, o: u16(0))
    a.label("N");          a.dat(2, lambda a, o: u16(0))
    a.label("trapid");     a.dat(2, lambda a, o: u16(0))
    a.label("work");       a.dat(2, lambda a, o: u16(0))
    a.label("outbuf");     a.dat(4, lambda a, o: b"\x00\x00\x00\x00")
    a.label("fname_res");  a.dat(8, lambda a, o: b"RES.BIN\x00")
    a.label("fname_fail"); a.dat(9, lambda a, o: b"FAIL.BIN\x00")
    return a


def main():
    a = build()
    com = a.assemble()
    with open(os.path.join(OUT, "DEEP.COM"), "wb") as f:
        f.write(com)
    labels = {name: ORG + off for name, off in a.labels.items()}
    with open(os.path.join(OUT, "deep_labels.json"), "w") as f:
        json.dump({"org": ORG, "size": len(com), "labels": labels,
                   "n_iters": N_ITERS, "driver_sp": DRIVER_SP,
                   "trap_entry_id": TRAP_ENTRY_ID, "trap_disp_id": TRAP_DISP_ID}, f, indent=1)
    print(f"DEEP.COM: {len(com)} bytes")
    for name in ("entry", "after_entry", "DISPATCH", "after_disp", "INNER", "CONT", "cont_loop", "fail_write"):
        print(f"  {name:12s} file=0x{a.labels[name]:04X} segoff=0x{ORG + a.labels[name]:04X}")


if __name__ == "__main__":
    main()
