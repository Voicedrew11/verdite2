using System.Reflection;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using KingsField2 = Recompiled.KingsField2_game;

namespace Kf2;

/// <summary>
/// Enemies turn faster: a multiplier on the top speed and the acceleration a
/// creature turns toward the player with, so a player who can spin the view with a
/// mouse is not circling something that turns at a pad's pace.
///
/// Every creature turn the motion code makes goes through one routine,
/// <c>func_8003C8D8(rec, target, maxSpeed, accel)</c>: the turn velocity at
/// <c>rec+0x58</c> gains <c>accel</c> toward the target, is clamped to
/// <c>±maxSpeed</c> and is added to the yaw at <c>rec+0x42</c>; a step that carries
/// the yaw past the target puts it on the target. The motion routine
/// (<c>func_8003DEBC</c>) passes the current rule's speed and the creature type's
/// acceleration (<c>desc+0x4</c>); two wrappers (<c>func_8003CA08</c>,
/// <c>func_8003CCAC</c>) and three area modules call it with their own. A pre-hook scales the speed by the multiplier and the acceleration by
/// its square, so a turn of any size takes 1/k the time; every caller is covered
/// and nothing is stored: x1 is the game's own turn.
///
/// Only a turn at the player is scaled. The chase writes its heading as
/// <c>rec+0x64 = func_80015394(player - creature)</c> on the tick it turns
/// (<c>0x8003E6F8</c>, <c>0x8003F000</c>), and a wander heading is <c>rand</c>
/// (<c>0x8003E448</c>), so
/// a target within <see cref="AtPlayer"/> of the player's bearing is a chase; a
/// creature milling about keeps the game's pace instead of twitching.
///
/// **Tracking while standing.** A creature turns only in the actions that call the
/// turn; the rest hold whatever facing they had. The type-10 creature by save 2
/// chases for 3-4 ticks and then stands in action <c>0x19</c> for 5-16 s without
/// turning, so it re-aims at the player in bursts. With <see cref="Track"/> on and
/// the multiplier above x1, once a tick each active, living creature that the game
/// neither turned nor moved, and is engaged, is turned at the player through the
/// same routine, at
/// <see cref="TrackSpeed"/> and its type's acceleration, scaled like any other turn
/// at the player. A creature that walks keeps its path. Engaged is from the game's
/// own chase turn until the creature's next turn elsewhere (a wander) or it goes
/// inactive; a creature that has not chased is left alone, so an idle one does not
/// stand watching the player from across the area. A timer (20 s) was tried first and
/// ran out on the type-10 creature still standing at the player in <c>0x19</c>. See "Enemies turn faster" in
/// docs/PATCHES_AND_MODS.md and "The creature turn is one routine" in
/// docs/GAME_INTERNALS.md.
///
///     KF2_ENEMYTURN=1..8        the multiplier (Gameplay ▸ Enemy turn speed)
///     KF2_ENEMYTURN_TRACK=0     no turning at the player while standing (Gameplay ▸
///                               Track you while standing; on)
///     KF2_ENEMYTURN_ALL=1       scale every turn, wandering too -- comparison only
///     KF2_ENEMYTURN_PROBE=1     a census of the turn's callers every 5 s
///     KF2_ENEMYTURN_PROBE=2     also a line a tick for each active creature in reach:
///                               its action, facing error, and whether it turned
/// </summary>
public static class EnemyTurn
{
    const uint Turn = 0x8003C8D8;

    // The creature record, as the turn reads it.
    const int YawOff = 0x42, VelOff = 0x58, ActionOff = 0xE, TypeOff = 0x2;
    const int PosXOff = 0x2C, PosZOff = 0x34;
    const uint PlayerX = 0x801994EC, PlayerZ = 0x801994F4;

    /// <summary>The overshoot test compares the half-turn either side of the target,
    /// so a step must stay well under 0x800 or a turn could pass the target without
    /// seeing it. A quarter turn a tick is past anything the game asks for.</summary>
    const int MaxStep = 0x400;

    /// <summary>How far a target may be from the player's bearing and still be a
    /// chase, 1.4 degrees. The chase's heading is the bearing on the tick of the turn,
    /// and the trace measured it within 3 of this file's <see cref="Bearing"/>; at
    /// 0x80 (11 degrees) 5-15% of wander turns passed for chases.</summary>
    const int AtPlayer = 16;


    public const string Key = "kf2.enemyturn";
    public const float Min = 1f, Max = 8f;
    public const string TrackKey = "kf2.enemyturn.track";

