using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Dispatch;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;

namespace Kf2;

/// <summary>
/// Make the cut eleventh area loadable.
///
///     KF2_AREA10=0            leave it unreachable, as the disc does
///     KF2_AREA10_RTMD=8       which RTMD.T entry to give it (default: keep
///                             whatever is already loaded)
///
/// ## What is on the disc
///
/// `FDAT.T` runs in groups of three -- map data, object data, code module -- so
/// area N is entries 3N, 3N+1, 3N+2. Entries 24-29 are zero length, which is why
/// areas **8 and 9 do not exist**. Entries 30, 31 and 32 are a complete group of
/// the usual shape, and **area 10's content is all still there**:
///
/// * entry 30 is 67,584 bytes and decodes as `[64000, 2194]` -- the same
///   two-block chain every live area has, the first block being the 80x80x10
///   tile map. It holds **3,571 drawn tiles**, against 5,732 for area 0 and
///   2,490 for area 7, and the map has rooms, corridors and a diagonal
///   passage. It is a level, not a placeholder.
/// * entry 31 is 28,672 bytes and decodes as `[12992, 3200, 8400, 2048]` --
///   **byte-for-byte the same block chain as all eight live areas**, the first
///   block being the entity descriptor table `HitGuard` walks.
/// * `RTIM.T` entry 10 is 202 KB of textures. Entries 8 and 9 are zero length,
///   exactly as `FDAT.T`'s two cut groups are -- so that archive was built when
///   this area still existed and still counted as number ten.
///
/// So the geometry, the objects and the textures load through the game's own
/// routine with no help at all. Three things do not, and this patch is those
/// three.
///
/// ## One: the code module cannot be loaded where it is linked for
///
/// The area loader `func_8001689C` builds the module's destination with a
/// literal `lui a2,0x801A / addiu a2,a2,-3972`, so **every code module this game
/// can load, loads at `0x8019F07C`** -- and `fdat32` is linked for `0x80193B38`.
/// Its fifteen calls into the host land mid-instruction in this `GAME.EXE` and
/// none of its thirty-six global addresses is one a live module uses, so it was
/// linked against a different build entirely. See "fdat32 is a cut area" in
/// `docs/RECOMPILATION.md`.
///
/// Loading it where it is linked for is not an option either: `0x80193B38` plus
/// its 6 KB reaches `0x80195338`, and the live `GAME.EXE` keeps the billboard
/// table at `0x80195174` and two more globals inside that window.
///
/// **So the area runs with no area script.** The loader itself provides the
/// fallback: at `0x80016978` it writes `0x80064B64` -- a static table of 32
/// identical pointers to a bare `jr ra` -- into the module pointer at
/// `0x8017E068` *before* reading the module, and `0x800169C0` overwrites it with
/// `0x8019F07C` afterwards. <see cref="AfterLoaderStep"/> puts the stub back.
/// Every one of the module's thirty-two dispatch slots -- the per-frame stage 6
/// tick, the area's damage hook, the load-time init at slot 5 -- then does
/// nothing, which is what the game does in any area before its module arrives.
/// Doors, levers and scripted triggers are what that costs.
///
/// The module's bytes are still read into `0x8019F07C`, because the read is what
/// drives the loader's state machine and skipping it would stall it. They are
/// never executed. The read also arms the `fdat32` overlay in
/// <see cref="Dispatcher"/>, which can never *activate* -- activation wants a
/// write inside the first 2 KB of `0x80193B38` -- so the pending entry would sit
/// there until some unrelated write to `GAME.EXE`'s BSS happened to land in that
/// window and load thirteen dead functions. <see cref="Dispatcher.ClearPending"/>
/// closes that.
///
/// ## Two: the per-area saved state has exactly ten slots
///
/// `func_8004913C` fills **ten** words -- its loop counter is a literal 9 down to
/// -1 -- from a ten-entry `u16` table at `0x801B6988`, each entry an offset into
/// the saved event block at `0x801B3188` or `0xFFFF` for "this area has none".
/// Two callers index that array by area index and neither bounds it:
///
/// * `func_80049710(area)` -- restore, called from the loader at `0x80016C68`.
///   For area 10 it reads `sp+0x38`, which is the frame's own saved `s0`, and
///   walks it as a pointer.
/// * `func_800492B8(area)` -- harvest, called from `func_800162DC` when the
///   loaded area changes. For area 10 it reads and then **frees** `sp+0x38`,
///   which in that frame is the head of its own 3 KB scratch buffer.
///
/// Both are skipped for any index the table cannot hold. That is not a
/// workaround for area 10 so much as the bound the routines never had: `0xFF`
/// reaches neither today only because each caller happens to test for it first.
/// The behaviour it produces for area 10 is the one every area gets on its first
/// visit -- `func_80049710` already returns immediately when the slot is zero,
/// which is what an unvisited area's slot holds.
///
/// **That ten is worth reading twice.** The save format has room for areas 0..9
/// and area 10 is outside it, while `RTIM.T` numbers this area's textures 10 and
/// leaves 8 and 9 empty. The two archives disagree about how many areas there
/// were, which is the same disagreement `fdat32`'s link address records.
///
/// ## Three: `RTMD.T` has no entry for it
///
/// The loader's request carries five slots and `func_80024154(area, ...)` sets
/// all of them to the area index. Slot 1 is an `RTMD.T` entry -- the area's model
/// data, which the loader stakes at `0x8012E9AC` and publishes through
/// `func_8002E628(0, ...)` into the model-pointer table at `0x8018E18C`.
/// **`RTMD.T` holds nine entries and nothing bounds the lookup**:
/// `func_80017F1C` reads `tab[i]` and `tab[i+1]` straight out of a header buffer
/// allocated for exactly `count+1` of them, so asking for entry 10 reads two
/// words of heap past the end and hands the CD a wild sector and length.
///
/// Slot 1 therefore goes in as `0xFF`, which the loader reads as *keep what is
/// loaded* and skips the read entirely (`0x80016AB8`). Area 10 is drawn with the
/// model set of whichever area you warped from. `KF2_AREA10_RTMD=<n>` pins an
/// entry instead, and **8 is the one to try**: `RTMD.T`'s entries 0..7 are the
/// eight live areas and entry 8 is claimed by nothing, in an archive that -- 
/// unlike `RTIM.T` -- has no zero-length holes where areas 8 and 9 would be, so
/// it was built when the area count was nine rather than eleven.
///
/// Which of the two looks right is a picture, and nobody has looked at it.
/// </summary>
public static class Area10
{
    /// <summary>The area this is all about: `FDAT.T` entries 30, 31 and 32.</summary>
    public const int Index = 10;

