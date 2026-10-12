using Xunit.Abstractions;

namespace NaviDnD.UiTests;

// E2E: status effects (CellEntity.Effects/StatusEffect) replaced the former free-text
// State field. Fixtures/effects_game.json: a hero with three effects of different duration (limited by
// rounds / until long rest / indefinite), a visible goblin "GB1" with the "Burning" effect and a control
// healthy goblin "GB2" with no effects.
public class EffectsScenarioTests
{
    private readonly ITestOutputHelper _output;

    public EffectsScenarioTests(ITestOutputHelper output) => _output = output;

    [Fact]
    public void Legend_ShowsEntityEffectName_NextToEntity()
    {
        using var game = GameSession.Launch(fixtureFileName: "effects_game.json");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.MenuSelect(game.Pid, "CONTINUE");
        Assert.True(
            GameConsole.WaitForRowContains(game.Pid, 1, GameConsole.TitleReadWidth, "→ MAP ←", 5000),
            "Map screen did not appear after [F1] in the menu.");
        _output.WriteLine("Map screen rendered.");
        TestPacing.Step();

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        var failures = new List<string>();

        _output.WriteLine("Checking that the burning goblin \"GB1\" shows the effect name \"Burning\" in the legend...");
        var burningRow = rows.FirstOrDefault(r => r.Contains("GB1"));
        if (burningRow == null) failures.Add("Row with \"GB1\" not found in the legend.");
        else if (!burningRow.Contains("Burning"))
            failures.Add($"The burning goblin \"GB1\" does not show the \"Burning\" effect: \"{burningRow.TrimEnd()}\"");
        TestPacing.Step();

        _output.WriteLine("Checking that the healthy goblin \"GB2\" has no effects and no \"Burning\"...");
        var healthyRow = rows.FirstOrDefault(r => r.Contains("GB2"));
        if (healthyRow == null) failures.Add("Row with \"GB2\" not found in the legend.");
        else if (healthyRow.Contains("Burning"))
            failures.Add($"The healthy goblin \"GB2\" unexpectedly shows the \"Burning\" effect: \"{healthyRow.TrimEnd()}\"");
        TestPacing.Step();

        _output.WriteLine(failures.Count == 0 ? "All checks passed." : $"Failed checks: {failures.Count}.");
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }

    [Fact]
    public void CharacterEffectsTab_ShowsActiveStatusesWithDuration()
    {
        using var game = GameSession.Launch(fixtureFileName: "effects_game.json");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen rendered (Inventory tab).");
        TestPacing.Step();

        _output.WriteLine("Sending [F8] — waiting for the Conditions sub-tab...");
        GameConsole.SendKey(game.Pid, ConsoleKey.F8);
        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "→ CONDITIONS ←", 5000),
            "The Conditions sub-tab did not activate after pressing [F8].");
        _output.WriteLine("Conditions sub-tab rendered.");
        TestPacing.Step();

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        var failures = new List<string>();

        // The list row marker on the Conditions sub-tab (HeroDisplay.DrawEffectsSection) is just
        // a space, with no visible symbol. Search by "Name:" (the name is immediately followed by ": <duration>",
        // see InvLine.Rest below) — this tells a list row apart from the short summary of names in the hero card
        // above ("Conditions   Poisoned, ..."), where the name is followed by a comma, not a colon.
        _output.WriteLine("Checking the round-limited effect (\"Poisoned\" — until round 8)...");
        var poisonRow = rows.FirstOrDefault(r => r.Contains("Poisoned:"));
        if (poisonRow == null) failures.Add("List row \"Poisoned:\" not found on the Conditions sub-tab.");
        else if (!poisonRow.Contains("until round 8"))
            failures.Add($"The \"Poisoned\" effect does not show the duration \"until round 8\": \"{poisonRow.TrimEnd()}\"");
        TestPacing.Step();

        _output.WriteLine("Checking the until-long-rest effect (\"Exhaustion\")...");
        var exhaustionRow = rows.FirstOrDefault(r => r.Contains("Exhaustion:"));
        if (exhaustionRow == null) failures.Add("List row \"Exhaustion:\" not found on the Conditions sub-tab.");
        else if (!exhaustionRow.Contains("until long rest"))
            failures.Add($"The \"Exhaustion\" effect does not show \"until long rest\": \"{exhaustionRow.TrimEnd()}\"");
        TestPacing.Step();

        _output.WriteLine("Checking the indefinite effect (\"Blessing of the Spirits\")...");
        var blessingRow = rows.FirstOrDefault(r => r.Contains("Blessing of the Spirits:"));
        if (blessingRow == null) failures.Add("List row \"Blessing of the Spirits:\" not found on the Conditions sub-tab.");
        else if (!blessingRow.Contains("indefinite"))
            failures.Add($"The indefinite effect does not show \"indefinite\": \"{blessingRow.TrimEnd()}\"");
        TestPacing.Step();

        _output.WriteLine("Checking that the short effects summary is gone from the hero card (only on the Conditions sub-tab)...");
        var summaryRow = rows.FirstOrDefault(r => r.Contains("Conditions")); // the tab's "CONDITIONS" label is uppercase, it won't match
        if (summaryRow != null)
            failures.Add($"The hero card unexpectedly still has a \"Conditions\" label row outside the sub-tab: \"{summaryRow.TrimEnd()}\"");
        TestPacing.Step();

        _output.WriteLine(failures.Count == 0 ? "All checks passed." : $"Failed checks: {failures.Count}.");
        Assert.True(failures.Count == 0, "UI test FAIL:\n" + string.Join("\n", failures.Select(f => "  - " + f)));
    }

    // Regression: switching the sub-tab by MOUSE CLICK goes through a separate path
    // (MouseUiHelper → display.PendingCommand → InputBox.ReadKeyWithPolling), not through reading
    // ConsoleKey directly — it had its own list of known commands ("F1".."F7","Esc"), not
    // updated together with the rest when the Conditions sub-tab was added. The keyboard [F8]
    // (see the test above) does not touch this branch and did not catch the bug — the process crashed with an unhandled
    // InvalidOperationException "Unknown pending command: F8" only on a mouse click.
    [Fact]
    public void CharacterEffectsTab_MouseClick_SwitchesTabWithoutCrashing()
    {
        using var game = GameSession.Launch(fixtureFileName: "effects_game.json");
        _output.WriteLine($"Game launched (pid {game.Pid}).");

        Navigation.ToCharacterScreen(game.Pid);
        _output.WriteLine("Character screen rendered (Inventory tab).");
        TestPacing.Step();

        var rows = GameConsole.ReadRows(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth);
        int rowIdx = rows.FindIndex(r => r.Contains("[F8]"));
        Assert.True(rowIdx >= 0, "The \"[F8]CONDITIONS\" label not found on the Inventory sub-tab — nothing to click.");
        var tab = ScreenAssertions.FindTab(rows[rowIdx], "[F8]");
        Assert.NotNull(tab);

        _output.WriteLine("Clicking the \"[F8]CONDITIONS\" label with the mouse...");
        GameConsole.SendLeftClick(game.Pid, tab!.Value.startX, rowIdx);

        Assert.True(
            GameConsole.WaitForAnyRowContains(game.Pid, 0, GameConsole.ScanRowCount, GameConsole.TitleReadWidth, "→ CONDITIONS ←", 5000),
            "The Conditions sub-tab did not activate after a mouse click on [F8] (or the game crashed).");
        _output.WriteLine("Conditions sub-tab rendered, the game did not crash.");
    }
}
