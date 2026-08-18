# Input: pad, sticks, keyboard and mouse

**How the game reads input at all.** It never calls `libpad`. It reads the pad
through the BIOS (`B(16h) PAD_dr`) via libetc's `PadRead(id)`, which hands back
`~*(u_long*)buf` — so the word is active **high**, and its two button bytes are in
the opposite order to the runtime's `Controller` bit layout. Stage 3 of the main
loop stores that word to `0x80199554` once a frame and every consumer reads that
global; the area modules never touch it, so all player control is `GAME.EXE` code.

**Nothing here hardcodes a pad bit.** The control code tests `pad & mask[i]`
against a 24-word action-mask table at `0x8006E568`–`0x8006E5D0`, which is what
makes the game's own control-config screen work — so the port reads the table too,
and gets the byte order right by construction by ORing the game's own mask words
back into the game's own pad word. The table, its decode and the sign conventions
are in "The action-mask table" in [GAME_INTERNALS.md](GAME_INTERNALS.md), and the
three-branch velocity shape every axis has is in "Every control axis has the same
three branches" there — that shape is what makes all of this cheap.

**Two corrections are recorded below rather than quietly fixed**, because both are
the same mistake: the mask table names the *button* behind an action and says
nothing about what the action is, or which way an axis runs. A plausible story
about a branch is not evidence. The pitch sign and the attack/use pair both had to
be settled by playing the game.

## Analog twin-stick control

`patches/Analog.cs` (it began as a mod) gives the game continuous analog turning,
looking and walking on a modern layout — left stick walks and strafes, right stick
turns and looks — and it does it **without replacing any of the game's own
movement code**.

The sticks were always there: `InputManager` fills
`Controller.LeftX/LeftY/RightX/RightY` from SDL every poll. Nothing consumed
them, because the game reads `PAD_dr` and gets a digital word, and the runtime's
default binding just wires the left stick to the D-pad
(`GamepadBindings.Up = [11, 104]`) — which in this game means the left stick
*turns*, at the fixed rate, like the D-pad does.

**The trick is the three-branch velocity shape** documented under "Every control
axis has the same three branches" in [GAME_INTERNALS.md](GAME_INTERNALS.md).
Because each axis is `vel += rate>>2` on a held button and then
`angle_or_position += vel`, a hook can pre-load the velocity word with
`target - accel` and assert the matching button in the game's pad global: the
game's own next instruction adds `accel`, lands exactly on `target`, and then
applies it through its own path. Collision, the pitch limit, the walk
normalisation, footsteps and animation all run untouched, on an amount the stick
chose. Nothing is replaced, no `HookManager.AddReplace` — two pre-hooks, on
`func_80028DB8` (turn/look) and `func_800290D4` (walk/strafe), plus one post-hook
on stage 3 (`func_8002A550`) for the probe.

The clamps are never fought: with `|target| ≤ rate` and the button asserted in
the direction of `target`'s sign, the pre-loaded value is always inside `±rate`
after the game's own accumulate, so the clamp branch is not taken.

Four things worth keeping:

* **The camera accelerates while the stick is held out.** A d-pad is down or it
  is not, so the game has no notion of a ramp; a stick held at the edge for half
  a second should be sweeping faster than one just pushed. The patch ramps a
  look-speed multiplier to 2.2× over half a second past 80% deflection and drops
  it three times faster, which — with the cap lifted above — is what took the
  camera from "stiff" to something like a modern shooter's. Fine aim near centre
  is untouched, because the ramp never starts there.
* **The fractional carry is not optional.** At 30 fps a small deflection rounds
  to a zero step every frame; without carrying the remainder the player simply
  does not move below about a third of stick.
* **Buttons come from the mask table, never hardcoded**, so the patch follows the
  game's own control-config screen — and gets the byte order right by
  construction, since it ORs the game's own mask words back into the game's own
  pad word.
