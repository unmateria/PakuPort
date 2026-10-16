"""El JUEZ DE FIDELIDAD: corre la MISMA cinta con y sin remake, y compara.

Es lo que faltaba cuando la intro de un juego de prueba se dio por buena y la prueba manual la tumbo:
no habia forma de decir en numeros que se habia ganado y que se habia roto, asi que se juzgaba a
ojo. Aqui salen cuatro cifras, y ninguna es opinable:

  **cobertura** — que fraccion de los blits de la partida llevan arte, global y POR ESCENA. Una
      cobertura global alta puede esconder una escena entera sin tocar, y eso se ve en pantalla.
  **huecos**   — los assets sin arte, ORDENADOS POR CUANTO SALEN. Da igual que falten 200 si el
      que mas se ve sale 412 veces: se ataca por ahi, no por orden alfabetico.
  **deriva**   — arte que se sale de su sitio: instancias que se van de la pantalla, o cuyo
      tamano declarado no es el que el motor midio. Es como se detecta un sprite mal colocado.
  **regresion**— lo que TENIA arte en el juicio anterior y ya no. Sin esto, "mejorar" puede
      romper y nadie se entera hasta la prueba manual.

Y la comprobacion que sostiene a las otras cuatro: **el invitado tiene que dibujar exactamente lo
mismo** en las dos corridas. Si no, no se estan comparando dos versiones de lo mismo — se estan
comparando dos juegos, y el juicio no vale. Lo que lo hace posible es la CINTA: bajo
`PAKU_TAPE_REPLAY` el reloj se ancla al contador de instrucciones (`InputTape.ClockLocked`), asi
que la ejecucion es repetible.

Se compara la SECUENCIA DE BLITS —que se dibujo, donde, de que tamano y con que identidad— y no
unos cuantos fotogramas volcados, por una razon medida: los fotogramas se muestrean cada 1/70 s y
en un instante de transicion la muestra puede caer un fotograma antes o despues, asi que dos
corridas IDENTICAS ya diferian en el ultimo de seis. La traza de blits no tiene ese problema y
ademas es mucho mas fuerte: son los 1611 dibujos de la partida entera, no seis muestras. Del
`t=`/`frame=` se prescinde a proposito — el tick exacto en que ocurre cada blit tiene jitter de
planificacion, y lo que importa es QUE se dibujo, no en que microsegundo.

Las dos corridas cortan en la MISMA INSTRUCCION (`PAKU_AUTOEXIT_INSN` = ms x cyclesPerMs del
game.json), como `remake try`: cortar por reloj de pared hacia que la de referencia y la del remake
pararan en instantes distintos y la comprobacion de arriba saliera ROJA en dos corridas identicas
(medido en un juego de prueba: 24.657 frente a 25.465 blits). Sin cyclesPerMs no hay corte
repetible y se dice.

Cada corrida usa una COPIA del host (sin `Core`, `obj` ni `logs`): no toca los logs ni las partidas
del export que alguien este jugando. Si el juicio sale VERDE las copias se borran; si sale ROJO se
conserva SOLO el ultimo, en `<remake>/juicio-rojo/`, con la salida de cada corrida.

Uso:  python tools/remake_kit/judge_fidelidad.py <id> --export <dir> --remake <dir> [--ms N]
"""

import argparse
import glob
import hashlib
import json
import os
import re
import shutil
import sys
import subprocess
import tempfile
from collections import defaultdict


RAIZ = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))


class CorridaFallida(Exception):
    pass


