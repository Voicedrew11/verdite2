using System.Diagnostics;
using System.Numerics;
using System.Text;
using ImGuiNET;
using NativeFileDialogNET;
using RecompOne.Runtime;
using RecompOne.Runtime.Cdrom;
using RecompOne.Runtime.Config;
using RecompOne.Runtime.Host.Window;

namespace Verdite2.Launcher;

/// <summary>
/// The first-run language question, asked before the US disc is: English, the
/// Retranslation, or Japanese, and for Japanese, where the Japanese disc is. See "The
/// first-run language step" in docs/JAPANESE.md.
///
/// Asked once, and only when the launcher is about to ask for the disc, so a player
/// who already has a game is left alone; VERDITE2_SETUP=1 asks again.
///
/// The keys and values are Verdite Core's LanguageChoice's and the Retranslation's
/// (patches/retranslation/Retranslation.cs), which is game code and not compiled here.
/// None starts "kf2.", because onboarding.json's "existing" prefix would then take a
/// new player for one who has played.
/// </summary>
static class LanguageStep
{
    internal const string LanguageKey = "verdite.language";
    internal const string JapaneseDiscKey = "verdite.language.jpdisc";
    internal const string Japanese = "ja";
    internal const string Retranslation = "retranslation";
    const string AskedKey = "verdite.language.asked";

    /// <summary>How long to wait for the window to open the picker before asking anyway.</summary>
    static readonly TimeSpan PickerWait = TimeSpan.FromSeconds(10);

    /// <summary>Frames for a closed popup to leave the stack, so the next is not drawn inside it.</summary>
    const int FramesToClose = 2;

    /// <summary>On the launcher's main thread, after the window is made and before the disc is asked for.</summary>
    public static void Run()
    {
        bool discNeeded = !HasValidDisc();
        bool forced = Environment.GetEnvironmentVariable("VERDITE2_SETUP") == "1";
        if (!forced && (!discNeeded || ConfigManager.View.Has(AskedKey))) return;

        Localization.Merge(Strings);

        var picker = discNeeded ? HidePicker() : null;
        var popup = new LanguagePopup();
        PopupManager.Register(popup);
        popup.Open();
        while (popup.IsOpen) Runtime.Pump();

        if (picker is null) return;
        LetPopupsClose();
        picker.Open();
    }

    /// <summary>The window's own test for opening the picker, so the two agree.</summary>
    static bool HasValidDisc()
    {
        var path = Runtime.CdPath;
        return !string.IsNullOrWhiteSpace(path) && File.Exists(path) && Runtime.ValidateDisc(path) is null;
    }

    /// <summary>
    /// The window opens the disc picker as it loads, so it is closed here to be
    /// opened again after this step. Null if it never appeared.
    /// </summary>
    static DiscPickerPopup? HidePicker()
    {
        var clock = Stopwatch.StartNew();
        while (PopupManager.Get<DiscPickerPopup>() is not { IsOpen: true })
        {
            if (clock.Elapsed > PickerWait) return null;
            Runtime.Pump();
        }

        var picker = PopupManager.Get<DiscPickerPopup>()!;
        picker.Close();
        LetPopupsClose();
        return picker;
    }

    static void LetPopupsClose()
    {
        for (int i = 0; i < FramesToClose; i++) Runtime.Pump();
    }

