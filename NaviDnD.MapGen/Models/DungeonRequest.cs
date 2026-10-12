namespace NaviDnD.MapGen.Models;

public class DungeonRequest
{
    public List<RoomRequest> Rooms { get; set; } = [];
    public List<PassageRequest> Passages { get; set; } = [];
    /// <summary>Exits/entrances to another map — a corridor to the edge of the grid.</summary>
    public List<MapTransition> Transitions { get; set; } = [];
}

public class MapTransition
{
    /// <summary>
    /// A coordinate outside the grid; indicates the direction of the transition.
    /// North: [col, 0], South: [col, 16], West: [0, row], East: [21, row].
    /// </summary>
    public List<int> EdgePoint { get; set; } = [];
    /// <summary>Name of the room to run the corridor to.</summary>
    public string Room { get; set; } = "";
    public int Width { get; set; } = 1;
    public CorridorShape Shape { get; set; } = CorridorShape.Straight;
    public bool HasDoor { get; set; } = false;
}

public class RoomRequest
{
    public string Name { get; set; } = "";
    public RoomShape Shape { get; set; } = RoomShape.Rectangular;
    public RoomSize Size { get; set; } = RoomSize.Medium;
    public string? Zone { get; set; }
}

public class PassageRequest
{
    public string From { get; set; } = "";
    public string To { get; set; } = "";
    /// <summary>Opening = rooms touching directly; Corridor = a corridor Room.</summary>
    public PassageType Type { get; set; } = PassageType.Corridor;
    /// <summary>Corridor width in cells (1–3). Ignored for Opening.</summary>
    public int Width { get; set; } = 1;
    /// <summary>Corridor shape. Ignored for Opening.</summary>
    public CorridorShape Shape { get; set; } = CorridorShape.LShaped;
    /// <summary>Draw a door at the entrance to the target room.</summary>
    public bool HasDoor { get; set; } = false;
}

public enum PassageType
{
    /// <summary>A direct opening between two touching rooms, no corridor.</summary>
    Opening,
    /// <summary>A corridor Room connecting the rooms.</summary>
    Corridor,
}

public enum CorridorShape
{
    Straight,  // fewest turns (H→V or V→H)
    LShaped,   // BFS shortest path (usually one turn)
    Winding,   // a winding random path
}

public enum RoomShape
{
    Rectangular,
    Circular,
    Irregular,
    LShaped,
}

public enum RoomSize
{
    Small,
    Medium,
    Large,
    Huge,
}
