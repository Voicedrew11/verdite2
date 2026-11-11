using System;
using System.Collections.Generic;
using System.Diagnostics;
using Spu = RecompOne.Runtime.Spu;

namespace Kf2.Japanese;

/// <summary>
/// The Japanese movies' music. Brought in from the Japanese Disc mod (verdite2-mods,
/// mods/jpdisc, Music.cs) unchanged but for its name; <see cref="JapaneseMovies"/> plays it.
///
/// The Japanese OP\OP.S and OP\ED.S carry no sound at all: the Japanese OPEN.EXE and
/// END.EXE play each one's music as a libsnd sequence while it streams (OPEN.EXE
/// func_800134F0: SsSeqPlay of the second SEQ in OP\OP.D, once; END.EXE: the SEQ in
/// OP\ED.D, on repeat, faded out from frame 1336 over 64 frames and started again
/// once for the credits). The US movies carry their own (narrated) XA sound
/// instead, and the US END.EXE is built without SsVabOpen, SsSeqOpen and SsSeqPlay,
/// so the sequence is played here, onto the port's SPU directly.
///
/// ---- the data ----
///
/// - VAB ("pBAV"): a 32-byte header (+0x12 programs, +0x14 tones, +0x16 VAGs, +0x18
///   master volume, +0x19 pan); 128 programs of 16 bytes (+0 tones, +1 volume, +4
///   pan); then 16 tones of 32 bytes for each program that has any, in order (+1
///   mode, 4 = reverb; +2 volume; +3 pan; +4 centre note; +5 fine tune in 128ths of
///   a semitone; +6, +7 the key range; +0xC, +0xD the pitch-bend range; +0x10 ADSR1;
///   +0x12 ADSR2; +0x16 the VAG, from 1); then 256 VAG sizes in 8-byte units (VAG n
///   starts where VAG n - 1 ends; VAG 1 at the body's start). The body is the VAGs,
///   SPU ADPCM, which go to SPU RAM as they are.
/// - SEQ ("pQES"): +4 version (BE), +8 ticks a quarter note (BE u16), +10 the tempo
///   in microseconds a quarter note (BE 24 bits), +13 the time signature, then MIDI
///   events from +15 with running status: 9n note on (velocity 0 off), 8n off, Bn
///   control (7 volume, 10 pan, 11 expression), Cn program, En pitch bend, FF 51
///   tempo, FF 2F the end. Both songs here use only notes, programs and volume.
///
/// ---- playing it ----
///
/// The VAB body goes into SPU RAM below the reverb's work area (above whatever
/// libsnd put at the bottom). Each note takes one SPU voice per tone of its program
/// whose key range holds it, set up as libsnd's own note-on does it (OPEN.EXE's
/// NoteOn 0x8002448C, SpuVmKeyOn 0x80021B5C, the pitch at 0x80020D30 and the volume
/// at 0x80020040):
///
/// - Pitch: n = note + 60 - centre; the table at 0x8003DE18 (16 steps a semitone,
///   0x1000 at step 0) at (n % 12) x 16 + fine / 8, shifted left by n / 12 - 5.
/// - Volume, in integers: velocity x the channel's volume (control 7, as it is at the
///   key-on) / 127; x VAB volume x 0x3FFF / 0x3F01; x program volume x tone volume /
///   0x3F01; x the song's volume / 127. Each pan (tone, program, then the channel's at
///   the key-on) turns one side down: under 64 the right by pan / 64, else the left
///   by (127 - pan) / 64. Then each side is squared over 0x3FFF: libsnd's curve.
/// - The tone's ADSR as it is; reverb on for tones whose mode has bit 2.
///
/// The song is timed by the sound itself: the port's mixer calls Spu.Mixed after each
/// buffer it mixes (256 samples at 44.1 kHz), and the events due by then are played
/// out there, so they land within a buffer of their time however the game's frames
/// come. (While no sound is being mixed, VSync moves it on by the clock instead.)
/// Voices are taken from 23 down, so whatever libsnd keeps at the bottom is left
/// alone, and every one this keyed is keyed off when it stops.
/// </summary>
static class MovieMusic
{
    const uint Spu0 = 0x1F801C00, Kon = 0x1F801D88, Koff = 0x1F801D8C, Pmon = 0x1F801D90, Non = 0x1F801D94, Eon = 0x1F801D98;
    const uint RevStart = 0x1F801DA2, SpuCnt = 0x1F801DAA, RevVolL = 0x1F801D84, RevRegs = 0x1F801DC0;