    /// <summary>
    /// The three languages the runtime ships: a key missing one shows as the key.
    /// </summary>
    const string Strings = """
    {
      "strings": {
        "verdite2.language.title": { "en": "Language", "pt-BR": "Idioma", "es-419": "Idioma" },
        "verdite2.language.english": { "en": "English", "pt-BR": "Inglês", "es-419": "Inglés" },
        "verdite2.language.retranslation": { "en": "English, retranslated", "pt-BR": "Inglês, retraduzido", "es-419": "Inglés, retraducido" },
        "verdite2.language.both": { "en": "Japanese", "pt-BR": "Japonês", "es-419": "Japonés" },
        "verdite2.language.note": {
          "en": "English is the US release's text. Retranslated is a new English translation made from the Japanese release, for signs, dialogue, descriptions, menus and names. Japanese takes the text, menus and movies from your own Japanese disc, laid over the US disc. You can change the language any time in the settings.",
          "pt-BR": "Inglês é o texto da versão americana. Retraduzido é uma nova tradução em inglês feita a partir da versão japonesa, para placas, diálogos, descrições, menus e nomes. Japonês usa o texto, os menus e os vídeos do seu próprio disco japonês, aplicados sobre o disco americano. Você pode mudar o idioma a qualquer momento nas configurações.",
          "es-419": "Inglés es el texto de la versión estadounidense. Retraducido es una nueva traducción al inglés hecha a partir de la versión japonesa, para carteles, diálogos, descripciones, menús y nombres. Japonés toma el texto, los menús y los videos de tu propio disco japonés, aplicados sobre el disco estadounidense. Puedes cambiar el idioma en cualquier momento en la configuración."
        },
        "verdite2.language.next": { "en": "Next", "pt-BR": "Avançar", "es-419": "Siguiente" },
        "verdite2.language.back": { "en": "Back", "pt-BR": "Voltar", "es-419": "Atrás" },
        "verdite2.jpdisc.title": { "en": "Choose your Japanese disc", "pt-BR": "Escolha seu disco japonês", "es-419": "Elige tu disco japonés" },
        "verdite2.jpdisc.description": {
          "en": "Choose the disc image (.cue or .chd) of the Japanese King's Field II (SLPS-00069 or SLPS-91003). You will choose the US disc next.",
          "pt-BR": "Escolha a imagem do disco (.cue ou .chd) do King's Field II japonês (SLPS-00069 ou SLPS-91003). Em seguida, você escolherá o disco americano.",
          "es-419": "Elige la imagen del disco (.cue o .chd) del King's Field II japonés (SLPS-00069 o SLPS-91003). A continuación elegirás el disco estadounidense."
        },
        "verdite2.jpdisc.wrong": {
          "en": "This is not the Japanese King's Field II (SLPS-00069 or SLPS-91003).",
          "pt-BR": "Este não é o King's Field II japonês (SLPS-00069 ou SLPS-91003).",
          "es-419": "Este no es el King's Field II japonés (SLPS-00069 o SLPS-91003)."
        },
        "verdite2.jpdisc.unreadable": {
          "en": "This file could not be read as a cue/bin pair or a CHD.",
          "pt-BR": "Este arquivo não pôde ser lido como um par cue/bin ou um CHD.",
          "es-419": "Este archivo no se pudo leer como un par cue/bin o un CHD."
        }
      }
    }
    """;
}

/// <summary>The step's two pages: the question, then the Japanese disc. Draws on the launcher's main thread.</summary>
sealed class LanguagePopup : Popup
{
    static readonly string[] JapaneseVolumeIds = ["SLPS-00069", "SLPS-91003"];   // JapaneseDisc's test, so a disc accepted here is one the game takes
    const int PrimaryVolumeSector = 16;
    const int VolumeIdAt = 40;
    const int VolumeIdBytes = 32;
    const int SectorBytes = 2048;
    const float BrowseWidth = 90f;
    const float ButtonWidth = 120f;

    enum Choice { English, Retranslation, Japanese }

    Choice _choice;
    bool _onDiscPage;
    string _path = "";
    string _error = "";

    protected override string TitleKey => _onDiscPage ? "verdite2.jpdisc.title" : "verdite2.language.title";
    protected override Vector2 Size => new(540f, 0f);
    protected override bool Closable => false;

    protected override void DrawContent()
    {
        if (_onDiscPage) DrawDiscPage();
        else DrawQuestion();
    }

