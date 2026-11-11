using ImGuiNET;
using Kf2.Text;

namespace Kf2.Settings;

/// <summary>
/// <see cref="Lettering"/>'s switches, under Video ▸ Text: the Text mod's six,
/// carried over with their saved keys, and its two buttons for the character maps.
/// A change the hooks read is queued to the game thread (<see cref="Lettering.Post"/>);
/// the switch itself flips at once. See "The game's text in new fonts" in
/// docs/PATCHES_AND_MODS.md.
/// </summary>
public sealed class TextPage : IPatchPage
{
    public string Id => "text";

    public string Title => "Text";

    public int Order => 35;

    public IReadOnlyList<string> Patches => ["kf2.text"];

    static string _note = "";

    public void Draw()
    {
        if (!Lettering.Enabled || Lettering.StoodDown)
        {
            ImGui.TextDisabled(Lettering.StoodDown
                ? "The Text mod is enabled in the Mods panel, so it draws the text and these do nothing."
                : "Off for this run (KF2_TEXT=0): the game's own lettering.");
            return;
        }

        int font = Lettering.Modern ? 1 : 0;
        if (ImGui.Combo("Font", ref font, "Classic\0Modern\0"))
        {
            Lettering.Modern = font == 1;
            PatchSettings.Set(Lettering.FontKey, font);
        }
        Tip("Modern: the port's lettering below. Classic: the game's own letters, and the switches below do nothing. Also PORT SETTINGS > GAMEPLAY > FONT.");
        if (!Lettering.Modern) ImGui.BeginDisabled();

        bool pictures = GameText.ShowPictures;
        if (ImGui.Checkbox("Signs and dialogue in the new fonts", ref pictures))
        {
            GameText.SetPictures(pictures);
            PatchSettings.Set(Lettering.PicturesKey, pictures);
        }
        Tip("Signs, dialogue, descriptions and place cards redrawn in the new fonts. Off shows the disc's own, unless a mod words them.");

        bool cased = GameText.NormalCase;
        if (ImGui.Checkbox("Normal capitalisation", ref cased))
        {
            GameText.NormalCase = cased;
            Lettering.Post(HudMessages.Invalidate);
            PatchSettings.Set(Lettering.CaseKey, cased);
        }
        Tip("Menus, names and messages as Use Item, Short Sword, Level up. Off keeps the game's capitals.");

        bool propPictures = Fonts.PropPictures;
        if (ImGui.Checkbox("Proportional spacing in signs and dialogue", ref propPictures))
        {
            Fonts.PropPictures = propPictures;
            PatchSettings.Set(Lettering.PropPicturesKey, propPictures);
        }
        Tip("Letters placed by their shape and lines wrapped by their width. Applies to the next sign shown.");

        bool propMenus = Fonts.PropMenus;
        if (ImGui.Checkbox("Proportional spacing in menus", ref propMenus))
        {
            Fonts.PropMenus = propMenus;
            Lettering.Post(HudMessages.Invalidate);
            PatchSettings.Set(Lettering.PropMenusKey, propMenus);
        }
        Tip("The same for menu labels. Numbers stay in their columns.");

        bool shadowPictures = Shadow.Pictures;
        if (ImGui.Checkbox("Shadow under signs and dialogue", ref shadowPictures))
        {
            Shadow.Pictures = shadowPictures;
            PatchSettings.Set(Lettering.ShadowPicturesKey, shadowPictures);
        }
        Tip("A dark shadow a pixel down and right of the letters, fading with them.");

        bool shadowMenus = Shadow.Menus;
        if (ImGui.Checkbox("Shadow under menu text", ref shadowMenus))
        {
            Shadow.Menus = shadowMenus;
            PatchSettings.Set(Lettering.ShadowMenusKey, shadowMenus);
        }
        Tip("The same for menu labels, numbers and the short messages in play.");
        if (!Lettering.Modern) ImGui.EndDisabled();

        ImGui.TextDisabled($"Fonts: {Fonts.Status}");
        ImGui.TextWrapped($"Character maps: {Fonts.Folder}");
        ImGui.TextDisabled("dialogue.png, sign.png and menu.png hold every letter; edit one and restart to use it.");
        if (ImGui.Button("Restore the port's fonts"))
        {
            Lettering.Post(Fonts.Restore);
            _note = "Restoring; applied in play.";
        }
        Tip("Writes the port's own character maps over the folder's, edits included.");
        ImGui.SameLine();
        if (ImGui.Button("Rebuild from the disc"))
        {
            Lettering.Post(Fonts.Rebuild);
            _note = "Rebuilding; applied in play.";
        }
        Tip("Replaces the character maps with the game's original letters, cut from your disc.");
        if (_note.Length > 0) ImGui.TextDisabled(_note);
    }

    static void Tip(string text)
    {
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(text);
    }
}
