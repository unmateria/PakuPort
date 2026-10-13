# DAC sweep — objective net for the default palette left by the BIOS

Compares, byte by byte against the oracle (stock DOSBox-X), the palette the video BIOS loads
into the DAC after a set-mode. `DAC.COM` is a hand-assembled probe that runs unchanged in both
worlds.

## Why it exists

Found as a side effect while building another probe: mode 13h loaded only **16 of the 256**
DAC entries and left the rest at zero, so every colour index >= 16 rendered black even though
the game wrote the pixel.

The reference is the **measured behaviour**, not a re-typed algorithm: reproducing the BIOS
HSV palette from memory would be inventing it. `DAC.ORACULO.BIN` was captured by running
`DAC.COM` in DOSBox-X, and the C# table is *generated* from that file
(`gen_palette_cs.py`, with `--check` to verify it).

## What it measures

`DAC.BIN` = 3072 bytes = 4 modes x 768 (256 entries x R,G,B, 6 bits each), in the order of
`MODES` in `gen_dac.py`: **03h, 0Dh, 12h, 13h**. Offset of mode n = n x 768. For each mode the
COM sets it, writes 0 to the read index (3C7h) and does 768 `IN` from 3C9h (auto-increment).

| mode | BIOS path | status |
|---|---|---|
| 13h | 256-colour palette (16 EGA + 16 greys + 216 HSV + 8 black) | **closed**, 768/768 |
| 0Dh | 64-entry EGA palette | already matched, 768/768 |
| 03h, 12h | 64-entry EGA palette | **open**: 68 bytes / 49 entries per mode |

### The EGA-64 gap (03h/12h), left open on purpose

`LoadDefaultVgaPalette` builds the 64 entries from a 16-entry table with
`ega[(i&7) | ((i&0x10)>>1)]`, which differs from the hardware in 49 entries (the oracle gives
`6 -> (42,42,0)` where PakuPort gives `(42,21,0)`, and `8 -> (0,0,21)` where it gives
`(0,0,0)`). It is not a standalone table: `Int10hHandler` maps `AttrRegs[i+8] = i+0x10`
*because* of how that table is built, so fixing one without the other would change the text-mode
colours of every game. Closing it properly needs modelling the real attribute->DAC path
(P54S / Color Select) plus a visual check. Until then the judge keeps it from getting worse.

## How to run (from the repository root)

```powershell
python tools\dac_sweep\gen_dac.py                  # optional: DAC.COM (11008 B) is checked in
# oracle (run minimised); the COM writes DAC.BIN next to itself
dosbox-x -conf tools\dac_sweep\oracle.conf
Move-Item tools\dac_sweep\DAC.BIN tools\dac_sweep\DAC.ORACULO.BIN -Force

# PakuPort
dotnet run -c Release --project src\PakuPort.Cli -- port tools\dac_sweep\DAC.COM --no-package --no-smoke
cd export\dac
$env:PAKU_INTERP_ONLY="1"; $env:PAKU_NOSPLASH="1"; $env:PAKU_AUTOEXIT_MS="8000"
dotnet run -c Release --no-build
copy DAC.BIN ..\..\tools\dac_sweep\DAC.PAKU.BIN
cd ..\..
python tools\dac_sweep\judge_dac.py -v             # entry by entry
python tools\dac_sweep\judge_dac.py --baseline     # re-freeze the yardstick
```

`DAC.ORACULO.BIN` is ground truth: do not regenerate it unless the probe changes (then the C#
table and the baseline must be redone too).

## Judge and measured result

Modes 0Dh and 13h must be at zero mismatches; 03h and 12h are checked against `baseline.json`
and fail only if they get worse. Measured: mode 13h went from 16/256 entries loaded to
**768/768 byte-exact**; baseline = 136 bad bytes of 3072 (68 in 03h, 68 in 12h, 0 elsewhere).
