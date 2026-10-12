using NaviDnD.Data;
using NaviDnD.Data.Models;
using NaviDnD.Helpers;

namespace NaviDnD.Display;

public class HeroDisplay
{
    private readonly WorldState _settings;
    private readonly DisplayConfig _display;

    // Example with the "inv" tab active: "  → INVENTORY ←    [F7]FEATURES    [F8]CONDITIONS      ◄ ►"
    // Every tab takes a FIXED width (SubTabPrefixLen + label + SubTabSuffixLen)
    // whether active or not; only the decorator itself changes ("[Fn]"/"  " ↔ "  → "/" ←"), not its
    // length, so a tab's name never shifts when switching between them.
    private const int SubTabIndent = 3;
    private const int SubTabGap = 6;
    private const int SubTabPrefixLen = 4; // "[Fn]" or "  → "
    private const int SubTabSuffixLen = 2; // "  " (inactive) or " ←" (active)
    private const int SubTabBarContentLen = 137; // up to "◄ N/M ►"

    private static (string Key, string Label, string FKey)[] _subTabs =>
    [
        ("inv",  L.T("INVENTORY"),   "F6"),
        ("abil", L.T("FEATURES"), "F7"),
        ("eff",  L.T("CONDITIONS"),   "F8"),
        ("spell",L.T("SPELLS"),  "F9"),
    ];

    private BorderDrawer? _cachedBorderDrawer;
    private int _subTabTitleY = -1;
    private int _subTabContentStart = -1;
    private bool _canScrollLeft;
    private bool _canScrollRight;
    private int _subTabPage;
    private int _subTabPageCount = 1;
    private bool _leftArrowHovered;
    private bool _rightArrowHovered;
    private string? _hoveredSubTab; // "inv", "abil", "eff", or null

    private int _invContentStartY = -1;
    private int _invRows = 0;
    private int _invSplitX = -1;
    private int[] _invCol1RowToItem = [];
    private int[] _invCol2RowToItem = [];

    private int _spellContentStartY = -1;
    private int _spellRows = 0;
    private int[] _spellSplitX = []; // 3 boundaries between 4 columns (absolute screen X)
    private int[][] _spellColRowToItem = [];

    // The hero has no non-deleted spells at all: the "SPELLS" tab is shown grey and
    // unclickable (SubTabInfo gives it no click/hover zone), so it doesn't lead to an empty
    // page with no spells. Public: Program.cs checks the same thing before [F9].
    // How far the hero can see right now (like the view on the map, MapObjectsProvider): under the open sky, by daylight
    // (at night, darkvision or 10 ft); indoors, their own sight or «by the light» (torches, glowing items).
    // The sheet used to show the visionFt field as is: empty for most heroes and not what the map uses.
    private string SightText(Hero hero)
    {
        if (hero.VisionFt == 0) return L.T("blind");
        int dv = hero.DarkvisionFt ?? 0;
        string dark = dv > 0 ? " · " + L.F("darkv. {0}", dv) : "";
        if (hero.Position is { Count: >= 2 } p && _settings.Map.TerrainAt(p[0], p[1]) is { Indoor: false })
        {
            int light = MovementCalculator.DaylightFt(_settings, p[0], p[1]) ?? Math.Max(dv, 10);
            int ft = hero.VisionFt is > 0 and var v ? Math.Min(v, light) : light;
            return L.F("{0} ft", ft) + (_settings.Time.PartOfDay == PartsOfDay.Night ? " " + L.T("(night)") : "");
        }
        return (hero.VisionFt is > 0 and var own ? L.F("{0} ft", own) : L.T("by light")) + dark;
    }

    public bool HasSpells => _settings.Hero?.Spells?.Any(s => s.Deleted != true) == true;

    // «Level Up» mode (LevelUpDisplay): the sheet shows the hero after leveling (Preview), differences from
    // the current one (PreviewBase) in gold («+2 (15) → +3 (16)»), new features and spells with a gold «+».
    public Hero? Preview { get; set; }
    public Hero? PreviewBase { get; set; }

    private string? PreviewValue(string? before, string? after) =>
        PreviewBase == null || before == after ? null : before == null ? after : $"{before} → {after}";

    private bool IsNewInPreview(string name) => PreviewBase != null
        && !PreviewBase.Abilities.Any(a => a.Name == name) && !(PreviewBase.Spells ?? []).Any(sp => sp.Name == name);

    // The sheet in the game (not in the new game editor): show the «Level Up» button when it is available.
    public bool OfferLevelUp { get; set; }

    // The «[F10]LEVEL UP» button at the bottom of the stats column (screen coordinates), for click and hover (MouseUiHelper); null if none.
    public (int y, int x, int width)? LevelUpHit { get; private set; }

    public bool IsOnLevelUp((short x, short y) pos) =>
        LevelUpHit is { } h && pos.y == h.y && pos.x >= h.x && pos.x < h.x + h.width;

    private static readonly List<int> LevelUpColor = [235, 190, 80];

    private static string ActiveKey(CharacterSubTab tab) => tab switch
    {
        CharacterSubTab.Inventory => "inv",
        CharacterSubTab.Abilities => "abil",
        CharacterSubTab.Spells => "spell",
        _ => "eff",
    };

    // *StartX..*EndX — clickable zone for the inactive "[Fn]Name" label (-1,-1 if that tab is active)
    // leftX, rightX — positions of ◄ and ►
    public (int invStartX, int invEndX, int abilStartX, int abilEndX, int effStartX, int effEndX,
            int spellStartX, int spellEndX,
            int leftX, int rightX, int titleY, bool canScrollLeft, bool canScrollRight) SubTabInfo
    {
        get
        {
            if (_subTabTitleY < 0 || _subTabContentStart < 0)
                return (-1, -1, -1, -1, -1, -1, -1, -1, -1, -1, -1, false, false);

            int cs = _subTabContentStart;
            string activeKey = ActiveKey(_display.CharacterSubTab);
            var zones = new Dictionary<string, (int start, int end)>();
            int pos = SubTabIndent;
            for (int i = 0; i < _subTabs.Length; i++)
            {
                var (key, label, _) = _subTabs[i];
                bool active = key == activeKey;
                int slotWidth = SubTabPrefixLen + label.Length + SubTabSuffixLen;
                bool clickable = key != "spell" || HasSpells;
                // The clickable zone is "[Fn]Name" itself, without the trailing filler spaces.
                // Without the «[Fn]» hint: hovering over it doesn't highlight the tab or change the cursor.
                if (!active && clickable) zones[key] = (cs + pos + SubTabPrefixLen, cs + pos + SubTabPrefixLen + label.Length);
                pos += slotWidth;
                if (i < _subTabs.Length - 1) pos += SubTabGap;
            }

            (int s, int e) Z(string k) => zones.TryGetValue(k, out var v) ? v : (-1, -1);
            var inv = Z("inv"); var abil = Z("abil"); var eff = Z("eff"); var spell = Z("spell");

            int leftX = cs + SubTabBarContentLen;
            int rightX = leftX + 3 + SubTabPageText().Length;
            return (inv.s, inv.e, abil.s, abil.e, eff.s, eff.e, spell.s, spell.e,
                    leftX, rightX,
                    _subTabTitleY, _canScrollLeft, _canScrollRight);
        }
    }

    public (int startY, int rows, int splitX, int[] col1Map, int[] col2Map) InventoryHitInfo =>
        (_invContentStartY, _invRows, _invSplitX, _invCol1RowToItem, _invCol2RowToItem);

