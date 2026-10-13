#!/usr/bin/env python3
"""Synthetic "near-consumed co-routine" tests: generate NCORA.COM / NCORB.COM by hand.

Captures a "near-consumed co-routine" class seen in a real-mode game: a driver in ANOTHER segment ends a
co-routine with a retf whose transported destination PROPAGATES through the far call-site
guard (rejected by the cross-segment fail-safe Cpu.ShouldFarRetRedirect: FarRetCs != c.CS,
because FarDispatch left c.CS at the DRIVER's segment) and then STOPS at the NEAR call-site
guard of the caller (SP == _spEntry -> CONSUMED branch). Lifter.cs CallGuard (near) has no
redirect: it discards the popped destination (NoteFarRetUnwind -> gaps.log farret-unwind)
and resumes INLINE at the call-site's static return -> the pass dies.

The real-mode ALIASING trick makes the driver "another segment" inside one COM: entry fills
[ptr_drv] with segment CS+0x10 and an offset already reduced by 0x100 at assemble time —
same linear address (FarDispatch resolves the same lifted key), DIFFERENT segment value, so
the far-site redirect is vetoed exactly like that game's graphics driver.

NCORA.COM — the class (expected RED on HEAD):

  entry (always lifted): fills [ptr_loop].seg = CS and [ptr_drv].seg = CS+0x10, then
    `call far [ptr_loop]`. Resuming inline here = poison FAIL.BIN trap 0xEEEE (bad unwind
    reached the bottom call-site).
  BUCLE island (via [ptr_loop], resident CS): N=10000 CORO-style loop. Each iteration:
    push cs / push offset loop_check (manual far continuation in the RESIDENT segment) /
    `call near OUTER`. The instruction after the near call (trap_pair) is POISON: the class
    fires there -> FAIL.BIN (counter, trap 0xDDDD).
  OUTER (near-called from BUCLE): `call far [ptr_drv]` -> DRIVER. The instruction after the
    far call is POISON trap 0xAAAA (the driver discards OUTER's near return; on hardware it
    never runs; a wrong redirect-to-far-site fix would light it).
  DRIVER island (via [ptr_drv], ALIAS CS+0x10): trivial work + `add sp,6` (drops its own far
    pair AND OUTER's near return) + `retf` -> pops the hand-pushed loop_check:CS.

  Hardware: retf lands on loop_check, N reaches 0, RES.BIN = counter (2 bytes LE, 10000).
  HEAD: far-site guard PROPAGATES (SP > _spEntry; redirect vetoed cross-segment) -> near
  guard CONSUMES (SP == _spEntry, FarRetIp=loop_check != trap_pair) -> destination DISCARDED
  (farret-unwind in gaps.log) -> resumes inline on trap_pair -> FAIL.BIN counter=1 0xDDDD.

NCORB.COM — the LEGITIMATE case the future fix must NOT break (expected GREEN on HEAD):

  Same skeleton, but the near call to OUTER carries 2 INLINE PARAM bytes (Borland idiom:
  OUTER does push bp / mov bp,sp / `add word [bp+2],2` / pop bp — Emitter.cs
  DetectInlineParamFunctions detects it and the lifted call-site emits `goto L_skip` after
  the guard). BUCLE pushes CS before the near call, so (adjusted-return-cell, pushed CS)
  form a far pair; the DRIVER drops only its own far pair (`add sp,4` — NOT 6: the adjusted
  near-return cell IS the retf IP) and retf's the ADJUSTED destination L_skip. The lifted
  near guard sees FarRet=true + mismatch (L_skip != static ret), discards, and the inline
  resume via `goto L_skip` IS the correct continuation: green by construction. A future fix
  that redirects this consumed-mismatch would run L_skip TWICE — the BX canary (driver sets
  BX=0x5AA5 before retf; L_skip checks and then kills it) catches that -> FAIL.BIN 0xEEEE.
  The inline bytes themselves are `mov bl,0xEE` (2 bytes, decodable so the skip target
  snaps exactly): if the lifter ever FAILS to detect the inline-params site, the inline
  resume executes them, the canary dies with BL=0xEE and the fail path reports the
  distinct trap 0xCCCC (inline-params not detected) instead.

  Hardware: driver retf lands on the adjusted cell (L_skip); RES.BIN = 10000.

NCORC.COM — the RESIDUAL-FLAG frontier ([NREDIR] counterexamples; expected RED on HEAD):

  The root of the 5th attempt: the lifted far-site guards' PROPAGATE-redirect branch calls
  Interp.Run on the transported co-routine WITHOUT consuming c.FarRet first, and only ever
  re-raises (`if (Run) c.FarRet = true`). When the continuation ends in a BALANCED NEAR RET
  (Run == false) the stale flag travels up already-attended: the next guard that sees it
  re-redirects to the SAME (already executed) FarRetCs/Ip -> double execution.

  entry (MAIN, always lifted): pushes cont (near, ALIAS offset), then the K far pair
    (alias), then `call far [ptr_A]`. Its guard is where the STALE redirect re-fires on
    HEAD. main_resume (`jmp done`) is the LEGIT inline resume once the flag is clean.
  A_ISLAND (resident, far-called): `call far [ptr_drv]` -> DRV. Its guard takes the
    PROPAGATE branch (the driver unwinds A's own pair) and REDIRECTS to K — genuinely
    correct, but on HEAD the flag enters Interp.Run still set. a_poison = 0xAAAA.
  DRV island (ALIAS CS+0x10): `add sp,8` (its own far pair AND A's far pair) + `retf` ->
    pops the hand-pushed K:alias pair.
  K (interp'd co-routine): runs ONCE on hardware; `ret` pops cont (balanced near exit ->
    Interp.Run returns false -> HEAD leaves the flag set). A second entry (the stale
    re-redirect) is detected by `cmp [counter],0` -> trap 0xDCDC.

  Hardware: K's ret lands on cont -> RES.BIN counter = 1. HEAD: FAIL.BIN 0xDCDC.

Success = RES.BIN counter 10000 (A/B) / 1 (C). NCORA FAIL.BIN 0xDDDD = the class fired
(expected on HEAD). NCORB FAIL.BIN 0xEEEE = double-executed continuation (a fix broke the
veto); 0xCCCC = inline-params not detected; 0xBBBB/0xAAAA = wrong-place resumes (other
bugs). NCORC FAIL.BIN 0xDCDC = stale-flag re-redirect double-ran the co-routine."""
import json
import os

