# PakuPort CLI reference

Every command below is written as `pakuport <command>`, which stands for
`dotnet run -c Release --project src\PakuPort.Cli --` run from the repository root. Run it from
the root: most commands read and write relative paths such as `games\<id>\`, `export\<id>\` and
`entrega\`.

This reference is taken from the code (`src\PakuPort.Cli\`). Only the options the code actually
reads are listed. The CLI's own console messages are in Spanish; where one is quoted it is kept
verbatim, with a short English gloss.

General rules that apply to every command:

- `--help` or `-h` **anywhere** on the command line prints the built-in help and exits, without
  running anything.
- An option that takes a value takes the next argument (`--out dir`). There is no `--opt=value`
  form.
- An unknown command prints `comando desconocido: <cmd>` ("unknown command").
- Commands from the second group (marked *needs `--game`*) stop with
  `<cmd>: falta --game <ruta a game.json>` ("missing --game <path to game.json>") if it is absent.
- Commands that take a function accept it either as the first argument after the command
  (`pakuport verify FUN_1000_189 --game ...`) or as `--func FUN_1000_189`. Without one they print
  `uso: pakuport <cmd> <FUN_1000_off> [--game <game.json>]` ("usage").

The examples use the bundled sample: `samples\catch\dos\CATCH.COM`, whose id is `catch`.

---

## 1. Porting

### `port`

```
pakuport port <PATH\GAME.EXE|.COM> [--id <name>] [--out <dir>] [--smoke-ms <n>] [--keys <autokeys>]
                                   [--no-smoke] [--no-package] [--reinit]
```

The whole chain in one command:

1. **init**: writes `games\<id>\game.json` (+ `codemap.json`). An existing `game.json` is kept
   as is (it may carry hand calibration); `--reinit` regenerates it. If the existing `game.json`
   belongs to a *different* binary with the same file name, `port` stops and asks you to choose
   `--id <other-name>` (recommended) or `--reinit` (overwrites). If the file header says LE
   (32-bit protected mode), a short protected-mode `game.json` is written instead of running
   `init`.
2. **dump-image** (16-bit MZ only): canonical memory image into `games\<id>\explorer\`, which
   enables the real-startup boot (`--boot-real`) of the export.
3. **export** into `--out` (default `export\<id>`). If `games\<id>\seeds-runtime.log` exists it is
   passed as `--seeds` automatically. `--keys` is also passed as `--probe-keys`.
4. Copies the game's data files next to the export (never overwrites generated files).
5. `dotnet build -c Release` of the export (20-minute cap).
6. **Smoke run**, headless, for `--smoke-ms` ms (default `20000`), driven by `--keys` if given.
   `--no-smoke` skips it.
7. **package** into `entrega\` unless `--no-package`. This step runs its own smoke (15 s) even
   when `--no-smoke` was given.

| Option | Effect |
|---|---|
| `--id <name>` | Game id (folder name under `games\` and `export\`). Default: the file name, lower-cased. |
| `--out <dir>` | Export folder. Default `export\<id>`. |
| `--smoke-ms <n>` | Length of the smoke run. Default 20000. |
| `--keys <autokeys>` | Keys that drive the smoke run *and* the discovery probe of the export (`~` = pause), so code reachable only after menu input gets found. |
| `--no-smoke` | Skip step 6. |
| `--no-package` | Skip step 7. |
| `--reinit` | Regenerate an existing `game.json`. |

Before starting, `port` warns if the binary uses a DOS extender PakuPort does not implement.
It ends with a summary block `== PORT RESUMEN: <id> ==` ("port summary").

```
pakuport port samples\catch\dos\CATCH.COM --no-package
```

### `package`

```
pakuport package <game.json|id> [--keys <autokeys>] [--smoke-ms <n>] [--no-smoke]
```

Turns an already exported and buildable `export\<id>` into a double-click delivery in
`entrega\<folder>\`: `dotnet publish` self-contained for win-x64, the game's data files, an empty
`logs\` and a `LEEME.txt` (reused from the previous delivery if there is one). The executable is
`Jugar<Id>.exe` and the folder `<Id>-PakuPort`, unless `game.json` sets `packageName` /
`packageDir`. The published exe gets a smoke run (default 15000 ms; `--no-smoke` skips it) that
must report no error and some non-black pixels. A previous delivery is kept as `<folder>_preN`.
If `game.json` has `"publishFlow": "solo-nucleo"`, the export's `Core\` is refreshed from `src\`
and rebuilt first, without regenerating the export.

```
pakuport package catch --no-smoke
```

### `export` *(needs `--game`)*

```
pakuport export --game <game.json> [--out <dir>] [--boot-real] [--seeds <file>]
                [--probe-keys <autokeys>] [--portadas <board-dir>]
