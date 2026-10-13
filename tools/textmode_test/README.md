# textmode_test — the text font lives in PLANE 2, and the CRTC sets the geometry

## What it measures

Six cases, each GREEN/RED, no human eye involved:

| Case | Checks |
|---|---|
| **a** | After `INT 10h AH=00 AL=03`, the framebuffer is 640x400 and the `'A'` in cell (0,0) matches the 8x16 ROM (`RomFonts.Font8x16`) **pixel by pixel**, not the 8x8 font with doubled rows. |
| **b** | After `AH=11h AL=12h` (8x8 double dot with reload): 50 rows, character height 8, BDA `40:84`=49 and `40:85`=8, and a character on text row 49 is drawn on pixel rows 392..399. |
| **c** | **User** font via `AH=11h AL=00` (ES:BP, CX characters, DX first character, BH bytes/char, BL block): the glyph loaded for 0x80 is exactly the one drawn. |
| **d** | Loading **through PORTS**: the same glyph written to `A000:(0x80*32)` with map mask = 4 and odd/even off — the path used by DOSBox-X `INT10_LoadFont` (`src/ints/int10_memory.cpp:59`) and by programs that load their font by hand — is used when drawing. |
| **e** | `AH=11h AL=30` returns `CX` = real height and `DL` = rows-1 read from the BDA, not constants. |
| **f** | The **boot state** (`new VideoState()`, no `INT 10h` at all) already has the 8x16 ROM in plane 2 with stride 32 — a game with `videoMode` in its `game.json` never calls set-mode. |

It also prints a **note** without verdict: how close `Int10hHandler.Font8x8` (the old table,
only used to write text in **graphics** modes) is to DOSBox-X `int10_font_08`. Today it is
1024 bytes (128 characters, not 256) and differs in 6 bytes: the `'S'` glyph (0x53).

## How to run (from the repo root)

```
dotnet run -c Release --project tools\textmode_test
```

Exit code 0 if everything is GREEN, 1 if any case is RED.

## Negative control (no judge goes green without having been seen red)

With the code before the fix — `RenderText` always drawing 25 rows of 8x16 with the 8x8 font
doubled, and every `AL` of `AH=11h` a `break;` — **all six cases are RED**:

```
[ROJO ] a) modo 3: 640x400 y 'A' = ROM 8x16: 640x400, pixeles distintos del glifo 8x16 = 37
[ROJO ] b) AH=11h AL=12h: 50 filas de 8 px: BDA 40:84=24 40:85=16 CRTC alto=16 filas=25 ...
[ROJO ] e) AH=11h AL=30 tras pasar a 50 filas: CX=16 DL=24 (se esperaba CX=8 DL=49)
[ROJO ] c) fuente de usuario AH=11h AL=00 para el 0x80: pixeles distintos = 120
[ROJO ] d) carga por PUERTOS al plano 2: pixeles distintos = 120
[ROJO ] f) estado de arranque: ROM 8x16 ya en el plano 2: bytes distintos = 2320
```

## Why it exists

A DOS music tracker that runs in 80x50 with its own font showed **only the top half** of its
interface, with wrong glyphs. It is a class problem affecting every text-mode program
(utilities, trackers, game menus with custom glyphs), because on a VGA the text font **is not an
emulator table**: it lives in VRAM plane 2 with 32 bytes per character, and the geometry
(columns, character height, rows) comes from the CRTC.
