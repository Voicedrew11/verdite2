using System.Globalization;
using System.Numerics;
using System.Text.RegularExpressions;
using ImGuiNET;
using RecompOne.Runtime.Host;
using RecompOne.Runtime.Host.Window;

namespace Verdite.Core;

/// <summary>
/// The setup's pages: a welcome with three presets (Original, Balanced, Modern)
/// and Custom, the display step, a tiers page per category (a tier for each of its
/// groups), the game's own choice pages (if its spec has any), a summary. Every choice on the
/// welcome is a starting point, not an answer: a preset fills the pages with its
/// choices and Custom with what the player has now, and the player walks them all
/// to the summary either way. In the launcher (<see cref="Build"/> set) a strip at the foot follows
/// the game's build, and the popup stays up after Finish until the build is done,
/// which is what holds the game back until both are. In the game it is opened
/// from the settings page and closes on Finish, or on a list of what waits for
/// the next launch.
///
/// Every page can be driven by the mouse, the keyboard or a pad: ImGui's own
/// navigation, switched on only while the popup is open (the runtime's windows
/// never had it) and fed the pad's buttons here, since the runtime's ImGui backend
/// feeds it only the keyboard. On top of that navigation, the pad has shortcuts of
/// its own: Start is Next (Finish on the summary), Circle/B is Back, and left and
/// right on a focused list or slider step its value without opening or grabbing it.
/// The keyboard has a Next of its own too, PageDown, and the foot's hints name only
/// the device that was used last (<see cref="_padActive"/>).
/// </summary>
internal sealed class OnboardingPopup : Popup
{
    public const int TierCount = 3;
    /// <summary>The tier the welcome's badge and the tiers page's preset row mark as recommended.</summary>
    const int RecommendedTier = 2;
    static readonly string[] TierKeys = ["verdite.setup.tier.original", "verdite.setup.tier.originalplus", "verdite.setup.tier.modern"];

    /// <summary>The welcome card's title, the heading and the badge, as multiples of the text size.</summary>
    const float CardTitleScale = 1.3f;
    const float HeadingScale = 1.5f;
    /// <summary>A tiers page's group header, as a multiple of the text size: larger than the body, so the groups read as sections.</summary>
    const float GroupScale = 1.22f;

    /// <summary>
    /// The popup's scale: 1 at 1080 pixels high, larger above it, so the text stays
    /// readable at 1440 and above. Read each frame; every pixel size here is multiplied by it.
    /// </summary>
    static float UiScale => Math.Max(1f, ImGui.GetMainViewport().Size.Y / 1080f);

    protected override string TitleKey => "verdite.setup.title";
    protected override Vector2 Size => new Vector2(1100f, 680f) * UiScale;

    /// <summary>Its own Cancel and Back: Escape is Back here, not close.</summary>
    protected override bool Closable => false;

    /// <summary>The launcher's build, as the setup's strip shows it.</summary>
    public readonly record struct BuildState(string Status, int Step, int Steps, string? Error, bool Done);

    /// <summary>Set in the launcher: the build running beside setup.</summary>
    public Func<BuildState>? Build { get; init; }

    /// <summary>True once the choices were applied or setup skipped.</summary>
    public bool Finished { get; private set; }

    enum Page { Welcome, Display, Tiers, Choice, Summary, Waiting, Report }

    readonly OnboardingSpec _spec;
    readonly string _tag;
    readonly bool _inGame;

    Page _page;
    bool _focus;
    /// <summary>
    /// The device the player used last: a pad button pressed sets it, a key, a click or
    /// a mouse move clears it. It picks which input hints the foot shows.
    /// </summary>
    bool _padActive;
    int[] _display = [];
    int[][] _tiers = [];
    int[] _startDisplay = [];
    int[][] _startTiers = [];
    /// <summary>The tiers page on screen, an index into the spec's categories.</summary>
    int _catAt;
    /// <summary>The choice page on screen, an index into the spec's pages.</summary>
    int _pageAt;
    SetupPageChoice[] _pages = [];
    SetupPageChoice[] _startPages = [];
    /// <summary>Whether the player has changed a choice page; one untouched follows its category.</summary>
    bool[] _pageTouched = [];
    Onboarding.Result? _result;
    /// <summary>Each preset's display as one line ("1280 x 720 / 16:9 / 60 fps"), by tier, from OnOpened.</summary>
    string[] _specLines = [];

    public OnboardingPopup(OnboardingSpec spec, string tag, bool inGame)
    {
        _spec = spec;
        _tag = tag;
        _inGame = inGame;
    }

    protected override void OnOpened()
    {
        Go(Page.Welcome);
        _result = null;
        Finished = false;
        _padActive = false;
        // A first run starts the display on what suits the monitor; any other run
        // (from the game, after a version bump, forced) on what is set now, so
        // clicking through changes nothing the player chose.
        bool suggest = !_inGame && Onboarding.FirstRun(_spec);
        _display = [.. _spec.Display.Select(f => suggest ? Onboarding.Suggest(_spec, f) : Onboarding.Detect(_spec, f))];
        _tiers = [.. _spec.Categories.Select(c => Onboarding.Detect(_spec, c))];
        // What the player started with, for "Keep as is": a value set by hand is kept
        // when the player does not pick a style for it.
        _startDisplay = [.. _display];
        _startTiers = [.. _tiers.Select(t => (int[])t.Clone())];
        _pages = [.. _spec.Pages.Select(p => Onboarding.Detect(_spec, p))];
        _startPages = [.. _pages.Select(c => c.Clone())];
        _pageTouched = new bool[_spec.Pages.Count];
        // Deterministic for the open (the monitor is read once), so each preset's line is made once.
        _specLines = [.. Enumerable.Range(0, TierCount).Select(t => SpecLine(Onboarding.Preset(_spec, t)))];
        Nav(true);
    }

    /// <summary>A preset's display as one line: each display choice in the spec's order, the
    /// window's left out, a trailing parenthetical stripped, the parts joined with " / ".</summary>
    string SpecLine((int[] Display, int[][] Tiers, SetupPageChoice[] Pages) preset)
    {
        var parts = new List<string>();
        for (int i = 0; i < _spec.Display.Count && i < preset.Display.Length; i++)
        {
            var field = _spec.Display[i];
            int o = preset.Display[i];
            if (field.Id == "window" || o < 0 || o >= field.Options.Count) continue;
            parts.Add(Regex.Replace(OptionLabel(field.Options[o]), @"\s*\([^)]*\)$", ""));
        }
        return string.Join(" / ", parts);
    }

    protected override void OnClosed() => Nav(false);

    protected override void Update()
    {
        if (!IsOpen) return;
        ReadDevice();
        FeedPad();
        if (_page == Page.Waiting && (Build is null || Build().Done || Build().Error is not null)) Close();
    }

    // ---- Pages ----

    protected override void DrawContent()
    {
        float s = UiScale;
        ImGui.SetWindowFontScale(s);
        _lastFocusId = _focusId;
        _focusId = 0;

        var style = ImGui.GetStyle();
        float footer = ImGui.GetFrameHeightWithSpacing() + style.ItemSpacing.Y * 2f;
        if (Build is not null) footer += ImGui.GetTextLineHeightWithSpacing() + ImGui.GetFrameHeightWithSpacing();

        // A visible scrollbar, so a page taller than the window says it scrolls. BeginChild reads
        // the style, so it is pushed before and popped after the child is ended.
        ImGui.PushStyleVar(ImGuiStyleVar.ScrollbarSize, 12f * s);
        bool open = ImGui.BeginChild("##page", new Vector2(0f, -footer));
        // The page is a window of its own, so its font scale is set again.
        ImGui.SetWindowFontScale(s);
        if (open)
        {
            switch (_page)
            {
                case Page.Welcome: Welcome(); break;
                case Page.Display: Display(); break;
                case Page.Tiers: Tiers(); break;
                case Page.Choice: Choice(); break;
                case Page.Summary: Summary(); break;
                case Page.Waiting: Waiting(); break;
                case Page.Report: Report(); break;
            }
        }
        ImGui.EndChild();
        ImGui.PopStyleVar();

        ImGui.Separator();
        if (Build is not null) Strip();
        Footer();
        Shortcuts();
    }

