using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: the map legend (LegendDisplay.GetLegendLines) must show ONLY what the hero actually
// sees (matching the MapObjectsProvider render filter — Hidden!=true && VisibleCells), and must NOT
// show hidden (hidden:true) objects such as traps the hero has not discovered yet.
//
// Exception: the grid draws only ONE symbol per cell (entities always cover objects —
// MapObjectsProvider.WriteCellEntity checks entities before objects, and the hero covers everything on
// its cell). If an object is in the same cell as the hero or another entity, the legend must still
// list it (visibility itself is checked separately), but with coordinates — otherwise
// the player won't know where exactly the object is, since its symbol is not visible on the grid.
//
// Fixtures/legend_visibility_game.json: the hero at [1,3] stands on a "Scroll" (object covered by the hero);
// the NPC "Guard" at [5,3] stands on a "Ring" (object covered by another entity); a "Torch" at [2,3] is
// an ordinary visible item, not covered by anything (a control check that coordinates are NOT
// appended for it); a "Trap" at [3,3] is hidden:true (the hero has not discovered it, it must not be in the legend).
public class LegendVisibilityScenarioTests
{
    private readonly ITestOutputHelper _output;

    public LegendVisibilityScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Legend_ShowsOnlyVisibleItems_AndAnnotatesCoveredOnesWithCoordinates()
    {
        using var game = GameSession.Launch(fixtureFileName: "legend_visibility_game.json");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered.");
        TestPacing.Step();

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        var failures = new List<string>();

        _output.WriteLine("Checking that the hidden (hidden:true) trap is not shown on screen at all...");
        if (rows.Any(r => r.Contains("Trap")))
            failures.Add("The hidden (hidden:true) \"Trap\" is visible on screen — the hero should not have discovered it.");
        TestPacing.Step();

        _output.WriteLine("Checking that the grid shows the hero and the NPC, not the items they cover...");
        var heroRow = rows.FirstOrDefault(r => r.Contains("HRO"));
        if (heroRow == null) failures.Add("Hero symbol \"HRO\" not found on the grid.");
        else if (heroRow.Contains("[S]")) failures.Add("The covered item's symbol \"[S]\" is visible on the hero's cell — the hero should cover it.");

        var npcRow = rows.FirstOrDefault(r => r.Contains("NPC"));
        if (npcRow == null) failures.Add("NPC symbol \"NPC\" not found on the grid.");
        else if (npcRow.Contains("[R]")) failures.Add("The covered item's symbol \"[R]\" is visible on the NPC's cell — the entity should cover it.");
        TestPacing.Step();

        _output.WriteLine("Checking that covered items are still listed in the legend — with coordinates...");
        CheckCoveredInLegend(rows, "Scroll", "[1,3]", failures);
        CheckCoveredInLegend(rows, "Ring", "[5,3]", failures);
        TestPacing.Step();

        _output.WriteLine("Checking that an ordinary (uncovered) item is in the legend without coordinates...");
        var torchRow = rows.FirstOrDefault(r => r.Contains("Torch"));
        if (torchRow == null) failures.Add("Item \"Torch\" not found in the legend.");
        else if (torchRow.Contains('[') || torchRow.Contains(']'))
            failures.Add($"The ordinary (uncovered) item \"Torch\" unexpectedly has coordinates: \"{torchRow.TrimEnd()}\"");
        TestPacing.Step();

        _output.WriteLine(failures.Count == 0
            ? "All checks passed."
            : $"Failed checks: {failures.Count}.");
        TestPacing.Step();
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }

    private static void CheckCoveredInLegend(List<string> rows, string itemName, string expectedCoords, List<string> failures)
    {
        var row = rows.FirstOrDefault(r => r.Contains(itemName));
        if (row == null) { failures.Add($"Covered item \"{itemName}\" not found in the legend — it must be listed despite being covered on the grid."); return; }
        if (!row.Contains(expectedCoords))
            failures.Add($"Covered item \"{itemName}\" found in the legend, but without the expected coordinates \"{expectedCoords}\": \"{row.TrimEnd()}\"");
    }
}
