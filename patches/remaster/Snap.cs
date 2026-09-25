using System.Security.Cryptography;
using System.Text.Json.Nodes;
using RecompOne.Runtime;
using RecompOne.Runtime.Assets;

namespace Kf2.Remaster;

/// <summary>
/// <c>snap</c>: the presented picture read back (0069), hashed, compared with the
/// last snap and optionally written as a PNG. With the world paused and the view
/// pinned, two snaps are the same frame, so a hash is the "off is bit-identical"
/// test and the changed-pixel count is how much a feature moved.
///
///     snap [hash | PATH.png] [after N] [buffer Y|any]
///
/// The two display buffers of a paused frame can differ by a few pixels of
/// rounding (measured: 4 pixels, by 3 levels, at 0,0 against 0,240), so a snap
/// takes the buffer the last one did, or the one at VRAM row 0 first.
///
/// The reply waits for the present, so it is sent from the thread that presents.
/// </summary>
public static class Snap
{
    /// <summary>Presents skipped by default, so an edit made by the command before
    /// has reached a drawn frame and that frame the screen.</summary>
    const int DefaultAfter = 2;

    static byte[]? _last;
    static int _lastW, _lastH;
    static string? _lastHash;

    public static void Run(string args, Action<string> reply)
    {
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? path = null;
        int after = DefaultAfter, buffer = _lastBuffer >= 0 ? _lastBuffer : 0;
        for (int i = 0; i < a.Length; i++)
        {
            if (a[i] == "after" && i + 1 < a.Length && int.TryParse(a[i + 1], out int n)) { after = n; i++; }
            else if (a[i] == "buffer" && i + 1 < a.Length) { buffer = int.TryParse(a[i + 1], out int y) ? y : -1; i++; }
            else if (a[i] != "hash") path = a[i];
        }
        PresentSnap.Request((rgba, w, h, dx, dy) => reply(Taken(rgba, w, h, dy, path)), after, buffer);
    }

    static int _lastBuffer = -1;

    static string Taken(byte[] rgba, int w, int h, int buffer, string? path)
    {
        var o = new JsonObject { ["ok"] = true, ["cmd"] = "snap", ["w"] = w, ["h"] = h, ["buffer"] = buffer };
        string hash = Convert.ToHexStringLower(SHA256.HashData(rgba))[..16];
        o["hash"] = hash;
        o["previous"] = _lastHash;

        if (_last != null && _lastW == w && _lastH == h)
        {
            long changed = 0;
            int x0 = w, y0 = h, x1 = -1, y1 = -1, maxDelta = 0;
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                int i = (y * w + x) * 4;
                int d = Math.Max(Math.Abs(rgba[i] - _last[i]),
                        Math.Max(Math.Abs(rgba[i + 1] - _last[i + 1]), Math.Abs(rgba[i + 2] - _last[i + 2])));
                if (d == 0) continue;
                changed++;
                maxDelta = Math.Max(maxDelta, d);
                x0 = Math.Min(x0, x); y0 = Math.Min(y0, y); x1 = Math.Max(x1, x); y1 = Math.Max(y1, y);
            }
            o["changed"] = changed;
            o["changedShare"] = Math.Round(100.0 * changed / ((long)w * h), 3);
            o["maxDelta"] = maxDelta;
            if (changed > 0) o["changedRect"] = new JsonArray(x0, y0, x1 - x0 + 1, y1 - y0 + 1);
        }
        else if (_last != null)
            o["changed"] = $"size {_lastW}x{_lastH} -> {w}x{h}";

        if (path != null)
        {
            var opaque = (byte[])rgba.Clone();
            for (int i = 3; i < opaque.Length; i += 4) opaque[i] = 255;
            try
            {
                string full = Path.GetFullPath(path);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                PngWriter.WriteRgba(full, opaque, w, h);
                o["path"] = full;
            }
            catch (Exception e) { o["ok"] = false; o["error"] = e.Message; }
        }

        _last = rgba; _lastW = w; _lastH = h; _lastHash = hash; _lastBuffer = buffer;
        return o.ToJsonString();
    }

    public static string Usage => "snap [hash | PATH.png] [after N] [buffer Y|any] - the presented picture: hash, change against the last snap, PNG";
}
