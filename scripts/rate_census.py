#!/usr/bin/env python3
"""Find the words that move at the render rate, by running the same scene twice.

    python3 scripts/rate_census.py --run --seconds 30
    python3 scripts/rate_census.py --compare census-20.txt census-144.txt
    python3 scripts/rate_census.py --run --scenario walk --fps 20 144

The other three scripts answer questions you already thought to ask. This one
finds sites nobody has noticed: run an identical scene at two render rates, and
rank every word of the game's memory by how much more often it changed at the
higher one. A word on the tick clock changes at the same rate in both runs; a word
on the render clock does not.

## Reading the ratio

Sampling happens on the emulated vblank, a wall-clock 60 Hz grid, so the sample
rate is the same in both runs -- but it also **ceilings the measurement at 60
changes a second**. A word stepping at 144 Hz reports 60. So the ratio between a
20 fps run and a 144 fps run tops out near **3.0**, not 7.2:

    ~1.0    tick-clocked -- the world's own rate, which is usually correct
    >1.5    render-clocked -- a candidate
    <0.7    slower at the higher rate, which usually means the two runs did not
            see the same scene; check that before reading anything into it

## What a high ratio does not mean

It is a candidate list, not a defect list. `FrameSmoothing` and `ObjectSmoothing`
write interpolated values every rendered frame **on purpose** and will rank near
the top -- turn both off for a census run. A primitive-buffer cursor or a frame
counter is legitimately per-frame too. Deciding is still a person's job; this only
narrows where to look, and `scripts/find_writers.py` (used automatically here)
says which code is responsible.

Two runs must see the same scene. The default scenario stands still in the save's
own starting area, which is repeatable; anything involving movement is not, and
`--scenario walk` is offered for comparison rather than for trust.
"""

from __future__ import annotations

import argparse
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import kf2run as kf2
import kf2model as model
import find_writers

SCRATCH = Path("/tmp/kf2-rate-census")


# Structures whose records the report is worth folding together. A rotation field
# stepping in eighty object slots is one finding, not eighty rows -- and the
# offset within the record is the part that names it. The object table's shape is
# the same one AgentServer reports `nearby` from.
STRUCTS = [
    ("object table", 0x80177714, 0x44, 396),
]


def in_struct(addr: int) -> tuple[str, int, int] | None:
    for name, base, stride, count in STRUCTS:
        if base <= addr < base + stride * count:
            off = addr - base
            return name, off // stride, off % stride
    return None


def read_dump(path: Path) -> tuple[dict[int, int], dict[str, float]]:
    meta: dict[str, float] = {}
    counts: dict[int, int] = {}
    for line in path.read_text().splitlines():
        if line.startswith("#"):
            parts = line[1:].split()
            if len(parts) == 2:
                try:
                    meta[parts[0]] = float(parts[1])
                except ValueError:
                    pass
            continue
        addr, changes = line.split()
        counts[int(addr, 16)] = int(changes)
    return counts, meta


