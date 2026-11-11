using ImGuiNET;

namespace Kf2.Settings;

/// <summary>
/// <see cref="CursorMemory"/>'s switches, under Gameplay: the mod's three settings, and
/// the switch the mod had in the Mods panel. See "Cursor memory" in
/// docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class CursorMemoryPage : IPatchPage
{
    public string Id => "cursormemory";

    public string Title => "";

    public int Order => 26;

    public IReadOnlyList<string> Patches => ["kf2.cursormemory.port"];

    public void Draw()
    {
        bool stoodDown = CursorMemory.StoodDown;
        ImGui.BeginDisabled(stoodDown);

        bool on = CursorMemory.Enabled;
        if (ImGui.Checkbox("Cursor memory", ref on))
        {
            CursorMemory.Enabled = on;
            PatchSettings.Set(CursorMemory.OnKey, on);
        }
        Tip(stoodDown
            ? "The Cursor Memory mod is enabled in the Mods panel and does this itself."
            : "Menu pages open where you left them, or on what is equipped.");

        ImGui.BeginDisabled(!on);
        Part("Remember the cursor on the Items page", CursorMemory.ItemsKey,
             () => CursorMemory.Items, v => CursorMemory.Items = v,
             "The Items page opens on the item you last had highlighted.");
        Part("Remember the cursor on the Magic page", CursorMemory.MagicKey,
             () => CursorMemory.Magic, v => CursorMemory.Magic = v,
             "The Magic page opens on the spell you last had highlighted.");
        Part("Equipment sub menus open on what is equipped", CursorMemory.EquippedKey,
             () => CursorMemory.Equipped, v => CursorMemory.Equipped = v,
             "Weapon, armour, accessories, attack magic and the second slot open on what is equipped.");
        ImGui.EndDisabled();

        ImGui.EndDisabled();
    }

    static void Part(string label, string key, Func<bool> get, Action<bool> set, string tip)
    {
        bool value = get();
        if (ImGui.Checkbox(label, ref value))
        {
            set(value);
            PatchSettings.Set(key, value);
        }
        Tip(tip);
    }

    static void Tip(string text)
    {
        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled)) ImGui.SetTooltip(text);
    }
}
