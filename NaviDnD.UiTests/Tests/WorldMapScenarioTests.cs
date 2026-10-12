using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: the world map ([F2] World) and the local area ([F1] Map). Fixtures/world_game.json is a game without a tactical map (the hero is in
// the village of Springs in world world-uitest, Fixtures/world_uitest.json: chronicle — Erdwald, Forestridge, the capital Brightburg):
// "Continue" opens the world ("@ You: Springs"), the Map tab is gray and F1 does not open it, the journal's [F8] "Locations" —
// known places, a click on the capital — a route in the legend, [F10] — travel (the DM's reply is the SendAction mock).
// An encounter on the road is random — the test checks that the hero left Springs, not where exactly they arrived.
public class WorldMapScenarioTests
{
    private readonly ITestOutputHelper _output;

    public WorldMapScenarioTests(ITestOutputHelper output) => _output = output;

    // Fixtures/world_location_game.json is the same game in Springs, but with a tactical map (approach + village for the
    // world cell, the hero at the bottom entrance). F2 — world, F1 — local area; a step down through the exit to the world map takes the hero
    // out into the world, the location is saved for Springs (Worlds/<id>/locations), and the Map tab turns gray.
    [Fact]
    public void Location_F1TogglesWorld_StepOutThroughWorldExit_SavesLocation()
    {
        using var game = GameSession.Launch(fixtureFileName: "world_location_game.json");
        string worldDir = Path.Combine(game.RealStorageDir, "Worlds", "world-uitest");
        Directory.CreateDirectory(worldDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world_uitest.json"), Path.Combine(worldDir, "world.json"), overwrite: true);
        try
        {
            Navigation.MenuSelect(game.Pid, "CONTINUE");
            Assert.True(GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000), "The Map tab did not open.");
            bool local = GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Time:", 5000);
            if (!local) Dump(game.Pid);
            Assert.True(local, "With a tactical map the Map tab should open on the local area (legend \"Time:\").");

            GameConsole.SendKey(game.Pid, ConsoleKey.F2);
            Assert.True(GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "You: Springs", 5000),
                "F2 did not open the world.");
            GameConsole.SendKey(game.Pid, ConsoleKey.F1);
            Assert.True(GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Time:", 5000),
                "F1 did not return to the local area.");
            TestPacing.Step();

            // Step out through the exit to the world map.
            GameConsole.SendKey(game.Pid, ConsoleKey.DownArrow);
            bool left = GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "You: Springs", 6000);
            if (!left) Dump(game.Pid);
            Assert.True(left, "Stepping through the exit did not take the hero to the world map.");
            Assert.True(GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ WORLD ←", 5000), "After leaving — not the World tab.");
            Assert.True(Directory.Exists(Path.Combine(worldDir, "locations")) && Directory.GetFiles(Path.Combine(worldDir, "locations")).Length > 0,
                "The location was not saved for the place on leaving.");
        }
        finally
        {
            game.Dispose();
            try { Directory.Delete(worldDir, recursive: true); } catch { }
        }
    }

    private void Dump(int pid)
    {
        foreach (var r in GameConsole.ReadRows(pid, 0, 60, GameConsole.TitleReadWidth)) _output.WriteLine(r.TrimEnd());
    }

    [Fact]
    public void NoLocation_ShowsWorld_JournalPlaces_TravelByClickAndF10()
    {
        using var game = GameSession.Launch(fixtureFileName: "world_game.json", mockedActions: ["SendAction"], mockResponsesFolder: "WorldTravel");
        // The game's world lives in the world library next to the save; removed after the test.
        string worldDir = Path.Combine(game.RealStorageDir, "Worlds", "world-uitest");
        Directory.CreateDirectory(worldDir);
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "world_uitest.json"), Path.Combine(worldDir, "world.json"), overwrite: true);
        try
        {
            Navigation.MenuSelect(game.Pid, "CONTINUE");
            Assert.True(GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ WORLD ←", 5000),
                "Without a tactical map CONTINUE should open the World tab.");
            bool worldShown = GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "You: Springs", 5000);
            if (!worldShown) Dump(game.Pid);
            Assert.True(worldShown, "Without a tactical map the Map tab should show the world with the hero (\"You: Springs\" in the legend).");
            TestPacing.Step();

            // The Map tab is gray — F1 does not open it.
            GameConsole.SendKey(game.Pid, ConsoleKey.F1);
            Thread.Sleep(600);
            Assert.Contains("→ WORLD ←", GameConsole.ReadRow(game.Pid, 1, GameConsole.TitleReadWidth));

            // Journal → Locations: known places.
            GameConsole.SendKey(game.Pid, ConsoleKey.F3);
            Assert.True(GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ JOURNAL ←", 5000), "The journal did not open on [F3].");
            GameConsole.SendKey(game.Pid, ConsoleKey.F8);
            Assert.True(GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                rows => rows.Any(r => r.Contains("Springs")) && rows.Any(r => r.Contains("Brightburg")), 5000),
                "The journal's \"Locations\" has no known places (Springs, Brightburg).");
            TestPacing.Step();

            // Back to the world.
            GameConsole.SendKey(game.Pid, ConsoleKey.F2);
            Assert.True(GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "You: Springs", 5000),
                "After [F2] from the journal the world map did not open.");

            // Click the capital's icon on the map (the label " Brightburg" is to the right of the icon; the leftmost one is on the map,
            // further right is the list of places in the legend).
            var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
            var label = rows.Select((r, i) => (row: i, col: r.IndexOf("Brightburg", StringComparison.Ordinal)))
                .Where(t => t.col > 0).OrderBy(t => t.col).FirstOrDefault();
            Assert.True(label.col > 0, "The label of the capital Brightburg is not visible on the world map.");
            _output.WriteLine($"Capital label: row {label.row}, column {label.col}");
            GameConsole.SendLeftClick(game.Pid, label.col - 2, label.row);
            Assert.True(GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Path: Brightburg", 5000),
                "After clicking the capital there is no route row in the legend.");
            TestPacing.Step();

            // Set off.
            GameConsole.SendKey(game.Pid, ConsoleKey.F10);
            Assert.True(GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "make camp", 20000),
                "The DM's travel narration did not appear (SendAction mock).");
            Assert.True(GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => r.Any(x => x.Contains("You: ")) && !r.Any(x => x.Contains("You: Springs")), 8000),
                "After travelling the hero stayed in Springs.");
        }
        finally
        {
            game.Dispose();
            try { Directory.Delete(worldDir, recursive: true); } catch { }
        }
    }
}
