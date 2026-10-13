# Speed probe: is PakuPort's `cyclesPerMs` the same unit as DOSBox-X's `cycles`?

An objective net for calibrating games **with no clock of their own**, i.e. games whose speed is
proportional to the instructions per second they get. It was written after several by-eye A/B
speed comparisons on such a game failed to converge.

## Why it exists

A speed A/B judged by eye cannot tell "our emulator runs faster than the original" from "the
user wants another speed". And the unit is not obvious: PakuPort's `cyclesPerMs` counts
**instructions per millisecond**, while DOSBox's `cycles` counts *cycles*, and a real 8086 needs
several per instruction. If the units differed, the same number would give different speeds in
each world and every comparison against the oracle would be wrong. This has to be measured.

## What it measures

`SPEED.COM` (148 bytes, hand-assembled, checked with capstone) counts how many iterations two
loops of very different cost complete in 18 BIOS ticks (~0.989 s):

| loop | instr/iter | body |
|------|-----------|------|
| A | 4 | registers only + one `cmp` against memory |
| B | 8 | three memory accesses + a word `MUL` |

The **ratio between worlds** gives the speed factor; the **difference between A and B inside one
world** tells whether the emulator charges real instruction cost or one cycle per instruction.

`SPEED.BIN` layout (8 bytes): `+0` dword = loop A iterations, `+4` dword = loop B iterations.

## Measured result

| | loop A | loop B |
|---|---|---|
| Oracle DOSBox-X `cycles=3000, core=normal, cputype=386` | 2,969,302 instr/s | 2,969,335 instr/s |
| PakuPort `cyclesPerMs=3000` | 2,998,471 instr/s | 2,994,338 instr/s |

1. **A and B agree to the fourth digit in each world**, although B is much more expensive on a
   real 8086 -> DOSBox-X with `core=normal` charges **1 cycle per instruction**.
2. **Both worlds agree within 1.0 %** -> `cyclesPerMs=N` == `cycles=N`. Comparing against the
   oracle with the same number is legitimate.

This refuted an earlier measurement that credited the oracle with 4.7-7.3 M instr/s at
`cycles=3000` (impossible: more instructions than cycles).

## `REPTEST.COM`: what `SPEED.COM` missed

`SPEED.COM` has no `REP` prefix, so its conclusion was true **and irrelevant for any game that
draws**:

- DOSBox-X `core=normal` charges one cycle **per element** inside every REP loop
  (`if ((--CPU_Cycles) <= 0) break;` in `src/cpu/core_normal/string.h`).
- PakuPort ran the whole `while (c.CX != 0) { c.CX--; Body(); }` **inside a single
  `Cpu.Step()`**: a 1000-word `rep movsw` cost 1 tick instead of 1000.

Measured with `REPTEST.COM` (a loop of 1000-word `rep movsw`), same binary, `cycles=315` in both
worlds:

| | iterations/s |
|---|---|
| PakuPort before the fix | 23,946 |
| PakuPort after (`Cpu.ChargeStringElement`) | **308** |
| Oracle DOSBox-X | 218 |

From **110x divergence to ~1.4x**. On a test game that spends 58 % of its instructions in a blit
made of four `rep movsw`, the bug meant repainting the map 159 times/s where the original does
~45, and **no `cyclesPerMs` value could fix it**, because it scaled compute and blit by
different factors.

**Residual gap (+41 %), reported as is.** Two known causes, neither closed:

1. At `cycles=315` DOSBox delivers **285,468 instr/s, 90.6 % of nominal** (measured with
   `oracle315.conf`); at `cycles=3000` it delivers 99 %. PakuPort does not reproduce that
   low-speed deficit.
2. When DOSBox runs out of budget in the middle of a REP it leaves, does `LOADIP` and
   **re-executes the instruction**, paying the fetch again (3-4 times per iteration here).

Together they do not explain all 41 % (~29 % unexplained). That is why the judge watches the
**baseline** (regression, 5 % tolerance) as well as the gap against the oracle (50 %).

## How to run (from the repo root)

```powershell
python tools\speed_probe\gen_speed.py          # optional: the .COM is checked in
python tools\speed_probe\gen_reptest.py

# Oracle (stock DOSBox-X, minimized). Writes SPEED.BIN / REPTEST.BIN into tools\speed_probe.
dosbox-x -conf tools\speed_probe\oracle.conf      # cycles=3000
dosbox-x -conf tools\speed_probe\oracle315.conf   # cycles=315
dosbox-x -conf tools\speed_probe\reptest.conf     # REPTEST at cycles=315

# PakuPort
dotnet run -c Release --project src\PakuPort.Cli -- port tools\speed_probe\SPEED.COM --no-package --no-smoke
cd export\speed
$env:PAKU_INTERP_ONLY="1"; $env:PAKU_NOSPLASH="1"; $env:PAKU_AUTOEXIT_MS="12000"; $env:PAKU_CYCLES="3000"
dotnet run -c Release --no-build
```

The PakuPort result lands in `SPEED.BIN` of the **working directory** (`export\speed`); copy it
to `tools\speed_probe\SPEED.PAKU.BIN`. Same for REPTEST with `PAKU_CYCLES=315`.

```
python tools\speed_probe\judge_speed.py     # default threshold: 3 %
```

The judge compares `*.PAKU.BIN` against `*.ORACULO.BIN` (frozen ground truth: do not regenerate
unless the probe changes) and exits 1 on failure. It watches that `cyclesPerMs=N` still equals
`cycles=N`, and that **the oracle still charges 1 cycle per instruction** (A/B coherence): if
someone changes `core` or `cputype` in the conf, the judge says so instead of printing a
misleading number. Current state: **GREEN, max gap 0.98 %**.

## Gotcha

The probe relies on the BIOS tick at `0040:006C` advancing: if it were frozen, the probe would
never finish instead of giving a wrong number. Getting a result at all proves the clock runs.