* **The left stick leaks into turning** unless the turn masks are taken away from
  it, because the runtime binds the left stick to the D-pad and the D-pad *is*
  the turn control. Measured before the fix: 168 yaw steps in 300 frames with
  the right stick idle. The patch therefore owns the turn bits with a zero step
  whenever the left stick is deflected, and leaves them alone when both sticks
  are centred — so the D-pad still plays exactly as it did.
* **The per-frame speed limit only exists in the two button branches.** The
  branch that runs with *neither* button down decays the velocity by the same
  step and then applies whatever is left, unclamped — so writing `target + accel`
  and asserting nothing lands on `target` however large it is, while writing
  `target - accel` and asserting the button is capped at the game's own rate.
  That is the whole difference between a camera that tops out at the d-pad's
  74°/s and one that does not, and it costs one branch in `Drive`. Confirmed in
  play: mean yaw steps of 39 against a frame rate limit of 28, and a `turnVel` of
  37 sitting in memory, which no button can produce. Nothing else gameplay-side
  reads the turn masks — the only other readers are the control-config screen —
  so dropping the button for a frame is free.
* **A released velocity ramps down, and on a stick that reads as inertia.** The
  game drops nothing: pitch decays by 3 a frame from a limit of 32, so releasing
  the stick keeps the view moving for about eleven frames — a third of a second,
  some 16°. That is reasonable for a button, which cannot be released halfway,
  and wrong for a stick, and it showed up *only* on pitch because the leak fix
  above was already zeroing turn whenever the left stick moved. The patch therefore
  drives a released camera axis to zero for one frame and then hands it back, so
  L2/R2 and the D-pad still work. Movement is deliberately left alone: its
  ramp-down is the walking momentum the game has always had.
* **Sticks idle means the patch is idle.** Both hooks return before touching memory, which
  is what keeps D-pad and keyboard play identical to having it switched off — which is why it can default to on.
* **The look hook is now shared with the mouse.** `Mouse.TakeLook` hands
  `BeforeLook` a step in the same yaw units and this class writes the word, so
  two devices cannot fight over one velocity; the look hook therefore also runs
  with the sticks switched off. See "Mouse look" below.

`KF2_ANALOG_PROBE=1` reports the velocities, the yaw and pitch steps, the walk
speed and turn rate next to the stick deflection that produced them, and dumps
the mask table once. That dump is the evidence for the sign conventions in the
table above — with one gap it cannot close: it names the button behind an action
but not which way the view moves. That cost the first build an inverted look
axis, fixed by playing it; **increasing pitch looks down**. The "Invert look Y"
toggle is now a preference rather than a guess.

### Analog control became a patch, under Input

The fourth conversion, and the reason is "What belongs in a mod, and what does
not" applied to the pad: **a controller plugged into the port without this has its
left stick bound to the D-pad, and the D-pad in this game turns rather than
walking.** A player is not going to guess that the fix is a package under `mods/`
that ships disabled. So the mod is now `patches/Analog.cs` plus
`patches/AnalogProbe.cs`, on by default.

Defaulting it *on* costs nothing, and that is a property of the design rather than
a hope: both hooks test the shaped stick vector first and return before reading
game memory, so with the sticks centred — every keyboard player, every D-pad
player — the patch is two float comparisons a frame and the game plays exactly as
it did. The mod could not have shipped that way at all, being off by default.

The mechanical part was the same as the three before it, with one addition:

- `[PreHook]`/`[PostHook]` became `SymbolRegistry.Resolve` plus
  `HookManager.AddPre/AddPost` in an `Attach` deferred to the first
  `OverlayLoadedEvent`, hook bodies became `public`, and `OnLoad`'s env reads
  became `Configure()` from `Program.cs` with the config read moved to
  `RuntimeReadyEvent`.
- **`Configure` reads its own environment** instead of being handed the strings
  the way `NoDither.Configure(probe)` and `AutoReload.Configure(a, b, c)` are.
  Eighteen string parameters would be a worse index of which variable sets what
  than the table in the class comment already is, so `Program.cs` keeps the list
  in its own comment and calls `Configure()` bare.