```

Generates the .NET project of the game (default `export\<id>`): the game's code, a copy of the
PakuPort core, the host program, `.csproj` and `README.md`. Build and run it with
`dotnet run -c Release` inside that folder. For a `"format": "le"` game it generates the 32-bit
host instead (the game runs as is on the 32-bit interpreter); only `--out` applies there.

| Option | Effect |
|---|---|
| `--out <dir>` | Output folder. Default `export\<id>`. |
| `--boot-real` | Start from the program's real startup over the memory image left by `dump-image` (`explorer\memimage.bin` + `image.json`), and ignore a captured gameplay snapshot. Without the flag, if those files exist the real entry is still seeded for discovery. |
| `--seeds <file>` | Extra function addresses to discover: every line with a `0xNNNN` value (for example the `logs\gaps.log` that a running export writes). Addresses outside the image are reported and skipped. |
| `--probe-keys <autokeys>` | Drive the discovery probe with these keys (deterministic, by instruction budget) instead of the default key thread, so code behind menus is discovered. |
| `--portadas <board-dir>` | Copy the translated functions of a remake board (`respuesta.cs` files from `expediente tablero`) into the project and compile them in, so the game runs them in place of the original code. Without it the export is the plain one. |

```
pakuport export --game games\catch\game.json
pakuport export --game games\catch\game.json --portadas games\catch\expediente
```

### `init`

```
pakuport init <GAME.EXE|.COM> [--id <name>]
```

Static analysis of the binary. Writes `games\<id>\game.json` and `games\<id>\codemap.json`
(load segment derived from a simulated DOS boot, code map from prologues and call targets,
overlay and entry detection). `port` runs it for you. Prints the next steps
(`survey`, `export`).

```
pakuport init samples\catch\dos\CATCH.COM --id catch
```

### `run` *(needs `--game`)*

```
pakuport run --game <game.json> [--exe <path>] [--entry <hex>] [--corpus <func>] [--boot-real] [--snapshot]
```

A diagnostic run inside the CLI process (to play, build and run the export instead). It
discovers and lifts the game's functions, compiles them, runs them for a fixed budget
(10,000,000 steps) and writes the final framebuffer as a BMP to an `output\` folder two levels
above the game's directory (`run_<exe>.bmp`), with the count of non-black pixels.

| Option | Effect |
|---|---|
| `--exe <path>` | Use this executable instead of `game.json`'s `exePath`. |
| `--entry <hex>` | Entry offset; overrides `entryOverride` from `game.json`. |
| `--corpus <func>` | Inject the DGROUP of that function's first corpus case before running. |
| `--boot-real` | Start from the real startup over `explorer\memimage.bin` (needs `dump-image` first). |
| `--snapshot` | Start inside the game from `explorer\gameplay_snapshot.bin` + `gameplay_regs.json`. |

### `discover` *(needs `--game`)*

```
pakuport discover --game <game.json>
```

Discovers every function reachable from the corpus seeds, the entry point and the code map, and
prints coverage and opcode statistics. Writes `functions.json` next to `game.json` (resident
function offsets and functions with inline parameters).

### `dump-image` *(needs `--game`)*

```
pakuport dump-image --game <game.json> [--output <dir>]
```

Writes the canonical memory image of an MZ game to `--output` (default: `explorer\` next to
`game.json`): `image.bin`, `image.json` (coordinates, real entry, real stack) and, when it can be
built, `memimage.bin` (the image as DOS leaves it in memory after unpacking). `.COM` games are
refused: `dump-image: solo soportado para MZ/EXE; COM no aplica.` ("only supported for MZ/EXE").

---

## 2. Measuring and the remake board

The current path to a remake: record a corpus of real invocations while the export runs
(`PAKU_CORPUS=<dir>`), build the board with `expediente tablero`, write `respuesta.cs` files, and
judge them with `banco juzga`.

### `corpus-diag`

```
pakuport corpus-diag --corpus <dir> [--game <game.json>] [--func <FUN_1000_xxxx|xxxx>] [--case <n>]
                     [--traza <n>] [--traza-desde <n>] [--traza-regs] [--presupuesto <n>]
