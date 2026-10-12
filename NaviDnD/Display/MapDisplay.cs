using NaviDnD.Data;
using NaviDnD.Helpers;

namespace NaviDnD.Display;

public class MapDisplay
{
    private readonly WorldState _settings;
    private readonly DisplayConfig _display;
    private readonly LegendDisplay _legendDisplay;

    public MapDisplay(WorldState settings, DisplayConfig display, LegendDisplay legendDisplay)
    {
        _settings = settings;
        _display = display;
        _legendDisplay = legendDisplay;
        _legendDisplay.WorldLegend = () => (WorldView.World,
            WorldView.VisiblePlaces(MapAreaWidth, MapAreaHeight, _display.WorldCenterX, _display.WorldCenterY, _display.WorldZoomStep, _display.WorldFilter));
    }

    public void DrawMap(MapObjectsProvider provider)
    {
        var settings = _settings;
        int startTop = Term.CursorTop;

        var mapBg = settings.Map.Colors.MapBackground;
        var mapFg = settings.Map.Colors.MapForeground;
        var mainBg = _display.MainBackground;
        var mainFg = _display.MainForeground;

        ColorHelper.SetBackgroundColor(mapBg);
        ColorHelper.SetForegroundColor(mapFg);

        string Delimetr(int n) => new string(_display.Delimetr, n);

        // Viewport (camera): vc×vr cells on screen, the bottom-left one is (c0, r0) in map coordinates.
        // The provider gets map coordinates, and the numbers along the edges are map coordinates too.
        ClampCamera();
        int cols = _display.ViewCols(settings.Map), rows = _display.ViewRows(settings.Map);
        var cellWidth = _display.CellWidth;

        // Calculate map width and legend width
        int baseMapWidth = cols * (cellWidth + 1) + 7;
        int legendWidth = _display.GetLegendWidth(cols);
        int mapWidth = baseMapWidth;

        var borderDrawer = new BorderDrawer(settings, _display);

        // Pre-build legend lines
        var legendLines = _legendDisplay.GetLegendLines();
        int separatorIndex = _legendDisplay.SeparatorLineIndex;
        int terrainSeparator = _legendDisplay.TerrainSeparatorIndex;
        int li = 0;

        // Always advances li by 1; returns content action or empty.
        Action GetLegend()
        {
            int current = li++;
            return current < legendLines.Count
                ? () =>
                {
                    ColorHelper.SetBackgroundColor(mainBg);
                    ColorHelper.SetForegroundColor(mainFg);
                    legendLines[current]();
                    ColorHelper.SetBackgroundColor(mapBg);
                    ColorHelper.SetForegroundColor(mapFg);
                }
                : () => { };
        }

        // Draws one legend row: separator with ├┤ when at separatorIndex (above the combat block), normal content otherwise.
        // World: the same row layout, with the test world map (WriteWorldRow) instead of cells.
        bool worldMap = _display.MapLevel == MapLevel.World;
        int gridRow = 0; // row number within the map area (0 = column numbers, 1 = top border, …)

        void DrawRow(Action leftContent)
        {
            int myRow = gridRow++;
            if (worldMap) leftContent = () => WriteWorldRow(myRow);
            if (li == separatorIndex)
            {
                li++;
                borderDrawer.DrawContentLine2ColumnsRightSeparator(leftContent, mapWidth, legendWidth, _legendDisplay.SeparatorJunction);
            }
            else if (li == terrainSeparator)
            {
                // Above the terrain block: ├────┬────┤, with ┬ where the line between its columns runs down.
                li++;
                borderDrawer.DrawContentLine2ColumnsRightSeparator(leftContent, mapWidth, legendWidth,
                    _legendDisplay.SeparatorJunction, BorderDrawer.JunctionDown);
            }
            else
            {
                borderDrawer.DrawContentLine2Columns(leftContent, GetLegend(), mapWidth, legendWidth);
            }
        }

        // Separator with 2-column divider
        borderDrawer.DrawSeparatorWith2Parts('┬', mapWidth, legendWidth);

        // Map area rows: column numbers, borders, cells (BuildLocationRows); on the World map, WriteWorldRow.
        var mapRows = worldMap ? null : BuildLocationRows(provider);
        for (int i = 0; i < MapAreaHeight; i++)
            DrawRow(mapRows?[i] ?? (() => { }));

        ColorHelper.SetBackgroundColor(mainBg);
        ColorHelper.SetForegroundColor(mainFg);

        int currentHeight = Term.CursorTop - startTop;
        _display.MapHigh = currentHeight;
        int needed = _display.MaxHigh - currentHeight;
        if (needed > 0)
        {
            for (int i = 0; i < needed; i++)
            {
                if (Term.CursorTop >= Term.WindowHeight - 1) break;
                DrawRow(() => { });
            }
        }

        borderDrawer.DrawSeparatorWith2Parts('┴', mapWidth, legendWidth);
        DrawEndLeft = Term.CursorLeft;
        DrawEndTop = Term.CursorTop;

        // Legend snapshot: RedrawAfterMove redraws only the rows that changed.
        _legendSnapshot = worldMap ? null : legendLines.Select(CaptureOutput).ToList();
        _legendSnapshotSeparator = separatorIndex;
        _legendRowsDrawn = gridRow;
        _junctionSnapshot = _legendDisplay.SeparatorJunction;
        _terrainSeparatorSnapshot = terrainSeparator;
    }

