#!/usr/bin/env python3
"""Build the message-text glyph table from the disc: patches/MessageGlyphs.cs.

Signs and dialogue are 4-bit TIMs in CD/COM/TALK.T and CD/COM/ITEM.T (see "Full-screen
messages are pictures" in docs/GAME_INTERNALS.md), drawn in one fixed-width font on an
8x14 grid starting at x=4. This cuts every image into cells, reads each image with
tesseract, lines the characters up against the non-empty cells of each line, and takes
the majority vote per cell bitmap.

The output maps a hash of each cell's 1-bit mask to a character -- no bitmaps and no
text from the disc -- and patches/MessageText.cs decodes a message with it at run time.
The cell rule here and the one in MessageText.Decode must stay the same.

    python3 scripts/msg_glyphs.py disc/KingsField2.cue [--dump DIR]

--dump writes each image's decoded text to DIR (for proofreading; do not commit it).
"""
import argparse, os, struct, subprocess, sys, tempfile
from collections import Counter, defaultdict
from multiprocessing import Pool

sys.path.insert(0, os.path.dirname(__file__))
from pathlib import Path  # noqa: E402
from extract_file import find_entry  # noqa: E402
from inspect_disc import open_disc, resolve_image  # noqa: E402

CELL_W, CELL_H, ORIGIN_X = 8, 14, 4
PHASES = (3, 10)              # line tops, mod 14: odd and even line counts are centred
OVERRIDES = {"¥": "y"}  # tesseract reads the one descender-clipped 'y' as a yen sign


def lum(c):
    return ((c & 31) * 2 + ((c >> 5) & 31) * 5 + ((c >> 10) & 31)) / 8.0


def tims(data):
    count = struct.unpack_from("<H", data, 0)[0]
    offs = struct.unpack_from("<%dH" % (count + 1), data, 2)
    for i in range(count):
        s, e = offs[i] * 2048, offs[i + 1] * 2048
        if e <= s:
            continue
        tag, flags = struct.unpack_from("<II", data, s)
        if tag != 0x10 or flags != 8:
            continue
        p = s + 8
        n = struct.unpack_from("<I", data, p)[0]
        clut = struct.unpack_from("<16H", data, p + 12)
        p += n
        n, _, _, iw, ih = struct.unpack_from("<IHHHH", data, p)
        raw = data[p + 12:p + n]
        w = iw * 4
        px = bytes((raw[k >> 1] >> (4 * (k & 1))) & 15 for k in range(w * ih))
        yield i, clut, w, ih, px


def fnv64(b):
    h = 0xCBF29CE484222325
    for x in b:
        h = ((h ^ x) * 0x100000001B3) & 0xFFFFFFFFFFFFFFFF
    return h


def mask_bytes(on, w, h, x0, y0):
    """A cell as 14 bytes, bit c of byte r = column c of row r."""
    out = bytearray(CELL_H)
    for r in range(CELL_H):
        y = y0 + r
        if y >= h:
            continue
        v = 0
        for c in range(CELL_W):
            if on[y * w + x0 + c]:
                v |= 1 << c
        out[r] = v
    return bytes(out)


def cells(clut, w, h, px):
    """Lines of cell masks, or None if the image is not on the grid."""
    bg = Counter(px).most_common(1)[0][0]
    mx = max((lum(clut[i]) for i in set(px) if i != bg), default=0)
    if mx == 0:
        return None
    on = bytes(1 if p != bg and lum(clut[p]) >= 0.5 * mx else 0 for p in px)
    rows = [y for y in range(h) if any(on[y * w:(y + 1) * w])]
    if not rows or any(on[y * w + x] for y in rows for x in range(ORIGIN_X)):
        return None
    r0 = rows[0]
    offs = [(r0 - p) % CELL_H for p in PHASES if (r0 - p) % CELL_H <= 4]
    if not offs:
        return None
    top = r0 - min(offs)
    lines = []
    for y in range(top, h, CELL_H):
        lines.append([mask_bytes(on, w, h, x, y) for x in range(ORIGIN_X, w - CELL_W + 1, CELL_W)])
    return lines


