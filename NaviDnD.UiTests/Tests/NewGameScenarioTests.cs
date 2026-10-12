using System.Text.Json;
using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: creating a new hero (Menu → [F2] → 5 form fields) → character editor ([F3]START)
// → a game on a generated map. The AI provider is mocked (GameSession.LaunchForNewGame) —
// CreateNewGame/StartNewGame take canonical responses from Fixtures/MockResponses/, the real
// model is not called even once during the test.
public class NewGameScenarioTests
{
    private static readonly (byte r, byte g, byte b) MainBackground = (18, 18, 22);

    private readonly ITestOutputHelper _output;

    public NewGameScenarioTests(ITestOutputHelper output) => _output = output;

    // Esc at any point in the form must cancel hero creation and return to the Menu (ScreenRegistry:
    // Screen.NewGame.Commands["Esc"] → Screen.Menu) — until now this was checked neither at the start of the form
    // nor (more importantly) in the middle of it, after some fields have already been filled in. CreateNewGame must
    // not be called at all (NewGameDisplay.GetFieldInput returns null as soon as it
    // recognizes "Esc" among the screen's commands, before the final await _aiClient.CreateNewGame(...)).
    [Fact]
    public void EscMidAnketa_CancelsCreation_ReturnsToMenu()
    {
        using var game = GameSession.LaunchForNewGame();
        _output.WriteLine($"Game launched (pid {game.Pid}), Menu screen rendered.");

        _output.WriteLine("Sending [F2] — waiting for the character creation form...");
        Assert.True(Navigation.ToAnketa(game.Pid),
            "The character creation form did not appear after pressing [F2] in the menu.");
        TestPacing.Step();

        _output.WriteLine("Filling in the first two form fields (name, symbol), then cancelling with Esc in the middle of the third...");
        TypeField(game.Pid, "Test Hero To Cancel");
        TypeField(game.Pid, "ESC");
        GameConsole.SendKey(game.Pid, ConsoleKey.Escape);
        // Leaving game creation shows a warning that progress will not be saved: [Enter] leaves.
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "New game progress will not be saved", 5000),
            "After [Esc] in the middle of the form no warning about leaving to the menu appeared.");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "MAIN MENU", 5000),
            "After [Esc] in the middle of the character creation form we did not return to the Menu screen.");
        _output.WriteLine("Menu screen rendered — hero creation cancelled, CreateNewGame should not have been called.");
        TestPacing.Step();

        // Reopening the form must start from a clean slate — text entered earlier
        // must not leak into the new attempt (a regression guard against state leaking between forms).
        _output.WriteLine("Opening the form again — checking that the fields are empty (no leak of the previous input)...");
        Assert.True(Navigation.ToAnketa(game.Pid),
            "Could not reopen the character creation form after cancelling.");

        // The title (row 1) updates first — the form body may finish drawing one poll tick
        // later (the same class of race as in CheckEditorScreen above) — wait rather than checking a single snapshot.
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => !r.Any(x => x.Contains("Test Hero To Cancel")), 3000),
            "The form reopened after cancelling still shows text from the previous (cancelled) attempt.");
        _output.WriteLine("The form reopened with no trace of the previous cancelled input.");
        TestPacing.Step();
    }

    [Fact]
    public void CreateHeroAndStartNewGame()
    {
        using var game = GameSession.LaunchForNewGame();
        _output.WriteLine($"Game launched (pid {game.Pid}), Menu screen rendered.");
        var failures = new List<string>();

        _output.WriteLine("Sending [F2] — waiting for the character creation form...");
        Assert.True(Navigation.ToAnketa(game.Pid),
            "The character creation form did not appear after pressing [F2] in the menu.");
        _output.WriteLine("Form rendered. Filling in 5 fields (name/symbol/description/setting/wish)...");

        // The AI response is mocked and does not depend on the entered text — but the real form must
        // really accept input (text, Enter between fields), otherwise the test does not check the UI.
        TypeField(game.Pid, "Test Hero");
        TypeField(game.Pid, "TST");
        TypeField(game.Pid, "A test hero for the game creation e2e test.");
        // The form does not create the hero on the last field — color/portrait and the button come next.
        GameConsole.SendKey(game.Pid, ConsoleKey.F3);   // the form's "NEXT" — same as [F3] (a button badge without arrows)

        // SelectiveMockProvider deliberately delays the mock response by 5s (MockResponseDelayMs) —
        // this is the only way to really check that the waiting spinner (Spinner.While) is drawn
        // during that time, rather than just assuming it is there.
        _output.WriteLine("Checking that the \"Creating the character\" spinner is visible while waiting for CreateNewGame (mock, artificial 5s delay)...");
        if (!GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Creating the character", 3000))
            failures.Add("The \"Creating the character\" spinner did not appear while waiting for the CreateNewGame (mock) response.");

        _output.WriteLine("Waiting for the CreateNewGame (mock) response and the character editor screen...");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ EDITOR ←", 20000),
            "The character editor screen did not appear after filling in the form — CreateNewGame (mock) did not finish.");
        // The title (row 1) updates first — the hero card below may finish drawing
        // one poll tick later; without a pause here ReadRows sometimes catches a half-drawn card.
        Thread.Sleep(200);
        _output.WriteLine("Character editor screen rendered.");
        CheckEditorScreen(game.Pid, failures);
        TestPacing.Step();

        _output.WriteLine("[character editor] Checking sub-tab switching [F6]/[F7] (Inventory/Features)...");
        CheckEditorTabSwitching(game.Pid, failures);
        TestPacing.Step();

        CheckFixHeroEditWithAskPlayer(game, failures);
        TestPacing.Step();

        // Settle pause: FixHeroForNewGame+ask_player have just run SpinAsync/the option selection
        // loop — the outer game loop (Program.cs) needs a moment to get back to waiting
        // for input on the editor screen before the next key is sent (the same class
        // of race already seen when reading the hero card right after a mock response).
        Thread.Sleep(500);
        _output.WriteLine("Sending [F3] (START) → the \"Adventure\" step → START GAME...");
        Assert.True(Navigation.StartFromEditor(game.Pid), "After [F3] in the editor the \"Adventure\" step did not open.");

        _output.WriteLine("Checking that the \"Creating the world\" spinner is visible while waiting for StartNewGame (mock, artificial 5s delay)...");
        bool sawSpinner = GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Creating the world", 3000);
        if (!sawSpinner)
        {
            // Occasionally [F3] doesn't get through on the first try — but ONLY when running the WHOLE test suite
            // in a row (5+ game processes started and killed before this), never in an isolated
            // run; the screen reliably returns to a correct editor state
            // (not somewhere else) — looks like a race in the automation itself (sending a key
            // via WriteConsoleInput under load), not a game bug. Retry the key press once.
            _output.WriteLine("No spinner appeared — repeating Enter on the \"Adventure\" step...");
            GameConsole.SendKey(game.Pid, ConsoleKey.Enter);
            sawSpinner = GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Creating the world", 5000);
        }
        if (!sawSpinner)
            failures.Add("The \"Creating the world\" spinner did not appear while waiting for the StartNewGame (mock) response, even after repeating [F3].");

        _output.WriteLine("Waiting for the Map screen...");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 12000),
            "Map screen did not appear after [F3] — StartNewGame (mock) did not finish.");
        Thread.Sleep(200);
        _output.WriteLine("Map screen rendered (the world was generated from the StartNewGame mock response).");
        CheckMapScreen(game.Pid, failures);
        TestPacing.Step();

        _output.WriteLine("Sending [F4] — waiting for the Character screen, checking the hero's final state...");
        GameConsole.SendKey(game.Pid, ConsoleKey.F4);
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ CHARACTER ←", 5000),
            "Character screen did not appear after [F4] on the map.");
        Thread.Sleep(200);
        CheckCharacterScreen(game.Pid, failures);
        TestPacing.Step();

        _output.WriteLine(failures.Count == 0
            ? "All checks passed."
            : $"Failed checks: {failures.Count}.");
        // The final Step() goes before the Assert, not after: if the checks failed, Assert.True below
        // throws and the code after it won't run, but the debugging pause is needed either way.
        TestPacing.Step();
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }

    private static void TypeField(int gamePid, string text)
    {
        GameConsole.SendText(gamePid, text);
        GameConsole.SendKey(gamePid, ConsoleKey.Enter);
        Thread.Sleep(150);
    }

    private void CheckEditorScreen(int gamePid, List<string> failures)
    {
        const string context = "character editor";

        _output.WriteLine($"[{context}] Checking tabs [F2]/[F3]/[Esc]...");
        string title = GameConsole.ReadRow(gamePid, 1, GameConsole.TitleReadWidth);
        ScreenAssertions.RequireTab(title, "[F4]", "ADVENTURE", context, failures);
        ScreenAssertions.RequireTab(title, "[Esc]", "MENU", context, failures);
        TestPacing.Step();

        _output.WriteLine($"[{context}] Checking borders...");
        var rows = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        ScreenAssertions.CheckBorders(rows, context, failures);
        TestPacing.Step();

        _output.WriteLine($"[{context}] Checking the created hero's card (name, hp, inventory)...");
        CheckMockedHeroCard(gamePid, rows, context, failures);
        TestPacing.Step();
    }

    // IMPORTANT: [F2] in EditorScreen leads to Screen.NewGameAbilities/AbilityDisplay — that is a REAL
    // stub (see AbilityDisplay.Draw(): it draws only a frame with the "COMPANION" title, there is no content
    // at all — BuildAbilityLines exists but is never called). The real Inventory/Features
    // sub-tabs are F6/F7 INSIDE the Screen.NewGameCharacter screen (HeroDisplay,
    // the same mechanism as on the gameplay Character screen — see GameScenarioTests). We check
    // exactly those: switching the sub-tab must change the card's visible content.
    private void CheckEditorTabSwitching(int gamePid, List<string> failures)
    {
        const string context = "character editor";

        GameConsole.SendKey(gamePid, ConsoleKey.F7);
        if (!GameConsole.WaitForAnyRowContains(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "→ FEATURES ←", 3000))
        {
            failures.Add($"[{context}] Could not switch to the [F7] (Features) sub-tab in the hero card.");
            return;
        }
        var abilityRows = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        if (!abilityRows.Any(r => r.Contains("Test Ability 1")))
            failures.Add($"[{context}] After switching to the [F7] sub-tab the features from the CreateNewGame mock response are not shown.");

        GameConsole.SendKey(gamePid, ConsoleKey.F6);
        if (!GameConsole.WaitForAnyRowContains(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "→ INVENTORY ←", 3000))
        {
            failures.Add($"[{context}] Could not return to the [F6] (Inventory) sub-tab in the hero card.");
            return;
        }
        var invRows = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        if (!invRows.Any(r => r.Contains("Test Sword")))
            failures.Add($"[{context}] After returning to the [F6] sub-tab the inventory from the CreateNewGame mock response is not shown.");
    }

    // Checks that during character editing (FixHeroForNewGame, an action separate from
    // SendAction) the ask_player mechanism (a question dialog from the AI) works just as correctly —
    // GameAiClient.SpinAsync polls ask_player_request.json the same way for ANY action,
    // not only for the gameplay SendAction (see InteractiveResponseTests).
    private void CheckFixHeroEditWithAskPlayer(GameSession game, List<string> failures)
    {
        const string context = "character editing (FixHeroForNewGame)";

        _output.WriteLine("Sending a character edit request — waiting for FixHeroForNewGame (mock) to start...");
        GameConsole.SendText(game.Pid, "give me an elegant cloak, but ask about my plans first");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);
        Thread.Sleep(150);

        string askPlayerPath = Path.Combine(game.RealStorageDir, "ask_player_request.json");
        string[] options = ["Explore the dungeon", "Fight", "Negotiate with the inhabitants"];
        var askRequest = new { history = new[] { new { text = "What are your plans?" } }, options };
        _output.WriteLine("Writing ask_player_request.json — checking that the question is shown during character editing too...");
        File.WriteAllText(askPlayerPath, JsonSerializer.Serialize(askRequest));

        foreach (var option in options)
            if (!GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, option, 4000))
            {
                failures.Add($"[{context}] Answer option \"{option}\" not shown in the question dialog.");
                return;
            }
        _output.WriteLine("Question shown during editing — choosing the first option...");

        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);
        // The short timeout is deliberate: the answer must appear IMMEDIATELY (DialogDisplay.
        // PlayAskPlayerRequest redraws the dialog right after the choice), not only
        // when FixHeroForNewGame (mock, artificial 5s delay) has fully finished —
        // a wide window would mask a regression of the immediate redraw. We look specifically for
        // "Test Hero: <answer>" (with the author prefix) — the bare option text is still visible in the
        // OPTION LIST ITSELF regardless of whether the answer reached the history, and would give a false positive.
        if (!GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, $"Test Hero: {options[0]}", 2000))
        {
            failures.Add($"[{context}] The chosen answer option did not appear in the dialog history IMMEDIATELY after the choice.");
            return;
        }

        _output.WriteLine("Waiting for the FixHeroForNewGame (mock) response...");
        if (!GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "part of your gear", 15000))
        {
            failures.Add($"[{context}] The FixHeroForNewGame (mock) response did not arrive after answering the question.");
            return;
        }

        // NOT "sleep a fixed time + one snapshot": the dialog panel (where the cloak just
        // showed up in the DM line) and the hero card on the left are redrawn by DIFFERENT calls
        // at DIFFERENT times — the card updates only on the next iteration of the outer game
        // loop. Under load (the whole test suite in a row) a fixed pause is sometimes not enough —
        // so we poll the screen until the final item appears rather than guessing with Thread.Sleep.
        // Only the start of the name: the equipment slot in the card truncates a long name with "..."
        // (HeroDisplay.FitLine); capitalized — in the DM line it is lowercase, "elegant cloak".
        // Wait long: the patch is applied after the DM line has finished typing out character by character.
        if (!GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "Elegant", 20000))
            failures.Add($"[{context}] Item \"Elegant Cloak\", added by the FixHeroForNewGame mock response, not found on screen.");
    }

    private void CheckMapScreen(int gamePid, List<string> failures)
    {
        const string context = "map screen (new game)";

        _output.WriteLine($"[{context}] Checking tabs [F2]/[F3]/[Esc]...");
        string title = GameConsole.ReadRow(gamePid, 1, GameConsole.TitleReadWidth);
        ScreenAssertions.RequireTab(title, "[F2]", "WORLD", context, failures);
        ScreenAssertions.RequireTab(title, "[F3]", "JOURNAL", context, failures);
        ScreenAssertions.RequireTab(title, "[F4]", "CHARACTER", context, failures);
        ScreenAssertions.RequireTab(title, "[F5]", "COMPANION", context, failures);
        ScreenAssertions.RequireTab(title, "[Esc]", "MENU", context, failures);
        TestPacing.Step();

        _output.WriteLine($"[{context}] Checking borders...");
        var rows = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        ScreenAssertions.CheckBorders(rows, context, failures);
        TestPacing.Step();

        _output.WriteLine($"[{context}] Checking grid column numbers...");
        if (!rows.Any(r => r.Contains(" 1 ") || r.TrimEnd().EndsWith(" 1")))
            failures.Add($"[{context}] Grid column numbers not found on the map screen.");
        TestPacing.Step();

        // From the StartNewGame mock response (Fixtures/MockResponses/StartNewGame/response.json) —
        // the hero symbol, an NPC symbol and an object must actually be drawn on the grid.
        _output.WriteLine($"[{context}] Checking the symbols of the hero (TST), the NPC (NPC) and the object ([C]) on the grid...");
        if (!rows.Any(r => r.Contains("TST")))
            failures.Add($"[{context}] Hero symbol \"TST\" not found on the map.");
        if (!rows.Any(r => r.Contains("NPC")))
            failures.Add($"[{context}] NPC symbol \"NPC\" not found on the map.");
        if (!rows.Any(r => r.Contains("[C]")))
            failures.Add($"[{context}] Object symbol \"[C]\" not found on the map.");
        TestPacing.Step();
    }

    private void CheckCharacterScreen(int gamePid, List<string> failures)
    {
        const string context = "character screen (new game)";

        var rows = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        _output.WriteLine($"[{context}] Checking that the hero from CreateNewGame (mock) was saved and is visible after StartNewGame (Storage.Save)...");
        CheckMockedHeroCard(gamePid, rows, context, failures);

        // The FixHeroForNewGame edit (made BEFORE StartNewGame) must survive Storage.Save()
        // and the redraw on this screen — otherwise character editing is lost when the game starts.
        _output.WriteLine($"[{context}] Checking that the FixHeroForNewGame edit (Elegant Cloak) was kept...");
        if (!rows.Any(r => r.Contains("Elegant Cloak")))
            failures.Add($"[{context}] Item \"Elegant Cloak\" from FixHeroForNewGame did not survive StartNewGame/Storage.Save().");
        TestPacing.Step();
    }

    // Card content checks shared by the editor and the character screen — both screens draw
    // it with the same HeroDisplay.DrawHeroCard over the same WorldState.Hero.
    private void CheckMockedHeroCard(int gamePid, List<string> rows, string context, List<string> failures)
    {
        if (!rows.Any(r => r.Contains("Test Hero")))
            failures.Add($"[{context}] Hero name \"Test Hero\" (from the CreateNewGame mock response) not found on screen.");
        if (!rows.Any(r => r.Contains("10/10")))
            failures.Add($"[{context}] Hero HP \"10/10\" not found on screen.");
        if (!rows.Any(r => r.Contains("Test Sword")))
            failures.Add($"[{context}] Item \"Test Sword\" not found in the inventory.");
        if (!rows.Any(r => r.Contains("Test Torch")))
            failures.Add($"[{context}] Item \"Test Torch\" not found in the inventory.");
        // Features live on a separate sub-tab ([F7]/a separate NewGame screen) —
        // they are not checked here together with the inventory, which is open by default.

        // The hero color is chosen in the form and carried over on top of the CreateNewGame response — by default
        // the first in the NewGameDisplay palette ("AMBER" [220, 170, 60]), not [100, 200, 255] from the mock.
        ScreenAssertions.CheckTextColor(gamePid, rows, "Test Hero", (220, 170, 60), 60, MainBackground, context, failures);
    }
}