    private int _terrainSeparatorSnapshot = -1;

    // Where the cursor was left after DrawMap: everything below the map is drawn from there.
    public int DrawEndLeft { get; private set; }
    public int DrawEndTop { get; private set; }

    private List<string>? _legendSnapshot;
    private int _legendSnapshotSeparator = -1;
    private int _legendRowsDrawn;

    private static string CaptureOutput(Action write)
    {
        var console = Console.Out;
        var sw = new StringWriter();
        Console.SetOut(sw);
        try { write(); }
        finally { Console.SetOut(console); }
        return sw.ToString();
    }

    // Hero step / camera shift: the map area in one write (as when dragging) plus only the changed
    // legend rows. A full DrawMap on every step meant thousands of small console writes and the camera "jerked".
    // If the legend layout changed (combat started, a different number of rows), redraw everything.
    public void RedrawAfterMove(MapObjectsProvider provider)
    {
        var lines = _legendDisplay.GetLegendLines();
        if (_display.MapLevel != MapLevel.Location || _legendSnapshot == null
            || lines.Count != _legendSnapshot.Count || _legendDisplay.SeparatorLineIndex != _legendSnapshotSeparator)
        {
            Term.SetCursorPosition(0, _display.MapDrawTop);
            DrawMap(provider);
            return;
        }

        WriteRowsBatched(BuildLocationRows(provider));
        RedrawChangedLegendLines(lines);
    }

    // Only the changed legend rows (hover: room highlight, path). If the layout changed,
    // do nothing: the next full DrawMap will redraw it.
    public void RefreshLegend()
    {
        var lines = _legendDisplay.GetLegendLines();
        if (_display.MapLevel != MapLevel.Location || _legendSnapshot == null
            || lines.Count != _legendSnapshot.Count || _legendDisplay.SeparatorLineIndex != _legendSnapshotSeparator) return;
        RedrawChangedLegendLines(lines);
    }

    private void RedrawChangedLegendLines(List<Action> lines)
    {
        for (int i = 0; i < lines.Count && i < _legendRowsDrawn; i++)
        {
            string text = CaptureOutput(lines[i]);
            if (text != _legendSnapshot![i]) RedrawLegendLine(i, lines);
        }

        // The separator above the terrain block moved/appeared/disappeared: the old row becomes an ordinary legend
        // row again (RedrawLegendLine restores its │ │ borders), the new one becomes ├──┬──┤.
        int terrainSep = _legendDisplay.TerrainSeparatorIndex;
        if (terrainSep != _terrainSeparatorSnapshot)
        {
            int old = _terrainSeparatorSnapshot;
            _terrainSeparatorSnapshot = terrainSep;
            if (old >= 0 && old < _legendRowsDrawn) RedrawLegendLine(old, lines);
            if (terrainSep >= 0 && terrainSep < _legendRowsDrawn)
            {
                Term.SetCursorPosition(LegendX - 1, _display.MapDrawTop + 1 + terrainSep);
                ColorHelper.WriteColored("├" + BorderDrawer.SeparatorLine(_display.GetLegendWidth(ViewCols),
                        _legendDisplay.SeparatorJunction, BorderDrawer.JunctionDown) + "┤",
                    fgColor: MouseUiHelper.FrameColor(_display), bgColor: _display.MainBackground);
            }
        }

        // Terrain appeared/disappeared above the combat block separator: redraw the joint (┴) on the separator.
        var junction = _legendDisplay.SeparatorJunction;
        int sep = _legendDisplay.SeparatorLineIndex;
        if (junction != _junctionSnapshot && sep >= 0 && sep < _legendRowsDrawn)
        {
            Term.SetCursorPosition(LegendX, _display.MapDrawTop + 1 + sep);
            ColorHelper.WriteColored(BorderDrawer.SeparatorLine(_display.GetLegendWidth(ViewCols), junction),
                fgColor: MouseUiHelper.FrameColor(_display), bgColor: _display.MainBackground);
            _junctionSnapshot = junction;
        }
    }

    private int? _junctionSnapshot;

