using System.Collections.Concurrent;
using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2.Text;

/// <summary>
/// The game's text in new fonts: the three fonts redrawn with upper and lower case
/// and proportional spacing (Fonts.cs, Spacing.cs), every sign, dialogue line,
/// description and place card redrawn from its US text (Pictures.cs, GameText.cs,
/// UsText.cs), the menus, names and the short messages in play in normal
/// capitalisation (MenuLabels.cs, MenuCells.cs, HudMessages.cs), and optional drop
/// shadows (Shadow.cs). Was the Text mod (kf2.text) of the verdite2-mods collection;
/// see "The game's text in new fonts" in docs/PATCHES_AND_MODS.md.
///
///     KF2_TEXT=0                 none of it: the game's own lettering (comparison)
///     KF2_TEXT_PROBE=1           a line for each picture redrawn and each upload
///     KF2_TEXT_TEST=3:0,6:360    open these messages (file:entry) from the main loop,
///                                10 s in and then one after each closes
///                                (KF2_AUTOPAD dismisses them)
///
/// **Other mods reach it the way they reached the mod**: the process data slot
/// "kf2.text" holds a dictionary of plain delegates (see <see cref="Api"/>), which
/// the Retranslation, Shortcuts and Japanese Disc mods look up through their
/// TextApi.cs, so they keep working unchanged. A mod that lists "kf2.text" under
/// "dependencies" is still skipped by the loader, since no mod by that id exists
/// any more; the line has to come out of its mod.json.
///
/// **The Text mod itself stands this down.** If it is installed and enabled, this
/// attaches nothing and leaves the slot to it, rather than two copies replacing
/// one routine.
///
/// Threads: every hook runs on the game thread. The settings window (the host
/// thread) only flips the switches below and queues work through
/// <see cref="Post"/>, which the main loop's post drains.
/// </summary>
public static class Lettering
{
    public const string Slot = "kf2.text";

    const uint UploadTim = 0x80035684;     // func_80035684(buf): a message TIM, before upload
    const uint MainLoop = 0x800140AC;      // stage 9, once a frame in play
    const uint ShowMessage = 0x80035B48;   // func_80035B48(file, entry): open a message

    public const string PicturesKey = "kf2.text.game.pictures";
    public const string CaseKey = "kf2.text.game.case";
    public const string PropPicturesKey = "kf2.text.proportional.pictures";
    public const string PropMenusKey = "kf2.text.proportional.menus";
    public const string ShadowPicturesKey = "kf2.text.shadow.pictures";
    public const string ShadowMenusKey = "kf2.text.shadow.menus";

    static readonly ModInfo _self = new()
    {
        Id = "kf2.text",
        Name = "Text",
        Version = "1.0",
        Description = "The game's text in new fonts, with lower case and proportional spacing.",
    };

    /// <summary>KF2_TEXT=0 turns all of it off.</summary>
    public static bool Enabled { get; private set; } = true;

    /// <summary>Every hook committed; false while standing down for the Text mod.</summary>
    public static bool Active => _hooked && !StoodDown;

    /// <summary>The Text mod is installed and enabled, so this leaves it the game.</summary>
    public static bool StoodDown { get; private set; }

    public static bool Probe { get; private set; }

    static bool _queued, _hooked;
    static readonly ConcurrentQueue<Action> _work = new();
    static readonly Dictionary<string, string> _strings = new();
    static readonly object _stringsLock = new();
    static readonly Queue<(uint File, uint Entry)> _tests = new();
    static long _testAt = -1;
    static bool _inMessage;

    public static void Configure(string? enabled, string? probe, string? test)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled != "0";
        if (!string.IsNullOrWhiteSpace(probe)) Probe = probe != "0";
        foreach (var part in (test ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var fe = part.Split(':');
            if (fe.Length == 2 && uint.TryParse(fe[0], out uint f) && uint.TryParse(fe[1], out uint e))
                _tests.Enqueue((f, e));
            else Console.WriteLine($"[KF2] text: KF2_TEXT_TEST expects file:entry pairs, not '{part}'; ignored");
        }
    }

