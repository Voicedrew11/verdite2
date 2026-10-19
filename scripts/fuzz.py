#!/usr/bin/env python3
"""Run many headless, seeded random playthroughs in parallel and report what broke.

    python3 scripts/fuzz.py --runs 64 --jobs 6 --frames 20000
    python3 scripts/fuzz.py --runs 200 --guards off          # the guards' faults come back
    python3 scripts/fuzz.py --replay fuzz/runs/000042        # play one back, headless
    python3 scripts/fuzz.py --replay fuzz/runs/000042 --windowed   # ... or in a window

Each run is the built port (``bin/Release/net10.0/KingsField2``, so build first)
with ``KF2_HEADLESS=1 KF2_TURBO=1 KF2_FUZZ=<seed>``, in a directory of its own
under ``fuzz/runs/`` holding copies of the repo's saves and settings -- every file
the runtime writes resolves against the working directory, so two runs sharing one
would trample each other's cards. ``patches/Fuzz.cs`` does the playing and prints
everything it finds as ``[KF2-FUZZ]`` lines; this script launches, watches,
classifies and deduplicates:

    crash       the process exited 1, or wrote a crash report
    hang        a hang report (the runtime's watchdog, KF2_HANG seconds without a
                frame), or the wall-clock timeout; the process is killed either way
    fault       a hook threw (HookManager turned it off and the game went on)
    violation   an invariant of Fuzz.cs broke (HP over max, off the map, a stall...)
    guard       HitGuard or WallGuard stepped in -- the fault they fence happened
    ok          ran its frames out and exited 0

Findings group by signature (the top frames of a report's stack, or an invariant's
kind and area) into ``fuzz/report.md`` and ``fuzz/report.json``, each with the
seeds that hit it and the recording (``fuzz-input.log``) that replays it.
Replay is close, not exact: the game's rand and the pad follow the seed, the CD's
timing and the host's threads do not. See "Fuzzing headless playthroughs" in
docs/DEVELOPMENT.md.
"""

from __future__ import annotations

import argparse
import json
import os
import re
import shutil
import signal
import subprocess
import sys
import time
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
EXE = REPO / "bin/Release/net10.0/KingsField2"
CUE = REPO / "disc/KingsField2.cue"
SEEDED_FILES = ("carda.sav", "cardb.sav", "settings.json", "interface.ini")

FRAME_RE = re.compile(r"(func_[0-9A-F]{8}|Kf2\.[\w.]+|RecompOne\.Runtime\.[\w.]+)")
GUARD_RE = re.compile(r"\[KF2\] (hit|wall) guard: (?!on\b|off\b)(.*)")
VIOLATION_RE = re.compile(r"\[KF2-FUZZ\] violation (\S+) frame=(\d+) seed=\S+ (.*)")
AREA_RE = re.compile(r"area[= ](\d+)")


@dataclass
class Run:
    seed: int
    dir: Path
    proc: subprocess.Popen | None = None
    started: float = 0.0
    hang_seen: float = 0.0
    killed: str | None = None
    exit: int | None = None
    outcome: str = "ok"
    findings: list[dict] = field(default_factory=list)
    frames: int = 0
    seconds: float = 0.0


def rel(p: Path) -> str:
    """A path as the report shows it: from the repo when it is inside it."""
    p = Path(p)
    return str(p.relative_to(REPO)) if p.is_relative_to(REPO) else str(p)


def stale_build() -> str | None:
    if not EXE.exists():
        return f"{EXE.relative_to(REPO)} is missing; dotnet build KingsField2Recomp.csproj -c Release"
    built = EXE.with_suffix(".dll").stat().st_mtime if EXE.with_suffix(".dll").exists() else EXE.stat().st_mtime
    sources = [REPO / "Program.cs", *REPO.glob("patches/**/*.cs")]
    newer = [p for p in sources if p.stat().st_mtime > built + 1]
    if newer:
        return f"{len(newer)} source file(s) newer than the build (e.g. {newer[0].relative_to(REPO)})"
    return None


def prepare(run_dir: Path) -> None:
    if run_dir.exists():
        shutil.rmtree(run_dir)
    run_dir.mkdir(parents=True)
    for name in SEEDED_FILES:
        src = REPO / name
        if src.exists():
            shutil.copy2(src, run_dir / name)


