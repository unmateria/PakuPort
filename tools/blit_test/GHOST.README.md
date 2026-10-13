# GHOST.COM — liveness net for a moving sprite (HD replacement)

**Status: GREEN. 1 live instance and 11 dead — the survivor is the cursor's final position.**
The probe was born RED on purpose (12 live, 11 ghosts) to MEASURE the gap, and was closed
afterwards with the rule below, once identity by provenance made a defensible rule possible.

## How to run (from the repository root)

```
python tools\blit_test\gen_ghost.py                  # writes GHOST.COM (verified with capstone)
dotnet run -c Release --project src\PakuPort.Cli -- port tools\blit_test\GHOST.COM
copy tools\blit_test\GHOST.COM export\ghost\         # NOTE: port does NOT refresh the .COM in the export
python tools\blit_test\judge_ghost.py
```

## What it reproduces

The frame semantics of the HD replacement say an instance lives while the game redraws it and
dies when a **substantial fraction of its rect** (25 %, per-instance dirty-pixel bitmap) is
overwritten. Validating that on a real-mode game exposed a case it does not cover: the mouse
cursor is **composed with the background** and, when it moves, the game blits **deltas** from
its composition buffer — the abandoned position receives ~8 % of touches, never 100 %. That is
under the threshold, so the old instance never dies: a **ghost** is left at every position the
sprite went through.

`GHOST.COM` (about 60 bytes) does exactly that in mode 13h, with no game involved: 12 steps, a
16x8 sprite drawn row by row (`rep movsw`), a ~65k-instruction delay to leave the burst window,
erasure of **6.25 %** of the rect (8 bytes of 128) and a 16 px advance **without overlap** — so
the new blit cannot be what erases the old one. Linear geometry (13h) on purpose: what is
measured is liveness, not addressing. Everything the judge expects is decoded from GHOST.COM
itself.

Measured before the fix: `blits=96 reemplazados=12 vivas=12`, the 12 trail positions
(32,100) ... (208,100). The pixel count on screen, 1440 = 12x128 - 96, confirms the program did
exactly what it was designed to do.

## Why lowering the threshold does not close it

There are two real cases, both with **few touches**, that need opposite answers:

| case | touches on the rect | what must happen |
|---|---|---|
| cursor passing over a full-screen scene | < 1 % | the scene **lives** |
| moving sprite, abandoned position (this probe) | ~6-8 % | the instance **dies** |

Area fraction cannot tell them apart. And `judge_remake.py` pins a third case any fix must keep:
**a one-shot drawing nobody ever repaints (a title screen) lives forever** — so "dies when it
stops being refreshed" does not work either.

## The rule that closed it (provenance-based)

A second death condition was added to the area threshold:

> An instance dies if the **same asset** appears at **another position** and the instance
> **has already been touched** (`DirtyPx > 0`: someone wrote over it, however little) without
> being redrawn since.

`DirtyPx > 0` is the hinge that keeps the legitimate cases alive:

| case | what happens | result |
|---|---|---|
| moving cursor (this probe) | old position gets ~6 % touches **and** the asset reappears elsewhere | **dies** |
| border of N identical sprites drawn once | nobody writes over them -> `DirtyPx == 0` | lives |
| full-screen scene with a cursor passing over | touched, but **never reappears elsewhere** -> rule never fires | lives |
| one-shot drawing (title screen) | no touches, no reappearance | lives (`judge_remake.py`) |

## Alternatives considered

1. **Chain provenance through the composition buffer**: follow RAM->RAM->VRAM instead of
   looking at sprite rects. Faithful to the real mechanism, no guessing; also the most
   expensive, and it touches `BlitProvenance`.
2. **"The asset moved"**: if the same FNV is registered elsewhere, earlier un-refreshed
   instances die after a grace period. Cheap and fixes the cursor; breaks a border of N
   identical sprites drawn once each.
3. **Liveness by regime**: tell moving assets (many positions over time) from static ones and
   expire only the former by refresh. Keeps the one-shot case; needs per-asset history and a
   "moving" criterion not measured on any game.

The net is what matters: this judge says whether a rule works, and `judge_remake.py` +
`judge_remake_ega.py` say whether it broke what already worked.