```

Runs each recorded case through the interpreter (that is, the original code) and classifies why
a case does **not** reproduce, by symptom: `Verde` (reproduces), `Excepcion`, `SinFin` (used up
the step budget), `PilaDesbordada` (stack ended far below), `NoRetorna` (ended elsewhere than the
recorded return), `DesyncEfectos` (asked for an effect out of order), `EfectosDeMas` (left
recorded effects unconsumed), `Registro` (memory equal, a register differs), `Memoria`.

| Option | Effect |
|---|---|
| `--corpus <dir>` | Required. The corpus folder. |
| `--game <game.json>` | Mount the code world the same way `interp-verify` does. Without it, `<corpus>\mundo.bin` must exist. |
| `--func <name>` | Only this function (`FUN_1000_` prefix optional); prints the per-case detail instead of the summary table. |
| `--case <n>` | Only this case index. |
| `--traza <n>` | Record up to `n` executed instructions. |
| `--traza-desde <n>` | Start recording the trace after `n` instructions. |
| `--traza-regs` | Add registers to each trace line. |
| `--presupuesto <n>` | Step budget per case. Default 20,000,000. Lower is faster but a long legitimate case shows up as `SinFin`. |

```
pakuport corpus-diag --corpus games\catch\corpus --game games\catch\game.json
```

### `expediente capas`

```
pakuport expediente capas --game <game.json> --corpus <dir> --capas <csv> [--out <json>]
```

Aggregates, per function, the layer profile that a running export records with
`PAKU_CAPAS=<csv>`, and classifies each function by what it **did**: `Render` (wrote VRAM, INT
10h, VGA ports), `Audio` (OPL, Sound Blaster, MPU-401, speaker ports), `ES` (INT 21h), `Entrada`
(mouse/keyboard), `Tiempo` (clock, PIT), `Plataforma` (PIC, DMA, multiplex/EMS/DPMI),
`Desconocida`, and `Logica` (touched none of those). Prints the split and the share of events
attributed to a function. `--out` writes the result as JSON (the board reads it from
`games\<id>\capas.json` by default).

```
pakuport expediente capas --game games\catch\game.json --corpus games\catch\corpus --capas logs\capas-catch.csv --out games\catch\capas.json
```

`pakuport expediente` with no sub-verb prints the usage of both sub-verbs.

### `expediente tablero`

```
pakuport expediente tablero --game <game.json> --corpus <dir> [--out <dir>]
                            [--capas <json>] [--escenas <json>] [--frontera <json>]
