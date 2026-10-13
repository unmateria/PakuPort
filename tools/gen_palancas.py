#!/usr/bin/env python3
"""Genera el indice de palancas de diagnostico (PAKU_*) DERIVANDOLO del codigo.

No es un documento a mano: se regenera. Si el codigo cambia y el indice no, --check lo
suspende, asi que no puede pudrirse en silencio como paso con las notas fosiles.

Uso:
    python gen_palancas.py                  regenera PALANCAS.md y palancas.json
    python gen_palancas.py --check          sale != 0 si lo generado difiere de lo que hay
                                            en disco (para meterlo en los gates)
    python gen_palancas.py --raiz <dir>     donde esta el repo de PakuPort (solo se LEE)
    python gen_palancas.py --salida <dir>   donde escribir

Colocado en `<repo>/tools/` (su sitio), detecta el repo solo y escribe en `<repo>/docs/`
(creandolo si no existe) sin necesidad de flags. Si se ejecuta desde fuera del repo, SOLO LEE
el repo y escribe a su lado, salvo que se le pida otro sitio con --salida.

De donde sale cada dato:
    nombre      de GetEnvironmentVariable("PAKU_X") en src/  -- uso real, no menciones
    forma       del codigo que rodea al uso (== "1", int.TryParse, IsNullOrEmpty...)
    que hace    del bloque de comentario que la documenta; se prefiere aquel cuya PRIMERA
                linea la nombra (el patron dominante: "// PAKU_X=1: hace tal cosa"), y si
                no, el bloque pegado a su declaracion
    sitios      fichero:linea de cada uso

Lo que NO tiene comentario sale marcado como SIN DOCUMENTAR: el indice no solo lista,
tambien senala lo que falta por explicar.
"""

import json
import re
import sys
from pathlib import Path

AQUI = Path(__file__).resolve().parent


def opcion(nombre):
    if nombre in sys.argv:
        i = sys.argv.index(nombre)
        if i + 1 < len(sys.argv):
            return Path(sys.argv[i + 1]).resolve()
    return None


def localiza_raiz():
    """El repo de PakuPort. Se busca, no se da por supuesto ni se cablea una ruta."""
    candidatas = [
        opcion("--raiz"),
        AQUI.parent,                    # el script colocado en pakuport/tools/
        Path.cwd(),                     # ejecutado desde la raiz de pakuport
        Path.cwd().parent / "pakuport",  # desde un repo hermano
        AQUI.parents[3] / "pakuport" if len(AQUI.parents) > 3 else None,
    ]
    for c in candidatas:
        if c and (c / "src" / "PakuPort.Core").is_dir():
            return c
    sys.exit(
        "No encuentro el repo de PakuPort (busco un directorio con src/PakuPort.Core).\n"
        "Dimelo con: python gen_palancas.py --raiz <ruta al repo de pakuport>"
    )


RAIZ = localiza_raiz()
SRC = RAIZ / "src"
# Si el script vive en pakuport/tools/, su sitio natural es pakuport/docs/. Si vive fuera,
# escribe a su lado y NO toca el repo ajeno.
SALIDA = opcion("--salida") or (RAIZ / "docs" if AQUI == RAIZ / "tools" else AQUI)
MD = SALIDA / "PALANCAS.md"
JSON = SALIDA / "palancas.json"

# Las comillas pueden venir dobladas (""X""): la plantilla que se copia a cada Jugar*.exe
# vive DENTRO de Program.cs como cadena verbatim. Distinguirlas no es un detalle de parseo,
# es lo que dice donde funciona cada palanca: en el motor y sus herramientas, o solo en el
# ejecutable publicado del juego. Sin esto se perdian 21 palancas reales.
# Hay palancas de FAMILIA: el nombre lleva un sufijo variable interpolado
# (GetEnvironmentVariable($"PAKU_DISCO_{letra}")). Sin reconocerlas, una palanca real y
# documentada no aparecia en el indice, y --check daba verde con el hueco dentro.
USO = re.compile(r'GetEnvironmentVariable\(\s*\$?("{1,2})(PAKU_[A-Z0-9_]+)(\{[^"}]*\})?\1\s*\)')
NOMBRE = re.compile(r"\bPAKU_[A-Z0-9_]+\b")
ES_COMENTARIO = re.compile(r"^\s*(///?)\s?(.*)$")

# Etiquetas de las marcas XML de los /// <summary>, que estorban al leer.
XML = re.compile(r"</?(summary|remarks|para|c|see[^>]*|b|i)>")


