using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: two real bugs in the character editor (Screen.NewGameCharacter) — hovering the mouse over
// an inventory item did not show its picture, and Tab did not page through the item selection. The root of both: the
// MouseUiHelper.HandleGameScreen call in Program.cs for this screen did not pass onTab/onShiftTab/
// onInventoryItemHover (unlike the exact same call for the gameplay Screen.Character,
// which has these callbacks) — with null callbacks HandleGameScreen silently does nothing for Tab and
// does not call MouseUiHelper.SetSelectedImage on hover. Worse: the missing onTab meant
// that the literal string "Tab" fell through into handling as ordinary player text input and
// flew off to FixHeroForNewGame as if the player had literally written "Tab".
public class NewGameCharacterEditorScenarioTests
{
    private readonly ITestOutputHelper _output;

    public NewGameCharacterEditorScenarioTests(ITestOutputHelper output) => _output = output;

    private static void TypeField(int gamePid, string text)
    {
        GameConsole.SendText(gamePid, text);
        GameConsole.SendKey(gamePid, ConsoleKey.Enter);
        Thread.Sleep(150);
    }

    // F2 → the form (5 fields) → the character editor. A shared entry path that duplicates the start of
    // NewGameScenarioTests.CreateHeroAndStartNewGame — its own GameSession per file; there is no need to copy
    // the rest of the run (F3/StartNewGame etc.) here, only the editor is tested.
    private void OpenEditor(GameSession game)
    {
        Assert.True(Navigation.ToAnketa(game.Pid),
            "The character creation form did not appear after [F2] in the menu.");

        TypeField(game.Pid, "Test Hero");
        TypeField(game.Pid, "TST");
        TypeField(game.Pid, "A test hero for the e2e test.");
        GameConsole.SendKey(game.Pid, ConsoleKey.F3);   // the form's "NEXT" — same as [F3] (a button badge without arrows)

        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ EDITOR ←", 20000),
            "The character editor screen did not appear — CreateNewGame (mock) did not finish.");
        Thread.Sleep(200);
    }

    // "Test Sword" is equipped (equipmentedSlot:["R Hand"]) — its name appears on screen
    // TWICE: in the equipment slot list ("R Hand   Test Sword") and in the inventory list itself
    // ("• Test Sword: ..."). We look specifically for the second row — with a colon after the name, which
    // the equipment slot row does not have.
    private static string InventoryLineMarker(string itemName) => itemName + ":";

    // Fixtures/MockResponses/CreateNewGame/response.json: "Test Sword" is the only item with
    // an image set ("lorc/broadsword"), specifically for this test. The mock hero has no
    // hero.image of its own, so before hovering the right panel shows the default D20 (DiceArt.D20,
    // see DialogDisplay.GetDefaultRightPanelLines) — any change of the panel content after
    // hovering means the item's picture was actually drawn.
    [Fact]
    public void HoverInventoryItemWithImage_UpdatesRightPanel()
    {
        using var game = GameSession.LaunchForNewGame();
        _output.WriteLine($"Game launched (pid {game.Pid}).");
        OpenEditor(game);
        _output.WriteLine("Character editor rendered.");

        string marker = InventoryLineMarker("Test Sword");
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, marker, 5000),
            "Item \"Test Sword\" not found in the editor's inventory list.");

        var beforeRows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        int itemRow = beforeRows.FindIndex(r => r.Contains(marker));
        Assert.True(itemRow >= 0, "The \"Test Sword\" item row not found in the inventory list.");

        int hoverCol = beforeRows[itemRow].IndexOf(marker, StringComparison.Ordinal) + 2;
        _output.WriteLine("Hovering the mouse over \"Test Sword\" (it has an image in the mock response)...");
        GameConsole.SendMouseMove(game.Pid, hoverCol, itemRow);

        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => r[itemRow].Contains(marker) && r[itemRow].Contains("←"), 3000),
            "Hovering over an item in the editor did not highlight the row with the \"←\" marker.");
        _output.WriteLine("Row highlighted — the engine registered the hover.");

        // RerenderRightPanel is called separately from the row highlight (the same pattern as
        // redrawing the card after a mock response in other tests) — poll rather than sleeping a fixed time.
        bool panelChanged = GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
            afterRows =>
            {
                for (int i = 0; i < beforeRows.Count && i < afterRows.Count; i++)
                {
                    if (i == itemRow) continue;
                    if (beforeRows[i] != afterRows[i]) return true;
                }
                return false;
            }, 3000);

        Assert.True(panelChanged,
            "After hovering over an item with an image no picture appeared — the dialog's right panel did not change " +
            "(regression: onInventoryItemHover not passed to MouseUiHelper.HandleGameScreen for Screen.NewGameCharacter).");
        _output.WriteLine("Right panel changed — the item picture was drawn.");
        TestPacing.Step();
    }

    // Tab must cycle through inventory items (as on the gameplay Character screen) —
    // the "←" marker must move from one item to another. Previously onTab was not passed:
    // Tab did NOTHING AT ALL for the selection, and (worse) flew off as the literal text "Tab" to
    // FixHeroForNewGame — we check both that the marker really moves and that no "Tab" message
    // from the player appears in the dialog history.
    [Fact]
    public void TabKey_CyclesInventorySelection_AndIsNotSentAsPlayerText()
    {
        using var game = GameSession.LaunchForNewGame();
        _output.WriteLine($"Game launched (pid {game.Pid}).");
        OpenEditor(game);
        _output.WriteLine("Character editor rendered.");

        // The sword is equipped (duplicated in the equipment slots — needs the marker with ":"), the torch is not equipped
        // (equipmentedSlot:[], no duplicate — but it has quantity:3, formatted "Test Torch ×3:",
        // so for it we search the plain name without ":", not InventoryLineMarker).
        string swordMarker = InventoryLineMarker("Test Sword");
        const string torchMarker = "Test Torch";
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, torchMarker, 5000),
            "Item \"Test Torch\" not found in the editor's inventory list.");

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        int swordRow = rows.FindIndex(r => r.Contains(swordMarker));
        int torchRow = rows.FindIndex(r => r.Contains(torchMarker));
        Assert.True(swordRow >= 0 && torchRow >= 0, "Both test items must be visible on the first inventory page.");

        _output.WriteLine("Pressing [Tab] — waiting for the first item (\"Test Sword\") to be selected...");
        GameConsole.SendKey(game.Pid, ConsoleKey.Tab);
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => r[swordRow].Contains("←"), 3000),
            "[Tab] in the character editor did not select the first inventory item (the \"←\" marker did not appear).");

        _output.WriteLine("Pressing [Tab] again — waiting for the marker to move to the second item (\"Test Torch\")...");
        GameConsole.SendKey(game.Pid, ConsoleKey.Tab);
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => r[torchRow].Contains("←") && !r[swordRow].Contains("←"), 3000),
            "A second [Tab] did not move the \"←\" marker from \"Test Sword\" to \"Test Torch\".");
        _output.WriteLine("Tab correctly cycles the item selection.");

        var afterRows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        Assert.DoesNotContain(afterRows, r => r.Contains("Test Hero: Tab"));
        _output.WriteLine("Tab did not end up in the dialog history as a player text message.");
        TestPacing.Step();
    }
}
