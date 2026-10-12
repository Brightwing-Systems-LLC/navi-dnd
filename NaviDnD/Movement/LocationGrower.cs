using NaviDnD.Data.Models;
using NaviDnD.MapGen.Generators;

namespace NaviDnD;

// A location made of 20×15 blocks (MapConfig.Chunks) following the AI's plan (plan_location): which blocks, where they sit
// relative to each other and what each holds story-wise. The code builds the geometry of all blocks at once — the location
// is finite; the AI populates a block when the hero approaches it (UnpopulatedNearHero → PopulateChunk).
public static class LocationGrower
{
    // At what distance (in cells) from the edge of a not-yet-populated block it gets populated.
    public const int TriggerDistance = 4;

    public const int MaxBlocks = 8;

    // A plan block: X — right (east), Y — up (north), Z — floor (0 — ground, 1+ — above, −1… — cellars),
    // relative to each other; the first is the starting one (entrance from outside, Z = 0). Terrain is the block's terrain type:
    // dungeon (default), cave, building, village or open terrain (MapChunk.IsOutdoorTheme). Building is the
    // building type (tavern, house, shop, temple, manor, barracks, tower): it drives the layout and furniture.
    public record PlannedBlock(int X, int Y, string? Purpose, string? Terrain = null, int Z = 0, string? Building = null);

    public static readonly string[] Terrains = ["dungeon", MapChunk.Cave, MapChunk.Building, MapChunk.Village, "forest", "plains", "hills", "mountain", "swamp", "desert", "snow", "taiga"];

    // The world map cell where the location is built (GameWorld.SiteAt): open-block terrain (by biome), a river
    // through the location (axis, width), sides with water (sea/lake — the location edge is water). If absent — as the DM intended.
    public sealed record WorldSite(string Theme, bool? RiverHorizontal, int RiverWidth, HashSet<ChunkSide> WaterSides);

