#!/usr/bin/env python3
"""Desensambla la imagen LE ya cargada, en las MISMAS coordenadas que usa `pakuport le-run`.

Sin esto, seguir la pista de un fallo obliga a convertir a mano entre offset de fichero,
direccion del objeto y direccion de carga, y ahi es donde se cuelan los errores tontos.

    python tools\\le_loader\\desensamblar.py <exe> 0x5F340 [--n 24] [--base 0x10000]
"""
import argparse, os, sys
from capstone import Cs, CS_ARCH_X86, CS_MODE_32

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from le_ref import load

ap = argparse.ArgumentParser()
ap.add_argument('exe')
ap.add_argument('addr')
ap.add_argument('--n', type=int, default=24)
ap.add_argument('--base', default='0x10000')
a = ap.parse_args()

img = load(open(a.exe, 'rb').read(), int(a.base, 0)).mem
at = int(a.addr, 0)
md = Cs(CS_ARCH_X86, CS_MODE_32)
for k, i in enumerate(md.disasm(bytes(img[at:at + a.n * 8]), at)):
    print('  %08X  %-16s %s %s' % (i.address, i.bytes.hex(), i.mnemonic, i.op_str))
    if k + 1 >= a.n:
        break