- Precedence is the same rule, spelled once instead of per knob: `Env(name, key,
  ref value)` records the *key* in a `_fromEnv` set, and `Saved(key, ref value)`
  skips any key in it. Eighteen `…FromEnv` bools would have been the alternative.
- `AnalogProbe` stayed a separate class and kept its own switch, because it is
  the only half of the pair that costs anything when idle — its hook runs once a
  frame whether or not a stick moved.

Verified in a run with the disc: `[KF2] analog: on, 3 hook(s)`, alongside
`pacing: 5 hook(s)`, `dither: 12 hook(s)` and `autoreload`, with no replace
conflict — note that the probe's post-hook and auto reload's post-hook are on the
*same* function (`func_8002A550`, the end of stage 3) and both run, since
`HookManager` keeps a list per function and only `Replace` is exclusive. Writing
`kf2.analog.turn=2.5` and `kf2.analog.lookdeadzone=0.3` into `interface.ini` came
back as `(deadzone 0.3, turn x2.5, move x1)` on the next start, which is the
`RuntimeReadyEvent` read landing before `Attach` — the ordering the whole settings
mechanism depends on. With the probe on, the mask table dumped once and the
windows reported the patch driving the game: `look 185 move 279` frames out of
300, `mean |step| 58` against a turn rate of 28, which is the overspeed path (see
the per-frame speed limit above) working exactly as it did as a mod.

### Open: the page is under the fold, and the fix is a section rather than a hoist

The page is in the right *section* and the wrong *place in it*. `Extend` draws
after a section's own content, and the input section's own content is two tab
bars, sixteen binding rows and a reset button — around 450px of a 500px window —
so "Analog sticks" starts below the fold and is only found by scrolling past every
button in the game. Left as it is for now; the options were weighed and two of
three were rejected on their merits:

- **Wrapping the input section** to draw the page above the binding table works —
  `Register` replaces by id, so a wrapper forwarding `Id`/`TitleKey`/`Order` can
  draw first and then delegate — but it has the port taking ownership of a section
  the runtime registers, in a checkout that is gitignored and moves under us. That
  is the same pattern already tried and dropped under "Renaming a runtime section"
  in [PATCHES_AND_MODS.md](PATCHES_AND_MODS.md),
  and it was rejected again here.
- **Moving it to `gameplay`** puts deadzone, curve and invert-Y under a heading
  that means rules of play, and leaves the pane about controls with no sign the
  sticks are configurable at all.
- **A `controls` section of the port's own**, `Order = 1` so it sits beside Input,
  is the one to build: a sidebar entry rather than a hoist, and no wrapper, since
  registering a *new* id needs no patch to the checkout — the `gameplay` lever.
  The intended shape is a tab bar inside it, one tab per control page, which also
  makes the id and the label agree from the start (`controls` / "Controls", and
  "Controles" covers both pt-BR and es-419). Note a tab bar cannot be added to the
  runtime's *own* Input pane: `InputSettingsSection.Draw` opens and closes both of
  its bars inside itself, so a third tab beside Keyboard/Gamepad needs the wrapper
  above. With one page the tab bar should stay off — a lone tab promises siblings
  that do not exist, the same objection as a `SeparatorText` over a lone checkbox,
  and it would sit directly on the rule the page's own `Title` already draws.

The underlying gap is upstream's: **`SettingsRegistry.Extend` has no ordering
argument**, so an extension can only ever land at the bottom of a pane. Worth an
issue, and it is the gap behind all three options above.

### Open: the camera does not feel consistent in every direction

Reported from play and **not yet diagnosed** — the camera's speed does not feel
even across directions, and not all the time. Nothing below is confirmed; these
are the candidates worth measuring first, and three of them are certain to be
*real* effects whether or not they are the one being felt:

