using System.ComponentModel;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Unicode;
using ModelContextProtocol.Server;
using NaviDnD.Data.Models;
using NaviDnD.MapGen.Generators;

namespace NaviDnD.McpServer;

// The world map for the DM: world_query — info about a place, what's nearby, a route; add_place — a new place on the map
// (the code picks the cell). The game's world = geometry (Storage/Worlds/<world.id>/world.json next to the state file)
// + the game layer (world in the state file: concept, places, trails). The tools write nothing themselves —
// add_place returns a patch that the DM puts in its response (like advance_round).
[McpServerToolType]
public sealed class WorldQueryTools(McpServerConfig config)
{
    private static readonly object _logLock = new();

    private static readonly JsonSerializerOptions _readOptions = new() { PropertyNameCaseInsensitive = true };
    private static readonly JsonSerializerOptions _writeOptions = new()
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    [McpServerTool]
    [Description("""
        World map: the game's places (known to the hero and the DM's hidden plans), kingdoms, roads, distances.
        query is one of:
          "place:Name" — what the place is, whose, where, description, surroundings (+ the DM's secret);
          "near:Name" — places within ~6 days' travel: direction and travel time;
          "route:From;To" — the route on foot: how many days, whether by road, through which kingdoms;
          "overview" — the whole world: concept, kingdoms, places.
        One world cell ≈ a quarter day's travel by road. The name can be inexact (part of the name).
        """)]
    public string WorldQuery(string query)
    {
        Log($"world_query → \"{query}\"");
        try
        {
            var (world, _, _) = LoadWorld();
            if (world == null) return "There is no world.";
            string q = query.Trim();
            int colon = q.IndexOf(':');
            string mode = (colon >= 0 ? q[..colon] : q).Trim().ToLowerInvariant();
            string arg = colon >= 0 ? q[(colon + 1)..].Trim() : "";
            switch (mode)
            {
                case "place" or "место":
                    return WorldAtlas.FindPlace(world, arg) is { } p ? WorldAtlas.PlaceInfo(world, p, withDmNotes: true) : $"No place \"{arg}\".";
                case "near" or "рядом":
                    return WorldAtlas.FindPlace(world, arg) is { } n ? WorldAtlas.Near(world, n) : $"No place \"{arg}\".";
                case "route" or "путь":
                    var parts = arg.Split(';', 2);
                    if (parts.Length < 2) return "Format: route:From;To";
                    var a = WorldAtlas.FindPlace(world, parts[0]);
                    var b = WorldAtlas.FindPlace(world, parts[1]);
                    if (a == null || b == null) return $"No place \"{(a == null ? parts[0] : parts[1]).Trim()}\".";
                    return WorldAtlas.Route(world, a, b) is { } r ? $"{a.Name} → {b.Name}: {r.Summary}" : $"\"{b.Name}\" can't be reached on foot from \"{a.Name}\" (water).";
                default:
                    return WorldAtlas.Overview(world, withDmNotes: true);
            }
        }
        catch (Exception ex)
        {
            Log($"world_query error: {ex.Message}");
            return $"Error: {ex.Message}";
        }
    }

