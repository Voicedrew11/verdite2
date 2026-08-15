# disc/

Place your own dump of **King's Field II (NTSC-J, SLPS-00069)** here:

```
disc/KingsField2.cue
disc/KingsField2.bin
```

The `.cue` filename must match the `cue` field in `config/kf2.json`.

Nothing in this directory is committed (see `.gitignore`) and no game data is
distributed with this project. Dump the disc you own — RecompOne reads the
original data at recompile time and the runtime reads it again at play time, so
a working port still requires the disc.

## Verifying the dump

Once the files are in place:

```bash
python3 scripts/inspect_disc.py disc/KingsField2.cue
```

That prints `SYSTEM.CNF` (which names the boot executable and its load address)
and lists the disc filesystem, which is what drives the overlay configuration.
