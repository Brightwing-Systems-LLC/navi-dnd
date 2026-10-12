using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Unicode;
using ModelContextProtocol.Server;
using NaviDnD;
using NaviDnD.Clients;
using NaviDnD.Data.Models;
using NaviDnD.MapGen;
using NaviDnD.MapGen.Generators;
using NaviDnD.MapGen.Models;

namespace NaviDnD.McpServer;

[McpServerToolType]
public sealed class MapGenTools(McpServerConfig config)
{
    private static readonly JsonSerializerOptions _readOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private static readonly object _logLock = new();

    [McpServerTool]
    [Description("""
        Generates a map and writes it to worldState (cols, rows, rooms, doors).
        Returns the generated map's JSON so the AI can place entities and objects in
        rooms based on their coordinates and shape.

        MapGenRequest schema:
          type: "Cave" | "Dungeon" | "Building" | "OpenArea"  (default: "Dungeon")
          dungeon: {          ← required only for type "Dungeon"
            rooms: [
              { name: string,   ← REQUIRED, in the game's language
                shape: "Rectangular"|"Circular"|"Irregular"|"LShaped",
                size: "Small"|"Medium"|"Large"|"Huge", zone?: string }
            ]
            passages: [
              { from: string, to: string,
                type: "Opening"|"Corridor",
                width?: int (1–2), shape?: "Straight"|"LShaped"|"Winding",
                hasDoor?: bool }
            ]
            transitions: [
              { edgePoint: [col, row], room: string,
                width?: int, shape?: string, hasDoor?: bool }
            ]
          }

        Room shapes:
          "Rectangular" — a plain rectangle
          "Circular"    — an ellipse inscribed in the bounding rectangle
          "Irregular"   — a rectangle with organically uneven edges
          "LShaped"     — an L-shaped room: one corner cut off (35–60% of each side), edges
                          slightly uneven; good for guardrooms, side chapels and the like.

        zone values: "north", "south", "west", "east", "center",
                     "north_west", "north_east", "south_west", "south_east"

        transitions.edgePoint — a coordinate outside the grid; sets the entrance side:
          West [0, row]    East [21, row]
          North [col, 0]   South [col, 16]

        Recommendations: 3–6 rooms; at least one transition to exit the map.

        The map is generated on a cols×rows grid (default 20×15, which the edgePoints above refer to),
        then placed in the center of the 100×100 location field. All coordinates in the RESPONSE (rooms, doors) are already
        field coordinates: place the hero, creatures and objects by exactly those.
        """)]
    public string GenerateMap(
        [Description("MapGenRequest as a JSON string")]
        string requestJson)
    {
        Log($"→ GenerateMap: {requestJson}");

        MapGenRequest request;
        try
        {
            request = JsonSerializer.Deserialize<MapGenRequest>(requestJson, _readOptions)
                      ?? new MapGenRequest();
        }
        catch (Exception ex)
        {
            string err = $"{{\"error\": \"Invalid requestJson: {ex.Message}\"}}";
            Log($"← (parse error) {err}");
            return err;
        }

        IMapGenerator generator = request.Type switch
        {
            MapType.Dungeon  => new DungeonGenerator(),
            MapType.Cave     => new HardcodedCaveGenerator(),
            MapType.Building => new HardcodedCaveGenerator(),
            MapType.OpenArea => new HardcodedCaveGenerator(),
            _                => new HardcodedCaveGenerator(),
        };

        MapConfig mapConfig;
        try
        {
            mapConfig = generator.Generate(request);
        }
        catch (Exception ex)
        {
            string err = $"{{\"error\": \"Map generation failed: {ex.Message}\"}}";
            Log($"← (gen error) {err}");
            return err;
        }

        // The generator draws the map from (1,1) — place it in the center of the 100×100 location field (MapConfig.CenterInField)
        // so there is room on every side. The response below is already in field coordinates.
        mapConfig.CenterInField();

        // Apply the new map into worldState and persist it
        try
        {
            var mapUpdate = new { map = mapConfig };
            string mapUpdateJson = JsonSerializer.Serialize(mapUpdate, _writeOptions);

            var storage = LoadStorage();
            storage.ApplyUpdateWorldState(mapUpdateJson);
            SaveStorage(storage);
        }
        catch (Exception ex)
        {
            string err = $"{{\"error\": \"Failed to persist map: {ex.Message}\"}}";
            Log($"← (save error) {err}");
            return err;
        }

        // Return only what the AI needs: room positions + real doors (not passthrough) + entities/objects
        var aiResponse = new
        {
            map   = new { colors = mapConfig.Colors },
            rooms = mapConfig.Rooms?.Select((r, i) => new { id = i, r.Name, r.Color, r.Positions }).ToList(),
            doors = mapConfig.Doors?
                             .Select((d, i) => new { id = i, d.From, d.To, d.IsDoor, d.Color, d.IsDoorOpen, d.Hidden, d.IsEntry }).ToList(),
            //area         = mapConfig.Area,
            //triggerCells = mapConfig.TriggerCells,
        };

        string result = JsonSerializer.Serialize(aiResponse, _writeOptions);
        Log($"← ({result.Length} chars): {result}");
        return result;
    }

