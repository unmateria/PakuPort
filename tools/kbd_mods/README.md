# Modifier probe: do Shift, Ctrl and Alt reach the game?

An objective net for a hole in the port **host** that had been there from the start and that no
gate saw: modifier keys did not exist.

## Why it exists

The host template's `MapKey` table had no entry for Shift, Ctrl, Alt, CapsLock, NumLock or
ScrollLock. It returned `(0,0)` and the input loop's `if (sc != 0)` dropped them:

* a game reading raw scancodes from `0x60` — those that install their own `INT 9` ISR, which
  are many — **could not see those keys**: neither make nor break arrived;
* the BIOS `INT 9` never received `2Ah`/`1Dh`/`38h`, so the BDA keyboard flags (`0040:0017`)
  stayed at zero forever. `INT 16h AH=02` lied, and the BIOS itself did not apply Shift or Ctrl
  when putting the key in its buffer.

What hid the hole: the host computed the ASCII of capital letters on its own before queuing them
in its private `INT 16h` buffer. Everything else was dead.

## The fix

`MapKey` maps all six (`2A`/`36` shift, `1D` ctrl, `38` alt, `3A` caps, `45` num, `46` scroll)
with ASCII 0, like the `scan_to_scanascii` table in DOSBox-X `bios_keyboard.cpp`. They go **only
through the hardware path**: putting them in the `INT 16h` buffer would make a game waiting for a
key "eat" a Shift as a keypress. In the BIOS, `HandleInt09` already moved the shift/ctrl/alt bits
and now also toggles the three lock bits.

`PAKU_AUTOKEYS` gains pairs to **hold** a modifier: `%`/`&` Shift, `*`/`_` Ctrl, `=`/`|` Alt — so
`%a&` types Shift+A. They are explicit pairs rather than a prefix because what must be expressed
is exactly that: the modifier stays down while the keys in between are typed. And they go
through `MapKey`, **the same function as a real keypress**, so the harness measures the user's
path and not a shortcut of its own.

## What `MODS.COM` measures

411 bytes, hand-assembled, checked with capstone. It watches the keyboard from the
`INT 15h AH=4Fh` hook (already green, see `tools\kbd_int15`) and returns `CF=1`, so it **does
not steal `INT 9`**: the BIOS goes on as usual and what it does can be measured too. It records
the raw scancodes, the `0040:0017` byte at the moment of each non-modifier key (with the modifier
still held) and the entries the BIOS leaves in its buffer, read directly from the BDA. Output:
`MODS.BIN`, 34 bytes.

## How to run (from the repo root)

```powershell
python tools\kbd_mods\gen_mods.py
dotnet run -c Release --project src\PakuPort.Cli -- port tools\kbd_mods\MODS.COM --no-package --no-smoke
cd export\mods
$env:PAKU_NOSPLASH='1'; $env:PAKU_AUTOEXIT_MS='45000'
$env:PAKU_AUTOKEYS='~~~%~a~&~~~*~b~_~~~=~c~|~~~~'
dotnet run -c Release --no-build
cd ..\..
python tools\kbd_mods\judge_mods.py export\mods\MODS.BIN
```

## The second half: the BIOS table

With modifiers arriving, `Alt+C` still left a bare `c` where a real BIOS leaves `2E00`. The `INT 9`
only had **the `normal` column** of the keyboard table and improvised the rest (capitals with
`ToUpper`, Ctrl with `letter & 0x1F`): Alt did not exist, Ctrl on non-letters returned the
character unchanged, and function keys did not change with any modifier. Instead of patching
Alt, the **whole** `bios_keyboard.cpp` table (`normal`/`shift`/`control`/`alt`) was ported with
its exact selection:

* priority `alt` > `ctrl` > `shift` > `normal` (`bios_keyboard.cpp:824-835`);
* on the keypad the digit comes from the `shift` column chosen by **shift XOR numlock**
  (line 809, *"both means off"*), and `Alt` on the keypad delivers no key because the BIOS is
  accumulating an Alt+digits code there;
* `CapsLock` inverts shift **only on letters** (lines 840-849).

## Measured result

| | scancodes | flags 40:17 | BIOS buffer |
|---|---|---|---|
| before | `1E 9E 30 B0 2E AE` (letters only) | `20 20 20` (NumLock only) | `1E61` `3062` `2E63` |
| after | `2A 1E 9E AA · 1D 30 B0 9D · 38 2E AE B8` | `22 24 28` | `1E41` `3002` `2E00` |

`RESUMEN VERDE=0 ROJO=11` -> `VERDE=12 ROJO=0`. Note the buffer: before, the BIOS stored a
lower-case `a`, a bare `b` and a bare `c`; now it stores `A`, the `Ctrl+B` control character and
the `Alt+C` alt-scancode.
