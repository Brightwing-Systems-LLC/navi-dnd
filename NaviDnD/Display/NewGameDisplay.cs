using NaviDnD.Clients;
using NaviDnD.Data;
using NaviDnD.Data.Models;
using NaviDnD.Helpers;

namespace NaviDnD.Display;

// New game form in the menu style: fields on the left (↑↓ to any field, Enter to edit, ←→ to flip through
// color and portrait), a live hero preview on the right (portrait in their color, name, symbol as on the map).
// Text fields are filled in sequence: Enter saves the field and immediately opens the next one (the UI tests
// fill them the same way). Race and class flip through the ruleset's list (Prompts/{RuleSet}/heroOptions.json): changing them picks a portrait
// to match, if there is one. Portraits are a ready-made set; below it, «CHOOSE FILE…» (a Windows dialog; PNG/JPG/BMP/GIF/SVG
// is copied into Storage/Portraits).
public class NewGameDisplay
{
    private enum Kind { Text, Color, Portrait, Actions, Button, Choice, AiButton, RandomButton }

    private sealed class Field(string label, Kind kind, int maxLength = 0, string? fixedValue = null)
    {
        public string Label = label;
        public Kind Kind = kind;
        public int MaxLength = maxLength;
        public string Value = fixedValue ?? "";
        public bool ReadOnly = fixedValue != null; // a fixed value: focus and input skip it
        public List<string> Options = [];           // Choice: options (←→), the selected one is Index
        public int Index;
    }



    private sealed record Portrait(string Image, string Title);

    private static (string Name, List<int> Rgb)[] Palette =>
    [
        (L.T("AMBER"), [220, 170, 60]), (L.T("SCARLET"), [210, 70, 60]), (L.T("EMERALD"), [70, 190, 110]),
        (L.T("AZURE"), [80, 150, 230]), (L.T("AMETHYST"), [160, 105, 220]), (L.T("TURQUOISE"), [70, 200, 200]),
        (L.T("COPPER"), [205, 125, 75]), (L.T("SILVER"), [190, 195, 205]), (L.T("GOLD"), [240, 210, 90]),
        (L.T("PINK"), [230, 120, 170]), (L.T("FOREST"), [120, 175, 70]), (L.T("ASHEN"), [155, 145, 135]),
    ];

    // Ready-made portrait set (game-icons), flipped through after the ones picked by the model.
    private static Portrait[] Gallery =>
    [
        new("delapouite/wizard-face", L.T("MAGE")), new("delapouite/woman-elf-face", L.T("ELF WOMAN")),
        new("delapouite/dwarf-face", L.T("DWARF")), new("delapouite/barbarian", L.T("BARBARIAN")),
        new("delapouite/monk-face", L.T("MONK")), new("delapouite/bandit", L.T("BANDIT")),
        new("cathelineau/witch-face", L.T("WITCH")), new("cathelineau/nun-face", L.T("PRIESTESS")),
        new("darkzaitzev/ninja-head", L.T("NINJA")), new("darkzaitzev/hooded-assassin", L.T("ASSASSIN")),
        new("darkzaitzev/hooded-figure", L.T("WANDERER")), new("lorc/cowled", L.T("HERMIT")),
        new("lorc/visored-helm", L.T("KNIGHT")), new("delapouite/closed-barbute", L.T("WARRIOR")),
        new("delapouite/black-knight-helm", L.T("BLACK KNIGHT")), new("delapouite/overlord-helm", L.T("OVERLORD")),
        new("delapouite/viking-helmet", L.T("VIKING")), new("delapouite/spartan-helmet", L.T("HOPLITE")),
        new("delapouite/samurai-helmet", L.T("SAMURAI")), new("kier-heyl/elf-helmet", L.T("ELF WARRIOR")),
        new("kier-heyl/dwarf-helmet", L.T("DWARF WARRIOR")), new("kier-heyl/dwarf-king", L.T("DWARF KING")),
        new("delapouite/orc-head", L.T("ORC")), new("delapouite/goblin-head", L.T("GOBLIN")),
        new("delapouite/kenku-head", L.T("KENKU")), new("lorc/lizardman", L.T("LIZARDFOLK")),
        new("faithtoken/dragon-head", L.T("DRAGONBORN")), new("delapouite/vampire-dracula", L.T("VAMPIRE")),
        new("lorc/werewolf", L.T("WEREWOLF")), new("lorc/cultist", L.T("CULTIST")),
        new("delapouite/plague-doctor-profile", L.T("PLAGUE DOCTOR")), new("delapouite/fairy", L.T("FAIRY")),
        new("delapouite/archer", L.T("ARCHER")), new("cathelineau/swordman", L.T("SWORDSMAN")),
    ];

    private static string[] PortraitActions => [L.T("CHOOSE FILE…")];

    // Ruleset races and classes with a matching portrait (gallery slug): Prompts/{RuleSet}/heroOptions.json.
    private sealed record HeroOption(string Name, string? Portrait);
    private readonly Dictionary<string, string?> _optionPortraits = [];
    private readonly List<string> _names = [];   // names for «RANDOM»
    private static readonly Random _rnd = new();

