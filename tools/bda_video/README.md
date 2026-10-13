# Video BDA — the BIOS lied about which mode the machine was in

Objective net for a gap that had been there from the start: **nobody** wrote the video fields
of the BIOS Data Area.

## Why it exists

`INT 10h AH=00` changed the emulator's internal state (`VideoState`) and left the BDA with the
power-on values. Measured on a real-mode EGA game, with the game already drawing its logo in
mode `0Dh`:

| | PakuPort | oracle |
|---|---|---|
| `40:49` video mode | `03` | `0D` |
| `40:4A` columns | `50` | `28` |
| `40:4D` page size (hi) | `00` | `20` |
| `40:60/61` cursor scan lines | `00`/`00` | `07`/`06` |
| `40:65` CRT mode control | `00` | `3E` |
| `40:84` rows - 1 | `00` | `18` |
| `40:85` character height | `00` | `08` |
| `40:87` EGA flags | `00` | `60` |

Nine of eighteen fields. Any game asking the BIOS which mode it is in, how many rows there are
or how tall the font is — through `AH=0Fh`, `AH=11h AL=30h`, or reading `0040:0049` directly,
which is very common — got a false answer.

It surfaced while chasing a different gap (CGA modes on EGA): applying the Attribute Controller
table to the EGA modes turned that game's logo turquoise. The colour of those registers derives
from `40:66`, and the whole BDA was unwritten. With the BDA fixed the logo does not change a
single pixel and is identical to the oracle (64000/64000), which also closed the CGA-on-EGA gap.

## The fix it guards

`SetupVideoBda` in set-mode, with the `tw`/`th`/`ch`/`plength` columns of `ModeList_VGA`
(DOSBox-X `int10_modes.cpp`) and the `FinishSetMode` sequence: mode, columns, page size and
start, active page, CRTC port (`3B4h` in mono modes 7 and `0Fh`), rows-1, character height,
flags (`0x60`, plus bit 7 if the caller asked not to clear), the 8 page cursors set to zero, and
cursor shape only in text modes.

`40:65` and `40:66` are **only rewritten in the CGA-compatible modes (0-7)**; in EGA/VGA modes
the value of the last of those persists. This was measured, not deduced: the probe repeats
mode 3 at the end and the oracle restores `29h`/`30h`. So a game that goes through mode 6 and
then jumps to `0Dh` leaves `3Eh`/`3Fh`.

## What BDAV.COM measures

607 bytes, hand-assembled (disassembly verified with capstone if installed). For each mode in
the list (03, 04, 06, 0D, 0E, 10, 12, 13 and 03 again) it sets the mode and copies the video
fields of the BDA into its buffer; at the end it writes `BDAV.BIN` (34 bytes per mode:
`0040:0049..0066` and `0040:0084..0087`). Ground truth is `BDAV.ORACULO.BIN`, captured with the
**same** binary in DOSBox-X. The judge ignores `40:50..40:5F` (the cursor positions of the 8
pages): positional noise, not mode configuration.

## How to run (from the repository root)

```powershell
python tools\bda_video\gen_bdav.py
dotnet run -c Release --project src\PakuPort.Cli -- port tools\bda_video\BDAV.COM --no-package --no-smoke
cd export\bdav
$env:PAKU_NOSPLASH='1'; $env:PAKU_AUTOEXIT_MS='10000'
dotnet run -c Release --no-build
cd ..\..
python tools\bda_video\judge_bdav.py export\bdav\BDAV.BIN tools\bda_video\BDAV.ORACULO.BIN
```

To re-capture the ground truth: `dosbox-x -conf tools\bda_video\oracle.conf` runs the COM and
leaves `BDAV.BIN` in this folder; rename it to `BDAV.ORACULO.BIN`.

## Measured result

`RESUMEN campos distintos = 67` -> **`0`**, across the nine steps of the probe.
