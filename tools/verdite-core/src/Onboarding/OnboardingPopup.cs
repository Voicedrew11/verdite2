using System.Numerics;
using ImGuiNET;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;

namespace Verdite.Core;

/// <summary>
/// The setup's pages: a welcome, the display step, Basic or Advanced, the tiers,
/// a summary. In the launcher (<see cref="Build"/> set) a strip at the foot follows
/// the game's build, and the popup stays up after Finish until the build is done,
/// which is what holds the game back until both are. In the game it is opened
/// from the settings page and closes on Finish, or on a list of what waits for
/// the next launch.
///
/// Every page can be driven by the mouse, the keyboard or a pad: ImGui's own
/// navigation, switched on only while the popup is open (the runtime's windows
/// never had it) and fed the pad's buttons here, since the runtime's ImGui backend
/// feeds it only the keyboard.
/// </summary>
internal sealed class OnboardingPopup : Popup
{
    public const int TierCount = 3;
    static readonly string[] TierKeys = ["verdite.setup.tier.original", "verdite.setup.tier.faithful", "verdite.setup.tier.modern"];

    protected override string TitleKey => "verdite.setup.title";
    protected override Vector2 Size => new(720f, 580f);

    /// <summary>Its own Cancel and Back: Escape is Back here, not close.</summary>
    protected override bool Closable => false;

    /// <summary>The launcher's build, as the setup's strip shows it.</summary>
    public readonly record struct BuildState(string Status, int Step, int Steps, string? Error, bool Done);

    /// <summary>Set in the launcher: the build running beside setup.</summary>
    public Func<BuildState>? Build { get; init; }

    /// <summary>True once the choices were applied or setup skipped.</summary>
    public bool Finished { get; private set; }

    enum Page { Welcome, Display, Mode, Tiers, Summary, Waiting, Report }

    readonly OnboardingSpec _spec;
    readonly string _tag;
    readonly bool _inGame;

    Page _page;
    bool _advanced;
    int[] _display = [];
    int[][] _tiers = [];
    Onboarding.Result? _result;

    public OnboardingPopup(OnboardingSpec spec, string tag, bool inGame)
    {
        _spec = spec;
        _tag = tag;
        _inGame = inGame;
    }

    protected override void OnOpened()
    {
        _page = Page.Welcome;
        _advanced = false;
        _result = null;
        Finished = false;
        // A first run starts the display on what suits the monitor; a run from the
        // game starts every choice on what is set now.
        _display = [.. _spec.Display.Select(f => _inGame ? Onboarding.Detect(_spec, f) : Onboarding.Suggest(_spec, f))];
        _tiers = [.. _spec.Categories.Select(c => Onboarding.Detect(_spec, c))];
        Nav(true);
    }

    protected override void OnClosed() => Nav(false);

    protected override void Update()
    {
        if (!IsOpen) return;
        FeedPad();
        if (_page == Page.Waiting && (Build is null || Build().Done)) Close();
    }

    // ---- Pages ----