    /// <summary>How many areas the saved-state table at `0x801B6988` can hold.
    /// A literal 9 in `func_8004913C`'s loop counter, so ten slots, 0..9.</summary>
    public const int SavedAreas = 10;

    /// <summary>`0xFF` in a resource slot means "keep the current one" -- the
    /// loader tests for it and skips the read.</summary>
    public const uint KeepResource = 0xFF;

    public static bool Enabled { get; private set; } = true;

    /// <summary>The `RTMD.T` entry area 10 is given, or <see cref="KeepResource"/>
    /// to leave the resident one alone.</summary>
    public static uint Rtmd { get; private set; } = KeepResource;

    // ---- addresses ----

    /// <summary>Slot 0 of the pending resource request -- the area being asked
    /// for. `func_800162DC` writes it; the loader consumes it and never clears
    /// it, so it stays readable as "the area that was last requested".</summary>
    const uint PendingArea = 0x8017E06C;

    /// <summary>The loaded area module's base. `0x80064B64` is the stub table of
    /// 32 pointers to a bare `jr ra`; `0x8019F07C` is where a real module goes.
    /// </summary>
    const uint ModulePtr  = 0x8017E068;
    const uint StubTable  = 0x80064B64;
    const uint LiveModule = 0x8019F07C;

    // ---- functions ----

    const uint Loader      = 0x8001689C;   // the loader's step; post
    const uint RestoreArea = 0x80049710;   // per-area saved state, in;  pre
    const uint HarvestArea = 0x800492B8;   // per-area saved state, out; pre

    static readonly ModInfo _self = new()
    {
        Id = "kf2.area10",
        Name = "Area 10",
        Version = "1.0",
        Description = "Loads the cut eleventh area: its map, objects and textures, with no area script.",
    };

    public static void Configure(string? enabled, string? rtmd)
    {
        if (!string.IsNullOrWhiteSpace(enabled)) Enabled = enabled != "0";

        if (string.IsNullOrWhiteSpace(rtmd)) return;
        if (rtmd is "keep" or "-1")
            Rtmd = KeepResource;
        else if (int.TryParse(rtmd, out int n) && n >= 0 && n <= 0xFE)
            Rtmd = (uint)n;
        else
            Console.Error.WriteLine($"[KF2] area 10: KF2_AREA10_RTMD={rtmd} is not an entry index " +
                                    "or 'keep'; keeping the resident model set.");
    }

