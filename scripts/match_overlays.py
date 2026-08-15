#!/usr/bin/env python3
"""Carry a function identified in one overlay across to the other two.

OPEN.EXE, GAME.EXE and END.EXE are three separate links of the same PSY-Q
libraries, so every library routine exists in all three at a different address.
Identifying one by hand and then finding its twins by hand is three times the
work, and the third one is where the mistake goes.

This matches on a relocation-insensitive normal form: `j`/`jal` targets, `lui`
immediates and the 16-bit displacement of loads, stores and `addiu` are masked
out, leaving opcodes and register numbers. Absolute addresses are exactly what
differs between the links, so masking them is the point; what is left is still
unique for anything past a dozen instructions. Short functions can match in
several places -- the output says so rather than picking one.

Usage:
    # where is open's DrawOTag in the other two overlays?
    python3 scripts/match_overlays.py disc/KingsField2.cue 0x80016078

    # re-derive the whole libgpu map and print it as config patches
    python3 scripts/match_overlays.py disc/KingsField2.cue --libgpu

    # from a different overlay
    python3 scripts/match_overlays.py disc/KingsField2.cue -f game 0x80060818
"""

import argparse
import json
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from extract_file import find_entry
from inspect_disc import open_disc, resolve_image

ROOT = Path(__file__).resolve().parent.parent
EXE_HEADER_SIZE = 0x800

OVERLAYS = [("open", "OPEN.EXE"), ("game", "GAME.EXE"), ("end", "END.EXE")]

# libgpu, as identified in `open`. The public API dispatches through a driver
# table whose slots are the routines that actually touch GP0/GP1/DMA; only the
# public layer is worth naming, so that is what is listed here. Names carrying a
# `?` are inferred from shape alone and have not been pinned to a string or a
# struct layout.
LIBGPU = [
    ("ResetGraph",     0x80015A8C),
    ("SetGraphDebug",  0x80015D28),
    ("GetGraphType?",  0x80015D8C),
    ("GetGraphDebug",  0x80015D9C),
    ("SetDispMask",    0x80015DC4),
    ("DrawSync",       0x80015E04),
    ("ClearImage",     0x80015E34),
    ("LoadImage",      0x80015E84),
    ("StoreImage",     0x80015EC0),
    ("MoveImage",      0x80015EFC),
    ("ClearOTag",      0x80015F68),
    ("ClearOTagR",     0x80015FBC),
    ("DrawPrim",       0x80015FF4),
    ("DrawOTag",       0x80016078),
    ("PutDrawEnv",     0x800160D0),
    ("GetDrawEnv",     0x80016190),
    ("PutDispEnv",     0x800161F0),
]

# The subset the runtime reimplements; SdkPatches.cs binds these by name, and a
# linear sweep produces no names, so they are mapped by address in kf2.json.
HLE = {"DrawOTag", "DrawSync", "PutDrawEnv", "PutDispEnv"}

JR_RA = 0x03E00008

# Opcodes whose low 16 bits are an address-bearing immediate.
MASK_IMM_OPS = {
    0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E,              # addi .. xori
    0x20, 0x21, 0x22, 0x23, 0x24, 0x25, 0x26,              # lb .. lwr
    0x28, 0x29, 0x2A, 0x2B, 0x2E, 0x32, 0x3A,              # sb .. swc2
}


class Overlay:
    def __init__(self, name, data, text_addr):
        self.name = name
        self.data = data
        # ResolveOverlay slices the file from `skip`, so the payload starts at
        # 0x800 -- but the whole file is what we have here, and keeping the
        # header in place means file offset 0 maps to text_addr - 0x800.
        self.base = text_addr - EXE_HEADER_SIZE
        n = len(data) // 4
        self.words = struct.unpack(f"<{n}I", data[:n * 4])
        self.norm = normalize(self.words)
        fm = ROOT / "config" / "funcmaps" / f"{name}.json"
        self.sizes = {}
        if fm.exists():
            for f in json.loads(fm.read_text())["functions"]:
                self.sizes[int(f["address"], 16)] = f["size"]

    def index(self, addr):
        i = (addr - self.base) // 4
        if i < 0 or i >= len(self.words):
            sys.exit(f"{addr:#010x} is outside {self.name}")
        return i

    def addr(self, index):
        return self.base + index * 4

    def length(self, addr):
        """Function length in words: the function map if it knows, else up to
        and including the delay slot of the first `jr $ra`."""
        if addr in self.sizes:
            return self.sizes[addr] // 4
        i = self.index(addr)
        for k in range(i, min(i + 4096, len(self.words))):
            if self.words[k] == JR_RA:
                return k - i + 2
        sys.exit(f"no `jr $ra` within 16 KiB of {addr:#010x} in {self.name}")


def normalize(words):
    out = []
    for w in words:
        op = w >> 26
        if op in (2, 3):                 # j / jal: absolute target
            w &= 0xFC000000
        elif op == 0x0F:                 # lui: upper half of an address
            w &= 0xFFFF0000
        elif op in MASK_IMM_OPS:         # lower half of an address
            w &= 0xFFFF0000
        out.append(w)
    return tuple(out)


