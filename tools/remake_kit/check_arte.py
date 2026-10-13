"""La ADUANA: valida el arte entregado SIN ejecutar el juego, y en segundos.

Por que existe, dicho con el caso que lo motiva: un piloto anterior de remake se dio por bueno y en
la prueba manual se vio que "faltan la mitad de los sprites". El fallo no estaba en el juego ni
en el motor — estaba en el arte, y llevaba horas ahi sin que nada lo dijera. **El error se tiene
que ver AL METERLO, no al mirarlo.**

Lo que se rechaza, y por que cada cosa:

 1. No es PNG legible, o no es de 8 bits sin entrelazar. Sin esto no hay nada que juzgar.
 2. **Falta algun fotograma de una animacion.** Entregar unos si y otros no fue EXACTAMENTE el
    fallo del piloto: la mezcla HD/original se percibe como "faltan sprites". O todos, o ninguno.
 3. La proporcion se desvia de la del original mas de lo tolerado. Un sprite mas ancho de lo que
    el juego cree que es no se "ve mejor": se sale de su caja y baila respecto al fondo.
 4. Sin transparencia donde el original la tenia. El motor compone sobre lo que ya hay pintado;
    un fondo opaco tapa el escenario.
 5. Croma o halo residual en el borde. Los recortes por celda del piloto dejaron bordes de caja
    visibles, y eso no se ve en la miniatura pero si en pantalla.
 6. **Es el original escalado.** La primera prueba manual del kit se leyo como "no veo nada en HD" y
    tenia razon: las tres imagenes entregadas eran capturas del propio juego, con el cursor del
    raton quemado dentro, y pasaron la aduana entera porque ninguna regla miraba QUE se entregaba,
    solo su forma. Se compara con la referencia y se rechaza lo que coincida casi del todo.

Lo que ADMITE se escribe en `remake.json`, que es el fichero que el motor consume de verdad
(`PAKU_REMAKE`). El arte no llega al juego hasta que pasa por aqui: asi la degradacion es
segura por construccion — lo que no entra se ve original escalado, nunca roto.

Uso:  python tools/remake_kit/check_arte.py <id> --remake games/<id>/remake [--tolerancia 0.10]
Sale != 0 si hay rechazos.
"""

import argparse
import json
import os
import sys

AQUI = os.path.dirname(os.path.abspath(__file__))
sys.path.insert(0, AQUI)
import pngio  # noqa: E402


def borde_sucio(w, h, rgba):
    """Cuenta pixeles del BORDE que son opacos y de un verde/magenta de croma.

    Un croma que sobrevive al recorte es la firma del sprite-sheet mal cortado: el modelo pinta
    sobre un fondo croma y el recorte se queda con una franja. Se mira solo el borde porque un
    verde legitimo DENTRO del dibujo (hierba, un uniforme) no es un defecto.
    """
    n = 0
    for x in range(w):
        for y in (0, h - 1):
            n += es_croma(rgba, (y * w + x) * 4)
    for y in range(h):
        for x in (0, w - 1):
            n += es_croma(rgba, (y * w + x) * 4)
    return n


def es_croma(rgba, d):
    r, g, b, a = rgba[d], rgba[d + 1], rgba[d + 2], rgba[d + 3]
    if a < 128:
        return 0
    verde = g > 180 and r < 110 and b < 110
    magenta = r > 180 and b > 180 and g < 110
    return 1 if (verde or magenta) else 0