    // splitX: 3 boundaries between 4 columns; colMaps[col][row] is the global index into SortedSpells(hero),
    // or -1 if this cell has no spell (like InventoryHitInfo, but for 4 columns).
    public (int startY, int rows, int[] splitX, int[][] colMaps) SpellsHitInfo =>
        (_spellContentStartY, _spellRows, _spellSplitX, _spellColRowToItem);

    public void SetSubTabArrowHover(bool leftHovered, bool rightHovered)
    {
        if (leftHovered == _leftArrowHovered && rightHovered == _rightArrowHovered) return;
        _leftArrowHovered = leftHovered;
        _rightArrowHovered = rightHovered;
        RedrawSubTabTitle();
    }

    // Hovering over the «Level Up» button: the plate gets brighter (like the new game wizard buttons); only it is redrawn.
    private bool _levelUpHovered;
    private string _levelUpText = "";
    private bool _levelUpActive;

    public void SetLevelUpHover(bool hovered)
    {
        if (hovered == _levelUpHovered) return;
        _levelUpHovered = hovered;
        if (LevelUpHit is not { } h) return;
        int sl = Console.CursorLeft, st = Console.CursorTop;
        bool vis = OperatingSystem.IsWindows() && Console.CursorVisible;
        Console.CursorVisible = false;
        Console.SetCursorPosition(h.x + LevelUpKey.Length, h.y);
        WriteLevelUpButton();
        Console.SetCursorPosition(sl, st);
        Console.CursorVisible = vis;
    }

    private const string LevelUpKey = "[F10]";

    private void WriteLevelUpButton()
    {
        bool on = _levelUpActive && _levelUpHovered;
        var fg = !_levelUpActive ? ColorHelper.Darker(_display.MainForeground, 0.5) : on ? ColorHelper.Pale(LevelUpColor, 0.5) : LevelUpColor;
        var bg = ColorHelper.MixWith(_display.MainBackground, _display.MainForeground, on ? 0.42 : 0.16);
        ColorHelper.WriteColored(_levelUpText, fgColor: fg, bgColor: bg);
    }

    public void SetSubTabLabelHover(string? hovered) // "inv", "abil", "eff", or null
    {
        if (hovered == _hoveredSubTab) return;
        _hoveredSubTab = hovered;
        RedrawSubTabTitle();
    }

    // The "current/total" text between the ◄/► arrows lives in one place so the formula here and in SubTabInfo
    // (the ► position depends on this text's length) don't drift apart.
    private string SubTabPageText() => $"{_subTabPage + 1}/{_subTabPageCount}";

    private void RedrawSubTabTitle()
    {
        if (_cachedBorderDrawer == null || _subTabTitleY < 0) return;
        ColorHelper.SetBackgroundColor(_display.MainBackground);
        ColorHelper.SetForegroundColor(_display.MainForeground);
        Console.CursorVisible = false;
        int savedLeft = Console.CursorLeft, savedTop = Console.CursorTop;
        Console.SetCursorPosition(0, _subTabTitleY);
        DrawSubTabTitle(_display.CharacterSubTab);
        Console.SetCursorPosition(savedLeft, savedTop);
    }

    private void DrawSubTabTitle(CharacterSubTab active)
    {
        string activeKey = ActiveKey(active);
        var dim = _display.MainForeground;
        var hoverCol = ColorHelper.Pale(_display.MainForeground, 0.75);
        var arrowHi  = ColorHelper.Pale(_display.MainForeground, 0.75);
        var arrowInactive = ColorHelper.Darker(_display.MainForeground, 0.5);
        // Input is temporarily unavailable (the die is spinning): the [Fn] prefixes and both arrows are dimmed,
        // regardless of whether paging/hovering is actually possible right now.
        bool disabled = _display.InputDisabled;
        var leftColor  = disabled ? arrowInactive : !_canScrollLeft  ? arrowInactive : (_leftArrowHovered  ? arrowHi : _display.MainForeground);
        var rightColor = disabled ? arrowInactive : !_canScrollRight ? arrowInactive : (_rightArrowHovered ? arrowHi : _display.MainForeground);
        string pageText = SubTabPageText();

        _cachedBorderDrawer!.DrawTitleLine(() =>
        {
            _subTabContentStart = Console.CursorLeft;
            Console.Write(new string(' ', SubTabIndent));
            int written = SubTabIndent;
            for (int i = 0; i < _subTabs.Length; i++)
            {
                var (key, label, fkey) = _subTabs[i];
                bool active2 = key == activeKey;
                bool noSpells = key == "spell" && !HasSpells;
                var color = noSpells ? arrowInactive : active2 ? hoverCol : _hoveredSubTab == key ? hoverCol : dim;
                // The decorator is always SubTabPrefixLen/SubTabSuffixLen characters; its look changes,
                // not its length, so the tab name doesn't shift when switching.
                string prefix = active2 ? "  → " : $"[{fkey}]";
                string suffix = active2 ? " ←" : "  ";
                var prefixColor = disabled && !active2 ? arrowInactive : active2 ? color : MouseUiHelper.KeyHintColor(_display);
                if (!active2 && !disabled) MouseUiHelper.WriteKeyHint(prefix, _display);
                else ColorHelper.WriteColored(prefix, prefixColor);
                ColorHelper.WriteColored(label + suffix, color);
                written += SubTabPrefixLen + label.Length + SubTabSuffixLen;
                if (i < _subTabs.Length - 1)
                {
                    Console.Write(new string(' ', SubTabGap));
                    written += SubTabGap;
                }
            }
            int filler = Math.Max(0, SubTabBarContentLen - written);
            ColorHelper.WriteColored(new string(' ', filler), _display.MainForeground);
            ColorHelper.WriteColored("◄", leftColor);
            Console.Write(" ");
            Console.Write(pageText);
            Console.Write(" ");
            ColorHelper.WriteColored("►", rightColor);
        }, SubTabBarContentLen + pageText.Length + 4);
    }

    public HeroDisplay(WorldState settings, DisplayConfig display)
    {
        _settings = settings;
        _display = display;
    }

    private class ColoredLine
    {
        public string Prefix { get; set; }
        public string ColoredText { get; set; }
        public string Suffix { get; set; }
        public List<int>? Color { get; set; }
        public bool HasColor => Color != null && Color.Count == 3;
        public string? FullText { get; set; } // the value didn't fit and was cut with «...»: the full text for the marquee
        public string? KeyHint { get; set; }  // «[F10]» before the text, in the title's key-hint color
    }

    // ── Truncated sheet values and the marquee on hover ─────────────────────────
    // A value that doesn't fit in its column is drawn truncated with «...» (no line wrap); on mouse
    // hover, after a pause, it scrolls like a news ticker (UpdateMarquee from mouse polling).
    private readonly List<(int y, int x, int width, string text, List<int>? color)> _truncated = [];
    private int _marqueeIdx = -1;
    private DateTime _marqueeStart;
    private int _marqueeStep = -1;
    private const int MarqueeStepMs = 160;
    private const int MarqueePauseSteps = 4;
    private const string MarqueeGap = "   ";

    private static ColoredLine FitLine(string prefix, string text, int width, List<int>? color)
    {
        int available = Math.Max(1, width - prefix.Length);
        bool truncated = text.Length > available;
        string shown = truncated ? (available > 3 ? text[..(available - 3)] + "..." : text[..available]) : text;
        return new ColoredLine
        {
            Prefix = prefix,
            ColoredText = shown,
            Suffix = new string(' ', Math.Max(0, width - (prefix.Length + shown.Length))),
            Color = color,
            FullText = truncated ? text : null,
        };
    }

