# Packaging

How the port becomes something a person can download, and why it is shaped the
way it is. `docs/DEVELOPMENT.md` is still the document for working on the port;
this one is about shipping it.

## The problem a release has to solve

`generated/` is a translation of FromSoftware's code. The compiled form of it is
no less derived than the source, so **there is no binary of this game that can be
distributed** — which is why `generated/` is gitignored and why the README's
claim that the project ships no game data has to keep being true of a release and
not only of the repository.

The way out is that the *inputs* to a build are all distributable. The addresses
in `config/` are metadata about the disc's code layout, not the code. The port's
own 22k lines under `patches/` and `Program.cs` are original work under the
repository's MIT licence. The recompiler and the runtime are MIT. Only the output
is encumbered — so the release ships the inputs and produces the output on the
machine of somebody who owns the disc.

That is also, and separately, a *correctness* win rather than only a legal one.
The generated dispatch tables bake absolute LBAs from one mastering
(`generated/fdat02.cs` reads `public int LbaStart => 457;`), and `Dispatcher`
arms an overlay swap on a CD read hitting that exact sector. A prebuilt binary
would silently fail to load area modules on a differently mastered dump. A
per-user recompile reads those LBAs off the player's own image.

## The two projects

| project | builds without the disc | what it is |
|---|---|---|
| `Verdite2.Launcher/Verdite2.Launcher.csproj` | **yes** | the shipped executable |
| `KingsField2Recomp.csproj` | no | the developer path, unchanged |

They are opposites on purpose. `KingsField2Recomp.csproj` compiles `generated/`
and `patches/` through the SDK's default globs, which is what makes iteration
incremental and is why nothing about the developer workflow changed. The launcher
compiles neither: it carries them as *payload* and compiles them at first run.

`.github/workflows/ci.yml` asserts the difference on every push, because it is
easy to break by accident and impossible to notice locally, where `generated/`
exists.

## What the release contains

Beside the executable, `content/`:

- `content/config/` — `kf2.json` and the thirteen funcmaps. Addresses, names and
  sizes; no disc bytes.
- `content/src/` — `Program.cs` and `patches/**`, as source text.
- `content/mods/` — copied into the data directory the first time each file is
  seen. `.mods-seeded` records the relative paths that have ever been seeded,
  rather than being a bare "seeding has happened" marker: a mod the player has
  deleted stays deleted because its path is in the record, and a mod a later
  release adds is still seeded because its path is not. A bare marker gets the
  first of those right and the second wrong, silently — nothing reports a mod
  that never arrived.

Plus `RecompOne.Runtime.dll`, `recompone.dll`, Roslyn and the self-contained .NET
runtime. About 109 MB laid out, 41 MB as an AppImage.

## First run

1. `Paths.Prepare()` resolves the data directory and **chdirs into it**. That one
   line is the whole of the packaging fix for file locations: the runtime
   addresses everything it owns with a bare relative path — `settings.json`,
   `interface.ini`, `carda.sav`, `cardb.sav`, `carda.fog`, `mods/.cache` — so they
   all resolve there and none of them needed a patch.

   `%LOCALAPPDATA%\Verdite2`, or `$XDG_DATA_HOME/verdite2` else
   `~/.local/share/verdite2`. `VERDITE2_DATA` overrides it.

2. `Runtime.DiscValidator = DiscCheck.Validate`. **That slot has existed since the
   runtime was written and nothing ever filled it**, so until now any file at all
   was accepted. It checks `SYSTEM.CNF` boots `SLUS_001.58` and that the four
   files the recompile reads are present and long enough, and it names
   `SLUS-00255` explicitly — the US-boxed *King's Field II* is a different game,
   is what most people will reach for, and would otherwise build.

3. `Runtime.WaitForValidDisc()` — the runtime's own picker, which already opens a
   native file dialog and saves the accepted path. It needed nothing but the
   validator.

4. A cue on the command line — the developer form, `Verdite2 other.cue` — is
   settled **here**, before the key, and is validated like any other disc. It has
   to be: the generated dispatch tables bake absolute LBAs from one mastering, so
   an assembly keyed on the saved disc and then handed a different image would
   silently fail to load its area modules, which is the exact failure the per-user
   recompile exists to prevent. Whatever is played is what is keyed and built.

