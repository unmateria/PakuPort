# Platform probes and the DOSBox-X oracle

PakuPort runs MS-DOS programs on an emulated PC (DOS, BIOS, VGA, timer, keyboard, mouse, sound)
inside .NET. A **probe** is a tiny synthetic DOS program, written by a generator script, that
exercises **one** subsystem and leaves its result somewhere a script can read (a file, a trace,
a screen capture, a WAV). The same binary runs in PakuPort and, where it makes sense, in a stock
**DOSBox-X** (the *oracle*). A **judge** script compares the two worlds objectively, byte-exact
or numerically. Nothing is judged by eye.

All probes live under `tools\`, one folder each, with a `README.md` that holds the exact
commands and the measured result. This page is the map; the folder README is the authority.

## 1. Why probes exist

The method has three rules:

1. **Implement the platform by specification, not by symptom.** When a program misbehaves, the
   fix is to make the subsystem behave like the reference in general (DOSBox-X source, cited with
   file and line in the code), not to patch the case that showed up. A probe states the
   specification as a test that needs no game.
2. **Measure against an independent reference.** The expected value comes from the oracle run
   of the same binary, from a formula in the reference source, or from an independent CPU
   implementation (Unicorn). Never from the code under test and never from memory.
3. **No judge goes GREEN without having been seen RED.** Every net carries its negative
   control: it was run against the code *before* the fix (or against a deliberate mutation) and
   it failed. Examples from the READMEs: `dosfs_test` gave 19 of 22 RED on the old code;
   `textmode_test` gave 6 of 6 RED; `GHOST.COM` was born RED (12 live instances, 11 ghosts) on
   purpose; the 32-bit opcode vectors started at 38/1677. A judge that has never failed has not
   shown it can see the bug.

Two further habits follow from these rules:

- **Several judges check the oracle as well as PakuPort.** `judge_speed.py` checks that the
  oracle still charges one cycle per instruction, so a changed `.conf` is reported instead of
  producing a misleading number. `judge_crtc.py` fails if the oracle file's md5 changes.
- **Gaps that stay open are frozen, not hidden.** `dac_sweep` and `crtc_sweep` keep a
  `baseline.json`: a known-open divergence may not get worse, and the README says why it is
  still open.

## 2. The oracle: stock DOSBox-X

**What to install**

- **.NET 8 SDK** (PakuPort and the C# harnesses).
- **Python 3.** Most generators and judges use only the standard library.
- **DOSBox-X**, unmodified, reachable as `dosbox-x` on `PATH`. The coroutine gate also reads
  `PAKU_DOSBOX` (full path to `dosbox-x.exe`). DOSBox-X is only needed to **re-capture** ground
  truth or to run the probes whose oracle result is not checked in (see the table).
- **Python packages, only for the scripts that import them:**

| package | needed by | required? |
|---|---|---|
| `capstone` | every `gen_*.py` that hand-assembles a `.COM` | **optional**: inside `try`, only prints the disassembly to check the bytes |
| `capstone` | `le_loader\desensamblar.py`, `gen_opcode_vectors32.py` | required |
| `Pillow` | `dac_expand\judge_dacx.py`, `crtc_sweep\judge_sa13.py`, `blit_catalog\judge_ega.py`, `blit_test\judge_remake.py` | required |
| `numpy` | `opl_synth_test\wav_analyze.py` | required |
| `unicorn` | `gen_opcode_vectors.py`, `gen_opcode_vectors32.py` | required |
| `keystone-engine` | `gen_opcode_vectors32.py` | required |

**How the `.conf` files work.** Each oracle `.conf` pins the machine so the comparison means
something, and mounts the probe folder with a path **relative to the repository root**:

```ini
[cpu]
core=normal
cputype=386
cycles=3000
[dosbox]
machine=svga_s3
[autoexec]
mount c "tools\dac_expand"
c:
DACX.COM
```

So DOSBox-X must be started **from the repository root**. The probe writes its result file
next to itself in the mounted folder (e.g. `tools\dac_sweep\DAC.BIN`); most `.conf` files end
with `exit`. Probes whose result is what is *shown* (`DACX.COM`, `SA13.COM`) do not exit and need
a screen capture. `speed_probe` has extra `.conf` files at `cycles=315`; its README says changing
`core`, `cputype` or `cycles` invalidates the comparison. The coroutine suite runs at
`cycles=20000`.

**Running one by hand** (from the repository root, minimised so it does not take the focus):

```powershell
dosbox-x -conf tools\dac_sweep\oracle.conf
Move-Item tools\dac_sweep\DAC.BIN tools\dac_sweep\DAC.ORACULO.BIN -Force
```

`tools\coroutine_test\run_gate.py` shows how to launch it minimised from a script
(`Start-Process ... -WindowStyle Minimized`, working directory = repository root).

**Naming.** `*.ORACULO.*` is ground truth captured in DOSBox-X and checked in; `*.PAKU.*` is the
matching PakuPort capture. Do not regenerate an `ORACULO` file unless the probe itself changes,
and if it does, re-freeze the judge baseline too.

**Running the PakuPort side.** For `.COM` probes the pattern is always the same: `port` the
binary (it creates `export\<id>`), run the export headless with an automatic exit, read the
result from the export folder:

```powershell
dotnet run -c Release --project src\PakuPort.Cli -- port tools\<probe>\<NAME>.COM --no-package --no-smoke
cd export\<id>
$env:PAKU_NOSPLASH='1'; $env:PAKU_AUTOEXIT_MS='8000'
dotnet run -c Release --no-build
```

Common switches seen in the READMEs: `PAKU_INTERP_ONLY=1` (interpreter only),
`PAKU_CYCLES=<n>`, `PAKU_AUTOKEYS=<script>` (scripted key presses), `PAKU_FB_PNG=<file>`
(framebuffer dump), `PAKU_LOG_PRESENT=1`, `PAKU_WAV_DUMP`. The full list is `docs\PALANCAS.md`.
Some READMEs note that `port` does not refresh the `.COM` already in the export; they copy it by
hand.

## 3. The probes

"Oracle" = whether a DOSBox-X run is part of the ground truth. "checked in" means the oracle file
is in the repo, so DOSBox-X is only needed to re-capture it.

| folder | subsystem | what it measures | how it is judged | oracle? |
|---|---|---|---|---|
| `bda_video` | video BIOS / BDA | video fields of the BIOS Data Area after `INT 10h AH=00` for 9 mode steps (`BDAV.BIN`, 34 B per mode) | `judge_bdav.py`: field by field against `BDAV.ORACULO.BIN`, cursor positions ignored | yes, checked in |
| `blit_test` | blit telemetry / HD replacement | provenance of blits (`BLIT*`, `CHAIN`, `COMPOSE`), EGA planar decoding (`EGA.COM`), liveness of a moving sprite (`GHOST.COM`) | `judge_chain.py`, `judge_compose.py`, `judge_ghost.py`, `judge_remake*.py`, `tools\blit_catalog\judge_ega.py` (expected pattern computed from the COM's own data tables) | no |
| `coroutine_test` | CPU control flow (far return, co-routines, `#DE`, ISRs) | 9 suites (`CORO`, `DEEP`, `PSTK`, `NCORA/B/C`, `DE`, `ISTK`, `IIDM`) under forced lifted/interpreted partitionings | `run_gate.py`: counter in `RES.BIN` (e.g. 10000); `FAIL.BIN` = poison reached | yes, to prove the program correct (not for `DE`) |
| `crtc_sweep` | VGA CRTC / Attribute Controller | CRTC registers after each set-mode (15 modes), the r11 write-protect, CGA palette on EGA/VGA, start address in mode 13h | `judge_crtc.py`, `judge_lock.py`, `judge_cgapal.py` byte-exact + baseline; `judge_sa13.py` image comparison | yes, checked in |
| `dac_expand` | VGA DAC to screen colour | 6-bit to 8-bit expansion over a 64-step grey ramp | `judge_dacx.py`: `dacexpand()` formula **and** the oracle PNG | yes, checked in |
| `dac_sweep` | VGA DAC default palette | 256 x RGB after set-mode in modes 03h, 0Dh, 12h, 13h (`DAC.BIN`, 3072 B) | `judge_dac.py`: byte-exact for 0Dh/13h, baseline for 03h/12h | yes, checked in |
| `dosfs_test` | DOS file system | 22 cases: `findfirst`, 8.3 names, cwd, extra drives, through `INT 21h` | C# harness, GREEN/RED per case, exit code | no (rules ported from DOSBox-X source) |
| `governor_test` | speed governor | a live speed change must not block; the governor must still throttle | C# harness on the real code path, 2 cases | no |
| `kbd_int15` | BIOS keyboard (`INT 15h AH=4Fh`) | the BIOS offers each scancode to the hook and obeys pass / drop / translate | `judge_kbd15.py` on `KBD15.BIN` (BDA buffer read directly) | no |
| `kbd_mods` | keyboard modifiers + BIOS table | Shift/Ctrl/Alt scancodes, `40:17` flags, BIOS buffer entries | `judge_mods.py` on `MODS.BIN` | no |
| `mouse_center_test` | mouse driver | range and cursor position after reset in mode 13h | `judge_mcen.py`: against `MCEN.ORACULO.BIN` **and** the `(max+1)/2` rule | yes, checked in |
| `mouse_detect` | IVT / mouse detection | first byte at the `INT 33h` vector must not be `CF` (bare IRET) | `judge_mdet.py` on `MDET.BIN`; the reset result is the control | no |
| `mouse_raw_test` | mouse mickeys | mickeys keep counting while the cursor is clipped at the edge | C# harness, 3 cases (one non-regression control) | no |
| `opl_synth_test` | OPL2 FM synth | one FM note must produce non-zero PCM; `wav_analyze.py` reports RMS and dominant frequency of a WAV | prints `SYNTH OK` / `SILENT` (no README) | no |
| `platform_sweep` | BIOS/DOS interrupts and I/O ports | 129 cases: set all registers, `INT nn` or `in`/`out`, dump registers and flags | `judge.py`: oracle `RESULT.BIN` against `result_paku.json` (from `pakuport platform-test`) per `tests.json` | yes, **must be captured** (`TEST.COM` and `RESULT.BIN` are not checked in) |
| `present_rate_test` | frame presentation | ~70 captures/s, no frame at twice the period, regular interval | `judge_present.py` reads the `PAKU_LOG_PRESENT` trace | no |
| `speaker_test` | PC speaker | PIT channel 2 tone (`SPK.COM`) and direct-mode bit banging (`SPK2.COM`) | `run_gate.py`: dominant frequency of the WAV dump, 1000.15 Hz within 2 % | no |
| `speed_probe` | CPU speed unit | `cyclesPerMs=N` equals DOSBox-X `cycles=N`; REP charged per element (`REPTEST.COM`) | `judge_speed.py`: against `*.ORACULO.BIN`, 3 % threshold, plus oracle coherence | yes, checked in |
| `textmode_test` | VGA text mode | font in plane 2, 8x16 / 8x8 geometry, user fonts by BIOS and by ports, boot state | C# harness, 6 cases, exit code | no |
| `vmint_test` | CPU: threaded VM through a software INT | inline operands after `int E7` consumed as data; checksum `0x056F` in `RESULT.BIN` | `run_gate.py` over two partitionings (no README) | no |
| `vsync_test` | frame capture phase | captures per second and their phase inside the refresh cycle | a meter, not a gate: it reports, it does not judge (no README) | no |
| `le_loader` | LE executable format | not a probe: a Python **reference** loader (`le_ref.py`) whose fixup table must be consumed byte by byte, plus a disassembler in load coordinates | its image is compared against a memory dump of the same program loaded in the oracle | yes (dump) |

