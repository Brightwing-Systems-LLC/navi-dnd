using Xunit.Abstractions;


namespace NaviDnD.UiTests;

// E2E: reading a note in the inventory (HeroInventory.Text) — MouseUiHelper.SetSelectedInventoryItem
// (paged display: page 0 — picture+name, then the text itself) and the "pinning" mechanism
// for the selection (DisplayConfig.PinnedInventoryIndex) on top of ordinary hover (DisplayConfig.
// SelectedInventoryIndex). Design: a click/Tab pins the item ("←" survives the mouse leaving); ordinary
// hover over ANOTHER item temporarily changes the picture on the right, but does NOT remove "←" from the pinned one —
// it moves to the new item only on an explicit click/Tab (HeroDisplay.DrawInventorySection:
// arrow1/arrow2 follow pinIdx, not hover, while something is pinned). The arrow color is the same
// in both cases (the ordinary "pale" selection color) — what distinguishes pinning is not the color but that
// the arrow does not disappear when the mouse leaves. See Fixtures/full_game.json: "Longsword"
// (hero.inventory[1]) is the only item with a `text` set.
public class NoteReadingScenarioTests
{
    private readonly ITestOutputHelper _output;

    public NoteReadingScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void PinNoteItem_PersistsAcrossHover_AndPagesViaMouseAndKeyboard()
    {
        using var game = GameSession.Launch();
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        const string noteItem = "Longsword";
        // "Arrows" (not equipped) — unlike "Longbow" (equipped in both hands) it is not duplicated
        // by a row in the equipment slots above the inventory list, where FindIndex would catch the wrong row.
        const string otherItem = "Arrows";

        // HeroDisplay.DrawSubTabTitle draws ITS OWN "◄ N/M ►" indicator on the row with the
        // INVENTORY/FEATURES/CONDITIONS labels (inventory list pagination) — with 18 items in the fixture
        // it also has 2 pages, so "◄ 1/2 ►" is on screen at the same time as the note indicator.
        // Tell them apart by row: the note panel (DialogDisplay) has no "INVENTORY" label on the same row.
        static bool IsNoteIndicatorRow(string line, string marker) => line.Contains(marker) && !line.Contains("INVENTORY");
        bool NoteIndicatorPresent(List<string> rows, string marker) => rows.Any(r => IsNoteIndicatorRow(r, marker));

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, noteItem, 5000),
            $"Item \"{noteItem}\" did not appear on the Character screen.");
        _output.WriteLine("Character screen (Inventory tab) rendered.");
        TestPacing.Step();

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        int noteRow = rows.FindIndex(r => r.Contains(noteItem));
        int otherRow = rows.FindIndex(r => r.Contains(otherItem));
        Assert.True(noteRow >= 0 && otherRow >= 0, "Both test items must be visible on the first inventory page.");
        int noteCol = rows[noteRow].IndexOf(noteItem, StringComparison.Ordinal) + 2;
        int otherCol = rows[otherRow].IndexOf(otherItem, StringComparison.Ordinal) + 2;

        // 1. Hover shows page 1/2 (picture+name) — before the click there is no pinning,
        // the "←" marker simply follows the hover for now (like an ordinary, non-note item).
        _output.WriteLine($"Hovering the mouse over \"{noteItem}\" (it has text in the fixture)...");
        GameConsole.SendMouseMove(game.Pid, noteCol, noteRow);
        Thread.Sleep(150);
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => r[noteRow].Contains(noteItem) && r[noteRow].Contains("←"), 3000),
            $"Hovering over \"{noteItem}\" did not highlight the row with the \"←\" marker.");
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => NoteIndicatorPresent(r, "◄ 1/2 ►"), 3000),
            "After hovering over an item with text the \"◄ 1/2 ►\" page indicator did not appear.");
        _output.WriteLine("Page indicator 1/2 appeared on hover.");
        TestPacing.Step();

        // 2. A click pins the item — unlike ordinary hover, the marker and the page indicator
        // survive the mouse moving to an empty area (ordinary hover would reset them to null, as
        // InventoryHoverScenarioTests checks).
        _output.WriteLine($"Clicking \"{noteItem}\" — pinning the selection, then moving the mouse to an empty area...");
        GameConsole.SendLeftClick(game.Pid, noteCol, noteRow);
        Thread.Sleep(150);
        GameConsole.SendMouseMove(game.Pid, 2, 2);
        Thread.Sleep(150);
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => r[noteRow].Contains("←"), 3000),
            "After the click and the mouse leaving, the \"←\" marker disappeared — the item was not pinned.");
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => NoteIndicatorPresent(r, "◄ 1/2 ►"), 3000),
            "After the click and the mouse leaving, the page indicator disappeared — the pinned item should stay in the right panel.");
        _output.WriteLine("The pin survived the mouse leaving.");
        TestPacing.Step();

        // 3. Hovering over ANOTHER item temporarily changes the picture on the right (it has no text — the page
        // indicator disappears), but the "←" marker does NOT leave the pinned one — only an explicit click/Tab
        // moves the pin.
        _output.WriteLine($"Hovering the mouse over \"{otherItem}\" — a temporary preview, the pin must not be removed...");
        GameConsole.SendMouseMove(game.Pid, otherCol, otherRow);
        Thread.Sleep(150);
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => !NoteIndicatorPresent(r, "◄ 1/2 ►"), 3000),
            $"The note's page indicator is still visible while hovering over \"{otherItem}\" (it has no text) — the preview did not switch.");
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => r[noteRow].Contains("←") && !r[otherRow].Contains("←"), 3000),
            $"Hovering over \"{otherItem}\" removed the \"←\" marker from the pinned \"{noteItem}\" (it must stay until \"{otherItem}\" is explicitly clicked).");
        _output.WriteLine("The preview changed, but the pin stayed in place.");
        TestPacing.Step();

        // 4. The mouse leaving B brings back the display of the pinned A — the marker was in place anyway, but the content
        // on the right must return to the note.
        _output.WriteLine($"Moving the mouse off \"{otherItem}\" — the content on the right should return to the pinned \"{noteItem}\"...");
        GameConsole.SendMouseMove(game.Pid, 2, 2);
        Thread.Sleep(150);
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => NoteIndicatorPresent(r, "◄ 1/2 ►"), 3000),
            $"After the mouse left \"{otherItem}\" the page indicator of the pinned \"{noteItem}\" did not come back.");
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => r[noteRow].Contains("←") && !r[otherRow].Contains("←"), 3000),
            $"After the mouse left \"{otherItem}\" the \"←\" marker is not on the pinned \"{noteItem}\".");
        _output.WriteLine("The pinned item's display was restored correctly.");
        TestPacing.Step();

        // 5. A click on the ► arrow pages to the note's text page.
        rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        int indicatorRow = rows.FindIndex(r => IsNoteIndicatorRow(r, "◄ 1/2 ►"));
        Assert.True(indicatorRow >= 0, "Page indicator \"◄ 1/2 ►\" not found to locate the arrow.");
        int indicatorStart = rows[indicatorRow].IndexOf("◄ 1/2 ►", StringComparison.Ordinal);
        int rightArrowCol = indicatorStart + "◄ 1/2 ►".Length - 1;
        _output.WriteLine("Clicking the ► arrow — paging to the text page...");
        GameConsole.SendLeftClick(game.Pid, rightArrowCol, indicatorRow);
        Thread.Sleep(150);
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "◄ 2/2 ►", 3000),
            "Clicking ► did not page to 2/2.");
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "engraved", 3000),
            "The note text (\"...engraved...\") was not shown on page 2/2.");
        _output.WriteLine("The note text was shown on page 2/2.");
        TestPacing.Step();

        // 6. A click on the ◄ arrow brings back the picture (page 1/2).
        rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        indicatorRow = rows.FindIndex(r => r.Contains("◄ 2/2 ►"));
        Assert.True(indicatorRow >= 0, "Page indicator \"◄ 2/2 ►\" not found to locate the arrow.");
        int leftArrowCol = rows[indicatorRow].IndexOf("◄ 2/2 ►", StringComparison.Ordinal);
        GameConsole.SendLeftClick(game.Pid, leftArrowCol, indicatorRow);
        Thread.Sleep(150);
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => NoteIndicatorPresent(r, "◄ 1/2 ►"), 3000),
            "Clicking ◄ did not return to page 1/2.");
        _output.WriteLine("Clicking ◄ brought back the picture (page 1/2).");
        TestPacing.Step();

        // 7. Keyboard paging (RightArrow/LeftArrow with a pinned note — InputBox
        // returns "NoteLeft"/"NoteRight" instead of ordinary text input).
        _output.WriteLine("Checking keyboard paging (RightArrow/LeftArrow)...");
        GameConsole.SendKey(game.Pid, ConsoleKey.RightArrow);
        Thread.Sleep(150);
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "◄ 2/2 ►", 3000),
            "RightArrow did not page the note to 2/2.");
        GameConsole.SendKey(game.Pid, ConsoleKey.LeftArrow);
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                r => NoteIndicatorPresent(r, "◄ 1/2 ►"), 3000),
            "LeftArrow did not return the note to page 1/2.");
        _output.WriteLine("Keyboard paging works.");
        TestPacing.Step();
    }
}
