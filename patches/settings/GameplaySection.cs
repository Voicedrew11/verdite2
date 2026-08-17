using RecompOne.Runtime.Host.Window;

namespace Kf2.Settings;

/// <summary>
/// A settings tab of the port's own: **Gameplay**.
///
/// The runtime's five sections are all about the machine — the interface, the
/// pad, the picture, the paths, the sound — and none of them is a place to put a
/// change to how the *game* behaves. Auto reload is not a video option and it is
/// not an input option; it is a rule about what happens when you die. Extending
/// `display` with it, the way the frame rate and the dither switch extend it,
/// would put it under a heading that says something else.
///
/// So this is the one case where the port adds a section rather than joining one.
/// <c>ISettingsSection</c> is public and <c>SettingsRegistry.Register</c> takes
/// any implementation, so it needs no patch to the RecompOne checkout — and the
/// section is deliberately **empty of its own content**: everything in it is a
/// patch page registered through <c>PatchSettings.Register("gameplay", …)</c> and
/// drawn by the same extension mechanism as the pages in Video. A patch that
/// changes the rules of play picks this id; nothing else about it differs.
///
/// <see cref="Order"/> 7 puts the tab between Video (5) and Audio (10), so the
/// runtime's own sections keep their order and the port's tab sits among them
/// rather than being appended after Paths.
/// </summary>
public sealed class GameplaySection : ISettingsSection
{
    public string Id => "gameplay";

    /// <summary>Merged by <see cref="PatchSettings"/> for all three of the
    /// runtime's languages — a key with only English behind it makes the other
    /// two warn on every miss and then show the key itself.</summary>
    public string TitleKey => "settings.gameplay";

    public int Order => 7;

    /// <summary>Nothing of its own. <c>SettingsPopup</c> draws the section's own
    /// content and then its extensions, so the pages are the whole pane.</summary>
    public void Draw() { }
}