    sealed class Tone { public int Mode, Vol, Pan, Center, Fine, Min, Max, PbMin, PbMax, Adsr1, Adsr2, Vag; }
    sealed class Program { public int Vol, Pan; public List<Tone> Tones = new(); }
    readonly record struct Ev(long Tick, byte Kind, byte Ch, int A, int B);   // Kind: 0 off, 1 on, 2 control, 3 program, 4 bend, 5 tempo, 6 end

    sealed class Song
    {
        public int Resolution, Tempo;
        public List<Ev> Events = new();
    }

    sealed class Voice
    {
        public bool Held, Used;
        public int Ch, Note, Vel, Pan;      // Vel: velocity x channel volume / 127, as at the key-on; Pan: the channel's then
        public Tone? Tone;
        public Program? Prog;
        public long Since;
    }

    sealed class Channel { public int Prog, Vol = 127, Pan = 64, Expr = 127, Bend = 8192; }

    static Program?[] _progs = new Program?[128];
    static int _vabVol = 127, _vabPan = 64;
    static uint[] _vagAddr = new uint[256];
    static Song? _song;
    static readonly Voice[] _v = new Voice[24];
    static readonly Channel[] _ch = new Channel[16];
    static readonly object _gate = new();
    static double _now;             // seconds of the song played
    static readonly Stopwatch _sinceMix = Stopwatch.StartNew(), _sinceTick = Stopwatch.StartNew();
    static bool _attached;
    static readonly Action<short[], short[], int> _onMixed = (_, _, frames) => Mixed(frames);
    static readonly ushort[] PitchTable = new ushort[192];
    static double _tickAt;          // ticks played up to _clockAt
    static double _clockAt;         // seconds
    static int _tempo, _next, _plays;
    static int _vol = 64;           // the song's volume, as SsSeqSetVol takes it
    static long _serial;
    /// <summary>A song is loaded and sounding (its last notes may be ringing out
    /// after its end), until Stop().</summary>
    public static bool Playing { get; private set; }
    static bool _done;              // past the song's end: nothing more to play
    /// <summary>Past the song's end (its held notes may still be ringing).</summary>
    public static bool Done => _done;
    public static string Status = "";

    // The reverb as OPEN.EXE's libsnd set it up (type 5, depth 0x20), to set up
    // the same in END.EXE, which turns no reverb on.
    static ushort[]? _reverb;

    static Spu? Chip => RecompOne.Runtime.Runtime.Spu;

    static MovieMusic()
    {
        for (int i = 0; i < 24; i++) _v[i] = new Voice();
        for (int i = 0; i < 16; i++) _ch[i] = new Channel();
        // libsnd's pitch table (OPEN.EXE 0x8003DE18): 12 semitones of 16 steps
        for (int i = 0; i < 192; i++) PitchTable[i] = (ushort)Math.Floor(4096 * Math.Pow(2, i / 192.0));
    }

    /// <summary>Time the song by the mixer from now on.</summary>
    static void Attach()
    {
        if (_attached) return;
        Spu.Mixed += _onMixed;
        _attached = true;
    }

    /// <summary>Stop listening to the mixer.</summary>
    public static void Detach()
    {
        Stop();
        if (_attached) Spu.Mixed -= _onMixed;
        _attached = false;
    }

    static void Mixed(int frames)
    {
        _sinceMix.Restart();
        lock (_gate)
        {
            if (!Playing) return;
            _now += frames / 44100.0;
            // this is the port's mixer thread: nothing may be thrown back into it
            try { Advance(); }
            catch (Exception e) { Playing = false; Status = "the music: " + e.Message; }
        }
    }

    static int U16(byte[] d, int o) => d[o] | d[o + 1] << 8;

    /// <summary>Load a VAB (header and body) into SPU RAM and a SEQ, and play it
    /// <paramref name="plays"/> times (0: until stopped).</summary>
    public static bool Start(byte[] head, byte[] body, byte[] seq, int plays, int volume)
    {
        Attach();
        lock (_gate) return StartLocked(head, body, seq, plays, volume);
    }

