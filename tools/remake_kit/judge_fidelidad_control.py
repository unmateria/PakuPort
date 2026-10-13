"""Control negativo del juez de fidelidad: se estropea el arte a proposito y tiene que salir ROJO.

Un juez que solo se ha visto verde no ha demostrado nada. Este rompe el contrato de dos maneras
distintas, cada una apuntando a una metrica, y exige que el juez lo cace las dos veces:

  1. **Deriva.** Se le mete a un asset un desplazamiento enorme (`ax`), asi que su instancia HD
     acaba fuera de la pantalla. Es el modelo de "un sprite mal colocado", que en el remake de
     un juego de prueba se vio como sprites que se pisaban.
  2. **Regresion.** Se quita del contrato un asset que antes SI tenia arte. Es el modelo de
     "mejorar algo y romper otra cosa sin enterarse hasta la prueba manual".

Y entre medias comprueba lo obvio que nadie comprueba: que sin estropear nada el juez sale VERDE.
Si saliera rojo siempre, cazar los dos fallos no significaria nada.

  python tools/remake_kit/judge_fidelidad_control.py <id> --export <dir> --remake <dir>
"""

import argparse
import json
import os
import shutil
import subprocess
import sys

AQUI = os.path.dirname(os.path.abspath(__file__))


def juzga(id_, export, remake, ms):
    r = subprocess.run([sys.executable, os.path.join(AQUI, 'judge_fidelidad.py'), id_,
                        '--export', export, '--remake', remake, '--ms', str(ms)],
                       capture_output=True, text=True,
                       cwd=os.path.dirname(os.path.dirname(AQUI)))
    return r.returncode, r.stdout + r.stderr


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('id')
    ap.add_argument('--export', required=True)
    ap.add_argument('--remake', required=True)
    ap.add_argument('--ms', type=int, default=25000)
    a = ap.parse_args()

    runtime = os.path.join(a.remake, 'remake.json')
    historico = os.path.join(a.remake, 'informe.json')
    copia = runtime + '.control'
    copia_h = historico + '.control'
    shutil.copyfile(runtime, copia)
    if os.path.exists(historico):
        shutil.copyfile(historico, copia_h)

    fallos = []
    try:
        original = json.load(open(runtime, encoding='utf-8'))
        claves = sorted(original['assets'])
        if not claves:
            raise SystemExit('ROJO: remake.json no admite nada; no hay nada que estropear')

        print('== 1. sin estropear nada: el juez tiene que salir VERDE ==')
        rc, out = juzga(a.id, a.export, a.remake, a.ms)
        print('   ' + (out.strip().splitlines() or ['(sin salida)'])[-1])
        if rc != 0:
            fallos.append('el juez sale ROJO con el arte intacto: el liston no puede ser "todo no"')

        print('== 2. un asset desplazado fuera de la pantalla: tiene que salir ROJO (deriva) ==')
        roto = json.loads(json.dumps(original))
        roto['assets'][claves[0]]['ax'] = 4000
        json.dump(roto, open(runtime, 'w', encoding='utf-8'))
        rc, out = juzga(a.id, a.export, a.remake, a.ms)
        linea = next((l for l in out.splitlines() if l.startswith('deriva')), '(sin linea de deriva)')
        print('   ' + linea.strip())
        if rc == 0:
            fallos.append(f'con {claves[0]} desplazado 4000 px el juez sigue VERDE: no mide la deriva')

        print('== 3. un asset que pierde su arte: tiene que salir ROJO (regresion) ==')
        shutil.copyfile(copia, runtime)
        rc, _ = juzga(a.id, a.export, a.remake, a.ms)      # deja el historico al dia
        menos = json.loads(json.dumps(original))
        quitado = claves[0]
        del menos['assets'][quitado]
        json.dump(menos, open(runtime, 'w', encoding='utf-8'))
        rc, out = juzga(a.id, a.export, a.remake, a.ms)
        linea = next((l for l in out.splitlines() if 'regresion' in l), '(sin linea de regresion)')
        print('   ' + linea.strip())
        if rc == 0:
            fallos.append(f'quitando el arte de {quitado} el juez sigue VERDE: no mide la regresion')
    finally:
        shutil.move(copia, runtime)
        if os.path.exists(copia_h):
            shutil.move(copia_h, historico)

    if fallos:
        print('ROJO:')
        for f in fallos:
            print('  ' + f)
        return 1
    print('VERDE: el juez sale verde con el arte bueno y ROJO con cada uno de los dos destrozos.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
