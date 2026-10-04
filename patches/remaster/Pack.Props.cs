using System.Numerics;
using System.Text.Json.Nodes;

namespace Kf2.Remaster;

/// <summary>
/// The props document, <c>areas/&lt;n&gt;/props.json</c>: under <c>props</c>, each prop
/// named, with the object model it draws (the id a pick names, <c>model:A:object:ID</c>),
/// its position in world units with up at -Y, its rotation in degrees about X, Y and Z,
/// its scale against the model's own, and the tile half it is lit and culled on. See
/// "Phase 8, the first slice" in docs/REMASTER.md.
///
///     { "name": "urn 1", "model": 300, "position": [36000, -1280, 74000], "rotation": [0, 90, 0] }
/// </summary>
public static partial class Pack
{
    public readonly record struct Prop(string Name, int Model, Vector3 Position, Vector3 Rotation, Vector3 Scale,
                                       bool Upper, bool Off);

    const string PropsKey = "props";

    static JsonObject? PropsDoc(int area) => _set.Props.TryGetValue(area, out var d) ? d : null;

    public static string? PropsFingerprint(int area) => PropsDoc(area) is { } d ? Str(d["fingerprint"]) : null;

    static JsonObject? FindProp(int area, string name)
    {
        if (PropsDoc(area)?[PropsKey] is not JsonArray list) return null;
        foreach (var n in list)
            if (n is JsonObject o && Str(o["name"]) == name) return o;
        return null;
    }

    static Prop ParseProp(JsonObject o, string name)
    {
        var scale = o["scale"] is JsonValue sv && sv.TryGetValue(out double s) ? new Vector3((float)s) : Vec(o["scale"], Vector3.One);
        return new Prop(name, Int(o["model"]) ?? -1, Vec(o["position"], Vector3.Zero), Vec(o["rotation"], Vector3.Zero), scale,
                        Str(o["half"]) == "upper", o["enabled"] is JsonValue ev && ev.TryGetValue(out bool en) && !en);
    }

    public static IEnumerable<Prop> Props(int area)
    {
        if (PropsDoc(area)?[PropsKey] is not JsonArray list) yield break;
        foreach (var n in list)
            if (n is JsonObject o && Str(o["name"]) is { } name)
                yield return ParseProp(o, name);
    }

    public static Prop? GetProp(int area, string name) => FindProp(area, name) is { } o ? ParseProp(o, name) : null;

    public static string FreePropName(int area, string stem = "prop")
    {
        for (int i = 1; ; i++)
            if (FindProp(area, $"{stem} {i}") == null) return $"{stem} {i}";
    }

    public static bool AddProp(int area, string fingerprint, string name, int model, Vector3 position, float yaw, bool upper)
    {
        if (string.IsNullOrWhiteSpace(name) || FindProp(area, name) != null) return false;
        EditDoc(s => s.Props, PropsKey, area, fingerprint, $"add prop {name}", doc =>
        {
            var o = new JsonObject { ["name"] = name, ["model"] = model };
            SetPosition(o, position);
            o["rotation"] = Arr(new Vector3(0f, MathF.Round(yaw), 0f));
            if (upper) o["half"] = "upper";
            ((JsonArray)doc[PropsKey]!).Add(o);
        });
        return true;
    }

    public static void RemoveProp(int area, string name)
    {
        if (FindProp(area, name) == null) return;
        EditDoc(s => s.Props, PropsKey, area, PropsFingerprint(area) ?? "", $"remove prop {name}", doc =>
        {
            var list = (JsonArray)doc[PropsKey]!;
            foreach (var n in list)
                if (n is JsonObject o && Str(o["name"]) == name) { list.Remove(o); break; }
        });
    }

    public static JsonObject? PropSnapshot(int area, string name) => FindProp(area, name)?.DeepClone() as JsonObject;

    /// <summary>A change to one prop as one undo entry; <paramref name="before"/> is what
    /// undo puts back, so a slider held over many frames is one entry.</summary>
    public static void SetProp(int area, string name, string label, Action<JsonObject> change, JsonObject? before = null)
    {
        var cur = FindProp(area, name);
        if (cur == null) return;
        var from = before ?? (JsonObject)cur.DeepClone();
        if (before != null) PutProp(area, name, before);
        Edit($"{name}: {label}", () => { if (FindProp(area, name) is { } o) change(o); }, () => PutProp(area, name, from));
    }

    public static void CommitProp(int area, string name, string label, JsonObject before)
    {
        if (FindProp(area, name) is not { } cur) return;
        var after = (JsonObject)cur.DeepClone();
        Edit($"{name}: {label}", () => PutProp(area, name, after), () => PutProp(area, name, before));
    }

    public static void PreviewProp(int area, string name, Action<JsonObject> change)
    {
        if (FindProp(area, name) is not { } o) return;
        change(o);
        Dirty = true;
        Version++;
    }

    static void PutProp(int area, string name, JsonObject snapshot)
    {
        if (PropsDoc(area)?[PropsKey] is not JsonArray list) return;
        for (int i = 0; i < list.Count; i++)
            if (list[i] is JsonObject o && Str(o["name"]) == name)
            {
                list[i] = snapshot.DeepClone();
                return;
            }
    }

    public static void SetRotation(JsonObject o, Vector3 degrees)
    {
        static float Wrap(float d) => MathF.Round(((d % 360f) + 360f) % 360f, 1);
        o["rotation"] = Arr(new Vector3(Wrap(degrees.X), Wrap(degrees.Y), Wrap(degrees.Z)));
    }

    /// <summary>One number when the three agree, which is how a scale is usually meant.</summary>
    public static void SetScale(JsonObject o, Vector3 s)
    {
        s = Vector3.Clamp(s, new Vector3(0.05f), new Vector3(8f));
        if (s == Vector3.One) o.Remove("scale");
        else if (s.X == s.Y && s.Y == s.Z) o["scale"] = Math.Round(s.X, 3);
        else o["scale"] = Arr(s);
    }
}
