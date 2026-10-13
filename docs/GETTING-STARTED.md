# Getting started

## What you need

| | required for | notes |
|---|---|---|
| **Windows 10/11 x64** | everything | PakuPort is developed and tested on Windows. Other systems are untested. |
| **.NET 10 SDK** | everything | builds PakuPort; the ports it generates target .NET 8, which the 10 SDK also builds. |
| **Python 3.10+** | the remake tools, the self-test | the remake kit and the board tools use only the standard library. |
| DOSBox-X | the platform probes only | the "oracle": the reference PakuPort is compared against. Not needed to port or remake. |
| `capstone`, `unicorn`, `Pillow`, `numpy` (pip) | some probe generators and judges | only the scripts under `tools/<probe>/` that import them. |

NuGet packages (Iced, Roslyn, Raylib-cs, xUnit) are restored automatically on the first build.

## Build and verify

```powershell
dotnet build PakuPort.slnx -c Release
dotnet test tests\PakuPort.Tests -c Release
python tools\selftest.py
```

The unit tests are thousands of single-instruction checks of the CPU against reference vectors.
`selftest.py` is the one that matters: it ports the bundled sample, records it, generates its
remake board, judges three reference translations (and a deliberately broken one, which must
fail), plugs them into the game and proves the game ends byte-identical. It takes a few minutes
and must finish with:

```
== SELFTEST: GREEN ==
```

Its full log is in `logs\selftest.log`.

## The command line

Every command is run from the repository root:

```powershell
dotnet run -c Release --project src\PakuPort.Cli -- <command> [arguments]
```

The rest of the documentation writes this as `pakuport <command>`. If you prefer a real
`pakuport` command, publish the CLI once and put it on your `PATH`:

```powershell
dotnet publish src\PakuPort.Cli -c Release -o tools-bin
$env:PATH += ";$PWD\tools-bin"     # the executable is tools-bin\PakuPort.Cli.exe
```

## Port your first program

```powershell
pakuport port samples\catch\dos\CATCH.COM --no-package
```

`port` does the whole chain in one go — identify the executable, load it, discover its code,
generate a .NET project, copy the game's files next to it, build it and run a short headless
smoke test — and prints a summary:

```
== PORT RESUMEN: catch ==
  init: OK
  export: OK
  datos: 1 ficheros copiados
  build: OK
  smoke: [autoexit] logica=arrancada terminada=False error=ninguno pixeles_no_negros=3675/64000
```

It writes two folders:

- `games\catch\` — the game's description (`game.json`) and, later, everything you measure.
- `export\catch\` — the generated .NET project. **Do not edit it**: it is regenerated from `src\`.

Run it:

```powershell
cd export\catch
dotnet run -c Release
```

Without `--no-package`, `port` also publishes a self-contained `Jugar<Game>.exe` into
`entrega\<game>\` that runs on any Windows PC by double-clicking.

> **Point `port` at a folder that holds only the game.** It copies the executable's whole folder
> as "the game's data". That is what you want for a real game (its data files must travel with
> it) and exactly what you do not want if the executable sits next to unrelated files.

## Keys of a ported game

| key | does |
|---|---|
| F11 | fullscreen on/off |
| Ctrl+F11 | 4:3 CRT aspect ↔ square pixels |
| Ctrl+F10 | release the mouse |
| Ctrl+F1 / Ctrl+F2 | slower / faster CPU |
| Ctrl+F3 | back to the speed in `game.json` |
| Ctrl+F12 | quit |

Every other key goes to the game.

## Speed, and why tapes need it

`game.json` may hold a `cyclesPerMs` value: how many guest instructions run per millisecond
(the same unit as DOSBox's `cycles=`). A freshly ported game has none (0), which means "free
speed": a fixed instruction budget per presented frame. Two things need a fixed value:

- **recording or replaying an input tape** (`PAKU_TAPE_RECORD` / `PAKU_TAPE_REPLAY`), because a
  tape anchors the game's clock to the instruction counter;
- **the remake kit** (`pakuport remake capture`), for the same reason.

Find a speed that feels right with Ctrl+F1/F2 while playing, then write it into `game.json`
and **export again** — `game.json` is baked into the generated project, so editing it does
nothing until you do:

```json
  "cyclesPerMs": 3000,
```

```powershell
pakuport export --game games\catch\game.json
cd export\catch; dotnet build -c Release
```

For a one-off run, the environment variable `PAKU_CYCLES=3000` overrides the baked value
without re-exporting. Without any speed, a tape run says so and stops:
`PAKU_TAPE_RECORD/REPLAY exigen cyclesPerMs>0`.

## Runs without a window

Every measuring run in this documentation is headless and silent, so it never steals focus:

```powershell
$env:PAKU_HEADLESS='1'; $env:PAKU_NOSPLASH='1'; $env:PAKU_NOAUDIO='1'
$env:PAKU_AUTOEXIT_MS='8000'           # stop after 8 s (host clock)
$env:PAKU_FB_PNG='..\..\logs\shot.png' # save the last frame
dotnet run -c Release --no-build
```

For runs that must be repeatable, cut by instructions instead of milliseconds
(`PAKU_AUTOEXIT_INSN=60000000`) and replay a tape: two such runs leave the same memory and the
same screen, byte for byte. The full list of switches is in
[PALANCAS.md](PALANCAS.md), generated from the code (its descriptions come from the source
comments and are in Spanish).

Next: the [tutorial](TUTORIAL.md).
