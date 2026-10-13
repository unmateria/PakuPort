# DAC expansion (6 bits -> 8) — every game's colours came out dimmed

Objective net for a divergence that was not in the drawing but in the **colour**, and that
affected every game at once.

## Why it exists

The VGA DAC stores 6 bits per component. To widen them to the 8 bits of the framebuffer
PakuPort did `v << 2` (max **252**) while DOSBox-X does the equivalent of `(v * 255) / 63`
(max **255**): same palette index, `(0,0,168)` here and `(0,0,170)` there. The exact formula is
in DOSBox-X `src/hardware/vga_dac.cpp`, `dacexpand()`:

```c
(v << 2) | (v >> 4)      // abcdef -> abcdefab
```

`v << 2` alone is only right when the two top bits are zero, so **the error grows with
brightness**: 48 of the 64 possible values were wrong and pure white never reached 255.

The DAC sweep (`tools\dac_sweep`) cannot see this: it compares the 6 bits **read back through
the ports**, which are identical in both worlds. What diverges is the colour on screen, so only
the framebuffer can measure it.

## What DACX.COM measures

56 bytes, hand-assembled (disassembly verified with capstone if installed). It sets mode 13h,
loads a **64-step grey ramp** into the DAC (index `i` -> `r=g=b=i`, every possible 6-bit value)
and paints 64 vertical bands of 5 px. The judge samples the centre of each band in each world's
PNG. Ground truth is **double**, and both must hold:

1. the `dacexpand()` formula;
2. the **oracle** PNG (`DACX.ORACULO.png`, a 640x400 screen capture of DOSBox-X), in case the
   formula in the source were not what the machine really does.

## How to run (from the repository root)

```powershell
python tools\dac_expand\gen_dacx.py
dotnet run -c Release --project src\PakuPort.Cli -- port tools\dac_expand\DACX.COM --no-package --no-smoke
cd export\dacx
$env:PAKU_NOSPLASH='1'; $env:PAKU_AUTOEXIT_MS='8000'; $env:PAKU_FB_PNG='..\..\dacx.png'
dotnet run -c Release --no-build
cd ..\..
python tools\dac_expand\judge_dacx.py dacx.png tools\dac_expand\DACX.ORACULO.png
```

To re-capture the oracle: `dosbox-x -conf tools\dac_expand\oracle.conf` and take a screen
capture once the bands are shown.

## Measured result

| band (v) | before | expected | oracle |
|---|---|---|---|
| 16 | 64 | 65 | 65 |
| 31 | 124 | 125 | 125 |
| 42 | **168** | **170** | **170** |
| 62 | 248 | 251 | 251 |
| 63 | **252** | **255** | **255** |

`RESUMEN VERDE=1 ROJO=2` (48/64 bands wrong, white at 252) -> **`VERDE=4 ROJO=0`**, with all
64 bands **identical to the oracle**. `DACX.ANTES.png` keeps the capture of the red measurement.
