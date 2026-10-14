#!/usr/bin/env python3
"""Lint the runtime's GLSL for what one driver forgives and another does not.

The shaders are C# raw string literals (GlShaders.cs, and the snippets spliced
into them with .Replace("//@name", X.Glsl)). This pulls each one out as the
runtime would compile it and checks two things:

1. Derivatives in non-uniform control flow. dFdx/dFdy/fwidth, and the texture
   calls that take an implicit level of detail, are differences across a 2x2
   quad; once a pixel of the quad has left by `discard` or `return` they are
   undefined. At a polygon's edge part of the quad is helper pixels, whose
   interpolants run past the polygon, so a discard there is ordinary. Mesa
   answered anyway; a Windows driver gave every edge pixel a wrong normal and
   the occlusion pass drew a dark line along every edge (RENDERING.md, "A dark
   line along every edge on Windows"). Rule: in any function, no derivative
   (directly, or through a function that takes one) textually after a
   `discard`, a `return`, or a call to a function that can discard. Take them
   at the top of main. A reviewed exception carries `derivatives-ok: <why>` in
   a comment on its line.

2. glslangValidator, when installed (--glslang): every stage compiled by the
   reference front end, which is strict where a vendor's compiler is lenient.

Exit status 1 on any finding.

    python3 scripts/shader_lint.py [--glslang] [--dump DIR]
"""
import argparse
import os
import re
import shutil
import subprocess
import sys
import tempfile

ROOT = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", "RecompOne.Runtime", "Gpu")
SOURCES = [
    "Backends/Common/GlShaders.cs",
    "DistanceFade.cs",
    "LinearDepthCue.cs",
    "NeighbourBlend.cs",
]

DERIV = re.compile(r"\b(dFdx|dFdy|fwidth|dFdxFine|dFdyFine|dFdxCoarse|dFdyCoarse)\s*\(")
# An implicit-LOD lookup takes derivatives too, but on a texture with one level
# they only choose between the min and mag filters, so only the samplers that
# carry a mip chain count. Keep this in step with the runtime's GenerateMipmap /
# *Mipmap* filters: the texel atlas (GlTexCache), replacement textures (0073),
# the reflection mip chains and the retained cubemap.
MIPMAPPED = {"uMip", "uAtlas", "uRepTex", "uColorMip", "uPlanarMip", "uCube"}
TEX = re.compile(r"\b(texture|texture2D|textureProj|textureOffset|texture2DProj|textureCube)\s*\(\s*(\w+)")
CALL = re.compile(r"\b([A-Za-z_]\w*)\s*\(")
OK = "derivatives-ok:"

# --- extraction ------------------------------------------------------------

LITERAL = re.compile(
    r'(?:const|static\s+readonly)\s+string\s+(\w+)\s*=\s*"""\n(.*?)\n([ \t]*)"""((?:\s*\.Replace\(\s*"[^"]*"\s*,\s*[\w.]+\s*\))*)\s*;',
    re.S)
REPLACE = re.compile(r'\.Replace\(\s*"([^"]*)"\s*,\s*([\w.]+)\s*\)')


def raw_literal(body, indent):
    out = []
    for line in body.split("\n"):
        out.append(line[len(indent):] if line.startswith(indent) else line.lstrip())
    return "\n".join(out)


def extract():
    lits, chains = {}, {}
    for rel in SOURCES:
        text = open(os.path.join(ROOT, rel)).read()
        owner = os.path.splitext(os.path.basename(rel))[0]
        for m in LITERAL.finditer(text):
            name = m.group(1)
            key = name if owner == "GlShaders" else f"{owner}.{name}"
            lits[key] = (raw_literal(m.group(2), m.group(3)), rel, text[:m.start()].count("\n") + 2)
            chains[key] = REPLACE.findall(m.group(4) or "")
    shaders = {}
    for key, (src, rel, line) in lits.items():
        for marker, ref in chains[key]:
            ref = ref.removeprefix("GlShaders.")
            if ref not in lits:
                sys.exit(f"shader_lint: {key} splices {ref}, which was not found")
            src = src.replace(marker, lits[ref][0])
        shaders[key] = (src, rel, line)
    return shaders


def stage(name, src):
    if "#version" not in src or "void main" not in src:
        return None
    if re.search(r"Fs(120)?$", name):
        return "frag"
    if re.search(r"Vs(120)?$", name):
        return "vert"
    return None

# --- the derivative rule ---------------------------------------------------


def strip_comments(src):
    """Blank comments out, keeping offsets and lines; note lines with the marker."""
    ok_lines = set()
    out = list(src)
    for m in re.finditer(r"//[^\n]*|/\*.*?\*/", src, re.S):
        if OK in m.group(0):
            ok_lines.add(src.count("\n", 0, m.start()))
        for i in range(m.start(), m.end()):
            if out[i] != "\n":
                out[i] = " "
    return "".join(out), ok_lines