    public void UpdateMarquee((short x, short y)? mouse)
    {
        if (mouse is { } m)
        {
            int idx = _truncated.FindIndex(t => t.y == m.y && m.x >= t.x && m.x < t.x + t.width);
            if (idx != _marqueeIdx)
            {
                if (_marqueeIdx >= 0 && _marqueeIdx < _truncated.Count) DrawMarqueeFrame(_marqueeIdx, -1);
                _marqueeIdx = idx;
                _marqueeStart = DateTime.UtcNow;
                _marqueeStep = -1;
            }
        }
        if (_marqueeIdx < 0 || _marqueeIdx >= _truncated.Count) return;

        int step = Math.Max(0, (int)((DateTime.UtcNow - _marqueeStart).TotalMilliseconds / MarqueeStepMs) - MarqueePauseSteps);
        if (step == _marqueeStep) return;
        _marqueeStep = step;
        DrawMarqueeFrame(_marqueeIdx, step);
    }

    // step < 0: the original truncated view with «...»; otherwise a ticker window «text   text» shifted by step.
    private void DrawMarqueeFrame(int idx, int step)
    {
        var (y, x, width, text, color) = _truncated[idx];
        string frame;
        if (step < 0)
            frame = width > 3 ? text[..(width - 3)] + "..." : text[..width];
        else
        {
            string loop = text + MarqueeGap;
            int offset = step % loop.Length;
            frame = (loop + loop)[offset..(offset + width)];
        }

        bool vis = Console.CursorVisible;
        int sl = Console.CursorLeft, st = Console.CursorTop;
        Console.CursorVisible = false;
        Console.SetCursorPosition(x, y);
        if (color is { Count: 3 }) ColorHelper.WriteColored(frame, color);
        else Console.Write(frame);
        Console.SetCursorPosition(sl, st);
        Console.CursorVisible = vis;
    }

    private readonly List<(int y, int[] items, Action draw)> _selectionRows = [];
    private (int inv, int invPin, int spell, int spellPin) _selectionState;

    public void RefreshSelection()
    {
        var next = (_display.SelectedInventoryIndex, _display.PinnedInventoryIndex,
            _display.SelectedSpellIndex, _display.PinnedSpellIndex);
        if (next == _selectionState) return;
        int[] changed = [_selectionState.inv, _selectionState.invPin, _selectionState.spell,
            _selectionState.spellPin, next.Item1, next.Item2, next.Item3, next.Item4];
        int x = Console.CursorLeft, y = Console.CursorTop;
        bool visible = Console.CursorVisible;
        try
        {
            Console.CursorVisible = false;
            foreach (var row in _selectionRows)
                if (row.items.Any(i => i >= 0 && changed.Contains(i)))
                {
                    Console.SetCursorPosition(0, row.y);
                    row.draw();
                }
            _selectionState = next;
        }
        finally { Console.SetCursorPosition(x, y); Console.CursorVisible = visible; }
    }