def limpia(texto):
    texto = XML.sub("", texto)
    return texto.replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&").strip()


def bloques_de_comentario(lineas):
    """Todos los bloques de lineas de comentario contiguas: [(inicio, fin, texto)]."""
    bloques, actual, inicio = [], [], None
    for i, linea in enumerate(lineas):
        m = ES_COMENTARIO.match(linea)
        if m:
            if inicio is None:
                inicio = i
            actual.append(limpia(m.group(2)))
        elif inicio is not None:
            bloques.append((inicio, i - 1, " ".join(t for t in actual if t)))
            actual, inicio = [], None
    if inicio is not None:
        bloques.append((inicio, len(lineas) - 1, " ".join(t for t in actual if t)))
    return [b for b in bloques if b[2]]


def forma_de_uso(lineas, i, nombre):
    """Deduce como se le pasa el valor mirando lo que envuelve a ESTE uso concreto.

    Se mira por posicion, no por ventana de lineas: una misma linea puede leer dos palancas
    distintas con formas distintas, y una ventana las mezcla — asi salian palancas con tres
    formas contradictorias, que informa peor que no decir nada.
    """
    # Comillas dobladas de la plantilla normalizadas: asi los patrones valen en los dos ambitos.
    linea = lineas[i].replace('""', '"')
    aguja = f'GetEnvironmentVariable("{nombre}")'
    pos = linea.find(aguja)
    if pos < 0:
        return "sin determinar"

    # La aguja no lleva el "Environment." de delante; sin quitarlo, `antes` nunca termina en
    # "int.TryParse(" y las palancas con NIVELES (1, 2, 3...) se leian como si fueran de si/no.
    antes = linea[:pos].rstrip().removesuffix("Environment.").removesuffix("System.")
    despues = linea[pos + len(aguja) :].lstrip()

    if despues.startswith('== "1"'):
        return "=1"
    if despues.startswith("=="):
        valor = re.match(r'==\s*"([^"]*)"', despues)
        if valor:
            return f'={valor.group(1)}'
    if antes.endswith("int.TryParse(") or antes.endswith("TryParse("):
        return "numero"
    if antes.endswith("IsNullOrEmpty("):
        return "presencia o valor"
    if despues.startswith("??"):
        return "valor de texto"
    # Asignada tal cual a una variable: el valor ES el dato (una ruta, una lista de teclas...).
    if re.search(r"(string\??|var)\s+\w+\s*=\s*$", antes):
        return "valor de texto"
    return "presencia o valor"


def recoge():
    palancas = {}
    for fichero in sorted(SRC.rglob("*.cs")):
        lineas = fichero.read_text(encoding="utf-8", errors="replace").splitlines()
        bloques = bloques_de_comentario(lineas)
        rel = fichero.relative_to(RAIZ).as_posix()

        for i, linea in enumerate(lineas):
            for comillas, nombre, sufijo in USO.findall(linea):
                # El nombre completo lleva el sufijo variable ("PAKU_DISCO_<LETRA>"); para
                # BUSCAR su documentacion vale el prefijo, que es lo que aparece en el comentario.
                busca = nombre
                if sufijo:
                    nombre += "<LETRA>" if nombre.endswith("_") else "<X>"
                p = palancas.setdefault(
                    nombre,
                    {"nombre": nombre, "forma": set(), "ambito": set(), "sitios": [], "doc": None, "doc_de": None},
                )
                p["sitios"].append(f"{rel}:{i + 1}")
                p["forma"].add(forma_de_uso(lineas, i, nombre))
                p["ambito"].add("plantilla" if len(comillas) == 2 else "motor")

                if p["doc"]:
                    continue

                # 1) el bloque cuya PRIMERA linea la nombra: es su documentacion canonica.
                elegido = None
                for ini, fin, texto in bloques:
                    primera = limpia(ES_COMENTARIO.match(lineas[ini]).group(2))
                    if busca in NOMBRE.findall(primera):
                        elegido = (texto, f"{rel}:{ini + 1}")
                        break
                # 2) si no, el bloque pegado a la declaracion (<=3 lineas de codigo en medio).
                if not elegido:
                    for ini, fin, texto in bloques:
                        if 0 <= i - fin <= 3:
                            elegido = (texto, f"{rel}:{ini + 1}")
                # 3) si no, cualquier bloque del fichero que la mencione.
                if not elegido:
                    for ini, fin, texto in bloques:
                        if busca in NOMBRE.findall(texto):
                            elegido = (texto, f"{rel}:{ini + 1}")
                            break
                if elegido:
                    p["doc"], p["doc_de"] = elegido

    for p in palancas.values():
        p["forma"] = sorted(p["forma"])
        p["ambito"] = sorted(p["ambito"])
    return palancas


