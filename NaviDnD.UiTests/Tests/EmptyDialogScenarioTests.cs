using NaviDnD.Data;
using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: with an empty history (DialogDisplay.Draw) the dialog block used to collapse to the title +
// separator with not a single content row — instead of the fixed height of MaxHistoryLines rows
// it has with a non-empty history. Fixtures/effects_game.json sets "history": [] (and a full hero, including
// equipmentSlots — needed to render the Character screen safely).
public class EmptyDialogScenarioTests
{
    private readonly ITestOutputHelper _output;

    public EmptyDialogScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DialogBlock_WithEmptyHistory_StillRendersFixedHeight()
    {
        using var game = GameSession.Launch(fixtureFileName: "effects_game.json");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen rendered.");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "DIALOG", 5000),
            "Title \"DIALOG\" not found on screen.");

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        var failures = new List<string>();

        int titleIdx = rows.FindIndex(r => r.Contains("DIALOG"));
        Assert.True(titleIdx >= 0); // already checked by WaitForAnyRowContains above, but we need the index

        int maxHistoryLines = new DisplayConfig().MaxHistoryLines;

        _output.WriteLine($"Dialog title is row {titleIdx}. Checking {maxHistoryLines} content rows below it despite the empty history...");

        int topSeparatorIdx = titleIdx + 1;
        if (topSeparatorIdx >= rows.Count || !rows[topSeparatorIdx].TrimStart().StartsWith('├'))
            failures.Add($"Row {topSeparatorIdx} (right below the title) is not a \"├...\" separator: \"{rows.ElementAtOrDefault(topSeparatorIdx)?.TrimEnd()}\"");

        for (int i = 0; i < maxHistoryLines; i++)
        {
            int rowIdx = topSeparatorIdx + 1 + i;
            if (rowIdx >= rows.Count || !rows[rowIdx].TrimStart().StartsWith('│'))
            {
                failures.Add($"Row {rowIdx} (dialog content, {i + 1} of {maxHistoryLines}) is not a \"│...\" content row: \"{rows.ElementAtOrDefault(rowIdx)?.TrimEnd()}\". The block collapsed before reaching its fixed height.");
                break;
            }
        }

        int bottomSeparatorIdx = topSeparatorIdx + 1 + maxHistoryLines;
        if (bottomSeparatorIdx >= rows.Count || !rows[bottomSeparatorIdx].TrimStart().StartsWith('├'))
            failures.Add($"Row {bottomSeparatorIdx} (bottom edge of the block after {maxHistoryLines} content rows) is not a \"├...\" separator: \"{rows.ElementAtOrDefault(bottomSeparatorIdx)?.TrimEnd()}\"");

        TestPacing.Step();
        _output.WriteLine(failures.Count == 0 ? "All checks passed." : $"Failed checks: {failures.Count}.");
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }
}
