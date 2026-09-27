using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host.Window;

namespace Verdite2.Launcher;

/// <summary>
/// The startup notice: once per launch, when <see cref="UpdateCheck"/> finds a newer
/// release, and only while no other popup is open, so it never lands on the disc
/// picker or the build. <see cref="UpdateBadge"/> stays in the menu bar after it.
/// </summary>
sealed class UpdatePopup : Popup
{
    protected override string TitleKey => "verdite2.update.title";
    protected override Vector2 Size => new(420f, 0f);

    UpdateCheck.Release? _shown;

    protected override void Update()
    {
        if (_shown is not null || IsOpen || PopupManager.AnyOpen) return;
        if (UpdateCheck.Available is not { } release) return;
        _shown = release;
        Open();
    }

    protected override void DrawContent()
    {
        if (_shown is not { } release) return;

        ImGui.TextWrapped(string.Format(Localization.T("verdite2.update.available"), release.Tag.TrimStart('v')));
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(string.Format(Localization.T("verdite2.update.running"), Ver.Number));
        ImGui.PopStyleColor();
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();

        var spacing = ImGui.GetStyle().ItemSpacing.X;
        var width = (ImGui.GetContentRegionAvail().X - spacing * 2f) / 3f;

        UpdateBadge.PushGold();
        var open = ImGui.Button(Localization.T("verdite2.update.download"), new Vector2(width, 0f));
        UpdateBadge.PopGold();
        if (ImGui.IsItemHovered()) ImGui.SetTooltip(release.Url);
        if (open)
        {
            UpdateBadge.OpenUrl(release.Url);
            Close();
        }

        ImGui.SameLine();
        if (ImGui.Button(Localization.T("verdite2.update.skip"), new Vector2(width, 0f)))
        {
            UpdateBadge.Skip(release);
            Close();
        }

        ImGui.SameLine();
        if (ImGui.Button(Localization.T("verdite2.update.later"), new Vector2(width, 0f))) Close();
    }
}
