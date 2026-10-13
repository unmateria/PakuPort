"""Juez del ORDEN DE DIBUJO y la PERSISTENCIA, con su control negativo.

Verdad terreno POR CONSTRUCCION (ver `gen_orden.py`): ORDEN.COM pinta A, luego B encima de A,
luego C encima de B; deja un sprite P que no vuelve a tocar y borra otro, T, repintando el fondo
varios fotogramas despues.

Lo que se exige:
  * `C` tapa a `B`, `B` tapa a `A`, y a `C` no lo tapa nadie.
  * `P` sale PERSISTENTE y `T` sale TRANSITORIO.

Y el control negativo, sin el cual esto no valdria nada: ORDEN-INV.COM pinta los mismos tres
cuadrados EN EL ORDEN CONTRARIO, y la medida tiene que darse la vuelta entera. Un juez que
afirmara "A antes que B" pasaria los dos y no estaria midiendo nada.

  python tools/remake_kit/judge_orden.py        VERDE / ROJO, sale != 0 si falla
"""

import json
import os
import subprocess
import sys

RAIZ = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
AQUI = os.path.dirname(os.path.abspath(__file__))

def fnv(color, nbytes):
    """La clave de un blit de `movs` es el FNV-1a de sus bytes de ORIGEN (Interp.LogBlit).

    Se calcula aqui en vez de copiar un hash a mano: si el motor cambiara de funcion de hash,
    esto fallaria ruidosamente en vez de comparar contra una constante fosil.
    """
    h = 2166136261
    for _ in range(nbytes):
        h = ((h ^ color) * 16777619) & 0xFFFFFFFF
    return f'{h:08X}'


# Cada fila de un cuadrado de 64 px son 64 bytes; las de los sprites de 32 px, 32 bytes.
A, B, C = fnv(0x0A, 64), fnv(0x0B, 64), fnv(0x0C, 64)
P, T = fnv(0x0D, 32), fnv(0x0E, 32)      # el que persiste y el que se borra


def mide(export):
    """Corre el .COM con la telemetria del kit y devuelve el mapa de capas."""
    logs = os.path.join(RAIZ, 'export', export, 'logs')
    blit = os.path.join(logs, 'blit.log')
    for viejo in (blit, os.path.join(logs, 'blitraw.bin')):
        if os.path.exists(viejo):
            os.remove(viejo)
    env = dict(os.environ, PAKU_LOG_BLIT='3', PAKU_INTERP_ONLY='1',
               PAKU_PRESENT_CAPTURE='1', PAKU_NOSPLASH='1', PAKU_AUTOEXIT_MS='6000')
    r = subprocess.run(['dotnet', 'run', '-c', 'Release', '--no-build'],
                       cwd=os.path.join(RAIZ, 'export', export), env=env,
                       capture_output=True, text=True, timeout=600)
    if not os.path.exists(blit):
        raise SystemExit(f'ROJO: {export} no dejo blit.log\n{r.stdout[-600:]}{r.stderr[-600:]}')
    salida = os.path.join(RAIZ, 'export', export, 'capas.json')
    subprocess.run([sys.executable, os.path.join(AQUI, 'build_capas.py'),
                    '--blit-log', blit, '--out', salida], check=True, cwd=RAIZ)
    return json.load(open(salida, encoding='utf-8'))['capas']


def comprueba(capas, orden, etiqueta):
    """`orden` = los tres rellenos del fondo a la superficie."""
    fallos = []
    fondo, medio, arriba = orden
    for cl in (fondo, medio, arriba, P, T):
        if cl not in capas:
            fallos.append(f'{etiqueta}: la clave {cl} no aparece en la medida')
    if fallos:
        return fallos

    if medio not in capas[fondo]['tapado_por']:
        fallos.append(f'{etiqueta}: a {fondo} deberia taparlo {medio}, y tapado_por dice '
                      f'{capas[fondo]["tapado_por"]}')
    if arriba not in capas[medio]['tapado_por']:
        fallos.append(f'{etiqueta}: a {medio} deberia taparlo {arriba}, y tapado_por dice '
                      f'{capas[medio]["tapado_por"]}')
    if capas[arriba]['tapado_por']:
        fallos.append(f'{etiqueta}: {arriba} va arriba del todo y algo lo tapa: '
                      f'{capas[arriba]["tapado_por"]}')
    if not (capas[fondo]['capa'] < capas[medio]['capa'] < capas[arriba]['capa']):
        fallos.append(f'{etiqueta}: las capas no van de fondo a superficie: '
                      f'{fondo}={capas[fondo]["capa"]} {medio}={capas[medio]["capa"]} '
                      f'{arriba}={capas[arriba]["capa"]}')
    if capas[P]['persistencia'] != 'persistente':
        fallos.append(f'{etiqueta}: {P} no se toca nunca mas y sale '
                      f'{capas[P]["persistencia"]} (reescrito={capas[P]["reescrito_despues"]})')
    if capas[T]['persistencia'] != 'transitorio':
        fallos.append(f'{etiqueta}: {T} se borra repintando el fondo y sale '
                      f'{capas[T]["persistencia"]} (reescrito={capas[T]["reescrito_despues"]})')
    return fallos


def main():
    print('-- ORDEN.COM: se pinta A, luego B encima, luego C encima --')
    directo = mide('orden')
    fallos = comprueba(directo, (A, B, C), 'directo')

    print('-- ORDEN-INV.COM (control negativo): el orden contrario --')
    inverso = mide('orden-inv')
    fallos += comprueba(inverso, (C, B, A), 'invertido')

    # Y lo que de verdad prueba que se esta MIDIENDO: los dos no pueden dar lo mismo.
    if directo[A]['tapado_por'] == inverso[A]['tapado_por']:
        fallos.append('el control negativo da el MISMO resultado que el directo: esto no mide '
                      'el orden de dibujo, lo afirma')

    for etiqueta, capas in (('directo', directo), ('invertido', inverso)):
        print(f'  {etiqueta}:')
        for cl in (A, B, C, P, T):
            v = capas.get(cl)
            if v:
                print(f'    {cl}  capa={v["capa"]}  tapado_por={v["tapado_por"] or "-"}  '
                      f'reescrito_despues={v["reescrito_despues"]:.2f}  {v["persistencia"]}')

    if fallos:
        print('ROJO:')
        for f in fallos:
            print('  ' + f)
        return 1
    print('VERDE: el orden de dibujo y la persistencia salen medidos, y al invertir el pintado '
          'la medida se da la vuelta entera.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
