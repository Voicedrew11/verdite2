using System.Globalization;
using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using RecompOne.Runtime;

namespace Kf2.Remaster;

/// <summary>
/// The remaster's <c>KF2_SHELL</c> verbs, run on the game thread from
/// <see cref="AgentServer"/>'s VSync queue. They go through the same document calls
/// the panel does, so the undo stack sees them:
///
///     edit [on|off|toggle]                          the editor, which pauses the world
///     select [here | tile:A:X:Z:lower|upper | pick GX GY]
///     set selected|tile:... material NAME|none
///     set material:NAME reflectivity|f0 VALUE
///     set remaster on|off
///     pack save|reload|undo|redo|list|add NAME
///     remaster                                      the status, as the probe line has it
/// </summary>
public static class Shell
{
    public static readonly string[] Verbs = ["edit", "select", "set", "pack", "remaster"];

    public static readonly string[] Help =
    [
        "edit [on|off|toggle] - the remaster editor, which pauses the world",
        "select [here | tile:A:X:Z:lower|upper | pick GX GY] - pick a tile half; GX GY in game pixels",
        "set selected|tile:... material NAME|none; set material:NAME reflectivity|f0 V; set remaster on|off",
        "pack save|reload|undo|redo|list|add NAME - the working pack",
        "remaster - area, fingerprint, what is applied",
    ];

    public static string Run(string verb, string args)
    {
        var a = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        try
        {
            return verb switch
            {
                "edit" => Edit(a),
                "select" => Select(a),
                "set" => Set(a),
                "pack" => PackVerb(a),
                "remaster" => Status(),
                _ => Err(verb, "unknown verb"),
            };
        }
        catch (Exception e) { return Err(verb, e.Message); }
    }

    static string Edit(string[] a)
    {
        string mode = a.Length > 0 ? a[0].ToLowerInvariant() : "toggle";
        bool open = mode switch { "on" => true, "off" => false, "toggle" => !Editor.Open, _ => Editor.Open };
        if (mode is not ("on" or "off" or "toggle")) return Err("edit", "edit on|off|toggle");
        Editor.SetOpen(open);
        return Ok("edit", new JsonObject { ["open"] = open, ["pauses"] = Identity.Area >= 0 });
    }

    static string Select(string[] a)
    {
        var m = Runtime.Mem;
        if (m == null) return Err("select", "not running");
        if (a.Length == 0) return Ok("select", Describe(Editor.Selected));
        if (a[0] == "here")
        {
            Editor.Select(Identity.PlayerTile(m));
            return Ok("select", Describe(Editor.Selected));
        }
        if (a[0] == "pick")
        {
            if (a.Length < 3 || !float.TryParse(a[1], CultureInfo.InvariantCulture, out float gx)
                || !float.TryParse(a[2], CultureInfo.InvariantCulture, out float gy))
                return Err("select", "select pick GX GY");
            var view = Pick.Read(m);
            var hit = Pick.Floor(m, view, new Vector2(gx, gy), out var at);
            if (hit == null) return Err("select", "no floor under that pixel");
            Editor.Select(hit);
            var d = Describe(hit);
            d["hit"] = new JsonArray(MathF.Round(at.X), MathF.Round(at.Y), MathF.Round(at.Z));
            return Ok("select", d);
        }
        if (!TileKey.TryParse(a[0], out var k)) return Err("select", $"cannot read '{a[0]}'");
        Editor.Select(k);
        return Ok("select", Describe(k));
    }

