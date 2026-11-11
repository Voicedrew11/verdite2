using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using Kf2.Text;

namespace Kf2.Retranslation;

/// <summary>
/// A new English translation made from the Japanese release, from the Retranslation
/// mod (kf2.retranslation) of PerdueEgg's verdite2-mods. The second of three languages on
/// PORT SETTINGS ▸ GAMEPLAY ▸ LANGUAGE (ENGLISH, RETRANSLATION, JAPANESE), live. All the lettering is <see cref="Lettering"/>'s;
/// this carries only the text (Script.cs), in three parts, each with a switch on
/// Video ▸ Text:
///
///  - Pictures: every sign, dialogue line, description and place card, set on
///    <see cref="Kf2.Text.Pictures"/> over the US text underneath.
///  - Menus and names: GAME.EXE's menu-font text rewritten in RAM, the labels built in
///    code rewritten for their draw (MenuStrings.cs), and the short messages in play
///    through the string table's "hud:" keys.
///  - Encyclopedia: names, titles and cards in the string table, for the Encyclopedia
///    mod, which looks them up itself.
///
/// The text is Script.cs's, or the Mod EN column of a workbook in the data directory's
/// retranslation/ folder (Workbook.cs): read at start, and again on Import.
///
///     KF2_RETRANSLATION=0|1    the Retranslation, or not, for this run
///
/// It is always drawn in the port's font: while it is the language, Lettering draws
/// MODERN whatever FONT says. It applies only in a run that is not Japanese, with the
/// lettering on and the Retranslation mod not enabled; otherwise the game's text is put back.
///
/// Threads: <see cref="Follow"/>, <see cref="BeforeLabel"/> and <see cref="AfterLabel"/>
/// run on the game thread, from Lettering's label replace and main-loop post. The
/// settings (host thread, or the game thread's PORT SETTINGS page) only set the wanted
/// state and hand over imported tables; the game thread puts them in place.
/// </summary>
public static class Retranslation
{
    const string Owner = "kf2.retranslation";

    public const string OnKey = "kf2.retranslation.on";
    public const string PicturesKey = "kf2.retranslation.pictures";
    public const string StringsKey = "kf2.retranslation.strings";
    public const string EncyclopediaKey = "kf2.retranslation.encyclopedia";
    public const string WorkbookKey = "kf2.retranslation.workbook";

    /// <summary>The Retranslation is the language (LANGUAGE ▸ RETRANSLATION). Stored as 1,
    /// absent otherwise.</summary>
    public static bool On { get; set; }

    // The parts, the mod's own switches and keys (Video ▸ Text).
    public static bool Pictures { get; set; } = true;
    public static bool Strings { get; set; } = true;
    public static bool Encyclopedia { get; set; } = true;
    public static bool ReadWorkbookAtStart { get; set; } = true;

    static bool? _forced;
    static volatile Tables? _pending;

    // What is in place now; game thread only.
    static bool _pictures, _hud, _encyclopedia, _strings;

    /// <summary>The Retranslation mod is enabled in the Mods panel, so it carries the text.</summary>
    public static bool StoodDown => ModEnabled();

    /// <summary>It can take effect: the port's lettering is on, the run is not Japanese
    /// and the mod is not doing the same.</summary>
    public static bool Usable =>
        Lettering.Enabled && !Lettering.StoodDown && !StoodDown && !Japanese.JapaneseDisc.Active;

    /// <summary>Something is in place now.</summary>
    public static bool Applied => _pictures || _hud || _encyclopedia || _strings;

    public static void Configure(string? env)
    {
        if (string.IsNullOrWhiteSpace(env)) return;
        if (env is "0" or "1") _forced = env == "1";
        else Console.WriteLine($"[KF2] retranslation: KF2_RETRANSLATION is 0 or 1, not '{env}'; ignored");
    }

