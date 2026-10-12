using NaviDnD.Clients;
using NaviDnD.Data;
using NaviDnD.Data.Models;
using NaviDnD.Display;
using NaviDnD.Helpers;

namespace NaviDnD;

internal class MapScreenLoop
{
    private readonly WorldState _settings;
    private readonly DisplayConfig _display;
    private readonly Storage _storage;
    private readonly GameAiClient _aiClient;
    private readonly AppConfig _config;
    private readonly MapDisplay _mapDisplay;
    private readonly LegendDisplay _legendDisplay;
    internal MapScreenLoop(
        WorldState settings, DisplayConfig display, Storage storage,
        GameAiClient aiClient, AppConfig config,
        MapDisplay mapDisplay, LegendDisplay legendDisplay)
    {
        _settings = settings;
        _display = display;
        _storage = storage;
        _aiClient = aiClient;
        _config = config;
        _mapDisplay = mapDisplay;
        _legendDisplay = legendDisplay;
    }

    internal async Task RunAsync(ScreenConfig screen, string title)
    {
        _display.ArrowKeysMovement = true;
        MouseUiHelper.SetDefaultImage(_display, _settings.Map.DefaultImage);
        var movementHandler = new MovementHandler(_settings, _storage, _display);
        int mapDrawTop = Console.CursorTop;
        _display.MapDrawTop = mapDrawTop;
        bool firstMapDraw = true;
        var mapObjectsProvider = new MapObjectsProvider(_settings, _display, _storage, _config);
        // The camera follows the hero only when he has moved; otherwise after dragging the map with the mouse
        // the camera would jump right back to him.
        List<int>? followedHeroPos = null;
        // Last hover on the world map (place, cell), from UpdateWorldHover.
        (int place, int x, int y)? lastWorldHit = null;
        void FollowHeroIfMoved()
        {
            var hp = _settings.Hero?.Position;
            if (hp == null || (followedHeroPos != null && hp.SequenceEqual(followedHeroPos))) return;
            bool first = followedHeroPos == null;
            // Stairs to another floor (it lies elsewhere on the field): the camera goes straight to the hero.
            bool jumped = !first && hp.Count >= 2
                && _settings.Map.FloorAt(hp[0], hp[1]) != _settings.Map.FloorAt(followedHeroPos![0], followedHeroPos[1]);
            if (jumped)
            {
                // Floors of the same building (shared outline): the camera shifts by the block offset, so the building stays in place
                // on screen and only the contents change. A basement dungeon/cave (different layout): the hero goes to the center.
                var from = _settings.Map.ChunkAt(followedHeroPos![0], followedHeroPos[1]);
                var to = _settings.Map.ChunkAt(hp[0], hp[1]);
                if (_mapDisplay.LockToFloorIfFits()) { }
                else if (from is { Theme: MapChunk.Building } && to is { Theme: MapChunk.Building })
                {
                    _display.CameraCol += to.OriginCol - from.OriginCol;
                    _display.CameraRow += to.OriginRow - from.OriginRow;
                    _mapDisplay.ClampCamera();
                    if (!_mapDisplay.HeroInView()) _mapDisplay.CenterOnHeroIfNeeded(force: true);
                }
                else _mapDisplay.CenterOnHeroIfNeeded(force: true);
            }
            followedHeroPos = [.. hp];
            if (first) _mapDisplay.CenterOnHeroIfNeeded();
            // After stairs, no "catching up" at the window edge: otherwise stepping onto stairs by a wall shifted the map by a cell.
            if (!jumped) _mapDisplay.FollowHero();
        }

        Dictionary<(int col, int row, bool d), (int cost, (int col, int row, bool d)? parent)>? hoverReachability = null;
        (int col, int row, bool diagonal)? hoverSearchOrigin = null;

        Action redrawAction = () =>
        {
            Console.CursorVisible = false;
            Console.SetCursorPosition(0, mapDrawTop);
            hoverReachability = null;
            mapObjectsProvider.Refresh();
            FollowHeroIfMoved();
            _mapDisplay.DrawMap(mapObjectsProvider);
        };
        _display.OnMapRedraw = redrawAction;
        _display.OnMapCellsRedraw = cells =>
        {
            bool vis = Console.CursorVisible;
            Console.CursorVisible = false;
            int sl = Console.CursorLeft, st = Console.CursorTop;
            foreach (var (c, r) in cells) _mapDisplay.RedrawCellFrame(c, r, mapObjectsProvider);
            Console.SetCursorPosition(sl, st);
            Console.CursorVisible = vis;
        };
        DialogDisplay? hoverDialog = null;
        Task? pendingImageLoad = null;
        Action? pendingImageUpdate = null;
        string[]? imageBeforeLoad = null;
        _display.OnTabKey = () =>
        {
            int total = _legendDisplay.GetSelectableMapItemCount();
            if (total > 0)
            {
                _display.SelectedMapItemIndex = _display.SelectedMapItemIndex < 0 ? 0
                    : (_display.SelectedMapItemIndex + 1) % total;
                SetMapEntityImage();
            }
        };
        _display.OnShiftTabKey = () =>
        {
            int total = _legendDisplay.GetSelectableMapItemCount();
            if (total > 0)
            {
                _display.SelectedMapItemIndex = _display.SelectedMapItemIndex <= 0 ? total - 1
                    : _display.SelectedMapItemIndex - 1;
                SetMapEntityImage();
            }
        };
        DateTime? hoverSince = null;
        (int col, int row)? pendingHoverCell = null;
        // Furniture/stairs under the cursor whose picture is currently in the image block (hovering a cell with no creature).
        object? shownFurniture = null;
        Door? pendingHoverDoor = null;
        (int col, int row)? mouseHoverCandidate = null;
        DateTime? doorHoverSince = null;
        (int col, int row, bool isHorizontal)? hoveredDoorWall = null;
        var titleTabs = MouseUiHelper.ComputeTitleTabs(title);
        string? hoveredTabKey = null;
        (short x, short y)? pendingMousePosition = null;

        void ApplyPreparedImage()
        {
            if (pendingImageLoad?.IsCompleted != true || pendingImageUpdate == null) return;
            var apply = pendingImageUpdate;
            pendingImageLoad = null;
            pendingImageUpdate = null;
            // A different screen/card may have replaced this request while the image loaded.
            if (!ReferenceEquals(imageBeforeLoad, _display.SelectedImageLines)) return;
            apply();
            (hoverDialog ?? _aiClient.ActiveDialog)?.RerenderRightPanel();
        }

        void QueueImage(string? image, Action apply)
        {
            imageBeforeLoad = _display.SelectedImageLines;
            pendingImageUpdate = apply;
            pendingImageLoad = SvgToBrailleConverter.PrepareAsync(image);
            ApplyPreparedImage();
        }

        void SetMapImage(string? image, string? name, List<int>? color = null) =>
            QueueImage(image, () => MouseUiHelper.SetSelectedImage(_display, image, name, color));

        void SetMapEntityImage(int page = 0)
        {
            var (image, _, _) = _legendDisplay.GetSelectedEntityImageAndName();
            int selected = _display.SelectedMapItemIndex;
            QueueImage(page == 0 ? image : null, () =>
            {
                if (_display.SelectedMapItemIndex == selected)
                    MouseUiHelper.SetSelectedMapEntity(_display, _legendDisplay, _settings, page);
            });
        }

        _display.PollAction = () =>
        {
            var activeDialog = hoverDialog ?? _aiClient.ActiveDialog;
            ApplyPreparedImage();
            // During streaming the active title may change (SwitchTab); recompute tabs from it.
            var activeTitle = _display.StreamingTabTitle ?? title;
            var activeTabs = _display.StreamingTabTitle != null
                ? MouseUiHelper.ComputeTitleTabs(activeTitle)
                : titleTabs;

            var (mousePos, clickPos, _) = ConsoleMouseReader.DrainMouseEvents();
            if (ConsoleMouseReader.IsDragging) { pendingMousePosition = null; return; }
            MouseUiHelper.HandleDialogArrowClick(clickPos, activeDialog);
            if (clickPos.HasValue)
            {
                var clickedTabKey = MouseUiHelper.GetHoveredTabKey(clickPos.Value.x, clickPos.Value.y, activeTabs);
                if (clickedTabKey != null) { Sound.PlayClick(); _display.PendingCommand = clickedTabKey; return; }
                if (clickPos.Value.y is >= 0 and <= 2) { ConsoleMouseReader.StartWindowDrag(); return; }
                // The "[F10] END TURN" button in the legend's "Turn order" row.
                if (_display.MapLevel == MapLevel.Location && _legendDisplay.IsHeroTurn && _legendDisplay.EndTurnButton is { x0: >= 0 } eb
                    && clickPos.Value.y == _display.MapDrawTop + 1 + _legendDisplay.CombatLineIndex
                    && clickPos.Value.x >= _mapDisplay.LegendX + eb.x0 && clickPos.Value.x <= _mapDisplay.LegendX + eb.x1)
                {
                    Sound.PlayClick();
                    _display.PendingCommand = "F10";
                    return;
                }
                // World map: click on a filter toggle in the legend.
                if (_display.MapHoverEnabled && activeDialog != null && _display.MapLevel == MapLevel.World)
                {
                    var zones = _mapDisplay.WorldLegendHitZones();
                    int zi = Array.FindIndex(zones, z => clickPos.Value.y == z.row && clickPos.Value.x >= z.x0 && clickPos.Value.x <= z.x1);
                    if (zi == 0)
                    {
                        // Stage 1 debug: a new world of a different size.
                        var cur = _mapDisplay.WorldView.World;
                        var made = WorldLibrary.Create(cur.Size == WorldSizes.Large ? WorldSizes.Small : WorldSizes.Large, new Random().Next());
                        if (_settings.World != null) _settings.World = new GameWorldLink { Id = made.Id };   // debug: the game moves to the new world
                        lastWorldHit = null;
                        RedrawWorldKeepCursor();
                        return;
                    }
                    if (zi == 1) { ToggleWorldFilter(null); return; }
                    if (zi >= 2) { ToggleWorldFilter(WorldMapView.Categories[zi - 2].key); return; }
                }
                // World map: press and drag and the image follows the cursor (like a map in a browser); a click without
                // moving, on a place, selects it.
                if (_display.MapHoverEnabled && activeDialog != null && _display.MapLevel == MapLevel.World
                    && _mapDisplay.IsInMapArea(clickPos.Value))
                {
                    int clickedPlace = _mapDisplay.WorldHit(clickPos.Value).place;
                    double startX = _display.WorldCenterX, startY = _display.WorldCenterY;
                    bool worldPanned = false;
                    double lastCenterX = startX, lastCenterY = startY;
                    ConsoleMouseReader.BeginDrag(clickPos.Value, (dx, dy) =>
                    {
                        worldPanned = true;
                        _mapDisplay.PanWorld(startX, startY, dx, dy);
                        if (_display.WorldCenterX == lastCenterX && _display.WorldCenterY == lastCenterY) return;
                        lastCenterX = _display.WorldCenterX;
                        lastCenterY = _display.WorldCenterY;
                        RedrawWorldKeepCursor(refreshLegend: false);
                    }, () =>
                    {
                        if (worldPanned) RedrawWorldKeepCursor();
                        if (!worldPanned)
                        {
                            // A click sets the route destination (a place or any cell, including unexplored ones); on a place it also selects it.
                            var hit = _mapDisplay.WorldHit(clickPos.Value);
                            if (clickedPlace >= 0) _display.SelectedWorldPlace = clickedPlace;
                            Sound.PlayClick();
                            if (hit is { } th) SetTravelTarget(clickedPlace >= 0 ? (_mapDisplay.WorldView.World.Places[clickedPlace].X, _mapDisplay.WorldView.World.Places[clickedPlace].Y) : (th.x, th.y));
                            if (clickedPlace >= 0) ShowWorldPlaceCard(clickedPlace);
                            RedrawWorldKeepCursor();
                            RerenderRightPanelKeepCursor();
                        }
                    });
                    return;
                }
                // A location is a field up to 100×100: press and drag to move the camera. Release without moving for a normal click.
                if (_display.MapHoverEnabled && activeDialog != null && _display.MapLevel == MapLevel.Location
                    && _mapDisplay.IsInMapArea(clickPos.Value))
                {
                    int startCol = _display.CameraCol, startRow = _display.CameraRow;
                    int lastCol = startCol, lastRow = startRow;
                    bool panned = false;
                    ConsoleMouseReader.BeginDrag(clickPos.Value, (dx, dy) =>
                    {
                        _mapDisplay.PanCamera(startCol, startRow, dx, dy);
                        // The mouse moved less than a cell: same camera, don't redraw the frame.
                        if (_display.CameraCol == lastCol && _display.CameraRow == lastRow) return;
                        lastCol = _display.CameraCol;
                        lastRow = _display.CameraRow;
                        panned = true;
                        _display.HoveredCell = null;
                        bool vis = Console.CursorVisible;
                        int sl = Console.CursorLeft, st = Console.CursorTop;
                        Console.CursorVisible = false;
                        _mapDisplay.RedrawLocationViewport(mapObjectsProvider, refreshLegend: false); // only the map area, in one write
                        Console.SetCursorPosition(sl, st);
                        Console.CursorVisible = vis;
                    }, () =>
                    {
                        if (!panned) HandleMapClick(clickPos.Value);
                        else
                        {
                            int sl = Console.CursorLeft, st = Console.CursorTop;
                            _mapDisplay.RefreshLegend();
                            Console.SetCursorPosition(sl, st);
                        }
                    });
                    return;
                }
                if (_display.MapHoverEnabled) HandleMapClick(clickPos.Value);
            }

            // World map: mouse wheel zooms in (away from you) / out, relative to the point under the cursor.
            // Always consume the scroll; otherwise what accumulated on other views would fire later.
            int wheel = ConsoleMouseReader.TakeWheel(out var wheelPos);
            if (MouseUiHelper.HandleDialogWheel(activeDialog, wheel, wheelPos)) { }   // over the dialog: scroll the history
            else if (wheel != 0 && _display.MapHoverEnabled && activeDialog != null
                && _display.MapLevel == MapLevel.World && _mapDisplay.IsInMapArea(wheelPos))
            {
                _mapDisplay.ZoomWorld(wheel, wheelPos);
                RedrawWorldKeepCursor();
            }
            // Terrain: the wheel over the legend scrolls the list (away from you = up).
            else if (wheel != 0 && activeDialog != null && _display.MapLevel == MapLevel.Location
                && wheelPos.x >= _mapDisplay.LegendX && wheelPos.y > _display.MapDrawTop
                && wheelPos.y <= _display.MapDrawTop + _legendDisplay.SeparatorLineIndex)
            {
                _display.LegendScroll = Math.Max(0, _display.LegendScroll - wheel);
                bool lvis = Console.CursorVisible;
                int lsl = Console.CursorLeft, lst = Console.CursorTop;
                Console.CursorVisible = false;
                _mapDisplay.RefreshLegend();
                Console.SetCursorPosition(lsl, lst);
                Console.CursorVisible = lvis;
            }

            if (!_display.MapHoverEnabled || _display.MapLevel != MapLevel.Location)
            {
                if (mousePos.HasValue)
                {
                    var newKey = MouseUiHelper.GetHoveredTabKey(mousePos.Value.x, mousePos.Value.y, activeTabs);
                    if (newKey != hoveredTabKey)
                    {
                        if (hoveredTabKey != null) MouseUiHelper.SetTabHighlight(activeTitle, activeTabs, hoveredTabKey, false, _display);
                        hoveredTabKey = newKey;
                        if (hoveredTabKey != null) MouseUiHelper.SetTabHighlight(activeTitle, activeTabs, hoveredTabKey, true, _display);
                    }
                    if (hoveredTabKey != null)
                    {
                        ConsoleMouseReader.SetCursorShape(true);
                    }
                    else
                    {
                        var (isOnUp, isOnDown) = MouseUiHelper.DialogArrowHover(mousePos.Value, activeDialog);
                        // The world map can be dragged: show the "hand" cursor over it.
                        bool overWorld = _display.MapHoverEnabled && activeDialog != null
                            && _display.MapLevel == MapLevel.World && _mapDisplay.IsInMapArea(mousePos.Value);
                        if (_display.MapLevel == MapLevel.World && activeDialog != null) UpdateWorldHover(overWorld ? mousePos.Value : null);
                        ConsoleMouseReader.SetCursorShape(isOnUp || isOnDown || overWorld);
                        activeDialog?.SetArrowHover(isOnUp, isOnDown);
                    }
                }
                return;
            }

            // Commit pending entity selection after 50 ms of stable hover
            if (pendingHoverCell.HasValue && hoverSince.HasValue
                && pendingHoverCell == _display.HoveredCell
                && (DateTime.UtcNow - hoverSince.Value).TotalMilliseconds >= 50)
            {
                pendingHoverCell = null;
                hoverSince = null;
                int commitIdx = _display.HoveredCell.HasValue
                    ? _legendDisplay.GetSelectableIndexForCell(_display.HoveredCell.Value.col, _display.HoveredCell.Value.row)
                    : -1;
                if (commitIdx != _display.SelectedMapItemIndex)
                {
                    _display.SelectedMapItemIndex = commitIdx;
                    if (commitIdx >= 0)
                    {
                        SetMapEntityImage();
                    }
                    else
                    {
                        SetMapImage(null, null, null);
                    }
                    bool vis0 = Console.CursorVisible;
                    Console.CursorVisible = false;
                    int sl0 = Console.CursorLeft, st0 = Console.CursorTop;
                    _mapDisplay.RefreshLegend();
                    activeDialog?.RerenderRightPanel();
                    SyncHoverImage();
                    Console.SetCursorPosition(sl0, st0);
                    Console.CursorVisible = vis0;
                }
            }

            // Commit door image after 50 ms of stable hover
            if (pendingHoverDoor != null && doorHoverSince.HasValue
                && _display.HoveredDoor == pendingHoverDoor
                && (DateTime.UtcNow - doorHoverSince.Value).TotalMilliseconds >= 50)
            {
                pendingHoverDoor = null;
                doorHoverSince = null;
                var committedDoor = _display.HoveredDoor!;
                string doorLabel = committedDoor.IsWorldExit == true ? L.T("Exit to the world map")
                    : committedDoor.IsWindow == true ? ((committedDoor.IsDoorOpen ?? false) ? L.T("Window open") : L.T("Window"))
                    : (committedDoor.IsDoorOpen ?? false) ? L.T("Door open") : L.T("Door closed");
                SetMapImage(MouseUiHelper.DoorImage(committedDoor, _settings.Map), doorLabel, committedDoor.Color);
                bool visDoor = Console.CursorVisible;
                Console.CursorVisible = false;
                int slDoor = Console.CursorLeft, stDoor = Console.CursorTop;
                activeDialog?.RerenderRightPanel();
                Console.SetCursorPosition(slDoor, stDoor);
                Console.CursorVisible = visDoor;
            }

            mousePos ??= pendingMousePosition;
            pendingMousePosition = null;
            if (mousePos == null) return;

            // Title tab hover
            var newTabKey = MouseUiHelper.GetHoveredTabKey(mousePos.Value.x, mousePos.Value.y, activeTabs);
            if (newTabKey != hoveredTabKey)
            {
                if (hoveredTabKey != null) MouseUiHelper.SetTabHighlight(activeTitle, activeTabs, hoveredTabKey, false, _display);
                hoveredTabKey = newTabKey;
                if (hoveredTabKey != null) MouseUiHelper.SetTabHighlight(activeTitle, activeTabs, hoveredTabKey, true, _display);
            }
            if (hoveredTabKey != null) { ConsoleMouseReader.SetCursorShape(true); return; }

            // Check for wall/door before cell
            var wall = _mapDisplay.ScreenToWall(mousePos.Value.x, mousePos.Value.y);
            var wallDoor = wall.HasValue
                ? mapObjectsProvider.GetHoveredDoor(wall.Value.col, wall.Value.row, wall.Value.isHorizontal)
                : null;

            if (wallDoor != null)
            {
                ConsoleMouseReader.SetCursorShape(true);
                activeDialog?.SetArrowHover(false, false);

                if (wallDoor != _display.HoveredDoor)
                {
                    bool visD = Console.CursorVisible;
                    Console.CursorVisible = false;
                    int slD = Console.CursorLeft, stD = Console.CursorTop;

                    var doorOldCell = _display.HoveredCell;
                    var oldDoorWall = hoveredDoorWall;
                    int oldSelectedIdx = _display.SelectedMapItemIndex;

                    _display.HoveredCell = null;
                    pendingHoverCell = null;
                    hoverSince = null;
                    shownFurniture = null; // the image block is now taken by the door: show the furniture again on return

                    if (oldSelectedIdx >= 0)
                    {
                        // Had entity selected — full redraw to clear legend arrow
                        _display.SelectedMapItemIndex = -1;
                        _display.HoveredDoor = wallDoor;
                        hoveredDoorWall = wall;
                        SetMapImage(null, null, null);
                        if (doorOldCell.HasValue)
                            _mapDisplay.RedrawCell(doorOldCell.Value.col, doorOldCell.Value.row, mapObjectsProvider);
                        if (oldDoorWall.HasValue) RedrawDoor(oldDoorWall.Value);
                        RedrawDoor(wall.Value);
                        _mapDisplay.RefreshLegend();
                        activeDialog?.RerenderRightPanel();
                    }
                    else
                    {
                        // Targeted redraws
                        if (oldDoorWall.HasValue)
                        {
                            _display.HoveredDoor = null;
                            RedrawDoor(oldDoorWall.Value);
                        }
                        if (doorOldCell.HasValue)
                            _mapDisplay.RedrawCell(doorOldCell.Value.col, doorOldCell.Value.row, mapObjectsProvider);
                        _display.HoveredDoor = wallDoor;
                        hoveredDoorWall = wall;
                        RedrawDoor(wall.Value);
                        SetMapImage(null, null, null);
                        activeDialog?.RerenderRightPanel();
                    }

                    pendingHoverDoor = wallDoor;
                    doorHoverSince = DateTime.UtcNow;

                    Console.SetCursorPosition(slD, stD);
                    Console.CursorVisible = visD;
                }
                return;
            }
            else if (_display.HoveredDoor != null)
            {
                // Left a door — clear hover
                bool visC = Console.CursorVisible;
                Console.CursorVisible = false;
                int slC = Console.CursorLeft, stC = Console.CursorTop;

                if (hoveredDoorWall.HasValue)
                {
                    _display.HoveredDoor = null;
                    RedrawDoor(hoveredDoorWall.Value);
                }
                else
                {
                    _display.HoveredDoor = null;
                }
                hoveredDoorWall = null;
                pendingHoverDoor = null;
                doorHoverSince = null;
                shownFurniture = null;
                SetMapImage(null, null, null);
                activeDialog?.RerenderRightPanel();

                Console.SetCursorPosition(slC, stC);
                Console.CursorVisible = visC;
            }

            var cell = KnownCellOrNull(_mapDisplay.ScreenToCell(mousePos.Value.x, mousePos.Value.y));

            var (isOnUpArrow, isOnDownArrow) = MouseUiHelper.DialogArrowHover(mousePos.Value, cell.HasValue ? null : activeDialog);
            var (isOnNoteLeft, isOnNoteRight) = MouseUiHelper.NotePageArrowHover(mousePos.Value, cell.HasValue ? null : activeDialog, _display);
            ConsoleMouseReader.SetCursorShape(cell.HasValue || isOnUpArrow || isOnDownArrow || isOnNoteLeft || isOnNoteRight);
            activeDialog?.SetArrowHover(isOnUpArrow, isOnDownArrow);
            activeDialog?.SetNoteArrowHover(isOnNoteLeft, isOnNoteRight);

            if (cell == _display.HoveredCell)
            {
                mouseHoverCandidate = null;
                return;
            }

            // Jitter filter: require the same cell in two consecutive polls before committing.
            // One-poll delay (~10ms) is enough to suppress hardware jitter without visible lag.
            if (cell != mouseHoverCandidate)
            {
                mouseHoverCandidate = cell;
                pendingMousePosition = mousePos;
                return;
            }

            mouseHoverCandidate = null;
            var oldCell = _display.HoveredCell;
            _display.HoveredCell = cell;
            bool vis = Console.CursorVisible;
            Console.CursorVisible = false;
            int sl = Console.CursorLeft, st = Console.CursorTop;

            int newEntityIdx = cell.HasValue
                ? _legendDisplay.GetSelectableIndexForCell(cell.Value.col, cell.Value.row)
                : -1;
            // Hovering a creature/object shows it; an empty cell or the cursor off the map shows
            // the one pinned by a click (if any), otherwise nothing.
            int desiredIdx = cell.HasValue && newEntityIdx >= 0 ? newEntityIdx : PinnedIndex();

            if (desiredIdx == _display.SelectedMapItemIndex)
            {
                // Same committed entity (or same "none") — cancel any pending
                pendingHoverCell = null;
                hoverSince = null;
                if (oldCell.HasValue) _mapDisplay.RedrawCell(oldCell.Value.col, oldCell.Value.row, mapObjectsProvider);
                if (cell.HasValue)    _mapDisplay.RedrawCell(cell.Value.col, cell.Value.row, mapObjectsProvider);
            }
            else if (cell.HasValue && newEntityIdx >= 0)
            {
                // Different entity — highlight cell immediately, start/restart 50ms timer
                pendingHoverCell = cell;
                hoverSince = DateTime.UtcNow;
                if (oldCell.HasValue) _mapDisplay.RedrawCell(oldCell.Value.col, oldCell.Value.row, mapObjectsProvider);
                _mapDisplay.RedrawCell(cell.Value.col, cell.Value.row, mapObjectsProvider);
            }
            else
            {
                // Empty cell or the cursor left the map: restore the pinned one (or clear the selection).
                pendingHoverCell = null;
                hoverSince = null;
                RestorePinnedOrClear();
                if (oldCell.HasValue) _mapDisplay.RedrawCell(oldCell.Value.col, oldCell.Value.row, mapObjectsProvider);
                if (cell.HasValue) _mapDisplay.RedrawCell(cell.Value.col, cell.Value.row, mapObjectsProvider);
                activeDialog?.RerenderRightPanel();
            }

            SyncHoverImage();

            UpdateHoverInfo(_display.HoveredCell);
            Console.SetCursorPosition(sl, st);
            Console.CursorVisible = vis;

            // Furniture or stairs under the cursor (no creature/object on the cell): picture and name in the image block.
            void SyncHoverImage()
            {
                var cell = _display.HoveredCell;
                int newEntityIdx = cell.HasValue ? _legendDisplay.GetSelectableIndexForCell(cell.Value.col, cell.Value.row) : -1;
                bool knownCell = cell is { } kc && newEntityIdx < 0 && (_config.RevealMap || _storage.ExploredCells.Contains(kc));
                var furnitureHere = knownCell ? _settings.Map.FurnitureAt(cell!.Value.col, cell.Value.row) : null;
                var terrainHere = knownCell && furnitureHere == null ? _settings.Map.TerrainAt(cell!.Value.col, cell.Value.row) : null;
                var stairHere = terrainHere != null && TerrainCatalog.IsStair(terrainHere.Code) ? terrainHere : null;
                // Terrain is per kind (neighboring cells of the same kind don't redraw the picture).
                object? hoverThing = (object?)furnitureHere ?? (stairHere != null ? cell : terrainHere);
                if (!Equals(hoverThing, shownFurniture))
                {
                    shownFurniture = hoverThing;
                    if (furnitureHere != null)
                    {
                        string label = L.T(FurnitureCatalog.DisplayName(furnitureHere)) + (string.IsNullOrEmpty(furnitureHere.State) ? "" : $" ({furnitureHere.State})");
                        if (FurnitureCatalog.Terrain(furnitureHere) is { } fk && TerrainCatalog.Properties(fk) is { } fprops) label += "\n" + fprops;
                        SetMapImage(furnitureHere.Image ?? FurnitureCatalog.Get(furnitureHere.Kind)?.Image, label,
                            FurnitureCatalog.Terrain(furnitureHere)?.LegendColor);
                        activeDialog?.RerenderRightPanel();
                    }
                    else if (stairHere != null)
                    {
                        var (sc, sr) = cell!.Value;
                        string label = _settings.Map.StairTarget(sc, sr) is var (tc, tr)
                            ? $"{L.T(stairHere.Name)} → {MapConfig.FloorLabel(_settings.Map.FloorAt(tc, tr))}" : L.T(stairHere.Name);
                        SetMapImage(TerrainCatalog.Image(stairHere), label, stairHere.LegendColor);
                        activeDialog?.RerenderRightPanel();
                    }
                    else if (terrainHere != null)
                    {
                        string label = L.T(terrainHere.Name) + (TerrainCatalog.Properties(terrainHere) is { } tprops ? "\n" + tprops : "");
                        SetMapImage(TerrainCatalog.Image(terrainHere), label, terrainHere.LegendColor ?? terrainHere.Fg);
                        activeDialog?.RerenderRightPanel();
                    }
                    else if (_display.SelectedMapItemIndex < 0)
                    {
                        SetMapImage(null, null, null);
                        activeDialog?.RerenderRightPanel();
                    }
                }
            }

            void RedrawDoor((int col, int row, bool isHorizontal) dw)
            {
                if (dw.isHorizontal)
                    _mapDisplay.RedrawHorizontalWall(dw.col, dw.row, mapObjectsProvider);
                else
                    _mapDisplay.RedrawVerticalWall(dw.col, dw.row, mapObjectsProvider);
            }

        };
        var capturedMapPollAction = _display.PollAction;
        _display.MapPollActionFactory = _ => capturedMapPollAction;

        // The cell under the cursor is "interactive" (hand + highlight + hints) only if the hero
        // sees it or has already explored it: you can't feel around with the mouse for what you haven't seen.
        (int col, int row)? KnownCellOrNull((int col, int row)? c) =>
            c is { } k && _display.MapLevel == MapLevel.Location
            && (_config.RevealMap || _storage.ExploredCells.Contains(k) || _storage.VisibleCells.Contains(k)) ? c : null;

        // Hover on the world map: a place highlights its icon and shows the card (picture, name, type, kingdom);
        // otherwise the cell's terrain and whose land it is.
        void UpdateWorldHover((short x, short y)? pos)
        {
            (int place, int x, int y)? hit = pos is { } p ? _mapDisplay.WorldHit(p) : null;
            if (hit == lastWorldHit) return;
            var world = _mapDisplay.WorldView.World;
            bool placeChanged = (hit?.place ?? -1) != _display.HoveredWorldPlace;
            bool tileInfoChanged = hit is not { } h0 || lastWorldHit is not { } l0 || h0.place != l0.place
                || (h0.place < 0 && (world.BiomeAt(h0.x, h0.y) != world.BiomeAt(l0.x, l0.y) || world.OwnerAt(h0.x, h0.y) != world.OwnerAt(l0.x, l0.y)
                    || world.RoadAt(h0.x, h0.y) != world.RoadAt(l0.x, l0.y) || (world.RiverAt(h0.x, h0.y) > 0) != (world.RiverAt(l0.x, l0.y) > 0)
                    || (GameWorld.HeroTile(_settings) is { } ht0 && (ht0 == (h0.x, h0.y)) != (ht0 == (l0.x, l0.y)))));
            lastWorldHit = hit;
            _display.HoveredWorldPlace = hit?.place ?? -1;
            if (placeChanged) RedrawWorldKeepCursor();
            if (!tileInfoChanged) return;
            if (hit is not { } h)
                ShowWorldPlaceCard(_display.SelectedWorldPlace);
            else if (h.place >= 0)
                ShowWorldPlaceCard(h.place);
            else if (world.InBounds(h.x, h.y))
            {
                char b = world.BiomeAt(h.x, h.y);
                int own = world.OwnerAt(h.x, h.y);
                string land = WorldBiomes.IsWater(b) ? "" : own >= 0 && own < world.Kingdoms.Count ? world.Kingdoms[own].Name : L.T("no man's land");
                // A river or road on the cell takes priority (its own picture and name); the terrain goes on the second line.
                int road = world.RoadAt(h.x, h.y);
                var biome = WorldBiomes.Get(b);
                string biomeName = L.T(biome.Name);
                var (title, image, color) = world.RiverAt(h.x, h.y) > 0 && road > 0 ? (L.T("Bridge"), road == 2 ? "delapouite/stone-bridge" : "delapouite/arch-bridge", new List<int> { 150, 108, 70 })
                    : world.RiverAt(h.x, h.y) > 0 ? (L.T("River"), "delapouite/river", new List<int> { 72, 122, 178 })
                    : road == 2 ? (L.T("Highway"), "delapouite/stone-path", new List<int> { 176, 148, 102 })
                    : road == 1 ? (L.T("Path"), "delapouite/trail", new List<int> { 146, 128, 96 })
                    : (biomeName, WorldMapView.BiomeImage(b), biome.Color);
                string terrain = title == biomeName ? "" : biomeName.ToLowerInvariant();
                // The hero's cell: "You are here" with his portrait.
                if (GameWorld.HeroTile(_settings) == (h.x, h.y))
                {
                    terrain = title == biomeName ? biomeName.ToLowerInvariant() : title.ToLowerInvariant();
                    (title, image, color) = (L.T("You are here"), _settings.Hero?.Image ?? image, _settings.Hero?.Color ?? color);
                }
                string second = string.Join(" · ", new[] { terrain, land }.Where(t => t.Length > 0));
                SetMapImage(image, title + (second.Length > 0 ? "\n" + second : ""), color);
            }
            else ShowWorldPlaceCard(_display.SelectedWorldPlace);
            RerenderRightPanelKeepCursor();
        }

        // The world place card in the image block (hover or selection).
        void ShowWorldPlaceCard(int place)
        {
            var world = _mapDisplay.WorldView.World;
            if (place < 0 || place >= world.Places.Count) { SetMapImage(null, null, null); return; }
            var pl = world.Places[place];
            string kingdom = pl.Kingdom >= 0 && pl.Kingdom < world.Kingdoms.Count ? " · " + world.Kingdoms[pl.Kingdom].Name : "";
            SetMapImage(WorldMapView.PlaceImage(pl.Type), pl.Name + "\n" + L.T(WorldPlaceTypes.Label(pl.Type)) + kingdom,
                _mapDisplay.WorldView.PlaceLabelColor(pl));
        }

        void RerenderRightPanelKeepCursor()
        {
            bool vis = Console.CursorVisible;
            int sl = Console.CursorLeft, st = Console.CursorTop;
            Console.CursorVisible = false;
            hoverDialog?.RerenderRightPanel();
            Console.SetCursorPosition(sl, st);
            Console.CursorVisible = vis;
        }

        // Tab / Shift+Tab on the world map: next/previous place among those in the map window (as in the legend);
        // the map doesn't move and the set of places doesn't change.
        void SelectWorldPlace(int delta)
        {
            var list = _mapDisplay.VisibleWorldPlaces();
            if (list.Count == 0) return;
            int pos = list.IndexOf(_display.SelectedWorldPlace);
            pos = pos < 0 ? (delta > 0 ? 0 : list.Count - 1) : (pos + delta + list.Count) % list.Count;
            _display.SelectedWorldPlace = list[pos];
            Sound.PlayClick();
            ShowWorldPlaceCard(_display.SelectedWorldPlace);
            RedrawWorldKeepCursor();
            RerenderRightPanelKeepCursor();
        }

        // Route destination on the world map: a route from the hero (clicking the hero's cell clears the destination).
        void SetTravelTarget((int x, int y) target)
        {
            bool heroTile = GameWorld.HeroTile(_settings) == target;
            var plan = heroTile ? null : TravelService.PlanTo(_settings, target);
            if (plan == null && !heroTile && GameWorld.HeroTile(_settings) != null)
                SetMapImage("lorc/waves", L.T("Unreachable on foot\nwater or mountain peaks in the way"), [110, 150, 200]);
            _display.WorldTarget = plan != null ? target : null;
            _display.WorldRoute = plan?.Route.Path;
            var w = _mapDisplay.WorldView.World;
            _display.WorldRouteInfo = plan == null ? null
                : (plan.TargetPlace ?? NaviDnD.MapGen.Generators.WorldAtlas.TileName(w, target.x, target.y), plan.Route.Days, plan.Route.RoadShare);
        }

        // Set off: the code walks the hero along the route (days, encounters), and the DM describes the road in one call.
        async Task StartTravel(DialogDisplay dialog)
        {
            _settings.History ??= [];
            if (_display.WorldTarget is not { } target || TravelService.PlanTo(_settings, target) is not { } plan)
            {
                _settings.History.Add(new Data.Models.DialogMessage { Text = L.T("Pick a destination by clicking the world map, then [F10] to travel.") });
                _storage.Save();
                return;
            }
            if (_settings.Combat?.Active == true)
            {
                _settings.History.Add(new Data.Models.DialogMessage { Text = L.T("Combat is on — you can't travel now.") });
                _storage.Save();
                return;
            }
            var pace = (TravelService.Pace)_display.TravelPace;
            string dest = plan.TargetPlace ?? (GameWorld.ForMaster(_settings) is { } tw ? NaviDnD.MapGen.Generators.WorldAtlas.TileName(tw, target.x, target.y) : L.W("the unknown"));
            string report = TravelService.Go(_settings, plan, pace, new Random());
            // Arrived at a place with a saved location: "everything as it was", the hero at the entrance.
            if (plan.TargetPlace != null && _settings.World?.Place == plan.TargetPlace && GameWorld.Geo(_settings) is { } geo
                && GameWorld.SavedLocationJson(geo, plan.TargetPlace) is { } json)
            {
                _storage.ApplyUpdateWorldState("{\"map\":" + json + "}");
                if (_settings.Hero != null && LocationGrower.EntranceCell(_settings.Map) is var (ec, er)) _settings.Hero.Position = [ec, er];
            }
            _display.WorldTarget = null;
            _display.WorldRoute = null;
            _display.WorldRouteInfo = null;
            if (GameWorld.HeroTile(_settings) is { } h) { _display.WorldCenterX = h.x + 0.5; _display.WorldCenterY = h.y + 0.5; }
            _settings.History.Add(new Data.Models.DialogMessage { Author = _settings.Hero?.Name ?? "Hero", Text = L.WF("Setting off: {0} ({1} pace).", dest, L.W(TravelService.PaceName(pace))) });
            _storage.Save();
            _display.DialogScrollOffset = 0;
            _aiClient.ActiveDialog = dialog;
            dialog.PrepareStreaming();
            MusicDirector.Traveling = true;   // while the DM describes the road, the travel music plays
            try { await _aiClient.Travel(report, dialog.AppendStreamChunk, dialog.NewStreamingMessage, dialog.TickSpinner, null); }
            finally { MusicDirector.Traveling = false; }
            dialog.FinalizeStreaming();
            while (Console.KeyAvailable) Console.ReadKey(true);
            _display.PendingCommand = GameWorld.HasLocation(_settings) ? "F1" : null;
            RedrawWorldKeepCursor();
        }

        // key == null means "All": turn on all categories (if all are already on, turn them all off).
        void ToggleWorldFilter(string? key)
        {
            if (key == null)
            {
                bool all = WorldMapView.Categories.All(c => _display.WorldFilter.Contains(c.key));
                _display.WorldFilter.Clear();
                if (!all) foreach (var c in WorldMapView.Categories) _display.WorldFilter.Add(c.key);
            }
            else if (!_display.WorldFilter.Remove(key)) _display.WorldFilter.Add(key);
            Sound.PlayClick();
            var world = _mapDisplay.WorldView.World;
            int sel = _display.SelectedWorldPlace;
            if (sel >= 0 && sel < world.Places.Count && !_display.WorldFilter.Contains(WorldMapView.Category(world.Places[sel].Type)))
            {
                _display.SelectedWorldPlace = -1;
                SetMapImage(null, null, null);
            }
            RedrawWorldKeepCursor();
        }

        void RedrawWorldKeepCursor(bool refreshLegend = true)
        {
            bool vis = Console.CursorVisible;
            int sl = Console.CursorLeft, st = Console.CursorTop;
            Console.CursorVisible = false;
            _mapDisplay.RedrawWorldViewport(refreshLegend);
            Console.SetCursorPosition(sl, st);
            Console.CursorVisible = vis;
        }

        // Hover hint as a combat block row: the distance, and on the hero's turn in combat, the path; highlight of the
        // room/zone under the cursor (the image panel is left alone; it only shows the selected element's picture).
        // Only the legend rows that changed are redrawn.
        void UpdateHoverInfo((int col, int row)? c)
        {
            _display.HoverPath = ComputeHoverPath(c);
            _mapDisplay.RefreshLegend();
        }

        // Distance to the cell always; in combat on the hero's turn, also the path (to the cell next to the creature if the cursor is on it).
        HoverPathInfo? ComputeHoverPath((int col, int row)? c)
        {
            if (c is not { } t || _settings.Hero?.Position is not { Count: >= 2 } hp || (hp[0], hp[1]) == t) return null;
            int distanceFt = TargetGeometry.DistanceFt(hp[0], hp[1], t.col, t.row);
            if (_settings.Combat?.Active != true || _settings.Combat.CurrentTurn != _settings.Hero.Symbol)
                return new HoverPathInfo(distanceFt, false, null, null);

            var origin = (hp[0], hp[1], _storage.DiagonalUsed > 0);
            if (hoverSearchOrigin != origin) hoverReachability = null;
            hoverSearchOrigin = origin;
            var reached = hoverReachability ??= MovementCalculator.Dijkstra(
                _settings, hp[0], hp[1], 1000, -1, origin.Item3, passPeaceful: true);
            int? Cost(int col, int row)
            {
                if ((col, row) == (hp[0], hp[1])) return 0;
                int? best = null;
                foreach (bool d in (bool[])[false, true])
                    if (reached.TryGetValue((col, row, d), out var v) && (best == null || v.cost < best)) best = v.cost;
                return best;
            }

            var occupant = (_settings.Map.Entities ?? []).FirstOrDefault(e => e.Deleted != true
                && e.Position is { Count: >= 2 } p && p[0] == t.col && p[1] == t.row);
            if (occupant == null) return new HoverPathInfo(distanceFt, true, Cost(t.col, t.row), null);

            int? toNeighbour = null;
            for (int dc = -1; dc <= 1; dc++)
            for (int dr = -1; dr <= 1; dr++)
                if ((dc != 0 || dr != 0) && Cost(t.col + dc, t.row + dr) is int v && (toNeighbour == null || v < toNeighbour))
                    toNeighbour = v;
            return new HoverPathInfo(distanceFt, true, toNeighbour, occupant.Symbol);
        }

        // The cursor left the map: restore the one pinned by a click (if it's still on the map and visible) or clear the selection.
        void RestorePinnedOrClear()
        {
            var pinned = _display.PinnedMapEntity;
            int pinnedIdx = pinned?.Position is { Count: >= 2 } pp && pinned.Deleted != true
                ? _legendDisplay.GetSelectableIndexForCell(pp[0], pp[1]) : -1;
            if (pinnedIdx < 0) _display.PinnedMapEntity = null;
            SelectMapItem(pinnedIdx);
        }

        void SelectMapItem(int idx)
        {
            _display.SelectedMapItemIndex = idx;
            if (idx >= 0) SetMapEntityImage();
            else
            {
                SetMapImage(null, null, null);
                _display.NotePage = 0;
                _display.NotePageCount = 0;
            }
        }

        int PinnedIndex() => _display.PinnedMapEntity?.Position is { Count: >= 2 } p
            ? _legendDisplay.GetSelectableIndexForCell(p[0], p[1]) : -1;

        // Click on the map: ◄ ► of the monster card in the image panel pages it (the arrow keys on the map
        // are taken by movement); a creature/object pins its picture (a repeated click changes nothing);
        // an empty explored cell unpins.
        void HandleMapClick((short x, short y) click)
        {
            var na = hoverDialog?.NotePageArrowPositions;
            if (na is { } a && a.arrowY >= 0 && click.y == a.arrowY && _display.NotePageCount > 1
                && (click.x == a.leftX || click.x == a.rightX))
            {
                int page = Math.Clamp(_display.NotePage + (click.x == a.leftX ? -1 : 1), 0, _display.NotePageCount - 1);
                if (page == _display.NotePage) return;
                Sound.PlayClick();
                SetMapEntityImage(page);
                hoverDialog?.RerenderRightPanel();
                return;
            }

            var clicked = KnownCellOrNull(_mapDisplay.ScreenToCell(click.x, click.y));
            if (clicked is not { } cc) return;

            int idx = _legendDisplay.GetSelectableIndexForCell(cc.col, cc.row);
            if (idx < 0)
            {
                if (_display.PinnedMapEntity == null) return;
                _display.PinnedMapEntity = null;
                Sound.PlayClick();
                SelectMapItem(-1);
                RedrawSelection(true);
                return;
            }

            pendingHoverCell = null;
            hoverSince = null;
            bool selectionChanged = idx != _display.SelectedMapItemIndex;
            _display.SelectedMapItemIndex = idx;
            var entity = _legendDisplay.GetSelectedEntity();
            if (entity == null) return;
            if (entity != _display.PinnedMapEntity) Sound.PlayClick(); // already pinned: a repeated click changes nothing
            _display.PinnedMapEntity = entity;
            if (!selectionChanged) return;
            SetMapEntityImage();
            RedrawSelection(true);
        }

        // The selected map element changed: the legend (the "←" marker of the selection means a full map redraw) and the image panel.
        void RedrawSelection(bool redrawMap)
        {
            bool vis = Console.CursorVisible;
            int sl = Console.CursorLeft, st = Console.CursorTop;
            Console.CursorVisible = false;
            if (redrawMap)
            {
                _mapDisplay.RefreshLegend();
            }
            (hoverDialog ?? _aiClient.ActiveDialog)?.RerenderRightPanel();
            Console.SetCursorPosition(sl, st);
            Console.CursorVisible = vis;
        }
        _display.MapOnTabKey = _display.OnTabKey;
        _display.MapOnShiftTabKey = _display.OnShiftTabKey;
        _display.RedrawCurrentContent = () =>
        {
            int sl = Console.CursorLeft, st = Console.CursorTop;
            Console.CursorVisible = false;
            Console.SetCursorPosition(0, mapDrawTop);
            hoverReachability = null;
            mapObjectsProvider.Refresh();
            _mapDisplay.DrawMap(mapObjectsProvider);
            Console.SetCursorPosition(sl, st);
        };

        async Task RunEnemyTurn()
        {
            if (_settings.Combat?.Active != true) return;
            if (string.IsNullOrEmpty(_settings.Combat.CurrentTurn)) return;
            var entitySymbols = _settings.Map.Entities?
                .Where(e => e.Deleted != true)
                .Select(e => e.Symbol)
                .ToHashSet() ?? [];
            if (!entitySymbols.Contains(_settings.Combat.CurrentTurn)) return;

            Console.SetCursorPosition(0, mapDrawTop);
            hoverReachability = null;
            mapObjectsProvider.Refresh();
            _mapDisplay.DrawMap(mapObjectsProvider);
            var enemyHistory = new DialogDisplay(_settings, _display, _storage);
            _aiClient.ActiveDialog = (enemyHistory);
            enemyHistory.PrepareStreaming();
            await _aiClient.FireEnemyTurn(
                enemyHistory.AppendStreamChunk,
                enemyHistory.NewStreamingMessage,
                enemyHistory.TickSpinner,
                onRedraw: redrawAction);
            enemyHistory.FinalizeStreaming();
            while (Console.KeyAvailable) Console.ReadKey(true);
        }

        // spotted is checked not only on the hero's step (TryMove): the AI's reply may have opened a door or removed
        // an obstacle, and enemies in combat may have approached an ally not yet in the fight. Without this check, enemies
        // that had seen the hero stayed silent until his next step, and he had time to get away.
        // Filling the location block by block (LocationGrower): the geometry of every block in the plan already exists; one request to
        // the model fills a block when the hero approaches it, before he enters.
        var populateAttempted = new HashSet<MapChunk>();
        async Task<bool> GrowLocationIfNeeded()
        {
            if (_display.MapLevel != MapLevel.Location || _config.DisableTriggers) return false;
            // One attempt per block per run: if the AI reply failed, don't hammer it with requests on every step.
            var chunk = LocationGrower.UnpopulatedNearHero(_settings);
            if (chunk == null || !populateAttempted.Add(chunk)) return false;

            var dialog = new DialogDisplay(_settings, _display, _storage);
            _aiClient.ActiveDialog = dialog;
            dialog.PrepareStreaming();
            await _aiClient.PopulateChunk(chunk, dialog.AppendStreamChunk, dialog.NewStreamingMessage, dialog.TickSpinner, redrawAction);
            dialog.FinalizeStreaming();
            _aiClient.ActiveDialog = null;
            // Don't clear the key buffer: the player didn't start the filling, and what they typed meanwhile is their next
            // action (previously the text silently vanished and a combat turn was wasted).
            return true;
        }

        async Task FireEncounterTriggers(DialogDisplay dialog)
        {
            if (_config.DisableTriggers || _config.RevealMap) return;
            var encounters = movementHandler.CollectEncounterTriggers();
            if (encounters.Count == 0) return;

            redrawAction();
            _aiClient.ActiveDialog = dialog;
            dialog.PrepareStreaming();
            await _aiClient.FireTrigger(encounters, [], [], dialog.AppendStreamChunk, dialog.NewStreamingMessage, dialog.TickSpinner, redrawAction);
            dialog.FinalizeStreaming();
            _aiClient.ActiveDialog = null;
            while (Console.KeyAvailable) Console.ReadKey(true);
        }

        // The final record of the enemies' turn can also advance the round (totalRounds+1, see § COMBAT):
        // the same reminder about onRound/onExpire effects must fire here too, through the same
        // deduplicated path; otherwise periodic effects (for example, a burning enemy) never
        // tick during combat. The same class of bug that was fixed for SendAction outside combat.
        async Task RunEnemyTurnAndCheckRoundEnd()
        {
            await RunEnemyTurn();
            if (_settings.Combat?.Active == true)
                await FireEncounterTriggers(new DialogDisplay(_settings, _display, _storage));

            var bundle = _storage.TakeRoundBundle();
            if (bundle != null && !_config.DisableTriggers && !_config.RevealMap)
            {
                var roundHistory = new DialogDisplay(_settings, _display, _storage);
                _aiClient.ActiveDialog = (roundHistory);
                roundHistory.PrepareStreaming();
                await _aiClient.FireRoundTransition(bundle, roundHistory.AppendStreamChunk, roundHistory.NewStreamingMessage, roundHistory.TickSpinner, redrawAction);
                roundHistory.FinalizeStreaming();
                _aiClient.ActiveDialog = (null);
                while (Console.KeyAvailable) Console.ReadKey(true);
            }
        }

        bool IsHeroTurnOver()
        {
            if (_settings.Combat?.Active != true) return false;
            if (_settings.Combat.CurrentTurn != _settings.Hero?.Symbol) return false;
            var actions = _settings.Hero?.Actions;
            if (actions == null || actions.Count == 0) return false;
            return actions.All(a => a.Deleted == true || a.Value == 0)
                && (_settings.Hero!.SpeedLeft ?? 1) == 0;
        }

        void PanByKey(string input)
        {
            var (dx, dy) = input switch
            {
                "PanLeft" => (-1, 0),
                "PanRight" => (1, 0),
                "PanUp" => (0, 1),
                _ => (0, -1),
            };
            int sl = Console.CursorLeft, st = Console.CursorTop;
            bool vis = Console.CursorVisible;
            Console.CursorVisible = false;
            if (_display.MapLevel == MapLevel.World)
            {
                // World map: Y grows downward, a step is several screen characters.
                _mapDisplay.PanWorld(_display.WorldCenterX, _display.WorldCenterY, -dx * 6, dy * 3);
                _mapDisplay.RedrawWorldViewport();
            }
            else
            {
                const int step = 2;
                _display.CameraCol += dx * step;
                _display.CameraRow += dy * step;
                _mapDisplay.ClampCamera();
                _display.HoveredCell = null;
                _mapDisplay.RedrawLocationViewport(mapObjectsProvider);
            }
            Console.SetCursorPosition(sl, st);
            Console.CursorVisible = vis;
        }

        bool movedFrameDrawn = false;
        int downTurnRound = -1;

        // A service line in the dialog, without repeats (the player presses the same thing).
        void AddNoteOnce(string text)
        {
            _settings.History ??= [];
            if (_settings.History.LastOrDefault()?.Text == text) return;
            _settings.History.Add(new Data.Models.DialogMessage { Text = text });
            _storage.Save();
        }

        // The hero's turn is over: pass to the next living participant by initiative (round-robin). false means nobody to pass to.
        bool PassTurnToNext()
        {
            if (_settings.Combat is not { Active: true, Initiative: { Count: > 0 } init } combat) return false;
            var order = init.Where(i => i.Deleted != true).OrderByDescending(i => i.Score).ToList();
            int at = order.FindIndex(i => i.Symbol == combat.CurrentTurn);
            var next = order.Skip(at + 1).Concat(order.Take(at + 1)).FirstOrDefault(i => i.Symbol != _settings.Hero?.Symbol
                && _settings.Map.Entities?.Any(e => e.Deleted != true && e.Symbol == i.Symbol) == true);
            if (next == null) return false;
            _settings.History ??= [];
            _settings.History.Add(new Data.Models.DialogMessage { Author = _settings.Hero?.Name ?? "Hero", Text = L.W("I end my turn.") });
            combat.CurrentTurn = next.Symbol;
            _storage.Save();
            return true;
        }

        while (true)
        {
            // Hide cursor before any repositioning/drawing to avoid visual jumping
            Console.CursorVisible = false;

            // On movement: restore cursor and redraw in-place (no Console.Clear)
            if (!firstMapDraw)
                Console.SetCursorPosition(0, mapDrawTop);
            firstMapDraw = false;

            // The hero moved / the AI changed the world: recompute the path to the cell under the cursor.
            // After a simple step the frame is already drawn the fast way (RedrawAfterMove): no need for a second time.
            if (!movedFrameDrawn)
            {
                hoverReachability = null;
                _display.HoverPath = ComputeHoverPath(KnownCellOrNull(_display.HoveredCell));
                mapObjectsProvider.Refresh();
                FollowHeroIfMoved();
                _mapDisplay.DrawMap(mapObjectsProvider);
            }
            else
                Console.SetCursorPosition(_mapDisplay.DrawEndLeft, _mapDisplay.DrawEndTop);
            movedFrameDrawn = false;

            // New game: the DM's intro, character by character, with sound, like a normal reply.
            if (_aiClient.HasPendingIntro)
            {
                var introDialog = new DialogDisplay(_settings, _display, _storage);
                _aiClient.ActiveDialog = introDialog;
                introDialog.PrepareStreaming();
                await _aiClient.PlayIntro(introDialog.AppendStreamChunk, introDialog.NewStreamingMessage, redrawAction);
                introDialog.FinalizeStreaming();
                _aiClient.ActiveDialog = null;
                while (Console.KeyAvailable) Console.ReadKey(true);
                continue;
            }

            // The hero reached an exit to a block not yet created: the code generates it, the model fills it.
            if (await GrowLocationIfNeeded()) continue;

            var mapHistory = new DialogDisplay(_settings, _display, _storage);
            hoverDialog = mapHistory;
            // It's the hero's turn but he's unconscious (0 hit points): he doesn't act himself; the DM runs his turn (death saving
            // throw etc. per the tabletop rules), once a round, and the player has nothing to type.
            string? downTurn = null;
            if (_settings.Hero?.Dead == true) AddNoteOnce(L.F("{0} is dead. Game over — [Esc] menu.", _settings.Hero.Name));
            if (_settings.Combat is { Active: true } dc && dc.CurrentTurn == _settings.Hero?.Symbol
                && _settings.Hero is { IsDown: true, Dead: not true } && downTurnRound != _settings.Time.TotalRounds)
            {
                downTurnRound = _settings.Time.TotalRounds;
                downTurn = L.WF("[{0} is unconscious — the DM plays the turn]", _settings.Hero.Name);
            }
            var mapInput = downTurn ?? mapHistory.Draw();
            hoverDialog = null;

            // The hero died, the game is over: actions and movement aren't accepted (tabs and menu work as usual).
            if (_settings.Hero?.Dead == true && mapInput is { Length: > 0 } && !mapInput.StartsWith('F') && mapInput != "Esc")
            {
                AddNoteOnce(L.F("{0} is dead. Game over — [Esc] menu.", _settings.Hero.Name));
                continue;
            }

            // [F10] (or the button in the combat block) ends the hero's turn: the turn passes to the next by initiative, enemy turns go through the usual
            // "enemy turn" call. Previously the hero sent the text "I end my turn", and the DM wrote undeclared actions for him
            // ("you step back half a pace", "you catch your breath").
            if (mapInput == "F10" && _display.MapLevel == MapLevel.Location)
            {
                if (!_legendDisplay.IsHeroTurn) continue;
                if (PassTurnToNext())
                {
                    await RunEnemyTurnAndCheckRoundEnd();
                    break;
                }
                mapInput = L.W("I end my turn.");
            }
            if (mapInput == "F9" && _display.MapLevel == MapLevel.Location) {
                var braille = File.ReadAllText(Path.Combine(AppConfig.ProjectRoot, "Storage", "test.txt"));
                _settings.Images ??= [];
                _settings.Images.RemoveAll(i => i.Key == "test");
                _settings.Images.Add(new WorldImage { Key = "test", Content = braille, Height = braille.Split('\n').Length });
                _settings.ActiveImageKey = "test";
                continue;
            } // DEBUG

            // World map: [F10] sets off toward the selected destination, [F5] sets the pace.
            if (_display.MapLevel == MapLevel.World && mapInput == "F10")
            {
                await StartTravel(mapHistory);
                if (_display.PendingCommand != null) break;
                continue;
            }
            // [F12]: travel pace (slow / normal / fast).
            if (_display.MapLevel == MapLevel.World && mapInput == "F12")
            {
                _display.TravelPace = (_display.TravelPace + 1) % 3;
                RedrawWorldKeepCursor();
                continue;
            }

            if (string.IsNullOrEmpty(mapInput)) break;
            if (screen.Commands.TryGetValue(mapInput, out var screenCmd)) { screenCmd(); break; }
            if (_display.MapLevel == MapLevel.World && mapInput is "F6" or "F7" or "F8" or "F9")
            {
                ToggleWorldFilter(mapInput == "F6" ? null : WorldMapView.Categories[int.Parse(mapInput[1..]) - 7].key);
                movedFrameDrawn = true;
                continue;
            }
            if (_display.MapLevel == MapLevel.World && mapInput is "Tab" or "ShiftTab")
            {
                SelectWorldPlace(mapInput == "Tab" ? 1 : -1);
                while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                movedFrameDrawn = true;
                continue;
            }
            if (mapInput is "F6" or "F7" or "F8" or "NoteLeft" or "NoteRight") continue;   // service commands, not text for the DM

            // Ctrl+arrows move the camera without moving the hero (like dragging with the mouse).
            // Ctrl+"+"/"−" zooms the world map (the location map has no zoom).
            if (mapInput is "ZoomIn" or "ZoomOut")
            {
                if (_display.MapLevel == MapLevel.World)
                {
                    int sl = Console.CursorLeft, st = Console.CursorTop;
                    bool vis = Console.CursorVisible;
                    Console.CursorVisible = false;
                    _mapDisplay.ZoomWorldAtCenter(mapInput == "ZoomIn" ? 1 : -1);
                    _mapDisplay.RedrawWorldViewport();
                    Console.SetCursorPosition(sl, st);
                    Console.CursorVisible = vis;
                }
                movedFrameDrawn = true;
                continue;
            }

            if (mapInput.StartsWith("Pan"))
            {
                PanByKey(mapInput);
                movedFrameDrawn = true; // the frame is already redrawn: no full DrawMap needed
                continue;
            }
            // Alt+←/→ pages the card in the image panel (plain arrows on the map mean movement).
            // Without a card the command is simply ignored rather than sent to the model as action text.
            if (mapInput is "Left" or "Right")
            {
                int page = Math.Clamp(_display.NotePage + (mapInput == "Left" ? -1 : 1), 0, Math.Max(0, _display.NotePageCount - 1));
                if (_display.SelectedMapItemIndex >= 0 && _display.NotePageCount > 1 && page != _display.NotePage)
                {
                    Sound.PlayClick();
                    SetMapEntityImage(page);
                }
                continue;
            }

            if (mapInput == "Tab" || mapInput == "ShiftTab")
            {
                int totalCount = _legendDisplay.GetSelectableMapItemCount();
                if (totalCount > 0)
                {
                    if (mapInput == "Tab")
                        _display.SelectedMapItemIndex = _display.SelectedMapItemIndex < 0 ? 0
                            : (_display.SelectedMapItemIndex + 1) % totalCount;
                    else
                        _display.SelectedMapItemIndex = _display.SelectedMapItemIndex <= 0 ? totalCount - 1
                            : _display.SelectedMapItemIndex - 1;
                    SetMapEntityImage();
                }
                while (Console.KeyAvailable) Console.ReadKey(intercept: true);
                continue;
            }

            // On the world map the arrows move the map, not the hero (he's on the location).
            if (_display.MapLevel == MapLevel.World && mapInput.StartsWith("Move"))
            {
                if (mapInput.Contains("North")) PanByKey("PanUp");
                if (mapInput.Contains("South")) PanByKey("PanDown");
                if (mapInput.Contains("West")) PanByKey("PanLeft");
                if (mapInput.Contains("East")) PanByKey("PanRight");
                movedFrameDrawn = true;
                continue;
            }

            var moved = movementHandler.TryMove(mapInput, out var aiInput, out var activatedTriggers, out var activatedAreaTriggers, out var activatedDoorTriggers);
            if (movementHandler.LeaveRequested)
            {
                // Stepping through an exit onto the world map: the location is saved under the place, and the "Map" tab becomes the world.
                movementHandler.LeaveRequested = false;
                string where = _settings.World?.Place ?? L.T("the location");
                GameWorld.LeaveLocation(_settings);
                _settings.History ??= [];
                _settings.History.Add(new Data.Models.DialogMessage { Text = L.F("You leave {0} — world map: click a destination, [F10] to travel.", where) });
                _storage.Save();
                _display.PendingCommand = "F2";
                break;
            }
            if (moved == null)
            {
                _settings.History ??= [];
                _settings.History.Add(new Data.Models.DialogMessage { Author = _settings.Hero?.Name ?? "Hero", Text = mapInput });
                _display.DialogScrollOffset = 0;
                _aiClient.ActiveDialog = (mapHistory);
                mapHistory.PrepareStreaming();
                await _aiClient.SendAction(aiInput, mapHistory.AppendStreamChunk, mapHistory.NewStreamingMessage, mapHistory.TickSpinner, redrawAction);
                mapHistory.FinalizeStreaming();
                while (Console.KeyAvailable) Console.ReadKey(true);
                // The DM built a scene (plan_location): go to the terrain map; the tactical map is gone: go to the world.
                if ((_display.MapLevel == MapLevel.World) == GameWorld.HasLocation(_settings))
                {
                    _display.PendingCommand = GameWorld.HasLocation(_settings) ? "F1" : "F2";
                    break;
                }
                await FireEncounterTriggers(mapHistory);

                // SendAction itself can advance the round (spending action resources outside combat):
                // the reminder about onRound/onExpire effects must go through the same deduplicated
                // path as after movement, rather than relying on the AI figuring out on its own
                // to apply them inside its own reply (that caused repeated/unprompted
                // damage from periodic effects).
                var sendActionRoundBundle = _storage.TakeRoundBundle();
                if (sendActionRoundBundle != null && !_config.DisableTriggers && !_config.RevealMap)
                {
                    _aiClient.ActiveDialog = (mapHistory);
                    mapHistory.PrepareStreaming();
                    await _aiClient.FireRoundTransition(sendActionRoundBundle, mapHistory.AppendStreamChunk, mapHistory.NewStreamingMessage, mapHistory.TickSpinner, redrawAction);
                    mapHistory.FinalizeStreaming();
                    _aiClient.ActiveDialog = (null);
                    while (Console.KeyAvailable) Console.ReadKey(true);
                    break;
                }

                await RunEnemyTurnAndCheckRoundEnd();
                break;
            }
            // Hero moved — show new position before any AI call
            Console.CursorVisible = false;
            hoverReachability = null;
            _display.HoverPath = ComputeHoverPath(KnownCellOrNull(_display.HoveredCell));
            mapObjectsProvider.Refresh();
            FollowHeroIfMoved();
            _mapDisplay.RedrawAfterMove(mapObjectsProvider);
            movedFrameDrawn = true;
            var roundBundle = _storage.TakeRoundBundle();
            if (roundBundle != null && !_config.DisableTriggers && !_config.RevealMap)
            {
                _aiClient.ActiveDialog = (mapHistory);
                mapHistory.PrepareStreaming();
                await _aiClient.FireRoundTransition(roundBundle, mapHistory.AppendStreamChunk, mapHistory.NewStreamingMessage, mapHistory.TickSpinner, redrawAction);
                mapHistory.FinalizeStreaming();
                _aiClient.ActiveDialog = (null);
                while (Console.KeyAvailable) Console.ReadKey(true);
                break;
            }
            if (moved == true && !_config.DisableTriggers && !_config.RevealMap)
            {
                _aiClient.ActiveDialog = (mapHistory);
                mapHistory.PrepareStreaming();
                await _aiClient.FireTrigger(activatedTriggers, activatedAreaTriggers, activatedDoorTriggers, mapHistory.AppendStreamChunk, mapHistory.NewStreamingMessage, mapHistory.TickSpinner, redrawAction);
                mapHistory.FinalizeStreaming();
                _aiClient.ActiveDialog = (null);
                while (Console.KeyAvailable) Console.ReadKey(true);
                await FireEncounterTriggers(mapHistory);
                await RunEnemyTurnAndCheckRoundEnd();
                break;
            }
            if (IsHeroTurnOver())
            {
                await RunEnemyTurnAndCheckRoundEnd();
                break;
            }
            // Local move: loop back for in-place redraw.
            // Held arrow key: keyboard auto-repeat queues events faster than a step with redraw runs;
            // without a flush the hero kept "coasting" on the accumulated buffer after release (as with Tab before).
            // While the key is held, new repeats keep arriving, so movement isn't interrupted.
            while (Console.KeyAvailable) Console.ReadKey(intercept: true);
        }

        _display.ArrowKeysMovement = false;
        _display.PollAction = null;
        // OnMapRedraw is intentionally kept so tab-switching from Character/Abilities
        // screens can still show the map view during streaming.
        _display.HoveredCell = null;
        _display.HoveredDoor = null;
        ConsoleMouseReader.SetCursorShape(false);
    }
}
