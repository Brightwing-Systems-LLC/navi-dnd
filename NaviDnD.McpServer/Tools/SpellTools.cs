using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using NaviDnD.Helpers;

namespace NaviDnD.McpServer;

// The D&D 5e spell catalog (NaviDnD.Helpers.SpellDatabase, GameData/DnD5e_spells_BD.dtn) — before
// this tool the AI invented the spell's localized name and description itself, so the hero's spell name
// (hero.spells[].name) often didn't match the canonical name in the database, and the spell
// card (MouseUiHelper.SetSelectedSpell) couldn't find the data by name — it showed only the
// level, with no school/casting time/range/text. Search is by ENGLISH name (the AI reliably
// remembers it from training, unlike an exact translation); the result is the name in the game's language,
// to be used as is.
[McpServerToolType]
public sealed class SpellTools(McpServerConfig config)
{
    private static readonly object _logLock = new();

    [McpServerTool]
    [Description("""
        Finds a D&D 5e spell in the catalog by its ENGLISH name — do NOT invent or describe
        the spell yourself, find it here first. query = the English name or part of it
        ("fireball", "cure wounds"). level = the spell level (0 = cantrip) — if given, the
        result contains ONLY spells of that level; if unsure, leave it out.
        Returns comma-separated names of matches in the game's language (lang), most relevant first —
        take the best one and use it LITERALLY as hero.spells[].name, without changing a letter. An empty string means
        nothing was found (at that level, if given): only then may you invent your own
        (homebrew) spell and its description.
        """)]
    public string FindSpell(
        string query,
        [Description("Spell level (0 = cantrip) — a strict filter, optional")] int? level = null)
    {
        Log($"find_spell → \"{query}\" level={level?.ToString() ?? "?"}");
        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return "";

            var normalizedQuery = NormalizeForCompare(query);
            var queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (queryWords.Length == 0)
                return "";

            var scored = new List<(SpellInfo spell, int score)>();
            foreach (var spell in SpellDatabase.AllSpells)
            {
                if (string.IsNullOrEmpty(spell.NameEn)) continue;
                if (level.HasValue && spell.Level != level.Value) continue;

                string normalizedName = NormalizeForCompare(spell.NameEn);
                var nameWords = normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                int score = ScoreMatch(normalizedName, nameWords, queryWords, normalizedQuery);
                if (score <= 0) continue;
                scored.Add((spell, score));
            }

            var names = scored
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.spell.Name, StringComparer.Ordinal)
                .Take(15)
                .Select(x => x.spell.Localized.Name)
                .ToList();

            string result = string.Join(", ", names);
            Log($"find_spell ← {names.Count} results: {result}");
            return result;
        }
        catch (Exception ex)
        {
            Log($"find_spell ← (ERROR) {ex.Message}");
            return "";
        }
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