    static bool StartLocked(byte[] head, byte[] body, byte[] seq, int plays, int volume)
    {
        StopLocked();
        var spu = Chip;
        if (spu == null) { Status = "no SPU"; return false; }
        try
        {
            ReadVab(head);
            _song = ReadSeq(seq);
        }
        catch (Exception e) { Status = "the music: " + e.Message; Console.WriteLine($"[jpdisc] music: {e.Message}"); return false; }

        uint top = 0x80000;
        if ((spu.ReadReg16(SpuCnt) & 0x80) != 0 && spu.ReadReg16(RevStart) != 0) top = (uint)spu.ReadReg16(RevStart) << 3;
        uint at = (uint)Math.Max(0x1010, (top - body.Length - 0x40) & ~0xF);
        spu.DmaWrite(at, body);
        uint off = 0;
        for (int i = 1; i < 256; i++) { _vagAddr[i] = at + off; off += (uint)(U16(head, VagTable(head) + 2 * i) * 8); }

        foreach (var c in _ch) { c.Prog = 0; c.Vol = 127; c.Pan = 64; c.Expr = 127; c.Bend = 8192; }
        foreach (var v in _v) { v.Held = false; v.Used = false; }
        // Our voices make no noise and take no pitch modulation.
        spu.WriteReg16(Non, 0); spu.WriteReg16(Non + 2, 0);
        spu.WriteReg16(Pmon, 0); spu.WriteReg16(Pmon + 2, 0);
        _tempo = _song.Tempo; _next = 0; _tickAt = 0; _clockAt = 0; _now = 0; _plays = plays; _vol = volume;
        Playing = true; _done = false;
        Status = $"playing ({_song.Events.Count} events)";
        return true;
    }

    static int VagTable(byte[] head)
    {
        int blocks = 0;
        for (int p = 0; p < 128; p++) if (head[32 + p * 16] > 0) blocks++;
        return 32 + 128 * 16 + blocks * 512;
    }

    static void ReadVab(byte[] h)
    {
        if (h.Length < 32 || h[0] != 'p' || h[1] != 'B' || h[2] != 'A' || h[3] != 'V') throw new InvalidOperationException("not a VAB");
        _vabVol = h[0x18]; _vabPan = h[0x19];
        _progs = new Program?[128];
        int block = 0;
        for (int p = 0; p < 128; p++)
        {
            int o = 32 + p * 16, tones = h[o];
            if (tones == 0) continue;
            var prog = new Program { Vol = h[o + 1], Pan = h[o + 4] };
            for (int t = 0; t < tones && t < 16; t++)
            {
                int q = 32 + 128 * 16 + block * 512 + t * 32;
                prog.Tones.Add(new Tone
                {
                    Mode = h[q + 1], Vol = h[q + 2], Pan = h[q + 3], Center = h[q + 4], Fine = h[q + 5],
                    Min = h[q + 6], Max = h[q + 7], PbMin = h[q + 0xC], PbMax = h[q + 0xD],
                    Adsr1 = U16(h, q + 0x10), Adsr2 = U16(h, q + 0x12), Vag = (short)U16(h, q + 0x16),
                });
            }
            _progs[p] = prog;
            block++;
        }
    }

    static Song ReadSeq(byte[] d)
    {
        if (d.Length < 15 || d[0] != 'p' || d[1] != 'Q' || d[2] != 'E' || d[3] != 'S') throw new InvalidOperationException("not a SEQ");
        var s = new Song { Resolution = d[8] << 8 | d[9], Tempo = d[10] << 16 | d[11] << 8 | d[12] };
        int p = 15, status = 0; long t = 0;
        int Vlq()
        {
            int v = 0;
            while (p < d.Length) { int b = d[p++]; v = v << 7 | (b & 0x7F); if ((b & 0x80) == 0) break; }
            return v;
        }
        while (p < d.Length)
        {
            t += Vlq();
            if (p >= d.Length) break;
            if ((d[p] & 0x80) != 0) status = d[p++];
            int hi = status & 0xF0, ch = status & 15;
            if (status == 0xFF)
            {
                int type = d[p++];
                if (type == 0x51) { s.Events.Add(new Ev(t, 5, 0, d[p] << 16 | d[p + 1] << 8 | d[p + 2], 0)); p += 3; }
                else if (type == 0x2F) { s.Events.Add(new Ev(t, 6, 0, 0, 0)); break; }
                else throw new InvalidOperationException($"meta event {type:X2}");
            }
            else if (hi == 0x90) { int n = d[p], v = d[p + 1]; p += 2; s.Events.Add(new Ev(t, (byte)(v > 0 ? 1 : 0), (byte)ch, n, v)); }
            else if (hi == 0x80) { int n = d[p]; p += 2; s.Events.Add(new Ev(t, 0, (byte)ch, n, 0)); }
            else if (hi == 0xB0) { s.Events.Add(new Ev(t, 2, (byte)ch, d[p], d[p + 1])); p += 2; }
            else if (hi == 0xC0) { s.Events.Add(new Ev(t, 3, (byte)ch, d[p], 0)); p += 1; }
            else if (hi == 0xE0) { s.Events.Add(new Ev(t, 4, (byte)ch, d[p + 1] << 7 | d[p], 0)); p += 2; }
            else throw new InvalidOperationException($"MIDI status {status:X2}");
        }
        if (s.Events.Count == 0 || s.Events[^1].Kind != 6) s.Events.Add(new Ev(t, 6, 0, 0, 0));
        return s;
    }

