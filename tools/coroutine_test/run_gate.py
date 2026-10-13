#!/usr/bin/env python3
"""FarRet co-routine class gates. Two synthetic suites, both hand-assembled COMs exported in
THREE forced partitionings (via `extraSeeds` in games/<id>/game.json; `entryOverride` pinned
to the COM entry 0x100 so seeding an island does NOT hijack the entry point), built and run
headless, judged by the counter written to RES.BIN.

  CORO  (gen_test.py, CORO.COM) — the INTRA-module co-routine (blitter) class. Its retf lands
        on a continuation at the caller's own stack level (SP <= _spEntry) -> the unwind STOPS
        at that frame -> the FarRet redirect fires. GREEN today: all three write
        RES.BIN == 10000. This suite is a REGRESSION guard for the landed fix.

  DEEP  (gen_deep.py, DEEP.COM) — the "unwind-to-the-bottom" class, which CORO does NOT
        cover. A driver-init retf returns to a continuation pushed
        on a RELOCATED low driver stack, discarding several frames at once; the popped SP is a
        positive signed delta from EVERY frame's _spEntry (including the COM entry's 0xFFFE) ->
        every C# guard takes the PROPAGATE branch -> the thread exits cleanly BEFORE the
        continuation runs (the redirect only acts in the STOP branch, so it is never invoked).
        TODAY (HEAD): RED = NEITHER RES.BIN nor FAIL.BIN is written (a clean "salida limpia"
        thread exit, matching the AdLib symptom). When the fix lands (resume at the bottom of
        the thread when the unwind transports a DriverRegistry destination) this suite flips to
        GREEN (RES.BIN == 10000). A FAIL.BIN would instead mean a poison fired (the unwind
        stopped/redirected rather than propagated) — a DIFFERENT mechanism, reported explicitly.

  The DEEP program is proven CORRECT on real hardware by the DOSBox-X oracle (deep_oracle.conf):
  it writes RES.BIN == 10000. In pure interpretation (Interp.WholeProgram, no lifted C# frames)
  the retf just continues to the continuation (Interp.cs Retf case, WholeProgram branch) — also
  correct. So the DEEP red is the PORT's lifted-frame unwind model losing the thread, NOT a bug
  in the synthetic program.

  NCOR  (gen_ncor.py, NCORA.COM + NCORB.COM) — the "near-consumed co-routine" class (seen in
        a real-mode game). A cross-segment driver retf (real-mode ALIAS CS+0x10, same
        linear) PROPAGATES through the far-site guard (ShouldFarRetRedirect vetoes: FarRetCs !=
        c.CS) and STOPS at the caller's NEAR call-site guard, whose CONSUMED branch discards
        the transported destination and resumes inline. NCORA = the class: RED expected on HEAD
        (FAIL.BIN trap 0xDDDD + farret-unwind in gaps.log); reported like DEEP's red (class
        present, does NOT fail the gate). NCORB = the LEGITIMATE veto case (near site WITH
        inline params: the discarded mismatch resumes via the lifted `goto L_skip`, which IS
        the destination the driver transported): GREEN expected on HEAD and it must STAY green
        with any future fix — its BX canary catches a double-executed continuation (trap 0xEEEE)
        and the tripwire inline bytes catch a lost inline-params detection (trap 0xCCCC).

Exit 0 iff the state is as expected: CORO all-green (fix intact) AND the DEEP oracle green
(program correct). The DEEP export verdict is reported (RED today = class present; GREEN = fix
landed) and its 3 partitions must agree (deterministic). Same contract for NCOR: both oracles
green, NCORA partitions agree (class/fixed reported), NCORB must be all-green.
"""
import json
import os
import shutil
import struct
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
CLI = os.path.join(REPO, "src", "PakuPort.Cli")

AUTOEXIT_MS = os.environ.get("CORO_AUTOEXIT_MS", "30000")
DOSBOX = os.environ.get("PAKU_DOSBOX") or shutil.which("dosbox-x") or "dosbox-x"

