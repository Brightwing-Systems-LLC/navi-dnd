using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: selecting an inventory item by mouse hover (MouseUiHelper.MakeCharacterPollAction — "Inventory
// item hover" — and HeroDisplay.WriteInvLine, which draws "←" next to the selected item's first
// row) — a recently added feature (see commit history), covered only by click pagination
// (GameScenarioTests.CheckInventoryPagination), but neither by the hover itself nor by the selection
// reset when the mouse leaves.
public class InventoryHoverScenarioTests
{
    private readonly ITestOutputHelper _output;

    public InventoryHoverScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void HoverOverItem_HighlightsRow_AndResetsWhenMouseLeaves()
    {
        using var game = GameSession.Launch();
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        // The screen title and the card body (inventory) are drawn by DIFFERENT calls — the title may
        // change before the inventory list below has been drawn (the same race that was fixed in
        // GameScenarioTests.FullGameplayScenario). Explicitly wait for the item to appear rather than relying on
        // it having been drawn by the time Navigation.ToCharacterScreen returns.
        const string itemMarker = "Longsword";
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, itemMarker, 5000),
            $"Item \"{itemMarker}\" did not appear on the Character screen (the card did not finish drawing?).");
        _output.WriteLine("Character screen (Inventory tab) rendered.");
        TestPacing.Step();

        // "Longsword" is the first unequipped item on the first inventory page (see also
        // GameScenarioTests.CheckInventoryPagination) — guaranteed visible without pagination.
        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        int itemRow = rows.FindIndex(r => r.Contains(itemMarker));
        Assert.True(itemRow >= 0, $"Item \"{itemMarker}\" not found on the first inventory page.");
        Assert.DoesNotContain("←", rows[itemRow]);
        _output.WriteLine($"Item \"{itemMarker}\" found on row {itemRow}; without hover there is no \"←\" marker yet.");

        int hoverCol = rows[itemRow].IndexOf(itemMarker, StringComparison.Ordinal) + 2;
        _output.WriteLine("Hovering the mouse over the item row...");
        GameConsole.SendMouseMove(game.Pid, hoverCol, itemRow);
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => r[itemRow].Contains(itemMarker) && r[itemRow].Contains("←"), 3000),
            $"After hovering over \"{itemMarker}\" the row was not highlighted with the \"←\" marker.");
        _output.WriteLine("Row highlighted with the \"←\" marker on hover.");
        TestPacing.Step();

        _output.WriteLine("Moving the mouse outside the inventory list — the selection should reset...");
        GameConsole.SendMouseMove(game.Pid, 2, 2);

        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => !r[itemRow].Contains("←"), 3000),
            $"After the mouse left \"{itemMarker}\" the \"←\" marker did not disappear — hover selection does not reset.");
        _output.WriteLine("Hover selection reset after the mouse left.");
        TestPacing.Step();
    }
}
