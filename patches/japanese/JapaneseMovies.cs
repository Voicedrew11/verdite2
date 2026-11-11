using System;
using System.Collections.Generic;
using System.Reflection;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Context;
using RecompOne.Runtime.Events;
using RecompOne.Runtime.Memory;
using RecompOne.Runtime.Modding;
using RecompOne.Runtime.Sdk;

namespace Kf2.Japanese;

/// <summary>
/// A Japanese run's title screen, opening and ending: the Japanese release's, as the
/// Japanese Disc mod (verdite2-mods, mods/jpdisc) plays them, without its re-lettering
/// of the movies' cards in English. The mod's notes are the reference for every address
/// here (its NOTES.md and Hooks.cs); docs/JAPANESE.md, "The title and the movies".
///
/// - The movies: the US prologue OP\OP2.S is served sector for sector from the Japanese
///   OP\OP.S, and the US ending OP\ED0.S from the Japanese OP\ED.S (MixedDiscImage's
///   stream ranges). The footage is the same frame for frame; the Japanese files carry
///   the Japanese text cards and no sound, take fewer sectors a frame, and so play at
///   their own 15 frames a second. The ending is longer: END.EXE's frame count to stop
///   at is raised to the Japanese END.EXE's, 0x858, so the Japanese staff credits play.
///   The US ending's second part (ED1.S: the four spells, the US credits, The End) is
///   left out, as the mod does by default, and the Japanese last frame held.
/// - Their music: the Japanese programs play it as a libsnd sequence while each movie
///   streams (OP\OP.D's second SEQ once; OP\ED.D's on repeat, turned down from frame
///   0x538 and started once more for the credits). The US END.EXE cannot, so
///   <see cref="MovieMusic"/> plays both onto the port's SPU.
/// - The title: the US OPEN.EXE reads OP\OPU.D for its pictures, and OP\OP.D (the
///   Japanese gold logo and the doorway, also on the US disc) with four buttons held.
///   This makes it read OP\OP.D.
///
/// Stands down while the Japanese Disc mod is enabled, which then does all of it.
/// </summary>
public static class JapaneseMovies
{
    const string UsOpening = "OP/OP2.S", UsEnding = "OP/ED0.S", JpOpening = "OP/OP.S", JpEnding = "OP/ED.S";
    const string ModId = "kf2.jpdisc";

    // ---- OPEN.EXE ----
    const uint OpenMovie = 0x80013DC8, OpenStream = 0x80014170, SetMasterVolume = 0x8001FD90;
    const uint OpenShown = 0x8003EA58, OpenStopAt = 0x8003EA60;
    const uint TitleLoad = 0x80012084, TitleName = 0x80011000;
    const int PrologueMovie = 2;

    // ---- END.EXE ----
    const uint EndMovie = 0x80011CC4, EndStream = 0x80011EA0;
    const uint EndStopAt = 0x80039A04, EndShown = 0x800399FC;
    const int JpEndFrames = 0x858, JpEndFade = 0x538, SongVolume = 0x40;
    const int EndingMovie = 0, UsCreditsMovie = 1;
    const int OpeningRingVSyncs = 90;

    static readonly ModInfo _self = new()
    {
        Id = "kf2.japanesemovies",
        Name = "Japanese title and movies",
        Version = "1.0",
        Description = "The Japanese release's title screen, opening and ending, with their music, in a Japanese run.",
    };

    static bool _queued;
    static byte[]? _opd, _edd;
    static int _openMovie = -1, _endMovie = -1;
    static bool _openMusic, _endMusic, _endAgain;

    /// <summary>The videos served from the Japanese disc, keyed by the US file. A pair missing
    /// on either disc, or a Japanese file longer than the US one, is left out with a note.</summary>
    public static IReadOnlyList<StreamRange> BuildStreamRanges(DiscFs us, DiscFs jp, List<string> notes)
    {
        var ranges = new List<StreamRange>();
        foreach (var (usPath, jpPath) in new[] { (UsOpening, JpOpening), (UsEnding, JpEnding) })
        {
            if (!us.Locate(usPath, out int usLba, out uint usSize) || !jp.Locate(jpPath, out int jpLba, out uint jpSize))
            {
                notes.Add($"{usPath}: no {jpPath} on the Japanese disc; the US movie plays");
                continue;
            }
            int usSectors = Sectors(usSize), jpSectors = Sectors(jpSize);
            if (jpSectors > usSectors)
            {
                notes.Add($"{jpPath} is {jpSectors} sectors, more than {usPath}'s {usSectors}; the US movie plays");
                continue;
            }
            ranges.Add(new StreamRange(usLba, usSectors, jpLba, jpSectors));
            notes.Add($"{usPath}: served from {jpPath} ({jpSectors} of {usSectors} sectors)");
        }
        return ranges;
    }

    static int Sectors(uint bytes) => (int)((bytes + DiscArchive.SectorBytes - 1) / DiscArchive.SectorBytes);

