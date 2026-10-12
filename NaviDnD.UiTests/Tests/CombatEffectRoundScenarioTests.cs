using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E regression: during combat the round is advanced by the FINAL ENEMY TURN ENTRY (totalRounds+1, see
// SendAction systemPrompt § COMBAT), not only by SendAction outside combat (see BurningEffectRoundTransitionTests)
// and not only by grid movement. Before the fix MapScreenLoop.RunEnemyTurnAndCheckRoundEnd the engine did not
// check Storage.TakeRoundBundle() after the enemy turn at all — a hero effect (e.g. "Burning")
// would never expire during combat while rounds were advanced solely by the enemy turn.
//
// Fixtures/combat_effect_round_game.json: combat active, goblin's turn (all of the hero's actions/speed already
// spent — the typical reason the turn belongs to the enemy); "Burning" is set with expiresAtRound:1 (not
// onRound) — at totalRounds:1 it has NOT expired yet (currentRound > expiresAtRound, strictly), so the very first
// check after the player's action (round still 1) does not fire — this is essential, otherwise the effect
// would fire BEFORE the enemy turn and the test would prove nothing about the fix itself. Three "SendAction" mock responses:
// 1) a stub for the player's action, 2) the goblin's turn — attacks and passes the turn to the hero with totalRounds+1 (now
// currentRound(2) > expiresAtRound(1) — the effect has expired), 3) the round transition, called ONLY if
// the fix actually worked — removes "Burning".
public class CombatEffectRoundScenarioTests
{
    private readonly ITestOutputHelper _output;

    public CombatEffectRoundScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EnemyTurnEndingRound_TriggersHeroOnRoundEffect_DuringCombat()
    {
        using var game = GameSession.Launch(
            fixtureFileName: "combat_effect_round_game.json", mockedActions: ["SendAction"], mockResponsesFolder: "CombatEffectRound");
        _output.WriteLine($"Game launched (pid {game.Pid}).");
        var failures = new List<string>();

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered. Sending the player's action (1st mock call — a stub, it is the goblin's turn)...");
        TestPacing.Step();

        GameConsole.SendText(game.Pid, "I wait for my turn");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Waiting", 3000),
            "Waiting spinner for the player's action did not appear.");
        _output.WriteLine("Action is being processed. Next the engine itself calls the goblin's turn (2nd mock call)...");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Round 2", 25000),
            "The round did not advance to 2 — the final enemy turn entry (2nd mock call) was not applied.");
        _output.WriteLine("Goblin's turn finished, the round advanced to 2. Waiting for the \"Burning\" tick (3rd mock call)...");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "finally go out", 25000),
            "Text of the 3rd mock response (\"Burning\" expiring during combat) did not appear — MapScreenLoop.RunEnemyTurnAndCheckRoundEnd did not fire.");
        TestPacing.Step();

        _output.WriteLine("Checking the hero's final HP (9 after the goblin's attack, then 8 after the \"Burning\" tick)...");
        if (!GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "❤ 8/10", 5000))
            failures.Add("After the goblin's attack and the \"Burning\" tick the hero's HP did not update to \"❤ 8/10\".");

        _output.WriteLine(failures.Count == 0 ? "All checks passed." : $"Failed checks: {failures.Count}.");
        TestPacing.Step();
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }
}
