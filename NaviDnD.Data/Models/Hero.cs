using System.Text.Json.Serialization;

namespace NaviDnD.Data.Models;

public class Hero : LivingEntity
{
    // Heroic Inspiration — not tied to any feature/class, so it's not a
    // resource but a static field like Hp/VisionFt (shown in the sheet's base block).
    public bool? Inspiration { get; set; }

    // The Stealth check total while the hero is hiding; null — not hiding. The engine only passes it
    // into the spotted trigger context — the AI compares it with the creatures' Perception by the tabletop rules.
    public int? Stealth { get; set; }

    // The round until whose end the hero's movement doesn't provoke opportunity attacks (Disengage). A round
    // number rather than a bool — it expires by itself when the round changes, no separate reset needed.
    public int? NoOpportunityAttacksRound { get; set; }

    // The hero died (the DM decides by the tabletop rules) — game over: input is closed, only exit to the menu.
    public bool? Dead { get; set; }

    // The engine tracks level and XP (LevelRules): the DM awards XP with an xpAward patch, and the level rises only in the
    // "New level" window by the player's choice. level/xp from the DM's response aren't applied — only from the save (Storage).
    // null — level 1 / 0 XP.
    public int? Level { get; set; }

    public int? Xp { get; set; }

    // A level-up in progress (the "New level" window): the DM's options and the hit point roll are in the save, so cancelling,
    // exiting to the menu or restarting doesn't ask the AI again and doesn't allow rerolling hit points. Engine only.
    public LevelUpDraft? LevelUpDraft { get; set; }

    // Hit points at zero ("0/12") — unconscious/dying: the hero doesn't take their own turn, the DM plays it.
    [JsonIgnore]
    public bool IsDown => Hp is { Length: > 0 } hp && int.TryParse(hp.Split('/')[0].Trim(), out int cur) && cur <= 0;

    // Default [] (not null) — protects the UI from crashing (HeroDisplay etc. read these lists without ?.)
    // if the hero patch broke off halfway (e.g. the AI sent a wrong field type further down the
    // JSON) and some of the properties from ApplyHero never got applied at all.
    public List<HeroStat> Stats { get; set; } = [];

    public List<HeroInventory> Inventory { get; set; } = [];

    public List<string> EquipmentSlots { get; set; } = [];

    public List<HeroAbility> Abilities { get; set; } = [];

    public List<HeroResource> Resources { get; set; } = [];

    public List<HeroSkill> Skills { get; set; } = [];

    public List<HeroAction>? Actions { get; set; }

    public List<HeroSpell>? Spells { get; set; }

    // A special stat by key (StatKeys) or null.
    public HeroStat? Stat(string key) => Stats?.FirstOrDefault(s => s.Deleted != true && s.Key == key);
}

public class LevelUpDraft
{
    // For which level (a draft for another level doesn't fit).
    public int Level { get; set; }

    // The DM's response with options (including ones added at the player's request) — JSON for LevelUp.LoadPlan.
    public string? Plan { get; set; }

    // The Hit Die result (rolled once).
    public int? HpDie { get; set; }
}

public class HeroInventory : IPatchable
{
    public bool? Deleted { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public int? Quantity { get; set; }

    public List<string>? EquipmentedSlot { get; set; }

    public List<int> Color { get; set; }

    public string? Image { get; set; }

    // Readable text (note/letter/scroll) — if set, the UI picture panel shows it
    // page by page instead of/alongside Image, not Description (that stays a short line in the list).
    public string? Text { get; set; }

    public List<StatusEffect>? Effects { get; set; }
}

public class HeroStat : IPatchable
{
    public bool? Deleted { get; set; }

    // Key of a special stat (StatKeys: race, class, armorClass…) — code finds the stat by it; Storage sets it from
    // the name in any language. null for other stats.
    public string? Key { get; set; }

    public string Name { get; set; }

    [JsonConverter(typeof(LenientStringConverter))]
    public string Value { get; set; }
}

public class HeroAbility : IPatchable
{
    public bool? Deleted { get; set; }

    public string Name { get; set; }

    public string Description { get; set; }

    public List<int> Color { get; set; }
}

public class HeroResource : IPatchable
{
    public bool? Deleted { get; set; }

    public string Name { get; set; }

    [JsonConverter(typeof(LenientStringConverter))]
    public string Value { get; set; }

    // ResourceCategories.Abilities or .Spells — which character sheet subtab shows this resource
    // (HeroDisplay.DrawHeroCard); Storage maps the DM's word to a key. A resource always belongs to one of the
    // two — general ones (not tied to anything) practically don't exist in 5e.
    public string? Category { get; set; }
}

public class HeroSkill : IPatchable
{
    public bool? Deleted { get; set; }

    public string Name { get; set; }

    [JsonConverter(typeof(LenientStringConverter))]
    public string Value { get; set; }
}

public class HeroAction : IPatchable
{
    public bool? Deleted { get; set; }

    public string Name { get; set; }

    public int Value { get; set; }

    public int MaxValue { get; set; }
}

public class HeroSpell : IPatchable
{
    public bool? Deleted { get; set; }

    public string Name { get; set; }

    // 0 = cantrip, 1-9 — spell level.
    public int Level { get; set; }

    public List<int> Color { get; set; }

    // Whether the spell is prepared (relevant for preparing classes, e.g. wizard/cleric) —
    // shown as a dot in the list. An unprepared spell is still visible in the list, just without the dot.
    public bool? Prepared { get; set; }
}

