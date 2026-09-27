using System.Globalization;
using System.Numerics;
using System.Text.Json.Nodes;
using RecompOne.Runtime;

namespace Kf2.Remaster;

/// <summary>The <c>prop</c> verb: the area's props, through the same document calls
/// the editor makes. A name's underscores are spaces.</summary>
public static partial class Shell
{
    const string PropHelp =
        "prop [list] | objects | add NAME MODEL [here | pick GX GY | X Y Z] | remove NAME | " +
        "set NAME model ID|position X Y Z|rotation X Y Z|scale S [S S]|half lower|upper|enabled on|off - " +
        "the area's props: object models the area has, placed where the author puts them; objects lists the live ones a prop can copy";

    static string PropVerb(string[] a)
    {
        var m = Runtime.Mem;
        if (m == null) return Err("prop", "not running");
        int area = Identity.Area;
        if (area < 0 || !Identity.Settled) return Err("prop", "no settled area");
        string op = a.Length > 0 ? a[0] : "list";
        float F(int i) => float.Parse(a[i], CultureInfo.InvariantCulture);
        switch (op)
        {
            case "list":
                return Ok("prop", PropList(area));
            case "objects":
            {
                var list = new JsonArray();
                foreach (var o in Props.LiveObjects(m))
                    list.Add(new JsonObject
                    {
                        ["slot"] = o.Slot, ["model"] = o.Model, ["type"] = o.Kind, ["kind"] = o.DefKind,
                        ["half"] = o.Half, ["clip"] = o.Clip, ["assembler"] = o.Assembler, ["flags"] = o.Flags,
                        ["position"] = new JsonArray(o.Position.X, o.Position.Y, o.Position.Z),
                        ["rotation"] = new JsonArray(o.Rotation.X, o.Rotation.Y, o.Rotation.Z),
                        ["scale"] = new JsonArray(o.Scale.X, o.Scale.Y, o.Scale.Z),
                    });
                return Ok("prop", new JsonObject { ["objects"] = list });
            }
            case "add":
            {
                if (a.Length < 3 || !int.TryParse(a[2], out int model)) return Err("prop", "prop add NAME MODEL [here|pick GX GY|X Y Z]");
                if (Props.Unusable(m, model) is { } bad) return Err("prop", bad);
                string name = a[1].Replace('_', ' ');
                Vector3 pos;
                if (a.Length >= 6 && a[3] != "pick") pos = new Vector3(F(3), F(4), F(5));
                else if (a.Length >= 6 && a[3] == "pick")
                {
                    if (!Faces.Recording) return Err("prop", "the editor is closed (edit on), so no triangles are recorded");
                    if (Editor.PropPlaceAt(m, new Vector2(F(4), F(5))) is not { } p) return Err("prop", "no surface under that pixel");
                    pos = p;
                }
                else pos = Editor.PlayerFeet(m);
                bool upper = Identity.PlayerTile(m) is { Half: TileKey.Upper };
                if (!Pack.AddProp(area, Identity.FingerprintText, name, model, pos, 0f, upper)) return Err("prop", $"'{name}' exists");
                Editor.SelectProp(name);
                return Ok("prop", PropList(area));
            }
            case "remove":
                if (a.Length < 2) return Err("prop", "prop remove NAME");
                Pack.RemoveProp(area, a[1].Replace('_', ' '));
                return Ok("prop", PropList(area));
            case "set":
            {
                if (a.Length < 4) return Err("prop", "prop set NAME FIELD V...");
                string name = a[1].Replace('_', ' ');
                if (Pack.GetProp(area, name) == null) return Err("prop", $"no prop '{name}'");
                string field = a[2];
                if (field == "model" && (!int.TryParse(a[3], out int id) || Props.Unusable(m, id) is { }))
                    return Err("prop", int.TryParse(a[3], out id) ? Props.Unusable(m, id)! : $"'{a[3]}' is not a model id");
                System.Action<JsonObject>? change = field switch
                {
                    "model" => o => o["model"] = int.Parse(a[3]),
                    "position" when a.Length >= 6 => o => Pack.SetPosition(o, new Vector3(F(3), F(4), F(5))),
                    "rotation" when a.Length >= 6 => o => Pack.SetRotation(o, new Vector3(F(3), F(4), F(5))),
                    "scale" when a.Length >= 6 => o => Pack.SetScale(o, new Vector3(F(3), F(4), F(5))),
                    "scale" => o => Pack.SetScale(o, new Vector3(F(3))),
                    "half" when a[3] is "lower" or "upper" => o => { if (a[3] == "upper") o["half"] = "upper"; else o.Remove("half"); },
                    "enabled" => o => { if (a[3] is "on" or "1") o.Remove("enabled"); else o["enabled"] = false; },
                    _ => null,
                };
                if (change == null) return Err("prop", $"cannot set '{field}' from that");
                Pack.SetProp(area, name, $"{field} = {string.Join(' ', a[3..])}", change);
                return Ok("prop", PropList(area));
            }
        }
        return Err("prop", PropHelp);
    }

    static JsonObject PropList(int area)
    {
        var list = new JsonArray();
        var m = Runtime.Mem;
        var view = m != null ? Lights.ReadView(m) : default;
        foreach (var p in Pack.Props(area))
        {
            var o = new JsonObject
            {
                ["name"] = p.Name, ["model"] = p.Model,
                ["position"] = new JsonArray(p.Position.X, p.Position.Y, p.Position.Z),
                ["rotation"] = new JsonArray(p.Rotation.X, p.Rotation.Y, p.Rotation.Z),
                ["scale"] = new JsonArray(p.Scale.X, p.Scale.Y, p.Scale.Z),
                ["half"] = p.Upper ? "upper" : "lower",
                ["status"] = Props.Status(p.Name),
            };
            if (p.Off) o["enabled"] = false;
            if (m != null && view.Project(p.Position, out var s, out float z))
                o["screen"] = new JsonArray(MathF.Round(s.X, 1), MathF.Round(s.Y, 1), MathF.Round(z));
            list.Add(o);
        }
        return new JsonObject
        {
            ["props"] = list, ["resolved"] = Props.Resolved, ["drawn"] = Props.Submitted,
            ["refused"] = Props.Refused,
        };
    }
}