def parecido_al_original(ruta_hd, w, h, rgba, ruta_ref):
    """Fraccion de pixeles en que el arte entregado COINCIDE con el original.

    Existe por el caso real (prueba manual en un juego de prueba): las tres imagenes que habia en `hd/`
    no eran arte, eran capturas del propio juego escaladas —con el cursor del raton quemado
    dentro— y la aduana las admitio sin decir nada, porque ninguna regla miraba QUE se entregaba,
    solo su forma. El remake se dio por hecho y el juego se veia exactamente igual.

    Se compara reduciendo el entregado al tamano del original por vecino mas cercano y contando
    los pixeles casi identicos. Un escalado sale >0.97 (solo difiere por el filtro); un dibujo
    nuevo, aunque respete encuadre y colores, baja mucho de ahi.
    """
    try:
        rw, rh, rref, _ = pngio.leer(ruta_ref)
    except (pngio.PngError, OSError):
        return None
    if rw == 0 or rh == 0:
        return None
    muestra, refer = [], []
    for y in range(rh):
        sy = y * h // rh
        for x in range(rw):
            sx = x * w // rw
            o, p = (y * rw + x) * 4, (sy * w + sx) * 4
            refer.append((rref[o], rref[o + 1], rref[o + 2]))
            muestra.append((rgba[p], rgba[p + 1], rgba[p + 2]))

    def coinciden(k):
        n = 0
        for (r0, g0, b0), (r1, g1, b1) in zip(refer, muestra):
            if (abs(r0 - r1 * k) <= 16 and abs(g0 - g1 * k) <= 16 and abs(b0 - b1 * k) <= 16):
                n += 1
        return n / len(refer)

    # Tambien con el BRILLO normalizado. Una captura del juego tomada con otro motor —o
    # reescalada por una herramienta que toco los niveles— es la misma imagen con otra escala de
    # color, y comparando valores absolutos se cuela: medido en un juego de prueba, el arte falso tenia
    # EXACTAMENTE la mitad de valor en cada canal (el DAC de 6 bits expandido mal) y pasaba la
    # aduana con un 6,6 % de parecido. Lo que delata a un escalado no es el brillo, es que el
    # dibujo sea el mismo.
    suma_r = sum(sum(p) for p in refer) or 1
    suma_m = sum(sum(p) for p in muestra) or 1
    return max(coinciden(1.0), coinciden(suma_r / suma_m))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('id')
    ap.add_argument('--remake', required=True)
    ap.add_argument('--parecido-max', type=float, default=0.97,
                    help='parecido maximo con el original antes de considerarlo un escalado')
    ap.add_argument('--tolerancia', type=float, default=0.10,
                    help='desvio maximo de la proporcion ancho/alto respecto al original')
    ap.add_argument('--croma-max', type=int, default=0,
                    help='pixeles de croma tolerados en el borde')
    a = ap.parse_args()

    man_path = os.path.join(a.remake, 'manifiesto.json')
    if not os.path.exists(man_path):
        print(f'ROJO: no existe {man_path}')
        return 2
    with open(man_path, encoding='utf-8') as fh:
        man = json.load(fh)
    ref_path = os.path.join(a.remake, 'ref.json')
    refs = {}
    if os.path.exists(ref_path):
        with open(ref_path, encoding='utf-8') as fh:
            refs = json.load(fh).get('refs', {})
    pend_path = os.path.join(a.remake, 'pendiente.json')
    encargos = {}
    if os.path.exists(pend_path):
        with open(pend_path, encoding='utf-8') as fh:
            encargos = {e['clave']: e for e in json.load(fh).get('encargos', [])}

    # --- que animacion espera que fotogramas ---
    miembros_de = {}
    for clave, e in encargos.items():
        an = e.get('animacion')
        if an:
            miembros_de.setdefault(an['id'], []).append(clave)

    admitidos, rechazos = {}, []
    entregados = set()

    for clave, ent in man.get('assets', {}).items():
        tex = ent.get('tex') or ''
        ruta = os.path.join(a.remake, tex)
        if not tex or not os.path.exists(ruta):
            continue                                  # sin entregar: no es un rechazo, es un hueco
        entregados.add(clave)
        enc = encargos.get(clave, {})

        try:
            w, h, rgba, meta = pngio.leer(ruta)
        except pngio.PngError as ex:
            rechazos.append((tex, str(ex)))
            continue
        if w == 0 or h == 0:
            rechazos.append((tex, 'imagen vacia'))
            continue

        ow, oh = ent.get('w') or enc.get('w') or 0, ent.get('h') or enc.get('h') or 0
        if ow and oh:
            prop_o, prop_n = ow / oh, w / h
            desvio = abs(prop_n - prop_o) / prop_o
            if desvio > a.tolerancia:
                rechazos.append((tex, f'proporcion {w}x{h} = {prop_n:.3f} frente a {ow}x{oh} = '
                                      f'{prop_o:.3f} del original ({desvio * 100:.0f}% de desvio, '
                                      f'el tope es {a.tolerancia * 100:.0f}%)'))
                continue

        # La transparencia se exige SOLO si el original la tenia. Un sprite recortado deja ver el
        # escenario por sus huecos y sin alfa lo tapa; un fondo de pantalla completa no tiene
        # huecos, y exigirle alfa seria inventarse un requisito. `necesita_alfa` sale de medir
        # cuantos pixeles de su rectangulo el juego dejo sin escribir.
        if enc.get('necesita_alfa', True) and not meta['tenia_alfa']:
            rechazos.append((tex, 'sin canal de transparencia, y el original SI tiene huecos por '
                                  'los que se ve el escenario: opaco, lo taparia'))
            continue

        sucio = borde_sucio(w, h, rgba)
        if sucio > a.croma_max:
            rechazos.append((tex, f'borde con croma residual ({sucio} px)'))
            continue

        # Que lo entregado NO sea el original escalado. Sin esta regla, un directorio `hd/` lleno
        # de capturas del propio juego pasa la aduana entera y el remake se ve identico al
        # original — que es como se leyo la primera prueba manual del kit.
        info_ref = refs.get(clave) or {}
        if info_ref.get('png'):
            parecido = parecido_al_original(ruta, w, h, rgba,
                                            os.path.join(a.remake, 'ref', info_ref['png']))
            if parecido is not None and parecido >= a.parecido_max:
                rechazos.append((tex, f'es el ORIGINAL escalado: coincide con la referencia en el '
                                      f'{parecido * 100:.1f}% de los pixeles (el tope es '
                                      f'{a.parecido_max * 100:.0f}%). Esto no es un remake; el '
                                      f'juego se veria igual.'))
                continue

        admitidos[clave] = {'tex': tex, 'ax': ent.get('ax', 0), 'ay': ent.get('ay', 0),
                            'w': ow or w, 'h': oh or h}

    # --- animaciones a medias: el fallo que mato al piloto ---
    for anim, claves in sorted(miembros_de.items()):
        hechos = [c for c in claves if c in admitidos]
        if hechos and len(hechos) != len(claves):
            faltan = [c for c in claves if c not in admitidos]
            for c in hechos:
                admitidos.pop(c, None)                # una animacion a medias no entra ENTERA
            rechazos.append((anim, f'animacion incompleta: entregados {len(hechos)} de '
                                   f'{len(claves)} fotogramas, faltan {", ".join(faltan[:6])}'
                                   f'{"..." if len(faltan) > 6 else ""}. O todos, o ninguno: '
                                   f'mezclar HD y original en una animacion se ve como que '
                                   f'faltan sprites.'))

    # Lo que MAS se ve y sigue sin arte. Sin este orden, quien dibuja empieza por lo que tiene
    # delante y no por lo que cambia la partida: un asset que sale 173 veces y otro que sale una
    # cuestan lo mismo de dibujar y no valen lo mismo.
    faltan = [(e.get('veces') or 0, c, e) for c, e in encargos.items()
              if c not in admitidos and e.get('forma') != 'fila']
    faltan.sort(reverse=True, key=lambda t: t[0])
    if faltan:
        vistas_tot = sum(v for v, _, _ in faltan) or 1
        print(f'  lo que mas se ve y aun no tiene arte (de {len(faltan)} elementos):')
        for veces, clave, e in faltan[:8]:
            print(f'    {clave:<26} {veces:>5} veces  {100 * veces / vistas_tot:>5.1f} % de lo '
                  f'que queda   -> {e.get("destino", "")}')

    runtime = os.path.join(a.remake, 'remake.json')
    with open(runtime, 'w', encoding='utf-8') as fh:
        json.dump({'assets': admitidos}, fh, indent=1, ensure_ascii=False)

    total_man = len(man.get('assets', {}))
    if rechazos:
        print(f'RECHAZADAS {len(rechazos)} de {len(entregados)}:')
        for nombre, motivo in rechazos:
            print(f'  {nombre:<44} {motivo}')
        print(f'Las otras {len(admitidos)} entran. Corrige esas y vuelve a pasar check.')
    else:
        print(f'{len(admitidos)} de {len(entregados)} entregadas entran. '
              f'Sin rechazos.')
    print(f'{total_man - len(entregados)} encargos siguen sin arte (se veran originales).')
    print(f'contrato de ejecucion -> {runtime}')
    return 1 if rechazos else 0


if __name__ == '__main__':
    sys.exit(main())
