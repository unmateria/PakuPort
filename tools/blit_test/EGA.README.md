# EGA.COM — synthetic net for the EGA planar decoder

142 bytes written by hand and verified with `capstone` in 16-bit mode (the disassembly matches
the listing below byte for byte).

It sets mode **0Dh** (EGA 320x200, 16 colours, planar, A000h, 40 bytes per line) and paints
**two** figures with a known, asymmetric pattern, one per write path the telemetry has to cover:

| figure | how it is written | why |
|---|---|---|
| 1 | `mov es:[di],al` byte by byte | the "loose write" path (games that draw with plain `mov`) -> `blitraw.bin` |
| 2 | `rep movsb` | the string-op path; in planar modes the `blit.log` rectangle is **not** enough (the source byte is not the pixel), so it must also show up in `blitraw.bin` |

Both use **write mode 0** with the **map mask** (Sequencer index 2, port 3C4/3C5) set to a single
plane per pass: `1, 2, 4, 8`. With `EnableSetReset=0`, `BitMask=0xFF`, `DataRotate=0` and
`LogicOp=0` (the state the BIOS set-mode leaves, see `Int10hHandler.SetModeRegisters`), the CPU
byte lands **as is** in the enabled plane and the other three stay intact, so the expected result
is exactly the data table. Set-mode clears the four planes, so the background is 0.

## Geometry

- Figure 1: `di = 0x00F3 = 243 = 6*40 + 3` -> row 6, byte column 3, **5 rows x 2 bytes**
  = 16x5 pixels at (x=24, y=6). Row step `add di,40`.
- Figure 2: `di = 0x032A = 810 = 20*40 + 10` -> row 20, byte column 10, **1 row x 4 bytes**
  = 32x1 pixels at (x=80, y=20).

They are more than one cell apart in both directions so the connected-components clustering
(8-neighbourhood over byte cells) returns **two distinct assets**.

## Listing (org 100h)

```
0100  FC                 cld
0101  B8 0D 00           mov ax,000Dh
0104  CD 10              int 10h                ; mode 0Dh: 320x200x16 planar
0106  B8 00 A0           mov ax,0A000h
0109  8E C0              mov es,ax
010B  BE 56 01           mov si,TABLA1          ; 0156h
010E  B3 01              mov bl,1               ; plane bit
                     P1LOOP:
0110  BA C4 03           mov dx,03C4h           ; Sequencer index
0113  B0 02              mov al,2               ; reg 2 = Map Mask
0115  EE                 out dx,al
0116  42                 inc dx                 ; 3C5 = data
0117  8A C3              mov al,bl
0119  EE                 out dx,al              ; only plane bl
011A  BF F3 00           mov di,00F3h           ; row 6, byte column 3
011D  B9 05 00           mov cx,5               ; 5 rows
                     ROWLOOP:
0120  8A 04              mov al,[si]
0122  26 88 05           mov es:[di],al
0125  46                 inc si
0126  8A 04              mov al,[si]
0128  26 88 45 01        mov es:[di+1],al
012C  46                 inc si
012D  83 C7 28           add di,40
0130  E2 EE              loop ROWLOOP
0132  D0 E3              shl bl,1
0134  80 FB 10           cmp bl,10h
0137  72 D7              jb P1LOOP
0139  B3 01              mov bl,1
                     P2LOOP:
013B  BA C4 03           mov dx,03C4h
013E  B0 02              mov al,2
0140  EE                 out dx,al
0141  42                 inc dx
0142  8A C3              mov al,bl
0144  EE                 out dx,al
0145  BF 2A 03           mov di,032Ah           ; row 20, byte column 10
0148  B9 04 00           mov cx,4
014B  F3 A4              rep movsb              ; DS:SI (TABLA2) -> ES:DI
014D  D0 E3              shl bl,1
014F  80 FB 10           cmp bl,10h
0152  72 E7              jb P2LOOP
0154  CD 20              int 20h
```

`si` advances by itself: it leaves figure 1 pointing exactly at TABLA2 (0156h + 40 = 017Eh).

## Tables (plane-major: plane 0, 1, 2, 3)

`TABLA1` at 0156h (file offset 0x56), 4 planes x 5 rows x 2 bytes = 40 bytes:

```
plane 0:  80 01   C0 03   E0 07   F0 0F   F8 1F
plane 1:  01 80   03 C0   07 E0   0F F0   1F F8
plane 2:  0F 0F   00 FF   FF 00   F0 F0   AA 55
plane 3:  00 00   11 22   44 88   33 CC   0F F0
```

`TABLA2` at 017Eh (file offset 0x7E), 4 planes x 4 bytes = 16 bytes:

```
plane 0:  81 42 24 18
plane 1:  18 24 42 81
plane 2:  F0 0F 33 CC
plane 3:  01 02 04 08
```

Colour index of a pixel = `p0 | p1<<1 | p2<<2 | p3<<3`, taking the matching bit of each plane,
with **bit 7 = leftmost pixel**. The values are chosen so an error in plane order, bit order or
row shows up immediately (e.g. the first row of figure 1 starts at index 1 and its second byte
starts at 2).

The judge `tools/blit_catalog/judge_ega.py` **reads these tables from EGA.COM itself** and computes
the expected pattern with that formula, so no magic numbers are duplicated.

## How to run (from the repository root)

```
python tools\blit_catalog\judge_ega.py
```

Full chain: headless capture of `export\ega` -> `build_catalog.py` -> PNG -> pixel-by-pixel
comparison. `judge_remake_ega.py` (this folder) checks that the HD instance registered by the
second figure lands on the right game pixel in a planar mode.
