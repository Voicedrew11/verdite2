# Audio

The SPU mixer, its reverb and interpolation, XA resampling and the host output.
Everything here is runtime code in the vendored tree (`patches/recompone/0043`):
`Spu` is a hardware model, not a recompiled function, so `HookManager` cannot
reach it. `patches/AudioQuality.cs` is the switch, `patches/AudioProbe.cs` the
probe and dump, `patches/settings/AudioPage.cs` the two combos under Audio.

## What the audio path is

In-game music and effects are the SPU: the game's `SsSeq`/`SpuVm` sequencer
writes voice registers, `Spu.Mix` renders 24 ADPCM voices at 44.1 kHz on the
`spu-mixer` thread, 256 frames at a time, into an 8-buffer OpenAL queue. The
intro and ending movies are XA sectors decoded by `XaAudio` and resampled to
44.1 kHz inside the same mix. Until `0043` none of it had been touched by the
port; "it sounds right" in `docs/TODO.md` was a correctness check.

**The game uses reverb everywhere.** `GAME.EXE` calls `SsUtSetReverbType(4)`
(PSY-Q's `STUDIO_C`) with depth `0x28`, and again with a per-area type from
`s1`; `OPEN.EXE` and `END.EXE` set it too. Measured: the probe identifies the
register block in area 1 as **studio large** (`dAPF1`/`dAPF2` = `0xE3`/`0xA9`).

## Measuring it

Nothing in this document can be judged from a frame counter, and the final
judgement is by ear. What can be counted:

- `KF2_AUDIO_PROBE=1` — a line a second: peak active voices, voice-sum and
  final-mix clamps, mixer cost per 256-frame buffer, OpenAL underruns and stalls,
  mix and wet RMS in dBFS, the reverb preset and which path ran it, the
  interpolation, the device rate and the resampler OpenAL is using.
- `KF2_AUDIO_DUMP=dir` — `kf2-mix-*.wav` (the final mix) and `kf2-wet-*.wav` (the
  reverb return after `vLOUT`/`vROUT`, before main volume), 16-bit stereo
  44.1 kHz, sizes rewritten every second so a killed run leaves a readable file.
- `scripts/audio_spectrum.py` — RMS, and the energy above 11.025 kHz and 16 kHz
  relative to the whole, through a 4th-order high-pass. Stdlib only.

The method used for every number below: `KF2_AUTOSTART=2`, 60 s, analyse 30-55 s
(`--skip 30 --seconds 25`), which is area 1 standing at the save point. The mixer
runs on a wall-clock thread, so two runs are never byte-identical; levels agree to
a few tenths of a dB between runs of the same mode.

## The reverb skipped the console's half-band filter

The SPU's reverb network runs at 22.05 kHz. On hardware its input is decimated
through a 39-tap half-band FIR and its output interpolated back through the same
kernel. Upstream's `Spu.Mix` did neither: it ran `ReverbStep` on every other raw
input sample (aliasing everything above 11 kHz down into the tail) and **held**
each output for two samples (imaging the whole tail around 22.05 kHz). That is
the grainy, metallic top on every wet sound.

`KF2_REVERB=hardware` adds both filters, with psx-spx's coefficients and
DuckStation's ring layout (a 64-entry input ring and a 32-entry output ring, each
stored twice so a window never wraps; even output ticks take the centre tap,
which is the stored sample). The coefficients were checked before use: flat to
8 kHz (−0.09 dB), −6.02 dB at 11.025 kHz, −92 dB at 16 kHz, DC gain 0.99994.

Measured, wet return, studio large:

| | wet RMS | >11 kHz | >16 kHz |
|---|---|---|---|
| `legacy` | −40.3 dBFS | −28.4 dB | −29.1 dB |
| `hardware` | −40.2 dBFS | −52.1 dB | −66.7 dB |

Same level, 24 dB less energy above 11 kHz and 37 dB less above 16 kHz — almost
all of legacy's HF share sat above 16 kHz, which is the hold's image. It shows in
the **final mix** too (>11 kHz −38.1 → −45.3 dB): most of what was bright about
the legacy mix was reverb artefact, not the music.

**Mechanism measured, never listened to.** `legacy` stays the default, and the
settings page's *Original* means `legacy` (`AudioQuality.OriginalReverb`), until
someone has compared the two by ear; flipping that constant is the whole change,
and no saved config needs migrating because the page saves its own index.

Switching path clears the ring state, and leaving the enhanced path clears the
reverb work area in SPU RAM, since the hardware network reads its own past
output back out of it and would replay a stale tail.

## An enhanced reverb sized from the game's registers

`KF2_REVERB=enhanced` replaces the network and nothing around it. Per-voice EON
sends, the CD reverb enable, `vLIN`/`vRIN` and `vLOUT`/`vROUT` are still the
game's, so what is wet and how wet is decided exactly as before; bit 7 of SPUCNT
gates the input. `Hardware/SpuReverb.cs` is an 8-line feedback delay network at
44.1 kHz: a pre-delay, four input allpasses per channel, Householder feedback,
a one-pole damping filter per line, and two lines modulated ±12 samples at 0.31
and 0.47 Hz to break up ringing. No decimation, so no filter to get wrong.

