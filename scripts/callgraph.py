"""Read the recompiler's emitted C# as a call graph and a table of global writes.

There is no decompilation and no `.map` here, but `generated/*.cs` is a complete,
regular rendering of every instruction the sweep found, and two questions worth
answering are plain text in it:

  * **who calls whom** -- `KingsField2.func_XXXXXXXX(c, m);`
  * **who writes which global** -- PSY-Q reaches a global through a `lui`/`addiu`
    pair, which the recompiler emits as a register loaded with `0xHHHH0000u`
    followed by a store through it with a constant displacement.

Both are approximations and it is worth being precise about how they fail, since
the output is evidence and not proof.

`Dispatcher.Call(c, m, reg)` is an indirect jump -- a switch table, a driver
table, an overlay's per-frame slot -- and its target is not statically known. A
subtree that reaches one is marked `indirect`, and any claim about what it cannot
reach is only as good as that mark.

Global addresses are recovered by a tiny dataflow over the emitted assignments,
tracking only registers assigned a literal in the same function and invalidating
one the moment it is assigned anything else. That catches the `lui`/`addiu` idiom
that PSY-Q uses for statics, which is what matters, and misses anything reached
through a pointer, an array index or a struct base in a register -- so an empty
result means "not written through a literal address", never "not written".
"""

from __future__ import annotations

import re
from collections import defaultdict
from dataclasses import dataclass, field
from pathlib import Path

REPO = Path(__file__).resolve().parent.parent
GENERATED = REPO / "generated"

RX_FUNC = re.compile(r"^\s*public static void (func_[0-9A-Fa-f]{8}(?:_\w+)?)\(CpuContext c, IMemory m\)")
RX_CALL = re.compile(r"KingsField2\.(func_[0-9A-Fa-f]{8}(?:_\w+)?)\(c, m\)")
RX_INDIRECT = re.compile(r"Dispatcher\.Call\(c, m,")

# c.At = 0x80070000u;  /  c.V0 = 0x80170000u;  -- the lui half
RX_LOAD_HI = re.compile(r"^\s*c\.(\w+) = (0x[0-9A-Fa-f]{8})u;")
# c.V1 = c.V1 + 0x7714u;  /  c.S4 = c.S4 - 0x6B14u;  -- the addiu half
RX_ADD_IMM = re.compile(r"^\s*c\.(\w+) = c\.(\w+) ([+-]) (0x[0-9A-Fa-f]+)u;")
# m.WriteU32((c.At - 0x1A34u), ...)  /  m.WriteU8(c.S0, ...)
RX_STORE = re.compile(r"m\.Write(U8|U16|U32)\(\(?c\.(\w+)(?:\s*([+-])\s*(0x[0-9A-Fa-f]+)u)?\)?,")
# any other assignment to a register kills our knowledge of it
RX_ASSIGN = re.compile(r"^\s*c\.(\w+) = ")

WIDTH = {"U8": 1, "U16": 2, "U32": 4}


@dataclass
class Func:
    name: str
    overlay: str
    start: int                      # line number in the source file
    end: int
    calls: set[str] = field(default_factory=set)
    indirect: bool = False
    writes: dict[int, int] = field(default_factory=dict)   # address -> width
    has_backedge: bool = False

    @property
    def addr(self) -> int:
        return int(self.name[5:13], 16)


