"""EL CONTROL DE LAS FUNCIONES PORTADAS — que el juego USA la traduccion, y que no cambia nada.

Una funcion traducida que pasa el juez todavia no prueba nada sobre el juego: el juez la mide
contra invocaciones grabadas, no dentro de la partida. Aqui se comprueban las dos mitades que
faltan:

  1. **se ejecuta de verdad**: el contador de `logs/portadas.log` tiene que ser mayor que cero.
     Verde en el juez y cero veces aqui son cosas distintas — la segunda dice que el juego ni
     siquiera pasa por esa funcion en esta partida.
  2. **el juego hace exactamente lo mismo**: se comparan, con y sin el gancho, el MiB de memoria
     del invitado Y LA PANTALLA (los cuatro planos del VGA mas el DAC, que NO viven en ese MiB).

Cosas que costaron y que no hay que volver a aprender:

**El corte tiene que ser por INSTRUCCIONES** (`PAKU_AUTOEXIT_INSN`), no por milisegundos:
`PAKU_AUTOEXIT_MS` es un cronometro del anfitrion y dos corridas identicas acaban en puntos
distintos del programa. Medido: 104.719 y 104.357 blits, y los fotogramas presentados ya divergian
en el segundo de ocho.

**No se puede juzgar con la traza de blits**: solo anota las copias en bloque (`movs`/`stos`), asi
que una funcion que pinta con `mov` sueltos es invisible para ella. Medido: con blits, una
traduccion ROTA A PROPOSITO salia verde.

**Y NO se puede juzgar solo con `Memory.Raw`**: la VRAM son buffers de VideoState, no parte del
MiB del invitado, asi que un A/B que solo mire la memoria es ciego para todo lo que el juego
DIBUJA. Por eso el corte vuelca tambien `<memdump>.vram` y aqui se compara.

**El ruido de fondo tiene que ser CERO.** Lo fue desde que se quito la postrera
dependencia del reloj del anfitrion que quedaba bajo la cinta (la reproducion de la cinta la hacia
el bucle de render, a fotogramas de reloj de pared; ahora la hace el hilo del juego en
`MachineTimer.Poll`). Antes de eso, dos corridas identicas dejaban entre 21 y 1.466 bytes
distintos segun la tanda y ningun juez podia afirmar nada. Si vuelve a haber ruido, esto sale
ROJO diciendolo: es un fallo del arnes, no del remake, y taparlo con un umbral seria fingir.

**La ventana importa, y mucho.** El volcado se compara en la instruccion del corte, asi que solo
ve lo que a esas alturas siga en pie. Medido en un juego de prueba: la funcion traducida se ejecuta
en las instrucciones 39-53 y a las 300 M de la partida ni la buena ni una version ROTA a proposito
dejan rastro — el juego lleva millones de instrucciones sobreescribiendo. Un control negativo es
obligatorio (mutar la traduccion y ver que esto sale ROJO) y, si sale que la mutacion tampoco se nota, la ventana
esta mal elegida o la funcion no hace nada observable en esa partida: en los dos casos este
control no juzga la traduccion, y hay que decirlo en vez de dar un verde vacio.

Uso:  python tools/control_portadas.py --export export/<id> --cinta games/<id>/remake/cinta.tape
"""

import argparse
import os
import shutil
import subprocess
import sys
import tempfile


def corre(export, cinta, insn, con_portadas, destino):
    """Una corrida cortada en la instruccion `insn`.

    Devuelve (memoria del invitado, pantalla, uso de las portadas)."""
    os.makedirs(destino, exist_ok=True)
    mem = os.path.join(os.path.abspath(destino), 'mem.bin')
    # SIN VENTANA Y SIN SONIDO, siempre: esto se lanza muchas veces seguidas y puede haber
    # alguien trabajando en la misma maquina. PAKU_HEADLESS va explicito aunque el corte por
    # instrucciones ya lo implique, para que quede dicho en el sitio donde se lanza.
    env = dict(os.environ,
               PAKU_INTERP_ONLY='1', PAKU_NOSPLASH='1',
               PAKU_HEADLESS='1', PAKU_NOAUDIO='1', PAKU_SIN_FRENO='1',
               PAKU_AUTOEXIT_INSN=str(insn),
               PAKU_TAPE_REPLAY=os.path.abspath(cinta),
               PAKU_MEMDUMP=mem)
    if con_portadas:
        env.pop('PAKU_SIN_PORTADAS', None)
    else:
        env['PAKU_SIN_PORTADAS'] = '1'

    log = os.path.join(export, 'logs', 'portadas.log')
    if os.path.exists(log):
        os.remove(log)

    p = subprocess.run(['dotnet', 'run', '-c', 'Release', '--no-build'], cwd=export, env=env,
                       capture_output=True, text=True, encoding='utf-8', errors='replace',
                       timeout=3600)
    if not os.path.exists(mem):
        # Without the dump there is nothing to compare: say why instead of judging nothing.
        salida = [l for l in ((p.stdout or '') + (p.stderr or '')).splitlines()
                  if l.strip() and not l.startswith('INFO:')]
        print('       (la corrida no dejo volcado; lo ultimo que dijo el juego:)')
        for l in salida[-6:]:
            print('       | ' + l)

    if os.path.exists(log):
        shutil.copyfile(log, os.path.join(destino, 'portadas.log'))
    return (lee(mem), lee(mem + '.vram'), uso(os.path.join(destino, 'portadas.log')))


