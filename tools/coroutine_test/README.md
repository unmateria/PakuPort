# FarRet / co-routine / ISR class probes (`CORO`, `DEEP`, `PSTK`, `NCORA/B/C`, `DE`, `ISTK`, `IIDM`)

## What it measures

Hand-assembled `.COM` programs (about 50-230 bytes each, no external assembler) that reproduce,
in isolation, control-flow patterns that broke the lifted (C#) port of real DOS games. Each one
is exported by PakuPort in several forced partitionings (which islands are lifted and which are
interpreted, via `extraSeeds` in `games/<id>/game.json`), run headless, and judged by the
counter written to `RES.BIN`. A `FAIL.BIN` with a trap id means a poison instruction ran, i.e.
the port resumed somewhere a real CPU never would.

| suite | generator | pattern |
|-------|-----------|---------|
| CORO  | `gen_test.py` | Intra-module co-routine (blitter): `retf` to a manually pushed continuation at the caller's own stack level. |
| DEEP  | `gen_deep.py` | "Unwind-to-the-bottom": a driver init switches to a low private stack and `retf`s several frames up; every lifted guard used to PROPAGATE and the thread exited "cleanly" before the continuation ran. |
| PSTK  | `gen_pstk.py` | Private-stack driver far return: the popped pair is BY VALUE the caller's genuine return, but SP deltas were compared across different stacks and the return was discarded. GREEN also requires zero `farret-unwind` in `logs/gaps.log`. |
| NCORA | `gen_ncor.py` | Near-consumed co-routine: a driver in another segment (real-mode alias `CS+0x10`) `retf`s a hand-pushed continuation; the near call-site guard used to discard it. Run with `PAKU_NREDIR=1`. |
| NCORB | `gen_ncor.py` | The LEGITIMATE veto case (Borland inline-params call-site): resuming inline IS correct here. Must stay GREEN with any future fix. |
| NCORC | `gen_ncor.py` | Residual-flag frontier: a stale `c.FarRet` after a balanced near `ret` made the next guard re-execute the continuation. |
| DE    | `gen_de.py` | `#DE` parity lifted vs interpreted: 10000 divide-by-zero plus 10000 divide-overflow faults through a real-mode INT 0 handler (8086 semantics, resume after the `div`). No DOSBox oracle (pushed-IP semantics vary per CPU type). |
| ISTK  | `gen_istk.py` | Timer ISR that switches to a private stack; the nested interpreter run used to bail on the first `ret` there and leave SP relocated. Interp-only. |
| IIDM  | `gen_iidm.py` | `pushf / push cs / call sub` ending in IRET used as a return idiom inside an ISR; the nested run used to end on that first IRET. Interp-only. |

## Why it exists

Each suite was distilled from a crash or hang in a real-mode game (an AdLib driver init that
killed the game thread, a graphics driver that returned through an aliased segment, a sound
driver ISR that leaked words onto the interrupted stack, a C runtime long-division helper that
killed the host with `DivideByZeroException`). The synthetic program reproduces the pattern
deterministically so the fix can be judged red/green without the game.

The DOSBox-X oracle `.conf` files run the same COMs on a stock DOSBox-X to prove the program
itself is correct (`RES.BIN == expected`), so any red is the port's fault.

## The core pattern (CORO)

- **entry** (COM entry `seg:0100`, always lifted): fills the segment word of two far pointers
  with CS, then `call far [ptr_loop]`. Returning here is a poison.
- **BUCLE** (reachable only via `[ptr_loop]`): N=10000 iterations of `push cs` +
  `push offset loop_check` + `call far [ptr_row]`; at the end writes `RES.BIN` (2 bytes LE,
  expected 10000) and exits.
- **FILA** (reachable only via `[ptr_row]`): `add sp,4` (drops its own return pair) + `retf`,
  which jumps to the manually pushed `loop_check`. If the port resumes at the call-site's saved
  return instead, it lands on `trap_pair`, which writes `FAIL.BIN` (trap `0xDDDD`).

```
loop_check:                       SP=L
  push cs                         SP=L-2
  push offset loop_check          SP=L-4
  call far [ptr_row]              SP=L-8   (return pair -> trap_pair)
FILA:
  add sp,4                        SP=L-4
  retf  -> CORRECT: loop_check     BUG: trap_pair (popped destination discarded)
```

## How to run (from the repo root)

```
dotnet build src\PakuPort.Cli -c Release
python tools\coroutine_test\run_gate.py
```

The gate regenerates every COM, exports/builds/runs each partitioning, and runs the DOSBox-X
oracles (`*_oracle.conf`, headless with `SDL_VIDEODRIVER=dummy`). To run one oracle by hand:

```
dosbox-x -conf tools\coroutine_test\deep_oracle.conf
```

Environment: `PAKU_DOSBOX` = path to `dosbox-x.exe` (default: `dosbox-x` on PATH);
`CORO_AUTOEXIT_MS` = headless window per run (default 30000; each COM finishes in under 1 s).

Exit code 0 iff every suite is in its expected state (see `SUITES` in `run_gate.py`).

## Measured result

- CORO: GREEN 3/3 (`RES.BIN == 10000`) since the FarRet redirect fix.
- DEEP: was RED 3/3 (no `RES.BIN`, no `FAIL.BIN`, `gaps.log` showed the genuine destination
  `0110:0131` discarded); GREEN after the bottom-resume fix. Oracle and pure interpretation
  both write `RES.BIN == 10000`.
- PSTK: GREEN after the by-value guard fix (compare `FarRetCs:FarRetIp` with the pushed pair
  before the SP-delta check).
- NCORA: GREEN 2/2 behind `PAKU_NREDIR=1` (the near redirect ships OFF by default because it
  misbehaved in a real game during a manual playtest); NCORB stays GREEN.
- NCORC: GREEN 2/2 after consuming the flag before `Interp.Run`.
- DE, ISTK, IIDM: expected GREEN on the current tree.

Caveat: for a plain COM the export runs an iterative probe that executes the entry and discovers
the far-called islands dynamically, so partitions with fewer seeds tend to converge to
all-lifted. The classes above were verified to be boundary-independent (same verdict lifted
-> lifted, interp -> lifted and interp -> interp).

## Files

- `gen_*.py` — generators; each writes its `.COM` and `*_labels.json` (island offsets used as
  `@NAME` seeds).
- `run_gate.py` — the automated gate.
- `*_oracle.conf` — DOSBox-X configs (mount path relative to the repo root).
- `*.COM`, `*_labels.json`, `RES.BIN` — generated artifacts.
