# How it works, and why it is built this way

## The short version

PakuPort does not decompile a game. It **runs** it — the original machine code, instruction by
instruction, on an emulated IBM PC written in C# — and wraps that in a .NET program. Everything
else is built on top of that one fact: because the game is running under PakuPort's control,
PakuPort can *watch* it, and what it writes down is not a guess about what the code might do but
a record of what it did.

```
 GAME.EXE ──► loader ──► interpreter ◄──► emulated PC ──► host window / sound / input
 (MZ, COM,     (EXEPACK,   (16-bit and     (DOS, BIOS,       (Raylib: the ported .exe)
  LE)           overlays)   32-bit x86)     VGA, timers,
                                            sound, mouse)
                               │
                               └──► measurements: corpus, layers, coverage, blits, sounds,
                                    input tape ──► remake board ──► judge ──► your C#
```

## Why an interpreter, and not a recompiler

PakuPort started as a static recompiler: decode the executable, lift every function to C#, emit a
program. It hit the wall every static approach hits on DOS software: **finding the code is
undecidable**. Jump tables, self-modifying code, overlays loaded at run time, code reached only
through function pointers or after a particular menu choice, copy protections that decrypt
themselves through a debugger trap — a disassembler sees bytes and has to guess which are code.
Every wrong guess is a crash in the generated program, discovered by a player.

The lifter and the code emitter still exist (the generated `GameCode.cs` is their output, and the
legacy `verify`/`emit` commands use them), but since the pivot **the interpreter is the engine**.
It executes whatever the CPU would execute, discovers nothing in advance and therefore misses
nothing. Lifted code is kept as a possible accelerator, not as the product.

Two attempts that also failed and shaped the design:

- **Recording the game in another emulator and building from its dumps** produced a working
  program tied to that emulator's runtime and to sessions somebody had to play by hand.
- **Asking language models to rewrite a game in clean C# from the disassembly** produced code
  that invented behaviour and silently dropped some. That is why PakuPort's remake path never
  trusts a model: it gives the model measured facts and checks every answer with a judge.

## The pieces

### Loaders — `src/PakuPort.Core/`

- **MZ** (`MzLoader`, `ExeImage`): DOS `.EXE` with relocations. **COM** images load at offset
  100h of their segment.
- **EXEPACK** (`ExePackDecompressor`): detected and unpacked in memory.
- **Overlays** (`Overlay`): Borland VROOMM overlay thunks (`INT 3Fh`) resolved statically.
- **LE** (`LeLoader`): the 32-bit Linear Executable produced by Watcom for DOS extenders. PakuPort
  does not run the original extender; it *replaces* it with its own DPMI/DOS host
  (`Platform/FlatServices`), which is what the game expected the extender to be.

### The CPU — `Cpu`, `Interp`, `Interp32`, `Fpu`

`Cpu` holds the registers and implements every arithmetic and logic operation with its flags,
following the Intel manuals; each family is tested against an independent reference (Unicorn)
through the vectors in `tests/PakuPort.Tests/vectors`. `Interp` executes 16-bit code — real mode,
286-style protected mode with descriptor caches, task switching, debugger traps. `Interp32`
executes the flat 32-bit world of DOS extenders, with paging. Programs that live in both worlds
(a 16-bit extender kernel loading 32-bit code) bounce between the two interpreters following the
D bit of the real code descriptor. `Fpu` is a software x87.

**The clock is the instruction counter.** The PIT, the BIOS tick, IRQ timing, the vertical
retrace, the Sound Blaster's DMA: everything derives from how many instructions the guest has
executed (`cyclesPerMs` converts that to time). That single decision is what makes runs
repeatable, and it is why a translated function has to *charge* the instructions the original
would have spent.

### The emulated PC — `src/PakuPort.Core/Platform/`

| | |
|---|---|
| DOS | `Int21hHandler`, `DosFileSystem`, `DiscosExtra`: files (8.3 short names, current directory per drive, extra drives), memory blocks (MCB chain), EXEC of child programs and drivers, TSRs, console |
| BIOS | `Int10hHandler` (all video services and ROM fonts), `Int16hHandler` (keyboard), `Int1AhHandler` (timer/RTC), `Int33hHandler` (mouse driver), `BiosInterrupts` (the rest), the BIOS data area |
| Video | `VideoState`, `VgaPortHandler`, `CgaPortHandler`: one unified VRAM of four 64 KB planes for CGA/EGA/VGA/text, the real register model (sequencer, graphics controller, CRTC, attribute controller, DAC), Mode X, split screen, VESA 2.0 banked modes |
| Machine | `Machine/`: PIC, PIT, DMA (both 8237s), 8042 keyboard controller, CMOS, game port, PC speaker, Sound Blaster (8 and 16-bit DMA), OPL2 synthesizer, MPU-401 (UART) |
| 32-bit host | `FlatServices`: DPMI (INT 31h), DOS called from 32-bit code, VCPI/XMS answers, the proprietary services some embedded extenders use |

Much of this is **ported, not reimagined**, from DOSBox-X, and the code says from where — file
and line — wherever a table or a behaviour was taken (see [NOTICE](../NOTICE.md)). The rule is
"implement the platform by specification, not by symptom": when a game misbehaves, the fix is to
make the subsystem behave like the reference in general, verified by a probe, not to patch the
case that showed up.

