using NaviDnD.Data.Models;

namespace NaviDnD;

public class WorldState
{
    // File format version (StorageFormat): old saves are rewritten when the game updates.
    public int FormatVersion { get; set; } = StorageFormat.Current;
    // The game's language (L.Russian/L.English) — the world language at creation: narration, history, names; L.World on load.
    public string Language { get; set; } = L.English;
    public MapConfig Map { get; set; } = new MapConfig();
    public List<DialogMessage>? History { get; set; } = [];
    public Hero? Hero { get; set; }
    public Narrative? Narrative { get; set; }
    public GameTime Time { get; set; } = new GameTime();
    public List<ScheduledEvent>? ScheduledEvents { get; set; }
    public CombatState? Combat { get; set; }
    public List<WorldImage>? Images { get; set; }
    public string? ActiveImageKey { get; set; }
    // The game's world: geometry is the world library file (Storage/Worlds/<id>), lore and discovered places are here (GameWorld).
    public GameWorldLink? World { get; set; }

    // A travel summary the DM did not receive (the server did not respond): the journey is already done — it goes to the DM
    // with the player's next action so the road, food and encounter are not lost.
    public string? PendingTravel { get; set; }
}

// A game in the world: lore and places are the world's shared chronicle (WorldMap.Chronicle, carried into other games in this world);
// the game holds what its hero knows (Known — chronicle place names; capitals are known to everyone), where they are (world cell X,Y and
// Place, if they are in one), and where they have been (Visited).
public class GameWorldLink
{
    public string Id { get; set; } = "";
    public string? Place { get; set; }
    public int? X { get; set; }
    public int? Y { get; set; }
    public List<string> Known { get; set; } = [];
    public List<string> Visited { get; set; } = [];
    [System.Text.Json.Serialization.JsonIgnore] public int Version { get; set; }

    public bool Knows(string name) => Known.Any(k => k.Equals(name, StringComparison.OrdinalIgnoreCase));
}

public class WorldImage : IPatchable
{
    public bool? Deleted { get; set; }
    public string Key { get; set; } = "";
    public string Name { get; set; } = "";
    public int Width { get; set; }
    public int Height { get; set; }
    public string Content { get; set; } = "";
}

public class GameTime
{
    // Round since the start of the current part of the day — the AI resets it to 1 on every PartOfDay change
    // (see Prompts/Dnd5e/SendAction systemPrompt.md § Time), so it is not monotonic across the game.
    public int TotalRounds { get; set; } = 1;

    // A PartsOfDay key (night/morning/day/evening; see Prompts/Dnd5e/SendAction systemPrompt.md § Time). Storage maps the DM's word
    // in any language to a key; an unknown one stays as is (treated as day for lighting and music).
    public string PartOfDay { get; set; } = PartsOfDay.Morning;

    // In-game day number — the AI increments it itself when the cycle passes through night (see the prompt).
    public int Day { get; set; } = 1;

    // Part of day for display — in the interface language; an unknown value is shown as is.
    public static string PartOfDayText(string? key) => L.T(PartsOfDay.Russian(key));
}

public class CombatState
{
    public bool Active { get; set; }
    public string CurrentTurn { get; set; } = "";
    public List<InitiativeEntry>? Initiative { get; set; }
}

public class InitiativeEntry : IPatchable
{
    public bool? Deleted { get; set; }
    public string Symbol { get; set; } = "";
    public int Score { get; set; }
    // On the hero's side (a friendly creature joined the fight): makes no opportunity attacks on the hero; enemies are its targets.
    public bool? Ally { get; set; }
}