    /// <summary>The song's volume, 0-127 (SsSeqSetVol's).</summary>
    public static void SetVolume(int v)
    {
        lock (_gate) SetVolumeLocked(v);
    }

    static void SetVolumeLocked(int v)
    {
        v = Math.Clamp(v, 0, 127);
        if (v == _vol) return;
        _vol = v;
        var spu = Chip;
        if (spu == null) return;
        for (int i = 0; i < 24; i++) if (_v[i].Used) Volume(spu, i);
    }

    public static int Volume() => _vol;

    /// <summary>Once a VSync: while the mixer isn't running (no sound device), move
    /// the song on by the clock.</summary>
    public static void Tick()
    {
        double dt = _sinceTick.Elapsed.TotalSeconds;
        _sinceTick.Restart();
        if (_sinceMix.Elapsed.TotalSeconds < 0.25) return;
        lock (_gate)
        {
            if (!Playing) return;
            _now += Math.Min(dt, 0.1);
            Advance();
        }
    }

    /// <summary>Play out the events due by <see cref="_now"/>.</summary>
    static void Advance()
    {
        if (!Playing || _done || _song == null) return;
        var spu = Chip;
        if (spu == null) return;
        double now = _now;
        double ticks = _tickAt + (now - _clockAt) * _song.Resolution * 1e6 / Math.Max(1, _tempo);
        var ev = _song.Events;
        while (Playing && _next < ev.Count && ev[_next].Tick <= ticks)
        {
            var e = ev[_next++];
            switch (e.Kind)
            {
                case 0: NoteOff(spu, e.Ch, e.A); break;
                case 1: NoteOn(spu, e.Ch, e.A, e.B); break;
                case 2: Control(spu, e.Ch, e.A, e.B); break;
                case 3: _ch[e.Ch].Prog = e.A; break;
                case 4: _ch[e.Ch].Bend = e.A; foreach (var i in Held(e.Ch)) Pitch(spu, i); break;
                case 5:
                    // the tempo changes from this tick on
                    _clockAt += (e.Tick - _tickAt) * Math.Max(1, _tempo) / (_song.Resolution * 1e6);
                    _tickAt = e.Tick;
                    _tempo = e.A;
                    ticks = _tickAt + (now - _clockAt) * _song.Resolution * 1e6 / Math.Max(1, _tempo);
                    break;
                case 6:
                    if (_plays == 1 || e.Tick == 0)
                    {
                        // The song is over, but its last notes ring on (libsnd stops
                        // sequencing; it doesn't cut what is sounding): held notes are
                        // let go, and everything is silenced only by Stop().
                        LetGo(spu);
                        _done = true;
                        Status = "finished, ringing out";
                        return;
                    }
                    if (_plays > 1) _plays--;
                    // again from the top
                    Release(spu);
                    _next = 0; _tempo = _song.Tempo; _tickAt = 0; _clockAt = now; ticks = 0;
                    foreach (var c in _ch) { c.Vol = 127; c.Pan = 64; c.Expr = 127; c.Bend = 8192; }
                    break;
            }
        }
    }

    /// <summary>The same song again from the top, <paramref name="plays"/> times
    /// (0: until stopped), at this volume.</summary>
    public static void Restart(int plays, int volume)
    {
        lock (_gate) RestartLocked(plays, volume);
    }