### The host — the generated `export/<id>/Program.cs`

`pakuport export` writes a .NET project: a copy of the core, the game's configuration baked into
a host program, and the game's files. The host opens a window (Raylib), turns key presses into
scancodes, mouse motion into mickeys, mixes the sound sources, presents frames at the VGA's
70 Hz rhythm, and offers the measuring switches. `pakuport package` publishes it as a
self-contained `Jugar<Game>.exe`.

## Determinism

A measurement you cannot repeat is an anecdote. Three things make a PakuPort run repeatable:

1. **The input tape** (`PAKU_TAPE_RECORD` / `PAKU_TAPE_REPLAY`): every key, mouse movement and
   button, stamped with the guest's instruction count, replayed by the game thread at the same
   instructions.
2. **No host clocks under a tape**: the retrace, the Sound Blaster's block IRQ and the tape
   itself run on guest time. (Each of these was found as a source of divergence and removed;
   `PAKU_RASTRO` logs where two runs first differ, which is how they were found.)
3. **Cutting by instructions** (`PAKU_AUTOEXIT_INSN`), not milliseconds, and dumping the guest's
   memory and the video planes at that point (`PAKU_MEMDUMP`).

With the three, two runs of the same game leave the same memory and the same screen **byte for
byte** — the self-test checks it on every run.

## Measuring

Off by default, each switched on by an environment variable, each costing nothing when off.
The full, generated list is [PALANCAS.md](PALANCAS.md); the main families:

| what | switch | gives you |
|---|---|---|
| calls of each function | `PAKU_CORPUS` | `.pfc` cases: registers and memory in/out, effects, the code run — the test bench |
| who does what | `PAKU_CAPAS` | VRAM writes, port I/O and interrupts per function → render / audio / input / logic |
| what code ran | `PAKU_COBERTURA` | every executed byte → the frontier of the unknown |
| who drew what | `PAKU_LOG_BLIT`, `PAKU_PROC_PIXEL` | every blit with its source identity; which code wrote each pixel |
| sounds | `PAKU_LOG_AUDIO_EV` | sound events with fingerprints |
| DOS and BIOS | `PAKU_LOG_INT21`, `PAKU_INT16_TRACE`, … | every call with its arguments |
| where execution is | `PAKU_SAMPLE_PC_MS`, `PAKU_RASTRO` | program-counter samples; a trace for diffing two runs |

## From measurements to a remake

### The corpus and the unit of work

`PAKU_CORPUS` learns the targets of `call` instructions and records real invocations of each
(eight by default). Each case stores the registers and data segment at entry and exit, every
interrupt, port read and IRQ in between with its result, the code window it ran, and the
interrupt vectors it depended on — enough to **replay the case without the rest of the game**.

A game written in assembly does much of its work in loops that never return (the main loop is
called once; the attract mode runs from the entry point). So a unit can also be **one iteration
of a loop**: a backward jump taken marks a loop head, and an iteration ends when execution comes
back to it — or leaves the loop — with the same stack.

### The board

`pakuport expediente tablero` writes one folder per unit with a C# skeleton whose header carries
everything measured: proposed name **with the evidence that supports it** (or none), layer,
scene, coverage, observed calling convention, globals read and written with values seen, the
callees with the exact `o.Llama(...)` to call them, what to do with the stack, the instruction
budget, and the commands to judge and to query the original. `COMO-ESCRIBIR.md` is the contract.

### The judge

`pakuport banco juzga` compiles each `respuesta.cs` with Roslyn and runs it on each recorded
case: same input registers and memory, same answers from interrupts and ports (replayed from
the recording), and compares registers, flags, the data segment and the instruction count. It
first runs **the original** through the same harness: whatever the original itself cannot
reproduce is `INJUZGABLE`, not a failure of the translation.

`Original` (`Verify/Original.cs`) is what lets you translate one function at a time: from your
C#, `o.Llama(off, ret)` runs the original callee on the same CPU and memory, `o.Interrupcion(n)`
an interrupt, `o.LeePuerto8(p)` a port — so nothing below the function has to be translated
first.

### Plugging in

`pakuport export --portadas <board>` compiles the translations into the game, each in its own
namespace, registered at the address of the function's entry: whenever execution arrives there,
by whatever path, the C# runs instead of the bytes. A per-function recursion guard falls back to
the original if a translation re-enters itself too deeply. `tools/control_portadas.py` proves the
result with an A/B on the deterministic harness.

## The rules behind all of it

These came from mistakes, each paid for, and they are why the tools say what they say:

1. **Measured, not guessed.** The board writes only what was observed, says how many cases it
   is based on, and says where there is no data. A name without evidence is not proposed.
2. **No judge is trusted until it has been seen red.** Every check ships with its negative
   control: a deliberate mutation that must fail.
3. **A green on nothing is a red.** A comparison with nothing to compare (no dump, no cases, a
   function that never ran) is reported as such, never as a pass.
4. **Fix the platform, not the game.** Generated exports are never edited; the fix goes into
   `src/`, verified against the reference, for every game at once.
5. **One run, many recorders.** Measurements that will be cross-referenced are taken in the
   same run; two runs of a game, however similar, are different evidence.
6. **No AI in the pipeline.** PakuPort itself is deterministic code. A model may *use* its
   output, and is then judged like anyone else.
