using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: the automatic enemy turn (MapScreenLoop.RunEnemyTurn → GameAiClient.FireEnemyTurn) — after
// ANY free-text player action the engine itself calls the turn of whoever's CombatState.CurrentTurn
// is now active, if it is a creature (not the hero). FireEnemyTurn uses the same system prompt
// (Prompts/Dnd5e/SendAction) as an ordinary SendAction — so both calls within one test
// are intercepted by ONE mock action name, "SendAction". They are told apart by the numbered
// SelectiveMockProvider response files (response.json — 1st call, response2.json — 2nd) — this gives TRULY
// DIFFERENT, not duplicated, content for the "player action" and for the "automatic enemy turn".
//
// response2.json in both folders (EnemyAttack/EnemyMove) is written strictly in the format from
// Prompts/Dnd5e/SendAction/systemPrompt.md § COMBAT "Enemy turn history structure": the patch is NOT a root
// key but nested in history[].patch, movement goes through movePath, and the final enemy turn entry
// must pass the turn back to the hero (combat.currentTurn=hero, totalRounds+1, hero.actions/
// speedLeft reset) — the prompt's self-check, item 10.
//
// Fixtures/enemy_turn_game.json: the hero at [1,3] with ALREADY spent actions/speed (which is
// exactly why it is now the turn of the goblin "GBL" at [5,3]), Combat.Active=true, CurrentTurn="GBL".
public class EnemyTurnBehaviorScenarioTests
{
    private readonly ITestOutputHelper _output;

    public EnemyTurnBehaviorScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EnemyAttackBehavior_AppliesDamageToHero_AndHandsTurnBackToHero()
    {
        using var game = GameSession.Launch(
            fixtureFileName: "enemy_turn_game.json", mockedActions: ["SendAction"], mockResponsesFolder: "EnemyAttack");
        _output.WriteLine($"Game launched (pid {game.Pid}).");
        var failures = new List<string>();

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered. Sending the player's action (1st mock call — a stub without a patch)...");
        TestPacing.Step();

        GameConsole.SendText(game.Pid, "I wait for my turn");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Waiting", 3000),
            "Waiting spinner for the player's action did not appear.");
        _output.WriteLine("The player's action is being processed. After it the engine must automatically call the goblin's turn (2nd mock call)...");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Attack: d20", 20000),
            "The goblin's turn did not arrive (2nd mock call — attack) — the automatic FireEnemyTurn call did not happen.");
        _output.WriteLine("The goblin attacked. Waiting for the turn to pass back to the hero (the final enemy turn entry)...");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Round 2", 5000),
            "After the enemy turn the round did not advance to \"Round 2\" — the final turn entry did not pass the turn back to the hero.");
        TestPacing.Step();

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);

        _output.WriteLine("Checking damage to the hero, the turn returning to the hero and the speed/action reset...");
        if (!rows.Any(r => r.Contains("❤ 6/10")))
            failures.Add("After the enemy turn the hero's HP did not update to \"❤ 6/10\" — FireEnemyTurn did not apply the attack patch.");

        var initiativeRow = rows.FirstOrDefault(r => r.Contains("Initiative"));
        if (initiativeRow == null || !initiativeRow.Contains("⚔️HRO"))
            failures.Add($"After the enemy turn the turn must return to the hero (\"⚔️HRO\"): \"{initiativeRow?.TrimEnd()}\"");

        // Speed is reset TO the full maximum (30/30) — not a single square is "spent", so
        // checking the text is enough here (CheckMovementSquares also checks the color of a SPENT
        // square, and at 30/30 there simply are none — there would be nothing to sample).
        var moveRow = rows.FirstOrDefault(r => r.Contains("Movement:"));
        if (moveRow == null || !moveRow.Contains("30/30"))
            failures.Add($"After the enemy turn speed must be reset to 30/30: \"{moveRow?.TrimEnd()}\"");

        CombatStateScenarioTests.CheckActionSymbol(game.Pid, rows, "Action", '●', (70, 200, 70), spent: false, failures);
        CombatStateScenarioTests.CheckActionSymbol(game.Pid, rows, "Bonus action", '■', (160, 100, 50), spent: false, failures);
        CombatStateScenarioTests.CheckActionSymbol(game.Pid, rows, "Reaction", '▲', (220, 70, 70), spent: false, failures);
        TestPacing.Step();

        _output.WriteLine(failures.Count == 0
            ? "All checks passed."
            : $"Failed checks: {failures.Count}.");
        TestPacing.Step();
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }

    [Fact]
    public void EnemyMoveBehavior_ChangesEnemyPositionOnGrid_AndHandsTurnBackToHero()
    {
        using var game = GameSession.Launch(
            fixtureFileName: "enemy_turn_game.json", mockedActions: ["SendAction"], mockResponsesFolder: "EnemyMove");
        _output.WriteLine($"Game launched (pid {game.Pid}).");
        var failures = new List<string>();

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered. Remembering the goblin's initial column on the grid...");
        // The legend with active combat is taller than usual (+7 rows of CombatReservedLines) — the console window needs
        // a moment to draw the lower part after the initial drawing of the title (see the same
        // class of race in CombatStateScenarioTests). Wait for "Time:" — it is drawn as the block's last row.
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Time:", 5000),
            "The \"Time:\" row never appeared in the legend.");
        TestPacing.Step();

        var initialRows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        int initialCol = FindGoblinGridColumn(initialRows);
        Assert.True(initialCol >= 0, "Goblin symbol \"GBL\" not found on the grid (in the map area, not the legend) in the initial state.");

        _output.WriteLine("Sending the player's action (1st mock call — a stub without a patch)...");
        GameConsole.SendText(game.Pid, "I wait for my turn");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Waiting", 3000),
            "Waiting spinner for the player's action did not appear.");
        _output.WriteLine("The player's action is being processed. After it the engine must automatically move the goblin (2nd mock call)...");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "moves closer", 20000),
            "The goblin's turn did not arrive (2nd mock call — movement) — the automatic FireEnemyTurn call did not happen.");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Round 2", 5000),
            "After the enemy turn the round did not advance to \"Round 2\" — the final turn entry did not pass the turn back to the hero.");
        TestPacing.Step();

        var finalRows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        int finalCol = FindGoblinGridColumn(finalRows);
        Assert.True(finalCol >= 0, "Goblin symbol \"GBL\" not found on the grid (in the map area) after the enemy turn.");

        _output.WriteLine($"Goblin column: was {initialCol}, now {finalCol} (should decrease — the goblin moved closer to the hero).");
        if (finalCol >= initialCol)
            failures.Add($"The goblin did not move on the grid after the automatic enemy turn (\"move\" behavior): column was {initialCol}, now {finalCol}.");

        _output.WriteLine("Checking the turn returning to the hero and the speed/action reset...");
        var initiativeRow = finalRows.FirstOrDefault(r => r.Contains("Initiative"));
        if (initiativeRow == null || !initiativeRow.Contains("⚔️HRO"))
            failures.Add($"After the enemy turn the turn must return to the hero (\"⚔️HRO\"): \"{initiativeRow?.TrimEnd()}\"");

        // Speed is reset TO the full maximum (30/30) — not a single square is "spent", so
        // checking the text is enough here (CheckMovementSquares also checks the color of a SPENT
        // square, and at 30/30 there simply are none — there would be nothing to sample).
        var moveRow = finalRows.FirstOrDefault(r => r.Contains("Movement:"));
        if (moveRow == null || !moveRow.Contains("30/30"))
            failures.Add($"After the enemy turn speed must be reset to 30/30: \"{moveRow?.TrimEnd()}\"");

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

    // The map and the legend are drawn on the SAME row side by side, and the legend ALSO shows the creature's
    // name/symbol as text ("GBL : Goblin - ...") — searching for "GBL" across the whole row may find the legend
    // instead of the grid itself (they are not necessarily on the same physical row, since the legend is not tied
    // to the creature's Y coordinate). Restrict the search to the left part of the row — the map area, up to
    // the inner border with the legend (MapDisplay: mapWidth = cols*(cellWidth+1)+7 = 20*4+7 = 87).
    private const int MapContentWidth = 87;

    private static int FindGoblinGridColumn(List<string> rows)
    {
        foreach (var row in rows)
        {
            if (row.Length <= 1) continue;
            var gridPart = row[..Math.Min(row.Length, MapContentWidth)];
            int col = gridPart.IndexOf("GBL", StringComparison.Ordinal);
            if (col >= 0) return col;
        }
        return -1;
    }
}
