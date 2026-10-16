# Limitations

What does not work, or works with conditions. Every item was measured, not assumed.

## General

- **Windows only, as far as anyone knows.** Developed and tested on Windows 10/11 x64. The core
  is plain .NET and the host uses Raylib, so other systems may work, but nobody has tried, and
  the tools use Windows paths in places.
- **Spanish in places.** Parts of the source comments, the command-line messages and the files
  generated for the remake board (`COMO-ESCRIBIR.md`, the headers of `PLANTILLA.cs`) are in
  Spanish. The documentation is in English; a Spanish README is in [README.es.md](../README.es.md).
- **Not every game runs.** PakuPort was developed against about two dozen commercial games and
  most play end to end; some stop at copy protections that need the original floppy, some at
  extenders that are not implemented (**Phar Lap** is not supported), some at hardware corners
  nobody has needed yet. When a game fails, [PROBES.md](PROBES.md) and the `PAKU_*` logs are how
  you find out why.
- **Speed of 32-bit 3D games.** The interpreter is fast enough for most DOS games, but a
  demanding 32-bit 3D engine can produce fewer frames per second than it did on the hardware it
  was written for.

## Porting

- `port` copies **the whole folder** of the executable as the game's data. Point it at a folder
  that contains only the game.
- `game.json` is **baked into the export**. After editing it, run `pakuport export` again (or
  override at run time with the `PAKU_*` variables, e.g. `PAKU_CYCLES`).
- Repeatable runs need a fixed speed (`cyclesPerMs`), an input tape and a cut by instructions
  (`PAKU_AUTOEXIT_INSN`). A run cut by milliseconds is not repeatable.

## The remake board and the judge

- **The corpus is recorded by the 16-bit interpreter only.** Games that run in the 32-bit flat
  world (DOS/4GW and similar) can be ported and played, but get no corpus, no board and no
  judge yet.
- **The judge does not see video memory.** It compares registers, flags, the data segment and
  the instruction count. A drawing routine translated wrong can come out green; every *Render*
  function carries this warning in its header, and the A/B control (`tools/control_portadas.py`,
  which compares the screen) is the real check for those.
- **Coverage is what was played.** The judge tests the paths the recording exercised. The board
  says how much of each function ran; the rest has no test.
- **Loop iterations cannot be plugged in.** A *vuelta de bucle* unit is judged, but
  `export --portadas` only hooks function entries.
- **The legacy lifted-code commands** (`verify`, `emit`, `decode`, …) read MZ executables only;
  on anything else they refuse. They belong to the pipeline before the interpreter; use the
  board and `banco juzga`.
- **Per-frame recording for readable remakes** (`PAKU_FOTOGRAMAS`) captures the CGA screen only.

## HD art

- **Moving sprites.** The runtime punches holes in an HD texture wherever the game draws over it
  afterwards, measured against how the area looked when the texture first appeared. Sprites that
  move by erasing and redrawing every frame never match, so their HD versions appear pierced,
  with original pixels through the holes. Visible in the bundled sample. Static screens,
  backgrounds and UI elements are not affected.
- **Coverage of grouped figures.** A figure delivered under a `grupo:` key replaces rows that
  keep their own blit keys, so the coverage *by blits* can read 0 % while the figure is
  replaced. `remake judge` reports the coverage by logical drawings separately; read that one.
- **Exports made before `pakuport-host.json`.** `remake capture` refuses an export without that
  contract and prints the command that regenerates it. Regenerate once.
- **What the blit telemetry sees.** Block copies (`movs`/`stos`) only; pixels written one `mov`
  at a time have no identity there (use `PAKU_PROC_PIXEL`). Games that compose in their own RAM
  pages and never write through the video window produce an empty capture; `capture` detects it
  and stops.
- **Composed screenshots are exact in the game, not in the HD layer.** `remake try` and
  `PAKU_CORTE_FOTO` freeze the guest at the same instruction in every run, but the hole-punching
  of the HD layer depends on how many frames the host presented, so a few pixels at the edges
  of a replaced figure can differ between two runs.

## Legal

PakuPort ships no games. A port contains the original game's code and data: it is for your own
use with a copy you own, not for redistribution.
