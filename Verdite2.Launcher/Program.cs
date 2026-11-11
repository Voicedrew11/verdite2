using Verdite.Launcher;

// Verdite2 -- the shipped entry point.
//
// The launcher is Verdite Core's (tools/verdite-core/launcher): it settles where
// files live, asks for a disc, builds the game from it on first run and hands
// over. This is what it needs to know about this port. See docs/PACKAGING.md.

const string JapaneseDiscMessage =
    "This is the Japanese King's Field II. The port needs the US disc, SLUS-00158 " +
    "(King's Field), as its main disc. Choose Japanese at the language step, or in " +
    "Settings, and point it at this disc to play in Japanese.";

return Launcher.Run(new LauncherGame
{
    Name = "Verdite2",
    AppId = "verdite2",
    GameTitle = "King's Field",
    Serial = "SLUS-00158",

    // King's Field II (SLUS-00255) is the game most people will reach for, and it
    // is a DIFFERENT GAME. The series was renumbered for the West: this port is of
    // King's Field (SLUS-00158), the US release of the Japanese King's Field II.
    // Every address in config/ is wrong for SLUS-00255 and it would build.
    WrongDiscs = new Dictionary<string, string>
    {
        ["SLUS-00255"] =
            "This is King's Field II (SLUS-00255), which is a different game. " +
            "The series was renumbered for the West: this port is of King's Field " +
            "(SLUS-00158), the US release of the Japanese King's Field II.",

        // The Japanese King's Field II (SLPS-00069, and the PlayStation the Best
        // SLPS-91003) is the disc the Japanese language is taken from
        // (patches/japanese/JapaneseDisc.cs), not the port's main disc.
        ["SLPS-00069"] = JapaneseDiscMessage,
        ["SLPS-91003"] = JapaneseDiscMessage,
    },

    RecompilerConfig = "kf2.json",
    BeforeDisc = Verdite2.Launcher.LanguageStep.Run,
    GameAssembly = "KingsField2",
    UpdateRepository = "Voicedrew11/verdite2",

    // OPEN.EXE, or GAME.EXE arriving afresh, is the title; an area module (the
    // attract demo loads one too) or END.EXE is play.
    PlayAfter = overlay => overlay switch
    {
        "open" or "game" => false,
        "end" => true,
        _ when overlay.StartsWith("fdat", StringComparison.Ordinal) => true,
        _ => null,
    },
}, args);
