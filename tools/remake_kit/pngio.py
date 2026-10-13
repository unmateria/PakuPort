"""Lectura y escritura de PNG con la libreria estandar, sin Pillow.

Por que a mano: el kit de remakes es PRODUCTO — lo ejecuta un tercero en su maquina — y la
regla de la casa es minimizar dependencias. `tools/blit_catalog/build_catalog.py` ya escribe
PNG a mano con zlib desde la fase de catalogo; lo que faltaba era LEERLOS, que es lo que
necesita la aduana (`pakuport remake check`) para juzgar el arte entregado.

Alcance deliberado: PNG no entrelazado de 8 bits por muestra en los cinco tipos de color
(gris, RGB, indexado, gris+alfa, RGBA), mas tRNS en indexado y gris/RGB. Todo lo demas se
RECHAZA con un mensaje que dice que es, en vez de devolver pixeles plausibles pero falsos —
que es la unica forma de fallo que este proyecto no se puede permitir.
"""

import struct
import zlib

FIRMA = b'\x89PNG\r\n\x1a\n'

# canales por tipo de color PNG (0 gris, 2 RGB, 3 indexado, 4 gris+alfa, 6 RGBA)
CANALES = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}


class PngError(Exception):
    """El fichero no es un PNG que sepamos leer. El mensaje dice exactamente por que."""


def _trozos(datos):
    """Genera (tag, cuerpo) recorriendo el fichero. Comprueba el CRC de cada trozo."""
    pos = len(FIRMA)
    while pos + 8 <= len(datos):
        (n,) = struct.unpack('>I', datos[pos:pos + 4])
        tag = datos[pos + 4:pos + 8]
        cuerpo = datos[pos + 8:pos + 8 + n]
        if len(cuerpo) != n:
            raise PngError(f'trozo {tag.decode("latin1")} truncado')
        (crc,) = struct.unpack('>I', datos[pos + 8 + n:pos + 12 + n])
        if zlib.crc32(tag + cuerpo) & 0xFFFFFFFF != crc:
            raise PngError(f'CRC malo en el trozo {tag.decode("latin1")}')
        yield tag, cuerpo
        pos += 12 + n


def _desfiltra(cruda, w, h, bpp):
    """Deshace los filtros por linea del PNG. `bpp` = bytes por pixel (>=1)."""
    ancho = w * bpp
    salida = bytearray(h * ancho)
    previa = bytearray(ancho)
    p = 0
    for y in range(h):
        if p >= len(cruda):
            raise PngError('faltan lineas de pixeles (fichero truncado)')
        filtro = cruda[p]
        linea = bytearray(cruda[p + 1:p + 1 + ancho])
        if len(linea) != ancho:
            raise PngError('linea de pixeles truncada')
        p += 1 + ancho
        if filtro == 1:      # Sub
            for i in range(bpp, ancho):
                linea[i] = (linea[i] + linea[i - bpp]) & 0xFF
        elif filtro == 2:    # Up
            for i in range(ancho):
                linea[i] = (linea[i] + previa[i]) & 0xFF
        elif filtro == 3:    # Average
            for i in range(ancho):
                izq = linea[i - bpp] if i >= bpp else 0
                linea[i] = (linea[i] + ((izq + previa[i]) >> 1)) & 0xFF
        elif filtro == 4:    # Paeth
            for i in range(ancho):
                a = linea[i - bpp] if i >= bpp else 0
                b = previa[i]
                c = previa[i - bpp] if i >= bpp else 0
                pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                pr = a if (pa <= pb and pa <= pc) else (b if pb <= pc else c)
                linea[i] = (linea[i] + pr) & 0xFF
        elif filtro != 0:
            raise PngError(f'filtro de linea desconocido: {filtro}')
        salida[y * ancho:(y + 1) * ancho] = linea
        previa = linea
    return salida


