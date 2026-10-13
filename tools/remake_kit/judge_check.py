"""Juez de la ADUANA: fabrica arte deliberadamente malo y exige que se rechace, uno a uno.

Un validador que nunca ha dicho que no es un sello de goma. Esto le pone delante los cinco
defectos que el kit promete cazar —y ademas UNO BUENO, porque un validador que lo rechaza todo
tampoco vale— y comprueba que el veredicto es el correcto en los seis casos.

Los defectos no son inventados: son los que se pagaron caros.
  * animacion a medias   -> un piloto anterior de remake ("faltan la mitad de los sprites")
  * borde con croma      -> los recortes por celda del sprite-sheet
  * proporcion torcida   -> un sprite que se sale de su caja y baila respecto al fondo
  * sin transparencia    -> tapa el escenario al componer
  * PNG ilegible         -> lo mas basico, y si no se comprueba se cuela

  python tools/remake_kit/judge_check.py         VERDE / ROJO, y sale != 0 si falla
"""

import json
import os
import shutil
import subprocess
import sys
import tempfile

AQUI = os.path.dirname(os.path.abspath(__file__))
RAIZ = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
sys.path.insert(0, AQUI)
import pngio  # noqa: E402


def png(ruta, w, h, color=(200, 30, 30, 255), croma_borde=False):
    rgba = bytearray()
    for y in range(h):
        for x in range(w):
            borde = croma_borde and (x in (0, w - 1) or y in (0, h - 1))
            rgba += bytes((0, 255, 0, 255) if borde else color)
    pngio.escribir(ruta, w, h, rgba)


def main():
    tmp = tempfile.mkdtemp(prefix='judge_check_')
    hd = os.path.join(tmp, 'hd')
    os.makedirs(hd)

    # Un encargo por defecto + uno sano. Los tamanos del "original" son los que fija el contrato.
    assets = {
        'BUENO':     {'tex': 'hd/bueno.png',     'ax': 0, 'ay': 0, 'w': 32, 'h': 16},
        'PROPORCION': {'tex': 'hd/proporcion.png', 'ax': 0, 'ay': 0, 'w': 32, 'h': 16},
        'OPACO':     {'tex': 'hd/opaco.png',     'ax': 0, 'ay': 0, 'w': 32, 'h': 16},
        'CROMA':     {'tex': 'hd/croma.png',     'ax': 0, 'ay': 0, 'w': 32, 'h': 16},
        'ROTO':      {'tex': 'hd/roto.png',      'ax': 0, 'ay': 0, 'w': 32, 'h': 16},
        'ANIM_A':    {'tex': 'hd/anim_a.png',    'ax': 0, 'ay': 0, 'w': 32, 'h': 16},
        'ANIM_B':    {'tex': 'hd/anim_b.png',    'ax': 0, 'ay': 0, 'w': 32, 'h': 16},
    }
    with open(os.path.join(tmp, 'manifiesto.json'), 'w', encoding='utf-8') as fh:
        json.dump({'version': 1, 'juego': 'judge', 'assets': assets}, fh)

    # ANIM_A y ANIM_B son la misma animacion: entregar solo A tiene que tumbar a los dos.
    encargos = [{'clave': k, 'w': 32, 'h': 16,
                 'animacion': ({'id': 'anim-000', 'fotograma': 0, 'de': 2}
                               if k in ('ANIM_A', 'ANIM_B') else None)}
                for k in assets]
    with open(os.path.join(tmp, 'pendiente.json'), 'w', encoding='utf-8') as fh:
        json.dump({'encargos': encargos}, fh)

    png(os.path.join(hd, 'bueno.png'), 128, 64)                       # 4x, misma proporcion
    png(os.path.join(hd, 'proporcion.png'), 128, 20)                  # achatado a proposito
    png(os.path.join(hd, 'opaco.png'), 128, 64)                       # se reescribe sin alfa
    png(os.path.join(hd, 'croma.png'), 128, 64, croma_borde=True)
    open(os.path.join(hd, 'roto.png'), 'wb').write(b'no soy un png')
    png(os.path.join(hd, 'anim_a.png'), 128, 64)                      # su hermano NO se entrega

    # "opaco": PNG RGB (tipo 2), sin canal alfa ni tRNS.
    import struct
    import zlib
    crudo = bytearray()
    for _ in range(64):
        crudo.append(0)
        crudo += bytes((200, 30, 30)) * 128

    def trozo(tag, cuerpo):
        return (struct.pack('>I', len(cuerpo)) + tag + cuerpo
                + struct.pack('>I', zlib.crc32(tag + cuerpo) & 0xFFFFFFFF))
    open(os.path.join(hd, 'opaco.png'), 'wb').write(
        b'\x89PNG\r\n\x1a\n'
        + trozo(b'IHDR', struct.pack('>IIBBBBB', 128, 64, 8, 2, 0, 0, 0))
        + trozo(b'IDAT', zlib.compress(bytes(crudo)))
        + trozo(b'IEND', b''))

    r = subprocess.run([sys.executable, os.path.join(AQUI, 'check_arte.py'), 'judge',
                        '--remake', tmp], capture_output=True, text=True)
    salida = r.stdout + r.stderr
    print(salida)

    fallos = []
    esperado = {
        'hd/proporcion.png': 'proporcion',
        'hd/opaco.png': 'transparencia',
        'hd/croma.png': 'croma',
        'hd/roto.png': 'PNG',
        'anim-000': 'animacion incompleta',
    }
    for nombre, pista in esperado.items():
        linea = next((l for l in salida.splitlines() if l.strip().startswith(nombre)), None)
        if linea is None:
            fallos.append(f'{nombre}: NO fue rechazado (deberia)')
        elif pista.lower() not in linea.lower():
            fallos.append(f'{nombre}: rechazado por el motivo equivocado -> {linea.strip()}')

    admitidos = json.load(open(os.path.join(tmp, 'remake.json'), encoding='utf-8'))['assets']
    if 'BUENO' not in admitidos:
        fallos.append('BUENO: el arte correcto fue rechazado (el liston no puede ser "todo no")')
    for malo in ('PROPORCION', 'OPACO', 'CROMA', 'ROTO', 'ANIM_A'):
        if malo in admitidos:
            fallos.append(f'{malo}: se colo en remake.json')
    if r.returncode == 0:
        fallos.append('check_arte devolvio 0 habiendo rechazos')

    shutil.rmtree(tmp, ignore_errors=True)
    if fallos:
        print('ROJO:')
        for f in fallos:
            print('  ' + f)
        return 1
    print(f'VERDE: los 5 defectos rechazados con su motivo, y el arte bueno admitido '
          f'({len(admitidos)}/7 en remake.json)')
    return 0


if __name__ == '__main__':
    sys.exit(main())