    public static void Install()
    {
        Event.AddListener<VSyncEvent>(_ =>
        {
            if (!JapaneseDisc.Active) return;
            try { MovieMusic.Tick(); EndingMusic(); }
            catch (Exception e) { Console.WriteLine($"[KF2] japanese: the movies' music stops: {e.Message}"); MovieMusic.Stop(); }
        });
        Event.AddListener<OverlayLoadedEvent>(_ =>
        {
            // A new executable: whatever was playing belongs to the old one.
            MovieMusic.Stop();
            _endMovie = _openMovie = -1;
        });
        HookAttach.OnOverlayLoad("japanese movies", Attach);
    }

    static bool Attach()
    {
        if (!JapaneseDisc.Active) return true;
        SymbolRegistry.Build();
        var hooks = new (string Overlay, uint Address, bool Pre, string Impl)[]
        {
            ("open", TitleLoad, true, nameof(BeforeTitleLoad)),
            ("open", OpenMovie, true, nameof(BeforeOpenMovie)),
            ("open", OpenMovie, false, nameof(AfterOpenMovie)),
            ("open", OpenStream, true, nameof(BeforeOpenStream)),
            ("open", SetMasterVolume, true, nameof(BeforeMasterVolume)),
            ("end", EndMovie, true, nameof(BeforeEndMovie)),
            ("end", EndStream, true, nameof(BeforeEndStream)),
        };
        var targets = new List<MethodInfo>();
        foreach (var hook in hooks)
        {
            if (SymbolRegistry.Resolve(hook.Overlay, null, hook.Address) is not { } target)
            {
                Console.Error.WriteLine($"[KF2] japanese movies: {hook.Overlay} 0x{hook.Address:X8} did not resolve; the US title and movies");
                return false;
            }
            targets.Add(target);
        }

        if (!_queued)
        {
            _queued = true;
            _opd = JapaneseDisc.ReadJapaneseFile("OP/OP.D");
            _edd = JapaneseDisc.ReadJapaneseFile("OP/ED.D");
            for (int i = 0; i < hooks.Length; i++)
            {
                var method = typeof(JapaneseMovies).GetMethod(hooks[i].Impl, BindingFlags.Public | BindingFlags.Static)!;
                if (hooks[i].Pre) HookManager.AddPre(_self, targets[i], method);
                else HookManager.AddPost(_self, targets[i], method);
            }
        }
        HookManager.Commit();
        bool ok = targets.TrueForAll(HookAttach.Installed);
        if (!ok) Console.Error.WriteLine("[KF2] japanese movies: a hook did not commit; retrying on the next overlay load");
        return ok;
    }

