using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: arrow-key movement and fog of war (MapObjectsProvider.ComputeHeroVisibility/WriteCellEntity)
// are covered by logic unit tests (CalculateMovementTests), but were never run through the real
// game loop/UI — no existing UI test sends the hero a movement arrow and checks that
// entity visibility on the grid changes dynamically with distance.
//
// Fixtures/fog_of_war_game.json: hero at [1,3] with visionFt=5 (1 cell), a "Skeleton" creature (SK1) at
// [4,3] with its own visionFt=0 (it never "notices" the hero by itself — otherwise an encounter
// trigger would fire and an AI mock would be needed). Plain movement without triggers does not call the AI at all (see
// MapScreenLoop.RunAsync: moved == false → redraw in place and wait for input again), so no
// mock is needed here — this is a pure check of fog-of-war rendering through the real game process.
public class MovementFogOfWarTests
{
    private readonly ITestOutputHelper _output;

    public MovementFogOfWarTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void ArrowMovement_RevealsAndHidesEntity_AsVisionRangeChanges()
    {
        using var game = GameSession.Launch(fixtureFileName: "fog_of_war_game.json");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered.");
        TestPacing.Step();

        var initialRows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        Assert.DoesNotContain(initialRows, r => r.Contains("SK1"));
        _output.WriteLine("Skeleton (SK1) is outside the hero's vision — its symbol is not shown on the map, as expected.");
        TestPacing.Step();

        _output.WriteLine("Moving the hero east twice — the distance to the skeleton shrinks to 1 cell (5ft)...");
        GameConsole.SendKey(game.Pid, ConsoleKey.RightArrow);
        Thread.Sleep(400);
        GameConsole.SendKey(game.Pid, ConsoleKey.RightArrow);

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "SK1", 3000),
            "After closing to vision range (5ft) the skeleton symbol \"SK1\" did not appear on the map — dynamic fog of war did not work.");
        _output.WriteLine("Skeleton entered vision range — its symbol is shown.");
        TestPacing.Step();

        _output.WriteLine("Moving back west twice — the skeleton should hide in the fog again...");
        GameConsole.SendKey(game.Pid, ConsoleKey.LeftArrow);
        Thread.Sleep(400);
        GameConsole.SendKey(game.Pid, ConsoleKey.LeftArrow);

        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                rows => !rows.Any(r => r.Contains("SK1")), 3000),
            "After moving out of vision range the skeleton symbol \"SK1\" is still visible — visibility must be dynamic (not \"seen once, visible forever\").");
        _output.WriteLine("Skeleton is out of sight again beyond vision range.");
        TestPacing.Step();
    }
}
