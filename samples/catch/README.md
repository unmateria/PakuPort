# CATCH — the sample game

A tiny, original MS-DOS game written for PakuPort's tutorial and self-test. Stars fall; move the
paddle with the arrow keys to catch them; three misses and the game is over; ESC quits. It
starts in **demo mode** (an autopilot plays) so it can be recorded with nobody at the keyboard;
the first arrow key hands control to the player.

| folder | contents |
|---|---|
| `dos/CATCH.COM` | the game, 1,092 bytes. Port this. It is alone in its folder on purpose: `pakuport port` copies the executable's whole folder as the game's data. |
| `src/` | its source: a .NET program that assembles it with Iced's code assembler, so building it needs only the .NET SDK. Every routine is a near procedure ending in `ret`. |
| `solutions/` | reference translations of three of its functions, used by the tutorial and the self-test |
| `hd-art/make_hd_art.py` | stands in for an artist in the HD-art part of the tutorial |
| `selftest-pin.json` | the memory and screen hashes the self-test expects |

Rebuild the binary (the self-test checks it comes out byte-identical):

```powershell
dotnet run --project samples\catch\src -- samples\catch\dos\CATCH.COM
```

## Its functions

Addresses as they appear in the remake board (`FUN_1000_<offset>`):

| offset | routine | layer | |
|---|---|---|---|
| 010B | main loop | logic | not a function: recorded as one *loop iteration* unit |
| 0149 | `screen_offset` | logic | `DI = y*320 + x` |
| 0155 | `blit` | render | copy a sprite with `rep movsb`, row by row |
| 0170 | `fill` | render | clear a rectangle with `rep stosb` |
| 0189 | `random` | logic | `seed = seed*25173 + 13849` |
| 019A | `check_catch` | logic | does the star overlap the paddle? |
| 01B9 | `to_decimal` | logic | score → four ASCII digits |
| 01D2 | `print_at` | render | BIOS text (INT 10h AH=02h/0Eh) |
| 01E4 | `draw_hud` | render | score and lives |
| 0215 / 0228 | `sound_on` / `sound_off` | audio | PC speaker through PIT channel 2 |
| 022F | `tick_sound` | logic | count a beep down |
| 0240 | `wait_vsync` | render | wait for the vertical retrace (port 3DAh) |
| 024E | `read_input` | input | INT 16h; arrows move, ESC quits |
| 0289 | `autopilot` | logic | demo mode |
| 02C2 | `update_star` | logic | fall, score or lose a life |
| 031D | `respawn_star` | logic | back to the top at a random column |
| 0332 | `draw_frame` | logic | erase and redraw both sprites (calls `fill`, `blit`) |
| 039B | `set_palette` | render | DAC entries 32–35 through ports 3C8h/3C9h |
| 03AD | `new_game` | logic | reset and draw the scene |

`clamp_paddle` (reached by jumps, never called) and `game_over` (reached only when the autopilot
loses) do not appear in a short demo recording — which is exactly what the frontier and the
board report.
