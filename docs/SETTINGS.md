# Port settings in the game's own menu

**PORT SETTINGS**, in place of RETURN at the bottom of the in-game menu, opens
pages of the port's settings drawn with the game's own menu routines. Ported from
Verdite3 (its `docs/SETTINGS.md` is the model's full write-up) on 2026-10-09. The
machinery is the same code renamed; the list and the page are this game's. How the
menu was read is "RETURN is the chooser's last item, and the port settings page
takes it" in [GAME_INTERNALS.md](GAME_INTERNALS.md).

**Measured, not judged**: the page has never been looked at. Whether it reads
well, and whether the digits draw (see below), is the user's to check.

## The rules

1. **Nothing is written unless a person changed that setting.** Opening the page,
   moving the cursor, or changing a value and changing it back write nothing.
2. **No key means the default.** A step back onto the default removes the key, so a
   player who never chose follows the default if a release changes it.
3. **A write touches only the keys that changed.**
4. **The page stages its changes.** They apply live and reach `interface.ini` only
   on SAVE CHANGES. DISCARD CHANGES, or quitting with the page open, puts the live
   values back and writes nothing.
5. **A setting a `KF2_*` variable set is locked in game** (drawn darker, refused).
6. **A value off the page's steps is shown as it is** until the player moves it.
7. **Keys and their text are what the Settings window writes** (`True`/`False` for
   a switch, which is what `GetBool` reads), so no player's file is migrated.
8. **Nothing goes into game memory or the card.**

`interface.ini` belongs to the host thread and the menu runs on the game thread, so
every write goes through `SettingsStore`, which queues it for the host's next frame.

## The pieces

| file | what |
|---|---|
| `tools/verdite-core/src/Settings/PortSetting.cs` | one setting: key, variables, menu label, steps and their text, default, live getter and setter, how it is stored |
| `patches/PortSettings.cs` | the list, the four pages, the combined rows; checked at start-up (labels against the font and the box) and at `RuntimeReadyEvent` (every declared default is what the feature boots at) |
| `tools/verdite-core/src/Settings/SettingsStore.cs` | the one writer, pumped on the host thread by an always-open panel that draws nothing (`Panels.kf2settingsstore`) |
| `tools/verdite-core/src/Settings/SettingsSession.cs` | the page without its drawing: open, step, reset, save, discard |
| `tools/verdite-core/src/Settings/WindowMode.cs` | windowed, fullscreen or borderless: the runtime's two keys, applied on the host thread |
| `tools/verdite-core/src/Settings/MenuFont.cs` | text to the label font's codes |
| `patches/SettingsPage.cs` | the item, the loop, the drawing, the question |

The Settings window's own pages (`patches/settings/*Page.cs`) are unchanged and
write the same keys; the list does not draw into the window, as Verdite3's does.
The shell's `settings` verb drives a session from the game thread (`settings`,
`settings open|step <key> <dir>|reset <key>|save|discard`).

## The page

| button | does |
|---|---|
| Up / Down | the row, wrapping (sound `0x10`) |
| Left / Right, Cross | the value one step, going round (sound `0x11`) |
| L1 / R1 | the page, wrapping, back to the first row |
| Circle / Start | leave (sound `0x12`); with changes, SAVE CHANGES / DISCARD CHANGES / RETURN first |

