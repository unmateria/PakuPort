# PakuPort

**Run MS-DOS games as .NET programs, and rebuild them function by function with a judge that
says yes or no.**

PakuPort takes a DOS executable (`.EXE` or `.COM`, real mode or 32-bit protected mode) and turns
it into a .NET project that runs the game: an x86 interpreter executes the original bytes on top
of an emulated PC — DOS, BIOS, VGA/EGA/CGA, keyboard, mouse, timer, PC speaker, Sound Blaster and
OPL2. That is the **port**.

Then it helps you replace the original, piece by piece, with code and art of your own. That is
the **remake**. For every function of the game it writes a folder with the original disassembly
and everything it *measured* while watching the game run: calling convention, globals touched,
what layer it belongs to (drawing, sound, input or logic), how much of it actually ran. You write
the function in C#; a judge replays real recorded calls and compares registers, flags and memory
byte by byte. Green or red, no eyeballing. Your translation can then be plugged into the running
game while everything you have not translated yet keeps running from the original binary.

It works with or without an AI. The folders are written so that a person can read them, and so
that a language model can work on them blind and still be checked by the judge instead of
trusted.

![The bundled sample game, ported](docs/images/catch-ported.png)

*CATCH, the tiny original DOS game bundled in [`samples/catch`](samples/catch), running as a
.NET program after `pakuport port`.*

## What is in the box

| | |
|---|---|
| **Port** | `pakuport port GAME.EXE` → a .NET project in `export/<id>/` that plays the game, and optionally a double-click `.exe` |
| **Platform** | DOS (files, memory, EXEC, TSRs), BIOS, VGA/EGA/CGA/text modes, Mode X, VESA, PIT/PIC/DMA, keyboard, mouse, joystick, PC speaker, Sound Blaster (incl. 16-bit DMA), OPL2, MPU-401; DPMI/VCPI hosts for DOS/4GW, DOS/16M and embedded extenders, paging |
| **Measuring tools** | dozens of `PAKU_*` switches that log who draws what, who plays which sound, which code ran, every DOS call, every input; a deterministic input tape |
| **The remake board** | `pakuport expediente tablero`: one folder per function with its measured facts, its disassembly and a C# skeleton |
| **The judge** | `pakuport banco juzga`: replays recorded calls against your C#; checks registers, flags, memory and even the instruction count ("rhythm") |
| **Ask the original** | `pakuport oracle-run`: run one original function with any input you choose |
| **Plug it in** | `pakuport export --portadas`: your translations run inside the game; an A/B control proves nothing changed |
| **HD art kit** | `pakuport remake`: captures every sprite with its identity, writes the art brief, validates what you deliver and swaps it in at runtime |
| **Platform probes** | small synthetic DOS programs in `tools/` that compare PakuPort against DOSBox-X, one subsystem at a time |

## Quick start

You need Windows, the [.NET 10 SDK](https://dotnet.microsoft.com/download) and Python 3.10+.

```powershell
git clone <this repository> pakuport
cd pakuport
dotnet build PakuPort.slnx -c Release
python tools\selftest.py
```

`selftest.py` runs the whole chain on the bundled sample and must end with `SELFTEST: GREEN`.
Then port the sample yourself:

```powershell
dotnet run -c Release --project src\PakuPort.Cli -- port samples\catch\dos\CATCH.COM --no-package
cd export\catch
dotnet run -c Release
```

The [tutorial](docs/TUTORIAL.md) takes it from there, step by step, all the way to translated
functions running inside the game.

## Documentation

| Read this | to |
|---|---|
| [docs/GETTING-STARTED.md](docs/GETTING-STARTED.md) | install, build, verify, port your first program, the keys of a ported game |
| [docs/TUTORIAL.md](docs/TUTORIAL.md) | the full walkthrough on the sample game: port → measure → board → translate → judge → plug in → HD art |
| [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) | the architecture, and *why* it is built this way |
| [docs/REMAKE-WITH-AI.md](docs/REMAKE-WITH-AI.md) | how to hand the board to a language model safely |
| [docs/REMAKE-BY-HAND.md](docs/REMAKE-BY-HAND.md) | the same work without any AI |
| [docs/HD-ART.md](docs/HD-ART.md) | the art kit: capture, brief, deliver, validate, judge |
| [docs/CLI.md](docs/CLI.md) | every command |
| [docs/PROBES.md](docs/PROBES.md) | the platform probes and the DOSBox-X oracle |
| [docs/LIMITATIONS.md](docs/LIMITATIONS.md) | what does not work, said plainly |
| [CONTRIBUTING.md](CONTRIBUTING.md) | rules of the house |
| [README.es.md](README.es.md) | este README, en español |

## Status

PakuPort was developed against about two dozen commercial DOS games, from 1980s real-mode
`.COM` programs to 32-bit DOS/4GW titles. Most of them play end to end; the ones that don't are
listed with their diagnosis in the author's notes, not here, because **this repository ships no
games and names none**. Bring your own, legally obtained copies.

It is a research-grade tool: powerful, measured, and opinionated, but not polished. Parts of the
source comments and of the command-line output are in Spanish. Read
[docs/LIMITATIONS.md](docs/LIMITATIONS.md) before you start.

## License

GPL-3.0-or-later. Parts of the platform layer are derived from DOSBox-X (GPL-2.0-or-later) and
are marked in the code with the file and line they come from; the xBRZ scaler is GPL-3.0. See
[NOTICE.md](NOTICE.md) and [LICENSES/THIRD-PARTY.txt](LICENSES/THIRD-PARTY.txt).

PakuPort does not include, and must not be used to distribute, copyrighted games. The ports it
produces contain the original game's code and data and are for your own use with copies you own.
