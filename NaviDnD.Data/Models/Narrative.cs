namespace NaviDnD.Data.Models;

public class Narrative
{
    /// <summary>Setting: tone, atmosphere, world description</summary>
    public string World { get; set; } = "";

    /// <summary>Current quest / main arc</summary>
    public string CurrentArc { get; set; } = "";

    /// <summary>Adventure parameters chosen by the player (genre, focus, length, difficulty, pace) — the DM follows them all game</summary>
    public string Style { get; set; } = "";

    /// <summary>Active and completed story threads</summary>
    public List<PlotThread> PlotThreads { get; set; } = [];

    /// <summary>Remembered NPCs: attitude and key facts</summary>
    public List<NpcRecord> Npcs { get; set; } = [];

    /// <summary>
    /// Monster kinds the hero has identified (MonsterKey from map.entities) — by kind, not by a specific
    /// entity: study a goblin once and every goblin after that is recognizable. The list is replaced wholesale
    /// by a patch (not PatchByIndex) — id/deleted are never written here.
    /// </summary>
    public List<string>? KnownMonsters { get; set; }
}

public class PlotThread : IPatchable
{
    public bool? Deleted { get; set; }

    public string Name { get; set; }

    /// <summary>"active" | "revealed" | "completed"</summary>
    public string Status { get; set; }

    /// <summary>What the hero knows about the quest (shown in the journal).</summary>
    public string Description { get; set; }

    /// <summary>true — the hero doesn't know about the thread yet (a conspiracy, a future twist): it's not in the journal.</summary>
    public bool? Hidden { get; set; }

    /// <summary>DM notes: the hidden truth, plot plans — for the AI only, never shown to the hero.</summary>
    public string? DmNotes { get; set; }

    /// <summary>
    /// Steps along the quest ("Learned that the headman has the key") — the hero's journal. Like npcs[i].memory: the engine
    /// only appends new lines (Storage.MergeInto).
    /// </summary>
    public List<string>? Steps { get; set; }
}

public class NpcRecord : IPatchable
{
    public bool? Deleted { get; set; }

    public string Name { get; set; }

    /// <summary>Attitude toward the hero: friendly / neutral / hostile / unknown</summary>
    public string Attitude { get; set; }

    /// <summary>What the hero knows about the NPC: who they are, what they look like, what they do (shown in the journal)</summary>
    public string Notes { get; set; }

    /// <summary>true — the hero has met the NPC or knows about them: only these are visible in the journal.</summary>
    public bool? Met { get; set; }

    /// <summary>DM notes: the NPC's true motives, secrets, plans — for the AI only, never shown to the hero.</summary>
    public string? DmNotes { get; set; }

    /// <summary>
    /// Memory of conversations with the hero: each line is a fact with an explicit author ("Nurs said: …").
    /// The engine only appends new lines (Storage.MergeInto); old ones aren't lost when notes are overwritten.
    /// </summary>
    public List<string>? Memory { get; set; }
}