At the question, RETURN or Circle goes back to the page with the changes still
staged. The page last shown is kept for the next open. Seven rows fit under the
header (Y 45..201). There is no hint row (the game's menu has none).

**The menu pointer reaches the page** (2026-10-09, Verdite3's `48db447`): the page hands
its drawn rows to `MenuMouse.Rows` (each box at the record's X - 6, Y - 6, the item
template's 124x24), with the header and the value column as the page's own area. The
page's loop keeps its cursor in a register, so hover reaches it as one Up or Down a pad
read until the drawn cursor is under the pointer (three pushes that do not move it give
up until the pointer moves), a click on the selected row steps its value as Right does,
and a click clear of the page backs out as Circle does; the question's three rows go
through the chooser as the drawer's do. The shell's `point <x> <y>|left|right|off` puts
a pointer at game pixels in place of the host's. Measured (`KF2_AUTOSTART=1`, Circle, Up,
Cross, then `point`): pointing at row 3 from 0 took three pushes, a click there stepped
HUD to OFF, pointing at row 1 moved it back, a click at (5,230) opened the question.
**Not felt with a real mouse.**

| page | row | values | stands for |
|---|---|---|---|
| DISPLAY | DISPLAY | WINDOWED, FULLSCREEN, BORDERLESS | the runtime's `Fullscreen` and `Borderless` |
| | RESOLUTION | 240P … 1920P | `RenderScale` 1..8 |
| | ASPECT | 4/3, 16/10, 16/9, 21/9 | `kf2.widescreen.aspect` |
| | HUD AT EDGES | OFF, ON | `kf2.widescreen.hud`, dimmed at 4/3 |
| | FRAME RATE | ORIGINAL, 30 … 360, UNCAPPED | `kf2.framepacing.fps` and the five `kf2.smoothing.*` keys: ORIGINAL is the setup's 20 fps with the smoothing off, any other rate smooths; a rate with the smoothing off (the Settings window) shows `NO SMOOTH` |
| GRAPHICS | TEXTURE FILTER | OFF, TRILINEAR, 2X … 16X | `kf2.mipmaps.on` and `kf2.aniso.level` |
| | PER-PIXEL LIGHT | OFF, ON | `kf2.perpixel.on` |
| | AMB. OCCLUSION | OFF, LOW, MEDIUM, HIGH | `kf2.ao.on` and `kf2.ao.quality` |
| | SHADING | DITHER, NONE, SMOOTH | `kf2.nodither.on` and `kf2.truecolor.on`, true color the master, as `ShadingPage` |
| WORLD | RENDER DISTANCE | ORIGINAL, ENHANCED | `kf2.renderdistance`: 10.5 tiles (off) or 15 (its maximum; not judged) |
| | FOG | CORNERS, PIXEL DEPTH, DISTANCE | `kf2.fog`: the game's fog at the corners, by pixel depth, or by distance from the eye (the default since 2026-10-09; a kept `kf2.fog` still wins) (`KF2_FOG`; RENDERING, "Fog by distance from the eye") |
| | WATER | ORIGINAL, ENHANCED, FULL | `kf2.murk.on`, `kf2.waves.on`, `kf2.ssr.planar`: ENHANCED murk and waves, FULL the planar reflection too (the default) |
| GAMEPLAY | RELOAD ON DEATH | OFF, ON | `kf2.autoreload.enabled` |
| | COMPARE GEAR | OFF, ON | `kf2.gearcompare.enabled` |
| | ENEMY TURN | ORIGINAL, X1.25 … X8 | `kf2.enemyturn` (PATCHES_AND_MODS, "Enemies turn faster"); tracking while standing stays in the Settings window |
| | MAP | OFF, WHOLE AREA, AS YOU GO | `kf2.map.on` and `kf2.map.fog`, the map the master, as `MapPage`; OFF leaves the fog choice |
| CONTROLS | CONTROLS | 2D, 2D ACCEL, 3D | `kf2.controls` (`True` is 3D) and `kf2.analog.accel`: the setup's turning page as a row; 3D clears the acceleration, as the setup's tank option does (INPUT, "2D and 3D controls") |
| | INVERTED CAMERA | OFF, ON | `kf2.analog.invertpitch`; dimmed in 3D |
| | STICK SENS | X0.5 … X3 | `kf2.analog.turn` and `kf2.analog.pitch`, both to the step; a pair set apart shows CUSTOM |
| | MOUSE SENS | X0.25 … X5 | `kf2.mouse.turn` and `kf2.mouse.look`, as STICK SENS; dimmed only under `KF2_MOUSE=0` |
| | GYRO | OFF, ON | `kf2.itemturn.gyro` and `kf2.gyroaim.on`: a held item (picked up or previewed) turns and a drawn bow aims with the pad's gyroscope (INPUT, "Turning a picked-up item", "Rumble and gyro aim with a drawn bow"); a pair set apart shows CUSTOM |
| | RUMBLE | OFF, ON, HD | `kf2.rumble.on`, `kf2.rumble.hd`; OFF leaves the HD choice |
| SOUND | 3D AUDIO | ORIGINAL, SPEAKERS, HEADPHONES | `kf2.audio.positional` (AUDIO, "Positional audio") |

The minimap has no saved key (N toggles it for the session).

### What the page is for

The user's rule (2026-10-13): a row is on the page when a player would choose it for
their hardware or their taste (the pad's gyro and rumble, speakers or headphones, how
fast enemies turn, how much of the map to show), not because the port has a switch for
it. The renderer's fidelity switches (perspective, sub-pixel, Z-buffer, even fog,
interpolation, reverb) stay in the Settings window, and **what can be inferred is not
asked**: ORIGINAL frame rate is the original motion, so the smoothing goes off with it,
as in Verdite3, where the smoothers follow pacing. Settled, not to be offered again:

* **Message fade length** is never a port setting (Settings window only).
* **Instant mouse look** is not a setting at all: always on (INPUT, "The mouse leads the tick").
* **The menu pointer** is not a real choice; it stays in the Settings window.
* **Smoothing** has no row of its own; FRAME RATE carries it.
* **Mouse look and the menu pointer** are not settings at all, anywhere: a mouse is
  used or it is not, and a player after the original experience is on a pad anyway.
  Both are always on, their saved keys (`kf2.mouse.on`, `kf2.menumouse.on`) are no
  longer read, the setup has no Mouse group (it wrote both off for Original, which
  stranded the mouse with no control to bring it back), and the Settings window's
  two checkboxes are gone. `KF2_MOUSE=0` and `KF2_MENUMOUSE=0` are the comparisons.

## Measured (2026-10-09)

An isolated run directory (the build, the cards, `settings.json`, the player's
`interface.ini`), `KF2_AUTOSTART=1`, area 1, driven with the shell's `press` and
`KF2_SETTINGSPAGE_PROBE=1`; `interface.ini` diffed against the copy, `Panels.*`
aside.

| check | result |
|---|---|
| boot | `18 declared on 4 pages, 14 kept in interface.ini, 0 default(s) wrong`; `PORT SETTINGS in place of RETURN` |
| Circle, Up (wraps to the bottom item), Cross | the page opened on DISPLAY |
| R1 round all four pages, Circle | `left, nothing changed`, identical |
| PER-PIXEL LIGHT Right, Circle, DISCARD CHANGES | `discarded`, identical |
| RESOLUTION Right, SAVE CHANGES | one line, `RenderScale=6` to `7` |
| RELOAD ON DEATH Right, SAVE | one line added, `kf2.autoreload.enabled=False` |
| RELOAD ON DEATH back to ON, SAVE | `back to its default`, the key removed |
| Circle on the menu after the page | the menu closed; the managed stack back in stage 13 |

No exception. **Not judged**: how the page looks; whether the label font really has
digits where Verdite3's does (no string of this game's uses them, so a number
drawing as something else is the first thing to look for); the window changing
mode.