    public void DrawHeroCard()
    {
        _selectionRows.Clear();
        _selectionState = (_display.SelectedInventoryIndex, _display.PinnedInventoryIndex, _display.SelectedSpellIndex, _display.PinnedSpellIndex);
        var settings = _settings;
        var hero = Preview ?? settings.Hero;
        if (hero == null) return;

        int startTop = Console.CursorTop;

        ColorHelper.SetBackgroundColor(_display.MainBackground);
        ColorHelper.SetForegroundColor(_display.MainForeground);

        int innerWidth = _display.InnerWidth(_display.ViewCols(settings.Map));
        var borderDrawer = new BorderDrawer(settings, _display);
        _cachedBorderDrawer = borderDrawer;

        borderDrawer.DrawSeparatorTop();

        int leftKeyMax = hero.EquipmentSlots.Max(s => s.Length);
        leftKeyMax = Math.Max(leftKeyMax, L.T("Inspiration").Length);
        leftKeyMax = Math.Max(leftKeyMax, hero.Resources?.Count > 0 ? hero.Resources.Max(s => s.Name.Length) : 0);

        // Race/Class are ordinary hero.stats (like Experience/Level etc.), but shown not in the stats
        // column but right under the name (they're the character's basics); they're pulled out of the stats separately,
        // before middleItems is built, so they aren't duplicated.
        string? StatValue(string key) => hero.Stat(key)?.Value;
        string? heroRace = StatValue(StatKeys.Race);
        string? heroClass = StatValue(StatKeys.Class);

        // Spellcasting stats follow the same logic: ordinary hero.stats, but shown not in the stats
        // column but in the dynamic block of column 1 (only on the Spells sub-tab), before the spell
        // slots (when those appear) and the resources of the "Spells" category.
        // Prepared/Known: the stat stores only the MAXIMUM (a number, by class/level rules);
        // the current number is always computed right here from hero.spells (not cantrips, Level > 0), rather than
        // kept as a separate number in the stats: the AI used to have to recount N by hand on
        // every spell prepared/unprepared and couldn't keep up, so the number drifted from the actual list.
        int preparedSpellCount = hero.Spells?.Count(s => s.Deleted != true && s.Level > 0 && s.Prepared == true) ?? 0;
        int knownSpellCount    = hero.Spells?.Count(s => s.Deleted != true && s.Level > 0) ?? 0;
        string? preparedMax = StatValue(StatKeys.SpellsPrepared);
        string? knownMax    = StatValue(StatKeys.SpellsKnown);

        // Level preview: a spellcasting stat changed, shown «before → after» in gold.
        string? SpellStat(string key, string? shown, string? before)
        {
            string? was = PreviewBase?.Stat(key)?.Value;
            return PreviewBase == null || was == hero.Stat(key)?.Value || shown == null ? shown : $"{before ?? was} → {shown}";
        }
        int baseSpells(bool prepared) => PreviewBase?.Spells?.Count(s => s.Deleted != true && s.Level > 0 && (!prepared || s.Prepared == true)) ?? 0;
        (string label, string? value)[] spellStatLines =
        [
            (L.T("Spellcasting abil."), SpellStat(StatKeys.SpellAbility, StatValue(StatKeys.SpellAbility), null)),
            (L.T("Spell save DC"), SpellStat(StatKeys.SpellSave, StatValue(StatKeys.SpellSave), null)),
            (L.T("Spell attack"), SpellStat(StatKeys.SpellAttack, StatValue(StatKeys.SpellAttack), null)),
            (L.T("Spells prepared"), SpellStat(StatKeys.SpellsPrepared, preparedMax != null ? $"{preparedSpellCount}/{preparedMax}" : null,
                $"{baseSpells(true)}/{PreviewBase?.Stat(StatKeys.SpellsPrepared)?.Value}")),
            (L.T("Spells known"), SpellStat(StatKeys.SpellsKnown, knownMax != null ? $"{knownSpellCount}/{knownMax}" : null,
                $"{baseSpells(false)}/{PreviewBase?.Stat(StatKeys.SpellsKnown)?.Value}")),
        ];
        leftKeyMax = Math.Max(leftKeyMax, spellStatLines.Max(l => l.label.Length));

        var leftSlots = new List<(string slotName, string itemName, List<int> itemColor)>
        {
            (L.T("Name").PadRight(leftKeyMax) + "   ", $"{hero.Symbol} {hero.Name}", hero.Color),
        };
        if (!string.IsNullOrEmpty(heroRace))
            leftSlots.Add((L.T("Race").PadRight(leftKeyMax) + "   ", heroRace, null));
        // Level (managed by the engine) is in the class line: «Fighter 1 lvl».
        var rules = LevelRules.Current;
        int level = LevelRules.LevelOf(hero);
        if (!string.IsNullOrEmpty(heroClass))
            leftSlots.Add((L.T("Class").PadRight(leftKeyMax) + "   ", L.F("{0} {1} lvl", heroClass, level),
                PreviewBase != null && LevelRules.LevelOf(PreviewBase) != level ? LevelUpColor : null));
        leftSlots.Add(("HP".PadRight(leftKeyMax) + "   ", (PreviewValue(PreviewBase?.Hp, hero.Hp) ?? hero.Hp) + " ❤",
            PreviewValue(PreviewBase?.Hp, hero.Hp) != null ? LevelUpColor : null));
        leftSlots.Add((L.T("Sight").PadRight(leftKeyMax) + "   ", SightText(hero), null));
        leftSlots.Add((L.T("Inspiration").PadRight(leftKeyMax) + "   ", hero.Inspiration == true ? L.T("Yes") : "—", null));

        // A divider right after the base block; next comes the contextual part of column 1, different
        // for each sub-tab: Inventory shows equipment slots, Features/Spells their resources
        // (Rage/Ki/Superiority Dice or slots etc., HeroResource.Category), Conditions nothing
        // (it has its own info below). Equipment slots used to show always; now only
        // on their own sub-tab, as originally intended.
        leftSlots.Add(("".PadRight(leftKeyMax) + "   ", "", null));

        switch (_display.CharacterSubTab)
        {
            case CharacterSubTab.Inventory:
                if (hero.EquipmentSlots != null && hero.EquipmentSlots.Any())
                {
                    var equipped = hero.Inventory?
                        .Where(i => i.EquipmentedSlot?.Count > 0)
                        .SelectMany(i => i.EquipmentedSlot!.Select(slot => (slot, i.Name, i.Color)))
                        .GroupBy(x => x.slot)
                        .ToDictionary(g => g.Key, g => (g.Last().Name, g.Last().Color)) ?? new();

                    foreach (var slot in hero.EquipmentSlots)
                    {
                        string prefixBase = $"{slot.PadRight(leftKeyMax)}   ";
                        if (equipped.TryGetValue(slot, out var itemInfo))
                            leftSlots.Add((prefixBase, itemInfo.Name, itemInfo.Color));
                        else
                            leftSlots.Add((prefixBase, "—", null));
                    }
                }
                else
                {
                    leftSlots.Add(("", L.T("(no slots)"), null));
                }
                break;

            case CharacterSubTab.Abilities:
                AddResourcesByCategory(ResourceCategories.Abilities);
                break;

            case CharacterSubTab.Spells:
                // Spellcasting stats come before spell slots (when they appear) and resources.
                foreach (var (label, value) in spellStatLines)
                    if (!string.IsNullOrEmpty(value))
                        leftSlots.Add((label.PadRight(leftKeyMax) + "   ", value, value.Contains(" → ") ? LevelUpColor : null));
                AddResourcesByCategory(ResourceCategories.Spells);
                break;
        }

        void AddResourcesByCategory(string category)
        {
            if (hero.Resources == null) return;
            foreach (var resource in hero.Resources.Where(r => r.Deleted != true
                && r.Category == category))
            {
                string? was = PreviewBase?.Resources?.FirstOrDefault(x => x.Deleted != true && x.Name == resource.Name)?.Value;
                string? changed = PreviewValue(was, resource.Value);
                leftSlots.Add((resource.Name.PadRight(leftKeyMax) + "   ", changed ?? resource.Value ?? "", changed != null ? LevelUpColor : null));
            }
        }

        var middleItems = new List<(string key, string value, List<int> valueColor)>();
        if (hero.Stats != null && hero.Stats.Any())
        {
            // Race/Class/spellcasting stats are already shown in column 1 (leftSlots); don't duplicate them.
            string[] shownElsewhere = [StatKeys.Race, StatKeys.Class, StatKeys.SpellAbility, StatKeys.SpellSave,
                StatKeys.SpellAttack, StatKeys.SpellsPrepared, StatKeys.SpellsKnown];
            // Experience is the engine's line at the end of the column; we don't duplicate the DM's old habit of an «Experience» stat.
            // Creature size and type in one line, «Creature: Medium humanoid» (the column is shorter, leaving room for the level button).
            var size = hero.Stats.FirstOrDefault(s => s.Deleted != true && CreatureNames.IsSize(s.Name));
            var type = hero.Stats.FirstOrDefault(s => s.Deleted != true && CreatureNames.IsType(s.Name));
            foreach (var stat in hero.Stats.Where(s => s.Deleted != true && !shownElsewhere.Contains(s.Key) && !ClassLevel.IsExperienceStat(s.Name)))
            {
                if (size != null && type != null && stat == type) continue;
                if (size != null && type != null && stat == size)
                {
                    string t = type.Value?.Trim() ?? "";
                    middleItems.Add((L.T("Creature"), $"{size.Value?.Trim()} {(t.Length > 0 ? char.ToLower(t[0]) + t[1..] : "")}".Trim(), null));
                    continue;
                }
            {
                string? changed = PreviewValue(PreviewBase?.Stats?.FirstOrDefault(x => x.Deleted != true && x.Name == stat.Name)?.Value, stat.Value);
                middleItems.Add((stat.Name, changed ?? stat.Value, changed != null ? LevelUpColor : null));
            }
            }
        }
        if (middleItems.Count == 0)
        {
            middleItems.Add(("", L.T("(no stats)"), null));
        }
        // Experience is the last line of the stats column; with enough XP, the «Level Up» button sits centered at the bottom of the column.
        middleItems.Add((L.T("Experience"), rules.NextXp(level) is int next ? $"{hero.Xp ?? 0}/{next}" : (hero.Xp ?? 0).ToString(), null));
        // Enough XP: the button is visible; in combat it is grey and can't be pressed (LevelUp.Available).
        bool showLevelUp = OfferLevelUp && Preview == null && hero.Dead != true && rules.CanLevelUp(hero);
        bool canLevelUp = showLevelUp && LevelUp.Available(_settings);

        var rightItems = new List<(string key, string value, List<int> valueColor)>();
        if (hero.Skills != null && hero.Skills.Any())
        {
            foreach (var skill in hero.Skills.Where(s => s.Deleted != true))
            {
                string? changed = PreviewValue(PreviewBase?.Skills?.FirstOrDefault(x => x.Deleted != true && x.Name == skill.Name)?.Value, skill.Value);
                rightItems.Add((skill.Name, changed ?? skill.Value, changed != null ? LevelUpColor : null));
            }
        }
        if (rightItems.Count == 0)
        {
            rightItems.Add(("", L.T("(no skills)"), null));
        }

        // -3: left padding at the start of each of the 3 columns (Console.Write(" ") below).
        int colWidth = (innerWidth - 2 - 3) / 3;
        int leftWidth = colWidth;
        int middleWidth = colWidth;
        int rightWidth = innerWidth - 2 - 3 - leftWidth - middleWidth;

        int middleKeyMax = middleItems.Max(r => r.key.Length);
        int rightKeyMax  = rightItems.Max(r => r.key.Length);

        // Values don't wrap: if it doesn't fit, it's cut with «...», and the full text scrolls as a marquee on hover.
        var leftLines = leftSlots.Select(slot => FitLine(slot.slotName, slot.itemName, leftWidth, slot.itemColor)).ToList();
        var middleLines = middleItems.Select(item => FitLine(item.key.PadRight(middleKeyMax) + "   ", item.value, middleWidth, item.valueColor)).ToList();
        var rightLines = rightItems.Select(item => FitLine(item.key.PadRight(rightKeyMax) + "   ", item.value, rightWidth, item.valueColor)).ToList();

        // The button is the last line of the block, separated from Experience by at least one empty line.
        int levelUpRow = -1;
        int maxLinesCount = Math.Max(leftLines.Count, Math.Max(middleLines.Count + (showLevelUp ? 2 : 0), rightLines.Count));
        if (showLevelUp)
        {
            while (middleLines.Count < maxLinesCount - 1) middleLines.Add(new ColoredLine { Prefix = "", ColoredText = "", Suffix = new string(' ', middleWidth), Color = null });
            // The «  LEVEL UP  » plate on a background, centered in the column; the «[F10]» hint is to its left and doesn't count for centering.
            const string key = LevelUpKey;
            string button = $"  {L.T("LEVEL UP")}  ";
            if (key.Length + button.Length > middleWidth) button = button[..Math.Max(0, middleWidth - key.Length)];
            int pad = Math.Max(key.Length, (middleWidth - button.Length) / 2) - key.Length;
            if (canLevelUp) levelUpRow = middleLines.Count;
            _levelUpText = button;
            _levelUpActive = canLevelUp;
            if (!canLevelUp) _levelUpHovered = false;
            middleLines.Add(new ColoredLine { Prefix = new string(' ', pad), KeyHint = key, ColoredText = button,
                Suffix = new string(' ', Math.Max(0, middleWidth - pad - key.Length - button.Length)),
                Color = canLevelUp ? LevelUpColor : ColorHelper.Darker(_display.MainForeground, 0.5) });
        }
        while (leftLines.Count   < maxLinesCount) leftLines.Add(new ColoredLine   { Prefix = "", ColoredText = "", Suffix = new string(' ', leftWidth),   Color = null });
        while (middleLines.Count < maxLinesCount) middleLines.Add(new ColoredLine { Prefix = "", ColoredText = "", Suffix = new string(' ', middleWidth), Color = null });
        while (rightLines.Count  < maxLinesCount) rightLines.Add(new ColoredLine  { Prefix = "", ColoredText = "", Suffix = new string(' ', rightWidth),  Color = null });

        _truncated.Clear();
        _marqueeIdx = -1;
        LevelUpHit = null;
        // Where the column text starts in a row: padding + frame + " " (column 1), then «│ » between columns.
        int leftX   = DisplayConfig.LeftMargin + 2;
        int middleX = leftX + leftWidth + 2;
        int rightX  = middleX + middleWidth + 2;

        for (int i = 0; i < maxLinesCount; i++)
        {
            var left   = leftLines[i];
            var middle = middleLines[i];
            var right  = rightLines[i];

            int rowY = Console.CursorTop;
            if (i == levelUpRow) LevelUpHit = (rowY, middleX + middle.Prefix.Length, (middle.KeyHint?.Length ?? 0) + middle.ColoredText.Length);   // with the hint
            if (left.FullText != null)   _truncated.Add((rowY, leftX + left.Prefix.Length, left.ColoredText.Length, left.FullText, left.Color));
            if (middle.FullText != null) _truncated.Add((rowY, middleX + middle.Prefix.Length, middle.ColoredText.Length, middle.FullText, middle.Color));
            if (right.FullText != null)  _truncated.Add((rowY, rightX + right.Prefix.Length, right.ColoredText.Length, right.FullText, right.Color));

            borderDrawer.DrawContentLine(() =>
            {
                Console.Write(" ");
                if (left.HasColor) { Console.Write(left.Prefix); ColorHelper.WriteColored(left.ColoredText, left.Color!); Console.Write(left.Suffix); }
                else               { Console.Write(left.Prefix); Console.Write(left.ColoredText); Console.Write(left.Suffix); }
                ColorHelper.WriteColored("│", MouseUiHelper.FrameColor(_display));
                Console.Write(" ");
                if (middle.KeyHint != null) { Console.Write(middle.Prefix); MouseUiHelper.WriteKeyHint(middle.KeyHint, _display); WriteLevelUpButton(); Console.Write(middle.Suffix); }
                else if (middle.HasColor) { Console.Write(middle.Prefix); ColorHelper.WriteColored(middle.ColoredText, middle.Color!); Console.Write(middle.Suffix); }
                else                 { Console.Write(middle.Prefix); Console.Write(middle.ColoredText); Console.Write(middle.Suffix); }
                ColorHelper.WriteColored("│", MouseUiHelper.FrameColor(_display));
                Console.Write(" ");
                if (right.HasColor) { Console.Write(right.Prefix); ColorHelper.WriteColored(right.ColoredText, right.Color!); Console.Write(right.Suffix); }
                else                { Console.Write(right.Prefix); Console.Write(right.ColoredText); Console.Write(right.Suffix); }
            });
        }

        borderDrawer.DrawSeparatorBottom();

        int dialogReserve = _display.MaxHistoryLines + 7;
        int availableForContent = Math.Max(1, Console.WindowHeight - Console.CursorTop - dialogReserve - 1);
        int statsDrawn = Console.CursorTop - startTop;
        int predictedNeeded = Math.Max(0, _display.MaxHigh - (statsDrawn + availableForContent - 2));
        int totalContent = availableForContent + predictedNeeded;

        switch (_display.CharacterSubTab)
        {
            case CharacterSubTab.Inventory:
                DrawInventorySection(hero, innerWidth, totalContent, borderDrawer);
                break;
            case CharacterSubTab.Abilities:
                DrawAbilitiesSection(hero, innerWidth, totalContent, borderDrawer);
                break;
            case CharacterSubTab.Spells:
                DrawSpellsSection(hero, innerWidth, totalContent, borderDrawer);
                break;
            default:
                DrawEffectsSection(hero, innerWidth, totalContent, borderDrawer);
                break;
        }

        int currentHeight = Console.CursorTop - startTop;
        _display.HeroHigh = currentHeight;
        int needed = _display.MaxHigh - currentHeight;

        if (_display.CharacterSubTab == CharacterSubTab.Spells)
        {
            var (w1, w2, w3, w4) = SpellColumnWidths(innerWidth);
            if (needed > 0)
            {
                for (int i = 0; i < needed; i++)
                    borderDrawer.DrawContentLine4Columns(() => {}, () => {}, () => {}, () => {}, w1, w2, w3, w4);
            }
            borderDrawer.DrawSeparatorWith4Parts('─', w1, w2, w3, w4);
        }
        else
        {
            int col1W = TwoColumnDividerX(innerWidth);
            int col2W = innerWidth - 1 - col1W;
            if (needed > 0)
            {
                for (int i = 0; i < needed; i++)
                    borderDrawer.DrawContentLine2Columns(() => {}, () => {}, col1W, col2W);
            }
            borderDrawer.DrawSeparatorWith2Parts('─', col1W, col2W);
        }
    }

