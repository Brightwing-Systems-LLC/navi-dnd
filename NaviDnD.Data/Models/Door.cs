namespace NaviDnD.Data.Models;

// Door represents both physical doors and open passages between rooms.
// Topology (From/To) is permanent — never deleted, only modified.
public class Door
{
    public List<int> From { get; set; }
    public List<int> To { get; set; }

    // true  = physical door (respects IsDoorOpen for movement and LOS)
    // false = open passage (always passable, no door rendered)
    // To break a door: set IsDoor = false, Color = null
    public bool? IsDoor { get; set; }

    // Visual color of the door frame. Null for passages.
    public List<int>? Color { get; set; }

    // true  = door is open (movement and LOS pass through)
    // false = door is closed (blocks movement and LOS)
    // Only meaningful when IsDoor = true.
    public bool? IsDoorOpen { get; set; }

    // true = invisible to hero — treated as solid wall for rendering, movement and LOS.
    // Use to create secret passages (DC > passive perception) or to block a passage permanently.
    public bool? Hidden { get; set; }

    // true = this door connects to the map edge (entry/exit point for the hero).
    // One of From/To will have a coordinate outside the grid bounds.
    public bool? IsEntry { get; set; }

    // true = an exit from the location to the world map (the entrance outside the starting chunk). Drawn as a door
    // in its own color (LocationGrower.WorldExitColor); stepping out through it takes the hero to the world map.
    public bool? IsWorldExit { get; set; }

    // true = a window (IsDoor = true): always see-through, passable only when open (IsDoorOpen).
    public bool? IsWindow { get; set; }

    public string? Image { get; set; }

    public CellTriggers? Triggers { get; set; }
    public List<string>? AiInfo { get; set; }
}
