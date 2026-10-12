using System.Text.Json;
using NaviDnD.Data.Models;

namespace NaviDnD.Helpers;

// The rules level table (GameData/DnD5e_levels.json): XP thresholds, proficiency bonus, class hit dice, Ability Score
// Improvement levels, XP per monster by Challenge Rating. Read once per process (Lazy). This is the
// deterministic part of levelling up — no AI; what a class gains at a level is decided by the DM (LevelUp).
public sealed class LevelRules
{
    public sealed class ClassRule
    {
        public string[] Names { get; set; } = [];
        public int HitDie { get; set; } = 8;
        public int[]? AsiLevels { get; set; }
    }

    public int[] Xp { get; set; } = [0];
    public int[] Proficiency { get; set; } = [2];
    public int[] AsiLevels { get; set; } = [];
    public int MaxScore { get; set; } = 20;
    public Dictionary<string, int> CrXp { get; set; } = [];
    public List<ClassRule> Classes { get; set; } = [];

    private static readonly string FilePath = Path.Combine(AppConfig.AssetDirectory("GameData"), "DnD5e_levels.json");

    private static readonly Lazy<LevelRules> Loaded = new(() =>
    {
        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            return JsonSerializer.Deserialize<LevelRules>(File.ReadAllText(FilePath), options) ?? new LevelRules();
        }
        catch { return new LevelRules(); }   // no file — no levels (MaxLevel 1), the game runs as before
    });

    public static LevelRules Current => Loaded.Value;

    public int MaxLevel => Xp.Length;

    public static int LevelOf(Hero hero) => Math.Max(1, hero.Level ?? 1);

    // XP for a level (threshold); beyond the table, the last one.
    public int XpFor(int level) => Xp[Math.Clamp(level, 1, MaxLevel) - 1];

    // Threshold of the next level; null means the level is the maximum.
    public int? NextXp(int level) => level < MaxLevel ? Xp[level] : null;

    public int ProficiencyAt(int level) => Proficiency.Length == 0 ? 2 : Proficiency[Math.Clamp(level, 1, Proficiency.Length) - 1];

    // Enough XP for the next level (outside combat — the screen decides).
    public bool CanLevelUp(Hero hero) => NextXp(LevelOf(hero)) is int next && (hero.Xp ?? 0) >= next;

    // The hero's class from the "Class" stat (any language; a subclass in parentheses is fine); unknown means null.
    public ClassRule? ClassOf(Hero hero)
    {
        string cls = ClassLevel.Split(hero.Stat(StatKeys.Class)?.Value).cls.ToLowerInvariant();
        return Classes.FirstOrDefault(c => c.Names.Any(n => cls.StartsWith(n.ToLowerInvariant())));
    }

    // The class's Hit Die; an unknown class gets d8 (the middle one).
    public int HitDie(Hero hero) => ClassOf(hero)?.HitDie ?? 8;

    public bool IsAsiLevel(Hero hero, int level) => (ClassOf(hero)?.AsiLevels ?? AsiLevels).Contains(level);

    // XP for a reference monster (monsterKey) by its Challenge Rating; not found means 0.
    public int MonsterXp(string? monsterKey) =>
        MonsterDatabase.Find(monsterKey) is { } m && CrXp.TryGetValue(m.Cr.Trim(), out int xp) ? xp : 0;
}