    // Location map area rows from top to bottom (MapAreaHeight of them): column numbers, top border,
    // cells/grid lines, bottom border, column numbers. The same ones for DrawMap and the fast redraw
    // while dragging (RedrawLocationViewport).
    private List<Action> BuildLocationRows(MapObjectsProvider provider)
    {
        ClampCamera();
        var mainBg = _display.MainBackground;
        var mainFg = MouseUiHelper.FrameColor(_display);   // cell numbers along the edges use the border color
        int cellWidth = _display.CellWidth;
        int c0 = _display.CameraCol, r0 = _display.CameraRow;
        int topRow = TopRow, rightCol = RightCol;
        string Delimetr(int n) => new string(_display.Delimetr, n);

        void ColumnNumbers()
        {
            ColorHelper.WriteColored(Delimetr(cellWidth + 2), fgColor: mainFg, bgColor: mainBg);
            for (int c = c0; c <= rightCol; c++)
            {
                ColorHelper.WriteColored(c.ToString(), fgColor: mainFg, bgColor: mainBg);
                if (c < rightCol)
                {
                    int spaces = cellWidth - (c.ToString().Length - 1); // 1–3 digits, the column step stays the same
                    ColorHelper.WriteColored(Delimetr(spaces), fgColor: mainFg, bgColor: mainBg);
                }
            }
        }

        // Horizontal grid line at height y (between map rows), with corners.
        void GridLine(double y, CornerType left, CornerType middle, CornerType right, bool trailingPad)
        {
            ColorHelper.WriteColored(Delimetr(cellWidth), fgColor: mainFg, bgColor: mainBg);
            provider.WriteCorner(c0 - 0.5, y, left);
            for (int c = c0; c <= rightCol; c++)
            {
                provider.WriteLineHorisontal(c, y);
                provider.WriteCorner(c + 0.5, y, c == rightCol ? right : middle);
            }
            if (trailingPad) ColorHelper.WriteColored(Delimetr(cellWidth), fgColor: mainFg, bgColor: mainBg);
        }

        var rows = new List<Action>
        {
            ColumnNumbers,
            () => GridLine(topRow + 0.5, CornerType.TopLeft, CornerType.TopMiddle, CornerType.TopRight, false),
        };
        for (int row = topRow; row >= r0; row--)
        {
            int r = row;
            rows.Add(() =>
            {
                WriteRowNumbers(r, cellWidth, mainBg, mainFg);
                for (int c = c0; c <= rightCol; c++)
                {
                    provider.WriteLineVertical(c - 0.5, r);
                    provider.WriteCellEntity(c, r);
                }
                provider.WriteLineVertical(rightCol + 0.5, r);
                WriteRowNumbers(r, cellWidth, mainBg, mainFg);
            });
            if (row > r0)
                rows.Add(() => GridLine(r - 0.5, CornerType.LeftMiddle, CornerType.Cross, CornerType.RightMiddle, true));
        }
        rows.Add(() => GridLine(r0 - 0.5, CornerType.BottomLeft, CornerType.BottomMiddle, CornerType.BottomRight, true));
        rows.Add(() =>
        {
            ColumnNumbers();
            ColorHelper.WriteColored(Delimetr(2), fgColor: mainFg, bgColor: mainBg);
        });
        return rows;
    }

    // Only the map area (no legend/screen borders), on every drag step. Each row
    // is drawn into memory and the whole frame goes to the console in ONE write (row positions via ANSI CUP):
    // with normal output every colored fragment is a separate console write, thousands per frame.
    private static readonly System.Text.RegularExpressions.Regex AnsiEscape =
        new(@"\x1b\[[0-9;]*[A-Za-z]", System.Text.RegularExpressions.RegexOptions.Compiled);

    private void WriteRowsBatched(IReadOnlyList<Action> rows)
    {
        var mapBg = _settings.Map.Colors.MapBackground ?? _display.MainBackground;
        var mapFg = _settings.Map.Colors.MapForeground ?? _display.MainForeground;
        var bg = _display.MainBackground;
        var frame = new System.Text.StringBuilder();
        var console = Console.Out;
        try
        {
            for (int i = 0; i < rows.Count; i++)
            {
                var row = new StringWriter();
                Console.SetOut(row);
                ColorHelper.SetBackgroundColor(mapBg);
                ColorHelper.SetForegroundColor(mapFg);
                rows[i]();
                string text = row.ToString();
                int visible = AnsiEscape.Replace(text, "").Length;
                frame.Append($"\x1b[{_display.MapDrawTop + 1 + i + 1};{DisplayConfig.LeftMargin + 2}H").Append(text);
                if (visible < MapAreaWidth)
                    frame.Append($"\x1b[48;2;{bg[0]};{bg[1]};{bg[2]}m").Append(' ', MapAreaWidth - visible);
            }
        }
        finally
        {
            Console.SetOut(console);
        }
        Console.Write(AnsiColorRuns.Compact(frame.ToString()));
        ColorHelper.SetBackgroundColor(_display.MainBackground);
        ColorHelper.SetForegroundColor(_display.MainForeground);
    }

    public void RedrawLocationViewport(MapObjectsProvider provider, bool refreshLegend = true)
    {
        if (_display.MapLevel != MapLevel.Location) return;
        WriteRowsBatched(BuildLocationRows(provider));
        if (refreshLegend) RefreshLegend(); // the legend is whatever is in the map window: it changes after the camera moves
    }

