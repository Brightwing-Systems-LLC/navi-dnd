using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: handling of a malformed/unprocessable AI response (GameAiClient.RecordError). Until now
// no test fed the game a broken response to confirm that an error message is shown instead of a
// crash or hang. The mock response (Fixtures/MockResponses/MalformedResponse/SendAction/
// response.json) is plain text without a single JSON character, so SequentialHistoryPlayer.
// PlayAsync (JsonNode.Parse) throws an exception, which GameAiClient.SendAction catches, adding
// "Error: ..." straight into the dialog history.
public class ErrorHandlingScenarioTests
{
    private readonly ITestOutputHelper _output;

    public ErrorHandlingScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void MalformedAiResponse_ShowsErrorMessage_InsteadOfCrashing()
    {
        using var game = GameSession.Launch(mockedActions: ["SendAction"], mockResponsesFolder: "MalformedResponse");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen rendered.");
        TestPacing.Step();

        _output.WriteLine("Sending an action — the SendAction mock will return non-JSON text...");
        GameConsole.SendText(game.Pid, "I look around the room");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Error", 15000),
            "After a malformed (non-JSON) AI response no error message appeared in the dialog — broken-response handling does not work.");
        _output.WriteLine("Error message shown — the game neither crashed nor hung.");
        TestPacing.Step();

        // The game must stay in a working state — the character screen is still rendered
        // (borders/tabs in place), not stuck in some intermediate/broken state.
        // A short pause before scanning ALL rows — right after "Error" first appears the
        // dialog panel may still be redrawing (it is drawn row by row).
        Thread.Sleep(300);
        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        var failures = new List<string>();
        ScreenAssertions.CheckBorders(rows, "character screen after AI error", failures);
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
        TestPacing.Step();
    }
}
