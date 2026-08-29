# Patches and mods: how the port's own code is attached

Two ways to run C# against the recompiled game, and a rule for choosing between
them. Then the settings UI both feed into, and the two features whose whole story
lives here — frame pacing and auto reload.

## Where each patch is written up

| file | what it does | detail |
|---|---|---|
| `FramePacing.cs` | paces the picture, and holds the world to 20 Hz | this file |
| `MenuPacing.cs`, `LoopPacing.cs` | run a loop that renders its own frames at the world's rate, and fill the gap by redrawing | this file |
| `FrameSmoothing.cs`, `ObjectSmoothing.cs`, `AnimSmoothing.cs` | carry the view, everything that moves, and MO clip time, between ticks | this file |
| `DrawCensus.cs` | attributes the frame's primitives to the routine that drew them | [GAME_INTERNALS.md](GAME_INTERNALS.md) |
| `AutoReload.cs` | reloads the last save on death | this file |
| `NoDither.cs` | clears the GPU dither bit | [RENDERING.md](RENDERING.md) |
| `Perspective.cs`, `Subpixel.cs`, `ZBuffer.cs` | switches and probes over the GTE depth mechanisms | [RENDERING.md](RENDERING.md) |
| `Widescreen.cs`, `CullCone.cs`, `ViewClip.cs`, `PrimBuffer.cs` | aspect ratio and the culls it runs into | [WIDESCREEN.md](WIDESCREEN.md) |
| `Analog.cs`, `AnalogProbe.cs`, `Mouse.cs`, `KeyLayout.cs` | pad, mouse and keyboard | [INPUT.md](INPUT.md) |
| `EndingHold.cs` | keeps the window alive through `END.EXE`'s final spin | [RUNTIME.md](RUNTIME.md) |
| `UiScale.cs` | forces and saves the interface scale, for a config too large to edit in | [RUNTIME.md](RUNTIME.md) |
| `settings/*` | the pages all of the above draw into | this file |

`mods/kf2debug` is the measurement/debug tool that stayed a mod; its findings are
in [GAME_INTERNALS.md](GAME_INTERNALS.md).

## Mods

**RecompOne has a modding system; use it.** `RecompOne.Runtime/Modding/` is 839
lines of `ModLoader`, `HookManager`, `SymbolRegistry`, hook attributes and a
`ModsPopup` UI, and the generated `Entry.cs` already calls `ModLoader.LoadAll()`.
It was missed on the first pass here and a parallel one was built by hand; that
was wasted work and two wrong beliefs, both recorded below so they are not
re-derived.

A mod is a folder (or zip) under `mods/` with a `mod.json` and C# sources,
**compiled at run time by Roslyn** and hooked by address:

```
mods/kf2debug/mod.json   + GameState.cs,     noclip, invincibility, warp and a live
                           Noclip.cs,        state readout
                           Cheats.cs, Warp.cs,
                           Hotkeys.cs, DebugPanel.cs, DebugMod.cs
```

(What building `kf2debug` turned up about the game is under "Debug tools" in
[GAME_INTERNALS.md](GAME_INTERNALS.md).)

Two things the debug mod needed that are not obvious from the mods above:
**`PanelManager.Register(IPanel)` is public**, so a mod can add a dockable window
next to CPU State rather than living inside the Mods popup — but panels do *not*
auto-populate the menu bar (`MainMenuBar` declares each built-in one by hand), so
it also needs a `MenuRegistry.Menu(...).Panel<T>(...)` entry. And
**`HostWindow.IsKeyDown(Key)` is public**, which is the route to real hotkeys;
note it lives in `RecompOne.Runtime.Host`, not `.Host.Window` like everything
else in that folder. F1 and F11 are the host's own.

```csharp
[PostHook("game", Address = 0x80060818)]
static void AfterDrawOTag(CpuContext c, IMemory m) { ... }

[PreHook("game", Address = 0x80037C0C)]        // return false to skip the original
static bool BeforeStage(CpuContext c, IMemory m) => ...;
```

`HookManager` detours the emitted method through MonoMod, so **a hook site costs
no config entry and no recompile** — `SymbolRegistry` resolves `overlay + address`
(or `overlay + function name`) against the dispatcher's tables, which are all
registered up front in `Entry.Run`. `[Replace]` also exists, and can take a
leading `orig` parameter to call through to the original.

Mods are toggled in the game's own Mods panel, with reload, and `IMod.DrawSettings`
puts an ImGui panel under each one. State persists to `interface.ini` as
`mods.<id>.enabled`.

### What belongs in a mod, and what does not

`patches/FramePacing.cs` is deliberately **not** a mod. It is a correctness fix
that has to be on: as a loaded package it could be absent, disabled, or fail to
compile, and the failure mode would just be the game running too fast. It still
attaches through `HookManager` — the same runtime detour, no config entry — just
from `Program.cs` with its own `ModInfo`, so it cannot be turned off by accident.
Measurement tools, which are genuinely optional and expensive, are real mods.

`patches/NoDither.cs` (see "Dithering" in [RENDERING.md](RENDERING.md)) moved the
other way — it was `mods/nodither` — and the
reason is not correctness. It is that **a mod is a package that can be absent, and
a picture the port offers should not be**: the dither switch is a graphics option
like vsync, it costs nothing when off, and a player looking for it in Display
should find it there rather than have to know a package exists. That is the
general test for the move. Being a patch is also what lets it default to *on*
(dither cleared) — a mod defaults to disabled and would have shipped doing
nothing. The conversion itself was mechanical: `[PreHook]`/`[PostHook]` attributes
became `SymbolRegistry.Resolve` plus `HookManager.AddPre/AddPost` in an `Attach`
deferred to the first `OverlayLoadedEvent`, hook bodies had to become `public`,
`OnLoad`'s config read moved to `RuntimeReadyEvent`, and `DrawSettings` became an
`IPatchPage`. Nothing about the hooking changed, because `HookManager` is the same
detour either way.

`patches/AutoReload.cs` moved for a third reason, which is the same test applied
to behaviour rather than to a picture: **four screens of menu after every death is
not a taste a player should have to find a package to fix.** It is the first patch
whose settings are not a machine option at all, and it is why the port has a
Gameplay section — see "Auto reload became a patch" below.

`patches/Analog.cs` is the same test applied to the *pad*, and it is the clearest
case of the four: with the patch absent, a modern controller's left stick is bound
to the D-pad, and the D-pad in this game turns rather than walks — so the port's
answer to "I plugged a controller in" is a stick that spins the camera. That is
not a taste either. It defaults to on for the reason under "Analog control became
a patch" in [INPUT.md](INPUT.md): **sticks centred means the hooks return before
touching memory**, so
keyboard and D-pad play with it on is identical to play with it off, and the cost
of shipping it enabled is nothing at all.

`patches/Widescreen.cs` is the dither test again — an aspect ratio is a picture
the port should offer without a package having to load — and it is the case that
separates that test from the *default*. Being a patch decides where the switch
lives; it does not decide which way the switch points, and this one ships pointing
at 4:3 because the picture has never been checked by eye. See "Widescreen became a
patch" in [WIDESCREEN.md](WIDESCREEN.md).

Config `patches[]` entries are still the right tool for **`replace`**, which is
how the whole SDK is bound: those are needed before any mod could load, and there
are 63 of them.

### Four things that will bite

1. **`ModCompiler` does not enable implicit usings.** Every mod file must name
   `System`, `System.Collections.Generic`, `System.Linq` itself. This is the real
   cost of runtime compilation: it is a *runtime* error, printed as
   `[Mods] <id>: ... error CS0103` and then the mod silently does not load.
2. **`mods/**` must be removed from the csproj `Compile` glob**, exactly like
   `tools/**`. Otherwise the SDK compiles every mod into the main assembly *as
   well as* Roslyn compiling it at run time, giving two copies that can drift.
3. **Mods default to disabled** (`IsEnabled` falls back to `false`), and
   `LoadAll` returns silently when nothing is enabled — no log line at all. A mod
   that appears to do nothing is probably just off.
4. **`mods/.cache/`** holds the compiled assemblies and is written into the repo;
   it is gitignored.

**Seen once, not reproduced, and not a fifth item:** the very first run with the
dither mod hung after both mods reported their hooks and before
`ModLoader.LoadAll` printed `loaded N/N` — i.e. in `HookManager.Commit`, with the
worker thread absent from `dotnet-stack` output entirely. Five later runs of the
same pair committed instantly. Recorded so it is recognised rather than chased if
it ever happens again: the stack will show the main thread in
`ModLoader.LoadAll → HostWindow.Pump`.

## Patch settings: a patch's knobs go in the runtime's own sections

A mod gets a settings UI for free — `IMod.DrawSettings` is drawn under the gear
button in the Mods popup — and a patch got nothing, so a patch's only knob was an
environment variable read once at startup. That is the real cost of moving
something out of `mods/` and into `patches/`: the code keeps working and the whole
panel disappears. `patches/settings/` is the replacement.

**It is not a panel of its own.** `SettingsRegistry.Extend(sectionId, draw)` takes
a section id and a callback and runs it after that section's own content, which
means a patch's settings can go where a user would already look for them. The
frame rate belongs under Video beside vsync and render scale; a King's Field
box off to one side would be a worse place to keep it. So a patch registers a page
against a section:

```csharp
PatchSettings.Register("display", new FramePacingPage());   // IPatchPage: Id, Title, Draw()
```