## 4. Worked example: `dac_expand`

**The question.** The VGA DAC stores 6 bits per colour component. How are they widened to the
8 bits of the screen? PakuPort did `v << 2` (maximum 252). DOSBox-X `src/hardware/vga_dac.cpp`,
`dacexpand()`, does `(v << 2) | (v >> 4)` (maximum 255). The DAC sweep cannot see this, because it
reads the 6-bit values back through the ports, which match in both worlds; only the framebuffer
shows it.

**The probe.** `gen_dacx.py` writes `DACX.COM`, 56 hand-assembled bytes: set mode 13h, load a
64-step grey ramp into the DAC (index `i` gets `r=g=b=i`, every possible 6-bit value) and paint
64 vertical bands of 5 px. The judge samples the centre of each band. Ground truth is double,
and both must hold: the `dacexpand()` formula, and the oracle capture `DACX.ORACULO.png` (in case
the source did not match what the machine does).

**Run it** (from the repository root; commands from `tools\dac_expand\README.md`):

```powershell
python tools\dac_expand\gen_dacx.py
dotnet run -c Release --project src\PakuPort.Cli -- port tools\dac_expand\DACX.COM --no-package --no-smoke
cd export\dacx
$env:PAKU_NOSPLASH='1'; $env:PAKU_AUTOEXIT_MS='8000'; $env:PAKU_FB_PNG='..\..\dacx.png'
dotnet run -c Release --no-build
cd ..\..
python tools\dac_expand\judge_dacx.py dacx.png tools\dac_expand\DACX.ORACULO.png
```

