using NaviDnD.Data.Models;

namespace NaviDnD.Data;

public enum CharacterSubTab { Inventory, Abilities, Effects, Spells }

// What the map loop shows: the location (F1 screen) or the world map (F4 screen). A location can be larger
// than the screen — it's viewed through a camera (CameraCol/Row); there's no separate "district". The world is a test map for now.
public enum MapLevel { World, Location }

public class DisplayConfig
{
    public const int LegendPanelWidth = 58; //38;
    private const int BaseCols = 20;

    // Left margin before the frame (BorderDrawer.WriteLeftMargin) — for symmetry with the mandatory
    // guard column on the right (see ConsoleSetup.SetConsoleConfig: without it auto-wrap on the
    // last buffer column breaks SafeNewLine). A single point of change — used in every
    // absolute screen column calculation (mouse clicks, hit-testing map cells, etc.).
    public const int LeftMargin = 1;

    public int CellWidth { get; init; } = 3;
    public char Delimetr { get; init; } = ' ';
    public int MaxHistoryLines { get; init; } = 21;

    // Scroll of the tactical map legend list (lines from the top), mouse wheel over the legend.
    public int LegendScroll { get; set; }

    // Map height (15 rows, see DungeonGenerator.MaxRows / HardcodedCaveGenerator.Rows) at the current cell width.
    // Serves as a "floor" for MaxHigh until the map has been drawn even once (e.g. in the character editor when creating a game) —
    // without it the character/abilities card in the editor would be shorter than during play.
    public const int FixedMapContentHigh = 34;

    public int MapHigh { get; set; }
    public int HeroHigh { get; set; }
    public int AbilityHigh { get; set; }
    public int MaxHigh => Math.Max(Math.Max(MapHigh, HeroHigh), Math.Max(AbilityHigh, FixedMapContentHigh));

    public List<int> Door { get; init; } = [128, 128, 128 ];
    public  List<int> MainBackground { get; init; }= [18, 18, 22];
    public List<int> MainForeground { get; init; } = [160, 210, 160];
    public List<int> SystemCommandHistory { get; init; } = [120, 120, 130];

    public bool ArrowKeysMovement { get; set; } = false;
    public int CombatReservedLines { get; init; } = 8;   // turn order, 3 actions, path, movement, place, time

    public int DialogRightPanelWidth { get; set; } = 36;

    public int DialogScrollOffset { get; set; } = 0;
    public int InventoryScrollOffset { get; set; } = 0;
    public int AbilityScrollOffset { get; set; } = 0;
    public int AbilityPageOffset { get; set; } = 0;
    public int EffectsPageOffset { get; set; } = 0;
    public int SpellsPageOffset { get; set; } = 0;

    public CharacterSubTab CharacterSubTab { get; set; } = CharacterSubTab.Inventory;
    public MapLevel MapLevel { get; set; } = MapLevel.Location;

    // ── Location map camera ───────────────────────────────────────────────────
    // The map can be larger than the screen: a window of at most ViewportCols×ViewportRows cells is shown.
    // CameraCol/CameraRow — map coordinates of the window's bottom-left cell. The whole screen layout (width,
    // legend, frames) is computed from the window size (ViewCols/ViewRows), not the whole map.
    public const int ViewportCols = 20;
    public const int ViewportRows = 15;
    public int CameraCol { get; set; } = 1;
    public int CameraRow { get; set; } = 1;
    // The window is always ViewportCols×ViewportRows: a location is a field of up to MapConfig.MaxSize×MaxSize that
    // the camera moves over; cells outside the generated map are just void (unexplored).
    public int ViewCols(MapConfig map) => ViewportCols;
    public int ViewRows(MapConfig map) => ViewportRows;

    // Window cell (1..ViewCols, 1..ViewRows; ConsoleMouseReader.ScreenToMapCell) → map cell.
    public (int col, int row)? ScreenCellToWorld((int col, int row)? cell) =>
        cell is { } c ? (c.col + CameraCol - 1, c.row + CameraRow - 1) : null;
    // World map camera: the center (in world cells) is dragged with the mouse; zoom with the wheel/Ctrl ±
    // (0 — detailed, a cell = 2 characters; 1..4 — world cells per half-block "pixel", WorldMapView).
    public double WorldCenterX { get; set; } = -1;
    public double WorldCenterY { get; set; } = -1;
    public int WorldZoomStep { get; set; } = 3;