1. **The game turns you slower while you walk.** Stage 3 sets the turn rate to
   `0x1C` (28) when a movement button is down and `0x23` (35) when none is, so
   horizontal speed drops by a fifth the moment you move. The patch inherits this
   because it reads the rate out of `0x8019955C` each frame. This is the best fit
   for "not all the time" and it is the game's own rule, so overriding it is a
   decision, not a fix.
2. **Vertical and horizontal are not the same scale, and the ratio moves.** Yaw
   steps by up to `rate` (28 or 35) and pitch by a fixed 32, so the vertical
   speed is constant while the horizontal one changes with what you are doing.
   Standing still the camera is relatively faster sideways than it is walking.
3. **Diagonals reach full deflection sooner than cardinals.** `AxisToByte` gains
   the axis by 1.3 and clamps *per axis*, so a stick pushed to a corner reads
   (1, 1) — radial magnitude 1.41, not 1. `Shape` renormalises the direction
   correctly, but the deadzone and curve are applied to a magnitude that saturates
   at a different point depending on the angle, which makes mid-deflection
   diagonals proportionally quicker than mid-deflection cardinals.
4. **The acceleration ramp is radial and applies to both axes.** Sweeping hard
   sideways speeds the vertical axis up too, so a diagonal flick during a sweep
   is faster than the same flick from rest.

The measurement to take is the patch's own: hold a fixed deflection at eight
directions in turn with `KF2_ANALOG_PROBE=1` and compare mean |yaw step| and the
pitch total, once standing still and once walking. Candidate 1 will show up as
two clean bands and would settle it immediately.

Measured as a mod, loaded alongside `widescreen` (and the dither patch, then still
`mods/nodither`): `loaded 3/3 mod(s), 9 function(s) hooked`, no replace conflict,
300 frames per 10-second window — the pacing floor is untouched. As a patch the
probe reports the same 300 frames per window, now with the frame pacing, the
dither patch and auto reload all hooked as well.

### Open: the twin-stick controls broke after the dragon stone went into the fountain

Reported from play and **not yet reproduced or diagnosed**. Putting the dragon
stone into the fountain — a scripted world event — and the sticks stopped
working afterwards. Unknown so far: whether it was both sticks or one, whether
the D-pad still worked, and whether it survived leaving the area or a reload.
Those three answers narrow it to one of the candidates below on their own, so
they are worth capturing next time before anything else.

What makes this worth writing down rather than guessing at: it has **two
silent bail-outs that a scripted event is exactly the thing to trigger**.

1. **The rate guards.** `BeforeLook` returns when `*(u32*)0x8019955C <= 0` and
   `BeforeMove` when `*(u32*)0x80199558 <= 0` (`Analog.cs:247,291`). They exist so
   the patch never divides a scale by a zero the game is holding, and the game
   plausibly zeroes exactly these while a scripted sequence has the camera. If
   the event leaves either at zero, that axis is dead until something sets it
   back — and the patch would be *reporting* the game's state faithfully rather
   than having a bug of its own. **This is the first thing to check** and it is
   one read each.
2. **The action-mask table.** `Drive` reads the button masks out of
   `0x8006E568`–`0x8006E5D0` every frame rather than hardcoding them, which is
   what makes it follow the control-config screen. If the event rewrites or
   clears that table, `inc`/`dec` come back zero, no button is asserted, and the
   game takes its neither-button branch — which *decays* the velocity instead of
   accumulating it. That reads as controls that are alive but weak and wrong,
   not as controls that are dead, so it is distinguishable from candidate 1 by
   feel alone.
3. **The player action state.** `0x801994E1` drives the jump table at
   `0x80011300`; states other than the ordinary ones take arms that need not call
   `func_80028DB8` and `func_800290D4` at all. If the event parks the player in
   such a state, the patch's hooks simply never run — and neither does the game's
   own control code, so the D-pad would be dead too. **That is the question the
   "did the D-pad still work?" answer settles**, and it is the one case where
   nothing is wrong with the patch.

Cheap next step: reproduce with `KF2_ANALOG_PROBE=1`, which already reports the
control state, and read `0x8019955C` / `0x80199558` / `0x801994E1` at the moment
it breaks. All three candidates are one memory read apart.