    // Horizontal lines above/below the columns, without ┬ ┴ ┼: the vertical divider butts into a solid line.

    // Single source of truth for the divider position in 2-column sections (Inventory/Features/
    // Conditions); used both there and in SpellColumnWidths for the 2nd line, so they can't
    // drift apart when one is edited without the other.
    private static int TwoColumnDividerX(int innerWidth) => (innerWidth - 1) / 2 ;

    // The 2nd vertical line must match the shared Inventory/Features/Conditions divider
    // (same innerWidth, same formula); the 3rd line stays put (doesn't move); the difference
    // between them is taken up by the 3rd column, not the 1st/4th, so their positions don't shift either.
    private static (int w1, int w2, int w3, int w4) SpellColumnWidths(int innerWidth)
    {
        int available = innerWidth - 3; // 3 inner dividers between 4 columns
        int baseW = available / 4;
        int remainder = available - baseW * 4;

        int w1 = baseW + remainder - 2; // the 1st line is 1 character further left; div2/div3 don't move
        int w4 = baseW + 1;
        int div2Target = TwoColumnDividerX(innerWidth) - 1;
        int div3Target = w1 + baseW + (baseW - 1);  // the previous (correct) position of the 3rd line
        int w2 = div2Target - w1;
        int w3 = div3Target - div2Target + 2;

        return (w1, w2, w3, w4);
    }

