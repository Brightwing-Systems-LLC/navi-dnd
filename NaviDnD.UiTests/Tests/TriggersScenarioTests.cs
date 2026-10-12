using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: hero movement on the map activates triggers (MovementHandler.TryMove → GameAiClient.FireTrigger),
// described in CLAUDE.md as a finished part of "Exploration", but previously not checked through the real UI —
// no existing test walked the hero through an onStep trap or a hidden object's onVisible trigger.
// FireTrigger uses the same system prompt (Prompts/Dnd5e/SendAction) as an ordinary
// SendAction (see GameAiClient.BuildSendActionContext) — so a mock named after the "SendAction" action
// intercepts both ordinary player actions and trigger firings alike.
public class TriggersScenarioTests
{
    private readonly ITestOutputHelper _output;

    public TriggersScenarioTests(ITestOutputHelper output) => _output = output;

    // Fixtures/trap_game.json: hero at [1,3], a "Snare Trap" object at [2,3] with triggers.onStep —
    // one step east must land right on it.
    [Fact]
    public void OnStepTrap_MovingOntoIt_FiresAiTrigger()
    {
        using var game = GameSession.Launch(
            fixtureFileName: "trap_game.json", mockedActions: ["SendAction"], mockResponsesFolder: "Triggers");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered. Moving the hero east — onto the trap cell [2,3]...");
        TestPacing.Step();

        GameConsole.SendKey(game.Pid, ConsoleKey.RightArrow);

        // Stepping on the trap must call FireTrigger (SendAction mock, artificial 5s delay) —
        // the "Waiting..." spinner proves a real AI call happened, not just a local
        // render of the new position (ordinary movement without triggers does not call the AI at all — see
        // MovementFogOfWarTests, where the silent step is checked by contradiction).
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Waiting", 3000),
            "After stepping onto the cell with the onStep trap no waiting spinner appeared — FireTrigger was not called.");
        _output.WriteLine("Waiting spinner appeared — FireTrigger called. Waiting for the (mock) response...");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "continues", 10000),
            "FireTrigger (mock) response did not arrive after the onStep trap fired.");
        _output.WriteLine("Trap firing response shown.");
        TestPacing.Step();
    }

    // Fixtures/hidden_object_game.json: hero at [1,3], a hidden "Secret Sigil" object at [5,3]
    // (hidden:true, triggers.onVisible), hero vision 10ft (2 cells). After the first step east
    // (distance becomes 3 cells) triggers.onVisible must not fire yet — the hero has not seen the
    // object; the second step (distance 2 cells) must "notice" it and call FireTrigger,
    // even though the object itself is never shown on the grid (hidden:true).
    [Fact]
    public void HiddenObjectOnVisible_EnteringVisionRange_FiresAiTrigger()
    {
        using var game = GameSession.Launch(
            fixtureFileName: "hidden_object_game.json", mockedActions: ["SendAction"], mockResponsesFolder: "HiddenTrigger");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered. First step east — the object is still out of vision range...");
        TestPacing.Step();

        GameConsole.SendKey(game.Pid, ConsoleKey.RightArrow);

        // Ordinary local movement (without triggers) must not call the AI at all — the absence of
        // the spinner during a short window proves that FireTrigger was NOT called prematurely.
        Thread.Sleep(1200);
        var rowsAfterFirstStep = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        Assert.DoesNotContain(rowsAfterFirstStep, r => r.Contains("Waiting"));
        _output.WriteLine("No spinner appeared — as expected, the object has not been noticed yet. Second step east...");
        TestPacing.Step();

        GameConsole.SendKey(game.Pid, ConsoleKey.RightArrow);

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Waiting", 3000),
            "After the second step (the object should enter vision range) no waiting spinner appeared — the hidden object's onVisible trigger did not fire.");
        _output.WriteLine("Waiting spinner appeared — the hidden object was noticed, FireTrigger called. Waiting for the (mock) response...");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "continues", 10000),
            "FireTrigger (mock) response did not arrive after the onVisible trigger fired.");
        _output.WriteLine("Hidden object discovery response shown.");
        TestPacing.Step();
    }
}