## Onboarding

The first-run setup, shown by the launcher **while the game is being built** and
holding the game back until both are done, and run again from the settings page's
DISPLAY page (RUN SETUP, its last row, not a setting). The welcome has three
presets as buttons in its body, and **Customise…** as a fourth. Its footer holds
only Skip setup (launcher) or Cancel (in game):

* **Recommended**: Modern in every group, with the display suggested from the
  monitor (`Onboarding.Suggest`). Window mode has no suggestion and stays where it
  starts.
* **Original++**: Original++ in every group, with the display suggested from the
  monitor, as Recommended.
* **Original**: Original in every group, and each display field's `original`
  option: 4:3, 240p (1x) and 20 fps. Window mode is left alone.
* **Customise…**: walks the display, then the tiers, then the turning page, then the
  summary. It starts from what the player had set, not from a preset clicked earlier.

A preset goes to the summary. Its top button, **Adjust these choices…**, goes to
the display page with the preset's choices kept. The summary's Back returns to
the welcome. Every page puts keyboard and pad focus on its first control: the
welcome's first button, the summary's Adjust or Finish, the report's Close. Basic (one
style for each of Controls, Presentation and Gameplay) and Advanced (one for each
group inside them) are a switch at the top of the tiers page. Every group has
three tiers:

* **Original**: the game as the PlayStation ran it.
* **Original++**: that look and feel, with what the hardware got wrong fixed, and
  the controls and conveniences that keep the game's own pace, and the last save
  reloaded on death.
