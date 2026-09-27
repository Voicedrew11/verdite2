using System.Globalization;
using System.Text.Json.Nodes;
using RecompOne.Runtime;

namespace Kf2.Remaster;

/// <summary>The <c>camera</c> verb: the editor's free camera, through the same calls the
/// panel makes. The editor must be open.</summary>
public static partial class Shell
{
    const string CameraHelp =
        "camera [state] | on|off | player | move RIGHT UP FORWARD | turn RIGHT UP | at X Y Z [PITCH YAW] - the editor's free camera " +
        "(move in world units along the eye's axes, turn in angle units, 0x1000 a turn); state gives the eye, its axes and the models the frame submitted";

    static string CameraVerb(string[] a)
    {
        var m = Runtime.Mem;
        if (m == null) return Err("camera", "not running");
        string sub = a.Length > 0 ? a[0].ToLowerInvariant() : "state";
        if (sub != "state" && !Editor.Open) return Err("camera", "the editor is closed (edit on)");
        bool Floats(int from, int count, out float[] v)
        {
            v = new float[count];
            if (a.Length < from + count) return false;
            for (int i = 0; i < count; i++)
                if (!float.TryParse(a[from + i], CultureInfo.InvariantCulture, out v[i])) return false;
            return true;
        }
        switch (sub)
        {
            case "state": break;
            case "on" or "off":
                EditorCamera.SetOn(sub == "on", m);
                if (sub == "on" && !EditorCamera.On) return Err("camera", "no camera handed to stage 13 yet");
                break;
            case "player":
                if (!EditorCamera.On) return Err("camera", "the free camera is off");
                EditorCamera.ToPlayer(m);
                break;
            case "move":
                if (!EditorCamera.On) return Err("camera", "the free camera is off");
                if (!Floats(1, 3, out var d)) return Err("camera", "camera move RIGHT UP FORWARD");
                EditorCamera.Move(m, new System.Numerics.Vector3(d[0], d[1], d[2]));
                break;
            case "turn":
                if (!EditorCamera.On) return Err("camera", "the free camera is off");
                if (!Floats(1, 2, out var t)) return Err("camera", "camera turn RIGHT UP");
                EditorCamera.Turn(t[0], t[1]);
                break;
            case "at":
            {
                if (!EditorCamera.On) return Err("camera", "the free camera is off");
                if (!Floats(1, 3, out var p)) return Err("camera", "camera at X Y Z [PITCH YAW]");
                var cur = EditorCamera.Current;
                bool angles = Floats(4, 2, out var ang);
                EditorCamera.Place(new Camera((int)p[0], (int)p[1], (int)p[2],
                    angles ? (short)ang[0] : cur.Pitch, angles ? (short)ang[1] : cur.Yaw, cur.Roll));
                break;
            }
            default:
                return Err("camera", CameraHelp);
        }
        return Ok("camera", CameraState(m));
    }

    static JsonObject CameraState(RecompOne.Runtime.Memory.IMemory m)
    {
        static JsonArray Vec(System.Numerics.Vector3 v)
            => new(Math.Round(v.X, 3), Math.Round(v.Y, 3), Math.Round(v.Z, 3));
        static JsonArray Cam(Camera c) => new(c.X, c.Y, c.Z, c.Pitch, c.Yaw, c.Roll);
        var (right, down, fwd) = EditorCamera.Axes(m);
        var o = new JsonObject
        {
            ["on"] = EditorCamera.On,
            ["drawn"] = Cam(Camera.Read(m)),
            ["axes"] = new JsonObject { ["right"] = Vec(right), ["down"] = Vec(down), ["forward"] = Vec(fwd) },
            ["speed"] = EditorCamera.Speed,
            ["armHidden"] = Stage13.HideArmOnOverride && Stage13.ViewOverride != null,
        };
        if (Stage13.Handed is { } h) o["player"] = Cam(h);
        var kinds = new JsonObject();
        foreach (var d in ModelWalk.Scene)
        {
            string k = d.Kind.ToString().ToLowerInvariant();
            kinds[k] = (kinds[k]?.GetValue<int>() ?? 0) + 1;
        }
        o["models"] = kinds;
        return o;
    }
}
