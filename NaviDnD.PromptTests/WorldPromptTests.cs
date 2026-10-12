using NaviDnD.Clients;
using NaviDnD.Data.Models;
using NaviDnD.MapGen.Generators;
using Xunit;
using Xunit.Abstractions;

namespace NaviDnD.PromptTests;

// The world map with a real model (expensive — run deliberately, like the rest of this project's tests):
// StartNewGame on a fresh world (world/kingdom concept, starting place via add_place, a location from the world
// cell), travel (TravelService + GameAiClient.Travel), a question about ruins (add_place during play).
// Worlds live in NaviDnD.PromptTests/Storage/Worlds (this project's AppConfig.ProjectRoot); production ones are not touched.
public class WorldPromptTests(ITestOutputHelper output)
{
    // GameAiClient reads prompts from AppConfig.ProjectRoot (for this project, NaviDnD.PromptTests) — for the duration
    // of the test we put a copy of the production prompts there.
    private static string CopyPrompts()
    {
        string src = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "NaviDnD", "Prompts"));
        string dst = Path.Combine(AppConfig.ProjectRoot, "Prompts");
        foreach (var file in Directory.GetFiles(src, "*", SearchOption.AllDirectories))
        {
            string target = Path.Combine(dst, Path.GetRelativePath(src, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
        return dst;
    }

    [Fact]
    public async Task StartTravelAndAddPlace_WorldGrowsWithTheGame()
    {
        string prompts = CopyPrompts();
        try { await Scenario(); }
        finally { try { Directory.Delete(prompts, recursive: true); } catch { } }
    }

    // New game wizard, "World" step → "INVENT": the concept of a new world (name, premise, every kingdom with its capital).
    [Fact]
    public async Task CreateWorld_GivesConceptForEveryKingdom()
    {
        string prompts = CopyPrompts();
        try
        {
            var world = WorldGenerator.Generate(new WorldGenerator.Options(5, WorldSizes.Large));
            var storage = new Storage();
            var config = PromptScenario.TestConfig();
            var client = new GameAiClient(storage.WorldState, storage, new SwitchableAiProvider(config), config);
            bool ok = await client.CreateWorld(world, "a harsh north, vikings and ancient ruins in the south");
            var chron = world.Chronicle;
            output.WriteLine($"{chron.Name}: {chron.Description} | {chron.DmNotes}");
            foreach (var k in chron.Kingdoms)
                output.WriteLine($"  K{k.Id} {k.Name} (capital {chron.Places.FirstOrDefault(p => p.Slot == world.Kingdoms[k.Id].Capital)?.Name}): {k.Description} | {k.DmNotes}");
            Assert.True(ok, "CreateWorld was not applied");
            Assert.False(string.IsNullOrEmpty(chron.Name));
            Assert.Equal(world.Kingdoms.Count, chron.Kingdoms.Count);
            Assert.Equal(world.Kingdoms.Count, chron.Places.Count(p => p.Type == WorldPlaceTypes.Capital));
        }
        finally { try { Directory.Delete(prompts, recursive: true); } catch { } }
    }

    // New game with a hero from the library: same level/stats/spells, full HP, effects cleared, new gear.
    [Fact]
    public async Task ReuseHero_KeepsLevelAndSkills_RefitsGear()
    {
        string prompts = CopyPrompts();
        try
        {
            var source = PromptScenario.LoadUiTestFixture("full_game.json").WorldState.Hero!;
            string heroJson = System.Text.Json.JsonSerializer.Serialize(source, new System.Text.Json.JsonSerializerOptions { PropertyNamingPolicy = System.Text.Json.JsonNamingPolicy.CamelCase });
            var storage = new Storage();
            var config = PromptScenario.TestConfig();
            var client = new GameAiClient(storage.WorldState, storage, new SwitchableAiProvider(config), config);
            await client.ReuseHero(new NewGameData { Name = source.Name, Symbol = source.Symbol ?? "HRO", Description = "", Setting = "DnD 5e", SettingWish = "" }, heroJson);
            var hero = storage.WorldState.Hero!;
            string Stat(Data.Models.Hero h, string n) => h.Stats?.FirstOrDefault(s => s.Name == n)?.Value?.ToString() ?? "";
            output.WriteLine($"before: {Stat(source, "Class")}, HP {source.Hp}, items: {string.Join(", ", source.Inventory?.Select(i => i.Name) ?? [])}");
            output.WriteLine($"after: {Stat(hero, "Class")}, HP {hero.Hp}, items: {string.Join(", ", hero.Inventory?.Where(i => i.Deleted != true).Select(i => i.Name) ?? [])}");
            Assert.Equal(Stat(source, "Class"), Stat(hero, "Class"));
            Assert.Equal(Stat(source, "Strength"), Stat(hero, "Strength"));
            Assert.Equal(source.Spells?.Count ?? 0, hero.Spells?.Count ?? 0);
            Assert.Empty(hero.Effects ?? []);
        }
        finally { try { Directory.Delete(prompts, recursive: true); } catch { } }
    }

    private async Task Scenario()
    {
        var storage = PromptScenario.LoadUiTestFixture("full_game.json");
        storage.NewGameData = new NewGameData
        {
            Name = storage.WorldState.Hero!.Name, Symbol = storage.WorldState.Hero.Symbol ?? "HRO", Description = "",
            Setting = "DnD 5e", SettingWish = "A small adventure: a village by the forest, old ruins nearby, a somewhat grim tone.",
            WorldSize = WorldSizes.Small,
        };
        var config = PromptScenario.TestConfig();
        var client = new GameAiClient(storage.WorldState, storage, new SwitchableAiProvider(config), config);
        var ws = storage.WorldState;

        // 1. Start of the game.
        await client.StartNewGame("");
        var geo = GameWorld.Geo(ws)!;
        output.WriteLine($"World: {geo.Chronicle.Name} — {geo.Chronicle.Description}");
        foreach (var k in geo.Chronicle.Kingdoms) output.WriteLine($"  K{k.Id} {k.Name}: {k.Description}");
        foreach (var p in geo.Chronicle.Places) output.WriteLine($"  place {p.Name} ({p.Type}) [{p.X},{p.Y}] slot {p.Slot}: {p.Description} | {p.DmNotes}");
        output.WriteLine($"Hero: place={ws.World?.Place} tile={GameWorld.HeroTile(ws)} known={string.Join(",", ws.World?.Known ?? [])}");
        output.WriteLine($"Location: {(GameWorld.HasLocation(ws) ? string.Join(",", ws.Map.Chunks?.Select(c => c.Theme) ?? []) : "none")}");
        if (GameWorld.HeroTile(ws) is { } ht) output.WriteLine($"World cell: {WorldBiomes.Get(geo.BiomeAt(ht.x, ht.y)).Name}, site {GameWorld.SiteAt(geo, ht.x, ht.y)}");
        foreach (var m in ws.History ?? []) output.WriteLine($"  > {m.Author}: {m.Text}");
        Assert.False(string.IsNullOrEmpty(geo.Chronicle.Description), "world has no concept");
        Assert.NotEmpty(geo.Chronicle.Kingdoms);
        Assert.NotNull(ws.World?.Place);
        Assert.NotNull(GameWorld.HeroTile(ws));

        // 2. Travel to the capital.
        var world = GameWorld.ForMaster(ws)!;
        var capital = world.Places.First(p => p.Type == WorldPlaceTypes.Capital);
        var plan = TravelService.PlanTo(ws, (capital.X, capital.Y));
        Assert.NotNull(plan);
        int historyBefore = ws.History?.Count ?? 0;
        string report = TravelService.Go(ws, plan!, TravelService.Pace.Normal, new Random(11));
        output.WriteLine(report);
        await client.Travel(report);
        output.WriteLine($"After travel: place={ws.World?.Place} tile={GameWorld.HeroTile(ws)} day={ws.Time.Day} {ws.Time.PartOfDay} loc={GameWorld.HasLocation(ws)}");
        foreach (var m in (ws.History ?? []).Skip(historyBefore)) output.WriteLine($"  > {m.Author}: {m.Text}");
        Assert.True((ws.History?.Count ?? 0) > historyBefore, "the DM did not describe the journey");
        Assert.DoesNotContain(ws.History!.Skip(historyBefore), m => m.Text?.StartsWith("Error") == true);

        // 3. A rumour about a place — the DM adds it to the map.
        int placesBefore = geo.Chronicle.Places.Count;
        historyBefore = ws.History!.Count;
        await client.SendAction("I ask the first person I meet where the nearest village or town is, the one they're heading to themselves — what it's called and how far it is.");
        foreach (var m in ws.History.Skip(historyBefore)) output.WriteLine($"  > {m.Author}: {m.Text}");
        foreach (var p in geo.Chronicle.Places.Skip(placesBefore)) output.WriteLine($"  new place {p.Name} ({p.Type}) [{p.X},{p.Y}] trail={p.Trail?.Count / 2}: {p.Description} | {p.DmNotes}");
        output.WriteLine($"Knows: {string.Join(", ", ws.World!.Known)}");
        Assert.True(geo.Chronicle.Places.Count > placesBefore, "the DM did not add a place to the map (add_place)");
    }
}
