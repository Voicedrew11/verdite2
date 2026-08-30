#!/usr/bin/env python3
"""Run a scenario across a matrix of rate settings and print the table.

    python3 scripts/rate_matrix.py menu-scroll --fps 20 60 144
    python3 scripts/rate_matrix.py death-clock --fps 20 60 144 --tickrate 20 30
    python3 scripts/rate_matrix.py menu-scroll --fps 144 --env KF2_MENUPACING=0
    python3 scripts/rate_matrix.py --list

Every rate claim in docs/ should be reproducible by one of these. The output is a
markdown table ready to paste, and `--json` keeps the raw numbers so a later run
can be diffed against an earlier one.

**Read the rate a scenario reports, not the duration it reports.** Some of these
numbers are stable across runs and some are not -- the menu repeat's spin measured
1.2 ms on one run and 16.6 ms on the next at the same settings, while the repeat
*rate* it produced was 37.5 and 36.0. A scenario says which of its numbers it
considers load-bearing; the others are context.
"""

from __future__ import annotations

import argparse
import itertools
import json
import re
import statistics
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))
import kf2run as kf2

SCRATCH = Path("/tmp/kf2-rate-matrix")


# --- scenarios ---------------------------------------------------------------
#
# A scenario drives the game and returns {column: value}. It may assume an area
# is up and settled. Keep them short: the matrix multiplies their cost.

def sc_menu_scroll(run: kf2.Run) -> dict:
    """Open the menu, hold Down, and measure the cursor repeat and the blink.

    Needs KF2_MENUPACING_PROBE=1, which the scenario asks for below.
    """
    kf2.press("Circle", 150)
    time.sleep(2.5)
    kf2.hold("Down", 2.0)
    time.sleep(1.5)

    spins = [float(m.group(1)) for m in run.matching(r"menu pacing: ([\d.]+) ms over")]
    blinks = [(float(m.group(1)), float(m.group(2)))
              for m in run.matching(r"blink stepped ([\d.]+) times a second over ([\d.]+) menu frames")]

    # The blink freezes when the cursor is not moving, so the windows that matter
    # are the ones where it stepped at all -- the rest are the menu sitting idle.
    scrolling = [b for b in blinks if b[0] > 1.0]

    return {
        "repeats/2s": len(spins),
        "repeat rate": round(len(spins) / 2.0, 1) if spins else 0.0,
        "spin ms": round(statistics.median(spins), 1) if spins else None,
        "blink/s": round(max((b[0] for b in scrolling), default=0.0), 1),
    }


def sc_death_clock(run: kf2.Run) -> dict:
    """Kill the player and time the 65-tick death counter.

    Stage 3 bumps 0x8019951A once per logic tick, so its slope against wall time
    *is* the tick rate. This is the regression check for KF2_TICKRATE.

    **Needs KF2_AUTORELOAD=0, which the scenario asks for below, and reading it
    without that is what produced the "the death clock stops short above the tick
    rate" open question.** AutoReload pins this very counter at 31
    (`AutoReload.HoldAt`) for the whole of its delay, so the animation finishes
    while the fade and the game's own respawn never come due -- so with it on the
    numerator is a clamp, the denominator is `AutoReload.Delay`, and the ratio of
    the two is not a rate at all. Measured at KF2_FPS=165: 31 frames in 2.06 s
    with auto reload on, 65 in 3.25 s -- 20.02 ticks/s -- with it off.
    """
    # The kill does not always take on the first ask -- `inGame` goes true as the
    # save loads, and wait_in_game's fixed settle is not always enough for the
    # area to be up and the player killable, which at KF2_FPS=20 measured as a
    # flat `0 death frames` until --settle was raised to 25. Confirm the counter
    # actually started rather than reporting a zero that means "never died".
    t0 = None
    for _ in range(3):
        kf2.shell("kill")
        sent = time.time()
        while time.time() - sent < 3.0:
            if kf2.state().get("deathFrames", 0):
                t0 = sent
                break
            time.sleep(0.05)
        if t0 is not None:
            break

    if t0 is None:
        return {"death frames": 0, "seconds": None, "ticks/s": None,
                "note": "kill never took -- not in an area yet, try --settle"}

    last = 0
    deadline = t0 + 20.0
    while time.time() < deadline:
        st = kf2.state()
        frames = st.get("deathFrames", 0)
        if frames and frames >= last:
            last = frames
        if last and (not frames or st.get("dead") is False):
            break
        time.sleep(0.05)
    elapsed = time.time() - t0
    return {
        "death frames": last,
        "seconds": round(elapsed, 2),
        "ticks/s": round(last / elapsed, 2) if elapsed > 0 else None,
    }


def sc_walk(run: kf2.Run) -> dict:
    """Hold Up for two seconds and measure the distance covered.

    The cross-check that does not go through the death clock: the game moves a
    fixed amount per tick, so distance scales with the tick rate and not with the
    frame rate.
    """
    a = kf2.state().get("pos", [0, 0, 0])
    kf2.hold("Up", 2.0)
    b = kf2.state().get("pos", [0, 0, 0])
    dx, dz = b[0] - a[0], b[2] - a[2]
    return {"units/2s": round((dx * dx + dz * dz) ** 0.5)}


