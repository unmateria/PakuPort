"""Do the fleet's exports still compile with the Core you just changed? Without regenerating them.

`tools/republicar_flota.py --compilar` builds each export with the Core/ it ALREADY has, so after
touching src/PakuPort.Core it compiles old code and comes out green without having looked at what
you wrote (it happened: a Sound Blaster log kept its two-day-old format after a green --compilar).
Regenerating the 80 exports to find out takes an hour and rewrites the exports.

This copies only each host's SOURCES (.cs/.csproj, not the game data or bin/obj/logs) to a
temporary directory, puts the current Core in them with the same copier `export` uses
(ExportCore.Copiar) and compiles. The real exports and the deliveries are not touched. What it
does not check: a change to the host TEMPLATE (src/PakuPort.Cli/Program.cs) needs a real re-export.

The temporary copies are deleted when everything is green; on red they are kept, with each game's
compilation.log, and the path is printed.

Usage (from the repo root, after touching src/PakuPort.Core):
    python tools/export_core_check/check_fleet.py [--exports export] [--juegos id1,id2] [--todos]
By default it checks the games in FLOTA (tools/republicar_flota.py); --todos, or a repository
without that list, takes every export with a .csproj.
"""
import argparse
import ast
import os
import shutil
import subprocess
import sys
import tempfile
from concurrent.futures import ThreadPoolExecutor

ROOT = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
SOURCES = ('.cs', '.csproj', '.props', '.targets')
SKIP = {'bin', 'obj', 'logs', 'Core'}


def flota():
    tree = ast.parse(open(os.path.join(ROOT, 'tools', 'republicar_flota.py'), encoding='utf-8').read())
    for node in tree.body:
        if isinstance(node, ast.Assign) and any(isinstance(t, ast.Name) and t.id == 'FLOTA' for t in node.targets):
            return [game for game, _ in ast.literal_eval(node.value)]
    raise SystemExit('no encuentro FLOTA en tools/republicar_flota.py')


def copy_sources(source, target):
    for d, dirs, files in os.walk(source):
        dirs[:] = [x for x in dirs if not (os.path.samefile(d, source) and x in SKIP)]
        for f in files:
            if f.endswith(SOURCES):
                rel = os.path.relpath(os.path.join(d, f), source)
                os.makedirs(os.path.dirname(os.path.join(target, rel)), exist_ok=True)
                shutil.copyfile(os.path.join(d, f), os.path.join(target, rel))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--exports', default=os.path.join(ROOT, 'export'))
    ap.add_argument('--juegos', help='ids separados por comas')
    ap.add_argument('--todos', action='store_true')
    a = ap.parse_args()

    lista = os.path.join(ROOT, 'tools', 'republicar_flota.py')
    if a.juegos:
        ids = a.juegos.split(',')
    elif a.todos or not os.path.exists(lista):   # no fleet list: every export
        ids = sorted(d for d in os.listdir(a.exports)
                     if any(f.endswith('.csproj') for f in os.listdir(os.path.join(a.exports, d))))
    else:
        ids = flota()
    tmp = tempfile.mkdtemp(prefix='export-core-check-')
    targets = []
    for game in ids:
        source = os.path.join(a.exports, game)
        if not os.path.isdir(source):
            print(f'{game}: (no hay export, se salta)')
            continue
        target = os.path.join(tmp, game)
        copy_sources(source, target)
        targets.append((game, target))
    if not targets:
        shutil.rmtree(tmp, ignore_errors=True)
        raise SystemExit('ROJO: ningun export que comprobar')

    r = subprocess.run(['dotnet', 'run', '--project', os.path.join(ROOT, 'tools', 'export_core_check'),
                        '-c', 'Release', '--', *[t for _, t in targets]],
                       cwd=ROOT, capture_output=True, text=True, encoding='utf-8', errors='replace')
    if r.returncode != 0:
        print((r.stdout + r.stderr)[-2000:])
        raise SystemExit(f'ROJO: no se pudo copiar el Core (copias en {tmp})')

    def compila(item):
        game, path = item
        b = subprocess.run(['dotnet', 'build', '-c', 'Release', '--nologo', '-clp:ErrorsOnly'], cwd=path,
                           capture_output=True, text=True, encoding='utf-8', errors='replace', timeout=900)
        with open(os.path.join(path, 'compilation.log'), 'w', encoding='utf-8') as fh:
            fh.write(b.stdout + b.stderr)
        print(f'{game}: {"VERDE" if b.returncode == 0 else "ROJO"}', flush=True)
        return b.returncode == 0

    with ThreadPoolExecutor(max_workers=2) as pool:
        ok = list(pool.map(compila, targets))
    if all(ok):
        shutil.rmtree(tmp, ignore_errors=True)
        print(f'VERDE: {len(ok)} exports compilan con el Core actual')
        return 0
    print(f'ROJO: {ok.count(False)} de {len(ok)} no compilan; copias y compilation.log en {tmp}')
    return 1


if __name__ == '__main__':
    sys.exit(main())