    // A new location from the plan. The map's previous contents (rooms, creatures…) are reset, the hero is placed
    // at the entrance outside (startInside — inside, in the first room past the entrance). Returns the starting block or the plan
    // error text (the map is then untouched).
    public static (MapChunk? start, string? error) CreatePlanned(WorldState ws, List<PlannedBlock> blocks, string theme = "dungeon", bool startInside = false,
        WorldSite? site = null)
    {
        if (Validate(blocks, theme) is { } error) return (null, error);

        // Floors lie in different parts of the block grid: ground in the centre, the rest in free cells.
        var rng = new Random();
        if (PlaceFloors(blocks, rng) is not { } place) return (null, "the floors do not fit on the grid — make the plan smaller");

        var map = ws.Map;
        map.Rooms = [];
        map.Doors = [];
        map.Area = [];
        map.Entities = [];
        map.Objects = [];
        map.Chunks = [];
        map.Stairs = null;
        map.Furniture = [];
        map.Cols = MapConfig.MaxSize;
        map.Rows = MapConfig.MaxSize;
        // Map colours as in DungeonGenerator; without this a black background and white grid lines remain.
        map.Colors = new ColorSetting { MapBackground = new NaviDnD.Data.DisplayConfig().MainBackground, MapForeground = [200, 190, 160] };

        var chunks = blocks.Select(b =>
        {
            var (gx, gy) = place[b];
            string t = b.Terrain ?? theme;
            // Open terrain matches the world map (no desert in the mountains).
            if (site != null && MapChunk.IsOutdoorTheme(t)) t = site.Theme;
            return new MapChunk { X = gx, Y = gy, Z = b.Z, Seed = rng.Next(), Theme = t, Purpose = b.Purpose, Style = t == MapChunk.Building ? b.Building ?? "house" : null };
        }).ToList();
        // Adjacency follows the plan (x, y, z): blocks of different floors that end up next to each other on the grid are not connected.
        var byPlan = blocks.Select((b, i) => (b, i)).ToDictionary(x => (x.b.X, x.b.Y, x.b.Z), x => chunks[x.i]);
        var planOf = chunks.Select((c, i) => (c, i)).ToDictionary(x => x.c, x => blocks[x.i]);
        MapChunk? Neighbor(MapChunk c, ChunkSide side)
        {
            var (dx, dy) = ChunkExit.Step(side);
            var b = planOf[c];
            return byPlan.GetValueOrDefault((b.X + dx, b.Y + dy, b.Z));
        }

        foreach (var c in chunks.Where(c => c.Z > 0))
            if (byPlan.GetValueOrDefault((planOf[c].X, planOf[c].Y, 0)) is { } ground)
                (c.GroundX, c.GroundY) = (ground.X, ground.Y);

        var exits = chunks.ToDictionary(c => c, _ => new List<ChunkExit>());
        var start = chunks[0];
        var freeSides = Enum.GetValues<ChunkSide>().Where(s => Neighbor(start, s) == null).ToList();
        var startSide = freeSides[rng.Next(freeSides.Count)];
        exits[start].Add(new ChunkExit { Side = startSide, Offset = RandomOffset(startSide, rng), External = true });

        // Connections: a spanning tree over same-floor neighbours + an occasional extra passage — a loop. Floors are
        // connected to each other only by stairs — each floor has its own tree.
        var linked = new HashSet<(MapChunk, MapChunk)>();
        void Link(MapChunk a, ChunkSide side)
        {
            var b = Neighbor(a, side)!;
            int offset = RandomOffset(side, rng);
            exits[a].Add(new ChunkExit { Side = side, Offset = offset });
            exits[b].Add(new ChunkExit { Side = ChunkExit.Opposite(side), Offset = offset });
            linked.Add((a, b));
            linked.Add((b, a));
        }

        var visited = new HashSet<MapChunk>();
        foreach (var root in chunks)
        {
            if (!visited.Add(root)) continue;
            var queue = new Queue<MapChunk>([root]);
            while (queue.Count > 0)
            {
                var c = queue.Dequeue();
                foreach (var side in Enum.GetValues<ChunkSide>().OrderBy(_ => rng.Next()))
                {
                    if (Neighbor(c, side) is not { } n || !visited.Add(n)) continue;
                    Link(c, side);
                    queue.Enqueue(n);
                }
            }
        }
        foreach (var c in chunks)
            foreach (var side in new[] { ChunkSide.Right, ChunkSide.Top })
                if (Neighbor(c, side) is { } n && !linked.Contains((c, n)) && rng.Next(4) == 0)
                    Link(c, side);

        // One outline for the whole building: floors of the same plan cell (x, y) share an outline seed.
        var footprintSeeds = new Dictionary<(int, int), int>();
        int noiseSeed = rng.Next();
        // The river from the world map crosses the whole terrain (one line on the grid, blocks join up).
        OutdoorGenerator.RiverSpec? river = null;
        if (site?.RiverHorizontal is bool rh && chunks.Any(c => MapChunk.IsOutdoorTheme(c.Theme) && c.Z == 0))
        {
            var outdoor = chunks.Where(c => MapChunk.IsOutdoorTheme(c.Theme) && c.Z == 0).ToList();
            int center = rh ? (outdoor.Min(c => c.OriginRow) + outdoor.Max(c => c.OriginRow) + MapChunk.Rows) / 2 + rng.Next(-3, 4)
                            : (outdoor.Min(c => c.OriginCol) + outdoor.Max(c => c.OriginCol) + MapChunk.Cols) / 2 + rng.Next(-4, 5);
            river = new OutdoorGenerator.RiverSpec(rh, center, site.RiverWidth, rng.Next());
        }
        foreach (var c in chunks)
        {
            c.Exits = exits[c];
            if (c.Theme == MapChunk.Building)
            {
                var b = planOf[c];
                if (!footprintSeeds.TryGetValue((b.X, b.Y), out int fs)) footprintSeeds[(b.X, b.Y)] = fs = rng.Next();
                var seamDoors = Enum.GetValues<ChunkSide>()
                    .Where(side => Neighbor(c, side)?.Theme == MapChunk.Building && side is ChunkSide.Right or ChunkSide.Top).ToList();
                var built = BuildingGenerator.Generate(c, exits[c], fs, ground: c.Z == 0, GroundBands(c), seamDoors);
                c.Terrain = c.Z == 0 && site != null ? Reskin(built.Terrain, site.Theme) : built.Terrain;
                map.Rooms.AddRange(built.Rooms);
                map.Doors.AddRange(built.Doors);
                map.Furniture.AddRange(built.Furniture);
                map.Chunks.Add(c);
                continue;
            }
            if (c.Theme == MapChunk.Village)
            {
                var village = BuildingGenerator.GenerateVillage(c, exits[c], GroundBands(c));
                c.Terrain = site != null ? Reskin(village.Terrain, site.Theme) : village.Terrain;
                map.Rooms.AddRange(village.Rooms);
                map.Doors.AddRange(village.Doors);
                map.Furniture.AddRange(village.Furniture);
                map.Chunks.Add(c);
                continue;
            }
            if (!MapChunk.IsOutdoorTheme(c.Theme)) { AddChunk(map, c, exits[c]); continue; }

            // Edges of open terrain: towards neighbouring terrain/a building (its yard) — open, towards a dungeon — a rock
            // wall (its entrance is an opening in the rock), towards the void — a natural barrier by terrain type.
            var outdoorBands = new Dictionary<ChunkSide, char>();
            foreach (var side in Enum.GetValues<ChunkSide>())
            {
                if (Neighbor(c, side) is { } n)
                {
                    if (!MapChunk.IsOpenGround(n.Theme)) outdoorBands[side] = '^';
                }
                else outdoorBands[side] = site?.WaterSides.Contains(side) == true ? 'W'
                    : c.Theme switch { "hills" or "mountain" or "desert" => '^', "swamp" => 'W', "snow" or "taiga" => 'e', _ => 'F' };
            }
            var result = OutdoorGenerator.Generate(c, exits[c], outdoorBands, noiseSeed, c.Z == 0 ? river : null);
            c.Terrain = result.Terrain;
            map.Doors.AddRange(result.Doors);
            map.Chunks.Add(c);
        }

        // Stairs between floors stacked on top of each other in the plan.
        foreach (var lower in chunks)
        {
            var b = planOf[lower];
            if (byPlan.GetValueOrDefault((b.X, b.Y, b.Z + 1)) is { } upper)
                AddStairs(map, lower, upper, rng);
        }

        var (startCol, startRow) = exits[start][0].AnchorCell(start);
        MarkWorldExit(map, startCol, startRow);
        if (ws.Hero != null)
            ws.Hero.Position = startInside && FirstRoomCell(map, start, startCol, startRow) is var (ic, ir) ? [ic, ir] : [startCol, startRow];
        return (start, null);

        // Edges of a building/settlement yard: towards open ground — open, towards a dungeon — rock, no neighbour — trees and thicket.
        Dictionary<ChunkSide, char> GroundBands(MapChunk c)
        {
            var bands = new Dictionary<ChunkSide, char>();
            foreach (var side in Enum.GetValues<ChunkSide>())
            {
                var n = Neighbor(c, side);
                if (n == null) bands[side] = BuildingGenerator.NaturalBand;
                else if (!MapChunk.IsOpenGround(n.Theme)) bands[side] = '^';
            }
            return bands;
        }
    }

