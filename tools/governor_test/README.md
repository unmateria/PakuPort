# Governor judge: a live speed change must not freeze the game

An objective net for a hang found in a manual playtest: several games froze when the speed was
changed live with Ctrl+F1 or Ctrl+F2.

## Why it exists

The governor (`MachineTimer.Poll`) throttles the VM by comparing executed Steps against those
due at `N` cycles/ms since an **anchor**:

```csharp
aheadSteps = (Cpu.TickCount - _cycleAnchorSteps) - nowMs * c.CyclesPerMs;
while (aheadSteps > c.CyclesPerMs) { Thread.Sleep(1); ... }
```

That sum measures accumulated debt and **is only valid at constant speed**. Lowering the speed
without re-anchoring re-evaluates the whole past with the new number: after two minutes at 3000,
switching to 100 makes the governor believe it is an hour of work ahead, and it sleeps until real
time catches up. From the outside it is a hang.

The fix is `SyncGovernorAnchor`: re-anchor as soon as `CyclesPerMs` changes, including the
check **inside** the wait loop (the key can be pressed while the game thread is already asleep
there).

## What it measures

No game involved, on the real code path:

| case | checks | why |
|------|--------|-----|
| 1 | the Poll after the change does not block (< 100 ms) | that is the hang |
| 2 | after the change, running ahead still sleeps ~300 ms | a net that only asked "do not block" would pass a disabled governor |

## How to run (from the repo root)

```powershell
dotnet run -c Release --project tools\governor_test
```

## Measured result

400 ms of play at 3000, then down to 100 (the sharpest step available):

| | case 1 | case 2 |
|---|---|---|
| before | **11,627 ms blocked** (RED) | 291 ms (green) |
| after | **0.0 ms** (GREEN) | 293 ms (GREEN) |

The 11.6 s are 400 ms x (3000/100 - 1). The freeze grows with play time: five minutes at 3000
before dropping to 100 is about **2.4 hours**.