AMBITOS = {
    "motor": "Motor — valen en el motor y sus herramientas (`pakuport ...`, `le-run`)",
    "plantilla": "Plantilla del host — valen en el `Jugar<Juego>.exe` publicado",
}


def render(palancas):
    ficheros = {p["sitios"][0].rsplit(":", 1)[0] for p in palancas.values()}
    sin_doc = sorted(p["nombre"] for p in palancas.values() if not p["doc"])

    out = []
    out.append("# Palancas de diagnostico `PAKU_*` — indice GENERADO")
    out.append("")
    out.append("> **No editar a mano.** Lo genera `tools/gen_palancas.py` leyendo `src/`.")
    out.append("> Regenerar: `python tools/gen_palancas.py`")
    out.append("> Comprobar que no se ha quedado desfasado: `python tools/gen_palancas.py --check`")
    out.append("> (sale distinto de cero si el codigo cambio y esto no; sirve como gate).")
    out.append("")
    out.append(f"**{len(palancas)} palancas** en **{len(ficheros)} ficheros**.")
    if sin_doc:
        out.append("")
        out.append(f"**{len(sin_doc)} sin documentar** — no tienen ningun comentario que las explique:")
        out.append("")
        out.append(", ".join(f"`{n}`" for n in sin_doc))
    out.append("")
    out.append("Todas estan APAGADAS por defecto y ninguna altera el comportamiento del juego.")
    out.append("")

    out.append("## Indice alfabetico")
    out.append("")
    out.append("| Palanca | Forma | Ambito | Donde vive |")
    out.append("|---|---|---|---|")
    for nombre in sorted(palancas):
        p = palancas[nombre]
        fichero = p["sitios"][0].rsplit(":", 1)[0].replace("src/PakuPort.", "")
        out.append(f"| `{nombre}` | {' / '.join(p['forma'])} | {' + '.join(p['ambito'])} | {fichero} |")
    out.append("")

    for ambito, titulo in AMBITOS.items():
        del_ambito = [p for p in palancas.values() if ambito in p["ambito"]]
        if not del_ambito:
            continue
        out.append("")
        out.append(f"## {titulo}")
        por_fichero = {}
        for p in del_ambito:
            por_fichero.setdefault(p["sitios"][0].rsplit(":", 1)[0], []).append(p)
        for fichero in sorted(por_fichero):
            out.append("")
            out.append(f"### `{fichero}`")
            for p in sorted(por_fichero[fichero], key=lambda x: x["nombre"]):
                out.append("")
                out.append(f"#### `{p['nombre']}`  ·  {' / '.join(p['forma'])}")
                out.append("")
                out.append(p["doc"] if p["doc"] else "**SIN DOCUMENTAR** — no hay comentario que la explique.")
                if len(p["sitios"]) > 1:
                    out.append("")
                    out.append(f"Usada en: {', '.join('`' + s + '`' for s in p['sitios'])}")
    out.append("")
    return "\n".join(out)


def main():
    palancas = recoge()
    md = render(palancas)
    js = json.dumps(
        {
            "total": len(palancas),
            "sin_documentar": sorted(p["nombre"] for p in palancas.values() if not p["doc"]),
            "palancas": [palancas[n] for n in sorted(palancas)],
        },
        indent=2,
        ensure_ascii=False,
    )

    if "--check" in sys.argv:
        problemas = []
        for destino, generado in ((MD, md), (JSON, js)):
            if not destino.exists():
                problemas.append(f"falta {destino.name}")
            elif destino.read_text(encoding="utf-8") != generado:
                problemas.append(f"{destino.name} esta desfasado")
        if problemas:
            print("ROJO: " + "; ".join(problemas))
            print("      el codigo cambio y el indice no. Ejecuta: python gen_palancas.py")
            return 1
        print(f"VERDE: indice al dia ({len(palancas)} palancas)")
        return 0

    SALIDA.mkdir(parents=True, exist_ok=True)
    MD.write_text(md, encoding="utf-8")
    JSON.write_text(js, encoding="utf-8")
    sin_doc = sum(1 for p in palancas.values() if not p["doc"])
    print(f"leido: {RAIZ}")
    print(f"{len(palancas)} palancas -> {MD} y {JSON}")
    print(f"  sin documentar: {sin_doc}")
    return 0


if __name__ == "__main__":
    sys.exit(main())