    // The first room cell on the way from the outside entrance (by the hero's steps: inside a room — to an adjacent cell, between
    // rooms/the yard — only through a door or passage) — the hero is placed here if the game starts inside.
    private static (int col, int row)? FirstRoomCell(MapConfig map, MapChunk chunk, int col, int row)
    {
        var roomOf = new Dictionary<(int, int), int>();
        for (int i = 0; i < map.Rooms!.Count; i++)
            foreach (var p in map.Rooms[i].Positions ?? [])
                if (p.Count >= 2 && chunk.Contains(p[0], p[1])) roomOf[(p[0], p[1])] = i;
        var links = new HashSet<((int, int), (int, int))>();
        foreach (var d in map.Doors ?? [])
        {
            if (d.IsWindow == true || d.From is not { Count: >= 2 } f || d.To is not { Count: >= 2 } t) continue;
            links.Add(((f[0], f[1]), (t[0], t[1])));
            links.Add(((t[0], t[1]), (f[0], f[1])));
        }
        bool Walkable((int c, int r) x) => chunk.Contains(x.c, x.r) && map.TerrainAt(x.c, x.r) is { BlocksMove: false } k && !TerrainCatalog.IsStair(k.Code);
        var start = (col, row);
        var seen = new HashSet<(int, int)> { start };
        var queue = new Queue<(int c, int r)>([start]);
        while (queue.Count > 0)
        {
            var x = queue.Dequeue();
            if (roomOf.TryGetValue(x, out int room) && map.Rooms[room].Passage != true)
            {
                // A step or two into the room, not on the threshold.
                var deeper = new[] { (x.c + 1, x.r), (x.c - 1, x.r), (x.c, x.r + 1), (x.c, x.r - 1) }
                    .FirstOrDefault(n => roomOf.TryGetValue(n, out int rn) && rn == room && Walkable(n) && !seen.Contains(n));
                return deeper != default ? deeper : x;
            }
            foreach (var n in new[] { (x.c + 1, x.r), (x.c - 1, x.r), (x.c, x.r + 1), (x.c, x.r - 1) })
            {
                if (!Walkable(n) || seen.Contains(n)) continue;
                int ra = roomOf.GetValueOrDefault(x, -1), rb = roomOf.GetValueOrDefault(n, -1);
                if (ra != rb && !links.Contains((x, n))) continue;
                seen.Add(n);
                queue.Enqueue(n);
            }
        }
        return null;
    }

