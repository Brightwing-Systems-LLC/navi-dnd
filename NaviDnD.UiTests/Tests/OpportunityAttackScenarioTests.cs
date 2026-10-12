using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: opportunity attack (MovementHandler.GetOpportunityAttackTriggers) — during active
// combat, the hero retreating from a cell adjacent to an entity provokes the
// "opportunity_attack_on_hero" trigger, which goes through the same FireTrigger mechanism as ordinary
// map triggers (see TriggersScenarioTests) — previously no UI test covered this.
//
// Fixtures/opportunity_attack_game.json: hero at [3,3], orc "ORC" at [4,3] (adjacent cell), combat
// active, hero's turn. A step west increases the distance from 1 to 2 cells — exactly the moment
// an opportunity attack is provoked.
public class OpportunityAttackScenarioTests
{
    private readonly ITestOutputHelper _output;

    public OpportunityAttackScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void RetreatingFromAdjacentEnemyInCombat_TriggersOpportunityAttack()
    {
        using var game = GameSession.Launch(
            fixtureFileName: "opportunity_attack_game.json", mockedActions: ["SendAction"], mockResponsesFolder: "OpportunityAttack");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered. Retreating west — away from the orc adjacent to the hero...");
        TestPacing.Step();

        GameConsole.SendKey(game.Pid, ConsoleKey.LeftArrow);

        // Retreating from an adjacent cell must call FireTrigger (SendAction mock, artificial
        // 5s delay) — the spinner proves a real AI call, not just a local redraw of the position.
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Waiting", 3000),
            "After retreating from the cell adjacent to the orc no waiting spinner appeared — the opportunity attack did not fire.");
        _output.WriteLine("Waiting spinner appeared — the opportunity attack fired, FireTrigger called. Waiting for the (mock) response...");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "continues", 10000),
            "FireTrigger (mock) response did not arrive after the opportunity attack.");
        _output.WriteLine("Opportunity attack response shown.");
        TestPacing.Step();
    }
}
