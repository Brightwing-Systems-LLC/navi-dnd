using System.Text.Json.Serialization;

namespace NaviDnD.Data.Models;

// A 20×15 location chunk in the MapConfig.MaxSize×MaxSize field grid (MapConfig.Chunks). Code generates the chunk's
// geometry (ChunkGenerator); the AI only fills it with creatures and objects (PopulateChunk).
// The AI sets the location's chunks with a plan at the start of the game (plan_location → LocationGrower.CreatePlanned):
// the geometry of all chunks is built at once, the contents when the hero approaches a chunk.
public class MapChunk
{
    public const int Cols = 20;
    public const int Rows = 15;
    public const int GridCols = MapConfig.MaxSize / Cols; // 5
    public const int GridRows = MapConfig.MaxSize / Rows; // 6

    public int X { get; set; }               // horizontal chunk index, 0..GridCols-1
    public int Y { get; set; }               // vertical chunk index (map rows grow upward), 0..GridRows-1
    public int Seed { get; set; }
    public string Theme { get; set; } = "dungeon";
    public List<ChunkExit> Exits { get; set; } = [];
    public bool Populated { get; set; }      // the AI has already placed creatures/objects
    public string? Purpose { get; set; }     // the chunk's purpose in the location plan (what's here story-wise)

    // Open-ground terrain (OutdoorGenerator): Rows rows of Cols TerrainCatalog codes, row 0 is
    // the bottom one (OriginRow+1). null — a chunk of rooms (dungeon). A space is void.
    public List<string>? Terrain { get; set; }

    // Floor: 0 — ground, 1+ — upper floors, −1… — basements. The floors of one building are stacked in the plan,
    // but on the field they lie in different places of the grid; only the hero's floor is visible on screen.
    public int Z { get; set; }

    // An upper floor of a building: the grid cell of the ground-floor chunk below it — the view out of the windows (yard, street)
    // is drawn from the ground-floor map (MapObjectsProvider.ApplyOutsideView).
    public int? GroundX { get; set; }
    public int? GroundY { get; set; }

    public static bool IsOutdoorTheme(string? theme) => theme is "forest" or "plains" or "hills" or "mountain" or "swamp" or "desert" or "snow" or "taiga";

    // Building: rooms packed together with shared walls, doors between them, furniture (BuildingGenerator).
    public const string Building = "building";

    // Settlement: several houses, streets, a square with a well, vegetable gardens (BuildingGenerator.GenerateVillage).
    public const string Village = "village";

    // Ground under the open sky with an unobstructed edge passage: terrain, a building's yard, a settlement.
    public static bool IsOpenGround(string? theme) => IsOutdoorTheme(theme) || theme is Building or Village;

    // Building type (building): tavern, house, shop, temple, manor, barracks, tower… — sets the layout and furniture.
    public string? Style { get; set; }

    // Natural cave: hall-like rooms without flagstones or doors (dungeon is man-made: crypt, ruins, fortress).
    public const string Cave = "cave";

    // Cell (OriginCol+1, OriginRow+1) is the chunk's bottom-left cell.
    [JsonIgnore] public int OriginCol => X * Cols;
    [JsonIgnore] public int OriginRow => Y * Rows;

    public bool Contains(int col, int row) =>
        col > OriginCol && col <= OriginCol + Cols && row > OriginRow && row <= OriginRow + Rows;
}

public enum ChunkSide { Left, Right, Bottom, Top }

// An exit on a chunk edge: a seam at cell Offset along the side (1..Rows for Left/Right, 1..Cols for Bottom/Top).
// The neighboring chunk gets an entrance on the opposite side at the same Offset — corridors meet across the border.
// External — the entrance from outside (where the hero came from at the start of the game): no chunks beyond it.
public class ChunkExit
{
    public ChunkSide Side { get; set; }
    public int Offset { get; set; }
    public bool External { get; set; }

    public static ChunkSide Opposite(ChunkSide side) => side switch
    {
        ChunkSide.Left => ChunkSide.Right,
        ChunkSide.Right => ChunkSide.Left,
        ChunkSide.Bottom => ChunkSide.Top,
        _ => ChunkSide.Bottom,
    };

    public static (int dx, int dy) Step(ChunkSide side) => side switch
    {
        ChunkSide.Left => (-1, 0),
        ChunkSide.Right => (1, 0),
        ChunkSide.Bottom => (0, -1),
        _ => (0, 1),
    };

    // The last cell of the exit corridor inside the chunk (in field coordinates).
    public (int col, int row) AnchorCell(MapChunk chunk) => Side switch
    {
        ChunkSide.Left => (chunk.OriginCol + 1, chunk.OriginRow + Offset),
        ChunkSide.Right => (chunk.OriginCol + MapChunk.Cols, chunk.OriginRow + Offset),
        ChunkSide.Bottom => (chunk.OriginCol + Offset, chunk.OriginRow + 1),
        _ => (chunk.OriginCol + Offset, chunk.OriginRow + MapChunk.Rows),
    };
}
