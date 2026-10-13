"""Genera el indice de capacidades del kit de remakes DERIVANDOLO del codigo.

No es un documento a mano: se regenera. Si el codigo cambia y el indice no, `--check` lo
suspende, asi que no puede pudrirse en silencio — que es exactamente lo que le paso a una
lista de requisitos escrita a mano, que dio por "pendiente" media docena de cosas ya construidas
y por eso la intro de un juego de prueba se hizo a mano y a ciegas. Mismo patron que
`tools/gen_palancas.py`, y por la misma razon.

De donde sale cada dato:
    herramienta   de los ficheros .py del kit y de las carpetas de tooling que el kit encadena
    que hace      de la PRIMERA linea de su docstring de modulo (lo que el autor dijo que hace)
    entradas      de los `add_argument` de su argparse: nombre, si es obligatorio y su defecto
    verbos        de los `case` del despachador de src/PakuPort.Cli/RemakeKit.cs
    piezas        de las clases de PakuPort.Core que el kit usa, con su resumen XML

Lo que no tiene docstring sale marcado como SIN DOCUMENTAR: el indice no solo lista, tambien
senala lo que falta por explicar.

Uso:
    python tools/remake_kit/gen_indice.py            regenera docs/REMAKE-KIT-INDICE.md
    python tools/remake_kit/gen_indice.py --check    sale != 0 si lo generado difiere del disco
"""

import ast
import os
import re
import sys

AQUI = os.path.dirname(os.path.abspath(__file__))
RAIZ = os.path.dirname(os.path.dirname(AQUI))

# Las herramientas que el kit encadena, en el orden real del encadenado.
CADENA = [
    ('tools/blit_catalog/build_catalog.py', 'catalogo'),
    ('tools/blit_catalog/build_tracks.py', 'catalogo'),
    ('tools/player_id/find_player.py', 'catalogo'),
    ('tools/scene_hist/build_scenes.py', 'escenas'),
    ('tools/audio_catalog/build_sounds.py', 'audio'),
    ('tools/dossier/build_dossier.py', 'dossier'),
    ('tools/remake_kit/build_ref.py', 'kit'),
    ('tools/remake_kit/build_capas.py', 'kit'),
    ('tools/remake_kit/build_texto.py', 'kit'),
    ('tools/remake_kit/build_pendiente.py', 'kit'),
    ('tools/remake_kit/check_arte.py', 'kit'),
    ('tools/remake_kit/judge_fidelidad.py', 'kit'),
]

# Los jueces del kit: cada uno con su control negativo, que es lo que los hace valer.
JUECES = [
    ('tools/remake_kit/judge_check.py', 'la aduana rechaza los cinco defectos y admite lo bueno'),
    ('tools/remake_kit/judge_orden.py', 'orden de dibujo y persistencia, con el pintado invertido'),
    ('tools/remake_kit/judge_no_perturba.py', 'el remake no cambia la VRAM del invitado'),
]

# Piezas del motor de las que depende el kit, con el fichero donde vive cada una.
PIEZAS = [
    ('BlitProvenance', 'src/PakuPort.Core/BlitProvenance.cs'),
    ('BlitRawLog', 'src/PakuPort.Core/BlitRawLog.cs'),
    ('RemakeMap', 'src/PakuPort.Core/RemakeMap.cs'),
    ('InputTape', 'src/PakuPort.Core/InputTape.cs'),
    ('AudioEventLog', 'src/PakuPort.Core/AudioEventLog.cs'),
]


def resumen_py(ruta):
    """La primera frase del docstring de modulo: lo que el autor dijo que hace."""
    try:
        arbol = ast.parse(open(ruta, encoding='utf-8').read())
    except (OSError, SyntaxError):
        return None, []
    doc = ast.get_docstring(arbol)
    texto = doc.strip().split('\n')[0] if doc else None
    return texto, argumentos(arbol)


def argumentos(arbol):
    """Los add_argument del argparse: nombre, obligatorio y defecto."""
    out = []
    for nodo in ast.walk(arbol):
        if not (isinstance(nodo, ast.Call) and isinstance(nodo.func, ast.Attribute)
                and nodo.func.attr == 'add_argument'):
            continue
        if not nodo.args or not isinstance(nodo.args[0], ast.Constant):
            continue
        nombre = nodo.args[0].value
        req, defecto = False, None
        for kw in nodo.keywords:
            if kw.arg == 'required' and isinstance(kw.value, ast.Constant):
                req = bool(kw.value.value)
            if kw.arg == 'default' and isinstance(kw.value, ast.Constant):
                defecto = kw.value.value
        if not nombre.startswith('-'):
            req = True
        out.append((nombre, req, defecto))
    return out


