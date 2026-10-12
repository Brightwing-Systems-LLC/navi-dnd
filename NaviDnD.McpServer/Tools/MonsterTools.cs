using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using NaviDnD.Helpers;

namespace NaviDnD.McpServer;

// The D&D 5e monster catalog (NaviDnD.Helpers.MonsterDatabase, GameData/DnD5e_monsters_BD.dtn) — modelled
// on SpellTools.FindSpell: the AI searches by ENGLISH name when placing creatures on the map
// (StartNewGame) and takes the returned name literally as map.entities[].monsterKey. Full stats (AC/HP/
// attacks, etc.) the map pulls itself from the same database by monsterKey — this tool doesn't return them, only
// the name, so the whole stat block doesn't travel through the AI and back.
[McpServerToolType]
public sealed class MonsterTools(McpServerConfig config)
{

    [McpServerTool]
    [Description("""
        Finds a D&D 5e monster in the catalog by its ENGLISH name — do NOT invent stats yourself, first
        find the canonical monster here. query = the English name or part of it ("goblin",
        "skeleton warrior").
        Returns comma-separated names of matches in the game's language (lang), most relevant first —
        take the best one and use it LITERALLY as map.entities[].monsterKey (the engine pulls the full stats
        itself by that name; you don't need to specify them). An empty string means the monster isn't in
        the catalog: then invent the creature and its stats yourself and leave monsterKey out.
        """)]
    public string FindMonster(string query)
    {
        Log($"find_monster → \"{query}\"");
        try
        {
            if (string.IsNullOrWhiteSpace(query))
                return "";

            var normalizedQuery = NormalizeForCompare(query);
            var queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (queryWords.Length == 0)
                return "";

            var scored = new List<(MonsterInfo monster, int score)>();
            foreach (var monster in MonsterDatabase.AllMonsters)
            {
                if (string.IsNullOrEmpty(monster.NameEn)) continue;
                string normalizedName = NormalizeForCompare(monster.NameEn);
                var nameWords = normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                int score = ScoreMatch(normalizedName, nameWords, queryWords, normalizedQuery);
                if (score <= 0) continue;
                scored.Add((monster, score));
            }

            var names = scored
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.monster.Name, StringComparer.Ordinal)
                .Take(15)
                .Select(x => x.monster.LocalizedName)
                .ToList();

            string result = string.Join(", ", names);
            Log($"find_monster ← {names.Count} results: {result}");
            return result;
        }
        catch (Exception ex)
        {
            Log($"find_monster ← (ERROR) {ex.Message}");
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
        SearchToolLog.Write(config.LogPath, message);
    }
}