The section ids are the runtime's own — `interface`, `input`, `display`, `paths`,
`audio` — plus `gameplay`, the one section the port registers itself, for patches
that change how the game plays rather than how the machine behaves (see "Auto
reload became a patch"). Registration is deferred to `RuntimeReadyEvent`, which is
late enough that `HostWindow`'s `Load` has registered the runtime's five — so the
port's own section can join them there, and an id matching no section is reported
at startup instead of silently drawing nothing. A page is plain ImGui,
which is what makes this the landing site for a converted mod: its `DrawSettings`
body moves across unchanged. `NoDitherPage` is the first one that arrived that
way — `mods/nodither`'s checkbox, in Video under the frame rate, with the mod's
`_on` field now `NoDither.Enabled` and its `Runtime.View` calls now
`PatchSettings.Set`. Its explanatory paragraphs did *not* come across: a mod's
panel is a place to explain itself, a settings section is a list of switches, so
the prose became a hover tooltip and the counters stayed on the console.

**Pages sharing a `Title` share one heading.** `SeparatorText` over a lone
checkbox is the checkbox's own label written twice with a rule through it, so the
dither switch is titled `Enhancements` and anything else of that size can join it
there — perspective correction, sub-pixel positioning and the Z-buffer all did;
`FramePacingPage`, which is a combo plus a live measurement, keeps its own. The
list is already sorted by title, so drawing a heading only when it changes is the
whole implementation.

**A page could not get *inside* a runtime section, and now it can.** `Extend`
draws after the section's whole body, so an option that is one of the section's
*ordinary* options — an aspect ratio, which is the same kind of choice as the
render scale — could only land in a block underneath everything, below the GPU
backend combo, which reads as the port's box rather than as a picture setting.
`patches/recompone/0013` is the fix and it is six lines:
`SettingsRegistry.DrawSlot(slotId)` walks the same `Extend` table by an arbitrary
id, and `DisplaySettingsSection` calls it once, right after the render scale, as
`"display.render_scale"`. `PatchSettings.RegisterSlot` is the port-side half; a
slot page draws **bare**, with no `SeparatorText`, so it sits in line with
fullscreen, vsync and render scale rather than under a heading.

The asymmetry to know about: `Register` reports an unknown *section* id at
startup, because the sections are enumerable. **An unknown slot id is silent** —
`Extend` takes any string and nothing lists the slots — so a typo there costs a
control that simply never appears.

**A page big enough to need shaping is the point where two rules stop scaling.**
`AnalogPage` is eighteen knobs in the `input` section, under the button-binding
table, and it broke both of the small pages' habits:

- *Static properties with `Set…` methods* are how the other patches expose live
  state, and they exist to clamp. Eighteen of them would clamp nothing a slider
  had not already, so `Analog`'s settings are plain **public static fields** —
  which is also what lets the page pass them straight to `ImGui.SliderFloat` by
  `ref` instead of copying a value out and back. The clamp that a slider does
  *not* do is ctrl+click typing, and `ImGuiSliderFlags.AlwaysClamp` is that.
- *A flat list of switches* is right for three of them and wrong for eighteen. The
  three that matter — the master switch and the two sensitivities — stay at the
  top, and the deadzones, curves, acceleration ramp, axis inversions and the probe
  go under an `ImGui.TreeNode`, with `BeginDisabled` greying the lot when the
  master switch is off.

It also carries the first thing in a patch page that is **not** a setting: a live
readout of both sticks, drawn dimmed at the bottom. Deadzone is the setting people
get wrong, and the number that decides it is how far the pad in hand rests from
centre — without the readout that is a guess, checked by walking into a wall.

### Renaming a runtime section without patching the checkout

"Display" is where the window lives; everything in that section is how the picture
is made, and the port only adds to it — a frame rate and a dither switch beside
vsync and render scale. Renaming it to **Video** turns out not to need
`patches/recompone/` at all: `SettingsPopup` draws each tab from
`Localization.T(section.TitleKey)`, and `Localization.Merge(json)` is public and
overwrites by key, so `PatchSettings` merges one string for `settings.display` at
the same `RuntimeReadyEvent` it registers the pages.

**Only English is overridden**, because the runtime's other two languages already
say exactly this: pt-BR is `Vídeo` and es-419 is `Video`. Overriding a key means
supplying every language that key has, or the ones left out keep the old word —
here the old word is already the new one.

The **id stays `display`**. Only the label moves, so `Register("display", …)`,
`SettingsRegistry.Extend` and the runtime's own section are all untouched. Two
things make this safe: `Merge` calls `EnsureBase` first, so the embedded table is
loaded before the override lands on top of it rather than being loaded over it
afterwards; and the override goes into the per-language tables themselves, so a
later `SetLanguage` re-points at a table that already carries it. Verified against
`RecompOne.Runtime.dll` directly — the sibling keys (`settings.display.vsync`,
`settings.input`) are untouched, and `settings.display` has exactly one consumer
in the runtime, that section's own `TitleKey`.

This is worth knowing generally: **anything the runtime shows through
`Localization.T` can be renamed from the port**, which is a much cheaper lever
than a patch to a gitignored checkout.

**A heading over the runtime's own controls was tried and dropped.**
`SettingsPopup` draws one `SeparatorText` per pane, from the section's
`TitleKey`, and then the section's content bare, so fullscreen and vsync end up
the only unlabelled controls on a page whose other groups are named. Wrapping the
section works — `ISettingsSection` is public, `SettingsRegistry.Register` replaces
by id, and a wrapper forwarding `Id`/`TitleKey`/`Order` can draw a heading before
delegating `Draw` — but it buys a rule and a word for four controls the pane title
already names, and it is one more thing to keep working against an upstream that
does not know about it. The pane is `Video` and the port's groups are named under
it. If the runtime ever grows more sections worth grouping, the missing per-group
heading is upstream's to fix and is worth an issue rather than a wrapper.

Settings persist through `PatchSettings.Get/Set`, which is `Runtime.View` plus an
immediate `SaveView` — the same `interface.ini` store the mods use, keyed
`kf2.<patch>.<name>`. **A persisted default cannot be read in `Configure`:**
`ConfigManager.Load()` runs inside `HostWindow.Initialize`, which is inside
`Runtime.Initialize`, which is *after* `Program.cs`, and `Load` replaces the whole
`ViewConfig` — so an early read sees an empty config and an early write is thrown
away. Read it on `RuntimeReadyEvent`, dispatched at the end of that same
`Initialize`. `FramePacing` does this and keeps the precedence the mods use:
`KF2_FPS` beats the saved value.

### The rate became a setting, so every hook is installed whatever it is set to

Hooks attach at the first overlay load; the rate is chosen from the settings long
after, and cannot add one then. So `FramePacing` hooks **all** of it up front —
the frame gate, the three gated stages, the delta scaler — whatever `KF2_FPS`
said, and each hook decides per frame whether to do anything. At 30 every one of
them runs the original and the port behaves exactly as it did before any of this
existed; that is what makes 30 a safe default rather than a code path of its own.

Two consequences of the rate being live rather than fixed at startup: `Attach` does
not skip when pacing is off, since "uncapped" is a choice that can be taken back,
and `AfterDrawOTag` tests `Enabled` itself.

Two further shapes worth copying. The rate is a **double, not a vblank divisor** —
"arbitrary" was the point, and 144 is not 60/n — and the saved key changed with
it, from `kf2.framepacing.vblanks` to `kf2.framepacing.fps`. `SavedRate` reads the
old key once, converts (`n` → `60/n`, `0` → uncapped) and writes the new one, so an
existing config keeps the rate it had. And the settings page is presets **plus a
free number**: a player on a 165 Hz panel should be able to say 165, and the
slider only appears once Custom is chosen, so the common case stays one control.

## Frame pacing: the port is pinned to the fastest band

King's Field's game speed **is** its frame rate — everything advances a fixed
amount per loop iteration — and the loop always waits an integer number of
vblanks, so the achievable rates are quantised to 60/n. That quantisation is the
"banding" the game is known for: on NTSC hardware a frame costs 2, 3 or 4 vblanks
depending on scene load, so the game runs at 30, 20 or 15 fps and *plays* at
correspondingly different speeds. (PAL bands off 50 Hz instead — 25, 16.7, 12.5 —
which is where its ~17 fps ceiling comes from: more consistent, slower, and the
reason the PAL release feels different.)

Counting `VSync(0)` calls between consecutive `DrawOTag`s over a 30-minute
session, 49,570 rendered frames, is a direct measurement of which band each frame
landed in:

| vblanks charged | rate | frames | share |
|---|---|---|---|
| 1 | 60 fps | 6,960 | 14.0% |
| 2 | 30 fps | 42,460 | 85.7% |
| 3 | 20 fps | 30 | 0.1% |
| 4+ | ≤15 fps | ~120 | 0.2% |

**The port sits in the top band essentially always.** In an area it is 87% at
30 fps and 13% at 60 — the intro and title screens are ~99% at 60, because there
the loop only asks for one vblank. Nothing here is a throttle doing its job;
`DrawOTag` returns immediately on an HLE GPU and the MIPS is native code, so no
frame ever costs enough to fall into a slower band the way a real PlayStation
would under load.

**Read that last sentence again, because it is the whole trap.** The histogram
above is a measurement *of the port*, and the reason the 3-vblank band holds 0.1%
of its frames is precisely that the port cannot fall into it. It is not evidence
about the console; it is evidence that the port never does what the console did.

So the earlier framing of "twice as fast as the console" was too blunt. Precisely:
**in light scenes the port matches hardware's best case exactly, in heavy scenes
it is up to 2× faster because it never bands down, and for one frame in eight it
runs at 60 fps — twice the NTSC ceiling, which is faster than the game can go on
any console.**

### The reference band is 3 vblanks, not 2

**This overturns what the rest of this section originally concluded**, which was
that the reference speed is the top band, 30 fps, "which is both the design
ceiling and where the port already spends 87% of its frames". The second half of
that was circular, per the paragraph above. The first half confuses two things:

* **What the code asks for.** Two vblanks, the literal `2` at `0x800178A4`. That
  is a reading, it is confirmed, and it has not changed — see "The loop's own rate
  gate" in [GAME_INTERNALS.md](GAME_INTERNALS.md).
* **What the console delivered.** King's Field is heavy enough that the loop
  misses that deadline under load and the frame costs three vblanks. Since the
  game's speed *is* its frame rate, the band it actually lands in is the speed the
  game was played at — 20.

A port that makes the 2-vblank deadline on every frame therefore plays the whole
game **half again as fast** as the console did, not "the same as hardware's best
case". The reference is **20**, and it is now the default: `FramePacing.LogicHz`.

**No counter here can settle that**, and it should not be presented as though one
did. The port cannot observe hardware, and the histogram above is the shape of
evidence that looks like it can and does not. It is a judgement about the console,
so it is a *setting* — 30 is one combo entry away, under Video, and every
measurement below was taken at both.

**Step one is a floor, not a scale factor.** Enforce a minimum of two vblanks
(33.3 ms) per rendered frame and the port is a constant 30 fps: exactly NTSC's
fastest band, never above it, and without the banding that made the original's
speed wander. No game knowledge, no constants touched — and it is strictly more
consistent than hardware ever was. **Done**; see below.

### The floor, as built

`patches/FramePacing.cs`, attached as a post hook on `DrawOTag` in all three
overlays through `HookManager` at run time. Three things about the shape of it
are worth keeping:

**It is not a change to `FrameClock`.** The runtime's throttle paces per *`VSync`
call*, and from inside it a one-vblank frame is indistinguishable from the first
half of a two-vblank one — the information it needs (where the frame ends)
arrives at `DrawOTag`, which `FrameClock` never sees. The floor therefore keeps
its own deadline and lets the two clocks coexist: for a frame the floor extends,
`FrameClock`'s deadline simply falls behind real time and its wait collapses to
nothing (it resyncs on the `wait < -100` path every few frames), so the floor
ends up the sole pacer for exactly the frames that need one, and a no-op for the
frames that were already two vblanks long.

**It needed no RecompOne patch and no config entry.** `SymbolRegistry.Resolve`
turns `("game", 0x80060818)` into the emitted method and `HookManager.AddPost`
detours it, so the hook is installed at run time. It composes with the `replace`
that binds `LibGpu.DrawOTag`: `HookManager.Invoke` runs pres, then the
replacement, then posts.

The attach is deferred to the first `OverlayLoadedEvent`, because the dispatcher
tables `SymbolRegistry` reads are registered inside `Entry.Run` — after
`Program.cs` has run, but before anything is loaded, so the first load event is
the earliest moment every overlay resolves.

Note the namespace trap: generated code is `Recompiled.KingsField2`, a *class*
named after the project, which shadows any namespace called `KingsField2` — hence
`Kf2`.

**The vblank defines the frame boundary, not the `DrawOTag` call.** A second
ordering table with no `VSync` between it and the first belongs to the frame
already in flight; charging the floor per call would halve the rate of any screen
that draws more than one OT. King's Field draws exactly one (no zero-vblank gap
appears in any measurement), but the guard is free — the hook counts `VSync` calls
off a pre-hook on the libetc thunk and returns early when the count is zero. (It
counted *vblanks* until the fix described under "Any frame rate"; that is what made
every rate above 30 run the game fast.)

The deadline is absolute rather than `now + 33.3`, so a frame that overruns is
paid for out of the next one instead of the rate drifting down by the accumulated
jitter, with one frame of debt as the limit: past that the game has *stopped*
drawing (a disc read, a module swap) rather than run late, and the cadence
restarts instead of running flat out to catch up.

Three env vars, all read in `Program.cs`:

```bash
KF2_FPS=20          # 20 fps, the default; any number, or off for no floor
KF2_TICKRATE=20     # ticks a second the world runs at; 30 is the other answer
KF2_FPS_GATE=80037C0C+8002A550+80040348+80046A60+8004910C+80033FBC+8002DC78   # what is ticked
```

(That gate list *replaces* the default set rather than adding to it, and the rate
is an arbitrary double rather than a vblank divisor — see "Any frame rate" below,
which supersedes the two-stage, every-other-frame scheme this paragraph used to
describe.)

The band table above was measured by a `framestats` mod (since removed) — the same
count of `VSync(0)`s between `DrawOTag`s that fed it, without the gigabytes of
`KF2_LOG=sdk`. Bands were reported per window, so consecutive lines separated the
title screen from an area; restoring the mod is the way to check a pacing change.

### What the floor actually changed — and a correction

Measuring with `KF2_FPS=off` makes the deviation look different
from the aggregate above, and more specific. In an area the port is **already at
exactly 30 fps, asking for two vblanks on every single frame**, for minutes at a
time — then it flips into a burst where it asks for one:

```
floor off                                        floor on
30.0 fps  450 frames  2:100.0%                   30.0 fps  300 frames  2:100.0%
28.3 fps  424 frames  1:8.5%  2:91.5%            30.0 fps  300 frames  2:100.0%
30.0 fps  450 frames  2:100.0%                   30.0 fps  301 frames  2:100.0%
39.1 fps  587 frames  1:83.8% 2:14.0%  ...       30.0 fps  301 frames  2:100.0%
51.6 fps  775 frames  1:87.7% 2:11.0%  ...       30.0 fps  300 frames  2:100.0%
30.0 fps  451 frames  2:100.0%                   30.0 fps  300 frames  2:100.0%
```

So the one-vblank frames are **not spread evenly through play** the way "14% of
frames" implies — they cluster, and while a cluster lasts the whole game runs at
about 1.7× speed for half a minute at a stretch. That is a worse bug than a
uniform overspeed and an easier one to feel: the world lurches, then settles.

The floor removes them. Over 3,200 frames of the floor-on run **no report window
came out above 30.0 fps**, and eight consecutive in-area windows were 30.0 fps at
`2:100.0%` exactly. Windows *below* 30 stay below it — the title screen and the
intro are CD-bound at 7–15 fps with the floor on or off, which is right: a floor
is a ceiling on speed, not a promise of one; the music that ran half speed there
was the vblank domain ticking at picture rate — see "The vblank fired when the
game asked" in `docs/RUNTIME.md`.

Two things the measurement says that are worth keeping in mind for step two:

- **The game asks for two vblanks on its own almost all the time.** Whatever
  decides that count is not measuring host time; it is the game's own pacing
  logic, and the port's job is only to stop it going faster than the top band.
- **The band histogram is unchanged by the floor** in steady state (`2:100%` in
  both columns), which is the evidence that the floor is not fighting the game's
  loop or the runtime's `FrameClock` — it just absorbs the slack.

**Step two was 60 fps, and it turned out to be a different shape than this.**
What follows replaces the plan sketched here: the thing to remove is the game's
own frame gate rather than a vblank floor, and the world is held to a rate by a
clock rather than by halving deltas. **And the reference rate is not 30** — see
"The reference band is 3 vblanks, not 2" above. See below.

## Any frame rate: three gates, one logic clock, and a smoothed view

**Confirmed mechanism throughout; the picture has not been checked by eye.**

### The frame boundary was the vblank, and that made every rate above 30 run fast

Recorded first because it is the mistake the rest of this section was written
around. `FramePacing.AfterDrawOTag` opened with `if (_vblanks == 0) return;`,
counting `VSyncEvent` — which since `0021` is **the emulated vblank on a fixed
wall-clock 60 Hz grid**, not the game asking to present. That is once a frame only
while a frame lasts at least 16.7 ms. Above 60 rendered fps most frames reached
`DrawOTag` with no vblank elapsed and were thrown away: no `Floor()`, no
`AdvanceLogicClock()`, not counted in `Measured`. Two things followed.

* `_tickThisFrame` kept the **previous** frame's value, so the frame after a
  ticking one ran the gated stages again. With N frames per vblank the world ticked
  at `30 × N`.
* `Floor()` ran at most 60 times a second while advancing its deadline by
  `1000/target` each time, so above 60 it fell a frame behind on every call, reset,
  and stopped throttling. `FrameClock`'s permissive `2 × target` ceiling — a
  *ceiling*, never meant to pace anything — became the real limiter, which is why
  the rendered rate came out at exactly twice what was asked for.

Measured in an area, slot 2, before the fix:

| `KF2_FPS` | rendered | world ticks/s | death clock (65 ticks) |
|---|---|---|---|
| 30 | 30 | 30.0 | 2.13 s ✓ |
| 60 | **120** | **~59** | **1.10 s** |
| 120 | **240** | far above 60 | — |

The rate-independent form of the same "don't charge a second ordering table
twice" rule is **the ordering table drawn after the game asked to present**, so
the boundary is now the `VSync` *call*: a pre-hook on the libetc thunk
(`open 0x8001EB88`, `game 0x8005FCC8`, `end 0x8001B154`) counts calls and
`AfterDrawOTag` returns early only when the count is zero. Above 30 the game's own
frame gate is skipped, so a frame carries exactly one call — the presenter
`func_8002E0FC`, which does `VSync(0)` immediately before `DrawOTag`. At 30
nothing changes: the presenter's call still lands before the OT and the frame
gate's spin calls land after it. After the fix, every rate from 30 to 144 measures
**30.0-30.3 world ticks a second and a rendered rate equal to the number asked
for**.

`mods/framestats` carried the same guard and so reported the vblank-bearing subset
as its frame rate; it now uses the call boundary too, and a `0:` band in its
vblanks-per-frame histogram is the correct answer for a frame shorter than a
vblank.

### Three things held the port at 30, and only one of them was the game's

| # | gate | where |
|---|---|---|
| 1 | **the game's own frame gate**, `func_80017880` — spins on the vblank credit at `0x801B6CA8` until it reaches **2**, then zeroes it. Called by stage 13 as its last act. | `GAME.EXE`; see "The loop's own rate gate" in [GAME_INTERNALS.md](GAME_INTERNALS.md) |
| 2 | **`FrameClock`**, a hard-coded 60 Hz applied per `VSync` *call* inside `Runtime.PresentFrame` | `patches/recompone/0025` makes it settable |
| 3 | **`FramePacing.Floor()`**, the port's own deadline at the frame boundary | `patches/FramePacing.cs` |

Gate 1 is the one that was missing from the earlier write-up, and it is decisive:
since `0021` advances the emulated vblank on a wall-clock 60 Hz grid, that spin
paces the port to exactly 30 fps whatever the host does. **No rate above 30 is
reachable while it runs** — which means the odd/even stage gate the port shipped
before was skipping stages on frames that were still 33 ms apart.

`patches/FramePacing.cs` therefore hooks it with a `pre` that returns `false`,
writing `0` to `0x801B6CA8` itself because the function that would have zeroed it
did not run. Only `GAME.EXE`'s copy is hooked — `OPEN.EXE` and `END.EXE` link the
same routine at their own addresses, but the title and the ending are CD-bound at
7–15 fps and have no world to tick.

**That hook used to return `true` at 30 and below**, on the reasoning that there
the game paced itself exactly as it does on hardware and the port added nothing.
That stopped being true the moment the tick rate became a number of its own: the
gate does not only pace, it also decides how often the world advances, and it
knows one answer for both — 30. Left running at the 20 fps default it would pin
the world back to 30 Hz and make `LogicHz` a lie exactly where it matters most.
So **it is now skipped at every rate**, and two consequences follow:

* **The port's most-tested configuration is gone.** There is no longer a setting
  at which none of this class does anything; `Floor()` is the pacer always.
* **A rendered frame carries exactly one `VSync` call at every rate** — the
  presenter's — because the gate's spin calls were the second one. The frame
  boundary is therefore the same shape at 20 fps as at 144, which is the simpler
  of the two cases it used to have to handle. `mods/framestats` reading `2` on
  calls/frame now means the gate is back.

`0x801B6CAC` and the vblank callback `func_80017850` are untouched: only the spin
is skipped, so the CD timeout riding on that counter is unaffected.

`LibEtc`'s vblank grid is deliberately **left at 60 Hz**. The music sequencer, the
root-counter events and the game's own `0x801B6CA8` all hang off it, so raising it
would speed the audio up; present rate and vblank rate are independent, which is
exactly what `0021` set up. `FrameClock` is handed a permissive ceiling
(`2 × target`, never below 60) rather than the target, because it paces per
`VSync` call and a frame can carry more than one — a per-call throttle cannot
express a frame rate. The floor, which sits at `DrawOTag` and therefore knows
where a frame ends, is the only real pacer.