To re-capture the oracle: `dosbox-x -conf tools\dac_expand\oracle.conf` and take a screen
capture once the bands are shown.

**Seen RED, then GREEN.** Before the fix the judge printed `RESUMEN VERDE=1 ROJO=2`: 48 of 64
bands wrong, white at 252 (band 42: 168 where the oracle shows 170). After: `VERDE=4 ROJO=0`,
all 64 bands identical to the oracle. `DACX.ANTES.png` keeps the RED capture as evidence.

## 5. Adding a new probe

1. **One subsystem, one question.** Write down the specification first (reference source file
   and line, or the hardware document) and what a wrong implementation would produce.
2. **A generator script** `tools\<probe>\gen_<name>.py` that writes the `.COM` from bytes
   assembled by hand in the script, with the listing as comments. If `capstone` is installed,
   disassemble the result and print it, so the bytes are checked against the listing (keep the
   import inside `try`, as the existing generators do). Prefer a result **file** written through
   `INT 21h`, with stores through a `CS:` prefix so a handler that changes `DS` cannot corrupt
   them, and a zero-filled buffer inside the COM. Use `sti` at start if the probe needs IRQs: in
   the port a `.COM` starts with `IF=0`.
3. **The same binary in both worlds.** An `oracle.conf` with the pinned machine (`core=normal`,
   `cputype=386`, `cycles=3000`, `machine=svga_s3`) and a mount relative to the repository root.
   Capture the oracle result once, rename it `*.ORACULO.*` and check it in.