    // Fallback (the AI sent no plan): a single block.
    public static MapChunk CreateStart(WorldState ws, string theme = "dungeon") =>
        CreatePlanned(ws, [new PlannedBlock(0, 0, null)], theme).start!;

    private static string? Validate(List<PlannedBlock> blocks, string theme)
    {
        if (blocks.Count == 0) return "the plan is empty — at least one block is needed";
        if (blocks.Count > MaxBlocks) return $"no more than {MaxBlocks} blocks";
        if (blocks.Select(b => (b.X, b.Y, b.Z)).Distinct().Count() != blocks.Count) return "two blocks have the same x,y,z";
        if (blocks.FirstOrDefault(b => b.Terrain != null && !Terrains.Contains(b.Terrain)) is { } badTerrain)
            return $"unknown terrain \"{badTerrain.Terrain}\", allowed: {string.Join(", ", Terrains)}";
        if (blocks[0].Z != 0) return "the first (starting) block must be on the ground (z = 0)";
        var planned = blocks.ToDictionary(b => (b.X, b.Y, b.Z));
        foreach (var b in blocks.Where(b => b.Z != 0))
        {
            string t = b.Terrain ?? theme;
            if (MapChunk.IsOutdoorTheme(t) || t == MapChunk.Village) return $"{t} — only on the ground (z = 0)";
            if (b.Z > 0)
            {
                if (planned.GetValueOrDefault((b.X, b.Y, b.Z - 1)) is not { } below)
                    return $"block ({b.X},{b.Y},{b.Z}) needs block ({b.X},{b.Y},{b.Z - 1}) below it — floors stack on top of each other";
                if ((below.Terrain ?? theme) != MapChunk.Building || t != MapChunk.Building)
                    return $"above ground only building floors are allowed: block ({b.X},{b.Y},{b.Z}) and the one below it must be building";
            }
            if (b.Z < 0 && !planned.ContainsKey((b.X, b.Y, b.Z + 1)))
                return $"cellar ({b.X},{b.Y},{b.Z}) needs block ({b.X},{b.Y},{b.Z + 1}) above it";
        }
        foreach (var floor in blocks.GroupBy(b => b.Z))
        {
            int w = floor.Max(b => b.X) - floor.Min(b => b.X) + 1, h = floor.Max(b => b.Y) - floor.Min(b => b.Y) + 1;
            if (w > MapChunk.GridCols || h > MapChunk.GridRows)
                return $"floor z={floor.Key} spans {w}×{h} blocks, maximum {MapChunk.GridCols}×{MapChunk.GridRows}";
        }

        // Connectivity: side neighbours on the same floor + stairs between floors of the same cell (x, y).
        var seen = new HashSet<(int, int, int)> { (blocks[0].X, blocks[0].Y, 0) };
        var queue = new Queue<(int x, int y, int z)>([(blocks[0].X, blocks[0].Y, 0)]);
        while (queue.Count > 0)
        {
            var (x, y, z) = queue.Dequeue();
            foreach (var n in new[] { (x + 1, y, z), (x - 1, y, z), (x, y + 1, z), (x, y - 1, z), (x, y, z + 1), (x, y, z - 1) })
                if (planned.ContainsKey(n) && seen.Add(n)) queue.Enqueue(n);
        }
        if (seen.Count != blocks.Count)
            return "blocks must connect: by sides on the same floor (neighbours differing by 1 in x or y) or as floors on top of each other";
        var b0 = blocks[0];
        if (Enum.GetValues<ChunkSide>().All(s =>
            {
                var (dx, dy) = ChunkExit.Step(s);
                return planned.ContainsKey((b0.X + dx, b0.Y + dy, 0));
            }))
            return "the first (starting) block needs a free side for the entrance from outside";
        return null;
    }