    private sealed record LocationPlanRequest(List<LocationGrower.PlannedBlock>? Blocks, string? Theme, bool? StartInside, int? WorldX = null, int? WorldY = null);

    [McpServerTool]
    [Description("""
        A story-driven location plan: which 20×15 blocks it consists of and what is in each. The engine immediately builds
        the geometry of all blocks (rooms, doors, passages between neighboring blocks) and places the hero at the entrance
        outside the first block. Returns the starting block (rooms with ids and cells, doors) — populate it in your
        response; the other blocks are populated separately when the hero approaches them.

        planJson: {"blocks": [{"x": 0, "y": 0, "z": 0, "terrain": "forest", "purpose": "..."}, ...], "startInside": false, "worldX": 12, "worldY": 34}
          worldX/worldY — the place's world map cell (from the add_place patch) if the hero isn't there yet (start of the game); otherwise not needed.
          startInside — true if the story starts the game inside the starting block (in a tavern, a house): the hero
          stands in the first room past the entrance; otherwise outside at the entrance.
          blocks[0] — the starting block, with the entrance from outside (exit from the location); x — east, y — north; neighboring blocks differ by 1
          in x or y; all blocks are joined by their sides into one shape; 1–8 blocks, the shape no bigger than 5×6.
          terrain — dungeon (man-made: crypt, ruins, fortress, catacombs — rooms, corridors, doors,
          tiled floor; the default) | cave (a natural cave: uneven halls, crawlways, stalagmites, no doors)
          | building (a building: a shaped hull, rooms with shared walls, doors, windows, furniture; at z=0 in a yard;
            "building": tavern | house | shop | temple | manor | barracks | tower — the layout: tavern/shop/
            temple/manor/barracks at z=0 have a large hall with a bar/counter/altar and back rooms; above — a corridor with rooms)
          | village (a settlement: 3–5 houses, streets, a square with a well, gardens; a tavern/temple is a separate building nearby)
          | forest | plains | hills | mountain | swamp | desert | snow | taiga (open terrain: relief without rooms; the kind
            of terrain and the river are taken by the engine from the world map — the hero's cell; give any open one). Terrain next to a dungeon block
          is separated by a rock wall with a passage (a cave/dungeon entrance from a forest or a mountain).
          z — the floor (default 0 — ground): upper floors of a building are blocks with the same x,y and z=1,2… (terrain building,
          with building under each); a cellar/dungeon under a block is z=-1… (dungeon/cave/building). Floors of the same
          x,y cell are joined by stairs; blocks[0] is at z=0; x/y neighbors on the same z are joined by a passage.
          purpose — what the block holds in the story (who/what is there, its role in the story), one sentence.
        """)]
    public string PlanLocation(
        [Description("The location plan as a JSON string")]
        string planJson)
    {
        Log($"→ PlanLocation: {planJson}");
        LocationPlanRequest? plan;
        try
        {
            plan = JsonSerializer.Deserialize<LocationPlanRequest>(planJson, _readOptions);
        }
        catch (Exception ex)
        {
            return $"{{\"error\": \"Invalid planJson: {ex.Message}\"}}";
        }

        try
        {
            var storage = LoadStorage();
            // Terrain comes from the world map cell where the hero is (forest/mountains/desert, river, coast).
            LocationGrower.WorldSite? site = null;
            if (storage.WorldState.World is { } link)
            {
                string worlds = Path.Combine(Path.GetDirectoryName(config.WorldStatePath) ?? ".", "Worlds");
                if (WorldLibrary.LoadFrom(Path.Combine(worlds, link.Id, "world.json")) is { } geo)
                {
                    var all = GameWorld.Compose(geo, link, includeHidden: true);
                    (int x, int y)? tile = plan?.WorldX is int wx && plan.WorldY is int wy ? (wx, wy)
                        : link.X is int hx && link.Y is int hy ? (hx, hy)
                        : link.Place is { } pl && MapGen.Generators.WorldAtlas.FindPlace(all, pl) is { } p ? (p.X, p.Y) : null;
                    if (tile is { } t) site = GameWorld.SiteAt(geo, t.x, t.y);
                }
            }
            var (start, error) = LocationGrower.CreatePlanned(storage.WorldState, plan?.Blocks ?? [], plan?.Theme ?? "dungeon", plan?.StartInside == true, site);
            if (start == null)
            {
                Log($"← (plan error) {error}");
                return $"{{\"error\": \"{error}\"}}";
            }
            SaveStorage(storage);
            // Mid-game (SendAction) the game keeps the state in memory — a flag: reread the map from the save.
            try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(config.WorldStatePath) ?? ".", Storage.LocationPlannedFlag), ""); } catch { }

            var ctx = new AiContextBuilder(storage.WorldState);
            string result = ctx.LocationPlan(start) + "\n" + ctx.ChunkState(start);
            Log($"← ({result.Length} chars): {result}");
            return result;
        }
        catch (Exception ex)
        {
            string err = $"{{\"error\": \"Location generation failed: {ex.Message}\"}}";
            Log($"← (gen error) {err}");
            return err;
        }
    }

    // ── Helpers ────────────────────────────────────────────────────────────

    private Storage LoadStorage()
    {
        string json = RetryOnIo(() =>
        {
            using var stream = new FileStream(config.WorldStatePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            return reader.ReadToEnd();
        });
        var storage = new Storage();
        storage.ApplySavedState(json);
        return storage;
    }

    private void SaveStorage(Storage storage)
    {
        string json = JsonSerializer.Serialize(storage.WorldState, _writeOptions);
        RetryOnIo(() =>
        {
            using var stream = new FileStream(config.WorldStatePath, FileMode.Create, FileAccess.Write, FileShare.Read);
            using var writer = new StreamWriter(stream, Encoding.UTF8);
            writer.Write(json);
        });
    }

    private static T RetryOnIo<T>(Func<T> action, int attempts = 5, int delayMs = 30)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { return action(); }
            catch (IOException) when (i < attempts - 1) { Thread.Sleep(delayMs); }
        }
        throw new InvalidOperationException("IO retry limit exceeded");
    }

    private static void RetryOnIo(Action action, int attempts = 5, int delayMs = 30)
    {
        for (int i = 0; i < attempts; i++)
        {
            try { action(); return; }
            catch (IOException) when (i < attempts - 1) { Thread.Sleep(delayMs); }
        }
        throw new InvalidOperationException("IO retry limit exceeded");
    }

    private void Log(string message)
    {
        if (config.LogPath is null) return;
        lock (_logLock)
            File.AppendAllText(config.LogPath, $"  MapGen {message}\n", new UTF8Encoding(false));
    }
}
