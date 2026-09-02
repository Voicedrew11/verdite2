#!/usr/bin/env python3
"""What the disc holds for each area, and what the cut eleventh one is.

Every area's content is spread over five archives under `CD/COM/`, all of them
the same shape -- a `u16` entry count, a `u16` table of start sectors in 2 KiB
units, then the entries -- and all of them indexed, directly or by a fixed
offset, by the area index the loader carries in slot 0:

    FDAT.T   entries 3N, 3N+1, 3N+2   map data, object data, the code module
    RTMD.T   entry N                  the area's model data (slot 1)
    RTIM.T   entry N                  its textures (slot 2)
    VAB.T    entry N+1, entry N+320   sound (slots 3 and 4)

**Areas 8 and 9 have no data at all; area 10 has almost all of it.** That is
what this script is for: it prints the coverage per area so the holes are a
reading rather than a claim, and it decodes the FDAT block chains and the tile
map so a cut area can be told from a placeholder.

    python3 scripts/area_content.py disc/KingsField2.cue
    python3 scripts/area_content.py disc/KingsField2.cue --map 10
    python3 scripts/area_content.py disc/KingsField2.cue --map 10 --heights

See "Area 10 is cut content that still loads" in docs/GAME_INTERNALS.md and
"fdat32 is a cut area" in docs/RECOMPILATION.md.
"""

import argparse
import struct
import sys
from pathlib import Path

sys.path.insert(0, str(Path(__file__).parent))
from extract_file import find_entry
from inspect_disc import open_disc, resolve_image

# The map is 80x80 tiles of 10 bytes -- 800 a row, 64000 in all, which is exactly
# the first block of every FDAT.T map entry. A tile has two drawn halves; each is
# a model index (drawn when < 240) and a height byte scaled by << 7.
TILES = 80
TILE_STRIDE = 10
ROW_STRIDE = TILES * TILE_STRIDE
EMPTY_MODEL = 240

ARCHIVES = ["MO.T", "TALK.T", "VAB.T", "FDAT.T", "RTIM.T", "RTMD.T", "ITEM.T"]

# The highest area index the per-area saved-state table can hold: func_8004913C
# fills exactly ten words from the u16 table at 0x801B6988.
SAVED_AREAS = 10


def read_archive(disc, name):
    """(sizes, blob-reader) for one `count / start-sector table / entries` file."""
    entry = find_entry(disc, f"CD/COM/{name}")
    data = disc.read(entry["lba"], entry["size"])
    count = struct.unpack_from("<H", data, 0)[0]
    table = [struct.unpack_from("<H", data, 2 + 2 * i)[0] for i in range(count + 1)]
    return data, table


def entry_bytes(data, table, i):
    if i + 1 >= len(table):
        return None          # past the end: func_80017F1C would read heap here
    return data[table[i] * 2048:table[i + 1] * 2048]


def chain(blob):
    """The length-prefixed block chain an FDAT data entry is."""
    out, off = [], 0
    while off + 4 <= len(blob):
        n = struct.unpack_from("<I", blob, off)[0]
        if n == 0 or off + 4 + n > len(blob):
            break
        out.append(n)
        off += n + 4
    return out


def tile_map(blob):
    """The 64000-byte first block, or None if this entry has no chain."""
    if len(blob) < 4:
        return None
    n = struct.unpack_from("<I", blob, 0)[0]
    if n != ROW_STRIDE * TILES:
        return None
    return blob[4:4 + n]


def draw_map(tiles, heights=False):
    for z in range(TILES):
        row = []
        for x in range(TILES):
            o = ROW_STRIDE * z + TILE_STRIDE * x
            ma, ha, mb, hb = tiles[o], tiles[o + 1], tiles[o + 5], tiles[o + 6]
            a, b = ma < EMPTY_MODEL, mb < EMPTY_MODEL
            if not (a or b):
                row.append("  ." if heights else ".")
            elif heights:
                row.append(f"{(ha if a else hb):3d}")
            else:
                row.append("#" if a and b else ("A" if a else "B"))
        yield f"{z:3d} " + ("".join(row) if not heights else "".join(row))


def main():
    ap = argparse.ArgumentParser(description=__doc__,
                                formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("image", type=Path)
    ap.add_argument("--map", type=int, metavar="AREA",
                    help="print the area's 80x80 tile map instead of the coverage table")
    ap.add_argument("--heights", action="store_true",
                    help="with --map, print each drawn tile's height byte")
    ap.add_argument("--areas", type=int, default=12,
                    help="how many area indices to report (default 12)")
    args = ap.parse_args()

    disc = open_disc(resolve_image(args.image))
    arc = {name: read_archive(disc, name) for name in ARCHIVES}

    def size(name, i):
        data, table = arc[name]
        b = entry_bytes(data, table, i)
        return None if b is None else len(b)

    if args.map is not None:
        blob = entry_bytes(*arc["FDAT.T"], args.map * 3)
        tiles = tile_map(blob) if blob else None
        if tiles is None:
            sys.exit(f"area {args.map} has no map block")
        for line in draw_map(tiles, args.heights):
            print(line)
        return 0

    fdat_data, fdat_table = arc["FDAT.T"]
    print(f"{'area':>4}  {'map':>7} {'objects':>8} {'module':>7} "
          f"{'RTMD':>7} {'RTIM':>7} {'VAB+1':>7} {'VAB+320':>7}  tiles  blocks")
    for a in range(args.areas):
        m = size("FDAT.T", a * 3)
        o = size("FDAT.T", a * 3 + 1)
        c = size("FDAT.T", a * 3 + 2)
        rtmd = size("RTMD.T", a)
        rtim = size("RTIM.T", a)
        vab1 = size("VAB.T", a + 1)
        vab2 = size("VAB.T", a + 320)

        blob = entry_bytes(fdat_data, fdat_table, a * 3)
        tiles = tile_map(blob) if blob else None
        lit = (sum(1 for z in range(TILES) for x in range(TILES)
                   if tiles[ROW_STRIDE * z + TILE_STRIDE * x] < EMPTY_MODEL
                   or tiles[ROW_STRIDE * z + TILE_STRIDE * x + 5] < EMPTY_MODEL)
               if tiles else 0)
        blocks = chain(blob) if blob else []

        def cell(v):
            return "  -  " if v is None else ("  0  " if v == 0 else f"{v // 1024:4d}K")

        note = "" if a < SAVED_AREAS else "   (no saved-state slot)"
        print(f"{a:>4}  {cell(m):>7} {cell(o):>8} {cell(c):>7} "
              f"{cell(rtmd):>7} {cell(rtim):>7} {cell(vab1):>7} {cell(vab2):>7}  "
              f"{lit:5d}  {blocks}{note}")

    print()
    print("A '-' is an index the archive does not hold. func_80017F1C bounds "
          "nothing, so asking for one reads two words of heap past the header "
          "buffer and hands the CD a wild sector -- which is why area 10's RTMD "
          "slot goes in as 0xFF. See patches/Area10.cs.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
