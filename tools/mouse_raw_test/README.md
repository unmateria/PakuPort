# Mickeys against the screen edge

An objective net for a mouse regression found in a manual playtest of a mode-13h game:
everything worked except the mouse, which "does not move across the whole screen".

## Why it exists

That game does not read the cursor position: it integrates the **mickeys** from
`INT 33h AH=0Bh` in its own space. PakuPort's driver derived them from the delta of
`MouseX/MouseY`, and that position arrives from the host **already clipped to the image edge**,
so any movement made while pressed against an edge was lost. A tiny desync between the game's
cursor and the host's is then enough for the game's cursor never to reach the opposite edge.

A real driver counts hardware mickeys and clips the cursor **separately**. DOSBox-X
`src/ints/mouse.cpp`, `Mouse_CursorMoved`:

```c
if (user_cursor_locked) {                                  // only with the mouse captured
    mouse.mickey_accum_x += (dx * mouse.mickeysPerPixel_x);
    ...
}
...
if (mouse.x > mouse.max_x) mouse.x = mouse.max_x;          // afterwards, position only
```

The fix copies that split: `Int33hHandler.RawMotionMode` + `AddRawMotion(dx, dy)`, fed by the
host with raw motion **while the mouse is captured**, accumulating pixel fractions (DOSBox's
`mickey_accum`) so slow movement with a 3x window is not lost to truncation.
`PAKU_MOUSE_ABS=1` restores the old model.

## What it measures

In mode 13h (320x200):

| case | checks |
|------|--------|
| 1 | the cursor **does** stop at the edge (the position is still clipped) |
| 2 | mickeys keep counting against the edge: 100 pushes of 3 px -> 600 mickeys |
| 3 | **non-regression control**: without `RawMotionMode`, the path for games that read `AH=03` gives exactly the same mickeys as before |

Case 2 has no RED version against the old code: the old model could not express "the mouse
moved but the cursor did not", which is precisely what was missing.

## How to run (from the repo root)

```powershell
dotnet run -c Release --project tools\mouse_raw_test
```

## Measured result

`VERDE=3 ROJO=0` — cursor pinned at 639, 600 mickeys accumulated, position mode untouched.
What is verified here is that the mickey/position split now matches the real driver; whether
it fully explains the playtest report is confirmed only by playing the game.