    private void LoadHeroOptions(string ruleSet)
    {
        try
        {
            string path = Path.Combine(AppConfig.ProjectRoot, "Prompts", ruleSet, "heroOptions.json");
            var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path));
            foreach (var (key, field) in new[] { ("races", RaceField), ("classes", ClassField), ("levels", LevelField), ("alignments", AlignmentField),
                         ("backgrounds", BackgroundField) })
                foreach (var o in root?[key]?.AsArray() ?? [])
                {
                    // An option is a string («1») or an object with a portrait ({"name","portrait"}).
                    bool plain = o is System.Text.Json.Nodes.JsonValue;
                    // Options are English (translation keys); in the form and for the DM they're in the world's language.
                    string name = L.W(plain ? o!.ToString() : (string?)o?["name"] ?? "");
                    if (name.Length == 0) continue;
                    _fields[field].Options.Add(name);
                    if (!plain) _optionPortraits[name] = (string?)o?["portrait"];
                }
            foreach (var n in root?[L.WorldIsEnglish ? "namesEn" : "names"]?.AsArray() ?? [])
                if (n?.ToString() is { Length: > 0 } s) _names.Add(s);
        }
        catch { /* no list: the choosers stay empty */ }
        foreach (int f in ChoiceFields)
        {
            if (_fields[f].Options.Count == 0) _fields[f].Options.Add("—");
            _fields[f].Value = _fields[f].Options[0];
        }
    }

    // «RANDOM»: random name (the symbol is its first letters), race, class, level, alignment, background and color;
    // the portrait matches them.
    private void Randomize()
    {
        Sound.PlayClick();
        if (_fields[RandomField].ReadOnly) return;
        if (_editing) StopEditing();
        ArchiveNewHero();
        if (_names.Count > 0)
        {
            string name = _names[_rnd.Next(_names.Count)];
            _fields[NameField].Value = name;
            _fields[SymbolField].Value = name.ToUpperInvariant().PadRight(3, name.ToUpperInvariant()[^1])[..3];
        }
        foreach (int f in ChoiceFields)
        {
            var field = _fields[f];
            field.Index = _rnd.Next(field.Options.Count);
            field.Value = field.Options[field.Index];
        }
        _color = _rnd.Next(Palette.Length);
        PortraitFor(ClassField);
    }

    // Select a chooser option by value (not in the list: add it; a hero's race/class from the library).
    private void SetChoice(int field, string value)
    {
        var f = _fields[field];
        if (value.Length == 0) value = f.Options[0];
        int i = f.Options.IndexOf(value);
        if (i < 0) { f.Options.Add(value); i = f.Options.Count - 1; }
        f.Index = i;
        f.Value = value;
    }

    // Race/class changed: pick a matching portrait from the gallery (a custom picture from a file is left alone). If the selected one has
    // no match, go by the other field.
    private void PortraitFor(int changed)
    {
        if (_portraits[_portrait].Image.StartsWith(SvgToBrailleConverter.PortraitsPrefix)) return;
        int other = changed == RaceField ? ClassField : RaceField;
        foreach (int f in new[] { changed, other })
            if (_optionPortraits.GetValueOrDefault(_fields[f].Value) is { } slug && _portraits.FindIndex(p => p.Image == slug) is int pi and >= 0)
            {
                _portrait = pi;
                return;
            }
    }
    private const int PortraitWidth = 36; // as in the game (SvgToBrailleConverter default): 36 × 18

    private readonly WorldState _settings;
    private readonly DisplayConfig _display;
    private readonly ScreenConfig _screen;
    private readonly GameAiClient _aiClient;
    private readonly BorderDrawer _borderDrawer;
    private int _startTop;

    // New game steps: which ones you can go to (F1–F4 / click in the title). The form persists between visits to the step.
    public Func<int, bool> CanGo = _ => false;
    public NewGameData? Data { get; private set; }
    // The hero created by the model on the last «NEXT» (Program saves their sheet to the hero library).
    public bool CreatedNow { get; set; }
    // Race/class/description/hero choice the hero was created with: if they changed, recreate (with a warning);
    // if not, name, symbol, color and portrait just carry over into the hero.
    private string? _createdKey;
    private bool _recreateWarned;
    private int? _gotoStep;
    private string? _tabHovered;

    private readonly List<Field> _fields =
    [
        new(L.T("NAME##person"), Kind.Text, 30), new(L.T("SYMBOL"), Kind.Text, 3),
        new(L.T("RACE"), Kind.Choice), new(L.T("CLASS"), Kind.Choice),
        new(L.T("DESCRIPTION"), Kind.Text, 1500),
        new(L.T("COLOR"), Kind.Color), new(L.T("PORTRAIT"), Kind.Portrait), new("", Kind.Actions),
        new(L.T("HERO"), Kind.Choice),
        new(L.T("REQUEST"), Kind.Text, 300), new(L.T("INVENT"), Kind.AiButton),
        new(L.T("LEVEL"), Kind.Choice), new(L.T("ALIGNMENT"), Kind.Choice),
        new(L.T("RANDOM"), Kind.RandomButton),
        new(L.T("BACKGROUND"), Kind.Choice),
        new(L.T("NEXT"), Kind.Button),
    ];
    private const int NameField = 0, SymbolField = 1, RaceField = 2, ClassField = 3, DescriptionField = 4,
        ColorField = 5, HeroField = 8, PromptField = 9, AiField = 10, LevelField = 11, AlignmentField = 12, RandomField = 13,
        BackgroundField = 14;
    private const string Setting = "DnD 5e";   // only one ruleset for now (not shown in the form)

    // Field order on screen (↑↓): HERO at the top, the button at the bottom (in the _fields list the button is last).
    // The hero description is the bottom block (like the world description): text, a request to the model, INVENT/CHANGE.
    private static readonly int[] Order = [HeroField, 0, 1, 2, 3, LevelField, AlignmentField, BackgroundField, 5, 6, 7, RandomField,
        DescriptionField, PromptField, AiField, 15];

    // HERO (like WORLD on the «World» step): «new hero» (first), deferred options from this game creation (Draft: ready,
    // read-only; only the one you moved on with goes into the library) and library heroes (Card).
    private sealed record HeroDraft(string[] Values, int Color, string Image);
    private sealed record HeroChoice(HeroLibrary.Card? Card = null, HeroDraft? Draft = null);
    private readonly List<HeroChoice> _heroes = [new()];

    private int _focus;
    private bool _editing;
    private int _color;
    private readonly List<Portrait> _portraits = [.. Gallery];
    private int _portrait;
    private int _action;             // the selected button in the row under the portrait
    private string _message = "";
    private readonly List<(int row, int x, int w)> _actionPos = []; // buttons under the portrait (for the mouse)

    // Layout
    private int _width, _height, _x0, _bodyTop;
    private int _buttonRow, _buttonX, _buttonWidth, _formLeft;
    private readonly List<(int row, int x)> _swatches = [];      // palette swatches (for clicks)
    private char[,] _chars = new char[0, 0];
    private List<int>?[,] _fg = new List<int>?[0, 0];
    private List<int>?[,] _bg = new List<int>?[0, 0];
    private readonly List<(int row, int field)> _fieldRows = []; // body rows taken by a field (for the mouse)
    private (int row, int left, int right)? _arrows;              // ◄ ► on the focused field (color/portrait)

    // Centered layout: a «FORM» frame on the left, a «HERO» frame on the right with the same height.
    private const int FormWidth = 56, PreviewWidth = 40, Gap = 3, BoxInner = 22;
    private const int DescHeight = 13;                        // the «HERO DESCRIPTION» block
    private const int LabelCol = 3, ValueCol = 18;           // from the left edge of the form frame
    private const int MultiLines = 3;                         // lines for description/wishes

    private List<int> Fg => _display.MainForeground;
    private List<int> Bright => ColorHelper.Pale(_display.MainForeground, 0.75);
    private List<int> Dim => ColorHelper.Darker(_display.MainForeground, 0.5);

    // The world language the form was built in (options, names): if the world changed to another language, the form is recreated.
    public string Language { get; } = L.World;

    public NewGameDisplay(WorldState settings, DisplayConfig display, ScreenConfig screen, GameAiClient aiClient)
    {
        _aiClient = aiClient;
        _screen = screen;
        _settings = settings;
        _display = display;
        _borderDrawer = new BorderDrawer(settings, display);
        _startTop = Term.CursorTop;
        LoadHeroOptions(aiClient.RuleSet);
        PortraitFor(RaceField);
        _fields[HeroField].Options.Add(L.T("new hero"));
        foreach (var h in HeroLibrary.All())
        {
            _heroes.Add(new(h));
            _fields[HeroField].Options.Add(h.Name);
        }
    }

    // A HERO option was chosen: a library persona goes into the fields; «new» starts the fields from scratch.
    // A library hero has a fixed race and class (level and features carry over as is).
    // What was entered for a new hero is kept while you flip through library heroes (go back to «new» and it's all as it was).
    private static readonly int[] PersonalFields = [NameField, SymbolField, RaceField, ClassField, LevelField, AlignmentField, BackgroundField, DescriptionField];
    private static readonly int[] ChoiceFields = [RaceField, ClassField, LevelField, AlignmentField, BackgroundField];
    private HeroDraft? _newHeroDraft;
    private string _newHeroPrompt = "";

    private HeroDraft Snapshot() => new([.. PersonalFields.Select(f => _fields[f].Value)], _color, _portraits[_portrait].Image);

    private void ApplyHeroChoice(int previous)
    {
        if (previous == 0) { _newHeroDraft = Snapshot(); _newHeroPrompt = _fields[PromptField].Value; }
        _descScroll = 0;
        var h = _heroes[_fields[HeroField].Index];
        bool isNew = h.Card == null && h.Draft == null;
        // A ready hero (library or deferred option), like a ready world: everything is read-only.
        foreach (int f in PersonalFields.Concat([ColorField, ColorField + 1, ColorField + 2, PromptField, AiField, RandomField])) _fields[f].ReadOnly = !isNew;
        _fields[PromptField].Value = isNew ? _newHeroPrompt : "";
        if (h.Card != null) { ApplyPrefill(h.Card); return; }
        var d = h.Draft ?? _newHeroDraft;
        for (int i = 0; i < PersonalFields.Length; i++)
        {
            string v = d?.Values[i] ?? "";
            if (ChoiceFields.Contains(PersonalFields[i])) SetChoice(PersonalFields[i], v);
            else _fields[PersonalFields[i]].Value = v;
        }
        if (d != null)
        {
            _color = d.Color;
            int pi = _portraits.FindIndex(p => p.Image == d.Image);
            if (pi >= 0) _portrait = pi;
        }
    }

    // A new hero that already has a description becomes a ready option in the HERO chooser before «RANDOM» (only for
    // the duration of this game creation), and the new one's description starts from scratch (like a world with a description before a new map).
    private void ArchiveNewHero()
    {
        if (_fields[HeroField].Index != 0 || _fields[DescriptionField].Value.Trim().Length == 0) return;
        _heroes.Insert(1, new(Draft: Snapshot()));
        _fields[HeroField].Options.Insert(1, _fields[NameField].Value.Trim() is { Length: > 0 } n ? n : L.T("draft"));
        _fields[DescriptionField].Value = _fields[PromptField].Value = "";
        _message = L.T("The previous version is in the HERO list (◄ ►) while this game is being created");
    }

    // A library hero (the «Hero» step of the new game wizard): the form fields are filled with their persona and can be adjusted.
    private void ApplyPrefill(HeroLibrary.Card h)
    {
        _fields[NameField].Value = h.Name;
        _fields[SymbolField].Value = h.Symbol;
        if (h.Race.Length > 0) SetChoice(RaceField, h.Race);
        if (h.Class.Length > 0) SetChoice(ClassField, h.Class);
        SetChoice(LevelField, System.Text.RegularExpressions.Regex.Match(h.Level, @"\d+").Value);
        SetChoice(AlignmentField, HeroStat(h.HeroJson, StatKeys.Alignment));
        SetChoice(BackgroundField, HeroStat(h.HeroJson, StatKeys.Background));
        _fields[DescriptionField].Value = h.Description;   // no description: empty (not the previous hero's text)
        if (h.Color != null && Array.FindIndex(Palette, p => p.Rgb.SequenceEqual(h.Color)) is int ci and >= 0) _color = ci;
        if (h.Image != null)
        {
            int pi = _portraits.FindIndex(p => p.Image == h.Image);
            if (pi < 0) { _portraits.Insert(0, new Portrait(h.Image, h.Name.ToUpperInvariant())); pi = 0; }
            _portrait = pi;
        }
    }

    // A hero stat from their JSON (character sheet) by key (StatKeys); if missing, "".
    private static string HeroStat(string? heroJson, string key)
    {
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(heroJson ?? "")?["stats"]?.AsArray()
                .FirstOrDefault(s => (string?)s?["key"] == key)?["value"]?.ToString() ?? "";
        }
        catch { return ""; }
    }

    // The form (step 2). Returns the step to go to: 1 is the world, 3/4 is onward (hero created, Data), 0 is exit to the menu.
    public async Task<int> Show()
    {
        _startTop = Term.CursorTop;
        _titleTabsCache = null;   // the title may have changed (available steps)
        _tabHovered = null;
        _exitToMenu = false;
        _recreateWarned = false;
        Term.CursorVisible = false;
        _borderDrawer.DrawSeparator();
        _bodyTop = _startTop + 1;
        _width = _display.InnerWidth(_display.ViewCols(_settings.Map));
        _height = Math.Max(24, Term.WindowHeight - _bodyTop - 1);
        _x0 = DisplayConfig.LeftMargin + 1;
        Term.SetCursorPosition(0, _bodyTop);
        for (int r = 0; r < _height; r++) _borderDrawer.DrawContentLine(() => { });
        _borderDrawer.DrawBottomBorder();

        _editing = true; // the first field goes straight into input
        Redraw();

        while (true)
        {
            // The model is writing the description: the screen waits (Esc only), the spinner turns.
            if (_describeTask != null)
            {
                if (_describeTask.IsCompleted) FinishDescribe();
                else
                {
                    ConsoleMouseReader.DrainWhileWaiting();
                    ConsoleMouseReader.SetCursorShape(false);   // nothing can be pressed: a normal pointer
                    if (ConsoleMouseReader.TryReadKey() is { Key: ConsoleKey.Escape }) return 0;
                    _frame++;
                    Redraw();
                    await Task.Delay(100);
                }
                continue;
            }
            if (HandleMouse() is { } mouseResult)
            {
                if (_exitToMenu) { _exitToMenu = false; return 0; }
                if (_gotoStep is int g)
                {
                    _gotoStep = null;
                    if (await Go(g, sound: false) is int to) return to;
                    Redraw();
                    continue;
                }
                if (mouseResult && await Go(3) is int next) return next;
                continue;
            }
            // Keys come straight from the console buffer (code + character), like in the main menu: Console.ReadKey
            // mixed with mouse reading parsed some presses wrong.
            if (ConsoleMouseReader.TryReadKey() is not { } key) { Thread.Sleep(15); continue; }

            // Characters arriving in one batch: a file dragged into the window gets «typed» by the console as its path;
            // we ignore such a paste (a custom picture only via «CHOOSE FILE…»).
            var keys = new List<ConsoleKeyInfo> { key };
            if (!char.IsControl(key.KeyChar))
            {
                while (ConsoleMouseReader.TryReadKey() is { } next)
                {
                    keys.Add(next);
                    if (char.IsControl(next.KeyChar)) break;
                }
                string burst = new(keys.Where(k => !char.IsControl(k.KeyChar)).Select(k => k.KeyChar).ToArray());
                if (burst.Length > 3 && IsDroppedFile(burst)) keys.RemoveAll(k => !char.IsControl(k.KeyChar));
            }

            foreach (var k in keys)
            {
                if (k.Key == ConsoleKey.Escape) return 0;
                if (k.Key is >= ConsoleKey.F1 and <= ConsoleKey.F4)
                {
                    if (await Go(k.Key - ConsoleKey.F1 + 1) is int to) return to;
                    break;
                }
                if (k.Key != ConsoleKey.Enter) _recreateWarned = false;   // the warning only applies to consecutive Enters
                _message = ""; // hint/error lasts until the next key press
                bool create = _editing ? HandleEditKey(k) : HandleNavKey(k);
                if (create && await Go(3) is int next) return next;
            }
            Redraw();
        }
    }

    // Go to a step: 1 is the world (what's filled in stays), 3/4 is onward: the hero is created (or appearance changes
    // carry over). null means stay (not everything is filled in, or waiting for recreate confirmation).
    private async Task<int?> Go(int step, bool sound = true)
    {
        if (step == 1) { if (sound) Sound.PlayClick(); return 1; }
        if (step == 2) return null;
        if (!CanCreate())
        {
            _message = L.T("Fill in all form fields: ") + string.Join(", ", MissingFields());
            return null;
        }
        string key = EssentialKey();
        if (_createdKey == key && Data != null)
        {
            ApplyLooks();
            return step;
        }
        if (_createdKey != null && !_recreateWarned)
        {
            _recreateWarned = true;
            _message = L.T("Race, class, level, background or hero changed — the character will be created again.\nEditor changes will be lost. Press again to create.");
            return null;
        }
        _recreateWarned = false;
        Data = await Create();
        _createdKey = key;
        CreatedNow = true;
        return step;
    }

    // The description (backstory) isn't the character sheet: editing it doesn't recreate the hero, it just carries over.
    // The HERO choice is compared by the record itself (indices shift when a deferred option lands in the chooser).
    private string EssentialKey() => string.Join("|", System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(_heroes[_fields[HeroField].Index]),
        _fields[RaceField].Value.Trim(),
        _fields[ClassField].Value.Trim(), _fields[LevelField].Value, _fields[BackgroundField].Value);

    // An already created hero: name, symbol, color and portrait from the form go into the hero as is, without the model.
    private void ApplyLooks()
    {
        var portrait = _portraits[_portrait];
        Data!.Name = _fields[NameField].Value.Trim();
        Data.Symbol = _fields[SymbolField].Value.Trim();
        Data.Description = _fields[DescriptionField].Value.Trim();
        Data.Alignment = _fields[AlignmentField].Value;
        Data.Color = [.. Palette[_color].Rgb];
        Data.Image = string.IsNullOrEmpty(portrait.Image) ? null : portrait.Image;
        if (_settings.Hero is not { } hero) return;
        hero.Name = Data.Name;
        if (hero.Stat(StatKeys.Alignment) is { } align)
            align.Value = Data.Alignment;
        if (Data.Symbol.Length > 0) hero.Symbol = Data.Symbol;
        hero.Color = [.. Data.Color];
        if (Data.Image != null) hero.Image = Data.Image;
    }

    private static bool IsDroppedFile(string text)
    {
        string path = text.Trim().Trim('"');
        try { return Path.IsPathRooted(path) && (File.Exists(path) || Directory.Exists(path)); }
        catch { return false; }
    }

    // ── Keyboard ──────────────────────────────────────────────────────────────
    private bool HandleEditKey(ConsoleKeyInfo key)
    {
        var field = _fields[_focus];
        ref string value = ref field.Value;
        int max = field.MaxLength;

        // The description is a field with a cursor: ←→ Home End ↑↓ PgUp/PgDn; ↑ on the first / ↓ on the last line moves to the neighboring field.
        if (_focus == DescriptionField && key.Key != ConsoleKey.Enter)
        {
            if (TextArea.Edit(ref value, ref _descCursor, key, max, _descRect.w, _descRect.rows)) { _descFollow = true; return false; }
            if (key.Key is ConsoleKey.UpArrow or ConsoleKey.DownArrow)
            {
                StopEditing();
                MoveFocus(key.Key == ConsoleKey.UpArrow ? -1 : 1);
            }
            return false;
        }

        if (ClipboardText.IsPasteKey(key))
        {
            string paste = ClipboardText.Get();
            value += paste[..Math.Min(paste.Length, Math.Max(0, max - value.Length))];
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.Enter when _focus == PromptField:
                if (_fields[PromptField].Value.Trim().Length == 0) return false;   // empty input: don't send
                StopEditing();
                RunDescribe();
                return false;
            case ConsoleKey.Enter:
                StopEditing();
                // Moving through the text fields in sequence: input for the next one opens right away (as in the form before).
                // (after CLASS comes the description in the bottom block, after the description the request line).
                if (field.Kind == Kind.Text && _focus != PromptField)
                {
                    int pos = Array.IndexOf(Order, _focus);
                    int next = Order.Skip(pos + 1).FirstOrDefault(i => _fields[i].Kind == Kind.Text && !_fields[i].ReadOnly, -1);
                    _focus = next >= 0 ? next : NextFocusable(_focus, 1);
                    _editing = _fields[_focus].Kind == Kind.Text;
                    if (_focus == DescriptionField) FocusDescriptionEnd();
                }
                return false;
            case ConsoleKey.UpArrow: StopEditing(); MoveFocus(-1); return false;
            case ConsoleKey.DownArrow: StopEditing(); MoveFocus(1); return false;
            case ConsoleKey.Backspace:
                if (value.Length > 0) value = value[..^1];
                return false;
        }
        if (!char.IsControl(key.KeyChar) && value.Length < max) value += key.KeyChar;
        return false;
    }

    private bool HandleNavKey(ConsoleKeyInfo key)
    {
        var field = _fields[_focus];
        switch (key.Key)
        {
            case ConsoleKey.UpArrow: MoveFocus(-1); return false;
            case ConsoleKey.DownArrow or ConsoleKey.Tab: MoveFocus(1); return false;
            case ConsoleKey.LeftArrow: Step(field, -1); return false;
            case ConsoleKey.RightArrow: Step(field, 1); return false;
            case ConsoleKey.Enter:
                switch (field.Kind)
                {
                    case Kind.Text: _editing = true; return false;
                    case Kind.Actions: RunAction(_action); return false;
                    case Kind.AiButton: RunDescribe(); return false;
                    case Kind.RandomButton: Randomize(); return false;
                    case Kind.Button:
                        if (CanCreate()) return true;
                        _message = L.T("Fill in all form fields: ") + string.Join(", ", MissingFields());
                        return false;
                }
                return false;
        }
        // Typing or pasting on a text field goes straight into input.
        if (field.Kind == Kind.Text && !field.ReadOnly && (!char.IsControl(key.KeyChar) || ClipboardText.IsPasteKey(key)))
        {
            _editing = true;
            return HandleEditKey(key);
        }
        return false;
    }

    // A focused text field is open for input right away: the «▌» cursor shows where text will go.
    private void MoveFocus(int dir)
    {
        _focus = NextFocusable(_focus, dir);
        _editing = _fields[_focus].Kind == Kind.Text;
        if (_focus == DescriptionField) FocusDescriptionEnd();
        Sound.PlayClick();
    }

    // The next field in direction dir, skipping fixed ones (SETTING); at the edge it stays put.
    private int NextFocusable(int from, int dir)
    {
        int pos = Array.IndexOf(Order, from);
        for (int i = pos + dir; i >= 0 && i < Order.Length; i += dir)
            if (!_fields[Order[i]].ReadOnly && (Order[i] != HeroField || _heroes.Count > 1)) return Order[i];
        return from;
    }

    private void Step(Field field, int dir)
    {
        if (field.Kind == Kind.Color) _color = (_color + dir + Palette.Length) % Palette.Length;
        else if (field.Kind == Kind.Portrait) _portrait = (_portrait + dir + _portraits.Count) % _portraits.Count;
        else if (field.Kind == Kind.Actions) _action = (_action + dir + PortraitActions.Length) % PortraitActions.Length;
        else if (field.Kind == Kind.Choice && field.Options.Count > 0)
        {
            int previous = field.Index;
            field.Index = (field.Index + dir + field.Options.Count) % field.Options.Count;
            if (field == _fields[HeroField]) ApplyHeroChoice(previous);
            else
            {
                field.Value = field.Options[field.Index];
                int fi = _fields.IndexOf(field);
                if (fi is RaceField or ClassField) PortraitFor(fi);
            }
        }
        else return;
        Sound.PlayClick();
    }

    private void StopEditing() => _editing = false;

    // The button under the portrait: a custom picture via a Windows dialog.
    private void RunAction(int action)
    {
        Sound.PlayClick();
        PickCustomImage();
    }

    // You can create once all the form's text fields are filled in.
    private bool CanCreate() => !MissingFields().Any();

    private IEnumerable<string> MissingFields() =>
        _fields.Where(f => f.Kind == Kind.Text && f != _fields[PromptField] && f.Value.Trim().Length == 0).Select(f => f.Label.ToLowerInvariant());

    // ── Hero description from the model ─────────────────────────────────────
    private Task<string?>? _describeTask;
    private int _frame;
    private (int row, int x, int w)? _aiButton, _randomButton;
    // Description: cursor, scroll (first visible line), follow the cursor (after an edit), text location.
    private int _descCursor, _descScroll;
    private bool _descFollow = true;
    private (int top, int x, int w, int rows) _descRect = (0, 0, 1, 1);

    private void FocusDescriptionEnd()
    {
        _descCursor = _fields[DescriptionField].Value.Length;
        _descFollow = true;
    }
    private bool _randomHovered;
    private readonly Dictionary<int, (int row, int x, int w)> _choiceArrows = [];   // choosers: field → ◄ … ►
    private bool _aiHovered;
    private (int field, int dir) _arrowHovered;   // the chooser arrow under the mouse

    // INVENT/CHANGE: a description (appearance, personality, backstory) from name/race/class and the request; if there's text,
    // the model refines it.
    private void RunDescribe()
    {
        Sound.PlayClick();
        if (_describeTask != null || _fields[AiField].ReadOnly) return;
        // Without a request the model only invents from scratch; rewriting a finished description «for nothing» is not allowed.
        if (_fields[PromptField].Value.Trim().Length == 0 && _fields[DescriptionField].Value.Trim().Length > 0)
        {
            _message = L.T("Write in \"Input\" what to change in the description");
            _focus = PromptField;
            _editing = true;
            return;
        }
        _describeTask = _aiClient.DescribeHero(_fields[NameField].Value.Trim(), _fields[RaceField].Value.Trim(),
            _fields[ClassField].Value.Trim(), _fields[DescriptionField].Value.Trim(), _fields[PromptField].Value.Trim(),
            $"Level: {_fields[LevelField].Value}\nAlignment: {_fields[AlignmentField].Value}\nBackground: {_fields[BackgroundField].Value}");
    }

    private void FinishDescribe()
    {
        if (_describeTask?.IsFaulted == true)
            _message = _describeTask.Exception?.GetBaseException() is AiSetupException setup
                ? setup.Message : L.T("The AI didn't respond — try again.");
        else if (_describeTask is { IsCompletedSuccessfully: true, Result: { Length: > 0 } text })
        {
            _fields[DescriptionField].Value = text.Length > 1500 ? text[..1500] : text;
            _fields[PromptField].Value = "";   // request done: the input line is cleared
            _descScroll = 0;
            _descCursor = text.Length;
            _descFollow = false;   // new text: from the start
        }
        else _message = L.T("The AI didn't respond — try again.");
        _describeTask = null;
        Redraw();
    }

    private bool _buttonHovered;
    private bool _exitToMenu;  // click on «[Esc]MENU» in the title
    private List<MouseUiHelper.TitleTab> _titleTabs => _titleTabsCache ??= MouseUiHelper.ComputeTitleTabs(_screen.Title);
    private List<MouseUiHelper.TitleTab>? _titleTabsCache;
    private int _actionHovered = -1; // the button under the portrait that the mouse is over

    // ── Mouse ─────────────────────────────────────────────────────────────────
    // null means nothing; false means something changed (redrawn); true means «CREATE» was pressed.
    private bool? HandleMouse()
    {
        var (move, click, _) = ConsoleMouseReader.DrainMouseEvents();
        // The wheel over the description scrolls the text.
        if (ConsoleMouseReader.TakeWheel(out var wp) is int notches and not 0)
        {
            int wr = wp.y - _bodyTop - _descRect.top, wc = wp.x - _x0;
            if (wr >= 0 && wr < _descRect.rows && wc >= _formLeft && wc < _formLeft + FormWidth + Gap + PreviewWidth)
            {
                _descScroll -= notches;
                _descFollow = false;
                Redraw();
                return false;
            }
        }
        if (move is { } m)
        {
            int hoverField = FieldAt(m.x, m.y);
            bool overButton = hoverField == _fields.Count - 1;
            int overAction = ActionAt(m.x, m.y);
            bool clickable = hoverField >= 0 && !_fields[hoverField].ReadOnly && _fields[hoverField].Kind != Kind.Actions;
            string? overTab = MouseUiHelper.GetHoveredTabKey(m.x, m.y, _titleTabs);
            ConsoleMouseReader.SetCursorShape(clickable || overTab != null || overAction >= 0 || IsArrow(m.x, m.y) != 0 || SwatchAt(m.x, m.y) >= 0);
            if (overTab != _tabHovered)
            {
                if (_tabHovered != null) MouseUiHelper.SetTabHighlight(_screen.Title, _titleTabs, _tabHovered, false, _display);
                if (overTab != null) MouseUiHelper.SetTabHighlight(_screen.Title, _titleTabs, overTab, true, _display);
                _tabHovered = overTab;
            }
            bool overAi = OnRect(_aiButton, m.x, m.y);
            bool overRandom = OnRect(_randomButton, m.x, m.y);
            if (overRandom) ConsoleMouseReader.SetCursorShape(true);
            if (overRandom != _randomHovered) { _randomHovered = overRandom; Redraw(); }
            var overArrow = ChoiceArrowAt(m.x, m.y);
            if (overAi || overArrow.dir != 0) ConsoleMouseReader.SetCursorShape(true);
            if (overButton != _buttonHovered || overAction != _actionHovered || overAi != _aiHovered || overArrow != _arrowHovered)
            {
                _buttonHovered = overButton;
                _actionHovered = overAction;
                _aiHovered = overAi;
                _arrowHovered = overArrow;
                Redraw();
            }
        }
        if (click is not { } c) return null;

        int swatch = _fields[ColorField].ReadOnly ? -1 : SwatchAt(c.x, c.y);
        if (swatch >= 0)
        {
            if (_editing) StopEditing();
            _focus = _fields.FindIndex(f => f.Kind == Kind.Color);
            _color = swatch;
            Sound.PlayClick();
            Redraw();
            return false;
        }

        // «[Esc]MENU» in the title works like Esc, «[Fn]step» like F1–F4; the rest of the header drags the window.
        if (MouseUiHelper.GetHoveredTabKey(c.x, c.y, _titleTabs) is { } tabKey)
        {
            Sound.PlayClick();
            ConsoleMouseReader.SetCursorShape(false);
            if (tabKey == "Esc") _exitToMenu = true;
            else _gotoStep = tabKey[1] - '0';
            return false;
        }
        if (c.y < _bodyTop) { ConsoleMouseReader.StartWindowDrag(); return null; }
        int actionClicked = _fields[ColorField + 2].ReadOnly ? -1 : ActionAt(c.x, c.y);
        if (actionClicked >= 0)
        {
            if (_editing) StopEditing();
            _focus = _fields.FindIndex(f => f.Kind == Kind.Actions);
            _action = actionClicked;
            RunAction(actionClicked);
            Redraw();
            return false;
        }
        int arrow = IsArrow(c.x, c.y);
        if (arrow != 0)
        {
            // The portrait arrows flip the portrait, wherever the focus is.
            if (_editing) StopEditing();
            _focus = _fields.FindIndex(f => f.Kind == Kind.Portrait);
            Step(_fields[_focus], arrow);
            Redraw();
            return false;
        }

        if (OnRect(_randomButton, c.x, c.y))
        {
            _focus = RandomField;
            Randomize();
            Redraw();
            return false;
        }
        if (OnRect(_aiButton, c.x, c.y))
        {
            if (_editing) StopEditing();
            _focus = AiField;
            RunDescribe();
            Redraw();
            return false;
        }
        if (ChoiceArrowAt(c.x, c.y) is { dir: not 0 } arrowHit)
        {
            if (_editing) StopEditing();
            _focus = arrowHit.field;
            Step(_fields[arrowHit.field], arrowHit.dir);
            Redraw();
            return false;
        }

        int field = FieldAt(c.x, c.y);
        if (field < 0 || _fields[field].ReadOnly || _fields[field].Kind == Kind.Actions) return null;
        if (_fields[field].Kind == Kind.Choice)
        {
            // A choice (world/hero): a click goes to the next option.
            if (_editing) StopEditing();
            _focus = field;
            Step(_fields[field], 1);
            Redraw();
            return false;
        }
        if (_fields[field].Kind == Kind.Button)
        {
            if (_editing) StopEditing();
            Sound.PlayClick();
            if (CanCreate()) return true;
            _focus = field;
            _message = L.T("Fill in all form fields: ") + string.Join(", ", MissingFields());
            Redraw();
            return false;
        }
        if (_editing) StopEditing();
        _focus = field;
        Sound.PlayClick();
        _editing = _fields[field].Kind == Kind.Text;
        // A click on the description text puts the cursor there.
        if (field == DescriptionField)
        {
            _descCursor = TextArea.CursorAt(_fields[DescriptionField].Value, _descScroll, _descRect.w,
                c.y - _bodyTop - _descRect.top, c.x - _x0 - _descRect.x);
            _descFollow = true;
        }
        Redraw();
        return false;
    }

    // The field under the mouse: only inside the form frame (the preview rows on the right line up with the field rows),
    // the «CREATE» button only on its text.
    private int FieldAt(short x, short y)
    {
        foreach (var (row, field) in _fieldRows)
        {
            if (y != _bodyTop + row) continue;
            bool inside = _fields[field].Kind == Kind.Button
                ? IsOnButton(x)
                : field is DescriptionField or PromptField   // the bottom block spans the full width
                    ? x > _x0 + _formLeft && x < _x0 + _formLeft + FormWidth + Gap + PreviewWidth - 1
                    : x > _x0 + _formLeft && x < _x0 + _formLeft + FormWidth - 1;
            return inside ? field : -1;
        }
        return -1;
    }

    private int SwatchAt(short x, short y)
    {
        for (int i = 0; i < _swatches.Count; i++)
        {
            var (row, sx) = _swatches[i];
            if (y == _bodyTop + row && x >= _x0 + sx && x < _x0 + sx + 2) return i;
        }
        return -1;
    }

    private int ActionAt(short x, short y)
    {
        for (int i = 0; i < _actionPos.Count; i++)
        {
            var (row, ax, w) = _actionPos[i];
            if (y == _bodyTop + row && x >= _x0 + ax && x < _x0 + ax + w) return i;
        }
        return -1;
    }

    private bool OnRect((int row, int x, int w)? r, short x, short y) =>
        r is { } b && y == _bodyTop + b.row && x >= _x0 + b.x && x < _x0 + b.x + b.w;

    private (int field, int dir) ChoiceArrowAt(short x, short y)
    {
        foreach (var (field, a) in _choiceArrows)
        {
            if (y != _bodyTop + a.row) continue;
            if (x == _x0 + a.x) return (field, -1);
            if (x == _x0 + a.x + a.w) return (field, 1);
        }
        return (0, 0);
    }

    private bool IsOnButton(short x) => x >= _x0 + _buttonX && x < _x0 + _buttonX + _buttonWidth;

    private int IsArrow(short x, short y)
    {
        if (_arrows is not { } a || y != _bodyTop + a.row) return 0;
        if (x == _x0 + a.left) return -1;
        if (x == _x0 + a.right) return 1;
        return 0;
    }

    // ── Portraits ─────────────────────────────────────────────────────────────
    // «CHOOSE FILE…»: a Windows dialog; the file is copied into Storage/Portraits and goes first in the gallery.
    private void PickCustomImage()
    {
        string? path = FileDialog.OpenFile(L.T("Hero portrait"),
            L.T("Images (PNG, JPG, BMP, GIF, SVG)") + "|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.svg");
        if (path == null) return;
        if (!SvgToBrailleConverter.IsSupportedImageFile(path))
        {
            _message = L.T("Format not supported (PNG, JPG, BMP, GIF, SVG)");
            return;
        }
        try
        {
            Directory.CreateDirectory(SvgToBrailleConverter.PortraitsDir);
            string fileName = Path.GetFileName(path);
            string target = Path.Combine(SvgToBrailleConverter.PortraitsDir, fileName);
            for (int n = 2; File.Exists(target) && !SameFile(target, path); n++)
                target = Path.Combine(SvgToBrailleConverter.PortraitsDir, $"{Path.GetFileNameWithoutExtension(path)}_{n}{Path.GetExtension(path)}");
            if (!File.Exists(target)) File.Copy(path, target);

            string image = SvgToBrailleConverter.PortraitsPrefix + Path.GetFileName(target);
            if (SvgToBrailleConverter.Convert(image, "", PortraitWidth) == null)
            {
                _message = L.T("Could not read the image");
                return;
            }
            _portraits.RemoveAll(p => p.Image == image);
            _portraits.Insert(0, new Portrait(image, L.T("CUSTOM: ") + Path.GetFileNameWithoutExtension(target).ToUpperInvariant()));
            _portrait = 0;
            _message = "";
        }
        catch (Exception ex)
        {
            _message = L.T("Could not load the image: ") + ex.Message;
        }
    }

    private static bool SameFile(string a, string b) =>
        string.Equals(Path.GetFullPath(a), Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase);

    // ── Creation ──────────────────────────────────────────────────────────────
    private async Task<NewGameData> Create()
    {
        var portrait = _portraits[_portrait];
        var result = new NewGameData
        {
            Name = _fields[NameField].Value.Trim(),
            Symbol = _fields[SymbolField].Value.Trim(),
            Race = _fields[RaceField].Value.Trim(),
            Class = _fields[ClassField].Value.Trim(),
            Level = _fields[LevelField].Value,
            Alignment = _fields[AlignmentField].Value,
            Background = _fields[BackgroundField].Value,
            Description = _fields[DescriptionField].Value.Trim(),
            Setting = Setting,
            SettingWish = "",   // wishes for the game come at the «Adventure» step after the editor
            Color = [.. Palette[_color].Rgb],
            Image = string.IsNullOrEmpty(portrait.Image) ? null : portrait.Image,
        };

        // The wait indicator is centered, in place of the hint under the button (not the shared spinner at the screen edge).
        // A library hero isn't recreated: they're prepared for a new adventure (equipment, condition).
        _editing = false;
        var reused = _heroes[_fields[HeroField].Index].Card;
        var aiTask = reused?.HeroJson is { } heroJson ? _aiClient.ReuseHero(result, heroJson) : _aiClient.CreateNewGame(result);
        string waitText = reused?.HeroJson != null ? L.T("Preparing the hero for a new adventure...") : L.T("Creating the character...");
        for (int frame = 0; !aiTask.IsCompleted; frame++)
        {
            // While the character is being created, clicks aren't accepted and the pointer is normal (not the «hand»).
            ConsoleMouseReader.DrainWhileWaiting();
            ConsoleMouseReader.SetCursorShape(false);
            _message = $"{Spinner.Frames[frame % Spinner.Frames.Length]} {waitText}";
            Redraw();
            await Task.Delay(100);
        }
        _message = "";
        await aiTask;
        return result;
    }

    // ── Drawing ───────────────────────────────────────────────────────────────
    // Everything centered on screen: the title, under it the «FORM» and «HERO» frames of equal height, below them the button and
    // the hint. Fields are packed tightly in groups; description and wishes take several wrapped lines.
    private void Redraw()
    {
        _chars = new char[_height, _width];
        _fg = new List<int>?[_height, _width];
        _bg = new List<int>?[_height, _width];
        for (int r = 0; r < _height; r++) for (int c = 0; c < _width; c++) _chars[r, c] = ' ';
        _fieldRows.Clear();
        _arrows = null;

        int total = FormWidth + Gap + PreviewWidth;
        int left = Math.Max(1, (_width - total) / 2);
        _formLeft = left;
        int boxHeight = BoxInner + 2;
        int blockHeight = boxHeight + 1 + DescHeight + 1 + 1 + 3;   // frames, description, button, hint (up to 2 lines)
        int top = Math.Max(0, (_height - blockHeight) / 2);
        int boxTop = top; // no separate title: «NEW GAME» is already in the screen's title row

        DrawBox(boxTop, left, FormWidth, boxHeight, L.T("FORM"));
        DrawBox(boxTop, left + FormWidth + Gap, PreviewWidth, boxHeight, L.T("HERO"));

        // Form fields: groups separated by an empty line, from the top of the frame (not centered, or else the whole form
        // would shift when the description/wishes add a line).
        _swatches.Clear();
        int y = boxTop + 2;
        int valueWidth = FormWidth - ValueCol - 3;
        _actionPos.Clear();
        foreach (int group in new[] { HeroField, -1, NameField, SymbolField, -1, RaceField, ClassField, LevelField, AlignmentField, BackgroundField, -1,
                     ColorField, -1, ColorField + 1, -1, ColorField + 2 })
        {
            if (group < 0) { y++; continue; }
            y += DrawField(group, y, left, valueWidth);
        }

        // The button is inactive (dark, no arrows) until all fields are filled in; when active it has arrows
        // when selected by keyboard or hovered by the mouse. The width is the same, so the text doesn't jump.
        // «RANDOM» is a plate at the bottom of the form frame (like the map buttons on the «World» step); a ready hero has none.
        _randomButton = null;
        if (!_fields[RandomField].ReadOnly)
        {
            int ry = boxTop + boxHeight - 3, rx = left + LabelCol;
            _randomButton = (ry, rx, Chip(ry, rx, L.T("RANDOM"), _focus == RandomField || _randomHovered, false));
        }

        DrawDescription(boxTop + boxHeight + 1, left, total);

        _buttonRow = boxTop + boxHeight + 1 + DescHeight + 1;
        var button = _fields[^1];
        bool enabled = CanCreate();
        bool highlighted = enabled && (_focus == _fields.Count - 1 || _buttonHovered);
        _buttonX = left + Math.Max(0, (total - button.Label.Length - 4) / 2);
        _buttonWidth = Chip(_buttonRow, _buttonX, button.Label, highlighted, !enabled);
        _fieldRows.Add((_buttonRow, _fields.Count - 1));
        // A hint or message, up to two lines («\n» is a line break).
        var hint = (_message.Length > 0 ? _message : L.T("[↑↓]FIELD   [Enter]EDIT   [←→]BROWSE   [F1]WORLD   [Esc]MENU")).Split('\n');
        for (int i = 0; i < hint.Length && i < 2; i++)
            CenterIn(_buttonRow + 2 + i, left, total, Fit(hint[i], total), _recreateWarned ? Bright : Dim);

        DrawPreview(boxTop + 1, left + FormWidth + Gap);

        Term.CursorVisible = false;
        for (int r = 0; r < _height; r++) WriteRow(r);
    }

    // The «HERO DESCRIPTION» block (like the world description): text on top, your own or from the model; below the divider, the request's «Input:»
    // and INVENT/CHANGE. A ready hero shows only the text across the whole block.
    private void DrawDescription(int top, int left, int total)
    {
        var desc = _fields[DescriptionField];
        string name = _fields[NameField].Value.Trim();
        DrawBox(top, left, total, DescHeight, name.Length > 0 ? L.T("HERO DESCRIPTION") + " · " + name.ToUpperInvariant() : L.T("HERO DESCRIPTION"));
        int lx = left + 3, width = total - 6;
        bool locked = desc.ReadOnly;
        int rows = locked ? DescHeight - 2 : DescHeight - 4;
        bool editing = _focus == DescriptionField && _editing;
        _descRect = (top + 1, lx, width, rows);
        // Text: word wrap, a cursor (highlighted cell), scrolling by wheel, ↑↓, PgUp/PgDn; a scrollbar on the right.
        if (desc.Value.Length == 0 && !editing)
            Put(top + 1, lx, Fit(L.T("appearance, personality, backstory — in your own words or \"INVENT\" from a request"), width), Dim);
        else
        {
            var view = TextArea.View(desc.Value, _descCursor, ref _descScroll, width, rows, editing && _descFollow,
                out int curRow, out int curCol, out int totalLines);
            for (int i = 0; i < view.Count; i++) Put(top + 1 + i, lx, view[i], editing ? Bright : Fg);
            if (editing && curRow >= 0)
            {
                string line = view[curRow];
                Put(top + 1 + curRow, lx + curCol, curCol < line.Length ? line[curCol].ToString() : " ", _display.MainBackground, bg: Bright);
            }
            if (TextArea.Bar(rows, totalLines, _descScroll) is { } bar)
                for (int i = 0; i < rows; i++) Put(top + 1 + i, left + total - 2, bar[i] ? "┃" : "│", bar[i] ? Bright : Dim);
        }
        if (!locked)
            for (int i = 0; i < rows; i++) _fieldRows.Add((top + 1 + i, DescriptionField));
        _aiButton = null;
        if (locked) return;

        int sep = top + DescHeight - 3, iy = sep + 1;
        Put(sep, left, "├" + new string('─', total - 2) + "┤", MouseUiHelper.FrameColor(_display));
        if (_describeTask != null)
        {
            // The model is thinking: the whole line goes to the wait indicator, without «Input:» and the button.
            Put(iy, lx, Fit(Spinner.Frames[_frame % Spinner.Frames.Length] + " " + L.T("The AI is writing the hero description…"), width), Bright);
            return;
        }
        string goText = desc.Value.Trim().Length > 0 ? L.T("CHANGE") : L.T("INVENT");
        int gx = left + total - 3 - (goText.Length + 4);
        bool promptFocus = _focus == PromptField;
        string inputLabel = L.T("Input: ");
        Put(iy, lx, inputLabel, promptFocus ? Bright : Fg);
        int px = lx + inputLabel.Length, promptW = gx - 2 - px;
        var prompt = _fields[PromptField].Value;
        bool empty = prompt.Length == 0 && !(promptFocus && _editing);
        string text = empty ? L.T("what to invent or how to change the description (optional)") : TailFit(prompt + (promptFocus && _editing ? "▌" : ""), promptW);
        Put(iy, px, Fit(text, promptW), empty ? Dim : promptFocus ? Bright : Fg);
        _fieldRows.Add((iy, PromptField));
        int w = Chip(iy, gx, goText, _focus == AiField || _aiHovered, false);
        _aiButton = (iy, gx, w);
    }

    // One form field; returns how many lines it took.
    private int DrawField(int index, int y, int left, int valueWidth)
    {
        var f = _fields[index];
        bool focused = index == _focus;
        int lx = left + LabelCol, vx = left + ValueCol;
        // The button row has no label: its focus is shown by the arrows on the button itself.
        if (f.Kind != Kind.Actions)
            Put(y, lx - 2, focused ? "→ " + f.Label : "  " + f.Label, focused ? Bright : Fg);

        int lines = 1;
        switch (f.Kind)
        {
            case Kind.Text:
            {
                bool editing = focused && _editing;
                bool multi = f.MaxLength > 100;
                if (f.Value.Length == 0 && !editing) { Put(y, vx, "—", Dim); break; }
                // Wrap by characters at the field width (not by words): while typing, text doesn't jump,
                // it moves to a new line only when the current one is full.
                string text = f.Value + (editing ? "▌" : "");
                var wrapped = multi ? Chunk(text, valueWidth) : [text];
                if (wrapped.Count == 0) wrapped.Add(editing ? "▌" : "");
                int max = multi ? MultiLines : 1;
                // While typing, the last lines (so you see what's being typed); otherwise, the first ones with «…».
                var shown = editing ? wrapped.Skip(Math.Max(0, wrapped.Count - max)).ToList() : wrapped.Take(max).ToList();
                if (!editing && wrapped.Count > max) shown[^1] = Fit(shown[^1] + "…", valueWidth);
                for (int i = 0; i < shown.Count; i++) Put(y + i, vx, Fit(shown[i], valueWidth), editing ? Bright : Fg);
                // Height follows the text: a new line appears only when the current one is full (up to 3).
                lines = shown.Count;
                break;
            }

            case Kind.Color:
            {
                // The palette as swatches in a row, with «▲» and the name under the selected one: nothing jumps,
                // ←→ move the arrow, a swatch can be picked with a click.
                // Swatches are the background of two characters with a gap; the selected one isn't marked: you see it in
                // the hero preview (portrait and name in its color).
                for (int i = 0; i < Palette.Length; i++)
                {
                    int sx = vx + i * 3;
                    Put(y, sx, "  ", Fg, bg: Palette[i].Rgb);
                    if (!f.ReadOnly) _swatches.Add((y, sx));
                }
                break;
            }

            case Kind.Actions:
            {
                if (f.ReadOnly) break;   // a ready hero: the portrait doesn't change
                // Buttons under the portrait work like menu items: the selected «→ … ←» is the one under the mouse, and without the mouse
                // the one selected with arrows (when the row has focus).
                int ax = vx - 2;
                for (int i = 0; i < PortraitActions.Length; i++)
                {
                    bool sel = _actionHovered >= 0 ? i == _actionHovered : focused && i == _action;
                    int w = Chip(y, ax, PortraitActions[i], sel, false);
                    _actionPos.Add((y, ax, w));
                    ax += w + 2;
                }
                break;
            }

            case Kind.Choice:
            {
                // A fixed-width field: the arrows don't jump while flipping, the value is centered.
                int inner = valueWidth - 4, textW = inner;
                // HERO: «which of how many», muted, by the right arrow (like WORLD on the «World» step).
                if (index == HeroField)
                {
                    string counter = $"{f.Index + 1}/{f.Options.Count}";
                    Put(y, vx + 2 + inner - counter.Length, counter, Dim);
                    textW -= counter.Length + 1;
                }
                string text = f.Options.Count == 0 ? "—" : Fit(f.Options[f.Index], textW);
                Put(y, vx + 2 + (textW - text.Length) / 2, text, focused ? Bright : Fg);
                _choiceArrows.Remove(index);
                if (f.ReadOnly) break;   // a ready hero: no arrows
                // The arrows are always active (you can flip with the mouse without focus); brighter under the mouse.
                Put(y, vx, "◄", focused || _arrowHovered == (index, -1) ? Bright : Fg);
                Put(y, vx + 2 + inner + 1, "►", focused || _arrowHovered == (index, 1) ? Bright : Fg);
                _choiceArrows[index] = (y, vx, inner + 3);
                break;
            }

            case Kind.Portrait:
            {
                // The name sits in a fixed-width field: «◄» and «►» stay in place while flipping.
                var p = _portraits[_portrait];
                const int titleWidth = 16; // + the «matching… 1/35» counter on the right; they don't overlap
                string title = Fit(p.Title, titleWidth);
                int rx = vx + 2 + titleWidth + 1;
                // A ready hero: their portrait, no arrows or counter.
                if (f.ReadOnly)
                {
                    Put(y, vx + 2 + (titleWidth - title.Length) / 2, title, Fg);
                    break;
                }
                // The arrows are always active (you can flip with the mouse even without focus on the field).
                Put(y, vx, "◄", focused ? Bright : Fg);
                Put(y, vx + 2 + (titleWidth - title.Length) / 2, title, focused ? Bright : Fg);
                Put(y, rx, "►", focused ? Bright : Fg);
                _arrows = (y, vx, rx);
                PutRight(y, left + FormWidth - 3, $"{_portrait + 1}/{_portraits.Count}", Dim);
                break;
            }
        }
        for (int i = 0; i < lines; i++) _fieldRows.Add((y + i, index));
        return lines;
    }

    // The «HERO» frame: the portrait in the hero's color, under it the name, the symbol as on the map, and where the portrait came from.
    private void DrawPreview(int y, int left)
    {
        var color = Palette[_color].Rgb;
        var p = _portraits[_portrait];
        int inner = PreviewWidth - 2;
        int artX = left + 1 + (inner - PortraitWidth) / 2;
        var art = string.IsNullOrEmpty(p.Image) ? null : SvgToBrailleConverter.Convert(p.Image, "", PortraitWidth);
        int artRows = PortraitWidth / 2;
        if (art != null)
            for (int i = 0; i < art.Length && i < artRows; i++) Put(y + 1 + i, artX, art[i], color, skipBlank: true);
        else
            CenterIn(y + artRows / 2, left, PreviewWidth, string.IsNullOrEmpty(p.Image) ? L.T("Enter — choose a file") : L.T("no image"), Dim);

        int ty = y + 1 + artRows;
        string name = _fields[NameField].Value.Trim();
        CenterIn(ty, left, PreviewWidth, name.Length == 0 ? L.T("HERO NAME") : Fit(name, inner - 2), name.Length == 0 ? Dim : color);

        // The symbol looks like the hero's cell on the map (MapObjectsProvider: room background, paler, slightly green).
        string symbol = _fields[SymbolField].Value.Trim();
        string onMap = L.T("ON MAP  ???");
        int sx = CenterIn(ty + 1, left, PreviewWidth, onMap, Dim);
        var cellBg = ColorHelper.MixWith(ColorHelper.Pale([55, 55, 65]), [0, 200, 80], 0.18);
        Put(ty + 1, sx + onMap.Length - 3, symbol.Length == 0 ? "???" : symbol.PadRight(3)[..3], ColorHelper.Saturate(color), bg: cellBg);
    }

    // A frame in the game's frame color, the title in a gap of the top line: «╭─ FORM ──…──╮».
    private void DrawBox(int y, int x, int w, int h, string title)
    {
        var line = MouseUiHelper.FrameColor(_display);   // block frames are muted, as on the «World» step
        Put(y, x, "╭─ ", line);
        Put(y, x + 3, title, Bright);
        Put(y, x + 3 + title.Length, " " + new string('─', Math.Max(0, w - title.Length - 5)) + "╮", line);
        for (int r = 1; r < h - 1; r++)
        {
            Put(y + r, x, "│", line);
            Put(y + r, x + w - 1, "│", line);
        }
        Put(y + h - 1, x, "╰" + new string('─', w - 2) + "╯", line);
    }

    // A plate button «  TEXT  » on a fill (as on the «World» step): selected is a brighter fill, unavailable is muted.
    // Returns the width.
    private int Chip(int y, int x, string text, bool on, bool disabled)
    {
        string t = $"  {text}  ";
        var bg = ColorHelper.MixWith(_display.MainBackground, _display.MainForeground, on ? 0.42 : 0.16);
        Put(y, x, t, disabled ? Dim : on ? ColorHelper.Pale(_display.MainForeground, 0.85) : Fg, bg: bg);
        return t.Length;
    }

    private int CenterIn(int row, int left, int width, string text, List<int> color)
    {
        int x = left + Math.Max(0, (width - text.Length) / 2);
        Put(row, x, text, color);
        return x;
    }

    private void PutRight(int row, int right, string text, List<int> color) => Put(row, right - text.Length + 1, text, color);

    private static List<string> Chunk(string text, int width)
    {
        var lines = new List<string>();
        for (int i = 0; i < text.Length; i += width) lines.Add(text.Substring(i, Math.Min(width, text.Length - i)));
        return lines;
    }

    private static string Fit(string text, int width) =>
        text.Length <= width ? text : text[..Math.Max(0, width - 1)] + "…";

    private static string TailFit(string text, int width) =>
        text.Length <= width ? text : "…" + text[^(width - 1)..];

    private void Put(int row, int x, string text, List<int> color, bool skipBlank = false, List<int>? bg = null)
    {
        if (row < 0 || row >= _height) return;
        for (int i = 0; i < text.Length && x + i < _width; i++)
        {
            if (x + i < 0) continue;
            if (skipBlank && text[i] == '⠀') continue;
            _chars[row, x + i] = text[i];
            _fg[row, x + i] = color;
            _bg[row, x + i] = bg;
        }
    }

    private void WriteRow(int row)
    {
        Term.SetCursorPosition(_x0, _bodyTop + row);
        int c = 0;
        while (c < _width)
        {
            var fg = _fg[row, c];
            var bg = _bg[row, c];
            int start = c;
            while (c < _width && ReferenceEquals(_fg[row, c], fg) && ReferenceEquals(_bg[row, c], bg)) c++;
            var run = new string(Enumerable.Range(start, c - start).Select(i => _chars[row, i]).ToArray());
            ColorHelper.WriteColored(run, fgColor: fg ?? _display.MainForeground, bgColor: bg ?? _display.MainBackground);
        }
    }
}