    void DrawQuestion()
    {
        if (ImGui.RadioButton(Localization.T("verdite2.language.english"), _choice == Choice.English)) _choice = Choice.English;
        if (ImGui.RadioButton(Localization.T("verdite2.language.retranslation"), _choice == Choice.Retranslation)) _choice = Choice.Retranslation;
        if (ImGui.RadioButton(Localization.T("verdite2.language.both"), _choice == Choice.Japanese)) _choice = Choice.Japanese;
        ImGui.Spacing();
        ImGui.TextWrapped(Localization.T("verdite2.language.note"));
        Rule();

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - ButtonWidth) * 0.5f);
        if (!ImGui.Button(Localization.T("verdite2.language.next"), new Vector2(ButtonWidth, 0f))) return;
        if (_choice == Choice.Japanese) _onDiscPage = true;
        else Keep(_choice == Choice.Retranslation ? LanguageStep.Retranslation : null, japaneseDisc: null);
    }

    void DrawDiscPage()
    {
        ImGui.TextWrapped(Localization.T("verdite2.jpdisc.description"));
        ImGui.Spacing();

        float spacing = ImGui.GetStyle().ItemSpacing.X;
        ImGui.SetNextItemWidth(ImGui.GetContentRegionAvail().X - BrowseWidth - spacing);
        ImGui.InputText("##jp-disc-path", ref _path, 1024);
        ImGui.SameLine();
        if (ImGui.Button(Localization.T("common.browse"), new Vector2(BrowseWidth, 0f))) Browse();

        if (_error.Length > 0)
        {
            ImGui.Spacing();
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.38f, 0.38f, 1f));
            ImGui.TextWrapped(_error);
            ImGui.PopStyleColor();
        }
        Rule();

        ImGui.SetCursorPosX(ImGui.GetCursorPosX() + (ImGui.GetContentRegionAvail().X - (ButtonWidth * 2f + spacing)) * 0.5f);
        if (ImGui.Button(Localization.T("verdite2.language.back"), new Vector2(ButtonWidth, 0f)))
        {
            _onDiscPage = false;
            _error = "";
        }
        ImGui.SameLine();
        if (ImGui.Button(Localization.T("common.confirm"), new Vector2(ButtonWidth, 0f))) Confirm();
    }

    static void Rule()
    {
        ImGui.Spacing();
        ImGui.Separator();
        ImGui.Spacing();
    }

    void Browse()
    {
        try
        {
            using var dialog = new NativeFileDialog().SelectFile().AddFilter(Localization.T("disc.filter"), "cue,chd");
            if (dialog.Open(out string? picked, FolderOf(_path)) != DialogResult.Okay || string.IsNullOrWhiteSpace(picked)) return;
            _path = picked;
            _error = "";
        }
        catch (Exception e)
        {
            _error = Localization.T("disc.error.dialog", e.Message);
        }
    }

    static string? FolderOf(string path) =>
        File.Exists(path) ? Path.GetDirectoryName(Path.GetFullPath(path)) : null;

    void Confirm()
    {
        var path = _path.Trim();
        if (Problem(path) is { } problem)
        {
            _error = Localization.T(problem);
            return;
        }
        Keep(LanguageStep.Japanese, Path.GetFullPath(path));
    }

    /// <summary>The string key saying why this is not the Japanese disc; null when it is.</summary>
    static string? Problem(string path)
    {
        if (!File.Exists(path)) return "disc.error.not_found";
        try
        {
            using var image = DiscImage.Open(path);
            return JapaneseVolumeIds.Contains(VolumeId(image)) ? null : "verdite2.jpdisc.wrong";
        }
        catch (Exception e)
        {
            Console.WriteLine($"[Verdite2] language: cannot read {path}: {e.Message}");
            return "verdite2.jpdisc.unreadable";
        }
    }

    static string VolumeId(IDiscImage image)
    {
        var sector = image.ReadSectorData(PrimaryVolumeSector, SectorBytes);
        return Encoding.ASCII.GetString(sector, VolumeIdAt, VolumeIdBytes).TrimEnd();
    }

    /// <summary>The answer, written: the language's value (none for English), and Japanese's disc.</summary>
    void Keep(string? language, string? japaneseDisc)
    {
        var values = ConfigManager.View.Values;
        values["verdite.language.asked"] = "1";
        if (language is null) values.Remove(LanguageStep.LanguageKey);
        else values[LanguageStep.LanguageKey] = language;
        if (japaneseDisc is not null) values[LanguageStep.JapaneseDiscKey] = japaneseDisc;
        Runtime.SaveView();
        Console.WriteLine($"[Verdite2] language: {language ?? "en"}");
        Close();
    }
}