    // A block-grid cell for each plan block. The ground floor is in the centre; every other floor goes whole
    // (keeping its layout) into a free spot, preferably not right next to other floors.
    private static Dictionary<PlannedBlock, (int x, int y)>? PlaceFloors(List<PlannedBlock> blocks, Random rng)
    {
        var place = new Dictionary<PlannedBlock, (int x, int y)>(ReferenceEqualityComparer.Instance);
        var taken = new Dictionary<(int, int), int>(); // grid cell → floor
        foreach (var floor in blocks.GroupBy(b => b.Z).OrderBy(g => Math.Abs(g.Key)).ThenByDescending(g => g.Key))
        {
            var list = floor.ToList();
            int minX = list.Min(b => b.X), minY = list.Min(b => b.Y);
            int w = list.Max(b => b.X) - minX + 1, h = list.Max(b => b.Y) - minY + 1;
            (int ox, int oy)? best = null;
            int bestScore = int.MaxValue;
            List<(int x, int y)> offsets = floor.Key == 0
                ? [((MapChunk.GridCols - w) / 2, (MapChunk.GridRows - h) / 2)]
                : Enumerable.Range(0, MapChunk.GridCols - w + 1)
                    .SelectMany(x => Enumerable.Range(0, MapChunk.GridRows - h + 1).Select(y => (x, y)))
                    .OrderBy(_ => rng.Next()).ToList();
            foreach (var (ox, oy) in offsets)
            {
                var cells = list.Select(b => (b.X - minX + ox, b.Y - minY + oy)).ToList();
                if (cells.Any(taken.ContainsKey)) continue;
                // The fewer neighbours from other floors (diagonals included), the better. The grid edge is worse: the camera
                // hits the boundary there, and the map would shift when taking the stairs.
                int score = cells.Sum(c => taken.Count(t => t.Value != floor.Key
                    && Math.Abs(t.Key.Item1 - c.Item1) <= 1 && Math.Abs(t.Key.Item2 - c.Item2) <= 1))
                    + cells.Count(c => c.Item1 == 0 || c.Item1 == MapChunk.GridCols - 1 || c.Item2 == 0 || c.Item2 == MapChunk.GridRows - 1) * 10;
                if (score < bestScore) { bestScore = score; best = (ox, oy); }
            }
            if (best is not var (bx, by)) return null;
            foreach (var b in list)
            {
                var cell = (b.X - minX + bx, b.Y - minY + by);
                place[b] = cell;
                taken[cell] = floor.Key;
            }
        }
        return place;
    }