def sc_modal_rate(run: kf2.Run) -> dict:
    """Open the menu, then warp, and read what rate each modal loop iterated at.

    A modal loop -- one that takes the main loop over and presents its own frames
    -- is outside the stage gate by construction, so before patches/LoopPacing.cs
    everything it stepped ran at the render rate. The menu is the interface case
    (no world drawn, held to the vblank) and the area transition's fade is the
    world case (held to LogicHz). The load-bearing numbers are the two modal
    columns: they must stop tracking the render rate while `main/s` keeps
    following it.

    Needs KF2_LOOPPACING_PROBE=1, which the scenario asks for below. Pair it with
    --env KF2_LOOPPACING=0 for the before.
    """
    kf2.press("Circle", 150)
    time.sleep(5.0)
    kf2.press("Circle", 150)
    time.sleep(1.0)

    ui = [float(m.group(1)) for m in
          run.matching(r"loop pacing: [\d.]+ main-loop, [\d.]+ modal world, ([\d.]+) modal interface")]

    try:
        kf2.shell("warp 5", timeout=30)
    except Exception:
        pass
    time.sleep(8.0)

    rows = [(float(m.group(1)), float(m.group(2)))
            for m in run.matching(r"loop pacing: ([\d.]+) main-loop, ([\d.]+) modal world,")]
    iters = [float(m.group(1)) for m in
             run.matching(r"([\d.]+) world iteration\(s\) a second")]

    # Only the windows a loop actually ran in say anything; the rest are the main
    # loop on its own, and averaging those in reports the fade as slower than it is.
    #
    # The two columns say different things and both matter. `world iter/s` is the
    # loop *body* -- the thing that was running too fast, and which must equal the
    # tick rate. `modal world/s` is the *picture*, which must equal the render rate:
    # the gap between iterations is filled with redraws so the smoothing
    # patches have something to carry.
    return {
        "main/s": round(max((r[0] for r in rows), default=0.0), 1),
        "modal world/s": round(max((r[1] for r in rows), default=0.0), 1),
        "world iter/s": round(max(iters, default=0.0), 1),
        "modal ui/s": round(max((u for u in ui if u > 1.0), default=0.0), 1),
    }


def sc_idle(run: kf2.Run) -> dict:
    """Stand still for five seconds. For pairing with KF2_RATECENSUS."""
    time.sleep(5.0)
    return {"ok": True}


SCENARIOS = {
    "menu-scroll": (sc_menu_scroll, {"KF2_MENUPACING_PROBE": "1"},
                    "open the menu, hold Down; cursor repeat and blink rate"),
    "death-clock": (sc_death_clock, {"KF2_AUTORELOAD": "0"},
                    "kill, and time the 65-tick death counter"),
    "walk":        (sc_walk, {}, "hold Up for 2 s; distance is tick-rate bound"),
    "modal-rate":  (sc_modal_rate, {"KF2_LOOPPACING_PROBE": "1"},
                    "open the menu, then warp; what rate each self-rendered loop ran at"),
    "idle":        (sc_idle, {}, "stand still for 5 s (pair with KF2_RATECENSUS)"),
}


# --- the matrix --------------------------------------------------------------

def main() -> int:
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("scenario", nargs="?", help="which scenario to run")
    ap.add_argument("--fps", nargs="*", default=["20", "60", "144"])
    ap.add_argument("--tickrate", nargs="*", default=[None])
    ap.add_argument("--env", nargs="*", default=[],
                    help="extra KEY=VALUE applied to every run in the matrix")
    ap.add_argument("--json", type=Path, help="also write the raw numbers here")
    ap.add_argument("--settle", type=float, default=10.0)
    ap.add_argument("--list", action="store_true")
    args = ap.parse_args()

    if args.list or not args.scenario:
        print("scenarios:")
        for name, (_, env, doc) in SCENARIOS.items():
            extra = f"  [{kf2.fmt_env(env)}]" if env else ""
            print(f"  {name:<12} {doc}{extra}")
        return 0

    if args.scenario not in SCENARIOS:
        print(f"no scenario '{args.scenario}'; --list to see them", file=sys.stderr)
        return 2

    fn, scenario_env, doc = SCENARIOS[args.scenario]
    fixed = dict(kv.split("=", 1) for kv in args.env)

    rows = []
    combos = list(itertools.product(args.fps, args.tickrate))
    for i, (fps, tick) in enumerate(combos, 1):
        env = dict(scenario_env)
        env["KF2_FPS"] = fps
        if tick:
            env["KF2_TICKRATE"] = tick
        env.update(fixed)

        label = f"fps={fps}" + (f" tick={tick}" if tick else "")
        print(f"[{i}/{len(combos)}] {label} ...", file=sys.stderr, flush=True)

        log = SCRATCH / f"{args.scenario}-{fps}-{tick or 'default'}.log"
        run = kf2.launch(env, log)
        try:
            kf2.wait_in_game(settle=args.settle)
            result = fn(run)
        except Exception as e:                      # a bad config should cost one row
            print(f"    failed: {e}", file=sys.stderr)
            result = {"error": str(e)[:60]}
        finally:
            kf2.stop()

        rows.append({"fps": fps, "tickrate": tick or "", **result})

    columns = ["fps"] + (["tickrate"] if any(r["tickrate"] for r in rows) else [])
    for r in rows:
        for k in r:
            if k not in columns and k not in ("fps", "tickrate"):
                columns.append(k)

    print()
    print(f"### {args.scenario} — {doc}")
    if fixed:
        print(f"\n`{kf2.fmt_env(fixed)}`")
    print()
    print("| " + " | ".join(columns) + " |")
    print("|" + "|".join("---" for _ in columns) + "|")
    for r in rows:
        print("| " + " | ".join(str(r.get(c, "")) for c in columns) + " |")

    if args.json:
        args.json.write_text(json.dumps(rows, indent=2))
        print(f"\nraw numbers in {args.json}", file=sys.stderr)
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
