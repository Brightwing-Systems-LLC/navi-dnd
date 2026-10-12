using System.ComponentModel;
using System.Text;
using ModelContextProtocol.Server;
using NaviDnD;

namespace NaviDnD.McpServer;

// The game-icons.net icon library on disk: icons/000000/ffffff/1x1/<author>/<name>.svg —
// see NaviDnD/Helpers/SvgToBrailleConverter.cs, which resolves THE SAME "author/name" slug to a file
// for rendering as braille art. The slug list is cached lazily once for the lifetime of the process.
[McpServerToolType]
public sealed class IconTools(McpServerConfig config)
{
    private static readonly string IconsBasePath =
        Path.Combine(AppConfig.AssetDirectory("icons"), "000000", "ffffff", "1x1");

    private static readonly Lazy<List<string>> _slugs = new(LoadSlugs);


    private static List<string> LoadSlugs()
    {
        if (!Directory.Exists(IconsBasePath)) return [];
        var result = new List<string>();
        foreach (var authorDir in Directory.GetDirectories(IconsBasePath))
        {
            string author = Path.GetFileName(authorDir);
            foreach (var file in Directory.GetFiles(authorDir, "*.svg"))
                result.Add($"{author}/{Path.GetFileNameWithoutExtension(file)}");
        }
        return result;
    }

    [McpServerTool]
    [Description("""
        Finds game-icons.net icon slugs for the "image" field. query = comma-separated alternatives from
        specific to general ("longsword,sword") — tries each in turn until one
        matches; one call, don't repeat. Terms: 1-2 plain English nouns, the item
        type, not a flavourful name ("sword", not "Blade of Kings"). A general icon
        set, not tailored to D&D — it may not find exotic items.
        Returns a comma-separated list of matching slugs (author/name) — pick yourself which
        name fits best. Empty result → no matches; leave out "image", don't guess.
        """)]
    public string FindIcon([Description("comma-separated alternatives from specific to general, e.g. \"longsword,sword\"")] string query)
    {
        Log($"find_icon → \"{query}\"");
        try
        {
            var terms = query.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (terms.Length == 0)
                return Error("empty query");

            foreach (var term in terms)
            {
                var slugs = SearchOne(term);
                if (slugs.Count == 0) continue;

                string hit = string.Join(",", slugs);
                Log($"find_icon ← matchedTerm:\"{term}\" ({slugs.Count} results): {hit}");
                return hit;
            }

            Log($"find_icon ← no term matched in \"{query}\"");
            return "";
        }
        catch (Exception ex)
        {
            Log($"find_icon ← (ERROR) {ex.Message}");
            return Error(ex.Message);
        }
    }

    private static List<string> SearchOne(string term)
    {
        string normalized = NormalizeForCompare(term);
        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length == 0) return [];

        var scored = new List<(string slug, int score)>();
        foreach (var slug in _slugs.Value)
        {
            string name = slug[(slug.IndexOf('/') + 1)..];
            string normalizedName = NormalizeForCompare(name);
            var nameWords = normalizedName.Split(' ', StringSplitOptions.RemoveEmptyEntries);

            int score = ScoreMatch(normalizedName, nameWords, words, normalized);
            if (score > 0) scored.Add((slug, score));
        }

        return scored
            .OrderByDescending(x => x.score)
            .ThenBy(x => x.slug, StringComparer.Ordinal)
            .Take(20)
            .Select(x => x.slug)
            .ToList();
    }

    // "long-sword_01" → "long sword 01" — hyphens/underscores as word separators, so that
    // "long sword" and "longsword" match word by word, not only as an exact substring.
    private static string NormalizeForCompare(string s) =>
        s.ToLowerInvariant().Replace('-', ' ').Replace('_', ' ');

    private static int ScoreMatch(string normalizedName, string[] nameWords, string[] queryWords, string queryNormalized)
    {
        if (normalizedName == queryNormalized) return 100;
        if (queryWords.All(nameWords.Contains)) return 80;
        if (normalizedName.StartsWith(queryNormalized, StringComparison.Ordinal)) return 60;
        if (queryWords.All(t => normalizedName.Contains(t, StringComparison.Ordinal))) return 40;
        return 0;
    }

    private static string Error(string message) => $"{{\"error\":\"{message}\"}}";

    private void Log(string message)
    {
        SearchToolLog.Write(config.LogPath, message);
    }
}