```

Builds the remake board: one folder per unit of the corpus (a function, or one turn of a loop),
each with `desensamblado.asm` and `PLANTILLA.cs`, a C# skeleton whose header carries what was
measured (layer with its proof, scenes, executed code coverage, calling convention, globals,
callees and how to call them, proposed name with its evidence, and the exact `oracle-run` and
judge commands). Also writes `banco.json` (the same format `banco juzga` reads), `expediente.json`,
`COMO-ESCRIBIR.md` (how to write a translation) and `LEEME.md`. Units the recorder saw but has no
cases for, and a `PROGRAMA` unit for the program's entry point, get a folder too.

| Option | Effect |
|---|---|
| `--out <dir>` | Default `expediente\` next to `game.json`. |
| `--capas <json>` | Default `capas.json` next to `game.json`. |
| `--escenas <json>` | Default `escenas-funciones.json` next to `game.json`. |
| `--frontera <json>` | Default `frontera.json` next to `game.json`. |

Missing optional inputs are declared in each header rather than invented. Requires
`<corpus>\mundo.bin`.

```
pakuport expediente tablero --game games\catch\game.json --corpus games\catch\corpus
```

### `banco prepara | encargos | juzga`

```
pakuport banco prepara  --game <game.json> --corpus <dir> --out <dir> [--n <count>] [--verdes <log>]
pakuport banco encargos --banco <dir>
pakuport banco juzga    --banco <dir> [--exige-ritmo]
```

- **`prepara`** builds a blind test bench with a frozen selection rule: functions with at least
  3 cases, 5 to 200 instructions, spread evenly by offset, up to `--n` (default 20), plus the three
  shortest as a positive control. With `--verdes <log>` only functions listed on the `VERDES:`
  line of that `interp-verify` log are eligible (the code recommends a log made with
  `--registros`). Writes `banco.json` and, per function, `desensamblado.asm` and `ENCARGO.md`
  (the brief). Requires `<corpus>\mundo.bin`.
- **`encargos`** rewrites the `ENCARGO.md` files of an existing bench with today's material,
  without changing the selection.
- **`juzga`** compiles every `<dir>\<func>\respuesta.cs` together and runs each against its
  recorded cases, comparing registers and memory. Before judging a translation it runs the
  **original** through the same cases; if the original does not reproduce them the function is
  `INJUZGABLE` (unjudgeable) and does not count against anyone. It also compares the instruction
  and clock-tick count of each case against the original ("ritmo", rhythm). Per function the
  result is `VERDE` (green), `rojo` (red), `NO COMPILA`, `sin respuesta` (no answer),
  `INJUZGABLE` or `SIN CASOS` (compiles, nothing to measure against). With `--exige-ritmo` a
  translation is green only if its rhythm is also exact in every case.

A translation is a public static class `Respuesta` with
`public static void Ejecuta(Cpu c, Memory m, Original o)` (or `ushort` return for a loop turn);
`COMO-ESCRIBIR.md` in the board has the full contract.

```
pakuport banco juzga --banco games\catch\expediente --exige-ritmo
```

### `oracle-run` *(needs `--game`)*

```
pakuport oracle-run <FUN_1000_xxxx> --game <game.json> [--base <n>] [--entrada <json>] [--out <json>] [--pfc <file>]
pakuport oracle-run --game <game.json> --func <FUN_1000_xxxx> ...
```

Asks the original code a **new** question: runs one function through the interpreter with an
input you provide, and reports what it did. The code world is `explorer\memimage.bin` next to
`game.json` if present, otherwise the executable's canonical image.

| Option | Effect |
|---|---|
| `--base <n>` | Start from corpus case `n` of this function (default 0 when the game's corpus has cases for it), so DGROUP is a real game state. If the case's address does not match this `game.json`, it stops with an explanation (usually the wrong `game.json`). Without corpus cases it starts from clean memory. |
| `--entrada <json>` | The input, applied on top of the base state (schema below). |
| `--out <json>` | Write the answer there instead of printing it. |
| `--pfc <file>` | Also save the question and answer as a corpus case file (`.pfc`) that the case judge reads as is. |

**Input JSON** (every field optional):

```json
{
  "registros": { "ax": "0x0010", "bx": 5, "ds": "1E6E", "flags": "0x0002" },
  "memoria": [
    { "en": "ds:0x0100", "bytes": "AABBCC" },
    { "en": "0x12345", "fichero": "buffer.bin" }
  ],
  "llamar_antes": [ "FUN_1000_19a", { "funcion": "FUN_1000_1b9", "registros": { "ax": 1 } } ],
  "devolver": [
    { "en": "ds:0x0100", "bytes": 16 },
    { "en": "es:0x0000", "bytes": 64000, "a": "out.bin" }
  ]
}
```

- `registros`: `ax bx cx dx si di bp sp ds es ss flags`. A value is a JSON number or a string
  read as **hex** (`"0x"` optional). Unknown names are ignored with a warning. Giving `ds`
  re-anchors the DGROUP base to `ds*16` before the memory patches are applied.
- `memoria`: patches written after the base state. `en` is an address; the data is either
  `bytes` (hex string, spaces allowed) or `fichero` (a file's contents). A patch that lands
  inside the program's code (when `residentCodeEnd` is in `game.json`) produces a warning.
- `llamar_antes`: functions run first, in order, on the same memory and CPU (a name, or
  `{ "funcion", "registros" }`). The stack pointer is restored after each one.
- `devolver`: memory to return in full after the run: `en`, `bytes` (length), and optional `a`
  (file to write the bytes to; without it up to 256 bytes come back as hex).

Addresses: `ds:<hex>` is relative to the DGROUP base, `es:`/`ss:`/`cs:<hex>` use those
registers (the entry values for `memoria`, the exit values for `devolver`), `<hexseg>:<hexoff>`
is a literal segment, and a bare hex number is a linear address.

**Output JSON**: `func`, `contraste`, `ghidra_offset`, `ok`, `error`, `avisos` (warnings),
`base_corpus`, `ds_base`, `codigo`, `entrada` and `salida` (registers), `instrucciones`,
`tramos_escritos`, `bytes_escritos`, `tramos_omitidos`, `direcciones_distintas`, `camino` (unique
addresses visited, in order), `memoria_escrita` (DGROUP ranges that changed, before/after, up to
256 ranges of up to 64 bytes each) and `devuelto`.

When starting from a recorded case **without** `--entrada`, the case's recorded effects are
replayed and `contraste` is a self-check against the recorded answer:
`VERDE: identico al caso grabado del original` ("green: identical to the original's recorded
case") or `ROJO: difiere del caso grabado — ...` ("red: differs from the recorded case", with the
registers and byte count that differ). With `--entrada` the recorded effects are not applied,
and a warning says so. Exit code 0, or 2 if the run raised an error.

```
pakuport oracle-run --game games\catch\game.json --func FUN_1000_189 --base 0
pakuport oracle-run --game games\catch\game.json --func FUN_1000_189 --base 0 --entrada seed1.json
```

### `interp-verify` *(needs `--game`)*

```
pakuport interp-verify --game <game.json> [--corpus <dir>] [--strict] [--registros]
```

Replays the whole corpus through the interpreter (pure interpretation of the original bytes) and
compares each function's cases with the case judge. Prints `VERDE`, `ROJO`, `ERROR` and `SKIP`
counts and the lists. A case whose address does not belong to this `game.json`'s code world is
skipped; if every case is skipped it says the configuration is wrong.

| Option | Effect |
|---|---|
| `--corpus <dir>` | Use this corpus instead of the one in `game.json`. |
| `--strict` | Turn off the noise masking (which otherwise tolerates small divergences common to all failing cases). |
| `--registros` | Also compare output registers, not only memory. Implies `--strict`. |

Exit code is 0 only when at least 20 functions are green (it doubles as a regression gate),
otherwise 3.

```
pakuport interp-verify --game games\catch\game.json --registros
```

---

## 3. HD art kit: `remake`

```
pakuport remake [<id>]
pakuport remake doctor  <id> [--export <dir>] [--json]
pakuport remake capture <id> [--minutos <n>] [--teclas <keys>] [--raton] [--export <dir>]
pakuport remake study   <id> [--export <dir>]
pakuport remake hoja    <id>
pakuport remake try     <id> <key> <image.png> [--ms <n>]
pakuport remake check   <id>
pakuport remake judge   <id> [--export <dir>]
pakuport remake play    <id> [--export <dir>]
```

Everything lives in `games\<id>\remake\`. The analysis steps are Python scripts under
`tools\remake_kit\` (and other `tools\` folders), launched by the CLI with the paths already
resolved; Python must be on the `PATH`. `--export` defaults to `export\<id>`. If `<id>` is
omitted and exactly one game has a `remake\` folder, that one is used.

- **`remake [<id>]`** (status) says which step you are on and prints the literal next command
  (`Siguiente:` = "next"). It also surfaces bad news from the last judgement (low coverage,
  assets that lost their art, an oversized capture), and whether the study is current
  (`estudio: vigente`) or was made from another capture (`antiguo`).
- **`doctor`** checks that the export still matches what generated it. `export` leaves a
  `pakuport-host.json` in the export with the `game.json`, the game binary, the host's
  `Program.cs` and `.csproj`, the template's hash and the exact export command. A host edited by
  hand, generated for another `game.json` or binary, or one that cannot record what the capture
  needs (the 32-bit LE host records neither tape nor blits) is refused, with the command that
  fixes it. An export made before this contract has none: regenerate it once with that command.
  A changed template only warns. `capture` runs the same check before recording.
- **`capture`** records a session with the right telemetry switched on. Requires `cyclesPerMs`
  in `game.json` (without a calibrated speed the tape does not replay identically). It refreshes
  the export's `Core\` from `src\`, builds it, and runs it, recording `remake\cinta.tape` (the
  input tape) and copying the telemetry into `remake\captura\`. `--minutos` caps the session
  (default 3). Without `--teclas` the window opens and you play; with `--teclas <keys>` the run is
  scripted with a hidden window, and `--raton` then also moves the mouse during the recording.
  If almost nothing was recorded while the game did draw, it stops and explains that the game
  composes off-screen, which the kit cannot follow yet. Each run records into
  `remake\intentos\<date>`, and the tape and the telemetry are published **together**, only if
  the run ended well (no error, no `[EXIT] Error`, events on the tape); a failed run leaves the
  previous capture as it was. The published attempt is deleted; the last three failed ones are
  kept to look at.
- **`study`** measures the capture and produces the catalogue, reference images (`remake\ref\`),
  drawing groups, layer order, text, the dossier and `remake\pendiente.json` (the list of art to
  make). Needs the tape from `capture`. **Any step that fails stops it.** It leaves a seal
  (`estudio-procedencia.json`) with the fingerprint of the capture, the binary and the tools it
  used; `check` and `judge` refuse a study made from another capture.
- **`hoja`** renders the whole job as one page to look at (`tools\remake_kit\build_hoja.py`).
- **`try`** shows one image before and after, in seconds (`tools\remake_kit\try_asset.py`). The
  keys come from the `hoja`. `--ms` is passed through to that script (see source).
- **`check`** validates the delivered art against the contract (`remake\manifiesto.json`, from
  `study`) without running the game (`tools\remake_kit\check_arte.py`).
- **`judge`** runs the tape with and without the remake and compares
  (`tools\remake_kit\judge_fidelidad.py`). It needs `remake\remake.json`, the admitted art; if it
  is missing the CLI tells you to run `check`. Both runs stop at the same instruction
  (`--ms` × `cyclesPerMs`) and run in copies of the host, so the export is not touched. It is
  RED if the host fails or if **nothing was replaced** (neither blits nor logical drawings): with
  that tape the art is not being judged. A RED judgement keeps the output of each run in
  `remake\juicio-rojo\` (the last one only).
- **`play`** runs the export with the remake applied (`PAKU_REMAKE`), or original if there is no
  admitted art. Note this is `remake play`, unrelated to the top-level `play` of section 6.

```
pakuport remake capture catch --minutos 1 --teclas "~"
pakuport remake study catch
pakuport remake check catch
pakuport remake judge catch
```

---

## 4. 32-bit / LE tools

### `le-dump`

```
pakuport le-dump <exe> [--base <0xHEX>] [--out <file.bin>]
```

Loads a 32-bit LE executable with the production loader at `--base` (default `0`), prints its
objects, entry `EIP`/`ESP` and the number of fixups applied, and with `--out` writes the flat
image. It exists so a loader judge can measure the production loader against an oracle dump.

### `le-run`

```
pakuport le-run <exe> [--insns <n>] [--base <0xHEX>] [--sin-sb] [--permitir-escritura]
                [--teclas <keys>] [--machacar <key>] [--cada <n>]
                [--raw <file>] [--volcar <0xADDR[:LEN]>] [--imagen <file.ppm>]