def env_for(args, seed: int, replay: Path | None = None) -> dict:
    env = dict(os.environ)
    for k in [k for k in env if k.startswith("KF2_")]:
        del env[k]
    env.update({
        "KF2_FUZZ_FRAMES": str(args.frames),
        "KF2_AUTOSTART": args.start,
        "KF2_HANG": str(args.hang),
    })
    if not getattr(args, "windowed", False):
        env.update({"KF2_HEADLESS": "1", "KF2_TURBO": "1"})
    if replay:
        env["KF2_FUZZ_REPLAY"] = str(replay)
    else:
        env["KF2_FUZZ"] = str(seed)
    if args.godmode:
        env["KF2_FUZZ_GODMODE"] = "1"
    if args.no_macros:
        env["KF2_FUZZ_MACROS"] = "0"
    if args.stop:
        env["KF2_FUZZ_STOP"] = "1"
    if args.guards == "off":
        env["KF2_HITGUARD"] = "0"
        env["KF2_WALLGUARD"] = "0"
    for kv in args.env:
        k, _, v = kv.partition("=")
        env[k] = v
    return env


def launch(run: Run, env: dict) -> None:
    log = open(run.dir / "log.txt", "w")
    run.proc = subprocess.Popen([str(EXE), str(CUE)], cwd=run.dir, env=env, stdout=log,
                                stderr=subprocess.STDOUT, start_new_session=True)
    run.started = time.time()


def kill(run: Run, why: str) -> None:
    run.killed = why
    try:
        os.killpg(run.proc.pid, signal.SIGKILL)
    except ProcessLookupError:
        pass


def reports(run: Run, kind: str) -> list[Path]:
    d = run.dir / "crashes"
    return sorted(d.glob(f"*-{kind}.log")) + sorted(d.glob(f"*-{kind}-*.log")) if d.exists() else []


def poll(run: Run, timeout: float) -> bool:
    """True once the run is finished (exited or killed)."""
    if run.proc.poll() is not None:
        run.exit = run.proc.returncode
        return True
    now = time.time()
    if reports(run, "hang"):
        # The watchdog reports and the game sits there; give it a moment to come
        # back on its own ("frames again after Ns"), then end it.
        run.hang_seen = run.hang_seen or now
        if now - run.hang_seen > 3.0:
            kill(run, "hang report")
    if now - run.started > timeout:
        kill(run, "timeout")
    return False


def signature(report: Path) -> tuple[str, str]:
    """(signature, headline) from a crash, hang or fault report."""
    text = report.read_text(errors="replace")
    head = ""
    for line in text.splitlines():
        s = line.strip()
        if re.match(r"^[\w.]+(Exception|Error)\b", s) or "unmapped call" in s:
            head = s[:200]
            break
    frames = []
    for line in text.splitlines():
        if not line.strip().startswith("at "):
            continue
        m = FRAME_RE.search(line)
        if m and m.group(1) not in frames and not m.group(1).startswith(("RecompOne.Runtime.Dispatch",
                                                                         "RecompOne.Runtime.Modding",
                                                                         "RecompOne.Runtime.Memory")):
            frames.append(m.group(1))
        if len(frames) == 4:
            break
    if frames:
        return " > ".join(frames), head
    # A hang whose game thread stopped calling anything has no stack to name it by;
    # where it stopped (overlay, area, dead or alive) is the next best thing.
    m = re.search(r"== Game ==\s*(\{.*\})", text)
    if m:
        try:
            g = json.loads(m.group(1))
            where = f"{g.get('overlay')} area {g.get('area')}{' dead' if g.get('dead') else ''}"
            return f"no stack, {where}", head or f"no stack, {where}"
        except ValueError:
            pass
    return head or "no stack", head


def classify(run: Run) -> None:
    log = (run.dir / "log.txt").read_text(errors="replace") if (run.dir / "log.txt").exists() else ""
    run.seconds = time.time() - run.started
    m = re.findall(r"\[KF2-FUZZ\] (?:status|done) frame=(\d+)", log)
    run.frames = int(m[-1]) if m else 0
    done = "[KF2-FUZZ] done " in log

    for kind in ("crash", "hang", "fault"):
        for rep in reports(run, kind):
            sig, head = signature(rep)
            run.findings.append({"kind": kind, "sig": f"{kind}: {sig}", "detail": head,
                                 "report": rel(rep)})
    if run.exit == 1 and not reports(run, "crash"):
        tail = [l for l in log.splitlines() if "crash" in l.lower() or "Exception" in l][-1:]
        run.findings.append({"kind": "crash", "sig": "crash: exit 1, no report",
                             "detail": tail[0][:200] if tail else ""})
    if run.exit is not None and run.exit < 0 and run.killed is None:
        run.findings.append({"kind": "crash", "sig": f"crash: signal {-run.exit}", "detail": ""})
    if run.killed == "timeout" and not done and not reports(run, "hang"):
        run.findings.append({"kind": "hang", "sig": "hang: wall-clock timeout, no report",
                             "detail": f"last frame {run.frames}"})

    for kind, frame, detail in VIOLATION_RE.findall(log):
        area = AREA_RE.search(detail)
        run.findings.append({"kind": "violation", "sig": f"violation: {kind}" + (f" area {area.group(1)}" if area else ""),
                             "detail": f"frame {frame}: {detail}"})
    for which, detail in GUARD_RE.findall(log):
        area = AREA_RE.search(detail)
        run.findings.append({"kind": "guard", "sig": f"guard: {which}" + (f" area {area.group(1)}" if area else ""),
                             "detail": detail[:200]})

    order = ["crash", "hang", "fault", "violation", "guard"]
    kinds = {f["kind"] for f in run.findings}
    run.outcome = next((k for k in order if k in kinds), "ok")


