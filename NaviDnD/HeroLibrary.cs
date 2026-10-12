using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NaviDnD.Data.Models;

namespace NaviDnD;

// The hero library (Storage/Heroes/*.json): the whole hero (HeroJson — stats, skills, spells, abilities,
// race, class, level, equipment) + personality for the character sheet. Updated from saved games (the hero's latest
// state, with progress), and stays even if the game is deleted. A new game with the same hero: level and abilities
// are kept, equipment and condition start fresh (GameAiClient.ReuseHero).
public static class HeroLibrary
{
    public sealed class Card
    {
        public int FormatVersion { get; set; } = StorageFormat.Current;
        public string Name { get; set; } = "";
        public string Symbol { get; set; } = "";
        public string Race { get; set; } = "";
        public string Class { get; set; } = "";      // without the level
        public string Level { get; set; } = "";      // «3» — hero.level
        public string Description { get; set; } = "";
        public List<int>? Color { get; set; }
        public string? Image { get; set; }
        public string? HeroJson { get; set; }
        public DateTime SavedAt { get; set; }
        [System.Text.Json.Serialization.JsonIgnore] public string File { get; set; } = "";
    }

    public static string Dir => Path.Combine(AppConfig.ProjectRoot, "Storage", "Heroes");
    private static readonly JsonSerializerOptions _json = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.Create(System.Text.Unicode.UnicodeRanges.All),
    };

    // All heroes, newest first (pulling heroes in from saved games beforehand).
    public static List<Card> All()
    {
        for (int slot = 1; slot <= Storage.MaxGames; slot++)
            if (Storage.SlotExists(slot)) SyncFromSave(Storage.SlotPath(slot));
        var list = new List<Card>();
        if (!Directory.Exists(Dir)) return list;
        foreach (var f in Directory.GetFiles(Dir, "*.json"))
            try
            {
                if (JsonSerializer.Deserialize<Card>(File.ReadAllText(f, Encoding.UTF8)) is { Name.Length: > 0 } c)
                {
                    c.File = f;
                    list.Add(c);
                }
            }
            catch { /* corrupt file — skip it */ }
        return list.OrderByDescending(c => c.SavedAt).ToList();
    }

    // The hero's description from the character sheet (appearance, personality, backstory) — into their card.
    public static void SetDescription(string name, string description)
    {
        try
        {
            string file = Path.Combine(Dir, Slug(name) + ".json");
            if (!File.Exists(file) || JsonSerializer.Deserialize<Card>(File.ReadAllText(file, Encoding.UTF8)) is not { } card) return;
            card.Description = description;
            File.WriteAllText(file, JsonSerializer.Serialize(card, _json), Encoding.UTF8);
        }
        catch { /* not written — the description stays in this game */ }
    }

    // A saved game's hero — into the library (by name; a newer one overwrites).
    public static void SyncFromSave(string savePath)
    {
        try
        {
            var hero = JsonNode.Parse(File.ReadAllText(savePath, Encoding.UTF8))?["hero"];
            string name = (string?)hero?["name"] ?? "";
            if (hero == null || name.Length == 0) return;
            string file = Path.Combine(Dir, Slug(name) + ".json");
            var savedAt = File.GetLastWriteTime(savePath);
            var old = File.Exists(file) ? JsonSerializer.Deserialize<Card>(File.ReadAllText(file, Encoding.UTF8)) : null;
            if (old != null && old.SavedAt >= savedAt) return;
            string Stat(string key) => hero["stats"]?.AsArray()
                .FirstOrDefault(s => (string?)s?["key"] == key)?["value"]?.ToString() ?? "";
            var (cls, clsLevel) = ClassLevel.Split(Stat(StatKeys.Class));
            var color = hero["color"]?.AsArray().Select(v => (int)v!).ToList();
            var card = new Card
            {
                Name = name, Symbol = (string?)hero["symbol"] ?? "", Race = Stat(StatKeys.Race),
                Class = cls,
                Level = ((int?)hero["level"] ?? clsLevel ?? 1).ToString(),
                // The description (backstory from the character sheet) is not stored in the game save — it stays from the card.
                Description = (string?)hero["description"] ?? old?.Description ?? "", Color = color is { Count: 3 } ? color : null,
                Image = (string?)hero["image"], HeroJson = hero.ToJsonString(), SavedAt = savedAt,
            };
            Directory.CreateDirectory(Dir);
            File.WriteAllText(file, JsonSerializer.Serialize(card, _json), Encoding.UTF8);
        }
        catch { /* could not be read — the hero just will not appear in the list */ }
    }

    public static void Delete(Card card)
    {
        try { if (File.Exists(card.File)) File.Delete(card.File); } catch { }
    }

    private static string Slug(string name)
    {
        var sb = new StringBuilder();
        foreach (char ch in name.ToLowerInvariant()) sb.Append(char.IsLetterOrDigit(ch) ? ch : '-');
        return sb.ToString().Trim('-') is { Length: > 0 } s ? s : "hero";
    }
}