class Graph:
    def __init__(self, overlays: list[str] | None = None):
        self.funcs: dict[str, Func] = {}
        self.callers: dict[str, set[str]] = defaultdict(set)
        for path in sorted(GENERATED.glob("*.cs")):
            overlay = path.stem
            if overlay in ("Entry", "Stubs"):
                continue
            if overlays and overlay not in overlays:
                continue
            self._parse(path, overlay)
        for f in self.funcs.values():
            for callee in f.calls:
                self.callers[callee].add(f.name)

    # -- parsing --------------------------------------------------------------

    def _parse(self, path: Path, overlay: str) -> None:
        lines = path.read_text(errors="replace").splitlines()
        current: Func | None = None
        literal: dict[str, int] = {}
        labels: set[str] = set()

        for n, line in enumerate(lines, 1):
            m = RX_FUNC.match(line)
            if m:
                if current:
                    current.end = n - 1
                name = m.group(1)
                # An overlay redefinition of the same address (game vs open) keeps
                # the first; every address-based claim here names its overlay.
                current = self.funcs.setdefault(name, Func(name, overlay, n, n))
                literal, labels = {}, set()
                continue

            if current is None:
                continue
            current.end = n

            for c in RX_CALL.finditer(line):
                current.calls.add(c.group(1))
            if RX_INDIRECT.search(line):
                current.indirect = True

            if line.lstrip().startswith("L") and line.rstrip().endswith(": ;"):
                labels.add(line.strip().split(":")[0])
            elif "goto L" in line:
                target = line.split("goto ")[1].split(";")[0].strip()
                if target in labels:
                    current.has_backedge = True     # jumps to a label already seen

            self._track(line, literal, current)

        if current:
            current.end = len(lines)

    @staticmethod
    def _track(line: str, literal: dict[str, int], f: Func) -> None:
        """One line of the tiny dataflow. Order matters: a store reads the state
        this line's assignment would clobber, so stores are handled first."""
        for s in RX_STORE.finditer(line):
            width, reg, sign, off = s.group(1), s.group(2), s.group(3), s.group(4)
            base = literal.get(reg)
            if base is None:
                continue
            delta = int(off, 16) if off else 0
            addr = base - delta if sign == "-" else base + delta
            if 0x80000000 <= addr < 0x80800000:
                f.writes[addr] = max(f.writes.get(addr, 0), WIDTH[width])

        m = RX_LOAD_HI.match(line)
        if m:
            literal[m.group(1)] = int(m.group(2), 16)
            return

        m = RX_ADD_IMM.match(line)
        if m:
            dst, src, sign, imm = m.groups()
            base = literal.get(src)
            if base is None:
                literal.pop(dst, None)
            else:
                literal[dst] = base - int(imm, 16) if sign == "-" else base + int(imm, 16)
            return

        m = RX_ASSIGN.match(line)
        if m:
            literal.pop(m.group(1), None)

    # -- queries --------------------------------------------------------------

    def by_addr(self, addr: int) -> Func | None:
        return self.funcs.get(f"func_{addr:08X}") or self.funcs.get(f"func_{addr:08x}")

    def subtree(self, name: str, limit: int = 100_000) -> set[str]:
        """Every function reachable from `name`, itself included."""
        seen, stack = {name}, [name]
        while stack and len(seen) < limit:
            f = self.funcs.get(stack.pop())
            if not f:
                continue
            for callee in f.calls:
                if callee not in seen:
                    seen.add(callee)
                    stack.append(callee)
        return seen

    def subtree_blocked(self, name: str, blocked: set[str]) -> set[str]:
        """Reachability that refuses to enter any function in `blocked`.

        Used to ask "can this reach a drawing routine *without* going through a
        modal loop", which is the difference between a gate that would drop
        primitives and one that only decides whether a sub-loop is entered.
        """
        seen, stack = {name}, [name]
        while stack:
            f = self.funcs.get(stack.pop())
            if not f:
                continue
            for callee in f.calls:
                if callee in seen or callee in blocked:
                    continue
                seen.add(callee)
                stack.append(callee)
        return seen

    def reaches_indirect(self, names: set[str]) -> bool:
        return any(self.funcs[n].indirect for n in names if n in self.funcs)

    def writers(self, addr: int) -> list[str]:
        return sorted(f.name for f in self.funcs.values() if addr in f.writes)

    def writes_in_subtree(self, name: str) -> dict[int, set[str]]:
        out: dict[int, set[str]] = defaultdict(set)
        for n in self.subtree(name):
            f = self.funcs.get(n)
            if not f:
                continue
            for addr in f.writes:
                out[addr].add(n)
        return out