## The keyboard layout, and changing a default RecompOne provides

`patches/KeyLayout.cs`. RecompOne's default keyboard bindings are a *console's*
defaults spelled on a keyboard — face buttons on Z X A S, shoulders on Q W E R,
the D-pad on the arrows. That is the right generic answer for a runtime that has
to run any PS1 game and the wrong one for this one, because King's Field walks
**and turns** on the D-pad: the arrows alone are a tank control, and mouse look
in the other hand has nothing sensible to do.

Read through the action-mask table, the pad means:

| pad | action | pad | action |
|---|---|---|---|
| Up / Down | walk forward / back | L1 / R1 | strafe left / right |
| Left / Right | turn | L2 / R2 | look up / down |
| Square | attack | Circle | the in-game menu |
| Cross | the action button: use, open | Triangle | cast |

which rearranges into the layout every first-person game has used since Quake:

```
W A S D  walk and strafe        mouse    turn and look
arrows   walk and turn                   left attacks, right casts, middle uses
Space    attack     F  use      Q cast   Tab  menu
```

**Pitch is the mouse's, and only the mouse's.** The game looks up and down on
L2/R2 and a pad still does, but a keyboard pair for it buys a worse version of
something the mouse does continuously, so the layout leaves those two bindings
empty rather than spending two more keys on them.

### Yes, and it needs no patch to the checkout

`ConfigManager.Game` is public, `Keys` is a settable property and `SaveGame()` is
public, so the port can decide its own bindings. **What matters is when.**

* **`Configure()` runs from Program.cs, before `ConfigManager.Load`.** Load either
  overwrites `Game` from `settings.json`, or — when there is no such file —
  *saves the object it finds in memory*. So writing the bindings before it makes
  them the port's **defaults**, in exactly the relationship RecompOne's own
  defaults have with the file: a fresh install gets them, and after that the
  player's file wins. Nothing is overridden and nothing needs a marker.
* **`Install()` migrates an existing `settings.json`, once.** Anyone who has
  already run the port has a file full of stock bindings, and a default that only
  reaches new installs is not much of a default. The rewrite is guarded twice: it
  happens only if every one of the sixteen bindings is *exactly* stock (one key
  someone chose stops it), and it records `kf2.keys.layout=1` in `interface.ini`
  so that a deliberate return to stock is not undone on the next launch.

Measured, by running the port against a config in each state:

| config | marker | result |
|---|---|---|
| stock | unset | migrated — `Up` becomes `W` |
| a layout the port shipped before | older | migrated to the current one |
| stock | set | untouched: the player asked for stock |
| stock with one key changed | unset | untouched: it is a customised file |
| none at all | — | Load saves the port's layout as its defaults |
| any | — | `KF2_KEYS=fps` overrides both guards |

That second row is not hypothetical: version 1 of the layout had attack and use
swapped (see "A correction: the mask table names the button, not the verb"
below), and fixing it meant bumping
`Version` to 2 and recording v1 in `Superseded`. Without both, a config carrying
the old layout reads as *customised* and would never be corrected. This is the
one piece of bookkeeping a changed default costs.

The marker lives in `interface.ini` rather than in `settings.json` because
`settings.json` is the thing being migrated, and a flag inside it would mean
growing the runtime's own config schema.

**The one thing this cannot do** is change what the runtime's *own* "Reset to
defaults" button under Input resets to — that is `new KeyBindings()` inside
`InputSettingsSection`, and reaching it would mean patching the checkout to hold
an opinion about one game. So the port adds its own pair of buttons instead
(`Kf2.Settings.KeyLayoutPage`): "King's Field layout" and "RecompOne layout",
beside the table they both write.

### The second binding the schema cannot hold

`KeyBindings` is one key per button — a `string`, not a list, unlike
`GamepadBindings`, which is `int[]`. So binding W to Up *takes the up arrow off
it*, and the up arrow is how the in-game menu moves: a straight WASD swap leaves
menus scrolling on W and S while their horizontal movement is still on the
arrows.

