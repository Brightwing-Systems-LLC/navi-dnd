namespace NaviDnD.Data.Models;

// Furniture — a piece of furnishing on one or more cells (MapConfig.Furniture): a bar counter in a row,
// a 1×2 bed, a 2×2 table… Built like a zone (Area: name, positions, aiInfo — the same format for the AI), but kept
// in a separate list: it has its own physics by kind (passability, sight, cover from TerrainCatalog —
// MapConfig.TerrainAt checks furniture first, then the floor), and it doesn't go into the legend or zone triggers.
// Drawn solid, including the grid lines between its own cells. Placed by code (BuildingGenerator); the AI sees
// it (map.furniture) and patches it by id: break it (deleted), overturn/search it (state, name),
// move it (positions), build a new one (kind + positions). The first cell is the "head" (for a bed — the pillow).
public class Furniture : Area
{
    public string Kind { get; set; } = "table";
    public string? State { get; set; }
    public string? Image { get; set; }
}

public sealed record FurnitureKind(string Kind, char Code, string Name, string Image, List<int>? HeadBg = null);

public static class FurnitureCatalog
{
    public static readonly IReadOnlyDictionary<string, FurnitureKind> Kinds = new[]
    {
        new FurnitureKind("counter", 'B', "Bar counter", "delapouite/bar-stool"),
        new FurnitureKind("stall", 'L', "Stall", "delapouite/shop"),
        new FurnitureKind("table", 'Y', "Table", "delapouite/table"),
        new FurnitureKind("bed", 'E', "Bed", "delapouite/bed", HeadBg: [214, 206, 188]),
        new FurnitureKind("cabinet", 'H', "Cabinet", "delapouite/bookshelf"),
        new FurnitureKind("hearth", 'O', "Hearth", "delapouite/fireplace"),
        new FurnitureKind("barrels", 'X', "Barrels", "delapouite/barrel"),
        new FurnitureKind("altar", 'A', "Altar", "delapouite/star-altar"),
        new FurnitureKind("bench", 'N', "Bench", "delapouite/park-bench"),
    }.ToDictionary(k => k.Kind);

    public static FurnitureKind? Get(string? kind) => kind != null ? Kinds.GetValueOrDefault(kind) : null;

    public static FurnitureKind? ByCode(char code) => Kinds.Values.FirstOrDefault(k => k.Code == code);

    // Physics and look of the item's cell.
    public static TerrainKind? Terrain(Furniture f) => Get(f.Kind) is { } k ? TerrainCatalog.Get(k.Code) : null;

    public static string DisplayName(Furniture f) => !string.IsNullOrEmpty(f.Name) ? f.Name : Get(f.Kind)?.Name ?? f.Kind;
}