    public static void Install()
    {
        if (!Enabled)
        {
            Console.WriteLine("[KF2] text: off (KF2_TEXT=0); the game's own lettering");
            return;
        }
        // Before any mod's OnLoad, so a mod that asks at load time finds it.
        AppDomain.CurrentDomain.SetData(Slot, Api());
        HudMessages.Lookup = GetString;
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            var view = RecompOne.Runtime.Runtime.View;
            Fonts.PropPictures = view.GetBool(PropPicturesKey, true);
            Fonts.PropMenus = view.GetBool(PropMenusKey, true);
            Shadow.Pictures = view.GetBool(ShadowPicturesKey, false);
            Shadow.Menus = view.GetBool(ShadowMenusKey, false);
            GameText.NormalCase = view.GetBool(CaseKey, true);
            GameText.SetPictures(view.GetBool(PicturesKey, true));
        });
        HookAttach.OnOverlayLoad("text", Attach);
    }

    /// <summary>Run <paramref name="work"/> on the game thread, at the end of the
    /// next main-loop frame: how the settings window changes what the hooks read.</summary>
    public static void Post(Action work) => _work.Enqueue(work);

    static bool ModInstalled()
    {
        foreach (var e in ModLoader.Mods)
            if (e.Enabled && string.Equals(e.Info.Id, Slot, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool Attach()
    {
        if (ModInstalled())
        {
            if (!StoodDown) Console.WriteLine("[KF2] text: the Text mod is enabled; it draws the text, and this stands down");
            StoodDown = true;
            return true;
        }

        SymbolRegistry.Build();
        var upload = SymbolRegistry.Resolve("game", null, UploadTim);
        var loadImage = SymbolRegistry.Resolve("game", null, MenuCells.LoadImage);
        var label = SymbolRegistry.Resolve("game", null, MenuLabels.DrawLabel);
        var link = SymbolRegistry.Resolve("game", null, Shadow.Link);
        var head = SymbolRegistry.Resolve("game", null, Shadow.FrameHead);
        var present = SymbolRegistry.Resolve("game", null, Shadow.Presenter);
        var loop = SymbolRegistry.Resolve("game", null, MainLoop);
        var sprite = SymbolRegistry.Resolve("game", null, Shadow.HudSprite);
        var show = SymbolRegistry.Resolve("game", null, ShowMessage);
        MethodInfo?[] all = [upload, loadImage, label, link, head, present, loop, sprite, show];
        if (all.Any(t => t == null))
        {
            Console.Error.WriteLine("[KF2] text: a routine did not resolve; the game's own lettering");
            return false;
        }

        // Queued once: Installed() is true for a function any patch has hooked.
        if (!_queued)
        {
            _queued = true;
            MethodInfo M(string n) => typeof(Lettering).GetMethod(n, BindingFlags.Public | BindingFlags.Static)!;
            HookManager.AddPre(_self, upload!, M(nameof(BeforeUpload)));
            HookManager.AddPre(_self, loadImage!, M(nameof(BeforeLoadImage)));
            HookManager.AddReplace(_self, label!, M(nameof(DrawLabel)));
            HookManager.AddPre(_self, link!, M(nameof(BeforeLink)));
            HookManager.AddPost(_self, link!, M(nameof(AfterLink)));
            HookManager.AddPost(_self, head!, M(nameof(AfterFrameHead)));
            HookManager.AddPre(_self, present!, M(nameof(BeforePresent)));
            HookManager.AddPost(_self, loop!, M(nameof(AfterMainLoop)));
            HookManager.AddPre(_self, sprite!, M(nameof(BeforeHudSprite)));
            HookManager.AddPost(_self, sprite!, M(nameof(AfterHudSprite)));
            HookManager.AddPre(_self, show!, M(nameof(BeforeShow)));
            HookManager.AddPost(_self, show!, M(nameof(AfterShow)));
        }
        HookManager.Commit();

        _hooked = all.All(HookAttach.Installed);
        Console.WriteLine(_hooked
            ? $"[KF2] text: on; pictures {(GameText.ShowPictures ? "redrawn" : "the disc's")}, " +
              $"{(GameText.NormalCase ? "normal case" : "capitals")}, fonts in {Fonts.Folder}"
            : "[KF2] text: not every hook committed; retrying on the next overlay load");
        return _hooked;
    }

    // ---- the hooks: each guards itself, since a throw would turn the patch off ----

    public static void BeforeUpload(CpuContext c, IMemory m)
    {
        // A Japanese run's pictures are the Japanese disc's own (Japanese.JapaneseMix).
        if (StoodDown || Japanese.JapaneseDisc.Active) return;
        try { Pictures.BeforeUpload(m, c.A0); }
        catch (Exception e) { Console.WriteLine($"[KF2] text: picture: {e.Message}"); }
    }

    public static void BeforeLoadImage(CpuContext c, IMemory m)
    {
        if (StoodDown) return;
        try { MenuCells.BeforeLoadImage(c, m); }
        catch (Exception e) { Console.WriteLine($"[KF2] text: font: {e.Message}"); }
        if (Japanese.JapaneseDisc.Active) return;   // the messages in play are the Japanese disc's
        try { HudMessages.BeforeLoadImage(c, m); }
        catch (Exception e) { Console.WriteLine($"[KF2] text: on-screen messages: {e.Message}"); }
    }

    public static void DrawLabel(Action<CpuContext, IMemory> orig, CpuContext c, IMemory m)
    {
        if (StoodDown) { orig(c, m); return; }
        if (Japanese.JapaneseMenu.TryDraw(c, m)) return;
        MenuLabels.Draw(orig, c, m);
    }

    public static void BeforeLink(CpuContext c, IMemory m)
    {
        if (!StoodDown) Shadow.BeforeLink(c, m);
    }

    public static void AfterLink(CpuContext c, IMemory m)
    {
        if (StoodDown) return;
        try { Shadow.AfterLink(m); }
        catch (Exception e) { Shadow.Menus = false; Console.WriteLine($"[KF2] text: menu shadow, switched off: {e.Message}"); }
    }

    public static void AfterFrameHead(CpuContext c, IMemory m)
    {
        if (!StoodDown) Shadow.AfterFrameHead(m);
    }

    public static void BeforePresent(CpuContext c, IMemory m)
    {
        if (StoodDown) return;
        try { Shadow.BeforePresent(m); }
        catch (Exception e) { Shadow.Pictures = false; Console.WriteLine($"[KF2] text: picture shadow, switched off: {e.Message}"); }
    }

    public static void BeforeHudSprite(CpuContext c, IMemory m)
    {
        if (!StoodDown) Shadow.BeforeHudSprite(c, m);
    }

    public static void AfterHudSprite(CpuContext c, IMemory m)
    {
        if (StoodDown) return;
        try { Shadow.AfterHudSprite(m); }
        catch (Exception e) { Console.WriteLine($"[KF2] text: message shadow: {e.Message}"); }
    }

    public static void AfterMainLoop(CpuContext c, IMemory m)
    {
        if (StoodDown) return;
        while (_work.TryDequeue(out var work))
        {
            try { work(); }
            catch (Exception e) { Console.WriteLine($"[KF2] text: {e.Message}"); }
        }
        try { MenuCells.UploadOnce(c, m); }
        catch (Exception e) { Console.WriteLine($"[KF2] text: font: {e.Message}"); }
        if (!Japanese.JapaneseDisc.Active)
        {
            try { HudMessages.UploadOnce(c, m); }
            catch (Exception e) { Console.WriteLine($"[KF2] text: on-screen messages: {e.Message}"); }
        }
        if (_tests.Count > 0) OpenTest(c, m);
    }

    public static void BeforeShow(CpuContext c, IMemory m) => _inMessage = true;

    public static void AfterShow(CpuContext c, IMemory m) => _inMessage = false;

    /// <summary>KF2_TEXT_TEST: open the next queued message through the game's own
    /// routine, from the main loop, where the game's modal loops open them too.</summary>
    static void OpenTest(CpuContext c, IMemory m)
    {
        if (_inMessage) return;
        long now = Environment.TickCount64;
        if (_testAt < 0) { _testAt = now + 10_000; return; }
        if (now < _testAt) return;

        var (file, entry) = _tests.Dequeue();
        Console.WriteLine($"[KF2] text: test opens ({file}, {entry})");
        var saved = c.Snapshot();
        try
        {
            c.A0 = file;
            c.A1 = entry;
            KingsField2.func_80035B48(c, (PSMemory)m);
        }
        finally { c.Restore(saved); }
    }

    // ---- what other mods call ----

    static string? GetString(string key)
    {
        lock (_stringsLock) return _strings.TryGetValue(key, out var v) ? v : null;
    }

    static void SetString(string key, string? value)
    {
        lock (_stringsLock)
        {
            if (value == null) _strings.Remove(key);
            else _strings[key] = value;
        }
        if (key.StartsWith(HudMessages.Key, StringComparison.Ordinal)) Post(HudMessages.Invalidate);
    }

    /// <summary>
    /// The published functions, exactly as the Text mod published them, so a mod's
    /// TextApi.cs finds the same names and signatures:
    ///
    ///   "Version"      int
    ///   "Ready"        Func&lt;bool&gt;                       the fonts are loaded
    ///   "MenuReady"    Func&lt;bool&gt;                       lower case is in the menu font
    ///   "MenuIndex"    Func&lt;char, int&gt;                  menu font cell, -1 none
    ///   "MenuChar"     Func&lt;int, char&gt;                  the character a cell shows, '\0' none
    ///   "MenuEncode"   Func&lt;string, int, byte[]&gt;        cells (at most n) then 0xFF
    ///   "MenuShown"    Func&lt;string, string&gt;            a label in the game's capitals as drawn
    ///   "SetPicture"   Action&lt;string, int, int, string, string&gt;  owner, archive, entry, style, text
    ///   "ClearPictures" Action&lt;string&gt;                  owner
    ///   "NextPicture"  Action&lt;string, string&gt;           style, text: the next picture shown
    ///   "Lines"        Func&lt;string, string, int&gt;        style, text: lines it wraps to
    ///   "SetString"    Action&lt;string, string?&gt;          the shared string table: key, value (null removes)
    ///   "GetString"    Func&lt;string, string?&gt;
    ///   "MenuPlace"    Func&lt;string, (int[] Cells, int[] X, int Width)&gt;
    ///   "MenuShadow"   Action&lt;IMemory, uint, int&gt;      a shadow under a menu letter quad just linked
    ///   "Coverage"     Func&lt;string, string, int, (byte[] Ink, int W, int H)&gt;
    ///
    /// The short messages in play are worded through the string table as "hud:" +
    /// the message's number, 0-35 in the page's order (21 is GOLD).
    /// </summary>
    static Dictionary<string, object> Api() => new()
    {
        ["Version"] = 1,
        ["Ready"] = new Func<bool>(Fonts.Ready),
        ["MenuReady"] = new Func<bool>(() => MenuCells.Ready),
        ["MenuIndex"] = new Func<char, int>(MenuCells.Index),
        ["MenuChar"] = new Func<int, char>(MenuCells.Char),
        ["MenuEncode"] = new Func<string, int, byte[]>(MenuCells.Encode),
        ["MenuShown"] = new Func<string, string>(GameText.Shown),
        ["SetPicture"] = new Action<string, int, int, string, string>(Pictures.Set),
        ["ClearPictures"] = new Action<string>(Pictures.Clear),
        ["NextPicture"] = new Action<string, string>(Pictures.Next),
        ["Lines"] = new Func<string, string, int>(Pictures.Lines),
        ["SetString"] = new Action<string, string?>(SetString),
        ["GetString"] = new Func<string, string?>(GetString),
        ["MenuPlace"] = new Func<string, (int[] Cells, int[] X, int Width)>(MenuLabels.PlaceText),
        ["MenuShadow"] = new Action<IMemory, uint, int>((m, quad, slot) =>
        {
            try { Shadow.Under(m, quad, (uint)slot, Shadow.KeepFree); }
            catch (Exception) { }
        }),
        ["Coverage"] = new Func<string, string, int, (byte[] Ink, int W, int H)>(Pictures.Coverage),
    };
}