def write_report(out: Path, runs: list[Run], args) -> None:
    groups: dict[str, list[tuple[Run, dict]]] = defaultdict(list)
    for r in runs:
        seen = set()
        for f in r.findings:
            if f["sig"] in seen:
                continue
            seen.add(f["sig"])
            groups[f["sig"]].append((r, f))
    counts = defaultdict(int)
    for r in runs:
        counts[r.outcome] += 1
    frames = sum(r.frames for r in runs)
    secs = sum(r.seconds for r in runs) or 1.0

    data = {
        "args": vars(args) | {"replay": None},
        "counts": counts,
        "frames": frames,
        "frames_per_second_per_run": round(frames / secs, 1),
        "runs": [{"seed": r.seed, "dir": rel(r.dir), "outcome": r.outcome, "exit": r.exit,
                  "killed": r.killed, "frames": r.frames, "seconds": round(r.seconds, 1),
                  "findings": r.findings} for r in runs],
        "signatures": [{"sig": sig, "kind": hits[0][1]["kind"], "runs": len(hits),
                        "seeds": [r.seed for r, _ in hits],
                        "example": {"dir": rel(hits[0][0].dir),
                                    "recording": rel((hits[0][0].dir / "fuzz-input.log")),
                                    **hits[0][1]}}
                       for sig, hits in sorted(groups.items(), key=lambda kv: -len(kv[1]))],
    }
    (out / "report.json").write_text(json.dumps(data, indent=2))

    lines = ["# Fuzz report", "",
             f"{len(runs)} run(s), {frames} frames of play "
             f"({frames / 15 / 3600:.1f} h of game time at 15 ticks/s), "
             f"{frames / secs:.0f} frames/s per run.", "",
             "| outcome | runs |", "|---|---|"]
    lines += [f"| {k} | {counts[k]} |" for k in ("crash", "hang", "fault", "violation", "guard", "ok") if counts[k]]
    lines += ["", "## Signatures", ""]
    if not groups:
        lines.append("Nothing found.")
    a = data["args"]
    flags = f" --frames {a['frames']}" + (f" --start {a['start']}" if a["start"] != "2" else "") \
        + (" --godmode" if a["godmode"] else "") + (" --no-macros" if a["no_macros"] else "") \
        + (" --guards off" if a["guards"] == "off" else "") + "".join(f" --env {kv}" for kv in a["env"])
    for s in data["signatures"]:
        ex = s["example"]
        lines += [f"### {s['sig']}", "",
                  f"- {s['runs']} run(s), seeds {', '.join(map(str, s['seeds'][:12]))}"
                  + (" ..." if len(s["seeds"]) > 12 else ""),
                  f"- example: `{ex['dir']}`" + (f", report `{ex['report']}`" if ex.get("report") else ""),
                  f"- rerun the seed: `python3 scripts/fuzz.py --runs 1 --seed-base {s['seeds'][0]}{flags}`",
                  f"- or replay its input: `python3 scripts/fuzz.py --replay {ex['dir']}`"]
        if ex.get("detail"):
            lines.append(f"- {ex['detail']}")
        lines.append("")
    (out / "report.md").write_text("\n".join(lines) + "\n")