The port adds the arrows back the same way the mouse buttons arrive — by ORing
the bit into the word inside `PAD_dr`, where nothing asks which device set it.
Up and Down therefore have two keys each, which the binding table cannot show and
does not need to. It costs one keyboard poll a millisecond, not one per `PAD_dr`,
and it only runs while the port's own layout is the one in place: a player who
went back to stock, or who bound the arrows themselves, has already said what
those keys do.

The bit order is the same swap the mouse buttons use, and the mask dump confirms
it for this pair specifically: `0x8006E590` reads `0x1000`, which is
`Controller.Up` (`0x0010`) with its halves exchanged.

## Mouse look

`patches/Mouse.cs` steers with the mouse, and presses pad buttons with its
buttons. It is **off by default** and its knobs are under Input, below the stick
ones.

**The look half is not a hook.** A mouse and a stick are two ways of asking for
the same thing — the per-frame turn and pitch step — and `Analog.BeforeLook`
already owns that word: it pre-loads the velocity the game is about to accumulate
into and asserts the matching button out of the game's own mask table. So the
mouse hands that hook a number and nothing else, added to the stick's term before
a single `Step` call rounds and carries the remainder. Two devices, one decision,
one write. The one change to the surrounding logic is that the hook now runs with
the sticks switched *off*: keyboard-and-mouse is a scheme of its own, not a
variant of the pad one.

### Three things a mouse is not

* **It is displacement, not a rate.** A stick deflection says "turn at this speed
  while I hold it"; mouse motion says "turn by this much, once". Everything on
  the stick page that shapes a held rate — deadzone, response curve, the
  acceleration ramp — is therefore absent here and deliberately so, and neither
  feeds nor reads the ramp.
* **Its release is not optional.** The game ramps a released look velocity down
  by 3 a frame from a limit of 32 — about eleven frames. On a stick that is a
  defensible feel and is the `CameraInstantStop` option; on a mouse the hand has
  stopped and the camera has not, so the axis is put down the frame motion stops
  whatever that setting says. Hence the second pair of ownership flags in
  `Analog` (`_mouseTurn`, `_mousePitch`): they exist to make the release ignore
  the stick's option, and to hand the axis straight back to the D-pad and L2/R2
  one frame later.
* **The pointer runs out of desktop.** An absolute pointer stops at the edge of
  the screen halfway through a turn, so motion is only motion while the cursor is
  locked to the window. That lock is the only part of this that the runtime had
  to grow — `patches/recompone/0017`.

### The angle scale, and where it comes from

Yaw is 12 bits to the circle (`yaw & 0xFFF`), and the game's own numbers confirm
it: the D-pad's turn rate of `0x1C` a frame at 30 fps is 74°/s, which is the
figure the frame-pacing work already measured. Pitch is in the same units, held
inside ±`0x2BC` — about 62° either side of level.

The default is **0.15° a pixel**, so a quarter turn is about 600 px of movement
at sensitivity 1. Window pixels, not mouse counts: the host reports the pointer
in the window's own space, so a larger window turns slightly slower for the same
movement of the hand. Raw cursor mode takes the desktop's pointer acceleration
out of it, but not that.

Two ceilings the stick does not need:

* **`StepCap`, 1024 yaw units (a quarter turn) a frame.** The stick's ceiling is
  four times the game's own per-frame rate, which is generous for something
  asking for a *rate* and much too small for a flick, which arrives as three or
  four very large frames. A cap is still wanted: a mouse knocked off the desk
  should not spin the view eleven times.
* **`StaleMs`, 250 ms.** The accumulator fills whenever the pointer moves and is
  only spent by a routine that runs while the game is walking around. The in-game
  menu blocks inside its own call, an area load takes seconds — and coming back
  out with every pixel moved in the meantime still queued would swing the camera
  through whatever the hand did while the player was reading. Motion older than
  this is dropped rather than applied.

