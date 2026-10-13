# Mouse reset centring probe: which units does the game think the driver speaks?

An objective net for a silent class of failure: the mouse driver answers every call correctly,
but the game **infers the wrong scale** and from then on halves everything it receives.

## Why it exists

A manual playtest of a mode-13h game reported that the mouse cursor "does not reach the right
side of the screen". The cause is in the game's own executable (bytes verified): right after
the driver reset it **autodetects** the scale by reading the cursor position:

```asm
mov word [flag],0     ; assume a pixel-unit driver
mov ax,3
int 33h               ; -> CX = X position after the reset
cmp cx,00A0h          ; 160 = centre of a 320-wide screen
je  is_1x
mov word [flag],1     ; not 160 -> the driver speaks virtual-640
```

With the flag set the game does `shl cx,1` when setting range/position and `shr` when reading
them. PakuPort's reset left the cursor at `0,0`: the game read 0, concluded "virtual-640" and
halved everything — **the cursor only covered half of the screen**.

## The fix (both parts are needed)

1. **The reset centres the cursor**, like a real driver — DOSBox-X `src/ints/mouse.cpp`,
   `Mouse_Reset`:
   ```c
   mouse.x = static_cast<float>((mouse.max_x + 1)/ 2);
   mouse.y = static_cast<float>((mouse.max_y + 1)/ 2);
   ```
   PakuPort stores the position in game pixels and applies the virtual contract in `ExposeX`,
   so the centre is computed on the physical screen, and `AX=0000`/`AX=0021` also redo the
   range for the current mode (`Mouse_Reset` calls `Mouse_AfterNewVideoMode`).
2. **`mouseXScale: 2` in that game's `games/<id>/game.json`**: the real Microsoft driver reports
   X in 0..639 also in 320-wide modes. The automatic policy gives scale 1 to memimage worlds and
   is left alone because the other games it was tested with were validated on it; the per-game
   knob exists for this case.

Without (1) the game infers the wrong scale; without (2) it infers it right but the driver
speaks another one.

## What `MCEN.COM` measures

94 bytes, hand-assembled, checked with capstone. In **mode 13h** it writes `MCEN.BIN` (10 bytes):

| offset | content |
|--------|---------|
| +0 | `max_x` declared by the driver (`AX=0026`) |
| +2 | `max_y` |
| +4 | **X position after the reset** — the deciding value |
| +6 | Y position after the reset |
| +8 | `AX` from the reset (control: `FFFF` = driver installed) |

The judge compares against the **oracle ground truth** (`MCEN.ORACULO.BIN`, the same binary run
in DOSBox-X) and also enforces the explicit `(max+1)/2` rule.

## How to run (from the repo root)

```powershell
python tools\mouse_center_test\gen_mcen.py
# ground truth (only if the .COM changes):
#   dosbox-x -conf tools\mouse_center_test\oracle.conf   -> copy MCEN.BIN to MCEN.ORACULO.BIN
dotnet run -c Release --project src\PakuPort.Cli -- port tools\mouse_center_test\MCEN.COM --no-package --no-smoke
cd export\mcen; $env:PAKU_AUTOEXIT_MS='6000'; $env:PAKU_NOSPLASH='1'; dotnet run -c Release --no-build; cd ..\..
python tools\mouse_center_test\judge_mcen.py export\mcen\MCEN.BIN
```

## Measured result

| | range | position after reset |
|---|---|---|
| oracle | (0,0)-(639,199) | **320,100** |
| before | (0,0)-(639,199) | 0,0 **RED** |
| after | (0,0)-(639,199) | **320,100 GREEN** (byte-exact) |