    /// <summary>The Japanese Disc mod is enabled: it plays the title and movies, and this stands down.</summary>
    static bool ModEnabled()
    {
        foreach (var e in ModLoader.Mods)
            if (e.Enabled && string.Equals(e.Info.Id, ModId, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    static bool On => JapaneseDisc.Active && !ModEnabled();

    // ---- OPEN.EXE ----

    /// <summary>func_80012084 copies "OPU.D" from 0x80011000 to name the title's file: "OP.D" there
    /// is the Japanese title, which the US game shows with four buttons held.</summary>
    public static void BeforeTitleLoad(CpuContext c, IMemory m)
    {
        if (!On) return;
        if (m.ReadU8(TitleName) != (byte)'O' || m.ReadU8(TitleName + 1) != (byte)'P') return;
        m.WriteU8(TitleName + 2, (byte)'.');
        m.WriteU8(TitleName + 3, (byte)'D');
        m.WriteU8(TitleName + 4, 0);
        m.WriteU8(TitleName + 5, 0);
    }

    public static void BeforeOpenMovie(CpuContext c, IMemory m)
    {
        _openMovie = (int)c.A0;
        if (On) MovieMusic.KeepReverb();
    }

    /// <summary>The prologue starts streaming: the Japanese opening's music, OP.D's second SEQ, once.</summary>
    public static void BeforeOpenStream(CpuContext c, IMemory m)
    {
        if (_openMovie != PrologueMovie || !On || _opd == null) return;
        if (SoundBank.Of(_opd, 1) is not { } bank)
        {
            Console.WriteLine("[KF2] japanese: the Japanese OP.D has no opening music");
            return;
        }
        _openMusic = MovieMusic.Start(bank.Head, bank.Body, bank.Seq, 1, SongVolume);
    }

    /// <summary>The movie player's fade-out after the last frame (SsSetMVol): first the Japanese
    /// ending of the music, 90 VSyncs of the last note unless a button ended the movie, then the
    /// song turned down a step a VSync and stopped, as the Japanese func_800134F0 does.</summary>
    public static void BeforeMasterVolume(CpuContext c, IMemory m)
    {
        if (!_openMusic || !MovieMusic.Playing) return;
        _openMusic = false;
        uint a0 = c.A0, a1 = c.A1, a2 = c.A2, a3 = c.A3, v0 = c.V0, v1 = c.V1;
        try
        {
            bool skipped = m.ReadU32(OpenShown) + 2 < m.ReadU32(OpenStopAt);
            if (!skipped)
                for (int k = 0; k < OpeningRingVSyncs && MovieMusic.Playing; k++) { c.A0 = 0; LibEtc.VSync(c, m); }
            for (int v = MovieMusic.Volume(); v >= 0 && MovieMusic.Playing; v--)
            {
                MovieMusic.SetVolume(v);
                c.A0 = 0;
                LibEtc.VSync(c, m);
            }
        }
        finally
        {
            MovieMusic.Stop();
            c.A0 = a0; c.A1 = a1; c.A2 = a2; c.A3 = a3; c.V0 = v0; c.V1 = v1;
        }
    }

    public static void AfterOpenMovie(CpuContext c, IMemory m)
    {
        if (_openMusic) MovieMusic.Stop();
        _openMusic = false;
        _openMovie = -1;
    }

    // ---- END.EXE ----

    /// <summary>func_80011CC4(n): the US ending's second part (n = 1) is left out, and the Japanese
    /// ending's last frame stays up with its music playing on (EndingHold holds it).</summary>
    public static bool BeforeEndMovie(CpuContext c, IMemory m)
    {
        _endMovie = (int)c.A0;
        return !(_endMovie == UsCreditsMovie && On);
    }

    /// <summary>The ending starts streaming: the Japanese END.EXE's frame count, and ED.D's music on repeat.</summary>
    public static void BeforeEndStream(CpuContext c, IMemory m)
    {
        if (_endMovie != EndingMovie || !On) return;
        m.WriteU32(EndStopAt, JpEndFrames);
        if (_edd == null) return;
        if (SoundBank.Of(_edd, 0) is not { } bank)
        {
            Console.WriteLine("[KF2] japanese: the Japanese ED.D has no music");
            return;
        }
        MovieMusic.ApplyReverb();
        _endMusic = MovieMusic.Start(bank.Head, bank.Body, bank.Seq, 0, SongVolume);
        _endAgain = false;
    }

    /// <summary>Each VSync: the ending's music turned down from frame 0x538 and started once more,
    /// as the Japanese END.EXE does it.</summary>
    static void EndingMusic()
    {
        if (!_endMusic || _endAgain || !MovieMusic.Playing || _endMovie != EndingMovie) return;
        var m = RecompOne.Runtime.Runtime.Mem;
        if (m == null) return;
        int shown = (int)m.ReadU32(EndShown);
        if (shown < JpEndFade || shown > JpEndFrames + 8) return;
        int v = SongVolume - (shown - JpEndFade);
        if (v > 0) { MovieMusic.SetVolume(v); return; }
        MovieMusic.Restart(1, SongVolume);
        _endAgain = true;
    }
}

/// <summary>A VAB and SEQs, as OP\OP.D and OP\ED.D hold them: OP.D its title pictures (TIMs)
/// first, then the VAB header, the VAB body and two SEQs (the title's and the opening's); ED.D
/// the VAB header, the body and one SEQ. From the Japanese Disc mod.</summary>
static class SoundBank
{
    /// <summary>The VAB and the SEQ number <paramref name="seq"/> (from 0) after it, or null.</summary>
    public static (byte[] Head, byte[] Body, byte[] Seq)? Of(byte[] d, int seq)
    {
        int p = 0;
        while (p + 8 <= d.Length && BitConverter.ToUInt32(d, p) == 0x10)       // TIMs: id, flags, then blocks
        {
            uint flag = BitConverter.ToUInt32(d, p + 4);
            int q = p + 8;
            if ((flag & 8) != 0) q += (int)BitConverter.ToUInt32(d, q);
            q += (int)BitConverter.ToUInt32(d, q);
            if (q <= p || q > d.Length) return null;
            p = q;
        }
        if (p + 32 > d.Length || d[p] != 'p' || d[p + 1] != 'B' || d[p + 2] != 'A' || d[p + 3] != 'V') return null;
        int blocks = 0;
        for (int k = 0; k < 128; k++) if (d[p + 32 + k * 16] > 0) blocks++;
        int sizes = p + 32 + 128 * 16 + blocks * 512, headLen = sizes + 512 - p;
        if (sizes + 512 > d.Length) return null;
        int bodyLen = 0;
        for (int k = 0; k < 256; k++) bodyLen += BitConverter.ToUInt16(d, sizes + 2 * k) * 8;
        int body = p + headLen, s = body + bodyLen;
        if (s > d.Length) return null;
        var seqs = new List<int>();
        for (int k = s; k + 4 <= d.Length; k++)
            if (d[k] == 'p' && d[k + 1] == 'Q' && d[k + 2] == 'E' && d[k + 3] == 'S') seqs.Add(k);
        if (seq >= seqs.Count) return null;
        int from = seqs[seq], to = seq + 1 < seqs.Count ? seqs[seq + 1] : d.Length;
        return (d[p..body], d[body..s], d[from..to]);
    }
}