def campaign(args) -> int:
    out = Path(args.out).resolve()
    (out / "runs").mkdir(parents=True, exist_ok=True)
    seeds = list(range(args.seed_base, args.seed_base + args.runs))
    pending = [Run(seed=s, dir=out / "runs" / f"{s:06d}") for s in seeds]
    active: list[Run] = []
    finished: list[Run] = []
    t0 = time.time()
    try:
        while pending or active:
            while pending and len(active) < args.jobs:
                r = pending.pop(0)
                prepare(r.dir)
                launch(r, env_for(args, r.seed))
                active.append(r)
            time.sleep(0.5)
            for r in list(active):
                if poll(r, args.timeout):
                    r.proc.wait()
                    r.exit = r.proc.returncode if r.exit is None else r.exit
                    classify(r)
                    active.remove(r)
                    finished.append(r)
                    tag = "" if r.outcome == "ok" else "  <-- " + "; ".join(sorted({f["sig"] for f in r.findings}))[:160]
                    print(f"[{len(finished):4d}/{len(seeds)}] seed {r.seed:6d} {r.outcome:9s} "
                          f"{r.frames:6d} frames {r.seconds:6.1f}s{tag}", flush=True)
                    if r.outcome == "ok" and not args.keep_ok:
                        for p in r.dir.iterdir():
                            if p.name not in ("log.txt", "fuzz-input.log"):
                                shutil.rmtree(p) if p.is_dir() else p.unlink()
    except KeyboardInterrupt:
        print("interrupted; killing active runs", file=sys.stderr)
        for r in active:
            kill(r, "interrupted")
            r.proc.wait()
            r.exit = r.proc.returncode
            classify(r)
            finished.append(r)
    finished.sort(key=lambda r: r.seed)
    write_report(out, finished, args)
    bad = sum(r.outcome != "ok" for r in finished)
    print(f"\n{len(finished)} run(s) in {time.time() - t0:.0f}s, {bad} with findings; "
          f"{rel(out / 'report.md')}")
    return 1 if bad else 0


def replay(args) -> int:
    src = Path(args.replay).resolve()
    rec = src / "fuzz-input.log" if src.is_dir() else src
    if not rec.exists():
        sys.exit(f"no recording at {rec}")
    first = rec.read_text().splitlines()[0]
    m = re.search(r"seed (\d+) frames (\d+)", first)
    seed = int(m.group(1)) if m else 0
    if m and args.frames == argparse_default_frames:
        args.frames = int(m.group(2))
    args.godmode = args.godmode or " godmode 1" in first
    run = Run(seed=seed, dir=(src if src.is_dir() else src.parent) / "replay")
    prepare(run.dir)
    env = env_for(args, seed, replay=rec)
    # The game's rand is seeded as in the recorded run.
    env["KF2_FUZZ"] = str(seed)
    launch(run, env)
    print(f"replaying {rel(rec)} in {run.dir}")
    while not poll(run, args.timeout):
        time.sleep(0.5)
    run.proc.wait()
    run.exit = run.proc.returncode if run.exit is None else run.exit
    classify(run)
    print(f"{run.outcome}, {run.frames} frames, exit {run.exit}")
    for f in run.findings:
        print(f"  {f['sig']}  {f.get('detail', '')}")
    return 0 if run.outcome == "ok" else 1


argparse_default_frames = 20000


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__.split("\n\n")[0])
    ap.add_argument("--runs", type=int, default=32)
    ap.add_argument("--jobs", type=int, default=max(1, min((os.cpu_count() or 2) // 2, 6)),
                    help="runs at once (each is ~1 core and ~1 GB)")
    ap.add_argument("--frames", type=int, default=argparse_default_frames,
                    help="frames of play per run (a tick each under turbo; 15 = one second of game time)")
    ap.add_argument("--seed-base", type=int, default=1)
    ap.add_argument("--start", default="2", help="KF2_AUTOSTART: a save slot 1..3, or new")
    ap.add_argument("--godmode", action="store_true", help="HP topped up, so runs explore instead of reloading")
    ap.add_argument("--no-macros", action="store_true", help="the pad only: no warps, loads or teleports")
    ap.add_argument("--guards", choices=("on", "off"), default="on",
                    help="off lets HitGuard's and WallGuard's faults happen, as an oracle")
    ap.add_argument("--stop", action="store_true", help="end a run at its first violation")
    ap.add_argument("--hang", type=int, default=20, help="KF2_HANG: seconds without a frame before a hang report")
    ap.add_argument("--timeout", type=float, default=900, help="wall seconds before a run is killed")
    ap.add_argument("--env", action="append", default=[], metavar="KEY=VALUE", help="extra environment, repeatable")
    ap.add_argument("--out", default=str(REPO / "fuzz"))
    ap.add_argument("--keep-ok", action="store_true", help="keep the whole directory of a clean run")
    ap.add_argument("--replay", metavar="RUN_DIR", help="play a run's fuzz-input.log back")
    ap.add_argument("--windowed", action="store_true", help="with --replay: in a window, at the normal rate")
    ap.add_argument("--force", action="store_true", help="run even if the build looks stale")
    args = ap.parse_args()

    if not CUE.exists():
        sys.exit(f"no disc at {CUE.relative_to(REPO)}")
    stale = stale_build()
    if stale and not args.force:
        sys.exit(f"{stale} (or pass --force)")
    return replay(args) if args.replay else campaign(args)


if __name__ == "__main__":
    sys.exit(main())
