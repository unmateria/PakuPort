"""Decodifica los pixeles de cada blit REEMPLAZABLE y los guarda como imagen de referencia.

El problema que resuelve, medido en un juego de prueba con el kit ya montado: **1167 encargos y
cero con imagen**. Un juego que dibuja con `movs`/`stos` deja en `blit.log` la IDENTIDAD del
sprite —lo unico que el motor sabe reemplazar en ejecucion— pero no sus pixeles; las escrituras
sueltas dejan pixeles pero sin identidad. Las dos mitades estaban ahi y no se tocaban.

Con `PAKU_LOG_BLIT=3` el motor graba tambien las escrituras de las instrucciones de cadena,
envueltas en una pareja de marcas `A`...`B`, y la `B` lleva la clave del blit. Aqui se leen esos
grupos, se pintan sobre un lienzo del tamano de la pantalla y se recorta el rectangulo tocado:
eso es el sprite, con sus pixeles reales y su identidad real.

Lo que NO se hace, y es deliberado: adivinar. Un grupo sin `B` (un blit VRAM->VRAM, que no se
hashea para no cargar los latches de la EGA) se queda anonimo y se cuenta como tal.

Uso:
    python tools/remake_kit/build_ref.py --raw <blitraw.bin> --out <dir ref/>
                                         [--json <ref.json>] [--min-px 4] [--max 400]
"""

import argparse
import json
import os
import struct
import sys

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)
sys.path.insert(0, os.path.join(os.path.dirname(AQUI), 'blit_catalog'))
import build_catalog  # noqa: E402  — el decodificador de modos ya medido; no se reimplementa
import pngio  # noqa: E402


def lee_grupos(ruta):
    """Recorre blitraw.bin y devuelve (modo, escrituras, clave, prov) por grupo A..B.

    `escrituras` es una lista de (lin_o_addr, valor_o_planos). Las que caen fuera de una pareja
    A..B se devuelven como un grupo con clave None: son las escrituras sueltas de siempre.
    """
    datos = open(ruta, 'rb').read()
    if not datos.startswith(b'PBR1'):
        raise SystemExit(f'ROJO: {ruta} no empieza por PBR1')
    p = 4
    modo = 0
    abierto = None          # escrituras del grupo en curso (None = fuera de grupo)
    sueltas = []
    grupos = []
    n = len(datos)
    while p < n:
        tag = datos[p]
        p += 1
        if tag == ord('M'):
            modo = datos[p]
            p += 1
        elif tag == ord('W'):
            lin, val = struct.unpack_from('<IB', datos, p)
            p += 13                      # u32 lin + u8 val + u32 at + u32 dt
            (abierto if abierto is not None else sueltas).append((lin, val))
        elif tag == ord('P'):
            addr = struct.unpack_from('<I', datos, p)[0]
            p0, p1, p2, p3 = datos[p + 4:p + 8]
            p += 16                      # u32 addr + 4 planos + u32 at + u32 dt
            # Mismo empaquetado que read_blitraw: decode_byte espera los 4 planos en un entero.
            (abierto if abierto is not None else sueltas).append(
                (addr, p0 | (p1 << 8) | (p2 << 16) | (p3 << 24)))
        elif tag == ord('A'):
            if abierto:                  # grupo previo sin cerrar: sus escrituras son anonimas
                sueltas.extend(abierto)
            abierto = []
        elif tag == ord('B'):
            at = struct.unpack_from('<I', datos, p)[0]
            p += 4
            lc = datos[p]; p += 1
            clave = datos[p:p + lc].decode('latin1'); p += lc
            lp = datos[p]; p += 1
            prov = datos[p:p + lp].decode('latin1'); p += lp
            if abierto:
                if clave:
                    grupos.append((modo, abierto, clave, prov or None, at))
                else:
                    sueltas.extend(abierto)   # VRAM->VRAM: sin identidad, y se dice
            abierto = None
        else:
            raise SystemExit(f'ROJO: registro desconocido {tag!r} en el offset {p - 1}')
    if abierto:
        sueltas.extend(abierto)
    return grupos, sueltas


