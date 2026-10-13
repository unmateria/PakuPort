"""Juez de la sonda de velocidad: PakuPort contra el oraculo, con umbral.

Que vigila, y por que importa: que `cyclesPerMs=N` siga entregando las mismas
instrucciones/s que `cycles=N` en DOSBox-X con `core=normal`. Toda la calibracion de los
juegos SIN reloj propio descansa en esa equivalencia:
si el governor se descalibra, esos juegos cambian de velocidad en silencio y el sintoma que
ve el usuario -- "va disparado" o "va a rachas" -- se confunde facilmente con un bug de
emulacion. Ya paso: se gastaron tres diagnosticos (raton, game port, typematic) persiguiendo
un bug inexistente porque la referencia de velocidad estaba mal medida.

Verdad terreno: `SPEED.ORACULO.BIN`, capturado del oraculo real (la referencia es el
comportamiento MEDIDO, no un listado citado).

Uso: python tools\\speed_probe\\judge_speed.py [--umbral 3.0]
Sale con codigo 1 si la brecha supera el umbral, para poder encadenarlo en un gate.
"""
import os, struct, sys

D = os.path.dirname(os.path.abspath(__file__))
VENTANA = 18 / 18.2065          # 18 ticks del BIOS, en segundos
INSTR_POR_ITER = (4, 8)         # bucle A, bucle B
umbral = 3.0
if '--umbral' in sys.argv:
    umbral = float(sys.argv[sys.argv.index('--umbral') + 1])


def leer(nombre):
    ruta = os.path.join(D, nombre)
    if not os.path.exists(ruta):
        raise SystemExit(f'ROJO: falta {nombre} (ver README.md para regenerarlo)')
    datos = open(ruta, 'rb').read()
    if len(datos) != 8:
        raise SystemExit(f'ROJO: {nombre} mide {len(datos)} B, se esperaban 8')
    a, b = struct.unpack('<II', datos)
    return [a * INSTR_POR_ITER[0] / VENTANA, b * INSTR_POR_ITER[1] / VENTANA]


oraculo = leer('SPEED.ORACULO.BIN')
paku = leer('SPEED.PAKU.BIN')

print(f'sonda de velocidad - umbral {umbral:.1f} %')   # sin guion largo: la consola de Windows lo destroza
peor = 0.0
for i, bucle in enumerate(('A (4 instr/iter)', 'B (8 instr/iter, memoria + MUL)')):
    o, p = oraculo[i], paku[i]
    brecha = 100.0 * (p - o) / o
    peor = max(peor, abs(brecha))
    print(f'  bucle {bucle:32s} oraculo {o:12,.0f} | PakuPort {p:12,.0f} | {brecha:+6.2f} %')

# Coherencia interna del oraculo: si A y B divergen, es que ha dejado de cobrar 1 ciclo por
# instruccion (cambio de core o de cputype en la conf) y la comparacion ya no es legitima.
sesgo = 100.0 * abs(oraculo[1] - oraculo[0]) / oraculo[0]
print(f'  coherencia A vs B en el oraculo: {sesgo:.2f} % '
      f'({"1 ciclo por instruccion, como se midio" if sesgo < 1.0 else "OJO: ya no cobra 1 ciclo por instruccion"})')

rojo = peor > umbral
if rojo:
    print(f'ROJO: brecha maxima {peor:.2f} % > umbral {umbral:.1f} % (bucles SIN rep)')
else:
    print(f'VERDE: brecha maxima {peor:.2f} % <= umbral {umbral:.1f} % (bucles SIN rep)')

# ── Caso REP: lo que a los bucles de arriba se les escapaba ────────────────────────────────
# SPEED.COM no lleva prefijo REP, asi que su "misma unidad al 1 %" era cierta y a la vez
# irrelevante para cualquier juego que dibuje: DOSBox-X cobra un ciclo POR ELEMENTO dentro de
# un bucle REP y nosotros cobrabamos uno por la instruccion entera (110x mas rapido, medido).
# Este bloque vigila el caso, a cycles=315 en los dos mundos (ver reptest.conf).
BASELINE_REP = 308      # iteraciones nuestras tras el fix de REP por elemento
ORACULO_REP = 218       # verdad terreno, REPTEST.ORACULO.BIN
TOL_BASELINE = 5.0      # desviacion contra nuestro propio baseline: eso SI seria regresion
TOL_ORACULO = 50.0      # brecha contra el oraculo: hoy +41 %, ver README (deficit del 9,4 %
                        # de DOSBox a cycles bajos + coste de reentrada del REP interrumpido)

rep_paku = os.path.join(D, 'REPTEST.PAKU.BIN')
if os.path.exists(rep_paku):
    n = struct.unpack('<I', open(rep_paku, 'rb').read()[:4])[0]
    d_base = 100.0 * (n - BASELINE_REP) / BASELINE_REP
    d_orac = 100.0 * (n - ORACULO_REP) / ORACULO_REP
    print(f'\ncaso REP (rep movsw de 1000 palabras, cycles=315 en ambos mundos)')
    print(f'  PakuPort {n:,} iter/s | oraculo {ORACULO_REP:,} | baseline {BASELINE_REP:,}')
    print(f'  contra el oraculo:  {d_orac:+6.2f} %   (tolerado {TOL_ORACULO:.0f} %)')
    print(f'  contra el baseline: {d_base:+6.2f} %   (tolerado {TOL_BASELINE:.0f} %)')
    if abs(d_base) > TOL_BASELINE:
        print(f'ROJO: el caso REP se ha movido {d_base:+.2f} % respecto al baseline')
        rojo = True
    elif abs(d_orac) > TOL_ORACULO:
        print(f'ROJO: el caso REP se aleja {d_orac:+.2f} % del oraculo')
        rojo = True
    else:
        print('VERDE: el caso REP sigue en su sitio')
else:
    print('\ncaso REP: falta REPTEST.PAKU.BIN (ver README.md); NO evaluado')

sys.exit(1 if rojo else 0)
