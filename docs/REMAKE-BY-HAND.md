# Remaking a game by hand

Everything PakuPort produces is meant to be read by a person first. Nothing in the workflow
needs a language model: the board, the judge, the oracle and the A/B control are deterministic
tools, and they are at least as useful to you as to a machine — arguably more, because you can
also *ask the original* the questions a model would not think of.

This page is the human version of [REMAKE-WITH-AI.md](REMAKE-WITH-AI.md). Do the
[tutorial](TUTORIAL.md) first.

## A session, function by function

1. **Pick a function.** `games/<id>/capas.json` and the board say which functions are pure
   logic and how much of each ran. Leaves (functions that call nothing) with 100 % coverage are
   the cheapest wins; `pakuport banco juzga` shows what is already done.
2. **Read the header** of `PLANTILLA.cs` before the code. The calling convention, the globals
   with the values seen in real play and the list of callees tell you most of what the function
   is for. The values seen are often the fastest clue: a word that goes `2F1D → F4BA → ABDB` on
   every call is state that is being mutated, not a constant.
3. **Read `desensamblado.asm`.** It is the body as the game ran it, with addresses.
4. **Write `respuesta.cs`** following `COMO-ESCRIBIR.md`. Translate instruction by instruction
   first; make it pretty later, once it is green. Use the `Cpu` operations (`Add16`, `Cmp16`,
   `Mul16`, …) so the flags come out exactly as the 8086 leaves them.
5. **Judge:** `pakuport banco juzga --banco games/<id>/expediente --exige-ritmo`. Red tells you
   which case and which byte; `--exige-ritmo` makes a wrong instruction count red too.
6. **Break it on purpose** once: change one constant, see red, put it back. It takes a minute
   and it is the only proof that the cases actually exercise what you wrote.

## Asking the original

When the recorded cases do not cover a question — what happens with a negative value, with an
empty list, at a boundary — ask the binary:

```json
{
  "registros": { "ax": "0x0005" },
  "memoria":   [ { "en": "ds:0x044C", "bytes": "FFFF" } ]
}
```

```powershell
pakuport oracle-run --game games\<id>\game.json --func FUN_1000_189 --base 0 --entrada q.json --pfc extra.pfc
```

`--base N` starts from the state of the N-th recorded case (so the data segment is a real game
state, not zeros); your file overrides registers and memory. The answer lists the registers at
exit and every byte written, with before and after. With `--pfc`, the answer is saved as one
more case that the judge can consume — you have just written a test with the original as the
oracle. (`oracle-run` needs `"corpusDir"` in `game.json` to find the recorded states.)

## Reading what the game does

Before translating anything, it often pays to understand the game as a whole:

- **The dossier** — `python tools/dossier/build_dossier.py <id> ...` (run for you by
  `pakuport remake study`) writes `dossier.md`: identity, how it draws, its assets with PNGs,
  sounds, files it opens, platform features it uses, the code that runs.
- **Scenes** — `tools/expediente_escenas.py` attributes functions to the scenes of the game
  (title, menu, play…) from program-counter samples (`PAKU_SAMPLE_PC_MS`).
- **DOS and BIOS traces** — `PAKU_LOG_INT21=1` logs every DOS call with its arguments and the
  instruction count; `PAKU_INT16_TRACE=1` does the same for the keyboard BIOS.
- **Who wrote this pixel?** — `PAKU_PROC_PIXEL=1` tags every byte of video memory with the code
  that wrote it.

The full list of switches is in [PALANCAS.md](PALANCAS.md), generated from the source.

## Art by hand

The HD kit does not care who draws. `pakuport remake study <id>` writes the brief
(`games/<id>/remake/pendiente.json`) and the reference images (`ref/`); `pakuport remake hoja <id>`
puts the whole job on one HTML page you can look at. Draw each asset at its size or an exact
multiple, save it where the brief says, run `pakuport remake check <id>`. To see one image in
the game without the whole cycle, `pakuport remake try <id> <key> <image.png>` puts it in and
shows before and after. See [HD-ART.md](HD-ART.md).

## When the port itself is wrong

Sometimes the problem is not your translation: the port does not behave like the original. The
house rule is to fix PakuPort, not the game: find the subsystem, write a probe that shows the
difference against DOSBox-X ([PROBES.md](PROBES.md)), fix `src/`, and keep the probe as a
regression test. [CONTRIBUTING.md](../CONTRIBUTING.md) explains the workflow.
