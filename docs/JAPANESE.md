# Japanese text, menus and movies (KF2_LANGUAGE=ja)

The Japanese *King's Field II* supplies the dialogue and sign pictures, the item
descriptions, the messages in play, the menu's words, the title screen and the opening
and ending. It is laid over the US disc (`SLUS-00158`) in memory at boot, as Verdite3
does for its game (Verdite3's `docs/JAPANESE.md` is where the design came from). The US
program and all the gameplay data stay. Nothing is written to disk, and no byte of
either disc is in the repository: the source holds addresses, strides, counts and
hashes, and every word and picture is read from the player's own discs at run time.

| file | what |
|---|---|
| `patches/japanese/JapaneseDisc.cs` | finds the Japanese disc, decides the language, installs the disc layer (`DiscImage.Decorate`) |
| `patches/japanese/JapaneseMix.cs` | pure bytes: the replaced sectors |
| `patches/japanese/JapaneseMenu.cs` | the label hook, the choice, card-message and window hooks |
| `patches/japanese/JapaneseMenuText.cs` | the measured tables: which US label is which Japanese one |
| `patches/japanese/JapaneseProgram.cs` | the two Japanese programs: the Best one the tables are written in, and where the 1995 one has each address |
| `patches/japanese/JapaneseFontAtlas.cs` | the Japanese glyphs cached in the US label font page |
| `patches/japanese/JapaneseMovies.cs`, `MovieMusic.cs` | the title, the two movies, their music (from the Japanese Disc mod) |
| `tools/verdite-core/src/Language/` | shared with Verdite3: `MixedDiscImage`, `DiscFinder`, `DiscArchive`, `ExeInterpreter` (the MIPS constant reader), `LanguageChoice` |
| `Verdite2.Launcher/LanguageStep.cs` | the first-run question, ported from Verdite3 |

## Which disc, and what each gets

| disc | volume id | GAME.EXE | gets |
|---|---|---|---|
| King's Field II (Japan) | `SLPS-00069` | `27cc1603…` | everything |
| PlayStation the Best | `SLPS-91003` | `9e300cd2…` | everything |
| PlayStation the Best, Rev 1 | `SLPS-91003` | `9e300cd2…` (identical) | everything |

The menu tables are written in the Best program's addresses, and the 1995 program has
each of them elsewhere ("The 1995 program" below); a GAME.EXE that is neither is
refused by hash with one `[KF2] japanese:` line, and its menus stay English. The disc
layer itself reads each archive's table at run time and needs no hash.

- Language: `KF2_LANGUAGE=ja` or `en`; with no variable, the stored `verdite.language`
  (PORT SETTINGS ▸ GAMEPLAY ▸ LANGUAGE, or the Settings window's Gameplay tab); with
  neither, English. A change takes effect at the next start.
- The disc is looked for in this order: `KF2_JP_DISC`, the launcher's saved
  `verdite.language.jpdisc`, then every `.cue`/`.chd` in the US disc's folder, its
  `Japanese discs` subfolder and its `japanese` subfolder, in ordinal order, so the
  Best Rev 1 is taken before the bare Best and both before the 1995 disc.
- No Japanese disc: one line, and the game plays in English.

## What comes from the Japanese disc

Measured 2026-11-11 over all three Japanese discs against the US one (scratch scripts,
then the dry run below):

- **`CD/COM/TALK.T`**: same size and same 755-entry table on every disc. 379 entries
  differ, every one of them a TIM picture (they are its only non-empty entries): the
  dialogue and the signs. All 379 are served.
- **`CD/COM/ITEM.T`**: same size, same 548-entry table. 278 entries differ: 276 TIM
  pictures (the item descriptions, entries 120 on), served; 74 and 79, 3D models where
  the Japanese file has geometry the US one zeroed, kept US. (The 1995 disc differs in
  one more model.)
- **`CD/COM/FDAT.T`**: 70 entries; the tables differ only because entry lengths do.
  - Entry 47, the VRAM blocks (fonts, HUD sprites, the messages in play), differs in
    exactly three of its 500 blocks: the messages-in-play page at (704, 256), the label
    font page at (832, 256) and the label font's palette at (576, 509). **Only the
    messages-in-play block is copied** (its palette, (576, 508), is the same on both), and
    the entry's checksum rewritten. The label font stays US: see the atlas below.
  - Area data (entries 10, 16, 19 on the Best discs) differ by one creature statistic
    byte each, and eight area code modules differ: all kept US, as Verdite3 keeps
    gameplay data US.
- **The movies**: `OP/OP2.S` is served from the Japanese `OP/OP.S` (10,890 of 16,479
  sectors) and `OP/ED0.S` from `OP/ED.S` (21,410 of 25,703); past the shorter Japanese
  file a sector reads as zeros.
- **The title**: the Japanese title pictures are already on the US disc (`OP/OP.D`, the
  US game's own hidden title); OPEN.EXE is pointed at it.

Every non-empty archive entry ends with a checksum word (seed `0x12345678` plus every
word before it), the same format as Verdite3's; measured on all of TALK, ITEM and FDAT.

## The menu's text

The Japanese label routine is `0x8002108C` in the Japanese GAME.EXE, the counterpart of
`func_80021E10`. It reads `u16` codes to `0xFFFF` where the US reads bytes to `0xFF`;
glyph `g = code & 0xFFF` is a 15 × 15 cell at u = 15·(g & 15), v = 15·(g >> 4), one every
14 px; bit `0x1000` adds a dakuten (glyph `0x2E`) and `0x2000` a handakuten (`0x2F`) drawn
in the same place; `0xFF` is a blank cell that right-aligns a word. Its template
(`0x80064B88`) has the US one's tpage and palette slot, and a 15 × 15 cell.

**The atlas.** The Japanese font fills its whole page, so it cannot be uploaded over the
US one without losing the English the port still draws (PORT SETTINGS, the gear
comparison, any label with no Japanese). Instead the glyphs are copied, as they are
first drawn, into the free 15 × 15 cells of the US page (156 of them: past the port's
added lower case at u < 128, v < 105, and clear of the US space cell at (120, 105)),
each Japanese palette index mapped to the nearest US colour. A cell drawn within 3
vblanks is not reused, and each use checks the cell's pixels and rewrites them if an
area load re-uploaded the page.

**The Latin letters are the US font's.** The Japanese page has its own full-width Latin
cells: N E W H M P 1-8 at `0xE0`-`0xED`, "On" and "Off" at `0xF0`-`0xF4`, C and D at
`0x10D`-`0x10E` (the same on all three discs). Only four labels of the measured map use
them: HP and MP on status page 1, ON and OFF on the option page. They looked out of
place beside the US-lettered English, so a run of them is drawn from the US label font on
the same page, in the US routine's cells (the template's 8 × 15, u = 8·(g & 15),
v = 15·(g >> 4)), 7 px apart, or spaced by the port's lettering, and 1 px before the next
Japanese cell. Which letters is the FONT row's (PORT SETTINGS ▸ GAMEPLAY ▸ FONT): MODERN the
port's, so "On"/"Off" keep their lower case; CLASSIC the disc's capitals, ON/OFF. A label
right-aligned by its leading `0xFF` blanks is moved right by what the narrower letters
saved, so it still ends where the Japanese routine's would. Measured: the dry run's map;
the HP/MP/ON/OFF draws by eye: not checked.

**Which Japanese a label gets.** The US and Japanese programs have the same menu
routines in the same order; ten US routines that draw labels pair one to one with ten
Japanese ones (46 US label calls, 49 Japanese). A label is looked up, in order:

| source | key | count (Best) |
|---|---|---|
| a card message's record | the port's own region (below) | 11 calls |
| an option group's record (`0x80064CD4 + 0x134·g`, ↔ `0x80064C6C`) | the US record's address | 35 records |
| one of a two-choice routine's records (`func_800205C0`, `func_80021478`) | US call, argument | 4 calls: ON/OFF, QUIT/RETURN, YES/NO twice |
| a call in the site table (status pages, the format question) | US call; the Japanese call's record, read by `ExeInterpreter` | 29 |
| the shop's GOLD/CRYSTAL heading and NUMBER | US call and US text, each seed of a0 run through both programs | 2 |
| a list row or anything else | US text: the 140 names (`0x80065B24` ↔ `0x8006592C`, 24 bytes), the 24 words (`0x80065674`/24 ↔ `0x8006560C`/20), the groups' words | 119 + 24 + 25 |

The status page's condition (`0x8001FA2C`) is not a site: its record is copied from the
words by branch, so the words map serves it, Japanese padding and all.

**Card messages.** Nine small US routines fill a card message's records; each pairs
with a Japanese one by its place in the callers. After a US builder runs, the Japanese
builder is run by `ExeInterpreter` with the same a1-a3 and its records written into a
region of the port's own guest RAM (`PrimBuffer.TextScratch`, 0x400 bytes past the
props), never into the game's stack buffer, since Japan often uses more lines (up to
four where the US uses three). The window that shows it (`func_8001B8AC`, records in
a1 and count in a2; `func_8001D43C`, records in a0 and count in a1) is pointed at the
Japanese records and takes the Japanese call's count and box, read from the Japanese
caller. The format question sizes its own box (`func_800222B8` from `0x8001CD18`): US
(70, 91, 178 × 66), Japan (70, 92, 220 × 81). Under `KF2_PRIMBUF=1` there is no RAM past
2 MB, and the card messages stay English.