    // ── Camera ────────────────────────────────────────────────────────────────
    private int ViewCols => _display.ViewCols(_settings.Map);
    private int ViewRows => _display.ViewRows(_settings.Map);
    private int TopRow => _display.CameraRow + ViewRows - 1;
    private int RightCol => _display.CameraCol + ViewCols - 1;

    // The camera stays within the location field 1..MapConfig.MaxSize: it can't be dragged further.
    public void ClampCamera()
    {
        _display.CameraCol = Math.Clamp(_display.CameraCol, 1, NaviDnD.Data.Models.MapConfig.MaxSize - ViewCols + 1);
        _display.CameraRow = Math.Clamp(_display.CameraRow, 1, NaviDnD.Data.Models.MapConfig.MaxSize - ViewRows + 1);
    }

    // The hero stays at least FollowMargin cells from the window edge; otherwise the camera moves after him.
    private const int FollowMargin = 2;
    // The hero is centered in the window on the first map display after the game starts and whenever the hero is outside the window
    // (new game, load). After that the camera only follows him (FollowHero) or is dragged with the mouse.
    private bool _centeredOnce;
    // The hero's whole floor fits in the window (a single-block location): the camera covers it entirely and doesn't move
    // after the hero; the hero is wherever he stands, not necessarily centered. true means the camera was placed.
    public bool LockToFloorIfFits()
    {
        if (_settings.Hero?.Position is not { Count: >= 2 } hp || _settings.Map.Chunks is not { Count: > 0 } chunks) return false;
        int z = _settings.Map.FloorAt(hp[0], hp[1]);
        var floor = chunks.Where(c => c.Z == z).ToList();
        if (floor.Count == 0) return false;
        int c0 = floor.Min(c => c.OriginCol) + 1, c1 = floor.Max(c => c.OriginCol) + NaviDnD.Data.Models.MapChunk.Cols;
        int r0 = floor.Min(c => c.OriginRow) + 1, r1 = floor.Max(c => c.OriginRow) + NaviDnD.Data.Models.MapChunk.Rows;
        if (c1 - c0 + 1 > ViewCols || r1 - r0 + 1 > ViewRows) return false;
        _display.CameraCol = c0 - (ViewCols - (c1 - c0 + 1)) / 2;
        _display.CameraRow = r0 - (ViewRows - (r1 - r0 + 1)) / 2;
        ClampCamera();
        _centeredOnce = true;
        return true;
    }

    public void CenterOnHeroIfNeeded(bool force = false)
    {
        if (LockToFloorIfFits()) return;
        if (_settings.Hero?.Position is not { Count: >= 2 } hp) return;
        if (!force && _centeredOnce && InView(hp[0], hp[1])) return;
        _centeredOnce = true;
        _display.CameraCol = hp[0] - ViewCols / 2;
        _display.CameraRow = hp[1] - ViewRows / 2;
        ClampCamera();
        MoveToMostMap(hp, keepCurrent: false);
    }

    public void FollowHero()
    {
        if (LockToFloorIfFits()) return;
        if (_settings.Hero?.Position is not { Count: >= 2 } hp) return;
        int mx = Math.Min(FollowMargin, (ViewCols - 1) / 2), my = Math.Min(FollowMargin, (ViewRows - 1) / 2);
        int oldCol = _display.CameraCol, oldRow = _display.CameraRow;
        if (hp[0] < _display.CameraCol + mx) _display.CameraCol = hp[0] - mx;
        if (hp[0] > RightCol - mx) _display.CameraCol = hp[0] - (ViewCols - 1 - mx);
        if (hp[1] < _display.CameraRow + my) _display.CameraRow = hp[1] - my;
        if (hp[1] > TopRow - my) _display.CameraRow = hp[1] - (ViewRows - 1 - my);
        ClampCamera();
        // The camera moved after the hero: settle where the window shows the most map (no emptiness outside blocks).
        if (_display.CameraCol != oldCol || _display.CameraRow != oldRow) MoveToMostMap(hp, keepCurrent: true);
    }