def rasteriza(modo, escrituras):
    """Convierte las escrituras de un grupo en (w, h, rgba) recortado a lo que se toco.

    Reutiliza el decodificador de modos del catalogo (`addr_to_xy`, `decode_byte`), que es el
    que ya esta medido contra el oraculo — CGA entrelazado, EGA/VGA planar y 256 colores
    lineal. Aqui solo se compone y se recorta.
    """
    celdas = {}
    for direccion, dato in escrituras:
        xy = build_catalog.addr_to_xy(direccion, modo)
        if xy is None:
            continue
        x, y, ppb = xy
        celdas[(x // ppb, y)] = dato
    if not celdas:
        return None
    comp = build_catalog.components(celdas)
    if not comp:
        return None
    mayor = max(comp, key=len)                    # el sprite es la mancha grande del grupo
    filas, x0, y0, w, h = build_catalog.mask_rows(mayor, modo)
    return filas, x0, y0, w, h


def huecos(filas):
    """Fraccion de pixeles del rectangulo que el juego dejo sin escribir ('.' en la mascara)."""
    tot = sum(len(f) for f in filas)
    return (sum(f.count('.') for f in filas) / tot) if tot else 0.0


def forma(w, h):
    """Que ES el blit, dicho sin adornos. Cambia por completo lo que se le puede pedir a nadie.

    `fila`: el juego compone el sprite UNA FILA POR BLIT (un juego de prueba hace esto con casi todo).
    Cada fila tiene su propia identidad, asi que el motor solo sabe reemplazar por filas: no hay
    forma de dar un sprite HD entero sin cambiar el modelo de reemplazo. Se dice aqui en vez de
    entregar 894 tiras de 1 pixel de alto como si fueran sprites.
    `pantalla`: ocupa el modo entero — un fondo o una lamina, que SI se reemplaza de una pieza.
    """
    if h == 1:
        return 'fila'
    if w >= 320 and h >= 200:
        return 'pantalla'
    return 'sprite'


def lee_paleta(ruta):
    """Paletas EFECTIVAS de la partida, cada una con el FOTOGRAMA en que entro en vigor.

    Sin paleta, un asset de 256 colores —que en VRAM son INDICES, no colores— se dibuja en gris
    usando el indice como nivel de gris, y quien recibe esa referencia no ve nada parecido a lo
    que sale en pantalla. Ese era el estado de TODAS las referencias del 13h.

    Y la paleta no se elige: se PREGUNTA. Un juego la reprograma constantemente (un juego de prueba
    registra 255 generaciones en un minuto, casi todas pasos de un fundido), asi que "la que mas
    duro" es una respuesta equivocada de dos maneras: la mas duradera suele ser la del ARRANQUE
    —16 colores del modo texto, que deja sin color todo indice mayor que 15— y, entre las de 256,
    gana cualquier paso de fundido con 190 entradas a negro. La buena es la que estaba puesta en
    el fotograma en que el juego dibujo ESE asset, y eso es un dato medido: `blit.log` trae
    `frame=N` en cada blit y aqui se reconstruye el fotograma inicial de cada generacion
    acumulando su duracion (`tras=Kf` = lo que duro la ANTERIOR).

    Devuelve [(frame_inicial, [(r,g,b), ...]), ...] ordenado, solo con las de 256 entradas.
    """
    if not ruta or not os.path.exists(ruta):
        return []
    gens, pendiente, frame = [], None, 0
    for linea in open(ruta, encoding='utf-8', errors='replace'):
        if '[PALETA]' not in linea:
            continue
        try:
            partes = linea.split()
            duro = int(partes[3].rstrip('f').split('=')[1])
            crudo = bytes.fromhex(partes[-1])
        except (IndexError, ValueError):
            continue
        if pendiente is not None:
            gens.append((frame, pendiente))
            frame += duro
        else:
            frame = duro                            # lo que duro la paleta de arranque
        pendiente = [tuple(crudo[i:i + 3]) for i in range(0, len(crudo), 3)]
    if pendiente is not None:
        gens.append((frame, pendiente))
    return [g for g in gens if len(g[1]) >= 256]


def paleta_en(gens, desde, hasta):
    """La paleta con la que el asset SE VIO: la que mas duro mientras estuvo en pantalla.

    No vale quedarse con la vigente en el fotograma del blit. Un juego dibuja la imagen con la
    paleta a NEGRO y luego la sube (fundido de entrada): en el instante exacto del blit la
    paleta correcta todavia no existe, y la referencia sale gris o negra — medido en un juego de prueba con la
    pantalla de menu. El asset se ve durante un intervalo, y lo que el jugador vio es la paleta
    que mas frames estuvo puesta dentro de el.
    """
    if not gens:
        return None
    mejor, mejor_dur = None, -1
    for i, (f0, pal) in enumerate(gens):
        f1 = gens[i + 1][0] if i + 1 < len(gens) else hasta
        solape = min(f1, hasta) - max(f0, desde)
        if solape > mejor_dur:
            mejor, mejor_dur = pal, solape
    return mejor if mejor is not None else gens[0][1]


def cortes_de_pantalla(ruta):
    """Fotogramas en que el juego repinta la pantalla entera: acotan cuanto se ve cada asset."""
    if not ruta or not os.path.exists(ruta):
        return []
    fr = set()
    for linea in open(ruta, encoding='utf-8', errors='replace'):
        octetos = frame = None
        for campo in linea.split():
            if campo.startswith('bytes='):
                try:
                    octetos = int(campo[6:])
                except ValueError:
                    pass
            elif campo.startswith('frame='):
                try:
                    frame = int(campo[6:])
                except ValueError:
                    pass
        if octetos and frame is not None and octetos >= 32000:
            fr.add(frame)
    return sorted(fr)


def lee_presentados(carpeta):
    """Muestreo de la pantalla TAL COMO SE VE: [(frame, ruta, w, h), ...] ordenado por frame.

    Nombre: `fNNN.f<frame>.<w>x<h>.rgba`, escrito por PAKU_PRESENT_DUMP_EVERY. El frame en el
    nombre es el mismo contador que trae `blit.log`, y es lo que permite cruzarlos.
    """
    if not carpeta or not os.path.isdir(carpeta):
        return []
    salida = []
    for nombre in os.listdir(carpeta):
        if not nombre.endswith('.rgba'):
            continue
        # `fNNN.f<frame>.<w>x<h>.rgba`. El campo del FOTOGRAMA es el penultimo antes del tamano;
        # el primero es sólo el numero de orden del volcado. Confundirlos hace que el cruce con
        # blit.log compare un indice (0..416) contra un contador de fotogramas (0..4160) y elija
        # imagenes de cualquier otro momento, sin fallar ni avisar.
        partes = nombre.split('.')
        if len(partes) < 4:
            continue
        try:
            campo = partes[-3]
            if not (campo.startswith('f') and campo[1:].isdigit()):
                continue
            frame = int(campo[1:])
            w, h = (int(v) for v in partes[-2].split('x'))
        except ValueError:
            continue
        salida.append((frame, os.path.join(carpeta, nombre), w, h))
    return sorted(salida)


def tam_pantalla(modo):
    """Geometria de la pantalla en ese modo, para no recortar de un fotograma de OTRO modo.

    La partida arranca en texto (640x400) y salta a graficos: un asset de 320x200 cabe de sobra
    dentro de un fotograma de texto, asi que sin este filtro se recortaria de la pantalla
    equivocada — y el recorte saldria plausible, que es la peor forma de estar mal.
    """
    if modo in build_catalog.EGA_MODES:
        bpl, alto = build_catalog.EGA_MODES[modo]
        return bpl * 8, alto
    if modo in (4, 5, 6):
        return 640 if modo == 6 else 320, 200
    return 320, 200


def recorta_presentado(presentados, desde, hasta, x, y, w, h, pantalla, indices):
    """El asset tal como se vio: se recorta su rectangulo del fotograma presentado.

    De los fotogramas muestreados mientras el asset estaba en pantalla se elige el de MAS
    colores distintos en su rectangulo. Un fundido de entrada empieza en negro y termina en la
    imagen buena, asi que "el mas rico" es el que el jugador reconoce como esa pantalla; y si no
    hubo fundido, todos empatan y da igual cual salga.

    Devuelve RGBA del recorte, o None si no hay ningun fotograma en ese intervalo.
    """
    mejor, mejor_riqueza = None, -1
    for frame, ruta, fw, fh in presentados:
        if frame < desde or frame >= hasta:
            continue
        if (fw, fh) != pantalla or x + w > fw or y + h > fh:
            continue
        datos = open(ruta, 'rb').read()
        if len(datos) != fw * fh * 4:
            continue
        corte = bytearray()
        colores = set()
        for fila in range(h):
            o = ((y + fila) * fw + x) * 4
            trozo = datos[o:o + w * 4]
            corte += trozo
            for i in range(0, len(trozo), 4):
                colores.add(trozo[i:i + 3])
        if not consistente(corte, indices, w, h):
            continue
        if len(colores) > mejor_riqueza:
            mejor, mejor_riqueza = bytes(corte), len(colores)
    return mejor


def consistente(corte, indices, w, h):
    """¿Este fotograma sigue enseñando ESTE asset, o ya hay algo pintado encima?

    Prueba objetiva, sin umbrales que ajustar: los pixeles del asset son INDICES de paleta, asi
    que en el fotograma en que ese asset se ve, dos pixeles con el mismo indice tienen a la
    fuerza el mismo color. En cuanto el juego pinta cualquier otra cosa encima esa relacion se
    rompe. Sirve igual durante un fundido —una paleta oscura sigue siendo una funcion del
    indice— y por eso se combina con quedarse luego el fotograma de MAS colores: el fundido
    terminado. Sin esto, el criterio de "el mas rico del intervalo" se traia la pantalla
    de despues (medido en un juego de prueba: la portada salia siendo el menu).
    """
    de_indice = {}
    for fila in range(h):
        for col in range(w):
            idx = indices[fila][col]
            if idx is None:                       # pixel que el juego no escribio: no dice nada
                continue
            o = (fila * w + col) * 4
            color = bytes(corte[o:o + 3])
            visto = de_indice.get(idx)
            if visto is None:
                de_indice[idx] = color
            elif visto != color:
                return False
    return True


def indices_de(filas, modo):
    """La mascara de un asset como indices por pixel (None = el juego no escribio ahi)."""
    cpp = build_catalog.chars_per_pixel(modo)
    salida = []
    for fila in filas:
        fila_idx = []
        for i in range(0, len(fila), cpp):
            ch = fila[i:i + cpp]
            fila_idx.append(None if ch[0] == '.' else int(ch, 16))
        salida.append(fila_idx)
    return salida


def frames_por_clave(ruta):
    """Primer fotograma en que se dibuja cada clave, leido de blit.log."""
    if not ruta or not os.path.exists(ruta):
        return {}
    fr = {}
    for linea in open(ruta, encoding='utf-8', errors='replace'):
        clave = frame = None
        for campo in linea.split():
            if campo.startswith('fnv='):
                clave = campo[4:]
            elif campo.startswith('frame='):
                try:
                    frame = int(campo[6:])
                except ValueError:
                    pass
        if clave is not None and frame is not None and clave not in fr:
            fr[clave] = frame
    return fr


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--raw', required=True)
    ap.add_argument('--out', required=True)
    ap.add_argument('--json')
    ap.add_argument('--paleta', help='logs/paleta.log de la captura (color real del 13h)')
    ap.add_argument('--present', help='carpeta con el muestreo de fotogramas presentados')
    ap.add_argument('--blit-log', dest='blit_log',
                    help='logs/blit.log: da el fotograma de cada blit, y con el la paleta que '
                         'estaba puesta cuando ese asset se dibujo')
    ap.add_argument('--min-px', type=int, default=4)
    ap.add_argument('--max', type=int, default=4000)
    a = ap.parse_args()

    if not os.path.exists(a.raw):
        print(f'ROJO: no existe {a.raw} (¿la captura corrio con PAKU_LOG_BLIT=3?)')
        return 2
    grupos, sueltas = lee_grupos(a.raw)
    os.makedirs(a.out, exist_ok=True)
    gens_paleta = lee_paleta(a.paleta)
    frames = frames_por_clave(a.blit_log)
    cortes = cortes_de_pantalla(a.blit_log)
    presentados = lee_presentados(a.present)
    de_pantalla = reconstruidas = 0
    if not gens_paleta:
        print('AVISO: sin paleta capturada; las referencias de 256 colores saldran en GRIS y no '
              'se pareceran a lo que ve el jugador (falta logs/paleta.log de la captura).')
    elif not frames:
        print(f'AVISO: {len(gens_paleta)} paletas capturadas pero sin blit.log para saber cual '
              'tocaba a cada asset; se usa la primera y los colores pueden salir cambiados.')
    else:
        print(f'paleta: {len(gens_paleta)} generaciones de 256 colores, atadas al fotograma de '
              f'cada blit ({len(frames)} claves fechadas).')

    # Un mismo sprite se dibuja muchas veces; nos quedamos con UNA muestra por clave, la mayor
    # (la que menos recortada esta por el borde de la pantalla).
    mejor = {}
    for modo, escrituras, clave, prov, at in grupos:
        r = rasteriza(modo, escrituras)
        if r is None:
            continue
        filas, x0, y0, w, h = r
        if w * h < a.min_px:
            continue
        previo = mejor.get(clave)
        if previo is None or w * h > previo['w'] * previo['h']:
            mejor[clave] = dict(clave=clave, prov=prov, modo=modo, filas=filas,
                                w=w, h=h, x=x0, y=y0, at=f'{at >> 16:04X}:{at & 0xFFFF:04X}',
                                veces=(previo or {}).get('veces', 0) + 1)
        else:
            previo['veces'] += 1

    escritos = {}
    for n, (clave, info) in enumerate(sorted(mejor.items(), key=lambda kv: -kv[1]['veces'])):
        if n >= a.max:
            break
        # El PNG lo escribe el propio catalogo: su paleta por modo es la medida (EGA del BIOS,
        # CGA, y para el 13h la EFECTIVA capturada del DAC). Reusarla evita inventar aqui una
        # paleta paralela que luego nadie sabria de donde salio.
        nombre = 'ref-' + clave.replace(':', '-').replace('@', '-at-') + '.png'
        desde = frames.get(clave, 0)
        hasta = next((c for c in cortes if c > desde), 1 << 30)
        # Primero, lo que SE VIO. Si no hay fotograma muestreado mientras este asset estaba en
        # pantalla, se reconstruye desde los indices con la paleta del intervalo, que es lo mejor
        # que se puede hacer sin haberlo visto.
        corte = recorta_presentado(presentados, desde, hasta,
                                   info['x'], info['y'], info['w'], info['h'],
                                   tam_pantalla(info['modo']),
                                   indices_de(info['filas'], info['modo']))
        if corte is not None:
            pngio.escribir(os.path.join(a.out, nombre), info['w'], info['h'], corte)
            de_pantalla += 1
        else:
            build_catalog.write_png(os.path.join(a.out, nombre), info['filas'], info['modo'],
                                    paleta_en(gens_paleta, desde, hasta))
            reconstruidas += 1
        escritos[clave] = {'png': nombre, 'w': info['w'], 'h': info['h'],
                           'modo': info['modo'], 'prov': info['prov'],
                           'rutina': info['at'], 'veces': info['veces'],
                           'x': info['x'], 'y': info['y'],
                           'forma': forma(info['w'], info['h']),
                           # Fraccion del rectangulo que el juego NO escribio: son los huecos por
                           # los que se ve lo que hay debajo. Decide si el arte HD necesita canal
                           # alfa: exigirselo a un fondo de pantalla completa seria absurdo, y no
                           # exigirselo a un sprite recortado tapa el escenario.
                           'huecos': round(huecos(info['filas']), 4)}

    print(f'referencias: {de_pantalla} recortadas de la PANTALLA REAL, {reconstruidas} reconstruidas por paleta')
    if a.json:
        with open(a.json, 'w', encoding='utf-8') as fh:
            json.dump({'version': 1, 'fuente': a.raw, 'refs': escritos,
                       'grupos': len(grupos), 'claves': len(mejor),
                       'escrituras_anonimas': len(sueltas)}, fh, indent=1, ensure_ascii=False)

    reparto = {}
    for v in escritos.values():
        reparto[v['forma']] = reparto.get(v['forma'], 0) + 1
    print(f'{len(grupos)} blits con identidad · {len(mejor)} claves distintas · '
          f'{len(escritos)} imagenes de referencia · {len(sueltas)} escrituras anonimas')
    print('  reparto por forma: ' + ', '.join(f'{k}={v}' for k, v in sorted(reparto.items())))
    if reparto.get('fila', 0) > len(escritos) // 2:
        print('Este juego compone sus sprites FILA A FILA y cada fila tiene identidad propia, '
              'asi que NO trabajes con estas claves: son tiras de un pixel de alto. Las figuras '
              'enteras salen de build_grupos.py (claves "grupo:...") y son las que hay que '
              'dibujar.')
    if not grupos:
        print('AVISO: ningun blit trae identidad. Si el juego dibuja con movs/stos, la captura '
              'no corrio con PAKU_LOG_BLIT=3.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
