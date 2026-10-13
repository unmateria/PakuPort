# CRTC sweep — objective net for the CRTC state left by the video BIOS

Four hand-assembled `.COM` probes. Each one runs **unchanged** in the oracle (stock DOSBox-X)
and in PakuPort, and a judge compares the two results.

| probe | generator | judge | result channel |
|---|---|---|---|
| `CRTC.COM` — CRTC registers after every `INT 10h AH=00` | `gen_crtc.py` | `judge_crtc.py` | file `CRTC.BIN` (390 B) |
| `LOCK.COM` — the CRTC write-protect bit (r11 bit 7) | `gen_lock.py` | `judge_lock.py` | file `LOCK.BIN` (35 B) |
| `CGAPAL.COM` — Attribute Controller + DAC in CGA modes on EGA/VGA | `gen_cgapal.py` | `judge_cgapal.py` | file `CGAPAL.BIN` (192 B) |
| `SA13.COM` — start address honoured by the mode 13h renderer | `gen_sa13.py` | `judge_sa13.py` | screen capture |

`*.ORACULO.*` files are ground truth captured in DOSBox-X and stay in the repo; `*.PAKU.*`
are the matching PakuPort captures. Do not regenerate the oracle files unless the probe
changes (and then re-freeze the judge baseline too).

## Why it exists

The emulated set-mode programmed the Graphics Controller, Sequencer and Misc Output but never
touched `CrtcRegs`, which therefore stayed at zero unless the game wrote them. DOSBox-X computes
them from the mode table (`int10_modes.cpp`, `/* Program CRTC */`). A game that does a
read-modify-write of a CRTC register (the normal way to change a few bits) read **zero**
instead of the BIOS value and wrote garbage back. The sweep was written first, as the yardstick,
and the mode tables were ported afterwards.

## CRTC.COM

For each of 15 modes (`00 01 02 03 04 05 06 07 0D 0E 0F 10 11 12 13`) it calls
`INT 10h AH=00`, reads Misc Output (`in 3CCh`) and the 25 CRTC registers r00..r18
(`out base,i` / `in base+1`, read-only — the data port is never written). The CRTC base is
baked per mode: 3B4h for the mono modes 07h and 0Fh, 3D4h otherwise (DOSBox-X moves the CRTC
before programming it). Layout: 15 records x 26 bytes (`n*26`; bytes 0..24 = r00..r18,
byte 25 = Misc Output); `modes.json` gives the offsets. Stores use a CS prefix so a handler that
changes DS cannot corrupt them; the result buffer is zero-filled inside the COM itself.

Judge (`judge_crtc.py`, exit code 1 = RED): missing/mis-sized capture, no `baseline.json`, a drop
in global or per-mode matches, a `(mode, register)` pair that used to match and no longer does,
a changed oracle md5, or fewer modes with a correct Misc Output. `--baseline` re-freezes,
`-v` prints register by register.

**Measured:** 102/375 registers matched before the port (72.8 % gap; everything that matched was
a register the BIOS leaves at zero, and Misc Output did not clear bit 0 in modes 07h/0Fh).
After porting the tables: **375/375**, Misc Output 15/15. The judge is now pure anti-regression.

## LOCK.COM

DOSBox-X leaves r11 with bit 7 set (`8E`/`8C`/`85`) and then ignores writes to r00..r07
(`vga_crtc.cpp`, `if (crtc(read_only)) break;`). Porting the tables without the lock would have
accepted writes that real hardware drops — and r07 (overflow) feeds the active height and line
compare. The final-state sweep cannot see that, hence this probe. `LOCK.BIN` = r00..r18 after
set-mode (25) + r00..r07 re-read after writing them with the lock on (8) + r00 written with the
lock off (1) + r11 re-read after locking again (1). The judge requires byte equality between
worlds **and** the semantic assertions in each world separately.
**Measured:** 35/35, GREEN.

## CGAPAL.COM

On EGA/VGA, modes 4/5 are not a fixed-colour CGA: the 2-bit pixel goes through the Attribute
Controller and the DAC, so the four colours can be redefined — and a CGA title screen in one
test game did redefine them (the shapes were right, the colours were cyan/magenta/white where the
oracle showed blue/red/grey). Before changing the renderer this probe checks that the palette is
*stored* like the hardware does. Three phases, 64 B each (AC[0..15] + 64 DAC entries x 3):
after `AX=0004`, after `AH=0Bh BH=01 BL=00`, after `AX=1000h BX=011Ch`. Semantic assertions:
AC 1..3 = 13h/15h/17h in phase 0 and 12h/14h/16h afterwards, and none points at a black DAC entry.
**Measured:** the checked-in PakuPort capture is byte-identical to the oracle (192/192).

## SA13.COM

The mode 13h renderer read the first plane linearly from offset 0, ignoring start address
(r0C/r0D), stride (r13) and line compare, while the planar path honoured them. None of the games
it was tested with exercised this, so no existing gate saw it. The COM (58 B) sets mode 13h,
fills A000 so row n has colour n, sets the start address to 100 (= 400 px = one row + 80, so a
scale error and an offset error fail differently) and ends in `jmp $`.

The result is what is *shown*, which a COM cannot read back, so it is compared as images:
the oracle capture `SA13.ORACULO.png` (640x400, checked in) against PakuPort's framebuffer dump
`SA13.PAKU.png` (320x200). Regenerating the oracle capture needs a 320x200 (or 2x) VGA screen
capture of DOSBox-X while `sa13.conf` is running. The judge reduces both to 6-bit colour (to
measure layout only) and requires, in each world, exactly one colour step per row at x=240,
plus pixel equality between worlds.
**Measured:** RED before the fix (row 0 entirely colour 0) -> GREEN, 64000/64000 pixels.

## How to run (from the repository root)

```powershell
python tools\crtc_sweep\gen_crtc.py          # optional: the COMs are checked in
# oracle (run minimised); the COM writes CRTC.BIN next to itself
dosbox-x -conf tools\crtc_sweep\oracle.conf   # lock.conf / cgapal.conf / sa13.conf for the others
Move-Item tools\crtc_sweep\CRTC.BIN tools\crtc_sweep\CRTC.ORACULO.BIN -Force

# PakuPort
dotnet run -c Release --project src\PakuPort.Cli -- port tools\crtc_sweep\CRTC.COM --no-package --no-smoke
cd export\crtc
$env:PAKU_INTERP_ONLY="1"; $env:PAKU_NOSPLASH="1"; $env:PAKU_AUTOEXIT_MS="8000"
dotnet run -c Release --no-build
copy CRTC.BIN ..\..\tools\crtc_sweep\CRTC.PAKU.BIN
cd ..\..
python tools\crtc_sweep\judge_crtc.py -v
```

The same pattern applies to `LOCK`, `CGAPAL` and `SA13`. For `SA13` set
`$env:PAKU_FB_PNG="sa13_paku.png"` (6000 ms is enough) and copy it to
`tools\crtc_sweep\SA13.PAKU.png`. The exported program writes its result file in the export
directory (`export\<id>`). A CRTC run costs about 2,346 interpreted instructions.
