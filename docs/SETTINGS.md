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
| | FRAME RATE | 30 … 360, UNCAPPED | `kf2.framepacing.fps`; this game's pacing has no "original", so there is no ORIGINAL step |
| GRAPHICS | TEXTURE FILTER | OFF, TRILINEAR, 2X … 16X | `kf2.mipmaps.on` and `kf2.aniso.level` |
| | PER-PIXEL LIGHT | OFF, ON | `kf2.perpixel.on` |
| | AMB. OCCLUSION | OFF, LOW, MEDIUM, HIGH | `kf2.ao.on` and `kf2.ao.quality` |
| WORLD | RENDER DISTANCE | ORIGINAL, ENHANCED | `kf2.renderdistance`: 10.5 tiles (off) or 15 (its maximum; not judged) |
| | FOG | CORNERS, PIXEL DEPTH, DISTANCE | `kf2.fog`: the game's fog at the corners, by pixel depth, or by distance from the eye (the default since 2026-10-09; a kept `kf2.fog` still wins) (`KF2_FOG`; RENDERING, "Fog by distance from the eye") |
| | WATER | ORIGINAL, ENHANCED, FULL | `kf2.murk.on`, `kf2.waves.on`, `kf2.ssr.planar`: ENHANCED murk and waves, FULL the planar reflection too (the default) |
| GAMEPLAY | CONTROLS | 2D, 3D | `kf2.controls` (`True` is 3D): one stick, as tank controls (INPUT, "2D and 3D controls") |
| | INVERTED CAMERA | OFF, ON | `kf2.analog.invertpitch`; dimmed in 3D |
| | RELOAD ON DEATH | OFF, ON | `kf2.autoreload.enabled` |
| | COMPARE GEAR | OFF, ON | `kf2.gearcompare.enabled` |
| | MOUSE LOOK | OFF, LOOK, AIM | `kf2.mouse.on` and `kf2.mouse.aim`: AIM is mouse aim, the mouse moves a reticle and the player turns toward it (INPUT, "Mouse aim: the hand points, the player turns"); AIM turns mouse look on |
| | GYRO | OFF, ON | `kf2.gyroaim.on`: a drawn bow aims with the pad's gyroscope (INPUT, "Rumble and gyro aim with a drawn bow") |
| | RUMBLE | OFF, ON, HD | `kf2.rumble.on`, `kf2.rumble.hd`; OFF leaves the HD choice |

CONTROLS (2D, 3D; `kf2.controls`, `KF2_TANK`) sits first on GAMEPLAY; see "2D and 3D
controls" in `docs/INPUT.md`. The minimap has no saved key (N toggles it for the session).

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
DISPLAY page (RUN SETUP, its last row, not a setting). It asks the display first
(window mode, render resolution, aspect ratio, frame rate), then Basic (one style
for each of Controls, Presentation and Gameplay) or Advanced (one for each group
inside them). Every group has three tiers:

* **Original**: the game as the PlayStation ran it.
* **Faithful**: that look and feel, with what the hardware got wrong fixed;
  nothing added.
* **Modern**: today's defaults, so only features already judged. Mouse aim, the
  HUD at the edges, render distance and the remaster packs stay off, as they ship.

### Where setup lives

* `tools/verdite-core/src/Onboarding/` (`Onboarding`, `OnboardingPopup`): the
  game-agnostic half. It is compiled twice, by the launcher (`Launcher.targets`
  includes the directory) and as payload into the game, so every type is
  `internal` and the two copies share nothing but the files.
* `config/onboarding.json`: this game's half. It holds the display fields, the
  categories, groups and tiers (each tier a `{key: text | null}` map over the same
  keys in all three), `defaults` (what each key means when absent), and every
  string in en, pt-BR and es-419. It ships in `content/config`, so it is part of
  the build key: a change to it rebuilds.
* `patches/Setup.cs`: RUN SETUP, the live apply, and the shell's `setup` verb.
* `Launcher.cs`: `Onboarding.Due` decides whether setup is shown. `BuildGame` opens
  the setup in place of the build popup and shows the build as a strip at its
  foot, and it pumps until **both** the build and the setup are done. A failed
  build closes the setup into the usual failure popup. On a warm launch with setup
  due, it runs on its own (`RunSetup`).

### What setup writes, and when

* **Due** while `verdite.setup` in `interface.ini` is below the spec's `version`.
  A fresh install and a player whose settings predate setup both see it once.
  Finishing or skipping writes the flag; raise `version` to ask everyone again.
  `VERDITE2_SETUP=1` shows it regardless.
* The page's own rules: a value equal to its declared default **removes** the key,
  and a key already meaning the value is left alone. `null` in a tier is the
  default, so Modern writes nothing and removes what Original wrote.
* **Detecting** where a player is (a re-run starts there): a group matches a tier
  when every key of the tier already means its value. Faithful water is Original
  water and Faithful motion is Modern motion, so a group can match two tiers.
  A category therefore takes a tier every one of its groups matches (the later
  tier first), so a fresh install reads as Modern throughout rather than "Custom".
  A group matching nothing is **Custom**, and is left alone unless a tier is chosen.
* A first run starts the display on the monitor: its aspect, the largest render
  scale its height holds, its refresh rate (`$refresh` in a value is the
  display's rate, read when applied). Window mode has no suggestion. The runtime
  fills `Fullscreen=True` at boot, so the field's `default` (fullscreen) is where
  it starts while no key is written yet; windowed writes `False` explicitly for
  the same reason.
* **Live in the game**: the window keys are applied by core
  (`HostWindow.SetFullscreen`). Every other changed key goes to
  `Onboarding.LiveApply`: the Settings window's setter for the keys the game's
  page does not list, else the `PortSetting`'s `Apply`. A key with neither, or an
  `AtBoot` one, is listed at the end as taking effect at the next launch. In the
  launcher nothing is live: every key is read at boot.
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

**Not judged:** the pages' look and wording, pad navigation, and whether each tier
looks the way its name promises. Clicking through to Finish in the launcher (and
the game then starting) has not been watched either.