    [McpServerTool]
    [Description("""
        Adds a place to the world map: a city/village the hero has learned about, or a place they've been sent to
        (ruins, a lair, a shrine…). The code picks the cell by terrain (a settlement near water and roads, a lair in the
        wilds); a trail to a new village/lair is laid automatically. Returns a patch — put it in your response as is
        (the root "world"; it can be merged with other world fields).
        type: city | town | port | village | castle | ruins | dungeon | cave | shrine.
        near — from which place (default: where the hero is); direction — north/northeast/…; days — roughly
        how many days' travel; terrain — words: forest, thicket, mountains, hills, swamp, desert, snow, by a river, by the sea,
        by a lake, by a road, in the wilds; kingdom — a kingdom name or K-number.
        hidden=true — the DM's plan, the hero doesn't know about the place (not shown on the map; reveal it with a hidden:false patch).
        """)]
    public string AddPlace(
        string name, string type, string description,
        [Description("The DM's secret/plan, hidden from the hero")] string? dmNotes = null,
        string? near = null, string? direction = null, double? days = null, string? terrain = null,
        string? kingdom = null, bool hidden = false)
    {
        Log($"add_place → {name} ({type}) near:{near} dir:{direction} days:{days} terrain:{terrain} kingdom:{kingdom} hidden:{hidden}");
        try
        {
            var (all, geo, link) = LoadWorld();
            if (all == null || geo == null) return "There is no world.";
            if (all.Places.Any(p => p.Name.Equals(name.Trim(), StringComparison.OrdinalIgnoreCase)))
                return $"Place \"{name}\" already exists: {WorldAtlas.PlaceInfo(all, WorldAtlas.FindPlace(all, name)!, true)}";
            type = type.Trim().ToLowerInvariant();
            if (type == WorldPlaceTypes.Capital) return "Capitals are already on the map — choose another type.";
            var nearPlace = near is { Length: > 0 } nn ? WorldAtlas.FindPlace(all, nn)
                : link?.Place is { } here ? WorldAtlas.FindPlace(all, here)
                : link is { X: int hx, Y: int hy } ? new WorldPlace { Name = "the hero", X = hx, Y = hy } : null;
            int k = -1;
            if (kingdom is { Length: > 0 } kg)
                k = kg.Trim().StartsWith('K') && int.TryParse(kg.Trim()[1..], out int ki) ? ki
                    : all.Kingdoms.FindIndex(x => x.Name.Contains(kg.Trim(), StringComparison.OrdinalIgnoreCase));
            var used = geo.Chronicle.Places.Select(p => p.Slot).Where(s => s >= 0).ToHashSet();
            var spot = WorldAtlas.FindSpot(geo, new WorldAtlas.SpotRequest(type, nearPlace, direction, days ?? (nearPlace != null ? 2 : null), terrain, k),
                all.Places.Select(p => (p.X, p.Y)).ToList(), used);
            if (spot is not { } s) return "No suitable spot found — relax the conditions.";

            var patch = new { world = new { places = new[] { new { name = name.Trim(), type, x = s.x, y = s.y, slot = s.slot, hidden, description, dmNotes } } } };
            var probe = new WorldPlace { Name = name, Type = type, X = s.x, Y = s.y, Kingdom = geo.OwnerAt(s.x, s.y) };
            var sb = new StringBuilder();
            sb.AppendLine($"{name}: {L.Strip(WorldPlaceTypes.Label(type))}, {WorldAtlas.KingdomName(all, probe)}, {L.Strip(WorldAtlas.Compass(all, s.x, s.y))}; surroundings: {WorldAtlas.Surroundings(all, s.x, s.y, named: true)}");
            if (nearPlace != null && WorldAtlas.Route(all, nearPlace, probe) is { } r)
                sb.AppendLine($"From \"{nearPlace.Name}\": {L.Strip(WorldAtlas.Direction(s.x - nearPlace.X, s.y - nearPlace.Y))}, {r.Summary}");
            sb.AppendLine("Patch for the response:");
            sb.Append(JsonSerializer.Serialize(patch, _writeOptions));
            return sb.ToString();
        }
        catch (Exception ex)
        {
            Log($"add_place error: {ex.Message}");
            return $"Error: {ex.Message}";
        }
    }

    // The game's world with hidden places; geo — the geometry, link — the game layer (none — the shared world as is).
    private (WorldMap? all, WorldMap? geo, GameWorldLink? link) LoadWorld()
    {
        string worlds = Path.Combine(Path.GetDirectoryName(config.WorldStatePath) ?? ".", "Worlds");
        GameWorldLink? link = null;
        try { link = JsonSerializer.Deserialize<WorldState>(File.ReadAllText(config.WorldStatePath, Encoding.UTF8), _readOptions)?.World; }
        catch { /* no state file — the active world */ }
        string? id = link?.Id;
        if (string.IsNullOrEmpty(id) && File.Exists(Path.Combine(worlds, "active.txt")))
            id = File.ReadAllText(Path.Combine(worlds, "active.txt")).Trim();
        if (string.IsNullOrEmpty(id) || WorldLibrary.LoadFrom(Path.Combine(worlds, id, "world.json")) is not { } geo) return (null, null, link);
        return (link != null ? GameWorld.Compose(geo, link, includeHidden: true) : geo, geo, link);
    }

    private void Log(string message)
    {
        if (config.LogPath is null) return;
        lock (_logLock)
            File.AppendAllText(config.LogPath, $"  MCP {message}\n", new UTF8Encoding(false));
    }
}
