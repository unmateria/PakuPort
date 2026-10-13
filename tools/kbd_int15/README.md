# Keyboard hook probe `INT 15h AH=4Fh`: does the BIOS offer keys to the driver?

An objective net for a class of failure **invisible in every keyboard trace**: the game does not
read port `0x60`, does not call `INT 16h`, does not touch the BDA... and still expects keys.

## Why it exists

A manual playtest of a 3D game reported that everything worked except the keys. Measured in
PakuPort with the game running:

| measurement | result |
|---|---|
| own reads of port `0x60` | **0** (the only scancode read is the BIOS's) |
| `INT 16h` calls in-game | **0** (all calls came from the startup menus) |
| writes to the BDA buffer head (`PAKU_MEMWATCH=41A`) | **0** — nobody consumes keys |
| writes to the `INT 9` vector (`PAKU_MEMWATCH=24`) | **0** — IRQ1 is not stolen |

A dump of its live memory (`PAKU_MEMDUMP`) explained it: `INT 15h` pointed into the game's own
keyboard driver while `INT 09h` still pointed at the BIOS stub, and the driver had the hook
signature:

```asm
test byte cs:[flag],0FFh
je   ...
cmp  ah,4Fh          ; <-- keyboard intercept
je   ...
jmp  far cs:[prev]   ; chain to the previous INT 15h
```

On the PC/AT the BIOS offers **every scancode** to `INT 15h` with `AH=4Fh` and `CF=1` before
translating it; the driver may return another scancode in `AL` or drop it with `CF=0`. It is the
standard hook point for code that does not want to steal `INT 9`. PakuPort never called it, so
any game that installs its keyboard there goes **deaf**.

## The fix

`BiosInterrupts.HandleInt09` calls `INT 15h` with `AH=4Fh`, `AL=scancode`, `CF=1` **before**
translating anything, and obeys the answer: `CF=0` drops the key, and the returned `AL` is the
scancode put in the buffer. Ported from DOSBox-X `src/cpu/callback.cpp`, `case CB_IRQ1` (the real
`INT 9` body):

```
push ax / in al,60h / mov ah,4Fh / stc / int 15h / jnc skip / <BIOS handler> / skip: ...
```

The jump opcode is `73h` = **JNC**: with `CF=0` the BIOS skips its own handler. Only `AX` and
`FLAGS` are saved, which is all the real BIOS preserves.

## What `KBD15.COM` measures

439 bytes, hand-assembled, checked with capstone. It installs its own `INT 15h` and runs three
phases with **one press of `z` each** (scancode `2Ch`):

| phase | driver answers | expected in the BDA |
|---|---|---|
| 1 "pass" | `CF=1`, `AL` untouched | `2C7A` — the key as is |
| 2 "drop" | `CF=0` | *nothing* — the key disappears |
| 3 "translate" | `AL=1Eh`, `CF=1` | `1E61` — an `a`, not the pressed key |

Phases 2 and 3 prevent a half implementation: calling the driver and ignoring its answer would
pass phase 1 and still be wrong. The returned `CF` is set by modifying the stacked FLAGS and
leaving with `IRET` (the idiom real drivers use), not with `stc; retf 2`.

Everything is measured on the BDA buffer (`0040:001A` head / `001C` tail) read directly, **not
through `INT 16h`**, so the judge sees what the emulated BIOS does and not the parallel path by
which the port host also injects each key into its private `INT 16h` buffer.

Output: `KBD15.BIN`, 15 bytes (3 phases x `cnt` word + `AL` byte + BDA entry word).

## How to run (from the repo root)

```powershell
python tools\kbd_int15\gen_kbd15.py
dotnet run -c Release --project src\PakuPort.Cli -- port tools\kbd_int15\KBD15.COM --no-package --no-smoke
copy tools\kbd_int15\KBD15.COM export\kbd15\
cd export\kbd15
$env:PAKU_NOSPLASH='1'; $env:PAKU_AUTOEXIT_MS='60000'
$env:PAKU_AUTOKEYS='~~~z~~~~~~~~~~z~~~~~~~~~~z~~~~~~'
dotnet run -c Release --no-build
cd ..\..
python tools\kbd_int15\judge_kbd15.py export\kbd15\KBD15.BIN
```

The probe does `sti` at start: in the port a `.COM` starts with `IF=0`, and without interrupts
there is neither IRQ1 nor the BDA tick used for the timeout.

## Measured result

| | p1 called | p1 scancode | p1 BDA | p2 drops | p3 translates |
|---|---|---|---|---|---|
| before | `cnt=0` **RED** | `AL=00` **RED** | `2C7A` (sane control) | — | `0000` **RED** |
| after | `cnt=1` **GREEN** | `AL=2C` **GREEN** | `2C7A` **GREEN** | `0000` **GREEN** | `1E61` **GREEN** |

`RESUMEN VERDE=7 ROJO=0`. With the hook in place, the game that exposed it responds to keys
in-game.