    // The map window goes where it covers the most generated blocks of the hero's floor (don't show emptiness outside
    // blocks), while the hero stays visible and no closer than FollowMargin to the edge. The window may sit on the seam of two blocks. Among equals,
    // prefer the one closer to the current camera position (keepCurrent: following, no jumps) or with the hero centered. Old-format
    // maps (without blocks) stay as they are.
    private void MoveToMostMap(List<int> hp, bool keepCurrent)
    {
        if (_settings.Map.Chunks is not { Count: > 0 } chunks) return;
        int z = _settings.Map.FloorAt(hp[0], hp[1]);
        var rects = chunks.Where(c => c.Z == z)
            .Select(c => (c0: c.OriginCol + 1, c1: c.OriginCol + NaviDnD.Data.Models.MapChunk.Cols,
                          r0: c.OriginRow + 1, r1: c.OriginRow + NaviDnD.Data.Models.MapChunk.Rows)).ToList();
        if (rects.Count == 0) return;
        int mx = Math.Min(FollowMargin, (ViewCols - 1) / 2), my = Math.Min(FollowMargin, (ViewRows - 1) / 2);
        int max = NaviDnD.Data.Models.MapConfig.MaxSize;
        int prefCol = keepCurrent ? _display.CameraCol : hp[0] - ViewCols / 2;
        int prefRow = keepCurrent ? _display.CameraRow : hp[1] - ViewRows / 2;
        (int col, int row, int area, int dist)? best = null;
        for (int col = Math.Max(1, hp[0] - (ViewCols - 1 - mx)); col <= Math.Min(max - ViewCols + 1, hp[0] - mx); col++)
            for (int row = Math.Max(1, hp[1] - (ViewRows - 1 - my)); row <= Math.Min(max - ViewRows + 1, hp[1] - my); row++)
            {
                int area = 0;
                foreach (var (c0, c1, r0, r1) in rects)
                {
                    int w = Math.Min(c1, col + ViewCols - 1) - Math.Max(c0, col) + 1;
                    int h = Math.Min(r1, row + ViewRows - 1) - Math.Max(r0, row) + 1;
                    if (w > 0 && h > 0) area += w * h;
                }
                int dist = Math.Abs(col - prefCol) + Math.Abs(row - prefRow);
                if (best == null || area > best.Value.area || (area == best.Value.area && dist < best.Value.dist))
                    best = (col, row, area, dist);
            }
        if (best is { } b) { _display.CameraCol = b.col; _display.CameraRow = b.row; }
    }

    // Dragging the camera with the mouse: characters → cells (cell = CellWidth+1 characters × 2 rows).
    // Drag right/down and the map follows the cursor (camera moves left/up; map rows grow bottom to top).
    public void PanCamera(int startCol, int startRow, int dxChars, int dyChars)
    {
        _display.CameraCol = startCol - (int)Math.Round(dxChars / (double)(_display.CellWidth + 1));
        _display.CameraRow = startRow + (int)Math.Round(dyChars / 2.0);
        ClampCamera();
    }

    // Mouse → map cell/wall: ConsoleMouseReader counts in window cells (1..ViewCols); we offset by the camera.
    public (int col, int row)? ScreenToCell(short x, short y) =>
        _display.ScreenCellToWorld(ConsoleMouseReader.ScreenToMapCell(x, y, _display.MapDrawTop, ViewCols, ViewRows, _display.CellWidth));

    public (int col, int row, bool isHorizontal)? ScreenToWall(short x, short y) =>
        ConsoleMouseReader.ScreenToMapWall(x, y, _display.MapDrawTop, ViewCols, ViewRows, _display.CellWidth) is { } w
            ? (w.col + _display.CameraCol - 1, w.row + _display.CameraRow - 1, w.isHorizontal)
            : null;

    public bool HeroInView() => _settings.Hero?.Position is { Count: >= 2 } hp && InView(hp[0], hp[1]);

    private bool InView(int col, int row) =>
        col >= _display.CameraCol && col <= RightCol && row >= _display.CameraRow && row <= TopRow;

    public void RedrawVerticalWall(int leftCol, int row, MapObjectsProvider provider)
    {
        if (_display.MapLevel != MapLevel.Location) return; // the world map has no cells
        if (row < _display.CameraRow || row > TopRow || leftCol < _display.CameraCol - 1 || leftCol > RightCol) return;
        int cellW = _display.CellWidth + 1;
        int screenY = _display.MapDrawTop + 3 + (TopRow - row) * 2;
        int screenX = DisplayConfig.LeftMargin + 4 + (leftCol - _display.CameraCol + 1) * cellW;

        Term.SetCursorPosition(screenX, screenY);
        ColorHelper.SetBackgroundColor(_settings.Map.Colors.MapBackground ?? _display.MainBackground);
        ColorHelper.SetForegroundColor(_settings.Map.Colors.MapForeground ?? _display.MainForeground);

        provider.WriteLineVertical(leftCol + 0.5, row);

        ColorHelper.SetBackgroundColor(_display.MainBackground);
        ColorHelper.SetForegroundColor(_display.MainForeground);
    }

    public void RedrawHorizontalWall(int col, int upperRow, MapObjectsProvider provider)
    {
        if (_display.MapLevel != MapLevel.Location) return;
        if (col < _display.CameraCol || col > RightCol || upperRow < _display.CameraRow || upperRow > TopRow + 1) return;
        int cellW = _display.CellWidth + 1;
        int screenY = _display.MapDrawTop + 3 + (TopRow - upperRow) * 2 + 1;
        int screenX = DisplayConfig.LeftMargin + _display.CellWidth + 2 + (col - _display.CameraCol) * cellW;

        Term.SetCursorPosition(screenX, screenY);
        ColorHelper.SetBackgroundColor(_settings.Map.Colors.MapBackground ?? _display.MainBackground);
        ColorHelper.SetForegroundColor(_settings.Map.Colors.MapForeground ?? _display.MainForeground);

        provider.WriteLineHorisontal(col, upperRow - 0.5);

        ColorHelper.SetBackgroundColor(_display.MainBackground);
        ColorHelper.SetForegroundColor(_display.MainForeground);
    }