def lee(ruta):
    return open(ruta, 'rb').read() if os.path.exists(ruta) else b''


def uso(ruta):
    """Lo que dice logs/portadas.log: [(funcion, veces)]."""
    if not os.path.exists(ruta):
        return []
    salida = []
    for linea in open(ruta, encoding='utf-8', errors='replace'):
        campos = dict(p.split('=', 1) for p in linea.split() if '=' in p)
        if 'veces' in campos:
            salida.append((linea.split()[0], int(campos['veces'])))
    return salida


def distintos(a, b):
    """Los indices de los bytes en que dos volcados no coinciden, o None si no son comparables."""
    if not a or not b or len(a) != len(b):
        return None
    return {i for i in range(len(a)) if a[i] != b[i]}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--export', required=True, help='el juego exportado con --portadas')
    ap.add_argument('--cinta', required=True, help='la cinta de entrada (reloj anclado)')
    ap.add_argument('--insn', type=int, default=300000000,
                    help='en que instruccion se corta cada corrida')
    ap.add_argument('--controles', type=int, default=2,
                    help='corridas sin gancho con las que se comprueba que el arnes no hace ruido')
    a = ap.parse_args()

    tmp = tempfile.mkdtemp(prefix='control-portadas-')
    fallos, ejecutadas = [], []
    try:
        # 1. EL ARNES NO PUEDE HACER RUIDO. Sin esto, un verde no distingue "la traduccion es
        #    correcta" de "el arnes no mide nada". Y no hay umbral: o es cero, o esto sale rojo.
        controles = [corre(a.export, a.cinta, a.insn, False, os.path.join(tmp, 'sin_%d' % i))
                     for i in range(max(2, a.controles))]
        ruido = set()
        for i in range(len(controles)):
            for j in range(i + 1, len(controles)):
                for k, que in ((0, 'memoria'), (1, 'pantalla')):
                    d = distintos(controles[i][k], controles[j][k])
                    if d is None:
                        fallos.append('una corrida de control no dejo %s comparable' % que)
                    else:
                        ruido |= d
        comparables = all(c[0] and c[1] for c in controles)
        print(('VERDE' if comparables and not ruido else 'ROJO ') +
              '  el arnes es determinista: %d corridas identicas, %d bytes de diferencia'
              % (len(controles), len(ruido)) + ('' if comparables else ' (SIN VOLCADOS: no mide nada)'))
        if ruido:
            fallos.append('dos corridas identicas del juego NO dan el mismo resultado (%d bytes): '
                          'queda una dependencia del reloj del anfitrion o de un hilo del host, y '
                          'mientras siga ahi este control no puede afirmar nada' % len(ruido))

        # 2. LA CORRIDA CON EL GANCHO.
        con_mem, con_vram, usadas = corre(a.export, a.cinta, a.insn, True, os.path.join(tmp, 'con'))
        if not usadas:
            fallos.append('el juego no registro ninguna funcion traducida: '
                          'exporta con --portadas <tablero>')
        ejecutadas = [(f, v) for f, v in usadas if v > 0]
        for f, v in usadas:
            print(('VERDE' if v > 0 else 'ROJO ') +
                  '  %-18s %s' % (f, 'se ejecuto %d veces dentro del juego' % v if v > 0
                                  else 'no se ejecuto NUNCA'))
        if usadas and not ejecutadas:
            fallos.append('ninguna traduccion se ejecuto: el juego no pasa por esas funciones '
                          'en esta partida, asi que la comparacion no mide el gancho')

        # 3. EL JUICIO: con el gancho, ni la memoria ni la pantalla pueden cambiar un byte.
        for k, que in ((0, 'la memoria del invitado'), (1, 'la pantalla')):
            cambios = distintos(controles[0][k], (con_mem, con_vram)[k])
            if cambios is None:
                fallos.append('la corrida con el gancho no dejo %s comparable' % que)
                continue
            propios = sorted(cambios - ruido)
            print(('VERDE' if not propios else 'ROJO ') +
                  '  %s sale igual: %d bytes cambiados' % (que, len(propios)))
            if propios:
                print('       primeros: ' + ' '.join('0x%X' % x for x in propios[:8]))
                fallos.append('el gancho cambia %s en %d bytes' % (que, len(propios)))
    finally:
        shutil.rmtree(tmp, ignore_errors=True)

    if fallos:
        print('\n== CONTROL DE LAS PORTADAS: ROJO ==')
        for f in fallos:
            print('  ' + f)
        return 1
    print('\n== CONTROL DE LAS PORTADAS: VERDE ==')
    print('   %d traduccion(es) ejecutandose dentro del juego, y el invitado acaba igual.'
          % len(ejecutadas))
    print('   OJO: un verde aqui solo vale acompanado de su control negativo: cambia a proposito '
          'la traduccion, vuelve a exportar y repite; tiene que salir ROJO. Si la mutacion '
          'tampoco se nota en esta ventana, este control no esta juzgando la traduccion.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