    // Travel on the world map: the chosen destination (a cell), the route to it (cells) and the pace (0 — slow,
    // 1 — normal, 2 — fast). The destination is picked by clicking the map, [F10] — travel, [F12] — pace.
    public (int x, int y)? WorldTarget { get; set; }
    public List<(int x, int y)>? WorldRoute { get; set; }
    // Summary of the chosen route (for the legend without recomputing): where to, days at normal pace, road share.
    public (string dest, double days, double road)? WorldRouteInfo { get; set; }
    public int TravelPace { get; set; } = 1;
    // The world place under the cursor (index into WorldMap.Places) or -1.
    public int HoveredWorldPlace { get; set; } = -1;
    // The selected world place (Tab/click) — highlighted on the map, its card on the right; -1 — none.
    public int SelectedWorldPlace { get; set; } = -1;
    // World map filter: which categories to show (WorldMapView.Categories).
    public HashSet<string> WorldFilter { get; set; } = ["settlements", "sites", "nature"];

    // "F1"/"F2"/"F3" — which of the current screen's top-level tabs is active now (Program.cs
    // sets it per screen). Used only for sound: the key of an already active tab is a no-op.
    public string? ActiveTabKey { get; set; }

    // Input is temporarily unavailable (e.g. while the die is rolling) — tab/arrow rendering should
    // draw itself dimmed (ColorHelper.Darker) without waiting for an actual click/hover.
    public bool InputDisabled { get; set; }

    public string PendingInputText { get; set; } = "";
    public string? PendingCommand { get; set; }

    public int SelectedMapItemIndex { get; set; } = -1;
    public int SelectedInventoryIndex { get; set; } = -1;

    // The "pinned" inventory item (click or Tab/Shift+Tab) — unlike SelectedInventoryIndex
    // (which mouse hover also moves and resets when the mouse leaves), this survives the mouse leaving
    // the inventory: the pinned item's picture/note stays while the mouse hovers over the picture's own
    // page arrows (otherwise you couldn't get there — leaving the inventory would reset the display).
    public int PinnedInventoryIndex { get; set; } = -1;
    public int NotePage { get; set; } = 0;
    public int NotePageCount { get; set; } = 0;

    public int InventoryPageFirstItemIdx { get; set; } = -1;
    public int InventoryPageLastItemIdx { get; set; } = -1;
    public int InventoryPageFirstNextItemIdx { get; set; } = -1;
    public int InventoryPageFirstPrevItemIdx { get; set; } = -1;

    // The same hover/pin principle as the inventory (see PinnedInventoryIndex), except the
    // index is a position in HeroDisplay.SortedSpells(hero), not in the original hero.Spells.
    public int SelectedSpellIndex { get; set; } = -1;
    public int PinnedSpellIndex { get; set; } = -1;

    public int SpellsPageFirstItemIdx { get; set; } = -1;
    public int SpellsPageLastItemIdx { get; set; } = -1;
    public int SpellsPageFirstNextItemIdx { get; set; } = -1;
    public int SpellsPageFirstPrevItemIdx { get; set; } = -1;

    public string[]? SelectedImageLines { get; set; }
    public List<int>? SelectedImageColor { get; set; }
    // Spell card: the spell color paints only the first line (the name); the rest of the text
    // (school/fields/description) uses the normal color. Items/entities are colored entirely (flag = false).
    public bool SelectedImageColorTitleOnly { get; set; }
    // Multicolor lines of the picture panel (monster card): segments per line, a null color is the main one.
    // They apply only while SelectedImageLines is the same array they were built for
    // (any other SetSelected* replaces SelectedImageLines, and the old segments are ignored automatically).
    public List<(string text, List<int>? color)>?[]? SelectedImageSegments { get; set; }
    public string[]? SelectedImageSegmentsFor { get; set; }
    public string[]? DefaultImageLines { get; set; }
    public List<int>? DefaultImageColor { get; set; }

