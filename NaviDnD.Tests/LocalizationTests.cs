using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using NaviDnD.Data.Models;

namespace NaviDnD.Tests;

// Translation (L): every string from L.T/L.F/L.Plural and L.W/L.WF/L.WPlural in the sources has a Russian version
// in NaviDnD.Data/Localization/ru.json with the same placeholders {0}, {1}…
public class LocalizationTests
{
    private static readonly string[] SourceDirs = ["NaviDnD", "NaviDnD.Data", "NaviDnD.MapGen", "NaviDnD.Installer", "NaviDnD.McpServer"];
    private const string Literal = "\"((?:[^\"\\\\]|\\\\.)*)\"";

    [Fact]
    public void EveryWrappedStringHasRussianTranslation()
    {
        var missing = SourceKeys().Where(k => !L.RussianTable.ContainsKey(k.Key))
            .Select(k => $"{k.File}: {k.Key}").Distinct().ToList();
        Assert.True(missing.Count == 0, "No translation in ru.json:\n" + string.Join("\n", missing));
    }

    // The installer has its own dictionary (NaviDnD.Installer/ru.json) — the same translations, only its strings.
    [Fact]
    public void InstallerDictionaryMatchesGame()
    {
        string path = Path.Combine(RepoRoot(), "NaviDnD.Installer", "ru.json");
        var installer = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path))!;
        var wrong = SourceKeys().Where(k => k.Dir == "NaviDnD.Installer")
            .Where(k => !installer.TryGetValue(k.Key, out var v) || !L.RussianTable.TryGetValue(k.Key, out var game) || v != game)
            .Select(k => $"{k.File}: {k.Key}").Distinct().ToList();
        Assert.True(wrong.Count == 0, "Missing from NaviDnD.Installer/ru.json or differs from the game:\n" + string.Join("\n", wrong));
    }

    [Fact]
    public void TranslationsKeepPlaceholders()
    {
        var broken = L.RussianTable.Where(p => !Placeholders(p.Key).SetEquals(Placeholders(p.Value)))
            .Select(p => $"{p.Key} → {p.Value}").ToList();
        Assert.True(broken.Count == 0, "Placeholders do not match:\n" + string.Join("\n", broken));
    }

    [Fact]
    public void KeysAreStaticLiterals()
    {
        // L.T($"…") and L.T(@"…") cannot be found in ru.json: the key is a plain string, values go through L.F.
        var dynamic = SourceFiles().SelectMany(f => Regex.Matches(File.ReadAllText(f.File), @"L\.(T|F|W|WF)\(\s*[$@]")
            .Select(m => $"{Path.GetFileName(f.File)}: {m.Value}")).ToList();
        Assert.True(dynamic.Count == 0, string.Join("\n", dynamic));
    }

    // Catalog names are translated when shown (L.T(name)) — each must have a translation.
    [Fact]
    public void CatalogNamesHaveRussianTranslation()
    {
        string[] places = ["capital", "city", "town", "village", "castle", "port", "ruins", "dungeon", "cave", "shrine"];
        var names = WorldBiomes.All.Values.Select(b => b.Name)
            .Concat(places.Select(WorldPlaceTypes.Label))
            .Concat(TerrainCatalog.Kinds.Values.Select(k => k.Name))
            .Concat(FurnitureCatalog.Kinds.Values.Select(k => k.Name))
            .Concat(["north", "south", "west", "east", "northwest", "northeast", "southwest", "southeast", "center of the world"])
            .Concat(["Night", "Morning", "Day", "Evening"]);
        var missing = names.Where(n => !L.RussianTable.ContainsKey(n)).Distinct().ToList();
        Assert.True(missing.Count == 0, "No translation in ru.json:\n" + string.Join("\n", missing));
    }

    // Hero questionnaire and "Adventure" step options are English keys (Prompts/Dnd5e/*.json); a Russian game shows the translation.
    [Fact]
    public void HeroAndAdventureOptionsHaveRussianTranslation()
    {
        string dir = Path.Combine(RepoRoot(), "NaviDnD", "Prompts", "Dnd5e");
        var hero = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "heroOptions.json")))!;
        var values = new[] { "races", "classes", "alignments", "backgrounds" }
            .SelectMany(k => hero[k]!.AsArray().Select(o => o is System.Text.Json.Nodes.JsonValue ? o.ToString() : (string)o!["name"]!));
        var adventure = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(dir, "adventureOptions.json")))!;
        values = values.Concat(adventure["params"]!.AsArray()
            .SelectMany(p => p!["options"]!.AsArray().Select(o => o!.ToString()).Append((string)p["label"]!)));
        var missing = values.Where(v => !L.RussianTable.ContainsKey(v)).Distinct().ToList();
        Assert.True(missing.Count == 0, "No translation in ru.json:\n" + string.Join("\n", missing));
        Assert.NotEmpty(hero["namesEn"]!.AsArray());
    }

    [Fact]
    public void RussianSwitchesAndFallsBack()
    {
        try
        {
            L.SetLanguage(L.Russian);
            Assert.Equal("НАСТРОЙКИ", L.T("SETTINGS"));
            Assert.Equal("no such key", L.T("no such key"));
            Assert.Equal("на север", L.T("north##to"));
            Assert.Equal("день", L.Plural(21, "day", "days"));
            Assert.Equal("дня", L.Plural(3, "day", "days"));
            Assert.Equal("дней", L.Plural(12, "day", "days"));
            L.SetLanguage(L.English);
            Assert.Equal("SETTINGS", L.T("SETTINGS"));
            Assert.Equal("north", L.T("north##to"));
            Assert.Equal("day", L.Plural(1, "day", "days"));
            Assert.Equal("days", L.Plural(21, "day", "days"));
        }
        finally { L.SetLanguage(L.English); }
    }

    private static HashSet<string> Placeholders(string text) =>
        Regex.Matches(text, @"\{(\d+)[^}]*\}").Select(m => m.Groups[1].Value).ToHashSet();

    private static IEnumerable<(string Dir, string File, string Key)> SourceKeys()
    {
        foreach (var (dir, file) in SourceFiles())
        {
            string code = File.ReadAllText(file);
            string name = Path.GetFileName(file);
            foreach (Match m in Regex.Matches(code, @"L\.(?:T|F|W|WF)\(\s*" + Literal))
                yield return (dir, name, Unescape(m.Groups[1].Value));
            foreach (Match m in Regex.Matches(code, @"L\.W?Plural\([^,]+,\s*" + Literal + @"\s*,\s*" + Literal))
                yield return (dir, name, Unescape(m.Groups[1].Value) + "|" + Unescape(m.Groups[2].Value));
        }
    }

    private static string RepoRoot([CallerFilePath] string self = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(self)!, ".."));

    private static IEnumerable<(string Dir, string File)> SourceFiles()
    {
        string sep = Path.DirectorySeparatorChar.ToString();
        return SourceDirs.SelectMany(d => Directory.EnumerateFiles(Path.Combine(RepoRoot(), d), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(sep + "bin" + sep) && !f.Contains(sep + "obj" + sep)).Select(f => (d, f)));
    }

    private static string Unescape(string s) => Regex.Replace(s, @"\\(.)", m => m.Groups[1].Value switch
    {
        "n" => "\n", "t" => "\t", "r" => "\r", var c => c,
    });
}
