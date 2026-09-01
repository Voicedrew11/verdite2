"""What the port believes about the game's frame, read from the tree rather than
restated here.

Four facts get used by more than one script, and all four have gone stale in
prose at least once -- the `KF2_FPS_GATE` default was written down as three
addresses in CLAUDE.md, five in docs/, and six in the code. So each is derived
from the file that owns it:

  * **the thirteen main-loop stages** -- from `generated/game.cs`, the flat call
    list in the tail of `func_8001369C`
  * **which of them are held to the tick rate** -- from `DefaultGate` in
    `patches/FramePacing.cs`
  * **what counts as drawing** -- from the SDK bindings in `config/kf2.json`
  * **which functions are modal loops** -- computed: a backward branch whose
    subtree draws

That last one is the concept the stage gate cannot express, and it is why an
address can be inside a *gated* stage and still run at the render rate. Stage 3
is gated, but the in-game menu is a loop *inside* stage 3 that renders its own
frames, so `FramePacing` decides only whether the loop is entered and everything
inside it steps per rendered frame. Any classifier that stops at "which stage
contains this" gets the menu wrong.
"""

from __future__ import annotations

import re
from functools import lru_cache
from pathlib import Path

import callgraph

REPO = Path(__file__).resolve().parent.parent

MAIN_LOOP = "func_8001369C"
LOOP_LABEL = "L80013918"


def _strip_jsonc(text: str) -> str:
    text = re.sub(r"/\*.*?\*/", "", text, flags=re.S)
    return re.sub(r"^\s*//.*$", "", text, flags=re.M)


@lru_cache(maxsize=1)
def draw_addresses() -> dict[str, set[int]]:
    """Per overlay, the SDK entry points that mean "this presents"."""
    import json
    cfg = json.loads(_strip_jsonc((REPO / "config" / "kf2.json").read_text()))
    wanted = ("LibGpu.DrawOTag", "LibEtc.VSync", "LibGpu.PutDrawEnv", "LibGpu.PutDispEnv")
    out: dict[str, set[int]] = {}
    for p in cfg.get("patches", []):
        if any(p.get("target", "").endswith(w) for w in wanted):
            out.setdefault(p.get("overlay", "*"), set()).add(int(p["address"], 16))
    return out


@lru_cache(maxsize=1)
def gated() -> list[int]:
    """FramePacing's DefaultGate, read from the source so it cannot go stale."""
    src = (REPO / "patches" / "FramePacing.cs").read_text()
    block = re.search(r"static readonly uint\[\] DefaultGate\s*=\s*\[(.*?)\];", src, re.S)
    if not block:
        raise RuntimeError("cannot find DefaultGate in patches/FramePacing.cs")
    # Every entry carries a comment naming the globals that stage steps, and those
    # are addresses too -- so read the entries and not the prose.
    body = re.sub(r"//.*$", "", block.group(1), flags=re.M)
    return [int(a, 16) for a in re.findall(r"0x([0-9A-Fa-f]{8})u?\s*,", body)]


@lru_cache(maxsize=1)
def stages() -> list[str]:
    """The thirteen per-frame calls, in order, from the main loop's own body.

    Not a switch -- a flat list with a backward branch -- so the extraction is
    "every call after the loop label, in source order".
    """
    text = (REPO / "generated" / "game.cs").read_text(errors="replace").splitlines()
    g = graph()
    f = g.funcs[MAIN_LOOP]
    body = text[f.start - 1:f.end]
    try:
        start = next(i for i, l in enumerate(body) if l.strip().startswith(LOOP_LABEL))
    except StopIteration:
        raise RuntimeError(f"cannot find {LOOP_LABEL} in {MAIN_LOOP}")

    seen, order = set(), []
    for line in body[start:]:
        # The loop's own back-branch ends the frame; what follows it is the exit
        # path (func_80013A08 and the Exec arms), not a stage.
        if f"goto {LOOP_LABEL}" in line:
            break
        for m in callgraph.RX_CALL.finditer(line):
            if m.group(1) not in seen:
                seen.add(m.group(1))
                order.append(m.group(1))
    return order


@lru_cache(maxsize=1)
def graph() -> callgraph.Graph:
    return callgraph.Graph(["game"])


@lru_cache(maxsize=1)
def modal_loops() -> dict[str, set[str]]:
    """Loop function -> everything under it.

    A modal loop is a function with a backward branch whose subtree reaches one of
    the drawing entry points: it takes the main loop over and renders its own
    frames, so a whole-function gate on whatever stage contains it cannot cut one
    in half. The menu (`func_80018E80`), the fades, the cast and use animations
    are all this shape.

    The main loop itself matches the definition and is excluded -- it is the frame,
    not a loop inside one.
    """
    g = graph()
    draws = {f"func_{a:08X}" for a in draw_addresses().get("game", set())}
    # The main loop and the thirteen stages match the definition and are not what
    # the word means here: they *are* the frame. Several stages do have a backward
    # branch and do draw -- stage 3 among them -- and leaving them in makes every
    # function in the game look modal.
    skip = {MAIN_LOOP} | set(stages())
    out = {}
    for name, f in g.funcs.items():
        if name in skip or not f.has_backedge:
            continue
        sub = g.subtree(name)
        if sub & draws:
            out[name] = sub
    return out


@lru_cache(maxsize=1)
def _modal_members() -> dict[str, str]:
    """Function -> the outermost modal loop it sits under, if any."""
    loops = modal_loops()
    # Prefer the largest containing loop, so a nested helper is attributed to the
    # session rather than to the innermost animation.
    out: dict[str, str] = {}
    for loop, members in sorted(loops.items(), key=lambda kv: -len(kv[1])):
        for m in members:
            out.setdefault(m, loop)
    return out


@lru_cache(maxsize=1)
def _stage_subtrees() -> dict[str, set[str]]:
    g = graph()
    return {s: g.subtree(s) for s in stages()}


def classify(func_name: str) -> dict:
    """Is this function's per-call state held to the tick rate, and if not, why not."""
    gated_names = {f"func_{a:08X}" for a in gated()}
    subtrees = _stage_subtrees()

    containing = [s for s, sub in subtrees.items() if func_name in sub]
    in_gated = [s for s in containing if s in gated_names]
    in_ungated = [s for s in containing if s not in gated_names]

    # A gated ancestor is only protection if nothing between it and here is a
    # modal loop, which runs many times per gated call.
    modal = _modal_members().get(func_name)
    if func_name in gated_names:
        modal = None

    # Order matters, and reachability is not the same as "only runs here". A
    # function can sit under an ungated stage *and* be reachable from a menu; the
    # stage is the primary truth and the modal loop is worth saying as well.
    if func_name in gated_names:
        verdict = "tick rate: gated directly"
    elif in_ungated:
        verdict = f"render rate: under ungated {', '.join(in_ungated)}"
        if modal:
            verdict += f" (also reachable inside modal loop {modal})"
    elif modal:
        verdict = f"render rate: inside modal loop {modal}, the stage gate cannot reach it"
    elif in_gated:
        verdict = "tick rate: only under gated stages"
    elif containing:
        verdict = "unclear"
    else:
        verdict = "not on the per-frame path"

    return {
        "function": func_name,
        "stages": containing,
        "gated": in_gated,
        "ungated": in_ungated,
        "modal_loop": modal,
        "verdict": verdict,
    }