def corre(export, destino, remake, cinta, ms, ciclos):
    """Una corrida de la cinta. `remake` None = la version original."""
    os.makedirs(destino, exist_ok=True)
    run = os.path.join(destino, 'host')
    raiz_export = os.path.abspath(export)
    shutil.copytree(raiz_export, run, ignore=lambda d, nombres:
                    [n for n in nombres if n in ('Core', 'obj', 'logs')]
                    if os.path.abspath(d) == raiz_export else [])
    dlls = [f[:-len('.runtimeconfig.json')] + '.dll'
            for f in glob.glob(os.path.join(run, 'bin', 'Release', '*', '*.runtimeconfig.json'))]
    if len(dlls) != 1:
        raise CorridaFallida(f'hace falta UN host compilado en {os.path.join(export, "bin", "Release")} '
                             f'(hay {len(dlls)})')
    corte = {'PAKU_AUTOEXIT_INSN': str(ms * ciclos)} if ciclos > 0 else {'PAKU_AUTOEXIT_MS': str(ms)}
    # Nothing inherited: a PAKU_* left in the shell by another test would change what is measured.
    env = dict({k: v for k, v in os.environ.items() if not k.startswith('PAKU_')}, **corte,
               PAKU_INTERP_ONLY='1', PAKU_NOSPLASH='1', PAKU_NOAUDIO='1', PAKU_HEADLESS='1',
               PAKU_LOG_BLIT='1',                       # para saber QUE se dibujo y cuando
               PAKU_TAPE_REPLAY=os.path.abspath(cinta),  # reloj anclado: las dos corridas casan
               PAKU_PRESENT_DUMP=os.path.join(destino, 'f'),
               # Por FOTOGRAMAS, no por milisegundos: bajo la cinta la captura es determinista
               # (tiempo virtual), pero un cronometro de pared haria que las dos corridas
               # volcaran instantes distintos y el juez diria que el remake toca al juego.
               PAKU_PRESENT_DUMP_AFTER_FRAMES='400',
               PAKU_PRESENT_DUMP_N='6')
    if remake:
        env['PAKU_REMAKE'] = os.path.abspath(remake)
    r = subprocess.run(['dotnet', dlls[0]], cwd=run, env=env, capture_output=True, text=True,
                       encoding='utf-8', errors='replace', timeout=ms // 1000 + 600)
    with open(os.path.join(destino, 'salida.log'), 'w', encoding='utf-8') as fh:
        fh.write((r.stdout or '') + (r.stderr or ''))
    # A host that crashed or a guest that ended in error drew less, and comparing that would
    # judge the crash, not the art.
    if r.returncode != 0:
        raise CorridaFallida(f'el host termino con codigo {r.returncode}')
    if '[EXIT] Error' in (r.stderr or ''):
        raise CorridaFallida('el invitado termino con error ([EXIT] Error)')
    for nombre in ('blit.log', 'remake-coverage.log'):
        o = os.path.join(run, 'logs', nombre)
        if os.path.exists(o):
            shutil.copyfile(o, os.path.join(destino, nombre))
    return firma_de_lo_dibujado(os.path.join(destino, 'blit.log'), run)


# Lo que identifica un dibujo: que operacion, en que modo, a donde, de que tamano y con que
# identidad. NO va el `t=`/`frame=`: el tick exacto tiene jitter de planificacion y compararlo
# haria fallar dos corridas identicas.
CAMPOS_FIRMA = ('op', 'mode', 'df', 'dst', 'lin', 'n', 'bytes', 'fnv', 'fill', 'file')


def firma_de_lo_dibujado(ruta, run=None):
    """(numero de blits, sha1 de la secuencia) — el retrato de lo que hizo el invitado. La ruta
    del fichero de origen se normaliza: cada corrida lee los datos DOS de su propia copia."""
    if not os.path.exists(ruta):
        return (0, '')
    filas = []
    for linea in open(ruta, encoding='utf-8', errors='replace'):
        if not linea.startswith('t='):
            continue
        f = dict(p.split('=', 1) for p in linea.split() if '=' in p)
        if run and 'file' in f:
            f['file'] = f['file'].replace(os.path.abspath(run), '<DOS>')
        filas.append('|'.join(f.get(c, '') for c in CAMPOS_FIRMA))
    return (len(filas), hashlib.sha1('\n'.join(filas).encode()).hexdigest())


def clave_de(f):
    if 'fnv' in f:
        return ('file:' + f['file']) if 'file' in f else f['fnv']
    return f.get('fill')


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('id')
    ap.add_argument('--export', required=True)
    ap.add_argument('--remake', required=True)
    ap.add_argument('--ms', type=int, default=40000)
    a = ap.parse_args()
    if a.ms <= 0:
        ap.error('--ms tiene que ser positivo')
    gj = os.path.join(RAIZ, 'games', a.id, 'game.json')
    ciclos = int(json.load(open(gj, encoding='utf-8')).get('cyclesPerMs') or 0) if os.path.exists(gj) else 0
    if ciclos <= 0:
        print('AVISO: sin cyclesPerMs se corta por reloj del anfitrion y las dos corridas pueden parar '
              'en instantes distintos; si la comprobacion del invitado sale ROJA, es por eso.')

    cinta = os.path.join(a.remake, 'cinta.tape')
    runtime = os.path.join(a.remake, 'remake.json')
    for f in (cinta, runtime):
        if not os.path.exists(f):
            print(f'ROJO: falta {f}')
            return 2

    admitidos = set(json.load(open(runtime, encoding='utf-8')).get('assets', {}))
    if not admitidos:
        print('ROJO: remake.json no admite ningun asset. Pasa `check` con arte entregado.')
        return 2

    tmp = tempfile.mkdtemp(prefix='fidelidad_')
    rc = 1
    try:
        rc = juzga(a, tmp, cinta, runtime, admitidos, ciclos)
        return rc
    except CorridaFallida as e:
        print(f'ROJO: {e}; la salida de cada corrida queda en el juicio conservado.')
        return 1
    finally:
        rojo = os.path.join(a.remake, 'juicio-rojo')
        shutil.rmtree(rojo, ignore_errors=True)
        if rc != 0:
            for corrida in ('sin', 'con'):      # the logs are the evidence; the host copies weigh 100s of MB
                shutil.rmtree(os.path.join(tmp, corrida, 'host'), ignore_errors=True)
            shutil.move(tmp, rojo)
            print(f'   corridas conservadas (solo el ultimo rojo): {os.path.abspath(rojo)}')
        else:
            shutil.rmtree(tmp, ignore_errors=True)


def juzga(a, tmp, cinta, runtime, admitidos, ciclos):
    print('-- corrida SIN remake (la referencia) --')
    n_sin, h_sin = corre(a.export, os.path.join(tmp, 'sin'), None, cinta, a.ms, ciclos)
    print(f'   {n_sin} blits, firma {h_sin[:12]}')

    print('-- corrida CON remake --')
    n_con, h_con = corre(a.export, os.path.join(tmp, 'con'), runtime, cinta, a.ms, ciclos)
    print(f'   {n_con} blits, firma {h_con[:12]}')

    # --- la invariante que sostiene todo lo demas ---
    if (n_sin, h_sin) != (n_con, h_con):
        print(f'ROJO: el INVITADO no dibuja lo mismo con el remake puesto ({n_sin} blits vs '
              f'{n_con}). El reemplazo no es solo de presentacion, o la cinta no reproduce '
              f'igual las dos veces. Mientras eso pase, el resto de las cifras no valen.')
        return 1
    if n_sin == 0:
        print('ROJO: no se registro ningun blit; no hay nada que juzgar.')
        return 1

    # --- cobertura, global y por escena ---
    cov = os.path.join(tmp, 'con', 'remake-coverage.log')
    texto = open(cov, encoding='utf-8').read() if os.path.exists(cov) else ''
    m = re.search(r'blits=(\d+) reemplazados=(\d+) cobertura=([\d.,]+)%', texto)
    blits, reemp = (int(m.group(1)), int(m.group(2))) if m else (0, 0)
    cobertura = 100.0 * reemp / blits if blits else 0.0
    # Most art hangs from a whole drawing (`grupo:` keys), which the per-blit count above does
    # not see: RemakeMap prints that count on its own line, and it was being ignored.
    g = re.search(r'dibujos_logicos=(\d+) reemplazados=(\d+)', texto)
    dibujos, dibujos_reemp = (int(g.group(1)), int(g.group(2))) if g else (0, 0)

    escenas = []
    esc_path = os.path.join(a.remake, 'escenas.json')
    if os.path.exists(esc_path):
        escenas = json.load(open(esc_path, encoding='utf-8')).get('escenas', [])

    por_escena = defaultdict(lambda: [0, 0])     # escena -> [con arte, total]
    huecos = defaultdict(int)
    blit_log = os.path.join(tmp, 'con', 'blit.log')
    if os.path.exists(blit_log):
        for linea in open(blit_log, encoding='utf-8', errors='replace'):
            if not linea.startswith('t='):
                continue
            f = dict(p.split('=', 1) for p in linea.split() if '=' in p)
            cl = clave_de(f)
            if cl is None:
                continue
            t = int(f['t'])
            esc = next((e['escena'] for e in escenas
                        if e['t_inicio'] <= t < e['t_fin']), 0)
            tiene = cl in admitidos
            por_escena[esc][1] += 1
            if tiene:
                por_escena[esc][0] += 1
            else:
                huecos[cl] += 1

    # --- deriva: arte que no cabe donde dice el motor que va ---
    deriva = []
    for linea in texto.splitlines():
        if not linea.startswith('viva '):
            continue
        g = dict(p.split('=', 1) for p in linea.split() if '=' in p)
        try:
            x, y = int(g['x']), int(g['y'])
        except (KeyError, ValueError):
            continue
        if x < 0 or y < 0 or x >= 320 or y >= 200:
            deriva.append(f'{g.get("tex", "?")} en ({x},{y}), fuera de la pantalla')

    # --- regresion contra el juicio anterior ---
    historico = os.path.join(a.remake, 'informe.json')
    antes = json.load(open(historico, encoding='utf-8')) if os.path.exists(historico) else {}
    perdidos = sorted(set(antes.get('admitidos', [])) - admitidos)

    # --- informe ---
    L = [f'# Juicio de fidelidad — {a.id}', '',
         f'- **cobertura**: {cobertura:.1f} %  ({reemp} de {blits} blits con arte)',
         f'- **dibujos logicos**: {dibujos_reemp} de {dibujos} reemplazados',
         f'- **huecos**: {len(huecos)} assets sin arte'
         + (f', el mas visto sale {max(huecos.values())} veces' if huecos else ''),
         f'- **deriva**: {len(deriva)} instancias fuera de caja',
         f'- **regresion**: {"ninguna" if not perdidos else str(len(perdidos)) + " assets perdieron su arte"}',
         f'- el invitado dibuja exactamente lo mismo con y sin remake: si '
         f'({n_sin} blits, firma `{h_sin[:12]}`)',
         '']
    if por_escena:
        L += ['## Por escena', '', '| escena | con arte | blits | cobertura |', '|---|---|---|---|']
        for esc in sorted(por_escena):
            con, tot = por_escena[esc]
            L.append(f'| {esc} | {con} | {tot} | {100.0 * con / tot if tot else 0:.1f} % |')
        L.append('')
    if huecos:
        L += ['## Huecos, por cuanto se ven', '', '| clave | veces |', '|---|---|']
        for cl, n in sorted(huecos.items(), key=lambda kv: -kv[1])[:40]:
            L.append(f'| `{cl}` | {n} |')
        L.append('')
    if deriva:
        L += ['## Deriva', ''] + [f'- {d}' for d in deriva[:40]] + ['']
    if perdidos:
        L += ['## Regresion', ''] + [f'- `{c}` tenia arte y ya no' for c in perdidos[:40]] + ['']

    with open(os.path.join(a.remake, 'informe.md'), 'w', encoding='utf-8') as fh:
        fh.write('\n'.join(L))
    with open(historico, 'w', encoding='utf-8') as fh:
        json.dump({'cobertura': cobertura, 'blits': blits, 'reemplazados': reemp,
                   'dibujos_logicos': dibujos, 'dibujos_reemplazados': dibujos_reemp,
                   'admitidos': sorted(admitidos)}, fh, indent=1)

    detalle = ' · '.join(f'esc{e} {100.0 * v[0] / v[1] if v[1] else 0:.1f} %'
                         for e, v in sorted(por_escena.items()))
    print(f'cobertura {cobertura:.1f} %' + (f'  ({detalle})' if detalle else ''))
    # A figure delivered under a grupo: key replaces rows that keep their own blit keys, so the
    # coverage by blits can read 0 % with the figure replaced: say both.
    print(f'dibujos logicos: {dibujos_reemp} de {dibujos} reemplazados')
    print(f'huecos: {len(huecos)} assets sin arte'
          + (f', el mas visto sale {max(huecos.values())} veces' if huecos else '')
          + f'  -> {os.path.join(a.remake, "informe.md")}')
    print(f'deriva: {len(deriva)} fuera de caja        '
          f'regresion: {"ninguna" if not perdidos else len(perdidos)}')

    nada = reemp + dibujos_reemp == 0
    if nada:
        print('ROJO: en esta cinta no se reemplazo NADA (ni blits ni dibujos logicos): con ella '
              'no se juzga el arte admitido.')
    rojo = bool(deriva) or bool(perdidos) or nada
    print('ROJO' if rojo else 'VERDE')
    return 1 if rojo else 0


if __name__ == '__main__':
    sys.exit(main())