5. If `builds/<key>/KingsField2.dll` is absent, build it. `BuildKey` hashes the
   three executables and `FDAT.T` off the disc (not the file: an image can differ
   in padding, track layout or the 180 MB of streamed media and still recompile
   identically), the shipped sources **and the shipped config**, and the
   launcher's version. `content/config` is in there because it is the other half
   of what goes into the build — the function maps decide where each function
   starts and the SDK map decides which are bound to the runtime's HLE, so a
   corrected sweep changes the emitted C# with no source file having moved.

6. `Recompile.Run` drives the recompiler **in process** through
   `Assembly.EntryPoint` — its `Program.cs` is top-level statements, so its entry
   point is an ordinary invocable method. A second process was not an option: a
   self-contained publish has no `dotnet` to launch one with.

7. `GameCompile.Run` compiles the recompiler's output *and* the port's sources in
   one Roslyn pass. Together, because the port reaches into the recompiled code
   directly — `Program.cs` calls `Recompiled.Entry.Run`, and `AutoReload`,
   `AreaWarp` and `CullGrid` make fifteen static calls to
   `Recompiled.KingsField2.func_XXXXXXXX`. Splitting them would mean an interface
   boundary for each, or routing through `Dispatcher.Call`, which goes through
   `HookManager` and is therefore not the same call.

8. `AssemblyLoadContext.Default.LoadFromAssemblyPath`, then the assembly's entry
   point with the cue as `argv[0]`.

Measured on a 16-thread machine: **12.7 s** from launching the AppImage to a
built `KingsField2.dll` and a running game, of which the recompile is 0.85 s and
the rest is Roslyn. Warm launches skip to step 8.

## Two things that were nearly wrong

**The reference set must come from the host, not from what is loaded.**
`GameCompile.References()` reads `TRUSTED_PLATFORM_ASSEMBLIES`. The first version
walked `AppDomain.CurrentDomain.GetAssemblies()`, the way `ModCompiler` does — and
`ModCompiler` is right to, because a mod compiles against what the game has and by
then the game has loaded it. The launcher has loaded almost nothing. The build
failed on `patches/AgentServer.cs` with six errors about `System.Net.Sockets`,
purely because the launcher does not open a socket. Every framework assembly the
port uses and the launcher does not was missing for the same reason; sockets was
just the first one a patch happened to name.

**`ImplicitUsings` is an SDK feature, not a compiler one.** Both csprojs enable it
and the SDK answers by generating `GlobalUsings.g.cs`. Roslyn generates nothing,
so `GameCompile` supplies the `Microsoft.NET.Sdk` set itself. Without it the
port's 22k lines lose `System`, `System.Linq` and the rest, and fail in hundreds
of places that read as the port being broken rather than as a missing file.

More generally: **the compilation options in `GameCompile` and the properties in
`KingsField2Recomp.csproj` are two statements of one thing and must stay in step.**
A difference between them is a class of bug that only exists in the release. The
port's own notes record what losing the frame boundary looks like, and it is not a
crash — it is the whole game running fast from the title onward, silently. The
check for it is `KF2_FPS=144 KF2_FPS_PROBE=1` against the packaged binary, which
must read 20.0 ticks/s. Measured: 144.0 fps drawn, 19.9-20.0 ticks/s.

## Versioning

**The version is one line in `VERSION` at the repository root, and everything
else reads it.** That is the whole design, and it is a design rather than a
convention because five things have to agree about the number and only one of
them can parse XML: the launcher's assembly version, the AppImage's file name,
the Windows zip's, the Inno installer's, and the tag the release is cut on. The
number used to live in `<Version>` in `Verdite2.Launcher.csproj`, which the
AppImage script scraped with `grep -oP`, the Windows script parsed as XML, and
`verdite2.iss` simply **duplicated as a literal fallback** — three readers, one
of which could silently disagree.

`MAJOR.MINOR.PATCH`, no suffix. CI refuses anything else.