    static void RestartLocked(int plays, int volume)
    {
        var spu = Chip;
        if (_song == null || spu == null) return;
        if (Playing) Release(spu);
        foreach (var c in _ch) { c.Prog = 0; c.Vol = 127; c.Pan = 64; c.Expr = 127; c.Bend = 8192; }
        _tempo = _song.Tempo; _next = 0; _tickAt = 0; _clockAt = 0; _now = 0; _plays = plays; _vol = volume;
        Playing = true; _done = false;
        Status = "playing again from the top";
    }

    /// <summary>Stop at once: every voice this keyed is keyed off.</summary>
    public static void Stop()
    {
        lock (_gate) StopLocked();
    }

    static void StopLocked()
    {
        var spu = Chip;
        if (Playing && spu != null) Release(spu);
        Playing = false;
        _song = null;
    }

    static IEnumerable<int> Held(int ch)
    {
        for (int i = 0; i < 24; i++) if (_v[i].Held && _v[i].Ch == ch) yield return i;
    }

    static void Control(Spu spu, int ch, int c, int v)
    {
        var s = _ch[ch];
        if (c == 7) s.Vol = v;
        else if (c == 10) s.Pan = v;
        else if (c == 11) s.Expr = v;
        // libsnd only notes them: a note takes the channel's volume and pan as they
        // are when it is keyed on
    }

    static void NoteOn(Spu spu, int ch, int note, int vel)
    {
        var prog = _progs[_ch[ch].Prog & 127];
        if (prog == null) return;
        uint on = 0;
        foreach (var t in prog.Tones)
        {
            if (note < t.Min || note > t.Max || t.Vag <= 0 || t.Vag > 255) continue;
            int i = Free();
            var v = _v[i];
            v.Held = true; v.Used = true; v.Ch = ch; v.Note = note; v.Vel = vel * _ch[ch].Vol / 127; v.Pan = _ch[ch].Pan;
            v.Tone = t; v.Prog = prog; v.Since = ++_serial;
            uint r = Spu0 + (uint)i * 16;
            Volume(spu, i);
            Pitch(spu, i);
            spu.WriteReg16(r + 6, (ushort)(_vagAddr[t.Vag] >> 3));
            spu.WriteReg16(r + 8, (ushort)t.Adsr1);
            spu.WriteReg16(r + 0xA, (ushort)t.Adsr2);
            Reverb(spu, i, (t.Mode & 4) != 0);
            on |= 1u << i;
        }
        if (on != 0) { spu.WriteReg16(Kon, (ushort)on); spu.WriteReg16(Kon + 2, (ushort)(on >> 16)); }
    }

    static void NoteOff(Spu spu, int ch, int note)
    {
        uint off = 0;
        for (int i = 0; i < 24; i++)
        {
            var v = _v[i];
            if (!v.Held || v.Ch != ch || v.Note != note) continue;
            v.Held = false; v.Since = ++_serial;
            off |= 1u << i;
        }
        if (off != 0) { spu.WriteReg16(Koff, (ushort)off); spu.WriteReg16(Koff + 2, (ushort)(off >> 16)); }
    }

    /// <summary>Every held note let go, to ring out by its own release.</summary>
    static void LetGo(Spu spu)
    {
        uint off = 0;
        for (int i = 0; i < 24; i++) if (_v[i].Held) { off |= 1u << i; _v[i].Held = false; _v[i].Since = ++_serial; }
        if (off != 0) { spu.WriteReg16(Koff, (ushort)off); spu.WriteReg16(Koff + 2, (ushort)(off >> 16)); }
    }

    /// <summary>Every voice this keyed, keyed off and silenced, so nothing of it rings
    /// on into what the game plays next.</summary>
    static void Release(Spu spu)
    {
        uint off = 0;
        for (int i = 0; i < 24; i++) if (_v[i].Used) { off |= 1u << i; _v[i].Held = false; _v[i].Used = false; }
        if (off == 0) return;
        spu.WriteReg16(Koff, (ushort)off); spu.WriteReg16(Koff + 2, (ushort)(off >> 16));
        for (int i = 0; i < 24; i++)
            if ((off & 1u << i) != 0) { spu.WriteReg16(Spu0 + (uint)i * 16, 0); spu.WriteReg16(Spu0 + (uint)i * 16 + 2, 0); }
    }

