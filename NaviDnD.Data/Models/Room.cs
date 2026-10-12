namespace NaviDnD.Data.Models;

public class Room : Area
{
    // A passage (corridor, crawlway, gallery) — placed by the generator; the name is in the world language and may change.
    // Passages get no legend label, no partition doors are placed in them, and they aren't picked as the start.
    public bool? Passage { get; set; }
}