# Each suite: the generator, the COM name, the derived game id (= COM basename lowercased, the
# name `init` writes to games/<id>/), and three forced partitionings (BUCLE/DISPATCH & FILA/INNER
# island segoffs from *_labels.json). "expect" documents the intended HEAD verdict.
SUITES = [
    {
        "id": "coro", "gen": "gen_test.py", "com": "CORO.COM", "labels": "labels.json",
        "expect": "green",   # intra-module co-routine: the landed fix keeps all three at 10000
        "partitions": [
            ("a", "all-lifted",  ["0x113", "0x13D"]),  # BUCLE 0x113, FILA 0x13D
            ("b", "none",        []),
            ("c", "fila-only",   ["0x13D"]),
        ],
    },
    {
        "id": "deep", "gen": "gen_deep.py", "com": "DEEP.COM", "labels": "deep_labels.json",
        "expect": "class-red",  # today no RES/no FAIL (clean exit); after the fix, 10000
        "partitions": [
            ("a", "all-lifted",  ["0x11C", "0x129"]),  # DISPATCH 0x11C, INNER 0x129
            ("b", "none",        []),
            ("c", "inner-only",  ["0x129"]),
        ],
    },
    {
        # PSTK (gen_pstk.py) — private-stack driver far-return (evidence: a real-mode game's AdLib driver):
        # the driver relocates SP, re-pushes the caller's GENUINE return pair on its private stack
        # and retf's there. GREEN = RES.BIN == 10000 AND zero farret-unwind in logs/gaps.log (a
        # by-value genuine return must not unwind at all). RED (pre-fix) = RES.BIN correct via the
        # bottom-resume crutch BUT the discard is logged. "@NAME" seeds resolve via *_labels.json.
        "id": "pstk", "gen": "gen_pstk.py", "com": "PSTK.COM", "labels": "pstk_labels.json",
        "expect": "class-red",
        "partitions": [
            ("a", "drv-lifted",  ["@DRV"]),
            ("b", "none",        []),
        ],
    },
    {
        # NCORA (gen_ncor.py) — near-consumed co-routine discard (near-consumed class). The far-site
        # redirect is vetoed cross-segment (driver at alias CS+0x10) and the NEAR guard's consumed
        # branch discards the transported destination -> poison trap_pair. RED expected on HEAD
        # (FAIL.BIN 0xDDDD); flips to GREEN (RES.BIN == 10000) when the near-consumed fix lands.
        "id": "ncora", "gen": "gen_ncor.py", "com": "NCORA.COM", "labels": "ncora_labels.json",
        "expect": "class-red",
        # PAKU_NREDIR=1: the near redirect ships OFF by default (a manual playtest regression:
        # it recursed/misfired in a real game) — the gate proves the mechanism works behind the flag.
        "env": {"PAKU_NREDIR": "1"},
        "partitions": [
            ("a", "islands",     ["@BUCLE", "@DRV"]),
            ("b", "none",        []),
        ],
    },
    {
        # NCORB (gen_ncor.py) — the LEGITIMATE near-consumed mismatch (inline-params call-site:
        # the lifted `goto L_skip` resumes exactly where the driver's retf transported). GREEN
        # expected on HEAD and with ANY future fix (a redirect here would double-run the
        # continuation -> BX canary -> FAIL.BIN 0xEEEE; lost inline detection -> 0xCCCC).
        "id": "ncorb", "gen": "gen_ncor.py", "com": "NCORB.COM", "labels": "ncorb_labels.json",
        "expect": "green",
        # The veto case must hold precisely when the near redirect is ENABLED.
        "env": {"PAKU_NREDIR": "1"},
        "partitions": [
            ("a", "islands",     ["@BUCLE", "@DRV"]),
            ("b", "none",        []),
        ],
    },
    {
        # NCORC (gen_ncor.py) — the RESIDUAL-FLAG frontier ([NREDIR] counterexamples): the
        # far guard's PROPAGATE-redirect enters Interp.Run WITHOUT consuming c.FarRet; when the
        # co-routine ends in a balanced near ret (Run == false) the stale flag travels up and
        # the next guard re-redirects to the already-attended destination -> K double-executed.
        # RED expected on HEAD (FAIL.BIN 0xDCDC); GREEN (RES.BIN == 1, clean gaps.log) when the
        # redirect frontier consumes the flag. Both partitions exercise the LIFTED guard (the
        # class lives there; the interp's own redirects already consume before continuing).
        "id": "ncorc", "gen": "gen_ncor.py", "com": "NCORC.COM", "labels": "ncorc_labels.json",
        "expect": "class-red",
        "partitions": [
            ("a", "islands",     ["@A_ISLAND", "@DRV"]),
            ("b", "a-only",      ["@A_ISLAND"]),
        ],
    },
    {
        # DE (gen_de.py) — #DE divide-fault parity lifted-vs-interp (C runtime long-division
        # helper class). BUCLE does 10000 `div di` with DI=0; the INT 0
        # handler counts and IRETs (8086: resume after the div). BOTH partitions end up with
        # the div LIFTED (the export probe executes the COM and discovers BUCLE dynamically
        # even without seeds), so both were RED pre-fix (DivideByZeroException, no RES.BIN)
        # and GREEN is mandatory on both with the LiftMulDiv INT 0 trampoline. The reference
        # semantics is the interp's own Div/Idiv catch (Interp.cs), already exercised by a
        # tested game. No DOSBox oracle: #DE pushed-IP semantics vary per cputype — gen_de.py.
        "id": "de", "gen": "gen_de.py", "com": "DE.COM", "labels": "de_labels.json",
        "expect": "green",
        "partitions": [
            ("a", "bucle-lifted", ["@BUCLE"]),
            ("b", "none",         []),
        ],
    },
    {
        # ISTK (gen_istk.py) — the AdLib-driver hang class: a hardware timer ISR that switches to a
        # PRIVATE stack (higher-absolute SP, whose signed delta from the interrupted SP wraps
        # positive) and returns via IRET. The port delivers IRQ0 as a NESTED Interp.Run
        # (DosPlatform.FireInterrupt); WITHOUT the IsrMode fix the interp's frame-termination
        # heuristic `(short)(SP - sp0) > 0` bails on the FIRST ret executed on the private stack,
        # mid-ISR, leaving SP relocated -> the interrupted flow's next ret pops 0x0000 (in the
        # game: the driver's zero header -> permanent black screen). RED (pre-fix) = FAIL.BIN trap 0xADAD
        # (the main loop caught SP dropped below 0x8000). GREEN (fix) = RES.BIN (the ISR ran to its
        # terminal IRET; SP restored). Forced interp-only (PAKU_INTERP_ONLY): the bug lives in the
        # nested-ISR dispatch of the whole-program interpreter. Deterministic — the COM programs
        # the PIT fast so IRQ0 fires inside the loop; the DOSBox-X oracle proves the program correct.
        "id": "istk", "gen": "gen_istk.py", "com": "ISTK.COM", "labels": "istk_labels.json",
        "expect": "green",
        "env": {"PAKU_INTERP_ONLY": "1"},
        "partitions": [
            ("a", "interp", []),
        ],
    },
    {
        # IIDM (gen_iidm.py) — the sound-driver crash class: inside a hardware timer ISR the
        # game returns from a subroutine via the `pushf/push cs/call ... iret` IDIOM (far ret +
        # flags restore). That intermediate IRET is NOT the ISR's terminal one, but the nested
        # Interp.Run (IsrMode, WholeProgram=false, softIntDepth=0) ends on the FIRST IRET ->
        # the ISR tail never runs, its unpopped words leak onto the interrupted stack (in the
        # game: the blitter's next retf popped CS=0006 and walked the IVT as code). RED (pre-fix) =
        # FAIL.BIN trap 0xB1B1 (main loop caught SP != init). GREEN (fix: in IsrMode an IRET is
        # terminal only at the nested run's entry stack level SS:SP==ss0:sp0) = RES.BIN present.
        "id": "iidm", "gen": "gen_iidm.py", "com": "IIDM.COM", "labels": "iidm_labels.json",
        "expect": "green",
        "env": {"PAKU_INTERP_ONLY": "1"},
        "partitions": [
            ("a", "interp", []),
        ],
    },
]


