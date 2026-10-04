using System.Globalization;
using System.Text.Json.Nodes;
using RecompOne.Runtime;
using RecompOne.Runtime.Memory;

namespace Kf2.Remaster;

/// <summary>The <c>level</c> verb: the area's tile edits, through the same document
/// calls the editor makes. A target T is <c>here</c>, <c>selected</c> or
/// <c>tile:A:X:Z:lower|upper</c>.</summary>
public static partial class Shell
{
    const string LevelHelp =
        "level [status] | on|off | show [T] | set T FIELD VALUE|game | reset T | rewrites [reset] - the area's tile edits, which change gameplay " +
        "(T here|selected|tile:A:X:Z:lower|upper; FIELD mesh|height|collision|shape|light|stopsFlood); show gives each field as the game has it, " +
        "as loaded and as edited; rewrites is the census of halves the game rewrites itself";

    static string LevelVerb(string[] a)
    {
        var m = Runtime.Mem;
        if (m == null) return Err("level", "not running");
        string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "status";
        switch (sub)
        {
            case "status":
                return Ok("level", LevelStatus());
            case "on" or "off":
                Level.SetEnabled(sub == "on");
                return Ok("level", LevelStatus());
            case "show":
            {
                if (!LevelTarget(m, a.Length > 1 ? a[1] : "here", out var k, out var err)) return Err("level", err!);
                return Ok("level", DescribeHalf(m, k));
            }
            case "set":
            {
                if (a.Length < 4) return Err("level", "level set T FIELD VALUE|game");
                if (!LevelTarget(m, a[1], out var k, out var err)) return Err("level", err!);
                if (Level.CannotEdit(k) is { } why) return Err("level", why);
                if (TileField.Find(a[2]) is not { } field)
                    return Err("level", $"no field '{a[2]}'; one of {string.Join(", ", TileField.All)}");
                int? value = null;
                if (a[3] != "game")
                {
                    if (ParseFieldValue(field, a[3]) is not { } v) return Err("level", $"'{a[3]}' is not a value of {field.Name}");
                    if (Level.Unusable(field, v) is { } bad) return Err("level", bad);
                    value = v;
                }
                Pack.SetLevelField(k, field, value, Identity.FingerprintText);
                return Ok("level", DescribeHalf(m, k));
            }
            case "reset":
            {
                if (!LevelTarget(m, a.Length > 1 ? a[1] : "", out var k, out var err)) return Err("level", err!);
                if (Level.CannotEdit(k) is { } why) return Err("level", why);
                Pack.ResetLevelHalf(k);
                return Ok("level", DescribeHalf(m, k));
            }
            case "rewrites":
            {
                if (a.Length > 1 && a[1] == "reset") TileRewrites.Reset();
                var list = new JsonArray();
                foreach (var (key, bytes) in TileRewrites.List())
                    list.Add(new JsonObject { ["half"] = key.ToString(), ["fields"] = TileRewrites.DescribeBytes(bytes) });
                return Ok("level", new JsonObject
                {
                    ["area"] = Identity.Area, ["rewritten"] = list, ["file"] = TileRewrites.FilePath,
                    ["polls"] = TileRewrites.Polls, ["error"] = TileRewrites.LastError,
                });
            }
            default:
                return Err("level", LevelHelp);
        }
    }

    static JsonObject LevelStatus() => new()
    {
        ["on"] = Level.Enabled,
        ["remaster"] = Host.Enabled,
        ["label"] = Level.Label,
        ["area"] = Identity.Area,
        ["settled"] = Identity.Settled,
        ["fingerprint"] = Identity.Settled ? Identity.FingerprintText : null,
        ["baselineKnown"] = Identity.Baseline != null,
        ["blockLoads"] = Identity.BlockLoads,
        ["authored"] = Level.Authored,
        ["applied"] = Level.Applied,
        ["refused"] = Level.Refused,
        ["keptGames"] = Level.KeptGames,
        ["rewrittenHalves"] = TileRewrites.Count,
        ["halves"] = new JsonArray(Pack.LevelEdits(Identity.Area).Select(e => (JsonNode)new JsonObject
        {
            ["half"] = e.Key.ToString(), ["status"] = Level.Status(e.Key),
        }).ToArray()),
    };

    static bool LevelTarget(IMemory m, string s, out TileKey key, out string? err)
    {
        err = null;
        key = default;
        TileKey? k = s switch
        {
            "here" => Identity.PlayerTile(m),
            "selected" => Editor.Selected,
            _ => TileKey.TryParse(s, out var parsed) ? parsed : null,
        };
        if (k == null) err = s is "here" or "selected" ? $"no half is {s}" : "a target is here, selected or tile:A:X:Z:lower|upper";
        else key = k.Value;
        return k != null;
    }

    static int? ParseFieldValue(TileField f, string s)
    {
        if (f.IsFlag)
            return s.ToLowerInvariant() switch { "on" or "true" or "1" => 1, "off" or "false" or "0" => 0, _ => null };
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return int.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int h) ? h : null;
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v) ? v : null;
    }

    /// <summary>A half's fields three ways -- the game's now, as the disc loaded them, as
    /// edited -- with its status, the census, and its neighbours' floors on the same level.</summary>
    static JsonObject DescribeHalf(IMemory m, TileKey k)
    {
        var o = new JsonObject { ["half"] = k.ToString(), ["label"] = Level.Label };
        var edit = Pack.LevelEdit(k);
        bool here = Identity.Settled && k.Area == Identity.Area;
        var fields = new JsonObject();
        foreach (var f in TileField.All)
        {
            var (live, loaded, authored) = Level.State(m, k, f);
            var e = new JsonObject();
            if (here) e["game"] = live;
            if (loaded != null) e["loaded"] = loaded;
            if (authored != null) e["edit"] = authored;
            fields[f.Name] = e;
        }
        o["fields"] = fields;
        o["status"] = Level.Status(k);
        if (edit is { Problems.Count: > 0 } pe) o["problems"] = string.Join("; ", pe.Problems);
        if (Level.CannotEdit(k) is { } why) o["cannotEdit"] = why;
        if (!here) return o;
        o["floorY"] = -(Level.State(m, k, TileField.Height).Live << 7);
        o["rewrittenByGame"] = TileRewrites.Rewritten(k) is int bits and > 0 ? TileRewrites.DescribeBytes(bits) : null;
        var neighbours = new JsonObject();
        foreach (var (side, height) in Level.NeighbourHeights(m, k)) neighbours[side] = height;
        o["neighbourHeights"] = neighbours;
        return o;
    }
}
