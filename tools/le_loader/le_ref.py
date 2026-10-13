#!/usr/bin/env python3
"""Cargador LE de REFERENCIA.

No es el cargador del producto: ése va en C#. Éste existe por dos razones, y las dos importan.

1. **Validar el formato antes de escribir C#.** El LE es un formato donde "más hay que
   leer antes de escribir", y equivocarse en los fixups no da un error: da una imagen que
   arranca y se descarrilla 40.000 instrucciones después. Aquí el modelo se comprueba a coste
   cero y con una condición dura: la tabla de fixups tiene que consumirse BYTE A BYTE.

2. **Es el lado nuestro del juez.** La verdad terreno es el volcado del mismo juego cargado por
   DOS4GW en el oráculo; este script produce la imagen que hay que comparar contra ella.

Uso:
    python tools\\le_loader\\le_ref.py <fichero.exe> [--out imagen.bin] [--info]
"""
import argparse, struct, sys, collections

# --- flags de objeto (LE) ---
OBJ_READ, OBJ_WRITE, OBJ_EXEC = 0x01, 0x02, 0x04
OBJ_PRELOAD, OBJ_ALIAS16, OBJ_BIG32 = 0x40, 0x1000, 0x2000

# --- tipos de origen del fixup (nibble bajo de src_type) ---
SRC_BYTE, SRC_SEL16, SRC_PTR1616 = 0x00, 0x02, 0x03
SRC_OFF16, SRC_PTR1632, SRC_OFF32, SRC_REL32 = 0x05, 0x06, 0x07, 0x08
SRC_ALIAS, SRC_LIST = 0x10, 0x20

# --- flags de destino ---
TGT_MASK, TGT_INTERNAL = 0x03, 0x00
TGT_ADDITIVE, TGT_OFF32, TGT_ADD32, TGT_OBJ16 = 0x04, 0x10, 0x20, 0x40


class LeError(Exception):
    pass


class LeImage:
    """Imagen plana cargada + los datos que el juez compara."""

    def __init__(self):
        self.objects = []        # (num, base, vsize, flags, page_idx, page_cnt)
        self.mem = bytearray()   # espacio plano desde 0
        self.eip = 0
        self.esp = 0
        self.fixups = 0
        self.fixup_kinds = collections.Counter()
        self.touched = []        # (addr, tam) de cada fixup aplicado, para el juez