    /// <summary>The top speed of a turn made while standing, before the multiplier:
    /// between the slowest chase measured (11) and the player's own walking turn
    /// (0x1C). At x4 a standing creature follows at 64 a tick, 84 degrees a second.</summary>
    const int TrackSpeed = 16;

    /// <summary>1 is the game's own turn.</summary>
    public static float Multiplier { get; private set; } = 1f;

    public static void SetMultiplier(float k) => Multiplier = Math.Clamp(k, Min, Max);

    /// <summary>Turn a standing creature at the player each tick. Inert at x1.</summary>
    public static bool Track { get; set; } = true;

    static bool _fromEnv, _trackFromEnv, _probe, _trace, _all, _queued, _hooked, _stageHooked;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.enemyturn",
        Name = "Enemy turn speed",
        Version = "1.0",
        Description = "Scales how fast creatures turn.",
    };

    public static void Configure(string? multiplier, string? track, string? all, string? probe)
    {
        if (!string.IsNullOrWhiteSpace(multiplier))
        {
            if (float.TryParse(multiplier, System.Globalization.NumberStyles.Float,
                               System.Globalization.CultureInfo.InvariantCulture, out float k) && k >= Min && k <= Max)
            {
                SetMultiplier(k);
                _fromEnv = true;
            }
            else Console.WriteLine($"[KF2] enemy turn: KF2_ENEMYTURN expects {Min}..{Max}, not '{multiplier}'; ignored");
        }
        if (!string.IsNullOrWhiteSpace(track))
        {
            Track = track != "0";
            _trackFromEnv = true;
        }
        if (!string.IsNullOrWhiteSpace(all)) _all = all != "0";
        if (!string.IsNullOrWhiteSpace(probe))
        {
            _probe = probe != "0";
            _trace = probe == "2";
        }
    }

    public static void Install()
    {
        // The saved value is readable only once ConfigManager has loaded; the variable wins.
        Event.AddListener<RuntimeReadyEvent>(_ =>
        {
            if (!_fromEnv) SetMultiplier(RecompOne.Runtime.Runtime.View.GetFloat(Key, Multiplier));
            if (!_trackFromEnv) Track = RecompOne.Runtime.Runtime.View.GetBool(TrackKey, Track);
        });
        // An area's creatures reuse the table's records.
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            _engaged.Clear();
            _lastPos.Clear();
        });
        HookAttach.OnOverlayLoad("enemy turn", Attach);
    }

    static bool Attach()
    {
        SymbolRegistry.Build();
        var turn = SymbolRegistry.Resolve("game", null, Turn);
        if (turn == null)
        {
            Console.Error.WriteLine($"[KF2] enemy turn: no game function at 0x{Turn:X8}; creatures turn at the game's speed.");
            return false;
        }

        // Queued once: Installed() is true for a function any patch has hooked
        // (FramePacing gates stage 4), so it cannot say whether these are on it.
        var stage = SymbolRegistry.Resolve("game", null, Stage4);
        if (!_queued)
        {
            _queued = true;
            HookManager.AddPre(_self, turn,
                typeof(EnemyTurn).GetMethod(nameof(BeforeTurn), BindingFlags.Public | BindingFlags.Static)!);
            if (stage != null)
                HookManager.AddPost(_self, stage,
                    typeof(EnemyTurn).GetMethod(nameof(AfterStage4), BindingFlags.Public | BindingFlags.Static)!);
        }
        HookManager.Commit();

        _hooked = HookAttach.Installed(turn);
        _stageHooked = HookAttach.Installed(stage);
        Console.WriteLine($"[KF2] enemy turn: {(_hooked ? $"x{Multiplier:0.##}" : "off (hook not committed)")}" +
                          (_all ? ", every turn" : ", turns at the player") +
                          (_stageHooked ? Track ? ", tracking while standing" : "" : ", no tracking (stage 4 not hooked)") +
                          (_probe ? ", census on" : ""));
        return _hooked && _stageHooked;
    }

    public static void BeforeTurn(CpuContext c, IMemory m)
    {
        uint rec = c.A0;
        if (rec < 0x80010000u || rec >= 0x80200000u) return;
        bool atPlayer = Math.Abs(Wrap((int)c.A1 - Bearing(rec, m))) <= AtPlayer;
        bool chase = _all || atPlayer;
        if (_probe) Census(c, m, chase);
        if (_trace || _stageHooked) _turned[rec] = (chase, (int)c.A1);
        // Only the game's own turns engage or release; ours (RA marked Stage4) are
        // at the player by construction and say nothing about what it is doing.
        if (c.RA != Stage4)
        {
            if (atPlayer) _engaged.Add(rec);
            else _engaged.Remove(rec);
        }

        float k = Multiplier;
        if (k == 1f || !chase) return;
        // A turn starts from rest and gains accel a tick until it reaches maxSpeed,
        // so a short turn takes sqrt(2 angle / accel) ticks and a long one about
        // angle / maxSpeed. Speed times k and acceleration times k squared make
        // either k times faster. a2 is a u16 off the rule or the caller, a3 a u8 off
        // the descriptor; a zero stays zero, so a caller that does not turn
        // (0x8003EB18 passes no speed) keeps not turning.
        c.A2 = (uint)Scale((int)c.A2, k);
        c.A3 = (uint)Scale((int)c.A3, k * k);
    }

    static int Scale(int v, float k) =>
        v <= 0 || v >= MaxStep ? v : Math.Min(MaxStep, (int)MathF.Round(v * k));

    /// <summary>The player's bearing from the creature, as the chase computes it:
    /// <c>func_80015394(dx, dz)</c>, which is <c>-atan2(dx, dz)</c> in a turn of
    /// 4096 (the game's yaw runs clockwise seen from above).</summary>
    static int Bearing(uint rec, IMemory m)
    {
        int dx = (int)m.ReadU32(PlayerX) - (int)m.ReadU32(rec + PosXOff);
        int dz = (int)m.ReadU32(PlayerZ) - (int)m.ReadU32(rec + PosZOff);
        return -(int)MathF.Round(MathF.Atan2(dx, dz) * 2048f / MathF.PI) & 0xFFF;
    }

    // ---- The census ----

    sealed class Site
    {
        public int Calls, Turning, TowardPlayer, SpeedMin = int.MaxValue, SpeedMax, AccelMin = int.MaxValue, AccelMax, VelMax;
        public readonly SortedSet<int> Actions = [], Types = [];
    }

    const uint WrapA = 0x8003CA48, WrapB = 0x8003CCF0;   // the turn's return address in each wrapper

    static readonly Dictionary<ulong, Site> _sites = [];
    static long _windowStart;

    static void Census(CpuContext c, IMemory m, bool chase)
    {
        uint rec = c.A0;

        // Through a wrapper, the wrapper's caller says more: func_8003CA08 keeps its
        // return address at sp+0x20, func_8003CCAC at sp+0x24.
        ulong site = c.RA switch
        {
            WrapA => (ulong)m.ReadU32(c.SP + 0x20) << 32 | c.RA,
            WrapB => (ulong)m.ReadU32(c.SP + 0x24) << 32 | c.RA,
            _ => c.RA,
        };
        if (!_sites.TryGetValue(site, out var s)) _sites[site] = s = new Site();
        s.Calls++;
        int target = (int)(c.A1 & 0xFFF);
        int yaw = m.ReadU16(rec + YawOff) & 0xFFF;
        if (Wrap(target - yaw) != 0) s.Turning++;
        if (chase) s.TowardPlayer++;

        s.SpeedMin = Math.Min(s.SpeedMin, (int)c.A2); s.SpeedMax = Math.Max(s.SpeedMax, (int)c.A2);
        s.AccelMin = Math.Min(s.AccelMin, (int)c.A3); s.AccelMax = Math.Max(s.AccelMax, (int)c.A3);
        s.VelMax = Math.Max(s.VelMax, Math.Abs((short)m.ReadU16(rec + VelOff)));
        s.Actions.Add(m.ReadU8(rec + ActionOff));
        s.Types.Add(m.ReadU8(rec + TypeOff));

        long now = Environment.TickCount64;
        if (_windowStart == 0) _windowStart = now;
        if (now - _windowStart < 5000) return;
        _windowStart = now;
        foreach (var (ra, x) in _sites.OrderBy(p => p.Key))
            Console.WriteLine($"[KF2] enemy turn: from {(ra >> 32 != 0 ? $"0x{ra >> 32:X8} via " : "")}0x{(uint)ra:X8} {x.Calls} calls, {x.Turning} turning, " +
                              $"{x.TowardPlayer} {(_all ? "scaled" : "at the player, scaled")}; speed {x.SpeedMin}..{x.SpeedMax}, " +
                              $"accel {x.AccelMin}..{x.AccelMax}, |vel| <= {x.VelMax}; " +
                              $"actions {string.Join(',', x.Actions.Select(a => $"0x{a:X2}"))}, " +
                              $"types {string.Join(',', x.Types)} (x{Multiplier:0.##})");
        _sites.Clear();
    }

    // ---- Tracking while standing, and the trace ----

    const uint Stage4 = 0x80040348;
    const uint EntityBase = 0x8016C544;
    const int EntityStride = 0x7C, EntityCount = 200, ActiveOff = 0x9, HpOff = 0x1A;
    const int RuleOff = 0x60, TypeDescStride = 120;
    const uint DescBase = 0x80172624;
    const int DescAccelOff = 0x4;
    const int Actions = 0x20;   // the motion routine's jump table at 0x800120D8

    /// <summary>Stage 4's own run count, the word its one-in-four think test reads;
    /// bumped once at the end of every run. The post fires on frames the stage gate
    /// skipped too, and this is how it tells them apart.</summary>
    const uint Stage4Runs = 0x80175908;
    const int TraceReach = 12000;

    static readonly Dictionary<uint, (bool Chase, int Target)> _turned = [];
    static readonly Dictionary<uint, (int X, int Z)> _lastPos = [];
    static readonly HashSet<uint> _engaged = [];
    static uint _lastRun = uint.MaxValue;
    static int _tick;

    public static void AfterStage4(CpuContext c, IMemory m)
    {
        uint run = m.ReadU32(Stage4Runs);
        if (run == _lastRun) return;
        _lastRun = run;
        _tick++;

        bool track = Track && Multiplier > 1f && _hooked;
        int px = (int)m.ReadU32(PlayerX), pz = (int)m.ReadU32(PlayerZ);
        for (int i = 0; i < EntityCount; i++)
        {
            uint rec = EntityBase + (uint)(i * EntityStride);
            if (m.ReadU8(rec) == 0xFF || m.ReadU8(rec + ActiveOff) != 1)
            {
                _lastPos.Remove(rec);
                _engaged.Remove(rec);
                continue;
            }
            int x = (int)m.ReadU32(rec + PosXOff), z = (int)m.ReadU32(rec + PosZOff);
            bool stood = _lastPos.TryGetValue(rec, out var last) && last.X == x && last.Z == z;
            _lastPos[rec] = (x, z);
            bool turned = _turned.TryGetValue(rec, out var t);

            // A killing hit sets HP to 0 and installs the death rule (func_8003A9CC),
            // so a creature at 0 is dying and keeps the facing it fell with. An action
            // past the motion routine's table (0xF0 and up) is no rule's: a scripted
            // or parked creature, left as the game has it.
            bool engaged = _engaged.Contains(rec);
            bool tracked = track && engaged && stood && !turned && m.ReadU16(rec + HpOff) != 0 &&
                           m.ReadU8(rec + ActionOff) < Actions;
            if (tracked) TurnAtPlayer(c, m, rec);

            if (!_trace) continue;
            int dist = (int)MathF.Sqrt((float)(px - x) * (px - x) + (float)(pz - z) * (pz - z));
            if (dist > TraceReach) continue;
            int yaw = m.ReadU16(rec + YawOff) & 0xFFF;
            string turn = turned ? $"turn {(t.Chase ? "chase" : "other")} to {Wrap(t.Target - Bearing(rec, m)):+0;-0}"
                        : tracked ? "tracked" : stood ? engaged ? "no turn, standing, engaged" : "no turn, standing" : "no turn";
            Console.WriteLine($"[KF2] enemy trace: t{_tick} slot {i} type {m.ReadU8(rec + TypeOff)} " +
                              $"act 0x{m.ReadU8(rec + ActionOff):X2} rule 0x{m.ReadU32(rec + RuleOff):X8} " +
                              $"hp {m.ReadU16(rec + HpOff)} off {Wrap(Bearing(rec, m) - yaw):+0;-0} " +
                              $"vel {(short)m.ReadU16(rec + VelOff)} dist {dist} {turn}");
        }
        _turned.Clear();
    }

    /// <summary>The game's own turn, aimed at the player at <see cref="TrackSpeed"/>
    /// and the type's acceleration. It goes through the hooked routine, so
    /// <see cref="BeforeTurn"/> sees a turn at the player and scales it.</summary>
    static void TurnAtPlayer(CpuContext c, IMemory m, uint rec)
    {
        uint desc = DescBase + (uint)(m.ReadU8(rec + TypeOff) * TypeDescStride);
        var saved = c.Snapshot();
        try
        {
            c.A0 = rec;
            c.A1 = (uint)Bearing(rec, m);
            c.A2 = TrackSpeed;
            c.A3 = m.ReadU8(desc + DescAccelOff);
            c.RA = Stage4;   // only a marker for the census; the routine returns to C#
            KingsField2.func_8003C8D8(c, m);
        }
        finally
        {
            c.Restore(saved);
        }
    }

    /// <summary>An angle difference into -2048..2047.</summary>
    static int Wrap(int d) => ((d + 0x800) & 0xFFF) - 0x800;
}
