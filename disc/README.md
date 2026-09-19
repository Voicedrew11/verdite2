# disc/

Place your own dump of **King's Field (NTSC-U, `SLUS-00158`)** here — the North
American release of the Japanese *King's Field II* (`SLPS-00069`). The US-boxed
"King's Field II" (`SLUS-00255`) is a different game and will not work with this
port.

**Cue/bin** (names must match `config/kf2.json`):

```
disc/KingsField2.cue
disc/KingsField2.bin
```

**CHD** works at recompile time and at play time: pass the `.chd` path to the
recompiler and to `dotnet run` instead of the `.cue`, or set the `"cue"` field in
`config/kf2.json` to your CHD path (the key name is historical). Python helpers
under `scripts/` read cue/bin only; to use them with a CHD, extract first:

```bash
chdman extractcd -i "King's Field (USA).chd" -o KingsField2.cue -ob KingsField2.bin
```

Nothing in this directory is committed except this README (see `.gitignore`) and
no game data is distributed with this project. Dump the disc you own — RecompOne
reads the original data at recompile time and the runtime reads it again at play
time, so a working port still requires the disc.

## Verifying the dump

Once the files are in place:

```bash
python3 scripts/inspect_disc.py disc/KingsField2.cue
```

That prints `SYSTEM.CNF` (which names the boot executable and its load address)
and lists the disc filesystem, which is what drives the overlay configuration.
