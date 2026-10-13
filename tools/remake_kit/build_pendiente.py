"""Convierte lo que el motor MIDIO en un encargo que se puede ejecutar sin interpretar nada.

Entra: el catalogo (assets/instancias/tracks), las escenas y el jugador.
Sale:  `ref/` (una imagen por asset y una lamina por animacion),
       `pendiente.json` (LA LISTA DE TRABAJO) y
       `manifiesto.json` (el contrato, con el hueco `tex` que el arte tiene que rellenar).

La regla que gobierna este fichero: **el encargo no opina**. Cada campo sale de una medida —
el tamano, la escena, cuantas veces se dibuja, que hermanos tiene en su animacion— y lo que no
se ha podido medir se dice con su motivo (`ref: null` + `motivo`), nunca se rellena con algo
plausible. Un encargo con un dato inventado es peor que un encargo incompleto: el incompleto se
ve, el inventado se ejecuta.

DOS FAMILIAS DE ASSET, y conviene saber por que, porque explican los huecos del informe:

  * **rects** (`movs`/`stos`): el motor conoce su ORIGEN, asi que tienen identidad estable
    (`fnv` o `file:FICH@0xOFF`) y son los UNICOS que el motor sabe reemplazar en ejecucion
    (`RemakeMap.Note` solo se llama desde el camino de las instrucciones de cadena).
    A cambio, en el log no viajan sus pixeles.
  * **assets de pixel** (escrituras sueltas `mov`+bucle, p.ej. un juego de prueba entero): el catalogo
    SI los decodifica a PNG, pero no tienen origen, asi que hoy no hay clave por la que el motor
    pueda reconocerlos.

Se cruzan por `at` (el CS:IP de la rutina que dibuja): si el mismo sitio del programa produce
las dos cosas, la referencia de pixeles del uno vale como referencia del otro. Cuando el cruce
no da, se dice en el informe con su cuenta — es la medida honesta de cuanto del encargo se
puede ilustrar hoy.

Uso:
    python tools/remake_kit/build_pendiente.py <id> --catalog <dir> --out <dir>
                                               [--escenas <f.json>] [--max-ref 400]
"""

import argparse
import json
import os
import shutil
import sys
from collections import defaultdict

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)
import pngio  # noqa: E402


def carga_json(ruta, defecto=None):
    if not ruta or not os.path.exists(ruta):
        return defecto
    with open(ruta, encoding='utf-8') as fh:
        return json.load(fh)


def carga_jsonl(ruta):
    if not os.path.exists(ruta):
        return []
    filas = []
    with open(ruta, encoding='utf-8') as fh:
        for linea in fh:
            linea = linea.strip()
            if linea:
                filas.append(json.loads(linea))
    return filas


def escena_de(t, escenas):
    """Numero de escena que contiene el tick `t`, o None si no hay segmentacion."""
    for e in escenas:
        if e['t_inicio'] <= t < e['t_fin']:
            return e['escena']
    return None


def agrupa_animaciones(tracks):
    """Une en UNA animacion los assets que aparecen juntos en el recorrido de una entidad.

    Un track es una entidad seguida en el tiempo; los assets que se alternan en el mismo track
    son los fotogramas de su animacion. Dos tracks que comparten un asset son la misma entidad
    vista dos veces (dos vidas del mismo sprite), asi que sus assets se funden — por eso esto es
    una union de conjuntos y no un simple `group by track`.
    """
    padre = {}

    def raiz(x):
        padre.setdefault(x, x)
        while padre[x] != x:
            padre[x] = padre[padre[x]]
            x = padre[x]
        return x

    def une(a, b):
        ra, rb = raiz(a), raiz(b)
        if ra != rb:
            padre[ra] = rb

    orden = {}          # asset -> primer instante en que se le ve dentro de un track
    for tr in tracks:
        assets = tr.get('assets') or []
        for i, a in enumerate(assets):
            raiz(a)
            orden.setdefault(a, (tr.get('birth', 0), i))
            if i:
                une(assets[0], a)

    grupos = defaultdict(list)
    for a in padre:
        grupos[raiz(a)].append(a)

    animaciones = []
    for miembros in grupos.values():
        if len(miembros) < 2:
            continue                    # un solo fotograma no es una animacion
        miembros.sort(key=lambda a: orden.get(a, (0, 0)))
        animaciones.append(miembros)
    animaciones.sort(key=lambda g: -len(g))
    return animaciones