### The buttons go through PAD_dr, and that is the whole design

`PadReadEvent` fires inside the BIOS's `PAD_dr` and its `Buttons` field is read
back, so a held mouse button is ORed into the word the game is *about* to read.
That is worth more than a hook would be:

* it needs no address and no mask — nothing here knows what "attack" is;
* the **game's own control-config screen** decides what the button does, exactly
  as it does for the pad, so remapping in-game moves the mouse with it;
* it works in the menus, on the title screen and anywhere else the game reads the
  pad, none of which the control hooks reach.

The buffer is active low and carries the two button bytes the opposite way round
from `Controller`'s layout (libetc hands the game `~buffer`), which is the same
swap the mask table is stored under — so pressing a button is *clearing* the
swapped bit. See `AnalogProbe.Buttons` for the same rule stated from the other
end.

The listener is attached when the pointer is captured and dropped when it is let
go. `PAD_dr` is the busiest call in this game — every screen transition
busy-waits on it, hundreds of thousands of times a second — and a listener on
that bus is a cost every player would pay for a device most of them are not
using.

**Which pad buttons the defaults are, and why.** `func_8002957C` — the routine
`NOTES` already listed as "player control: attacks, items, magic" — reads exactly
four entries of the action-mask table, against the pad word at `0x80199554` and
the previous frame's at `0x80199556`:

| mask word | entry | button | tested | what the branch does | what it is |
|---|---|---|---|---|---|
| `0x8006E570` | 2 | Square | just pressed | `func_800262C8(0)` | **attack** |
| `0x8006E568` | 0 | Cross | **held** (this frame and last) | subtracts 500 a frame from two `u16` counters at `0x8019942E` and `0x80199432`, clamping at 0, and sets the state byte `0x8019941F` to 1, or to `0x28` when a counter reaches zero | **the action button** |
| `0x8006E574` | 3 | Triangle | just pressed | `func_80027DC0`, which indexes 26-byte records at `0x8019C5EC` and compares `0x8019942C` against the record's `+0x16` before it runs | **cast** |
| `0x8006E578` | 4 | Select | just pressed | the same routine on a second slot, plus `func_800197D4` / `func_800474D0` through a table at `0x8009B52C` | the second slot |

So the defaults are **left = Square, right = Triangle, middle = Cross**, and
Circle is deliberately not among them — it opens the in-game menu (entry 1,
`0x8006E56C`), and a menu on a mouse button under a captured pointer is a trap.

### A correction: the mask table names the button, not the verb

The first version of this shipped with **attack and use swapped**, and the way it
went wrong is worth keeping.

The branches above are real and were read out of the emitted code. The column on
the right was not: it was inferred. A button tested *while held* that drains a
pair of counters reads like a swing and its charge; a single press calling one
routine reads like using what is in front of you. Both readings are plausible and
the second one is backwards — **Square attacks and Cross is the action button**,
which one minute of play settles and no amount of reading the branch does.

That is the same failure the analog patch had on the pitch sign, in the same
place: the mask table gives the button behind an action and says nothing about
what the action *is*, and a plausible story about a branch is not evidence. What
the counters at `0x8019942E` and `0x80199432` actually are is now unidentified
again — they are not a swing charge, whatever else they may be.

It is also why the settings page says "Left button → Square" rather than "Left
button → Attack". The port presses a pad button; the game's own control-config
screen decides the verb, and the port would be lying if it claimed otherwise.

### Capture, and getting out again

Escape, by default. The game's own keymap is Z X A S Q W E R F G, Enter, right
shift and the arrows, and the host claims F1 and F11, so Escape is both free and
where a hand goes to get a pointer back. Three things release it:

* pressing the key again;
* opening any popup — settings, the mods list, the disc picker are all drawn over
  a running game and all want a cursor. This is the one state change nothing
  announces, so it is polled (throttled to 1 ms) off the pad listener, which is
  the one thing the game keeps doing wherever it is;