    private record struct InvLine(int ItemIdx, bool IsFirst, string Marker, string Name, List<int>? Color, string Rest, List<int>? MarkerColor = null);

    private void DrawInventorySection(Hero hero, int innerWidth, int preTitleRows, BorderDrawer borderDrawer)
    {
        int col1W = TwoColumnDividerX(innerWidth);
        int col2W = innerWidth - 1 - col1W;
        int selIdx = _display.SelectedInventoryIndex;

        var flat = new List<InvLine>();
        var inventory = hero.Inventory ?? [];
        for (int i = 0; i < inventory.Count; i++)
        {
            var item = inventory[i];
            string marker = item.EquipmentedSlot?.Count > 0 ? "•" : " ";
            string name = item.Quantity is > 1 ? $"{item.Name} ×{item.Quantity}" : item.Name;
            int prefixLen = marker.Length + name.Length + 2;
            // -1: reserve for the "←" arrow on hover/selection; without it the description could
            // fill the whole column width exactly (written == colWidth), leaving no room for the arrow
            // (the "written < colWidth" condition in WriteInvLine is strict, so it silently wasn't drawn).
            int firstLineWidth = Math.Max(1, col1W - prefixLen - 1);
            // Continuation lines (marker=" ", no name) aren't constrained by the item name; they
            // get the full column width minus just the marker, not the narrow budget of the first line.
            int continuationWidth = Math.Max(1, col1W - 1);
            string desc = string.IsNullOrEmpty(item.Description) ? "" : $"  {item.Description}";
            var descLines = TextWrapper.WrapText(desc, continuationWidth, firstLineWidth);

            flat.Add(new InvLine(i, true, marker, name, item.Color,
                ": " + (descLines.Count > 0 ? descLines[0] : "")));
            for (int d = 1; d < descLines.Count; d++)
                flat.Add(new InvLine(i, false, " ", "", null, descLines[d]));
        }

        if (flat.Count == 0)
            flat.Add(new InvLine(-1, false, "", "", null, L.T("(empty)")));

        int rows = Math.Max(1, preTitleRows - 4);
        int pageSize = rows * 2;
        int pageCount = Math.Max(1, (int)Math.Ceiling((double)flat.Count / pageSize));
        int page = Math.Min(_display.InventoryScrollOffset, pageCount - 1);
        _display.InventoryScrollOffset = page;

        _canScrollLeft  = page > 0;
        _canScrollRight = page < pageCount - 1;
        _subTabPage = page;
        _subTabPageCount = pageCount;

        _subTabTitleY = Console.CursorTop;
        DrawSubTabTitle(CharacterSubTab.Inventory);
        borderDrawer.DrawSeparatorWith2Parts('─', col1W, col2W);

        int startIdx = page * pageSize;
        var col1 = flat.Skip(startIdx).Take(rows).ToList();
        var col2 = flat.Skip(startIdx + rows).Take(rows).ToList();

        _invContentStartY = Console.CursorTop;
        _invRows = rows;
        // Padding + frame(1) up to the start of col1, then col1W up to the column divider.
        _invSplitX = col1W + 1 + DisplayConfig.LeftMargin;
        _invCol1RowToItem = Enumerable.Range(0, rows).Select(r => r < col1.Count ? col1[r].ItemIdx : -1).ToArray();
        _invCol2RowToItem = Enumerable.Range(0, rows).Select(r => r < col2.Count ? col2[r].ItemIdx : -1).ToArray();

        var visible = col1.Concat(col2).ToList();
        var firstVisible = flat.Skip(startIdx).FirstOrDefault(e => e.IsFirst);
        _display.InventoryPageFirstItemIdx = firstVisible.IsFirst ? firstVisible.ItemIdx : -1;
        _display.InventoryPageLastItemIdx = visible.Count > 0 ? visible.Max(e => e.ItemIdx) : -1;
        int nextStart = startIdx + pageSize;
        var nextFirst = flat.Skip(nextStart).FirstOrDefault(e => e.IsFirst);
        _display.InventoryPageFirstNextItemIdx = nextFirst.IsFirst ? nextFirst.ItemIdx : -1;
        int prevStart = startIdx - pageSize;
        var prevFirst = page > 0 ? flat.Skip(prevStart).FirstOrDefault(e => e.IsFirst) : default;
        _display.InventoryPageFirstPrevItemIdx = page > 0 && prevFirst.IsFirst ? prevFirst.ItemIdx : -1;

        int pinIdx = _display.PinnedInventoryIndex;
        for (int row = 0; row < rows; row++)
        {
            bool h1 = row < col1.Count;
            bool h2 = row < col2.Count;
            var e1 = h1 ? col1[row] : default;
            var e2 = h2 ? col2[row] : default;
            void DrawRow() => borderDrawer.DrawContentLine2Columns(
                () => { if (h1) WriteInvLine(e1, col1W, e1.ItemIdx == _display.SelectedInventoryIndex,
                    e1.ItemIdx == (_display.PinnedInventoryIndex >= 0 ? _display.PinnedInventoryIndex : _display.SelectedInventoryIndex)); },
                () => { if (h2) WriteInvLine(e2, col2W, e2.ItemIdx == _display.SelectedInventoryIndex,
                    e2.ItemIdx == (_display.PinnedInventoryIndex >= 0 ? _display.PinnedInventoryIndex : _display.SelectedInventoryIndex)); },
                col1W, col2W);
            _selectionRows.Add((Console.CursorTop, [h1 ? e1.ItemIdx : -1, h2 ? e2.ItemIdx : -1], DrawRow));
            DrawRow();
        }

        int fillerRows = Math.Max(0, preTitleRows - 4 - rows);
        for (int i = 0; i < fillerRows; i++)
            borderDrawer.DrawContentLine2Columns(() => {}, () => {}, col1W, col2W);
    }

