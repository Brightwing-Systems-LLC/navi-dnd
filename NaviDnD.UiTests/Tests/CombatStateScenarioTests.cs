using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: rendering of the combat state in the legend (LegendDisplay.BuildCombatLines) — turn order,
// available/spent actions, available/spent speed, round/day. Pure rendering, no
// AI calls (Fixtures/combat_state_game.json sets Combat.Active/Initiative/Actions
// statically) — the engine already shows all of this on screen, but previously no
// UI test checked it.
public class CombatStateScenarioTests
{
    internal static readonly (byte r, byte g, byte b) MainBackground = (18, 18, 22);
    internal const int ColorTolerance = 30;

    private readonly ITestOutputHelper _output;

    public CombatStateScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void CombatState_ShowsTurnOrderSpeedAndActionsCorrectly()
    {
        using var game = GameSession.Launch(fixtureFileName: "combat_state_game.json");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered.");
        TestPacing.Step();

        // The legend with active combat is taller than usual (+7 rows of CombatReservedLines) — the console window
        // needs a moment to work out the size and draw the lower part after the initial drawing
        // of the title. Wait for the "Time:" row — it is drawn LAST in BuildCombatLines, so its
        // appearance guarantees that all rows ABOVE it (turn order/actions/speed) are already drawn;
        // waiting for the first row ("Initiative") is not enough — it may appear before
        // the other rows of the same block have finished drawing.
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Time:", 5000),
            "The \"Time:\" row never appeared in the legend.");

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        var failures = new List<string>();

        _output.WriteLine("Checking turn order — the hero's current turn is marked \"⚔️HRO\", the goblin is shown without a mark...");
        var initiativeRow = rows.FirstOrDefault(r => r.Contains("Initiative"));
        if (initiativeRow == null) failures.Add("The \"Initiative\" row not found in the legend.");
        else
        {
            if (!initiativeRow.Contains("⚔️HRO")) failures.Add($"The hero's current turn is not marked \"⚔️HRO\": \"{initiativeRow.TrimEnd()}\"");
            if (!initiativeRow.Contains("GB1")) failures.Add($"Goblin \"GB1\" not found in the turn order: \"{initiativeRow.TrimEnd()}\"");
        }
        TestPacing.Step();

        _output.WriteLine("Checking the speed display (15/30, half the squares spent)...");
        CheckMovementSquares(game.Pid, rows, "15/30", (215, 215, 225), (65, 68, 78), failures);
        TestPacing.Step();

        _output.WriteLine("Checking actions: \"Action\" spent (gray ●), \"Bonus action\"/\"Reaction\" available (colored ■/▲)...");
        CheckActionSymbol(game.Pid, rows, "Action", '●', (65, 68, 78), spent: true, failures);
        CheckActionSymbol(game.Pid, rows, "Bonus action", '■', (160, 100, 50), spent: false, failures);
        CheckActionSymbol(game.Pid, rows, "Reaction", '▲', (220, 70, 70), spent: false, failures);
        TestPacing.Step();

        _output.WriteLine("Checking the round/day row...");
        var timeRow = rows.FirstOrDefault(r => r.Contains("Time:"));
        if (timeRow == null) failures.Add("The \"Time:\" row not found in the legend.");
        else if (!timeRow.Contains("Round 3") || !timeRow.Contains("Day 1") || !timeRow.Contains("Morning"))
            failures.Add($"The time row does not contain the expected \"Morning · Day 1 · Round 3\": \"{timeRow.TrimEnd()}\"");
        TestPacing.Step();

        _output.WriteLine(failures.Count == 0
            ? "All checks passed."
            : $"Failed checks: {failures.Count}.");
        TestPacing.Step();
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }

    // Fixture: speedLeft=15, speedMax=30 → 6 squares in total (max/5), 3 white (available) + 3 gray (spent).
    internal static void CheckMovementSquares(
        int gamePid, List<string> rows, string expectedFraction, (int r, int g, int b) availableColor, (int r, int g, int b) spentColor, List<string> failures)
    {
        var row = rows.FirstOrDefault(r => r.Contains("Movement:"));
        if (row == null) { failures.Add("The \"Movement:\" row not found in the legend."); return; }
        if (!row.Contains(expectedFraction))
            failures.Add($"The movement row does not show \"{expectedFraction}\": \"{row.TrimEnd()}\"");

        int rowIndex = rows.IndexOf(row);
        int firstSquare = row.IndexOf('■');
        int lastSquare = row.LastIndexOf('■');
        if (firstSquare < 0) { failures.Add("Speed squares \"■\" not found in the movement row."); return; }

        // The first square is available speed, the last is spent (if the whole remainder is available,
        // it is the same cell — then both checks simply coincide, which is expected).
        ScreenAssertions.CheckCellColor(gamePid, firstSquare, rowIndex, availableColor, ColorTolerance, MainBackground, "speed square (available)", failures);
        ScreenAssertions.CheckCellColor(gamePid, lastSquare, rowIndex, spentColor, ColorTolerance, MainBackground, "speed square (spent)", failures);
    }

    internal static void CheckActionSymbol(
        int gamePid, List<string> rows, string label, char symbol, (int r, int g, int b) expectedColor, bool spent, List<string> failures)
    {
        var row = rows.FirstOrDefault(r => r.Contains(label + ":"));
        if (row == null) { failures.Add($"Action row \"{label}\" not found in the legend."); return; }
        int rowIndex = rows.IndexOf(row);

        // Look for the symbol AFTER the label (the action symbol itself must be to the right of the label's colon).
        int labelEnd = row.IndexOf(label + ":") + label.Length + 1;
        int symbolCol = row.IndexOf(symbol, labelEnd);
        if (symbolCol < 0) { failures.Add($"Symbol \"{symbol}\" ({(spent ? "spent" : "available")}) not found in the \"{label}\" row: \"{row.TrimEnd()}\""); return; }

        ScreenAssertions.CheckCellColor(gamePid, symbolCol, rowIndex, expectedColor, ColorTolerance, MainBackground,
            $"action \"{label}\" ({(spent ? "spent" : "available")})", failures);
    }
}
