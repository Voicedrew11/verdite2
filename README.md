# Verdite2

Verdite2 is a PC port of King's Field (US) built atop of the [RecompOne](https://github.com/BlackLabelHQ/RecompOne) project.

## Features

- Widescreen support (16:9, 16:10, 21:9), with changed culling behavior
- Perspective-correct textures and corrected vertex wobbling
- 24-bit colour
- 60+ fps
- Consistent game speed
- A map, a minimap and fog of war
- Automatic save reload after death
- Keyboard and mouse support
- Modern Twin-stick FPS controls
- Mod support

## Status

Game is playable from start to finish. There may still be some intermittent issues.

## Requirements

A dump of the North American PlayStation release (`SLUS-00158`) in `.cue` / `.bin`
format. **Verdite2 ships no game data** and cannot be played without one.

Note that the US-boxed *King's Field II* (`SLUS-00255`) is a **different game** —
the series was renumbered for the West — and Verdite2 will refuse it.

## Installing

Download the build for your platform from
[Releases](https://github.com/Voicedrew11/verdite2/releases).

- **Windows** — run the installer, or unzip the portable build and run
  `Verdite2.exe`. It is unsigned, so SmartScreen will warn once: *More info* →
  *Run anyway*.
- **Linux** — `chmod +x Verdite2-*.AppImage` and run it.

On first launch Verdite2 asks for your disc image and **builds the game from it**.
That takes about fifteen seconds and happens once; later launches start straight
away. It has to work this way: the recompiled game code is derived from the disc,
so it cannot be distributed — only built from a copy you own.

Saves, settings and the built game live in `%LOCALAPPDATA%\Verdite2` on Windows
and `~/.local/share/verdite2` on Linux. Set `VERDITE2_DATA` to put them elsewhere.

macOS is not packaged yet.

## Building from source

For working on the port itself. Needs the .NET 10 SDK and the disc.

```bash
bash scripts/setup_tools.sh
dotnet run --project tools/RecompOne/RecompOne.Recompiler -c Release --no-build -- config/kf2.json
dotnet build KingsField2Recomp.csproj -c Release
dotnet run --project KingsField2Recomp.csproj -- disc/KingsField2.cue
```

To build the redistributable instead — which needs no disc — see
[docs/PACKAGING.md](docs/PACKAGING.md). `NOTES.md` is the index to everything else.

## Credits

Built on [RecompOne](https://github.com/BlackLabelHQ/RecompOne) (MIT). *King's
Field* is the property of FromSoftware; this project ships no game data.