    private void DrawAbilitiesSection(Hero hero, int innerWidth, int preTitleRows, BorderDrawer borderDrawer)
    {
        int col1W = TwoColumnDividerX(innerWidth);
        int col2W = innerWidth - 1 - col1W;

        var flat = new List<InvLine>();
        var abilities = hero.Abilities ?? [];
        for (int i = 0; i < abilities.Count; i++)
        {
            var ability = abilities[i];
            bool isNew = IsNewInPreview(ability.Name);
            string marker = isNew ? "+" : " ";
            string name = ability.Name;
            int prefixLen = marker.Length + name.Length + 2;
            // -1: reserve for the "←" arrow on hover/selection; without it the description could
            // fill the whole column width exactly (written == colWidth), leaving no room for the arrow
            // (the "written < colWidth" condition in WriteInvLine is strict, so it silently wasn't drawn).
            int firstLineWidth = Math.Max(1, col1W - prefixLen - 1);
            int continuationWidth = Math.Max(1, col1W - 1);
            string desc = string.IsNullOrEmpty(ability.Description) ? "" : $"  {ability.Description}";
            var descLines = TextWrapper.WrapText(desc, continuationWidth, firstLineWidth);

            flat.Add(new InvLine(i, true, marker, name, ability.Color,
                ": " + (descLines.Count > 0 ? descLines[0] : ""), isNew ? LevelUpColor : null));
            for (int d = 1; d < descLines.Count; d++)
                flat.Add(new InvLine(i, false, " ", "", null, descLines[d]));
        }

        if (flat.Count == 0)
            flat.Add(new InvLine(-1, false, "", "", null, L.T("(no features)")));

        int rows = Math.Max(1, preTitleRows - 4);
        int pageSize = rows * 2;
        int pageCount = Math.Max(1, (int)Math.Ceiling((double)flat.Count / pageSize));
        int page = Math.Min(_display.AbilityPageOffset, pageCount - 1);
        _display.AbilityPageOffset = page;

        _canScrollLeft  = page > 0;
        _canScrollRight = page < pageCount - 1;
        _subTabPage = page;
        _subTabPageCount = pageCount;

        _invContentStartY = -1;
        _invRows = 0;

        _subTabTitleY = Console.CursorTop;
        DrawSubTabTitle(CharacterSubTab.Abilities);
        borderDrawer.DrawSeparatorWith2Parts('─', col1W, col2W);

        int startIdx = page * pageSize;
        var col1 = flat.Skip(startIdx).Take(rows).ToList();
        var col2 = flat.Skip(startIdx + rows).Take(rows).ToList();

        for (int row = 0; row < rows; row++)
        {
            bool h1 = row < col1.Count;
            bool h2 = row < col2.Count;
            var e1 = h1 ? col1[row] : default;
            var e2 = h2 ? col2[row] : default;

            borderDrawer.DrawContentLine2Columns(
                () => { if (h1) WriteInvLine(e1, col1W, false); },
                () => { if (h2) WriteInvLine(e2, col2W, false); },
                col1W, col2W
            );
        }

        int fillerRows = Math.Max(0, preTitleRows - 4 - rows);
        for (int i = 0; i < fillerRows; i++)
            borderDrawer.DrawContentLine2Columns(() => {}, () => {}, col1W, col2W);
    }

    private static string EffectDuration(StatusEffect e) => e.ExpiresAtRound.HasValue
        ? L.F("until round {0}", e.ExpiresAtRound)
        : e.UntilLongRest == true ? L.T("until long rest") : L.T("indefinite");

    private void DrawEffectsSection(Hero hero, int innerWidth, int preTitleRows, BorderDrawer borderDrawer)
    {
        int col1W = TwoColumnDividerX(innerWidth);
        int col2W = innerWidth - 1 - col1W;

        var flat = new List<InvLine>();
        var effects = hero.Effects?.Where(e => e.Deleted != true).ToList() ?? [];
        for (int i = 0; i < effects.Count; i++)
        {
            var effect = effects[i];
            string marker = " ";
            string name = effect.Name;
            int prefixLen = marker.Length + name.Length + 2;
            // -1: reserve for the "←" arrow on hover/selection; without it the description could
            // fill the whole column width exactly (written == colWidth), leaving no room for the arrow
            // (the "written < colWidth" condition in WriteInvLine is strict, so it silently wasn't drawn).
            int firstLineWidth = Math.Max(1, col1W - prefixLen - 1);
            int continuationWidth = Math.Max(1, col1W - 1);
            string duration = EffectDuration(effect);
            string desc = string.IsNullOrEmpty(effect.Description)
                ? $"  {duration}"
                : $"  {duration} — {effect.Description}";
            var descLines = TextWrapper.WrapText(desc, continuationWidth, firstLineWidth);

            flat.Add(new InvLine(i, true, marker, name, effect.Color,
                ": " + (descLines.Count > 0 ? descLines[0] : "")));
            for (int d = 1; d < descLines.Count; d++)
                flat.Add(new InvLine(i, false, " ", "", null, descLines[d]));
        }

        int rows = Math.Max(1, preTitleRows - 4);
        int pageSize = rows * 2;
        int pageCount = Math.Max(1, (int)Math.Ceiling((double)flat.Count / pageSize));
        int page = Math.Min(_display.EffectsPageOffset, pageCount - 1);
        _display.EffectsPageOffset = page;

        _canScrollLeft  = page > 0;
        _canScrollRight = page < pageCount - 1;
        _subTabPage = page;
        _subTabPageCount = pageCount;

        _invContentStartY = -1;
        _invRows = 0;

        _subTabTitleY = Console.CursorTop;
        DrawSubTabTitle(CharacterSubTab.Effects);
        borderDrawer.DrawSeparatorWith2Parts('─', col1W, col2W);

        int startIdx = page * pageSize;
        var col1 = flat.Skip(startIdx).Take(rows).ToList();
        var col2 = flat.Skip(startIdx + rows).Take(rows).ToList();

        for (int row = 0; row < rows; row++)
        {
            bool h1 = row < col1.Count;
            bool h2 = row < col2.Count;
            var e1 = h1 ? col1[row] : default;
            var e2 = h2 ? col2[row] : default;

            borderDrawer.DrawContentLine2Columns(
                () => { if (h1) WriteInvLine(e1, col1W, false); },
                () => { if (h2) WriteInvLine(e2, col2W, false); },
                col1W, col2W
            );
        }

        int fillerRows = Math.Max(0, preTitleRows - 4 - rows);
        for (int i = 0; i < fillerRows; i++)
            borderDrawer.DrawContentLine2Columns(() => {}, () => {}, col1W, col2W);
    }

    // D&D 5e has no "0th" level in Roman numerals: a cantrip (level 0) gets its own heading.
    private static string LevelToRoman(int level) => level switch
    {
        1 => "I", 2 => "II", 3 => "III", 4 => "IV", 5 => "V",
        6 => "VI", 7 => "VII", 8 => "VIII", 9 => "IX",
        _ => level.ToString(),
    };

    private static string SpellColumnHeader(int level) => level == 0 ? L.T("CANTRIPS") : L.F("LEVEL {0}", LevelToRoman(level));

    // Single source of truth for spell order/filtering: used by both drawing (grouping by
    // level) and Program.cs (Tab/Shift+Tab), so both places refer to the same index.
    public static List<HeroSpell> SortedSpells(Hero hero) =>
        (hero.Spells ?? []).Where(s => s.Deleted != true).OrderBy(s => s.Level).ThenBy(s => s.Name).ToList();

