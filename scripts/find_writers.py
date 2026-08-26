#!/usr/bin/env python3
"""Given an address, name the functions that write it and say what rate it runs at.

    python3 scripts/find_writers.py 8006E5CC 80199554
    python3 scripts/find_writers.py --stage 2          # everything stage 2 writes
    python3 scripts/find_writers.py --modal            # the loops outside the gate
    python3 scripts/find_writers.py --audit            # every global, classified

This is the other half of `patches/RateCensus.cs`: the census says *which words*
move at the render rate, this says *which code* moves them and whether anything is
supposed to be holding them back.

The verdicts are the four ways a per-frame write can be reached:

  tick rate: gated directly          FramePacing skips the call on a non-tick frame
  tick rate: only under gated stages ... and so does everything below it
  render rate: under ungated <stage> stage 2 and stage 13, which present and so
                                     cannot be skipped
  render rate: inside modal loop     a loop that renders its own frames; the gate
                                     decides only whether it is entered

The last one is the trap, and it is why "which stage contains this" is not enough
on its own: the in-game menu lives inside stage 3, which *is* gated, and every
counter in it still stepped per rendered frame.

Addresses are recovered from `lui`/`addiu` pairs, so a global written through a
pointer or an array index does not appear. An empty answer means "not written
through a literal address", never "not written" -- see scripts/callgraph.py.
"""

from __future__ import annotations

import argparse
import sys
from collections import defaultdict
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import kf2model as model


def parse_addr(text: str) -> int:
    return int(text, 16) if not text.lower().startswith("0x") else int(text, 0)


def report(addr: int, quiet: bool = False) -> dict:
    g = model.graph()
    writers = g.writers(addr)
    if not writers:
        if not quiet:
            print(f"{addr:08X}: no function writes this through a literal address")
        return {"addr": addr, "writers": []}

    rows = [model.classify(w) for w in writers]
    if not quiet:
        print(f"{addr:08X}")
        for w, c in zip(writers, rows):
            width = g.funcs[w].writes.get(addr, 0)
            print(f"    {w}  (u{width * 8})  {c['verdict']}")
    return {"addr": addr, "writers": writers, "verdicts": [r["verdict"] for r in rows]}


def worst(verdicts: list[str]) -> str:
    """A word is only as tick-clocked as its loosest writer."""
    for v in verdicts:
        if v.startswith("render"):
            return v
    return verdicts[0] if verdicts else "unwritten"


def bucket(verdict: str) -> str:
    """Four classes, for grouping. The full verdict stays on the row."""
    if verdict.startswith("tick"):
        return "tick rate — held by the stage gate"
    if "modal loop" in verdict and "ungated" not in verdict:
        return "render rate — inside a modal loop the gate cannot reach"
    if verdict.startswith("render"):
        return "render rate — under a stage that presents and so cannot be gated"
    return verdict


def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("addresses", nargs="*", help="hex addresses to look up")
    ap.add_argument("--stage", help="stage number 1-13, or a func_ name: what it writes")
    ap.add_argument("--modal", action="store_true", help="list the modal loops")
    ap.add_argument("--audit", action="store_true",
                    help="every global on a per-frame path, grouped by verdict")
    args = ap.parse_args()

    g = model.graph()

    if args.modal:
        loops = model.modal_loops()
        print(f"{len(loops)} modal loops -- a backward branch whose subtree draws,")
        print("so the stage gate decides only whether each is entered.\n")
        for name in sorted(loops, key=lambda n: -len(loops[n])):
            f = g.funcs[name]
            globals_here = sorted(f.writes)
            print(f"  {name}  {len(loops[name])} functions below it, "
                  f"{len(globals_here)} globals written in its own body")
        return 0

    if args.stage:
        stages = model.stages()
        name = (stages[int(args.stage) - 1]
                if args.stage.isdigit() and 1 <= int(args.stage) <= len(stages)
                else args.stage)
        if name not in g.funcs:
            print(f"no such function {name}", file=sys.stderr)
            return 2
        writes = g.writes_in_subtree(name)
        print(f"{name} writes {len(writes)} globals through its subtree\n")
        for addr in sorted(writes):
            who = sorted(writes[addr])
            print(f"  {addr:08X}  {', '.join(who[:3])}{' ...' if len(who) > 3 else ''}")
        return 0

    if args.audit:
        buckets: dict[str, list[tuple[int, list[str]]]] = defaultdict(list)
        seen: set[int] = set()
        for stage in model.stages():
            for addr, who in g.writes_in_subtree(stage).items():
                if addr in seen:
                    continue
                seen.add(addr)
                v = worst([model.classify(w)["verdict"] for w in sorted(who)])
                buckets[bucket(v)].append((addr, sorted(who), v))

        print(f"{len(seen)} globals written on the per-frame path.")
        print("A writer is any function that stores to the address -- an initialiser")
        print("and a reset count too, so a render-rate row is a candidate, not a defect.\n")
        for name in sorted(buckets, key=lambda v: (v.startswith("tick"), v)):
            rows = sorted(buckets[name])
            print(f"## {name}  ({len(rows)})")
            for addr, who, v in rows:
                detail = v.split(": ", 1)[1] if ": " in v else v
                print(f"  {addr:08X}  {who[0]}{f' +{len(who) - 1}' if len(who) > 1 else ''}"
                      f"    {detail}")
            print()
        return 0

    if not args.addresses:
        ap.print_help()
        return 2

    for text in args.addresses:
        report(parse_addr(text))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
