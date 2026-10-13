"""Limpia lo REGENERABLE y lo temporal del repo, diciendo siempre que borra y cuanto libera.

Este proyecto genera mucha basura por diseno: cada juego porteado deja un `export/` con su
`bin/obj`, cada republicacion deja un backup `_preN` de la entrega, cada partida rota sus logs, y
cada diagnostico deja volcados de cientos de MB. Nada de eso es malo — pero se acumula,
y llego a 20 GB.

La regla que sigue: **solo se borra lo que se puede volver a generar**, y se dice en voz alta.
Nada de codigo, nada de datos de juego, nada de entregas vivas, nada de corpus ni de verdades
terreno de los jueces.

Por defecto NO borra: imprime lo que haria. Con `--hazlo` lo hace.

    python tools/limpiar.py             que se borraria y cuanto se libera
    python tools/limpiar.py --hazlo     hacerlo
    python tools/limpiar.py --hazlo --sin-bin    ...pero conservando los bin/obj de los exports
"""

import os
import re
import shutil
import sys

RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
HAZLO = '--hazlo' in sys.argv
SIN_BIN = '--sin-bin' in sys.argv

# Exports que usan los gates y los jueces: su `bin/obj` se reconstruye solo, pero si se borra hay
# que volver a compilarlos antes de pasar los jueces, asi que se avisa aparte.
EXPORTS_DE_JUECES = {'blit', 'blit2', 'blit3', 'chain', 'compose', 'ega', 'ghost',
                     'orden', 'orden-inv',
                     'coro-a', 'coro-b', 'coro-c', 'reptest'}

MB = 1024 * 1024
total = 0
victimas = []


def tam(ruta):
    if os.path.isfile(ruta):
        try:
            return os.path.getsize(ruta)
        except OSError:
            return 0
    n = 0
    for base, _, ficheros in os.walk(ruta):
        for f in ficheros:
            try:
                n += os.path.getsize(os.path.join(base, f))
            except OSError:
                pass
    return n


def apunta(ruta, motivo):
    global total
    if not os.path.exists(ruta):
        return
    n = tam(ruta)
    total += n
    victimas.append((n, os.path.relpath(ruta, RAIZ), motivo))


# --- 1. cachés de Python: se rehacen solas ---
for base, dirs, _ in os.walk(RAIZ):
    if '.git' in base:
        continue
    for d in list(dirs):
        if d == '__pycache__':
            apunta(os.path.join(base, d), 'cache de Python')

# --- 2. salida de compilacion de los exports: `dotnet build` la rehace ---
exp = os.path.join(RAIZ, 'export')
if os.path.isdir(exp) and not SIN_BIN:
    for juego in sorted(os.listdir(exp)):
        for sub in ('bin', 'obj'):
            p = os.path.join(exp, juego, sub)
            if os.path.isdir(p):
                marca = ' (lo usa un juez: habra que recompilarlo)' if juego in EXPORTS_DE_JUECES else ''
                apunta(p, f'compilacion regenerable{marca}')

# --- 3. logs rotados de partidas anteriores dentro de cada export ---
if os.path.isdir(exp):
    for juego in sorted(os.listdir(exp)):
        d = os.path.join(exp, juego, 'logs')
        if not os.path.isdir(d):
            continue
        for f in sorted(os.listdir(d)):
            if f.startswith('partida-'):
                apunta(os.path.join(d, f), 'log rotado de una partida vieja')

# --- 4. backups de entregas: se conserva el MAS NUEVO de cada juego ---
ent = os.path.join(RAIZ, 'entrega')
if os.path.isdir(ent):
    por_juego = {}
    for d in sorted(os.listdir(ent)):
        m = re.match(r'^(.*)_pre(\d+)$', d)
        if m and os.path.isdir(os.path.join(ent, d)):
            por_juego.setdefault(m.group(1), []).append((int(m.group(2)), d))
    for juego, lista in por_juego.items():
        lista.sort()
        for _, d in lista[:-1]:                      # todos menos el ultimo
            apunta(os.path.join(ent, d), f'backup viejo de {juego} (se conserva el mas nuevo)')

# --- 5. volcados de diagnostico gordos en logs/: los pequenos se quedan ---
lg = os.path.join(RAIZ, 'logs')
if os.path.isdir(lg):
    for f in sorted(os.listdir(lg)):
        p = os.path.join(lg, f)
        if f == 'gates.log':                         # el historial de gates SE QUEDA
            continue
        if os.path.isfile(p) and os.path.getsize(p) > 5 * MB:
            apunta(p, 'volcado de diagnostico antiguo (>5 MB)')

victimas.sort(reverse=True)
for n, ruta, motivo in victimas[:25]:
    print(f'  {n / MB:8.1f} MB  {ruta}   — {motivo}')
if len(victimas) > 25:
    print(f'  ... y {len(victimas) - 25} mas')

print(f'\n{len(victimas)} elementos, {total / MB:.0f} MB')
if not HAZLO:
    print('(esto es solo el listado; con --hazlo se borra)')
    sys.exit(0)

borrados = 0
for _, ruta, _ in victimas:
    p = os.path.join(RAIZ, ruta)
    try:
        if os.path.isdir(p):
            shutil.rmtree(p)
        else:
            os.remove(p)
        borrados += 1
    except OSError as e:
        print(f'  no se pudo borrar {ruta}: {e}')
print(f'borrados {borrados} de {len(victimas)} · {total / MB:.0f} MB liberados')
if not SIN_BIN:
    print('OJO: se han borrado los bin/obj de los exports. Antes de pasar los jueces del kit,')
    print('     recompila los que usen: dotnet build -c Release en export/<juego>.')