```

A probe: runs an LE program in the flat 32-bit world for a number of instructions and reports
where and why it stopped, whether `EIP` left the program's code, registers and segment bases,
video mode and non-black pixels, IRQs delivered, BIOS keyboard buffer state, the most frequent
VRAM indices, the first DAC entries, and the services the program asked for (and those
rejected).

| Option | Effect |
|---|---|
| `--insns <n>` | Instruction budget. Default 100000. |
| `--base <hex>` | Load address. Default `0x110000` (above 1 MiB). |
| `--sin-sb` | Do not install the Sound Blaster (a negative control for audio waits). |
| `--permitir-escritura` | Allow the program to write files. By default the probe is read-only. |
| `--teclas <keys>` | Run in chunks and press each key between chunks, held for a whole chunk. |
| `--machacar <key>` | After `--teclas`, press this key over and over for the rest of the budget (`--teclas` then use the first half). |
| `--cada <n>` | Instructions between presses for `--machacar`. Default 20,000,000. |
| `--raw <file>` | Write the framebuffer as raw RGBA. |
| `--volcar <0xADDR[:LEN]>` | Hex dump and disassemble live memory (default length 64). |
| `--imagen <file.ppm>` | Write the framebuffer as a PPM image. |

### `platform-test`

```
pakuport platform-test <tests.json> [--out <result.json>]
```

Runs a table of BIOS/DOS interrupt and I/O port cases against PakuPort's platform layer and
writes the output registers (and comparable buffers) per case to `--out` (default
`result_paku.json` next to `tests.json`), in the same logical format that the test program
produces in the reference emulator, so a judge can diff both. Schema of `tests.json`: see source
(`CmdPlatformTest` in `Program.cs`).

---

## 5. Legacy lifted-code commands

`verify`, `emit`, `decode`, `crosscheck`, `sanity`, `trace`, `render`, `survey`, `diag-red`
(all *need `--game`*) belong to the earlier pipeline, where each function was **lifted** to C#
and judged against the corpus. The current path is the interpreter plus the board and bench
(`interp-verify`, `expediente tablero`, `banco juzga`, `oracle-run`).

The lifted-code judge reads functions at MZ file offsets, so `verify`, `emit`, `decode`,
`crosscheck`, `sanity`, `trace` and `render` refuse any game whose format is not `exe`:

```
<cmd>: el juez del código lifteado sólo sabe leer ejecutables MZ (.EXE); este juego es '<format>'.
Usa el banco: pakuport expediente tablero + pakuport banco juzga.
```

("the lifted-code judge can only read MZ executables; this game is '<format>'. Use the bench:
...".) `survey` and `diag-red` use the same lifted judge but are not covered by that check.

| Command | What it does |
|---|---|
| `verify <func>` | Lifts the function and replays its corpus cases; prints `VERDE n/n` or `ROJO n/n` with the first divergence. Exit 0 green, 3 red, 2 error. |
| `emit <func>` | Prints the generated C# for the function's call tree, with opcodes used and unsupported ones. |
| `decode <func>` | Prints the function's decoded instructions (offset, bytes, text). |
| `crosscheck <func>` | Compares the decoder against capstone (offset, length, mnemonic). Needs `python` with the `capstone` package. |
| `sanity <func>` | Anti-false-green check: must be green clean and red after injecting a deliberate bug (first subtraction turned into an addition). |
| `trace <func> [--case <n>]` | Prints the path (`#IPS`) and DGROUP writes (`#WRITES`) of one case, in a parseable format. |
| `render <func> [--case <n>]` | Runs one case of the lifted function and writes the framebuffer to `output\render_<func>_<n>.bmp` (two levels above the game's directory). |
| `survey` | Sweeps the corpus: green, red, unsupported, decode errors, and the blockers of the unsupported functions. |
| `diag-red [--out <json>]` | Classifies every divergent byte of every red function (oracle-side effect, phantom write, real recalculation). Default output `logs\diag_rojas.json`. |

---

## 6. Optional Spice86 commands

`record`, `build`, `play`, `doctor` and `credits` drive [Spice86](https://github.com/OpenRakis/Spice86)
(Apache License 2.0), an earlier dynamic-extraction route. They are optional and legacy, and need an
external checkout of Spice86 **next to** this repository: the code expects, relative to the
repository root,

- `..\Spice86\src\Spice86\bin\Release\net10.0\Spice86.exe` (for `record`), and
- `..\Spice86\src\Spice86\Spice86.csproj` (for `build`; overridable with `--spice86`).

| Command | What it does |
|---|---|
| `record <exe> [--game-dir <dir>] [--output <dir>] [--max-minutes <n>]` | Plays the game under Spice86 (sound off) and dumps on clean exit. `--output` defaults to `dumps\<name>`; `--max-minutes` kills Spice86 after that time. |
| `build <dumps-dir> [--output <path.exe>] [--game-name <name>] [--spice86 <csproj>]` | Builds a standalone single-file exe from Spice86's `GeneratedProject\`. |
| `play <exe> [--game-dir <dir>] [--output-dir <dir>]` | `record` + `build` + launch. |
| `doctor [<dumps-dir>]` | Checks whether the dumps are ready for `build` (default folder `.`). |
| `credits` | Prints the attribution notice for Spice86. |