from gen_test import Asm, u16, rel8, rel16, _emit_write

OUT = os.path.dirname(os.path.abspath(__file__))
ORG = 0x100

N_ITERS = 10000
ALIAS_PARAS = 0x10       # driver segment = CS + 0x10, offsets reduced by 0x100 (same linear)
CANARY = 0x5AA5
TRAP_PAIR_ID = 0xDDDD    # A: near guard discarded the destination, resumed on the call pair
TRAP_ENTRY_ID = 0xEEEE   # A: unwound all the way back to entry's call-site
TRAP_ENTRY_B_ID = 0xBBBB # B: unwound all the way back to entry's call-site
TRAP_FARSITE_ID = 0xAAAA # A/B: OUTER's far call-site resumed inline (driver discarded it)
TRAP_CANARY_ID = 0xEEEE  # B: canary dead -> L_skip re-executed without a fresh driver retf
TRAP_INLINE_ID = 0xCCCC  # B: inline param bytes EXECUTED -> call-site did not emit the goto
TRAP_STALE_ID = 0xDCDC   # C: co-routine K entered TWICE -> stale-flag re-redirect (residual)


def _entry(a, trap_id):
    # entry: [ptr_loop].seg = CS (resident), [ptr_drv].seg = CS+0x10 (alias), then far call.
    a.label("entry")
    a.ins(2, lambda a, o: b"\x8C\xC8")                                     # mov ax, cs
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("ptr_loop") + 2))         # mov [ptr_loop+2], ax
    a.ins(3, lambda a, o: b"\x05" + u16(ALIAS_PARAS))                      # add ax, 0x10
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("ptr_drv") + 2))          # mov [ptr_drv+2], ax
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_loop")))         # call far [ptr_loop] -> BUCLE
    a.label("entry_poison")                                                # reached only on a bad unwind
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(trap_id))
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))      # jmp fail_write


