using ImGuiNET;
using Kf2.Remaster;
using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// The remaster's switch, under Video ▸ Enhancements after the reflections, and the
/// packs it applies under a heading of their own: the packs beneath the working pack,
/// each with its switch, and the working pack on top, with every reason an area of it
/// is not applied. See "Settings" in docs/REMASTER.md.
/// </summary>
public sealed class RemasterPage : IPatchPage
{
    public string Id => "remaster";
    public string Title => "Enhancements";
    public int Order => 29;

    const string Strings = """
    {
      "strings": {
        "kf2.remaster.label": {
          "en": "Remaster packs",
          "pt-BR": "Pacotes de remasterização",
          "es-419": "Paquetes de remasterización"
        },
        "kf2.remaster.tooltip": {
          "en": "Apply authored materials from the working pack. Off, nothing a pack holds reaches the picture. Shift+E opens the editor. Experimental.",
          "pt-BR": "Aplica os materiais criados no pacote de trabalho. Desligado, nada do pacote chega à imagem. Shift+E abre o editor. Experimental.",
          "es-419": "Aplica los materiales creados en el paquete de trabajo. Apagado, nada del paquete llega a la imagen. Shift+E abre el editor. Experimental."
        }
      }
    }
    """;

    public RemasterPage() => Localization.Merge(Strings);

    public void Draw()
    {
        bool on = Host.Enabled;
        if (ImGui.Checkbox(Localization.T("kf2.remaster.label"), ref on)) Host.SetEnabled(on);
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T("kf2.remaster.tooltip"));
    }
}

/// <summary>The packs found, what each holds, and why an area is not applied.</summary>
public sealed class RemasterPacksPage : IPatchPage
{
    public string Id => "remaster.packs";
    public string Title => "Remaster packs";
    public int Order => 30;

    static readonly System.Numerics.Vector4 Warn = new(1f, 0.75f, 0.3f, 1f), Bad = new(1f, 0.4f, 0.4f, 1f);

    public void Draw()
    {
        foreach (var l in Pack.Layers)
        {
            bool on = l.Enabled;
            ImGui.BeginDisabled(l.Error != null);
            if (ImGui.Checkbox($"{l.Name}##layer:{l.Id}", ref on)) Pack.SetLayerEnabled(l.Id, on);
            ImGui.EndDisabled();
            ImGui.SameLine();
            ImGui.TextDisabled($"priority {l.Priority}, {l.Documents} document(s)");
            if (ImGui.IsItemHovered()) ImGui.SetTooltip(l.Path);
            if (l.Error != null) ImGui.TextColored(Bad, l.Error);
            foreach (var why in l.SetAside) ImGui.TextColored(Warn, "set aside: " + why);
        }
        if (Pack.Layers.Count > 0)
            ImGui.TextDisabled("Each pack overrides those above it in this list per key; the working pack is on top of them all.");
        foreach (var why in Pack.WorkSetAside) ImGui.TextColored(Warn, "set aside: " + why);

        ImGui.Text("Working pack");
        ImGui.SameLine();
        ImGui.TextDisabled(Pack.Root);
        if (!Host.Enabled) ImGui.TextDisabled("Not applied: the remaster is off.");
        if (Pack.LastError != null) ImGui.TextColored(Bad, Pack.LastError);

        int mats = Pack.Materials().Count();
        ImGui.Text($"{mats} material(s)");
        if (Surfaces.Unallocated > 0)
            ImGui.TextColored(Warn, $"{Surfaces.Unallocated} past the id table apply nowhere.");

        bool level = Level.Enabled;
        if (ImGui.Checkbox($"Level edits ({Level.Label})", ref level)) Level.SetEnabled(level);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Apply a pack's tile edits: floors, collision and what is drawn. Off by default, because they change " +
                             "how an area plays. They never reach a save; a save made on an edited floor keeps the position, not the floor.");

        foreach (int area in Pack.Areas())
        {
            int tiles = Pack.Tiles(area).Count();
            int edits = Pack.LevelEdits(area).Count();
            string? fp = Pack.AreaFingerprint(area);
            ImGui.BulletText($"Area {area}: {tiles} tile half/halves, fingerprint {fp ?? "none"}");
            if (edits > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(Warn, $"{edits} level edit(s), {Level.Label}");
            }
            if (area != Identity.Area || !Identity.Settled) continue;
            ImGui.SameLine();
            if (fp != null && fp != Identity.FingerprintText)
                ImGui.TextColored(Bad, $"refused: this area reads {Identity.FingerprintText}");
            else
                ImGui.TextDisabled(Host.Enabled ? $"applied, {Surfaces.TilesApplied} in use" : "matches");
        }
        if (!Pack.Areas().Any()) ImGui.TextDisabled("No area has anything authored yet.");

        if (ImGui.Button("Reload")) Pack.Load();
        ImGui.SameLine();
        if (ImGui.Button("Open the editor")) Editor.SetOpen(true);
    }
}