def run(cmd, cwd=None, env=None, timeout=400):
    e = dict(os.environ)
    if env:
        e.update(env)
    return subprocess.run(cmd, cwd=cwd or REPO, env=e, capture_output=True,
                          text=True, timeout=timeout)


def gen_com(suite):
    run([sys.executable, os.path.join(HERE, suite["gen"])])
    with open(os.path.join(HERE, suite["labels"])) as f:
        return json.load(f)


def game_json_path(suite):
    return os.path.join(REPO, "games", suite["id"], "game.json")


def write_game_json(suite, extra_seeds):
    cfg = {
        "id": suite["id"], "title": suite["id"].upper(),
        "exePath": os.path.join(HERE, suite["com"]).replace("\\", "/"),
        "corpusDir": "", "format": "com",
        "loadSegment": "0x0110", "dsBase": "0x00000",
        "entryOverride": "0x100",          # pin the COM entry (else a seed hijacks it)
        "extraSeeds": extra_seeds,
    }
    with open(game_json_path(suite), "w") as f:
        json.dump(cfg, f, indent=2)


def ensure_codemap(suite):
    # `init` derives games/<id>/{game.json,codemap.json} (all COM offsets = code). game.json is
    # overwritten per partition afterwards, but codemap.json (read by GameConfig.Load) persists.
    run(["dotnet", "run", "-c", "Release", "--no-build", "--project", CLI,
         "--", "init", os.path.join(HERE, suite["com"])])