* **Modern**: the port's own choices for each feature already judged: the HUD at the edges and the world drawn out to 15 tiles (the
  View group), and the turning page's twin sticks with acceleration. The remaster
  packs stay off, as they ship; murky water is off in every tier and by default.

After the tiers comes **the turning page** (`pages` in the spec, `"id": "turning"`),
before the summary. It asks how the player turns, and how fast the creatures turn
toward them: three options (tank controls, twin stick, and twin stick with
acceleration), and an **Enemy turn speed** slider from x1 to x8 under them, with
the original's x1 labelled. Each option writes `kf2.controls` and `kf2.analog.accel`;
the accelerated option also moves the slider to x2.2, since holding the right stick
out speeds the turn up to that much. Its presets follow the tier the player chose:
Original and Original++ pick tank controls at x1, Modern picks twin stick with
acceleration at x2.2. The page **follows the Gameplay tier** until the player touches
it, so changing that tier moves it too; once touched it is left alone. Its text
explains the problem (the turn was fixed on the PlayStation, and creatures were
tuned to it) and why the port's faster turn needs faster creatures.

### Where setup lives

* `tools/verdite-core/src/Onboarding/` (`Onboarding`, `OnboardingPopup`): the
  game-agnostic half. It is compiled twice, by the launcher (`Launcher.targets`
  includes the directory) and as payload into the game, so every type is
  `internal` and the two copies share nothing but the files.
* `config/onboarding.json`: this game's half. It holds the display fields, the
  categories, groups and tiers (each tier a `{key: text | null}` map over the same
  keys in all three), `defaults` (what each key means when absent), and every
  string in en, pt-BR and es-419. A display field may carry `original`, the option
  index the Original preset picks; absent, the preset leaves that field where it
  starts. An index out of range is logged at load by `OnboardingSpec.Check`. It
  ships in `content/config`, so it is part of the build key: a change to it
  rebuilds.
* `patches/Setup.cs`: RUN SETUP, the live apply, and the shell's `setup` verb,
  including `setup preset original|recommended|original++`. Its live setters cover
  `kf2.enemyturn` (`EnemyTurn.SetMultiplier`) and `kf2.analog.accel`
  (`Analog.LookAccel`) as well as the tiers' keys; the boot check also covers the
  turning page's option keys and its slider.
* `Launcher.cs`: `Onboarding.Due` decides whether setup is shown. `BuildGame` opens
  the setup in place of the build popup and shows the build as a strip at its
  foot, and it pumps until **both** the build and the setup are done. If the build
  fails while setup is open, setup stays up, its strip says the build failed, the
  choices are saved on Finish, and the failure popup follows. On a warm
  launch with setup due, it runs on its own (`RunSetup`).

### What setup writes, and when