def leer(ruta):
    """Devuelve (w, h, rgba, meta). `rgba` es un bytearray de w*h*4 en orden R,G,B,A.

    `meta` lleva lo que hace falta para juzgar el fichero sin volver a abrirlo:
    `color_type`, `bit_depth`, `tenia_alfa` (si el PNG traia canal alfa o tRNS propios).
    """
    datos = open(ruta, 'rb').read()
    if not datos.startswith(FIRMA):
        raise PngError('no empieza por la firma PNG')

    w = h = prof = tipo = entrelazado = None
    paleta = b''
    trns = None
    idat = bytearray()
    for tag, cuerpo in _trozos(datos):
        if tag == b'IHDR':
            w, h, prof, tipo, _comp, _filt, entrelazado = struct.unpack('>IIBBBBB', cuerpo[:13])
        elif tag == b'PLTE':
            paleta = cuerpo
        elif tag == b'tRNS':
            trns = cuerpo
        elif tag == b'IDAT':
            idat += cuerpo
        elif tag == b'IEND':
            break

    if w is None:
        raise PngError('sin cabecera IHDR')
    if entrelazado:
        raise PngError('PNG entrelazado (Adam7): guardalo sin entrelazar')
    if prof != 8:
        raise PngError(f'{prof} bits por muestra; el kit solo lee 8')
    if tipo not in CANALES:
        raise PngError(f'tipo de color {tipo} desconocido')

    nc = CANALES[tipo]
    cruda = _desfiltra(zlib.decompress(bytes(idat)), w, h, nc)

    rgba = bytearray(w * h * 4)
    tenia_alfa = tipo in (4, 6) or trns is not None
    for i in range(w * h):
        o = i * nc
        d = i * 4
        if tipo == 0:                                   # gris
            v = cruda[o]
            r = g = b = v
            a = 0 if (trns and struct.unpack('>H', trns[:2])[0] == v) else 255
        elif tipo == 2:                                 # RGB
            r, g, b = cruda[o], cruda[o + 1], cruda[o + 2]
            a = 255
            if trns and len(trns) >= 6:
                tr, tg, tb = (struct.unpack('>HHH', trns[:6]))
                if (r, g, b) == (tr & 0xFF, tg & 0xFF, tb & 0xFF):
                    a = 0
        elif tipo == 3:                                 # indexado
            idx = cruda[o]
            if (idx + 1) * 3 > len(paleta):
                raise PngError('indice fuera de la paleta')
            r, g, b = paleta[idx * 3], paleta[idx * 3 + 1], paleta[idx * 3 + 2]
            a = trns[idx] if (trns and idx < len(trns)) else 255
        elif tipo == 4:                                 # gris + alfa
            r = g = b = cruda[o]
            a = cruda[o + 1]
        else:                                           # RGBA
            r, g, b, a = cruda[o], cruda[o + 1], cruda[o + 2], cruda[o + 3]
        rgba[d], rgba[d + 1], rgba[d + 2], rgba[d + 3] = r, g, b, a

    meta = {'color_type': tipo, 'bit_depth': prof, 'tenia_alfa': tenia_alfa}
    return w, h, rgba, meta


def escribir(ruta, w, h, rgba):
    """Escribe un PNG RGBA de 8 bits. `rgba` es w*h*4 bytes."""
    if len(rgba) != w * h * 4:
        raise PngError(f'se esperaban {w * h * 4} bytes de pixel y hay {len(rgba)}')
    crudo = bytearray()
    for y in range(h):
        crudo.append(0)                                 # filtro 0 (ninguno)
        crudo += rgba[y * w * 4:(y + 1) * w * 4]

    def trozo(tag, cuerpo):
        return (struct.pack('>I', len(cuerpo)) + tag + cuerpo
                + struct.pack('>I', zlib.crc32(tag + cuerpo) & 0xFFFFFFFF))

    open(ruta, 'wb').write(
        FIRMA
        + trozo(b'IHDR', struct.pack('>IIBBBBB', w, h, 8, 6, 0, 0, 0))
        + trozo(b'IDAT', zlib.compress(bytes(crudo), 9))
        + trozo(b'IEND', b''))