def load(data, load_base=0, pegados=True):
    """Carga un LE en un espacio plano cuyo primer objeto empieza en `load_base`.

    Los objetos van PEGADOS (cada uno tras el anterior, alineado a pagina), NO en la base que
    pide la tabla del fichero. Eso no se dedujo: lo dijo el oraculo (judge_le.py).

    `pegados=False` reproduce el modelo EQUIVOCADO (respetar las bases del fichero) y existe
    como control negativo del juez: si con el modelo malo el juez no se pone rojo, es que el
    juez no mide nada."""
    if data[:2] != b'MZ':
        raise LeError('no empieza por MZ')
    o = struct.unpack_from('<I', data, 0x3C)[0]
    if data[o:o + 2] != b'LE':
        raise LeError('no hay firma LE en e_lfanew=0x%X (hay %r)' % (o, data[o:o + 2]))

    u32 = lambda off: struct.unpack_from('<I', data, o + off)[0]
    u16 = lambda off: struct.unpack_from('<H', data, o + off)[0]

    npages        = u32(0x14)
    eip_object    = u32(0x18)
    eip           = u32(0x1C)
    esp_object    = u32(0x20)
    esp           = u32(0x24)
    page_size     = u32(0x28)
    last_page     = u32(0x2C)      # en LE: bytes utiles de la pagina final
    obj_tbl       = o + u32(0x40)
    nobj          = u32(0x44)
    page_map      = o + u32(0x48)
    fixup_pages   = o + u32(0x68)
    fixup_records = o + u32(0x6C)
    data_pages    = u32(0x80)      # OJO: offset ABSOLUTO en el fichero, no relativo a la cabecera

    img = LeImage()

    # --- objetos: PEGADOS desde load_base, alineados a pagina ---
    cursor = load_base
    for i in range(nobj):
        vsize, file_base, flags, pidx, pcnt, _ = struct.unpack_from('<6I', data, obj_tbl + i * 24)
        base = cursor if pegados else load_base + file_base
        img.objects.append((i + 1, base, vsize, flags, pidx, pcnt))
        reservado = max(vsize, pcnt * page_size)
        cursor += (reservado + page_size - 1) // page_size * page_size

    top = max(b + v for _, b, v, _, _, _ in img.objects)
    img.mem = bytearray(top)       # el hueco entre objetos y el BSS quedan a CERO

    # --- paginas ---
    # El mapa de paginas es GLOBAL (indices 1..npages) y cada objeto se queda un tramo.
    # Entrada LE de 4 bytes: numero de pagina en 3 bytes (los dos altos BIG-endian) + flags.
    for num, base, vsize, flags, pidx, pcnt in img.objects:
        for k in range(pcnt):
            e = page_map + (pidx - 1 + k) * 4
            hi = struct.unpack_from('>H', data, e)[0]
            lo = data[e + 2]
            pflags = data[e + 3]
            pagenum = (hi << 8) | lo
            dst = base + k * page_size
            if pflags == 0:                     # pagina fisica presente en el fichero
                src = data_pages + (pagenum - 1) * page_size
                n = last_page if pagenum == npages and last_page else page_size
                n = min(n, page_size, len(data) - src)
                # el objeto puede terminar antes que la pagina
                n = min(n, len(img.mem) - dst)
                img.mem[dst:dst + n] = data[src:src + n]
            elif pflags == 3:                   # rellena de ceros: ya lo esta
                pass
            else:
                raise LeError('pagina %d con flags=%d no soportada (iterada/invalida)'
                              % (pagenum, pflags))

    # --- fixups ---
    ents = [struct.unpack_from('<I', data, fixup_pages + i * 4)[0] for i in range(npages + 1)]
    # pagina global -> (base del objeto, offset de la pagina dentro del objeto)
    page_addr = {}
    for num, base, vsize, flags, pidx, pcnt in img.objects:
        for k in range(pcnt):
            page_addr[pidx + k] = base + k * page_size
    obj_base = {num: base for num, base, _, _, _, _ in img.objects}

    for pg in range(1, npages + 1):
        a, b = fixup_records + ents[pg - 1], fixup_records + ents[pg]
        i = a
        pbase = page_addr[pg]
        while i < b:
            st, fl = data[i], data[i + 1]
            i += 2
            kind = st & 0x0F
            if st & SRC_ALIAS:
                raise LeError('fixup con alias 16:16 no soportado (st=0x%02X)' % st)
            if st & SRC_LIST:
                cnt = data[i]; i += 1
                srcoffs = None                   # la lista viene al final del registro
            else:
                cnt = 1
                srcoffs = [struct.unpack_from('<h', data, i)[0]]; i += 2

            if (fl & TGT_MASK) != TGT_INTERNAL:
                raise LeError('fixup no interno (fl=0x%02X): este cargador no resuelve importaciones' % fl)

            if fl & TGT_OBJ16:
                tobj = struct.unpack_from('<H', data, i)[0]; i += 2
            else:
                tobj = data[i]; i += 1

            if kind == SRC_SEL16:
                toff = 0                          # un fixup de selector no lleva offset
            elif fl & TGT_OFF32:
                toff = struct.unpack_from('<I', data, i)[0]; i += 4
            else:
                toff = struct.unpack_from('<H', data, i)[0]; i += 2

            if fl & TGT_ADDITIVE:
                add = struct.unpack_from('<I' if fl & TGT_ADD32 else '<H', data, i)[0]
                i += 4 if fl & TGT_ADD32 else 2
                toff += add

            if st & SRC_LIST:
                srcoffs = [struct.unpack_from('<h', data, i + 2 * k)[0] for k in range(cnt)]
                i += 2 * cnt

            target = obj_base[tobj] + toff
            for so in srcoffs:
                # Un offset de origen NEGATIVO significa que el fixup empieza en la pagina
                # ANTERIOR y cruza el borde; el destino sigue siendo el mismo.
                addr = pbase + so
                apply_fixup(img, kind, addr, target)
                img.fixup_kinds['st=0x%02X fl=0x%02X' % (st, fl)] += 1
                img.fixups += 1
        if i != b:
            raise LeError('la tabla de fixups de la pagina %d no se consume exacta (%d != %d)'
                          % (pg, i, b))

    img.eip = obj_base[eip_object] + eip
    img.esp = obj_base[esp_object] + esp
    return img


