using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E regression: outside combat an ordinary free-text action (not grid movement) can itself advance
// the round — the hero spends the "Action", and the AI patches time.totalRounds+1 in the same response (see SendAction
// systemPrompt § ACTIONS AND ROUNDS). Before the fix MapScreenLoop.RunAsync called Storage.TakeRoundBundle()
// only after grid movement (arrow keys) — after a free-text action (SendAction) periodic
// effects (hero.effects[].onRound) were NOT checked at all, and nothing reminded the AI to apply the
// "Burning" effect tick for that round. Now TakeRoundBundle() is also checked after SendAction —
// this must cause a SECOND "SendAction" call (FireRoundTransition uses the same system
// prompt) with the effect tick.
//
// Fixtures/burning_effect_game.json: hero out of combat, hp 10/10, one active "Burning" effect with
// onRound (no onExpire/expiresAtRound — indefinite, removed only manually). SendAction mock:
// response.json (1st call, the player's action) spends the "Action" and itself advances totalRounds → 2, WITHOUT
// changing HP — this proves the AI mock does not have to burn itself; the engine must remind it
// with a separate call; response2.json (2nd call, FireRoundTransition) deals 1 damage from "Burning".
public class BurningEffectRoundTransitionTests
{
    private readonly ITestOutputHelper _output;

    public BurningEffectRoundTransitionTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void FreeTextAction_ThatAdvancesRound_TriggersOnRoundEffectTick()
    {
        using var game = GameSession.Launch(
            fixtureFileName: "burning_effect_game.json", mockedActions: ["SendAction"], mockResponsesFolder: "BurningEffect");
        _output.WriteLine($"Game launched (pid {game.Pid}).");
        var failures = new List<string>();

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered. Sending the player's free-text action (1st mock call — spends the Action and advances the round itself, no damage)...");
        TestPacing.Step();

        GameConsole.SendText(game.Pid, "I wait and look around");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Waiting", 3000),
            "Waiting spinner for the player's action did not appear.");
        _output.WriteLine("Action is being processed. After it the engine must detect the round change itself and send an effect reminder (2nd mock call)...");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Round 2", 20000),
            "The round did not advance to 2 — the 1st mock response (player's action) was not applied as expected.");
        _output.WriteLine("Round advanced to 2.");

        // Don't just wait for the text "Burning" — it is visible in the legend from the very start (the name of the hero's active
        // effect is always drawn), so that condition would be met instantly and prove
        // nothing. Wait specifically for the text of the SECOND mock response (response2.json) — it appears only
        // if Storage.TakeRoundBundle() after SendAction actually fired and called FireRoundTransition.
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "sear your skin again", 20000),
            "Text of the 2nd mock response (\"Burning\" effect tick) did not appear — Storage.TakeRoundBundle() did not fire after the free-text action.");
        TestPacing.Step();

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);

        _output.WriteLine("Checking that the effect damage was actually applied to the hero's HP...");
        if (!rows.Any(r => r.Contains("❤ 9/10")))
            failures.Add("After the \"Burning\" tick the hero's HP did not update to \"❤ 9/10\" — the round-transition patch was not applied.");
        TestPacing.Step();

        _output.WriteLine(failures.Count == 0
            ? "All checks passed."
            : $"Failed checks: {failures.Count}.");
        TestPacing.Step();
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }
}