def resumen_cs(ruta, clase):
    """El <summary> XML de la clase, aplanado a una frase."""
    try:
        texto = open(os.path.join(RAIZ, ruta), encoding='utf-8-sig').read()
    except OSError:
        return None
    m = re.search(r'///\s*<summary>(.*?)</summary>', texto, re.S)
    if not m:
        return None
    limpio = re.sub(r'^\s*///\s?', '', m.group(1), flags=re.M)
    limpio = re.sub(r'<[^>]+>', '', limpio)
    limpio = ' '.join(limpio.split())
    return limpio.split('. ')[0].strip(' .') + '.'


def verbos():
    """Los subcomandos que el despachador de RemakeKit.cs reconoce de verdad."""
    ruta = os.path.join(RAIZ, 'src/PakuPort.Cli/RemakeKit.cs')
    texto = open(ruta, encoding='utf-8-sig').read()
    m = re.search(r'esVerbo\s*=\s*verbo is (.+?);', texto, re.S)
    if not m:
        return []
    return re.findall(r'"(\w+)"', m.group(1))


def genera():
    L = ['# Kit de remakes — indice de capacidades (GENERADO)',
         '',
         'No editar a mano: sale de `python tools/remake_kit/gen_indice.py`, que lo deriva del',
         'codigo. `--check` suspende si el codigo cambia y este fichero no. Existe porque una nota',
         'de estado que nadie vuelve a comprobar manda al siguiente a reconstruir lo que ya esta —',
         'y eso costo un remake entero (ver `REMAKE-KIT.md`).',
         '',
         'El **como se usa** esta en [`REMAKE-KIT.md`](REMAKE-KIT.md). Esto es el inventario.',
         '',
         '## Verbos', '']
    v = verbos()
    L.append('`pakuport remake` + ' + ', '.join(f'`{x}`' for x in v) if v
             else '_No se pudo leer el despachador._')
    L += ['', 'Sin subcomando dice en que paso estas y cual es el comando siguiente.', '',
          '## Herramientas encadenadas', '']

    for rel, grupo in CADENA:
        ruta = os.path.join(RAIZ, rel)
        if not os.path.exists(ruta):
            L.append(f'### `{rel}`  \n**FALTA EN DISCO** (grupo: {grupo})\n')
            continue
        texto, args = resumen_py(ruta)
        L.append(f'### `{rel}`')
        L.append('')
        L.append(texto or '**SIN DOCUMENTAR** (no tiene docstring de modulo).')
        if args:
            L.append('')
            L.append('| entrada | obligatoria | defecto |')
            L.append('|---|---|---|')
            for nombre, req, defecto in args:
                d = '—' if defecto is None else f'`{defecto}`'
                L.append(f'| `{nombre}` | {"si" if req else "no"} | {d} |')
        L.append('')

    L += ['## Jueces', '',
          'Ninguno entra en verde sin haberse visto ROJO: cada uno lleva su control negativo.', '']
    for rel, que in JUECES:
        estado = '' if os.path.exists(os.path.join(RAIZ, rel)) else '  **FALTA EN DISCO**'
        L.append(f'- `{rel}` — {que}.{estado}')
    L += ['', '## Piezas del motor de las que depende', '']
    for clase, rel in PIEZAS:
        s = resumen_cs(rel, clase)
        L.append(f'- **`{clase}`** (`{rel}`) — {s or "**SIN DOCUMENTAR**"}')
    L.append('')
    return '\n'.join(L)


def main():
    destino = os.path.join(RAIZ, 'docs', 'REMAKE-KIT-INDICE.md')
    nuevo = genera()
    if '--check' in sys.argv:
        viejo = open(destino, encoding='utf-8').read() if os.path.exists(destino) else ''
        if viejo.replace('\r\n', '\n') != nuevo:
            print('ROJO: docs/REMAKE-KIT-INDICE.md esta desfasado respecto al codigo.')
            print('      Regeneralo:  python tools/remake_kit/gen_indice.py')
            return 1
        print('VERDE: el indice coincide con el codigo.')
        return 0
    os.makedirs(os.path.dirname(destino), exist_ok=True)
    with open(destino, 'w', encoding='utf-8', newline='\n') as fh:
        fh.write(nuevo)
    print(f'-> {destino}')
    return 0


if __name__ == '__main__':
    sys.exit(main())