* **Due** while `verdite.setup` in `interface.ini` is below the spec's `version`,
  **except for a player who already has settings**: no flag, and any stored key
  under the spec's `existing` prefix (`kf2.`). The game writes `kf2.keys.layout`
  on its first boot, so anyone who has ever played is not asked; their settings
  are theirs, and RUN SETUP is there. So a fresh install sees it once, and a
  player who finished or skipped it (the flag) sees it again when `version` is
  raised; a player who predates setup is never asked. `VERDITE2_SETUP=1` shows it
  regardless.
* The page's own rules: a value equal to its declared default **removes** the key,
  and a key already meaning the value is left alone. `null` in a tier is the
  default, so Modern writes nothing and removes what Original wrote.
* **Detecting** where a player is (a re-run starts there): a group matches a tier
  when every key of the tier already means its value. Original++ water is Original
  water and Original++ motion is Modern motion, so a group can match two tiers.
  A category therefore takes a tier every one of its groups matches (the later
  tier first), so a fresh install reads as Modern throughout rather than "Custom".
  A group matching nothing is **Custom**, and is left alone unless a tier is chosen.
* **Only a first run** (no flag, no settings) starts the display on the monitor:
  its aspect, the largest render scale its height holds, the frame rate nearest
  its refresh rate (`$refresh` in a value, the display's rate when applied, is
  still understood; this game no longer uses it). Every other run, a version
  bump or `VERDITE2_SETUP=1` included, starts on what is set, so clicking through
  changes nothing the player chose. Window mode has no suggestion.
