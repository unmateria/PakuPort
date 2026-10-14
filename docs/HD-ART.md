# HD art: the remake kit

The kit replaces a game's graphics with new art **without touching the game**: the guest keeps
drawing exactly what it always drew, and the host swaps in your images on the way to the screen.
The art never decides anything — what is drawn, where, at what size, in what order, whether it
stays — PakuPort measures all of that and writes it down. The artist (a person or an image
model) only delivers PNG files to the paths the kit names.

## Zero-art option: scalers

Before drawing anything, two switches make any ported game look sharper on a modern screen:

| switch | does |
|---|---|
| `PAKU_SCALE3X=1` | Scale3x on palette **indices** (not RGB, so it never invents edges between two similar colours): crisp pixel-art upscale |
| `PAKU_XBRZ=2..6` | xBRZ at ×2…×6 on the presented RGB frame: rounded diagonals and smooth type, cached per frame |

## The cycle

```
capture ─► study ─► (draw) ─► check ─► judge ─► play
                       ▲  try (one image, before/after)
```

`pakuport remake` with no verb tells you where you are and the literal next command.

### 1. `pakuport remake capture <id>`

Records a session with every telemetry switch the study needs, set for you (blits with pixels
and identity, palette, presented frames, audio events, program-counter samples) plus an input
tape, and keeps the results in `games/<id>/remake/captura/`.

- Needs `cyclesPerMs` in `game.json` (and the export regenerated): the tape and the judge depend
  on a fixed speed. Without it, it stops and says so.
- Without `--teclas`, a window opens and **you play**; close it or wait for `--minutos` (3).
- With `--teclas "<keys>"`, the run is unattended and the window hidden; the keys are fed
  through `PAKU_AUTOKEYS` (`~` is a pause). `--raton` wiggles the mouse into the tape.
- It refreshes the export's copy of the core and rebuilds it first, so it never measures an
  old engine.

### 2. `pakuport remake study <id>`

Turns the capture into the job:

| output in `games/<id>/remake/` | is |
|---|---|
| `pendiente.json` | **the brief**: one entry per asset — key, reference image, size, shape, needs alpha, how often it is drawn, by which routine, animation frames, layer, which assets it covers or is covered by, persistence, scenes, and `destino`, the path to save your image to |
| `ref/` | reference images, cut from the screen as the player saw it, with the real palette |
| `grupos.json` | figures rebuilt from rows: a game that draws a sprite one row per blit has one identity per row; consecutive rows drawn by the same routine are grouped into one figure (`grupo:XXXXXXXX`) — **draw those, not the rows** |
| `capas.json`, `escenas.json`, `texto.json` | draw order and persistence, scenes, text lines |
| `dossier.md` | the game explained |
| `manifiesto.json` | what was measured, for the next steps |

`pakuport remake hoja <id>` renders the whole brief as one HTML page.

### 3. Draw

Each asset at its `w`×`h` or an exact multiple, saved at its `destino` (under `hd/`). If an
entry has `animacion`, deliver **all** of its frames or none.

### 4. `pakuport remake check <id>`

The customs office: validates every delivered image **without running the game** (size and
multiple, alpha where needed, leftover chroma background, incomplete animations), and writes the
runtime contract `remake.json` with what passed. Anything rejected is listed with the reason;
anything not delivered simply stays original.

### 5. `pakuport remake judge <id>`

Replays the tape twice, without and with the remake, and reports:

- **no perturbation**: the guest draws exactly the same blits in both runs (a signature of
  every blit). The remake must never change the game; this is the non-negotiable check;
- coverage by blits, the biggest gaps (assets drawn most often with no art), drift (instances
  outside their box) and regressions; the report goes to `informe.md`.

### 6. `pakuport remake play <id>`

Plays the game with the remake on (`PAKU_REMAKE=games/<id>/remake/remake.json`).

### `pakuport remake try <id> <key> <image.png>`

One image, in the game, before and after side by side, without touching `remake.json`. Both
runs replay the session's tape and stop at the same **instruction** (`--ms` × `cyclesPerMs`),
with the guest frozen there while the hidden window saves the composed screen
(`PAKU_CORTE_FOTO=1`), so the two halves show the same instant of the game. Without a tape or a
fixed speed it falls back to a cut by host time and says so.

## How the replacement works

Every blit carries an identity computed at its **source** (which bytes were copied, where they
came from), not a hash of what ended up on screen — that is what failed in earlier attempts
(overlapping sprites, clipped edges, frames never captured, all mismatching). On the host side,
each HD instance is drawn over a shadow of the guest's screen, and **holes are punched** in it
wherever the game draws something new on top afterwards — the cursor, a highlight, a shot — so
that a texture can never hide live content. The guest is never touched: with dozens of
replacements active, video memory stays byte-identical.

## Known limits

- **Moving sprites.** The hole-punching rule compares against the area as it was when the
  texture first appeared. A sprite that moves by erasing and redrawing itself every frame never
  matches, so its HD texture shows up pierced, with original pixels through the holes. Visible
  in the bundled sample (`samples/catch`). Static screens, backgrounds and UI work.
- **Coverage of grouped figures.** `judge` counts coverage per blit key; a figure delivered as a
  `grupo:` key replaces rows that keep their own keys, so coverage can read 0 % while the figure
  is in fact replaced.
- **Games that compose off-screen.** If a game draws into its own RAM buffers and copies to
  video memory in a way the blit telemetry does not see, the capture comes out empty; `capture`
  detects it and stops instead of handing out an empty brief.
- **Drawing with plain `mov`.** The blit telemetry sees block copies (`movs`/`stos`). Pixels
  written one `mov` at a time have no blit identity; `PAKU_PROC_PIXEL=1` (which code wrote each
  pixel) is the tool for those.

More in [LIMITATIONS.md](LIMITATIONS.md).