Not done: the port's own pages and the gear comparison stay English (the US font is
kept for them); whether the Japanese GAME.EXE swaps confirm and cancel, as Verdite3's
does, has not been looked at.

## The 1995 program

The 1995 GAME.EXE (`27cc1603…`, 0x5D000 bytes of text against the Best's 0x5E800) is an
earlier build of the same program: an alignment of the two images matches 69,088 of the
Best's 96,768 words. The menu's data is identical where it lies: the option groups
(`0x80063F70`, the Best's `0x80064C6C`), the names (`0x80064C30` / `0x8006592C`), the
words (`0x80064910` / `0x8006560C`) and the label template (`0x80063E8C` / `0x80064B88`)
compare byte for byte, and so does FDAT entry 47, the fonts included. The label routine
is `0x800210AC` (0x464 bytes against 0x408), with the same constants, plus one `lui`.

The code moved, so `JapaneseProgram.Original` carries each of the tables' 79 Japanese
addresses to the 1995 program's:

- **Routines** were paired by order. Between `0x80018000` and `0x80030000`, the
  functions of both programs (found by their `addiu sp, sp, -n` prologue) were aligned
  by how many calls each makes: 160 of the Best's 170 paired. The unpaired are seven
  Best routines at `0x8001802C` on with nothing opposite them, `0x8001FDCC` and
  `0x800207C8` opposite the one routine `0x8001FC94`, and `0x80020A00` opposite two.
  `0x80020A00` (the shop's heading) is `0x80020990`, as the image alignment also says.
- **A call site** is the same call of its paired routine: the nth call to the label
  routine for a label, or the nth call of all for a builder, a window or a choice. Each
  of those landed on a call with the paired target, which also gave the nine builders
  (they have no frame of their own) and the choice routine `0x80020748`.
- **The check** is the dry run on each disc: the 1995 map describes the same 228 labels
  as the Best's, line for line, every site, choice, seed, window and box the same, and
  only the builders' addresses differ.

## The title and the movies

From the Japanese Disc mod (verdite2-mods `mods/jpdisc`; its NOTES.md has every
address). `JapaneseMovies` makes OPEN.EXE read `OP.D` for the title, serves the two
movies, raises END.EXE's frame count to the Japanese 0x858 so the Japanese staff
credits play, leaves out the US ending's second part (`ED1.S`) and holds the last
Japanese frame, and plays the opening's and ending's libsnd sequences onto the SPU
(`MovieMusic`, the mod's `Music.cs` renamed). The mod's re-lettering of the movie
cards in English is not taken: a Japanese run keeps the Japanese cards. With the mod
enabled, the built-in title and music stand down.

## Switches and checks

- `KF2_LANGUAGE`, `KF2_JP_DISC`, `KF2_JP_PROBE`, `KF2_JP_DRYRUN`: `docs/ENV_VARS.md`.
- Dry run: `KF2_LANGUAGE=ja KF2_JP_DRYRUN=1 dotnet bin/Release/net10.0/KingsField2.dll disc/KingsField2.cue`.
  Exits 0 after one line per file, the menu map, and every resolved site.

## Measured (2026-11-11)

- Dry run, all three discs: TALK 379, ITEM 276, entry 47's messages, both movies; 9,411
  sectors replaced; 228 labels, 0 left out; 156 free atlas cells; exit 0.
- Live, headless, Best Rev 1, `KF2_AUTOSTART=2` into fdat05, the in-game menu opened
  and a page entered through `KF2_SHELL`: 4,345 labels drawn in Japanese, **0 left to
  English**, 72 atlas writes, 0 forced evictions; the save load's CHECKING THE RAM CARD
  built from the Japanese builder (2 records); 9,428 sectors served; no exception.
- Live, the 1995 disc, the same route (the menu, a page, out again): 3,267 labels in
  Japanese, 0 left to English, 72 atlas writes, 0 forced; the card message built from the
  1995 builder `0x8001C550`; 9,428 sectors served; no exception.
- Pacing, `KF2_FPS=144 KF2_FPS_PROBE=1`: 144.0 fps drawn at 14.9-15.0 ticks/s in both
  languages and on both programs. English run: no `japanese` line.
- Choosing a map on the ITEM page opens the game's map viewer (`func_80019B54`), a loop
  of its own in which `KF2_SHELL` does not answer, in English as in Japanese: drive past
  it with a button press, not the shell.

## Open

- **Nothing has been judged by eye.** A conversation, a sign, an item description, the
  menus and status pages, a card message, LEVEL UP, the title, the opening and the
  ending all need a person, in a window (the runs above were headless).
- Not reached in a run, only read from the code: the status pages, the option page's
  ON/OFF, the shops, the format question, every card message but the first, the movies.
- The movies' music is the mod's, which the user played through as a mod; not heard in
  the port.
