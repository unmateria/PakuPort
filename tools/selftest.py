"""PakuPort's end-to-end self-test, on the bundled sample game (samples/catch).

Runs the whole chain a user would run, and demands exact results instead of "it didn't crash":

  1. the solution builds and the unit tests pass;
  2. samples/catch/src rebuilds CATCH.COM byte-identical to the committed binary;
  3. `pakuport port` ports it and the export compiles;
  4. the port is deterministic (two runs cut at the same instruction leave the same memory and
     the same screen) and the screen matches the pinned reference;
  5. a corpus is recorded and every case reproduces (`corpus-diag`);
  6. the remake board is generated, the three reference translations in samples/catch/solutions
     are GREEN with exact rhythm, and a one-unit mutation turns one RED (the judge can say no);
  7. the translations are plugged into the game (`export --portadas`) and the A/B control proves
     the game runs them and ends up byte-identical.

It works in its own id (`selftest`), so it never touches a games/catch you are using for the
tutorial, and deletes games/selftest and export/selftest at the end unless --keep is given.

Usage:  python tools/selftest.py [--keep] [--repin]
Log:    logs/selftest.log
"""
import hashlib
import json
import os
import re
import shutil
import subprocess
import sys
import time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
ID = 'selftest'
GAME_DIR = os.path.join(ROOT, 'games', ID)
EXPORT = os.path.join(ROOT, 'export', ID)
SAMPLE = os.path.join(ROOT, 'samples', 'catch')
PIN = os.path.join(SAMPLE, 'selftest-pin.json')
CUT = 60_000_000          # instructions: ~20 s of game time at 3000 instructions/ms

os.makedirs(os.path.join(ROOT, 'logs'), exist_ok=True)
_log = open(os.path.join(ROOT, 'logs', 'selftest.log'), 'a', encoding='utf-8')
failures = []


def log(msg):
    print(msg, flush=True)
    _log.write(f'[{time.strftime("%H:%M:%S")}] {msg}\n')
    _log.flush()


def run(args, cwd=ROOT, env=None, timeout=3600):
    p = subprocess.run(args, cwd=cwd, env=env, capture_output=True, text=True,
                       encoding='utf-8', errors='replace', timeout=timeout)
    out = (p.stdout or '') + (p.stderr or '')
    _log.write(f'$ {" ".join(args)}  (cwd={cwd})\n{out}\n')
    return p.returncode, out


def pakuport(*args, timeout=3600):
    return run(['dotnet', 'run', '-c', 'Release', '--no-build', '--project',
                os.path.join('src', 'PakuPort.Cli'), '--'] + list(args), timeout=timeout)


def check(name, ok, detail=''):
    log(('GREEN ' if ok else 'RED   ') + name + (f'  ({detail})' if detail else ''))
    if not ok:
        failures.append(name)
    return ok


def game_run(extra):
    """One headless, silent, unthrottled run of the export, cut at CUT instructions."""
    env = dict(os.environ, PAKU_HEADLESS='1', PAKU_NOSPLASH='1', PAKU_NOAUDIO='1',
               PAKU_INTERP_ONLY='1', PAKU_SIN_FRENO='1', PAKU_AUTOEXIT_INSN=str(CUT),
               PAKU_TAPE_REPLAY=os.path.join(GAME_DIR, 'empty.tape'))
    env.update(extra)
    return run(['dotnet', 'run', '-c', 'Release', '--no-build'], cwd=EXPORT, env=env)


def sha(path):
    return hashlib.sha256(open(path, 'rb').read()).hexdigest() if os.path.exists(path) else None