    public int MapDrawTop { get; set; }
    public (int col, int row)? HoveredCell { get; set; }
    public Door? HoveredDoor { get; set; }
    public bool MapHoverEnabled { get; set; } = true;
    public string? StreamingTabTitle { get; set; }
    public Action? PollAction { get; set; }
    public Action? OnMapRedraw { get; set; }
    // Spot redraw of map cells (without recomputing visibility) — for the select_target preview.
    public Action<IEnumerable<(int col, int row)>>? OnMapCellsRedraw { get; set; }
    // Active target selection (select_target): highlighted cells on the map. null — no selection.
    public TargetSelectionView? TargetSelection { get; set; }
    // The cell under the cursor: distance, and in combat on the hero's turn — the path and a preview of the cost in the movement bar.
    public HoverPathInfo? HoverPath { get; set; }
    // A map element pinned by a click: its picture comes back when the mouse leaves other cells.
    public CellEntity? PinnedMapEntity { get; set; }
    // How many dialog messages were already on screen: when a new one arrives, the dialog jumps to the last page.
    public int DialogSeenMessageCount { get; set; } = -1;
    public Func<string, (Action? draw, string? title)>? TabSwitchProvider { get; set; }

    // Poll action factories: called with current DialogDisplay (as object) to create fresh poll action on tab switch
    public Func<object, Action?>? CharacterPollActionFactory { get; set; }
    // Journal ([F3]): mouse polling (bookmarks, paging, hovering an entry) and Tab selection; JournalShown — the journal
    // is on screen now (then F6–F8 are its bookmarks, not the character subtabs).
    public Func<object, Action?>? JournalPollActionFactory { get; set; }
    public Action? JournalOnTabKey { get; set; }
    public Action? JournalOnShiftTabKey { get; set; }
    public bool JournalShown { get; set; }
    // Show "[F1]" key hints (AppConfig.ShowKeyHints); when off, spaces of the same length take their place.
    public bool ShowKeyHints { get; set; } = true;
    public Func<object, Action?>? MapPollActionFactory { get; set; }
    public Func<object, Action?>? AbilitiesPollActionFactory { get; set; }

    // Tab/ShiftTab key handlers active on current tab (for streaming context)
    public Action? OnTabKey { get; set; }
    public Action? OnShiftTabKey { get; set; }
    // Per-tab stored Tab handlers — restored by SwitchTab when switching tabs
    public Action? MapOnTabKey { get; set; }
    public Action? MapOnShiftTabKey { get; set; }
    public Action? CharacterOnTabKey { get; set; }
    public Action? CharacterOnShiftTabKey { get; set; }
    // Redraws the current tab's main content (not the dialog block); saves/restores cursor
    public Action? RedrawCurrentContent { get; set; }

    /// <summary>
    /// Calculate legend width. If cols < 20, width increases by (20 - cols) * 4.
    /// </summary>
    public int GetLegendWidth(int cols)
    {
        int extraWidth = Math.Max(0, (BaseCols - cols) * (CellWidth + 1));
        return LegendPanelWidth + extraWidth;
    }

    public int InnerWidth(int cols) => cols * (CellWidth + 1) + 8 + GetLegendWidth(cols);
}

// Hint about the cell under the cursor (a line of the combat block). ShowPath — combat and the hero's turn: show the path,
// otherwise only the distance. CostFt: null — unreachable; TargetSymbol — the cursor is on a creature (path to an adjacent cell).
public sealed record HoverPathInfo(int DistanceFt, bool ShowPath, int? CostFt, string? TargetSymbol);

public class TargetSelectionView
{
    public HashSet<(int col, int row)> Selectable { get; } = [];
    // Origin of the shape (the hero for a cone/line): the cell itself isn't painted, but the lines/corners around it
    // count as highlighted — the shape visually grows out of the hero.
    public (int col, int row)? Origin { get; set; }
    public HashSet<(int col, int row)> Preview { get; set; } = [];
    public HashSet<(int col, int row)> Selected { get; } = [];
}
