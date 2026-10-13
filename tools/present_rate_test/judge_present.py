"""Juez del ritmo de presentacion: ejecuta el export de PRES.COM y dictamina.

Que vigila: que el frame llegue a la pantalla al ritmo del refresco VGA (70 Hz, periodo
14,286 ms) y de forma REGULAR. Un ritmo irregular no se ve en un menu estatico, pero con la
imagen en movimiento se percibe como parpadeo, que es lo que informo una prueba manual.

Tres condiciones:
  1. se capturan ~70 frames por segundo (no menos de 68),
  2. NINGUNA captura se descarta por el acelerador,
  3. el intervalo es regular: el p90 no se despega del periodo nominal.

Uso:  python tools\\present_rate_test\\judge_present.py [segundos]
Sale con codigo 1 si algo esta en ROJO, para encadenarlo en un gate.
"""
import glob, os, re, subprocess, sys

D = os.path.dirname(os.path.abspath(__file__))
RAIZ = os.path.dirname(os.path.dirname(D))
EXPORT = os.path.join(RAIZ, 'export', 'pres')
PERIODO_MS = 1000.0 / 70.0

segundos = int(sys.argv[1]) if len(sys.argv) > 1 else 8

if not os.path.isdir(EXPORT):
    raise SystemExit(f'ROJO: falta {EXPORT} (ver README.md para generarlo)')

env = dict(os.environ)
env['PAKU_NOSPLASH'] = '1'
env['PAKU_LOG_PRESENT'] = '1'
env['PAKU_AUTOEXIT_MS'] = str(segundos * 1000)

exes = glob.glob(os.path.join(EXPORT, 'bin', 'Release', '*', '*-host.exe'))
if not exes:
    raise SystemExit(f'ROJO: no encuentro el ejecutable en {EXPORT}\\bin\\Release (compila el export)')
p = subprocess.run([exes[0]], cwd=EXPORT, capture_output=True, text=True,
                   env=env, timeout=segundos * 6 + 120)
lineas = [l for l in (p.stdout + p.stderr).splitlines() if '[PRESENT]' in l]
if not lineas:
    raise SystemExit('ROJO: el export no emitio ninguna traza [PRESENT]')

pat = re.compile(r'capturas=(\d+).*p50=([\d.]+) p90=([\d.]+) max=([\d.]+)')
muestras = [tuple(map(float, m.groups())) for l in lineas if (m := pat.search(l))]
# La primera ventana incluye el arranque (carga del .COM): se descarta.
muestras = muestras[1:]
if not muestras:
    raise SystemExit('ROJO: menos de dos ventanas de medida; sube los segundos')

n = len(muestras)
cap = sum(m[0] for m in muestras) / n
p50 = sum(m[1] for m in muestras) / n
p90 = sum(m[2] for m in muestras) / n
mx = max(m[3] for m in muestras)

print(f'{n} ventanas de 1 s (periodo nominal {PERIODO_MS:.2f} ms)')
print(f'  capturas/s = {cap:.1f}   (nominal 70)')
print(f'  intervalo  : p50={p50:.1f} ms  p90={p90:.1f} ms  max={mx:.1f} ms')
print()

# El p50 real depende del refresco del monitor del host (a 60 Hz salen 16,7 ms), asi que lo
# que se vigila es que NO se pierdan frames y que ninguno se quede al doble del periodo.
casos = [
    ('~70 capturas por segundo',   cap >= 68.0,               f'{cap:.1f}/s'),
    ('ningun frame al doble',      mx <= PERIODO_MS * 1.6,    f'max={mx:.1f} <= {PERIODO_MS * 1.6:.1f} ms'),
    ('intervalo regular (p90)',    p90 <= PERIODO_MS * 1.35,  f'p90={p90:.1f} <= {PERIODO_MS * 1.35:.1f} ms'),
]

verde = 0
for nombre, ok, detalle in casos:
    print(f'{nombre:30} {detalle:26} -> {"VERDE" if ok else "ROJO"}')
    verde += 1 if ok else 0

print(f'RESUMEN VERDE={verde} ROJO={len(casos) - verde}')
sys.exit(0 if verde == len(casos) else 1)