def load(cue):
    disc = open_disc(resolve_image(Path(cue)))
    out = {}
    for name, filename in OVERLAYS:
        entry = find_entry(disc, filename)
        data = disc.read(entry["lba"], entry["size"])
        if data[:8] != b"PS-X EXE":
            sys.exit(f"{filename} is not a PS-X EXE")
        text_addr = struct.unpack_from("<I", data, 0x18)[0]
        out[name] = Overlay(name, data, text_addr)
    return out


def search(src, addr, dst):
    """Every address in `dst` whose code has the same shape as src@addr."""
    n = src.length(addr)
    i = src.index(addr)
    want = src.norm[i:i + n]
    hits = []
    head = want[0]
    limit = len(dst.norm) - n
    for k in range(limit):
        if dst.norm[k] == head and dst.norm[k:k + n] == want:
            hits.append(dst.addr(k))
    return hits, n


def report(ovs, src_name, addrs, label=None):
    src = ovs[src_name]
    others = [n for n, _ in OVERLAYS if n != src_name]
    rows = []
    for addr in addrs:
        name = label(addr) if label else ""
        row = {"name": name, src_name: addr}
        for other in others:
            hits, n = search(src, addr, ovs[other])
            row[other] = hits
            row["words"] = n
        rows.append(row)

    width = max((len(r["name"]) for r in rows), default=4) or 4
    print(f"{'name'.ljust(width)}  {'insns':>5}  {src_name:>10}  " +
          "  ".join(f"{o:>10}" for o in others))
    for r in rows:
        cells = []
        for other in others:
            hits = r[other]
            if len(hits) == 1:
                cells.append(f"  {hits[0]:08X}")
            elif not hits:
                cells.append(f"{'NO MATCH':>10}")
            else:
                cells.append(f"{len(hits)} matches".rjust(10))
        print(f"{r['name'].ljust(width)}  {r['words']:5}  "
              f"  {r[src_name]:08X}  " + "  ".join(cells))

    ambiguous = [r for r in rows if any(len(r[o]) != 1 for o in others)]
    if ambiguous:
        print("\nambiguous or missing -- these are short enough to appear more "
              "than once, resolve them by the delta the unambiguous rows agree on:")
        for r in ambiguous:
            for other in others:
                if len(r[other]) != 1:
                    hits = " ".join(f"{h:08X}" for h in r[other]) or "none"
                    print(f"  {r['name'] or hex(r[src_name])} in {other}: {hits}")
    return rows


def deltas(rows, src_name, others):
    """The offset between links, from the rows that matched exactly once."""
    out = {}
    for other in others:
        seen = {}
        for r in rows:
            if len(r[other]) == 1:
                d = (r[other][0] - r[src_name]) & 0xFFFFFFFF
                seen[d] = seen.get(d, 0) + 1
        out[other] = seen
    return out


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                 formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("image", help="path to a .cue or .bin/.iso")
    ap.add_argument("addresses", nargs="*", help="addresses in the source overlay")
    ap.add_argument("-f", "--from", dest="src", default="open",
                    choices=[n for n, _ in OVERLAYS], help="source overlay")
    ap.add_argument("--libgpu", action="store_true",
                    help="re-derive the whole libgpu map and emit config patches")
    args = ap.parse_args()

    ovs = load(args.image)
    others = [n for n, _ in OVERLAYS if n != args.src]

    if args.libgpu:
        if args.src != "open":
            sys.exit("--libgpu is seeded from `open`")
        names = {addr: name for name, addr in LIBGPU}
        rows = report(ovs, "open", [a for _, a in LIBGPU], lambda a: names[a])

        # The links are laid out at a constant offset, so the rows that matched
        # exactly once agree on one delta per overlay -- which is what settles
        # the short functions that matched in several places.
        consensus = {}
        print("\ndelta between links (occurrences):")
        for other, seen in deltas(rows, "open", others).items():
            for d, count in sorted(seen.items(), key=lambda kv: -kv[1]):
                signed = d - 0x100000000 if d & 0x80000000 else d
                print(f"  {other}: {signed:+#x}  ({count} rows)")
            consensus[other] = max(seen, key=seen.get)

        print("\nconfig/kf2.json patches for the reimplemented subset:")
        for name, addr in LIBGPU:
            if name not in HLE:
                continue
            for ov in ("open", "game", "end"):
                if ov == "open":
                    a = addr
                else:
                    row = next(r for r in rows if r["open"] == addr)
                    want = (addr + consensus[ov]) & 0xFFFFFFFF
                    a = want if want in row[ov] else None
                if a is None:
                    print(f"    // {name} in {ov}: unresolved")
                    continue
                print(f'    {{ "overlay": "{ov}", "address": "0x{a:08X}",\n'
                      f'      "target": "RecompOne.Runtime.Sdk.LibGpu.{name}", '
                      f'"mode": "replace" }},')
        return 0

    if not args.addresses:
        ap.error("give at least one address, or --libgpu")
    report(ovs, args.src, [int(a, 0) for a in args.addresses])
    return 0


if __name__ == "__main__":
    sys.exit(main())
