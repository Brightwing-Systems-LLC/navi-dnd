using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using NaviDnD.Helpers;

namespace NaviDnD.McpServer;

// The D&D 5e equipment catalog (NaviDnD.Helpers.EquipmentDatabase, GameData/DnD5e_equipment_BD.dtn +
// DnD5e_magic_equipment_BD.dtn) — modelled on SpellTools.FindSpell: the AI searches by ENGLISH
// name when generating inventory/loot, so similar items don't get mixed up (e.g. Dart 1d4 vs
// Javelin 1d6 — both "thrown", easy to confuse from memory). Unlike spells, there is no
// separate reference card in the game — the AI writes hero.inventory[].description itself once,
// so the tool returns not only the canonical localized name but the mechanics too (damage/AC/properties/
// weight/cost); turning them into a short description line is up to the AI, as usual.
[McpServerToolType]
public sealed class EquipmentTools(McpServerConfig config)
{
    private static readonly object _logLock = new();

    [McpServerTool]
    [Description("""
        Finds D&D 5e equipment (mundane AND magic) in the catalog by its ENGLISH name — do NOT
        invent the mechanics of a similar item from memory, find the exact one here first. query =
        the English name or part of it ("javelin", "flame tongue").
        Returns one line per match (most relevant first): the name in the game's language (lang) —
        take it LITERALLY as inventory[].name — and the mechanics (damage/type, properties, AC, weight, cost, for
        magic items — rarity/attunement/effect). Rewrite the mechanics into a short description line
        in the game's language yourself, like the rest of the text. An empty result means the item isn't in the catalog:
        then invent it yourself; don't search again.
        """)]
    public string FindItem(string query)
    {
        Log($"find_item → \"{query}\"");
        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return "";

            var normalizedQuery = NormalizeForCompare(query);
            var queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (queryWords.Length == 0)
                return "";

            var scored = new List<(EquipmentInfo item, int score)>();
            foreach (var item in EquipmentDatabase.AllItems)
            {
                if (string.IsNullOrEmpty(item.NameEn)) continue;
                string normalizedName = NormalizeForCompare(item.NameEn);
                var nameWords = normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                int score = ScoreMatch(normalizedName, nameWords, queryWords, normalizedQuery);
                if (score <= 0) continue;
                scored.Add((item, score));
            }

            var lines = scored
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.item.Name, StringComparer.Ordinal)
                .Take(8)
                .Select(x => Summarize(x.item))
                .ToList();

            string result = string.Join("\n", lines);
            Log($"find_item ← {lines.Count} results");
            return result;
        }
        catch (Exception ex)
        {
            Log($"find_item ← (ERROR) {ex.Message}");
            return "";
        }
    }

    private static string Summarize(EquipmentInfo i)
    {
        var parts = new List<string>();
        if (i.DamageVal.Length > 0) parts.Add($"{i.DamageVal} {i.DamageType}".Trim());
        if (i.Props.Count > 0) parts.Add(string.Join(", ", i.Props));
        if (i.Ac.Length > 0) parts.Add($"AC {i.Ac}");
        if (i.Weight.Length > 0) parts.Add($"weight {i.Weight}");
        if (i.Cost.Length > 0) parts.Add($"cost {i.Cost}");
        if (i.Rarity.Length > 0) parts.Add($"rarity: {i.Rarity}");
        if (i.Category == "magic")
            parts.Add(i.Attunement.Length > 0 ? $"attunement: {i.Attunement}" : "attunement: not required");
        if (i.Text.Length > 0) parts.Add(i.Text);

        string type = i.TypeAdditions.Length > 0 ? $"{i.Type} {i.TypeAdditions}" : i.Type;
        return $"{i.LocalizedName}{(L.WorldIsEnglish ? "" : $" ({i.NameEn})")} [{i.Category}, {type}]: {string.Join("; ", parts)}";
    }

    private static string NormalizeForCompare(string s) =>
        s.ToLowerInvariant().Replace('-', ' ').Replace('\'', ' ').Replace(',', ' ');

    private static int ScoreMatch(string normalizedName, string[] nameWords, string[] queryWords, string queryNormalized)
    {
        if (normalizedName == queryNormalized) return 100;
        if (queryWords.All(nameWords.Contains)) return 80;
        if (normalizedName.StartsWith(queryNormalized, StringComparison.Ordinal)) return 60;
        if (queryWords.All(t => normalizedName.Contains(t, StringComparison.Ordinal))) return 40;
        return 0;
    }

    private void Log(string message)
    {
        if (config.LogPath is null) return;
        lock (_logLock)
            File.AppendAllText(config.LogPath, $"  MCP {message}\n", new UTF8Encoding(false));
    }
}
