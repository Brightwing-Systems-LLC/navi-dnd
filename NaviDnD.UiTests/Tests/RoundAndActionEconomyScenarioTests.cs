using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: outside combat, free movement that exceeds the remaining speed automatically
// advances the round and resets the hero's speed/actions (Storage.ApplyClientMovement /
// ResetActionsForNewRound) — not yet covered by any UI test; only that the legend can draw
// the round/actions at all (see CombatStateScenarioTests, where the round is static).
//
// Fixtures/round_advance_game.json: speedMax=speedLeft=10ft (2 cells), all actions already
// spent (value:0). Steps 1 and 2 (5ft each) fit exactly into the remaining speed — the round does not
// change. Step 3 needs another 5ft with 0 left — that is the moment the round advances and
// speed and actions reset to the new maximum.
public class RoundAndActionEconomyScenarioTests
{
    private readonly ITestOutputHelper _output;

    public RoundAndActionEconomyScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ExhaustingSpeedOutsideCombat_AdvancesRoundAndResetsActions()
    {
        using var game = GameSession.Launch(fixtureFileName: "round_advance_game.json");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered. Checking the initial state: Round 1, all actions spent...");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Time:", 5000),
            "The \"Time:\" row never appeared in the legend.");
        var initialRows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        var failures = new List<string>();
        var initialTimeRow = initialRows.FirstOrDefault(r => r.Contains("Time:"));
        if (initialTimeRow == null || !initialTimeRow.Contains("Round 1"))
            failures.Add($"Expected \"Round 1\" initially: \"{initialTimeRow?.TrimEnd()}\"");
        TestPacing.Step();

        _output.WriteLine("Step 1 east — spends 5ft, the remainder should become 5/10...");
        GameConsole.SendKey(game.Pid, ConsoleKey.RightArrow);
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "5/10", 3000),
            "After the first step speed did not show 5/10.");
        _output.WriteLine("After step 1: \"5/10\" shown.");
        TestPacing.Step();

        _output.WriteLine("Step 2 east — spends the remaining 5ft down to exactly zero, the round must not change yet...");
        GameConsole.SendKey(game.Pid, ConsoleKey.RightArrow);
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "0/10", 3000),
            "After the second step speed did not show 0/10.");
        _output.WriteLine("After step 2: \"0/10\" shown.");
        TestPacing.Step();

        var midRows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        var midTimeRow = midRows.FirstOrDefault(r => r.Contains("Time:"));
        if (midTimeRow == null || !midTimeRow.Contains("Round 1"))
            failures.Add($"After two steps (exactly the remaining speed) the round should not have changed: \"{midTimeRow?.TrimEnd()}\"");
        TestPacing.Step();

        _output.WriteLine("Third step — speed is no longer enough (0ft), this should advance the round and reset speed/actions...");
        TestPacing.Step();
        GameConsole.SendKey(game.Pid, ConsoleKey.RightArrow);

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Round 2", 3000),
            "After a step needing more speed than was left, the round did not advance to \"Round 2\".");
        _output.WriteLine("Round advanced. Checking the speed and action reset...");
        TestPacing.Step();

        var finalRows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);

        // New round: full allowance (10ft) minus the cost of the current step (5ft) = 5/10.
        CombatStateScenarioTests.CheckMovementSquares(game.Pid, finalRows, "5/10", (215, 215, 225), (65, 68, 78), failures);

        // Actions should have been reset from "spent" to "available".
        CombatStateScenarioTests.CheckActionSymbol(game.Pid, finalRows, "Action", '●', (70, 200, 70), spent: false, failures);
        CombatStateScenarioTests.CheckActionSymbol(game.Pid, finalRows, "Bonus action", '■', (160, 100, 50), spent: false, failures);
        CombatStateScenarioTests.CheckActionSymbol(game.Pid, finalRows, "Reaction", '▲', (220, 70, 70), spent: false, failures);
        TestPacing.Step();

        _output.WriteLine(failures.Count == 0
            ? "All checks passed."
            : $"Failed checks: {failures.Count}.");
        TestPacing.Step();
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }
}