    // Stairs: a lower-block "up" cell (U) ↔ an upper-block "down" cell (D). The cell is walkable, by a wall,
    // not next to a door/exit and does not cut a room in two (stepping on it moves the hero — you cannot walk through it).
    // Preferably in the main hall/corridor (a cell's weight is its room's size, a corridor's is the largest), ideally
    // in the same spot in both blocks (building floors stand on top of each other).
    private static void AddStairs(MapConfig map, MapChunk lower, MapChunk upper, Random rng)
    {
        var lowerCells = StairCandidates(map, lower);
        var upperCells = StairCandidates(map, upper);
        if (lowerCells.Count == 0 || upperCells.Count == 0) return;
        double Jitter() => rng.NextDouble() * 4;
        var common = lowerCells.Keys.Where(upperCells.ContainsKey)
            .Select(x => (x, score: lowerCells[x] + upperCells[x] + Jitter())).OrderByDescending(p => p.score).FirstOrDefault();
        var lowerBest = lowerCells.MaxBy(p => p.Value + Jitter());
        var upperBest = upperCells.MaxBy(p => p.Value + Jitter());
        bool aligned = common.score > 0; // the same cell on both floors — the stairs are stacked "one above the other"
        var (lc, lr) = aligned ? common.x : lowerBest.Key;
        var (uc, ur) = aligned ? common.x : upperBest.Key;
        SetTerrain(lower, lc, lr, 'U');
        SetTerrain(upper, uc, ur, 'D');
        (map.Stairs ??= []).Add(new StairLink
        {
            A = [lower.OriginCol + lc + 1, lower.OriginRow + lr + 1],
            B = [upper.OriginCol + uc + 1, upper.OriginRow + ur + 1],
        });
    }

