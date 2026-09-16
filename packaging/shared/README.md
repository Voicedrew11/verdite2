# Packaging assets

`verdite2.png` and `verdite2.ico` are the shipping mark: a green verdite
orb. The PNG is 256×256 with an alpha channel; the ICO holds 16, 32, 48 and
256 for Windows (taskbar, shortcuts, the stub and the launcher
`ApplicationIcon`).

`make-icon.py` generated the old placeholder "V" in the map palette. It will
refuse to overwrite these files unless you pass `--force`.

To replace the mark, overwrite the two files at those same sizes.