    // Screen column where the right part (legend) starts: margin + border + map width (baseMapWidth in DrawMap) + separator.
    public int LegendX => DisplayConfig.LeftMargin + 1 + (ViewCols * (_display.CellWidth + 1) + 7) + 1;

    // A single legend row (right column) without redrawing the map, for hover hints.
    // Legend row i is drawn on screen row MapDrawTop + 1 + i (DrawMap: the ┬ separator, then DrawRow for each).
    public void RedrawLegendLine(int index) => RedrawLegendLine(index, _legendDisplay.GetLegendLines());

    private void RedrawLegendLine(int index, List<Action> lines)
    {
        if (index < 0 || index >= lines.Count || index == _legendDisplay.SeparatorLineIndex
            || index == _legendDisplay.TerrainSeparatorIndex) return;
        if (_legendSnapshot != null && index < _legendSnapshot.Count) _legendSnapshot[index] = CaptureOutput(lines[index]);

        int x = LegendX;
        int width = _display.GetLegendWidth(ViewCols);
        // Row borders (│ left and right): where the terrain separator used to be, there were ├ ┤.
        Term.SetCursorPosition(x - 1, _display.MapDrawTop + 1 + index);
        ColorHelper.WriteColored("│", fgColor: MouseUiHelper.FrameColor(_display), bgColor: _display.MainBackground);
        ColorHelper.SetBackgroundColor(_display.MainBackground);
        ColorHelper.SetForegroundColor(_display.MainForeground);

        lines[index]();
        int rest = width - (Term.CursorLeft - x);
        if (rest > 0) Console.Write(new string(' ', rest));
        Term.SetCursorPosition(x + width, _display.MapDrawTop + 1 + index);
        ColorHelper.WriteColored("│", fgColor: MouseUiHelper.FrameColor(_display), bgColor: _display.MainBackground);
    }

    // A cell together with its frame: both vertical lines, the horizontal lines above/below and 4 corners.
    // Needed when highlighting depends on neighbors (select_target also colors the lines between cells).
    public void RedrawCellFrame(int col, int row, MapObjectsProvider provider)
    {
        if (_display.MapLevel != MapLevel.Location || !InView(col, row)) return;
        RedrawCell(col, row, provider);
        RedrawVerticalWall(col, row, provider);
        RedrawBoundarySegment(col, row + 0.5, provider);
        RedrawBoundarySegment(col, row - 0.5, provider);
    }

    // A stretch of horizontal grid line above/below a cell, with the corners at its ends.
    private void RedrawBoundarySegment(int col, double y, MapObjectsProvider provider)
    {
        int cellW = _display.CellWidth + 1;
        int screenY = _display.MapDrawTop + 4 + (int)Math.Round((TopRow - y - 0.5) * 2);
        int screenX = DisplayConfig.LeftMargin + 4 + (col - _display.CameraCol) * cellW;

        Term.SetCursorPosition(screenX, screenY);
        ColorHelper.SetBackgroundColor(_settings.Map.Colors.MapBackground ?? _display.MainBackground);
        ColorHelper.SetForegroundColor(_settings.Map.Colors.MapForeground ?? _display.MainForeground);

        provider.WriteCorner(col - 0.5, y, CornerTypeAt(col - 0.5, y));
        provider.WriteLineHorisontal(col, y);
        provider.WriteCorner(col + 0.5, y, CornerTypeAt(col + 0.5, y));

        ColorHelper.SetBackgroundColor(_display.MainBackground);
        ColorHelper.SetForegroundColor(_display.MainForeground);
    }

    // The same corner-type choice as in DrawMap, by position in the viewport.
    private CornerType CornerTypeAt(double x, double y)
    {
        bool top = y > TopRow, bottom = y < _display.CameraRow;
        bool left = x < _display.CameraCol, right = x > RightCol;
        return (top, bottom, left, right) switch
        {
            (true, _, true, _)  => CornerType.TopLeft,
            (true, _, _, true)  => CornerType.TopRight,
            (true, _, _, _)     => CornerType.TopMiddle,
            (_, true, true, _)  => CornerType.BottomLeft,
            (_, true, _, true)  => CornerType.BottomRight,
            (_, true, _, _)     => CornerType.BottomMiddle,
            (_, _, true, _)     => CornerType.LeftMiddle,
            (_, _, _, true)     => CornerType.RightMiddle,
            _                   => CornerType.Cross
        };
    }

