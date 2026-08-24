# Patches and mods: how the port's own code is attached

Two ways to run C# against the recompiled game, and a rule for choosing between
them. Then the settings UI both feed into, and the two features whose whole story
lives here — frame pacing and auto reload.

## Where each patch is written up

| file | what it does | detail |
|---|---|---|
| `FramePacing.cs` | holds every frame to two vblanks (30 fps) | this file |
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

### 60 fps became a setting

For the setting to offer 60, the stage gate has to already be hooked — hooks are
installed at the first overlay load and the rate is chosen long after. So
`FramePacing` now hooks `0x80040348` and `0x8002A550` (the pair under "60 fps"
below) whether or not 60 was asked for, and `BeforeStage` returns "run the
original" at any rate below 60. `KF2_FPS_GATE` replaces that default pair rather
than adding to it. At 30 this changes nothing; without it, choosing 60 from the
settings would run the whole game at double speed.

Two further consequences of the rate being live rather than fixed at startup:
`Attach` no longer skips when the floor is off, since "uncapped" is now a choice
that can be taken back, and `AfterDrawOTag` tests `Enabled` itself.

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

So the earlier framing of "twice as fast as the console" was too blunt. Precisely:
**in light scenes the port matches hardware's best case exactly, in heavy scenes
it is up to 2× faster because it never bands down, and for one frame in eight it
runs at 60 fps — twice the NTSC ceiling, which is faster than the game can go on
any console.** That last group is the part that is unambiguously wrong.

That reading makes the work much smaller than it first looked, because the
reference speed is not some variable hardware average — it is **the top band,
30 fps**, which is both the design ceiling and where the port already spends 87%
of its frames.

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
appears in any measurement), but the guard is free — the hook counts vblanks off
a `VSyncEvent` listener and returns early when the count is zero.

The deadline is absolute rather than `now + 33.3`, so a frame that overruns is
paid for out of the next one instead of the rate drifting down by the accumulated
jitter, with one frame of debt as the limit: past that the game has *stopped*
drawing (a disc read, a module swap) rather than run late, and the cadence
restarts instead of running flat out to catch up.

Two env vars, both read in `Program.cs`:

```bash
KF2_FPS=30          # 30 fps, the default; 60, or off for no floor
KF2_FPS_GATE=80040348+8002A550   # at 60, stages to run every other frame
```

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

**Step two, if 60 fps rendering is wanted**, is then a clean factor of two rather
than four: run at one vblank per frame, halve every per-tick movement delta, and
*double* the thresholds of per-tick counters (spell duration, torch burn,
i-frames). The asymmetry is the trap — dividing a counter's step instead of
multiplying its threshold makes it expire early. The cheaper variant, which gets
most of the feel in a first-person game, is to run the player's movement and view
at 60 with halved deltas and gate the world update to every other tick: smooth
camera, enemy timing untouched, and a 2:1 gate is far safer than 4:1.

Full decoupling — logic at 30, rendering interpolated at 60 — still needs
decomp-level knowledge of which state is positional, and is still not worth it.

### 60 fps: the mechanism exists, the map is half drawn

**Mechanism Confirmed; which stages to gate is Inferred and the delta halving is
still owed.**

**There is room.** Sampling the game thread 200 times in an area puts 161 samples
in the pacing sleep, 23 in `Present`, and six in game code — so a frame is about a
millisecond of MIPS and thirty-two of waiting. Rendering twice as often costs
nothing this port has not already got.

**The gate works.** A `pre` hook that returns `false` skips the original —
`HookManager.Invoke` honours it — and hooks attach by address at run time, so any
subset of the loop can be gated with no recompile and no config entry:

```bash
KF2_FPS=60 KF2_FPS_GATE=80040348+8002A550
```

Nothing is hooked when no gate list is given, so the mechanism costs nothing
while it is unused.

**What is missing is which stages to name**, and the probe has narrowed it to a
hypothesis rather than settled it.

*Established.* Over ten consecutive windows in an area, exactly four words outside
the display list change on **every single frame**, and one stage writes all four:

```
8017783C (buf5)  changed 656x/656 frames  mean -0.18   now -13348   by 80037C0C
80177848 (buf5)  changed 656x             0xNN000000               by 80037C0C
801778F8 (buf5)  changed 656x             0xNN800001               by 80037C0C
8017793C (buf5)  changed 656x             0xNN800001               by 80037C0C
```

Three are packed `u16:u16` pairs whose high halves range `0x0000`–`0x0E80` across
samples; the fourth is a signed scalar that sits between −13,226 and −13,352 in
every window, jitters by tens, and has a mean signed change of about zero — it
does not drift over four minutes. Separately, stage 4 (`func_80040348`) writes a
*marching* set of buf6 addresses — `8016C654`, `8016CBC0`, `8016CFA4`, `8016D1F4`,
`8016CCA0` in successive windows — rather than a fixed set.

*Inferred, not confirmed.* A 0–4096 range is the PSY-Q angle scale, so the buf5
four look like a **view/camera block** and stage 2 like the stage that maintains
it; a set of addresses that walks through a 9 KB buffer looks like **iteration
over an entity table**, making stage 4 a world updater. That would give the split
the cheap variant needs: gate stage 4, leave stage 2 alone, and the camera runs at
60 while the world stays at 30.

Note this reverses the reading of the write-count table above, where stage 2 looks
inert because it writes four words. It writes four words and they are *the* four.

*The experiment that settles it* is scripted input watched through a per-frame
write probe (the `loopprobe` mod that ran it has since been removed, so this
wants that mod restored) — `KF2_AUTOPAD=20:Up:5000,40:Right:5000`. It has not
successfully run yet: the port exited before `KF2_AUTOPAD`'s first press each
time it was tried.

If `0x8017783C` steps monotonically under held Up, it is positional; if the three
packed words swing under held Right and not under Up, they are the view angles.
Either result names the stage, and the gate follows immediately. Until then
`fps=60` with no gate list runs everything at double speed — the mod says so at
startup rather than pretending otherwise.

**No clock exists in these buffers.** Every busy word has a mean signed change of
about zero, so there is no frame counter to watch, and the verification idea of
"gate a stage and see a counter's rate halve" needs a counter found somewhere else
first. The area module's data at `0x8019F07C` is the next place to point the probe.

The delta-halving is still owed regardless — a view stage running at 60 with
unhalved deltas moves twice as fast, and no amount of gating fixes that from outside.
That is the one part of 60 fps that needs a constant identified in the game's own
code rather than a stage gated from outside it.

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

65 frames is **2.17 s at 30 fps** — and the default delay was 2.0 s. Five
frames of margin. The first run reloaded correctly and the second went back to
the beginning of the game, from identical code and an identical log line, which
is exactly what a race that tight looks like. At `KF2_FPS=60` the same 65 frames
is 1.08 s and the reload would always lose.

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

