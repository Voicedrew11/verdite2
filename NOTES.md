# King's Field II (SLPS-00069) — RecompOne port

Static recompilation of **King's Field II**, NTSC-J, From Software, 1995-07-21,
using [RecompOne](https://github.com/BlackLabelHQ/RecompOne) (MIT).

## Which game this is

The series was renumbered for the West, so the name is ambiguous:

| Chronological | Japan | North America |
|---|---|---|
| 1st (1994) | King's Field, SLPS-00017 | *not released* |
| 2nd (1995) | **King's Field II, SLPS-00069** | King's Field, SLUS-00158 |
| 3rd (1996) | King's Field III, SLPS-00377 | King's Field II, SLUS-00255 |

This project targets the **second game**, Japanese release, `SLPS-00069`.
If you meant the US-boxed "King's Field II", that is `SLUS-00255` and every
address, function map, and overlay entry here is wrong for it.

## Layout

```
config/kf2.json        recompiler config (schema: RecompOne.Recompiler/Config/ConfigLoader.cs)
config/funcmaps/       generated function maps -- address/name/size triples
patches/               hand-written C# replacing recompiled functions
scripts/inspect_disc.py  dumps SYSTEM.CNF + ISO9660 listing from a .cue/.bin
disc/                  your own dump (gitignored)
generated/             recompiler output (gitignored, derived from the disc)
tools/RecompOne/       upstream tool checkout (gitignored)
Program.cs             hand-owned entry point
KingsField2Recomp.csproj
```

`disc/` and `generated/` are gitignored because both contain copyrighted game
data. The port is code only; playing it requires your own disc.

## Prerequisites

.NET SDK 10 (both RecompOne projects target `net10.0`). On Fedora 43:

```bash
sudo dnf install dotnet-sdk-10.0
```

The runtime pulls Silk.NET, SDL, OpenGL and OpenAL via NuGet — all
cross-platform, with no Windows-only P/Invoke, so this builds and runs on Linux.

## Workflow

### 1. Supply the disc

Put `KingsField2.cue` + `KingsField2.bin` in `disc/`. The `.cue` name must match
the `cue` field in `config/kf2.json`.

### 2. Inspect it

```bash
python3 scripts/inspect_disc.py disc/KingsField2.cue
```

Confirms the sector layout, prints `SYSTEM.CNF` (which names the boot executable
— expected `SLPS_000.69` — plus TCB/EVENT/STACK), and lists every file with LBA
and size. Verified against both MODE1/2048 and MODE2/2352 images.

The recompiler discovers the boot EXE from `SYSTEM.CNF` on its own, so there is
no filename to configure. Note that the config's `main` field is **not** a
filename: it is the hex address of the game's `main()`, and it is optional
(`OverlayWriter.cs:235`).

### 3. Build the recompiler

```bash
dotnet build tools/RecompOne/RecompOne.Recompiler -c Release
```

### 4. Generate a function map

There is no King's Field decompilation producing an ELF + `.map`, so the
`-elf`/`-map` path is unavailable and we use a linear sweep. Upstream is blunt
about the tradeoff — a sweep "can and WILL get some data as code"
(`ConfigLoader.cs`), so expect to correct the map by hand as functions surface.

```bash
dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release -- \
  --generate-function-file -linear-sweep \
  -disc disc/KingsField2.cue \
  -base 0x80010000 \
  -file SLPS_000.69 \
  -out config/funcmaps/main.json
```

`-base` must be the executable's real load address, read from the PS1-EXE header
rather than assumed; `0x80010000` is only the common default. Every entry in a
function map requires a `size` — the loader throws if one is missing
(`FunctionMapLoader.cs`).

Instead of `-file`, a region can be addressed positionally with `-lba <decimal>`
and `-size <hex>`; the inspector prints both columns for exactly this.

### 5. Declare overlays

King's Field II streams per-area data off the disc. Any streamed blob that
contains **code** must be listed in `overlays[]` with its own base address and
function map; pure data files must not be. Each overlay accepts `file` or
`lba`/`size`, plus `offset`, `skip`, `rebase` and its own `stubs`/`ignored`.
Identifying which files carry code is the main investigative work here, and
[IvanDSM/KingsFieldRE](https://github.com/IvanDSM/KingsFieldRE) is the best
starting point — it has KFModTool and format notes for this exact game across
its regional variants, though no symbols, `.map` or ELF.

### 6. Recompile

```bash
dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build -- \
  config/kf2.json
```

Emits C# into `generated/`, which `KingsField2Recomp.csproj` globs.

When a function misbehaves, turn on `debug`, `addressComments` and
`disasmComments` in the config — they annotate the generated C# with source
addresses and disassembly. They inflate the output a lot, so keep them off
otherwise.

### 7. Build and run

```bash
dotnet build KingsField2Recomp.csproj -c Release
dotnet run --project KingsField2Recomp.csproj
```

## Fixing bad output

Two escape hatches, both per-function and both overlay-aware:

- `stubs[]` — replace a function with a no-op. Good for hardware pokes and
  copy-protection paths that have no meaning natively.
- `patches[]` — point a function at a hand-written C# implementation. Takes
  `function` or `address`, a `target`, and `mode` (default `replace`). The
  `overlay` field accepts a single name, a list, or `"*"` to apply the same
  patch everywhere that function appears.

Hand-written replacements live in `patches/`.

## Upstream contribution policy

RecompOne's maintainer rejects AI-authored pull requests outright ("AI PRs will
be rejected, no exceptions"). That is a policy about contributions to RecompOne
itself; the tool is MIT licensed and using it here is unaffected. It does mean
that any fix we make to the *recompiler* should be reported as an issue rather
than sent as a PR, unless you rewrite it yourself.