    public void RedrawCell(int col, int row, MapObjectsProvider provider)
    {
        if (_display.MapLevel != MapLevel.Location || !InView(col, row)) return;
        var settings = _settings;
        int cellW = _display.CellWidth + 1;
        int screenY = _display.MapDrawTop + 3 + (TopRow - row) * 2;
        int screenX = DisplayConfig.LeftMargin + 4 + (col - _display.CameraCol) * cellW;

        Term.SetCursorPosition(screenX, screenY);
        ColorHelper.SetBackgroundColor(settings.Map.Colors.MapBackground ?? _display.MainBackground);
        ColorHelper.SetForegroundColor(settings.Map.Colors.MapForeground ?? _display.MainForeground);

        provider.WriteLineVertical(col - 0.5, row);
        provider.WriteCellEntity(col, row);

        if (col == RightCol)
            provider.WriteLineVertical(col + 0.5, row);

        // Restore main colors so _currentForeground/_currentBackground stay consistent for subsequent rendering
        ColorHelper.SetBackgroundColor(_display.MainBackground);
        ColorHelper.SetForegroundColor(_display.MainForeground);
    }

    // ── World map (WorldMapView over WorldLibrary.Current), dragged with the mouse, zoomed with the wheel ─────
    // Map area on screen: the width of DrawMap's left column and all its rows (numbers, borders, cells).
    public int MapAreaWidth => ViewCols * (_display.CellWidth + 1) + 7;
    public int MapAreaHeight => ViewRows * 2 + 3;

    private WorldMapView? _worldView;

    // View of the game world (GameWorld: geometry + places known to the hero); when the world changes, the camera shows the whole world;
    // when a place is added, the camera stays.
    public WorldMapView WorldView
    {
        get
        {
            var world = GameWorld.ForDisplay(_settings);
            if (_worldView?.World != world)
            {
                bool sameWorld = _worldView?.World.Id == world.Id;
                _worldView = new WorldMapView(world, MapAreaWidth, MapAreaHeight);
                _display.HoveredWorldPlace = -1;
                _display.SelectedWorldPlace = -1;
                if (!sameWorld)
                {
                    // A new world on screen: if there's a hero, the camera is on him, zoomed in (cell = pixel); otherwise the whole world.
                    _display.WorldTarget = null;
                    _display.WorldRoute = null;
                    if (GameWorld.HeroTile(_settings) is { } h)
                    {
                        _display.WorldZoomStep = Math.Min(2, _worldView.MaxZoom);
                        _display.WorldCenterX = h.x + 0.5;
                        _display.WorldCenterY = h.y + 0.5;
                    }
                    else
                    {
                        _display.WorldZoomStep = _worldView.MaxZoom;
                        _display.WorldCenterX = world.Width / 2.0;
                        _display.WorldCenterY = world.Height / 2.0;
                    }
                }
            }
            // Hero and route on top of the map (the frame is rebuilt if they changed).
            _worldView.Hero = GameWorld.HeroTile(_settings);
            _worldView.HeroColor = WorldMapView.MarkerColor;   // same as the start marker during game creation
            _worldView.Route = _display.WorldRoute;
            return _worldView;
        }
    }

    public void ClampWorldCamera()
    {
        var w = WorldView.World;
        // Don't zoom out beyond "world fits the window", and the camera doesn't go past the world's edge.
        _display.WorldZoomStep = Math.Clamp(_display.WorldZoomStep, WorldMapView.MinZoom, WorldView.MaxZoom);
        var (pc, pr) = WorldScale;
        double halfW = MapAreaWidth / 2.0 * pc, halfH = MapAreaHeight / 2.0 * pr;
        _display.WorldCenterX = halfW * 2 >= w.Width ? w.Width / 2.0 : Math.Clamp(_display.WorldCenterX, halfW, w.Width - halfW);
        _display.WorldCenterY = halfH * 2 >= w.Height ? w.Height / 2.0 : Math.Clamp(_display.WorldCenterY, halfH, w.Height - halfH);
    }

    // World cells per character horizontally / per row vertically.
    private (double perCol, double perRow) WorldScale
    {
        get { double s = WorldView.Scale(_display.WorldZoomStep); return (s, 2 * s); }
    }

    // Dragging: cursor shift in characters → camera shift in world cells.
    public void PanWorld(double startCenterX, double startCenterY, int dxChars, int dyChars)
    {
        var (pc, pr) = WorldScale;
        _display.WorldCenterX = startCenterX - dxChars * pc;
        _display.WorldCenterY = startCenterY - dyChars * pr;
        ClampWorldCamera();
    }

    // Wheel: step ±1; the world point under the cursor stays under the cursor (like zoom in browser maps).
    // Keyboard zoom (Ctrl+"+"/"−") is relative to the center of the map area.
    public void ZoomWorldAtCenter(int notches) =>
        ZoomWorld(notches, ((short)(DisplayConfig.LeftMargin + 1 + MapAreaWidth / 2), (short)(_display.MapDrawTop + 1 + MapAreaHeight / 2)));