    public static void Install()
    {
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            var view = RecompOne.Runtime.Runtime.View;
            On = _forced ?? view.GetInt(OnKey, 0) != 0;
            Pictures = view.GetBool(PicturesKey, true);
            Strings = view.GetBool(StringsKey, true);
            Encyclopedia = view.GetBool(EncyclopediaKey, true);
            ReadWorkbookAtStart = view.GetBool(WorkbookKey, true);
            Workbook.KeepBuiltIn();
            if (ReadWorkbookAtStart && Workbook.Find() is { } path && Workbook.Read(path) is { } tables) _pending = tables;
            Console.WriteLine($"[KF2] retranslation: {(On ? "new" : "original")}; {Workbook.Status}");
        });
    }

    /// <summary>Use these tables from the next game-thread frame (Import, or the built-in text).</summary>
    internal static void Use(Tables tables) => _pending = tables;

    /// <summary>On the game thread: put in place what is wanted, take back what is not.</summary>
    public static void Follow(IMemory m)
    {
        bool live = On && Usable && Lettering.Drawing;
        if (_pending is { } tables)
        {
            _pending = null;
            // Keys shared with the string table are taken back with the old tables first,
            // so a name the new ones no longer change does not keep its old text.
            SetPictures(false);
            SetHud(false);
            SetEncyclopedia(false);
            tables.Install();
            MenuStrings.Refresh();
        }
        if (_pictures != (live && Pictures)) SetPictures(!_pictures);
        if (_hud != (live && Strings)) SetHud(!_hud);
        if (_encyclopedia != (live && Encyclopedia)) SetEncyclopedia(!_encyclopedia);

        _strings = live && Strings;
        try
        {
            if (_strings) MenuStrings.Ensure(m);
            else MenuStrings.Restore(m);
        }
        catch (Exception e) { Console.WriteLine($"[KF2] retranslation: strings: {e.Message}"); }
    }

    /// <summary>Before the label routine draws rec: a code-built label rewritten for the
    /// draw. Always paired with <see cref="AfterLabel"/>.</summary>
    public static void BeforeLabel(IMemory m, uint rec)
    {
        try { Follow(m); }
        catch (Exception e) { Console.WriteLine($"[KF2] retranslation: {e.Message}"); }
        try
        {
            if (_strings) MenuStrings.BeforeDraw(m, rec);
            else MenuStrings.Skip();
        }
        catch (Exception e) { Console.WriteLine($"[KF2] retranslation: label: {e.Message}"); }
    }

    public static void AfterLabel(IMemory m)
    {
        try { MenuStrings.AfterDraw(m); }
        catch (Exception e) { Console.WriteLine($"[KF2] retranslation: label: {e.Message}"); }
    }

    static void SetPictures(bool on)
    {
        _pictures = on;
        Kf2.Text.Pictures.Clear(Owner);
        if (!on) return;
        lock (Script.Sync)
            foreach (var (key, text) in Script.Messages)
            {
                int archive = key[0] == 'T' ? 3 : 6;
                if (int.TryParse(key.AsSpan(1), out int entry)) Kf2.Text.Pictures.Set(Owner, archive, entry, "auto", text);
            }
    }

    /// <summary>The short messages in play, by number: their page is redrawn with these.</summary>
    static void SetHud(bool on)
    {
        _hud = on;
        lock (Script.Sync)
            for (int i = 0; i < Script.Hud.Length; i++) Lettering.SetString("hud:" + i, on ? Script.Hud[i] : null);
    }

    static void SetEncyclopedia(bool on)
    {
        _encyclopedia = on;
        lock (Script.Sync)
        {
            foreach (var (english, ours) in Script.Encyclopedia) Lettering.SetString("encyclopedia:" + english, on ? ours : null);
            foreach (var (english, ours) in Script.EncyclopediaCards) Lettering.SetString("encyclopedia-card:" + english, on ? ours : null);
        }
    }

    static bool ModEnabled()
    {
        foreach (var e in ModLoader.Mods)
            if (e.Enabled && string.Equals(e.Info.Id, Owner, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}