def _tail(a):
    # done: RES.BIN (2 bytes = counter); fail_write: FAIL.BIN (4 bytes = counter, trapid)
    a.label("done")
    _emit_write(a, "fname_res", "counter", 2)
    a.label("fail_write")
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("counter")))              # mov ax, [counter]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf")))               # mov [outbuf], ax
    a.ins(3, lambda a, o: b"\xA1" + u16(a.segoff("trapid")))               # mov ax, [trapid]
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("outbuf") + 2))           # mov [outbuf+2], ax
    _emit_write(a, "fname_fail", "outbuf", 4)


def _data(a):
    a.label("ptr_loop")
    a.dat(4, lambda a, o: u16(a.segoff("BUCLE")) + u16(0))                 # off asm-time, seg runtime
    a.label("ptr_drv")                                                     # ALIAS: off reduced by 0x100
    a.dat(4, lambda a, o: u16(a.segoff("DRV") - 0x100) + u16(0))
    a.label("counter");    a.dat(2, lambda a, o: u16(0))
    a.label("N");          a.dat(2, lambda a, o: u16(0))
    a.label("trapid");     a.dat(2, lambda a, o: u16(0))
    a.label("work");       a.dat(2, lambda a, o: u16(0))
    a.label("outbuf");     a.dat(4, lambda a, o: b"\x00\x00\x00\x00")
    a.label("fname_res");  a.dat(8, lambda a, o: b"RES.BIN\x00")
    a.label("fname_fail"); a.dat(9, lambda a, o: b"FAIL.BIN\x00")


def build_a():
    a = Asm()
    _entry(a, TRAP_ENTRY_ID)

    # ---------- BUCLE island (via [ptr_loop], resident CS) ----------
    a.label("BUCLE")
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("counter")) + u16(0))       # mov [counter], 0
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("N")) + u16(N_ITERS))       # mov [N], 10000
    a.label("loop_check")                                                        # manual continuation target
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("counter")))                # inc [counter]
    a.ins(4, lambda a, o: b"\xFF\x0E" + u16(a.segoff("N")))                      # dec [N]
    a.ins(2, lambda a, o: b"\x74" + rel8(o, a.labels["L_done"]))                 # jz L_done
    a.ins(1, lambda a, o: b"\x0E")                                               # push cs
    a.ins(3, lambda a, o: b"\x68" + u16(a.segoff("loop_check")))                 # push offset loop_check
    a.ins(3, lambda a, o: b"\xE8" + rel16(o, a.labels["OUTER"]))                 # call near OUTER
    a.label("trap_pair")                                                         # near-call return: POISON
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_PAIR_ID))
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))            # jmp fail_write
    a.label("L_done")
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["done"]))                  # jmp done

    # ---------- OUTER (near-called from BUCLE): the far co-routine site ----------
    a.label("OUTER")
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_drv")))                # call far [ptr_drv] -> DRV (alias)
    a.label("outer_poison")                                                      # far-site return: never on hw
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_FARSITE_ID))
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))            # jmp fail_write

    # ---------- DRV island (via [ptr_drv], ALIAS CS+0x10) ----------
    a.label("DRV")
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("work")))                   # inc [work] (trivial work)
    a.ins(3, lambda a, o: b"\x83\xC4\x06")                                       # add sp, 6 (far pair + near ret)
    a.ins(1, lambda a, o: b"\xCB")                                               # retf -> hand-pushed loop_check:CS

    _tail(a)
    _data(a)
    return a


