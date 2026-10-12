namespace NaviDnD.Data.Models;
public class CellEntity : IPatchable
{
    public bool? Deleted { get; set; }

    public string Symbol { get; set; }

    public List<int> Position { get; set; }

    public string Name { get; set; }

    public List<int> Color { get; set; }

    public LightSource? Light { get; set; }
    // public bool? Explored { get; set; }

    public bool? Hidden { get; set; }

    public CellTriggers? Triggers { get; set; }
    public List<string>? AiInfo { get; set; }
    public string? Image { get; set; }

    public List<StatusEffect>? Effects { get; set; }
}

public class CellTriggers
{
    public TriggerEffect? OnStep { get; set; }
    public TriggerEffect? OnVisible { get; set; }

    // Creatures only. Fires the moment the creature STARTS seeing the hero (entering its field
    // of view), if it isn't in initiative. Persistent: not removed after firing — it fires again
    // at the next encounter.
    public TriggerEffect? Spotted { get; set; }

    // Creatures only. Once per round (via "Round N started"), if the creature sees the hero.
    public TriggerEffect? OnRound { get; set; }
}

[System.Text.Json.Serialization.JsonConverter(typeof(LenientTriggerEffectConverter))]
public class TriggerEffect
{
    public string? Effect { get; set; }
    public bool? Once { get; set; }
}
