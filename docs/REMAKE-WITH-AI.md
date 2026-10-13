# Remaking a game with an AI

PakuPort was designed so that a language model can do most of the translation work **without
being trusted**. The model never decides whether its answer is right; the judge does, against
real calls recorded from the original game. Your job is to set up the work so the model gets
facts, small tasks and fast, exact feedback.

> If you have not done the [tutorial](TUTORIAL.md), do it first. Everything here assumes a game
> that is ported, recorded and has a remake board (`games/<id>/expediente/`).

## What the model gets, and what it must not touch

Each folder of the board is one self-contained task:

| file | role | the model may |
|---|---|---|
| `PLANTILLA.cs` | skeleton + every measured fact in its header | read |
| `desensamblado.asm` | the original body | read |
| `../COMO-ESCRIBIR.md` | the contract: API, stack, flags, rhythm, calling the original | read |
| `respuesta.cs` | the translation | **write** |
| the corpus, `banco.json`, `game.json`, `src/` | the evidence and the judge | **never modify** |

Make that last row explicit in your instructions. A model under pressure to get a green will
happily "fix" the test instead of the code.

## The loop

```
for each folder (start with layer LOGIC, few callees, 100 % coverage):
    give the model:  PLANTILLA.cs + desensamblado.asm + COMO-ESCRIBIR.md
    model writes:    respuesta.cs
    run:             pakuport banco juzga --banco games/<id>/expediente --exige-ritmo
    if red:          give the model the judge's line for that function, verbatim
    stop after N attempts (3 is a good number) and move on — mark it for a human
```

The judge's red line is precise enough to act on, for example:

```
FUN_1000_189: rojo 0/8 (caso 0: memoria: 1 tramo(s), el primero @0x044C len 1: B9 -> BA) | ritmo EXACTO
```

("red 0/8 — case 0: memory: 1 range, the first at 0x044C, 1 byte: B9 where BA was expected").
Feed that back verbatim; do not paraphrase it.

**Order matters.** Start with leaves: logic functions that call nothing. Then functions whose
callees you can leave to the original with `o.Llama(...)` (the header gives the exact call for
each callee). Render and audio functions last: see the caveats below.

## A prompt that works

Adapt the paths. It is deliberately strict.

```text
You are translating ONE function of an MS-DOS game from x86 assembly to C#, for PakuPort.

Read, in this order:
  1. games/<id>/expediente/COMO-ESCRIBIR.md   — the contract (it is in Spanish; follow it exactly)
  2. games/<id>/expediente/<FUNC>/PLANTILLA.cs — the skeleton; its header lists facts MEASURED
     by running the game: calling convention, globals with observed values, callees and how to
     call them, stack handling, instruction budget
  3. games/<id>/expediente/<FUNC>/desensamblado.asm — the original code

Write games/<id>/expediente/<FUNC>/respuesta.cs and nothing else.

Rules:
- Reproduce the original's effect exactly: registers, FLAGS, memory and stack. Use the Cpu
  methods (Add16, Sub16, Cmp16, Mul16, ...) so flags are computed exactly like the 8086.
- Charge the instructions the original would have spent (o.Instrucciones / o.Repeticiones),
  following the rule in COMO-ESCRIBIR.md.
- For any call you do not translate, call the original: o.Llama(target, returnAddress) exactly
  as the header shows. Same for interrupts (o.Interrupcion) and ports.
- Do not invent names or purposes. If the header says "SIN NOMBRE", keep it unnamed.
- Never edit any other file. Never touch the corpus, banco.json, game.json or src/.
- When done, run:  pakuport banco juzga --banco games/<id>/expediente --exige-ritmo
  and report the line for <FUNC>. If it is red, fix respuesta.cs using the judge's message.
  At most 3 attempts; then stop and report what you tried.
```

## Running it with a coding agent

Any agent that can read files and run commands in the repository works. Two practical tips:

- The repository root has an [`AGENTS.md`](../AGENTS.md) with the house rules (never edit
  `export/`, never weaken a judge, run headless, …). Most coding agents read it automatically.