def compare(low: Path, high: Path, min_changes: int, min_rate: float,
            top: int, annotate: bool) -> int:
    a, ma = read_dump(low)
    b, mb = read_dump(high)
    sa, sb = ma.get("seconds", 0.0), mb.get("seconds", 0.0)
    if sa <= 0 or sb <= 0:
        print("a dump has no duration; was the census on?", file=sys.stderr)
        return 2

    print(f"# {low.name}: {sa:.1f}s, {ma.get('sampleHz', 0):.1f} samples/s, {len(a)} words moved")
    print(f"# {high.name}: {sb:.1f}s, {mb.get('sampleHz', 0):.1f} samples/s, {len(b)} words moved")
    if abs(ma.get("sampleHz", 0) - mb.get("sampleHz", 0)) > 6.0:
        print("# WARNING: the two runs did not sample at the same rate, so the "
              "ratios below are not comparable.")
    print()

    rows = []
    for addr in set(a) | set(b):
        ra, rb = a.get(addr, 0) / sa, b.get(addr, 0) / sb
        # Two filters, and both matter. A word that moved a handful of times over
        # half a minute produces a spectacular ratio and means nothing, so require
        # it to be doing something at the high rate as well.
        if max(a.get(addr, 0), b.get(addr, 0)) < min_changes or rb < min_rate:
            continue
        ratio = rb / ra if ra > 0 else float("inf")
        if ratio < 1.5:
            continue
        rows.append((ratio, addr, ra, rb))

    if not rows:
        print("nothing significant above 1.5 -- everything that moved tracks the tick rate.")
        return 0

    # Fold struct records: one finding per field, not one per slot.
    folded: dict[tuple[str, int], list[tuple[float, int, float, float]]] = {}
    loose = []
    for row in rows:
        hit = in_struct(row[1])
        if hit:
            folded.setdefault((hit[0], hit[2]), []).append(row)
        else:
            loose.append(row)

    g = model.graph() if annotate else None

    def verdict_for(addr: int) -> str:
        if not annotate:
            return ""
        writers = g.writers(addr)
        if not writers:
            return "no literal-address writer -- reached through a pointer or an index"
        v = find_writers.worst([model.classify(w)["verdict"] for w in writers])
        return f"{writers[0]}{f' +{len(writers) - 1}' if len(writers) > 1 else ''}  {v}"

    if folded:
        print("## structure fields\n")
        for (name, off), group in sorted(folded.items(), key=lambda kv: -len(kv[1])):
            ratios = [r[0] for r in group if r[0] != float("inf")]
            hi = sum(r[3] for r in group) / len(group)
            lo = sum(r[2] for r in group) / len(group)
            mid = sorted(ratios)[len(ratios) // 2] if ratios else float("inf")
            r = "inf" if mid == float("inf") else f"{mid:.2f}"
            print(f"  {name} +0x{off:02X}   {len(group)} record(s)   "
                  f"ratio {r}   {lo:.1f}/s -> {hi:.1f}/s")
            print(f"      e.g. {group[0][1]:08X}   {verdict_for(group[0][1])}")
        print()

    if loose:
        print(f"## individual words  ({len(loose)})\n")
        print(f"  {'addr':>8}  {'ratio':>6}  {'low/s':>6}  {'high/s':>6}  verdict")
        for i, (ratio, addr, ra, rb) in enumerate(sorted(loose, key=lambda r: -r[3])):
            if i >= top:
                print(f"  ... {len(loose) - top} more")
                break
            r = "inf" if ratio == float("inf") else f"{ratio:.2f}"
            print(f"  {addr:08X}  {r:>6}  {ra:6.1f}  {rb:6.1f}  {verdict_for(addr)}")
    return 0


def run(fps: list[str], seconds: float, scenario: str, extra: dict[str, str]) -> list[Path]:
    SCRATCH.mkdir(parents=True, exist_ok=True)
    dumps = []
    for rate in fps:
        dump = SCRATCH / f"census-{rate}.txt"
        env = {
            "KF2_FPS": rate,
            "KF2_RATECENSUS": "1",
            "KF2_RATECENSUS_OUT": str(dump),
            "KF2_RATECENSUS_PERIOD": "5",
            # The smoothing patches write interpolated values every rendered frame
            # by design and would otherwise be the loudest rows in the report.
            "KF2_SMOOTH": "0",
            "KF2_SMOOTH_OBJECTS": "0",
        }
        env.update(extra)
        print(f"[{rate} fps] {seconds:.0f}s of '{scenario}' ...", file=sys.stderr, flush=True)
        kf2.launch(env, SCRATCH / f"run-{rate}.log")
        try:
            kf2.wait_in_game()
            if scenario == "walk":
                kf2.hold("Up", seconds)
            else:
                time.sleep(seconds)
        finally:
            time.sleep(6.0)          # let one more periodic dump land
            kf2.stop()
        dumps.append(dump)
    return dumps


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--run", action="store_true")
    ap.add_argument("--compare", nargs=2, type=Path, metavar=("LOW", "HIGH"))
    ap.add_argument("--fps", nargs=2, default=["20", "144"], metavar=("LOW", "HIGH"))
    ap.add_argument("--seconds", type=float, default=30.0)
    ap.add_argument("--scenario", default="idle", choices=["idle", "walk"])
    ap.add_argument("--env", nargs="*", default=[])
    ap.add_argument("--min-changes", type=int, default=5)
    ap.add_argument("--min-rate", type=float, default=1.0,
                    help="ignore words changing less than this per second at the high rate")
    ap.add_argument("--top", type=int, default=60)
    ap.add_argument("--raw", action="store_true", help="skip the writer annotation")
    args = ap.parse_args()

    if args.compare:
        return compare(args.compare[0], args.compare[1], args.min_changes,
                       args.min_rate, args.top, not args.raw)
    if args.run:
        extra = dict(kv.split("=", 1) for kv in args.env)
        dumps = run(args.fps, args.seconds, args.scenario, extra)
        print()
        return compare(dumps[0], dumps[1], args.min_changes, args.min_rate,
                       args.top, not args.raw)

    ap.print_help()
    return 2


if __name__ == "__main__":
    raise SystemExit(main())