4. **A judge** `judge_<name>.py` that compares objectively and exits 1 on RED: byte equality,
   numeric thresholds, or semantic assertions per world. Read expected values from the probe or
   the oracle file, not from constants copied into the judge. If an open gap must be tolerated,
   freeze it in a `baseline.json` that may only improve.
5. **The negative control.** Run the judge against the code before the fix, or a deliberate
   mutation, and record that it went RED. Add a control case that must stay GREEN either way
   (e.g. the mouse reset in `mouse_detect`), so a RED cannot come from a broken harness.
6. **A README** with: why it exists, what the binary measures (size, layout of the result file),
   the exact commands from the repository root, and the measured result before and after.

For logic that needs no DOS program (file system, governor, mickeys, text font), a small C#
console project under `tools\` that calls the real PakuPort code and prints GREEN/RED per case
is the equivalent (`dosfs_test`, `governor_test`, `mouse_raw_test`, `textmode_test`).

## 6. CPU test vectors (Unicorn as the oracle)

Opcode semantics are measured against **Unicorn**, an x86 implementation independent from
PakuPort's. The expected values are produced by running the real instruction bytes in Unicorn;
nobody writes them by hand.

```powershell
python tools\gen_opcode_vectors.py      # 8/16-bit -> tests\PakuPort.Tests\vectors\opcodes.json
python tools\gen_opcode_vectors32.py    # 32-bit   -> tests\PakuPort.Tests\vectors\opcodes32.json
dotnet test tests\PakuPort.Tests --filter Interp32Tests
```

- **16-bit** (`gen_opcode_vectors.py`, consumed by `OpcodeFlagTests`, which measures the
  **lifter**): AX/DX results and the SDM-defined flags for 8/16-bit ALU, shift and similar
  opcodes over fixed operand sets. Run with `dotnet test tests\PakuPort.Tests --filter OpcodeFlagTests`.
- **32-bit** (`gen_opcode_vectors32.py`, consumed by `Interp32Tests`): 1,677 vectors in 80
  families. Each sets up the same bench in both worlds (code at `0000:2000`, a 64-byte scratch
  area, stack at `0000:1000`) and compares the eight 32-bit registers, the scratch and stack
  windows, and only the flags the Intel SDM defines for that opcode (each vector carries its own
  `check` list). Measured: 38/1677 before widening the core, 1677/1677 after.
- It runs in Unicorn's 16-bit mode on purpose: real mode plus the `0x66` prefix is where DOS
  programs run 32-bit code, so the vectors also cover the 16-bit decoding path.
- The snippets are assembled with keystone and **disassembled again with capstone**, and the
  text travels in the vector: keystone silently drops 32-bit addressing in 16-bit mode, which
  would have produced a GREEN vector testing a different instruction. Cases keystone cannot
  assemble are given as raw hex, checked with capstone.

`tools\OPCODES32.README.md` documents the harness decisions (iteration counting of `rep` in
Unicorn, masked `pushfd`) and what the suite does **not** cover (`iretd`, `lss`, protected-mode
instructions, 32-bit I/O).
