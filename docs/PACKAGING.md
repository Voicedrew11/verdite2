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
- `content/mods/` — seeded into the data directory on first run.

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

4. If `builds/<key>/KingsField2.dll` is absent, build it. `BuildKey` hashes the
   three executables and `FDAT.T` off the disc (not the file: an image can differ
   in padding, track layout or the 180 MB of streamed media and still recompile
   identically), the shipped sources, and the launcher's version.

5. `Recompile.Run` drives the recompiler **in process** through
   `Assembly.EntryPoint` — its `Program.cs` is top-level statements, so its entry
   point is an ordinary invocable method. A second process was not an option: a
   self-contained publish has no `dotnet` to launch one with.

6. `GameCompile.Run` compiles the recompiler's output *and* the port's sources in
   one Roslyn pass. Together, because the port reaches into the recompiled code
   directly — `Program.cs` calls `Recompiled.Entry.Run`, and `AutoReload`,
   `AreaWarp` and `CullGrid` make fifteen static calls to
   `Recompiled.KingsField2.func_XXXXXXXX`. Splitting them would mean an interface
   boundary for each, or routing through `Dispatcher.Call`, which goes through
   `HookManager` and is therefore not the same call.

7. `AssemblyLoadContext.Default.LoadFromAssemblyPath`, then the assembly's entry
   point with the cue as `argv[0]`.

Measured on a 16-thread machine: **12.7 s** from launching the AppImage to a
built `KingsField2.dll` and a running game, of which the recompile is 0.85 s and
the rest is Roslyn. Warm launches skip to step 7.

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

## Building a release

```bash
bash scripts/setup_tools.sh          # RecompOne at its pin, patches applied

bash packaging/linux/build-appimage.sh      # dist/Verdite2-<v>-x86_64.AppImage
pwsh packaging/windows/build-windows.ps1    # dist/…-win-x64.zip and the installer
```

Neither needs the disc. `.github/workflows/release.yml` runs both on a `v*` tag
and opens a draft release; it asserts that `generated/` and `disc/` are absent
before it packages anything.

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