* switching mouse look off in the settings page.

The key comes off `KeyboardEvent` rather than being polled through
`HostWindow.IsKeyDown`, unlike the debug mod's hotkeys, and that is not a
preference: every hook this port owns is in the walking-around part of the game,
and a pointer captured and then swallowed by the in-game menu has to be
releasable *from inside it*.

### What the runtime had to grow: `0017`

`InputManager` owns the `IMouse` and is `internal`, so the port could not reach
the cursor at all. The patch adds three things to it and forwards them from
`HostWindow`, beside the `IsKeyDown` that already plays this role for the
keyboard:

* `MouseCaptured` — `CursorMode.Raw` where the platform has it, `Disabled`
  otherwise. Both make GLFW report an unbounded virtual position; Raw is the same
  thing with the desktop's pointer acceleration taken out. It is a property that
  can be *read back*, because a platform may refuse, and `Mouse.SetCaptured`
  trusts the read rather than the write.
* `TakeMouseMotion` — the accumulated difference between successive `MouseMove`
  callbacks, cleared by the call. It accumulates whether or not anything has
  asked for capture (one subtraction per callback) and is zeroed on the mode
  change, because the pointer teleports when the cursor is locked or released and
  that jump is not motion anyone asked for.
* `IsMouseButtonDown`.

`Shutdown` gives the cursor back, so a crash on the way out does not leave a
hidden pointer behind.

### What is measured, and what is not

**Measured, and the whole path ran.** `KF2_ANALOG_PROBE=1` now reports the
mouse's mean turn and pitch step per frame beside the stick's, and four
consecutive ten-second windows of real play read:

```
mouse 135 (mean |turn| 72.6, |pitch|  6.3, capture on) | yaw stepped 131/300 frames, mean |step| 74.82, pitch total  820
mouse 191 (mean |turn| 72.0, |pitch| 16.4, capture on) | yaw stepped 181/300 frames, mean |step| 75.85, pitch total 2955
mouse 141 (mean |turn| 61.6, |pitch| 11.1, capture on) | yaw stepped 137/300 frames, mean |step| 63.31, pitch total 1575
mouse 189 (mean |turn| 64.0, |pitch| 10.7, capture on) | yaw stepped 187/300 frames, mean |step| 64.58, pitch total 2017
```

Three claims come out of that, and they are the ones that matter:

* **The frames agree.** 135 frames of mouse motion against 131 frames in which
  the yaw angle actually moved, 191 against 181, 141 against 137, 189 against
  187. The shortfall is the fractional carry holding a small step back a frame,
  which is what it is for.
* **The amount agrees.** The angle the mouse asked for and the angle the game
  applied are within a few percent in every window — 72.6 asked, 74.82 applied;
  61.6 asked, 63.31 applied. So the pixels-to-units conversion, the pre-loaded
  velocity and the game's own accumulate are all landing where the arithmetic
  says.
* **The overspeed branch is doing its job.** `turnVel -70` sitting in memory with
  the turn rate at `0x1C`: no button on the pad can produce that, which is
  `Drive`'s no-button branch — the same mechanism the stick's sensitivity above
  1.0 uses.

Also measured: the host accepts the cursor lock and releases it again on this
machine (`MouseAvailable: True`, `capture accepted: True`, and false again after
the release), the capture key engages it from the real key path, and the four
mask-table reads in `func_8002957C` above are read out of the emitted code rather
than guessed.

**Not measured, and it needs a person to say.** Whether 0.15°/px is the right
*feel*, and whether the pitch runs the right way round — the sign is inherited
from the stick's, and that one the analog work had to fix by playing it, so it is
exactly the sort of thing a counter cannot answer. The mouse buttons have not
been pressed at a door or a monster: the injection is one `&=` on a word the
BIOS is about to hand over, but nobody has watched a swing come out of it.

It stays **off by default** anyway, and for a reason the measurements do not
touch: a pointer that disappears into the game unasked is worse than one switch
to find.

