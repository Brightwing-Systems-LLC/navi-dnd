using System.Text.Json;
using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: the dice roll dialog and the AI question dialog (ask_player) — in the real game both are started by
// GameAiClient.SpinAsync, which polls roll_request.json/ask_player_request.json in
// NaviDnD/Storage/ WHILE waiting for the SendAction response (mock or real — doesn't matter). Since this is a file
// read, not the model call itself, the test can trigger the dialog just by writing such a file during
// the mock response's artificial delay (SelectiveMockProvider.MockResponseDelayMs, 5s) — without a single
// real AI call.
public class InteractiveResponseTests
{
    private readonly ITestOutputHelper _output;

    public InteractiveResponseTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void DiceRollRequest_DisplaysAndResolves()
    {
        using var game = GameSession.Launch(mockedActions: ["SendAction"], mockResponsesFolder: "DiceRoll");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen rendered.");
        TestPacing.Step();

        _output.WriteLine("Sending an action — waiting for SendAction (mock) to start...");
        GameConsole.SendText(game.Pid, "I attack the enemy with my sword");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);
        Thread.Sleep(150);

        string rollRequestPath = Path.Combine(game.RealStorageDir, "roll_request.json");
        var rollRequest = new
        {
            history = new[] { new { text = "Roll to attack!" } },
            difficulty = 15,
            modifiers = new[] { new { name = "Dexterity", value = 4 } },
        };
        _output.WriteLine("Writing roll_request.json — waiting for the game to pick it up and show the roll dialog...");
        File.WriteAllText(rollRequestPath, JsonSerializer.Serialize(rollRequest));
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "DC: 15", 4000),
            "The dice roll dialog did not show the difficulty (DC: 15).");
        TestPacing.Step();
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Dexterity", 2000),
            "The dice roll dialog did not show the modifier (+4 Dexterity).");
        TestPacing.Step();
        // "roll" appears only in the INTERACTIVE loop AFTER request.History has been printed —
        // until then key presses are simply discarded (see DialogDisplay.PlayDiceRollRequest:
        // `while (Console.KeyAvailable) Console.ReadKey(true);` right after the history streams).
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Space/Enter] roll", 3000),
            "The interactive \"[Space/Enter] roll\" prompt did not appear.");
        _output.WriteLine("Roll dialog shown correctly. Rolling (Space)...");
        TestPacing.Step();

        GameConsole.SendKey(game.Pid, ConsoleKey.Spacebar);
        // Navigation must respond while the dice is still settling, before its result.
        Thread.Sleep(120);
        GameConsole.SendKey(game.Pid, ConsoleKey.F1);
        Assert.True(GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth,
            "→ MAP ←", 1200), "Map navigation stalled during the dice animation.");
        GameConsole.SendKey(game.Pid, ConsoleKey.F4);
        Assert.True(GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth,
            "→ CHARACTER ←", 1200), "Character navigation stalled during the dice animation.");


        // The roll outcome is random (d20) — wait for any of the 4 possible outcomes.
        string[] outcomes = ["CRITICAL SUCCESS!", "CRITICAL FAILURE!", "SUCCESS", "FAILURE"];
        string? outcome = GameConsole.WaitForAnyRowContainsAny(
            game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, outcomes, 8000);
        Assert.True(outcome != null, "The roll animation did not show any of the expected outcomes.");
        _output.WriteLine($"Outcome shown: \"{outcome}\".");
        TestPacing.Step();

        // Critical success/failure are (80,220,80)/(220,80,80) — a plain "success"/"failure"
        // is painted with the same two colors (see DialogDisplay.BuildRollPanel: _outcomeColor).
        bool isSuccess = outcome!.Contains("success", StringComparison.OrdinalIgnoreCase);
        (int r, int g, int b) expectedColor = isSuccess ? (80, 220, 80) : (220, 80, 80);
        (byte r, byte g, byte b) background = (18, 18, 22);
        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        var failures = new List<string>();
        _output.WriteLine($"Checking the outcome color ({(isSuccess ? "green" : "red")} expected)...");
        ScreenAssertions.CheckTextColor(game.Pid, rows, outcome, expectedColor, 60, background, "roll dialog", failures);
        TestPacing.Step();

        _output.WriteLine("Waiting for SendAction (mock) to finish after the roll...");
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "continues", 10000),
            "The final SendAction (mock) message did not arrive after the roll was resolved.");

        // The final Step() goes before the Assert, not after: if the checks failed, Assert.True below
        // throws and the code after it won't run, but the debugging pause is needed either way.
        TestPacing.Step();
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }

    // Advantage/disadvantage (a paired d20 + the "ADVANTAGE"/"DISADVANTAGE" label — see DialogDisplay.
    // BuildRollPanel/PlayDiceRollRequest: isPair = request.Advantage || request.Disadvantage) —
    // until now only an ordinary single roll was checked (DiceRollRequest_DisplaysAndResolves).
    [Fact]
    public void DiceRollRequest_WithAdvantage_DisplaysPairAndResolves()
    {
        using var game = GameSession.Launch(mockedActions: ["SendAction"], mockResponsesFolder: "DiceRollAdvantage");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen rendered.");
        TestPacing.Step();

        _output.WriteLine("Sending an action — waiting for SendAction (mock) to start...");
        GameConsole.SendText(game.Pid, "I try to sneak past the guards");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);
        Thread.Sleep(150);

        string rollRequestPath = Path.Combine(game.RealStorageDir, "roll_request.json");
        var rollRequest = new
        {
            history = new[] { new { text = "Roll Stealth with advantage!" } },
            difficulty = 14,
            advantage = true,
            modifiers = new[] { new { name = "Dexterity", value = 4 } },
        };
        _output.WriteLine("Writing roll_request.json (advantage: true) — waiting for the roll dialog with a pair of dice...");
        File.WriteAllText(rollRequestPath, JsonSerializer.Serialize(rollRequest));
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "ADVANTAGE", 4000),
            "The roll dialog did not show the \"ADVANTAGE\" label for the paired roll.");
        _output.WriteLine("\"ADVANTAGE\" label shown.");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Space/Enter] roll", 3000),
            "The interactive \"[Space/Enter] roll\" prompt did not appear for the paired roll.");
        _output.WriteLine("Rolling (Space)...");
        TestPacing.Step();

        GameConsole.SendKey(game.Pid, ConsoleKey.Spacebar);

        string[] outcomes = ["CRITICAL SUCCESS!", "CRITICAL FAILURE!", "SUCCESS", "FAILURE"];
        string? outcome = GameConsole.WaitForAnyRowContainsAny(
            game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, outcomes, 8000);
        Assert.True(outcome != null, "The paired roll animation did not show any of the expected outcomes.");
        _output.WriteLine($"Outcome shown: \"{outcome}\".");
        TestPacing.Step();

        _output.WriteLine("Waiting for SendAction (mock) to finish after the roll...");
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "continues", 10000),
            "The final SendAction (mock) message did not arrive after the paired roll was resolved.");
        TestPacing.Step();
    }

    // Disadvantage — the same paired d20 as advantage, but with the "DISADVANTAGE" label, and
    // the LOWER of the two rolls is used (DialogDisplay.PlayDiceRollRequest: usedRoll =
    // Math.Min(roll1, roll2)). There are deliberately 3 modifiers (not 1, as in the other tests) —
    // this checks the step-by-step animation (BuildRollPanel: the modifier highlight in sync with the number on
    // the die in use, ~2.8s in total split evenly across all modifiers — with three modifiers
    // each one is shorter than it would be with one).
    [Fact]
    public void DiceRollRequest_WithDisadvantage_DisplaysPairAndMultipleModifiers()
    {
        using var game = GameSession.Launch(mockedActions: ["SendAction"], mockResponsesFolder: "DiceRollDisadvantage");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen rendered.");
        TestPacing.Step();

        _output.WriteLine("Sending an action — waiting for SendAction (mock) to start...");
        GameConsole.SendText(game.Pid, "I try to walk along the narrow icy ledge");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);
        Thread.Sleep(150);

        string rollRequestPath = Path.Combine(game.RealStorageDir, "roll_request.json");
        var rollRequest = new
        {
            history = new[] { new { text = "Roll Dexterity (Acrobatics) with disadvantage!" } },
            difficulty = 16,
            disadvantage = true,
            modifiers = new[]
            {
                new { name = "Dexterity", value = 3 },
                new { name = "Acrobatics", value = 2 },
                new { name = "Black Ice", value = -2 },
            },
        };
        _output.WriteLine("Writing roll_request.json (disadvantage: true, 3 modifiers) — waiting for the roll dialog with a pair of dice...");
        File.WriteAllText(rollRequestPath, JsonSerializer.Serialize(rollRequest));
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "DISADVANTAGE", 4000),
            "The roll dialog did not show the \"DISADVANTAGE\" label for the paired roll.");
        _output.WriteLine("\"DISADVANTAGE\" label shown.");
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Space/Enter] roll", 3000),
            "The interactive \"[Space/Enter] roll\" prompt did not appear for the paired roll.");
        _output.WriteLine("Rolling (Space)...");
        TestPacing.Step();

        GameConsole.SendKey(game.Pid, ConsoleKey.Spacebar);

        // All 3 modifiers must appear in the panel (BuildRollPanel: footer, Take(3)) —
        // regardless of which animation phase (highlight/pause) the poll catches them in.
        foreach (var mod in rollRequest.modifiers)
            Assert.True(
                GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, mod.name, 6000),
                $"Modifier \"{mod.name}\" was not shown in the roll panel.");
        _output.WriteLine("All 3 modifiers shown.");
        TestPacing.Step();

        string[] outcomes = ["CRITICAL SUCCESS!", "CRITICAL FAILURE!", "SUCCESS", "FAILURE"];
        string? outcome = GameConsole.WaitForAnyRowContainsAny(
            game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, outcomes, 10000);
        Assert.True(outcome != null, "The paired disadvantage roll animation did not show any of the expected outcomes.");
        _output.WriteLine($"Outcome shown: \"{outcome}\".");
        TestPacing.Step();

        _output.WriteLine("Waiting for SendAction (mock) to finish after the roll...");
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "continues", 10000),
            "The final SendAction (mock) message did not arrive after the paired disadvantage roll was resolved.");
        TestPacing.Step();
    }

    // A roll without difficulty (RollRequest.Difficulty == null) — this is exactly how the prompt (§ COMBAT START)
    // requires rolling the hero's initiative: roll_dice with modifiers:[{"name":"Initiative",...}], WITHOUT
    // difficulty (enemies don't roll initiative, the AI makes it up itself). DialogDisplay.BuildRollPanel
    // must simply not show the "DC:" row and not show "Success"/"Failure" — the very fact
    // that both fields are optional (RollRequest.Difficulty is int?) was never checked before.
    [Fact]
    public void DiceRollRequest_WithoutDifficulty_DisplaysNoDcOrOutcome()
    {
        using var game = GameSession.Launch(mockedActions: ["SendAction"], mockResponsesFolder: "DiceRollInitiative");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen rendered.");
        TestPacing.Step();

        _output.WriteLine("Sending an action — waiting for SendAction (mock) to start...");
        GameConsole.SendText(game.Pid, "enemies attack, let's fight");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);
        Thread.Sleep(150);

        string rollRequestPath = Path.Combine(game.RealStorageDir, "roll_request.json");
        // "difficulty" is deliberately ABSENT — as in a real initiative roll.
        var rollRequest = new
        {
            history = new[] { new { text = "Roll initiative!" } },
            modifiers = new[] { new { name = "Initiative", value = 2 } },
        };
        _output.WriteLine("Writing roll_request.json (no difficulty) — waiting for a roll dialog without a DC...");
        File.WriteAllText(rollRequestPath, JsonSerializer.Serialize(rollRequest));
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Initiative", 4000),
            "The roll dialog did not show the modifier (\"Initiative\").");
        _output.WriteLine("Modifier \"Initiative\" shown.");
        TestPacing.Step();

        var beforeRoll = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        Assert.DoesNotContain(beforeRoll, r => r.Contains("DC:"));

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Space/Enter] roll", 3000),
            "The interactive \"[Space/Enter] roll\" prompt did not appear.");
        _output.WriteLine("Rolling (Space)...");
        TestPacing.Step();

        GameConsole.SendKey(game.Pid, ConsoleKey.Spacebar);

        _output.WriteLine("Waiting for SendAction (mock) to finish after the roll...");
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "continues", 10000),
            "The final SendAction (mock) message did not arrive after the roll without difficulty was resolved.");

        // Final snapshot — DC: must not have appeared at ANY stage of the roll animation.
        // "Success"/"Failure" are not checked: the critical text ("Critical success!"/"...failure!")
        // can still appear on a natural 1/20 even without difficulty — that is not what matters here.
        var afterRoll = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        Assert.DoesNotContain(afterRoll, r => r.Contains("DC:"));
        TestPacing.Step();
    }

    [Fact]
    public void AskPlayerRequest_DisplaysAndResolves()
    {
        using var game = GameSession.Launch(mockedActions: ["SendAction"], mockResponsesFolder: "AskPlayer");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen rendered.");
        TestPacing.Step();

        _output.WriteLine("Sending an action — waiting for SendAction (mock) to start...");
        GameConsole.SendText(game.Pid, "I approach the fork in the road");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);
        Thread.Sleep(150);

        string askPlayerPath = Path.Combine(game.RealStorageDir, "ask_player_request.json");
        string[] options = ["Fight", "Flee", "Negotiate"];
        var askRequest = new
        {
            history = new[] { new { text = "What will you do?" } },
            options,
        };
        _output.WriteLine("Writing ask_player_request.json — waiting for the game to pick it up and show the question with options...");
        File.WriteAllText(askPlayerPath, JsonSerializer.Serialize(askRequest));
        TestPacing.Step();

        foreach (var option in options)
            Assert.True(
                GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, option, 4000),
                $"Answer option \"{option}\" not shown in the question dialog.");
        _output.WriteLine("All answer options shown. Choosing the first (Enter)...");
        TestPacing.Step();

        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);

        // The chosen option must appear in the dialog history IMMEDIATELY (DialogDisplay.
        // PlayAskPlayerRequest redraws the dialog block right after the choice), NOT
        // only when the whole SendAction (mock, artificial 5s delay) has fully finished.
        // The timeout here is deliberately short, not "with margin" — a wide window would mask
        // a regression (the test would pass anyway, simply by waiting for the final mock response).
        // IMPORTANT: we look specifically for "Rustem: <answer>" (with the author prefix) — not the bare option text,
        // which stays visible for a while in the OPTION LIST itself (PlayOptionsSelection) and would
        // give a false positive regardless of whether the answer reached the dialog history.
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, $"Rustem: {options[0]}", 2000),
            "The chosen answer option did not appear in the dialog history IMMEDIATELY after the choice — it must be shown at once, without waiting for the mock response.");
        TestPacing.Step();

        _output.WriteLine("Waiting for SendAction (mock) to finish after the answer...");
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "accepted", 10000),
            "The final SendAction (mock) message did not arrive after answering the question.");

        // The final Step() — a pause at the end of the test to visually check the final state
        // before GameSession.Dispose() kills the game process.
        TestPacing.Step();
    }

    // Without "options" DialogDisplay.PlayAskPlayerRequest takes a DIFFERENT branch — PlayFreeTextInput
    // (an open text field "Input (Ns): ") instead of choosing from options (PlayOptionsSelection).
    // This is a separate, previously unchecked display path — with its own input field and cursor.
    [Fact]
    public void AskPlayerRequest_FreeText_DisplaysAndResolves()
    {
        using var game = GameSession.Launch(mockedActions: ["SendAction"], mockResponsesFolder: "AskPlayerFreeText");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen rendered.");
        TestPacing.Step();

        _output.WriteLine("Sending an action — waiting for SendAction (mock) to start...");
        GameConsole.SendText(game.Pid, "I look around");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);
        Thread.Sleep(150);

        string askPlayerPath = Path.Combine(game.RealStorageDir, "ask_player_request.json");
        // "options" is deliberately ABSENT from the request — this is exactly what switches the dialog to
        // open text input instead of a list of options.
        var askRequest = new { history = new[] { new { text = "What exactly are you looking for?" } } };
        _output.WriteLine("Writing ask_player_request.json (no options) — waiting for the open text field...");
        File.WriteAllText(askPlayerPath, JsonSerializer.Serialize(askRequest));
        TestPacing.Step();

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "looking for?", 4000),
            "The question text was not shown.");
        // IMPORTANT: the ordinary dialog window (InputBox) shows "Input: " as a placeholder
        // by default when idle — plain "Input" would match it INSTANTLY, before the
        // interactive open-input loop (PlayFreeTextInput) actually opens, and the typed text
        // would go nowhere. PlayFreeTextInput has a different format — "Input (Ns): " with a parenthesis and
        // a timer — so we look specifically for "Input (", which does not match the ordinary InputBox placeholder.
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Input (", 3000),
            "The open answer text field (\"Input (Ns): \") was not shown.");
        _output.WriteLine("Text field shown. Typing a free-text answer...");
        TestPacing.Step();

        const string freeTextAnswer = "I search the wall for a secret door";
        GameConsole.SendText(game.Pid, freeTextAnswer);
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);

        // The entered answer must appear in the dialog history IMMEDIATELY (DialogDisplay.
        // PlayAskPlayerRequest redraws the dialog block right after input), NOT
        // only when the whole SendAction (mock, artificial 5s delay) has fully finished.
        // The timeout here is deliberately short — a wide window would mask a regression (the test
        // would pass anyway, simply by waiting for the final mock response). IMPORTANT: we look specifically for
        // "Rustem: <answer>" (with the author prefix) — the bare answer text is still visible in the INPUT
        // FIELD ITSELF (PlayFreeTextInput draws it on the "Input (Ns): <text>" row) regardless of whether
        // the answer reached the dialog history, and would give a false positive.
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, $"Rustem: {freeTextAnswer}", 2000),
            "The entered free-text answer did not appear in the dialog history IMMEDIATELY after input — it must be shown at once, without waiting for the mock response.");
        TestPacing.Step();

        _output.WriteLine("Waiting for SendAction (mock) to finish after the free-text answer...");
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "continues", 15000),
            "The final SendAction (mock) message did not arrive after the free-text answer.");

        TestPacing.Step();
    }
}