def build_b():
    a = Asm()
    _entry(a, TRAP_ENTRY_B_ID)

    # ---------- BUCLE island (via [ptr_loop], resident CS) ----------
    a.label("BUCLE")
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("counter")) + u16(0))       # mov [counter], 0
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("N")) + u16(N_ITERS))       # mov [N], 10000
    a.label("loop_top")
    a.ins(1, lambda a, o: b"\x0E")                                               # push cs (CS half of the far pair)
    a.ins(3, lambda a, o: b"\xE8" + rel16(o, a.labels["OUTER"]))                 # call near OUTER (+2 inline bytes)
    a.label("inline_par")                                                        # 2 INLINE PARAM bytes (data; decode
    a.dat(2, lambda a, o: b"\xB3\xEE")                                           #   as `mov bl,0xEE` = tripwire)
    a.label("L_skip")                                                            # adjusted continuation (retf lands here)
    a.ins(4, lambda a, o: b"\x81\xFB" + u16(CANARY))                             # cmp bx, 5AA5 (driver canary)
    a.ins(2, lambda a, o: b"\x75" + rel8(o, a.labels["canary_fail"]))            # jne canary_fail
    a.ins(3, lambda a, o: b"\xBB" + u16(0))                                      # mov bx, 0 (consume the canary)
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("counter")))                # inc [counter]
    a.ins(4, lambda a, o: b"\xFF\x0E" + u16(a.segoff("N")))                      # dec [N]
    a.ins(2, lambda a, o: b"\x75" + rel8(o, a.labels["loop_top"]))               # jnz loop_top
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["done"]))                  # jmp done
    a.label("canary_fail")
    a.ins(3, lambda a, o: b"\x80\xFB\xEE")                                       # cmp bl, 0xEE (inline bytes ran?)
    a.ins(2, lambda a, o: b"\x75" + rel8(o, a.labels["cf_generic"]))             # jne cf_generic
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_INLINE_ID))
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))            # jmp fail_write
    a.label("cf_generic")
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_CANARY_ID))
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))            # jmp fail_write

    # ---------- OUTER (near-called, Borland inline-params callee) ----------
    a.label("OUTER")
    a.ins(1, lambda a, o: b"\x55")                                               # push bp
    a.ins(2, lambda a, o: b"\x89\xE5")                                           # mov bp, sp
    a.ins(4, lambda a, o: b"\x83\x46\x02\x02")                                   # add word [bp+2], 2 (skip inline)
    a.ins(1, lambda a, o: b"\x5D")                                               # pop bp
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_drv")))                # call far [ptr_drv] -> DRV (alias)
    a.label("outer_poison")                                                      # far-site return: never on hw
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_FARSITE_ID))
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))            # jmp fail_write

    # ---------- DRV island (via [ptr_drv], ALIAS CS+0x10) ----------
    a.label("DRV")
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("work")))                   # inc [work] (trivial work)
    a.ins(3, lambda a, o: b"\xBB" + u16(CANARY))                                 # mov bx, 5AA5 (canary)
    a.ins(3, lambda a, o: b"\x83\xC4\x04")                                       # add sp, 4 (ONLY its far pair: the
    a.ins(1, lambda a, o: b"\xCB")                                               #   adjusted near cell IS the retf IP)
                                                                                 # retf -> L_skip:CS (adjusted)
    _tail(a)
    _data(a)
    return a


