# -*- coding: utf-8 -*-
"""EL CONTROL DE LOS NOMBRES CON EVIDENCIA — que ningun nombre este inventado.

POR QUE EXISTE. El tablero propone un nombre para cada funcion, y un nombre se lee como un HECHO:
quien traduce lo usa sin verificarlo. Asi que la pregunta no es "que bonitos son los nombres" sino
**si cada uno se puede rastrear hasta una instruccion concreta del binario**. Eso es lo que se
comprueba aqui, y contra el desensamblado que el propio tablero deja al lado, no contra la
intencion de quien escribio el codigo.

Las cuatro comprobaciones:

  1. **Ningun nombre sin evidencia.** Si hay nombre, tiene que haber al menos una linea de
     evidencia que cite una direccion.
  2. **Cada evidencia citada EXISTE.** Para cada `int NNh AH=XXh en 0xDDDD`, en el desensamblado
     tiene que haber un `int NNh` en esa direccion. Una cita que no se puede seguir es peor que
     ninguna.
  3. **La subfuncion citada esta de verdad en el cuerpo.** Antes de esa INT tiene que haber un
     `mov ah,XX` o `mov ax,XXYY` con ese valor.
  4. **Los textos citados existen.** Toda evidencia que dice "el texto ..." tiene que citar una
     cadena de al menos cuatro caracteres. (Que EMPIECE donde dice ya lo garantiza el generador;
     esto solo caza que no se cuele un fragmento.)

Y con `--mutar`, el CONTROL NEGATIVO: se cambia el AH de una evidencia y se comprueba que la
comprobacion 3 la RECHAZA. Un juez que nunca se ha visto rojo no prueba nada.

Uso:
    python tools/control_nombres.py --tablero games/<id>/expediente
    python tools/control_nombres.py --tablero games/<id>/expediente --mutar
"""
import argparse
import json
import os
import re
import sys

CITA_INT = re.compile(r'int ([0-9A-F]{2})h AH=([0-9A-F]{2})h en 0x([0-9A-F]{4})')
CITA_TEXTO = re.compile(r'texto "([^"]*)"')
ASM_INT = re.compile(r'^\s*([0-9A-Fa-f]{4}):\s*int\s+([0-9A-Fa-f]{1,2})h\b')
ASM_MOV_AH = re.compile(r'\bmov\s+ah\s*,\s*([0-9A-Fa-f]{1,2})h?\b')
ASM_MOV_AX = re.compile(r'\bmov\s+ax\s*,\s*([0-9A-Fa-f]{1,4})h?\b')


def asm_de(tablero, func):
    """El desensamblado que el tablero dejo al lado: {direccion: linea}."""
    p = os.path.join(tablero, func, 'desensamblado.asm')
    if not os.path.exists(p):
        return None
    lineas = {}
    for l in open(p, encoding='utf-8', errors='replace'):
        m = re.match(r'^\s*([0-9A-Fa-f]{4})[\s:]', l)
        if m:
            lineas[int(m.group(1), 16)] = l.rstrip('\n')
    return lineas


def comprueba(tablero, mutar):
    exp = json.load(open(os.path.join(tablero, 'expediente.json'), encoding='utf-8'))
    fallos, revisadas, citas, textos = [], 0, 0, 0

    for f in exp['funciones']:
        ev = f.get('evidencia_del_nombre') or []
        if f.get('nombre') and not any(re.search(r'0x[0-9A-F]{4}|\[[0-9A-F]{4}h\]', e) for e in ev):
            fallos.append('%s: tiene nombre "%s" y ninguna evidencia cita una direccion'
                          % (f['func'], f['nombre']))
        asm = asm_de(tablero, f['func'])
        if asm is None:
            continue
        revisadas += 1
        for e in ev:
            for t in CITA_TEXTO.findall(e):
                textos += 1
                if len(t) < 4:
                    fallos.append('%s: cita un texto de menos de 4 caracteres: %r' % (f['func'], t))
            m = CITA_INT.search(e)
            if not m:
                continue
            citas += 1
            n, ah, at = int(m.group(1), 16), int(m.group(2), 16), int(m.group(3), 16)
            if mutar:
                ah = (ah + 1) & 0xFF          # el control negativo: la cita deja de ser cierta
            linea = asm.get(at)
            if linea is None:
                fallos.append('%s: cita una INT en 0x%04X y ahi no hay instruccion' % (f['func'], at))
                continue
            mi = ASM_INT.match(linea)
            if not mi or int(mi.group(2), 16) != n:
                fallos.append('%s: en 0x%04X dice `int %02Xh` y el desensamblado pone: %s'
                              % (f['func'], at, n, linea.strip()))
                continue
            # El `mov ah` que justifica la subfuncion, buscado hacia atras como lo hace el generador.
            dirs = sorted(d for d in asm if d < at)
            visto = False
            for d in reversed(dirs[-12:]):
                l = asm[d]
                ma, mx = ASM_MOV_AH.search(l), ASM_MOV_AX.search(l)
                if ma and int(ma.group(1), 16) == ah: visto = True; break
                if mx and (int(mx.group(1), 16) >> 8) == ah: visto = True; break
            if not visto:
                fallos.append('%s: dice AH=%02Xh para la int de 0x%04X y no hay `mov ah` que lo ponga'
                              % (f['func'], ah, at))

    return fallos, revisadas, citas, textos


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--tablero', required=True)
    ap.add_argument('--mutar', action='store_true',
                    help='control negativo: corrompe el AH de cada cita y exige que salga ROJO')
    a = ap.parse_args()

    fallos, revisadas, citas, textos = comprueba(a.tablero, a.mutar)
    print('funciones con desensamblado revisadas: %d' % revisadas)
    print('citas de servicio comprobadas: %d   textos comprobados: %d' % (citas, textos))

    if a.mutar:
        # Con las citas corrompidas TIENE que fallar; si no, esto no comprueba nada.
        if fallos:
            print('CONTROL NEGATIVO VERDE: con el AH corrompido salen %d fallos '
                  '(el control si distingue)' % len(fallos))
            return 0
        print('CONTROL NEGATIVO ROJO: con el AH corrompido NO se queja de nada. '
              'La comprobacion no esta mirando lo que dice mirar.')
        return 1

    for x in fallos[:20]:
        print('  ROJO ' + x)
    if fallos:
        print('== NOMBRES: ROJO == %d evidencias no se sostienen' % len(fallos))
        return 1
    print('== NOMBRES: VERDE == toda evidencia citada existe en el desensamblado')
    print('   OJO: esto dice que las citas son ciertas, no que los nombres sean los mejores.')
    return 0


if __name__ == '__main__':
    sys.exit(main())