    public void ZoomWorld(int notches, (short x, short y) cursor)
    {
        int oldZoom = _display.WorldZoomStep;
        int newZoom = Math.Clamp(oldZoom - notches, WorldMapView.MinZoom, WorldView.MaxZoom);
        if (newZoom == oldZoom) return;
        double sx = cursor.x - (DisplayConfig.LeftMargin + 1) - MapAreaWidth / 2.0;   // characters from the center
        double sy = cursor.y - (_display.MapDrawTop + 1) - MapAreaHeight / 2.0;
        var (pc0, pr0) = WorldScale;
        double wx = _display.WorldCenterX + sx * pc0, wy = _display.WorldCenterY + sy * pr0;
        _display.WorldZoomStep = newZoom;
        var (pc1, pr1) = WorldScale;
        _display.WorldCenterX = wx - sx * pc1;
        _display.WorldCenterY = wy - sy * pr1;
        ClampWorldCamera();
    }

    // Click zones in the world legend (see LegendDisplay.AddWorldLegendLines): 0 = the last row (new world of a different
    // size, debug), 1 = "All", 2..4 = filter categories.
    public (int row, int x0, int x1)[] WorldLegendHitZones()
    {
        int w = _display.GetLegendWidth(ViewCols), half = w / 2;
        int top = _display.MapDrawTop + 1;
        return
        [
            (-100, 0, -1),   // formerly the "different-size world" debug; route rows are at the bottom now
            (top, LegendX, LegendX + half - 1), (top, LegendX + half, LegendX + w - 1),
            (top + 1, LegendX, LegendX + half - 1), (top + 1, LegendX + half, LegendX + w - 1),
        ];
    }

    // Places in the map window under the filter (legend order), for Tab.
    public List<int> VisibleWorldPlaces() =>
        WorldView.VisiblePlaces(MapAreaWidth, MapAreaHeight, _display.WorldCenterX, _display.WorldCenterY, _display.WorldZoomStep, _display.WorldFilter);

    // World place under the mouse cursor, or -1; the world cell, for the terrain hint.
    public (int place, int x, int y) WorldHit((short x, short y) pos)
    {
        int col = pos.x - (DisplayConfig.LeftMargin + 1), row = pos.y - (_display.MapDrawTop + 1);
        var view = WorldView;
        var (tx, ty) = view.TileAt(col, row, MapAreaWidth, MapAreaHeight, _display.WorldCenterX, _display.WorldCenterY, _display.WorldZoomStep);
        return (view.PlaceAtCell(col, row), tx, ty);
    }

    private void WriteWorldRow(int gridRow)
    {
        if (gridRow < 0 || gridRow >= MapAreaHeight) return;
        ClampWorldCamera();
        string line = WorldView.RenderRow(gridRow, MapAreaWidth, MapAreaHeight,
            _display.WorldCenterX, _display.WorldCenterY, _display.WorldZoomStep, _display.HoveredWorldPlace,
            _display.SelectedWorldPlace, _display.WorldFilter);
        Console.Write(line);
        // The row wrote colors directly: restore the map colors the rest of the drawing expects.
        ColorHelper.SetBackgroundColor(_settings.Map.Colors.MapBackground ?? _display.MainBackground);
        ColorHelper.SetForegroundColor(_settings.Map.Colors.MapForeground ?? _display.MainForeground);
    }

    // Only the map area (no legend or borders), on every drag step.
    public void RedrawWorldViewport(bool refreshLegend = true)
    {
        if (_display.MapLevel != MapLevel.World) return;
        WriteRowsBatched(Enumerable.Range(0, MapAreaHeight).Select(i => (Action)(() => WriteWorldRow(i))).ToList());
        if (!refreshLegend) return;
        // The world legend is the places in the window: panning/zooming/hover change it.
        var lines = _legendDisplay.GetLegendLines();
        for (int i = 0; i < Math.Min(lines.Count, MapAreaHeight); i++) RedrawLegendLine(i, lines);
    }

    public bool IsInMapArea((short x, short y) pos) =>
        pos.x >= DisplayConfig.LeftMargin + 1 && pos.x < DisplayConfig.LeftMargin + 1 + MapAreaWidth
        && pos.y >= _display.MapDrawTop + 1 && pos.y < _display.MapDrawTop + 1 + MapAreaHeight;

    // The row-number field is 3 characters: " 7 ", " 42", "100" (maps go up to MapConfig.MaxSize = 100).
    private void WriteRowNumbers(int row, int cellWidth, List<int> mainBg, List<int> mainFg)
    {
        string n = row.ToString();
        string field = n.Length >= 3 ? n : n.Length == 2 ? " " + n : " " + n + " ";
        ColorHelper.WriteColored(field, fgColor: mainFg, bgColor: mainBg);
    }
}