### The logic clock

The loop runs at whatever the floor allows, so the world would advance a fixed
amount that many times a second. A wall-clock accumulator ticks at `LogicHz` — 20
by default, 30 the other way — and the main-loop stages holding per-tick state run
only on a frame where it ticked. It is advanced by elapsed *time*, not by
`1/target` per frame, so a host that misses the target runs the world at `LogicHz`
anyway — the difference between "the picture stutters" and "the game plays in slow
motion".

**It runs in every configuration now**, not only above the tick rate, because
skipping the game's gate left it as the only thing holding the world down:

* At the render rate *equal* to the tick rate it ticks on every frame and nothing
  is skipped, because `Floor()` guarantees a frame is at least `1000/LogicHz` ms
  and the credit always reaches 1. Measured at `KF2_FPS=20`: 20.00 ticks/s.
* **Uncapped no longer runs the game fast.** `KF2_FPS=off` draws flat out (60,
  held there by `FrameClock`'s own ceiling) and still measures 19.99 ticks/s. The
  settings label said "runs too fast" and no longer should.
* Below the tick rate the world cannot catch up: a stage can be skipped but never
  run twice, so it ticks once per frame and the whole game plays slow. Measured at
  `KF2_FPS=20 KF2_TICKRATE=30`: 20.00 ticks/s, not 30. That is a diagnostic
  configuration, and the settings note says so.

Five things are gated:

```
3  func_8002A550   pad read, turn, walk, angle fold, the death counter at
                   0x8019951A, the poison tick, the buff timers at
                   0x80199472..0x80199482, and 0x80199488, the global frame
                   counter it bumps last
4  func_80040348   the 200-record entity table at 0x8016C544. Its AI runs one
                   entity in four off 0x80175908 & 3, which stays right for free
                   once the stage itself is gated
5  func_80046A60   128 effect/projectile slots at 0x8019CC6C, each with a lifetime
                   at rec+0x0E decremented once per call. Ungated, every spell and
                   effect expires at the render rate
6  func_8004910C   the area module's own per-frame entry -- slot 1 of the module
                   header, reached as *(u32*)(*(u32*)0x8017E068 + 4)
13 func_80033FBC   the fade state machine stage 13 calls, not stage 13 itself
```

`KF2_FPS_GATE` replaces that set, and `KF2_TICKRATE` sets the rate they run at.

**"Can it draw" is the test each of them had to pass**, and it is the reason the
list is what it is rather than "everything with a counter in it". A stage that
submits primitives cannot be skipped — at 120 fps three frames in four would be
missing whatever it drew. The check is static, against the emitted C#: walk the
function's call subtree and look for `DrawOTag`, `VSync`, `PutDispEnv` or
`PutDrawEnv`. What that found:

* **Stage 6** dispatches indirectly, so the target has to be read out of the
  module image rather than the call graph: slot 1 of the 32-word header, which is
  `FDAT.T` at the overlay's own `offset`. Six of the nine modules leave it an empty
  `jr $ra` (`fdat02`, `05`, `08`, `17`, `23`, `32`); `fdat11`, `fdat14` and
  `fdat20` use it for proximity and trigger logic — `fdat11`'s reads the player's
  position, calls the angle helpers `func_80015394`/`func_80015328` and writes
  state bytes. **No SDK entry point in any of the nine subtrees**, so it is gated.
* **The fade**, `func_80033FBC`, is a three-function subtree that cannot draw,
  which is why it can be gated even though its caller is the renderer. It is a
  four-state machine on the byte at `0x80192D42`: brightness `0x80192D44` steps
  `+0x14` a call until it reaches `0x64`, a hold counter at `0x80192D43` counts
  down, then brightness steps `-0x14` (written as `+0xEC`) back to zero. Ungated
  that whole sequence ran at the render rate and an area fade was four times
  quicker at 120 fps than on hardware.

**Stage 2 — the world's moving props, and why "can it draw" nearly kept it out.**
Doors, the drawbridge, the minecart and the spinning crystals all live in
`func_80037C0C`, main-loop stage 2, and until this was gated they moved at the
render rate. It walks the **object table at `0x80177714`** — 396 slots of `0x44`,
a slot free when the type byte at `rec+0x4` is `0xFF` — publishing each record to
`0x8017E04C` and its `0x18`-stride definition (indexed by the `u16` at `rec+0x6`,
in the table at `0x80175914`) to `0x8017E048`, then dispatching on that same type
byte through a **224-entry jump table at `0x8001191C`**, collapsing to thirty
distinct arms plus an indirect arm into the area module's own handler. Counting
its writes through the record base gives the census's three fields exactly:
`rec+0x18` (the position VECTOR's Y lane, 7 sites), `rec+0x24` (6) and `rec+0x40`
(20), alongside `rec+0x14`/`rec+0x1C` for X and Z and the state word at
`rec+0x08` — 43 sites, the busiest field in the function and the door/lift state
machine itself.

It was excluded for years on the reading that "all four SDK entry points are in
its 268-function subtree, so it presents". That is true of *reachability* and
false of what the rule protects against. Enumerate every function in the subtree
that calls a submitting or presenting entry point **directly** and there is
exactly one, reached by exactly one edge:

    func_80037C0C -> func_80037B5C -> func_800342D8 -> func_8002E0FC -> DrawOTag

`func_80037B5C` is a self-driving fade loop — it steps a tint from `a1` to `a2` by
`a3` and calls the tint drawer, stages 11, 12, 8 and **stage 13 itself** for each
step. It is entered from one arm of the state machine (an in-bounds trigger), and
the indirect area-module arms get to the renderer the same way, through the
message-box and cutscene loops `func_80047000`, `func_80048208` and
`func_8004831C`. Every one of those is an **extra** render inside the stage, never
the frame's own: the main loop still runs stage 13 afterwards. That is the same
exception stage 3 already carried, and a strictly narrower one — stage 3 has nine
such paths including a whole blocking menu session. So stage 2 is gated, with the
reason recorded in `check_gate.py`'s `KNOWN`, and **the cost is that entering a
fade or a cutscene can be deferred by up to one tick (50 ms)**. Skipping cannot
cut one in half: once entered, the stage is on the stack and drives its own
frames.

Measured with `rate_census.py --run --scenario idle --fps 20 144`, standing still
in area 1, the four records that move:

| field | before (20 -> 144) | after (20 -> 144) |
|---|---|---|
| object table `+0x18` | 13.0/s -> 49.8/s, ratio 3.84 | 13.7/s -> 17.7/s, ratio 1.29 |
| object table `+0x24` | 13.0/s -> 52.4/s, ratio 4.05 | 13.7/s -> 17.7/s, ratio 1.29 |

For scale, in the same pair of runs the already-gated animated-texture phase reads
16.8/s -> 18.4/s (ratio 1.10) and the global frame counter `0x80199488` reads
15.5/s -> 18.1/s (ratio 1.17). The props now alias the way the things that were
already right do. **The picture has not been looked at** — whether a fade or a
cutscene now starts visibly late, and whether a 20 Hz prop against a 144 fps
picture wants `KF2_SMOOTH_OBJECTS=1`, are both eye questions.

**What this still does not cover, recorded rather than discovered later.**

* **`rec+0x40` on two other slots is a different defect and is still open.** It
  survived the gate at ratio 3.69 (14.4/s -> 53.4/s), and gating `func_800331B4`
  as an experiment dropped it to 17.6/s, which is the attribution: the writer is
  **stage 13's own object pass**, not stage 2. Reading it, `rec+0x40` is a
  retrigger deadline in **vblank** units against the free-running count at
  `0x801B6CAC`, re-armed as `vbl + 6 * (u16 at rec+0x3E)`, and when it expires the
  routine computes a distance-attenuated volume against the player position at
  `0x801994EC` and calls the sound player `func_80014158`. So it is a per-object
  **ambient sound** — and with a small interval it retriggers once per rendered
  frame up to the 60 Hz vblank ceiling, i.e. 20/s at the default and 60/s at 144.
  It cannot be gated: `func_800331B4` walks the table and draws the models in the
  same loop, so no whole-function hook separates the two. **Not heard by ear**;
  the numbers above are all that is known.

**Animated textures — the sixth gated function, `func_8002DC78`.** The animated
water at the start, the main-hall fire and the creatures' scrolling skins are one
system: **eight slots at `0x80192D58`** (stride `0x18`), each holding a scroll
phase at `rec+0x4`, a per-slot advance rate at `rec+0x3`, a wrap at `rec+0xC`, and
a source-texture pointer at `rec+0xE`. `func_8002DC78` walks all eight every call —
it advances each phase, then re-uploads the scrolled region of the source texture
to VRAM through `func_80060624` (a `LoadImage`-style GPU transfer). It is called
once, straight from **stage 13** (the renderer, `func_800342D8`, at `0x800346..`),
so ungated it ran once per *rendered* frame: the scroll advanced at the render
rate — measured `rec+0x4` changing on **100 % of frames at 120 fps**, six times too
fast against the 20 Hz world (and the reported "runs high with the framerate":
faster the higher the frame rate). Its whole subtree is the VRAM upload — no
`DrawOTag`/`VSync`/`PutDispEnv`/`PutDrawEnv` — so it passes the same "can it draw"
test as the fade stepper `func_80033FBC` and is added to `FramePacing.DefaultGate`
alongside it. On a non-tick frame it is skipped: the phase holds and VRAM keeps the
last tick's frame, so the texture animates at the tick rate whatever the port
draws (measured `rec+0x4` back to **17 % of frames at 120 fps**, i.e. 20 Hz). No
setting and no new patch — it is one more address in the gate.
* **The jitter accumulator at `0x8006E608`** is in stage 13's *own body*
  (`func_800342D8`), not in a callee, so no hook can reach it — `HookManager` only
  detours whole functions and stage 13 must draw. It sums `func_80015374()` and
  decays by an eighth a call to drive the screen shake, so above the tick rate the
  shake settles faster and smaller. A quirk of amplitude, not a timer.

### The view has to be carried between ticks

A camera moving at the tick rate but presented several times as often is not
merely no better than drawing at the tick rate, it is worse: the picture updates
and the view does not. **Dropping the tick to 20 makes this more load-bearing, not
less** — a tick is now 50 ms rather than 33, so the camera stands still for half
again as long between them. `patches/FrameSmoothing.cs`
is one `pre`/`post` pair around **stage 8** (`func_80025A1C`), which is the whole
of "build the render camera from the player state" and the only thing between that
state and the picture — so the carried view lives for exactly one function call
and cannot accumulate, cannot reach the collision code and cannot reach a save.

**It interpolates, it does not extrapolate — and that is what stopped the bounce.**
The first version carried the view *forward* by last tick's velocity (`angle +
turnVel × frac`). That is smooth only while the velocity holds, and King's Field
damps a turn and stops dead at a wall, so the next tick's real angle was routinely
*less* than the one predicted and the view snapped back to it — reported as *"the
camera bounces back to a position it would have travelled in 20 Hz"*, every time a
turn eased off. It now keeps the view the game produced at the previous tick and at
this one and draws `lerp(prev, cur, frac)`, which can never reach a position the
game did not produce, so nothing overshoots and nothing snaps. The cost is a tick
of latency — the picture trails input by up to 50 ms — but the input is sampled at
the tick rate anyway, so that is a delay of the *display*, not of the response.

* **Yaw and pitch** are interpolated between the composed view angles at
  `0x80199504` / `0x80199506`, re-sampled on every frame the world advanced on.
  `frac` is `FramePacing.LogicPhase`, continuous across a tick boundary, so the
  camera does not jump on the frames where the world did advance. Yaw is 12-bit and
  wraps, so the interpolation takes the shortest way round.
* **Position** is interpolated between the player position at `0x801994EC`/`F0`/`F4`
  that `func_80028080` writes after the collision test, so walking a wall
  interpolates between two positions that already slid along it — no overshoot into
  it, and no snap back. A step past 1024 units on an axis is a warp rather than a
  walk and is left alone, the way `ObjectSmoothing` guards a placement.

**Both default to off** — a house rule, not a doubt about the mechanism. The
boundary bug that once pinned `LogicPhase` to 0 (a counted boundary was a whole tick
wide, so the credit went `0 → 1 → tick → 0` and never sat between) is long fixed;
the probe now reads e.g. `241/241 frames carried, mean phase 0.50 tick, yaw 17.7 u`
at 120 fps. The picture was checked by eye after the switch to interpolation and
reported *"incredible"*; the default stays off until that judgement is settled for
shipping.

### The camera is not the only thing that moves

Reported after the tick rate became a setting, playing at 60 fps against the 20 Hz
world with frame smoothing on: *"the enemies move at the correct speed, but they
are animated at a visibly lower framerate, while the HUD renders at 60 — and the
player's arm renders at 20 as well."* Every part of that is the design working as
built, and two thirds of it are fixable.

Smoothing the camera covers more of the picture than it sounds like it does,
because most of the picture is architecture that never moves: reproject a static
wall through a camera that moved and it is smooth. The HUD is rebuilt by stage 13
every rendered frame — its digits and gauge widths from the live HP/MP, its
orientation from the camera — so it is exempt by construction. What is left over is anything carrying a position of its own —
which still advances once a tick, and which now steps against a world sliding
smoothly past it. That contrast makes the step **more** visible than it is with
nothing smoothed at all, which is why this is the other half of frame smoothing
rather than an extra beside it.

`patches/ObjectSmoothing.cs` (`KF2_SMOOTH_OBJECTS=1`) is the same shape as
`FrameSmoothing` — a `pre` that writes, a `post` that puts back exactly what was
there — around **stage 13** (`func_800342D8`) rather than stage 8, because it is
the renderer that reads these and not the camera builder. Stage 13 writes nothing
but the display list, so the interpolated positions are gone before the next tick's
AI, a save or a proximity trigger can see them.

**There are two tables, because the renderer walks two.** `func_800331B4` loops
the **entity table** `0x8016C544` (200 records of `0x7C`, free at `+0x0`, position
a `VECTOR` at `+0x2C`, rotation three `s16` at `+0x40`) for **creatures/enemies**,
then loops the **object table** `0x80177714` (396 slots of `0x44`, `VECTOR` at
`+0x14`, free at `+0x4`) for static props and sprites — both the constants
`patches/AgentServer.cs` already reads for `nearby` (`entities` and `objects`).

For a while this carried only the object table, on the belief — from a
`KF2_DRAWCENSUS=2` reading of `func_80032588`'s `a2` — that "the renderer reads the
object table and not the entity record." That reading was taken with props on
screen and **no creatures near**, so it saw only the second loop. The entity record
is a copy *and* a source: stage 4 copies the object position into `rec+0x2C`, but
the first loop then draws creatures from that copy, plus its own rotation at
`rec+0x40`. Smoothing the object table alone therefore left every enemy
stepping in **both** position and facing — the reported jitter — so this carries
**both** tables, and the entity rotation on top. See "What in the renderer draws
what" in [GAME_INTERNALS.md](GAME_INTERNALS.md) for how that was established.

**It carries four tables now, not two, and it is a list rather than four copies of
the code.** The renderer draws from four (the inventory is in "What in the
renderer draws what" in [GAME_INTERNALS.md](GAME_INTERNALS.md)); this carried two,
and *both* omissions reached a player as "the animation runs at a low frame rate"
before anyone read `func_800331B4` to the end. The two that were missing are
**stage 5's table at `0x8019CC6C`** (128 x `0x48`, position `+0x14`, rotation
`+0x24` — the same layout as the object table, and gated, so it stepped at the
tick rate with nothing carrying it) and the **billboard sprites at `0x80195174`**
(128 x `0x18`, position `+0x8`, no rotation — already render-rate, so carrying
them is a no-op and harmless). `ObjectSmoothing.Tables` is now a `TableSpec[]` and
one routine walks it, so the next table is a row.

**The object table's emptiness test was the owning stage's, not the renderer's.**
Stage 2 steps a slot when the byte at `+0x4` is not `0xFF`; `func_800331B4` draws
it when the `u16` at `+0x6` is not `0xFF`. This used stage 2's, so any slot that
is drawn but not stepped by stage 2 was treated as free and never carried. What is
being interpolated is what is *drawn*, so the drawing test is the right one — and
a slot carried but not drawn costs a write and a restore and nothing else.

**The object table has a rotation lane too, and missing it left doors animating at
20 Hz.** This section used to say the object table had no rotation at all, on the
strength of the same draw census reading only `a2`. It has one — three `s16` at
`rec+0x24`/`+0x26`/`+0x28`, with the identical `0x800` yaw bias, built into the
`a3` triple by the object loop of `func_800331B4`. Carrying position alone left
anything that *turns* stepping at the tick rate against a position that glided,
which is what a door closing looks like at a low frame rate while its speed is
right. It is now carried the same way the entity lanes are: shortest way round a
4096-unit turn, the bits above the mask preserved, and restored in the post-hook.
Two things about it differ from the position path on purpose — **rotation is not a
rider on position**, so a slot whose origin never moves (a door swinging on its
hinge) is still carried, where the old `dx==dy==dz==0` skip dropped it; and **the
1024-unit placement guard is the position's alone**, since `DeltaAngle` already
takes the short way round, so a re-placed facing costs at worst half a turn of
sweep and cannot veto the slot. Measured at 144 fps, standing in a quiet area:
`289/289 frames carried, 4.0 object(s) each, 4.0 turning, biggest angle step 128 u`
and no `LEAKED` — 128 is `0x80`, exactly what the arm at `0x80038BA8` adds to
`rec+0x26` each tick, i.e. 1/32 of a turn. **Not looked at by eye.**

The entity rotation is interpolated the shortest way round a **4096-unit turn** (the
`0x800` yaw bias the renderer adds is half of it). The raw lanes are not confined to
one turn — the probe measures signed/accumulated values above `0xFFF` and just under
`0x10000` — so the carry works modulo 4096, the part the GTE reads, and leaves the
bits above it untouched. Measured at 60 fps in an area: `241/241 frames carried, 3.0
creature(s) each`, no leak, alongside the object pass.

Two things about it are worth stating:

* **It interpolates, on the same clock as the view — and the clock is the whole
  point.** It interpolated, was switched to extrapolating, and now interpolates
  again. Interpolating was right on its own terms — nothing in this table is steered
  by the player, so a tick of latency is free, and walking between two positions the
  game produced cannot overshoot. It was abandoned only because the *camera* then
  extrapolated: `FrameSmoothing` drew the view forward to `t + frac` while
  interpolating drew an object at `t - 1 + frac`, a whole tick apart — 50 ms at the
  default rate — and that constant offset did not read as latency, it read as **the
  objects moving more slowly than everything else** (*"the enemies still move
  visibly slower than the compass"*). The camera now interpolates too, so the two
  are back at the same instant, and interpolation is the better tool wherever it can
  be afforded: no bounce-back on a stop or a turn, which is exactly what forward
  extrapolation gave. **Two smoothers must agree about what time it is** — that is
  the rule worth carrying to whatever gets smoothed next.
* **A step over 1024 units on any axis is a placement, and is left alone.**
  Without it an object spawned, respawned, moved by a script, or simply placed
  when the area finished loading is swept a whole area's width over one tick. (It
  is needed for interpolation just as it was for extrapolation: prev and cur can
  straddle a placement either way.) The threshold comes from measurement, not
  taste: real motion peaked at **37 units a tick**, the player — the fastest thing
  in the game — covers 1817 units in 2 s at 20 Hz and so about **45 a tick**, and
  one window caught a **233,472-unit** step at area load. 1024 sits twenty times
  above anything that walks and two hundred times below the placement it has to
  catch.
* **It was raised to 8192 and made sticky, and that was reverted. The reasoning is
  worth keeping.** Play reported the final boss and the piranhas freaking out
  during an attack, and the argument was that a part whose step sits near the
  threshold is carried on the tick it comes in under and held on the tick it goes
  over, gliding and stopping twenty times a second while the parts either side of
  it keep gliding — the creature tearing itself apart. A bare threshold being a
  cliff, the decision was also made sticky, a slot already carried getting
  `GlidingFactor` × the threshold before it is called a placement.

  **The mechanism was impossible.** A creature is *one record* in the entity table
  — one position, one rotation triple (`0x8016C544`, 200 × `0x7C`, position
  `+0x2C`, rotation `+0x40`) — so the finest thing this patch can act on is the
  whole creature. It can make a boss judder as a body; it cannot shear one limb
  against another, and a limb-relative defect is the MO pose, which is
  `AnimSmoothing`'s clip clock. The boss was never confirmed to improve at 8192,
  so nothing verified was gained.

  **And the raise broke projectiles.** Play reported fireballs stuttering and
  appearing where they had not been, at anything above 1024. The effects table
  (`0x8019CC6C`, 128 × `0x48`) recycles its slots constantly, and a slot freed and
  refilled inside one tick is never observed free: `Prev` holds the dead
  projectile and `Cur` the new one. 1024 refuses that delta as a placement; 8192
  carries it, and the new fireball is drawn sliding in from wherever the old one
  died. Stickiness makes it worse again, since a recycled slot inherits the
  previous occupant's `Gliding` along with its position. That is a **wrong**
  position, not a rough one, so an unverified fix bought a verified regression and
  the default went back.
* **The threshold is per table, and that is the compromise.** Play, with the guard
  switchable: on `strict` the boss no longer freaks out and projectiles are
  perfect, but a boss moving fast still looks as though *its head snaps into the
  next frame of the animation*; on the raised modes the animation gains visible
  in-between frames and projectiles break. Both halves have the same cause. A
  creature is drawn from **two** smoothers — its root from `ObjectSmoothing`, its
  pose from `AnimSmoothing` — and when the guard refuses a fast creature's
  position, the root steps at the tick rate while the vertices go on morphing at
  the frame rate. The pose then slides ahead of the body, which is what the head
  snapping is; the raised modes "add in-between frames" mainly by giving the pose
  a root that moves with it. Meanwhile the projectile tables recycle slots
  constantly and are wrong at anything above 1024. The tables want opposite
  answers, so `TableSpec.Fast` scopes the raise to the **entity table** and every
  other table keeps 1024 whatever the mode is set to.
* **When a root is held, the pose is held with it.** Scoping is not enough on its
  own: a creature past even the raised cap brings the head snapping straight back.
  `ObjectSmoothing` publishes the position addresses it refused this tick and
  `AnimSmoothing` refuses those slots' pose carries for the same tick, so such a
  creature degrades to a coherent tick-rate creature rather than an incoherent
  smooth one. The two patches need no knowledge of each other's tables to do it:
  `func_80032588`'s `a2` — what `AnimSmoothing` already keys its per-creature
  state on — *is* `base + slot*stride + PosOff`, the address the carry writes
  through. The coupling is inert when `ObjectSmoothing` is off, since nothing is
  carrying any root then and holding every pose would disable pose smoothing
  rather than keep two smoothers in step. `KF2_SMOOTH_ANIM_PROBE=1` counts it as
  `refused (root held)`.
* **The default is `continuous`**, which is the mode play reported as visibly
  smoothest on a creature. What made the raise unshippable was projectiles, and
  the raise no longer reaches them. `sticky` is the more conservative choice if a
  creature is ever seen sliding in on spawn.
* **The lasting finding is that this threshold was doing two jobs.** Rejecting
  slot reuse and teleports wants it tight; admitting fast honest motion wants it
  loose. One global constant cannot serve both — the boss argument and the
  fireball report are the two ends of exactly that — and both numbers were derived
  in a quiet corridor from things that walk, which never bounded either job. The
  principled fix is to take the first job away from the threshold entirely by
  keying the sample on the slot's **identity** (the object table's definition
  index at `+0x6`, the entity table's byte at `+0x0`) and dropping the previous
  sample whenever it changes, so reuse is caught regardless of speed. Failing
  that, the threshold belongs per `TableSpec` rather than global: creatures walk,
  projectiles do not.
* **The symptom does not reproduce at 1024 any more**, which closes the argument.
  1024 is the value that was in place when the boss was first reported spazzing,
  so if the guard were the variable the revert would have brought it back. It did
  not. The only other behavioural change since that report is `AnimSmoothing`'s
  segment overrun becoming a **refusal for the whole tick** rather than a clamp to
  the segment's start — which is the pose, the layer that can actually move one
  limb against another. Not yet confirmed by stashing that change and re-running
  the boss, but everything else in between is documentation or the per-tick
  decision below, which under `strict` is a provable no-op.
* **The guard is a setting**, since the comparison is worth being able to make by
  eye: Video ▸ Enhancements ▸
  Placement guard, or `KF2_SMOOTH_OBJECTS_GUARD=strict|sticky|continuous`.
  `strict` is 1024 on every table. `sticky` is the raise described above,
  creatures only.
  `continuous` additionally raises the cap on a slot that merely *moved* last tick
  rather than one that was *carried* — sticky's hysteresis is one-way, so a slot
  can only become sticky by first passing the bare threshold — which makes it the
  widest of the three and the worst for slot reuse. Switching mid-session drops
  the per-slot hysteresis so the new rule does not inherit the old one's
  decisions.
* **The carry decision is made once per tick, not once per frame.** The deltas are
  tick-constant so the answer does not change within a tick — but the hysteresis
  reads the same per-slot state the decision writes, so re-deriving it on the
  second frame of a tick would judge that frame against what the first frame had
  just stored. Under `continuous` that is a real divergence: frame 1 of a fast
  step refuses and sets `MovedLast`, and frame 2 would then find the raised cap
  and carry, moving the object in the middle of a tick.

Measured at 60 fps against the 20 Hz world, standing in an area: `121/121 frames
carried, 3.0 objects each, mean phase 0.40 tick, offset 14 u, biggest tick step 37
u`, with the probe's own leak check — re-read every touched slot after the renderer
and compare it with what the pre-hook wrote — reporting nothing. The **death clock
is untouched**, which is the check that matters: 65 ticks in **3219 ms** against
the 3250 ms a 20 Hz world owes. Cost, at a 1000 fps cap so there is something to
see: 550–615 fps either way, the run-to-run spread wider than the difference.

**Off by default**, the house rule for a mechanism that has been measured and whose
picture has not been looked at.

#### The player's arm is the same bug after all

**This section used to say the opposite, and the correction is the finding.** It
read: *"It is 2D, drawn by the HUD builder `func_80031D5C` out of the fourteen-entry
table at `0x80067774` … what steps is its sprite index … Interpolating between two
authored sprites is not a thing."* Every clause of that is wrong except "welded to
the screen".

The arm is drawn by **`func_80032400`**, a fourth drawing callee of stage 13 that
the census labelled "early 2D" and nobody opened. It is a **3D MO-animated mesh**
posed by the same clip clock as every creature: `func_80034DA8(0x8019949C, 0x20,
u8[0x801994AE], (s16)u16[0x801994A4], …)`, which forwards the clip byte and the
clip time to `func_8003486C` exactly as `func_80032588` does. The full layout is in
"What in the renderer draws what" in [GAME_INTERNALS.md](GAME_INTERNALS.md).

**Why the original measurement pointed at the wrong routine** is worth keeping,
because the method was sound and the reading was not. `func_80032400` returns
before drawing anything while the swing clock reads `-1`, so a census taken
standing in an area sees it draw nothing, and a thirteen-tick swing inside a
two-second averaging window barely moves it. The row that *did* move on pressing
attack — the HUD builder's — moved **downward**, 56.9 → 54.0 → 52.7 packets, which
is the wrong direction for an arm appearing: attacking collapses the HP/MP gauges,
and those are entries 9 and 10 of the HUD table. A difference was taken, the only
row that moved was believed, and the direction was not checked.

So `AnimSmoothing` grew a **second front-end** rather than a second patch — see
"The arm rides the same clock" below.

A creature's **pose** is an MO mesh morph, not a second copy of object
motion. `ObjectSmoothing` already carries origin and Euler on all four
tables the renderer walks. The clip byte and the clip time arrive at
`func_80032588` as its **eighth and ninth stack words** (`caller SP+0x1C` and
`+0x20`) — all five of `func_800331B4`'s call sites fill them, from four
tables with four different strides, which is why the argument list and not a
table offset is the right description. Below `0x80`, `func_80034DA8` applies
packed vertex deltas (the MO format) into `0x80190AD8`; `func_8003486C` turns
the integer time into a segment and a 12.12 weight.

Stage 13 rebuilds that morph every frame — `L80034FCC` re-runs the decoder
even when the segment has not changed — so the only thing stuck on the tick is
the time. `patches/AnimSmoothing.cs` (`KF2_SMOOTH_ANIM=1`) interpolates last
tick's time with this one, hands `floor(t)` to `3486C` so the segment pick
stays right, and adds the fraction onto the weight `34A74` already consumes —
**subtracting it on a segment whose flag word is set**, because `3486C`
publishes `0x1000 - raw` there and the weight runs backwards. It writes no
game state at all: one register on one call, and the caller's own stack temp.

The guard on the clip-time step is on its **size and not its sign**, and that
cost one bug each way: a magnitude of 32 against a real 511 made the first
version carry nothing at all, and treating a negative step as a discontinuity
left every clip played in reverse stepping at the tick rate — the drawbridge
lever went up in 50 ms jumps while the same lever came down smoothly. A clip
swap is caught by the clip byte; only the size separates playback from a
re-seek, in either direction.

**The end of a cycle is neither, and it was a third bug.** A looping clip runs
its time up and then resets it *keeping the same clip byte*, so the wrap arrives
as one ordinary tick whose step is a whole cycle backwards — and interpolating
that plays the animation in reverse, at cycle-per-tick speed, across every frame
of that tick. That is the rewind reported as "the animation snaps back to its
first position at the end of the cycle". The size guard cannot be what catches
it: a cycle shorter than 4096 slips under it, and one longer only turns the
rewind into a hard cut.

What separates a wrap from a re-seek is **where the time landed**, not how far it
moved. A loop turning over lands within one tick's advance of the cycle's first
frame — the overshoot past the end is what the new time is made of — while a
ping-pong clip easing back through its own last frames lands where it already
was, and a re-seek lands anywhere. So the test is `sign(step) != sign(lastStep)`
**and** `cur <= |lastStep|` (mirrored for a clip played in reverse, which turns
over at the tail).

Having recognised it, the tick is then run *forwards* through the turnover
instead of being skipped: the clip advances about `lastStep` a tick and the part
of that already spent in the new cycle is the new time itself, so the cycle
turned over `1 - cur/lastStep` of the way through the tick. Before that fraction
the old cycle is still finishing, after it the new one is running. **No clip
length is needed**, which is as well — the only place the total duration exists
is the segment table `func_8003486C` walks — and overshooting the real end by up
to a frame is harmless, because past its last segment that clock answers with
that segment at a full `0x1000` weight, which is the pose the clip ends on
anyway. A wrap's step is a cycle length rather than a rate and is deliberately
not recorded as `lastStep`, or the *next* wrap would be unrecognisable.

**The turnover is synthesised, not measured, and that has a cost.** It is made
out of `lastStep`, so a `lastStep` that is not a real playback rate invents one
wrongly — and a clip whose time is merely *jittering* near its own head reads as
wrapping every other tick, with the invented pose sweeping a fraction of the clip
and then cutting to the head. That is worse than the hard cut it replaces, so a
wrap is only believed off two consecutive same-direction ticks (`WrapRun`); with
less than that behind it the tick is held at the game's own time, which is the
hard cut.

**A clip being fought over is not animating.** The remaining case is an attack
whose animation the AI restarts every tick because the conditions to finish it
are never met — a piranha, or the final boss with the player under its head. Its
time steps one way and back the next, never landing at a cycle boundary, so it is
neither playback nor a wrap; interpolating it sweeps the pose *continuously*
between the two instead of alternating between them, which reads as a violent
shake where the console showed a 20 Hz flicker. Three reversals net of steady
playback (`ThrashFlips`) and the slot is held at the game's own time until it
resolves. That is not a repair of the game's own indecision — it is a refusal to
draw it more often than the game makes it. A ping-pong clip flips once a
half-cycle and decays back long before it gets there.

`KF2_SMOOTH_ANIM_PROBE=1` reports `N cycle wrap(s) (longest time seen T), M
carried through, R re-seek(s), S stuck` in these two modes, which is also how to
tell whether a clip is looping — or thrashing — through this path at all. In
`Mode.Timeline` the same line reports the new predicate instead: `N playback (W
on the wrap, T turned, B in reverse), H held (no match), S settling, L with no
clip length`.

#### The clip is a timeline, and none of that knew how long it was

Everything above — the landing-site wrap test, the synthesised turnover, the
`WrapRun` settling count, the `ThrashFlips` hold, the 4096 magnitude cutoff, and
the bounded mode below with its whole-tick overrun refusal — is a repair of one
missing fact. The patch was lerping the integer clip time as if it were a
Euclidean scalar on an unbounded line. It is not: it is a point on a **circle**,
and the circumference is the clip's own length.

**That length is not missing.** It is the sum of the per-segment durations in the
very table `func_8003486C` walks, and the whole record is reachable from the
`bank` and `clip` the clock is called with — clip table at `bank + u32[bank +
0x10]`, record at `bank + u32[clipTable + clip*4]`, `u16` segment count, then
`bank`-relative `u32` pointers to records whose `u16` at `+0x2` is the duration.
See "The model pipeline has no skeleton" in `docs/GAME_INTERNALS.md` for the
layout. Measured live, every clip reached in areas 0, 2 and 7 is **4096** long,
which the legacy probe confirms from the other side: the highest clip time it
ever saw on one is 4095, and its "cycle wrap" steps are 3936, 3942 and 4032
against playback rates of 160, 154 and 64 — `rate - 4096` exactly.

`Mode.Timeline` is that, and it is one predicate where there were five. Playback
is constant velocity along the circle, including through 0. So each tick:

1. **the clip byte changed** — a hard cut, show the game's pose, start again;
2. **else unwrap the step against the settled rate.** The observed `cur` is
   consistent with any step `cur + kD - prev`, and `k` is not searched for, it is
   `round((rate - step) / D)` — the wrap count that puts the candidate nearest
   the rate. Two more candidates cover a clip that **reflects** at an endpoint
   rather than wrapping: a turn at 0 lands at `-raw`, a turn at the end at
   `2D - raw`. The nearest candidate wins if it is within half the rate of it.
   That single test *is* ordinary playback, the cycle wrap, the reverse clip and
   the ping-pong turn;
3. **nothing matched** — a re-seek, or an attack the AI is restarting every tick
   — so hold at the game's own time, and re-seed the rate from what was actually
   seen so the next tick has something to confirm against.

A carried tick interpolates along the path it just recognised,
`raw = prev + delta * phase`, folds it back onto the clip (modulo for a wrap, a
triangle fold for a turn), hands `floor(t)` to `func_8003486C` and spends the
leftover fraction on the 12.12 weight. **The fraction is under one clip unit, so
it cannot leave the segment `floor(t)` landed in** — the overrun refusal the
bounded mode needs has nothing left to refuse, and the clamp on the weight is
only rounding at the very top of a segment.

Two things it gives up. A settled rate takes one tick to establish, so a slot
carries from its **third** sample of a clip rather than its second — except at a
genuine clip change, where the first moving step *is* the definition of the rate
and there is nothing to have re-seeked away from, so that one is taken on trust.
A step of zero is deliberately neither: it leaves both the rate and that trust
alone, or a clip posed for a tick before it starts would lose its opening step to
a rate of 0. And a clip whose length cannot be read never carries at all, which
is the honest failure rather than a guess.

##### The first version shook, and the probe had already said so

Shipped as the default, `Mode.Timeline` made the **teleport crystals shake
rapidly up and down** — reported from play, at 144 fps. The probe had recorded
the cause a run earlier and it was read as success:

```
76 playback (6 on the wrap, 0 turned, 0 in reverse)
73 playback (0 on the wrap, 4 turned, 0 in reverse)
```

Those two columns contradict each other. A clip that genuinely reflects at an
endpoint runs **backwards** for the rest of its half-cycle, so every real turn
must be followed by ticks counted `in reverse`. There were none, in any window,
while turns fired 1-6 times a second. **Every turn was spurious**, and a spurious
turn is a shake by construction: the pose runs to the end of the clip and back
inside one tick, once per tick, at the frame rate. A crystal whose clip is a
vertical bob renders that as exactly what was reported.

Three defects, and they are worth separating because only the first is about
turns:

1. **The rate was not reflected after a turn.** The unwrapped path length was
   stored as the new rate, keeping the *pre-turn* sign, so the tick after a turn
   always mispredicted, re-seeded and turned again. Self-perpetuating, and the
   direct reason no turn was ever followed by reverse playback.
2. **A turn was accepted merely for scoring closer to the rate than straight
   playback.** The reflection is a free extra parameter, so it wins on noise: a
   clip that simply *slowed down* near the end of its cycle was explained as a
   reflection. A turn is now considered only once constant velocity has failed
   its own tolerance **and** the clip would genuinely have overshot that end.
3. **The opening step of a clip was trusted with no bound.** A seek into the
   middle of a clip is indistinguishable from the start of a fast one except by
   size, and `Nearest(step, D, 0)` admits up to `D/2`. Measured playback is
   64-290 units against `D = 4096`, so `FirstStepFrac` refuses an opening step
   past a quarter of the clip — an order of magnitude clear of anything real.

**The structural lesson is bigger than the three bugs.** `Mode.Timeline` is the
only mode that can ask for a pose *outside* the interval `[prev, cur]`; that is
deliberate, and it is how a loop plays forward through its wrap instead of
rewinding. But it means a misclassification does not produce a slightly wrong
pose, it produces a pose from somewhere else in the clip. `Mode.Time`, for all
its ad-hoc classifiers, cannot do that: whatever it decides a tick was, it draws
something between two poses the game actually produced. **The two designs differ
in where the risk sits, not only in how principled they are** — a principled
predicate with an unbounded interpolator against guessy classifiers on top of a
bounded one — and the premise that the classifiers were the whole problem was
half the picture.

So the fallback is bounded too, which is the fourth fix and the one that makes
the rest safe. A carry is accepted within `RateTolRel` *of the rate*, so the
tolerance is as wide as whatever was last recorded — and the hold path was
recording whatever it saw, including a seek of up to `D/2`. That handed the next
tick a tolerance of `D/8` and let it accept a pose from anywhere. `Seed` now
refuses to call an implausible step a rate at all and leaves the slot unsettled,
so the slot draws at the tick rate until the clip does something a clip could do.
**What makes the mode safe is not that the predicate is always right; it is that
being wrong costs a held tick rather than a pose out of nowhere.**

##### A fast clip could never settle a rate, and so never moved

Play reported a **flying gecko's backflip looking like it ran at a low frame
rate** while everything around it was smooth. That is what `Mode.Timeline` looks
like when it holds — and it was holding, permanently, on that one animation.

`FirstStepFrac` was written as a bound on what the *opening* step of a clip may
be carried on trust. It was also being applied to whether the step could be
**recorded as a rate at all**, and those are not the same question. A clip whose
genuine playback rate is larger than a quarter of its length then cannot settle:

* the slot has no rate, so it takes the unsettled branch;
* the step is past the bound, so it is neither carried nor recorded;
* nothing changed, so the next tick reasons identically and holds identically.

It never recovers, for the whole animation. (A slot *past* its first step
recovered in two ticks, which is why this only ever showed on one clip: the
strand needs the very first step of a fresh clip to be a big one, and a backflip
starting from a new clip byte is exactly that.) The probe could not show it
either — a stranded slot is silent, and "no carries" reads identically to "nothing
animating".

Both halves are fixed. **Size buys a tick of latency; repetition buys
correctness.** A step too big to trust is now *made to wait one tick* and
confirmed by a second observation, which is what "settled playback" means and is
evidence that magnitude cannot supply; `Seed` records what it saw whatever the
size. The danger a big rate actually posed was never its size but that the
acceptance window scales with it, so that is capped directly instead
(`RateTolCap`, a twentieth of the clip — about one tick's motion, binding only
above a rate of ~410, well past anything measured). And `_maxRefused` is now in
the probe line as `widest refused N`, which is the counter that makes a stranded
clip visible at all: **a refused step of 1344 showed up in the first run after
adding it**, on a 4096-unit clip, so steps past the old ceiling are ordinary.

##### What it measures now

At `KF2_FPS=144` with `KF2_SMOOTH_ANIM_PROBE=1`, sweeping areas 0, 2 and 7 —
553 playback ticks over the run, **7 recognised as cycle wraps, 0 as endpoint
turns**, against 15 holds and 0 slots settling; 381-527 weights carried a second
at a mean fraction of 0.55-0.59; `0 with no clip length` outside the frames of an
area transition. The load-bearing number is the **arc**: the widest step carried
over the whole run is **290 units**, the top of the measured playback range, so
nothing walked round the back of the circle, while the widest step *refused* was
1344 — the two staying far apart is the shape to want. Before the turn fix the
same scenes counted 1-6 turns a second. The same scenes under `Mode.Weight` show **8-17
carries a second refused for leaving their segment**, which is the defect
`Timeline` removes rather than bounds. The probe counts a morph submit with a
clip time of 0 separately (`N with a running clip`) so a scene full of MO-posed
props cannot read as success. Death clock unmoved: 65 frames in 3.16 s at 20 fps
with the mode on and off.

**`Mode.Timeline` is the default, and it took two readings by eye to get there.**
It shipped as the default, play reported the crystals; the default moved to
`Mode.Time`, which was the only mode with a positive report at that moment, while
the cause was found; with the four fixes in, play reports it looking very good
and it is the default again. The argument for it was always the stronger one.
What it lacked was the eye, and nothing in this repo could supply that.

##### Is it *correct*, though

On the invariant this was written to — *every in-between pose is a sample of the
game's own clip function between two consecutive ticks of playback, or else the
pose the game asked for* — `Timeline` satisfies it by construction and
`Mode.Time` does not. When `Timeline`'s predicate cannot explain a tick it
**holds**, which is the second half of the invariant; when `Mode.Time`'s
classifiers are wrong they still interpolate, and its turnover is *synthesised*
out of the last playback step rather than read, so the pose it draws across a
wrap is one the game never produced. That is a real difference in kind and it is
the honest reason to prefer `Timeline`.

"Objectively correct" is more than that claim, and four things are still open:

* **Three tuned constants remain** — `RateTolRel`, `RateTolAbs`,
  `FirstStepFrac`. Fewer than the five classifiers they replaced, and each is
  expressed against something measured (the slot's own rate, the clip's own
  length) rather than picked, but they are not derived.
* **"Playback is constant velocity" is an assumption about the game**, not a
  reading of it. It is strongly supported — 852 of 866 classified ticks were
  accepted as playback across three areas — but a clip that *eases* violates it
  and is held.
* **Two of the four cases the predicate claims have never been exercised.** The
  probe has never once counted an endpoint turn or a tick of reverse playback, in
  any run, before or after the fix. The ping-pong branch is the part of the
  predicate with no evidence behind it at all, and it is the part that shipped a
  visible defect; if anything shakes again, look there first. The gecko's
  backflip may well be the reverse case — the report was that it is the frontflip
  played backwards — in which case `0 in reverse` was a *second* symptom of the
  strand rather than a separate gap, and it should start counting now. Worth
  reading the probe next to that gecko.
* **Every clip measured is 4096 long.** The modulus is confirmed two ways (the
  highest clip time seen is 4095, and wrap steps are exactly `rate - 4096`), but
  a *uniform* length means the per-clip lookup would look identical if it were
  reading a constant. Finding one clip with a different `D` is what tests it.

What no counter here can say is the **picture**. Three cases need looking at: a
**looping** clip through its turnover (the wrap column proves the tick was
recognised, not that the pose is continuous across it), a clip played in
**reverse** — the drawbridge lever, which is what caught the old sign bug and
which the probe has still not reported a single instance of (`0 in reverse` in
every window) — and an **attack the AI restarts**, which is the case that lands
in the hold and should therefore look exactly like smoothing being off. The
crystals are now the fourth: they are the scene that caught the shake.

#### The bounded mode, and why it was the default

Play reported poses **spazzing out with interpolation on and never at 20 fps**,
and that reading is the one that matters: at 20 fps the phase is 0 on every
frame, so the carry is zero and the patch does nothing at all. The game's own
data is fine; the in-between pose is what is wrong.

Everything static analysis can settle says the mechanism is sound. The blender's
keyframe rebuild is **absolute rather than incremental** — `func_80034DA8` caches
on (clip, animation index, segment index) at `S1+0x2/+0x4/+0x6`, and a miss
rebuilds from a fixed per-segment keyframe list (`u16[seg+8]` through
`func_80034934`, then the list at `seg+0xA` accumulated by `func_800349F8`) — so
asking it for a segment out of order cannot desynchronise a decoder. The weight
slot really is the caller's: `func_8003486C` has no prologue, so its `SP+0x10` is
the blender's frame, where `SP+0x1C` was stored for it. The reversed-segment sign
is right. Also worth recording, since the doc comment had it loose: the submitter
passes the blender **three** distinct values — `a1` is the *bank* (from its own
`a1` register), `a2` the *animation index* (caller `SP+0x1C`, the byte tested
`< 0x80`), `a3` the *time* (caller `SP+0x20`) — and it is the animation index,
not the bank, that this patch keys slots on.

What **cannot** be settled from here is what the pipeline does when the segment
index moves at the *frame* rate rather than at the tick rate, and driving the
integer clip time is the only thing in this port that makes it do so.

So the default stopped doing it. `Mode.Weight` leaves the integer time exactly as
the game wrote it and spends the phase on the **12.12 blend weight alone**,
clamped to its segment: the frame stands `(1 - phase)` of a tick behind the pose
the game asked for, so `add = -(1 - phase) * step * 4096 / duration`. The pose is
then always between the segment's start and the pose the game asked for — it
cannot reach anywhere the game would not have drawn this tick, whatever the clip
time does — and the segment index, the cache and every decode are bit-for-bit
what they are with the patch absent. **That bound holds under every explanation
of the spazzing that could not be eliminated**, which is why it is the default
rather than one more guess at the cause.

It gives up motion *across* a segment boundary. **That has to be a refusal and
not a clamp**, and the first version of it clamped. A tick advances roughly one
segment (mean step 511 against `u16` durations), so on a clip whose segments are
short — a boss mid-attack — `(1 - phase) * step` is larger than the whole segment
for the early frames of every tick, and `Math.Clamp` turns those into the
segment's own *start* pose. The slot then holds still for part of the tick,
races through the remainder and jumps at the tick boundary: play reported the
final boss as **noticeably less smooth and more jittery with smoothing on than
with it off**, which is that, and it is a worse artefact than the 20 Hz stepping
the patch exists to remove. So a carry whose clamped value differs from the value
it asked for is dropped instead of written, and dropped for the **whole tick** —
the first frame of a tick carries the largest offset and so is the frame that
discovers the overrun, and a slot that carried on the later frames alone would
step *backwards* at the frame that stopped, which is the same jump one frame
later. A refused slot draws the pose the game asked for, identically to the patch
being off. `KF2_SMOOTH_ANIM_PROBE=1` reports `N refused (left the segment)`
beside the carry count; a clip whose segments are long enough never shows it.

Carrying properly across the boundary means picking the *earlier* segment, which
is precisely what `Mode.Weight` exists in order not to do, so within that mode it
stays a refusal. `Mode.Timeline` picks the earlier segment on purpose and bounds
the result a different way — the fraction it spends on the weight is under one
clip unit, so the segment `floor(t)` landed in is the segment that gets drawn —
which is why the refusal has nothing left to catch there.

**All three are selectable, because only the picture can separate them** — which
is not a formality here: the mode with the best argument behind it is the one
play caught shaking, and the switch is how that was found. The combo is
Video ▸ Enhancements ▸ Pose interpolation, under the animation checkbox, and
`KF2_SMOOTH_ANIM=timeline|weight|time` sets it from the console; switching clears
the per-slot state, so a creature on screen steps for one tick and then draws
under the new mode, which is what makes an A/B while something is animating
possible at all. **`Time` is the default**, because it is the only mode with a
positive report by eye. When the picture has been judged, the losers go.

Vertex-fetch lerp at `RotTransPers` was tried first and did not change the
picture — it interpolated a rigid majority, and the probe's "XYZ moved" line
was vertex 0 only. Measured not to touch the world clock, which the discarded
version did: 65 death frames in 3.25-3.28 s with it on at 20, 60 and 144 fps,
against 3.25-3.27 s with it off.

**The arm is not a sprite index and does not stay.** See "The player's arm is the
same bug after all" above, and "The arm rides the same clock" below.

#### The arm rides the same clock

`func_80034DA8` hands `func_8003486C` the clip byte and the clip time whoever
called it, so the arm needs no machinery of its own — only a way into the window
`BeforeClock`/`AfterClock` already work inside. That is two hook pairs and one
refactor:

* `BeforeSubmit`'s body below its argument reads became **`Observe(id, clip, time,
  rootCoupled)`**. Everything above that line is "where do the clip byte, the clip
  time and the slot identity live in *this* call"; everything below is the same for
  a creature and for the arm. `rootCoupled` is false for the arm — the hold that
  keeps a pose with a root `ObjectSmoothing` refused only means anything for
  something whose root is in one of those tables, and the arm is placed from the
  equipped weapon's record instead.
* A pair on **`func_80032400`** sets `_inArm`. That is the scope fence:
  `func_80034DA8` has four callers — the arm, the HUD builder and both object walks
  — and only the arm is carried here.
* A pair on **`func_80034DA8`** does for the arm what `BeforeSubmit` does for a
  creature: refuse when `_depth != 0` (the creature path already has that call in
  hand), open the window, and `Observe(a0, a2 & 0xFF, (s16)a3, rootCoupled: false)`.
  The slot key is `a0` = `0x8019949C`, the MO decoder cache slot, which is disjoint
  from every position pointer the creature front-end keys on.

**The idle frames are the one thing the arm needs that a creature does not.**
`func_80032400` draws nothing while the clock is `-1`, so the carry never sees the
gap between two swings; and the clip byte is the *kind* of attack rather than the
attack, so a second swing of the same kind would arrive as one enormous backwards
step off the end of the first and be classified as a re-seek for the whole of it.
The pre on `func_80032400` reads the clock itself, and resets the slot once per
idle gap. That needs no rule about what a swing looks like.

Nothing is written to game memory — one register on one call, plus the blender's
own stack temp, the same as the creature path — so there is no restore, no leak
check and no ordering constraint against `LoopPacing`. A redraw inside a modal loop
replays stage 13, so the arm is carried there too, for free.

Measured at `KF2_FPS=144` against the 20 Hz world, twenty-odd swings across areas 1
and 2, in both `Mode.Timeline` and `Mode.Time`: **clip 0, step 300 a tick, 4096-unit
clip, 13 ticks a swing, 0 held, 0 rigid**, and 86 of a swing's 94 rendered frames
carried — the uncarried ones being the opening tick, which has nothing to
interpolate from. The world clock is untouched: 60 death ticks in 3015 ms (19.9/s),
and the swing takes the same thirteen ticks and reaches the same clip time with the
carry on and off, which is the more direct check of the two.

The probe (`KF2_SMOOTH_ANIM_PROBE=1`) reports the arm on **its own line**, because
one slot against a scene's worth of creatures would otherwise vanish into the
rounding, and because the two questions it has to answer are its own: is the swing
a morph clip at all (`rigid 0`), and is its time moving (`step 300`). An idle second
reads `arm no swing`, which is deliberately not the same answer as `rigid`.

It rides `KF2_SMOOTH_ANIM` and the Video ▸ Enhancements ▸ Pose interpolation combo
— same mechanism, same predicate, same switch — and so is **off by default** with
the rest.

### The menu's cursor repeat is outside the gate by construction

The stage gate is a pre-hook on six main-loop entry points, so it can only decide
whether one of those six *runs*. The in-game menu is not one of them and never
passes through them: `func_80029CBC`, inside stage 3, `jal`s **`func_80018E80`**
on a just-pressed Circle, and that call **blocks for the whole menu session**,
running its own loop and presenting its own frames through `func_800226A8`
(`VSync` then `DrawOTag`). While it runs, the main loop is parked inside stage
3's `jal`; `FramePacing.AfterDrawOTag` still fires and the accumulator still
ticks, but `BeforeStage` is never consulted, so **nothing inside the menu is on
the tick clock**. This is the case the "Three things held the port at 30" section
already flags: skipping stage 3 decides whether such a loop is entered, it cannot
cut one in half. (Stage 3 reaches the renderer by more paths than that section
originally claimed — `scripts/check_gate.py` enumerates them — but they are all
extra renders inside the stage rather than the frame's own.)

What that cost is the cursor. The two steppers — `func_8001EA14` (a fixed option
list) and `func_8001EB70` (a scrolling one: inventory, equipment, magic) — open
the same way, and there is **no edge detection** in either:

    func_80022E90();   // the auto-repeat delay
    func_80022E58();   // PadRead(1), and latch 0x8006E5C4 if anything is down
    ... test Up (0x8006E590) / Down (0x8006E594) against that word, and step

Holding Up steps the cursor on every iteration of the menu loop. The only
throttle is **`func_80022E90`**:

    if (*0x8006E5C4 != 1) return;          // nothing was down last read
    *0x8006E5C4 = 0;
    for (s0 = 0; ; ) {
        if (PadRead(1) == 0) return;       // released
        if (s0 < 6) { s0++; VSync(0); continue; }
        *0x8006E5CC = 0; return;           // the repeat fires
    }

On hardware `VSync(0)` waits for the next vblank, so that spin costs **six
vblanks — 100 ms** whatever frame rate the game itself was achieving. Since
[`0021`](RUNTIME.md) the emulated vblank is a wall-clock grid and `VSync(0)`
presents and returns, so the only thing pacing a VSync *call* is `FrameClock` —
which `FramePacing.ApplyHostCeiling` deliberately sets permissive at
`max(60, TargetFps * 2)`, because it paces per call and a frame can carry more
than one. The delay inherited that ceiling, so raising the render rate shortened
it — and above 60 the ceiling stopped holding the spin at all.

Measured by holding Down in the menu for two seconds, driven over `KF2_SHELL`,
with `KF2_MENUPACING_PROBE=1` reporting what each spin cost:

| `KF2_FPS` | spin, unpaced | steps/s | spin, paced | steps/s |
|---|---|---|---|---|
| 20 (default) | 66 ms | 7.5 | 100.8 ms | 6.0 |
| 60 | 41 ms | 15.0 | 100.7 ms | 8.5 |
| 144 | 1–17 ms | **36–37** | 100.2 ms | 9.5 |

The 144 row is the complaint: thirty-seven steps a second through a list. **Read
the steps/s column, not the spin.** The unpaced spin is not reproducible — the
same configuration measured 1.2 ms on one run and 16.6 ms on the next — while the
repeat rate it produced was 37.5 and 36.0 across those same two. And it is **not**
the ceiling's arithmetic either: six calls at a 288/s ceiling would be 21 ms. The
ceiling is a per-call throttle that the menu's own frame is already spending
against, so predicting the number from the rate is exactly the mistake `0025`
warns about. The honest statement is that the delay was on the frame clock, and
the frame clock does not hold it.

`0025`'s own comment names the trap: the ceiling is permissive *because* it paces
per call, and a caller that needs a rate should keep its own deadline at the frame
boundary. `FramePacing` does; this delay is expressed in calls, so it did not.

**`patches/MenuPacing.cs` makes those six calls cost a vblank each again, and only
those.** One pre/post pair around `func_80022E90` marks the window, and a pre on
GAME.EXE's `VSync` thunk (`0x8005FCC8`) holds to the next 1/60 s boundary while it
is open. The six frames are still presented, which is the point of pacing the
calls rather than sleeping the shortfall afterwards — that would be ~79 ms of
frozen picture every repeat at 144 fps. `LibEtc`'s own `_vcount` cannot be the
clock, since it only advances *from* a `VSync` call and waiting on it would
deadlock, so the patch keeps its own 60 Hz grid.

**The residual is one menu frame**, and it is deliberate: 6.0 steps a second at
20 fps against 9.5 at 144, because after the spin returns the menu still renders
one frame at the render rate — 50 ms against 7 ms on top of a constant 100. That
is a 1.6× spread replacing a 5× one. Closing it entirely would mean pacing every
`VSync` inside `func_80018E80` rather than inside the repeat, which pins the whole
menu to 60 fps; not done, and one line away if the residual ever reads as a rate
dependence rather than as feel.

Three things about the shape are load-bearing. Adding `func_80018E80` to the gate
would skip the **entire menu session** rather than one iteration of its loop,
because `HookManager` detours whole functions. Gating the steppers is worse: both
return the new cursor index in `V0`, so a bare `return false` hands the caller
garbage. And it costs nothing when idle — `func_80022E90` returns before its loop
whenever `0x8006E5C4` is not 1, so nothing is paced unless a direction is actually
held. One hook covers every list in the game: `func_8001EA14`, `func_8001EB70`,
`func_8001B0D0`, `func_8001BB7C`, `func_8001BE60` and `func_800206E0` all call it.

It is on by default with no settings page — a correctness fix like frame pacing
rather than a taste like dithering, and the console fixed the number at one value.
`KF2_MENUPACING=0` is the comparison; `KF2_MENUPACING_PROBE=1` prints what each
repeat cost and how fast the blink stepped.

### The blink is the same bug one layer up

The cursor's highlight is an eight-step ramp up and back down. `func_80022530` —
the menu's **frame head**: buffer swap, OT pointer, `ClearOTag` — steps
`0x8006E5CC` by one in the direction at `0x8006E5D0`, clamping to 7 at the top and
latching `0xFFFFFFFF` at the bottom, and `func_80021A84` reads the counter as
`(v + 0x1F4) << 6` into a sprite's `+0xE` to pick one of eight cursor frames.
`func_8001EA14` zeroes the direction on every accepted move and `func_80022E90`
zeroes the counter when a repeat fires.

Two things about it were not what they looked like. It is **not a continuous
pulse** — the down ramp latches the direction off, so it is one wink per accepted
move, sixteen steps long, and sitting still in a menu steps it zero times a second
(measured). And the frame head runs **twice per iteration** of `func_80018E80`,
because the menu's inner loop makes two passes and each one presents.

It steps once per menu frame, so its rate is the render rate. Measured steps a
second while holding Down:

| `KF2_FPS` | unpaced | capped |
|---|---|---|
| 20 (default) | 15–19 | 8–13 |
| 60 | 30–35 | ~21 |
| 144 | 73–77 | 9–20 |

The frame head cannot be skipped — it swaps the buffer — so the fix is a pre/post
pair saving the two words and putting them back on a frame the grid did not
advance on, the same shape `ObjectSmoothing` uses. **Nothing sleeps for the
blink**: the menu still renders at the render rate, only the counter is held, so
this caps the wink rather than pacing the menu. That is what keeps it from
becoming the 60 fps menu the repeat fix deliberately avoided.

**60 Hz and not `LogicHz`, deliberately**, for the reason the repeat gives: a menu
frame is one `VSync(0)`, which is one vblank, and the tick rate is a judgement
about what the *world* achieved under load. Binding it there would make
`KF2_TICKRATE` — a setting about game speed — retune the interface.

**It does bite a little at the 20 fps default**, and that is worth recording
because the first guess was that it would not. The menu renders its frames in
pairs, and the second of a pair lands inside the same vblank slot as the first
whatever the render rate, so one of the two is always held. The default's wink
gets somewhat longer rather than staying exactly as it was.

**The cap is a choice, not a reading.** If the console's menu held 60 fps it
stepped the blink twice a vblank — the frame head runs twice an iteration — and
the faithful cap would be 8.3 ms, a 0.13 s wink instead of 0.27 s. That rests on
an assumption about a frame rate this port cannot observe, and the complaint being
fixed is "too fast", so the slower of the two is the default. `MenuPacing.BlinkMs`
is the one constant to change. **By eye is the only way to settle it, and it has
not been looked at.**

Every other modal loop was rate-dependent for the same structural reason, and
that list — the menu box open/close animation (`func_800356F4`), the transition
fade (`func_80037B5C`), the cutscene and message-box loops (`func_80047000`,
`func_80048208`, `func_8004831C`) and the spell-cast and item-use animations
(`func_800474D0`) — is what stopped being enumerated by hand. See "Loops that
render their own frames" below.

### Loops that render their own frames

**The generalisation of the two fixes above, and the reason the list stopped
growing by bug report.** Reported from play at a high rate: a picked-up item spins
too fast, an NPC's interact animation plays too fast. Both are the cursor's bug
with a different counter in a different loop, and the answer had to be structural
— finding the counters by playing the game is exactly what the two fixes above
cost.

**Why a modal loop escapes the gate.** `FramePacing` holds the world to `LogicHz`
by *skipping* five main-loop stages on a non-tick frame. A modal loop is entered
*from* one of those stages, so the gate decides only whether it is entered and
never cuts one in half. Inside, no gated stage is being called: the loop iterates
once per **rendered** frame, and everything it steps — a rotation, a fade level, a
sprite index, a message timer — runs at the render rate.

**The fix is one sentence.** On the console a modal loop's iteration *was* a
rendered frame and a rendered frame *was* a tick. The port broke that identity
everywhere the stage gate cannot reach, so put it back: **a modal loop's body runs
once per world tick, not once per rendered frame.** Nothing is enumerated, nothing
is snapshotted and no counter has to be found — the loop iterates as often as the
world ticks, so every number inside it is right by construction. It is the
smoothing fix run backwards: smoothing takes the phase between two ticks and *adds*
frames the game did not compute, this takes the same phase and *withholds*
iterations it should not have computed.

### Holding the loop is half of it, and the first version shipped only that half

The half that shipped paced the loop's *frames* to the tick rate, which is the
same thing as holding its body — one iteration, one frame — and it is what came
back from play: *"it runs at the correct speed, BUT the animation is at its
original framerate, and so is the camera."*

Of course it was, and the reason is already written down two sections up. The
frame the loop drew **was** the tick, so `FramePacing.LogicPhase` was 0 on every
one of them and `FrameSmoothing`, `ObjectSmoothing` and `AnimSmoothing` had
nothing to carry — exactly the state everything was in while the frame boundary
was broken and "the smoothing never ran at all". Pacing gives a modal loop the
console's *speed* and the console's *frame rate*, and the port's whole argument is
that those two should stop being the same number.

So the loop is not paced. It is **gated**, and the gap between its iterations is
filled with **redraws**: stage 13 called again at the phase the frame now stands
at. That is not a new mechanism. `func_80037B5C` already renders extra frames
inside a stage by calling stage 13, and that is precisely why stages 2 and 3 are
recorded gate exceptions; this does the same thing deliberately.

What it buys is that the world advances at `LogicHz` while the picture is drawn at
the render rate, and `ObjectSmoothing` and `AnimSmoothing` — which bracket stage 13
— carry the objects, the creatures and the poses between ticks the way they already
do everywhere else. Neither re-reads memory on a non-tick frame (each keeps `prev`
and `cur` in managed fields and only re-samples when `TickedThisFrame`), so a
redraw is safe for them by construction; it only re-lerps at the new phase.

The loop ends on the logic clock rather than on a counter: each redraw passes the
frame boundary itself, so it is paced by `FramePacing.Floor` and advances the
accumulator exactly as an ordinary frame does. `MaxExtraRenders` is a backstop
against a configuration nobody has thought of, not the mechanism.

### A redraw has to pass stage 13 its two pointers, and the first one shipped did not

Stage 13 is **`func_800342D8(VECTOR *pos, SVECTOR *rot)`**. It opens with
`func_8002E22C`, which copies 16 bytes from `a0` and 8 from `a1` into
`0x80192E78`/`0x80192E88` and builds the frame's whole view matrix out of them —
**unless both are zero, in which case it reuses what is already stored**. Stage 8,
`func_80025A1C(pos, rot)`, is the routine that *fills* those two blocks: 12 bytes
written through `a0`, six through `a1`. The main loop `func_8001369C` keeps two
stack scratch blocks (`s1 = sp+0x28`, `s0 = sp+0x38`) and hands the same pair to
both stages; `func_80037B5C` does the same with `sp+0x10`/`sp+0x20`.

The first version of the redraw called `stage8(c, m)` and `stage13(c, m)` with
whatever the register file held. After stage 13 that is the tail of
`func_8003549C`, so `a0` is a pointer into the sound-slot table near `0x8018EAA4`:
stage 8 wrote the camera *into live game data*, and stage 13 then projected the
world through it. A garbage view matrix draws next to nothing, and this game's
`PutDrawEnv` has `isbg=0` — there is no background clear, the game overwrites the
buffer by drawing the whole scene — so a mostly-empty frame leaves **the previous
contents of that buffer** on screen. Double-buffered at seven redraws a tick, that
is what came back from play: constant black flicker, no camera interpolation, and
"the frame you died on" alternating with the live picture.

So a redraw **replays stage 13 with the arguments the modal loop itself passed**,
recorded by a pre-hook on the same function. That is literally "draw this frame
again at a later phase", and it is right for all three call shapes in the game: the
fade's own stack blocks (still live below `sp`, and unchanged because the world is
frozen), the `0, 0` of the item-use and `fdat05`/`fdat14` loops, and `fdat23`'s
scripted cutscene camera. The register file is snapshotted and restored around the
redraws, so the loop resumes with exactly what stage 13 left it.

**Stage 8 is deliberately not replayed.** It would overwrite a cutscene's scripted
camera with the player's, and it buys nothing: no gated stage runs inside a modal
loop, so the *player* camera cannot move and `FrameSmoothing` has nothing to carry
there. Measured — `KF2_LOOPPACING_PROBE=2` reads `the loop's own view moved 0.0 u
(peak 0) and 0.0 units per iteration` through a transition fade, a warp and the
menu, over every window but the one that spans an area load.

### A camera the loop builds itself is the one that does move

Play reported it in one line: *"the camera is visibly moving at a lower framerate
in the modals."* `func_8004831C`, the cutscene and message-box loop, has two:

* one ramps a heading `0 -> 0x1000` by `0x200` an iteration and hands stage 13 the
  `u16` it has just written (`a0 = 0, a1 = s2`) — a **full turn in 32 steps**;
* the other steps `rec+0x26` by `0x40` an iteration and passes
  `a1 = 0x80199504`, the player's own composed view, the address `FrameSmoothing`
  already knows.

Held to the tick that is 11.25° a step against a 165 fps picture. It is not a
regression — it is the *speed* fix arriving without the smoothing half, the same
shape as every other complaint this port has had.

So a redraw **carries it**, in the shape the other two smoothing patches already
use. `LoopPacing` keeps the block the loop passed at the previous iteration and at
this one and draws `lerp(prev, cur, phase)` — **interpolated**, at `t - 1 + frac`,
so it agrees with `FrameSmoothing` and `ObjectSmoothing` and can never reach a
heading the loop did not produce. Three `u16` angles at `a1` and three words of
position at `a0`, which is exactly what `func_8002E22C` consumes; applied in the pre
and taken back in the post, so the interpolated values live for one stage 13 call
and the loop's own state is never touched. A step past `CutUnits` (1024, a quarter
turn, eight times the largest pan measured) is a **cut** rather than a pan and is
left alone, the way both other patches guard a placement.

Re-primed whenever the pointer pair changes, an overlay loads, or the main loop
draws a frame — that last one is also what keeps the carry out of the main loop
entirely, since `_carriable` can then never be true there and `FrameSmoothing`
stays the only thing carrying the player's view.

`KF2_LOOPPACING=nocarry` is the A/B: redraw, but leave the loop's own pan stepping
at the tick.

Measured at `KF2_FPS=165` with `KF2_LOOPPACING_PROBE=1`, which now prints the
recorded pair: the death fade ran at **165.0 modal world frames a second against
19.9 loop iterations, 7.3 redraws each**, and every window reported
`view a0=0x801FFEE8 a1=0x801FFEF8` — a stack address, the loop's own scratch. The
menu came out at 60.0, and at `KF2_FPS=20` the redraw count is **0.0**, which is
the "does nothing at or below the tick rate" claim in one number. Menu, warp, death
and auto-reload raised no exception and no unmapped call.

**Two things it costs, both stated rather than discovered later.** Whatever stage
13 steps in its *own* body now steps once per rendered frame inside a modal loop —
the jitter accumulator at `0x8006E608` and `func_800331B4`'s ambient-sound
retrigger — which makes a modal loop no worse than an ordinary frame rather than
better; both are already open in [TODO.md](TODO.md). And a redraw cannot reach a
counter the modal loop steps in its own body: a picked-up item's spin, or a
cutscene camera the loop pans itself, steps once a tick, which is the console's own
rate for it, and smoothing *that* would need the model submit's arguments
interpolated rather than a table.

**Install order is load-bearing.** `HookManager` runs the posts on a function in
the order they were added, so `LoopPacing` is installed in `Program.cs` *after*
all three smoothing patches: the redraw has to be asked for once their posts
have put the tables back, not while their interpolated values are still in them.

**Classifying a frame costs one hook.** `FramePacing.AfterDrawOTag` already fires
on a modal frame, because a modal loop presents through `func_8002E0FC` (stage 13)
or `func_800226A8` (the menu) and both `VSync` and then draw the table — so the
whole defect is that `Floor` was pacing that frame to `TargetFps`. Two questions
decide what it should pace to instead:

* **Is this the main loop's frame?** A pre-hook on **stage 9, `func_800140AC`**,
  whose *only* caller is `func_8001369C`, the main loop. Stages 3, 4 and 6 are the
  only others with a single caller; stage 1 looks like the obvious marker and is
  the wrong one — it is also called from two modal loops and three area modules.
  Stage 9 sits after every stage a modal loop is entered from (2, 3, 7), so a
  loop's own first frame classifies correctly, and it is not in the gate set, so
  `KF2_FPS_GATE` cannot disturb what it measures.
* **Did this modal frame draw the world?** The game's own frame gate
  `func_80017880` is called by stage 13 and by nothing else, and
  `FramePacing.BeforeFrameGate` already hooks it — so that answer costs no hook at
  all.

**The interface is the other case, and it is paced rather than filled.** A modal
loop that draws no world — the menu — has nothing for the smoothing patches to
carry and never calls stage 13, so there is no gap worth filling. It is paced, at
**60 Hz and not the tick rate**, for the reason `MenuPacing.BlinkMs` already gives:
a menu frame is one vblank, and `KF2_TICKRATE` is a setting about game *speed* with
no business retuning a cursor. The menu also presents twice per iteration of
`func_80018E80`, so binding it to a 20 Hz tick would make it respond at 10 Hz.
Pacing is also the **fallback** for a world-drawing loop if stage 13 cannot be
resolved or has not yet been seen with its arguments, and it is what
`KF2_LOOPPACING=pace` selects on purpose, as the A/B against the redraws: the speed
stays right and the picture goes back to stepping, which is the failure worth
having.

Measured with `python3 scripts/rate_matrix.py modal-rate --fps 20 144`, which
opens the menu and then warps, against the same run with
`--env KF2_LOOPPACING=0`:

| `KF2_FPS` | main/s | modal world/s | world iter/s | modal ui/s |
|---|---|---|---|---|
| 20, off | 15.0 | 21.0 | 21.0 | 20.1 |
| 20, on | 15.4 | 21.0 | 21.0 | 20.0 |
| 144, off | 111.9 | **33.8** | **33.8** | **144.0** |
| 144, on | 142.9 | **144.0** | **19.9** | **60.1** |

The two world columns say different things and both are load-bearing.
`world iter/s` is the loop **body** — the thing that was running too fast, and
which has to equal the tick rate. `modal world/s` is the **picture**, which has to
equal the render rate. Off, they are the same number, which *is* the defect; on,
they separate into 20 and 144. The 20 fps rows are the point of the "does nothing
at the tick rate" claim: identical either way. `main/s` is a maximum over windows
that included modal time and so reads low whenever a loop ran for part of one —
read the modal columns, not that one. The unpaced world row is 33.8 rather than
144 because a fade frame is a full world render and never reaches the target: it
was 1.7× too fast, and this class of complaint scales with whatever the loop can
achieve rather than with the number asked for.

**It does nothing at or below the tick rate**, which is where the defect does not
exist either: `LoopPacing.FrameMinMs` returns the length `FramePacing` would have
used anyway, and a modal frame always takes the *longer* of the two deadlines, so
nothing here can make the port run fast. It also stands down under
`KF2_FPS_LOGIC=full`. Nothing in the class reads or writes game memory; it only
ever lengthens a frame. `MenuPacing` is untouched by it — the repeat gate's six
`VSync(0)` calls present no ordering table, so they never reach the frame boundary
and this cannot see them.

**What it does not reach**, stated rather than discovered later: a counter stepped
inside a *drawing function's own body*, where no whole-function hook lands. Two are
known — stage 13's jitter accumulator at `0x8006E608` (the screen shake, which
settles faster and smaller above the tick rate) and the per-object ambient-sound
retrigger at `rec+0x40` in `func_800331B4`. Neither is an animation anyone has
reported; both need the hold/restore shape rather than a deadline, and both are
still in [TODO.md](TODO.md).

On by default and with no settings page, for the reason the menu repeat gives: a
correctness fix rather than a taste. `KF2_LOOPPACING=0` is the comparison.

### The comparison mode

`KF2_FPS_LOGIC=full` (`patches/FullRateLogic.cs`) does the other thing: no gating
at all, and the two rate words the game re-derives every frame — walk speed
`0x80199558` and turn rate `0x8019955C` — scaled by `30 / target` on a pre hook of
`func_80028DB8`. That is the seam `mods/kf2debug`'s speed multiplier already uses,
and the scale is applied multiplicatively because by then the words carry the run
ramp, the encumbered halving and the hit-stun zeroing.

It buys turning, walking and collision genuinely sampled at the render rate, and
it is **not** a shipping mode, for reasons worth stating before anyone measures
them: pitch steps by a flat 3 and does not scale; gravity integrates
`0x8019954E` per tick and does not scale; and every per-tick counter in the game —
the death sequence, spell lifetimes, the poison tick, equipment regen, animation —
runs at the render rate.

### What it costs, and what is still unverified

Sampling the game thread in an area put 161 of 200 samples in the pacing sleep, 23
in `Present` and six in game code — about a millisecond of MIPS against thirty-two
of waiting. Drawing more often costs nothing this port has not already got.

**The default is 20 fps and a 20 Hz world**, 1:1, which is the console's own
arrangement. What no counter answers: whether 20 fps is an acceptable shipped
default or whether the picture should be drawn faster than the world runs (checked
by eye once the smoothing interpolated, and reported *"incredible"* at a high rate),
and whether the full-rate mode feels better than a smoothed 20 despite its broken
timers. The interpolated camera no longer swims, lags into a bounce, or jitters
against a wall — that was the extrapolation, now replaced.

The counters that *are* answerable. The 65-tick death clock at `0x8019951A` is the
measurement, since stage 3 bumps it once per logic tick — its slope against wall
time *is* the tick rate. Driven from `KF2_SHELL`: `kill`, then poll `state` for
`deathFrames`. Measured in area 1, slot 2:

| `KF2_FPS` | `KF2_TICKRATE` | rendered | ticks/s | 65 ticks in |
|---|---|---|---|---|
| 20 | 20 (default) | 20.5 | **20.00** | 3.20 s |
| 30 | 20 | 30.5 | **19.97** | 3.20 s |
| 60 | 20 | 60.0 | **20.06** | 3.19 s |
| 120 | 20 | 120.5 | **20.00** | 3.19 s |
| 144 | 20 | 144.5 | **20.00** | 3.20 s |
| off | 20 | 60.0 | **19.99** | 3.20 s |
| 30 | 30 | 30.5 | 29.48 | 2.16 s |
| 60 | 30 | 59.0 | 29.87 | 2.14 s |
| 144 | 30 | 144.5 | 30.01 | 2.14 s |
| 20 | 30 | 20.5 | 20.00 | 3.20 s (render-limited, see above) |

`KF2_TICKRATE=30` reproducing 2.14-2.16 s is the regression check: it is the same
2.11-2.13 s this section recorded before the tick rate moved, so the setting is
genuinely a setting and not a one-way change. Rendered rate is stage-8 calls per
second off `KF2_SMOOTH_PROBE=1`.

Walk distance is the cross-check that does not go through the death clock. A fixed
2 s hold of Up covers **1817 units at 20 Hz against 2574 at 30 Hz** — the game
moves a fixed amount per tick, so the distance scales with the tick rate and not
with the frame rate. (The ratio is 0.71 rather than exactly 2/3 because the run
ramp completes in a fixed number of ticks either way.)

Frame smoothing, at 60 fps against the 20 Hz world, turning in place:
`120/120 frames carried, mean phase 0.60 tick, yaw 20.8 u` — further per frame
than the 18.1 u recorded against a 30 Hz world, which is the longer tick showing
up. **Its probe now says which of the two reasons a frame was skipped**; it used
to print "0 of N carried (phase idle)" whether the phase was zero or the player
was simply standing still, and the second reads as a broken logic clock when it is
nothing of the kind.

The measurement to run it against is `mods/framestats`, restored for this work and
now reporting **two** histograms: vblanks per frame (how long the frame took) and
`VSync` calls per frame (how many times the game asked). Since `0021` those are
different numbers, and conflating them is what made the first pass at frame pacing
read as circular.


## Auto reload

`patches/AutoReload.cs` reloads the last save when the player dies. King's Field
has no retry; without it a death costs the menu, then the load screen, then the
slot. (It began as `mods/autoreload` and became a patch — see "Auto reload became
a patch" below.)

It is small because it adds **no loading path of its own**. Both halves were
already in the game and both are written up in
[GAME_INTERNALS.md](GAME_INTERNALS.md): the death latch under "The character's
stats are buf2", and the in-game load sequence under "The game can load a save
without leaving the area". This is the wiring between them.

**One post-hook on `game` at `0x8002A550`** — the end of main-loop stage 3. Not `func_80029CBC`, which is where the game does its own load from:
that is dispatched only from the state machine's arms for states 1 and 2, so it
stops being called the moment the state byte latches to `0x11`. Stage 3 itself
runs every frame, dead or alive, because the death sequence *is* one of its arms.

Per frame it reads one byte, `0x801994E1`. Not `0x11` and it clears its arming
and returns, so a live player pays a single read for the whole mod. On the
edge into `0x11` it additionally requires `HP == 0`, which is what separates a
death from any other reason that byte could hold `0x11`, then starts a clock. The
reload fires once, after a configurable delay (default 2 s) during which the
game's own death sequence plays.

### The game is already reloading, and it wins the race

The thing that nearly sank this, and the reason the delay is not just a sleep.
**Death is a timeline, not a state**, clocked by a `u16` frame counter at
`0x8019951A`. `func_8002A264` zeroes it, state `0x11`'s handler increments it,
and those three sites are its only uses in `GAME.EXE`:

| counter | what the handler does |
|---|---|
| 1–31 | the death animation |
| 32–64 | fade to black, amount `(n − 32) << 7` |
| **65** | `func_80024154(0, 0, 0, 0, 0, 0xFF)` |

That last call is **the same area-entry routine the patch calls, with area 0** —
the game's own respawn, and what "you died, start again" actually is. (The branch
above it, at `0x8002AFBC`, is the resurrection-item path: a literal position and
yaw `0x0C00` into area 1. It is the debug-looking warp already noted at that
address.)

65 frames is **2.17 s at 30 ticks a second** — and the default delay was 2.0 s.
Five frames of margin. The first run reloaded correctly and the second went back
to the beginning of the game, from identical code and an identical log line, which
is exactly what a race that tight looks like. They are logic ticks rather than
rendered frames, so the margin follows `KF2_TICKRATE` and not `KF2_FPS`: at the
20 Hz default the same 65 ticks is 3.25 s, which is more margin, not less — but
the race is what the hold at frame 31 exists to remove, and it removes it at any
rate.

So it **holds the counter at 31** while it waits. The animation finishes,
the fade never starts, the respawn never comes due, and the delay becomes ours
rather than a bet against the game's clock. The reload logs the counter it fired
at for exactly this reason: `held at frame 31` is the assertion, and anything
near 65 would mean the hold had failed.

The reload is `func_80023638(slot)` and then `0x80029E0C`'s arm transcribed, with
a `c.SP -= 0x20` window for `func_80024154`'s fifth and sixth arguments — MIPS
passes those on the caller's frame at `sp+0x10` and `sp+0x14`. `c.Snapshot()` /
`c.Restore()` bracket the whole thing so the hooked function's caller sees the
registers it left.

Two details that are the patch's and not the game's:

- **`func_80029E5C` afterwards, but only if the state byte survived.** The game
  reaches that arm from a live state and so never has to clear the death latch;
  coming from `0x11`, something must.
- **The slot.** `0x8006E5D4` is the game's own record and is what "last used"
  means, but it is zero until a save or a load has run. Dying on a fresh New Game
  therefore has nothing to reload, and it logs once and leaves the death alone
  rather than inventing a slot. A fixed slot can be pinned in the settings.

`KF2_AUTORELOAD`, `KF2_AUTORELOAD_DELAY` and `KF2_AUTORELOAD_SLOT` mirror the
three settings, which persist to `interface.ini` under `kf2.autoreload.*`.

**Dying on demand is the hard part of testing this**, so the settings page has a
*Simulate death* button: it zeroes HP and calls `func_8002A264(0)`, which is
exactly what the damage path does. It refuses when max HP is zero, since buf2 is
clear until an area is up.

**Status: working on the attract demo, which dies on its own and is therefore a
free test rig** — leave the port running and it will kill the character for you.
Three consecutive deaths in one session each reloaded slot 2 into area 1 at
`HP 46/86, LV 6`, `held at frame 31` every time, with no `unmapped call` and no
exception.

`fdat02` in the overlay log is the tell for the failure: the game's own respawn
loads it, so a run that reloads correctly never does. With the setting off it
appears right after the death, and with it on it does not appear at all.

Still wanted from a real session: a death in a *different* area from the save,
which is the case that makes `func_80024154` re-enter a `fdat` module other than
the resident one, and a save written mid-session to confirm `0x8006E5D4` tracks
saves as well as loads.

### Auto reload became a patch, and the port grew a Gameplay tab

The reason is the one under "What belongs in a mod, and what does not": a mod is
something a player can reasonably be without, and *four screens of menu after
every death* is not a taste. A player who installs this port expects the retry to
be there, and a mod defaults to off and loads silently when disabled — the failure
mode is a death that just costs the menu, with nothing said. So `mods/autoreload`
is now `patches/AutoReload.cs`, on by default.

The port has done this conversion three times now (dither, frame pacing, this) and
the mechanical part is settled:

- `[PostHook("game", Address = …)]` becomes an explicit `SymbolRegistry.Resolve` +
  `HookManager.AddPost` under a `ModInfo` the patch declares for itself, attached
  on the **first `OverlayLoadedEvent`** — that is the earliest moment every
  overlay resolves, and hooks cannot be added once the game is past the loads.
- `IMod.OnLoad`'s config read becomes a `RuntimeReadyEvent` listener, because
  `ConfigManager.Load()` runs after `Program.cs`. The env vars move up into
  `Configure(…)` called from `Program.cs`, and each one sets a `…FromEnv` flag so
  the saved value cannot overwrite it — the same precedence `FramePacing` keeps.
- `IMod.DrawSettings` becomes an `IPatchPage`; instance fields become static
  properties with `Set…` methods, so the page reads live state instead of a copy.
- `OnUnload` has nowhere to go and needs nowhere: a patch is never unloaded, and
  this one never wrote game memory it had to put back.

**Where the page went is the part that was not mechanical.** The frame rate and
the dither switch extend the runtime's `display` section because they genuinely
are video options. Auto reload is not: it is a rule about what happens when you
die. The runtime's five sections are all about the machine — interface, input,
display, paths, audio — and none of them is that, so the port adds a sixth.

`ISettingsSection` is public and `SettingsRegistry.Register` replaces by id, so a
**new section needs no patch to the checkout** any more than renaming one did.
`patches/settings/GameplaySection.cs` is `Id = "gameplay"`, `Order = 7` (between
Video's 5 and Audio's 10, so the runtime's own order is untouched and the tab
sits among them rather than after Paths) and a `Draw()` that does nothing at all:
`SettingsPopup` draws a section's own content and *then* its extensions, so the
pages registered against `"gameplay"` are the entire pane. Registration happens in
`PatchSettings.RegisterUi`, on `RuntimeReadyEvent` — after `HostWindow`'s `Load`
has registered the five, before the popup is first drawn.

One difference from the `display` rename: `settings.gameplay` is a **new** key, so
`Localization.Merge` has to supply all three languages. An override of an existing
key can leave the others alone, since what is already there is what you wanted; a
new key with only `en` behind it makes pt-BR and es-419 warn on every lookup and
then show the key itself.

Verified in a run: the section list came back
`-10:interface | 0:input | 5:display=Video | 7:gameplay=Gameplay | 10:audio | 20:paths`,
and *Simulate death* from the new tab logged
`reloaded slot 2 into area 1 (HP 46/86, LV 6, held at frame 31)` — the same
result the mod gave, so the hook, the config read and the reload path all survived
the move.

## Auto start and the agent beacon

`patches/AutoStart.cs` (`KF2_AUTOSTART=<1..3>`) and `patches/AgentBeacon.cs`
(`KF2_AGENT=1`) are the pair that lets an automated tester **get into the game and
know it got there**. Both are patches driven by an environment variable, not mod
features, for the `KF2_AUTOPAD` reason: a mod is off by default and silent when
disabled, so an agent that did not know to enable it would get nothing.

### The wall is the title, not the Continue menu

The plan for auto start assumed the loader could be called at GAME.EXE's start
menu. It cannot, and the reason took several runs to pin down:

- **With no input the port never leaves OPEN.EXE.** Measured: 110 s at the title,
  still `overlay open`. The "attract demo walks itself in after a minute" only
  happens on some idle path this build did not take; an agent cannot rely on it.
- **`KF2_AUTOPAD` cannot help** — its clock is gated behind the first `fdat` load
  (`Program.cs`), the very thing that has not happened at the title.
- **Writing `Controller.State` does not reach the boot menus.** It advanced
  OPEN.EXE's title but did nothing at GAME.EXE's menu. The path that reaches the
  game *wherever it is* is `PAD_dr` — a `PadReadEvent` listener, exactly what
  `patches/Mouse.cs` uses "so the buttons work in its menus". The buffer is
  active-low and its two button bytes are swapped against `Controller`'s layout,
  so a pressed `Controller` bit is injected as `e.Buttons &= ~((b>>8)|(b<<8))`.
- **The start menu (`func_8001B35C`) is a blocking poll loop**, so the per-frame
  main-loop and stage-3 hooks (`0x80040348`, `0x8002A550`) never run there — none
  of them fired while stuck at the menu, which is why a stage-3-hook load could not
  fire either. The menu's cursor handler is `func_8001EA14` (2 options: 0 = Load →
  `func_8001B4F4`, 1 = New Game), and injected Up/Down did **not** move it; only
  Cross registered, always confirming the New Game default.

### The route that works: New Game as a vehicle, then load over it

So auto start does not fight the cursor. It drives three steps, all through
`PAD_dr`:

1. Pulse **Start** through OPEN.EXE's intro/title → `overlay game`.
2. Pulse **Cross** at the start menu → confirm the New Game default → `overlay
   fdat02`, a real area with the main loop turning.
3. The stage-3 post-hook (`0x8002A550`), which now runs because an area is live,
   waits ~90 frames and calls `AutoReload.LoadSlot(slot)` — the same menu-free
   loader (`func_80023638` + the post-load arm) AutoReload uses on death — to load
   the chosen slot **over** the New Game. Nothing is saved; the load replaces the
   scratch character with the slot's own area and state.

`AutoReload.Reload` was refactored to share `LoadSlot`, so the delicate MIPS-ABI
stack window (`func_80024154`'s fifth/sixth args at `sp+0x10`/`sp+0x14`) lives in
one place.

**Verified, end to end, read off the `KF2_AGENT` beacon (no screenshot):**

```
KF2_AUTOSTART=2 →  overlay main → open → game → fdat02 → fdat05
                   [KF2] autostart: loaded slot 2 into area 1 (HP 46/86, LV 6)
                   beacon: level 1/area 0/slot 0  →  level 6/exp 417/area 1/slot 2
KF2_AUTOSTART=3 →  [KF2] autostart: loaded slot 3 into area 0 (HP 71/79, LV 5)
                   beacon: … → level 5/exp 291/slot 3   (matches the card title "2-3 LV 5")
```

### The beacon

`AgentBeacon` emits one `[KF2-AGENT]` line on each `OverlayLoadedEvent` and a JSON
snapshot about once a second from `VSyncEvent` (on the game thread, so its memory
reads need no marshalling):

```
[KF2-AGENT] {"overlay":"fdat05","inGame":true,"dead":false,"hp":46,"maxHp":86,
             "mp":…,"maxMp":…,"level":6,"exp":417,"area":1,"slot":2,
             "deathFrames":0,"pos":[x,y,z]}
```

`inGame` is `MaxHp != 0` — the same title-vs-in-game oracle as
`mods/kf2debug/GameState.IsInGame`; when it is false the stat fields are omitted.
That one field, printed once a second, is what an agent needs to tell "stuck at
the title" from "in an area" — the failure that used to burn a tester's whole
budget. The stat addresses are carried locally (patches cannot reach the mod's
`GameState.cs`, which the mod loader compiles separately); a future consolidation
could move the shared map into `patches/`.

## The command channel
`patches/AgentServer.cs` (`KF2_SHELL=1`, or `KF2_SHELL=<port>`; default port
27900) is the acting half beside the beacon's watching half: a small TCP server
on loopback speaking a line protocol — one request per line, one single-line
JSON response, always:

```
state                 the beacon's snapshot as JSON
load <slot 1..3>      load a save through AutoReload.LoadSlot
warp <area 0..7>      re-enter an area through the game's own entry routine
press <button> [ms]   hold a pad button for ms (default 150); one press at a time,
                      replaced by the next
kill                  drop HP to zero, the way a hit would
nearby [radius=8192]  live records of the world tables within radius units of the
                      player, nearest first, tagged by table (objects; buf6
                      "entities", whose reading is still Inferred -- TODO.md)
```

A socket rather than stdin because stdout already carries the beacon and the
`[KF2]` log lines; a stdio channel would make every client demultiplex its
responses out of the logs. Off unless set, like every other agent switch: an
unasked listener is worse than one switch to find (the mouse-look precedent).
It is in `patches/` for the `KF2_AUTOPAD` reason — an agent that did not know to
enable a package would get nothing.

### Two marshal points, and why

Commands arrive on socket threads and must run on the game thread. Where they
run depends on whether the command re-enters the loader:

- **`state`, `press`, `kill`, `help`, `nearby` drain from a `VSyncEvent` listener** — the
  same place the beacon reads memory, so no cross-thread access and no new
  machinery.
- **`load` and `warp` drain from a post hook on main-loop stage 3**
  (`0x8002A550`). `func_80024154` waits on the CD by looping `func_80017818`,
  which calls VSync: running it inside the VSync event nests VSync inside
  itself and swaps overlays under a frame that is still drawing — the documented
  death of the debug panel's first warp button. Stage 3 is the game's own load
  path, which is where `AutoReload.Reload` and `AutoStart` already call
  `LoadSlot` from, and it only runs once an area is live — which is exactly the
  guarantee `load`/`warp` need. A heavy command issued anywhere else (at the
  title, say) times out after five seconds with that said in the error.

`kill` is safe at VSync: `AutoReload.Simulate` only snapshots the CPU, writes
HP, calls `func_8002A264` and restores — and the settings page already runs it
from inside Present-inside-VSync.

### One implementation of the warp

The warp core moved out of the debug mod into `patches/AreaWarp.cs`
(`Kf2.AreaWarp.TryRun`) so the server can reach it with no package enabled;
the mod's panel delegates to it, so there is still exactly one transcription of
the game's area-entry sequence. The synthetic press keeps its own inject state
rather than sharing `AutoStart._inject`: that field belongs to the boot driver,
and the two must never fight over it. The press itself rides `PadReadEvent` with
AutoStart's active-low byte-swapped math, which is what makes it work in the
boot menus and everywhere else the game reads the pad.

## The MCP layer

`mcp/` (`KingsField2Mcp.csproj`) is a standalone stdio MCP server that wraps
exactly the six verbs above as typed tools, so an MCP host — Claude Desktop, an
inspector, omp — can drive the same channel without knowing the line protocol.
It is a separate program with no NuGet dependencies (hand-rolled JSON-RPC over
stdin/stdout; stdout carries protocol messages only), it talks TCP loopback to
the shell, and the game itself needs no patch, no recompile and no config entry.

The endpoint defaults to `127.0.0.1:27900`; `KF2_MCP_ENDPOINT=<host:port>`
points a server instance elsewhere (a second game session on
`KF2_SHELL=<port>`, say) without touching the game's own switch.

```json
{ "mcpServers": { "kf2": { "command": "dotnet",
    "args": ["run", "--project", "<repo>/mcp/KingsField2Mcp.csproj", "-c", "Release", "--no-build"] } } }
```

The tools are `kf2_state`, `kf2_nearby`, `kf2_load_save`, `kf2_warp`,
`kf2_press_button` and `kf2_kill`, one per verb, with the button names and
ranges quoted from `AgentServer`. The shell stays the validator: replies come
back verbatim, an `ok:false` surfaces as a tool error, and a game that is not
running costs every tool one connection error rather than a dead host session.

