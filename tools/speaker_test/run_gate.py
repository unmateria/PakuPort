#!/usr/bin/env python3
"""SPK gate: judge the PC-speaker square wave OBJECTIVELY (measured frequency, not by ear).

Builds SPK.COM (gen_spk.py), ports it with the product command, runs the export headless with
PAKU_WAV_DUMP, and measures the dumped WAV:

  GREEN  = a tone is present AND its measured frequency is within 2% of 1000.15 Hz
  RED    = silence (the pre-fix behaviour: Speaker.cs generated nothing) or wrong pitch

Frequency is measured by counting zero crossings over the loudest 200 ms window, which is
exact for a square wave and needs no external dependencies (no numpy/scipy).

PASO 5 (remakes IA) reuses this same run as the judge of the audio-event catalog: the run
carries PAKU_LOG_AUDIO_EV=1, so logs/audio-events.log must contain EXACTLY ONE spk event
whose sequence is a single 1000 Hz segment (+-1 Hz) — i.e. the fingerprinted event agrees
with the frequency measured from the WAV.
"""
import os
import struct
import subprocess
import sys
import wave

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
CLI = os.path.join(REPO, "src", "PakuPort.Cli")
COM = os.path.join(HERE, "SPK.COM")
WAV = os.path.join(HERE, "spk.wav")

EXPECTED_HZ = 1193181.666 / 0x04A9
TOLERANCE = 0.02


def run(cmd, cwd=None, env=None, timeout=600):
    e = dict(os.environ)
    e.update(env or {})
    return subprocess.run(cmd, cwd=cwd or REPO, env=e, capture_output=True,
                          text=True, errors="replace", timeout=timeout)


def measure(path):
    """Return (peak_amplitude, measured_hz) of the loudest 200 ms window."""
    with wave.open(path, "rb") as w:
        rate = w.getframerate()
        raw = w.readframes(w.getnframes())
    n = len(raw) // 2
    data = struct.unpack(f"<{n}h", raw[: n * 2])
    win = int(rate * 0.2)
    if n < win:
        return 0, 0.0
    best_start, best_energy = 0, -1
    for start in range(0, n - win, win):
        energy = sum(abs(s) for s in data[start:start + win])
        if energy > best_energy:
            best_energy, best_start = energy, start
    seg = data[best_start:best_start + win]
    peak = max(abs(s) for s in seg)
    if peak < 100:
        return peak, 0.0
    # zero crossings (rising only) over the window -> frequency
    crossings = sum(1 for i in range(1, len(seg)) if seg[i - 1] < 0 <= seg[i])
    return peak, crossings / 0.2


def main():
    run([sys.executable, os.path.join(HERE, "gen_spk.py")])
    if not os.path.exists(COM):
        print("SPK: RED - no se genero SPK.COM")
        return 1

    p = run(["dotnet", "run", "-c", "Release", "--project", CLI, "--",
             "port", COM, "--no-package", "--no-smoke"])
    if p.returncode != 0:
        print("SPK: RED - el port fallo\n" + p.stdout[-1500:])
        return 1

    out_dir = os.path.join(REPO, "export", "spk")
    if os.path.exists(WAV):
        os.remove(WAV)
    evlog = os.path.join(out_dir, "logs", "audio-events.log")
    if os.path.exists(evlog):
        os.remove(evlog)
    r = run(["dotnet", "run", "-c", "Release", "--no-build"], cwd=out_dir,
            env={"PAKU_AUTOEXIT_MS": "8000", "PAKU_NOSPLASH": "1", "PAKU_WAV_DUMP": WAV,
                 "PAKU_LOG_AUDIO_EV": "1"},
            timeout=300)
    if not os.path.exists(WAV):
        print("SPK: RED - no se volco WAV\n" + (r.stdout + r.stderr)[-1500:])
        return 1

    peak, hz = measure(WAV)
    err = abs(hz - EXPECTED_HZ) / EXPECTED_HZ if hz else 1.0
    ok = peak >= 100 and err <= TOLERANCE
    print(f"SPK: {'VERDE' if ok else 'ROJO'} - pico={peak} medido={hz:.1f} Hz "
          f"esperado={EXPECTED_HZ:.1f} Hz error={err*100:.2f}%")
    return (0 if ok else 1) | (0 if judge_events(evlog) else 1) | direct_case()


def direct_case():
    """Direct-mode (bit-banged bit 1, gate off) case: SPK2.COM under a 3000/ms governor
    must produce a 1000 Hz square wave in the WAV. RED pre-fix (Speaker.cs only modeled
    the gated PIT tone). Returns 0 on green, 1 on red (composable into the exit code)."""
    run([sys.executable, os.path.join(HERE, "gen_spk2.py")])
    com2 = os.path.join(HERE, "SPK2.COM")
    wav2 = os.path.join(HERE, "spk2.wav")
    if not os.path.exists(com2):
        print("SPK-DIR: ROJO - no se genero SPK2.COM")
        return 1
    p = run(["dotnet", "run", "-c", "Release", "--project", CLI, "--",
             "port", com2, "--no-package", "--no-smoke"])
    if p.returncode != 0:
        print("SPK-DIR: ROJO - el port fallo\n" + p.stdout[-1500:])
        return 1
    if os.path.exists(wav2):
        os.remove(wav2)
    r = run(["dotnet", "run", "-c", "Release", "--no-build"],
            cwd=os.path.join(REPO, "export", "spk2"),
            env={"PAKU_AUTOEXIT_MS": "8000", "PAKU_NOSPLASH": "1", "PAKU_WAV_DUMP": wav2,
                 "PAKU_CYCLES": "3000", "PAKU_INTERP_ONLY": "1"},
            timeout=300)
    if not os.path.exists(wav2):
        print("SPK-DIR: ROJO - no se volco WAV\n" + (r.stdout + r.stderr)[-1500:])
        return 1
    peak, hz = measure(wav2)
    expected = 1000.0
    err = abs(hz - expected) / expected if hz else 1.0
    ok = peak >= 100 and err <= TOLERANCE
    print(f"SPK-DIR: {'VERDE' if ok else 'ROJO'} - pico={peak} medido={hz:.1f} Hz "
          f"esperado={expected:.1f} Hz error={err*100:.2f}%")
    return 0 if ok else 1


def judge_events(path):
    """PASO 5 judge: exactly one spk event, one segment, 1000 +-1 Hz."""
    if not os.path.exists(path):
        print("SPK-EV: ROJO - no se genero audio-events.log")
        return False
    evs = [l.strip() for l in open(path, encoding="utf-8", errors="replace")
           if " src=spk " in l]
    if len(evs) != 1:
        print(f"SPK-EV: ROJO - {len(evs)} eventos spk (esperado 1)")
        for l in evs[:5]:
            print("   " + l)
        return False
    ev = dict(t.split("=", 1) for t in evs[0].split(" ") if "=" in t)
    seq = ev.get("seq", "")
    parts = seq.split(",")
    hzs = []
    for p in parts:
        try:
            hzs.append(int(p.split(":")[0]))
        except ValueError:
            pass
    ok = len(hzs) == 1 and abs(hzs[0] - round(EXPECTED_HZ)) <= 1
    print(f"SPK-EV: {'VERDE' if ok else 'ROJO'} - 1 evento spk fnv={ev.get('fnv')} "
          f"n={ev.get('n')} ms={ev.get('ms')} pc={ev.get('pc')} seq={seq}")
    return ok


if __name__ == "__main__":
    sys.exit(main())