* **The frame rate is a slider** (`"slider": true` on a display field), its
  positions the options in order, as the Settings window's: 20 to 240 at the
  window's own stops, then Uncapped; 20 is labelled the original, and 30 is plain.
  A value matching no option on opening (72, say, from the game's page) gets one
  extra position or list entry before the options, "Keep current (72)". That is
  where the control starts and what it leaves alone until it is moved; the handle
  and the label agree.
* **Every tier's description is shown**, under its button: the chosen one in full
  colour, the others dimmed. The chosen button is also marked "> " (the font has
  no bullet glyph), so the choice is not colour alone. **Tiers that write the same
  keys are one button**, named with a slash ("Original / Original++"), its text the
  first tier's: water and the map (Original and Original++), and motion, comparing
  gear and death (Original++ and Modern). The spec keeps the two texts identical so
  they cannot drift. A group or category that starts Custom gets a **Keep as is**
  button before the tiers, which puts back what it started at.
* **A key every tier writes alike is a mistake in the spec**, logged at load:
  `null` in all three would put the player's own value back to the default
  whichever tier they chose, and read the group as Custom until then. That was
  the map's fog of war (`kf2.map.fog`), now out of the map group. The runtime
  fills `Fullscreen=True` at boot, so the field's `default` (fullscreen) is where
  it starts while no key is written yet; windowed writes `False` explicitly for
  the same reason.
* **Live in the game**: the window keys are applied by core
  (`HostWindow.SetFullscreen`). Every other changed key goes to
  `Onboarding.LiveApply`: the Settings window's setter for the keys the game's
  page does not list, else the `PortSetting`'s `Apply`. A key with neither, or an
  `AtBoot` one, is listed at the end as taking effect at the next launch. In the
  launcher nothing is live: every key is read at boot.
* **Checked against the code at boot** (`Setup.Check`, after every feature has
  read its saved value): each key the spec writes has a live setter, and each
  key not saved reads what `defaults` says it means. A feature whose default
  changes without the spec would make Modern write a key and a fresh install
  read as Custom; it prints `[KF2] setup check: <key> reads X with nothing saved,
  but defaults says Y`. A key whose override variable is set is skipped.
* RUN SETUP puts back anything the page had staged (setup chooses everything
  again), draws the page without reading buttons while setup is up, and opens a
  fresh session on what setup left.

### Setup measured (2026-10-10)

* Developer run, area 1, the shell's `setup`: the state read `Modern` for every
  group the player had left at defaults, and `-1` for the map (fog of war off) and
  the frame rate (165). `setup apply original` changed 31 keys, **all live**, with
  none left for the next launch; `settings` read every one back at its Original
  value (aniso 1, per-pixel 0, fog 0, mouse 0, rumble 0 ...).
  `setup apply modern` changed 30 back and left **none** of the group keys in
  `interface.ini`.
* Packaged launcher, empty `VERDITE2_DATA` (a `settings.json` holding only the
  disc): `onboarding.json` was found in `content/config`, the build finished
  (`builds/<key>/KingsField2.dll`), and the process waited with no `[KF2]` line,
  so the game was held until setup closes.

### Setup measured (2026-10-11)

* The boot check, from a directory with no `interface.ini`: `35 key(s), 0
  problem(s)`. With `kf2.ao.on` and `kf2.audio.interp` given wrong defaults in a
  copy of the spec, it named both (`reads 1 ... defaults says False`, `reads 2
  ... defaults says 1`); with `kf2.map.fog` put back as `null` in every map tier,
  the load named it.
* Built launcher, `VERDITE2_DATA` holding only the disc and
  `kf2.keys.layout=3`: it built and went straight to the game (`[KF2]` lines), no
  setup. The same build with an empty `interface.ini`: no `[KF2]` line in 30 s,
  held for setup.

**Not judged:** the frame-rate slider's look and its handling by pad, the pages' look and wording, pad navigation, and whether each tier
looks the way its name promises. Clicking through to Finish in the launcher (and
the game then starting) has not been watched either.

### Setup measured (2026-10-11, the presets)

* Developer run at the title, the shell: `setup preset original` changed 33 keys,
  all live, none left for the next launch: RenderScale 1,
  `kf2.widescreen.aspect` 1.3333334, `kf2.framepacing.fps` 20, and every group key
  at its Original value. `setup` then read every group at 0 and the display at
  0/0/0, with the window unchanged at 1.
* `setup preset recommended` changed the same 33 back: the monitor's suggestion of
  render scale 6, 165 fps and 16:9, and every group key at Modern. `setup` read
  every group at 2 (Modern).
* Boot check: `35 key(s), 0 problem(s)`.
* Both builds, the game and the launcher, compile the new popup.

**Not judged:** the welcome's two buttons, the description columns under the tier
buttons, the "> " mark, where keyboard focus lands, and the Keep as is button. The
popup has not been looked at.

### Setup measured (2026-10-11, Original++'s mouse aim and reload)

**Superseded (2026-10-13):** the mouse aim part below is gone from the setup and
the settings window; the tiers wrote only `kf2.mouse.on` and `kf2.menumouse.on`,
and since the same day the Mouse group is gone too (see "What the page is for").
See INPUT, "Mouse aim is not a setting". The reload part still stands.

* Original++'s mouse group writes `kf2.mouse.aim` True, `kf2.mouse.aimline` and
  `kf2.mouse.aimdot` False, and leaves mouse look on; Original and Modern put all
  three back to their defaults (aim off, line and dot on). On death, Original++ now
  reloads, so the group is one button, Original++ / Modern. The line and dot have
  live setters in `Setup.Window` (`KF2_MOUSE_AIM_LINE`, `KF2_MOUSE_AIM_DOT`).
* Developer run, the shell: boot check `38 key(s), 0 problem(s)`; `setup apply
  original++` then `setup` read every group at 1, and `setup apply modern` every
  group at 2. The line and dot were already off in that `interface.ini`, so the
  run proved the detection and the reset, not the write to False.

**Not judged:** mouse aim as Original++'s default has never been played through a
first run.

### Setup reworked (2026-10-13)

* **The welcome's choices were split between body and footer.** Two presets sat
  in the body and Customise in the footer where Next is, with Skip where Back is
  on the other pages. Recommended, Original++, Original and Customise… are now all body buttons,
  and the footer holds only Skip setup (launcher) or Cancel (in game).
* **There was no Original++ preset.** Original++ now has one, with the display
  suggested from the monitor like Recommended, so the three tiers each have a
  preset.
* **A preset could not be adjusted.** It went to the summary with nothing to
  change it. The summary's top button, Adjust these choices…, now opens the
  display page with the preset's choices kept. Customise… starts from what the
  player had set, not from a preset clicked earlier.
* **A pad had no focus past the welcome.** Every page now puts focus on its first
  control (the welcome's first button, the summary's Adjust or Finish, the
  report's Close).
* **A failed build lost the choices.** The failure closed setup, so anything the
  player had picked was gone. Setup now stays up with the build failed in its
  strip, the choices are saved on Finish, and the failure popup follows.
* **The middle tier is named Original++**, not Faithful, in every language and
  in the shell (`setup apply original++`); its localisation keys are
  `verdite.setup.originalplus` and `verdite.setup.tier.originalplus`.
* The shell's `setup` verb gains `setup preset original++`, beside original and
  recommended.
* The launcher's disc picker (step 3 of "First run" in `docs/PACKAGING.md`) is
  retitled and reworded by the launcher, and Cancel on it during the boot wait
  quits.

**Not judged:** none of it has been looked at or clicked through; measured only by
the build.

### Setup's turning page (2026-10-13)

* **A page after the tiers.** The turning page (`pages` in `config/onboarding.json`,
  between the tiers and the summary) owns `kf2.controls`, which the movement group
  no longer writes. Its three options are tank controls (3D), twin stick (2D), and
  twin stick (2D) with acceleration; the slider is **Enemy turn speed**, x1 to x8,
  format `x%.2f`, labelled "x1 (original)" at its bottom. Presets: Original and
  Original++ pick tank at x1, Modern picks twin stick with acceleration at x2.2. The
  page follows the Gameplay tier until touched.
* **The movement group loses `kf2.controls`.** It keeps only `kf2.analog.on`, and its
  three descriptions no longer say 3D or 2D: Original is the D-pad only, as the
  PlayStation had; Original++ and Modern (the same text) have the analog sticks on,
  and how they turn is chosen on the turning page.
* **A View group** (presentation, last): `kf2.widescreen.hud` and `kf2.renderdistance`.
  Original and Original++ leave both at the game's own (null, the HUD in the middle
  and the view distance as released); Modern writes the HUD at the edges and render
  distance 15. Modern no longer leaves those two off.
* **Murky water is off in every tier and by default.** `kf2.murk.on` is out of the
  water group's tiers and out of `defaults`, and `Murk.DefaultOn` is `false`, so
  nothing writes the key and a fresh install reads it off. The murk is still on the
  `murk` shell verb and `KF2_MURK=1`.
* **Defaults added** for the four keys the page and View group write:
  `kf2.analog.accel` `False`, `kf2.enemyturn` `1`, `kf2.widescreen.hud` `False` and
  `kf2.renderdistance` `10.5` (the stock distance). `kf2.controls` keeps `False`
  (twin stick, the page's untouched default).
* **Original++ wording.** The tier-1 summaries of Controls, Presentation and Gameplay
  now say the game is as intended with the hardware's faults fixed and a higher frame
  rate, and that turn speed is the original's. The Modern summaries of Controls,
  Presentation and Gameplay say what Modern now turns on, including the faster turn
  and the View group.
* **Measured** through the shell, `setup` now reporting each page's detected
  option and slider value: boot check `41 key(s), 0 problem(s)`; `setup preset
  recommended` read every group at 2 and the page at option 2, x2.2; `setup preset
  original++` every group at 1 and the page at option 0, x1, writing
  `kf2.controls`, `kf2.analog.accel` and `kf2.enemyturn` live.

**Not judged:** none of it has been looked at or clicked through; measured only by
the build.