**It is sized from the register block, not from anything King's Field-specific**:

- loop length — `mLSAME − dLSAME` and `mLDIFF − dRDIFF`, the reflection loops
  (one register word is 8 samples at 44.1 kHz); the lines are 0.45-1.0 of it
- decay — RT60 from `vWALL`, the gain once per pass of that loop, clamped to
  0.3-6 s
- damping — `vIIR` is a one-pole low-pass in the same loop; its cutoff at 22.05 kHz
  becomes each line's
- pre-delay — the earliest comb tap behind its reflection write, less the
  shortest line

Studio large comes out at a 122 ms loop, RT60 2.4 s, 7.2 kHz damping. The
parameters are derived once the reverb registers have been still for 2048
samples, since `SpuSetReverbModeParam` writes them one at a time from the game
thread; a change of line lengths fades the old tank out and the new one in over
25 ms each way. **The echo and delay presets (`dAPF1`, `dAPF2` ≤ 1) fall back to
the hardware path** — they are discrete repeats a tank would smear.

Output gain was matched to the hardware path's wet RMS on the same room: at 0.35
it measured −43.0 dBFS against −40.2, so 0.48, which measured −40.2. Its wet
return carries −52.2 dB above 11 kHz, the same as the filtered hardware path, so
its brightness is the damping's, not an artefact.

**Mechanism measured, never listened to.** Whether it is better is the whole
question and it is not one a counter answers.

## Voice interpolation

`KF2_SPU_INTERP` / *Interpolation*: `gauss` (the console's 4-tap Gaussian, the
default), `cubic` (Catmull-Rom on the same four samples) and `sinc` (8 taps, 256
phases, Kaiser β 5).

The Gaussian is a steep low-pass — composers mastered for it, which is why it
stays the default. The sinc needed two things the 4-tap kernels do not:

- **Four more samples of history.** `Voice.Buf` is 7 history + 28 now, and the
  Gaussian and cubic read its newest four, so their output is unchanged. The sinc
  window ends on the same sample rather than reading ahead of the decoded block,
  which would have meant decoding the next block early and moving the loop and
  end flags the game reads back. The cost is two samples of latency (45 µs).
- **Band-limiting by pitch.** A voice stepping faster than its source (`Pitch` >
  `0x1000`) aliases through a full-band kernel, so the table exists at cutoffs of
  1.0, 0.75, 0.5 and 0.25 of the source Nyquist and the voice's effective step
  picks one. At phase 0 of the full band the kernel is exactly the source sample.

Measured with `KF2_REVERB=hardware`, final mix. The legacy reverb's image sits
above 16 kHz and hides this comparison completely, so under `legacy` all three
read the same within 1.4 dB:

| | >11 kHz | >16 kHz | mixer, ms per 5.8 ms buffer |
|---|---|---|---|
| `gauss` | −45.3 dB | −55.8 dB | 0.09-0.10 |
| `cubic` | −40.0 dB | −47.2 dB | 0.10 |
| `sinc` | −40.6 dB | −56.4 dB | 0.11-0.14 |

Both open up the band the Gaussian shuts, about 5 dB more between 11 and 16 kHz.
They differ above 16 kHz: the game's samples are mostly recorded well below
44.1 kHz, so almost nothing up there is source content, and cubic's 8.6 dB more
of it is its own imaging where the band-limited sinc stays at the Gaussian's
level. RMS was unchanged in every mode (−29.8 to −30.0 dBFS).

**Mechanism measured, never listened to** — including whether opening the top at
all is welcome on material mastered for the Gaussian.

## XA resampling

`XaAudio` resampled 37.8 and 18.9 kHz to 44.1 kHz **linearly**. It now follows the
same setting — `gauss` keeps the linear path unchanged, `cubic` and `sinc` use the
kernels above over an 8-sample history. Movie audio only.

**Open: not measured.** Two 45 s boots through the intro (`gauss` against
`sinc`) differed by 0.7 dB above 11 kHz, but the probe showed 24 SPU voices
active and nothing proves XA was playing inside the analysed window, so that is
no evidence either way. The probe line now says `xa playing at <rate> Hz` or
`xa idle`; the next measurement should pick its window from that.

## The host output

- **Resampler.** OpenAL Soft 1.23.1 resamples the 44.1 kHz source to the device,
  which here is 48 kHz. `AL_SOFT_source_resampler` lists its resamplers in rising
  quality, and the source now takes the last: measured `23rd order Sinc` (its
  bsinc24), where the default is cubic. Silk binds no `alGetStringiSOFT`, so the
  name is read through `NativeLibrary`.
- **Underruns.** Counted as a refill that finds every queued buffer already
  played, and stalls as a source found stopped. 0 and 0 across every run below.
- **Clamps.** The voice sum and the final mix both clamp to 16 bits, as the
  hardware does. 0 and 0 in every run, so no float mix or limiter was added.
- **Cost.** 0.09 ms per 5.8 ms buffer with the Gaussian and legacy reverb, 0.14
  at worst with sinc and the hardware filters. The frame pacing is untouched:
  `KF2_FPS=144 KF2_FPS_PROBE=1` with sinc and hardware reverb reads 144.0 fps
  drawn at 19.9-20.0 ticks/s.