    static string Set(string[] a)
    {
        if (a.Length >= 2 && a[0] == "remaster")
        {
            Host.SetEnabled(a[1] is "on" or "1");
            return Ok("set", new JsonObject { ["remaster"] = Host.Enabled });
        }
        if (a.Length >= 3 && a[0].StartsWith("material:"))
        {
            string name = a[0]["material:".Length..];
            if (!Pack.HasMaterial(name)) return Err("set", $"no material '{name}'");
            if (a[1] is not ("reflectivity" or "f0")) return Err("set", "reflectivity or f0");
            if (!float.TryParse(a[2], CultureInfo.InvariantCulture, out float v)) return Err("set", $"cannot read '{a[2]}'");
            Pack.SetField(name, a[1], Math.Clamp(v, 0f, 1f));
            return Ok("set", new JsonObject { ["material"] = name, [a[1]] = Pack.GetField(name, a[1]) });
        }
        if (a.Length >= 3 && a[1] == "material")
        {
            TileKey k;
            if (a[0] == "selected")
            {
                if (Editor.Selected is not { } s) return Err("set", "nothing selected");
                k = s;
            }
            else if (!TileKey.TryParse(a[0], out k)) return Err("set", $"cannot read '{a[0]}'");
            if (!Identity.Settled || k.Area != Identity.Area) return Err("set", "the tile's area is not the settled one");
            if (Surfaces.Refused != null) return Err("set", Surfaces.Refused);
            string? name = a[2] == "none" ? null : a[2];
            if (name != null && !Pack.HasMaterial(name)) return Err("set", $"no material '{name}'");
            Pack.SetTile(k, name, Identity.FingerprintText);
            return Ok("set", Describe(k));
        }
        return Err("set", "set selected|tile:... material NAME|none; set material:NAME reflectivity|f0 V; set remaster on|off");
    }

    static string PackVerb(string[] a)
    {
        string op = a.Length > 0 ? a[0] : "list";
        switch (op)
        {
            case "save": Pack.Save(); break;
            case "reload": Pack.Load(); break;
            case "undo": if (!Pack.Undo()) return Err("pack", "nothing to undo"); break;
            case "redo": if (!Pack.Redo()) return Err("pack", "nothing to redo"); break;
            case "add":
                if (a.Length < 2) return Err("pack", "pack add NAME");
                Pack.AddMaterial(a[1]);
                break;
            case "list": break;
            default: return Err("pack", "save|reload|undo|redo|list|add NAME");
        }
        var mats = new JsonArray();
        foreach (var mat in Pack.Materials())
            mats.Add(new JsonObject
            {
                ["name"] = mat.Name, ["reflectivity"] = mat.Reflectivity, ["f0"] = mat.F0,
                ["id"] = Surfaces.IdOf(mat.Name),
            });
        var tiles = new JsonArray();
        if (Identity.Area >= 0)
            foreach (var (k, name) in Pack.Tiles(Identity.Area)) tiles.Add($"{k} = {name}");
        return Ok("pack", new JsonObject
        {
            ["root"] = Pack.Root, ["dirty"] = Pack.Dirty, ["error"] = Pack.LastError,
            ["materials"] = mats, ["tiles"] = tiles,
        });
    }

    static string Status()
    {
        var byMat = new JsonArray();
        foreach (long n in SurfaceMaterial.ByMaterial) byMat.Add(n);
        return Ok("remaster", new JsonObject
        {
            ["on"] = Host.Enabled,
            ["area"] = Identity.Area,
            ["settled"] = Identity.Settled,
            ["fingerprint"] = Identity.Settled ? Identity.FingerprintText : null,
            ["gap"] = Identity.LastGap,
            ["refused"] = Surfaces.Refused,
            ["tilesApplied"] = Surfaces.TilesApplied,
            ["packets"] = Surfaces.Packets,
            ["reflections"] = Reflections.Enabled,
            ["byMaterial"] = byMat,
            ["fromPacket"] = SurfaceMaterial.FromPacket,
            ["selected"] = Editor.Selected?.ToString(),
            ["editor"] = Editor.Open,
        });
    }

    static JsonObject Describe(TileKey? key)
    {
        if (key is not { } k) return new JsonObject { ["selected"] = null };
        var o = new JsonObject { ["selected"] = k.ToString(), ["material"] = Pack.TileMaterial(k) };
        var m = Runtime.Mem;
        if (m != null && k.Area == Identity.Area)
        {
            uint rec = Identity.HalfRecord(k.X, k.Z, k.Half);
            o["model"] = m.ReadU8(rec);
            o["height"] = m.ReadU8(rec + 1);
            o["drawn"] = m.ReadU8(rec) < 240;
        }
        return o;
    }

    static string Ok(string cmd, JsonObject body)
    {
        var o = new JsonObject { ["ok"] = true, ["cmd"] = cmd };
        foreach (var (k, v) in body) o[k] = v?.DeepClone();
        return o.ToJsonString();
    }

    static string Err(string cmd, string message)
        => new JsonObject { ["ok"] = false, ["cmd"] = cmd, ["error"] = message }.ToJsonString();
}
