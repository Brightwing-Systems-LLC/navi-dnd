namespace NaviDnD.Data.Models;

public class LivingEntity : CellEntity
{
    public string Hp { get; set; } = "";
    public int? SpeedMax { get; set; }
    public int? SpeedLeft { get; set; }
    /// <summary>
    /// Sight range in feet for spotting the hero.
    /// -1 = unlimited, 0 = blind, null = the default 30 ft, >0 = radius in feet (1 cell = 5 ft).
    /// </summary>
    public int? VisionFt { get; set; }

    // Darkvision range. In an unlit cell the creature sees the hero only within this range.
    // null — not set (darkness is ignored, as in old saves), 0 — can't see in darkness.
    public int? DarkvisionFt { get; set; }

    // A reference to a kind in MonsterDatabase (GameData/DnD5e_monsters_BD.dtn), e.g. "Goblin" — NOT a full
    // statblock (like HeroSpell.Name → SpellDatabase). The engine pulls the full stats from the
    // database by this key itself. Only for hostile map creatures — not used for the hero/NPCs.
    public string? MonsterKey { get; set; }
}
