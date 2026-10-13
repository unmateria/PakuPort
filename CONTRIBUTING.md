# Contributing

Thank you for wanting to make PakuPort better. The project has a few rules that are not style
preferences: each one exists because breaking it once cost a lot.

## Before you send anything

```powershell
dotnet build PakuPort.slnx -c Release
python tools\selftest.py
```

`selftest.py` must end with `SELFTEST: GREEN`. If your change legitimately changes what the
sample game leaves in memory or on screen (a fidelity fix in the video BIOS, for instance),
explain why in your pull request and re-pin with `python tools\selftest.py --repin`.

If you touched the platform layer, also run the probe(s) for that subsystem
([docs/PROBES.md](docs/PROBES.md)) and include their result.

## The rules of the house

1. **Fix `src/`, never an export.** `export/<id>/` is generated. A bug in a ported game is a bug
   in PakuPort, and its fix must hold for every game.
2. **Implement by specification, not by symptom.** When a game misbehaves, find what the real
   hardware, BIOS or DOS does (the Intel manuals, the IBM technical references, DOSBox-X's
   source as the reference implementation), implement *that*, and show it with a probe against
   the oracle. A special case keyed on one game is not accepted.
3. **No judge without its negative control.** A check that has never been seen failing proves
   nothing. Every new judge, gate or test that compares something comes with a deliberate
   mutation that makes it red.
4. **A pass on nothing is a fail.** If a comparison has nothing to compare (no dump, no cases, a
   function that never ran), it must say so and fail, never pass.
5. **Measurements are off by default and free when off.** New `PAKU_*` switches cost at most a
   static bool check in hot paths, and are documented in the code (the index in
   `docs/PALANCAS.md` is generated from it: `python tools\gen_palancas.py`).
6. **Runs never steal focus.** Anything a tool launches for measuring is headless and silent
   (`PAKU_HEADLESS=1`, `PAKU_NOAUDIO=1`).
7. **Say where code comes from.** Code derived from DOSBox-X keeps a comment with the source
   file and line. New third-party code needs a license compatible with GPL-3.0 and an entry in
   `LICENSES/THIRD-PARTY.txt`.
8. **A new file in `src/PakuPort.Core/` must be added to `src/PakuPort.Cli/ExportCore.cs`**,
   or every export stops compiling with `CS0103` while `src/` builds fine.
9. **No games, ever.** Do not commit game files, dumps of game memory, recordings of real games,
   or code bytes taken from a commercial binary — not even in tests. Use the bundled sample or a
   synthetic probe.

## Code style

Write the least code that solves the real problem; no abstractions for a single use. Comments
explain *why*, especially when the reason was learned the hard way. New comments in English.

## License of contributions

By contributing you agree that your contribution is licensed under GPL-3.0-or-later, like the
rest of the project.
