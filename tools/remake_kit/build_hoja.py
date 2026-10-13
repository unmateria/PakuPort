"""La HOJA DE CONTACTO: todo el trabajo pendiente en una pagina que se mira de un vistazo.

Por que existe. `pendiente.json` describe el encargo con precision, pero para decidir QUE merece
la pena dibujar hay que ver las cosas, y lo que habia era un directorio con 1165 PNG llamados
`ref-7D514451.png`. Abrirlos de uno en uno no es una herramienta, y sin verlos no se puede elegir:
un asset que sale 173 veces y otro que sale una son el mismo renglon en un JSON.

La hoja los ENSENA: agrupados por escena, ordenados por cuanto se ven, con su tamano obligatorio,
cuantas veces aparece cada uno y la ruta exacta donde dejar el arte. Y con el ENCARGO ya redactado
al lado, listo para copiar y pegarselo a quien vaya a dibujar —persona o modelo—, porque ese texto
es justo donde se cuela el error: quien dibuja no puede saber que la silueta no se toca, que el
tamano manda o que el fondo va transparente si nadie se lo dice.

Es HTML a proposito: se abre en cualquier navegador, no necesita instalar nada y las imagenes se
ven a tamano grande sin suavizar (image-rendering: pixelated), que es como hay que mirarlas.

Uso:
    python tools/remake_kit/build_hoja.py <id> --remake games/<id>/remake [--top 120]
"""

import argparse
import base64
import html
import json
import os

PLANTILLA_CSS = """
  :root { color-scheme: dark light; }
  body { font: 15px/1.5 system-ui, sans-serif; margin: 0; padding: 24px;
         background: #14161a; color: #e8e8ea; }
  h1 { font-size: 22px; margin: 0 0 4px; }
  .sub { color: #9aa0a8; margin-bottom: 24px; }
  h2 { font-size: 17px; margin: 32px 0 12px; padding-bottom: 6px;
       border-bottom: 1px solid #2a2f37; }
  .rejilla { display: grid; gap: 16px;
             grid-template-columns: repeat(auto-fill, minmax(230px, 1fr)); }
  .ficha { background: #1c1f26; border: 1px solid #2a2f37; border-radius: 8px; padding: 12px; }
  .ficha img { image-rendering: pixelated; max-width: 100%; background: #000;
               border-radius: 4px; display: block; margin-bottom: 8px; }
  .clave { font-family: ui-monospace, monospace; font-size: 12px; color: #7fd1ff;
           word-break: break-all; }
  .datos { color: #9aa0a8; font-size: 12px; margin: 4px 0 8px; }
  .veces { color: #ffd479; font-weight: 600; }
  details { margin-top: 8px; }
  summary { cursor: pointer; color: #9aa0a8; font-size: 12px; }
  pre { white-space: pre-wrap; background: #0f1114; border: 1px solid #2a2f37;
        border-radius: 6px; padding: 10px; font-size: 12px; margin: 8px 0 0; }
  .aviso { background: #2a1f14; border: 1px solid #6b4a1f; border-radius: 8px;
           padding: 12px 16px; margin-bottom: 20px; }
"""


def encargo_de(e, id_juego):
    """El texto que se le pasa a quien dibuja. Dice lo que NO puede cambiar, que es lo que se
    rompe cuando nadie lo dice: el tamano (el motor lo impone), la silueta (o el sprite baila
    respecto al fondo) y el fondo transparente (o tapa el escenario)."""
    w, h = e.get('w') or 0, e.get('h') or 0
    lineas = [
        f"Redibuja este elemento del videojuego «{id_juego}» en alta resolucion.",
        "",
        f"QUE ES: {e.get('clase') or 'elemento grafico'}"
        + (f", en la pantalla de {e['escena']}" if e.get('escena') else "")
        + f". Aparece {e.get('veces', 0)} veces en la partida.",
        f"TAMANO: la proporcion tiene que ser {w}x{h} exacta"
        + (f" (entrega a {w * 4}x{h * 4}, 4 aumentos)" if w and h else "") + ".",
        "",
        "OBLIGATORIO:",
        "- Misma silueta y misma composicion que el original: el motor lo coloca en el mismo",
        "  sitio, y si cambia la forma se despega del fondo.",
        "- Misma gama de color. No 'mejores' la paleta.",
    ]
    if e.get('necesita_alfa', True):
        lineas.append("- Fondo TRANSPARENTE (PNG con alfa). El original tiene huecos por los que")
        lineas.append("  se ve el escenario; un fondo opaco lo taparia.")
    else:
        lineas.append("- Ocupa el rectangulo entero: aqui no hay transparencia que respetar.")
    lineas += [
        "",
        "PROHIBIDO: anadir elementos que no esten, texto nuevo, marcos, firmas o bordes.",
        "  No lo escales ni lo suavices: REDIBUJALO. Un escalado del original se rechaza.",
        "",
        f"DEJA EL FICHERO EN: {e.get('destino', '')}",
    ]
    return "\n".join(lineas)


