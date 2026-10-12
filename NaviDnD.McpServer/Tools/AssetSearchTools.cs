using System.ComponentModel;
using ModelContextProtocol.Server;

namespace NaviDnD.McpServer;

[McpServerToolType]
public sealed class AssetSearchTools(McpServerConfig config)
{
    public sealed record SearchHit(string Query, string Matches);
    public sealed record SearchResults(SearchHit[] Monsters, SearchHit[] Icons);

    [McpServerTool]
    [Description("Finds monsters and icons in one batch: when picking creatures and their image independently, prefer one find_assets call over several find_monster/find_icon calls. Pass all known queries at once. Returns query and matches for each query; search rules and the matches format are the same as find_monster/find_icon. Empty matches means no match.")]
    public async Task<SearchResults> FindAssets(
        [Description("English monster names, e.g. [\"skeleton\"].")] string[]? monsterQueries = null,
        [Description("A separate query for each icon; alternatives within a query are comma-separated, e.g. [\"skeleton\",\"book,scroll\",\"tablet\",\"scroll\"].")] string[]? iconQueries = null)
    {
        monsterQueries ??= [];
        iconQueries ??= [];
        if (monsterQueries.Length + iconQueries.Length > 32)
            throw new ArgumentException("At most 32 searches per batch.");
        if (monsterQueries.Concat(iconQueries).Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Search queries must not be empty.");
        var monsters = new MonsterTools(config);
        var icons = new IconTools(config);
        var monsterTasks = monsterQueries.Select(query => Task.Run(() => new SearchHit(query, monsters.FindMonster(query)))).ToArray();
        var iconTasks = iconQueries.Select(query => Task.Run(() => new SearchHit(query, icons.FindIcon(query)))).ToArray();
        await Task.WhenAll(monsterTasks.Concat(iconTasks));
        return new SearchResults(monsterTasks.Select(task => task.Result).ToArray(), iconTasks.Select(task => task.Result).ToArray());
    }
}