    /// <summary>
    /// The page-wide keys. Escape and Circle/B step back, but never off the welcome,
    /// where Back is skip; Start and PageDown go on, as Next or Finish. Neither while a list was open or a slider
    /// held last frame: ImGui's navigation spends Circle/B closing or releasing it
    /// before this frame is drawn, so this frame alone cannot tell.
    /// </summary>
    void Shortcuts()
    {
        bool busy = _busy;
        _busy = _comboOpen || ImGui.IsAnyItemActive();
        _comboOpen = false;
        if (busy || !ImGui.IsWindowFocused(ImGuiFocusedFlags.RootAndChildWindows)) return;

        if (_page != Page.Welcome
            && (ImGui.IsKeyPressed(ImGuiKey.Escape, false) || ImGui.IsKeyPressed(ImGuiKey.GamepadFaceRight, false)))
            Back();
        else if (ImGui.IsKeyPressed(ImGuiKey.GamepadStart, false) || ImGui.IsKeyPressed(ImGuiKey.PageDown, false))
            Forward();
    }

    /// <summary>A list was open this frame, or (<see cref="_busy"/>) a list or a held slider last frame.</summary>
    bool _comboOpen, _busy;

    /// <summary>
    /// Left or right, with repeat, on the item just drawn when it has the focus and is
    /// not held: -1, 1, or 0. Lets the pad step a list or a slider in place, as a
    /// console menu does, instead of opening the list or grabbing the slider first.
    /// </summary>
    static int Step()
    {
        if (!ImGui.IsItemFocused() || ImGui.IsItemActive()) return 0;
        if (ImGui.IsKeyPressed(ImGuiKey.GamepadDpadLeft) || ImGui.IsKeyPressed(ImGuiKey.GamepadLStickLeft)
            || ImGui.IsKeyPressed(ImGuiKey.LeftArrow)) return -1;
        if (ImGui.IsKeyPressed(ImGuiKey.GamepadDpadRight) || ImGui.IsKeyPressed(ImGuiKey.GamepadLStickRight)
            || ImGui.IsKeyPressed(ImGuiKey.RightArrow)) return 1;
        return 0;
    }

    /// <summary>
    /// The welcome: its heading and text, then the three presets side by side (Original,
    /// Balanced, Modern), filling the page's height down to Custom, a full-width card
    /// at the foot. First run and a re-run differ only in their heading and text.
    /// </summary>
    void Welcome()
    {
        float s = UiScale;
        Heading(_inGame ? "verdite.setup.again.title" : "verdite.setup.welcome.title");
        Wrapped(_inGame ? (_spec.WelcomeAgain ?? "verdite.setup.again") : (_spec.Welcome ?? "verdite.setup.welcome"));
        if (Build is not null)
        {
            ImGui.Spacing();
            Wrapped("verdite.setup.welcome.building");
        }
        ImGui.Spacing();
        Dim("verdite.setup.welcome.start");
        ImGui.Spacing();

        var spacing = ImGui.GetStyle().ItemSpacing;
        var avail = ImGui.GetContentRegionAvail();
        static string? Badge(int tier) => tier == RecommendedTier ? Localization.T("verdite.setup.recommended") : null;
        var presets = new (string Id, int Tier, string Title, string? Badge, string Desc)[]
        {
            ("##card.original", 0, Localization.T("verdite.setup.original"), Badge(0), Localization.T("verdite.setup.original.desc")),
            ("##card.originalplus", 1, Localization.T("verdite.setup.originalplus"), Badge(1), Localization.T("verdite.setup.originalplus.desc")),
            ("##card.modern", 2, Localization.T("verdite.setup.tier.modern"), Badge(2), Localization.T("verdite.setup.recommended.desc")),
        };
        string customDesc = Localization.T("verdite.setup.customise.desc");
        float customH = CardHeight(avail.X, customDesc, null);
        float columnW = (avail.X - spacing.X * (presets.Length - 1)) / presets.Length;
        // The grid takes the height Custom leaves, but never less than its tallest card,
        // so a short window scrolls the page instead of cutting a card's text.
        float tallest = presets.Max(c => CardHeight(columnW, c.Desc, _specLines[c.Tier]));
        float gridH = Math.Max(tallest, avail.Y - customH - spacing.Y);

        for (int i = 0; i < presets.Length; i++)
        {
            var c = presets[i];
            if (i > 0) ImGui.SameLine();
            // The recommended card takes the focus on opening, wherever it sits in the row.
            if (c.Badge is not null && _focus) { ImGui.SetKeyboardFocusHere(); _focus = false; }
            if (Card(c.Id, c.Title, c.Badge, c.Desc, _specLines[c.Tier], new Vector2(columnW, gridH)))
                Pick(c.Tier);
        }
        if (Card("##card.custom", Localization.T("verdite.setup.customise"), null, customDesc, null, new Vector2(avail.X, customH)))
        {
            _display = [.. _startDisplay];
            _tiers = [.. _startTiers.Select(t => (int[])t.Clone())];
            _pages = [.. _startPages.Select(c => c.Clone())];
            _pageTouched = new bool[_spec.Pages.Count];
            Go(Page.Display);
        }
    }

    /// <summary>A card's padding and the gap between its lines, and the room its chevron keeps at the right.</summary>
    static float CardPad => 12f * UiScale;
    static float CardGap => 5f * UiScale;
    static float CardChevron => 28f * UiScale;

    /// <summary>The height a card of <paramref name="width"/> needs for its title, description and spec line.</summary>
    static float CardHeight(float width, string desc, string? spec)
    {
        float textW = width - CardPad * 2f - CardChevron;
        float h = CardPad + ImGui.GetTextLineHeight() * CardTitleScale + CardGap + ImGui.CalcTextSize(desc, false, textW).Y;
        if (spec is not null) h += CardGap + ImGui.CalcTextSize(spec, false, textW).Y;
        return h + CardPad;
    }

    /// <summary>
    /// A welcome card of <paramref name="size"/>: a rounded panel, its title (larger, faked
    /// bold, with a badge after it when given), its description wrapped to the card from
    /// the top, the spec line dimmed at the card's foot, and a chevron at the foot's right,
    /// since every card leads on to the pages. The panel is an invisible button, so the
    /// keyboard and the pad reach it like any button. Its colours are the theme's, as
    /// every other window's: a frame, lit as a hovered frame and edged in the navigation
    /// colour when hovered or focused. True when chosen.
    /// </summary>
    static bool Card(string id, string title, string? badge, string desc, string? spec, Vector2 size)
    {
        float s = UiScale;
        float pad = CardPad, gap = CardGap;
        float lineH = ImGui.GetTextLineHeight();
        float titleH = lineH * CardTitleScale;
        float textW = size.X - pad * 2f - CardChevron;

        // One group, so the card is one item to SameLine after, whatever its text moved.
        ImGui.BeginGroup();
        bool clicked = ImGui.InvisibleButton(id, size);
        bool lit = ImGui.IsItemHovered() || ImGui.IsItemFocused();
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var draw = ImGui.GetWindowDrawList();
        var font = ImGui.GetFont();
        var style = ImGui.GetStyle();
        uint Col(ImGuiCol c) => ImGui.ColorConvertFloat4ToU32(style.Colors[(int)c]);
        float round = style.FrameRounding;

        draw.AddRectFilled(min, max, Col(lit ? ImGuiCol.FrameBgHovered : ImGuiCol.FrameBg), round);
        draw.AddRect(min, max, Col(lit ? ImGuiCol.NavHighlight : ImGuiCol.Border), round,
            ImDrawFlags.None, (lit ? 2.5f : 1f) * s);

        uint ink = Col(ImGuiCol.Text);
        var origin = min + new Vector2(pad, pad);
        float big = ImGui.GetFontSize() * CardTitleScale;
        // Faked bold: the same text a pixel to the right.
        draw.AddText(font, big, origin, ink, title);
        draw.AddText(font, big, origin + new Vector2(s, 0f), ink, title);

        if (badge is not null)
        {
            const float badgeScale = 0.8f;
            float titleW = ImGui.CalcTextSize(title).X * CardTitleScale;
            float bh = lineH * badgeScale + 4f * s;
            float bw = ImGui.CalcTextSize(badge).X * badgeScale + 12f * s;
            var at = new Vector2(origin.X + titleW + 10f * s, origin.Y + (titleH - bh) * 0.5f);
            draw.AddRectFilled(at, at + new Vector2(bw, bh), Col(ImGuiCol.ButtonActive), round);
            draw.AddText(font, ImGui.GetFontSize() * badgeScale, at + new Vector2(6f * s, 2f * s), ink, badge);
        }

        // The description under the title, and the spec line at the foot, dimmed as the
        // rest of the page's secondary text. The wrap position is window-local, not screen.
        ImGui.SetCursorScreenPos(new Vector2(origin.X, origin.Y + titleH + gap));
        ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + textW);
        ImGui.TextWrapped(desc);
        if (spec is not null)
        {
            float specH = ImGui.CalcTextSize(spec, false, textW).Y;
            ImGui.SetCursorScreenPos(new Vector2(origin.X, max.Y - pad - specH));
            ImGui.PushStyleColor(ImGuiCol.Text, style.Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped(spec);
            ImGui.PopStyleColor();
        }
        ImGui.PopTextWrapPos();
        ImGui.EndGroup();

        float cx = max.X - pad - 6f * s, cy = max.Y - pad - lineH * 0.5f;
        draw.AddTriangleFilled(new Vector2(cx, cy - 7f * s), new Vector2(cx + 6f * s, cy),
            new Vector2(cx, cy + 7f * s), ink);
        return clicked;
    }

