namespace NaviDnD.UiTests;

// Shared Menu → Map → Character navigation — reused by test cases that don't care about the
// transition itself (it is checked separately in GameScenarioTests.FullGameplayScenario).
internal static class Navigation
{
    // Main menu without hotkeys (MainMenuDisplay): ↓ arrow until the «→ NAME ←» item, then Enter.
    public static void MenuSelect(int gamePid, string item)
    {
        string marker = $"→ {item} ←";
        for (int i = 0; i < 6; i++)
        {
            if (GameConsole.WaitForAnyRowContains(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, marker, i == 0 ? 1000 : 400))
            {
                GameConsole.SendKey(gamePid, ConsoleKey.Enter);
                return;
            }
            GameConsole.SendKey(gamePid, ConsoleKey.DownArrow);
        }
        var screen = GameConsole.ReadRows(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        throw new InvalidOperationException($"Failed to select menu item «{item}». Screen:\n" + string.Join("\n", screen));
    }

    // Menu → «NEW GAME» → «World» step: a new world must have a description — Enter (with no description the focus goes to input),
    // ↑ to the description, text, [F2] — on → the hero form.
    public static bool ToAnketa(int gamePid)
    {
        MenuSelect(gamePid, "NEW GAME");
        if (!GameConsole.WaitForAnyRowContains(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "WORLD DESCRIPTION", 5000)) return false;
        Thread.Sleep(300);
        GameConsole.SendKey(gamePid, ConsoleKey.Enter);
        Thread.Sleep(200);
        GameConsole.SendKey(gamePid, ConsoleKey.UpArrow);
        Thread.Sleep(200);
        GameConsole.SendText(gamePid, "Test world");
        Thread.Sleep(200);
        GameConsole.SendKey(gamePid, ConsoleKey.F2);
        return GameConsole.WaitForAnyRowContains(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "FORM", 5000);
    }

    // Hero editor [F4] → «Adventure» step → «START GAME» (Tab through the fields to the button, it is last; Enter starts).
    public static bool StartFromEditor(int gamePid)
    {
        GameConsole.SendKey(gamePid, ConsoleKey.F4);
        if (!GameConsole.WaitForAnyRowContains(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "→ ADVENTURE ←", 5000))
        {
            GameConsole.SendKey(gamePid, ConsoleKey.F4);   // occasionally the first [F4] doesn't arrive under load
            if (!GameConsole.WaitForAnyRowContains(gamePid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "→ ADVENTURE ←", 5000)) return false;
        }
        Thread.Sleep(300);
        for (int i = 0; i < 12; i++) { GameConsole.SendKey(gamePid, ConsoleKey.Tab); Thread.Sleep(60); }
        GameConsole.SendKey(gamePid, ConsoleKey.Enter);
        return true;
    }

    public static void ToCharacterScreen(int gamePid)
    {
        MenuSelect(gamePid, "CONTINUE");
        if (!GameConsole.WaitForRowContains(gamePid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000))
            throw new InvalidOperationException("Timed out waiting for the Map screen after [F1] in the menu.");

        string mapTitle = GameConsole.ReadRow(gamePid, 1, GameConsole.TitleReadWidth);
        var f2Tab = ScreenAssertions.FindTab(mapTitle, "[F4]")
            ?? throw new InvalidOperationException("Tab [F4] not found on the map screen.");

        GameConsole.SendLeftClick(gamePid, f2Tab.startX, 1);
        if (!GameConsole.WaitForRowContains(gamePid, 1, GameConsole.TitleReadWidth, "→ CHARACTER ←", 5000))
            throw new InvalidOperationException("Timed out waiting for the Character screen after clicking [F4].");
    }
}
