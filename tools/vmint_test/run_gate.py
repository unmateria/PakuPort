#!/usr/bin/env python3
"""Software-INT threaded-VM class gate: build VMINT.COM, export it in forced
partitionings, run each headless, and check the checksum written to RESULT.BIN.

The whole VM lives in the COM entry, which is ALWAYS lifted, so the discriminating
world is "entry lifted" (partition a). A second partition additionally seeds the
handler as its own lifted function to show the result is boundary-independent (the
handler is only ever reached through the installed IVT vector, so it runs in the
interp either way — mirrors tools/coroutine_test).

Success criterion (the VM-INT fix gate): every partition writes RESULT.BIN == 0x056F.
BEFORE the fix, the lifted `int E7` synthesises a nested-dispatch frame with return
IP = 0, so the handler reads the operand from a fixed byte and the checksum diverges
(RED). Exit 0 iff all partitions produced RESULT.BIN == expected.
"""
import json
import os
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
COM = os.path.join(HERE, "VMINT.COM")
GAME_DIR = os.path.join(REPO, "games", "vmint")
GAME_JSON = os.path.join(GAME_DIR, "game.json")
CLI = os.path.join(REPO, "src", "PakuPort.Cli")

AUTOEXIT_MS = os.environ.get("VMINT_AUTOEXIT_MS", "30000")

PARTITIONS = [
    ("a", "all-lifted",   []),          # entry lifted (THE discriminating world)
    ("b", "seed-handler", ["0x139"]),   # + handler lifted (boundary independence)
]


def run(cmd, cwd=None, env=None, timeout=400):
    e = dict(os.environ)
    if env:
        e.update(env)
    return subprocess.run(cmd, cwd=cwd or REPO, env=e, capture_output=True,
                          text=True, timeout=timeout)


def build_com():
    run([sys.executable, os.path.join(HERE, "gen_test.py")])
    with open(os.path.join(HERE, "labels.json")) as f:
        return json.load(f)


def write_game_json(extra_seeds):
    os.makedirs(GAME_DIR, exist_ok=True)
    cfg = {
        "id": "vmint", "title": "VMINT",
        "exePath": COM.replace("\\", "/"),
        "corpusDir": "", "format": "com",
        "loadSegment": "0x0110", "dsBase": "0x00000",
        "entryOverride": "0x100",          # pin the COM entry (else a seed hijacks it)
        "extraSeeds": extra_seeds,
    }
    with open(GAME_JSON, "w") as f:
        json.dump(cfg, f, indent=2)


def ensure_codemap():
    run(["dotnet", "run", "-c", "Release", "--no-build", "--project", CLI,
         "--", "init", COM])


def export_and_run(pid, extra_seeds):
    out_dir = os.path.join(REPO, "export", f"vmint-{pid}")
    for junk in ("bin", "obj"):
        p = os.path.join(out_dir, junk)
        if os.path.isdir(p):
            import shutil
            shutil.rmtree(p, ignore_errors=True)
    write_game_json(extra_seeds)

    exp = run(["dotnet", "run", "-c", "Release", "--no-build", "--project", CLI,
               "--", "export", "--game", GAME_JSON, "--out", out_dir])
    discovered = _grep(exp.stdout, "Funciones descubiertas")
    probe = [l for l in exp.stdout.splitlines() if "probe" in l.lower()]

    import shutil
    shutil.copyfile(COM, os.path.join(out_dir, "VMINT.COM"))
    b = run(["dotnet", "build", "-c", "Release"], cwd=out_dir)
    if b.returncode != 0:
        return {"id": pid, "error": "build failed", "out": b.stdout[-800:]}

    p = os.path.join(out_dir, "RESULT.BIN")
    if os.path.exists(p):
        os.remove(p)

    r = run(["dotnet", "run", "-c", "Release", "--no-build"], cwd=out_dir,
            env={"PAKU_AUTOEXIT_MS": AUTOEXIT_MS, "PAKU_NOSPLASH": "1"},
            timeout=int(AUTOEXIT_MS) // 1000 + 120)
    autoexit = _grep(r.stdout + r.stderr, "[autoexit]")

    res = _read_bin(p)
    return {"id": pid, "discovered": discovered, "probe": probe,
            "autoexit": autoexit, "res": res}


def _grep(text, needle):
    for line in reversed(text.splitlines()):
        if needle in line:
            return line.strip()
    return None


def _read_bin(path):
    if not os.path.exists(path):
        return None
    d = open(path, "rb").read()
    out = {"len": len(d), "hex": d.hex()}
    if len(d) >= 2:
        out["sum"] = struct.unpack("<H", d[:2])[0]
    return out


def main():
    labels = build_com()
    expected = labels["expected"]
    print(f"VMINT.COM {labels['size']} bytes; INT 0x{labels['int_no']:02X}; "
          f"expecting sum==0x{expected:04X} ({expected})")
    print("Building CLI...")
    b = run(["dotnet", "build", "src/PakuPort.Cli/PakuPort.Cli.csproj", "-c", "Release"])
    if b.returncode != 0:
        print(b.stdout[-1500:]); print("CLI build FAILED", file=sys.stderr); return 2
    ensure_codemap()

    results = []
    for pid, name, seeds in PARTITIONS:
        print(f"\n=== partition {pid} ({name}) extraSeeds={seeds} ===")
        res = export_and_run(pid, seeds)
        results.append((name, res))
        print(f"  {res.get('discovered')}")
        for pl in res.get("probe", []):
            print(f"  {pl}")
        print(f"  {res.get('autoexit')}")
        print(f"  RESULT.BIN={res.get('res')}")

    print("\n================ SUMMARY ================")
    all_ok = True
    for name, r in results:
        res = r.get("res")
        ok = res is not None and res.get("sum") == expected
        all_ok = all_ok and ok
        verdict = "PASS" if ok else "FAIL"
        detail = (f"sum=0x{res['sum']:04X}" if res else "no output")
        print(f"  {r['id']:2s} {name:14s} {verdict}  {detail}")
    print(f"\nGATE: {'GREEN (all 0x%04X)' % expected if all_ok else 'RED (VM-INT class present)'}")
    return 0 if all_ok else 1


if __name__ == "__main__":
    sys.exit(main())
