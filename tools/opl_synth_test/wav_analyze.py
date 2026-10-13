#!/usr/bin/env python3
"""Analiza un WAV mono 16-bit: RMS global, RMS por ventanas (tramos con/sin senal),
duracion de senal y frecuencias dominantes por FFT en las ventanas con energia.
Uso: python wav_analyze.py <ruta.wav>
"""
import sys, struct, wave
import numpy as np

path = sys.argv[1] if len(sys.argv) > 1 else "logs/opl.wav"
w = wave.open(path, "rb")
nch, sw, rate, nframes = w.getnchannels(), w.getsampwidth(), w.getframerate(), w.getnframes()
raw = w.readframes(nframes)
w.close()
x = np.frombuffer(raw, dtype="<i2").astype(np.float64)
if nch > 1:
    x = x.reshape(-1, nch).mean(axis=1)

dur = len(x) / rate
rms = np.sqrt(np.mean(x * x)) if len(x) else 0.0
peak = np.max(np.abs(x)) if len(x) else 0.0
print(f"file={path} rate={rate} ch={nch} frames={len(x)} dur={dur:.2f}s")
print(f"RMS_global={rms:.1f} peak={peak:.0f} (int16)")

# ventanas de 100 ms; una ventana "con senal" si su RMS supera un umbral
win = int(rate * 0.1)
thr = 60.0  # umbral RMS por ventana (int16); ~ -55 dBFS
nwin = len(x) // win
active = 0
active_ms = 0.0
peaks_hz = []
for i in range(nwin):
    seg = x[i * win:(i + 1) * win]
    wr = np.sqrt(np.mean(seg * seg))
    if wr > thr:
        active += 1
        active_ms += 100.0
        # FFT de la ventana para su frecuencia dominante
        sp = np.abs(np.fft.rfft(seg * np.hanning(len(seg))))
        fr = np.fft.rfftfreq(len(seg), 1.0 / rate)
        # ignora DC/subgraves
        mask = fr > 40
        if sp[mask].size:
            peaks_hz.append(fr[mask][np.argmax(sp[mask])])

print(f"ventanas={nwin} activas(RMS>{thr:.0f})={active} ({100.0*active/max(nwin,1):.1f}%) senal~={active_ms/1000:.1f}s")

if peaks_hz:
    ph = np.array(peaks_hz)
    # histograma de frecuencias dominantes por ventana
    vals, counts = np.unique(np.round(ph / 10) * 10, return_counts=True)
    order = np.argsort(-counts)
    print("frecuencias dominantes por ventana (Hz aprox : nº ventanas):")
    for j in order[:8]:
        print(f"  {vals[j]:.0f} Hz : {counts[j]}")

# FFT global de los tramos activos concatenados para el espectro agregado
if active:
    print("(RESULT: SIGNAL PRESENT)" if rms > thr else "(RESULT: WEAK)")
else:
    print("(RESULT: SILENT — el juego no programo el OPL en este tramo)")
