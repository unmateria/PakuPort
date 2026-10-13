# Instructions for AI coding agents

You are working in **PakuPort**, a tool that runs MS-DOS games as .NET programs (an x86
interpreter on an emulated PC) and helps rebuild them function by function under a judge.
Read [README.md](README.md) and [docs/HOW-IT-WORKS.md](docs/HOW-IT-WORKS.md) first.

## If your task is to translate functions of a game

Follow [docs/REMAKE-WITH-AI.md](docs/REMAKE-WITH-AI.md). In short:

- Work in **one** folder of the board, `games/<id>/expediente/<FUNC>/`. Read `../COMO-ESCRIBIR.md`
  (the contract), `PLANTILLA.cs` (measured facts in its header) and `desensamblado.asm`.
- Write **only** `respuesta.cs` in that folder.
- Judge with `pakuport banco juzga --banco games/<id>/expediente --exige-ritmo`. The judge is the
  only authority on whether a translation is correct. Feed its red message back into your fix.
- **Never** modify the corpus, `banco.json`, `game.json`, other folders' answers, `src/` or the
  tools in order to make a judgement pass.
- Do not invent names or purposes for functions. "SIN NOMBRE" (no name) is a valid answer.
- Code that the header says never ran has no test: stay as close to the original as possible
  there and say so.
- A *Render* function can be green and still draw wrong (the judge does not see video memory):
  check those with `tools/control_portadas.py`, which compares the screen.

## If your task is to change PakuPort itself

Follow [CONTRIBUTING.md](CONTRIBUTING.md). The non-negotiables:

- Fix `src/`, never a generated `export/<id>/`.
- Implement platform behaviour by specification (hardware manuals, DOSBox-X as reference
  implementation, cited by file and line), never as a special case for one game.
- Every new check ships with a negative control that makes it fail.
- A comparison with nothing to compare is a failure, not a pass.
- Every run you launch for measuring is headless and silent: `PAKU_HEADLESS=1`, `PAKU_NOAUDIO=1`,
  `PAKU_NOSPLASH=1`. Never open game windows or steal focus.
- A new file in `src/PakuPort.Core/` must be added to `src/PakuPort.Cli/ExportCore.cs`.
- Before finishing: `dotnet build PakuPort.slnx -c Release` and `python tools/selftest.py`
  (must end `SELFTEST: GREEN`).
- Never add game files, game memory dumps, recordings of real games or bytes from commercial
  binaries to the repository.

## Running commands

`pakuport <command>` means `dotnet run -c Release --project src/PakuPort.Cli -- <command>`, run
from the repository root. Command-line messages are partly in Spanish; the documentation in
`docs/` is in English.
