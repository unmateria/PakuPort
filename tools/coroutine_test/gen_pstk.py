#!/usr/bin/env python3
"""Synthetic "private-stack driver far-return" test: generate PSTK.COM by hand-assembling bytes.

Captures the class NAILED with runtime evidence in a real-mode game's boot (AdLib driver):
the sound-interface init (7901:0291) does the classic DOS driver prologue — pop its caller's
far return pair off the game stack, RELOCATE SS:SP to a private stack inside its data area
(`mov sp,1796h`; the buffer holds leftover file text, which is NORMAL), re-push the pair
there, run its far-call dance, and finally `retf` ON the private stack. The popped
destination (7901:01F9) is BY VALUE exactly the continuation of the lifted call-site that
invoked the driver (GameCode Fn_17600, push CS + push 0x01F9) — real hardware simply
continues there. But every unwind guard compares `(short)(SP - _spEntry) > 0` where
_spEntry was captured on the OLD stack: the delta is between DIFFERENT stacks, meaningless,
positive -> every frame PROPAGATES and the GENUINE return is discarded (farret-unwind in
gaps.log). The bottom-resume fallback then rescues the thread in the interpreter, so the
program may still complete — the RED signal of this suite is the DISCARD itself:

  GREEN = RES.BIN == 10000  AND  zero `farret-unwind` in logs/gaps.log  AND no FAIL.BIN
  (a by-value genuine return must not unwind AT ALL — and the loop must stay lifted).
  RED (HEAD today) = RES.BIN written via the bottom-resume crutch BUT gaps.log records the
  discarded genuine pair. FAIL.BIN = the resume landed somewhere wrong (different bug).

Regions (COM @ seg:0100):

  entry (always lifted, _spEntry ~ 0xFFFC): fills the segment word of [ptr_drv] with CS,
    then loops N=10000 times: `call far [ptr_drv]`. The inline continuation `loop_chk`
    (fall-through) is the GENUINE destination the driver's retf pops; it checks the BX
    canary set by the driver right before its retf (a resume with rancid registers fails
    it -> FAIL.BIN trap 0xBB), counts, and finally writes RES.BIN (2 bytes LE = counter).

  DRV island (via [ptr_drv]) — the private-stack driver:
    pop ax / pop dx           ; grab the caller's far return pair (IP, CS)
    mov [save_sp], sp         ; driver etiquette (save the caller stack)
    mov sp, DRIVER_SP         ; relocate to the private stack (COM: SS unchanged; the
                              ;   guards only see the SP delta, same degenerate compare)
    push dx / push ax         ; re-push the GENUINE pair on the private stack
    mov bx, 0x5AA5            ; canary
    retf                      ; hardware: continues at loop_chk with SP=DRIVER_SP

On real hardware / pure interpretation the loop completes on the private stack (the DOSBox-X
oracle pstk_oracle.conf proves it: RES.BIN == 10000)."""
import json
import os

from gen_test import Asm, u16, rel8, rel16, _emit_write

OUT = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100

N_ITERS = 10000
DRIVER_SP = 0x4000      # private stack top: (short)(0x4000 - 0xFFxx) > 0 -> guards PROPAGATE
CANARY = 0x5AA5
TRAP_CANARY_ID = 0x00BB  # loop_chk reached with a rancid BX (bad resume context)


def build():
    a = Asm()

    # ---------- entry (COM entry @ ORG, always lifted) ----------
    a.label("entry")
    a.ins(2, lambda a, o: b"\x8C\xC8")                                     # mov ax, cs
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("ptr_drv") + 2))          # mov [ptr_drv+2], ax
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("counter")) + u16(0))       # mov [counter], 0
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("N")) + u16(N_ITERS))       # mov [N], 10000
    a.label("loop_head")
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_drv")))          # call far [ptr_drv] -> DRV
    a.label("loop_chk")                                                    # GENUINE continuation (retf lands here)
    a.ins(4, lambda a, o: b"\x81\xFB" + u16(CANARY))                       # cmp bx, 5AA5 (driver canary)
    a.ins(2, lambda a, o: b"\x75" + rel8(o, a.labels["canary_fail"]))      # jne canary_fail
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("counter")))          # inc [counter]
    a.ins(4, lambda a, o: b"\xFF\x0E" + u16(a.segoff("N")))                # dec [N]
    a.ins(2, lambda a, o: b"\x75" + rel8(o, a.labels["loop_head"]))        # jnz loop_head
    _emit_write(a, "fname_res", "counter", 2)                              # write RES.BIN = counter, exit 4C00

    # ---------- canary_fail / fail_write: FAIL.BIN (counter, trapid) ----------
    a.label("canary_fail")
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_CANARY_ID))  # mov [trapid], 00BB
    a.label("fail_write")
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("counter")))              # mov ax, [counter]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf")))               # mov [outbuf], ax
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("trapid")))               # mov ax, [trapid]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf") + 2))           # mov [outbuf+2], ax
    _emit_write(a, "fname_fail", "outbuf", 4)

    # ---------- DRV island (via [ptr_drv]) : the private-stack driver ----------
    a.label("DRV")
    a.ins(1, lambda a, o: b"\x58")                                         # pop ax (return IP = loop_chk)
    a.ins(1, lambda a, o: b"\x5A")                                         # pop dx (return CS)
    a.ins(4, lambda a, o: b"\x89\x26" + u16(a.segoff("save_sp")))          # mov [save_sp], sp
    a.ins(3, lambda a, o: b"\xBC" + u16(DRIVER_SP))                        # mov sp, DRIVER_SP (private stack)
    a.ins(1, lambda a, o: b"\x52")                                         # push dx (re-push CS)
    a.ins(1, lambda a, o: b"\x50")                                         # push ax (re-push IP)
    a.ins(3, lambda a, o: b"\xBB" + u16(CANARY))                           # mov bx, 5AA5 (canary)
    a.ins(1, lambda a, o: b"\xCB")                                         # retf -> pops the genuine pair

    # ---------- data ----------
    a.label("ptr_drv")
    a.dat(4, lambda a, o: u16(a.segoff("DRV")) + u16(0))    # off (asm-time), seg (runtime)
    a.label("counter");    a.dat(2, lambda a, o: u16(0))
    a.label("N");          a.dat(2, lambda a, o: u16(0))
    a.label("trapid");     a.dat(2, lambda a, o: u16(0))
    a.label("save_sp");    a.dat(2, lambda a, o: u16(0))
    a.label("outbuf");     a.dat(4, lambda a, o: b"\x00\x00\x00\x00")
    a.label("fname_res");  a.dat(8, lambda a, o: b"RES.BIN\x00")
    a.label("fname_fail"); a.dat(9, lambda a, o: b"FAIL.BIN\x00")
    return a


def main():
    a = build()
    com = a.assemble()
    with open(os.path.join(OUT, "PSTK.COM"), "wb") as f:
        f.write(com)
    labels = {name: ORG + off for name, off in a.labels.items()}
    with open(os.path.join(OUT, "pstk_labels.json"), "w") as f:
        json.dump({"org": ORG, "size": len(com), "labels": labels,
                   "n_iters": N_ITERS, "driver_sp": DRIVER_SP,
                   "canary": CANARY, "trap_canary_id": TRAP_CANARY_ID}, f, indent=1)
    print(f"PSTK.COM: {len(com)} bytes")
    for name in ("entry", "loop_head", "loop_chk", "canary_fail", "DRV"):
        print(f"  {name:12s} file=0x{a.labels[name]:04X} segoff=0x{ORG + a.labels[name]:04X}")


if __name__ == "__main__":
    main()
