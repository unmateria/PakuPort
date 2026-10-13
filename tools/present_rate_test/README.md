# Presentation rate judge: does each frame reach the screen on time?

An objective net for a class of failure that **no screenshot shows**: every frame is correct,
but the *rhythm* at which frames are presented is not. On a static menu nothing is visible;
with a moving image it is perceived as flicker or stutter.

## Why it exists

A manual playtest of a 3D game reported sky flicker. First it had to be ruled out that the game
asked for something PakuPort did not provide; both measurements came out negative:

| measurement | result |
|---|---|
| reads of `0x3DA` in flight (heartbeat `retraceReads`) | **frozen**: the game does not wait for retrace |
| writes to the CRTC start address (`PAKU_LOG_CRTC=2`) | 2,771 writes, **all `r0D=00`**: no page flipping |

So the game draws freely and what the player sees depends **entirely** on PakuPort's
presentation rate. And that rate was broken:

```
[PRESENT] capturas=62 descartadas=8 intervalo ms: min=8.4 p50=14.3 p90=27.0 max=33.3
```

63 frames per second instead of 70, **~10 % dropped**, and a p90 at twice the period: one frame
in ten lasted as long as two.

## The cause

Nothing game-specific. `VideoState.CaptureFrontBuffer` had a throttle that merged "almost
simultaneous" captures by comparing `Environment.TickCount64`, whose resolution on Windows is
**~15.6 ms**, against a VGA period of **14.286 ms**. With the period SHORTER than the clock
granularity, two consecutive refreshes often fell in the same slot and the second was dropped —
periodically, which is what makes it visible. The same throttle had earlier hidden a finished
menu just before the `INT 16h` park (see the comment in `Int16hHandler`).

The fix removes it. The rhythm comes from the refresh tick, already periodic thanks to its own
real-time accumulator in `MachineTimer`, and there is nothing to coalesce: the park path only
captures in legacy mode.

## What `PRES.COM` measures

22 bytes, hand-assembled, checked with capstone. No game needed: it sets mode 13h and loops
writing to VRAM, so the refresh tick competes with real work. The judge reads the
`PAKU_LOG_PRESENT=1` trace and requires:

1. **~70 captures per second** (real VGA refresh),
2. **no frame at twice** the period,
3. **regular interval** (p90 close to nominal).

The measured p50 depends on the host monitor refresh (60 Hz gives 16.7 ms), so what is watched
is that no frame is lost or stretched.

## How to run (from the repo root)

```powershell
python tools\present_rate_test\gen_pres.py
dotnet run -c Release --project src\PakuPort.Cli -- port tools\present_rate_test\PRES.COM --no-package --no-smoke
python tools\present_rate_test\judge_present.py 8      # seconds to run the export
```

Note for re-measuring on a real game: a scene where nothing moves measures nothing (24
consecutive presented frames came out byte-identical). Some games also have their own
"eliminate flicker" option; that belongs to the game, not to PakuPort.

## Measured result

| | captures/s | dropped/s | p90 | max |
|---|---|---|---|---|
| before | 60.5 **RED** | 9-11 **RED** | 17.2 | 30.4 **RED** |
| after | 70.4 **GREEN** | 0 **GREEN** | 16.7 | 17.8 **GREEN** |