def export_and_run(suite, pid, extra_seeds):
    out_dir = os.path.join(REPO, "export", f"{suite['id']}-{pid}")
    for junk in ("bin", "obj"):
        p = os.path.join(out_dir, junk)
        if os.path.isdir(p):
            shutil.rmtree(p, ignore_errors=True)
    write_game_json(suite, extra_seeds)

    exp = run(["dotnet", "run", "-c", "Release", "--no-build", "--project", CLI,
               "--", "export", "--game", game_json_path(suite), "--out", out_dir])
    discovered = _grep(exp.stdout, "Funciones descubiertas")
    probe = [l for l in exp.stdout.splitlines() if "probe" in l.lower()]

    os.makedirs(out_dir, exist_ok=True)
    shutil.copyfile(os.path.join(HERE, suite["com"]), os.path.join(out_dir, suite["com"]))
    b = run(["dotnet", "build", "-c", "Release"], cwd=out_dir)
    if b.returncode != 0:
        return {"id": pid, "error": "build failed", "out": b.stdout[-800:]}

    for f in ("RES.BIN", "FAIL.BIN"):
        p = os.path.join(out_dir, f)
        if os.path.exists(p):
            os.remove(p)
    # a clean run writes NO gaps.log — remove the previous run's or it reads as a stale discard
    shutil.rmtree(os.path.join(out_dir, "logs"), ignore_errors=True)

    run_env = {"PAKU_AUTOEXIT_MS": AUTOEXIT_MS, "PAKU_NOSPLASH": "1"}
    run_env.update(suite.get("env") or {})
    r = run(["dotnet", "run", "-c", "Release", "--no-build"], cwd=out_dir,
            env=run_env, timeout=int(AUTOEXIT_MS) // 1000 + 120)
    autoexit = _grep(r.stdout + r.stderr, "[autoexit]")
    exitreason = _grep(r.stdout + r.stderr, "limpia") or _grep(r.stdout + r.stderr, "volvio a DOS")
    farret = [l for l in (r.stdout + r.stderr).splitlines() if "farret-unwind" in l]

    # gaps.log is the durable record of discarded destinations (PSTK judges on it)
    gaps_farret = []
    gp = os.path.join(out_dir, "logs", "gaps.log")
    if os.path.exists(gp):
        with open(gp) as gf:
            gaps_farret = [l.strip() for l in gf if "farret-unwind" in l]

    res = _read_bin(os.path.join(out_dir, "RES.BIN"))
    fail = _read_bin(os.path.join(out_dir, "FAIL.BIN"))
    return {
        "id": pid, "discovered": discovered, "probe": probe,
        "autoexit": autoexit, "exitreason": exitreason, "farret": farret[-3:],
        "gaps_farret": gaps_farret, "res": res, "fail": fail,
    }


def hw_oracle(conf_name, expected):
    """Run a suite's COM on the DOSBox-X oracle (real hardware). Proves the program is correct:
    RES.BIN == expected. This is the objective evidence that an export red is the port's
    fault, not the synthetic program's."""
    conf = os.path.join(HERE, conf_name)
    if not os.path.exists(DOSBOX) or not os.path.exists(conf):
        return {"skipped": f"dosbox={os.path.exists(DOSBOX)} conf={os.path.exists(conf)}"}
    for f in ("RES.BIN", "FAIL.BIN"):
        p = os.path.join(HERE, f)
        if os.path.exists(p):
            os.remove(p)
    # SDL_VIDEODRIVER=dummy: truly headless DOSBox (these programs only write files — no render
    # needed), so the oracle can NEVER steal the user's focus (verified: RES.BIN still written).
    # Minimized Start-Process stays as belt-and-braces if a build ignores the dummy driver.
    ps = (f"$p = Start-Process -FilePath '{DOSBOX}' "
          f"-ArgumentList '-conf','{conf}' -WindowStyle Minimized -PassThru; "
          f"if (-not $p.WaitForExit(60000)) {{ $p.Kill() }}")
    # cwd=REPO: the .conf mounts C: with a path relative to the repo root.
    subprocess.run(["powershell", "-NoProfile", "-Command", ps], cwd=REPO, timeout=90,
                   env={**os.environ, "SDL_VIDEODRIVER": "dummy"})
    res = _read_bin(os.path.join(HERE, "RES.BIN"))
    fail = _read_bin(os.path.join(HERE, "FAIL.BIN"))
    return {"res": res, "fail": fail,
            "ok": res is not None and res.get("counter") == expected}


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
        out["counter"] = struct.unpack("<H", d[:2])[0]
    if len(d) >= 4:
        out["trap"] = struct.unpack("<H", d[2:4])[0]
    return out


def run_suite(suite):
    labels = gen_com(suite)
    print(f"\n######## SUITE {suite['id'].upper()} ({suite['com']} {labels['size']} bytes; "
          f"expect counter=={labels['n_iters']} / 0x{labels['n_iters']:X}) ########")
    ensure_codemap(suite)
    results = []
    for pid, name, seeds in suite["partitions"]:
        seeds = [hex(labels["labels"][s[1:]]) if s.startswith("@") else s for s in seeds]
        print(f"\n=== {suite['id']} partition {pid} ({name}) extraSeeds={seeds} ===")
        res = export_and_run(suite, pid, seeds)
        results.append((name, res))
        print(f"  {res.get('discovered')}")
        for pl in res.get("probe", []):
            print(f"  {pl}")
        print(f"  {res.get('autoexit')}")
        print(f"  exit: {res.get('exitreason')}")
        for fl in res.get("farret", []):
            print(f"  {fl}")
        print(f"  gaps.log farret-unwind: {len(res.get('gaps_farret') or [])}")
        print(f"  RES.BIN={res.get('res')}")
        print(f"  FAIL.BIN={res.get('fail')}")
    return labels, results


def classify_deep(r, expected):
    """Deep partition verdict: 'fixed' (RES==expected), 'class' (no RES & no FAIL = clean
    propagate-to-death, the unwind-to-the-bottom bug), or 'other' (a poison fired / unexpected)."""
    res, fail = r.get("res"), r.get("fail")
    if res is not None and res.get("counter") == expected:
        return "fixed"
    if res is None and fail is None:
        return "class"
    return "other"


def main():
    all_suites = {}
    for suite in SUITES:
        all_suites[suite["id"]] = run_suite(suite)

    print("\n================ SUMMARY ================")
    ok = True

    # CORO: the landed intra-module fix must keep all three at 10000 (regression guard).
    coro_labels, coro_res = all_suites["coro"]
    exp = coro_labels["n_iters"]
    coro_green = 0
    for name, r in coro_res:
        res = r.get("res")
        g = res is not None and res.get("counter") == exp
        coro_green += 1 if g else 0
        detail = f"counter={res['counter']}" if res else (
            f"FAIL.BIN trap=0x{r['fail']['trap']:X}" if r.get("fail") else "no output")
        print(f"  CORO {r['id']:2s} {name:12s} {'PASS' if g else 'FAIL'}  {detail}")
    coro_ok = coro_green == len(coro_res)
    ok = ok and coro_ok
    print(f"  CORO: {coro_green}/{len(coro_res)} green (intra-module fix intact)"
          + ("" if coro_ok else "  <<< REGRESSION"))

    # DEEP: the unwind-to-the-bottom class. Report the verdict; the 3 partitions must agree.
    deep_labels, deep_res = all_suites["deep"]
    dexp = deep_labels["n_iters"]
    verdicts = []
    for name, r in deep_res:
        v = classify_deep(r, dexp)
        verdicts.append(v)
        res, fail = r.get("res"), r.get("fail")
        if v == "fixed":
            detail = f"counter={res['counter']} (fix landed)"
        elif v == "class":
            detail = "no RES / no FAIL = clean thread exit (unwind-to-the-bottom bug present)"
        else:
            detail = (f"FAIL.BIN trap=0x{fail['trap']:X} counter={fail['counter']} (poison: unwind stopped/redirected)"
                      if fail else f"RES={res} (unexpected)")
        print(f"  DEEP {r['id']:2s} {name:12s} {v.upper():5s} {detail}")
    deep_agree = len(set(verdicts)) == 1
    deep_verdict = verdicts[0] if deep_agree else "SPLIT"
    print(f"  DEEP: verdict={deep_verdict} across 3 partitions"
          + ("" if deep_agree else f"  <<< NON-DETERMINISTIC {verdicts}"))

    # DEEP oracle (real hardware): the program is correct -> the red is the port's fault.
    oracle = hw_oracle("deep_oracle.conf", dexp)
    if "skipped" in oracle:
        print(f"  DEEP oracle: SKIPPED ({oracle['skipped']}) — cannot prove program correctness")
        oracle_ok = False
    else:
        oracle_ok = oracle.get("ok", False)
        print(f"  DEEP oracle (DOSBox-X hardware): RES.BIN={oracle.get('res')} -> "
              + ("PROGRAM CORRECT (10000)" if oracle_ok else "UNEXPECTED"))

    ok = ok and oracle_ok and deep_agree

    # PSTK: private-stack driver far-return . GREEN needs counter==N AND a clean
    # gaps.log (zero farret-unwind): the genuine by-value return must not unwind at all.
    pstk_labels, pstk_res = all_suites["pstk"]
    pexp = pstk_labels["n_iters"]
    pverdicts = []
    for name, r in pstk_res:
        res, fail = r.get("res"), r.get("fail")
        nfr = len(r.get("gaps_farret") or [])
        if fail is not None:
            v, detail = "other", f"FAIL.BIN trap=0x{fail['trap']:X} counter={fail['counter']} (bad resume context)"
        elif res is not None and res.get("counter") == pexp:
            if nfr == 0:
                v, detail = "fixed", f"counter={res['counter']}, gaps.log clean (fix landed)"
            else:
                v, detail = "class", f"counter={res['counter']} via bottom-resume BUT {nfr} farret-unwind discards (class present)"
        else:
            v, detail = "other", f"RES={res} farret-unwind={nfr} (unexpected)"
        pverdicts.append(v)
        print(f"  PSTK {r['id']:2s} {name:12s} {v.upper():5s} {detail}")
    pstk_agree = len(set(pverdicts)) == 1
    pstk_verdict = pverdicts[0] if pstk_agree else "SPLIT"
    print(f"  PSTK: verdict={pstk_verdict} across {len(pverdicts)} partitions"
          + ("" if pstk_agree else f"  <<< NON-DETERMINISTIC {pverdicts}"))
    poracle = hw_oracle("pstk_oracle.conf", pexp)
    if "skipped" in poracle:
        print(f"  PSTK oracle: SKIPPED ({poracle['skipped']}) — cannot prove program correctness")
        poracle_ok = False
    else:
        poracle_ok = poracle.get("ok", False)
        print(f"  PSTK oracle (DOSBox-X hardware): RES.BIN={poracle.get('res')} -> "
              + ("PROGRAM CORRECT (10000)" if poracle_ok else "UNEXPECTED"))
    ok = ok and poracle_ok and pstk_agree

    # NCORA: near-consumed co-routine discard (near-consumed class). Verdict reported like DEEP's
    # red (class present = expected on HEAD, does NOT fail the gate); partitions must agree and
    # the oracle must prove the program correct.
    na_labels, na_res = all_suites["ncora"]
    naexp = na_labels["n_iters"]
    naverdicts = []
    na_signature = []
    for name, r in na_res:
        res, fail = r.get("res"), r.get("fail")
        nfr = len(r.get("gaps_farret") or [])
        if fail is not None and fail.get("trap") == 0xDDDD:
            v = "class"
            detail = (f"FAIL.BIN trap=0xDDDD counter={fail['counter']} + {nfr} farret-unwind "
                      "(near guard consumed & discarded the transported destination)")
            na_signature = r.get("gaps_farret") or na_signature
        elif res is not None and res.get("counter") == naexp:
            v, detail = "fixed", f"counter={res['counter']} (near-consumed fix landed)"
        else:
            v, detail = "other", f"RES={res} FAIL={fail} farret-unwind={nfr} (unexpected mechanism)"
        naverdicts.append(v)
        print(f"  NCORA {r['id']:2s} {name:12s} {v.upper():5s} {detail}")
    na_agree = len(set(naverdicts)) == 1
    na_verdict = naverdicts[0] if na_agree else "SPLIT"
    print(f"  NCORA: verdict={na_verdict} across {len(naverdicts)} partitions"
          + ("" if na_agree else f"  <<< NON-DETERMINISTIC {naverdicts}"))
    for sig in na_signature:
        print(f"  NCORA signature: {sig}")
    naoracle = hw_oracle("ncora_oracle.conf", naexp)
    if "skipped" in naoracle:
        print(f"  NCORA oracle: SKIPPED ({naoracle['skipped']}) — cannot prove program correctness")
        naoracle_ok = False
    else:
        naoracle_ok = naoracle.get("ok", False)
        print(f"  NCORA oracle (DOSBox-X hardware): RES.BIN={naoracle.get('res')} -> "
              + ("PROGRAM CORRECT (10000)" if naoracle_ok else "UNEXPECTED"))
    ok = ok and naoracle_ok and na_agree

    # NCORB: the legitimate veto case — must be GREEN on HEAD and stay green with any fix.
    # A red here FAILS the gate (0xEEEE = double-executed continuation, 0xCCCC = inline-params
    # detection lost, anything else = unexpected).
    nb_labels, nb_res = all_suites["ncorb"]
    nbexp = nb_labels["n_iters"]
    nb_green = 0
    for name, r in nb_res:
        res, fail = r.get("res"), r.get("fail")
        if fail is not None:
            trap = fail.get("trap")
            detail = {0xEEEE: "canary dead: continuation DOUBLE-EXECUTED (a fix broke the veto)",
                      0xCCCC: "inline bytes executed: inline-params site NOT detected",
                      }.get(trap, "unexpected poison")
            print(f"  NCORB {r['id']:2s} {name:12s} FAIL  FAIL.BIN trap=0x{trap:X} counter={fail['counter']} — {detail}")
        elif res is not None and res.get("counter") == nbexp:
            nb_green += 1
            print(f"  NCORB {r['id']:2s} {name:12s} PASS  counter={res['counter']} (legit near-consumed mismatch resumed via goto)")
        else:
            print(f"  NCORB {r['id']:2s} {name:12s} FAIL  RES={res} (no output / wrong counter)")
    nb_ok = nb_green == len(nb_res)
    print(f"  NCORB: {nb_green}/{len(nb_res)} green (legitimate veto case intact)"
          + ("" if nb_ok else "  <<< REGRESSION"))
    nboracle = hw_oracle("ncorb_oracle.conf", nbexp)
    if "skipped" in nboracle:
        print(f"  NCORB oracle: SKIPPED ({nboracle['skipped']}) — cannot prove program correctness")
        nboracle_ok = False
    else:
        nboracle_ok = nboracle.get("ok", False)
        print(f"  NCORB oracle (DOSBox-X hardware): RES.BIN={nboracle.get('res')} -> "
              + ("PROGRAM CORRECT (10000)" if nboracle_ok else "UNEXPECTED"))
    ok = ok and nb_ok and nboracle_ok

    # NCORC: the residual-flag frontier (5º intento root). Verdict reported like DEEP/NCORA
    # (class present on HEAD does NOT fail the gate); partitions must agree, oracle must pass.
    nc_labels, nc_res = all_suites["ncorc"]
    ncexp = nc_labels["n_iters"]
    ncverdicts = []
    for name, r in nc_res:
        res, fail = r.get("res"), r.get("fail")
        nfr = len(r.get("gaps_farret") or [])
        if fail is not None and fail.get("trap") == 0xDCDC:
            v = "class"
            detail = (f"FAIL.BIN trap=0xDCDC counter={fail['counter']} "
                      "(stale-flag re-redirect double-ran the co-routine)")
        elif fail is not None:
            v, detail = "other", f"FAIL.BIN trap=0x{fail['trap']:X} counter={fail['counter']} (unexpected poison)"
        elif res is not None and res.get("counter") == ncexp and nfr == 0:
            v, detail = "fixed", f"counter={res['counter']}, gaps.log clean (redirect frontier consumes the flag)"
        elif res is not None and res.get("counter") == ncexp:
            v, detail = "class", f"counter={res['counter']} BUT {nfr} farret-unwind discards (residual noted)"
        else:
            v, detail = "other", f"RES={res} farret-unwind={nfr} (unexpected)"
        ncverdicts.append(v)
        print(f"  NCORC {r['id']:2s} {name:12s} {v.upper():5s} {detail}")
    nc_agree = len(set(ncverdicts)) == 1
    nc_verdict = ncverdicts[0] if nc_agree else "SPLIT"
    print(f"  NCORC: verdict={nc_verdict} across {len(ncverdicts)} partitions"
          + ("" if nc_agree else f"  <<< NON-DETERMINISTIC {ncverdicts}"))
    ncoracle = hw_oracle("ncorc_oracle.conf", ncexp)
    if "skipped" in ncoracle:
        print(f"  NCORC oracle: SKIPPED ({ncoracle['skipped']}) — cannot prove program correctness")
        ncoracle_ok = False
    else:
        ncoracle_ok = ncoracle.get("ok", False)
        print(f"  NCORC oracle (DOSBox-X hardware): RES.BIN={ncoracle.get('res')} -> "
              + (f"PROGRAM CORRECT ({ncexp})" if ncoracle_ok else "UNEXPECTED"))
    ok = ok and ncoracle_ok and nc_agree

    # DE: #DE divide-fault parity lifted-vs-interp. GREEN mandatory on both partitions
    # (partition b IS the reference semantics; no external oracle — see gen_de.py).
    de_labels, de_res = all_suites["de"]
    deexp = de_labels["n_iters"]
    de_green = 0
    for name, r in de_res:
        res, fail = r.get("res"), r.get("fail")
        if res is not None and res.get("counter") == deexp:
            de_green += 1
            print(f"  DE {r['id']:2s} {name:12s} PASS  counter={res['counter']} (INT 0 trampoline)")
        elif fail is not None:
            print(f"  DE {r['id']:2s} {name:12s} FAIL  FAIL.BIN trap=0x{fail['trap']:X} (unexpected poison)")
        else:
            print(f"  DE {r['id']:2s} {name:12s} FAIL  no RES.BIN — DivideByZeroException killed the "
                  "lifted thread (LiftMulDiv missing the INT 0 trampoline)")
    de_ok = de_green == len(de_res)
    print(f"  DE: {de_green}/{len(de_res)} green (lifted/interp #DE parity)"
          + ("" if de_ok else "  <<< CLASS PRESENT"))
    ok = ok and de_ok

    # ISTK: the AdLib nested-ISR private-stack class. GREEN = RES.BIN present (any counter:
    # the loop count until IRQ0 fires is timing-dependent) AND no FAIL.BIN. RED (pre-fix) =
    # FAIL.BIN trap 0xADAD (the main loop caught SP relocated by the bailed ISR). The counter is
    # NOT fixed, so the judge checks PRESENCE, not a value.
    istk_labels, istk_res = all_suites["istk"]
    istk_ok = True
    for name, r in istk_res:
        res, fail = r.get("res"), r.get("fail")
        if fail is not None and fail.get("trap") == 0xADAD:
            istk_ok = False
            print(f"  ISTK {r['id']:2s} {name:12s} FAIL  FAIL.BIN trap=0xADAD counter={fail['counter']} "
                  "(nested ISR bailed on the private stack -> SP relocated -> the AdLib hang class)")
        elif fail is not None:
            istk_ok = False
            print(f"  ISTK {r['id']:2s} {name:12s} FAIL  FAIL.BIN trap=0x{fail['trap']:X} (unexpected poison)")
        elif res is not None:
            print(f"  ISTK {r['id']:2s} {name:12s} PASS  counter={res['counter']} "
                  "(ISR ran to its terminal IRET; SP restored — IsrMode fix)")
        else:
            istk_ok = False
            print(f"  ISTK {r['id']:2s} {name:12s} FAIL  no RES.BIN / no FAIL.BIN (ISR never fired? — inconclusive)")
    ioracle = hw_oracle("istk_oracle.conf", None)
    if "skipped" in ioracle:
        print(f"  ISTK oracle: SKIPPED ({ioracle['skipped']}) — cannot prove program correctness")
        ioracle_ok = False
    else:
        ioracle_ok = ioracle.get("res") is not None and ioracle.get("fail") is None
        print(f"  ISTK oracle (DOSBox-X hardware): RES.BIN={ioracle.get('res')} FAIL.BIN={ioracle.get('fail')} -> "
              + ("PROGRAM CORRECT (RES written, no FAIL)" if ioracle_ok else "UNEXPECTED"))
    ok = ok and istk_ok and ioracle_ok

    # IIDM: the sound-driver crash class (IRET idiom inside a hardware ISR). Judge mirrors
    # ISTK: GREEN = RES.BIN present, no FAIL.BIN; RED = FAIL.BIN trap 0xB1B1 (SP leaked ISR
    # words after the nested run bailed at the idiom IRET). Counter is timing-dependent.
    iidm_labels, iidm_res = all_suites["iidm"]
    iidm_ok = True
    for name, r in iidm_res:
        res, fail = r.get("res"), r.get("fail")
        if fail is not None and fail.get("trap") == 0xB1B1:
            iidm_ok = False
            print(f"  IIDM {r['id']:2s} {name:12s} FAIL  FAIL.BIN trap=0xB1B1 counter={fail['counter']} "
                  "(nested run bailed at the idiom IRET -> ISR words leaked onto the stack — the sound-driver class)")
        elif fail is not None:
            iidm_ok = False
            print(f"  IIDM {r['id']:2s} {name:12s} FAIL  FAIL.BIN trap=0x{fail['trap']:X} (unexpected poison)")
        elif res is not None:
            print(f"  IIDM {r['id']:2s} {name:12s} PASS  counter={res['counter']} "
                  "(idiom IRET ran in-interp; ISR reached its terminal IRET at entry stack level)")
        else:
            iidm_ok = False
            print(f"  IIDM {r['id']:2s} {name:12s} FAIL  no RES.BIN / no FAIL.BIN (ISR never fired? — inconclusive)")
    idoracle = hw_oracle("iidm_oracle.conf", None)
    if "skipped" in idoracle:
        print(f"  IIDM oracle: SKIPPED ({idoracle['skipped']}) — cannot prove program correctness")
        idoracle_ok = False
    else:
        idoracle_ok = idoracle.get("res") is not None and idoracle.get("fail") is None
        print(f"  IIDM oracle (DOSBox-X hardware): RES.BIN={idoracle.get('res')} FAIL.BIN={idoracle.get('fail')} -> "
              + ("PROGRAM CORRECT (RES written, no FAIL)" if idoracle_ok else "UNEXPECTED"))
    ok = ok and iidm_ok and idoracle_ok

    print("\n---------------------------------------------")
    print(f"CORO  (regression guard): {'GREEN 3/3' if coro_ok else 'RED — fix regressed'}")
    if deep_verdict == "class":
        print("DEEP  (unwind net): RED 3/3 as expected on HEAD — the unwind-to-the-bottom class is "
              "captured (clean thread exit, no work). Oracle proves the program itself is correct. "
              "This gate must go GREEN when the FarRet bottom-resume fix lands.")
    elif deep_verdict == "fixed":
        print("DEEP  (unwind net): GREEN 3/3 — the FarRet bottom-resume fix is in and works.")
    else:
        print(f"DEEP  (unwind net): {deep_verdict} — investigate (see per-partition detail above).")
    if pstk_verdict == "class":
        print("PSTK  (private-stack net): RED — the genuine by-value far return from a private driver "
              "stack is DISCARDED (bottom-resume rescues the program but gaps.log records the "
              "unwind). Must go GREEN (clean gaps.log) when the by-value guard fix lands.")
    elif pstk_verdict == "fixed":
        print("PSTK  (private-stack net): GREEN — by-value genuine returns resume inline, no unwind.")
    else:
        print(f"PSTK  (private-stack net): {pstk_verdict} — investigate (see per-partition detail above).")
    if na_verdict == "class":
        print("NCORA (cities net): RED as expected on HEAD — the near-consumed co-routine discard is "
              "captured (FAIL.BIN 0xDDDD at trap_pair; the transported destination lands in gaps.log). "
              "Oracle proves the program itself is correct. Must go GREEN when the near-consumed fix lands.")
    elif na_verdict == "fixed":
        print("NCORA (cities net): GREEN — the near-consumed destination is honoured (fix landed).")
    else:
        print(f"NCORA (cities net): {na_verdict} — investigate (see per-partition detail above).")
    print("NCORB (veto guard): " + ("GREEN — legitimate inline-params near mismatch keeps resuming "
          "via the lifted goto (no redirect, no double execution)." if nb_ok
          else "RED — the LEGITIMATE veto case broke (see per-partition detail above)."))
    if nc_verdict == "class":
        print("NCORC (residual net): RED as expected on HEAD — the PROPAGATE-redirect leaves the "
              "FarRet flag set after attending the co-routine (Run == false) and the stale flag "
              "re-redirects (FAIL.BIN 0xDCDC). Must go GREEN when the frontier consumes the flag.")
    elif nc_verdict == "fixed":
        print("NCORC (residual net): GREEN — the redirect frontier consumes the flag; no residual.")
    else:
        print(f"NCORC (residual net): {nc_verdict} — investigate (see per-partition detail above).")
    print("DE    (#DE parity): " + ("GREEN — lifted div faults vector through INT 0 like the interp."
          if de_ok else "RED — lifted div kills the host on a zero divisor (long-division helper class)."))
    print("IIDM  (IRET-idiom net): " + ("GREEN — the idiom IRET inside the ISR executes in-interp; only "
          "the entry-level IRET ends the nested run." if iidm_ok
          else "RED — the nested run bails at the first IRET (sound-driver crash class)."))
    print("ISTK  (AdLib ISR net): " + ("GREEN — the nested timer ISR switches to its private stack and "
          "runs to its terminal IRET (IsrMode); SP restored." if istk_ok
          else "RED — the nested ISR bails on the private-stack ret and relocates SP (the AdLib "
          "black-screen hang class). Needs the IsrMode fix."))
    print(f"\nGATE: {'OK (state as expected)' if ok else 'ATTENTION (see summary)'}")
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