    // The block's cells suitable for stairs (local coordinates) → weight (higher is more fitting).
    private static Dictionary<(int c, int r), int> StairCandidates(MapConfig map, MapChunk chunk)
    {
        // Cell → area: room index, −1 for open terrain outside rooms.
        var region = new Dictionary<(int c, int r), int>();
        var inAnyRoom = new HashSet<(int, int)>();
        var rooms = map.Rooms ?? [];
        for (int i = 0; i < rooms.Count; i++)
            foreach (var p in rooms[i].Positions ?? [])
            {
                if (p.Count < 2 || !chunk.Contains(p[0], p[1])) continue;
                var local = (p[0] - chunk.OriginCol - 1, p[1] - chunk.OriginRow - 1);
                inAnyRoom.Add(local);
                region[local] = i;
            }
        if (chunk.Terrain is { } rows)
            for (int r = 0; r < rows.Count; r++)
                for (int c = 0; c < rows[r].Length; c++)
                    if (rows[r][c] != ' ' && !inAnyRoom.Contains((c, r)))
                        region[(c, r)] = -1;

        var avoid = new HashSet<(int, int)>();
        void Avoid(int col, int row, int radius)
        {
            for (int dc = -radius; dc <= radius; dc++)
                for (int dr = -radius; dr <= radius; dr++)
                    avoid.Add((col - chunk.OriginCol - 1 + dc, row - chunk.OriginRow - 1 + dr));
        }
        foreach (var d in map.Doors ?? [])
            if (d.IsWindow != true)
                foreach (var p in new[] { d.From, d.To })
                    if (p is { Count: >= 2 } && chunk.Contains(p[0], p[1])) Avoid(p[0], p[1], 1);
        foreach (var e in chunk.Exits)
        {
            var (ac, ar) = e.AnchorCell(chunk);
            Avoid(ac, ar, 2);
        }
        // A middle floor: the stairs down are already placed — put the stairs up away from them.
        for (int r = 0; r < MapChunk.Rows; r++)
            for (int c = 0; c < MapChunk.Cols; c++)
                if (TerrainCatalog.IsStair(TerrainChar(chunk, c, r))) Avoid(chunk.OriginCol + c + 1, chunk.OriginRow + r + 1, 1);

        bool Free((int c, int r) x) => region.ContainsKey(x) && map.TerrainAt(chunk.OriginCol + x.c + 1, chunk.OriginRow + x.r + 1) is not { BlocksMove: true };
        (int, int)[] Around((int c, int r) x) => [(x.c + 1, x.r), (x.c - 1, x.r), (x.c, x.r + 1), (x.c, x.r - 1)];

        // In a room — by a wall; on open terrain — anywhere except the trail. If there are rooms (a building with a yard) —
        // the stairs go in the house, not in the yard.
        // Strict: not next to doors/exits/other stairs; relaxed: just not on a door or stairs itself.
        var exact = new HashSet<(int, int)>();
        foreach (var d in map.Doors ?? [])
            if (d.IsWindow != true)
                foreach (var p in new[] { d.From, d.To })
                    if (p is { Count: >= 2 }) exact.Add((p[0] - chunk.OriginCol - 1, p[1] - chunk.OriginRow - 1));
        bool strict = true;
        // Corridors have doors every 3–4 cells — there we avoid only the door cells themselves.
        bool InPassage((int c, int r) x) => region[x] >= 0 && rooms[region[x]].Passage == true;
        bool Avoided((int c, int r) x) => strict && !InPassage(x) ? avoid.Contains(x) : exact.Contains(x) || TerrainCatalog.IsStair(TerrainChar(chunk, x.c, x.r));
        List<(int c, int r)> Candidates(bool byWall) => region.Keys.Where(x => Free(x) && !Avoided(x) && TerrainChar(chunk, x.c, x.r) != '='
                && (!byWall || region[x] < 0 || Around(x).Any(n => !region.TryGetValue(n, out int rn) || rn != region[x]))
                && !CutsRegion(x, region, Free))
            .OrderBy(x => x).ToList();
        // Nothing by a wall (a cramped tower floor with a second staircase) — anywhere, as long as it does not cut the room in two.
        var list = Candidates(byWall: true);
        if (list.Count == 0) list = Candidates(byWall: false);
        if (list.Count == 0) { strict = false; list = Candidates(byWall: false); }
        if (list.Any(x => region[x] >= 0)) list = list.Where(x => region[x] >= 0).ToList();
        int Weight((int c, int r) x) => region[x] < 0 ? 1 : rooms[region[x]].Passage == true ? 100 : rooms[region[x]].Positions?.Count ?? 1;
        return list.ToDictionary(x => x, Weight);
    }

    // Cell x is a "chokepoint": without it its area (room / open terrain) falls apart.
    private static bool CutsRegion((int c, int r) x, Dictionary<(int c, int r), int> region, Func<(int c, int r), bool> free)
    {
        int id = region[x];
        var cells = region.Where(p => p.Value == id && p.Key != x && free(p.Key)).Select(p => p.Key).ToHashSet();
        if (cells.Count < 4) return true;
        var startCell = cells.First();
        var seen = new HashSet<(int, int)> { startCell };
        var queue = new Queue<(int c, int r)>([startCell]);
        while (queue.Count > 0)
        {
            var (c, r) = queue.Dequeue();
            foreach (var n in new[] { (c + 1, r), (c - 1, r), (c, r + 1), (c, r - 1) })
                if (cells.Contains(n) && seen.Add(n)) queue.Enqueue(n);
        }
        return seen.Count != cells.Count;
    }

    private static char TerrainChar(MapChunk chunk, int c, int r) =>
        chunk.Terrain is { } t && r < t.Count && c < t[r].Length ? t[r][c] : ' ';

