# Tutorial: from a DOS binary to translated functions running inside the game

This walkthrough uses **CATCH**, the small original game in [`samples/catch`](../samples/catch).
It is a real 16-bit DOS program — mode 13h graphics, palette writes, sprites copied with
`rep movsb`, BIOS text, the keyboard BIOS, the vertical retrace, the PC speaker — but it is ours,
it is 1,092 bytes, and its source is in the repository, so every claim PakuPort makes about it
can be checked against the truth. Everything below was run exactly as written; the outputs are
real (trimmed). Counts from runs cut by milliseconds (how many cases, how many times something
ran) can differ slightly from run to run; runs cut by instructions are exact.

Commands are run from the repository root. `pakuport` stands for
`dotnet run -c Release --project src\PakuPort.Cli --` (see [GETTING-STARTED](GETTING-STARTED.md)).

**The plan:**

1. [Port it](#1-port-it) — the game runs as a .NET program.
2. [Give it a fixed speed](#2-give-it-a-fixed-speed) — needed for anything repeatable.
3. [Record what it does](#3-record-what-it-does) — real calls of every function, and who does what.
4. [Check the recording](#4-check-the-recording) — every case must reproduce.
5. [Layers and frontier](#5-layers-and-frontier) — drawing, sound, input or logic; what really ran.
6. [Generate the remake board](#6-generate-the-remake-board) — one folder per function.
7. [Translate a function](#7-translate-a-function) — and get it judged.
8. [Prove the judge can say no](#8-prove-the-judge-can-say-no) — the negative control.
9. [Ask the original a new question](#9-ask-the-original-a-new-question) — `oracle-run`.
10. [Plug your C# into the game](#10-plug-your-c-into-the-game) — and prove nothing changed.
11. [Replace the art](#11-replace-the-art) — the HD kit.
12. [Where to go from here](#12-where-to-go-from-here).

---

## 1. Port it

```powershell
pakuport port samples\catch\dos\CATCH.COM --no-package
```

```
  Funciones descubiertas: 23
  Opcodes: 37 tipos, 0 no soportados
  ...
== PORT RESUMEN: catch ==
  init: OK
  export: OK
  datos: 1 ficheros copiados
  build: OK
  smoke: [autoexit] logica=arrancada terminada=False error=ninguno pixeles_no_negros=3675/64000
```

`games\catch\game.json` describes the game; `export\catch\` is a .NET project that plays it.
Play it (arrows move, ESC quits; it starts in demo mode and the first arrow key takes over):

```powershell
cd export\catch
dotnet run -c Release
```

Or take a screenshot without opening a window:

```powershell
cd export\catch
$env:PAKU_HEADLESS='1'; $env:PAKU_NOSPLASH='1'; $env:PAKU_AUTOEXIT_MS='8000'
$env:PAKU_FB_PNG='..\..\logs\catch.png'
dotnet run -c Release --no-build
```

![CATCH after 8 seconds](images/catch-ported.png)

## 2. Give it a fixed speed

Recording and replaying input tapes, the A/B control and the HD kit all need the game to run at
a fixed number of instructions per millisecond. Add it to `games\catch\game.json`:

```json
  "cyclesPerMs": 3000,
```

and export again (the game's settings are baked into the generated project):

```powershell
pakuport export --game games\catch\game.json
cd export\catch; dotnet build -c Release; cd ..\..
```

## 3. Record what it does

One run of the game, headless, with three recorders switched on **in the same run** — their
outputs are cross-referenced later, and two different runs would not match:

| switch | records |
|---|---|
| `PAKU_CORPUS=<dir>` | real invocations of every function: registers and memory in and out, every interrupt, port read and IRQ in between, and the code it ran |
| `PAKU_CAPAS=<csv>` | who writes video memory, who touches which ports, who calls which interrupt |
| `PAKU_COBERTURA=<csv>` | every byte of code that actually executed |

```powershell
mkdir logs\expediente -Force
cd export\catch
$env:PAKU_CORPUS='..\..\games\catch\corpus'
$env:PAKU_CAPAS='..\..\logs\expediente\capas-catch.csv'
$env:PAKU_COBERTURA='..\..\logs\expediente\cobertura-catch.csv'
$env:PAKU_HEADLESS='1'; $env:PAKU_NOSPLASH='1'; $env:PAKU_AUTOEXIT_MS='30000'
dotnet run -c Release --no-build
cd ..\..
```

```
[CORPUS] 146 casos de 20 funciones (20 vistas de 20 candidatas, 0 cierres sucios)
[CAPAS] 18 pares (evento, direccion)
[COBERTURA] 678 bytes de codigo en 4 tramos (444,497,161 instrucciones)
```

Nobody touched the keyboard: CATCH plays itself in demo mode, like the attract mode of many
games of the era. For a game that needs input, play it during the recording, or replay a tape
(`PAKU_TAPE_RECORD` / `PAKU_TAPE_REPLAY`).

The recorder keeps the **first eight calls** of each function by default. A unit is not only a
function that returns: the main loop of a game written in assembly is called once and never
returns, so the recorder also learns **loop heads** (a backward jump taken) and records single
iterations. That is why the board below has "2 vueltas de bucle" (loop iterations).

## 4. Check the recording

Before trusting a corpus, replay every case on the original code and see that it reproduces:

```powershell
pakuport corpus-diag --corpus games\catch\corpus --game games\catch\game.json
```

```
== corpus-diag games/catch/corpus: 146 casos de 20 funciones ==
-- por CASO --
  Verde              146
```

A case that does not reproduce is classified by symptom; `--func` and `--case` narrow it down.

## 5. Layers and frontier

```powershell
pakuport expediente capas --game games\catch\game.json --corpus games\catch\corpus `
    --capas logs\expediente\capas-catch.csv --out games\catch\capas.json
```

```
  Render          6  (30.0 %)   155 170 1d2 1e4 240 39b
  Audio           2  (10.0 %)   215 228
  Entrada         1  (5.0 %)   24e
  Logica         11  (55.0 %)   10b 149 189 19a 1b9 22f 289 2c2 31d 332 3ad
```

Because CATCH's source is in the repository, this can be checked against the truth:
`0155` is `blit`, `0170` is `fill`, `01D2` is `print_at`, `039B` is `set_palette` (Render);
`0215`/`0228` are `sound_on`/`sound_off` (Audio); `024E` is `read_input` (Input); `0189`
`random`, `019A` `check_catch` and `01B9` `to_decimal` are pure logic. The classification is
made from what each function *did*, never from what its code looks like.

The frontier says which code actually ran — the antidote to inventing behaviour for code
nobody has ever seen execute:

```powershell
python tools\expediente_frontera.py --cobertura logs\expediente\cobertura-catch.csv `
    --capas games\catch\capas.json --capas-csv logs\expediente\capas-catch.csv `
    --control --out games\catch\frontera.json
```

```
  codigo ejecutado FUERA del rango de toda funcion conocida: 0 bytes (0.0 % de lo ejecutado)
  CONTROL: direcciones con eventos de PAKU_CAPAS que la cobertura no cubre: 0 de 18
  VERDE: toda instruccion que otro subsistema vio ejecutarse esta cubierta
```

## 6. Generate the remake board

```powershell
pakuport expediente tablero --game games\catch\game.json --corpus games\catch\corpus
```

```
tablero: 21 unidades (19 llamadas, 2 vueltas de bucle) -> games\catch\expediente
  nombre con prueba: 4 (4 de confianza alta)
  avance: pakuport banco juzga --banco games/catch/expediente
```

```
games\catch\expediente\
├── COMO-ESCRIBIR.md        the contract: API, stack, flags, rhythm, calling the original
├── LEEME.md
├── banco.json              the judge's selection of cases
├── FUN_1000_189\
│   ├── PLANTILLA.cs        the skeleton, with everything measured in its header
│   └── desensamblado.asm   the original body
└── ...
```

The header of `FUN_1000_189\PLANTILLA.cs` (translated and trimmed):

```
// FUN_1000_189  —  enters at 0814:0189
// HOW IT WOULD BE CALLED:  NO NAME: there is no evidence of what it does. The honest answer.
// WHAT IT DOES:  Layer: LOGIC.  Code really executed: 17 of 17 bytes (100.0 %)
// CALLING CONVENTION (observed in 8 invocations)
//   Preserves: BX CX DX SI DI BP DS ES     Changes: AX     Returns with ret
// GLOBALS IT TOUCHES
//   ds:[044Ch] 2 bytes WRITTEN  seen: 2F1D->F4BA, F4BA->ABDB, ABDB->1BD0, 1BD0->1229, ...
// THE STACK, WHERE MOST MISTAKES HAPPEN ...
// THE RHYTHM: HOW MANY INSTRUCTIONS THE ORIGINAL WOULD HAVE SPENT ...
// IF IN DOUBT, ASK THE ORIGINAL:  pakuport oracle-run --game ... --func FUN_1000_189 ...
// HOW WHAT YOU WRITE IS JUDGED:   pakuport banco juzga --banco <board folder>
```

and `desensamblado.asm`:

```
0189: push dx
018A: mov ax,[44Ch]
018D: mov dx,6255h
0190: mul dx
0192: add ax,3619h
0195: mov [44Ch],ax
0198: pop dx
0199: ret
```

Note the name: **none**. A function that calls no system service and touches no port leaves no
evidence of its purpose, and PakuPort will not invent one — a made-up name is read as a fact by
whoever comes next. Names are proposed only with a quote of the evidence (a DOS service with its
subfunction, a port, a string), never from a hunch.

## 7. Translate a function

Write `games\catch\expediente\FUN_1000_189\respuesta.cs`. The contract is in
`COMO-ESCRIBIR.md`; in short: a static `Ejecuta(Cpu c, Memory m, Original o)`, registers are
fields of `c`, the 8086 operations with their flags are methods of `c`, memory is read and
written by segment and offset, the stack is memory and is compared, and you **charge** the
instructions the original would have spent (`o.Instrucciones(n)`), because in PakuPort the
game's clock *is* the instruction counter.

```csharp
using PakuPort.Core;
using PakuPort.Core.Verify;

// random: seed = seed * 25173 + 13849, returned in AX.
public static class Respuesta
{
    const ushort Seed = 0x044C;

    public static void Ejecuta(Cpu c, Memory m, Original o)
    {
        c.SP -= 2; m.WrU16(c.SS, c.SP, c.DX);      // push dx
        c.AX = m.RdU16(c.DS, Seed);
        c.DX = 25173;
        c.Mul16(c.DX);                              // DX:AX = AX * DX, sets CF/OF
        c.AX = c.Add16(c.AX, 13849);
        m.WrU16(c.DS, Seed, c.AX);
        c.DX = m.RdU16(c.SS, c.SP); c.SP += 2;      // pop dx
        c.SP += 2;                                  // ret
        o.Instrucciones(8);
    }
}
```

Judge it:

```powershell
pakuport banco juzga --banco games\catch\expediente --exige-ritmo
```

```
  FUN_1000_189: VERDE 8/8 | ritmo EXACTO
  FUN_1000_19a: sin respuesta
  ...
== BANCO: VERDE=1  ROJO=0  NO COMPILA=0  SIN RESPUESTA=20  INJUZGABLE=0  (de 21)   ritmo exacto: 1 (exigido)
```

`VERDE 8/8`: on the eight real calls recorded from the game, your C# leaves the same registers,
flags and memory as the original. `ritmo EXACTO`: and it charged the same number of
instructions. Before demanding anything of you, the judge runs **the original** through the same
hoop; whatever not even the original reproduces is reported as `INJUZGABLE` and counts against
nobody.

Two more reference translations are in [`samples/catch/solutions`](../samples/catch/solutions):
`check_catch` (comparisons and signed jumps, with a different instruction count per path) and
`to_decimal` (a `div` loop). Copy them into their folders and the board reads
`VERDE=3`.

## 8. Prove the judge can say no

A judge that has never been seen red proves nothing. Change one constant —
`c.Add16(c.AX, 13849)` → `13848` — and judge again:

```
  FUN_1000_189: rojo 0/8 (caso 0: memoria: 1 tramo(s), el primero @0x044C len 1: B9 -> BA) | ritmo EXACTO
```

Red in all eight cases, pointing at the exact byte: the seed at `044C`. Put the constant back.

## 9. Ask the original a new question

The corpus answers questions somebody already recorded. `oracle-run` asks a new one: it runs
the original bytes of one function — with DOS, files and video mounted — from a recorded state
(`--base N`, the N-th case) plus whatever you change in an input file:

```json
{ "memoria": [ { "en": "ds:0x044C", "bytes": "0100" } ] }
```

```powershell
pakuport oracle-run --game games\catch\game.json --func FUN_1000_189 --base 0 --entrada seed1.json
```

```
  "memoria_escrita": [ { "en": "ds:0x044C", "antes": "0100", "ahora": "6E98", ... } ],
  "salida": { "ax": "0x986E", ... }
```

Seed 1 gives `0x986E` = 25173 + 13849. The input file also accepts `"registros"` (`"ax"`,
`"bx"`, … `"flags"`); with `--pfc <file>` the answer is written as one more case for the judge.
For `oracle-run`, `game.json` must point at the corpus: add `"corpusDir": "games/catch/corpus"`.

## 10. Plug your C# into the game

```powershell
pakuport export --game games\catch\game.json --portadas games\catch\expediente
cd export\catch; dotnet build -c Release; cd ..\..
```

```
  portadas: 1 funciones traducidas viajan al juego (20 siguen sin traducir)
```

Now, whenever the game enters `0189`, your C# runs instead of the original bytes; the other 20
units still run from the binary. The game writes `logs\portadas.log` with how many times each
translation ran, and `PAKU_SIN_PORTADAS=1` switches them off without rebuilding.

Running is not the same as being right *inside* the game. The A/B control runs the game twice,
with and without your translations, cut at the same instruction and fed the same input tape, and
compares the guest's whole memory **and the screen** (the four VGA planes and the DAC), byte by
byte. CATCH plays itself, so its tape can be empty:

```powershell
Set-Content games\catch\empty.tape '0 T 0'
python tools\control_portadas.py --export export\catch --cinta games\catch\empty.tape --insn 60000000
```

```
VERDE  el arnes es determinista: 2 corridas identicas, 0 bytes de diferencia
VERDE  FUN_1000_189       se ejecuto 14 veces dentro del juego
VERDE  la memoria del invitado sale igual: 0 bytes cambiados
VERDE  la pantalla sale igual: 0 bytes cambiados
== CONTROL DE LAS PORTADAS: VERDE ==
```

And its negative control: with the mutated constant from step 8 exported into the game,

```
ROJO   la memoria del invitado sale igual: 15 bytes cambiados
ROJO   la pantalla sale igual: 294 bytes cambiados
== CONTROL DE LAS PORTADAS: ROJO ==
```

The stars fall in different columns. That is the whole method, at the scale of one function:
**measured facts in, a judge that can say no, and a proof that the game is unchanged.**

## 11. Replace the art

The HD kit has its own guide, [HD-ART.md](HD-ART.md). On CATCH, in short:

```powershell
pakuport remake capture catch --minutos 1 --teclas "~"     # record a session (hidden window)
pakuport remake study catch                               # measure: 13 briefs, 2 sprites
python samples\catch\hd-art\make_hd_art.py games\catch\remake grupo-AEE58220 grupo-FAF9CA1C
pakuport remake check catch                               # 2 of 2 delivered images accepted
pakuport remake judge catch                               # the game draws exactly the same
```

`study` finds that CATCH draws its sprites **row by row** (each `rep movsb` is one row) and
rebuilds the two figures — the 7×7 star and the 24×5 paddle — from the order the rows are drawn
in. The art brief (`games\catch\remake\pendiente.json`) says, for each figure, what to draw, at
which size and where to save it; `make_hd_art.py` stands in for the artist.

> ⚠️ **Known limitation, visible in this very sample:** the runtime replacement "punches holes"
> in an HD texture wherever the game draws over it afterwards, so a texture never hides live
> content. That rule works for static screens and fails for sprites that move by erasing and
> redrawing every frame: CATCH's HD star shows up pierced, with original pixels through the
> holes. See [LIMITATIONS.md](LIMITATIONS.md).

## 12. Where to go from here

- **More functions.** The board's progress is a number: `pakuport banco juzga`. Translate the
  rest of CATCH; for functions that call others, use `o.Llama(target, returnAddress)` (the
  ficha gives you the exact call) and the original does the callee for you.
- **With an AI.** Hand the board to a language model as described in
  [REMAKE-WITH-AI.md](REMAKE-WITH-AI.md): one folder per task, the judge as the only authority.
- **By hand.** [REMAKE-BY-HAND.md](REMAKE-BY-HAND.md) is the same workflow without any model.
- **Your own game.** Repeat steps 1–10 on it. Real games bring real trouble — protections,
  extenders, drivers, overlays — and the measuring switches in [PALANCAS.md](PALANCAS.md) are
  how you find out what is going on.