    /// <summary>The value slot 1 of the request takes for a given area -- the
    /// area index for the eight live ones, and for area 10 whatever
    /// <see cref="Rtmd"/> says, because `RTMD.T` has no entry 10 and the lookup
    /// is unbounded.</summary>
    public static uint RtmdSlotFor(int area) => area == Index ? Rtmd : (uint)area;

    static bool _hooked;

    public static void Install()
    {
        if (!Enabled) return;
        HookAttach.OnOverlayLoad("area 10", Attach, "\"Area 10\" in docs/GAME_INTERNALS.md");
    }

    static bool Attach()
    {
        SymbolRegistry.Build();

        var loader  = SymbolRegistry.Resolve("game", null, Loader);
        var restore = SymbolRegistry.Resolve("game", null, RestoreArea);
        var harvest = SymbolRegistry.Resolve("game", null, HarvestArea);

        if (loader == null || restore == null || harvest == null)
        {
            Console.Error.WriteLine("[KF2] area 10: missing " +
                (loader == null ? $"loader 0x{Loader:X8} " : "") +
                (restore == null ? $"state restore 0x{RestoreArea:X8} " : "") +
                (harvest == null ? $"state harvest 0x{HarvestArea:X8} " : "") +
                "-- warping there would run the module's own dispatch table or " +
                "index the ten-slot saved-state array out of range, so it stays refused.");
            return false;
        }

        HookManager.AddPost(_self, loader,
            typeof(Area10).GetMethod(nameof(AfterLoaderStep), BindingFlags.Public | BindingFlags.Static)!);
        HookManager.AddPre(_self, restore,
            typeof(Area10).GetMethod(nameof(BeforeAreaState), BindingFlags.Public | BindingFlags.Static)!);
        HookManager.AddPre(_self, harvest,
            typeof(Area10).GetMethod(nameof(BeforeAreaState), BindingFlags.Public | BindingFlags.Static)!);

        HookManager.Commit();

        _hooked = HookAttach.Installed(loader)
               && HookAttach.Installed(restore)
               && HookAttach.Installed(harvest);

        Console.WriteLine($"[KF2] area 10: {(_hooked ? "reachable" : "NOT reachable")}, " +
                          "no area script (fdat32 is linked for 0x80193B38), " +
                          $"models {(Rtmd == KeepResource ? "kept from the area you came from" : $"from RTMD.T entry {Rtmd}")}, " +
                          $"saved state skipped past slot {SavedAreas - 1}");

        return _hooked;
    }

    /// <summary>Is area 10 what was last asked for? The loader never clears the
    /// pending slot, so this stays true for as long as area 10 is the loaded one
    /// and goes false on the first step of a warp out of it -- which is what
    /// keeps <see cref="Dispatcher.ClearPending"/> below off the next module's
    /// overlay.</summary>
    static bool Requested(IMemory m) => m.ReadU8(PendingArea) == Index;

    /// <summary>Said the stub line once for this entry. The loader writes the
    /// live base **twice** per entry -- measured -- so the restore below has to
    /// run on every step and only the announcement is latched.</summary>
    static bool _announced;

    /// <summary>
    /// Put the stub dispatch table back, on the one loader step that replaced it.
    ///
    /// The loader sets `0x8017E068` to the stub before reading the module and to
    /// `0x8019F07C` after, so this runs once per entry into the area and the
    /// module's own thirty-two slots are never reached.
    /// </summary>
    public static void AfterLoaderStep(CpuContext c, IMemory m)
    {
        if (!_hooked || !Requested(m)) { _announced = false; return; }
        if (m.ReadU32(ModulePtr) != LiveModule) return;

        m.WriteU32(ModulePtr, StubTable);

        // The module's sectors armed the fdat32 overlay. It can never activate --
        // that wants a write inside 0x80193B38..+0x800 -- so without this the
        // pending entry outlives the load and an unrelated write into GAME.EXE's
        // BSS there would load thirteen functions nothing calls.
        Dispatcher.ClearPending();

        if (_announced) return;
        _announced = true;
        Console.WriteLine("[KF2] area 10: loaded with the stub dispatch table; " +
                          "no doors, levers or scripted triggers.");
    }

    /// <summary>
    /// Skip a per-area saved-state pass whose area has no slot.
    ///
    /// Shared by the restore (`func_80049710`) and the harvest
    /// (`func_800492B8`): both index the ten-word array `func_8004913C` fills,
    /// both do it with `area &lt;&lt; 2` off their own frame, and neither bounds
    /// it. Returning false is <c>HookManager</c>'s "skip the call".
    /// </summary>
    public static bool BeforeAreaState(CpuContext c, IMemory m)
        => (int)(c.A0 & 0xFFu) < SavedAreas;
}