    protected override void DrawContent()
    {
        var style = ImGui.GetStyle();
        float footer = ImGui.GetFrameHeightWithSpacing() + style.ItemSpacing.Y * 2f;
        if (Build is not null) footer += ImGui.GetTextLineHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing();

        if (ImGui.BeginChild("##page", new Vector2(0f, -footer)))
        {
            switch (_page)
            {
                case Page.Welcome: Welcome(); break;
                case Page.Display: Display(); break;
                case Page.Mode: Mode(); break;
                case Page.Tiers: if (_advanced) Advanced(); else Basic(); break;
                case Page.Summary: Summary(); break;
                case Page.Waiting: Waiting(); break;
                case Page.Report: Report(); break;
            }
        }
        ImGui.EndChild();

        ImGui.Separator();
        if (Build is not null) Strip();
        Footer();

        // Escape and the pad's Circle/B step back, but never off the welcome, where
        // Back is skip, and never while a combo's list is open, which they close.
        if (_page != Page.Welcome && ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)
            && (ImGui.IsKeyPressed(ImGuiKey.Escape, false) || ImGui.IsKeyPressed(ImGuiKey.GamepadFaceRight, false)))
            Back();
    }

    void Welcome()
    {
        Heading("verdite.setup.welcome.title");
        Wrapped(_spec.Welcome ?? "verdite.setup.welcome");
        if (Build is not null)
        {
            ImGui.Spacing();
            Wrapped("verdite.setup.welcome.building");
        }
        ImGui.Spacing();
        Dim("verdite.setup.tiers.intro");
    }

    void Display()
    {
        Heading("verdite.setup.display");
        Dim("verdite.setup.display.intro");
        if (Onboarding.Monitor() is { } m)
            Dim(Localization.T("verdite.setup.monitor", m.W, m.H, Onboarding.Refresh()), key: false);
        ImGui.Spacing();

        float labelW = ImGui.GetContentRegionAvail().X * 0.35f;
        for (int i = 0; i < _spec.Display.Count; i++)
        {
            var field = _spec.Display[i];
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Localization.T(field.Label));
            if (field.Tip is { } tip && ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T(tip));
            ImGui.SameLine(labelW);
            ImGui.SetNextItemWidth(-1f);
            string preview = _display[i] >= 0 ? OptionLabel(field.Options[_display[i]]) : Localization.T("verdite.setup.unchanged");
            if (ImGui.BeginCombo($"##{field.Id}", preview))
            {
                for (int o = 0; o < field.Options.Count; o++)
                {
                    bool selected = o == _display[i];
                    if (ImGui.Selectable(OptionLabel(field.Options[o]), selected)) _display[i] = o;
                    if (selected) ImGui.SetItemDefaultFocus();
                }
                ImGui.EndCombo();
            }
        }
    }

    void Mode()
    {
        Heading("verdite.setup.mode");
        ImGui.Spacing();
        var names = string.Join(", ", _spec.Categories.Select(c => Localization.T(c.Label)));
        float h = ImGui.GetTextLineHeightWithSpacing() * 3f;
        ImGui.PushStyleVar(ImGuiStyleVar.ButtonTextAlign, new Vector2(0f, 0.5f));
        if (ImGui.Button($"{Localization.T("verdite.setup.basic")}\n{Localization.T("verdite.setup.basic.desc", names)}", new Vector2(-1f, h)))
        {
            _advanced = false;
            _page = Page.Tiers;
        }
        ImGui.Spacing();
        if (ImGui.Button($"{Localization.T("verdite.setup.advanced")}\n{Localization.T("verdite.setup.advanced.desc")}", new Vector2(-1f, h)))
        {
            _advanced = true;
            _page = Page.Tiers;
        }
        ImGui.PopStyleVar();
    }

    void Basic()
    {
        Heading("verdite.setup.basic");
        Dim("verdite.setup.tiers.intro");
        for (int c = 0; c < _spec.Categories.Count; c++)
        {
            var cat = _spec.Categories[c];
            ImGui.Spacing();
            ImGui.SeparatorText(Localization.T(cat.Label));
            int now = _tiers[c].Distinct().Count() == 1 ? _tiers[c][0] : -1;
            int pick = TierRow(cat.Id, now);
            if (pick != now) Array.Fill(_tiers[c], pick);
            Dim(pick >= 0 && pick < cat.Tiers.Count ? cat.Tiers[pick] : "verdite.setup.custom");
        }
        ImGui.Spacing();
        if (ImGui.SmallButton(Localization.T("verdite.setup.toadvanced"))) _advanced = true;
    }

    void Advanced()
    {
        Heading("verdite.setup.advanced");
        Dim("verdite.setup.tiers.intro");
        float labelW = ImGui.GetContentRegionAvail().X * 0.3f;
        for (int c = 0; c < _spec.Categories.Count; c++)
        {
            var cat = _spec.Categories[c];
            ImGui.Spacing();
            ImGui.SeparatorText(Localization.T(cat.Label));
            for (int g = 0; g < cat.Groups.Count; g++)
            {
                var group = cat.Groups[g];
                ImGui.AlignTextToFramePadding();
                ImGui.TextUnformatted(Localization.T(group.Label));
                ImGui.SameLine(labelW);
                _tiers[c][g] = TierRow($"{cat.Id}.{group.Id}", _tiers[c][g]);
                ImGui.Indent(labelW);
                int t = _tiers[c][g];
                Dim(t >= 0 && t < group.Tiers.Count ? group.Tiers[t].Desc : "verdite.setup.custom");
                ImGui.Unindent(labelW);
            }
        }
        ImGui.Spacing();
        if (ImGui.SmallButton(Localization.T("verdite.setup.tobasic"))) _advanced = false;
    }

    void Summary()
    {
        Heading("verdite.setup.summary");
        Dim("verdite.setup.summary.intro");
        ImGui.Spacing();

        ImGui.SeparatorText(Localization.T("verdite.setup.display"));
        for (int i = 0; i < _spec.Display.Count; i++)
            Line(_spec.Display[i].Label, _display[i] >= 0 ? OptionLabel(_spec.Display[i].Options[_display[i]]) : Localization.T("verdite.setup.unchanged"));

        for (int c = 0; c < _spec.Categories.Count; c++)
        {
            var cat = _spec.Categories[c];
            ImGui.SeparatorText(Localization.T(cat.Label));
            bool one = _tiers[c].Distinct().Count() == 1;
            if (one && !_advanced)
            {
                Line(cat.Label, TierName(_tiers[c][0]));
                continue;
            }
            for (int g = 0; g < cat.Groups.Count; g++)
                Line(cat.Groups[g].Label, TierName(_tiers[c][g]));
        }
    }

    void Waiting()
    {
        Heading("verdite.setup.waiting.title");
        Wrapped("verdite.setup.waiting");
    }

    void Report()
    {
        Heading("verdite.setup.report.title");
        Wrapped("verdite.setup.report");
        ImGui.Spacing();
        foreach (var name in LaterNames()) ImGui.BulletText(name);
    }

    // ---- The foot of every page ----

    void Strip()
    {
        var state = Build!();
        if (state.Done)
            ImGui.TextUnformatted(Localization.T("verdite.setup.built"));
        else
            ImGui.TextUnformatted($"{Localization.T(state.Status)}{new string('.', 1 + (int)(ImGui.GetTime() * 2.0) % 3)}");
        float fraction = state.Done ? 1f : state.Steps <= 0 ? 0f : Math.Clamp(state.Step / (float)state.Steps, 0f, 1f);
        ImGui.ProgressBar(fraction, new Vector2(-1f, ImGui.GetFrameHeight() * 0.5f), "");
    }

    void Footer()
    {
        var style = ImGui.GetStyle();
        float avail = ImGui.GetContentRegionAvail().X;
        float half = (avail - style.ItemSpacing.X) / 2f;

        (string? left, string? right) = _page switch
        {
            Page.Welcome => (_inGame ? "verdite.setup.cancel" : "verdite.setup.skip", "verdite.setup.start"),
            Page.Display => ("verdite.setup.back", "verdite.setup.next"),
            Page.Mode => ("verdite.setup.back", null),
            Page.Tiers => ("verdite.setup.back", "verdite.setup.next"),
            Page.Summary => ("verdite.setup.back", "verdite.setup.finish"),
            Page.Report => (null, "verdite.setup.close"),
            _ => (null, null),
        };

        if (left is not null)
        {
            if (ImGui.Button(Localization.T(left), new Vector2(half, 0f))) Back();
            ImGui.SameLine();
        }
        else ImGui.Dummy(new Vector2(half, 0f));
        if (right is null) return;
        ImGui.SameLine(avail - half + style.WindowPadding.X);
        if (ImGui.Button(Localization.T(right), new Vector2(half, 0f))) Forward();
    }

    void Back()
    {
        switch (_page)
        {
            case Page.Welcome:
                if (_inGame) Close();
                else
                {
                    Onboarding.Skip(_spec, _tag);
                    Finished = true;
                    Done();
                }
                break;
            case Page.Display: _page = Page.Welcome; break;
            case Page.Mode: _page = Page.Display; break;
            case Page.Tiers: _page = Page.Mode; break;
            case Page.Summary: _page = Page.Tiers; break;
            case Page.Report: Close(); break;
        }
    }

    void Forward()
    {
        switch (_page)
        {
            case Page.Welcome: _page = Page.Display; break;
            case Page.Display: _page = Page.Mode; break;
            case Page.Tiers: _page = Page.Summary; break;
            case Page.Summary: Finish(); break;
            case Page.Report: Close(); break;
        }
    }

    void Finish()
    {
        _result = Onboarding.Apply(_spec, Onboarding.Sets(_spec, _display, _tiers), _tag);
        Finished = true;
        if (_inGame && _result.NextLaunch.Count > 0) _page = Page.Report;
        else Done();
    }

    /// <summary>Close, or in the launcher hold on the waiting page until the build is done.</summary>
    void Done()
    {
        if (Build is { } build && !build().Done) _page = Page.Waiting;
        else Close();
    }

    // ---- Pieces ----

    /// <summary>Three equal buttons, the chosen one lit; the tier clicked, else <paramref name="current"/>.</summary>
    static int TierRow(string id, int current)
    {
        var style = ImGui.GetStyle();
        float w = (ImGui.GetContentRegionAvail().X - style.ItemSpacing.X * (TierCount - 1)) / TierCount;
        int pick = current;
        for (int t = 0; t < TierCount; t++)
        {
            if (t > 0) ImGui.SameLine();
            bool lit = t == current;
            if (lit)
            {
                ImGui.PushStyleColor(ImGuiCol.Button, style.Colors[(int)ImGuiCol.ButtonActive]);
                ImGui.PushStyleColor(ImGuiCol.ButtonHovered, style.Colors[(int)ImGuiCol.ButtonActive]);
            }
            if (ImGui.Button($"{Localization.T(TierKeys[t])}##{id}", new Vector2(w, 0f))) pick = t;
            if (lit) ImGui.PopStyleColor(2);
        }
        return pick;
    }

    static string TierName(int t) => t >= 0 && t < TierCount ? Localization.T(TierKeys[t]) : Localization.T("verdite.setup.unchanged");

    /// <summary>An option's label: a localisation key (they all have a dot), or a
    /// plain one ("60", "16:9") shown as it is, which the runtime would warn about.</summary>
    static string OptionLabel(SetupOption o)
    {
        if (!o.Label.Contains('.')) return o.Label;
        return o.Set.Values.Any(v => v == "$refresh") ? Localization.T(o.Label, Onboarding.Refresh()) : Localization.T(o.Label);
    }

    /// <summary>The groups and display fields owning a key that waits for the next launch.</summary>
    IEnumerable<string> LaterNames()
    {
        if (_result is null) yield break;
        var seen = new HashSet<string>();
        foreach (var key in _result.NextLaunch)
        {
            string? name = _spec.Display.FirstOrDefault(f => f.Options.Any(o => o.Set.ContainsKey(key)))?.Label
                ?? _spec.Categories.SelectMany(c => c.Groups).FirstOrDefault(g => g.Tiers.Any(t => t.Set.ContainsKey(key)))?.Label;
            var text = Localization.T(name ?? key);
            if (seen.Add(text)) yield return text;
        }
    }

    static void Heading(string key)
    {
        ImGui.TextUnformatted(Localization.T(key));
        ImGui.Separator();
        ImGui.Spacing();
    }

    static void Wrapped(string key) => ImGui.TextWrapped(Localization.T(key));

    static void Dim(string text, bool key = true)
    {
        ImGui.PushStyleColor(ImGuiCol.Text, ImGui.GetStyle().Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextWrapped(key ? Localization.T(text) : text);
        ImGui.PopStyleColor();
    }

    static void Line(string labelKey, string value)
    {
        ImGui.TextUnformatted(Localization.T(labelKey));
        ImGui.SameLine(ImGui.GetContentRegionAvail().X * 0.45f);
        ImGui.TextUnformatted(value);
    }

    // ---- The pad, for ImGui's navigation ----

    /// <summary>SDL game-controller buttons, as the runtime numbers them (the stick
    /// directions are its own 102-105), to ImGui's gamepad keys.</summary>
    static readonly (int Button, ImGuiKey Key)[] PadKeys =
    [
        (0, ImGuiKey.GamepadFaceDown), (1, ImGuiKey.GamepadFaceRight),
        (2, ImGuiKey.GamepadFaceLeft), (3, ImGuiKey.GamepadFaceUp),
        (9, ImGuiKey.GamepadL1), (10, ImGuiKey.GamepadR1),
        (11, ImGuiKey.GamepadDpadUp), (12, ImGuiKey.GamepadDpadDown),
        (13, ImGuiKey.GamepadDpadLeft), (14, ImGuiKey.GamepadDpadRight),
        (102, ImGuiKey.GamepadLStickLeft), (103, ImGuiKey.GamepadLStickRight),
        (104, ImGuiKey.GamepadLStickUp), (105, ImGuiKey.GamepadLStickDown),
    ];

    readonly bool[] _held = new bool[PadKeys.Length];
    ImGuiConfigFlags _flags;
    ImGuiBackendFlags _backend;
    bool _nav;

    void Nav(bool on)
    {
        var io = ImGui.GetIO();
        if (on && !_nav)
        {
            _flags = io.ConfigFlags;
            _backend = io.BackendFlags;
            io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard | ImGuiConfigFlags.NavEnableGamepad;
            io.BackendFlags |= ImGuiBackendFlags.HasGamepad;
            _nav = true;
        }
        else if (!on && _nav)
        {
            for (int i = 0; i < PadKeys.Length; i++)
                if (_held[i]) { io.AddKeyEvent(PadKeys[i].Key, false); _held[i] = false; }
            io.ConfigFlags = _flags;
            io.BackendFlags = _backend;
            _nav = false;
        }
    }

    void FeedPad()
    {
        if (!_nav) return;
        var io = ImGui.GetIO();
        for (int i = 0; i < PadKeys.Length; i++)
        {
            bool down = HostWindow.IsPadButtonDown(PadKeys[i].Button);
            if (down == _held[i]) continue;
            _held[i] = down;
            io.AddKeyEvent(PadKeys[i].Key, down);
        }
    }

    /// <summary>The setup's own words, every key in the runtime's three languages.
    /// The game's groups and options are the game's, in its onboarding.json.</summary>
    public const string Strings = """
    {
      "strings": {
        "verdite.setup.title": { "en": "Setup", "pt-BR": "Configuração", "es-419": "Configuración" },
        "verdite.setup.welcome.title": { "en": "Welcome", "pt-BR": "Bem-vindo", "es-419": "Bienvenido" },
        "verdite.setup.welcome": {
          "en": "Before you play, choose how the game should look, sound and control. Everything here can be changed later in the settings.",
          "pt-BR": "Antes de jogar, escolha como o jogo deve parecer, soar e ser controlado. Tudo aqui pode ser alterado depois nas configurações.",
          "es-419": "Antes de jugar, elige cómo debe verse, sonar y controlarse el juego. Todo esto se puede cambiar después en la configuración."
        },
        "verdite.setup.welcome.building": {
          "en": "The game is being built from your disc in the meantime. It starts once both are done.",
          "pt-BR": "Enquanto isso, o jogo está sendo construído a partir do seu disco. Ele inicia quando as duas coisas terminarem.",
          "es-419": "Mientras tanto, el juego se está construyendo desde tu disco. Empieza cuando ambas cosas terminen."
        },
        "verdite.setup.tiers.intro": {
          "en": "Original is the game as the PlayStation ran it. Faithful keeps that look and feel and fixes what the hardware got wrong. Modern adds the port's enhancements.",
          "pt-BR": "Original é o jogo como o PlayStation o rodava. Fiel mantém essa aparência e sensação e corrige o que o hardware fazia errado. Moderno acrescenta as melhorias do port.",
          "es-419": "Original es el juego tal como lo ejecutaba la PlayStation. Fiel conserva ese aspecto y sensación y corrige lo que el hardware hacía mal. Moderno agrega las mejoras del port."
        },
        "verdite.setup.tier.original": { "en": "Original", "pt-BR": "Original", "es-419": "Original" },
        "verdite.setup.tier.faithful": { "en": "Faithful", "pt-BR": "Fiel", "es-419": "Fiel" },
        "verdite.setup.tier.modern": { "en": "Modern", "pt-BR": "Moderno", "es-419": "Moderno" },
        "verdite.setup.display": { "en": "Display", "pt-BR": "Tela", "es-419": "Pantalla" },
        "verdite.setup.display.intro": {
          "en": "How the picture is shown. These are asked on their own, apart from the styles that follow.",
          "pt-BR": "Como a imagem é exibida. Estas opções são perguntadas à parte dos estilos a seguir.",
          "es-419": "Cómo se muestra la imagen. Estas opciones se preguntan aparte de los estilos que siguen."
        },
        "verdite.setup.monitor": {
          "en": "Your display: {0} × {1} at {2} Hz",
          "pt-BR": "Sua tela: {0} × {1} a {2} Hz",
          "es-419": "Tu pantalla: {0} × {1} a {2} Hz"
        },
        "verdite.setup.mode": {
          "en": "How much would you like to choose?",
          "pt-BR": "Quanto você gostaria de escolher?",
          "es-419": "¿Cuánto te gustaría elegir?"
        },
        "verdite.setup.basic": { "en": "Basic", "pt-BR": "Básico", "es-419": "Básico" },
        "verdite.setup.basic.desc": {
          "en": "One style each for {0}.",
          "pt-BR": "Um estilo para cada: {0}.",
          "es-419": "Un estilo para cada uno: {0}."
        },
        "verdite.setup.advanced": { "en": "Advanced", "pt-BR": "Avançado", "es-419": "Avanzado" },
        "verdite.setup.advanced.desc": {
          "en": "A style for each part of those, one by one.",
          "pt-BR": "Um estilo para cada parte delas, uma por uma.",
          "es-419": "Un estilo para cada parte de ellas, una por una."
        },
        "verdite.setup.custom": {
          "en": "Custom: set part by part. Choose a style to replace it, or leave it as it is.",
          "pt-BR": "Personalizado: definido parte por parte. Escolha um estilo para substituir, ou deixe como está.",
          "es-419": "Personalizado: definido parte por parte. Elige un estilo para reemplazarlo, o déjalo como está."
        },
        "verdite.setup.unchanged": { "en": "Custom, unchanged", "pt-BR": "Personalizado, sem mudanças", "es-419": "Personalizado, sin cambios" },
        "verdite.setup.toadvanced": { "en": "Choose part by part (Advanced)", "pt-BR": "Escolher parte por parte (Avançado)", "es-419": "Elegir parte por parte (Avanzado)" },
        "verdite.setup.tobasic": { "en": "Choose by category (Basic)", "pt-BR": "Escolher por categoria (Básico)", "es-419": "Elegir por categoría (Básico)" },
        "verdite.setup.summary": { "en": "Ready", "pt-BR": "Pronto", "es-419": "Listo" },
        "verdite.setup.summary.intro": {
          "en": "These choices will be applied:",
          "pt-BR": "Estas escolhas serão aplicadas:",
          "es-419": "Se aplicarán estas opciones:"
        },
        "verdite.setup.waiting.title": { "en": "Almost there", "pt-BR": "Quase lá", "es-419": "Casi listo" },
        "verdite.setup.waiting": {
          "en": "Your settings are saved. The game starts as soon as it is built.",
          "pt-BR": "Suas configurações foram salvas. O jogo inicia assim que for construído.",
          "es-419": "Tu configuración se guardó. El juego empieza en cuanto esté construido."
        },
        "verdite.setup.built": { "en": "The game is built.", "pt-BR": "O jogo foi construído.", "es-419": "El juego está construido." },
        "verdite.setup.report.title": { "en": "Done", "pt-BR": "Concluído", "es-419": "Listo" },
        "verdite.setup.report": {
          "en": "Everything else has changed already. These take effect the next time the game starts:",
          "pt-BR": "Todo o resto já mudou. Estas entram em vigor na próxima vez que o jogo iniciar:",
          "es-419": "Todo lo demás ya cambió. Estas se aplican la próxima vez que inicie el juego:"
        },
        "verdite.setup.start": { "en": "Set up", "pt-BR": "Configurar", "es-419": "Configurar" },
        "verdite.setup.skip": { "en": "Skip, keep the defaults", "pt-BR": "Pular, manter o padrão", "es-419": "Omitir, mantener lo predeterminado" },
        "verdite.setup.cancel": { "en": "Cancel", "pt-BR": "Cancelar", "es-419": "Cancelar" },
        "verdite.setup.back": { "en": "Back", "pt-BR": "Voltar", "es-419": "Atrás" },
        "verdite.setup.next": { "en": "Next", "pt-BR": "Avançar", "es-419": "Siguiente" },
        "verdite.setup.finish": { "en": "Finish", "pt-BR": "Concluir", "es-419": "Finalizar" },
        "verdite.setup.close": { "en": "Close", "pt-BR": "Fechar", "es-419": "Cerrar" }
      }
    }
    """;
}
