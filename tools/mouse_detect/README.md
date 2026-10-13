# Mouse detection probe: does the game find our driver?

An objective net for a class of failure that **shows up in no driver trace**: the game decides
the machine has no mouse *before* calling INT 33h even once.

## Why it exists

A manual playtest of a mouse-only game reported that the mouse "does not work at all". With
`PAKU_LOG_INT33=1` the game made **zero** driver calls in 40 s. The reason is in the game's own
executable (bytes verified):

```asm
mov ax,3533h      ; read the INT 33h vector
int 21h           ; -> ES:BX
mov ax,es
or  ax,bx
jz  no_mouse      ; null vector
mov bl,es:[bx]
cmp bl,0CFh
jne has_mouse     ; <-- first byte 0xCF => "no driver installed"
```

This is the canonical check (Ralf Brown, INT 33h): **non-null vector AND first byte not `CF`**,
because a vector pointing at a bare IRET has no driver behind it. PakuPort put exactly `0xCF` in
its IVT stub (`F000:00nn`, one byte per vector), so any game that detects the mouse this way
concludes there is none. The driver itself answered the reset perfectly (`AX=FFFF`, 3 buttons);
nobody was asking it.

## The fix

**Two-byte** stubs (`NOP; IRET`) at `F000:1200 + n*2`, with the vectors pointing there
(`Cpu.IvtStubBase`). The `NOP` passes the check and the `IRET` returns as before. This is the
DOSBox model, whose stub is a callback followed by IRET. The historical one-byte area is still
written and dispatched, because some game ISRs jump to `F000:00nn` with a hard-coded address
instead of reading the vector.

## What `MDET.COM` measures

81 bytes, hand-assembled, checked with capstone. It writes `MDET.BIN` (9 bytes):

| offset | content |
|--------|---------|
| +0 | INT 33h vector segment |
| +2 | vector offset |
| +4 | **first byte it points to** — the deciding value |
| +5 | `AX` after the reset (`INT 33h AX=0000`) |
| +7 | `BX` after the reset (button count) |

The reset is the **control**: if it failed, the problem would be the driver, not detection.

## How to run (from the repo root)

```powershell
python tools\mouse_detect\gen_mdet.py
dotnet run -c Release --project src\PakuPort.Cli -- port tools\mouse_detect\MDET.COM --no-package --no-smoke
cd export\mdet; $env:PAKU_AUTOEXIT_MS='6000'; dotnet run -c Release --no-build; cd ..\..
python tools\mouse_detect\judge_mdet.py export\mdet\MDET.BIN
```

## Measured result

| | vector | first byte | reset |
|---|---|---|---|
| before | `F000:0033` | `CF` **RED** | `AX=FFFF BX=3` |
| after | `F000:1266` | `90` **GREEN** | `AX=FFFF BX=3` |
