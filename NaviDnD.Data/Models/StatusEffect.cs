namespace NaviDnD.Data.Models;

public class StatusEffect : IPatchable
{
    public bool? Deleted { get; set; }
    public string Name { get; set; }
    public string? Description { get; set; }
    public List<int>? Color { get; set; }

    // null = not limited by rounds (see UntilLongRest/indefinite)
    public int? ExpiresAtRound { get; set; }

    // true = removed on a long rest (the engine doesn't track this — the AI removes the effect itself
    // when it narrates a long rest)
    public bool? UntilLongRest { get; set; }

    public TriggerEffect? OnRound { get; set; }
    public TriggerEffect? OnExpire { get; set; }
}
