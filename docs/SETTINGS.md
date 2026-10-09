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
| `patches/PortSetting.cs` | one setting: key, variables, menu label, steps and their text, default, live getter and setter, how it is stored |
| `patches/PortSettings.cs` | the list, the four pages, the combined rows; checked at start-up (labels against the font and the box) and at `RuntimeReadyEvent` (every declared default is what the feature boots at) |
| `patches/SettingsStore.cs` | the one writer, pumped on the host thread by an always-open panel that draws nothing (`Panels.kf2settingsstore`) |
| `patches/SettingsSession.cs` | the page without its drawing: open, step, reset, save, discard |
| `patches/WindowMode.cs` | windowed, fullscreen or borderless: the runtime's two keys, applied on the host thread |
| `patches/MenuFont.cs` | text to the label font's codes |
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
header (Y 45..201). There is no hint row (the game's menu has none) and the menu
pointer is not wired into the page yet.

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
| | MOUSE LOOK | OFF, ON | `kf2.mouse.on` |

CONTROLS (2D, 3D; `kf2.controls`, `KF2_TANK`) sits first on GAMEPLAY; see "2D and 3D
controls" in `docs/INPUT.md`. Left out of Verdite3's: GYRO and RUMBLE, which this port does not
have; the minimap has no saved key (N toggles it for the session).

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