def build_c():
    a = Asm()
    # ---------- entry (MAIN): hand-pushes cont + the K far pair, then far-calls A ----------
    a.label("entry")
    a.ins(2, lambda a, o: b"\x8C\xC8")                                     # mov ax, cs
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("ptr_A") + 2))            # mov [ptr_A+2], ax (resident)
    a.ins(3, lambda a, o: b"\x05" + u16(ALIAS_PARAS))                      # add ax, 0x10
    a.ins(3, lambda a, o: b"\xA3" + u16(a.segoff("ptr_drv") + 2))          # mov [ptr_drv+2], ax (alias)
    a.ins(3, lambda a, o: b"\x68" + u16(a.segoff("cont") - 0x100))         # push cont (ALIAS off: K's ret pops it)
    a.ins(1, lambda a, o: b"\x50")                                         # push ax (K seg = alias)
    a.ins(3, lambda a, o: b"\x68" + u16(a.segoff("K") - 0x100))            # push K (ALIAS off)
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_A")))            # call far [ptr_A] -> A_ISLAND
    a.label("main_resume")                                                 # LEGIT inline resume (clean flag)
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["done"]))            # jmp done (RES.BIN <- counter)

    # ---------- A_ISLAND (resident, far-called): the PROPAGATE-redirect site ----------
    a.label("A_ISLAND")
    a.ins(4, lambda a, o: b"\xFF\x1E" + u16(a.segoff("ptr_drv")))          # call far [ptr_drv] -> DRV (alias)
    a.label("a_poison")                                                    # never on hw (driver unwinds this pair)
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_FARSITE_ID))
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))      # jmp fail_write

    # ---------- DRV island (ALIAS CS+0x10): unwinds TWO pairs, retf to K ----------
    a.label("DRV")
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("work")))             # inc [work] (trivial work)
    a.ins(3, lambda a, o: b"\x83\xC4\x08")                                 # add sp, 8 (its far pair + A's far pair)
    a.ins(1, lambda a, o: b"\xCB")                                         # retf -> hand-pushed K:alias

    # ---------- K co-routine (interp'd): once on hw; a stale re-redirect trips it ----------
    a.label("K")
    a.ins(5, lambda a, o: b"\x83\x3E" + u16(a.segoff("counter")) + b"\x00")# cmp word [counter], 0
    a.ins(2, lambda a, o: b"\x75" + rel8(o, a.labels["k_poison"]))         # jne k_poison (2nd entry = stale)
    a.ins(4, lambda a, o: b"\xFF\x06" + u16(a.segoff("counter")))          # inc [counter]
    a.ins(1, lambda a, o: b"\xC3")                                         # ret -> pops cont (balanced: Run == false)
    a.label("k_poison")
    a.ins(6, lambda a, o: b"\xC7\x06" + u16(a.segoff("trapid")) + u16(TRAP_STALE_ID))
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["fail_write"]))      # jmp fail_write
    a.label("cont")                                                        # hardware continuation of K's ret
    a.ins(3, lambda a, o: b"\xE9" + rel16(o, a.labels["done"]))            # jmp done

    _tail(a)
    a.label("ptr_A")
    a.dat(4, lambda a, o: u16(a.segoff("A_ISLAND")) + u16(0))              # off asm-time, seg runtime
    a.label("ptr_drv")                                                     # ALIAS: off reduced by 0x100
    a.dat(4, lambda a, o: u16(a.segoff("DRV") - 0x100) + u16(0))
    a.label("counter");    a.dat(2, lambda a, o: u16(0))
    a.label("trapid");     a.dat(2, lambda a, o: u16(0))
    a.label("work");       a.dat(2, lambda a, o: u16(0))
    a.label("outbuf");     a.dat(4, lambda a, o: b"\x00\x00\x00\x00")
    a.label("fname_res");  a.dat(8, lambda a, o: b"RES.BIN\x00")
    a.label("fname_fail"); a.dat(9, lambda a, o: b"FAIL.BIN\x00")
    return a


def _write(name, a, extra):
    com = a.assemble()
    with open(os.path.join(OUT, f"{name}.COM"), "wb") as f:
        f.write(com)
    labels = {n: ORG + off for n, off in a.labels.items()}
    meta = {"org": ORG, "size": len(com), "labels": labels, "n_iters": N_ITERS,
            "alias_paras": ALIAS_PARAS}
    meta.update(extra)
    with open(os.path.join(OUT, f"{name.lower()}_labels.json"), "w") as f:
        json.dump(meta, f, indent=1)
    print(f"{name}.COM: {len(com)} bytes")
    for n in sorted(a.labels, key=a.labels.get):
        if n in ("entry", "BUCLE", "loop_check", "loop_top", "trap_pair", "inline_par",
                 "L_skip", "OUTER", "outer_poison", "DRV", "done", "fail_write",
                 "main_resume", "A_ISLAND", "a_poison", "K", "k_poison", "cont"):
            print(f"  {n:12s} file=0x{a.labels[n]:04X} segoff=0x{ORG + a.labels[n]:04X}")


def main():
    _write("NCORA", build_a(), {"trap_pair_id": TRAP_PAIR_ID, "trap_entry_id": TRAP_ENTRY_ID,
                                "trap_farsite_id": TRAP_FARSITE_ID})
    _write("NCORB", build_b(), {"canary": CANARY, "trap_canary_id": TRAP_CANARY_ID,
                                "trap_inline_id": TRAP_INLINE_ID, "trap_entry_id": TRAP_ENTRY_B_ID,
                                "trap_farsite_id": TRAP_FARSITE_ID})
    _write("NCORC", build_c(), {"n_iters": 1, "trap_stale_id": TRAP_STALE_ID,
                                "trap_farsite_id": TRAP_FARSITE_ID})


if __name__ == "__main__":
    main()
