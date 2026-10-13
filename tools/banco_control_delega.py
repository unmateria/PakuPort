"""Genera el CONTROL POSITIVO del arnes de un banco: una respuesta por funcion que no traduce
nada y delega la funcion ENTERA al binario original (`Original.Llama`).

Para que sirve: separa "la traduccion esta mal" de "el arnes esta mal". Si esto sale rojo, el
fallo esta en el contrato de llamada o en el corpus, no en quien traduce. Es tambien la forma
barata de saber CUANTAS funciones del banco son JUZGABLES con un corpus dado, sin gastar agentes:
`banco juzga` solo pasa el original por el aro cuando hay una respuesta que juzgar.

    python tools/banco_control_delega.py banco/<id>-corpus banco/<id>-corpus-control-delega
    pakuport banco juzga --banco banco/<id>-corpus-control-delega
"""
import json, os, sys

PLANTILLA = '''using PakuPort.Core;
using PakuPort.Core.Verify;

// CONTROL POSITIVO DEL ARNES (generado por tools/banco_control_delega.py).
// No traduce nada: delega la funcion ENTERA al binario original. Si el arnes es fiel, esto
// tiene que salir VERDE; si sale rojo, el fallo esta en el arnes y no en quien traduce.
public static class Respuesta
{{
    public static void Ejecuta(Cpu c, Memory m, Original o)
    {{
        ushort ret = m.RdU16(c.SS, c.SP);
        c.SP += 2;                                   // quitar la direccion de retorno que ya esta
        o.Llama(0x{ip:04X});                             // {func}, entera
        m.WrU16(c.SS, (ushort)(c.SP - 2), ret);      // devolver la celda que Llama piso
    }}
}}
'''


def main(banco, out):
    d = json.load(open(os.path.join(banco, "banco.json"), encoding="utf-8"))
    os.makedirs(out, exist_ok=True)
    json.dump(d, open(os.path.join(out, "banco.json"), "w", encoding="utf-8"),
              indent=2, ensure_ascii=False)
    for f in d["funciones"]:
        dd = os.path.join(out, f["func"])
        os.makedirs(dd, exist_ok=True)
        with open(os.path.join(dd, "respuesta.cs"), "w", encoding="utf-8") as fh:
            fh.write(PLANTILLA.format(ip=int(f["ip"], 16), func=f["func"]))
    print(f"{len(d['funciones'])} respuestas de control -> {out}")


if __name__ == "__main__":
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(2)
    main(sys.argv[1], sys.argv[2])