def lamina(rutas, destino, columnas=8, margen=2):
    """Pega los fotogramas de una animacion en UNA imagen, en su orden real.

    Sin rejilla dibujada y con las celdas en posiciones conocidas: es el formato que se
    fijo para el arte (una sola lamina, no N encargos sueltos), y aqui sirve para lo mismo por
    el otro lado — quien mire la referencia ve la animacion entera de una vez, que es justo lo
    que no se vio en la intro que fallo.
    """
    imgs = []
    for r in rutas:
        try:
            w, h, rgba, _ = pngio.leer(r)
            imgs.append((w, h, rgba))
        except Exception:
            pass
    if not imgs:
        return None
    cw = max(i[0] for i in imgs) + margen * 2
    ch = max(i[1] for i in imgs) + margen * 2
    cols = min(columnas, len(imgs))
    filas = (len(imgs) + cols - 1) // cols
    W, H = cw * cols, ch * filas
    lienzo = bytearray(W * H * 4)               # transparente: el fondo no es dato
    for n, (w, h, rgba) in enumerate(imgs):
        ox = (n % cols) * cw + margen
        oy = (n // cols) * ch + margen
        for y in range(h):
            src = y * w * 4
            dst = ((oy + y) * W + ox) * 4
            lienzo[dst:dst + w * 4] = rgba[src:src + w * 4]
    pngio.escribir(destino, W, H, lienzo)
    return os.path.basename(destino)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('id')
    ap.add_argument('--catalog', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--escenas')
    ap.add_argument('--refs', help='ref.json de build_ref.py: pixeles atados a su identidad')
    ap.add_argument('--grupos', help='grupos.json: figuras compuestas por varios blits de una '
                                     'misma rutina, la unica forma de reemplazar un sprite en un '
                                     'juego que lo dibuja fila a fila')
    ap.add_argument('--capas', help='capas.json de build_capas.py: orden de dibujo y persistencia')
    ap.add_argument('--texto', help='texto.json de build_texto.py: glifos y rutinas procedurales')
    ap.add_argument('--max-ref', type=int, default=400,
                    help='tope de imagenes de referencia copiadas (las mas vistas primero)')
    a = ap.parse_args()

    cat = carga_json(os.path.join(a.catalog, 'assets.json'))
    if cat is None:
        print(f'ROJO: no hay {a.catalog}/assets.json — ¿corriste `remake capture` y `study`?')
        return 2
    instancias = carga_jsonl(os.path.join(a.catalog, 'instances.jsonl'))
    tracks = (carga_json(os.path.join(a.catalog, 'tracks.json'), {}) or {}).get('tracks', [])
    jugador = carga_json(os.path.join(a.catalog, 'player.json'), {}) or {}
    escenas = (carga_json(a.escenas, {}) or {}).get('escenas', [])

    ref_dir = os.path.join(a.out, 'ref')
    hd_dir = os.path.join(a.out, 'hd')
    os.makedirs(ref_dir, exist_ok=True)
    os.makedirs(hd_dir, exist_ok=True)

    assets = {x['id']: x for x in cat.get('assets', [])}
    rects = cat.get('rects', [])
    # Los dibujos logicos entran como un asset mas: llevan clave ("grupo:XXXXXXXX"), rectangulo e
    # imagen igual que cualquier otro, asi que el encargo, el manifiesto y la aduana los tratan
    # sin saber que son distintos. El motor los reconoce por el prefijo.
    grupos_json = carga_json(a.grupos, {}) or {}
    rects = list(rects) + list(grupos_json.get('rects', []))

    # --- donde y cuando se ve cada asset de pixel ---
    por_asset = defaultdict(list)
    for ins in instancias:
        por_asset[ins['asset']].append(ins)
    escenas_de_asset = {}
    for aid, lista in por_asset.items():
        vistas = {escena_de(i['t'], escenas) for i in lista}
        escenas_de_asset[aid] = sorted(v for v in vistas if v is not None)

    # --- animaciones, y el fotograma que ocupa cada asset dentro de la suya ---
    animaciones = agrupa_animaciones(tracks)
    anim_de = {}
    for n, miembros in enumerate(animaciones):
        for i, aid in enumerate(miembros):
            anim_de[aid] = (n, i, miembros)

    # --- etiqueta semantica que ya esta medida: quien responde al teclado ES el jugador ---
    papel_de_track = {f['id']: f.get('label') for f in jugador.get('ranking', [])}
    papel_de_asset = {}
    for tr in tracks:
        papel = papel_de_track.get(tr.get('id'))
        if papel and papel != 'otro':
            for aid in tr.get('assets') or []:
                papel_de_asset.setdefault(aid, papel)

    # --- referencias en disco ---
    orden = sorted(assets.values(), key=lambda x: -x.get('appearances', 0))
    ref_de = {}
    for x in orden[:a.max_ref]:
        origen = os.path.join(a.catalog, x.get('png', ''))
        if not x.get('png') or not os.path.exists(origen):
            continue
        nombre = f'{x["id"]}.png'
        shutil.copyfile(origen, os.path.join(ref_dir, nombre))
        ref_de[x['id']] = f'ref/{nombre}'

    laminas = {}
    for n, miembros in enumerate(animaciones):
        rutas = [os.path.join(a.out, ref_de[m]) for m in miembros if m in ref_de]
        if len(rutas) < 2:
            continue
        nom = lamina(rutas, os.path.join(ref_dir, f'anim-{n:03d}.png'))
        if nom:
            laminas[n] = f'ref/{nom}'

    # --- pixeles del blit, atados a su identidad POR CONSTRUCCION (marcas A..B del nivel 3) ---
    refs = (carga_json(a.refs, {}) or {}).get('refs', {})
    refs.update(grupos_json.get('refs', {}))
    capas = (carga_json(a.capas, {}) or {}).get('capas', {})
    texto = carga_json(a.texto, {}) or {}
    glifos = set(texto.get('glifos', []))
    # De cada glifo, la linea donde sale: el encargo tiene que ENSENAR la palabra, no la letra.
    linea_de = {}
    for ln in texto.get('lineas', []):
        for g in ln['glifos']:
            linea_de.setdefault(g, ln)

    entradas = []
    for r in rects:
        fnv = r.get('fnv')
        fich = r.get('file')
        clave = f'file:{fich}' if fich else fnv          # la procedencia manda sobre el hash
        # build_ref indexa por la clave cruda que emite el motor (el fnv, o el relleno del stos);
        # el manifiesto la prefija con `file:` cuando hay procedencia, asi que se busca por las dos.
        px = refs.get(fnv) or (refs.get(clave) if clave else None)
        ref = f'ref/{px["png"]}' if px else None

        anim = None
        info = anim_de.get(fnv)
        if info:
            n, i, miembros = info
            anim = {
                'id': f'anim-{n:03d}',
                'fotograma': i,
                'de': len(miembros),
                'lamina': laminas.get(n),
                'hermanos': [ref_de.get(m) for m in miembros],
            }

        entradas.append({
            'clave': clave,
            'identidad': 'procedencia' if fich else 'hash',
            'ref': ref,
            'motivo': None if ref else
                      'el motor conoce su origen pero no ha podido decodificar sus pixeles: o el '
                      'blit fue VRAM->VRAM (no se hashea el origen, cargaria los latches de la '
                      'EGA) o la captura no corrio con PAKU_LOG_BLIT=3.',
            'w': px['w'] if px else None,
            'h': px['h'] if px else None,
            'modo': px['modo'] if px else None,
            # `fila` = el juego compone el sprite una fila por blit, asi que esta entrada NO es
            # un sprite entero. Va delante para que nadie dibuje una nave donde se pide una tira.
            'forma': px['forma'] if px else None,
            # Si el original tiene huecos, el arte HD los necesita: por ahi se ve el escenario.
            'necesita_alfa': bool(px and px.get('huecos', 0) > 0.01),
            'bytes': r.get('bytes'),
            'veces': r.get('count', 0),
            'rutina': r.get('at'),
            'animacion': anim,
            # Medido, no supuesto: en que capa va, a quien tapa, quien lo tapa, y si el juego
            # repinta el fondo debajo (`transitorio`) o el dibujo se queda (`persistente`).
            'capa': cp.get('capa') if (cp := capas.get(clave) or capas.get(fnv) or {}) else None,
            'tapa_a': cp.get('tapa_a', []),
            'tapado_por': cp.get('tapado_por', []),
            'persistencia': cp.get('persistencia'),
            'escenas': escenas_de_asset.get(fnv, []),
            'papel': papel_de_asset.get(fnv),
            # La etiqueta honesta. `glifo` NO se dibuja suelto: se dibuja la linea de `texto`.
            'clase': ('fondo' if (px and px['forma'] == 'pantalla')
                      else 'glifo' if clave in glifos or fnv in glifos
                      else 'fila' if (px and px['forma'] == 'fila')
                      else 'sprite'),
            'texto': (lambda ln: {'id': ln['id'], 'imagen': ln['imagen'],
                                  'glifos': len(ln['glifos'])}
                      )(linea_de[clave]) if clave in linea_de else (
                      (lambda ln: {'id': ln['id'], 'imagen': ln['imagen'],
                                   'glifos': len(ln['glifos'])})(linea_de[fnv])
                      if fnv in linea_de else None),
            'destino': f'hd/{clave.replace(":", "-").replace("@", "-at-").replace("0x", "")}.png',
        })

    entradas.sort(key=lambda e: -e['veces'])

    # --- el contrato que el motor ejecuta ---
    manifiesto = {
        'version': 1,
        'juego': a.id,
        'generado_por': 'tools/remake_kit/build_pendiente.py',
        'assets': {
            e['clave']: {'tex': e['destino'], 'ax': 0, 'ay': 0,
                         'w': e['w'] or 0, 'h': e['h'] or 0}
            for e in entradas
        },
        'meta': {e['clave']: {'veces': e['veces'], 'rutina': e['rutina'],
                              'animacion': (e['animacion'] or {}).get('id'),
                              'escenas': e['escenas']}
                 for e in entradas},
    }

    con_ref = sum(1 for e in entradas if e['ref'])
    por_forma = {}
    for e in entradas:
        if e['forma']:
            por_forma[e['forma']] = por_forma.get(e['forma'], 0) + 1
    pendiente = {
        'version': 1,
        'juego': a.id,
        'como_se_usa': (
            'Cada entrada es un encargo cerrado: mira `ref` (y `animacion.lamina` si la hay), '
            'dibuja la imagen al tamano `w`x`h` o a un multiplo exacto, y guardala EN `destino`. '
            'Si la entrada tiene `animacion`, hay que entregar TODOS sus fotogramas o ninguno. '
            'Cuando termines: `pakuport remake check ' + a.id + '`.'),
        'resumen': {
            'encargos': len(entradas),
            'con_referencia': con_ref,
            'sin_referencia': len(entradas) - con_ref,
            'por_forma': por_forma,
            'por_clase': {c: sum(1 for e in entradas if e['clase'] == c)
                          for c in sorted({e['clase'] for e in entradas})},
            'lineas_de_texto': len(texto.get('lineas', [])),
            'animaciones': len(animaciones),
            'escenas': len(escenas),
            'assets_de_pixel': len(assets),
        },
        'encargos': entradas,
        # Lo que el juego pinta con CODIGO: no hay asset que sustituir, y decirlo con la
        # direccion de su rutina es mas util que dejar un hueco mudo que parece arte que falta.
        'procedurales': texto.get('procedurales', {}),
    }

    with open(os.path.join(a.out, 'pendiente.json'), 'w', encoding='utf-8') as fh:
        json.dump(pendiente, fh, indent=1, ensure_ascii=False)
    with open(os.path.join(a.out, 'manifiesto.json'), 'w', encoding='utf-8') as fh:
        json.dump(manifiesto, fh, indent=1, ensure_ascii=False)

    print(f'{len(entradas)} encargos ({con_ref} con imagen de referencia, '
          f'{len(entradas) - con_ref} sin ella) · {len(animaciones)} animaciones · '
          f'{len(assets)} assets de pixel · {len(escenas)} escenas')
    if entradas and con_ref == 0:
        print('AVISO: ningun encargo tiene imagen de referencia. Es lo esperado si el juego '
              'dibuja con movs/stos y las escrituras sueltas van suprimidas: hace falta el '
              'cruce de pixeles del nucleo (PAKU_LOG_BLIT=3).')
    return 0


if __name__ == '__main__':
    sys.exit(main())