    private static void SetTerrain(MapChunk chunk, int c, int r, char code)
    {
        chunk.Terrain ??= [.. Enumerable.Repeat(new string(' ', MapChunk.Cols), MapChunk.Rows)];
        var line = chunk.Terrain[r].ToCharArray();
        line[c] = code;
        chunk.Terrain[r] = new string(line);
    }

    // A building's yard and village streets — ground matching the world cell: sand and thorns in the desert, snow and firs in the snowlands
    // (house floors, furniture, fences are untouched — they have their own codes).
    private static List<string> Reskin(List<string> terrain, string theme)
    {
        Func<char, char>? map = theme switch
        {
            "desert" => ch => ch switch { '.' or ',' => 'a', '"' => 'c', 'T' => 'c', 'F' => '^', _ => ch },
            "snow" or "taiga" => ch => ch switch { '.' or ',' or '"' => 'n', 'T' or 'F' => 'e', _ => ch },
            "mountain" => ch => ch switch { '.' => 's', _ => ch },
            _ => null,
        };
        return map == null ? terrain : terrain.Select(row => new string(row.Select(map).ToArray())).ToList();
    }

    // The cell by the outside entrance (the exit to the world map) — where a hero returning to a saved location stands.
    public static (int col, int row)? EntranceCell(MapConfig map)
    {
        if (map.Chunks?.FirstOrDefault(c => c.Exits.Any(e => e.External)) is not { } start) return null;
        return start.Exits.First(e => e.External).AnchorCell(start);
    }

    // Colour of the location's exit door to the world map — distinct from ordinary doors.
    public static readonly List<int> WorldExitColor = [160, 90, 220];

    // The outside entrance (the anchor cell on the block edge) — the exit door to the world map: closed, in its own colour.
    // It also plugs the gap in the wall with the grid's void behind it.
    private static void MarkWorldExit(MapConfig map, int col, int row)
    {
        foreach (var door in map.Doors ?? [])
        {
            if (door.IsEntry != true || door.From is not { Count: >= 2 } f || f[0] != col || f[1] != row) continue;
            door.IsWorldExit = true;
            door.IsDoor = true;
            door.IsDoorOpen = false;
            door.Color = [.. WorldExitColor];
        }
    }

    // A not-yet-populated block the hero has approached (within TriggerDistance of its edges) — time to populate it.
    public static MapChunk? UnpopulatedNearHero(WorldState ws)
    {
        if (ws.Map.Chunks is not { Count: > 0 } chunks || ws.Hero?.Position is not { Count: >= 2 } hp) return null;
        int floor = ws.Map.FloorAt(hp[0], hp[1]);
        return chunks.FirstOrDefault(c => !c.Populated && c.Z == floor
            && hp[0] >= c.OriginCol + 1 - TriggerDistance && hp[0] <= c.OriginCol + MapChunk.Cols + TriggerDistance
            && hp[1] >= c.OriginRow + 1 - TriggerDistance && hp[1] <= c.OriginRow + MapChunk.Rows + TriggerDistance);
    }

    // The block's rooms and doors are appended to the lists — ids of existing rooms/doors do not change.
    private static void AddChunk(MapConfig map, MapChunk chunk, List<ChunkExit> exits)
    {
        chunk.Exits = exits;
        var result = ChunkGenerator.Generate(chunk, exits);
        chunk.Terrain = DungeonDecorator.Decorate(chunk, result.Rooms, result.Doors);
        (map.Rooms ??= []).AddRange(result.Rooms);
        (map.Doors ??= []).AddRange(result.Doors);
        (map.Chunks ??= []).Add(chunk);
    }

    // The seam is not right at the block corner — corridors to neighbouring exits do not merge.
    private static int RandomOffset(ChunkSide side, Random rng) => side is ChunkSide.Left or ChunkSide.Right
        ? rng.Next(3, MapChunk.Rows - 1)
        : rng.Next(3, MapChunk.Cols - 1);
}