def imagen(remake, ref, clave):
    """La miniatura EMBEBIDA en la propia pagina. Una hoja que depende de una carpeta de al lado
    deja de funcionar en cuanto alguien la mueve o la manda por correo, y entonces no la mira
    nadie; asi es un fichero suelto que se abre en cualquier sitio."""
    if not ref:
        return '<div class="datos">(sin imagen de referencia)</div>'
    ruta = os.path.join(remake, ref)
    if not os.path.exists(ruta):
        return '<div class="datos">(falta ' + html.escape(ref) + ')</div>'
    b64 = base64.b64encode(open(ruta, 'rb').read()).decode('ascii')
    return f'<img src="data:image/png;base64,{b64}" alt="{html.escape(clave)}">'


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('id')
    ap.add_argument('--remake', required=True)
    ap.add_argument('--top', type=int, default=150, help='cuantos encargos enseñar por escena')
    ap.add_argument('--salida', default='hoja.html')
    a = ap.parse_args()

    pend_path = os.path.join(a.remake, 'pendiente.json')
    if not os.path.exists(pend_path):
        print(f'ROJO: no existe {pend_path}. Pasa antes `pakuport remake study {a.id}`.')
        return 2
    with open(pend_path, encoding='utf-8') as fh:
        encargos = json.load(fh).get('encargos', [])

    # Lo que de verdad se puede reemplazar va PRIMERO y lo demas ni se enseña: una fila de un
    # pixel de alto no es un encargo que nadie pueda dibujar, y llenar la hoja de ellas es la
    # forma mas rapida de que no se mire.
    utiles = [e for e in encargos if e.get('forma') != 'fila']
    filas = len(encargos) - len(utiles)

    por_escena = {}
    for e in sorted(utiles, key=lambda e: -(e.get('veces') or 0)):
        por_escena.setdefault(e.get('escena') or 'sin escena', []).append(e)

    partes = [f'<!doctype html><meta charset="utf-8"><title>Remake de {html.escape(a.id)}: '
              f'hoja de contacto</title><style>{PLANTILLA_CSS}</style>',
              f'<h1>Remake de {html.escape(a.id)} — hoja de contacto</h1>',
              f'<div class="sub">{len(utiles)} elementos que se pueden dibujar, '
              f'en {len(por_escena)} escenas. Ordenados por cuanto se ven.</div>']
    if filas:
        partes.append(f'<div class="aviso"><b>{filas} encargos no se enseñan aqui</b>: son FILAS '
                      f'de un pixel de alto, porque este juego compone sus sprites linea a linea. '
                      f'No se dibujan de una en una — las figuras enteras son las claves '
                      f'<code>grupo:…</code> que si estan en esta hoja.</div>')

    for escena, lista in por_escena.items():
        partes.append(f'<h2>{html.escape(str(escena))} — {len(lista)} elementos</h2><div class="rejilla">')
        for e in lista[:a.top]:
            img = imagen(a.remake, e.get('ref'), e['clave'])
            partes.append(
                '<div class="ficha">' + img +
                f'<div class="clave">{html.escape(e["clave"])}</div>'
                f'<div class="datos">{e.get("w")}×{e.get("h")} · '
                f'<span class="veces">{e.get("veces", 0)}×</span> · {html.escape(str(e.get("clase") or ""))}</div>'
                f'<div class="datos">→ <code>{html.escape(str(e.get("destino") or ""))}</code></div>'
                f'<details><summary>encargo para copiar</summary>'
                f'<pre>{html.escape(encargo_de(e, a.id))}</pre></details></div>')
        partes.append('</div>')

    destino = os.path.join(a.remake, a.salida)
    with open(destino, 'w', encoding='utf-8') as fh:
        fh.write('\n'.join(partes))
    print(f'hoja de contacto: {len(utiles)} elementos en {len(por_escena)} escenas'
          + (f' ({filas} filas sueltas ocultas)' if filas else ''))
    print(f'  -> {destino}')
    return 0


if __name__ == '__main__':
    raise SystemExit(main())