def apply_fixup(img, kind, addr, target):
    """Escribe UN fixup. `addr` es la direccion plana del sitio a parchear."""
    m = img.mem
    if kind == SRC_OFF32:
        if 0 <= addr <= len(m) - 4:
            struct.pack_into('<I', m, addr, target & 0xFFFFFFFF)
            img.touched.append((addr, 4))
    elif kind == SRC_REL32:
        # autorrelativo: el valor es destino - (direccion del campo + 4)
        if 0 <= addr <= len(m) - 4:
            struct.pack_into('<i', m, addr, (target - (addr + 4)) & 0xFFFFFFFF
                             if target - (addr + 4) >= 0 else target - (addr + 4))
            img.touched.append((addr, 4))
    elif kind == SRC_OFF16:
        if 0 <= addr <= len(m) - 2:
            struct.pack_into('<H', m, addr, target & 0xFFFF)
            img.touched.append((addr, 2))
    elif kind == SRC_PTR1632:
        # 16:32 = offset de 32 bits + selector de 16. El selector solo se sabe en tiempo de
        # carga (lo asigna el host DPMI), asi que aqui se escribe el offset y se DEJA CONSTANCIA.
        if 0 <= addr <= len(m) - 6:
            struct.pack_into('<I', m, addr, target & 0xFFFFFFFF)
            img.touched.append((addr, 4))
    else:
        raise LeError('tipo de origen 0x%02X no soportado' % kind)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('exe')
    ap.add_argument('--out')
    ap.add_argument('--info', action='store_true')
    ap.add_argument('--base', default='0', help='base lineal del primer objeto (hex con 0x)')
    a = ap.parse_args()

    img = load(open(a.exe, 'rb').read(), int(a.base, 0))

    print('objetos: %d' % len(img.objects))
    for num, base, vsize, flags, pidx, pcnt in img.objects:
        f = []
        for m, n in ((OBJ_READ, 'R'), (OBJ_WRITE, 'W'), (OBJ_EXEC, 'X'),
                     (OBJ_PRELOAD, 'PRELOAD'), (OBJ_ALIAS16, 'ALIAS16:16'), (OBJ_BIG32, '32BIT')):
            if flags & m:
                f.append(n)
        print('  obj%d base=0x%06X vsize=0x%X paginas=%d  %s'
              % (num, base, vsize, pcnt, ','.join(f)))
    print('imagen plana: %d bytes (0x%X)' % (len(img.mem), len(img.mem)))
    print('EIP = 0x%08X   ESP = 0x%08X' % (img.eip, img.esp))
    print('fixups aplicados: %d' % img.fixups)
    if a.info:
        for k, v in sorted(img.fixup_kinds.items()):
            print('    %s  %d' % (k, v))
        nz = sum(1 for x in img.mem if x)
        print('bytes no nulos: %d (%.1f %%)' % (nz, 100.0 * nz / len(img.mem)))

    if a.out:
        open(a.out, 'wb').write(img.mem)
        print('-> %s' % a.out)


if __name__ == '__main__':
    sys.exit(main())