    // 4 columns, each its own spell level. A level that doesn't fit in one column takes
    // several columns in a row (the same heading repeats), and the following levels shift right;
    // so the columns are built as a flat list (level, chunk, global index of the first element
    // in SortedSpells) and only then cut into 4 per page.
    private void DrawSpellsSection(Hero hero, int innerWidth, int preTitleRows, BorderDrawer borderDrawer)
    {
        var (col1W, col2W, col3W, col4W) = SpellColumnWidths(innerWidth);

        int rows = Math.Max(1, preTitleRows - 4);
        int itemsPerCol = Math.Max(1, rows - 2); // level heading + divider under it

        var sorted = SortedSpells(hero);
        var columns = new List<(string Header, List<HeroSpell> Items, int GlobalStart)>();
        int globalOffset = 0;
        foreach (var group in sorted.GroupBy(s => s.Level).OrderBy(g => g.Key))
        {
            var items = group.ToList();
            for (int i = 0; i < items.Count; i += itemsPerCol)
                columns.Add((SpellColumnHeader(group.Key), items.Skip(i).Take(itemsPerCol).ToList(), globalOffset + i));
            globalOffset += items.Count;
        }
        if (columns.Count == 0)
            columns.Add(("", [], -1));

        int pageCount = Math.Max(1, (int)Math.Ceiling(columns.Count / 4.0));
        int page = Math.Min(_display.SpellsPageOffset, pageCount - 1);
        _display.SpellsPageOffset = page;

        _canScrollLeft  = page > 0;
        _canScrollRight = page < pageCount - 1;
        _subTabPage = page;
        _subTabPageCount = pageCount;

        _invContentStartY = -1;
        _invRows = 0;

        _subTabTitleY = Console.CursorTop;
        DrawSubTabTitle(CharacterSubTab.Spells);
        borderDrawer.DrawSeparatorWith4Parts('─', col1W, col2W, col3W, col4W);

        var pageColumns = columns.Skip(page * 4).Take(4).ToList();
        var visible = pageColumns.ToList();
        while (visible.Count < 4)
            visible.Add(("", [], -1));

        // Bounds for Tab/Shift+Tab page flipping, like InventoryPageFirst*/Last*.
        _display.SpellsPageFirstItemIdx = pageColumns.Count > 0 && pageColumns[0].Items.Count > 0
            ? pageColumns[0].GlobalStart : -1;
        var lastCol = pageColumns.LastOrDefault(c => c.Items.Count > 0);
        _display.SpellsPageLastItemIdx = lastCol.Items is { Count: > 0 }
            ? lastCol.GlobalStart + lastCol.Items.Count - 1 : -1;
        _display.SpellsPageFirstNextItemIdx = page < pageCount - 1 && columns[(page + 1) * 4].Items.Count > 0
            ? columns[(page + 1) * 4].GlobalStart : -1;
        _display.SpellsPageFirstPrevItemIdx = page > 0 ? columns[(page - 1) * 4].GlobalStart : -1;

        static string CenterHeader(string text, int width)
        {
            if (text.Length >= width) return text[..width];
            int pad = width - text.Length;
            int left = pad / 2;
            return new string(' ', left) + text + new string(' ', pad - left);
        }

        borderDrawer.DrawContentLine4Columns(
            () => ColorHelper.WriteColored(CenterHeader(visible[0].Header, col1W), _display.MainForeground),
            () => ColorHelper.WriteColored(CenterHeader(visible[1].Header, col2W), _display.MainForeground),
            () => ColorHelper.WriteColored(CenterHeader(visible[2].Header, col3W), _display.MainForeground),
            () => ColorHelper.WriteColored(CenterHeader(visible[3].Header, col4W), _display.MainForeground),
            col1W, col2W, col3W, col4W
        );
        borderDrawer.DrawInnerSeparatorWith4Parts(col1W, col2W, col3W, col4W);

        _spellContentStartY = Console.CursorTop;
        _spellRows = itemsPerCol;
        int leftContentX = DisplayConfig.LeftMargin + 1;
        _spellSplitX =
        [
            leftContentX + col1W,
            leftContentX + col1W + 1 + col2W,
            leftContentX + col1W + 1 + col2W + 1 + col3W,
        ];
        _spellColRowToItem = new int[4][];
        for (int c = 0; c < 4; c++)
        {
            var map = new int[itemsPerCol];
            for (int r = 0; r < itemsPerCol; r++)
                map[r] = r < visible[c].Items.Count ? visible[c].GlobalStart + r : -1;
            _spellColRowToItem[c] = map;
        }

        int selIdx = _display.SelectedSpellIndex;
        int pinIdx = _display.PinnedSpellIndex;

        for (int row = 0; row < itemsPerCol; row++)
        {
            int r = row;
            InvLine Line(int c)
            {
                var spell = visible[c].Items[r];
                bool isNew = IsNewInPreview(spell.Name);
                string marker = isNew ? "+" : spell.Prepared == true ? "•" : " ";
                return new InvLine(visible[c].GlobalStart + r, true, marker, spell.Name, spell.Color, "", isNew ? LevelUpColor : null);
            }
            bool Selected(int c) => r < visible[c].Items.Count && visible[c].GlobalStart + r == _display.SelectedSpellIndex;
            bool Arrow(int c) => r < visible[c].Items.Count &&
                (_display.PinnedSpellIndex >= 0 ? visible[c].GlobalStart + r == _display.PinnedSpellIndex : Selected(c));

            void DrawRow() => borderDrawer.DrawContentLine4Columns(
                () => { if (r < visible[0].Items.Count) WriteInvLine(Line(0), col1W, Selected(0), Arrow(0)); },
                () => { if (r < visible[1].Items.Count) WriteInvLine(Line(1), col2W, Selected(1), Arrow(1)); },
                () => { if (r < visible[2].Items.Count) WriteInvLine(Line(2), col3W, Selected(2), Arrow(2)); },
                () => { if (r < visible[3].Items.Count) WriteInvLine(Line(3), col4W, Selected(3), Arrow(3)); },
                col1W, col2W, col3W, col4W
            );
            _selectionRows.Add((Console.CursorTop, Enumerable.Range(0, 4).Select(c => r < visible[c].Items.Count ? visible[c].GlobalStart + r : -1).ToArray(), DrawRow));
            DrawRow();
        }

        int fillerRows2 = Math.Max(0, preTitleRows - 4 - (itemsPerCol + 2));
        for (int i = 0; i < fillerRows2; i++)
            borderDrawer.DrawContentLine4Columns(() => {}, () => {}, () => {}, () => {}, col1W, col2W, col3W, col4W);
    }

    private void WriteInvLine(InvLine line, int colWidth, bool selected, bool? showArrow = null)
    {
        bool arrow = showArrow ?? selected;
        var selFg = selected ? ColorHelper.Pale(_display.MainForeground, 0.6) : null;
        int written = 0;
        if (line.MarkerColor != null) ColorHelper.WriteColored(line.Marker, line.MarkerColor);
        else if (selFg != null) ColorHelper.WriteColored(line.Marker, selFg); else Console.Write(line.Marker);
        written += line.Marker.Length;
        var nameColor = selected && line.Color != null ? ColorHelper.Pale(line.Color, 0.6) : line.Color;
        if (nameColor != null && nameColor.Count == 3)
            ColorHelper.WriteColored(line.Name, nameColor);
        else if (selFg != null)
            ColorHelper.WriteColored(line.Name, selFg);
        else
            Console.Write(line.Name);
        written += line.Name.Length;
        if (selFg != null) ColorHelper.WriteColored(line.Rest, selFg); else Console.Write(line.Rest);
        written += line.Rest.Length;
        if (arrow && line.IsFirst && written < colWidth)
        {
            int remaining = colWidth - written - 1;
            if (remaining > 0) Console.Write(new string(' ', remaining));
            ColorHelper.WriteColored("←", selFg ?? ColorHelper.Pale(_display.MainForeground, 0.6));
        }
    }
}
