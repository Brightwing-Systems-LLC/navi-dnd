using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: SendAction (mock) unequips the bow, equips the sword, deletes one item and adds a new one —
// we check that the hero card (equipment slots + inventory list) reflects this correctly.
// The AI provider is mocked (Fixtures/MockResponses/Inventory/SendAction/response.json) — the real
// model is not called.
public class InventoryScenarioTests
{
    private readonly ITestOutputHelper _output;

    public InventoryScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void EquipUnequipAndInventoryChanges_DisplayCorrectly()
    {
        using var game = GameSession.Launch(mockedActions: ["SendAction"], mockResponsesFolder: "Inventory");
        _output.WriteLine($"Game launched (pid {game.Pid}).");
        var failures = new List<string>();

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen (Inventory tab) rendered.");

        // Fixtures/full_game.json: hero.inventory[0]=Longbow (L/R Hand), [1]=Longsword
        // (not equipped), [6]=Dagger. Mock response: the bow is unequipped, the sword equipped in [R Hand], the dagger deleted,
        // "Magic Ring" added.
        _output.WriteLine("Sending a game action — waiting for the SendAction (mock) response...");
        GameConsole.SendText(game.Pid, "I sling my bow over my back and take the sword in hand — the dagger has gone somewhere");
        GameConsole.SendKey(game.Pid, ConsoleKey.Enter);

        // "in your pocket." ends the message (see response.json): while the text is being typed out
        // letter by letter (SequentialHistoryPlayer, ~25ms/char), the hero card on the left is still the OLD one —
        // it is redrawn only when SendAction() has fully finished and the outer game
        // loop (Program.cs) redraws the screen. The hero.inventory patch has already been applied by then
        // (it is applied BEFORE the history starts typing — see SequentialHistoryPlayer.PlayAsync), but
        // the screen does not show it yet. Wait for the last words + a small margin, not the first
        // word of the phrase that comes along (otherwise we'd catch data BEFORE the patch/redraw).
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                "in your pocket.", 15000),
            "The SendAction (mock) response did not arrive — the history message did not appear.");
        _output.WriteLine("Response applied. Waiting for the hero card to redraw...");

        // NOT "sleep a fixed time + one snapshot": the hero card is redrawn by a DIFFERENT
        // call and at a DIFFERENT time than the dialog panel — under load (the whole test suite
        // in a row) a fixed pause is sometimes not enough. Do NOT just look for "Longsword" —
        // it is ALREADY visible in the ordinary inventory list (unequipped) BEFORE the patch, a plain Contains
        // would give a false positive on stale data. Wait specifically for the [R Hand] SLOT with the sword.
        Assert.True(
            GameConsole.WaitForRowsMatch(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth,
                rows => rows.Any(r => r.Contains("R Hand") && r.Contains("Longsword")), 5000),
            "The hero card was not redrawn with the updated equipment after the SendAction (mock) response.");
        TestPacing.Step();

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);

        _output.WriteLine("Checking the equipment change: bow unequipped ([L Hand] empty), sword equipped in [R Hand]...");
        CheckEquipSlot(rows, "R Hand", "Longsword", failures);
        CheckEquipSlotEmpty(rows, "L Hand", failures);
        TestPacing.Step();

        _output.WriteLine("Checking added/removed inventory items (paging through)...");
        var allPagesText = CollectAcrossPages(game.Pid);
        if (!allPagesText.Any(r => r.Contains("Magic Ring")))
            failures.Add("The added item \"Magic Ring\" was not found on any inventory page.");
        if (allPagesText.Any(r => r.Contains("Dagger")))
            failures.Add("The deleted item \"Dagger\" is still visible on one of the inventory pages.");
        TestPacing.Step();

        _output.WriteLine(failures.Count == 0
            ? "All checks passed."
            : $"Failed checks: {failures.Count}.");
        // The final Step() goes before the Assert, not after: if the checks failed, Assert.True below
        // throws and the code after it won't run, but the debugging pause is needed either way.
        TestPacing.Step();
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }

    // An equipment slot is a row like "R Hand   Longsword" (see HeroDisplay.DrawHeroCard —
    // leftSlots draws the slot name and the item name on ONE row).
    private static void CheckEquipSlot(List<string> rows, string slotName, string expectedItem, List<string> failures)
    {
        var row = rows.FirstOrDefault(r => r.Contains(slotName));
        if (row == null) { failures.Add($"Equipment slot \"{slotName}\" not found on screen."); return; }
        if (!row.Contains(expectedItem))
            failures.Add($"Slot \"{slotName}\" does not show \"{expectedItem}\": \"{row.TrimEnd()}\"");
    }

    private static void CheckEquipSlotEmpty(List<string> rows, string slotName, List<string> failures)
    {
        var row = rows.FirstOrDefault(r => r.Contains(slotName));
        if (row == null) { failures.Add($"Equipment slot \"{slotName}\" not found on screen."); return; }
        if (!row.Contains('—'))
            failures.Add($"Slot \"{slotName}\" should be empty (\"—\"), but: \"{row.TrimEnd()}\"");
    }

    // Walks through all inventory pages (► while present), collecting all text — to check
    // an item's presence/absence regardless of which page it ended up on.
    private static List<string> CollectAcrossPages(int gamePid, int maxPages = 10)
    {
        var all = new List<string>();
        for (int page = 0; page < maxPages; page++)
        {
            var rows = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
            all.AddRange(rows);

            int arrowRow = rows.FindIndex(r => r.Contains('►'));
            if (arrowRow < 0) break;
            int arrowCol = rows[arrowRow].IndexOf('►');
            GameConsole.SendLeftClick(gamePid, arrowCol, arrowRow);
            Thread.Sleep(400);
            TestPacing.Step();
        }
        return all;
    }
}
