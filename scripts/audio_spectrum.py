#!/usr/bin/env python3
"""Loudness and high-frequency share of the WAVs KF2_AUDIO_DUMP writes.

    python3 scripts/audio_spectrum.py dump/kf2-wet-*.wav [--skip 5] [--seconds 30]

Per file: RMS in dBFS, and the energy above 11.025 kHz and above 16 kHz as dB
relative to the whole signal. The 11 kHz figure is the one that tells the reverb's
resampling apart (the console's reverb runs at 22.05 kHz, so anything up there in
the wet return is aliasing or imaging); the 16 kHz one is interpolation brightness.
Stdlib only.
"""
import argparse
import array
import math
import sys
import wave


def highpass(fc, fs, q=math.sqrt(0.5)):
    w = 2 * math.pi * fc / fs
    alpha = math.sin(w) / (2 * q)
    cw = math.cos(w)
    a0 = 1 + alpha
    return ((1 + cw) / 2 / a0, -(1 + cw) / a0, (1 + cw) / 2 / a0, -2 * cw / a0, (1 - alpha) / a0)


def filtered_energy(x, coefs, stages=2):
    b0, b1, b2, a1, a2 = coefs
    for _ in range(stages):
        y = [0.0] * len(x)
        x1 = x2 = y1 = y2 = 0.0
        for i, s in enumerate(x):
            out = b0 * s + b1 * x1 + b2 * x2 - a1 * y1 - a2 * y2
            x2, x1, y2, y1 = x1, s, y1, out
            y[i] = out
        x = y
    return sum(v * v for v in x)


def db(ratio):
    return "-inf" if ratio <= 0 else f"{10 * math.log10(ratio):6.1f}"


def analyse(path, skip, seconds):
    with wave.open(path, "rb") as w:
        if w.getsampwidth() != 2 or w.getnchannels() != 2:
            sys.exit(f"{path}: expected 16-bit stereo")
        fs = w.getframerate()
        w.setpos(min(int(skip * fs), w.getnframes()))
        raw = array.array("h", w.readframes(int(seconds * fs) if seconds else w.getnframes()))
    if sys.byteorder != "little":
        raw.byteswap()
    mono = [(raw[i] + raw[i + 1]) / 65536.0 for i in range(0, len(raw) - 1, 2)]
    if not mono:
        return f"{path}: empty"
    total = sum(v * v for v in mono)
    rms = math.sqrt(total / len(mono)) if total else 0
    rms_db = "-inf" if rms == 0 else f"{20 * math.log10(rms):6.1f}"
    above11 = filtered_energy(mono, highpass(11025, fs)) / total if total else 0
    above16 = filtered_energy(mono, highpass(16000, fs)) / total if total else 0
    return (f"{path}: {len(mono) / fs:5.1f} s  rms {rms_db} dBFS  "
            f">11k {db(above11)} dB  >16k {db(above16)} dB")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("files", nargs="+")
    ap.add_argument("--skip", type=float, default=0, help="seconds to skip at the start")
    ap.add_argument("--seconds", type=float, default=0, help="seconds to analyse (0: all)")
    args = ap.parse_args()
    for f in args.files:
        print(analyse(f, args.skip, args.seconds))


if __name__ == "__main__":
    main()