FUNC = re.compile(r"^[ \t]*(?:[A-Za-z_]\w*\s+)+?([A-Za-z_]\w*)\s*\(([^;{}]*)\)\s*\{", re.M)


def functions(code):
    """name -> (body start, body end) for top-level functions."""
    out = {}
    for m in FUNC.finditer(code):
        name = m.group(1)
        if name in ("if", "for", "while", "switch"):
            continue
        depth, i = 1, m.end()
        while depth and i < len(code):
            depth += {"{": 1, "}": -1}.get(code[i], 0)
            i += 1
        out[name] = (m.end(), i - 1)
    return out


def mip_lookup(text):
    return any(m.group(2) in MIPMAPPED for m in TEX.finditer(text))


def check(name, src):
    code, ok_lines = strip_comments(src)
    funcs = functions(code)
    # Which functions take a derivative, and which can discard, transitively.
    uses, discards = set(), set()
    changed = True
    while changed:
        changed = False
        for f, (a, b) in funcs.items():
            body = code[a:b]
            calls = {c for c in CALL.findall(body) if c in funcs and c != f}
            if f not in uses and (DERIV.search(body) or mip_lookup(body) or calls & uses):
                uses.add(f)
                changed = True
            if f not in discards and (re.search(r"\bdiscard\b", body) or calls & discards):
                discards.add(f)
                changed = True
    findings = []
    for f, (a, b) in funcs.items():
        body = code[a:b]
        leave = None
        for m in re.finditer(r"\b(discard|return)\b|\b([A-Za-z_]\w*)\s*\(", body):
            what = m.group(1) or m.group(2)
            line = code.count("\n", 0, a + m.start())
            if m.group(1):
                if leave is None:
                    leave = (what, line)
                continue
            deriv = (DERIV.fullmatch(what + "(") is not None or what in uses and what != f
                     or mip_lookup(body[m.start():m.start() + 200].split(";")[0]) and TEX.match(body, m.start()))
            if what in discards and leave is None:
                leave = (f"{what}() (can discard)", line)
            if deriv and leave and line not in ok_lines:
                findings.append(
                    f"{name}: line {line + 1} of the shader: {what}() in {f}() after "
                    f"{leave[0]} at line {leave[1] + 1}: undefined in non-uniform control flow; "
                    f"take it at the top of main (or mark `// {OK} <why>`)\n"
                    f"    {src.splitlines()[line].strip()}")
    return findings

# --- glslang ---------------------------------------------------------------


def glslang(name, src, kind, tmp):
    path = os.path.join(tmp, f"{name}.{kind}")
    with open(path, "w") as f:
        f.write(src)
    r = subprocess.run(["glslangValidator", path], capture_output=True, text=True)
    if r.returncode == 0:
        return []
    lines = [l for l in (r.stdout + r.stderr).splitlines() if "ERROR" in l or "error" in l]
    return [f"{name}: glslangValidator: {l.replace(path, name)}" for l in lines] or [f"{name}: glslangValidator failed"]


def main():
    ap = argparse.ArgumentParser(description=__doc__.split("\n")[0])
    ap.add_argument("--glslang", action="store_true", help="also compile every stage with glslangValidator")
    ap.add_argument("--dump", metavar="DIR", help="write each extracted shader to DIR")
    args = ap.parse_args()

    shaders = {n: s for n, s in extract().items() if stage(n, s[0])}
    if not shaders:
        sys.exit("shader_lint: no shaders found")
    if args.dump:
        os.makedirs(args.dump, exist_ok=True)
        for n, (src, _, _) in shaders.items():
            with open(os.path.join(args.dump, f"{n}.{stage(n, src)}"), "w") as f:
                f.write(src)

    findings = []
    for n, (src, rel, line) in shaders.items():
        if stage(n, src) == "frag":
            findings += [f"{rel}:{line}: {x}" for x in check(n, src)]
    if args.glslang:
        if not shutil.which("glslangValidator"):
            sys.exit("shader_lint: --glslang needs glslangValidator (glslang-tools)")
        with tempfile.TemporaryDirectory() as tmp:
            for n, (src, rel, line) in shaders.items():
                findings += [f"{rel}:{line}: {x}" for x in glslang(n, src, stage(n, src), tmp)]

    for f in findings:
        print(f)
    frags = sum(1 for n, s in shaders.items() if stage(n, s[0]) == "frag")
    print(f"shader_lint: {len(shaders)} stages ({frags} fragment), {len(findings)} finding(s)"
          + ("" if args.glslang else "; glslang not run"))
    return 1 if findings else 0


if __name__ == "__main__":
    sys.exit(main())
