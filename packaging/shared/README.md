# Packaging assets

`verdite2.desktop` is the entry the AppImage carries; `package.env` one level up
names the port to Verdite Core's packaging scripts.

There is no mark here, on purpose. The port's icon is the game's own memory-card
icon, read off the player's disc by Verdite Core's `CardIcon` (where it is on
this disc: `patches/CardIconSource.cs`), and a release cannot carry it, so
nothing shipped has an icon of its own: the executables have no icon resource
and the AppImage gets a transparent square (appimagetool refuses an AppDir with
none). Once the game has read the disc, the window, the icon theme and the
shortcuts wear the card icon — see "The icon comes off the disc" in
`docs/PACKAGING.md`.
