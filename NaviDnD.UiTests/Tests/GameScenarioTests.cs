using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// Each test case starts ITS OWN independent NaviDnD.exe via GameSession.Launch() —
// the cases do not depend on each other and do not reuse game state.
//
// The state comes from Fixtures/full_game.json, built from a real game save
// (hero + dungeon map) rather than invented from scratch — so the checks cover
// a realistic amount of data (a full sheet of stats/skills, 9 inventory items,
// a map with 6 rooms/10 doors/4 creatures).
//
// _output.WriteLine writes to the test log (visible in `dotnet test` with -v n/detailed, in Test
// Explorer — always) — so the log shows WHAT exactly was checked, not just the final
// PASS/FAIL over the accumulated failures list.
public class GameScenarioTests
{
    private readonly ITestOutputHelper _output;

    public GameScenarioTests(ITestOutputHelper output) => _output = output;

    // Scenario: Menu → [F1] → Map (check borders and tabs) → click on [F3] → Character,
    // Inventory tab (card + dialog window with history text, hovering a tab,
    // pagination) → [F7] → Features tab (card + pagination).
    [Fact]
    public void FullGameplayScenario()
    {
        using var game = GameSession.Launch();
        _output.WriteLine($"Game launched (pid {game.Pid}), Menu screen rendered.");
        var failures = new List<string>();

        const string MapActiveMarker = "→ MAP ←";
        const string CharacterActiveMarker = "→ CHARACTER ←";

        _output.WriteLine("Sending [F1] — waiting for the Map screen...");
        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, MapActiveMarker, 5000),
            "Map screen did not appear after pressing [F1] in the menu.");
        _output.WriteLine("Map screen rendered.");
        CheckMapScreen(game.Pid, failures);
        TestPacing.Step();

        string mapTitle = GameConsole.ReadRow(game.Pid, 1, GameConsole.TitleReadWidth);
        var f2Tab = ScreenAssertions.FindTab(mapTitle, "[F4]");
        Assert.NotNull(f2Tab); // otherwise there is nothing to click next — report via an explicit Assert, not failures

        _output.WriteLine("Clicking the [F4] tab with the mouse — waiting for the Character screen...");
        GameConsole.SendLeftClick(game.Pid, f2Tab!.Value.startX, 1);
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, CharacterActiveMarker, 5000),
            "Character screen did not appear after a mouse click on the [F4] tab.");
        // The screen title (row 1) and the card body (dialog window, inventory) are drawn by DIFFERENT
        // calls — the title may change before the dialog window below has been drawn (see the same
        // race that was fixed in DialogDisplay.Draw()). Explicitly wait for the card body rather than relying on
        // it having been drawn by the time the title changed — otherwise CheckCharacterScreen below
        // reads the screen too early and does not find "DIALOG"/the inventory contents.
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "DIALOG", 5000),
            "The dialog window was not drawn after switching to the Character screen.");
        _output.WriteLine("Character screen rendered (Inventory tab).");
        CheckCharacterScreen(game.Pid, expectAbilities: false, failures);
        TestPacing.Step();

        CheckTopTabHoverEffects(game.Pid, "Inventory tab", failures);
        TestPacing.Step();
        CheckInventoryPagination(game.Pid, "Inventory tab", failures);
        TestPacing.Step();

        _output.WriteLine("Sending [F7] — waiting for the Features sub-tab...");
        GameConsole.SendKey(game.Pid, ConsoleKey.F7);
        // "→ CHARACTER ←" does not change when the sub-tab changes (it is the SCREEN title, not the sub-tab's) —
        // we must wait for the active SUB-TAB marker (drawn inside the card, not on row 1),
        // otherwise the check passes instantly with no confirmation that [F7] was handled at all.
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "→ FEATURES ←", 5000),
            "The Features sub-tab did not activate after pressing [F7].");
        _output.WriteLine("Features sub-tab rendered.");
        CheckCharacterScreen(game.Pid, expectAbilities: true, failures);
        TestPacing.Step();
        CheckAbilitiesPagination(game.Pid, "Features sub-tab", failures);
        TestPacing.Step();

        TestPacing.Step();
        _output.WriteLine(failures.Count == 0
            ? "All checks passed."
            : $"Failed checks: {failures.Count}.");
        // The final Step() goes before the Assert, not after: if the checks failed, Assert.True below
        // throws and the code after it won't run, but the debugging pause is needed either way.
        TestPacing.Step();
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }

    private void CheckMapScreen(int gamePid, List<string> failures)
    {
        _output.WriteLine("[map screen] Checking tabs [F2]/[F3]/[F4]/[F5]/[Esc]...");
        string title = GameConsole.ReadRow(gamePid, 1, GameConsole.TitleReadWidth);
        ScreenAssertions.RequireTab(title, "[F2]", "WORLD", "map screen", failures);
        ScreenAssertions.RequireTab(title, "[F3]", "JOURNAL", "map screen", failures);
        ScreenAssertions.RequireTab(title, "[F4]", "CHARACTER", "map screen", failures);
        ScreenAssertions.RequireTab(title, "[F5]", "COMPANION", "map screen", failures);
        ScreenAssertions.RequireTab(title, "[Esc]", "MENU", "map screen", failures);
        TestPacing.Step();

        _output.WriteLine("[map screen] Checking borders...");
        var rows = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        ScreenAssertions.CheckBorders(rows, "map screen", failures);
        TestPacing.Step();

        _output.WriteLine("[map screen] Checking grid column numbers...");
        // Map column headers (consecutive numbers) are a sign that the grid was actually drawn,
        // not just an empty frame. The numbers are field coordinates under the camera, not necessarily starting at 1.
        if (!rows.Any(r => System.Text.RegularExpressions.Regex.Matches(r, @"\b\d{1,3}\b").Count >= 10))
            failures.Add("Grid column numbers not found on the map screen.");
        TestPacing.Step();
    }

    private void CheckCharacterScreen(int gamePid, bool expectAbilities, List<string> failures)
    {
        string context = expectAbilities ? "Features sub-tab" : "Inventory tab";

        _output.WriteLine($"[{context}] Checking tabs [F1]/[F5]/[Esc]...");
        string title = GameConsole.ReadRow(gamePid, 1, GameConsole.TitleReadWidth);
        ScreenAssertions.RequireTab(title, "[F1]", "MAP", context, failures);
        ScreenAssertions.RequireTab(title, "[F5]", "COMPANION", context, failures);
        ScreenAssertions.RequireTab(title, "[Esc]", "MENU", context, failures);
        TestPacing.Step();

        _output.WriteLine($"[{context}] Checking borders...");
        var rows = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        ScreenAssertions.CheckBorders(rows, context, failures);
        TestPacing.Step();

        _output.WriteLine($"[{context}] Checking the hero name, the dialog window and the history text (DM and hero lines)...");
        bool sawHeroName = rows.Any(r => r.Contains("Rustem"));
        bool sawDialogWindow = rows.Any(r => r.Contains("DIALOG"));
        // From the fixture (Fixtures/full_game.json → history) — check that the real history text
        // actually reaches the screen, not just the dialog window frame.
        bool sawDmMessage = rows.Any(r => r.Contains("torches crackle faintly"));
        bool sawHeroMessage = rows.Any(r => r.Contains("I look around"));

        if (!sawHeroName) failures.Add($"[{context}] Hero name \"Rustem\" not found on screen.");
        if (!sawDialogWindow) failures.Add($"[{context}] Dialog window (\"DIALOG\") not found.");
        if (!sawDmMessage) failures.Add($"[{context}] DM line text from the history not found.");
        if (!sawHeroMessage) failures.Add($"[{context}] Hero line text from the history not found.");
        TestPacing.Step();

        if (expectAbilities)
        {
            _output.WriteLine($"[{context}] Checking the feature list...");
            // From the fixture (hero.abilities) — several different features must be visible
            // at once (checks that the list is not cut off after one entry).
            // "Second Wind" is deliberately not included — it is BOTH a feature
            // and a resource (hero.resources), and resources are always visible at the top of the card
            // regardless of the sub-tab — a match on that name proves nothing.
            string[] abilityNames = ["Fighting Style: Archery", "Fey Ancestry", "Keen Senses"];
            foreach (var name in abilityNames)
                if (!rows.Any(r => r.Contains(name)))
                    failures.Add($"[{context}] Feature \"{name}\" not found on screen.");
        }
        else
        {
            _output.WriteLine($"[{context}] Checking the INVENTORY sub-tab and items (including quantity)...");
            if (!rows.Any(r => r.Contains("INVENTORY")))
                failures.Add($"[{context}] Sub-tab \"INVENTORY\" not found.");

            // From the fixture (hero.inventory) — several different items must be visible
            // at once, including quantity (×38 for the arrows) — checks the column layout.
            string[] itemNames = ["Longbow", "Chain Shirt", "Arrows"];
            foreach (var name in itemNames)
                if (!rows.Any(r => r.Contains(name)))
                    failures.Add($"[{context}] Item \"{name}\" not found on screen.");
            if (!rows.Any(r => r.Contains("×38")))
                failures.Add($"[{context}] Arrow quantity (×38) not found — possibly a column padding problem.");
        }
        TestPacing.Step();

        CheckHeroNameColor(gamePid, rows, context, failures);
        TestPacing.Step();
    }

    // hero.color from Fixtures/full_game.json — the expected hero name color.
    private static readonly (int r, int g, int b) HeroNameColor = (80, 160, 120);
    // The default DisplayConfig.MainBackground — what to compare against to find the text's "ink".
    private static readonly (byte r, byte g, byte b) MainBackground = (18, 18, 22);
    private const int ColorTolerance = 60;

    private void CheckHeroNameColor(int gamePid, List<string> rows, string context, List<string> failures)
    {
        if (!rows.Any(r => r.Contains("Rustem"))) return; // the missing text itself is already reported by a separate check
        _output.WriteLine($"[{context}] Checking the hero name color (screenshot + pixel sampling)...");
        ScreenAssertions.CheckTextColor(gamePid, rows, "Rustem", HeroNameColor, ColorTolerance, MainBackground, context, failures);
    }

    // Hovering a clickable element must change its color (MouseUiHelper.SetTabHighlight) —
    // checked on the inactive [F1] tab (on the character screen this tab currently does nothing
    // on hover except highlighting — we compare the state BEFORE/AFTER rather than a specific
    // expected color, which is more reliable). The cursor (ConsoleMouseReader.SetCursorShape) is not checked:
    // it is the real system cursor (GetCursorInfo), which depends on where the real mouse pointer
    // physically is in the OS, not on synthetic WriteConsoleInput events —
    // the highlight is called by the same hover handler and reliably confirms that it fired.
    private const int HoverColorDelta = 10;

    private void CheckTopTabHoverEffects(int gamePid, string context, List<string> failures)
    {
        string title = GameConsole.ReadRow(gamePid, 1, GameConsole.TitleReadWidth);
        var tab = ScreenAssertions.FindTab(title, "[F1]");
        if (tab == null) return; // the missing tab is already reported by RequireTab

        _output.WriteLine($"[{context}] Checking that the [F1] tab changes color on mouse hover...");

        (byte r, byte g, byte b) background = (18, 18, 22);
        int sampleCol = tab.Value.startX + 1;

        // A neutral point far from any tabs/arrows — reset the hover before measuring.
        GameConsole.SendMouseMove(gamePid, 2, 10);
        Thread.Sleep(250);

        (byte r, byte g, byte b) colorBefore;
        try { colorBefore = GameConsole.SampleCellColor(gamePid, sampleCol, 1, background); }
        catch (Exception ex) { failures.Add($"[{context}] Could not sample the [F1] tab color before hover: {ex.Message}"); return; }

        GameConsole.SendMouseMove(gamePid, sampleCol, 1);
        Thread.Sleep(300);

        (byte r, byte g, byte b) colorAfter;
        try { colorAfter = GameConsole.SampleCellColor(gamePid, sampleCol, 1, background); }
        catch (Exception ex) { failures.Add($"[{context}] Could not sample the [F1] tab color after hover: {ex.Message}"); return; }

        bool colorChanged = Math.Abs(colorBefore.r - colorAfter.r) > HoverColorDelta
                         || Math.Abs(colorBefore.g - colorAfter.g) > HoverColorDelta
                         || Math.Abs(colorBefore.b - colorAfter.b) > HoverColorDelta;
        if (!colorChanged)
            failures.Add($"[{context}] The [F1] tab color did not change on hover: before ({colorBefore.r},{colorBefore.g},{colorBefore.b}), after ({colorAfter.r},{colorAfter.g},{colorAfter.b}).");
        TestPacing.Step();

        // Move the mouse away so it doesn't interfere with the next steps of the scenario.
        GameConsole.SendMouseMove(gamePid, 2, 10);
        Thread.Sleep(150);
    }

    // The fixture (Fixtures/full_game.json) deliberately has many items/features
    // with long descriptions — the list doesn't fit on one page, so the ◄/► arrows
    // must actually page. The marker is the first UNequipped item: equipped ones
    // (e.g. "Longbow") are always visible in the equipment slot block at the top of the card,
    // regardless of the inventory page, and are no good as a page marker.
    private void CheckInventoryPagination(int gamePid, string context, List<string> failures) =>
        CheckPagination(gamePid, context, "Longsword", "item", failures);

    private void CheckAbilitiesPagination(int gamePid, string context, List<string> failures) =>
        CheckPagination(gamePid, context, "Fighting Style: Archery", "feature", failures);

    private void CheckPagination(int gamePid, string context, string firstMarker, string itemKind, List<string> failures)
    {
        _output.WriteLine($"[{context}] Checking pagination ({itemKind}): ► forward, ◄ back...");
        var page1 = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);

        if (!page1.Any(r => r.Contains(firstMarker)))
        {
            failures.Add($"[{context}] First {itemKind} \"{firstMarker}\" not found on the first page.");
            return;
        }

        int arrowRow = page1.FindIndex(r => r.Contains('►'));
        if (arrowRow < 0)
        {
            failures.Add($"[{context}] Arrow \"►\" not found — the list should not fit on one page (the fixture has enough entries).");
            return;
        }
        int arrowCol = page1[arrowRow].IndexOf('►');

        GameConsole.SendLeftClick(gamePid, arrowCol, arrowRow);
        Thread.Sleep(500);
        TestPacing.Step();

        var page2 = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        if (page2.Any(r => r.Contains(firstMarker)))
            failures.Add($"[{context}] After clicking ► \"{firstMarker}\" is still visible — the page did not change.");

        int backArrowRow = page2.FindIndex(r => r.Contains('◄'));
        if (backArrowRow < 0)
        {
            failures.Add($"[{context}] Arrow \"◄\" not found after moving to the second page.");
            return;
        }
        int backArrowCol = page2[backArrowRow].IndexOf('◄');

        GameConsole.SendLeftClick(gamePid, backArrowCol, backArrowRow);
        Thread.Sleep(500);
        TestPacing.Step();

        var page1Again = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        if (!page1Again.Any(r => r.Contains(firstMarker)))
            failures.Add($"[{context}] After clicking ◄ we did not return to the first page — \"{firstMarker}\" is not visible.");
        TestPacing.Step();
    }
}