- Give each agent **one folder**. Several agents can work on different folders at the same time,
  since each writes only its own `respuesta.cs`. `banco juzga` judges every folder at once, so
  the overall progress is one number; run it from one place when the agents are done.

## After green: inside the game

A green in the judge means the function behaves like the original **on the recorded calls**. It
does not prove the game is unchanged with your function inside it. Do this regularly, with
every green translation in:

```powershell
pakuport export --game games\<id>\game.json --portadas games\<id>\expediente
cd export\<id>; dotnet build -c Release; cd ..\..
python tools\control_portadas.py --export export\<id> --cinta <tape> --insn <N>
```

It must say that every translation **ran** inside the game and that memory and screen are
byte-identical. Then mutate one translation on purpose and check that it turns red — if it does
not, the window `--insn` is wrong or the game never exercises that path, and the green above is
not evidence. ([TUTORIAL §10](TUTORIAL.md#10-plug-your-c-into-the-game) shows both sides.)

## Caveats every model must be told

These are measured failures, not hypotheticals:

1. **The judge does not see video memory.** It compares registers, flags and the data segment.
   A translation of a drawing routine that paints every pixel one position to the right can come
   out GREEN. Every function of layer *Render* carries this warning in its header. Use the A/B
   control above (it compares the screen) as the real check for those.
2. **Rhythm is not optional inside the game.** The game's clock is the instruction counter. A
   translation without rhythm that sits in a loop waiting for the timer waits forever. Judge with
   `--exige-ritmo`.
3. **`o.Llama` needs its return address.** The call writes it on the stack, the stack is memory
   and memory is compared. Use the exact call from the header.
4. **Green can still kill the game.** A translation that recurses into itself during play can
   overflow .NET's stack; PakuPort falls back to the original after 8 nested entries of the same
   translation, but it is a sign of a wrong translation, not a fix.
5. **The recording covers what was played.** The header says how much of the function's code
   ran; code that never ran has no test. A model that "completes" it is guessing — make it leave
   such paths as close to the original as possible and say so.
6. **Loop iterations are not functions.** A folder whose header says *VUELTA DE BUCLE* returns
   the address where execution continues, has no `ret` and must leave `SP` as it found it. Its
   translation is judged, but cannot be plugged into the game yet.

## Measuring how much the material helps

`pakuport banco prepara` builds a **blind benchmark**: a frozen selection of functions, each with
a task file (`ENCARGO.md`) that contains only what the tools produce today. Give it to agents
that know nothing else, judge with `banco juzga`, and you get one number. Change the tools,
regenerate the task files with `pakuport banco encargos` (same selection), run again, and you
know whether the change helped. `tools/banco_control_delega.py` writes the positive control
(an answer per function that just delegates to the original) so you can measure the bench's
denominator without spending a single model call.

## Beyond register-exact translations

The board's translations are faithful to the instruction: they are the original, in C#. A
*readable* remake — idiomatic C#, its own data structures, no registers — cannot be judged per
call, because it has no registers to compare. It is judged **per frame** instead:
`PAKU_FOTOGRAMAS` + `PAKU_FOTOGRAMA_EN=<address of the frame loop>` record the game's state and
screen at every frame together with every input the game read, and a remake fed those inputs
must reach the same state, frame after frame. (Today that recorder captures the CGA screen
only; see [LIMITATIONS](LIMITATIONS.md).)

## Art with image models

The HD kit ([HD-ART.md](HD-ART.md)) writes a brief per asset (`pendiente.json`): what to draw,
the reference image, the size or exact multiple, whether it needs transparency, and the path to
save it to. Lessons learned the hard way:

- Ask the model to **redraw** the reference at the higher resolution, not to "remaster" it — it
  must respect the grid, the silhouette and the palette's intent.
- Ask for **one sprite sheet** per character or object, on one canvas, in a grid, on a flat
  chroma background — sprite-by-sprite generation gives inconsistent results.
- Validate a concept with two or three sprites in the running game before generating hundreds.
- Run `pakuport remake check`: it rejects deliveries that would break the game (wrong size,
  leftover chroma, an animation delivered with frames missing).