    /// <summary>
    /// A preset (tier 0, 1 or 2) as the starting point: its choices fill the pages, and
    /// the player walks them from the display on, changing what they like.
    /// </summary>
    void Pick(int tier)
    {
        (_display, _tiers, _pages) = Onboarding.Preset(_spec, tier);
        _pageTouched = new bool[_spec.Pages.Count];
        Go(Page.Display);
    }

    void Display()
    {
        Heading("verdite.setup.display", StepText());
        Dim("verdite.setup.display.intro");
        if (Onboarding.Monitor() is { } m)
            Dim(Localization.T("verdite.setup.monitor", m.W, m.H, Onboarding.Refresh()), key: false);
        ImGui.Spacing();

        float labelW = ImGui.GetContentRegionAvail().X * 0.35f;
        for (int i = 0; i < _spec.Display.Count; i++)
        {
            var field = _spec.Display[i];
            bool keep = _startDisplay[i] < 0;
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted(Localization.T(field.Label));
            if (field.Tip is { } tip && ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T(tip));
            ImGui.SameLine(labelW);
            ImGui.SetNextItemWidth(-1f);
            // Only the first field takes the focus: _focus is spent here.
            if (_focus) { ImGui.SetKeyboardFocusHere(); _focus = false; }
            if (field.Slider)
            {
                Slider(field, ref _display[i], keep);
                continue;
            }
            string preview = _display[i] >= 0 ? OptionLabel(field.Options[_display[i]]) : KeepValue(field);
            bool listed = ImGui.BeginCombo($"##{field.Id}", preview);
            if (!listed && Step() is var step && step != 0)
                _display[i] = Math.Clamp(_display[i] + step, keep ? -1 : 0, field.Options.Count - 1);
            if (listed)
            {
                _comboOpen = true;
                if (keep)
                {
                    bool kept = _display[i] < 0;
                    if (ImGui.Selectable(KeepValue(field), kept)) _display[i] = -1;
                    if (kept) ImGui.SetItemDefaultFocus();
                }
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

    /// <summary>
    /// A category's tiers page: its name as the heading, the intro, the preset row that
    /// sets every group at once, then each of its groups with a tier of its own. The
    /// columns are cut once for the whole page, from the keep column any group on it
    /// needs, so every group's buttons line up down the page. The spacing is tighter
    /// here than on the other pages, so the page's six Presentation rows fit a 1280 by
    /// 800 window without scrolling.
    /// </summary>
    void Tiers()
    {
        var cat = _spec.Categories[_catAt];
        var style = ImGui.GetStyle();
        Heading(cat.Label, StepText());
        Dim("verdite.setup.tiers.intro");
        ImGui.PushStyleVar(ImGuiStyleVar.ItemSpacing, new Vector2(style.ItemSpacing.X, style.ItemSpacing.Y * 0.5f));
        bool anyKeep = Enumerable.Range(0, cat.Groups.Count).Any(g => _startTiers[_catAt][g] < 0);
        var grid = MakeTierGrid(ImGui.GetCursorPosX(), ImGui.GetContentRegionAvail().X, style.ItemSpacing.X, anyKeep);
        StepFocus(cat);
        if (PresetRow(cat, grid, anyKeep) is { } preset)
            for (int g = 0; g < cat.Groups.Count; g++) _tiers[_catAt][g] = preset;
        for (int g = 0; g < cat.Groups.Count; g++)
        {
            var group = cat.Groups[g];
            ImGui.Dummy(new Vector2(0f, 4f * UiScale));
            // The row's top, before its header, for the focus bar in the gutter.
            float top = ImGui.GetCursorScreenPos().Y;
            GroupHeader(group.Label);
            var pick = TierRow(g, $"{cat.Id}.{group.Id}", _tiers[_catAt][g], group.Tiers,
                               Spans(group), _startTiers[_catAt][g] < 0, grid, top);
            if (pick is { } p) _tiers[_catAt][g] = p < 0 ? _startTiers[_catAt][g] : p;
        }
        // The focus moved left or right onto a single column: that column is the one Up and Down keep.
        // A merged run covers several columns and leaves the preference where it was.
        if (!_focused.IsNone && _focused.Span == 1 && _focused != _lastFocused) _prefColumn = _focused.Start;
        _moveTo = TierCell.None;
        ImGui.PopStyleVar();
    }

    /// <summary>
    /// A tiers button, as the keyboard or pad sees it: its row (-1 the preset row, else the
    /// group's index), the first tier of its run (-1 for the keep button; a preset is its
    /// own tier), and how many columns the run covers (0 for keep). <see cref="None"/> when
    /// no tiers button has the focus.
    /// </summary>
    readonly record struct TierCell(int Row, int Start, int Span)
    {
        public static readonly TierCell None = new(int.MinValue, 0, 0);
        public bool IsNone => Row == int.MinValue;
    }

    /// <summary>The tiers button the focus was on last frame, to tell a move from a stay.</summary>
    TierCell _lastFocused = TierCell.None;
    /// <summary>The tiers button the focus is on this frame, set as the buttons are drawn.</summary>
    TierCell _focused = TierCell.None;
    /// <summary>
    /// The column Up and Down keep to. A single-column button that takes the focus by Left or
    /// Right sets it, so a row's run covering this column is where Up and Down land; a merged
    /// run does not, so crossing a "Balanced / Modern" run leaves Modern as the target.
    /// </summary>
    int _prefColumn = 2;
    /// <summary>
    /// The button Up or Down moved the focus to this frame, drawn as the keyboard focus
    /// before it is. Set and consumed in one frame, so ImGui's own move in the same frame is overridden.
    /// </summary>
    TierCell _moveTo = TierCell.None;

    /// <summary>
    /// Reads Up and Down this frame, from the tiers button the focus was on last frame: the
    /// row above or below takes the focus on the run that covers the preferred column. The
    /// preset row's Up and the last group's Down are left to ImGui's navigation, as is any
    /// press while an item is held.
    /// </summary>
    void StepFocus(SetupCategory cat)
    {
        _lastFocused = _focused;
        _focused = TierCell.None;
        _moveTo = TierCell.None;
        if (_lastFocused.IsNone || ImGui.IsAnyItemActive()) return;
        int dir = ImGui.IsKeyPressed(ImGuiKey.GamepadDpadUp) || ImGui.IsKeyPressed(ImGuiKey.GamepadLStickUp) || ImGui.IsKeyPressed(ImGuiKey.UpArrow) ? -1
            : ImGui.IsKeyPressed(ImGuiKey.GamepadDpadDown) || ImGui.IsKeyPressed(ImGuiKey.GamepadLStickDown) || ImGui.IsKeyPressed(ImGuiKey.DownArrow) ? 1
            : 0;
        int row = _lastFocused.Row + dir;
        if (dir != 0 && row >= -1 && row < cat.Groups.Count) _moveTo = PreferredCell(cat, row);
    }

    /// <summary>The button of a row that covers the preferred column: the preset of that tier, or the run covering it.</summary>
    TierCell PreferredCell(SetupCategory cat, int row)
    {
        if (row < 0) return new TierCell(-1, _prefColumn, 1);
        var span = Spans(cat.Groups[row]);
        if (_prefColumn >= span.Length) return TierCell.None;
        int s = span[_prefColumn];
        return new TierCell(row, s, span.Count(x => x == s));
    }

    /// <summary>Gives the keyboard focus to <paramref name="cell"/> when a move was set for it, just before it is drawn.</summary>
    void Aim(TierCell cell)
    {
        if (_moveTo.Row != cell.Row || _moveTo.Start != cell.Start) return;
        ImGui.SetKeyboardFocusHere();
        _moveTo = TierCell.None;
    }

    /// <summary>
    /// The preset row: Original, Balanced and Modern on the page's tier columns, each setting
    /// every group on the page to its tier. Lit when every group is at that tier (the same
    /// button, merged or not); when no preset is lit the groups are mixed, and a dim "Custom"
    /// marks it, in the keep column when the page has one. Modern carries the recommended
    /// note. The row takes the page's focus first, on Original. Returns the tier pressed, or null.
    /// </summary>
    int? PresetRow(SetupCategory cat, TierGrid grid, bool anyKeep)
    {
        var style = ImGui.GetStyle();
        float top = ImGui.GetCursorScreenPos().Y;
        float bottom = top;
        var lit = Enumerable.Range(0, TierCount).Select(t => cat.Groups.Count > 0
            && Enumerable.Range(0, cat.Groups.Count).All(g => Matches(cat.Groups[g], _tiers[_catAt][g], t))).ToArray();
        bool mixed = !lit.Any(x => x);
        int? pick = null;
        bool placed = false;
        void Place(Track at)
        {
            if (placed) ImGui.SameLine();
            placed = true;
            ImGui.SetCursorPosX(at.X);
        }

        if (mixed && anyKeep)
        {
            Place(grid.Keep);
            ImGui.AlignTextToFramePadding();
            ImGui.PushStyleColor(ImGuiCol.Text, style.Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextUnformatted(Localization.T("verdite.setup.mixed"));
            ImGui.PopStyleColor();
        }
        for (int t = 0; t < TierCount; t++)
        {
            Place(grid.Tiers[t]);
            if (t == 0 && _focus) { ImGui.SetKeyboardFocusHere(); _focus = false; }
            var cell = new TierCell(-1, t, 1);
            Aim(cell);
            string label = Localization.T(TierKeys[t]);
            if (t == RecommendedTier) label += $" ({Localization.T("verdite.setup.recommended")})";
            if (LitButton(label, $"{cat.Id}.preset{t}", lit[t], new Vector2(grid.Tiers[t].W, 0f), out bool focused)) pick = t;
            if (focused) _focused = cell;
            bottom = Math.Max(bottom, ImGui.GetItemRectMax().Y);
        }
        if (mixed && !anyKeep)
        {
            Dim("verdite.setup.mixed");
            bottom = Math.Max(bottom, ImGui.GetItemRectMax().Y);
        }
        if (_focused.Row == -1) GutterBar(top, bottom);
        return pick;
    }

    /// <summary>Whether a group is at tier <paramref name="t"/>: its own tier is set, and it is that tier's run, merged or not.</summary>
    static bool Matches(SetupGroup group, int current, int t)
    {
        if (current < 0 || current >= group.Tiers.Count || t >= group.Tiers.Count) return false;
        var span = Spans(group);
        return span[current] == span[t];
    }

    /// <summary>
    /// The focus marker in the left gutter of a tiers row: a bar beside it, from the top of
    /// the row's header to the bottom of its descriptions, drawn when one of its buttons has
    /// the focus. Positioned from the child window, so it follows the scroll.
    /// </summary>
    static void GutterBar(float top, float bottom)
    {
        float x = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X - 8f * UiScale;
        uint ink = ImGui.ColorConvertFloat4ToU32(ImGui.GetStyle().Colors[(int)ImGuiCol.Text]);
        ImGui.GetWindowDrawList().AddRectFilled(new Vector2(x, top), new Vector2(x + 3f * UiScale, bottom), ink);
    }

    /// <summary>
    /// One of the spec's choice pages: its heading and paragraphs, its label, an option
    /// button each (the chosen one lit, its description under it), then its slider.
    /// </summary>
    void Choice()
    {
        var page = _spec.Pages[_pageAt];
        var choice = _pages[_pageAt];
        Heading(page.Title, StepText());
        foreach (var body in page.Body)
        {
            Wrapped(body);
            ImGui.Spacing();
        }
        ImGui.SeparatorText(Localization.T(page.Label));
        var style = ImGui.GetStyle();
        for (int o = 0; o < page.Options.Count; o++)
        {
            var option = page.Options[o];
            bool lit = choice.Option == o;
            // The page's first button takes the focus, once; a page with no options gives it to the slider.
            if (o == 0 && _focus) { ImGui.SetKeyboardFocusHere(); _focus = false; }
            if (LitButton(OptionLabel(option), $"{page.Id}{o}", lit, new Vector2(-1f, 0f)))
            {
                choice.Option = o;
                _pageTouched[_pageAt] = true;
                if (option.Value is { } v) choice.Value = v;
            }
            if (option.Desc is { } desc)
            {
                if (!lit) ImGui.PushStyleColor(ImGuiCol.Text, style.Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.TextWrapped(Localization.T(desc));
                if (!lit) ImGui.PopStyleColor();
            }
            ImGui.Spacing();
        }

        if (page.Slider is { } s)
        {
            ImGui.Spacing();
            ImGui.TextUnformatted(Localization.T(s.Label));
            float v = (float)ValueOf(s, choice);
            // At the minimum the slider shows its label, not the number.
            string format = s.MinLabel is { } low && v <= s.Min + 1e-6 ? Localization.T(low).Replace("%", "%%") : s.Format;
            if (_focus) { ImGui.SetKeyboardFocusHere(); _focus = false; }
            if (ImGui.SliderFloat($"##{page.Id}.slider", ref v, (float)s.Min, (float)s.Max, format,
                                  ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoInput))
            {
                double d = s.Step > 0 ? Math.Round(v / s.Step) * s.Step : v;
                choice.Value = Math.Clamp(d, s.Min, s.Max);
                _pageTouched[_pageAt] = true;
            }
            if (Step() is var step && step != 0)
            {
                double by = s.Step > 0 ? s.Step : (s.Max - s.Min) / 20.0;
                choice.Value = Math.Clamp(Math.Round(v / by) * by + step * by, s.Min, s.Max);
                _pageTouched[_pageAt] = true;
            }
            if (s.Tip is { } tip && ImGui.IsItemHovered()) ImGui.SetTooltip(Localization.T(tip));
        }
    }

    /// <summary>A choice page's slider value now: its own, else the key's, else its minimum.</summary>
    double ValueOf(SetupSlider slider, SetupPageChoice choice) =>
        choice.Value ?? Onboarding.Level(_spec, slider.Key) ?? slider.Min;

    /// <summary>A slider's value as the summary shows it: its minimum label at the minimum, else its format.</summary>
    string SliderText(SetupSlider slider, double v) =>
        slider.MinLabel is { } low && v <= slider.Min + 1e-6 ? Localization.T(low) : Printf(slider.Format, v);

    /// <summary>A number in an ImGui printf format, the one forms the setup uses: <c>%.2f</c>, <c>%d</c>.</summary>
    static string Printf(string format, double v)
    {
        var m = Regex.Match(format, @"%(?:\.(\d+))?([fd])");
        if (!m.Success) return format;
        string number = m.Groups[2].Value == "d"
            ? Math.Round(v).ToString(CultureInfo.InvariantCulture)
            : v.ToString("F" + (m.Groups[1].Success ? m.Groups[1].Value : "6"), CultureInfo.InvariantCulture);
        return format[..m.Index] + number + format[(m.Index + m.Length)..];
    }

    void Summary()
    {
        Heading("verdite.setup.summary", StepText());
        Dim("verdite.setup.summary.intro");
        ImGui.Spacing();

        ImGui.SeparatorText(Localization.T("verdite.setup.display"));
        for (int i = 0; i < _spec.Display.Count; i++)
            Line(_spec.Display[i].Label, _display[i] >= 0 ? OptionLabel(_spec.Display[i].Options[_display[i]]) : KeepValue(_spec.Display[i]));

        for (int c = 0; c < _spec.Categories.Count; c++)
        {
            var cat = _spec.Categories[c];
            ImGui.SeparatorText(Localization.T(cat.Label));
            for (int g = 0; g < cat.Groups.Count; g++)
                Line(cat.Groups[g].Label, GroupName(cat.Groups[g], _tiers[c][g]));
        }

        for (int p = 0; p < _spec.Pages.Count; p++)
        {
            var page = _spec.Pages[p];
            var choice = _pages[p];
            ImGui.SeparatorText(Localization.T(page.Title));
            Line(page.Label, choice.Option >= 0 && choice.Option < page.Options.Count
                ? OptionLabel(page.Options[choice.Option])
                : Localization.T("verdite.setup.keep"));
            if (page.Slider is { } s) Line(s.Label, SliderText(s, ValueOf(s, choice)));
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
        if (state.Error is not null)
        {
            // A failed build keeps the setup open, so the choices are not lost; the
            // failure popup after it says why.
            ImGui.PushStyleColor(ImGuiCol.Text, new Vector4(1f, 0.38f, 0.38f, 1f));
            ImGui.TextWrapped(Localization.T("verdite.setup.buildfailed"));
            ImGui.PopStyleColor();
            return;
        }
        if (state.Done)
            ImGui.TextUnformatted(Localization.T("verdite.setup.built"));
        else
            ImGui.TextUnformatted($"{Localization.T(state.Status)}{new string('.', 1 + (int)(ImGui.GetTime() * 2.0) % 3)}");
        float fraction = state.Done ? 1f : state.Steps <= 0 ? 0f : Math.Clamp(state.Step / (float)state.Steps, 0f, 1f);
        ImGui.ProgressBar(fraction, new Vector2(-1f, ImGui.GetFrameHeight() * 0.5f), "");
    }

    /// <summary>
    /// The buttons at the foot, at their natural width (at least 120 pixels) and right-aligned
    /// as a group; on the left, the input prompt, dimmed. Waiting has neither. Back is a plain
    /// button; the right-hand one (Next, Finish or Close) is the primary, filled in the accent
    /// colour a lit button takes, its label faked bold as a lit button's is, and unmarked.
    /// </summary>
    void Footer()
    {
        var style = ImGui.GetStyle();
        float s = UiScale;
        float avail = ImGui.GetContentRegionAvail().X;

        (string? left, string? right) = _page switch
        {
            Page.Welcome => (_inGame ? "verdite.setup.cancel" : "verdite.setup.skip", (string?)null),
            Page.Display => ("verdite.setup.back", "verdite.setup.next"),
            Page.Tiers => ("verdite.setup.back", "verdite.setup.next"),
            Page.Choice => ("verdite.setup.back", "verdite.setup.next"),
            Page.Summary => ("verdite.setup.back", "verdite.setup.finish"),
            Page.Report => (null, "verdite.setup.close"),
            _ => (null, null),
        };
        if (left is null && right is null) return;

        float Width(string key) => Math.Max(120f * s, ImGui.CalcTextSize(Localization.T(key)).X + style.FramePadding.X * 2f);
        float leftW = left is null ? 0f : Width(left);
        float rightW = right is null ? 0f : Width(right);
        float group = leftW + (left is not null && right is not null ? style.ItemSpacing.X : 0f) + rightW;

        ImGui.AlignTextToFramePadding();
        ImGui.PushStyleColor(ImGuiCol.Text, style.Colors[(int)ImGuiCol.TextDisabled]);
        ImGui.TextUnformatted(Prompt(right));
        ImGui.PopStyleColor();
        ImGui.SameLine(avail - group);

        if (left is not null)
        {
            if (ImGui.Button(Localization.T(left), new Vector2(leftW, 0f))) Back();
            if (right is not null) ImGui.SameLine();
        }
        if (right is null) return;
        // Finish takes the focus on the summary, and Close on the report.
        if (_focus && (_page == Page.Report || _page == Page.Summary)) { ImGui.SetKeyboardFocusHere(); _focus = false; }
        if (LitButton(Localization.T(right), "footer.forward", true, new Vector2(rightW, 0f), out _, mark: false)) Forward();
    }

    /// <summary>
    /// The input prompt for the page's foot, for the device used last (<see cref="_padActive"/>):
    /// the keyboard's Enter, Escape and PageDown, or the pad's A, B and Start. Enter or A
    /// selects, Escape or B goes back (on the welcome only the button skips), and the last
    /// does the right-hand button (<paramref name="right"/>).
    /// </summary>
    string Prompt(string? right)
    {
        var prompt = _padActive
            ? $"A: {Localization.T("verdite.setup.prompt.select")}"
            : $"Enter: {Localization.T("verdite.setup.prompt.select")}";
        if (_page != Page.Welcome)
            prompt += _padActive
                ? $"    B: {Localization.T("verdite.setup.prompt.back")}"
                : $"    Esc: {Localization.T("verdite.setup.prompt.back")}";
        if (right is not null)
            prompt += _padActive
                ? $"    Start: {Localization.T(right)}"
                : $"    PgDn: {Localization.T(right)}";
        return prompt;
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
            case Page.Display: ToWelcome(); break;
            case Page.Tiers:
                if (_catAt > 0) { _catAt--; Go(Page.Tiers); }
                else Go(Page.Display);
                break;
            case Page.Choice:
                if (_pageAt > 0) { _pageAt--; Go(Page.Choice); }
                else LastTiers();
                break;
            case Page.Summary:
                if (_spec.Pages.Count > 0) { _pageAt = _spec.Pages.Count - 1; Go(Page.Choice); }
                else LastTiers();
                break;
            case Page.Report: Close(); break;
        }
    }

    void ToWelcome() => Go(Page.Welcome);

    /// <summary>Back onto the last category's tiers page, or the display when the spec has none.</summary>
    void LastTiers()
    {
        if (_spec.Categories.Count == 0) { Go(Page.Display); return; }
        _catAt = _spec.Categories.Count - 1;
        Go(Page.Tiers);
    }

    /// <summary>On past the tiers pages: the first choice page, else the summary.</summary>
    void AfterTiers()
    {
        if (_spec.Pages.Count > 0) Enter(0);
        else Go(Page.Summary);
    }

    /// <summary>
    /// Enter a choice page going forward. Untouched by the player, a page with a
    /// <see cref="SetupPage.Follows"/> category takes the preset of the tier that
    /// category is at, when every one of its groups is at the same tier.
    /// </summary>
    void Enter(int at)
    {
        _pageAt = at;
        var page = _spec.Pages[at];
        int c = page.Follows is null || _pageTouched[at] ? -1 : _spec.Categories.FindIndex(k => k.Id == page.Follows);
        if (c >= 0 && _tiers[c].Distinct().Count() == 1 && _tiers[c][0] is var t && t >= 0 && t < page.Presets.Count)
            _pages[at] = page.Presets[t].Clone();
        Go(Page.Choice);
    }

    /// <summary>Change page, and put the keyboard focus on the page's first widget.</summary>
    void Go(Page page)
    {
        _page = page;
        _focus = true;
        // A page's tiers focus is its own: the last frame's button, and any move in flight, belong to the page left.
        _focused = _lastFocused = _moveTo = TierCell.None;
    }

    void Forward()
    {
        switch (_page)
        {
            case Page.Display:
                if (_spec.Categories.Count > 0) { _catAt = 0; Go(Page.Tiers); }
                else AfterTiers();
                break;
            case Page.Tiers:
                if (_catAt + 1 < _spec.Categories.Count) { _catAt++; Go(Page.Tiers); }
                else AfterTiers();
                break;
            case Page.Choice:
                if (_pageAt + 1 < _spec.Pages.Count) Enter(_pageAt + 1);
                else Go(Page.Summary);
                break;
            case Page.Summary: Finish(); break;
            case Page.Report: Close(); break;
        }
    }

    void Finish()
    {
        _result = Onboarding.Apply(_spec, Onboarding.Sets(_spec, _display, _tiers, _pages), _tag);
        Finished = true;
        if (_inGame && _result.NextLaunch.Count > 0) Go(Page.Report);
        else Done();
    }

    /// <summary>
    /// Close, or in the launcher hold on the waiting page until the build is done. A
    /// build that failed closes too: the choices are applied, and the strip said why.
    /// </summary>
    void Done()
    {
        if (Build is { } build && build() is { Done: false, Error: null }) Go(Page.Waiting);
        else Close();
    }

    // ---- Pieces ----

    /// <summary>
    /// A field as a slider whose positions are its options, as the Settings window's
    /// frame rate is: the format is the option's label, a literal, so no index ever
    /// shows and typed entry is off. When <paramref name="keep"/>, the slider's first
    /// position, one below the options, is keep current, labelled with the value it
    /// keeps.
    /// </summary>
    void Slider(SetupField field, ref int index, bool keep)
    {
        int last = field.Options.Count - 1;
        int lo = keep ? -1 : 0;
        index = Math.Clamp(index, lo, last);
        string label = index >= 0 ? OptionLabel(field.Options[index]) : KeepValue(field);
        int at = index;
        if (ImGui.SliderInt($"##{field.Id}", ref at, lo, last, label.Replace("%", "%%"),
                            ImGuiSliderFlags.AlwaysClamp | ImGuiSliderFlags.NoInput))
            index = at;
        index = Math.Clamp(index + Step(), lo, last);
    }

    /// <summary>
    /// The tiers as buttons, one per run of identical tiers (<paramref name="span"/>, see
    /// <see cref="Spans"/>), each run's names joined and the lit one marked; the run's
    /// description wrapped under its button, and the higher-GPU-cost note after it when
    /// the run's first tier says so. A keep button comes first when
    /// <paramref name="keep"/>. Every cell sits on the page's <paramref name="grid"/>
    /// columns, not on the widths of its own row: a run's button spans from its first
    /// tier's column to its last, and its description takes the same left edge and wrap
    /// width. A description is dimmed unless its button is lit or has the focus. <paramref name="row"/>
    /// is the group's index, and <paramref name="top"/> the screen Y of its header, for the focus bar.
    /// Returns the run's first tier when one is clicked, -1
    /// for keep, or null when nothing was.
    /// </summary>
    int? TierRow(int row, string id, int current, IReadOnlyList<SetupTier> tiers, int[] span, bool keep, TierGrid grid, float top)
    {
        var style = ImGui.GetStyle();
        var starts = Enumerable.Range(0, span.Length).Where(t => span[t] == t).ToList();
        int? pick = null;
        var cells = new List<(Track At, int Start, bool Lit, bool Focused)>();
        bool placed = false;
        // A cell goes on its column: SameLine after the one before it, then the cursor to its X.
        void Place(Track at)
        {
            if (placed) ImGui.SameLine();
            placed = true;
            ImGui.SetCursorPosX(at.X);
        }

        if (keep)
        {
            Place(grid.Keep);
            var cell = new TierCell(row, -1, 0);
            Aim(cell);
            if (LitButton(Localization.T("verdite.setup.keep"), $"{id}keep", current < 0, new Vector2(grid.Keep.W, 0f), out bool kept)) pick = -1;
            if (kept) _focused = cell;
        }
        foreach (int s in starts)
        {
            int count = span.Count(x => x == s);
            var at = new Track(grid.Tiers[s].X, grid.Tiers[s + count - 1].Right - grid.Tiers[s].X);
            bool lit = current >= 0 && current < span.Length && span[current] == s;
            var cell = new TierCell(row, s, count);
            Place(at);
            Aim(cell);
            var names = string.Join(" / ", Enumerable.Range(s, count).Select(t => Localization.T(TierKeys[t])));
            if (LitButton(names, $"{id}{s}", lit, new Vector2(at.W, 0f), out bool focused)) pick = s;
            if (focused) _focused = cell;
            cells.Add((at, s, lit, focused));
        }

        float bottom = top;
        for (int k = 0; k < cells.Count; k++)
        {
            var (at, s, lit, focused) = cells[k];
            if (k > 0) ImGui.SameLine(at.X);
            else ImGui.SetCursorPosX(at.X);
            ImGui.BeginGroup();
            ImGui.PushTextWrapPos(ImGui.GetCursorPosX() + at.W);
            bool dimmed = !lit && !focused;
            if (dimmed) ImGui.PushStyleColor(ImGuiCol.Text, style.Colors[(int)ImGuiCol.TextDisabled]);
            ImGui.TextWrapped(s < tiers.Count ? Localization.T(tiers[s].Desc) : "");
            if (dimmed) ImGui.PopStyleColor();
            if (s < tiers.Count && tiers[s].Cost)
            {
                ImGui.PushStyleColor(ImGuiCol.Text, style.Colors[(int)ImGuiCol.TextDisabled]);
                ImGui.TextWrapped(Localization.T("verdite.setup.gpucost"));
                ImGui.PopStyleColor();
            }
            ImGui.PopTextWrapPos();
            ImGui.EndGroup();
            bottom = Math.Max(bottom, ImGui.GetItemRectMax().Y);
        }

        if (current < 0)
        {
            Dim("verdite.setup.custom");
            bottom = Math.Max(bottom, ImGui.GetItemRectMax().Y);
        }
        if (_focused.Row == row) GutterBar(top, bottom);
        return pick;
    }

    /// <summary>A column of a tiers page: its window-local left edge and its width, in whole pixels.</summary>
    readonly record struct Track(float X, float W)
    {
        public float Right => X + W;
    }

    /// <summary>
    /// The columns of a tiers page, shared by every row of it: the keep column (valid when
    /// any group on the page has a keep button) and one column per tier.
    /// </summary>
    readonly record struct TierGrid(Track Keep, Track[] Tiers);

    /// <summary>
    /// The page's columns: from <paramref name="x0"/> (window-local), a keep column of two
    /// thirds of a unit when <paramref name="keep"/>, then three equal tier columns, with
    /// the item spacing between. Cut once per page, so a button in one row lines up with
    /// the same button in every other row. Each edge is floored to a whole pixel, and a
    /// merged button is the edge of its first column to the edge of its last, so the
    /// shared edges agree to the pixel.
    /// </summary>
    static TierGrid MakeTierGrid(float x0, float avail, float gap, bool keep)
    {
        float keepUnits = keep ? 2f / 3f : 0f;
        int gaps = TierCount + (keep ? 1 : 0) - 1;
        float unit = (avail - gap * gaps) / (TierCount + keepUnits);
        float x = x0;
        var keepCol = new Track(0f, 0f);
        if (keep)
        {
            keepCol = Snap(x, unit * keepUnits);
            x += unit * keepUnits + gap;
        }
        var tiers = new Track[TierCount];
        for (int t = 0; t < TierCount; t++)
        {
            tiers[t] = Snap(x, unit);
            x += unit + gap;
        }
        return new TierGrid(keepCol, tiers);
    }

    /// <summary>A column from a window-local left edge and a width, with both edges floored to whole pixels.</summary>
    static Track Snap(float x, float w)
    {
        float left = MathF.Floor(x), right = MathF.Floor(x + w);
        return new Track(left, right - left);
    }

    /// <summary>
    /// For each tier of a group, the index of the first tier of its run of identical
    /// tiers (the same keys, every value the same), so a run shows as one button.
    /// </summary>
    static int[] Spans(SetupGroup group)
    {
        var span = new int[group.Tiers.Count];
        for (int t = 0; t < span.Length; t++)
            span[t] = t > 0 && SameSet(group.Tiers[t - 1], group.Tiers[t]) ? span[t - 1] : t;
        return span;
    }

    static bool SameSet(SetupTier a, SetupTier b) =>
        a.Set.Count == b.Set.Count
        && a.Set.All(kv => b.Set.TryGetValue(kv.Key, out var v) && Onboarding.SameText(kv.Value, v));

    /// <summary>
    /// A button, lit in the active colour when <paramref name="lit"/> and marked "> " so the state is not colour alone; true when clicked.
    /// The button has no label of its own: its text is drawn over it, twice a pixel apart
    /// as the headings are, so the labels read as bolder than the descriptions under them.
    /// The size is sized as ImGui sizes a button: a positive size is exact, and a zero or
    /// negative width is the room left on the row less its magnitude (the Choice page's -1).
    /// The focus is ImGui's own outline in the navigation colour, as the welcome's cards
    /// show it, and an unlit button with it takes the hovered fill; the lit fill and "> "
    /// stay the choice's alone. An inset border in the text colour was tried inside that
    /// outline and drew two frames round one button. The overload with
    /// <paramref name="focused"/> says whether it has the focus.
    /// <paramref name="mark"/> false drops the "> " of a lit button, for a button that is lit
    /// by its place rather than by a choice (the foot's Next).
    /// </summary>
    /// <summary>The button with the focus this frame, and last frame's, for <see cref="LitButton"/>'s hovered fill.</summary>
    static uint _focusId, _lastFocusId;

    static bool LitButton(string label, string id, bool lit, Vector2 size) =>
        LitButton(label, id, lit, size, out _);

    static bool LitButton(string label, string id, bool lit, Vector2 size, out bool focused, bool mark = true)
    {
        var style = ImGui.GetStyle();
        if (lit)
        {
            ImGui.PushStyleColor(ImGuiCol.Button, style.Colors[(int)ImGuiCol.ButtonActive]);
            ImGui.PushStyleColor(ImGuiCol.ButtonHovered, style.Colors[(int)ImGuiCol.ButtonActive]);
        }
        float w = size.X > 0f ? size.X : Math.Max(4f, ImGui.GetContentRegionAvail().X + size.X);
        float h = size.Y > 0f ? size.Y : ImGui.GetFrameHeight();
        // Whether this button has the focus is known only once it is drawn, so the hovered
        // fill follows the focus a frame late, as ImGui's own hover does.
        bool lift = !lit && ImGui.GetID($"##{id}") == _lastFocusId;
        if (lift) ImGui.PushStyleColor(ImGuiCol.Button, style.Colors[(int)ImGuiCol.ButtonHovered]);
        bool clicked = ImGui.Button($"##{id}", new Vector2(w, h));
        focused = ImGui.IsItemFocused();
        if (focused) _focusId = ImGui.GetItemID();
        if (lift) ImGui.PopStyleColor();
        if (lit) ImGui.PopStyleColor(2);

        string text = lit && mark ? $"> {label}" : label;
        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        var at = min + (max - min - ImGui.CalcTextSize(text)) * 0.5f;
        var draw = ImGui.GetWindowDrawList();
        uint ink = ImGui.ColorConvertFloat4ToU32(style.Colors[(int)ImGuiCol.Text]);
        draw.AddText(at, ink, text);
        draw.AddText(at + new Vector2(UiScale, 0f), ink, text);
        return clicked;
    }

    /// <summary>The text a display field shows when it keeps its current value.</summary>
    string KeepValue(SetupField field) => Localization.T("verdite.setup.keepvalue", Onboarding.Current(_spec, field) ?? "?");

    /// <summary>A group's tier by name: the names of its run when the tier is in one, "Keep as is" when it is set by hand.</summary>
    static string GroupName(SetupGroup group, int t)
    {
        if (t < 0 || t >= group.Tiers.Count) return Localization.T("verdite.setup.keep");
        var span = Spans(group);
        return string.Join(" / ", Enumerable.Range(0, span.Length).Where(x => span[x] == span[t])
            .Select(x => Localization.T(TierKeys[x])));
    }

    /// <summary>An option's label: a localisation key (they all have a dot), or a
    /// plain one ("60", "16:9") shown as it is, which the runtime would warn about.</summary>
    static string OptionLabel(SetupOption o) => OptionLabel(o.Label, o.Set);

    static string OptionLabel(SetupPageOption o) => OptionLabel(o.Label, o.Set);

    static string OptionLabel(string label, Dictionary<string, string?> set)
    {
        if (!label.Contains('.')) return label;
        return set.Values.Any(v => v == "$refresh") ? Localization.T(label, Onboarding.Refresh()) : Localization.T(label);
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

    /// <summary>
    /// A page's heading: 1.5x, faked bold (drawn twice, a pixel apart), over its separator.
    /// When <paramref name="right"/> is given (the step text), it is drawn at the text size,
    /// dimmed, right-aligned and vertically centred on the heading's line.
    /// </summary>
    static void Heading(string key, string? right = null)
    {
        var draw = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        var at = ImGui.GetCursorScreenPos();
        uint ink = ImGui.ColorConvertFloat4ToU32(style.Colors[(int)ImGuiCol.Text]);
        string text = Localization.T(key);
        float size = ImGui.GetFontSize() * HeadingScale;
        draw.AddText(ImGui.GetFont(), size, at, ink, text);
        draw.AddText(ImGui.GetFont(), size, at + new Vector2(UiScale, 0f), ink, text);
        if (right is not null)
        {
            uint dim = ImGui.ColorConvertFloat4ToU32(style.Colors[(int)ImGuiCol.TextDisabled]);
            float lineH = ImGui.GetTextLineHeight();
            float w = ImGui.CalcTextSize(right).X;
            // The heading's line is HeadingScale lines tall; the step's is one, so it sits a quarter line down.
            float x = at.X + ImGui.GetContentRegionAvail().X - w;
            float y = at.Y + (HeadingScale - 1f) * 0.5f * lineH;
            draw.AddText(ImGui.GetFont(), ImGui.GetFontSize(), new Vector2(x, y), dim, right);
        }
        ImGui.Dummy(new Vector2(0f, ImGui.GetTextLineHeight() * HeadingScale));
        ImGui.Separator();
        ImGui.Spacing();
    }

    /// <summary>
    /// The "Step X of N" a page shows on its heading: the pages after the welcome, the
    /// display as 1, each category's tiers page, each choice page, the summary as N.
    /// Null on the welcome, the waiting page and the report, which have none.
    /// </summary>
    string? StepText()
    {
        int n = 2 + _spec.Categories.Count + _spec.Pages.Count;
        int step = _page switch
        {
            Page.Display => 1,
            Page.Tiers => 2 + _catAt,
            Page.Choice => 2 + _spec.Categories.Count + _pageAt,
            Page.Summary => n,
            _ => 0,
        };
        return step == 0 ? null : Localization.T("verdite.setup.step", step, n);
    }

    /// <summary>
    /// A tiers group's header: a short rule, the name at <see cref="GroupScale"/> (faked
    /// bold, as <see cref="Heading"/> is), then a rule to the content's right edge, all
    /// centred on the text's line. It replaces ImGui's SeparatorText, which drew the name at
    /// the body size, where a group's name was lost among its descriptions.
    /// </summary>
    static void GroupHeader(string key)
    {
        var draw = ImGui.GetWindowDrawList();
        var style = ImGui.GetStyle();
        var at = ImGui.GetCursorScreenPos();
        uint rule = ImGui.ColorConvertFloat4ToU32(style.Colors[(int)ImGuiCol.Separator]);
        uint ink = ImGui.ColorConvertFloat4ToU32(style.Colors[(int)ImGuiCol.Text]);
        string text = Localization.T(key);
        float size = ImGui.GetFontSize() * GroupScale;
        float h = ImGui.GetTextLineHeight() * GroupScale;
        float mid = at.Y + h * 0.5f;
        float dash = 16f * UiScale, gap = 8f * UiScale;
        float textX = at.X + dash + gap;
        float textEnd = textX + ImGui.CalcTextSize(text).X * GroupScale;
        float right = at.X + ImGui.GetContentRegionAvail().X;

        draw.AddLine(new Vector2(at.X, mid), new Vector2(textX - gap, mid), rule, UiScale);
        draw.AddText(ImGui.GetFont(), size, new Vector2(textX, at.Y), ink, text);
        draw.AddText(ImGui.GetFont(), size, new Vector2(textX + UiScale, at.Y), ink, text);
        draw.AddLine(new Vector2(textEnd + gap, mid), new Vector2(right, mid), rule, UiScale);
        ImGui.Dummy(new Vector2(0f, h));
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
    /// directions are its own 102-105), to ImGui's gamepad keys. Square/X is left out:
    /// held, it is ImGui's window switcher, which takes the focus off the setup.</summary>
    static readonly (int Button, ImGuiKey Key)[] PadKeys =
    [
        (0, ImGuiKey.GamepadFaceDown), (1, ImGuiKey.GamepadFaceRight),
        (3, ImGuiKey.GamepadFaceUp), (6, ImGuiKey.GamepadStart),
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

    /// <summary>
    /// Feeds the pad's buttons to ImGui's navigation, and marks the pad as the device in use
    /// when one goes down. Runs after <see cref="ReadDevice"/>, so a pad press wins a frame
    /// in which both came.
    /// </summary>
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
            if (down) _padActive = true;
        }
    }

    /// <summary>
    /// Clears the pad as the device in use when the keyboard or the mouse acted this frame:
    /// a key from Tab to the keypad's equals (the gamepad keys are not in that run, so a pad
    /// never clears it), a mouse button clicked, or the mouse moved.
    /// </summary>
    void ReadDevice()
    {
        var io = ImGui.GetIO();
        bool mouse = io.MouseDelta.LengthSquared() > 0f
            || Enumerable.Range(0, 3).Any(b => ImGui.IsMouseClicked((ImGuiMouseButton)b));
        bool keys = Enumerable.Range((int)ImGuiKey.Tab, (int)ImGuiKey.KeypadEqual - (int)ImGuiKey.Tab + 1)
            .Any(k => ImGui.IsKeyPressed((ImGuiKey)k, false));
        if (mouse || keys) _padActive = false;
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
        "verdite.setup.welcome.start": {
          "en": "Choose a starting point. Every choice it makes is shown on the pages that follow, where you can change any of them.",
          "pt-BR": "Escolha um ponto de partida. Cada escolha dele aparece nas páginas seguintes, onde você pode mudar qualquer uma.",
          "es-419": "Elige un punto de partida. Cada opción que hace se muestra en las páginas siguientes, donde puedes cambiar cualquiera."
        },
        "verdite.setup.welcome.building": {
          "en": "The game is being built from your disc in the meantime. It starts once both are done.",
          "pt-BR": "Enquanto isso, o jogo está sendo construído a partir do seu disco. Ele inicia quando as duas coisas terminarem.",
          "es-419": "Mientras tanto, el juego se está construyendo desde tu disco. Empieza cuando ambas cosas terminen."
        },
        "verdite.setup.recommended": { "en": "Recommended", "pt-BR": "Recomendado", "es-419": "Recomendado" },
        "verdite.setup.recommended.desc": {
          "en": "Every enhancement the port adds: smoother frame rate, sharper textures and lighting, twin-stick controls, and better sound.",
          "pt-BR": "Todas as melhorias que o port acrescenta: taxa de quadros mais suave, texturas e iluminação mais nítidas, controles de dois analógicos e um som melhor.",
          "es-419": "Todas las mejoras que añade el port: una tasa de cuadros más fluida, texturas e iluminación más nítidas, controles de dos sticks y un mejor sonido."
        },
        "verdite.setup.originalplus": { "en": "Balanced", "pt-BR": "Equilibrado", "es-419": "Equilibrado" },
        "verdite.setup.originalplus.desc": {
          "en": "The PlayStation's look and feel, with its rendering artifacts (texture warping, vertex jitter) corrected, analog controls and enhanced sound.",
          "pt-BR": "A aparência e a sensação do PlayStation, com os artefatos de renderização (distorção de texturas, tremor de vértices) corrigidos, controles analógicos e som aprimorado.",
          "es-419": "El aspecto y la sensación de la PlayStation, con sus artefactos de renderizado (distorsión de texturas, temblor de vértices) corregidos, controles analógicos y sonido mejorado."
        },
        "verdite.setup.original": { "en": "Original", "pt-BR": "Original", "es-419": "Original" },
        "verdite.setup.original.desc": {
          "en": "The game as the PlayStation ran it: its picture, D-pad controls and sound.",
          "pt-BR": "O jogo como o PlayStation o rodava: a imagem, os controles do direcional e o som.",
          "es-419": "El juego tal como lo ejecutaba la PlayStation: su imagen, los controles de cruceta y el sonido."
        },
        "verdite.setup.customise": { "en": "Custom", "pt-BR": "Personalizado", "es-419": "Personalizado" },
        "verdite.setup.customise.desc": {
          "en": "Start from your current settings.",
          "pt-BR": "Comece pelas suas configurações atuais.",
          "es-419": "Empieza desde tu configuración actual."
        },
        "verdite.setup.again.title": { "en": "Run Setup Again", "pt-BR": "Refazer configuração", "es-419": "Repetir configuración" },
        "verdite.setup.again": {
          "en": "Choose again how the game should look, sound and control. Your current settings are where each page starts.",
          "pt-BR": "Escolha de novo como o jogo deve parecer, soar e ser controlado. Suas configurações atuais são o ponto de partida de cada página.",
          "es-419": "Elige de nuevo cómo debe verse, sonar y controlarse el juego. Tu configuración actual es el punto de partida de cada página."
        },
        "verdite.setup.prompt.select": { "en": "Select", "pt-BR": "Selecionar", "es-419": "Seleccionar" },
        "verdite.setup.prompt.back": { "en": "Back", "pt-BR": "Voltar", "es-419": "Atrás" },
        "verdite.setup.buildfailed": {
          "en": "The build failed. Finish setup to keep your choices; the reason follows.",
          "pt-BR": "A construção falhou. Conclua a configuração para manter suas escolhas; o motivo vem em seguida.",
          "es-419": "La construcción falló. Finaliza la configuración para conservar tus opciones; el motivo viene a continuación."
        },
        "verdite.setup.tiers.intro": {
          "en": "Original is the game as the PlayStation ran it. Balanced keeps that look and feel and fixes what the hardware got wrong. Modern adds the port's enhancements. Where two tiers look the same, they share a button.",
          "pt-BR": "Original é o jogo como o PlayStation o rodava. Equilibrado mantém essa aparência e sensação e corrige o que o hardware fazia errado. Moderno acrescenta as melhorias do port. Quando dois níveis parecem iguais, eles compartilham um botão.",
          "es-419": "Original es el juego tal como lo ejecutaba la PlayStation. Equilibrado conserva ese aspecto y sensación y corrige lo que el hardware hacía mal. Moderno agrega las mejoras del port. Cuando dos niveles se ven igual, comparten un botón."
        },
        "verdite.setup.gpucost": {
          "en": "Higher GPU cost.",
          "pt-BR": "Maior custo de GPU.",
          "es-419": "Mayor costo de GPU."
        },
        "verdite.setup.tier.original": { "en": "Original", "pt-BR": "Original", "es-419": "Original" },
        "verdite.setup.tier.originalplus": { "en": "Balanced", "pt-BR": "Equilibrado", "es-419": "Equilibrado" },
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
        "verdite.setup.keepvalue": {
          "en": "Keep current ({0})",
          "pt-BR": "Manter atual ({0})",
          "es-419": "Mantener actual ({0})"
        },
        "verdite.setup.keep": { "en": "Keep as is", "pt-BR": "Manter como está", "es-419": "Dejar como está" },
        "verdite.setup.custom": {
          "en": "Custom: set by hand. Choose a style to replace it, or keep it as it is.",
          "pt-BR": "Personalizado: definido à mão. Escolha um estilo para substituí-lo, ou mantenha como está.",
          "es-419": "Personalizado: definido a mano. Elige un estilo para reemplazarlo, o déjalo como está."
        },
        "verdite.setup.mixed": { "en": "Custom", "pt-BR": "Personalizado", "es-419": "Personalizado" },
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
        "verdite.setup.step": {
          "en": "Step {0} of {1}",
          "pt-BR": "Etapa {0} de {1}",
          "es-419": "Paso {0} de {1}"
        },
        "verdite.setup.skip": { "en": "Skip setup", "pt-BR": "Pular configuração", "es-419": "Omitir configuración" },
        "verdite.setup.cancel": { "en": "Cancel", "pt-BR": "Cancelar", "es-419": "Cancelar" },
        "verdite.setup.back": { "en": "Back", "pt-BR": "Voltar", "es-419": "Atrás" },
        "verdite.setup.next": { "en": "Next", "pt-BR": "Avançar", "es-419": "Siguiente" },
        "verdite.setup.finish": { "en": "Finish", "pt-BR": "Concluir", "es-419": "Finalizar" },
        "verdite.setup.close": { "en": "Close", "pt-BR": "Fechar", "es-419": "Cerrar" }
      }
    }
    """;
}