    /// <summary>A voice for a new note: one never used, else the one let go the
    /// longest ago, else the oldest held; from voice 23 down.</summary>
    static int Free()
    {
        for (int i = 23; i >= 0; i--) if (!_v[i].Used) return i;
        int best = -1;
        for (int i = 23; i >= 0; i--) if (!_v[i].Held && (best < 0 || _v[i].Since < _v[best].Since)) best = i;
        if (best >= 0) return best;
        best = 23;
        for (int i = 23; i >= 0; i--) if (_v[i].Since < _v[best].Since) best = i;
        return best;
    }

    static void Volume(Spu spu, int i)
    {
        var v = _v[i];
        if (v.Tone == null || v.Prog == null) return;
        int a = v.Vel * (_vabVol * 0x3FFF) / 0x3F01;
        a = a * v.Prog.Vol * v.Tone.Vol / 0x3F01;
        int l = a * _vol / 127, r = a * _vol / 127;
        foreach (int pan in new[] { v.Tone.Pan, v.Prog.Pan, v.Pan })
        {
            if (pan < 64) r = r * pan >> 6;
            else l = l * (127 - pan) >> 6;
        }
        l = l * l / 0x3FFF; r = r * r / 0x3FFF;
        uint reg = Spu0 + (uint)i * 16;
        spu.WriteReg16(reg, (ushort)Math.Clamp(l, 0, 0x3FFF));
        spu.WriteReg16(reg + 2, (ushort)Math.Clamp(r, 0, 0x3FFF));
    }

    static void Pitch(Spu spu, int i)
    {
        var v = _v[i];
        if (v.Tone == null) return;
        var t = v.Tone;
        int n = v.Note + 60 - t.Center, fine = Math.Min(t.Fine / 8, 15);
        int bend = _ch[v.Ch].Bend - 8192;                 // the songs here have none; as semitone steps of 1/16
        if (bend != 0)
        {
            int steps = (int)Math.Round((bend >= 0 ? bend / 8192.0 * t.PbMax : bend / 8192.0 * t.PbMin) * 16);
            int all = n * 16 + fine + steps;
            n = (int)Math.Floor(all / 16.0); fine = all - n * 16;
        }
        int oct = n >= 0 ? n / 12 : (n - 11) / 12, semi = n - oct * 12;
        int p = PitchTable[semi * 16 + fine], shift = oct - 5;
        p = shift >= 0 ? p << shift : p >> -shift;
        spu.WriteReg16(Spu0 + (uint)i * 16 + 4, (ushort)Math.Clamp(p, 0, 0x3FFF));
    }

    static void Reverb(Spu spu, int i, bool on)
    {
        uint reg = Eon + (i >= 16 ? 2u : 0u);
        int bit = i & 15;
        ushort e = spu.ReadReg16(reg);
        e = on ? (ushort)(e | 1 << bit) : (ushort)(e & ~(1 << bit));
        spu.WriteReg16(reg, e);
    }

    // ---- the reverb, from OPEN.EXE to END.EXE ----

    /// <summary>Note the reverb as libsnd has it now (OPEN.EXE, once it is set up).</summary>
    public static void KeepReverb()
    {
        var spu = Chip;
        if (spu == null || (spu.ReadReg16(SpuCnt) & 0x80) == 0) return;
        var r = new ushort[32 + 3];
        for (int k = 0; k < 32; k++) r[k] = spu.ReadReg16(RevRegs + (uint)k * 2);
        r[32] = spu.ReadReg16(RevStart);
        r[33] = spu.ReadReg16(RevVolL);
        r[34] = spu.ReadReg16(RevVolL + 2);
        _reverb = r;
    }

    /// <summary>Set up the reverb noted in OPEN.EXE, where none is on (END.EXE).</summary>
    public static void ApplyReverb()
    {
        var spu = Chip;
        if (spu == null || _reverb == null || (spu.ReadReg16(SpuCnt) & 0x80) != 0) return;
        var r = _reverb;
        // the work area, cleared, so whatever was there doesn't echo
        uint start = (uint)r[32] << 3;
        if (start > 0 && start < 0x80000) spu.DmaWrite(start, new byte[0x80000 - start]);
        spu.WriteReg16(RevStart, r[32]);
        for (int k = 0; k < 32; k++) spu.WriteReg16(RevRegs + (uint)k * 2, r[k]);
        spu.WriteReg16(RevVolL, r[33]);
        spu.WriteReg16(RevVolL + 2, r[34]);
        spu.WriteReg16(SpuCnt, (ushort)(spu.ReadReg16(SpuCnt) | 0x80));
    }
}