def main():
    keep = '--keep' in sys.argv
    repin = '--repin' in sys.argv
    log(f'=== selftest ({time.strftime("%Y-%m-%d %H:%M")}) ===')

    # 1. build + unit tests
    rc, _ = run(['dotnet', 'build', 'PakuPort.slnx', '-c', 'Release'])
    if not check('the solution builds', rc == 0):
        return 1
    rc, out = run(['dotnet', 'test', os.path.join('tests', 'PakuPort.Tests'), '-c', 'Release', '--no-build'])
    m = re.search(r'(?:Failed|Con error)[!:]?.*?(\d+).*?(?:Passed|Superado)[:]?\s*(\d+)', out)
    check('unit tests pass', rc == 0, m.group(0) if m else '')

    # 2. the sample rebuilds byte-identical
    tmp_com = os.path.join(ROOT, 'logs', 'selftest-CATCH.COM')
    rc, _ = run(['dotnet', 'run', '-c', 'Release', '--project', os.path.join(SAMPLE, 'src'), '--', tmp_com])
    check('samples/catch/src rebuilds CATCH.COM byte-identical',
          rc == 0 and sha(tmp_com) == sha(os.path.join(SAMPLE, 'dos', 'CATCH.COM')))

    # 3. port (into its own id)
    for d in (GAME_DIR, EXPORT):
        shutil.rmtree(d, ignore_errors=True)
    src_dir = os.path.join(ROOT, 'logs', 'selftest-dos')
    shutil.rmtree(src_dir, ignore_errors=True)
    os.makedirs(src_dir)
    shutil.copyfile(os.path.join(SAMPLE, 'dos', 'CATCH.COM'), os.path.join(src_dir, 'SELFTEST.COM'))
    rc, out = pakuport('port', os.path.join(src_dir, 'SELFTEST.COM'), '--no-package', '--no-smoke')
    if not check('pakuport port builds the export', rc == 0 and 'build: OK' in out):
        return finish(keep)
    gj = os.path.join(GAME_DIR, 'game.json')
    cfg = json.load(open(gj, encoding='utf-8'))
    cfg['cyclesPerMs'] = 3000                      # a tape needs a fixed speed
    json.dump(cfg, open(gj, 'w', encoding='utf-8'), indent=2)
    open(os.path.join(GAME_DIR, 'empty.tape'), 'w').write('0 T 0\n')
    # game.json is baked into the export when it is generated: re-export after editing it.
    rc, _ = pakuport('export', '--game', gj)
    rc2, _ = run(['dotnet', 'build', '-c', 'Release'], cwd=EXPORT)
    if not check('re-export with a fixed speed builds', rc == 0 and rc2 == 0):
        return finish(keep)

    # 4. determinism + pinned screen
    dumps = []
    for i in range(2):
        mem = os.path.join(ROOT, 'logs', f'selftest-mem{i}.bin')
        for f in (mem, mem + '.vram'):
            if os.path.exists(f):
                os.remove(f)
        game_run({'PAKU_MEMDUMP': mem})
        dumps.append((sha(mem), sha(mem + '.vram')))
    check('two identical runs leave the same memory and screen',
          dumps[0][0] is not None and dumps[0][1] is not None and dumps[0] == dumps[1])
    if repin and dumps[0][0] is not None and dumps[0] == dumps[1]:
        json.dump({'insn': CUT, 'memory': dumps[0][0], 'screen': dumps[0][1]},
                  open(PIN, 'w', encoding='utf-8'), indent=2)
        log(f'pinned -> {os.path.relpath(PIN, ROOT)}')
    pin = json.load(open(PIN, encoding='utf-8')) if os.path.exists(PIN) else {}
    check('memory and screen match the pinned reference',
          dumps[0][0] is not None and pin.get('memory') == dumps[0][0] and pin.get('screen') == dumps[0][1],
          'run with --repin after an intended change' if pin else 'no pin yet: run with --repin')

    # 5. corpus
    corpus = os.path.join(GAME_DIR, 'corpus')
    game_run({'PAKU_CORPUS': corpus})
    rc, out = pakuport('corpus-diag', '--corpus', corpus, '--game', gj)
    por_caso = re.search(r'-- por CASO --\n((?:\s+\S+\s+\d+\n)+)', out)
    rows = re.findall(r'^\s+(\S+)\s+(\d+)$', por_caso.group(1), re.M) if por_caso else []
    check('every recorded case reproduces', bool(rows) and all(k == 'Verde' for k, _ in rows),
          ', '.join(f'{k}={n}' for k, n in rows))

    # 6. board + judge, both ways
    board = os.path.join(GAME_DIR, 'expediente')
    rc, out = pakuport('expediente', 'tablero', '--game', gj, '--corpus', corpus)
    if not check('the remake board is generated', rc == 0 and os.path.isdir(board)):
        return finish(keep)
    solutions = os.path.join(SAMPLE, 'solutions')
    for fn in os.listdir(solutions):
        shutil.copyfile(os.path.join(solutions, fn, 'respuesta.cs'), os.path.join(board, fn, 'respuesta.cs'))
    rc, out = pakuport('banco', 'juzga', '--banco', board, '--exige-ritmo')
    m = re.search(r'VERDE=(\d+)\s+ROJO=(\d+)\s+NO COMPILA=(\d+)', out)
    check('the three reference translations are GREEN with exact rhythm',
          bool(m) and m.groups() == ('3', '0', '0'), m.group(0) if m else 'no summary')
    target = os.path.join(board, 'FUN_1000_189', 'respuesta.cs')
    good = open(target, encoding='utf-8').read()
    open(target, 'w', encoding='utf-8').write(good.replace('13849', '13848'))
    rc, out = pakuport('banco', 'juzga', '--banco', board)
    m = re.search(r'VERDE=(\d+)\s+ROJO=(\d+)', out)
    check('a one-unit mutation is caught (negative control)',
          bool(m) and m.groups() == ('2', '1'), m.group(0) if m else 'no summary')
    open(target, 'w', encoding='utf-8').write(good)

    # 7. the translations inside the game
    rc, out = pakuport('export', '--game', gj, '--portadas', board)
    rc2, _ = run(['dotnet', 'build', '-c', 'Release'], cwd=EXPORT)
    if check('export --portadas builds', rc == 0 and rc2 == 0):
        rc, out = run([sys.executable, os.path.join('tools', 'control_portadas.py'), '--export', EXPORT,
                       '--cinta', os.path.join(GAME_DIR, 'empty.tape'), '--insn', str(CUT)])
        check('A/B: the game runs the translations and ends byte-identical', rc == 0)

    return finish(keep)


def finish(keep):
    if not keep:
        for d in (GAME_DIR, EXPORT, os.path.join(ROOT, 'logs', 'selftest-dos')):
            shutil.rmtree(d, ignore_errors=True)
    log('\n== SELFTEST: ' + ('GREEN ==' if not failures else 'RED == ' + '; '.join(failures)))
    return 1 if failures else 0


if __name__ == '__main__':
    sys.exit(main())