def ocr(job):
    key, lines = job
    from PIL import Image
    W, H = CELL_W * len(lines[0]), CELL_H * len(lines)
    im = Image.new("L", (W + 16, H + 16), 255)
    pix = im.load()
    for li, row in enumerate(lines):
        for ci, m in enumerate(row):
            for r in range(CELL_H):
                for c in range(CELL_W):
                    if m[r] >> c & 1:
                        pix[8 + ci * CELL_W + c, 8 + li * CELL_H + r] = 0
    im = im.resize(((W + 16) * 4, (H + 16) * 4), Image.NEAREST)
    with tempfile.NamedTemporaryFile(suffix=".png") as f:
        im.save(f.name)
        out = subprocess.run(["tesseract", f.name, "-", "--psm", "6"], capture_output=True, text=True).stdout
    return key, out


def main():
    ap = argparse.ArgumentParser(description=__doc__.splitlines()[0])
    ap.add_argument("image")
    ap.add_argument("-o", "--out", default="patches/MessageGlyphs.cs")
    ap.add_argument("--dump")
    a = ap.parse_args()

    disc = open_disc(resolve_image(Path(a.image)))
    jobs = []
    for archive, name in ((3, "CD/COM/TALK.T"), (6, "CD/COM/ITEM.T")):
        e = find_entry(disc, name)
        data = disc.read(e["lba"], e["size"])
        for entry, clut, w, h, px in tims(data):
            lines = cells(clut, w, h, px)
            if lines:
                jobs.append(((archive, entry), lines))

    freq = Counter(m for _, lines in jobs for row in lines for m in row if any(m))
    # An image mostly made of one-off cells is a second font or a picture.
    jobs = [j for j in jobs if (lambda ks: ks and sum(freq[m] == 1 for m in ks) / len(ks) <= 0.3)(
        [m for row in j[1] for m in row if any(m)])]

    with Pool() as pool:
        text = dict(pool.map(ocr, jobs))

    norm = str.maketrans({"’": "'", "‘": "'", "“": '"', "”": '"', "|": "I"})
    votes = defaultdict(Counter)
    for key, lines in jobs:
        rows = [row for row in lines if any(any(m) for m in row)]
        said = [l.translate(norm).replace(" ", "") for l in text[key].splitlines() if l.strip()]
        if len(rows) != len(said):
            continue
        for row, s in zip(rows, said):
            ink = [m for m in row if any(m)]
            if len(ink) == len(s):
                for m, ch in zip(ink, s):
                    votes[m][ch] += 1

    table = {}
    for m, c in votes.items():
        ch = c.most_common(1)[0][0]
        table["%016x" % fnv64(m)] = OVERRIDES.get(ch, ch)

    decoded = sum(all(not any(m) or "%016x" % fnv64(m) in table for row in lines for m in row)
                  for _, lines in jobs)
    with open(a.out, "w") as f:
        f.write("// Generated by scripts/msg_glyphs.py from the disc; do not edit by hand.\n")
        f.write("// FNV-1a 64 of a cell's 14 row bytes -> the character. See MessageText.Decode.\n")
        f.write("namespace Kf2;\n\nstatic class MessageGlyphs\n{\n")
        f.write("    public static readonly Dictionary<ulong, char> Table = new()\n    {\n")
        for k, ch in sorted(table.items()):
            f.write("        [0x%sUL] = %s,\n" % (k, "'\\''" if ch == "'" else "'\\\\'" if ch == "\\" else "'%s'" % ch))
        f.write("    };\n}\n")
    print(f"{len(table)} glyphs; {decoded} of {len(jobs)} grid images decode completely -> {a.out}")

    if a.dump:
        os.makedirs(a.dump, exist_ok=True)
        for (archive, entry), lines in jobs:
            out = []
            for row in lines:
                s = "".join(" " if not any(m) else table.get("%016x" % fnv64(m), "□") for m in row)
                out.append(s.rstrip())
            with open(os.path.join(a.dump, f"{archive}_{entry:03d}.txt"), "w") as f:
                f.write("\n".join(out).strip("\n") + "\n")


if __name__ == "__main__":
    main()