| what | how it gets the number |
|---|---|
| the assembly | the csproj reads `../VERSION` into `<Version>` |
| `Verdite2-<v>-x86_64.AppImage` | `build-appimage.sh` reads `VERSION` |
| `Verdite2-<v>-win-x64.zip` | `build-windows.ps1` reads `VERSION` |
| `…-win-x64-setup.exe` | the script exports `VERDITE2_VERSION`; the `.iss` **errors** if it is unset |
| the git tag | `release.yml` asserts `v$(cat VERSION)` equals the tag it was triggered by |

That last row is the one that matters most, and it closes a failure that is
invisible until somebody has downloaded it: a `v0.2.0` tag on a tree that still
says `0.1.0` would publish `0.1.0` files under a `0.2.0` release, with nothing in
the process disagreeing.

### The number is not the build

A release is many commits wide, so `0.1.0` does not identify a binary. The
assembly's `InformationalVersion` is therefore `0.1.0+<9-char sha>`, stamped by
the csproj's `StampBuild` target off `git rev-parse` (or `VERDITE2_BUILD`, for a
build made outside a checkout — a source tarball, a distro package — falling back
to `local`). `Ver.Full` reads it back, and it is printed at startup, written at
the head of the build log, and quoted on an unhandled exception; `Ver.Number`
alone is in the window title. **What a bug report should quote is the full
string.**

The sha is deliberately **not** part of `BuildKey`. Hashing it would make every
commit — a docs-only one included — throw away the player's built game and
recompile it. What goes into that assembly is the shipped sources and the shipped
config, and `BuildKey` hashes those directly.

### Cutting one

```bash
bash scripts/release.sh 0.2.0        # bumps VERSION, commits, tags. Does not push.
git push origin HEAD && git push origin v0.2.0
```

The script refuses a malformed number, a dirty tree, a tag that already exists
and a bump to the version already in the file. It does not push, because pushing
the tag is what publishes the draft release and that is a decision rather than a
step. The workflow builds both platforms, asserts the tag against `VERSION`, and
opens a **draft** with the commits since the previous tag appended to the body.

## Building a release

```bash
bash scripts/setup_tools.sh          # RecompOne at its pin, patches applied

bash packaging/linux/build-appimage.sh      # dist/Verdite2-<v>-x86_64.AppImage
pwsh packaging/windows/build-windows.ps1    # dist/…-win-x64.zip and the installer
```

Neither needs the disc. `.github/workflows/release.yml` runs both on a `v*` tag
and opens a draft release; it asserts that `generated/` and `disc/` are absent,
and that the tag matches `VERSION`, before it packages anything.

**The release workflow has never run.** The tag pushed to test it was
`test-win-0.1.0`, which does not match the `tags: ['v*']` trigger, so only `ci`
fired; and `workflow_dispatch` is not offered for a workflow absent from the
**default branch**, which `release.yml` still is. So the Windows leg — the
installer, and the launcher on real Windows — is unexercised. Merging `dist` and
tagging `v*` is what settles it.

Trimming is off and must stay off: `MonoMod.RuntimeDetour` builds detours at run
time, `ModCompiler` hands Roslyn the loaded assemblies, and
`patches/AutoStart.cs` reflects over runtime internals.

`packaging/shared/verdite2.png` and `.ico` are **placeholders** generated by
`make-icon.py` in the palette the game's own map uses. They are not artwork and
should be replaced before a release anyone else sees.

## The one patch this needed

`patches/recompone/0030-expose-host-pump.patch` makes `HostWindow.Pump` public as
`Runtime.Pump`. The build blocks for seconds and a window that stops pumping for
seconds is one the desktop offers to force-quit; `WaitForValidDisc` already runs
exactly that loop but only ever for its own condition. Everything else the
progress UI needs was already public — `Popup` and `PopupManager.Register` — so
`BuildProgressPopup` lives in the launcher.

## Not done

macOS (`.app` for `osx-arm64` and `osx-x64`) and Flatpak. The data-directory work
both need is done; what is missing is the packaging and, for macOS, signing and
notarization. `.chd` is not supported — `CueFs` reads cue/bin, at recompile time
and at play time both, and a CHD decoder is real work rather than a wrapper.

**Never looked at by eye:** the progress popup itself, and the placeholder icon at
the sizes a desktop actually draws it.
